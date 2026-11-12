#nullable enable
using System.Threading;
using System;
using System.Collections.Generic;

namespace AvifDecoding
{
    // The parts of an AVIF file (ISO/IEC 23008-12, ISO/IEC 23000-22) that a still image needs: the primary item, its AV1
    // payload, and the properties that change how its pixels are shown. Grids, sequences and alpha are not read.
    internal sealed class AvifContainer
    {
        private const int MaxBoxDepth = 8;
        private const int MaxItems = 4096;
        private const int MaxProperties = 4096;
        private const int MaxExtents = 256;

        private readonly byte[] _data;
        private readonly Dictionary<uint, ItemLocation> _locations = new Dictionary<uint, ItemLocation>();
        private readonly Dictionary<uint, uint> _itemTypes = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, List<int>> _associations = new Dictionary<uint, List<int>>();
        private readonly List<Property> _properties = new List<Property>();
        private int _idatOffset = -1;
        private int _idatLength;
        private uint _primaryItem;
        private bool _hasPrimaryItem;
        private bool _hasMeta;

        private AvifContainer(byte[] data)
        {
            _data = data;
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool HasColorDescription { get; private set; }

        public int MatrixCoefficients { get; private set; }

        public bool FullRange { get; private set; }

        // Rotation anticlockwise in quarter turns (irot), and the mirror axis (imir): -1 none, 0 top-bottom, 1 left-right
        public int Rotation { get; private set; }

        public int MirrorAxis { get; private set; } = -1;

        public bool HasCleanAperture { get; private set; }

        public CleanAperture Crop { get; private set; }

        public byte[] Payload { get; private set; } = Array.Empty<byte>();

        public static bool IsAvif(ReadOnlySpan<byte> data)
        {
            if (data.Length < 16 || !IsType(data, 4, "ftyp"))
                return false;

            var size = ReadUInt32(data, 0);
            if (size < 16 || size > data.Length)
                return false;

            if (IsType(data, 8, "avif") || IsType(data, 8, "avis"))
                return true;

            for (var offset = 16; offset + 4 <= size; offset += 4)
            {
                if (IsType(data, offset, "avif") || IsType(data, offset, "avis"))
                    return true;
            }

            return false;
        }

        public static AvifContainer Parse(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (!IsAvif(data))
                throw Invalid("the file is not an AVIF file.");

            var container = new AvifContainer(data);
            container.ParseBoxes(0, data.Length, 0);
            container.Resolve();
            return container;
        }

        private void ParseBoxes(int start, int end, int depth)
        {

            var offset = start;
            while (offset < end)
            {
                if (end - offset < 8)
                    throw Invalid("a box header is truncated.");

                long size = ReadUInt32(_data, offset);
                var type = ReadUInt32(_data, offset + 4);
                var headerLength = 8;
                if (size == 1)
                {
                    if (end - offset < 16)
                        throw Invalid("a box header is truncated.");

                    size = (long)ReadUInt64(_data, offset + 8);
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = end - offset;
                }

                if (size < headerLength || size > end - offset)
                    throw Invalid("a box extends past its parent.");

                var bodyStart = offset + headerLength;
                var bodyEnd = (int)(offset + size);
                if (type == FourCc("meta"))
                    ParseMeta(bodyStart, bodyEnd, depth + 1);

                offset = bodyEnd;
            }
        }

        private void ParseMeta(int start, int end, int depth)
        {
            if (_hasMeta)
                throw Invalid("the file has several 'meta' boxes.");

            _hasMeta = true;
            var offset = start + 4;
            foreach (var box in EnumerateBoxes(offset, end, depth))
            {
                if (box.Type == FourCc("pitm"))
                    ParsePrimaryItem(box);
                else if (box.Type == FourCc("iloc"))
                    ParseItemLocations(box);
                else if (box.Type == FourCc("iinf"))
                    ParseItemInfos(box, depth + 1);
                else if (box.Type == FourCc("iprp"))
                    ParseItemProperties(box, depth + 1);
                else if (box.Type == FourCc("idat"))
                {
                    _idatOffset = box.Start;
                    _idatLength = box.End - box.Start;
                }
            }
        }

        private void ParsePrimaryItem(Box box)
        {
            var reader = new Reader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(3);
            _primaryItem = version == 0 ? reader.ReadUInt16() : reader.ReadUInt32();
            _hasPrimaryItem = true;
        }

        private void ParseItemLocations(Box box)
        {
            var reader = new Reader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(3);
            if (version > 2)
                throw Unsupported($"'iloc' version {version}");

            var sizes = reader.ReadByte();
            var offsetSize = sizes >> 4;
            var lengthSize = sizes & 15;
            var otherSizes = reader.ReadByte();
            var baseOffsetSize = otherSizes >> 4;
            var indexSize = version > 0 ? otherSizes & 15 : 0;
            var itemCount = version < 2 ? reader.ReadUInt16() : reader.ReadUInt32();
            if (itemCount > MaxItems)
                throw Invalid("the file has too many items.");

            for (var i = 0u; i < itemCount; i++)
            {
                var itemId = version < 2 ? reader.ReadUInt16() : reader.ReadUInt32();
                var constructionMethod = 0;
                if (version > 0)
                    constructionMethod = (int)(reader.ReadUInt16() & 15);

                reader.Skip(2);
                var baseOffset = reader.ReadSized(baseOffsetSize);
                var extentCount = (int)reader.ReadUInt16();
                if (extentCount > MaxExtents)
                    throw Invalid("an item has too many extents.");

                var extents = new List<Extent>(extentCount);
                for (var e = 0; e < extentCount; e++)
                {
                    if (indexSize > 0)
                        reader.ReadSized(indexSize);

                    var extentOffset = reader.ReadSized(offsetSize);
                    var extentLength = reader.ReadSized(lengthSize);
                    extents.Add(new Extent(baseOffset + extentOffset, extentLength));
                }

                _locations[itemId] = new ItemLocation(constructionMethod, extents);
            }
        }

        private void ParseItemInfos(Box box, int depth)
        {
            var reader = new Reader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(3);
            var entryCount = version == 0 ? reader.ReadUInt16() : reader.ReadUInt32();
            if (entryCount > MaxItems)
                throw Invalid("the file has too many items.");

            foreach (var entry in EnumerateBoxes(reader.Position, box.End, depth))
            {
                if (entry.Type != FourCc("infe"))
                    continue;

                var infe = new Reader(_data, entry.Start, entry.End);
                var infeVersion = infe.ReadByte();
                infe.Skip(3);
                if (infeVersion < 2)
                    continue;

                var itemId = infeVersion == 2 ? infe.ReadUInt16() : infe.ReadUInt32();
                infe.Skip(2);
                _itemTypes[itemId] = infe.ReadUInt32();
            }
        }

        private void ParseItemProperties(Box box, int depth)
        {
            foreach (var child in EnumerateBoxes(box.Start, box.End, depth))
            {
                if (child.Type == FourCc("ipco"))
                {
                    foreach (var property in EnumerateBoxes(child.Start, child.End, depth + 1))
                    {
                        if (_properties.Count >= MaxProperties)
                            throw Invalid("the file has too many properties.");

                        _properties.Add(new Property(property.Type, property.Start, property.End));
                    }
                }
                else if (child.Type == FourCc("ipma"))
                {
                    ParseAssociations(child);
                }
            }
        }

        private void ParseAssociations(Box box)
        {
            var reader = new Reader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(2);
            var flags = reader.ReadByte();
            var entryCount = reader.ReadUInt32();
            if (entryCount > MaxItems)
                throw Invalid("the file has too many items.");

            for (var i = 0u; i < entryCount; i++)
            {
                var itemId = version < 1 ? reader.ReadUInt16() : reader.ReadUInt32();
                var count = reader.ReadByte();
                if (!_associations.TryGetValue(itemId, out var indices))
                {
                    indices = new List<int>();
                    _associations[itemId] = indices;
                }

                for (var a = 0; a < count; a++)
                {
                    var index = (flags & 1) != 0 ? (int)(reader.ReadUInt16() & 0x7FFF) : reader.ReadByte() & 0x7F;
                    indices.Add(index);
                }
            }
        }

        private void Resolve()
        {
            if (!_hasMeta || !_hasPrimaryItem)
                throw Unsupported("an AVIF file without a primary image item");
            if (!_itemTypes.TryGetValue(_primaryItem, out var itemType))
                throw Invalid("the primary item has no 'infe' entry.");
            if (itemType != FourCc("av01"))
                throw Unsupported($"a primary item of type '{FourCcToString(itemType)}'");
            if (!_locations.TryGetValue(_primaryItem, out var location))
                throw Invalid("the primary item has no location.");

            Payload = ReadItem(location);
            if (_associations.TryGetValue(_primaryItem, out var indices))
            {
                foreach (var index in indices)
                {
                    if (index == 0 || index > _properties.Count)
                        throw Invalid("an item property association is out of range.");

                    ApplyProperty(_properties[index - 1]);
                }
            }
        }

        private void ApplyProperty(Property property)
        {
            var reader = new Reader(_data, property.Start, property.End);
            if (property.Type == FourCc("ispe"))
            {
                reader.Skip(4);
                Width = checked((int)reader.ReadUInt32());
                Height = checked((int)reader.ReadUInt32());
            }
            else if (property.Type == FourCc("colr"))
            {
                if (reader.ReadUInt32() != FourCc("nclx"))
                    return;

                // The colour primaries and the transfer characteristics: the RGB conversion does not need them
                reader.Skip(4);
                MatrixCoefficients = (int)reader.ReadUInt16();
                FullRange = (reader.ReadByte() & 0x80) != 0;
                HasColorDescription = true;
            }
            else if (property.Type == FourCc("irot"))
            {
                Rotation = reader.ReadByte() & 3;
            }
            else if (property.Type == FourCc("imir"))
            {
                MirrorAxis = reader.ReadByte() & 1;
            }
            else if (property.Type == FourCc("clap"))
            {
                Crop = new CleanAperture(
                    (int)reader.ReadUInt32(), (int)reader.ReadUInt32(),
                    (int)reader.ReadUInt32(), (int)reader.ReadUInt32(),
                    (int)reader.ReadUInt32(), (int)reader.ReadUInt32(),
                    (int)reader.ReadUInt32(), (int)reader.ReadUInt32());
                HasCleanAperture = true;
            }
        }

        private byte[] ReadItem(ItemLocation location)
        {
            long total = 0;
            foreach (var extent in location.Extents)
                total += ResolveExtentLength(location, extent);

            if (total <= 0 || total > _data.Length)
                throw Invalid("the item data is out of range.");

            var payload = new byte[total];
            var written = 0;
            foreach (var extent in location.Extents)
            {
                var length = ResolveExtentLength(location, extent);
                var start = ResolveExtentStart(location, extent);
                Buffer.BlockCopy(_data, (int)start, payload, written, (int)length);
                written += (int)length;
            }

            return payload;
        }

        private long ResolveExtentStart(ItemLocation location, Extent extent)
        {
            if (location.ConstructionMethod == 0)
            {
                if (extent.Offset > (ulong)_data.Length)
                    throw Invalid("an item extent is out of range.");

                return (long)extent.Offset;
            }

            if (location.ConstructionMethod == 1)
            {
                if (_idatOffset < 0 || extent.Offset > (ulong)_idatLength)
                    throw Invalid("an item extent is out of the 'idat' box.");

                return _idatOffset + (long)extent.Offset;
            }

            throw Unsupported($"item construction method {location.ConstructionMethod}");
        }

        private long ResolveExtentLength(ItemLocation location, Extent extent)
        {
            var start = ResolveExtentStart(location, extent);
            var limit = location.ConstructionMethod == 1 ? _idatOffset + _idatLength : _data.Length;
            var length = extent.Length == 0 ? (ulong)(limit - start) : extent.Length;
            if (length > (ulong)(limit - start))
                throw Invalid("an item extent is out of range.");

            return (long)length;
        }

        private IEnumerable<Box> EnumerateBoxes(int start, int end, int depth)
        {
            if (depth > MaxBoxDepth)
                throw Invalid("the boxes are nested too deeply.");

            var boxes = new List<Box>();
            var offset = start;
            while (offset < end)
            {
                if (end - offset < 8)
                    throw Invalid("a box header is truncated.");

                long size = ReadUInt32(_data, offset);
                var type = ReadUInt32(_data, offset + 4);
                var headerLength = 8;
                if (size == 1)
                {
                    if (end - offset < 16)
                        throw Invalid("a box header is truncated.");

                    size = (long)ReadUInt64(_data, offset + 8);
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = end - offset;
                }

                if (size < headerLength || size > end - offset)
                    throw Invalid("a box extends past its parent.");

                boxes.Add(new Box(type, offset + headerLength, (int)(offset + size)));
                offset = (int)(offset + size);
            }

            return boxes;
        }

        private static bool IsType(ReadOnlySpan<byte> data, int offset, string type)
        {
            return data[offset] == type[0] && data[offset + 1] == type[1] && data[offset + 2] == type[2] && data[offset + 3] == type[3];
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
        {
            return ((ulong)ReadUInt32(data, offset) << 32) | ReadUInt32(data, offset + 4);
        }

        private static uint FourCc(string type)
        {
            return ((uint)type[0] << 24) | ((uint)type[1] << 16) | ((uint)type[2] << 8) | type[3];
        }

        private static string FourCcToString(uint type)
        {
            return new string(new[] { (char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF) });
        }

        private static InvalidImageContentException Invalid(string message)
        {
            return new InvalidImageContentException("The AVIF file is invalid: " + message, ImageFormat.Avif);
        }

        private static UnsupportedImageFeatureException Unsupported(string feature)
        {
            return new UnsupportedImageFeatureException($"The AVIF feature '{feature}' is not supported.", ImageFormat.Avif, feature);
        }

        private readonly struct Box
        {
            public Box(uint type, int start, int end) :this()
            {
                Type = type;
                Start = start;
                End = end;
            }

            public uint Type { get; }

            public int Start { get; }

            public int End { get; }
        }

        private readonly struct Property
        {
            public Property(uint type, int start, int end) :this()
            {
                Type = type;
                Start = start;
                End = end;
            }

            public uint Type { get; }

            public int Start { get; }

            public int End { get; }
        }

        private readonly struct Extent
        {
            public Extent(ulong offset, ulong length) :this()
            {
                Offset = offset;
                Length = length;
            }

            public ulong Offset { get; }

            public ulong Length { get; }
        }

        private sealed class ItemLocation
        {
            public ItemLocation(int constructionMethod, List<Extent> extents)
            {
                ConstructionMethod = constructionMethod;
                Extents = extents;
            }

            public int ConstructionMethod { get; }

            public List<Extent> Extents { get; }
        }

        private struct Reader
        {
            private readonly byte[] _data;
            private readonly int _end;

            public Reader(byte[] data, int start, int end) :this()
            {
                _data = data;
                Position = start;
                _end = end;
            }

            public int Position { get; private set; }

            public byte ReadByte()
            {
                Ensure(1);
                return _data[Position++];
            }

            public uint ReadUInt16()
            {
                Ensure(2);
                var value = (uint)((_data[Position] << 8) | _data[Position + 1]);
                Position += 2;
                return value;
            }

            public uint ReadUInt32()
            {
                Ensure(4);
                var value = AvifContainer.ReadUInt32(_data, Position);
                Position += 4;
                return value;
            }

            public ulong ReadSized(int size)
            {
                switch (size)
                {
                    case 0:
                        return 0;
                    case 4:
                        return ReadUInt32();
                    case 8:
                        Ensure(8);
                        var value = ReadUInt64(_data, Position);
                        Position += 8;
                        return value;
                    default:
                        throw Invalid($"a field size of {size} bytes is not valid.");
                }
            }

            public void Skip(int count)
            {
                Ensure(count);
                Position += count;
            }

            private void Ensure(int count)
            {
                if (count < 0 || _end - Position < count)
                    throw Invalid("a box is truncated.");
            }
        }
    }

    internal readonly struct CleanAperture
    {
        public CleanAperture(int widthN, int widthD, int heightN, int heightD, int horizOffN, int horizOffD, int vertOffN, int vertOffD) :this()
        {
            WidthN = widthN;
            WidthD = widthD;
            HeightN = heightN;
            HeightD = heightD;
            HorizOffN = horizOffN;
            HorizOffD = horizOffD;
            VertOffN = vertOffN;
            VertOffD = vertOffD;
        }

        public int WidthN { get; }

        public int WidthD { get; }

        public int HeightN { get; }

        public int HeightD { get; }

        public int HorizOffN { get; }

        public int HorizOffD { get; }

        public int VertOffN { get; }

        public int VertOffD { get; }
    }
}

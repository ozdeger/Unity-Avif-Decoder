#nullable enable
using System;
using System.Collections.Generic;

namespace AvifDecoding
{
    // The boxes of an AVIF file (ISO/IEC 14496-12, ISO/IEC 23008-12, ISO/IEC 23000-22) that decoding needs: the items of the
    // 'meta' box with their properties, references and entity groups, and the tracks of the 'moov' box with their sample
    // tables. AvifImageGraph turns them into images.
    internal sealed class AvifContainer
    {
        private const int MaxBoxDepth = 12;
        private const int MaxItems = 65536;
        private const int MaxProperties = 65536;
        private const int MaxExtents = 4096;
        private const int MaxTracks = 64;
        private const int MaxSamples = 1 << 20;
        private const int MaxEntityGroups = 4096;

        private readonly byte[] _data;
        private readonly List<AvifBox> _properties = new List<AvifBox>();
        private int _idatOffset = -1;
        private int _idatLength;
        private uint _movieTimescale;
        private ulong _movieDuration;
        private bool _hasMovieFragments;
        private Dictionary<(uint Type, uint Target), List<AvifItem>>? _referrers;
        private readonly Dictionary<uint, byte[]> _itemData = new Dictionary<uint, byte[]>();
        private long _itemBytes;
        private static readonly List<AvifItem> NoItems = new List<AvifItem>();
        private readonly List<AvifBox> _movieFragments = new List<AvifBox>();
        private readonly Dictionary<uint, (uint DescriptionIndex, uint Duration, uint Size, uint Flags)> _trackDefaults = new Dictionary<uint, (uint, uint, uint, uint)>();

        private AvifContainer(byte[] data)
        {
            _data = data;
        }

        public byte[] Data => _data;

        public uint MajorBrand { get; private set; }

        public List<uint> CompatibleBrands { get; } = new List<uint>();

        public bool HasMeta { get; private set; }

        public bool HasPrimaryItem { get; private set; }

        public uint PrimaryItemId { get; private set; }

        public Dictionary<uint, AvifItem> Items { get; } = new Dictionary<uint, AvifItem>();

        public List<AvifEntityGroup> EntityGroups { get; } = new List<AvifEntityGroup>();

        public List<AvifTrack> Tracks { get; } = new List<AvifTrack>();

        public bool HasMovieFragments => _hasMovieFragments;

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
            container.ParseTopLevel();
            return container;
        }

        // The items that refer to an item with a reference of a type, in the order of the items
        public List<AvifItem> GetItemsReferencing(uint itemId, uint referenceType)
        {
            if (_referrers == null)
            {
                // Built once: a lookup per tile of a large grid must not scan every item
                _referrers = new Dictionary<(uint, uint), List<AvifItem>>();
                var ordered = new List<AvifItem>(Items.Values);
                ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
                foreach (var item in ordered)
                {
                    foreach (var (type, target) in item.EnumerateReferences())
                    {
                        if (!_referrers.TryGetValue((type, target), out var list))
                        {
                            list = new List<AvifItem>();
                            _referrers[(type, target)] = list;
                        }

                        if (list.Count == 0 || list[list.Count - 1] != item)
                            list.Add(item);
                    }
                }
            }

            return _referrers.TryGetValue((referenceType, itemId), out var found) ? found : NoItems;
        }

        // The nodes that AvifImageGraph built from the items of this file
        public Dictionary<(uint ItemId, bool WithAlpha), AvifImageNode> BuiltNodes { get; } = new Dictionary<(uint, bool), AvifImageNode>();

        // The data of an item is read once. Items do not share data in a valid file, so together they may not hold more than
        // twice the file: extents that overlap cannot multiply the memory that the file takes.
        public byte[] ReadItemData(AvifItem item, int depth = 0)
        {
            if (_itemData.TryGetValue(item.Id, out var cached))
                return cached;

            var data = ReadItemDataCore(item, depth);
            _itemBytes += data.Length;
            if (_itemBytes > (2L * _data.Length) + 65536)
                throw Invalid("the items hold more data than the file.");

            _itemData[item.Id] = data;
            return data;
        }

        private byte[] ReadItemDataCore(AvifItem item, int depth)
        {
            var location = item.Location ?? throw Invalid($"item {item.Id} has no location.");
            if (depth > 4)
                throw Invalid("the item locations refer to each other too deeply.");

            var ranges = new List<DataRange>(location.Extents.Count);
            long total = 0;
            foreach (var extent in location.Extents)
            {
                var range = ResolveExtent(item, location, extent, depth);
                ranges.Add(range);
                total += range.Length;
            }

            if (total > int.MaxValue)
                throw Invalid("the item data is out of range.");

            var payload = new byte[total];
            var written = 0;
            foreach (var range in ranges)
            {
                Buffer.BlockCopy(range.Data, range.Start, payload, written, range.Length);
                written += range.Length;
            }

            return payload;
        }

        // Construction method 0 reads the file, 1 the 'idat' box, and 2 the data of the items that the 'iloc' references of
        // the item point to (the extent index picks one, 1-based; 0 when there is no index)
        private DataRange ResolveExtent(AvifItem item, AvifItemLocation location, AvifExtent extent, int depth)
        {
            byte[] data;
            long start;
            long limit;
            switch (location.ConstructionMethod)
            {
                case 0:
                    data = _data;
                    start = (long)Math.Min(extent.Offset + location.BaseOffset, (ulong)long.MaxValue);
                    limit = _data.Length;
                    break;
                case 1:
                    if (_idatOffset < 0)
                        throw Invalid($"item {item.Id} is in an 'idat' box that the file does not have.");

                    data = _data;
                    var offset = extent.Offset + location.BaseOffset;
                    if (offset > (ulong)_idatLength)
                        throw Invalid("an item extent is out of the 'idat' box.");

                    start = _idatOffset + (long)offset;
                    limit = _idatOffset + _idatLength;
                    break;
                case 2:
                    var targets = item.GetReferences(AvifFourCc.Iloc);
                    var index = extent.Index == 0 ? 0 : (int)Math.Min(extent.Index - 1, int.MaxValue);
                    if (index >= targets.Count || !Items.TryGetValue(targets[index], out var target))
                        throw Invalid($"an extent of item {item.Id} refers to an item that does not exist.");

                    data = ReadItemData(target, depth + 1);
                    var targetOffset = extent.Offset + location.BaseOffset;
                    if (targetOffset > (ulong)data.Length)
                        throw Invalid("an item extent is out of the item it refers to.");

                    start = (long)targetOffset;
                    limit = data.Length;
                    break;
                default:
                    throw Unsupported($"item construction method {location.ConstructionMethod}");
            }

            if (start > limit)
                throw Invalid("an item extent is out of range.");

            var length = extent.Length == 0 ? (ulong)(limit - start) : extent.Length;
            if (length > (ulong)(limit - start))
                throw Invalid("an item extent is out of range.");

            return new DataRange(data, (int)start, (int)length);
        }

        public AvifBox GetProperty(int index) => _properties[index];

        private void ParseTopLevel()
        {
            foreach (var box in EnumerateBoxes(0, _data.Length, 0))
            {
                if (box.Type == AvifFourCc.Ftyp)
                    ParseFileType(box);
                else if (box.Type == AvifFourCc.Meta)
                    ParseMeta(box, 1);
                else if (box.Type == AvifFourCc.Moov)
                    ParseMovie(box, 1);
                else if (box.Type == AvifFourCc.Moof)
                    _movieFragments.Add(box);
            }

            foreach (var fragment in _movieFragments)
                ParseMovieFragment(fragment);
        }

        // ISO/IEC 14496-12, 8.8: each track fragment adds runs of samples to its track, with the defaults of trex and tfhd
        private void ParseMovieFragment(AvifBox moof)
        {
            _hasMovieFragments = true;
            var moofStart = moof.Start - 8;
            long previousEnd = moofStart;
            foreach (var traf in EnumerateBoxes(moof.Start, moof.End, 1))
            {
                if (traf.Type != AvifFourCc.Traf)
                    continue;

                AvifTrack? track = null;
                long baseOffset = moofStart;
                uint descriptionIndex = 1, duration = 0, size = 0, flags = 0;
                var children = EnumerateBoxes(traf.Start, traf.End, 2);
                foreach (var child in children)
                {
                    if (child.Type != AvifFourCc.Tfhd)
                        continue;

                    var reader = new AvifReader(_data, child.Start, child.End);
                    reader.Skip(1);
                    var tfhdFlags = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
                    var trackId = reader.ReadUInt32();
                    track = Tracks.Find(t => t.Id == trackId);
                    if (_trackDefaults.TryGetValue(trackId, out var defaults))
                        (descriptionIndex, duration, size, flags) = defaults;

                    baseOffset = (tfhdFlags & 0x20000) != 0 ? moofStart : previousEnd;
                    if ((tfhdFlags & 0x01) != 0)
                        baseOffset = (long)Math.Min(reader.ReadUInt64(), long.MaxValue);
                    if ((tfhdFlags & 0x02) != 0)
                        descriptionIndex = reader.ReadUInt32();
                    if ((tfhdFlags & 0x08) != 0)
                        duration = reader.ReadUInt32();
                    if ((tfhdFlags & 0x10) != 0)
                        size = reader.ReadUInt32();
                    if ((tfhdFlags & 0x20) != 0)
                        flags = reader.ReadUInt32();
                }

                if (track == null)
                    throw Invalid("a track fragment refers to a track that does not exist.");

                var dataOffset = baseOffset;
                foreach (var child in children)
                {
                    if (child.Type != AvifFourCc.Trun)
                        continue;

                    var reader = new AvifReader(_data, child.Start, child.End);
                    reader.Skip(1);
                    var trunFlags = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
                    var count = reader.ReadUInt32();
                    if (track.Samples.Count + (long)count > MaxSamples)
                        throw Invalid("a track has too many samples.");
                    if ((trunFlags & 0x01) != 0)
                        dataOffset = baseOffset + reader.ReadInt32();

                    var firstFlags = (trunFlags & 0x04) != 0 ? reader.ReadUInt32() : (uint?)null;
                    var perSample = ((trunFlags & 0x100) != 0 ? 4 : 0) + ((trunFlags & 0x200) != 0 ? 4 : 0) + ((trunFlags & 0x400) != 0 ? 4 : 0) + ((trunFlags & 0x800) != 0 ? 4 : 0);
                    if (perSample > 0 && count > (uint)(reader.Remaining / perSample))
                        throw Invalid("a track run is truncated.");

                    for (var i = 0u; i < count; i++)
                    {
                        var sampleDuration = (trunFlags & 0x100) != 0 ? reader.ReadUInt32() : duration;
                        var sampleSize = (trunFlags & 0x200) != 0 ? reader.ReadUInt32() : size;
                        var sampleFlags = (trunFlags & 0x400) != 0 ? reader.ReadUInt32() : i == 0 && firstFlags.HasValue ? firstFlags.Value : flags;
                        if ((trunFlags & 0x800) != 0)
                            reader.Skip(4);

                        if (dataOffset < 0 || dataOffset > _data.Length || sampleSize > _data.Length - dataOffset)
                            throw Invalid("a sample of a track fragment is out of the file.");

                        // sample_is_non_sync_sample is bit 16 of the sample flags
                        track.Samples.Add(new AvifSample((int)dataOffset, (int)sampleSize, sampleDuration, (sampleFlags & 0x10000) == 0, (int)descriptionIndex));
                        dataOffset += sampleSize;
                    }
                }

                previousEnd = dataOffset;
            }
        }

        private void ParseFileType(AvifBox box)
        {
            var reader = new AvifReader(_data, box.Start, box.End);
            MajorBrand = reader.ReadUInt32();
            reader.Skip(4);
            while (reader.Remaining >= 4)
                CompatibleBrands.Add(reader.ReadUInt32());
        }

        private void ParseMeta(AvifBox box, int depth)
        {
            if (HasMeta)
                throw Invalid("the file has several 'meta' boxes.");

            HasMeta = true;
            foreach (var child in EnumerateBoxes(box.Start + 4, box.End, depth))
            {
                if (child.Type == AvifFourCc.Pitm)
                    ParsePrimaryItem(child);
                else if (child.Type == AvifFourCc.Iloc)
                    ParseItemLocations(child);
                else if (child.Type == AvifFourCc.Iinf)
                    ParseItemInfos(child, depth + 1);
                else if (child.Type == AvifFourCc.Iprp)
                    ParseItemProperties(child, depth + 1);
                else if (child.Type == AvifFourCc.Iref)
                    ParseItemReferences(child, depth + 1);
                else if (child.Type == AvifFourCc.Grpl)
                    ParseEntityGroups(child, depth + 1);
                else if (child.Type == AvifFourCc.Idat)
                {
                    _idatOffset = child.Start;
                    _idatLength = child.End - child.Start;
                }
            }
        }

        private AvifItem GetOrAddItem(uint id)
        {
            if (!Items.TryGetValue(id, out var item))
            {
                if (Items.Count >= MaxItems)
                    throw Invalid("the file has too many items.");

                item = new AvifItem(id);
                Items[id] = item;
            }

            return item;
        }

        private void ParsePrimaryItem(AvifBox box)
        {
            var reader = new AvifReader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(3);
            PrimaryItemId = version == 0 ? reader.ReadUInt16() : reader.ReadUInt32();
            HasPrimaryItem = true;
        }

        private void ParseItemLocations(AvifBox box)
        {
            var reader = new AvifReader(_data, box.Start, box.End);
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

                // data_reference_index: 0 is this file, the only data the decoder reads
                var dataReference = reader.ReadUInt16();
                if (dataReference != 0)
                    throw Unsupported("item data in another file");

                var baseOffset = reader.ReadSized(baseOffsetSize);
                var extentCount = (int)reader.ReadUInt16();
                if (extentCount > MaxExtents)
                    throw Invalid("an item has too many extents.");

                var extents = new List<AvifExtent>(extentCount);
                for (var e = 0; e < extentCount; e++)
                {
                    var index = indexSize > 0 ? reader.ReadSized(indexSize) : 0;
                    var extentOffset = reader.ReadSized(offsetSize);
                    var extentLength = reader.ReadSized(lengthSize);
                    extents.Add(new AvifExtent(index, extentOffset, extentLength));
                }

                var item = GetOrAddItem(itemId);
                if (item.Location != null)
                    throw Invalid($"item {itemId} has several locations.");

                item.Location = new AvifItemLocation(constructionMethod, baseOffset, extents);
            }
        }

        private void ParseItemInfos(AvifBox box, int depth)
        {
            var reader = new AvifReader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(3);
            var entryCount = version == 0 ? reader.ReadUInt16() : reader.ReadUInt32();
            if (entryCount > MaxItems)
                throw Invalid("the file has too many items.");

            foreach (var entry in EnumerateBoxes(reader.Position, box.End, depth))
            {
                if (entry.Type != AvifFourCc.Infe)
                    continue;

                var infe = new AvifReader(_data, entry.Start, entry.End);
                var infeVersion = infe.ReadByte();
                infe.Skip(2);
                var flags = infe.ReadByte();
                if (infeVersion < 2)
                    continue;

                var itemId = infeVersion == 2 ? infe.ReadUInt16() : infe.ReadUInt32();
                infe.Skip(2);
                var item = GetOrAddItem(itemId);
                item.Type = infe.ReadUInt32();
                item.Hidden = (flags & 1) != 0;
                item.Name = infe.ReadString();
                if (item.Type == AvifFourCc.Mime)
                    item.ContentType = infe.ReadString();
                else if (item.Type == AvifFourCc.Uri)
                    item.ContentType = infe.ReadString();
            }
        }

        private void ParseItemProperties(AvifBox box, int depth)
        {
            foreach (var child in EnumerateBoxes(box.Start, box.End, depth))
            {
                if (child.Type == AvifFourCc.Ipco)
                {
                    foreach (var property in EnumerateBoxes(child.Start, child.End, depth + 1))
                    {
                        if (_properties.Count >= MaxProperties)
                            throw Invalid("the file has too many properties.");

                        _properties.Add(property);
                    }
                }
                else if (child.Type == AvifFourCc.Ipma)
                {
                    ParseAssociations(child);
                }
            }
        }

        // ipma may come before ipco, so the associations keep the indices and the items resolve them when they are read
        private void ParseAssociations(AvifBox box)
        {
            var reader = new AvifReader(_data, box.Start, box.End);
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
                var item = GetOrAddItem(itemId);
                for (var a = 0; a < count; a++)
                {
                    int value;
                    bool essential;
                    if ((flags & 1) != 0)
                    {
                        var raw = reader.ReadUInt16();
                        essential = (raw & 0x8000) != 0;
                        value = (int)(raw & 0x7FFF);
                    }
                    else
                    {
                        var raw = reader.ReadByte();
                        essential = (raw & 0x80) != 0;
                        value = raw & 0x7F;
                    }

                    // Index 0 means that no property is associated
                    if (value != 0)
                        item.PropertyAssociations.Add(new AvifPropertyAssociation(value, essential));
                }
            }
        }

        // Resolves the property associations of an item into boxes, in their order
        public List<AvifItemProperty> GetProperties(AvifItem item)
        {
            if (item.ResolvedProperties != null)
                return item.ResolvedProperties;

            var properties = new List<AvifItemProperty>(item.PropertyAssociations.Count);
            foreach (var association in item.PropertyAssociations)
            {
                if (association.Index > _properties.Count)
                    throw Invalid("an item property association is out of range.");

                properties.Add(new AvifItemProperty(_properties[association.Index - 1], association.Essential));
            }

            item.ResolvedProperties = properties;
            return properties;
        }

        // Every reference type (ISO/IEC 14496-12, 8.11.12): dimg, auxl, prem, cdsc, thmb, base, iloc, ...
        private void ParseItemReferences(AvifBox box, int depth)
        {
            var reader = new AvifReader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(3);
            foreach (var reference in EnumerateBoxes(reader.Position, box.End, depth))
            {
                var entry = new AvifReader(_data, reference.Start, reference.End);
                var fromItem = version == 0 ? entry.ReadUInt16() : entry.ReadUInt32();
                var count = (int)entry.ReadUInt16();
                var targets = new List<uint>(count);
                for (var i = 0; i < count; i++)
                    targets.Add(version == 0 ? entry.ReadUInt16() : entry.ReadUInt32());

                var item = GetOrAddItem(fromItem);
                item.AddReferences(reference.Type, targets);
                if (item.ReferenceCount > MaxItems)
                    throw Invalid($"item {fromItem} has too many references.");
            }
        }

        // ISO/IEC 14496-12, 8.15.3: each child box is an entity group of the grouping type of its box type
        private void ParseEntityGroups(AvifBox box, int depth)
        {
            foreach (var group in EnumerateBoxes(box.Start, box.End, depth))
            {
                if (EntityGroups.Count >= MaxEntityGroups)
                    throw Invalid("the file has too many entity groups.");

                var reader = new AvifReader(_data, group.Start, group.End);
                reader.Skip(4);
                var groupId = reader.ReadUInt32();
                var count = reader.ReadUInt32();
                if (count > MaxItems)
                    throw Invalid("an entity group has too many entities.");

                var entities = new List<uint>((int)count);
                for (var i = 0u; i < count; i++)
                    entities.Add(reader.ReadUInt32());

                EntityGroups.Add(new AvifEntityGroup(group.Type, groupId, entities));
            }
        }

        private void ParseMovie(AvifBox box, int depth)
        {
            foreach (var child in EnumerateBoxes(box.Start, box.End, depth))
            {
                if (child.Type == AvifFourCc.Mvhd)
                {
                    var reader = new AvifReader(_data, child.Start, child.End);
                    var version = reader.ReadByte();
                    reader.Skip(3);
                    reader.Skip(version == 1 ? 16 : 8);
                    _movieTimescale = reader.ReadUInt32();
                    _movieDuration = version == 1 ? reader.ReadUInt64() : reader.ReadUInt32();
                }
                else if (child.Type == AvifFourCc.Trak)
                {
                    if (Tracks.Count >= MaxTracks)
                        throw Invalid("the file has too many tracks.");

                    Tracks.Add(ParseTrack(child, depth + 1));
                }
                else if (child.Type == AvifFourCc.Mvex)
                {
                    foreach (var extends in EnumerateBoxes(child.Start, child.End, depth + 1))
                    {
                        if (extends.Type != AvifFourCc.Trex)
                            continue;

                        var reader = new AvifReader(_data, extends.Start, extends.End);
                        reader.Skip(4);
                        var trackId = reader.ReadUInt32();
                        _trackDefaults[trackId] = (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
                    }
                }
            }

            foreach (var track in Tracks)
                track.MovieTimescale = _movieTimescale;
        }

        private AvifTrack ParseTrack(AvifBox box, int depth)
        {
            var track = new AvifTrack();
            var hasHeader = false;
            var hasEdits = false;
            foreach (var child in EnumerateBoxes(box.Start, box.End, depth))
            {
                if (child.Type == AvifFourCc.Tkhd)
                {
                    var reader = new AvifReader(_data, child.Start, child.End);
                    var version = reader.ReadByte();
                    reader.Skip(3);
                    reader.Skip(version == 1 ? 16 : 8);
                    track.Id = reader.ReadUInt32();
                    reader.Skip(4);
                    track.TrackDuration = version == 1 ? reader.ReadUInt64() : ExtendIndefinite(reader.ReadUInt32());
                    reader.Skip(8 + 8 + 36);
                    track.Width = (int)(reader.ReadUInt32() >> 16);
                    track.Height = (int)(reader.ReadUInt32() >> 16);
                    hasHeader = true;
                }
                else if (child.Type == AvifFourCc.Tref)
                {
                    foreach (var reference in EnumerateBoxes(child.Start, child.End, depth + 1))
                    {
                        var reader = new AvifReader(_data, reference.Start, reference.End);
                        var ids = new List<uint>();
                        while (reader.Remaining >= 4 && ids.Count < MaxTracks)
                            ids.Add(reader.ReadUInt32());

                        track.AddReferences(reference.Type, ids);
                    }
                }
                else if (child.Type == AvifFourCc.Edts)
                {
                    hasEdits = true;
                    foreach (var edit in EnumerateBoxes(child.Start, child.End, depth + 1))
                    {
                        if (edit.Type == AvifFourCc.Elst)
                            ParseEditList(track, edit);
                    }
                }
                else if (child.Type == AvifFourCc.Mdia)
                {
                    ParseMedia(track, child, depth + 1);
                }
            }

            if (!hasHeader)
                throw Invalid("a track has no 'tkhd' box.");

            // ISO/IEC 23008-12, 9.6.1: a repeated edit list plays as many times as it fits in the track duration
            if (!hasEdits)
            {
                track.RepetitionCount = AvifTrack.RepetitionUnknown;
            }
            else if (!track.IsRepeating)
            {
                track.RepetitionCount = 0;
            }
            else if (track.TrackDuration == ulong.MaxValue)
            {
                track.RepetitionCount = AvifTrack.RepetitionInfinite;
            }
            else if (track.TrackDuration == 0 || track.SegmentDuration == 0)
            {
                throw Invalid("a repeated track has no duration.");
            }
            else
            {
                var count = (track.TrackDuration / track.SegmentDuration) + (track.TrackDuration % track.SegmentDuration != 0 ? 1UL : 0UL) - 1;
                track.RepetitionCount = count > int.MaxValue ? AvifTrack.RepetitionInfinite : (int)count;
            }

            return track;
        }

        private static ulong ExtendIndefinite(uint value) => value == uint.MaxValue ? ulong.MaxValue : value;

        private void ParseEditList(AvifTrack track, AvifBox box)
        {
            var reader = new AvifReader(_data, box.Start, box.End);
            var version = reader.ReadByte();
            reader.Skip(2);
            var flags = reader.ReadByte();
            track.IsRepeating = (flags & 1) != 0;
            var entryCount = reader.ReadUInt32();
            if (entryCount == 0)
                return;

            track.SegmentDuration = version == 1 ? reader.ReadUInt64() : reader.ReadUInt32();
        }

        private void ParseMedia(AvifTrack track, AvifBox box, int depth)
        {
            foreach (var child in EnumerateBoxes(box.Start, box.End, depth))
            {
                if (child.Type == AvifFourCc.Mdhd)
                {
                    var reader = new AvifReader(_data, child.Start, child.End);
                    var version = reader.ReadByte();
                    reader.Skip(3);
                    reader.Skip(version == 1 ? 16 : 8);
                    track.MediaTimescale = reader.ReadUInt32();
                    track.MediaDuration = version == 1 ? reader.ReadUInt64() : ExtendIndefinite(reader.ReadUInt32());
                }
                else if (child.Type == AvifFourCc.Hdlr)
                {
                    var reader = new AvifReader(_data, child.Start, child.End);
                    reader.Skip(8);
                    track.HandlerType = reader.ReadUInt32();
                }
                else if (child.Type == AvifFourCc.Minf)
                {
                    foreach (var info in EnumerateBoxes(child.Start, child.End, depth + 1))
                    {
                        if (info.Type == AvifFourCc.Stbl)
                            ParseSampleTable(track, info, depth + 2);
                    }
                }
            }
        }

        private void ParseSampleTable(AvifTrack track, AvifBox box, int depth)
        {
            var chunkOffsets = new List<ulong>();
            var chunkRuns = new List<(uint FirstChunk, uint SamplesPerChunk, uint DescriptionIndex)>();
            var sizes = new List<uint>();
            var durations = new List<(uint Count, uint Delta)>();
            HashSet<uint>? syncSamples = null;
            foreach (var child in EnumerateBoxes(box.Start, box.End, depth))
            {
                var reader = new AvifReader(_data, child.Start, child.End);
                if (child.Type == AvifFourCc.Stsd)
                {
                    reader.Skip(4);
                    var count = reader.ReadUInt32();
                    var index = 0u;
                    foreach (var entry in EnumerateBoxes(reader.Position, child.End, depth + 1))
                    {
                        if (index++ >= count)
                            break;

                        track.SampleEntries.Add(ParseSampleEntry(entry, depth + 2));
                    }
                }
                else if (child.Type == AvifFourCc.Stco || child.Type == AvifFourCc.Co64)
                {
                    reader.Skip(4);
                    var count = reader.ReadUInt32();
                    if (count > MaxSamples || count > (uint)(reader.Remaining / (child.Type == AvifFourCc.Stco ? 4 : 8)))
                        throw Invalid("a chunk offset table is too large.");

                    for (var i = 0u; i < count; i++)
                        chunkOffsets.Add(child.Type == AvifFourCc.Stco ? reader.ReadUInt32() : reader.ReadUInt64());
                }
                else if (child.Type == AvifFourCc.Stsc)
                {
                    reader.Skip(4);
                    var count = reader.ReadUInt32();
                    if (count > (uint)(reader.Remaining / 12))
                        throw Invalid("a sample-to-chunk table is too large.");

                    for (var i = 0u; i < count; i++)
                        chunkRuns.Add((reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
                }
                else if (child.Type == AvifFourCc.Stsz)
                {
                    reader.Skip(4);
                    var size = reader.ReadUInt32();
                    var count = reader.ReadUInt32();
                    if (count > MaxSamples)
                        throw Invalid("a track has too many samples.");
                    if (size == 0 && count > (uint)(reader.Remaining / 4))
                        throw Invalid("a sample size table is too large.");

                    for (var i = 0u; i < count; i++)
                        sizes.Add(size != 0 ? size : reader.ReadUInt32());
                }
                else if (child.Type == AvifFourCc.Stz2)
                {
                    reader.Skip(7);
                    var fieldSize = reader.ReadByte();
                    var count = reader.ReadUInt32();
                    if (fieldSize != 4 && fieldSize != 8 && fieldSize != 16)
                        throw Invalid("a compact sample size table has an invalid field size.");
                    if (count > MaxSamples || count > (uint)(reader.Remaining * 8L / fieldSize))
                        throw Invalid("a compact sample size table is too large.");

                    for (var i = 0u; i < count; i++)
                    {
                        if (fieldSize == 16)
                        {
                            sizes.Add(reader.ReadUInt16());
                        }
                        else if (fieldSize == 8)
                        {
                            sizes.Add(reader.ReadByte());
                        }
                        else
                        {
                            var pair = reader.ReadByte();
                            sizes.Add((uint)(pair >> 4));
                            if (++i < count)
                                sizes.Add((uint)(pair & 15));
                        }
                    }
                }
                else if (child.Type == AvifFourCc.Stts)
                {
                    reader.Skip(4);
                    var count = reader.ReadUInt32();
                    if (count > (uint)(reader.Remaining / 8))
                        throw Invalid("a time-to-sample table is too large.");

                    for (var i = 0u; i < count; i++)
                        durations.Add((reader.ReadUInt32(), reader.ReadUInt32()));
                }
                else if (child.Type == AvifFourCc.Stss)
                {
                    reader.Skip(4);
                    var count = reader.ReadUInt32();
                    if (count > (uint)(reader.Remaining / 4))
                        throw Invalid("a sync sample table is too large.");

                    syncSamples = new HashSet<uint>();
                    for (var i = 0u; i < count; i++)
                        syncSamples.Add(reader.ReadUInt32());
                }
            }

            // The samples in decoding order: each run of chunks holds the same number of samples, stored back to back
            var sampleIndex = 0;
            for (var run = 0; run < chunkRuns.Count && sampleIndex < sizes.Count; run++)
            {
                var (firstChunk, samplesPerChunk, descriptionIndex) = chunkRuns[run];
                var lastChunk = run + 1 < chunkRuns.Count ? chunkRuns[run + 1].FirstChunk - 1 : (uint)chunkOffsets.Count;
                if (firstChunk == 0 || lastChunk > chunkOffsets.Count || (run + 1 < chunkRuns.Count && chunkRuns[run + 1].FirstChunk <= firstChunk))
                    throw Invalid("a sample-to-chunk table is not valid.");

                for (var chunk = firstChunk; chunk <= lastChunk && sampleIndex < sizes.Count; chunk++)
                {
                    var offset = chunkOffsets[(int)chunk - 1];
                    for (var s = 0u; s < samplesPerChunk && sampleIndex < sizes.Count; s++)
                    {
                        var size = sizes[sampleIndex];
                        if (offset > (ulong)_data.Length || size > (ulong)_data.Length - offset)
                            throw Invalid("a sample is out of the file.");

                        track.Samples.Add(new AvifSample((int)offset, (int)size, 0, syncSamples == null || syncSamples.Contains((uint)sampleIndex + 1), (int)descriptionIndex));
                        offset += size;
                        sampleIndex++;
                    }
                }
            }

            if (track.Samples.Count != sizes.Count)
                throw Invalid("the chunks of a track do not hold all of its samples.");

            // Durations: samples past the end of stts keep the delta of its last run
            var position = 0;
            foreach (var (count, delta) in durations)
            {
                for (var i = 0u; i < count && position < track.Samples.Count; i++, position++)
                    track.Samples[position] = track.Samples[position].WithDuration(delta);
            }

            for (; position < track.Samples.Count && durations.Count > 0; position++)
                track.Samples[position] = track.Samples[position].WithDuration(durations[durations.Count - 1].Delta);
        }

        // VisualSampleEntry (ISO/IEC 14496-12, 12.1.3): 78 bytes of fields, then the boxes that describe the samples
        private AvifSampleEntry ParseSampleEntry(AvifBox box, int depth)
        {
            var entry = new AvifSampleEntry(box.Type);
            if (box.End - box.Start < 78)
                return entry;

            var reader = new AvifReader(_data, box.Start, box.End);
            reader.Skip(24);
            entry.Width = (int)reader.ReadUInt16();
            entry.Height = (int)reader.ReadUInt16();
            foreach (var child in EnumerateBoxes(box.Start + 78, box.End, depth))
                entry.Boxes.Add(child);

            return entry;
        }

        private List<AvifBox> EnumerateBoxes(int start, int end, int depth)
        {
            if (depth > MaxBoxDepth)
                throw Invalid("the boxes are nested too deeply.");

            var boxes = new List<AvifBox>();
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

                if (type == AvifFourCc.Uuid)
                    headerLength += 16;

                if (size < headerLength || size > end - offset)
                    throw Invalid("a box extends past its parent.");

                boxes.Add(new AvifBox(type, offset + headerLength, (int)(offset + size)));
                offset = (int)(offset + size);
            }

            return boxes;
        }

        private static bool IsType(ReadOnlySpan<byte> data, int offset, string type)
        {
            return data[offset] == type[0] && data[offset + 1] == type[1] && data[offset + 2] == type[2] && data[offset + 3] == type[3];
        }

        internal static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        internal static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
        {
            return ((ulong)ReadUInt32(data, offset) << 32) | ReadUInt32(data, offset + 4);
        }

        internal static InvalidImageContentException Invalid(string message)
        {
            return new InvalidImageContentException("The AVIF file is invalid: " + message, ImageFormat.Avif);
        }

        internal static UnsupportedImageFeatureException Unsupported(string feature)
        {
            return new UnsupportedImageFeatureException($"The AVIF feature '{feature}' is not supported.", ImageFormat.Avif, feature);
        }

        private readonly struct DataRange
        {
            public DataRange(byte[] data, int start, int length) :this()
            {
                Data = data;
                Start = start;
                Length = length;
            }

            public byte[] Data { get; }

            public int Start { get; }

            public int Length { get; }
        }
    }

    // A box: its type and its body, after the header
    internal readonly struct AvifBox
    {
        public AvifBox(uint type, int start, int end) :this()
        {
            Type = type;
            Start = start;
            End = end;
        }

        public uint Type { get; }

        public int Start { get; }

        public int End { get; }
    }

    internal readonly struct AvifPropertyAssociation
    {
        public AvifPropertyAssociation(int index, bool essential) :this()
        {
            Index = index;
            Essential = essential;
        }

        public int Index { get; }

        public bool Essential { get; }
    }

    internal readonly struct AvifItemProperty
    {
        public AvifItemProperty(AvifBox box, bool essential) :this()
        {
            Box = box;
            Essential = essential;
        }

        public AvifBox Box { get; }

        public uint Type => Box.Type;

        public bool Essential { get; }
    }

    internal readonly struct AvifExtent
    {
        public AvifExtent(ulong index, ulong offset, ulong length) :this()
        {
            Index = index;
            Offset = offset;
            Length = length;
        }

        public ulong Index { get; }

        public ulong Offset { get; }

        public ulong Length { get; }
    }

    internal sealed class AvifItemLocation
    {
        public AvifItemLocation(int constructionMethod, ulong baseOffset, List<AvifExtent> extents)
        {
            ConstructionMethod = constructionMethod;
            BaseOffset = baseOffset;
            Extents = extents;
        }

        public int ConstructionMethod { get; }

        public ulong BaseOffset { get; }

        public List<AvifExtent> Extents { get; }
    }

    internal sealed class AvifItem
    {
        private readonly List<(uint Type, List<uint> Targets)> _references = new List<(uint, List<uint>)>();

        public AvifItem(uint id)
        {
            Id = id;
        }

        public uint Id { get; }

        public uint Type { get; set; }

        public string Name { get; set; } = "";

        // The MIME type of a 'mime' item, or the URI type of a 'uri ' item
        public string ContentType { get; set; } = "";

        public bool Hidden { get; set; }

        public AvifItemLocation? Location { get; set; }

        public List<AvifPropertyAssociation> PropertyAssociations { get; } = new List<AvifPropertyAssociation>();

        public List<AvifItemProperty>? ResolvedProperties { get; set; }

        public int ReferenceCount { get; private set; }

        public void AddReferences(uint type, List<uint> targets)
        {
            _references.Add((type, targets));
            ReferenceCount += targets.Count;
        }

        // The items that this item refers to with a reference type, in order; several boxes of one type are concatenated
        public List<uint> GetReferences(uint type)
        {
            List<uint>? found = null;
            foreach (var (referenceType, targets) in _references)
            {
                if (referenceType != type)
                    continue;

                if (found == null)
                {
                    found = targets;
                }
                else
                {
                    found = new List<uint>(found);
                    found.AddRange(targets);
                }
            }

            return found ?? EmptyIds;
        }

        public IEnumerable<(uint Type, uint Target)> EnumerateReferences()
        {
            foreach (var (type, targets) in _references)
            {
                foreach (var target in targets)
                    yield return (type, target);
            }
        }

        public int CountReferenceBoxes(uint type)
        {
            var count = 0;
            foreach (var (referenceType, _) in _references)
            {
                if (referenceType == type)
                    count++;
            }

            return count;
        }

        private static readonly List<uint> EmptyIds = new List<uint>();
    }

    internal sealed class AvifEntityGroup
    {
        public AvifEntityGroup(uint type, uint id, List<uint> entities)
        {
            Type = type;
            Id = id;
            Entities = entities;
        }

        public uint Type { get; }

        public uint Id { get; }

        public List<uint> Entities { get; }
    }

    internal sealed class AvifSampleEntry
    {
        public AvifSampleEntry(uint type)
        {
            Type = type;
        }

        public uint Type { get; }

        public int Width { get; set; }

        public int Height { get; set; }

        // The boxes of the sample entry: av1C, colr, pasp, clli, mdcv, ccst, auxi, ...
        public List<AvifBox> Boxes { get; } = new List<AvifBox>();

        public AvifBox? Find(uint type)
        {
            foreach (var box in Boxes)
            {
                if (box.Type == type)
                    return box;
            }

            return null;
        }
    }

    internal readonly struct AvifSample
    {
        public AvifSample(int offset, int size, uint duration, bool isSync, int descriptionIndex) :this()
        {
            Offset = offset;
            Size = size;
            Duration = duration;
            IsSync = isSync;
            DescriptionIndex = descriptionIndex;
        }

        public int Offset { get; }

        public int Size { get; }

        // In the media timescale
        public uint Duration { get; }

        public bool IsSync { get; }

        public int DescriptionIndex { get; }

        public AvifSample WithDuration(uint duration) => new AvifSample(Offset, Size, duration, IsSync, DescriptionIndex);
    }

    internal sealed class AvifTrack
    {
        public const int RepetitionInfinite = -1;
        public const int RepetitionUnknown = -2;

        private readonly List<(uint Type, List<uint> Ids)> _references = new List<(uint, List<uint>)>();

        public uint Id { get; set; }

        public uint HandlerType { get; set; }

        public uint MovieTimescale { get; set; }

        public uint MediaTimescale { get; set; }

        public ulong MediaDuration { get; set; }

        // In the movie timescale; ulong.MaxValue when it is indefinite
        public ulong TrackDuration { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public bool IsRepeating { get; set; }

        public ulong SegmentDuration { get; set; }

        public int RepetitionCount { get; set; }

        public List<AvifSampleEntry> SampleEntries { get; } = new List<AvifSampleEntry>();

        public List<AvifSample> Samples { get; } = new List<AvifSample>();

        public void AddReferences(uint type, List<uint> ids) => _references.Add((type, ids));

        public List<uint> GetReferences(uint type)
        {
            var found = new List<uint>();
            foreach (var (referenceType, ids) in _references)
            {
                if (referenceType == type)
                    found.AddRange(ids);
            }

            return found;
        }
    }

    // A bounded big-endian reader over a range of a buffer: reading past the end is an invalid file
    internal struct AvifReader
    {
        private readonly byte[] _data;
        private readonly int _end;

        public AvifReader(byte[] data, int start, int end) :this()
        {
            _data = data;
            Position = start;
            _end = end;
        }

        public int Position { get; private set; }

        public int Remaining => _end - Position;

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

        public ulong ReadUInt64()
        {
            Ensure(8);
            var value = AvifContainer.ReadUInt64(_data, Position);
            Position += 8;
            return value;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public ulong ReadSized(int size)
        {
            switch (size)
            {
                case 0:
                    return 0;
                case 4:
                    return ReadUInt32();
                case 8:
                    return ReadUInt64();
                default:
                    throw AvifContainer.Invalid($"a field size of {size} bytes is not valid.");
            }
        }

        // A null-terminated UTF-8 string; one that runs to the end of the box is accepted
        public string ReadString()
        {
            var start = Position;
            while (Position < _end && _data[Position] != 0)
                Position++;

            var text = System.Text.Encoding.UTF8.GetString(_data, start, Position - start);
            if (Position < _end)
                Position++;

            return text;
        }

        public byte[] ReadBytes(int count)
        {
            Ensure(count);
            var bytes = new byte[count];
            Buffer.BlockCopy(_data, Position, bytes, 0, count);
            Position += count;
            return bytes;
        }

        public byte[] ReadRemaining() => ReadBytes(Remaining);

        public void Skip(int count)
        {
            Ensure(count);
            Position += count;
        }

        private void Ensure(int count)
        {
            if (count < 0 || _end - Position < count)
                throw AvifContainer.Invalid("a box is truncated.");
        }
    }

    internal static class AvifFourCc
    {
        public static readonly uint Ftyp = Make("ftyp");
        public static readonly uint Meta = Make("meta");
        public static readonly uint Moov = Make("moov");
        public static readonly uint Moof = Make("moof");
        public static readonly uint Mvex = Make("mvex");
        public static readonly uint Trex = Make("trex");
        public static readonly uint Traf = Make("traf");
        public static readonly uint Tfhd = Make("tfhd");
        public static readonly uint Trun = Make("trun");
        public static readonly uint Uuid = Make("uuid");
        public static readonly uint Pitm = Make("pitm");
        public static readonly uint Iloc = Make("iloc");
        public static readonly uint Iinf = Make("iinf");
        public static readonly uint Infe = Make("infe");
        public static readonly uint Iprp = Make("iprp");
        public static readonly uint Ipco = Make("ipco");
        public static readonly uint Ipma = Make("ipma");
        public static readonly uint Iref = Make("iref");
        public static readonly uint Idat = Make("idat");
        public static readonly uint Grpl = Make("grpl");
        public static readonly uint Mime = Make("mime");
        public static readonly uint Uri = Make("uri ");
        public static readonly uint Mvhd = Make("mvhd");
        public static readonly uint Trak = Make("trak");
        public static readonly uint Tkhd = Make("tkhd");
        public static readonly uint Tref = Make("tref");
        public static readonly uint Edts = Make("edts");
        public static readonly uint Elst = Make("elst");
        public static readonly uint Mdia = Make("mdia");
        public static readonly uint Mdhd = Make("mdhd");
        public static readonly uint Hdlr = Make("hdlr");
        public static readonly uint Minf = Make("minf");
        public static readonly uint Stbl = Make("stbl");
        public static readonly uint Stsd = Make("stsd");
        public static readonly uint Stco = Make("stco");
        public static readonly uint Co64 = Make("co64");
        public static readonly uint Stsc = Make("stsc");
        public static readonly uint Stsz = Make("stsz");
        public static readonly uint Stz2 = Make("stz2");
        public static readonly uint Stts = Make("stts");
        public static readonly uint Stss = Make("stss");

        // Item types, properties and references
        public static readonly uint Av01 = Make("av01");
        public static readonly uint Grid = Make("grid");
        public static readonly uint Iovl = Make("iovl");
        public static readonly uint Iden = Make("iden");
        public static readonly uint Sato = Make("sato");
        public static readonly uint Tmap = Make("tmap");
        public static readonly uint Exif = Make("Exif");
        public static readonly uint Ispe = Make("ispe");
        public static readonly uint Pixi = Make("pixi");
        public static readonly uint Colr = Make("colr");
        public static readonly uint Nclx = Make("nclx");
        public static readonly uint Prof = Make("prof");
        public static readonly uint Ricc = Make("rICC");
        public static readonly uint Av1C = Make("av1C");
        public static readonly uint AuxC = Make("auxC");
        public static readonly uint Auxi = Make("auxi");
        public static readonly uint Clap = Make("clap");
        public static readonly uint Irot = Make("irot");
        public static readonly uint Imir = Make("imir");
        public static readonly uint Pasp = Make("pasp");
        public static readonly uint Clli = Make("clli");
        public static readonly uint Mdcv = Make("mdcv");
        public static readonly uint Cclv = Make("cclv");
        public static readonly uint Amve = Make("amve");
        public static readonly uint A1op = Make("a1op");
        public static readonly uint Lsel = Make("lsel");
        public static readonly uint A1lx = Make("a1lx");
        public static readonly uint Dimg = Make("dimg");
        public static readonly uint Auxl = Make("auxl");
        public static readonly uint Prem = Make("prem");
        public static readonly uint Cdsc = Make("cdsc");
        public static readonly uint Thmb = Make("thmb");
        public static readonly uint Altr = Make("altr");
        public static readonly uint Pict = Make("pict");
        public static readonly uint Auxv = Make("auxv");
        public static readonly uint Vide = Make("vide");
        public static readonly uint Avis = Make("avis");
        public static readonly uint Avif = Make("avif");

        public static uint Make(string type)
        {
            return ((uint)type[0] << 24) | ((uint)type[1] << 16) | ((uint)type[2] << 8) | type[3];
        }

        public static string ToText(uint type)
        {
            return new string(new[] { (char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF) });
        }
    }
}

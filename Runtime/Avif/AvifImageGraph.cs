#nullable enable
using System;
using System.Collections.Generic;

namespace AvifDecoding
{
    internal enum AvifImageKind
    {
        // An AV1 image item (av01)
        Coded,

        // ISO/IEC 23008-12, 6.6.2.3: tiles side by side
        Grid,

        // ISO/IEC 23008-12, 6.6.2.2: input images composited on a canvas
        Overlay,

        // ISO/IEC 23008-12, 6.6.2.1: an input image with other transformative properties
        Identity,

        // ISO/IEC 23008-12, sample transform (sato): a per-sample expression over input images
        SampleTransform,
    }

    // The colour description of an image: the nclx code points and the ICC profile of its colr properties
    internal sealed class AvifColorProperties
    {
        public bool HasNclx { get; set; }

        public int ColorPrimaries { get; set; } = 2;

        public int TransferCharacteristics { get; set; } = 2;

        public int MatrixCoefficients { get; set; } = 2;

        public bool FullRange { get; set; }

        public byte[]? IccProfile { get; set; }
    }

    // The fields of an AV1 codec configuration (av1C)
    internal sealed class AvifAv1Config
    {
        public int Profile { get; set; }

        public int Level { get; set; }

        public int Tier { get; set; }

        public int BitDepth { get; set; }

        public bool Monochrome { get; set; }

        public int SubsamplingX { get; set; }

        public int SubsamplingY { get; set; }

        public int ChromaSamplePosition { get; set; }

        // The configOBUs: usually the sequence header
        public byte[] ConfigObus { get; set; } = Array.Empty<byte>();
    }

    // One image of the file and what it is made from: an item, or a sample of a track for sequences
    internal sealed class AvifImageNode
    {
        public AvifImageNode(AvifImageKind kind, uint itemId)
        {
            Kind = kind;
            ItemId = itemId;
        }

        public AvifImageKind Kind { get; }

        public uint ItemId { get; }

        // ispe, 0 when absent
        public int Width { get; set; }

        public int Height { get; set; }

        // Coded: the AV1 payload, the operating point (a1op) and the layer to output (lsel), -1 for the highest
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public int OperatingPoint { get; set; }

        public int LayerId { get; set; } = -1;

        public uint[]? LayerSizes { get; set; }

        public AvifAv1Config? Config { get; set; }

        // Derived images: the inputs in the order of the dimg reference
        public List<AvifImageNode> Inputs { get; } = new List<AvifImageNode>();

        // Grid and overlay: the output size; grid: the rows and columns
        public int Rows { get; set; }

        public int Columns { get; set; }

        public int OutputWidth { get; set; }

        public int OutputHeight { get; set; }

        // Overlay: the fill colour (16-bit R, G, B, A) and the offset of each input
        public ushort[] FillColor { get; set; } = new ushort[4];

        public int[] OffsetX { get; set; } = Array.Empty<int>();

        public int[] OffsetY { get; set; } = Array.Empty<int>();

        // Sample transform: the tokens and the depth of the result
        public AvifSampleTransform? Transform { get; set; }

        public AvifColorProperties Color { get; } = new AvifColorProperties();

        public int[]? PixiDepths { get; set; }

        // Transformative properties, in the order of HEIF: clap, irot, imir
        public bool HasCleanAperture { get; set; }

        public CleanAperture Crop { get; set; }

        public int Rotation { get; set; }

        public int MirrorAxis { get; set; } = -1;

        // Descriptive properties
        public uint PixelAspectH { get; set; } = 1;

        public uint PixelAspectV { get; set; } = 1;

        public AvifContentLightLevel? ContentLightLevel { get; set; }

        public AvifMasteringDisplay? MasteringDisplay { get; set; }

        // The alpha auxiliary image (auxl with an alpha auxC), and whether the colour is premultiplied by it (prem)
        public AvifImageNode? Alpha { get; set; }

        public bool AlphaPremultiplied { get; set; }

        public bool IsDerived => Kind != AvifImageKind.Coded;

        // A grid whose tiles are all coded images is joined in YCbCr, like libavif; other derivations work in RGBA
        public bool IsPlanar => Kind == AvifImageKind.Coded || (Kind == AvifImageKind.Grid && Inputs.TrueForAll(input => input.Kind == AvifImageKind.Coded))
            || Kind == AvifImageKind.SampleTransform;
    }

    internal sealed class AvifSampleTransform
    {
        public AvifSampleTransform(int bitDepth, List<AvifSampleTransformToken> tokens)
        {
            BitDepth = bitDepth;
            Tokens = tokens;
        }

        // The depth of the samples of the result (pixi)
        public int BitDepth { get; }

        public List<AvifSampleTransformToken> Tokens { get; }
    }

    internal readonly struct AvifSampleTransformToken
    {
        public const int Constant = 0;

        public AvifSampleTransformToken(int kind, int value) :this()
        {
            Kind = kind;
            Value = value;
        }

        // 0 constant, 1-32 input image index (1-based), 64-67 unary operators, 128-137 binary operators
        public int Kind { get; }

        public int Value { get; }
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

    // Builds the image of an item from the container: what it is derived from, its properties, its alpha
    internal static class AvifImageGraph
    {
        private const int MaxDepth = 8;
        private const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";
        private const string HevcAlphaUrn = "urn:mpeg:hevc:2015:auxid:1";

        // The properties that a reader may not ignore when they are essential: the ones this decoder applies or that do not
        // change the pixels. An essential property of another type makes the image unsupported (ISO/IEC 23008-12, 9.3.1).
        private static readonly HashSet<uint> UnderstoodProperties = new HashSet<uint>
        {
            AvifFourCc.Ispe, AvifFourCc.Pixi, AvifFourCc.Colr, AvifFourCc.Av1C, AvifFourCc.AuxC, AvifFourCc.Clap, AvifFourCc.Irot,
            AvifFourCc.Imir, AvifFourCc.Pasp, AvifFourCc.Clli, AvifFourCc.Mdcv, AvifFourCc.Cclv, AvifFourCc.Amve, AvifFourCc.A1op,
            AvifFourCc.Lsel, AvifFourCc.A1lx,
        };

        public static bool IsAlphaAuxiliary(AvifContainer container, AvifItem item)
        {
            var urn = GetAuxiliaryType(container, item);
            return urn == AlphaUrn || urn == HevcAlphaUrn;
        }

        // The aux_type of the auxC property of an item, or null
        public static string? GetAuxiliaryType(AvifContainer container, AvifItem item)
        {
            foreach (var property in container.GetProperties(item))
            {
                if (property.Type != AvifFourCc.AuxC)
                    continue;

                var reader = new AvifReader(container.Data, property.Box.Start, property.Box.End);
                reader.Skip(4);
                return reader.ReadString();
            }

            return null;
        }

        public static AvifImageNode Build(AvifContainer container, uint itemId)
        {
            return Build(container, itemId, 0, new HashSet<uint>(), findAlpha: true);
        }

        // An item that several derivations refer to is built once: the nodes are shared, so the work is bounded by the number
        // of items, not by the number of paths through them
        private static AvifImageNode Build(AvifContainer container, uint itemId, int depth, HashSet<uint> path, bool findAlpha)
        {
            if (depth > MaxDepth)
                throw AvifContainer.Invalid("the derived images are nested too deeply.");
            if (!container.Items.TryGetValue(itemId, out var item) || item.Type == 0)
                throw AvifContainer.Invalid($"item {itemId} has no 'infe' entry.");
            if (path.Contains(itemId))
                throw AvifContainer.Invalid($"item {itemId} is derived from itself.");
            if (container.BuiltNodes.TryGetValue((itemId, findAlpha), out var built))
                return built;

            path.Add(itemId);

            try
            {
                var properties = container.GetProperties(item);
                foreach (var property in properties)
                {
                    if (property.Essential && !UnderstoodProperties.Contains(property.Type))
                        throw AvifContainer.Unsupported($"the essential property '{AvifFourCc.ToText(property.Type)}'");
                }

                AvifImageNode node;
                if (item.Type == AvifFourCc.Av01)
                {
                    node = new AvifImageNode(AvifImageKind.Coded, itemId) { Payload = container.ReadItemData(item) };
                }
                else if (item.Type == AvifFourCc.Grid)
                {
                    node = new AvifImageNode(AvifImageKind.Grid, itemId);
                    ParseGrid(node, container.ReadItemData(item));
                }
                else if (item.Type == AvifFourCc.Iovl)
                {
                    node = new AvifImageNode(AvifImageKind.Overlay, itemId);
                    ParseOverlay(node, container.ReadItemData(item), item.GetReferences(AvifFourCc.Dimg).Count);
                }
                else if (item.Type == AvifFourCc.Iden)
                {
                    node = new AvifImageNode(AvifImageKind.Identity, itemId);
                }
                else if (item.Type == AvifFourCc.Sato)
                {
                    node = new AvifImageNode(AvifImageKind.SampleTransform, itemId);
                }
                else
                {
                    throw AvifContainer.Unsupported($"an image item of type '{AvifFourCc.ToText(item.Type)}'");
                }

                foreach (var property in properties)
                    ApplyProperty(container, node, property.Box);

                if (node.IsDerived)
                    AddInputs(container, item, node, depth, path);

                if (node.Kind == AvifImageKind.SampleTransform)
                    node.Transform = ParseSampleTransform(container.ReadItemData(item), node);

                if (findAlpha)
                    FindAlpha(container, item, node, depth, path);

                container.BuiltNodes[(itemId, findAlpha)] = node;
                return node;
            }
            finally
            {
                path.Remove(itemId);
            }
        }

        private static void AddInputs(AvifContainer container, AvifItem item, AvifImageNode node, int depth, HashSet<uint> path)
        {
            var inputs = item.GetReferences(AvifFourCc.Dimg);

            switch (node.Kind)
            {
                case AvifImageKind.Grid:
                    if (inputs.Count != node.Rows * node.Columns)
                        throw AvifContainer.Invalid("the grid does not have one 'dimg' input per cell.");

                    var listed = new HashSet<uint>();
                    foreach (var input in inputs)
                    {
                        if (!listed.Add(input))
                            throw AvifContainer.Invalid("the grid lists a tile twice.");
                    }

                    break;
                case AvifImageKind.Overlay:
                    if (inputs.Count != node.OffsetX.Length)
                        throw AvifContainer.Invalid("the overlay does not have one offset per input.");

                    break;
                case AvifImageKind.Identity:
                    if (inputs.Count != 1)
                        throw AvifContainer.Invalid("an identity derivation has not exactly one input.");

                    break;
                case AvifImageKind.SampleTransform:
                    if (inputs.Count < 1 || inputs.Count > 32)
                        throw AvifContainer.Invalid("a sample transform has not 1 to 32 inputs.");

                    break;
            }

            foreach (var input in inputs)
            {
                // The tiles of a grid bring their alpha through the alpha of the grid (FindAlpha); the inputs of the other
                // derivations are rendered with theirs
                var withAlpha = node.Kind != AvifImageKind.Grid;
                node.Inputs.Add(Build(container, input, depth + 1, path, withAlpha));
            }
        }

        // The alpha auxiliary image of an item: an item with an auxl reference to it and an alpha auxC property. A grid without
        // one whose tiles all have one gets the grid of their alphas, which libavif accepts too.
        private static void FindAlpha(AvifContainer container, AvifItem item, AvifImageNode node, int depth, HashSet<uint> path)
        {
            foreach (var auxiliary in container.GetItemsReferencing(item.Id, AvifFourCc.Auxl))
            {
                if (!IsAlphaAuxiliary(container, auxiliary))
                    continue;

                node.Alpha = Build(container, auxiliary.Id, depth + 1, path, findAlpha: false);
                node.AlphaPremultiplied = item.GetReferences(AvifFourCc.Prem).Contains(auxiliary.Id);
                return;
            }

            if (node.Kind != AvifImageKind.Grid || node.Inputs.Count == 0)
                return;

            var alphaGrid = new AvifImageNode(AvifImageKind.Grid, 0)
            {
                Rows = node.Rows,
                Columns = node.Columns,
                OutputWidth = node.OutputWidth,
                OutputHeight = node.OutputHeight,
            };
            var premultiplied = 0;
            foreach (var tile in node.Inputs)
            {
                AvifItem? alphaItem = null;
                foreach (var auxiliary in container.GetItemsReferencing(tile.ItemId, AvifFourCc.Auxl))
                {
                    if (IsAlphaAuxiliary(container, auxiliary))
                    {
                        alphaItem = auxiliary;
                        break;
                    }
                }

                if (alphaItem == null)
                    return;

                alphaGrid.Inputs.Add(Build(container, alphaItem.Id, depth + 1, path, findAlpha: false));
                if (container.Items[tile.ItemId].GetReferences(AvifFourCc.Prem).Contains(alphaItem.Id))
                    premultiplied++;
            }

            node.Alpha = alphaGrid;
            node.AlphaPremultiplied = premultiplied == node.Inputs.Count;
        }

        // The image of a track: the boxes of its sample entry describe its frames as properties describe an item
        public static AvifImageNode BuildTrackNode(AvifContainer container, AvifTrack track, int descriptionIndex = 1)
        {
            if (descriptionIndex < 1 || descriptionIndex > track.SampleEntries.Count)
                throw AvifContainer.Invalid($"track {track.Id} has no sample description {descriptionIndex}.");

            var entry = track.SampleEntries[descriptionIndex - 1];
            if (entry.Type != AvifFourCc.Av01)
                throw AvifContainer.Unsupported($"a track of '{AvifFourCc.ToText(entry.Type)}' samples");

            var node = new AvifImageNode(AvifImageKind.Coded, 0) { Width = entry.Width, Height = entry.Height };
            foreach (var box in entry.Boxes)
                ApplyProperty(container, node, box);

            return node;
        }

        public static bool IsAlphaTrack(AvifContainer container, AvifTrack track)
        {
            if (track.SampleEntries.Count == 0)
                return false;

            var auxi = track.SampleEntries[0].Find(AvifFourCc.Auxi);
            if (auxi == null)
                return track.HandlerType == AvifFourCc.Auxv;

            var reader = new AvifReader(container.Data, auxi.Value.Start, auxi.Value.End);
            reader.Skip(4);
            var urn = reader.ReadString();
            return urn == AlphaUrn || urn == HevcAlphaUrn;
        }

        internal static void ApplyProperty(AvifContainer container, AvifImageNode node, AvifBox box)
        {
            var reader = new AvifReader(container.Data, box.Start, box.End);
            var type = box.Type;
            if (type == AvifFourCc.Ispe)
            {
                reader.Skip(4);
                node.Width = checked((int)reader.ReadUInt32());
                node.Height = checked((int)reader.ReadUInt32());
            }
            else if (type == AvifFourCc.Colr)
            {
                var colourType = reader.ReadUInt32();
                if (colourType == AvifFourCc.Nclx)
                {
                    if (node.Color.HasNclx)
                        return;

                    node.Color.HasNclx = true;
                    node.Color.ColorPrimaries = (int)reader.ReadUInt16();
                    node.Color.TransferCharacteristics = (int)reader.ReadUInt16();
                    node.Color.MatrixCoefficients = (int)reader.ReadUInt16();
                    node.Color.FullRange = (reader.ReadByte() & 0x80) != 0;
                }
                else if ((colourType == AvifFourCc.Prof || colourType == AvifFourCc.Ricc) && node.Color.IccProfile == null)
                {
                    node.Color.IccProfile = reader.ReadRemaining();
                }
            }
            else if (type == AvifFourCc.Pixi)
            {
                reader.Skip(4);
                var count = reader.ReadByte();
                var depths = new int[count];
                for (var i = 0; i < count; i++)
                    depths[i] = reader.ReadByte();

                node.PixiDepths = depths;
            }
            else if (type == AvifFourCc.Av1C)
            {
                node.Config = ParseAv1Config(ref reader);
            }
            else if (type == AvifFourCc.Clap)
            {
                node.Crop = new CleanAperture(
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                node.HasCleanAperture = true;
            }
            else if (type == AvifFourCc.Irot)
            {
                node.Rotation = reader.ReadByte() & 3;
            }
            else if (type == AvifFourCc.Imir)
            {
                node.MirrorAxis = reader.ReadByte() & 1;
            }
            else if (type == AvifFourCc.Pasp)
            {
                node.PixelAspectH = reader.ReadUInt32();
                node.PixelAspectV = reader.ReadUInt32();
            }
            else if (type == AvifFourCc.Clli)
            {
                node.ContentLightLevel = new AvifContentLightLevel((int)reader.ReadUInt16(), (int)reader.ReadUInt16());
            }
            else if (type == AvifFourCc.Mdcv)
            {
                node.MasteringDisplay = ParseMasteringDisplay(ref reader);
            }
            else if (type == AvifFourCc.A1op)
            {
                node.OperatingPoint = reader.ReadByte();
            }
            else if (type == AvifFourCc.Lsel)
            {
                var layer = (int)reader.ReadUInt16();
                node.LayerId = layer == 0xFFFF ? -1 : layer;
            }
            else if (type == AvifFourCc.A1lx)
            {
                var large = (reader.ReadByte() & 1) != 0;
                var sizes = new uint[3];
                for (var i = 0; i < 3; i++)
                    sizes[i] = large ? reader.ReadUInt32() : reader.ReadUInt16();

                node.LayerSizes = sizes;
            }
        }

        public static AvifAv1Config ParseAv1Config(ref AvifReader reader)
        {
            var marker = reader.ReadByte();
            if ((marker & 0x80) == 0 || (marker & 0x7F) != 1)
                throw AvifContainer.Invalid("the 'av1C' box has an unknown version.");

            var b1 = reader.ReadByte();
            var b2 = reader.ReadByte();
            reader.ReadByte();
            var highBitDepth = (b2 & 0x40) != 0;
            var twelveBit = (b2 & 0x20) != 0;
            return new AvifAv1Config
            {
                Profile = b1 >> 5,
                Level = b1 & 31,
                Tier = b2 >> 7,
                BitDepth = highBitDepth ? (twelveBit ? 12 : 10) : 8,
                Monochrome = (b2 & 0x10) != 0,
                SubsamplingX = (b2 >> 3) & 1,
                SubsamplingY = (b2 >> 2) & 1,
                ChromaSamplePosition = b2 & 3,
                ConfigObus = reader.ReadRemaining(),
            };
        }

        public static AvifMasteringDisplay ParseMasteringDisplay(ref AvifReader reader)
        {
            var primaries = new int[6];
            for (var i = 0; i < 6; i++)
                primaries[i] = (int)reader.ReadUInt16();

            var whiteX = (int)reader.ReadUInt16();
            var whiteY = (int)reader.ReadUInt16();
            var max = reader.ReadUInt32();
            var min = reader.ReadUInt32();
            return new AvifMasteringDisplay(primaries, whiteX, whiteY, max, min);
        }

        // ImageGrid (ISO/IEC 23008-12, 6.6.2.3.2)
        private static void ParseGrid(AvifImageNode node, byte[] data)
        {
            var reader = new AvifReader(data, 0, data.Length);
            var version = reader.ReadByte();
            if (version != 0)
                throw AvifContainer.Unsupported($"'grid' version {version}");

            var flags = reader.ReadByte();
            node.Rows = reader.ReadByte() + 1;
            node.Columns = reader.ReadByte() + 1;
            var width = (flags & 1) != 0 ? reader.ReadUInt32() : reader.ReadUInt16();
            var height = (flags & 1) != 0 ? reader.ReadUInt32() : reader.ReadUInt16();
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
                throw AvifContainer.Invalid("the grid has no valid output size.");

            node.OutputWidth = (int)width;
            node.OutputHeight = (int)height;
        }

        // ImageOverlay (ISO/IEC 23008-12, 6.6.2.2.2): the fill colour, the canvas size, then a signed offset per input
        private static void ParseOverlay(AvifImageNode node, byte[] data, int inputCount)
        {
            var reader = new AvifReader(data, 0, data.Length);
            var version = reader.ReadByte();
            if (version != 0)
                throw AvifContainer.Unsupported($"'iovl' version {version}");

            var large = (reader.ReadByte() & 1) != 0;
            for (var i = 0; i < 4; i++)
                node.FillColor[i] = (ushort)reader.ReadUInt16();

            var width = large ? reader.ReadUInt32() : reader.ReadUInt16();
            var height = large ? reader.ReadUInt32() : reader.ReadUInt16();
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
                throw AvifContainer.Invalid("the overlay has no valid output size.");

            node.OutputWidth = (int)width;
            node.OutputHeight = (int)height;
            if (inputCount > reader.Remaining / (large ? 8 : 4))
                throw AvifContainer.Invalid("the overlay does not have one offset per input.");

            node.OffsetX = new int[inputCount];
            node.OffsetY = new int[inputCount];
            for (var i = 0; i < inputCount; i++)
            {
                node.OffsetX[i] = large ? reader.ReadInt32() : (short)reader.ReadUInt16();
                node.OffsetY[i] = large ? reader.ReadInt32() : (short)reader.ReadUInt16();
            }
        }

        // SampleTransform (libavif's sato): version 0, 32-bit intermediate values, then the tokens of a postfix expression
        private static AvifSampleTransform ParseSampleTransform(byte[] data, AvifImageNode node)
        {
            var reader = new AvifReader(data, 0, data.Length);
            var header = reader.ReadByte();
            var version = header >> 6;
            var intermediateDepth = header & 3;
            if (version != 0)
                throw AvifContainer.Unsupported($"'sato' version {version}");
            if (intermediateDepth != 2)
                throw AvifContainer.Unsupported("a sample transform with intermediate values other than 32-bit");

            var count = reader.ReadByte();
            if (count == 0)
                throw AvifContainer.Invalid("the sample transform has no tokens.");

            var tokens = new List<AvifSampleTransformToken>(count);
            var stack = 0;
            for (var i = 0; i < count; i++)
            {
                var kind = (int)reader.ReadByte();
                if (kind == AvifSampleTransformToken.Constant)
                {
                    tokens.Add(new AvifSampleTransformToken(kind, reader.ReadInt32()));
                    stack++;
                }
                else if (kind >= 1 && kind <= 32)
                {
                    if (kind > node.Inputs.Count)
                        throw AvifContainer.Invalid("the sample transform refers to an input it does not have.");

                    tokens.Add(new AvifSampleTransformToken(kind, 0));
                    stack++;
                }
                else if (kind >= 64 && kind <= 67)
                {
                    if (stack < 1)
                        throw AvifContainer.Invalid("the sample transform expression is not valid.");

                    tokens.Add(new AvifSampleTransformToken(kind, 0));
                }
                else if (kind >= 128 && kind <= 137)
                {
                    if (stack < 2)
                        throw AvifContainer.Invalid("the sample transform expression is not valid.");

                    tokens.Add(new AvifSampleTransformToken(kind, 0));
                    stack--;
                }
                else
                {
                    throw AvifContainer.Unsupported($"the sample transform operator {kind}");
                }
            }

            if (stack != 1 || reader.Remaining != 0)
                throw AvifContainer.Invalid("the sample transform expression is not valid.");

            var depth = node.PixiDepths != null && node.PixiDepths.Length > 0 ? node.PixiDepths[0] : 0;
            if (depth != 8 && depth != 10 && depth != 12 && depth != 16)
                throw AvifContainer.Unsupported($"a sample transform result of {depth} bits");

            return new AvifSampleTransform(depth, tokens);
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace AvifDecoding
{
    // Decoded Y, Cb and Cr samples (or Y alone for monochrome), with the colour description of the bitstream
    internal sealed class AvifPlanes
    {
        public AvifPlanes(int width, int height, int bitDepth, bool monochrome, int subsamplingX, int subsamplingY)
        {
            Width = width;
            Height = height;
            BitDepth = bitDepth;
            Monochrome = monochrome;
            SubsamplingX = monochrome ? 0 : subsamplingX;
            SubsamplingY = monochrome ? 0 : subsamplingY;
            ChromaWidth = monochrome ? 0 : (width + SubsamplingX) >> SubsamplingX;
            ChromaHeight = monochrome ? 0 : (height + SubsamplingY) >> SubsamplingY;
            Y = Array.Empty<ushort>();
        }

        public int Width { get; }

        public int Height { get; }

        public int BitDepth { get; }

        public bool Monochrome { get; }

        public int SubsamplingX { get; }

        public int SubsamplingY { get; }

        public int ChromaWidth { get; }

        public int ChromaHeight { get; }

        public ushort[] Y { get; set; }

        public int YStride { get; set; }

        public ushort[]? U { get; set; }

        public ushort[]? V { get; set; }

        public int CStride { get; set; }

        // The colour description of the sequence header, which a colr property overrides
        public bool SequenceColorPresent { get; set; }

        public int SequencePrimaries { get; set; } = 2;

        public int SequenceTransfer { get; set; } = 2;

        public int SequenceMatrix { get; set; } = 2;

        public bool SequenceFullRange { get; set; }

        public static AvifPlanes Allocate(int width, int height, int bitDepth, bool monochrome, int subsamplingX, int subsamplingY)
        {
            var planes = new AvifPlanes(width, height, bitDepth, monochrome, subsamplingX, subsamplingY);
            planes.Y = new ushort[checked(width * height)];
            planes.YStride = width;
            if (!monochrome)
            {
                planes.U = new ushort[checked(planes.ChromaWidth * planes.ChromaHeight)];
                planes.V = new ushort[planes.U.Length];
                planes.CStride = planes.ChromaWidth;
            }

            return planes;
        }

        public void CopyColorDescription(AvifPlanes source)
        {
            SequenceColorPresent = source.SequenceColorPresent;
            SequencePrimaries = source.SequencePrimaries;
            SequenceTransfer = source.SequenceTransfer;
            SequenceMatrix = source.SequenceMatrix;
            SequenceFullRange = source.SequenceFullRange;
        }

        // The samples of a decoded frame, with its film grain applied
        public static AvifPlanes FromFrame(AllocationScope scope, Av1FrameBuffer frame)
        {
            var sequence = frame.Sequence;
            var luma = frame.GetPlane(0);
            var monochrome = frame.PlaneCount == 1;
            var planes = new AvifPlanes(luma.Width, luma.Height, sequence.BitDepth, monochrome, sequence.SubsamplingX, sequence.SubsamplingY)
            {
                SequenceColorPresent = sequence.ColorDescriptionPresent,
                SequencePrimaries = sequence.ColorPrimaries,
                SequenceTransfer = sequence.TransferCharacteristics,
                SequenceMatrix = sequence.MatrixCoefficients,
                SequenceFullRange = sequence.FullRange,
            };

            var filmGrain = frame.Header.FilmGrain;
            if (filmGrain != null)
            {
                ApplyFilmGrain(scope, frame, filmGrain, planes);
                return planes;
            }

            planes.Y = new ushort[checked(luma.Width * luma.Height)];
            planes.YStride = luma.Width;
            for (var row = 0; row < luma.Height; row++)
                luma.GetRow(row).CopyTo(planes.Y.AsSpan(row * luma.Width, luma.Width));

            if (!monochrome)
            {
                planes.CStride = planes.ChromaWidth;
                planes.U = CopyPlane(frame.GetPlane(1), planes.ChromaWidth, planes.ChromaHeight);
                planes.V = CopyPlane(frame.GetPlane(2), planes.ChromaWidth, planes.ChromaHeight);
            }

            return planes;
        }

        private static ushort[] CopyPlane(Av1Plane plane, int width, int height)
        {
            var samples = new ushort[checked(width * height)];
            for (var row = 0; row < height; row++)
                plane.GetRow(row).Slice(0, width).CopyTo(samples.AsSpan(row * width, width));

            return samples;
        }

        private static void ApplyFilmGrain(AllocationScope scope, Av1FrameBuffer frame, Av1FilmGrainParameters parameters, AvifPlanes planes)
        {
            var sequence = frame.Sequence;
            var luma = frame.GetPlane(0);
            var lumaStride = luma.Width;
            var y = new ushort[checked(lumaStride * (((luma.Height + Av1FilmGrain.StripeHeight - 1) / Av1FilmGrain.StripeHeight) * Av1FilmGrain.StripeHeight))];
            ushort[]? u = null;
            ushort[]? v = null;
            var chromaStride = 0;
            if (frame.PlaneCount > 1)
            {
                var chroma = frame.GetPlane(1);
                chromaStride = chroma.Width;
                var chromaRows = (y.Length / lumaStride) >> sequence.SubsamplingY;
                u = new ushort[checked(chromaStride * (chromaRows + 1))];
                v = new ushort[u.Length];
            }

            using var grain = new Av1FilmGrain(scope, sequence, parameters, luma.Width, luma.Height);
            for (var stripe = 0; stripe < grain.StripeCount; stripe++)
            {
                var lumaRows = y.AsSpan(stripe * Av1FilmGrain.StripeHeight * lumaStride);
                Span<ushort> uRows = default;
                Span<ushort> vRows = default;
                if (u != null)
                {
                    var offset = (stripe << (5 - sequence.SubsamplingY)) * chromaStride;
                    uRows = u.AsSpan(offset);
                    vRows = v.AsSpan(offset);
                }

                grain.ApplyStripe(frame, stripe, lumaRows, lumaStride, uRows, vRows, chromaStride);
            }

            planes.Y = y;
            planes.YStride = lumaStride;
            planes.U = u;
            planes.V = v;
            planes.CStride = chromaStride;
        }
    }

    // Decodes the images whose samples stay in Y, Cb and Cr: coded images, grids of coded tiles, and sample transforms
    internal static class AvifPlanarDecoder
    {
        public static AvifPlanes Decode(AvifImageNode node, AllocationScope scope)
        {
            switch (node.Kind)
            {
                case AvifImageKind.Coded:
                    return DecodeCoded(node, scope);
                case AvifImageKind.Grid:
                    return DecodeGrid(node, scope);
                case AvifImageKind.SampleTransform:
                    return DecodeSampleTransform(node, scope, alpha: false) ?? throw new InvalidOperationException();
                default:
                    throw new InvalidOperationException("The image is not planar.");
            }
        }

        // The alpha samples of a planar image: its alpha item, or for a sample transform the expression over the alpha of its
        // inputs when they all have one
        public static AvifPlanes? DecodeAlpha(AvifImageNode node, AllocationScope scope)
        {
            if (node.Alpha != null)
                return Decode(node.Alpha, scope);

            return node.Kind == AvifImageKind.SampleTransform ? DecodeSampleTransform(node, scope, alpha: true) : null;
        }

        public static AvifPlanes DecodeCoded(AvifImageNode node, AllocationScope scope)
        {
            using var decoder = new Av1Decoder(scope, node.OperatingPoint);
            if (node.LayerId >= 0)
                decoder.MaxSpatialLayer = Math.Min(node.LayerId, Av1ObuHeader.MaxSpatialId);

            decoder.SetInput(node.Payload, 0, node.Payload.Length);
            var frame = decoder.DecodeNextOutputFrame(CancellationToken.None) ?? throw AvifContainer.Invalid("the AV1 payload has no frame.");
            try
            {
                scope.Limits.ChargeImage(frame.GetPlane(0).Width, frame.GetPlane(0).Height);
                return AvifPlanes.FromFrame(scope, frame);
            }
            finally
            {
                frame.Release();
            }
        }

        // The tiles are decoded one at a time and copied into the planes of the grid, which the conversion to RGB then reads
        // across the edges between tiles, as libavif does. The tile rules are those of HEIF (ISO/IEC 23008-12, 6.6.2.3.1) and
        // MIAF (ISO/IEC 23000-22, 7.3.11.4.2) that libavif checks.
        private static AvifPlanes DecodeGrid(AvifImageNode grid, AllocationScope scope)
        {
            // Before any tile is decoded: the canvas of a grid that is not the image itself (an alpha grid, a gain map, an
            // input of an overlay) is held to the pixel limit too
            scope.Limits.EnsureCanvasWithinLimits(grid.OutputWidth, grid.OutputHeight);
            scope.Limits.ChargeImage(grid.OutputWidth, grid.OutputHeight);
            AvifPlanes? planes = null;
            AvifPlanes? first = null;
            for (var index = 0; index < grid.Inputs.Count; index++)
            {
                var tile = Decode(grid.Inputs[index], scope);
                if (first == null)
                {
                    first = tile;
                    planes = CreateGridPlanes(grid, tile);
                }
                else
                {
                    CheckTileMatches(first, tile);
                }

                Paste(tile, planes!, (index % grid.Columns) * first.Width, (index / grid.Columns) * first.Height);
            }

            return planes!;
        }

        private static AvifPlanes CreateGridPlanes(AvifImageNode grid, AvifPlanes tile)
        {
            var tileWidth = tile.Width;
            var tileHeight = tile.Height;
            if ((long)tileWidth * grid.Columns < grid.OutputWidth || (long)tileHeight * grid.Rows < grid.OutputHeight)
                throw AvifContainer.Invalid("the grid tiles do not cover the output.");
            if ((long)tileWidth * (grid.Columns - 1) >= grid.OutputWidth || (long)tileHeight * (grid.Rows - 1) >= grid.OutputHeight)
                throw AvifContainer.Invalid("the last column or row of grid tiles is outside the output.");
            if (tileWidth < 64 || tileHeight < 64)
                throw AvifContainer.Invalid("a grid tile is smaller than 64 x 64 pixels.");
            if ((tile.SubsamplingX != 0 && ((tileWidth | grid.OutputWidth) & 1) != 0) || (tile.SubsamplingY != 0 && ((tileHeight | grid.OutputHeight) & 1) != 0))
                throw AvifContainer.Invalid("a grid tile or output size is odd along a subsampled chroma direction.");

            var planes = AvifPlanes.Allocate(grid.OutputWidth, grid.OutputHeight, tile.BitDepth, tile.Monochrome, tile.SubsamplingX, tile.SubsamplingY);
            planes.CopyColorDescription(tile);
            return planes;
        }

        // Every tile has the size, the format and the colour description of the first one
        private static void CheckTileMatches(AvifPlanes first, AvifPlanes tile)
        {
            if (tile.Width != first.Width || tile.Height != first.Height || tile.Monochrome != first.Monochrome || tile.BitDepth != first.BitDepth
                || tile.SubsamplingX != first.SubsamplingX || tile.SubsamplingY != first.SubsamplingY || tile.SequenceFullRange != first.SequenceFullRange
                || tile.SequenceColorPresent != first.SequenceColorPresent
                || (tile.SequenceColorPresent && (tile.SequenceMatrix != first.SequenceMatrix || tile.SequencePrimaries != first.SequencePrimaries || tile.SequenceTransfer != first.SequenceTransfer)))
                throw AvifContainer.Invalid("the grid tiles differ in size or format.");
        }

        // Copies a tile into the grid planes with its top left corner at (left, top), cropped to the output size
        private static void Paste(AvifPlanes tile, AvifPlanes grid, int left, int top)
        {
            var width = Math.Min(tile.Width, grid.Width - left);
            var height = Math.Min(tile.Height, grid.Height - top);
            for (var row = 0; row < height; row++)
                tile.Y.AsSpan(row * tile.YStride, width).CopyTo(grid.Y.AsSpan(((top + row) * grid.YStride) + left, width));

            if (grid.U == null || grid.V == null || tile.U == null || tile.V == null)
                return;

            var chromaLeft = left >> grid.SubsamplingX;
            var chromaTop = top >> grid.SubsamplingY;
            var chromaWidth = Math.Min(tile.ChromaWidth, grid.ChromaWidth - chromaLeft);
            var chromaHeight = Math.Min(tile.ChromaHeight, grid.ChromaHeight - chromaTop);
            for (var row = 0; row < chromaHeight; row++)
            {
                var offset = ((chromaTop + row) * grid.CStride) + chromaLeft;
                tile.U.AsSpan(row * tile.CStride, chromaWidth).CopyTo(grid.U.AsSpan(offset, chromaWidth));
                tile.V.AsSpan(row * tile.CStride, chromaWidth).CopyTo(grid.V.AsSpan(offset, chromaWidth));
            }
        }

        // libavif's sample transform: the postfix expression evaluated on every sample of every plane, with 32-bit
        // intermediate values that saturate, and the result clamped to the depth of the image
        private static AvifPlanes? DecodeSampleTransform(AvifImageNode node, AllocationScope scope, bool alpha)
        {
            var transform = node.Transform ?? throw new InvalidOperationException();
            var inputs = new List<AvifPlanes>(node.Inputs.Count);
            foreach (var input in node.Inputs)
            {
                if (!input.IsPlanar)
                    throw AvifContainer.Unsupported("a sample transform of derived images");

                if (alpha)
                {
                    var inputAlpha = DecodeAlpha(input, scope);
                    if (inputAlpha == null)
                        return null;

                    inputs.Add(inputAlpha);
                }
                else
                {
                    inputs.Add(Decode(input, scope));
                }
            }

            var first = inputs[0];
            foreach (var planes in inputs)
            {
                if (planes.Width != first.Width || planes.Height != first.Height || planes.Monochrome != first.Monochrome
                    || planes.SubsamplingX != first.SubsamplingX || planes.SubsamplingY != first.SubsamplingY)
                    throw AvifContainer.Invalid("the inputs of a sample transform differ in size or format.");
            }

            var monochrome = alpha || first.Monochrome;
            scope.Limits.ChargeImage(first.Width, first.Height);
            var result = AvifPlanes.Allocate(first.Width, first.Height, transform.BitDepth, monochrome, first.SubsamplingX, first.SubsamplingY);
            result.CopyColorDescription(first);
            var stack = new int[transform.Tokens.Count];
            var max = (1 << transform.BitDepth) - 1;
            for (var plane = 0; plane < (monochrome ? 1 : 3); plane++)
            {
                var width = plane == 0 ? first.Width : first.ChromaWidth;
                var height = plane == 0 ? first.Height : first.ChromaHeight;
                var output = plane == 0 ? result.Y : plane == 1 ? result.U! : result.V!;
                var outputStride = plane == 0 ? result.YStride : result.CStride;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var size = 0;
                        foreach (var token in transform.Tokens)
                        {
                            if (token.Kind == AvifSampleTransformToken.Constant)
                            {
                                stack[size++] = token.Value;
                            }
                            else if (token.Kind <= 32)
                            {
                                var input = inputs[token.Kind - 1];
                                var samples = plane == 0 ? input.Y : plane == 1 ? input.U! : input.V!;
                                var stride = plane == 0 ? input.YStride : input.CStride;
                                stack[size++] = samples[(y * stride) + x];
                            }
                            else if (token.Kind < 128)
                            {
                                stack[size - 1] = ApplyUnary(token.Kind, stack[size - 1]);
                            }
                            else
                            {
                                size--;
                                stack[size - 1] = ApplyBinary(token.Kind, stack[size - 1], stack[size]);
                            }
                        }

                        var value = stack[0];
                        output[(y * outputStride) + x] = (ushort)(value < 0 ? 0 : value > max ? max : value);
                    }
                }
            }

            return result;
        }

        private static int Saturate(long value) => value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;

        private static int ApplyUnary(int kind, int operand)
        {
            switch (kind)
            {
                case 64:
                    return Saturate(-(long)operand);
                case 65:
                    return operand >= 0 ? operand : Saturate(-(long)operand);
                case 66:
                    return ~operand;
                default:
                    if (operand <= 0)
                        return 0;

                    var log2 = 0;
                    for (operand >>= 1; operand != 0; operand >>= 1)
                        log2++;

                    return log2;
            }
        }

        private static int ApplyBinary(int kind, int left, int right)
        {
            switch (kind)
            {
                case 128:
                    return Saturate((long)left + right);
                case 129:
                    return Saturate((long)left - right);
                case 130:
                    return Saturate((long)left * right);
                case 131:
                    return right == 0 ? left : Saturate((long)left / right);
                case 132:
                    return left & right;
                case 133:
                    return left | right;
                case 134:
                    return left ^ right;
                case 135:
                    return Power(left, right);
                case 136:
                    return left <= right ? left : right;
                default:
                    return left <= right ? right : left;
            }
        }

        private static int Power(int left, int right)
        {
            if (left == 0 || left == 1)
                return left;
            if (left == -1)
                return right % 2 == 0 ? 1 : -1;
            if (right == 0)
                return 1;
            if (right == 1)
                return left;
            if (right < 0)
                return 0;

            long result = left;
            for (var i = 1; i < right; i++)
            {
                result *= left;
                if (result < int.MinValue || result > int.MaxValue)
                    return left > 0 || right % 2 == 0 ? int.MaxValue : int.MinValue;
            }

            return (int)result;
        }
    }
}

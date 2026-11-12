#nullable enable
using System.Collections.Generic;
using System.Threading;
using System;

namespace AvifDecoding
{
    // The decoded planes to 8-bit RGB, in integer arithmetic so that every platform gives the same pixels: chroma upsampled
    // with a triangle filter (9, 3, 3, 1 over 16 for 4:2:0, 3, 1 over 4 for 4:2:2) at the precision of the planes, then the
    // matrix coefficients and the sample range of ITU-T H.273 with coefficients rounded to 16 fractional bits.
    internal static class AvifRgbConverter
    {
        private const long One = 1L << 16;
        private const long Half = 1L << 15;
        private const long D = 10000;
        private const int OutputMax = 255;

        public static AvifRgbImage Convert(AllocationScope scope, Av1FrameBuffer frame, AvifContainer container)
        {
            var sequence = frame.Sequence;
            var luma = frame.GetPlane(0);
            var width = luma.Width;
            var height = luma.Height;
            var monochrome = frame.PlaneCount == 1;
            var subX = monochrome ? 0 : sequence.SubsamplingX;
            var subY = monochrome ? 0 : sequence.SubsamplingY;
            var bitDepth = sequence.BitDepth;
            var matrix = container.HasColorDescription ? container.MatrixCoefficients : sequence.ColorDescriptionPresent ? sequence.MatrixCoefficients : Av1Constants.McUnspecified;
            var fullRange = container.HasColorDescription ? container.FullRange : sequence.FullRange;

            var filmGrain = frame.Header.FilmGrain;
            var planes = filmGrain != null ? ApplyFilmGrain(scope, frame, filmGrain) : Planes.FromFrame(frame);

            var pixels = new byte[checked(width * height * 3)];
            if (monochrome)
                ConvertMonochrome(planes, width, height, bitDepth, fullRange, pixels);
            else if (matrix == Av1Constants.McIdentity)
                ConvertIdentity(planes, width, height, bitDepth, subX, subY, fullRange, pixels);
            else
                ConvertMatrix(planes, width, height, bitDepth, subX, subY, matrix, fullRange, pixels);

            return new AvifRgbImage(width, height, pixels);
        }

        private static Planes ApplyFilmGrain(AllocationScope scope, Av1FrameBuffer frame, Av1FilmGrainParameters parameters)
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

            return new Planes(y, lumaStride, u, v, chromaStride, frame.PlaneCount > 1 ? frame.GetPlane(1).Width : 0, frame.PlaneCount > 1 ? frame.GetPlane(1).Height : 0);
        }

        private static void ConvertMonochrome(Planes planes, int width, int height, int bitDepth, bool fullRange, byte[] pixels)
        {
            GetRange(bitDepth, fullRange, out var yOffset, out var yRange, out _);
            var cY = RoundedDivide(OutputMax * One, yRange);
            for (var row = 0; row < height; row++)
            {
                var yRow = planes.Y.Slice(row * planes.YStride, width);
                var output = row * width * 3;
                for (var x = 0; x < width; x++)
                {
                    var gray = Clamp(((yRow[x] - yOffset) * cY + Half) >> 16);
                    pixels[output++] = gray;
                    pixels[output++] = gray;
                    pixels[output++] = gray;
                }
            }
        }

        // MatrixCoefficients 0: the planes are G, B and R in full range
        private static void ConvertIdentity(Planes planes, int width, int height, int bitDepth, int subX, int subY, bool fullRange, byte[] pixels)
        {
            if (subX != 0 || subY != 0 || !fullRange)
                throw new InvalidImageContentException("The AVIF file is invalid: the identity matrix requires 4:4:4 samples in full range.", ImageFormat.Avif);

            var max = (1 << bitDepth) - 1;
            for (var row = 0; row < height; row++)
            {
                var output = row * width * 3;
                for (var x = 0; x < width; x++)
                {
                    pixels[output++] = Scale(planes.V[(row * planes.CStride) + x], max);
                    pixels[output++] = Scale(planes.Y[(row * planes.YStride) + x], max);
                    pixels[output++] = Scale(planes.U[(row * planes.CStride) + x], max);
                }
            }
        }

        // A row at a time: the chroma of the row is upsampled into cb and cr, then every pixel is three sums. The upsampled
        // chroma keeps its fractional bits (up to 4) and is rounded only once, with the sum. The sums stay below 2^30: each
        // term is at most the output range times 2^20, a little more for limited-range samples
        private static void ConvertMatrix(Planes planes, int width, int height, int bitDepth, int subX, int subY, int matrix, bool fullRange, byte[] pixels)
        {
            GetWeights(matrix, out var kr, out var kb);
            GetRange(bitDepth, fullRange, out var yOffsetLong, out var yRange, out var cRange);
            var yOffset = (int)yOffsetLong;
            var cOffset = 1 << (bitDepth - 1);
            var kg = D - kr - kb;
            const long Scale = OutputMax * One * 2;
            var cY = (int)RoundedDivide(OutputMax * One, yRange);
            var cRV = (int)RoundedDivide(Scale * (D - kr), D * cRange);
            var cBU = (int)RoundedDivide(Scale * (D - kb), D * cRange);
            var cGU = (int)RoundedDivide(Scale * kb * (D - kb), D * kg * cRange);
            var cGV = (int)RoundedDivide(Scale * kr * (D - kr), D * kg * cRange);
            var chromaWidth = planes.ChromaWidth;
            var chromaHeight = planes.ChromaHeight;

            // The triangle filter in two steps: 3 parts of the nearest chroma row and 1 of the next one, then the same along
            // the row. The result is the chroma times 2^shift, and the final shift divides it out
            var shift = (subX != 0 ? 2 : 0) + (subY != 0 ? 2 : 0);
            var chromaOffset = cOffset << shift;
            var outputShift = 16 + shift;
            var outputRounding = 1 << (outputShift - 1);
            var cb = new int[width];
            var cr = new int[width];
            var rowU = new int[chromaWidth];
            var rowV = new int[chromaWidth];
            for (var row = 0; row < height; row++)
            {
                var cy = row >> subY;
                var nearU = planes.U.Slice(cy * planes.CStride, chromaWidth);
                var nearV = planes.V.Slice(cy * planes.CStride, chromaWidth);
                if (subY != 0)
                {
                    var ny = Math.Min(Math.Max((row & 1) == 0 ? cy - 1 : cy + 1, 0), chromaHeight - 1);
                    var farU = planes.U.Slice(ny * planes.CStride, chromaWidth);
                    var farV = planes.V.Slice(ny * planes.CStride, chromaWidth);
                    for (var k = 0; k < rowU.Length; k++)
                    {
                        rowU[k] = (3 * nearU[k]) + farU[k];
                        rowV[k] = (3 * nearV[k]) + farV[k];
                    }
                }
                else
                {
                    for (var k = 0; k < rowU.Length; k++)
                    {
                        rowU[k] = nearU[k];
                        rowV[k] = nearV[k];
                    }
                }

                if (subX != 0)
                {
                    for (var cx = 0; cx < rowU.Length; cx++)
                    {
                        var x = cx << 1;
                        var u = 3 * rowU[cx];
                        var v = 3 * rowV[cx];
                        var left = cx > 0 ? cx - 1 : 0;
                        cb[x] = u + rowU[left] - chromaOffset;
                        cr[x] = v + rowV[left] - chromaOffset;
                        if (x + 1 < cb.Length)
                        {
                            var right = cx + 1 < rowU.Length ? cx + 1 : cx;
                            cb[x + 1] = u + rowU[right] - chromaOffset;
                            cr[x + 1] = v + rowV[right] - chromaOffset;
                        }
                    }
                }
                else
                {
                    for (var x = 0; x < cb.Length; x++)
                    {
                        cb[x] = rowU[x] - chromaOffset;
                        cr[x] = rowV[x] - chromaOffset;
                    }
                }

                var luma = planes.Y.Slice(row * planes.YStride, width);
                var output = row * width * 3;
                for (var x = 0; x < luma.Length; x++)
                {
                    var luminance = (((luma[x] - yOffset) * cY) << shift) + outputRounding;
                    var b = cb[x];
                    var r = cr[x];
                    pixels[output] = ClampToByte((luminance + (cRV * r)) >> outputShift);
                    pixels[output + 1] = ClampToByte((luminance - (cGU * b) - (cGV * r)) >> outputShift);
                    pixels[output + 2] = ClampToByte((luminance + (cBU * b)) >> outputShift);
                    output += 3;
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static byte ClampToByte(int value) => (uint)value <= OutputMax ? (byte)value : value < 0 ? (byte)0 : (byte)OutputMax;

        private static void GetRange(int bitDepth, bool fullRange, out long yOffset, out long yRange, out long cRange)
        {
            if (fullRange)
            {
                yOffset = 0;
                yRange = (1L << bitDepth) - 1;
                cRange = yRange;
                return;
            }

            yOffset = 16L << (bitDepth - 8);
            yRange = 219L << (bitDepth - 8);
            cRange = 224L << (bitDepth - 8);
        }

        // KR and KB over 10000 (ITU-T H.273, table 4)
        private static void GetWeights(int matrix, out long kr, out long kb)
        {
            switch (matrix)
            {
                case Av1Constants.McBt709:
                    kr = 2126;
                    kb = 722;
                    return;
                case Av1Constants.McFcc:
                    kr = 3000;
                    kb = 1100;
                    return;
                case Av1Constants.McBt470BG:
                case Av1Constants.McBt601:
                case Av1Constants.McUnspecified:
                    kr = 2990;
                    kb = 1140;
                    return;
                case Av1Constants.McSmpte240:
                    kr = 2120;
                    kb = 870;
                    return;
                case Av1Constants.McBt2020Ncl:
                    kr = 2627;
                    kb = 593;
                    return;
                default:
                    throw new UnsupportedImageFeatureException($"The AVIF matrix coefficients {matrix} are not supported.", ImageFormat.Avif, "matrix coefficients");
            }
        }

        private static long RoundedDivide(long numerator, long denominator) => (numerator + (denominator / 2)) / denominator;

        private static byte Scale(int value, int max) => (byte)(((value * OutputMax) + (max / 2)) / max);

        private static byte Clamp(long value) => value < 0 ? (byte)0 : value > OutputMax ? (byte)OutputMax : (byte)value;

        // The planes and their strides: the rows of the frame, or of the planes that the film grain was applied to
        private readonly ref struct Planes
        {
            public Planes(ReadOnlySpan<ushort> y, int yStride, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v, int cStride, int chromaWidth, int chromaHeight) :this()
            {
                Y = y;
                YStride = yStride;
                U = u;
                V = v;
                CStride = cStride;
                ChromaWidth = chromaWidth;
                ChromaHeight = chromaHeight;
            }

            public ReadOnlySpan<ushort> Y { get; }

            public int YStride { get; }

            public ReadOnlySpan<ushort> U { get; }

            public ReadOnlySpan<ushort> V { get; }

            public int CStride { get; }

            public int ChromaWidth { get; }

            public int ChromaHeight { get; }

            public static Planes FromFrame(Av1FrameBuffer frame)
            {
                var luma = frame.GetPlane(0);
                if (frame.PlaneCount == 1)
                    return new Planes(luma.Samples, luma.Stride, default, default, 0, 0, 0);

                var chroma = frame.GetPlane(1);
                return new Planes(luma.Samples, luma.Stride, chroma.Samples, frame.GetPlane(2).Samples, chroma.Stride, chroma.Width, chroma.Height);
            }
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;

namespace AvifDecoding
{
    // The decoded planes to RGB(A), in integer arithmetic so that every platform gives the same pixels: chroma upsampled with
    // a triangle filter (9, 3, 3, 1 over 16 for 4:2:0, 3, 1 over 4 for 4:2:2) at the precision of the planes, then the
    // matrix coefficients and the sample range of ITU-T H.273 with coefficients rounded to 16 fractional bits, to 8 or 16
    // bits. The weights of matrix coefficients 12 are derived from the colour primaries. Alpha is scaled to the output
    // depth and applied as libavif applies it.
    internal static class AvifRgbConverter
    {
        private const long One = 1L << 16;
        private const long Half = 1L << 15;
        private const long D = 10000;
        private const int McChromaDerivedNcl = 12;
        private const int McYCgCo = 8;
        private const int McYCgCoRe = 16;
        private const int McYCgCoRo = 17;

        // KR and KB derived from the colour primaries are rounded to 5 decimals: the products in the conversion stay in 64 bits
        private const long DerivedD = 100000;

        // The matrix coefficients and range that apply: those of the colr property, else those of the sequence header
        public static void ResolveColor(AvifPlanes planes, AvifColorProperties color, out int matrix, out int primaries, out int transfer, out bool fullRange)
        {
            if (color.HasNclx)
            {
                matrix = color.MatrixCoefficients;
                primaries = color.ColorPrimaries;
                transfer = color.TransferCharacteristics;
                fullRange = color.FullRange;
            }
            else
            {
                matrix = planes.SequenceColorPresent ? planes.SequenceMatrix : Av1Constants.McUnspecified;
                primaries = planes.SequenceColorPresent ? planes.SequencePrimaries : Av1Constants.CpUnspecified;
                transfer = planes.SequenceColorPresent ? planes.SequenceTransfer : 2;
                fullRange = planes.SequenceFullRange;
            }
        }

        public static AvifImage Convert(AvifPlanes planes, AvifPlanes? alpha, bool premultiplied, AvifColorProperties color, AvifPixelFormat format, AvifAlphaMode mode)
        {
            ResolveColor(planes, color, out var matrix, out var primaries, out var transfer, out var fullRange);
            var width = planes.Width;
            var height = planes.Height;
            var outMax = AvifImage.IsSixteenBit(format) ? 65535 : 255;
            var useAlpha = alpha != null && mode != AvifAlphaMode.Ignore;
            if (useAlpha && (alpha!.Width != width || alpha.Height != height))
                throw AvifContainer.Invalid("the alpha image and the color image differ in size.");

            var converter = CreateRowConverter(planes, matrix, primaries, transfer, fullRange, outMax);
            var image = AvifImage.Create(width, height, format);
            var channels = image.Channels;
            var r = new int[width];
            var g = new int[width];
            var b = new int[width];
            var a = useAlpha ? new int[width] : null;
            var alphaMax = useAlpha ? (1 << alpha!.BitDepth) - 1 : 0;
            for (var row = 0; row < height; row++)
            {
                converter.ConvertRow(row, r, g, b);
                if (a != null)
                {
                    var samples = alpha!.Y.AsSpan(row * alpha.YStride, width);
                    for (var x = 0; x < width; x++)
                    {
                        var value = ExpandAlpha(samples[x], alpha.BitDepth, alpha.SequenceFullRange);
                        a[x] = alphaMax == outMax ? value : (int)((((long)value * outMax * 2) + alphaMax) / (2L * alphaMax));
                    }

                    ApplyAlpha(r, g, b, a, outMax, premultiplied, channels == 4, mode);
                }

                Pack(image, row, r, g, b, a, outMax);
            }

            return image;
        }

        // Limited-range alpha, which version 1.0.0 of the AVIF specification allowed, is expanded to full range as libavif
        // does: (v - min) * full / (max - min), rounded, truncated toward zero and clamped
        internal static int ExpandAlpha(int value, int bitDepth, bool fullRange)
        {
            if (fullRange || bitDepth > 12)
                return value;

            var min = 16 << (bitDepth - 8);
            var span = 219 << (bitDepth - 8);
            var full = (1 << bitDepth) - 1;
            var expanded = (((value - min) * full) + (span / 2)) / span;
            return expanded < 0 ? 0 : expanded > full ? full : expanded;
        }

        // libavif: straight colors are multiplied by alpha for a format without alpha (the image over black) or when
        // premultiplied colors are asked for; premultiplied colors are divided by alpha for straight output
        private static void ApplyAlpha(int[] r, int[] g, int[] b, int[] a, int max, bool premultiplied, bool outputHasAlpha, AvifAlphaMode mode)
        {
            var multiply = !premultiplied && (!outputHasAlpha || mode == AvifAlphaMode.Premultiplied);
            var divide = premultiplied && outputHasAlpha && mode == AvifAlphaMode.Straight;
            for (var x = 0; x < a.Length; x++)
            {
                var alpha = a[x];
                if (multiply)
                {
                    r[x] = (int)((((long)r[x] * alpha) + (max / 2)) / max);
                    g[x] = (int)((((long)g[x] * alpha) + (max / 2)) / max);
                    b[x] = (int)((((long)b[x] * alpha) + (max / 2)) / max);
                }
                else if (divide && alpha < max)
                {
                    if (alpha == 0)
                    {
                        r[x] = g[x] = b[x] = 0;
                    }
                    else
                    {
                        r[x] = (int)Math.Min((((long)r[x] * max) + (alpha / 2)) / alpha, max);
                        g[x] = (int)Math.Min((((long)g[x] * max) + (alpha / 2)) / alpha, max);
                        b[x] = (int)Math.Min((((long)b[x] * max) + (alpha / 2)) / alpha, max);
                    }
                }
            }
        }

        private static void Pack(AvifImage image, int row, int[] r, int[] g, int[] b, int[]? a, int max)
        {
            var channels = image.Channels;
            var width = image.Width;
            var offset = row * width * channels;
            if (AvifImage.IsSixteenBit(image.PixelFormat))
            {
                var pixels = image.Pixels16;
                for (var x = 0; x < width; x++)
                {
                    pixels[offset] = (ushort)r[x];
                    pixels[offset + 1] = (ushort)g[x];
                    pixels[offset + 2] = (ushort)b[x];
                    if (channels == 4)
                        pixels[offset + 3] = (ushort)(a != null ? a[x] : max);

                    offset += channels;
                }
            }
            else
            {
                var pixels = image.Pixels;
                for (var x = 0; x < width; x++)
                {
                    pixels[offset] = (byte)r[x];
                    pixels[offset + 1] = (byte)g[x];
                    pixels[offset + 2] = (byte)b[x];
                    if (channels == 4)
                        pixels[offset + 3] = (byte)(a != null ? a[x] : max);

                    offset += channels;
                }
            }
        }

        private static RowConverter CreateRowConverter(AvifPlanes planes, int matrix, int primaries, int transfer, bool fullRange, int outMax)
        {
            if (planes.Monochrome)
                return new MonochromeRows(planes, fullRange, outMax);

            switch (matrix)
            {
                case Av1Constants.McIdentity:
                    if (planes.SubsamplingX != 0 || planes.SubsamplingY != 0)
                        throw AvifContainer.Unsupported("the identity matrix with subsampled chroma");

                    return new IdentityRows(planes, fullRange, outMax);
                case McYCgCo:
                    return new YCgCoRows(planes, fullRange, outMax);
                case McYCgCoRe:
                case McYCgCoRo:
                    if (!fullRange)
                        throw AvifContainer.Unsupported("YCgCo-R in limited range");

                    var rgbDepth = planes.BitDepth - (matrix == McYCgCoRe ? 2 : 1);
                    if (rgbDepth < 1)
                        throw AvifContainer.Invalid("a YCgCo-R image is too shallow for its RGB samples.");

                    return new YCgCoRRows(planes, rgbDepth, outMax);
                case 3:
                    throw new UnsupportedImageFeatureException("The AVIF matrix coefficients 3 (reserved) are not supported.", ImageFormat.Avif, "matrix coefficients");
                case 10:
                case 11:
                case 13:
                case 14:
                case 15:
                    return AvifNonLinearRows.Create(planes, matrix, primaries, transfer, fullRange, outMax);
                default:
                    if (matrix > 17)
                        throw new UnsupportedImageFeatureException($"The AVIF matrix coefficients {matrix} are not supported.", ImageFormat.Avif, "matrix coefficients");

                    return new WeightedRows(planes, matrix, primaries, fullRange, outMax);
            }
        }

        internal abstract class RowConverter
        {
            protected RowConverter(AvifPlanes planes, int outMax)
            {
                Planes = planes;
                OutMax = outMax;
            }

            protected AvifPlanes Planes { get; }

            protected int OutMax { get; }

            public abstract void ConvertRow(int row, int[] r, int[] g, int[] b);

            protected int Clamp(long value) => value < 0 ? 0 : value > OutMax ? OutMax : (int)value;
        }

        private sealed class MonochromeRows : RowConverter
        {
            private readonly long _yOffset;
            private readonly long _cY;

            public MonochromeRows(AvifPlanes planes, bool fullRange, int outMax) : base(planes, outMax)
            {
                GetRange(planes.BitDepth, fullRange, out _yOffset, out var yRange, out _);
                _cY = RoundedDivide(outMax * One, yRange);
            }

            public override void ConvertRow(int row, int[] r, int[] g, int[] b)
            {
                var y = Planes.Y.AsSpan(row * Planes.YStride, Planes.Width);
                for (var x = 0; x < y.Length; x++)
                    r[x] = g[x] = b[x] = Clamp((((y[x] - _yOffset) * _cY) + Half) >> 16);
            }
        }

        // MatrixCoefficients 0: the planes are G, B and R, in the range of luma
        private sealed class IdentityRows : RowConverter
        {
            private readonly long _offset;
            private readonly long _range;

            public IdentityRows(AvifPlanes planes, bool fullRange, int outMax) : base(planes, outMax)
            {
                GetRange(planes.BitDepth, fullRange, out _offset, out _range, out _);
            }

            public override void ConvertRow(int row, int[] r, int[] g, int[] b)
            {
                var y = Planes.Y.AsSpan(row * Planes.YStride, Planes.Width);
                var u = Planes.U.AsSpan(row * Planes.CStride, Planes.Width);
                var v = Planes.V.AsSpan(row * Planes.CStride, Planes.Width);
                for (var x = 0; x < y.Length; x++)
                {
                    r[x] = Scale(v[x]);
                    g[x] = Scale(y[x]);
                    b[x] = Scale(u[x]);
                }
            }

            private int Scale(int value)
            {
                var numerator = (value - _offset) * OutMax;
                return numerator <= 0 ? 0 : Clamp((numerator + (_range / 2)) / _range);
            }
        }

        // The rows of upsampled chroma: the triangle filter in two steps, 3 parts of the nearest chroma row and 1 of the next
        // one, then the same along the row. The values are the chroma, less its offset, times 2^Shift.
        internal abstract class ChromaRows : RowConverter
        {
            private readonly int[] _rowU;
            private readonly int[] _rowV;
            private readonly int _chromaOffset;

            protected ChromaRows(AvifPlanes planes, int outMax) : base(planes, outMax)
            {
                Shift = (planes.SubsamplingX != 0 ? 2 : 0) + (planes.SubsamplingY != 0 ? 2 : 0);
                _chromaOffset = (1 << (planes.BitDepth - 1)) << Shift;
                Cb = new int[planes.Width];
                Cr = new int[planes.Width];
                _rowU = new int[planes.ChromaWidth];
                _rowV = new int[planes.ChromaWidth];
            }

            protected int Shift { get; }

            protected int[] Cb { get; }

            protected int[] Cr { get; }

            protected void UpsampleChroma(int row)
            {
                var planes = Planes;
                var subX = planes.SubsamplingX;
                var subY = planes.SubsamplingY;
                var chromaWidth = planes.ChromaWidth;
                var cy = row >> subY;
                var nearU = planes.U.AsSpan(cy * planes.CStride, chromaWidth);
                var nearV = planes.V.AsSpan(cy * planes.CStride, chromaWidth);
                if (subY != 0)
                {
                    var ny = Math.Min(Math.Max((row & 1) == 0 ? cy - 1 : cy + 1, 0), planes.ChromaHeight - 1);
                    var farU = planes.U.AsSpan(ny * planes.CStride, chromaWidth);
                    var farV = planes.V.AsSpan(ny * planes.CStride, chromaWidth);
                    for (var k = 0; k < chromaWidth; k++)
                    {
                        _rowU[k] = (3 * nearU[k]) + farU[k];
                        _rowV[k] = (3 * nearV[k]) + farV[k];
                    }
                }
                else
                {
                    for (var k = 0; k < chromaWidth; k++)
                    {
                        _rowU[k] = nearU[k];
                        _rowV[k] = nearV[k];
                    }
                }

                var cb = Cb;
                var cr = Cr;
                if (subX != 0)
                {
                    for (var cx = 0; cx < chromaWidth; cx++)
                    {
                        var x = cx << 1;
                        var u = 3 * _rowU[cx];
                        var v = 3 * _rowV[cx];
                        var left = cx > 0 ? cx - 1 : 0;
                        cb[x] = u + _rowU[left] - _chromaOffset;
                        cr[x] = v + _rowV[left] - _chromaOffset;
                        if (x + 1 < cb.Length)
                        {
                            var right = cx + 1 < chromaWidth ? cx + 1 : cx;
                            cb[x + 1] = u + _rowU[right] - _chromaOffset;
                            cr[x + 1] = v + _rowV[right] - _chromaOffset;
                        }
                    }
                }
                else
                {
                    for (var x = 0; x < cb.Length; x++)
                    {
                        cb[x] = _rowU[x] - _chromaOffset;
                        cr[x] = _rowV[x] - _chromaOffset;
                    }
                }
            }
        }

        // Every pixel is three sums. The upsampled chroma keeps its fractional bits (up to 4) and is rounded only once, with
        // the sum.
        private sealed class WeightedRows : ChromaRows
        {
            private readonly long _yOffset;
            private readonly long _cY;
            private readonly long _cRV;
            private readonly long _cBU;
            private readonly long _cGU;
            private readonly long _cGV;
            private readonly int _outputShift;
            private readonly long _outputRounding;

            public WeightedRows(AvifPlanes planes, int matrix, int primaries, bool fullRange, int outMax) : base(planes, outMax)
            {
                GetWeights(matrix, primaries, out var kr, out var kb, out var d);
                GetRange(planes.BitDepth, fullRange, out _yOffset, out var yRange, out var cRange);
                var kg = d - kr - kb;
                var scale = outMax * One * 2;
                _cY = RoundedDivide(outMax * One, yRange);
                _cRV = RoundedDivide(scale * (d - kr), d * cRange);
                _cBU = RoundedDivide(scale * (d - kb), d * cRange);
                _cGU = RoundedDivide3(scale, kb, d - kb, d * kg * cRange);
                _cGV = RoundedDivide3(scale, kr, d - kr, d * kg * cRange);
                _outputShift = 16 + Shift;
                _outputRounding = 1L << (_outputShift - 1);
            }

            public override void ConvertRow(int row, int[] r, int[] g, int[] b)
            {
                UpsampleChroma(row);
                var luma = Planes.Y.AsSpan(row * Planes.YStride, Planes.Width);
                var cb = Cb;
                var cr = Cr;
                var shift = Shift;
                for (var x = 0; x < luma.Length; x++)
                {
                    var luminance = (((luma[x] - _yOffset) * _cY) << shift) + _outputRounding;
                    var u = cb[x];
                    var v = cr[x];
                    r[x] = Clamp((luminance + (_cRV * v)) >> _outputShift);
                    g[x] = Clamp((luminance - (_cGU * u) - (_cGV * v)) >> _outputShift);
                    b[x] = Clamp((luminance + (_cBU * u)) >> _outputShift);
                }
            }
        }

        // MatrixCoefficients 8 (ITU-T H.273, equations 47 to 50): R = Y - Cg + Co, G = Y + Cg, B = Y - Cg - Co, exactly over the
        // common denominator of the luma and chroma ranges
        private sealed class YCgCoRows : ChromaRows
        {
            private readonly long _yOffset;
            private readonly long _yRange;
            private readonly long _cRange;
            private readonly long _denominator;

            public YCgCoRows(AvifPlanes planes, bool fullRange, int outMax) : base(planes, outMax)
            {
                GetRange(planes.BitDepth, fullRange, out _yOffset, out _yRange, out _cRange);
                _denominator = _yRange * _cRange << Shift;
            }

            public override void ConvertRow(int row, int[] r, int[] g, int[] b)
            {
                UpsampleChroma(row);
                var luma = Planes.Y.AsSpan(row * Planes.YStride, Planes.Width);
                var cb = Cb;
                var cr = Cr;
                for (var x = 0; x < luma.Length; x++)
                {
                    var y = ((luma[x] - _yOffset) * _cRange) << Shift;
                    var cg = cb[x] * _yRange;
                    var co = cr[x] * _yRange;
                    r[x] = Scale(y - cg + co);
                    g[x] = Scale(y + cg);
                    b[x] = Scale(y - cg - co);
                }
            }

            private int Scale(long numerator)
            {
                return numerator <= 0 ? 0 : Clamp(((numerator * OutMax) + (_denominator / 2)) / _denominator);
            }
        }

        // MatrixCoefficients 16 and 17, YCgCo-Re and YCgCo-Ro (ITU-T H.273 (2024), equations 62 to 65): integer lifting back to
        // RGB samples 2 or 1 bits shallower than the planes, as libavif does, then scaled to the output depth
        private sealed class YCgCoRRows : ChromaRows
        {
            private readonly int _rgbMax;

            public YCgCoRRows(AvifPlanes planes, int rgbDepth, int outMax) : base(planes, outMax)
            {
                _rgbMax = (1 << rgbDepth) - 1;
            }

            public override void ConvertRow(int row, int[] r, int[] g, int[] b)
            {
                UpsampleChroma(row);
                var luma = Planes.Y.AsSpan(row * Planes.YStride, Planes.Width);
                var cb = Cb;
                var cr = Cr;
                var shift = Shift;
                var rounding = shift > 0 ? 1 << (shift - 1) : 0;
                for (var x = 0; x < luma.Length; x++)
                {
                    var cg = (cb[x] + rounding) >> shift;
                    var co = (cr[x] + rounding) >> shift;
                    var t = luma[x] - (cg >> 1);
                    var green = ClampRgb(t + cg);
                    var blue = ClampRgb(t - (co >> 1));
                    var red = ClampRgb(blue + co);
                    r[x] = Scale(red);
                    g[x] = Scale(green);
                    b[x] = Scale(blue);
                }
            }

            private int ClampRgb(int value) => value < 0 ? 0 : value > _rgbMax ? _rgbMax : value;

            private int Scale(int value) => _rgbMax == OutMax ? value : (int)((((long)value * OutMax * 2) + _rgbMax) / (2L * _rgbMax));
        }

        internal static void GetRangeValues(int bitDepth, bool fullRange, out long yOffset, out long yRange, out long cRange) => GetRange(bitDepth, fullRange, out yOffset, out yRange, out cRange);

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

        // KR and KB over d: over 10000 from ITU-T H.273, table 4, or derived from the colour primaries for matrix coefficients 12
        private static void GetWeights(int matrix, int primaries, out long kr, out long kb, out long d)
        {
            d = D;
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
                case McChromaDerivedNcl:
                    DeriveWeights(primaries, out kr, out kb);
                    d = DerivedD;
                    return;
                default:
                    throw new UnsupportedImageFeatureException($"The AVIF matrix coefficients {matrix} are not supported.", ImageFormat.Avif, "matrix coefficients");
            }
        }

        // ITU-T H.273, equations 32 to 37, exactly in integers: the chromaticities are over 10000, so both sides of each
        // fraction are products of four of them, below 2^56, and the scales cancel
        internal static void DeriveWeights(int primaries, out long kr, out long kb)
        {
            GetChromaticities(primaries, out var rx, out var ry, out var gx, out var gy, out var bx, out var by, out var wx, out var wy);
            var rz = 10000 - rx - ry;
            var gz = 10000 - gx - gy;
            var bz = 10000 - bx - by;
            var wz = 10000 - wx - wy;
            var denominator = wy * ((rx * ((gy * bz) - (by * gz))) + (gx * ((by * rz) - (ry * bz))) + (bx * ((ry * gz) - (gy * rz))));
            kr = RoundedRatio(ry * ((wx * ((gy * bz) - (by * gz))) + (wy * ((bx * gz) - (gx * bz))) + (wz * ((gx * by) - (bx * gy)))), denominator, DerivedD);
            kb = RoundedRatio(by * ((wx * ((ry * gz) - (gy * rz))) + (wy * ((gx * rz) - (rx * gz))) + (wz * ((rx * gy) - (gx * ry)))), denominator, DerivedD);
        }

        // The chromaticities over 10000 of the red, green and blue primaries and the white point (ITU-T H.273, table 2), with
        // libavif's values; unknown primaries take those of BT.709, as in libavif
        private static void GetChromaticities(int primaries, out long rx, out long ry, out long gx, out long gy, out long bx, out long by, out long wx, out long wy)
        {
            (rx, ry, gx, gy, bx, by, wx, wy) = primaries switch
            {
                4 => (6700L, 3300L, 2100L, 7100L, 1400L, 800L, 3100L, 3160L),
                5 => (6400L, 3300L, 2900L, 6000L, 1500L, 600L, 3127L, 3290L),
                6 or 7 => (6300L, 3400L, 3100L, 5950L, 1550L, 700L, 3127L, 3290L),
                8 => (6810L, 3190L, 2430L, 6920L, 1450L, 490L, 3100L, 3160L),
                9 => (7080L, 2920L, 1700L, 7970L, 1310L, 460L, 3127L, 3290L),
                10 => (10000L, 0L, 0L, 10000L, 0L, 0L, 3333L, 3333L),
                11 => (6800L, 3200L, 2650L, 6900L, 1500L, 600L, 3140L, 3510L),
                12 => (6800L, 3200L, 2650L, 6900L, 1500L, 600L, 3127L, 3290L),
                22 => (6300L, 3400L, 2950L, 6050L, 1550L, 770L, 3127L, 3290L),
                _ => (6400L, 3300L, 3000L, 6000L, 1500L, 600L, 3127L, 3290L),
            };
        }

        // numerator / denominator times scale, a power of 10, rounded half up. The quotient is worked out one decimal digit
        // at a time, so no product exceeds ten times the denominator
        private static long RoundedRatio(long numerator, long denominator, long scale)
        {
            if (denominator < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }

            var quotient = numerator / denominator;
            var remainder = numerator % denominator;
            for (var step = scale; step > 1; step /= 10)
            {
                remainder *= 10;
                quotient = (quotient * 10) + (remainder / denominator);
                remainder %= denominator;
            }

            return 2 * remainder >= denominator ? quotient + 1 : quotient;
        }

        private static long RoundedDivide(long numerator, long denominator) => (numerator + (denominator / 2)) / denominator;

        // a b c / denominator rounded half up, for products that may pass 2^63 (16-bit output with derived weights): decimal
        // holds them exactly, and the quotient of these small integers is never within 1e-20 of a half
        private static long RoundedDivide3(long a, long b, long c, long denominator)
        {
            var product = (decimal)a * b * c;
            return (long)decimal.Floor((product / denominator) + 0.5m);
        }
    }
}

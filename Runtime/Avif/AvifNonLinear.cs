#nullable enable
using System;

namespace AvifDecoding
{
    // The matrix coefficients that are not a linear matrix of the coded samples (ITU-T H.273, section 8.3): constant luminance
    // (10, BT.2020, and 13, chromaticity-derived) and ICtCp (14) go through the transfer characteristics, and SMPTE ST 2085
    // (11) has its own linear equations. They use floating point: Math.Pow, Math.Log and Math.Exp may differ in the last
    // place between platforms, which can only move an output sample whose exact value is within about 1e-9 of a rounding
    // boundary. libavif decodes none of them.
    internal static class AvifNonLinearRows
    {
        public static AvifRgbConverter.RowConverter Create(AvifPlanes planes, int matrix, int primaries, int transfer, bool fullRange, int outMax)
        {
            switch (matrix)
            {
                case 11:
                    return new Smpte2085Rows(planes, fullRange, outMax);
                case 10:
                case 13:
                    if (!AvifTransfer.IsSupported(transfer))
                        throw new UnsupportedImageFeatureException($"The AVIF matrix coefficients {matrix} need the transfer characteristics, which are {transfer} here.", ImageFormat.Avif, "matrix coefficients");

                    return new TransferRows(planes, matrix, primaries, transfer, fullRange, outMax);
                case 14:
                    if (transfer != AvifTransfer.Pq && transfer != AvifTransfer.Hlg)
                        throw new UnsupportedImageFeatureException($"The AVIF ICtCp matrix coefficients are defined for the PQ and HLG transfer characteristics, not for {transfer}.", ImageFormat.Avif, "matrix coefficients");

                    return new TransferRows(planes, matrix, primaries, transfer, fullRange, outMax);
                default:
                    throw new UnsupportedImageFeatureException($"The AVIF matrix coefficients {matrix} are not supported.", ImageFormat.Avif, "matrix coefficients");
            }
        }

        private static int ToSample(double value, int max) => !(value > 0) ? 0 : value >= 1 ? max : (int)Math.Floor((value * max) + 0.5);

        // MatrixCoefficients 11: E'Y = E'G, E'PB = (0.986566 E'B - E'Y) / 2 and E'PR = (E'R - 0.991902 E'Y) / 2
        private sealed class Smpte2085Rows : AvifRgbConverter.ChromaRows
        {
            private readonly double _yOffset;
            private readonly double _yScale;
            private readonly double _cScale;

            public Smpte2085Rows(AvifPlanes planes, bool fullRange, int outMax) : base(planes, outMax)
            {
                AvifRgbConverter.GetRangeValues(planes.BitDepth, fullRange, out var yOffset, out var yRange, out var cRange);
                _yOffset = yOffset;
                _yScale = 1.0 / yRange;
                _cScale = 1.0 / (cRange << Shift);
            }

            public override void ConvertRow(int row, int[] r, int[] g, int[] b)
            {
                UpsampleChroma(row);
                var luma = Planes.Y.AsSpan(row * Planes.YStride, Planes.Width);
                for (var x = 0; x < luma.Length; x++)
                {
                    var ey = (luma[x] - _yOffset) * _yScale;
                    var epb = Cb[x] * _cScale;
                    var epr = Cr[x] * _cScale;
                    r[x] = ToSample((0.991902 * ey) + (2 * epr), OutMax);
                    g[x] = ToSample(ey, OutMax);
                    b[x] = ToSample((ey + (2 * epb)) / 0.986566, OutMax);
                }
            }
        }

        private sealed class TransferRows : AvifRgbConverter.ChromaRows
        {
            // ITU-R BT.2100: L, M and S from linear R, G and B, then I, CT and CP from L', M' and S', over 4096
            private static readonly double[] RgbToLms = { 1688, 2146, 262, 683, 2951, 462, 99, 309, 3688 };
            private static readonly double[] LmsToIctcpPq = { 2048, 2048, 0, 6610, -13613, 7003, 17933, -17390, -543 };
            private static readonly double[] LmsToIctcpHlg = { 2048, 2048, 0, 3625, -7465, 3840, 9500, -9212, -288 };

            private readonly int _transfer;
            private readonly double _yOffset;
            private readonly double _yScale;
            private readonly double _cScale;
            private readonly double[]? _ictcpToLms;
            private readonly double[]? _lmsToRgb;
            private readonly double _kr;
            private readonly double _kb;
            private readonly double _kg;
            private readonly double _nb;
            private readonly double _pb;
            private readonly double _nr;
            private readonly double _pr;

            public TransferRows(AvifPlanes planes, int matrix, int primaries, int transfer, bool fullRange, int outMax) : base(planes, outMax)
            {
                _transfer = transfer;
                AvifRgbConverter.GetRangeValues(planes.BitDepth, fullRange, out var yOffset, out var yRange, out var cRange);
                _yOffset = yOffset;
                _yScale = 1.0 / yRange;
                _cScale = 1.0 / (cRange << Shift);
                if (matrix == 14)
                {
                    _lmsToRgb = Invert(RgbToLms);
                    _ictcpToLms = Invert(transfer == AvifTransfer.Hlg ? LmsToIctcpHlg : LmsToIctcpPq);
                    return;
                }

                // E'Y is the transfer function of the luminance KR R + KG G + KB B, and the chroma is E'B - E'Y and E'R - E'Y over
                // twice its extent, which differs on both sides of zero: NB = (1 - KB)', PB = 1 - KB', NR = (1 - KR)', PR = 1 - KR'
                long kr;
                long kb;
                long d;
                if (matrix == 10)
                {
                    kr = 2627;
                    kb = 593;
                    d = 10000;
                }
                else
                {
                    AvifRgbConverter.DeriveWeights(primaries, out kr, out kb);
                    d = 100000;
                }

                _kr = (double)kr / d;
                _kb = (double)kb / d;
                _kg = 1 - _kr - _kb;
                _nb = AvifTransfer.Encode(transfer, 1 - _kb);
                _pb = 1 - AvifTransfer.Encode(transfer, _kb);
                _nr = AvifTransfer.Encode(transfer, 1 - _kr);
                _pr = 1 - AvifTransfer.Encode(transfer, _kr);
            }

            public override void ConvertRow(int row, int[] r, int[] g, int[] b)
            {
                UpsampleChroma(row);
                var luma = Planes.Y.AsSpan(row * Planes.YStride, Planes.Width);
                for (var x = 0; x < luma.Length; x++)
                {
                    var ey = (luma[x] - _yOffset) * _yScale;
                    var epb = Cb[x] * _cScale;
                    var epr = Cr[x] * _cScale;
                    double er;
                    double eg;
                    double eb;
                    if (_ictcpToLms != null)
                    {
                        var m = _ictcpToLms;
                        var rgb = _lmsToRgb!;
                        var l = AvifTransfer.Decode(_transfer, (m[0] * ey) + (m[1] * epb) + (m[2] * epr));
                        var mm = AvifTransfer.Decode(_transfer, (m[3] * ey) + (m[4] * epb) + (m[5] * epr));
                        var s = AvifTransfer.Decode(_transfer, (m[6] * ey) + (m[7] * epb) + (m[8] * epr));
                        er = AvifTransfer.Encode(_transfer, (rgb[0] * l) + (rgb[1] * mm) + (rgb[2] * s));
                        eg = AvifTransfer.Encode(_transfer, (rgb[3] * l) + (rgb[4] * mm) + (rgb[5] * s));
                        eb = AvifTransfer.Encode(_transfer, (rgb[6] * l) + (rgb[7] * mm) + (rgb[8] * s));
                    }
                    else
                    {
                        eb = ey + (2 * (epb < 0 ? _nb : _pb) * epb);
                        er = ey + (2 * (epr < 0 ? _nr : _pr) * epr);
                        var luminance = AvifTransfer.Decode(_transfer, ey);
                        var green = (luminance - (_kr * AvifTransfer.Decode(_transfer, er)) - (_kb * AvifTransfer.Decode(_transfer, eb))) / _kg;
                        eg = AvifTransfer.Encode(_transfer, green);
                    }

                    r[x] = ToSample(er, OutMax);
                    g[x] = ToSample(eg, OutMax);
                    b[x] = ToSample(eb, OutMax);
                }
            }

            private static double[] Invert(double[] over4096)
            {
                var m = new double[9];
                for (var i = 0; i < 9; i++)
                    m[i] = over4096[i] / 4096;

                var det = (m[0] * ((m[4] * m[8]) - (m[5] * m[7]))) - (m[1] * ((m[3] * m[8]) - (m[5] * m[6]))) + (m[2] * ((m[3] * m[7]) - (m[4] * m[6])));
                return new[]
                {
                    ((m[4] * m[8]) - (m[5] * m[7])) / det, ((m[2] * m[7]) - (m[1] * m[8])) / det, ((m[1] * m[5]) - (m[2] * m[4])) / det,
                    ((m[5] * m[6]) - (m[3] * m[8])) / det, ((m[0] * m[8]) - (m[2] * m[6])) / det, ((m[2] * m[3]) - (m[0] * m[5])) / det,
                    ((m[3] * m[7]) - (m[4] * m[6])) / det, ((m[1] * m[6]) - (m[0] * m[7])) / det, ((m[0] * m[4]) - (m[1] * m[3])) / det,
                };
            }
        }
    }

    // The transfer characteristics functions of ITU-T H.273, table 3, on [0, 1]
    internal static class AvifTransfer
    {
        public const int Linear = 8;
        public const int Srgb = 13;
        public const int Pq = 16;
        public const int Hlg = 18;

        // The alpha and beta of Rec. ITU-R BT.709 at the precision that makes its two segments meet
        private const double Bt709Alpha = 1.09929682680944;
        private const double Bt709Beta = 0.018053968510807;
        private const double Smpte240Alpha = 1.1115;
        private const double Smpte240Beta = 0.0228;
        private const double PqM1 = 2610.0 / 16384;
        private const double PqM2 = 2523.0 / 4096 * 128;
        private const double PqC1 = 3424.0 / 4096;
        private const double PqC2 = 2413.0 / 4096 * 32;
        private const double PqC3 = 2392.0 / 4096 * 32;
        private const double HlgA = 0.17883277;
        private const double HlgB = 1 - (4 * HlgA);
        private static readonly double HlgC = 0.5 - (HlgA * Math.Log(4 * HlgA));

        public static bool IsSupported(int code) => code == 1 || (code >= 4 && code <= 18);

        // The functions of BT.709, BT.601, IEC 61966-2-4, BT.1361 and BT.2020 are one function on [0, 1]
        private static int GetCurve(int code) => code == 6 || code == 11 || code == 12 || code == 14 || code == 15 ? 1 : code;

        // Encoded to linear; the value is clipped to [0, 1]
        public static double Decode(int code, double value)
        {
            var v = !(value > 0) ? 0 : value > 1 ? 1 : value;
            switch (GetCurve(code))
            {
                case 1:
                    return v < 4.5 * Bt709Beta ? v / 4.5 : Math.Pow((v + (Bt709Alpha - 1)) / Bt709Alpha, 1 / 0.45);
                case 4:
                    return Math.Pow(v, 2.2);
                case 5:
                    return Math.Pow(v, 2.8);
                case 7:
                    return v < 4 * Smpte240Beta ? v / 4 : Math.Pow((v + (Smpte240Alpha - 1)) / Smpte240Alpha, 1 / 0.45);
                case Linear:
                    return v;
                case 9:
                    return v == 0 ? 0 : Math.Pow(10, (v - 1) * 2);
                case 10:
                    return v == 0 ? 0 : Math.Pow(10, (v - 1) * 2.5);
                case Srgb:
                    return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                case Pq:
                    var p = Math.Pow(v, 1 / PqM2);
                    return Math.Pow(Math.Max(p - PqC1, 0) / (PqC2 - (PqC3 * p)), 1 / PqM1);
                case 17:
                    return Math.Pow(v, 2.6) * (52.37 / 48);
                case Hlg:
                    return v <= 0.5 ? v * v / 3 : (Math.Exp((v - HlgC) / HlgA) + HlgB) / 12;
                default:
                    throw new ArgumentOutOfRangeException(nameof(code));
            }
        }

        // Linear to encoded, clipped to [0, 1]
        public static double Encode(int code, double value)
        {
            var l = !(value > 0) ? 0 : value;
            double v;
            switch (GetCurve(code))
            {
                case 1:
                    v = l < Bt709Beta ? 4.5 * l : (Bt709Alpha * Math.Pow(l, 0.45)) - (Bt709Alpha - 1);
                    break;
                case 4:
                    v = Math.Pow(l, 1 / 2.2);
                    break;
                case 5:
                    v = Math.Pow(l, 1 / 2.8);
                    break;
                case 7:
                    v = l < Smpte240Beta ? 4 * l : (Smpte240Alpha * Math.Pow(l, 0.45)) - (Smpte240Alpha - 1);
                    break;
                case Linear:
                    v = l;
                    break;
                case 9:
                    v = l < 0.01 ? 0 : 1 + (Math.Log10(l) / 2);
                    break;
                case 10:
                    v = l < Math.Sqrt(10) / 1000 ? 0 : 1 + (Math.Log10(l) / 2.5);
                    break;
                case Srgb:
                    v = l <= 0.0031308 ? 12.92 * l : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;
                    break;
                case Pq:
                    var y = Math.Pow(l, PqM1);
                    v = Math.Pow((PqC1 + (PqC2 * y)) / (1 + (PqC3 * y)), PqM2);
                    break;
                case 17:
                    v = Math.Pow(48 * l / 52.37, 1 / 2.6);
                    break;
                case Hlg:
                    v = l <= 1.0 / 12 ? Math.Sqrt(3 * l) : (HlgA * Math.Log((12 * l) - HlgB)) + HlgC;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(code));
            }

            return v > 1 ? 1 : v;
        }
    }
}

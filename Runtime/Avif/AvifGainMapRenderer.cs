#nullable enable
using System;

namespace AvifDecoding
{
    // ISO 21496-1: the rendition of an image with a gain map for a display of some HDR headroom, as libavif computes it
    // (avifRGBImageApplyGainMap). In linear light, where 1.0 is SDR white (203 nits for PQ and HLG):
    //   gain = lerp(gainMapMin, gainMapMax, g ^ (1 / gamma)), w = clamp((H - Hbase) / (Halt - Hbase), 0, 1)
    //   out = (base + baseOffset) * 2 ^ (gain * w) - alternateOffset
    // in the primaries of the base image or of the alternate image (use_base_colour_space), then converted to the output
    // primaries and transfer characteristics. It uses double floating point; the output may differ from libavif's (float)
    // by a few units in 65535.
    internal static class AvifGainMapRenderer
    {
        private const double SdrWhiteNits = 203;

        public static AvifImage Apply(AvifImage baseImage, AvifImage gainMap, AvifGainMapInfo metadata, int basePrimaries, int baseTransfer, int alternatePrimaries,
            double headroom, int outputPrimaries, int outputTransfer)
        {
            var width = baseImage.Width;
            var height = baseImage.Height;
            var result = AvifImage.Create(width, height, AvifPixelFormat.Rgba64);
            var weight = GetWeight(headroom, metadata);
            var mathPrimaries = metadata.UseBaseColorSpace || alternatePrimaries == 2 ? basePrimaries : alternatePrimaries;
            var toMath = basePrimaries != mathPrimaries ? GetRgbToRgb(basePrimaries, mathPrimaries) : null;
            var toOutput = mathPrimaries != outputPrimaries ? GetRgbToRgb(mathPrimaries, outputPrimaries) : null;
            var baseToOutput = basePrimaries != outputPrimaries ? GetRgbToRgb(basePrimaries, outputPrimaries) : null;
            if (weight == 0 && baseTransfer == outputTransfer && basePrimaries == outputPrimaries)
            {
                Array.Copy(baseImage.Pixels16, result.Pixels16, result.Pixels16.Length);
                return result;
            }

            var channels = Math.Max(metadata.GainMapMin.Length, 1);
            var min = new double[3];
            var max = new double[3];
            var gammaInverse = new double[3];
            var baseOffset = new double[3];
            var alternateOffset = new double[3];
            for (var c = 0; c < 3; c++)
            {
                var k = c < channels ? c : 0;
                min[c] = metadata.GainMapMin[k].ToDouble();
                max[c] = metadata.GainMapMax[k].ToDouble();
                gammaInverse[c] = 1 / metadata.GainMapGamma[k].ToDouble();
                baseOffset[c] = metadata.BaseOffset[k].ToDouble();
                alternateOffset[c] = metadata.AlternateOffset[k].ToDouble();
            }

            var pixel = new double[3];
            var gain = new double[3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = ((y * width) + x) * 4;
                    for (var c = 0; c < 3; c++)
                        pixel[c] = ToLinear(baseTransfer, baseImage.Pixels16[index + c] / 65535.0);

                    if (weight != 0)
                    {
                        if (toMath != null)
                            Multiply(toMath, pixel);

                        SampleGainMap(gainMap, width, height, x, y, gain);
                        for (var c = 0; c < 3; c++)
                        {
                            var log2 = ((1 - Math.Pow(gain[c], gammaInverse[c])) * min[c]) + (Math.Pow(gain[c], gammaInverse[c]) * max[c]);
                            pixel[c] = ((pixel[c] + baseOffset[c]) * Math.Pow(2, log2 * weight)) - alternateOffset[c];
                        }

                        if (toOutput != null)
                            Multiply(toOutput, pixel);
                    }
                    else if (baseToOutput != null)
                    {
                        Multiply(baseToOutput, pixel);
                    }

                    for (var c = 0; c < 3; c++)
                    {
                        var encoded = ToEncoded(outputTransfer, pixel[c]);
                        result.Pixels16[index + c] = (ushort)(!(encoded > 0) ? 0 : encoded >= 1 ? 65535 : Math.Floor((encoded * 65535) + 0.5));
                    }

                    result.Pixels16[index + 3] = baseImage.Pixels16[index + 3];
                }
            }

            return result;
        }

        private static double GetWeight(double headroom, AvifGainMapInfo metadata)
        {
            var baseHeadroom = metadata.BaseHdrHeadroom.ToDouble();
            var alternateHeadroom = metadata.AlternateHdrHeadroom.ToDouble();
            if (baseHeadroom == alternateHeadroom)
                return 0;

            var w = Math.Min(Math.Max((headroom - baseHeadroom) / (alternateHeadroom - baseHeadroom), 0), 1);
            return alternateHeadroom < baseHeadroom ? -w : w;
        }

        // The gain map at the position of a pixel of the base image, bilinearly when the sizes differ
        private static void SampleGainMap(AvifImage gainMap, int width, int height, int x, int y, double[] gain)
        {
            if (gainMap.Width == width && gainMap.Height == height)
            {
                var index = ((y * width) + x) * 4;
                for (var c = 0; c < 3; c++)
                    gain[c] = gainMap.Pixels16[index + c] / 65535.0;

                return;
            }

            var gx = Math.Min(Math.Max((((x + 0.5) * gainMap.Width) / width) - 0.5, 0), gainMap.Width - 1);
            var gy = Math.Min(Math.Max((((y + 0.5) * gainMap.Height) / height) - 0.5, 0), gainMap.Height - 1);
            var x0 = (int)gx;
            var y0 = (int)gy;
            var x1 = Math.Min(x0 + 1, gainMap.Width - 1);
            var y1 = Math.Min(y0 + 1, gainMap.Height - 1);
            var fx = gx - x0;
            var fy = gy - y0;
            for (var c = 0; c < 3; c++)
            {
                double At(int px, int py) => gainMap.Pixels16[(((py * gainMap.Width) + px) * 4) + c] / 65535.0;
                var top = (At(x0, y0) * (1 - fx)) + (At(x1, y0) * fx);
                var bottom = (At(x0, y1) * (1 - fx)) + (At(x1, y1) * fx);
                gain[c] = (top * (1 - fy)) + (bottom * fy);
            }
        }

        // libavif's transfer functions for gain maps: PQ and HLG give extended SDR, where 1.0 is SDR white (203 nits), and HLG
        // includes the OOTF of BT.2100 for a 1000-nit display
        private static double ToLinear(int transfer, double v)
        {
            switch (transfer)
            {
                case AvifTransfer.Pq:
                    return AvifTransfer.Decode(transfer, v) * 10000 / SdrWhiteNits;
                case AvifTransfer.Hlg:
                    return Math.Pow(AvifTransfer.Decode(transfer, v), 1.2) * 1000 / SdrWhiteNits;
                case AvifTransfer.Srgb:
                    return v < 0 ? 0 : v < 12.92 * 0.0030412825601275209 ? v / 12.92 : v < 1 ? Math.Pow((v + 0.0550107189475866) / 1.0550107189475866, 2.4) : 1;
                default:
                    return AvifTransfer.Decode(AvifTransfer.IsSupported(transfer) ? transfer : 1, v);
            }
        }

        private static double ToEncoded(int transfer, double l)
        {
            switch (transfer)
            {
                case AvifTransfer.Pq:
                    return AvifTransfer.Encode(transfer, Math.Min(Math.Max(l * SdrWhiteNits / 10000, 0), 1));
                case AvifTransfer.Hlg:
                    return AvifTransfer.Encode(transfer, Math.Pow(Math.Min(Math.Max(l * SdrWhiteNits / 1000, 0), 1), 1 / 1.2));
                case AvifTransfer.Srgb:
                    return l < 0 ? 0 : l < 0.0030412825601275209 ? l * 12.92 : l < 1 ? (1.0550107189475866 * Math.Pow(l, 1 / 2.4)) - 0.0550107189475866 : 1;
                default:
                    return AvifTransfer.Encode(AvifTransfer.IsSupported(transfer) ? transfer : 1, l);
            }
        }

        private static void Multiply(double[] m, double[] v)
        {
            var r = (m[0] * v[0]) + (m[1] * v[1]) + (m[2] * v[2]);
            var g = (m[3] * v[0]) + (m[4] * v[1]) + (m[5] * v[2]);
            var b = (m[6] * v[0]) + (m[7] * v[1]) + (m[8] * v[2]);
            v[0] = r;
            v[1] = g;
            v[2] = b;
        }

        // libavif's RGB to RGB conversion: through XYZ adapted to D50 with the Bradford transform
        private static double[] GetRgbToRgb(int source, int target)
        {
            return MultiplyMatrices(Invert(GetRgbToXyzD50(target)), GetRgbToXyzD50(source));
        }

        private static readonly double[] Bradford = { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296 };
        private static readonly double[] LmsD50 = { 0.996284, 1.02043, 0.818644 };

        private static double[] GetRgbToXyzD50(int primaries)
        {
            GetPrimaries(primaries, out var p);
            var white = new[] { p[6] / p[7], 1, (1 - p[6] - p[7]) / p[7] };
            var rgb = new[] { p[0], p[2], p[4], p[1], p[3], p[5], 1 - p[0] - p[1], 1 - p[2] - p[3], 1 - p[4] - p[5] };
            var s = Apply(Invert(rgb), white);
            var rgbXyz = MultiplyMatrices(rgb, new[] { s[0], 0, 0, 0, s[1], 0, 0, 0, s[2] });
            var lms = Apply(Bradford, white);
            var adaptation = new[] { LmsD50[0] / lms[0], 0, 0, 0, LmsD50[1] / lms[1], 0, 0, 0, LmsD50[2] / lms[2] };
            adaptation = MultiplyMatrices(Invert(Bradford), MultiplyMatrices(adaptation, Bradford));
            return MultiplyMatrices(adaptation, rgbXyz);
        }

        private static void GetPrimaries(int primaries, out double[] p)
        {
            p = primaries switch
            {
                4 => new[] { 0.67, 0.33, 0.21, 0.71, 0.14, 0.08, 0.310, 0.316 },
                5 => new[] { 0.64, 0.33, 0.29, 0.60, 0.15, 0.06, 0.3127, 0.3290 },
                6 or 7 => new[] { 0.630, 0.340, 0.310, 0.595, 0.155, 0.070, 0.3127, 0.3290 },
                8 => new[] { 0.681, 0.319, 0.243, 0.692, 0.145, 0.049, 0.310, 0.316 },
                9 => new[] { 0.708, 0.292, 0.170, 0.797, 0.131, 0.046, 0.3127, 0.3290 },
                10 => new[] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.3333, 0.3333 },
                11 => new[] { 0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.314, 0.351 },
                12 => new[] { 0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.3127, 0.3290 },
                22 => new[] { 0.630, 0.340, 0.295, 0.605, 0.155, 0.077, 0.3127, 0.3290 },
                _ => new[] { 0.64, 0.33, 0.30, 0.60, 0.15, 0.06, 0.3127, 0.3290 },
            };
        }

        private static double[] Apply(double[] m, double[] v) => new[]
        {
            (m[0] * v[0]) + (m[1] * v[1]) + (m[2] * v[2]),
            (m[3] * v[0]) + (m[4] * v[1]) + (m[5] * v[2]),
            (m[6] * v[0]) + (m[7] * v[1]) + (m[8] * v[2]),
        };

        private static double[] MultiplyMatrices(double[] a, double[] b)
        {
            var c = new double[9];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                    c[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
            }

            return c;
        }

        private static double[] Invert(double[] m)
        {
            var det = (m[0] * ((m[4] * m[8]) - (m[7] * m[5]))) - (m[1] * ((m[3] * m[8]) - (m[5] * m[6]))) + (m[2] * ((m[3] * m[7]) - (m[4] * m[6])));
            var inv = 1 / det;
            return new[]
            {
                ((m[4] * m[8]) - (m[7] * m[5])) * inv, ((m[2] * m[7]) - (m[1] * m[8])) * inv, ((m[1] * m[5]) - (m[2] * m[4])) * inv,
                ((m[5] * m[6]) - (m[3] * m[8])) * inv, ((m[0] * m[8]) - (m[2] * m[6])) * inv, ((m[3] * m[2]) - (m[0] * m[5])) * inv,
                ((m[3] * m[7]) - (m[6] * m[4])) * inv, ((m[6] * m[1]) - (m[0] * m[7])) * inv, ((m[0] * m[4]) - (m[3] * m[1])) * inv,
            };
        }
    }
}

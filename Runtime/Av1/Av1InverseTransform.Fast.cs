#nullable enable
using System;

namespace AvifDecoding
{
    /// <content>
    /// The 2D inverse transform of 8-bit streams with the kernels of Av1InverseTransform.Kernels.g.cs. With 8-bit samples the
    /// coefficients, the values between the row and the column transforms and every clamped value have 16 bits, so the 32-bit
    /// arithmetic of the kernels gives the values of <see cref="TransformScalar"/>, which stays the reference and transforms
    /// the other bit depths and the lossless blocks.
    /// </content>
    internal static partial class Av1InverseTransform
    {
        // Coefficients in the dequantization range, zero outside their first rows rows and first columns columns
        private static void TransformFast8(ReadOnlySpan<int> dequant, Span<int> residual, int txSz, int txType, int rows, int columns)
        {
            int log2W = Av1Tables.TxWidthLog2[txSz];
            int log2H = Av1Tables.TxHeightLog2[txSz];
            var w = 1 << log2W;
            var h = 1 << log2H;
            var tw = Math.Min(32, w);
            if (rows <= 0 || columns <= 0)
            {
                residual.Slice(0, w * h).Clear();
                return;
            }

            int rowShift = Av1Tables.TransformRowShift[txSz];
            var rowRounding = rowShift is 0 ? 0 : 1 << (rowShift - 1);
            var isRectangular2To1 = Math.Abs(log2W - log2H) is 1;
            var rowKind = GetRowKind(txType);
            var colKind = GetColumnKind(txType);

            // The rows that can hold coefficients: the row transform, the row shift and the clamp to the column range. A kernel
            // reads up to 32 inputs, whatever the count it is given: the inputs after the coefficients are cleared
            var th = Math.Min(32, h);
            for (var i = 0; i < rows; i++)
            {
                var source = dequant.Slice(i * tw, columns);
                var row = residual.Slice(i * w, w);
                if (isRectangular2To1)
                {
                    for (var j = 0; j < columns; j++)
                    {
                        row[j] = ((source[j] * 2896) + 2048) >> 12;
                    }
                }
                else
                {
                    source.CopyTo(row);
                }

                row.Slice(columns, tw - columns).Clear();

                ApplyFast(rowKind, log2W, columns, residual, i * w, 1);
                for (var j = 0; j < w; j++)
                {
                    row[j] = Sat16((row[j] + rowRounding) >> rowShift);
                }
            }

            residual.Slice(rows * w, (th - rows) * w).Clear();
            for (var j = 0; j < w; j++)
            {
                ApplyFast(colKind, log2H, rows, residual, j, w);
            }

            var samples = residual.Slice(0, w * h);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (samples[i] + 8) >> 4;
            }
        }

        // The 1D transform of 2^n points in place on d[o], d[o + s], ..., whose inputs after the first count are zero (the
        // kernel chosen for the count may still read them, up to the 32nd): every output is written
        private static void ApplyFast(Kind kind, int n, int count, Span<int> d, int o, int s)
        {
            switch (kind)
            {
                case Kind.Dct:
                    DctFast(n, count, d, o, s);
                    break;
                case Kind.Adst:
                    if (n is 2)
                        Adst4Fast(d, o, s);
                    else
                        AdstFast(n, count, d, o, s);

                    break;
                default:
                    IdentityFast(n, count, d, o, s);
                    break;
            }
        }

        // Section 7.13.2.6 "Inverse ADST4 process": the sums of products of 16-bit values with the 12-bit constants fit 32 bits
        private static void Adst4Fast(Span<int> d, int o, int s)
        {
            var t0 = d[o];
            var t1 = d[o + s];
            var t2 = d[o + (2 * s)];
            var t3 = d[o + (3 * s)];
            var s0 = Av1Constants.Sinpi19 * t0;
            var s1 = Av1Constants.Sinpi29 * t0;
            var s2 = Av1Constants.Sinpi39 * t1;
            var s3 = Av1Constants.Sinpi49 * t2;
            var s4 = Av1Constants.Sinpi19 * t2;
            var s5 = Av1Constants.Sinpi29 * t3;
            var s6 = Av1Constants.Sinpi49 * t3;
            var b7 = t0 - t2 + t3;
            s0 += s3;
            s1 -= s4;
            s3 = s2;
            s2 = Av1Constants.Sinpi39 * b7;
            s0 += s5;
            s1 -= s6;
            d[o] = (s0 + s3 + 2048) >> 12;
            d[o + s] = (s1 + s3 + 2048) >> 12;
            d[o + (2 * s)] = (s2 + 2048) >> 12;
            d[o + (3 * s)] = (s0 + s1 - s3 + 2048) >> 12;
        }

        // Section 7.13.2.15 "Inverse identity transform process"
        private static void IdentityFast(int n, int count, Span<int> d, int o, int s)
        {
            var length = 1 << n;
            var index = o;
            for (var i = 0; i < count; i++, index += s)
            {
                var value = d[index];
                d[index] = n switch
                {
                    2 => ((value * 5793) + 2048) >> 12,
                    3 => value * 2,
                    4 => ((value * 11586) + 2048) >> 12,
                    _ => value * 4,
                };
            }

            for (var i = count; i < length; i++, index += s)
            {
                d[index] = 0;
            }
        }
    }
}

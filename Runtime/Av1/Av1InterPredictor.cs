#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace AvifDecoding
{
    /// <summary>
    /// The motion compensation kernels of the AV1 decoder (AV1 specification, sections 7.11.3.4 "Block inter prediction process"
    /// and 7.11.3.5 "Block warp process"): they read a plane of a reference frame, whose samples outside the plane are the nearest
    /// ones inside, and produce predictions with the intermediate precision that the rounding variables define.
    /// </summary>
    internal static class Av1InterPredictor
    {
        /// <summary>The largest number of rows of the intermediate array of <see cref="PredictBlock"/>: a block of 128 rows from a reference of twice the height.</summary>
        public const int MaxIntermediateHeight = (((127 * 2048) + 1023) >> Av1Constants.ScaleSubpelBits) + 8;

        /// <summary>Predicts a block from a reference plane with the separable sub-sample interpolation filters.</summary>
        /// <param name="reference">The samples of the reference plane.</param>
        /// <param name="referenceStride">The number of samples of a row of <paramref name="reference"/>.</param>
        /// <param name="lastX">The column of the last sample of the reference plane.</param>
        /// <param name="lastY">The row of the last sample of the reference plane.</param>
        /// <param name="x">The column of the block in the reference plane, in units of 1/1024 sample.</param>
        /// <param name="y">The row of the block in the reference plane, in units of 1/1024 sample.</param>
        /// <param name="xStep">The horizontal distance between two samples of the block in the reference plane, in units of 1/1024 sample.</param>
        /// <param name="yStep">The vertical distance between two samples of the block in the reference plane, in units of 1/1024 sample.</param>
        /// <param name="w">The width of the block.</param>
        /// <param name="h">The height of the block.</param>
        /// <param name="horizontalFilter">The type of the horizontal filter (<c>InterpFilters[ candRow ][ candCol ][ 1 ]</c>).</param>
        /// <param name="verticalFilter">The type of the vertical filter (<c>InterpFilters[ candRow ][ candCol ][ 0 ]</c>).</param>
        /// <param name="interRound0">The number of bits rounded after the horizontal filter.</param>
        /// <param name="interRound1">The number of bits rounded after the vertical filter.</param>
        /// <param name="intermediate">A scratch of <see cref="MaxIntermediateHeight"/> rows of <paramref name="w"/> values.</param>
        /// <param name="pred">The prediction: <paramref name="h"/> rows of <paramref name="predStride"/> values.</param>
        /// <param name="predStride">The number of values of a row of <paramref name="pred"/>.</param>
        public static void PredictBlock(
            ReadOnlySpan<ushort> reference,
            int referenceStride,
            int lastX,
            int lastY,
            int x,
            int y,
            int xStep,
            int yStep,
            int w,
            int h,
            int horizontalFilter,
            int verticalFilter,
            int interRound0,
            int interRound1,
            Span<int> intermediate,
            Span<int> pred,
            int predStride)
        {
                PredictBlockScalar(reference, referenceStride, lastX, lastY, x, y, xStep, yStep, w, h, horizontalFilter, verticalFilter, interRound0, interRound1, intermediate, pred, predStride);
        }

        /// <summary>The scalar reference of <see cref="PredictBlock"/>.</summary>
        internal static void PredictBlockScalar(
            ReadOnlySpan<ushort> reference,
            int referenceStride,
            int lastX,
            int lastY,
            int x,
            int y,
            int xStep,
            int yStep,
            int w,
            int h,
            int horizontalFilter,
            int verticalFilter,
            int interRound0,
            int interRound1,
            Span<int> intermediate,
            Span<int> pred,
            int predStride)
        {
            var intermediateHeight = ((((h - 1) * yStep) + (1 << Av1Constants.ScaleSubpelBits) - 1) >> Av1Constants.ScaleSubpelBits) + 8;
            var filters = Av1Tables.SubpelFilters;
            var filterH = SelectFilter(horizontalFilter, w) * Av1Tables.SubpelFiltersLength1 * Av1Tables.SubpelFiltersLength2;
            var round0 = (1 << interRound0) >> 1;
            for (var r = 0; r < intermediateHeight; r++)
            {
                var source = reference.Slice(Math.Clamp((y >> Av1Constants.ScaleSubpelBits) + r - 3, 0, lastY) * referenceStride, lastX + 1);
                var destination = intermediate.Slice(r * w, w);
                for (var c = 0; c < w; c++)
                {
                    var p = x + (xStep * c);
                    var taps = filters.Slice(filterH + (((p >> 6) & Av1Constants.SubpelMask) * Av1Tables.SubpelFiltersLength2), 8);
                    var first = (p >> Av1Constants.ScaleSubpelBits) - 3;
                    var s = 0;
                    if (first >= 0 && first + 7 <= lastX)
                    {
                        var window = source.Slice(first, 8);
                        s = (taps[0] * window[0]) + (taps[1] * window[1]) + (taps[2] * window[2]) + (taps[3] * window[3])
                            + (taps[4] * window[4]) + (taps[5] * window[5]) + (taps[6] * window[6]) + (taps[7] * window[7]);
                    }
                    else
                    {
                        for (var t = 0; t < 8; t++)
                        {
                            s += taps[t] * source[Math.Clamp(first + t, 0, lastX)];
                        }
                    }

                    destination[c] = (s + round0) >> interRound0;
                }
            }

            var filterV = SelectFilter(verticalFilter, h) * Av1Tables.SubpelFiltersLength1 * Av1Tables.SubpelFiltersLength2;
            var round1 = (1 << interRound1) >> 1;
            for (var r = 0; r < h; r++)
            {
                var p = (y & 1023) + (yStep * r);
                var taps = filters.Slice(filterV + (((p >> 6) & Av1Constants.SubpelMask) * Av1Tables.SubpelFiltersLength2), 8);
                var rows = intermediate[((p >> Av1Constants.ScaleSubpelBits) * w)..];
                var destination = pred.Slice(r * predStride, w);
                for (var c = 0; c < w; c++)
                {
                    var s = (taps[0] * rows[c]) + (taps[1] * rows[w + c]) + (taps[2] * rows[(2 * w) + c]) + (taps[3] * rows[(3 * w) + c])
                        + (taps[4] * rows[(4 * w) + c]) + (taps[5] * rows[(5 * w) + c]) + (taps[6] * rows[(6 * w) + c]) + (taps[7] * rows[(7 * w) + c]);
                    destination[c] = (s + round1) >> interRound1;
                }
            }
        }


        // The 4-tap variants of the filters are used for the blocks of 4 samples or less in the direction of the filter
        private static int SelectFilter(int interpFilter, int size)
        {
            if (size > 4)
                return interpFilter;

            return interpFilter switch
            {
                Av1Constants.Eighttap or Av1Constants.EighttapSharp => 4,
                Av1Constants.EighttapSmooth => 5,
                _ => interpFilter,
            };
        }
    }
}

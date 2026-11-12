#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace AvifDecoding
{
    /// <summary>
    /// The constrained directional enhancement filter of AV1 (AV1 specification, section 7.15), applied in place to a deblocked
    /// frame. The filter of an 8x8 block reads the samples of the frame before the filter up to two rows and columns around the
    /// block: the frame is filtered by rows of 8x8 blocks, from a copy of the rows of the band and of the two rows above it.
    /// </summary>
    internal static class Av1Cdef
    {
        /// <summary>The number of rows of the unfiltered copy of a band: 8 rows, and 2 rows above and below.</summary>
        public const int SourceRows = 12;

        // The taps of a sample: 4 primary taps and 8 secondary taps
        private const int TapCount = 12;

        // The entries of the offsets of the taps of a direction: the 12 offsets, then their lowest and highest values
        private const int TapStride = TapCount + 2;

        /// <summary>Filters the planes of <paramref name="frame"/>, by rows of 8x8 blocks.</summary>
        /// <param name="frame">The deblocked frame.</param>
        /// <param name="state">The block-level state of the frame.</param>
        /// <param name="source">Scratch for the unfiltered rows: <see cref="SourceRows"/> rows of the stride of the luma plane, for each plane.</param>
        /// <param name="cancellationToken">The cancellation token of the operation.</param>
        public static void Apply(Av1FrameBuffer frame, Av1FrameState state, Span<ushort> source, CancellationToken cancellationToken)
        {
            var header = frame.Header;
            var sequence = frame.Sequence;
            var planeCount = sequence.PlaneCount;
            var bitDepth = sequence.BitDepth;
            var coeffShift = bitDepth - 8;
            var lumaStride = frame.GetPlane(0).Stride;
            var sourceLength = SourceRows * lumaStride;
            var cdefIdx = state.CdefIdx;
            var flags = state.Flags;
            var miCols = header.MiCols;
            var miRows = header.MiRows;

            // The offsets of the taps of the interior blocks of each plane in each direction, and their lowest and highest values
            Span<int> taps = stackalloc int[3 * 8 * TapStride];
            for (var plane = 0; plane < planeCount; plane++)
            {
                for (var dir = 0; dir < 8; dir++)
                {
                    var entry = taps.Slice(((plane * 8) + dir) * TapStride, TapStride);
                    GetTapOffsets(entry[..TapCount], frame.GetPlane(plane).Stride, dir);
                    GetTapExtent(entry[..TapCount], out entry[TapCount], out entry[TapCount + 1]);
                }
            }

            for (var r = 0; r < miRows; r += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The unfiltered rows: rows 0 and 1 are the two rows above the row of blocks, kept from the previous one
                for (var plane = 0; plane < planeCount; plane++)
                {
                    var subY = plane > 0 ? sequence.SubsamplingY : 0;
                    var buffer = frame.GetPlane(plane);
                    var stride = buffer.Stride;
                    var h = 8 >> subY;
                    var y0 = (r * Av1Constants.MiSize) >> subY;
                    var planeSource = source.Slice(plane * sourceLength, sourceLength);
                    if (r > 0)
                        planeSource.Slice(h * stride, 2 * stride).CopyTo(planeSource);

                    var rows = Math.Min(h + 2, buffer.AllocatedHeight - y0);
                    buffer.Samples.Slice(y0 * stride, rows * stride).CopyTo(planeSource[(2 * stride)..]);
                }

                for (var c = 0; c < miCols; c += 2)
                {
                    var idx = cdefIdx[((r >> 4) * state.CdefCols) + (c >> 4)];
                    if (idx is -1)
                        continue;

                    var index = (r * miCols) + c;
                    var skip = (flags[index] & flags[index + 1] & flags[index + miCols] & flags[index + miCols + 1] & Av1FrameState.SkipFlag) is not 0;
                    if (skip)
                        continue;

                    var luma = frame.GetPlane(0);
                    int yDir;
                    int variance;
                    if (header.CdefYPriStrength[idx] is 0 && (planeCount is 1 || header.CdefUvPriStrength[idx] is 0))
                    {
                        // Without primary strength, the direction is 0 and the variance is not used
                        yDir = 0;
                        variance = 0;
                    }
                    else
                    {
                        FindDirection(source[..sourceLength], luma.Stride, c * Av1Constants.MiSize, bitDepth, out yDir, out variance);
                    }

                    var priStr = header.CdefYPriStrength[idx] << coeffShift;
                    var secStr = header.CdefYSecStrength[idx] << coeffShift;
                    var dir = priStr is 0 ? 0 : yDir;
                    var varStr = (variance >> 6) is not 0 ? Math.Min(BitOperations.Log2((uint)(variance >> 6)), 12) : 0;
                    priStr = variance is not 0 ? ((priStr * (4 + varStr)) + 8) >> 4 : 0;
                    var damping = header.CdefDamping + coeffShift;
                    FilterBlock(luma, source[..sourceLength], r, c, 0, 0, miRows, miCols, priStr, secStr, damping, dir, coeffShift, taps.Slice(dir * TapStride, TapStride));
                    if (planeCount is 1)
                        continue;

                    priStr = header.CdefUvPriStrength[idx] << coeffShift;
                    secStr = header.CdefUvSecStrength[idx] << coeffShift;
                    dir = priStr is 0 ? 0 : Av1Tables.CdefUvDir[(((sequence.SubsamplingX * 2) + sequence.SubsamplingY) * 8) + yDir];
                    damping = header.CdefDamping + coeffShift - 1;
                    for (var plane = 1; plane < 3; plane++)
                    {
                        FilterBlock(frame.GetPlane(plane), source.Slice(plane * sourceLength, sourceLength), r, c, sequence.SubsamplingX, sequence.SubsamplingY, miRows, miCols, priStr, secStr, damping, dir, coeffShift, taps.Slice(((plane * 8) + dir) * TapStride, TapStride));
                    }
                }
            }
        }

        /// <summary>
        /// Section 7.15.2 "CDEF direction process" for the 8x8 luma block at column <paramref name="x0"/> of row 2 of
        /// <paramref name="source"/>.
        /// </summary>
        internal static void FindDirection(ReadOnlySpan<ushort> source, int stride, int x0, int bitDepth, out int yDir, out int variance)
        {
                FindDirectionScalar(source, stride, x0, bitDepth, out yDir, out variance);
        }

        /// <summary>The scalar reference of <see cref="FindDirection"/>.</summary>
        internal static void FindDirectionScalar(ReadOnlySpan<ushort> source, int stride, int x0, int bitDepth, out int yDir, out int variance)
        {
            Span<int> cost = stackalloc int[8];
            Span<int> partial = stackalloc int[8 * 15];
            partial.Clear();
            cost.Clear();
            for (var i = 0; i < 8; i++)
            {
                var row = source.Slice(((i + 2) * stride) + x0, 8);
                for (var j = 0; j < 8; j++)
                {
                    var x = (row[j] >> (bitDepth - 8)) - 128;
                    partial[i + j] += x;
                    partial[15 + i + (j / 2)] += x;
                    partial[30 + i] += x;
                    partial[45 + 3 + i - (j / 2)] += x;
                    partial[60 + 7 + i - j] += x;
                    partial[75 + 3 - (i / 2) + j] += x;
                    partial[90 + j] += x;
                    partial[105 + (i / 2) + j] += x;
                }
            }

            var divTable = Av1Tables.DivTable;
            for (var i = 0; i < 8; i++)
            {
                cost[2] += partial[30 + i] * partial[30 + i];
                cost[6] += partial[90 + i] * partial[90 + i];
            }

            cost[2] *= divTable[8];
            cost[6] *= divTable[8];
            for (var i = 0; i < 7; i++)
            {
                cost[0] += ((partial[i] * partial[i]) + (partial[14 - i] * partial[14 - i])) * divTable[i + 1];
                cost[4] += ((partial[60 + i] * partial[60 + i]) + (partial[60 + 14 - i] * partial[60 + 14 - i])) * divTable[i + 1];
            }

            cost[0] += partial[7] * partial[7] * divTable[8];
            cost[4] += partial[60 + 7] * partial[60 + 7] * divTable[8];
            for (var i = 1; i < 8; i += 2)
            {
                var p = partial.Slice(i * 15, 15);
                for (var j = 0; j < 4 + 1; j++)
                {
                    cost[i] += p[3 + j] * p[3 + j];
                }

                cost[i] *= divTable[8];
                for (var j = 0; j < 4 - 1; j++)
                {
                    cost[i] += ((p[j] * p[j]) + (p[10 - j] * p[10 - j])) * divTable[(2 * j) + 2];
                }
            }

            var bestCost = 0;
            yDir = 0;
            for (var i = 0; i < 8; i++)
            {
                if (cost[i] > bestCost)
                {
                    bestCost = cost[i];
                    yDir = i;
                }
            }

            variance = (bestCost - cost[(yDir + 4) & 7]) >> 10;
        }

        // Section 7.15.3 "CDEF filter process"
        // taps: the offsets of the taps of an interior block in the direction, then their lowest and highest values
        private static void FilterBlock(Av1Plane plane, ReadOnlySpan<ushort> source, int r, int c, int subX, int subY, int miRows, int miCols, int priStr, int secStr, int damping, int dir, int coeffShift, ReadOnlySpan<int> taps)
        {
            // Without strength the sum of the taps is zero, and the sample is within the range of its neighbors: it is unchanged
            if (priStr is 0 && secStr is 0)
                return;

            var stride = plane.Stride;
            var samples = plane.Samples;
            var x0 = (c * Av1Constants.MiSize) >> subX;
            var y0 = (r * Av1Constants.MiSize) >> subY;
            var w = 8 >> subX;
            var h = 8 >> subY;
            var priTaps = Av1Tables.CdefPriTaps.Slice(((priStr >> coeffShift) & 1) * 2, 2);
            var secTaps = Av1Tables.CdefSecTaps.Slice(((priStr >> coeffShift) & 1) * 2, 2);

            // The taps reach two samples around the block: when they are all inside the frame, none has to be tested
            var planeWidth = (miCols * Av1Constants.MiSize) >> subX;
            var planeHeight = (miRows * Av1Constants.MiSize) >> subY;
            if (x0 >= 2 && y0 >= 2 && x0 + w + 2 <= planeWidth && y0 + h + 2 <= planeHeight)
            {
                FilterInteriorBlock(samples.Slice((y0 * stride) + x0), source[(x0 - 2)..], stride, w, h, priStr, secStr, damping, taps, priTaps, secTaps);
                return;
            }

            FilterEdgeBlock(samples, stride, source, x0, y0, w, h, subX, subY, miRows, miCols, priStr, secStr, damping, dir, priTaps, secTaps);
        }

        /// <summary>
        /// Filters a block of a plane whose taps may be outside the frame: those are not available (<c>CdefAvailable</c> is 0).
        /// <paramref name="source"/> holds the unfiltered rows from two rows above the block.
        /// </summary>
        internal static void FilterEdgeBlock(Span<ushort> samples, int stride, ReadOnlySpan<ushort> source, int x0, int y0, int w, int h, int subX, int subY, int miRows, int miCols, int priStr, int secStr, int damping, int dir, ReadOnlySpan<byte> priTaps, ReadOnlySpan<byte> secTaps)
        {
                FilterEdgeBlockScalar(samples, stride, source, x0, y0, w, h, subX, subY, miRows, miCols, priStr, secStr, damping, dir, priTaps, secTaps);
        }

        /// <summary>The scalar reference of <see cref="FilterEdgeBlock"/>.</summary>
        internal static void FilterEdgeBlockScalar(Span<ushort> samples, int stride, ReadOnlySpan<ushort> source, int x0, int y0, int w, int h, int subX, int subY, int miRows, int miCols, int priStr, int secStr, int damping, int dir, ReadOnlySpan<byte> priTaps, ReadOnlySpan<byte> secTaps)
        {
            for (var i = 0; i < h; i++)
            {
                for (var j = 0; j < w; j++)
                {
                    var sum = 0;
                    int x = source[((i + 2) * stride) + x0 + j];
                    var max = x;
                    var min = x;
                    for (var k = 0; k < 2; k++)
                    {
                        for (var sign = -1; sign <= 1; sign += 2)
                        {
                            if (TryGet(source, stride, x0 + j, y0 + i, i + 2, dir, k, sign, subX, subY, miRows, miCols, out var p))
                            {
                                sum += priTaps[k] * Constrain(p - x, priStr, damping);
                                max = Math.Max(p, max);
                                min = Math.Min(p, min);
                            }

                            for (var dirOff = -2; dirOff <= 2; dirOff += 4)
                            {
                                if (TryGet(source, stride, x0 + j, y0 + i, i + 2, (dir + dirOff) & 7, k, sign, subX, subY, miRows, miCols, out var s))
                                {
                                    sum += secTaps[k] * Constrain(s - x, secStr, damping);
                                    max = Math.Max(s, max);
                                    min = Math.Min(s, min);
                                }
                            }
                        }
                    }

                    samples[((y0 + i) * stride) + x0 + j] = (ushort)Math.Clamp(x + ((8 + sum - (sum < 0 ? 1 : 0)) >> 4), min, max);
                }
            }
        }

        // The offsets of the taps in a window of the given stride: 4 primary taps, then 8 secondary taps; two taps of opposite
        // signs for each distance, the two taps of the first distance before the ones of the second
        private static void GetTapOffsets(Span<int> offsets, int stride, int dir)
        {
            var directions = Av1Tables.CdefDirections;
            for (var k = 0; k < 2; k++)
            {
                var offset = (directions[((dir * 2) + k) * 2] * stride) + directions[(((dir * 2) + k) * 2) + 1];
                offsets[2 * k] = offset;
                offsets[(2 * k) + 1] = -offset;
                var first = (dir + 6) & 7;
                var second = (dir + 2) & 7;
                var firstOffset = (directions[((first * 2) + k) * 2] * stride) + directions[(((first * 2) + k) * 2) + 1];
                var secondOffset = (directions[((second * 2) + k) * 2] * stride) + directions[(((second * 2) + k) * 2) + 1];
                offsets[4 + (4 * k)] = firstOffset;
                offsets[5 + (4 * k)] = -firstOffset;
                offsets[6 + (4 * k)] = secondOffset;
                offsets[7 + (4 * k)] = -secondOffset;
            }
        }

        // The filter of a block whose taps are all inside the frame. The window starts two rows above and two samples to the
        // left of the block: the block is at row 2 and column 2 of the window
        private static void FilterInteriorBlock(Span<ushort> destination, ReadOnlySpan<ushort> window, int stride, int w, int h, int priStr, int secStr, int damping, ReadOnlySpan<int> taps, ReadOnlySpan<byte> priTaps, ReadOnlySpan<byte> secTaps)
        {
            var priShift = priStr is 0 ? 0 : Math.Max(0, damping - BitOperations.Log2((uint)priStr));
            var secShift = secStr is 0 ? 0 : Math.Max(0, damping - BitOperations.Log2((uint)secStr));
            FilterInteriorRows(destination, window, stride, w, h, taps[..TapCount], taps[TapCount], taps[TapCount + 1], priStr, secStr, priShift, secShift, priTaps[0], priTaps[1], secTaps[0], secTaps[1]);
        }

        /// <summary>
        /// Filters the rows of a block whose taps are all inside the frame, from the offsets of its taps in the window.
        /// </summary>
        internal static void FilterInteriorRows(Span<ushort> destination, ReadOnlySpan<ushort> window, int stride, int w, int h, ReadOnlySpan<int> offsets, int priStr, int secStr, int priShift, int secShift, int priTap0, int priTap1, int secTap0, int secTap1)
        {
            if (offsets.Length is not TapCount)
            {
                FilterInteriorRowsScalar(destination, window, stride, w, h, offsets, priStr, secStr, priShift, secShift, priTap0, priTap1, secTap0, secTap1);
                return;
            }

            GetTapExtent(offsets, out var lowest, out var highest);
            FilterInteriorRows(destination, window, stride, w, h, offsets, lowest, highest, priStr, secStr, priShift, secShift, priTap0, priTap1, secTap0, secTap1);
        }

        // FilterInteriorRows for the 12 offsets of the taps, whose lowest and highest values are given
        private static void FilterInteriorRows(Span<ushort> destination, ReadOnlySpan<ushort> window, int stride, int w, int h, ReadOnlySpan<int> offsets, int lowest, int highest, int priStr, int secStr, int priShift, int secShift, int priTap0, int priTap1, int secTap0, int secTap1)
        {
                FilterInteriorRowsScalar(destination, window, stride, w, h, offsets, priStr, secStr, priShift, secShift, priTap0, priTap1, secTap0, secTap1);
        }

        /// <summary>The scalar reference of <see cref="FilterInteriorRows(Span{ushort}, ReadOnlySpan{ushort}, int, int, int, ReadOnlySpan{int}, int, int, int, int, int, int, int, int)"/>.</summary>
        internal static void FilterInteriorRowsScalar(Span<ushort> destination, ReadOnlySpan<ushort> window, int stride, int w, int h, ReadOnlySpan<int> offsets, int priStr, int secStr, int priShift, int secShift, int priTap0, int priTap1, int secTap0, int secTap1)
        {
            for (var i = 0; i < h; i++)
            {
                var center = ((i + 2) * stride) + 2;
                var row = destination.Slice(i * stride, w);
                for (var j = 0; j < w; j++, center++)
                {
                    int x = window[center];
                    var max = x;
                    var min = x;
                    var sum = 0;
                    for (var t = 0; t < 4; t++)
                    {
                        int p = window[center + offsets[t]];
                        max = Math.Max(p, max);
                        min = Math.Min(p, min);
                        if (priStr is not 0)
                            sum += (t < 2 ? priTap0 : priTap1) * ConstrainShifted(p - x, priStr, priShift);
                    }

                    for (var t = 4; t < TapCount; t++)
                    {
                        int p = window[center + offsets[t]];
                        max = Math.Max(p, max);
                        min = Math.Min(p, min);
                        if (secStr is not 0)
                            sum += (t < 8 ? secTap0 : secTap1) * ConstrainShifted(p - x, secStr, secShift);
                    }

                    row[j] = (ushort)Math.Clamp(x + ((8 + sum - (sum < 0 ? 1 : 0)) >> 4), min, max);
                }
            }
        }

        // The lowest and the highest offsets of the taps, and 0 (the center)
        private static void GetTapExtent(ReadOnlySpan<int> offsets, out int lowest, out int highest)
        {
            lowest = 0;
            highest = 0;
            foreach (var offset in offsets)
            {
                lowest = Math.Min(lowest, offset);
                highest = Math.Max(highest, offset);
            }
        }

        // The constrain function for a threshold that is not zero, whose damping shift is known
        private static int ConstrainShifted(int diff, int threshold, int shift)
        {
            var absolute = Math.Abs(diff);
            var magnitude = Math.Max(0, Math.Min(absolute, threshold - (absolute >> shift)));
            return diff < 0 ? -magnitude : magnitude;
        }

        // cdef_get_at: a sample is available when it is inside the frame
        private static bool TryGet(ReadOnlySpan<ushort> source, int stride, int x, int y, int sourceRow, int direction, int k, int sign, int subX, int subY, int miRows, int miCols, out int value)
        {
            var directions = Av1Tables.CdefDirections;
            var offsetY = sign * directions[((direction * 2) + k) * 2];
            var offsetX = sign * directions[(((direction * 2) + k) * 2) + 1];
            y += offsetY;
            x += offsetX;
            var candidateR = (y << subY) >> Av1Constants.MiSizeLog2;
            var candidateC = (x << subX) >> Av1Constants.MiSizeLog2;
            if (y < 0 || x < 0 || candidateR >= miRows || candidateC >= miCols)
            {
                value = 0;
                return false;
            }

            value = source[((sourceRow + offsetY) * stride) + x];
            return true;
        }

        private static int Constrain(int diff, int threshold, int damping)
        {
            if (threshold is 0)
                return 0;

            var dampingAdj = Math.Max(0, damping - BitOperations.Log2((uint)threshold));
            var magnitude = Math.Clamp(threshold - (Math.Abs(diff) >> dampingAdj), 0, Math.Abs(diff));
            return diff < 0 ? -magnitude : magnitude;
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;

namespace AvifDecoding
{
    /// <summary>
    /// The loop filter process of AV1 (AV1 specification, section 7.14): the deblocking of the edges of the transform blocks,
    /// applied in place to a reconstructed frame, on all vertical edges of a plane then on all its horizontal edges.
    /// </summary>
    internal ref struct Av1LoopFilter
    {
        // The number of filter levels of a frame without deltas per block: one per level index, segment, reference frame and mode type
        private const int LevelCount = Av1Constants.FrameLfCount * Av1Constants.MaxSegments * Av1Constants.TotalRefsPerFrame * 2;

        private readonly Av1SequenceHeader _sequence;
        private readonly Av1FrameHeader _header;
        private readonly ReadOnlySpan<byte> _miSizes;
        private readonly ReadOnlySpan<byte> _yModes;
        private readonly ReadOnlySpan<byte> _flags;
        private readonly ReadOnlySpan<byte> _segmentIds;
        private readonly ReadOnlySpan<sbyte> _refFrames;
        private readonly ReadOnlySpan<sbyte> _deltaLfs;
        private readonly int _miCols;
        private readonly int _bitDepth;

        // The filter levels of the blocks when they do not depend on a delta of the block (DeltaLfPresent is 0), indexed by
        // GetLevelIndex; empty otherwise
        private readonly Span<byte> _levels;
        private Span<ushort> _samples;
        private int _stride;
        private ReadOnlySpan<byte> _txSizes;
        private int _lvl;
        private int _limit;
        private int _blimit;
        private int _thresh;
        private int _subX;
        private int _subY;
        private int _frameWidth;
        private int _frameHeight;

        private Av1LoopFilter(Av1FrameBuffer frame, Av1FrameState state, Span<byte> levels) :this()
        {
            _sequence = frame.Sequence;
            _header = frame.Header;
            _miSizes = state.MiSizes;
            _yModes = state.YModes;
            _flags = state.Flags;
            _segmentIds = state.SegmentIds;
            _refFrames = _header.FrameIsIntra ? default : state.RefFrames;
            _deltaLfs = _header.DeltaLfPresent ? state.DeltaLfs : default;
            _miCols = _header.MiCols;
            _bitDepth = _sequence.BitDepth;
            if (!_header.DeltaLfPresent)
            {
                _levels = levels;
                for (var i = 0; i < Av1Constants.FrameLfCount; i++)
                {
                    for (var segment = 0; segment < Av1Constants.MaxSegments; segment++)
                    {
                        for (var reference = 0; reference < Av1Constants.TotalRefsPerFrame; reference++)
                        {
                            for (var modeType = 0; modeType < 2; modeType++)
                            {
                                _levels[GetLevelIndex(i, segment, reference, modeType)] = (byte)ComputeLevel(i, segment, reference, modeType, deltaLf: 0);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Filters the planes of <paramref name="frame"/> (section 7.14.1).</summary>
        /// <param name="frame">The reconstructed frame.</param>
        /// <param name="state">The block-level state of the frame.</param>
        /// <param name="cancellationToken">The cancellation token of the operation.</param>
        /// <remarks>
        /// An edge reads at most half the size of the transform blocks on each side of it (7 samples of a filter of size 16, 4
        /// of size 8 and 2 of size 4), so the edges of different rows of blocks of a pass never touch the same sample: the
        /// samples do not depend on the order of the rows.
        /// </remarks>
        public static void Apply(Av1FrameBuffer frame, Av1FrameState state, CancellationToken cancellationToken)
        {
            var header = frame.Header;
            for (var plane = 0; plane < frame.Sequence.PlaneCount; plane++)
            {
                if (plane is not 0 && header.LoopFilterLevel[1 + plane] is 0)
                    continue;

                for (var pass = 0; pass < 2; pass++)
                {
                    FilterRows(frame, state, plane, pass, 0, header.MiRows, cancellationToken);
                }
            }
        }

        // The edges of a pass of a plane on the rows of blocks from rowStart to rowEnd, two multiples of 4. The edges are filtered
        // by pairs of consecutive edges along their lines: the vertical edges of two rows of blocks, or the horizontal edges of
        // two columns. The lines of the edges of a pair are distinct and the edges that cross a line are still filtered in order,
        // so the samples are those of the order of section 7.14.1
        private static void FilterRows(Av1FrameBuffer frame, Av1FrameState state, int plane, int pass, int rowStart, int rowEnd, CancellationToken cancellationToken)
        {
            Span<byte> levels = stackalloc byte[LevelCount];
            var filter = new Av1LoopFilter(frame, state, levels);
            var sequence = frame.Sequence;
            var planeBuffer = frame.GetPlane(plane);
            filter._samples = planeBuffer.Samples;
            filter._stride = planeBuffer.Stride;
            filter._txSizes = state.GetLoopFilterTxSizes(plane);
            filter._subX = plane is 0 ? 0 : sequence.SubsamplingX;
            filter._subY = plane is 0 ? 0 : sequence.SubsamplingY;
            filter._frameWidth = frame.Header.FrameWidth;
            filter._frameHeight = frame.Header.FrameHeight;
            var rowStep = plane is 0 ? 1 : 1 << sequence.SubsamplingY;
            var colStep = plane is 0 ? 1 : 1 << sequence.SubsamplingX;
            var miCols = frame.Header.MiCols;
            var miRows = frame.Header.MiRows;
            var subX = plane is 0 ? 0 : sequence.SubsamplingX;
            var subY = plane is 0 ? 0 : sequence.SubsamplingY;
            var txSizes = filter._txSizes;
            for (var row = rowStart; row < rowEnd; row += pass is 0 ? 2 * rowStep : rowStep)
            {
                if ((row & 15) is 0)
                    cancellationToken.ThrowIfCancellationRequested();

                // Most of the positions are inside a transform block, where TryGetEdge rejects both edges of a pair
                if (pass is 0)
                {
                    // The vertical edges are the left columns of the transform blocks: from a column, the next one of a row is at the
                    // end of its transform block
                    var row2 = row + rowStep;
                    var hasRow2 = row2 < miRows;
                    var first = (row >> subY) * miCols;
                    var second = (row2 >> subY) * miCols;
                    for (var col = 0; col < miCols;)
                    {
                        var x = (col * Av1Constants.MiSize) >> subX;
                        var width = Av1Tables.TxWidth[txSizes[first + (col >> subX)]] - 1;
                        var isEdge = (x & width) is 0;
                        var next = (x | width) + 1;
                        if (hasRow2)
                        {
                            var width2 = Av1Tables.TxWidth[txSizes[second + (col >> subX)]] - 1;
                            isEdge |= (x & width2) is 0;
                            next = Math.Min(next, (x | width2) + 1);
                        }

                        if (isEdge)
                            filter.FilterEdgePair(plane, pass: 0, row, col, row2, col);

                        col = (next << subX) >> Av1Constants.MiSizeLog2;
                    }
                }
                else
                {
                    // A transform block has a horizontal edge on the row in all its columns or in none: after a pair without an
                    // edge, the next pair that may have one starts in the next transform block of its second column
                    var rowSizes = (row >> subY) * miCols;
                    var y = (row * Av1Constants.MiSize) >> subY;
                    for (var col = 0; col < miCols;)
                    {
                        var col2 = col + colStep;
                        var isEdge = (y & (Av1Tables.TxHeight[txSizes[rowSizes + (col >> subX)]] - 1)) is 0;
                        if (col2 < miCols)
                        {
                            var txSz2 = txSizes[rowSizes + (col2 >> subX)];
                            if (!isEdge && (y & (Av1Tables.TxHeight[txSz2] - 1)) is not 0)
                            {
                                var next = ((((col2 * Av1Constants.MiSize) >> subX) | (Av1Tables.TxWidth[txSz2] - 1)) + 1) << subX >> Av1Constants.MiSizeLog2;
                                col = next & ~((2 * colStep) - 1);
                                continue;
                            }

                            isEdge = true;
                        }

                        if (isEdge)
                            filter.FilterEdgePair(plane, pass: 1, row, col, row, col2);

                        col += 2 * colStep;
                    }
                }
            }
        }

        // Filters the edge (row, col) and the next one along its lines (row2, col2), outside the frame for the last edge of an odd
        // number: two edges of the same size are filtered together, 8 lines per vector
        private void FilterEdgePair(int plane, int pass, int row, int col, int row2, int col2)
        {
            var hasFirst = TryGetEdge(plane, pass, row, col, out var position, out var filterSize);
            var (limit, blimit, thresh) = (_limit, _blimit, _thresh);
            var position2 = 0;
            var filterSize2 = 0;
            var hasSecond = row2 < _header.MiRows && col2 < _miCols && TryGetEdge(plane, pass, row2, col2, out position2, out filterSize2);
            var step = pass is 0 ? 1 : _stride;
            var lineStep = pass is 0 ? _stride : 1;
            if (hasFirst && hasSecond && filterSize == filterSize2)
            {
                FilterLinePairs(_samples, position, step, lineStep, plane, filterSize, _bitDepth, limit, blimit, thresh, _limit, _blimit, _thresh);
                return;
            }

            if (hasFirst)
                FilterLines(_samples, position, step, lineStep, plane, filterSize, _bitDepth, limit, blimit, thresh);

            if (hasSecond)
                FilterLines(_samples, position2, step, lineStep, plane, filterSize2, _bitDepth, _limit, _blimit, _thresh);
        }

        // Section 7.14.2 "Edge loop filter process": whether the edge is filtered, the position of its first line, its filter
        // size, and its levels in _limit, _blimit and _thresh
        private bool TryGetEdge(int plane, int pass, int row, int col, out int position, out int filterSize)
        {
            position = 0;
            filterSize = 0;
            var subX = _subX;
            var subY = _subY;
            var dx = pass is 0 ? 1 : 0;
            var dy = 1 - dx;
            var x = col * Av1Constants.MiSize;
            var y = row * Av1Constants.MiSize;
            row |= subY;
            col |= subX;
            if (x >= _frameWidth || y >= _frameHeight || (pass is 0 && x is 0) || (pass is 1 && y is 0))
                return false;

            var xP = x >> subX;
            var yP = y >> subY;
            var txSizes = _txSizes;
            var txSz = txSizes[((row >> subY) * _miCols) + (col >> subX)];

            // Most of the positions are inside a transform block: they are rejected before the state of the block is read
            var isTxEdge = pass is 0 ? (xP & (Av1Tables.TxWidth[txSz] - 1)) is 0 : (yP & (Av1Tables.TxHeight[txSz] - 1)) is 0;
            if (!isTxEdge)
                return false;

            var prevRow = row - (dy << subY);
            var prevCol = col - (dx << subX);
            var index = (row * _miCols) + col;
            var prevTxSz = txSizes[((prevRow >> subY) * _miCols) + (prevCol >> subX)];
            var baseSize = pass is 0
                ? Math.Min(Av1Tables.TxWidth[prevTxSz], Av1Tables.TxWidth[txSz])
                : Math.Min(Av1Tables.TxHeight[prevTxSz], Av1Tables.TxHeight[txSz]);

            // The edges of intra blocks are filtered: the block edge and the skip flag only matter for the others
            var isIntra = _refFrames.IsEmpty || _refFrames[index * 2] <= Av1Constants.IntraFrame;
            if (!isIntra)
            {
                var miSize = _miSizes[index];
                var planeSize = Av1Tables.SubsampledSize[(((miSize * 2) + subX) * 2) + subY];
                var skip = (_flags[index] & Av1FrameState.SkipFlag) is not 0;

                // The block and transform sizes are powers of two, and xP is not negative
                var isBlockEdge = pass is 0
                    ? (xP & ((4 * Av1Tables.Num4x4BlocksWide[planeSize]) - 1)) is 0
                    : (yP & ((4 * Av1Tables.Num4x4BlocksHigh[planeSize]) - 1)) is 0;
                if (!isBlockEdge && skip)
                    return false;
            }

            // Section 7.14.3 "Filter size process"
            filterSize = Math.Min(plane is 0 ? 16 : 8, baseSize);
            ComputeFilterStrength(row, col, plane, pass);
            if (_lvl is 0)
                ComputeFilterStrength(prevRow, prevCol, plane, pass);

            // The 4 lines of the edge start at the position: rows for a vertical edge, columns for a horizontal one
            position = (yP * _stride) + xP;
            return _lvl is not 0;
        }

        /// <summary>
        /// Filters the 4 lines of an edge (section 7.14.6 for each line): the line i crosses the edge at
        /// <paramref name="position"/> + i * <paramref name="lineStep"/>, and its samples are <paramref name="step"/> apart.
        /// </summary>
        internal static void FilterLines(Span<ushort> samples, int position, int step, int lineStep, int plane, int filterSize, int bitDepth, int limit, int blimit, int thresh)
        {
                FilterLinesScalar(samples, position, step, lineStep, plane, filterSize, bitDepth, limit, blimit, thresh);
        }

        /// <summary>The scalar reference of <see cref="FilterLines"/>.</summary>
        internal static void FilterLinesScalar(Span<ushort> samples, int position, int step, int lineStep, int plane, int filterSize, int bitDepth, int limit, int blimit, int thresh)
        {
            for (var i = 0; i < Av1Constants.MiSize; i++)
            {
                FilterSamples(samples, position + (i * lineStep), step, plane, filterSize, bitDepth, limit, blimit, thresh);
            }
        }

        /// <summary>
        /// Filters the 8 lines of two consecutive edges of the same filter size: the lines of the edge at
        /// <paramref name="position"/>, with the levels <paramref name="limit0"/>, <paramref name="blimit0"/> and
        /// <paramref name="thresh0"/>, then the lines of the next edge, 4 lines further, with the levels <paramref name="limit1"/>,
        /// <paramref name="blimit1"/> and <paramref name="thresh1"/>.
        /// </summary>
        internal static void FilterLinePairs(Span<ushort> samples, int position, int step, int lineStep, int plane, int filterSize, int bitDepth, int limit0, int blimit0, int thresh0, int limit1, int blimit1, int thresh1)
        {
                FilterLinesScalar(samples, position, step, lineStep, plane, filterSize, bitDepth, limit0, blimit0, thresh0);
                FilterLinesScalar(samples, position + (Av1Constants.MiSize * lineStep), step, lineStep, plane, filterSize, bitDepth, limit1, blimit1, thresh1);
        }

        // Sections 7.14.4 "Adaptive filter strength process" and 7.14.5 "Adaptive filter strength selection process"
        private void ComputeFilterStrength(int row, int col, int plane, int pass)
        {
            var index = (row * _miCols) + col;
            int segment = _segmentIds[index];
            var reference = _refFrames.IsEmpty ? Av1Constants.IntraFrame : _refFrames[index * 2];
            int mode = _yModes[index];
            var modeType = mode >= Av1Constants.Nearestmv && mode is not Av1Constants.Globalmv and not Av1Constants.GlobalGlobalmv ? 1 : 0;
            var i = plane is 0 ? pass : plane + 1;
            int lvlSeg;
            if (!_levels.IsEmpty && (uint)segment < Av1Constants.MaxSegments && (uint)reference < Av1Constants.TotalRefsPerFrame)
            {
                lvlSeg = _levels[GetLevelIndex(i, segment, reference, modeType)];
            }
            else
            {
                var deltaLf = 0;
                if (!_deltaLfs.IsEmpty)
                    deltaLf = _deltaLfs[(index * Av1Constants.FrameLfCount) + (_header.DeltaLfMulti ? i : 0)];

                lvlSeg = ComputeLevel(i, segment, reference, modeType, deltaLf);
            }

            _lvl = lvlSeg;
            var sharpness = _header.LoopFilterSharpness;
            var shift = sharpness > 4 ? 2 : sharpness > 0 ? 1 : 0;
            _limit = sharpness > 0 ? Math.Clamp(lvlSeg >> shift, 1, 9 - sharpness) : Math.Max(1, lvlSeg >> shift);
            _blimit = (2 * (lvlSeg + 2)) + _limit;
            _thresh = lvlSeg >> 4;
        }

        // The filter level of section 7.14.4 (lvlSeg) of a block, from the index i of the level (the pass for the luma, 1 + the
        // plane for the chroma), its segment, its first reference frame and its mode type
        private readonly int ComputeLevel(int i, int segment, int reference, int modeType, int deltaLf)
        {
            var lvlSeg = Math.Clamp(deltaLf + _header.LoopFilterLevel[i], 0, Av1Constants.MaxLoopFilter);
            var feature = Av1Constants.SegLvlAltLfYV + i;
            if (_header.IsSegmentationFeatureActive(segment, feature))
                lvlSeg = Math.Clamp(_header.Segmentation.GetData(segment, feature) + lvlSeg, 0, Av1Constants.MaxLoopFilter);

            if (_header.LoopFilterDeltaEnabled)
            {
                var nShift = lvlSeg >> 5;
                if (reference is Av1Constants.IntraFrame)
                {
                    lvlSeg += _header.LoopFilterDeltas.RefDeltas[Av1Constants.IntraFrame] << nShift;
                }
                else
                {
                    lvlSeg += (_header.LoopFilterDeltas.RefDeltas[reference] << nShift) + (_header.LoopFilterDeltas.ModeDeltas[modeType] << nShift);
                }

                lvlSeg = Math.Clamp(lvlSeg, 0, Av1Constants.MaxLoopFilter);
            }

            return lvlSeg;
        }

        private static int GetLevelIndex(int i, int segment, int reference, int modeType)
            => (((((i * Av1Constants.MaxSegments) + segment) * Av1Constants.TotalRefsPerFrame) + reference) * 2) + modeType;

        // Section 7.14.6 "Sample filtering process"
        private static void FilterSamples(Span<ushort> samples, int position, int step, int plane, int filterSize, int bitDepth, int limit, int blimit, int thresh)
        {
            int q0 = samples[position];
            int q1 = samples[position + step];
            int p0 = samples[position - step];
            int p1 = samples[position - (2 * step)];

            // Section 7.14.6.2 "Filter mask process"
            var shift = bitDepth - 8;
            var hevMask = Math.Abs(p1 - p0) > (thresh << shift) || Math.Abs(q1 - q0) > (thresh << shift);
            int filterLen;
            if (filterSize is 4)
            {
                filterLen = 4;
            }
            else if (plane is not 0)
            {
                filterLen = 6;
            }
            else
            {
                filterLen = filterSize is 8 ? 8 : 16;
            }

            var limitBd = limit << shift;
            var blimitBd = blimit << shift;
            var masked = Math.Abs(p1 - p0) > limitBd || Math.Abs(q1 - q0) > limitBd || (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) / 2) > blimitBd;
            int q2 = 0;
            int p2 = 0;
            int q3 = 0;
            int p3 = 0;
            if (filterLen >= 6)
            {
                q2 = samples[position + (2 * step)];
                p2 = samples[position - (3 * step)];
                masked |= Math.Abs(p2 - p1) > limitBd || Math.Abs(q2 - q1) > limitBd;
            }

            if (filterLen >= 8)
            {
                q3 = samples[position + (3 * step)];
                p3 = samples[position - (4 * step)];
                masked |= Math.Abs(p3 - p2) > limitBd || Math.Abs(q3 - q2) > limitBd;
            }

            if (masked)
                return;

            var thresholdBd = 1 << shift;
            var flatMask = false;
            if (filterSize >= 8)
            {
                flatMask = Math.Abs(p1 - p0) <= thresholdBd && Math.Abs(q1 - q0) <= thresholdBd && Math.Abs(p2 - p0) <= thresholdBd && Math.Abs(q2 - q0) <= thresholdBd;
                if (filterLen >= 8)
                    flatMask &= Math.Abs(p3 - p0) <= thresholdBd && Math.Abs(q3 - q0) <= thresholdBd;
            }

            var flatMask2 = false;
            if (filterSize >= 16)
            {
                flatMask2 = true;
                for (var k = 4; k <= 6; k++)
                {
                    int q = samples[position + (k * step)];
                    int p = samples[position - ((k + 1) * step)];
                    flatMask2 &= Math.Abs(p - p0) <= thresholdBd && Math.Abs(q - q0) <= thresholdBd;
                }
            }

            if (filterSize is 4 || !flatMask)
            {
                NarrowFilter(samples, position, step, hevMask, bitDepth);
            }
            else if (filterSize is 8 || !flatMask2)
            {
                WideFilter(samples, position, step, plane, log2Size: 3);
            }
            else
            {
                WideFilter(samples, position, step, plane, log2Size: 4);
            }
        }

        // Section 7.14.6.3 "Narrow filter process"
        private static void NarrowFilter(Span<ushort> samples, int position, int step, bool hevMask, int bitDepth)
        {
            var offset = 0x80 << (bitDepth - 8);
            var min = -(1 << (bitDepth - 1));
            var max = (1 << (bitDepth - 1)) - 1;
            var ps1 = samples[position - (2 * step)] - offset;
            var ps0 = samples[position - step] - offset;
            var qs0 = samples[position] - offset;
            var qs1 = samples[position + step] - offset;
            var filter = hevMask ? Math.Clamp(ps1 - qs1, min, max) : 0;
            filter = Math.Clamp(filter + (3 * (qs0 - ps0)), min, max);
            var filter1 = Math.Clamp(filter + 4, min, max) >> 3;
            var filter2 = Math.Clamp(filter + 3, min, max) >> 3;
            samples[position] = (ushort)(Math.Clamp(qs0 - filter1, min, max) + offset);
            samples[position - step] = (ushort)(Math.Clamp(ps0 + filter2, min, max) + offset);
            if (!hevMask)
            {
                filter = (filter1 + 1) >> 1;
                samples[position + step] = (ushort)(Math.Clamp(qs1 - filter, min, max) + offset);
                samples[position - (2 * step)] = (ushort)(Math.Clamp(ps1 + filter, min, max) + offset);
            }
        }

        // Section 7.14.6.4 "Wide filter process"
        private static void WideFilter(Span<ushort> samples, int position, int step, int plane, int log2Size)
        {
            int n;
            if (log2Size is 4)
            {
                n = 6;
            }
            else
            {
                n = plane is 0 ? 3 : 2;
            }

            var n2 = log2Size is 3 && plane is 0 ? 0 : 1;

            // The taps of output i are the samples i + j for j from -n to n, with a weight of 2 for |j| <= n2 and 1 otherwise,
            // their positions clamped to -(n + 1) to n: the sum of a window of 2n + 1 samples plus a window of 2n2 + 1 samples,
            // both sliding by one sample from one output to the next. The samples are read before any of them is written.
            Span<int> values = stackalloc int[14];
            values = values[..((2 * n) + 2)];
            for (var k = 0; k < values.Length; k++)
            {
                values[k] = samples[position + ((k - n - 1) * step)];
            }

            var wide = 0;
            for (var j = -n; j <= n; j++)
            {
                wide += Tap(values, -n + j, n);
            }

            var narrow = 0;
            for (var j = -n2; j <= n2; j++)
            {
                narrow += Tap(values, -n + j, n);
            }

            var round = 1 << (log2Size - 1);
            for (var i = -n; i < n; i++)
            {
                samples[position + (i * step)] = (ushort)((wide + narrow + round) >> log2Size);
                wide += Tap(values, i + n + 1, n) - Tap(values, i - n, n);
                narrow += Tap(values, i + n2 + 1, n) - Tap(values, i - n2, n);
            }

            static int Tap(ReadOnlySpan<int> values, int p, int n) => values[Math.Clamp(p, -(n + 1), n) + n + 1];
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
namespace AvifDecoding
{
    /// <summary>
    /// The post-processing filters of the decode frame wrapup process (AV1 specification, section 7.4): the loop filter, CDEF,
    /// the super-resolution upscaling and the loop restoration, applied in place to the planes of a frame. The filters that read
    /// the frame of the previous stage do so from copies of a few rows, so that a frame is never duplicated.
    /// </summary>
    internal sealed class Av1PostFilter : IDisposable
    {
        private const int BoundaryRows = 4;

        private Av1PooledArray<ushort>? _rows;
        private Av1PooledArray<ushort>? _boundaries;
        private Av1PooledArray<int>? _scratch;

        /// <summary>Applies the filters that the header of <paramref name="frame"/> enables.</summary>
        /// <param name="scope">The allocation scope of the operation.</param>
        /// <param name="frame">The frame to filter in place.</param>
        /// <param name="state">The block-level state of the frame.</param>
        /// <param name="cancellationToken">The cancellation token of the operation.</param>
        /// <exception cref="ImageResourceLimitException">The working buffers exceed the live-allocation limit.</exception>
        public void Apply(AllocationScope scope, Av1FrameBuffer frame, Av1FrameState state, CancellationToken cancellationToken)
        {
            var header = frame.Header;
            var sequence = frame.Sequence;
            if (header.LoopFilterLevel[0] is not 0 || header.LoopFilterLevel[1] is not 0)
                Av1LoopFilter.Apply(frame, state, cancellationToken);

            var useCdef = !header.CodedLossless && !header.AllowIntrabc && sequence.EnableCdef;
            var useRestoration = header.UsesLr;
            if (!useCdef && !header.UseSuperres && !useRestoration)
                return;

            EnsureRows(scope, frame);
            if (useRestoration)
                SaveBoundaries(scope, frame);

            if (useRestoration)
                _scratch ??= Av1PooledArray<int>.Rent(scope, Av1LoopRestoration.ScratchLength, clear: false);

            // The saved rows do not depend on the stages that modify the frame: they are upscaled first
            if (header.UseSuperres && useRestoration)
                UpscaleBoundaries(frame, cancellationToken);

            if (useCdef)
                Av1Cdef.Apply(frame, state, _rows!.Span, cancellationToken);

            if (header.UseSuperres)
                Upscale(frame, _rows!.Span, cancellationToken);

            if (useRestoration)
                Restore(frame, state, _rows!.Span, _scratch!.Span, cancellationToken);
        }

        // TryGetRestorationStripe, in the saved rows
        private bool TryGetStripe(Av1FrameBuffer frame, int plane, int stripe, Span<ushort> rows, out ReadOnlySpan<ushort> source, out int sourceStride, out int y0, out int height)
        {
            var header = frame.Header;
            var sequence = frame.Sequence;
            var subX = plane > 0 ? sequence.SubsamplingX : 0;
            var subY = plane > 0 ? sequence.SubsamplingY : 0;
            var buffer = frame.GetPlane(plane);
            var stride = buffer.Stride;
            var planeWidth = (header.UpscaledWidth + subX) >> subX;
            var planeEndX = planeWidth - 1;
            var planeEndY = buffer.Height - 1;
            var samples = buffer.Samples;
            sourceStride = planeWidth + (2 * Av1LoopRestoration.Padding);
            var stripeStartY = (-8 + (stripe * 64)) >> subY;
            var stripeEndY = stripeStartY + (64 >> subY) - 1;
            y0 = Math.Max(0, stripeStartY);
            var y1 = Math.Min(planeEndY, stripeEndY);
            height = y1 - y0 + 1;
            source = default;
            if (height <= 0)
                return false;

            // The saved rows of the planes that precede this one
            var boundaryOffset = 0;
            for (var i = 0; i < plane; i++)
            {
                var other = frame.GetPlane(i);
                boundaryOffset += GetStripeCount(other.Height, i > 0 ? sequence.SubsamplingY : 0) * BoundaryRows * other.Stride;
            }

            var boundaries = _boundaries!.Span.Slice(boundaryOffset, GetStripeCount(buffer.Height, subY) * BoundaryRows * stride);
            for (var i = 0; i < height + (2 * Av1LoopRestoration.Padding); i++)
            {
                var y = Math.Clamp(y0 - Av1LoopRestoration.Padding + i, 0, planeEndY);
                ReadOnlySpan<ushort> row;
                if (y < stripeStartY)
                {
                    y = Math.Max(stripeStartY - 2, y);
                    row = boundaries.Slice(((stripe * BoundaryRows) + (y - (stripeStartY - 2))) * stride, planeWidth);
                }
                else if (y > stripeEndY)
                {
                    y = Math.Min(stripeEndY + 2, y);
                    row = boundaries.Slice((((stripe + 1) * BoundaryRows) + 2 + (y - (stripeEndY + 1))) * stride, planeWidth);
                }
                else
                {
                    row = samples.Slice(y * stride, planeWidth);
                }

                var destination = rows.Slice(i * sourceStride, sourceStride);
                destination[..Av1LoopRestoration.Padding].Fill(row[0]);
                row.CopyTo(destination[Av1LoopRestoration.Padding..]);
                destination[(Av1LoopRestoration.Padding + planeWidth)..].Fill(row[planeEndX]);
            }

            source = rows[..((height + (2 * Av1LoopRestoration.Padding)) * sourceStride)];
            return true;
        }

        private void EnsureRows(AllocationScope scope, Av1FrameBuffer frame)
        {
            var lumaStride = frame.GetPlane(0).Stride;
            var stripeLength = (Av1LoopRestoration.MaxBlockHeight + (2 * Av1LoopRestoration.Padding)) * (lumaStride + (2 * Av1LoopRestoration.Padding));
            var rowsLength = Math.Max(3 * Av1Cdef.SourceRows * lumaStride, stripeLength);
            if (_rows is null || _rows.Length < rowsLength)
            {
                _rows?.Dispose();
                _rows = null;
                _rows = Av1PooledArray<ushort>.Rent(scope, rowsLength, clear: false);
            }
        }

        private static int GetStripeCount(int planeHeight, int subY) => ((((planeHeight - 1) << subY) + 8) / 64) + 1;

        // The rows of the deblocked frame around each stripe boundary: the loop restoration of a stripe reads them instead of the
        // rows that CDEF modified (section 7.17.6)
        private void SaveBoundaries(AllocationScope scope, Av1FrameBuffer frame)
        {
            long length = 0;
            for (var plane = 0; plane < frame.PlaneCount; plane++)
            {
                var buffer = frame.GetPlane(plane);
                length += (long)GetStripeCount(buffer.Height, plane > 0 ? frame.Sequence.SubsamplingY : 0) * BoundaryRows * buffer.Stride;
            }

            if (_boundaries is null || _boundaries.Length < length)
            {
                _boundaries?.Dispose();
                _boundaries = null;
                _boundaries = Av1PooledArray<ushort>.Rent(scope, length, clear: false);
            }

            var boundaries = _boundaries.Span;
            var offset = 0;
            for (var plane = 0; plane < frame.PlaneCount; plane++)
            {
                var subY = plane > 0 ? frame.Sequence.SubsamplingY : 0;
                var buffer = frame.GetPlane(plane);
                var stride = buffer.Stride;
                var samples = buffer.Samples;
                var stripeCount = GetStripeCount(buffer.Height, subY);
                for (var stripe = 1; stripe < stripeCount; stripe++)
                {
                    var boundaryY = (-8 + (64 * stripe)) >> subY;
                    for (var i = 0; i < BoundaryRows; i++)
                    {
                        var y = Math.Min(boundaryY - 2 + i, buffer.Height - 1);
                        samples.Slice(y * stride, stride).CopyTo(boundaries[(offset + (((stripe * BoundaryRows) + i) * stride))..]);
                    }
                }

                offset += stripeCount * BoundaryRows * stride;
            }
        }

        // Section 7.16 "Upscaling process", for the rows of each plane, with a copy of each row
        // in scratch
        private static void Upscale(Av1FrameBuffer frame, Span<ushort> scratch, CancellationToken cancellationToken)
        {
            var header = frame.Header;
            var sequence = frame.Sequence;
            for (var plane = 0; plane < frame.PlaneCount; plane++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subX = plane > 0 ? sequence.SubsamplingX : 0;
                var buffer = frame.GetPlane(plane);
                var stride = buffer.Stride;
                var downscaledWidth = (header.FrameWidth + subX) >> subX;
                var upscaledWidth = (header.UpscaledWidth + subX) >> subX;
                var maxX = ((header.MiCols >> subX) * Av1Constants.MiSize) - 1;
                var samples = buffer.Samples;
                var row = scratch[..stride];
                for (var y = 0; y < buffer.Height; y++)
                {
                    var destination = samples.Slice(y * stride, stride);
                    destination.CopyTo(row);
                    Av1SuperRes.UpscaleRow(row, destination, downscaledWidth, upscaledWidth, maxX, sequence.BitDepth);
                }
            }
        }

        // Section 7.16, for the saved rows of the deblocked frame that the loop restoration reads
        private void UpscaleBoundaries(Av1FrameBuffer frame, CancellationToken cancellationToken)
        {
            var header = frame.Header;
            var sequence = frame.Sequence;
            var row = _rows!.Span[..frame.GetPlane(0).Stride];
            var boundaryOffset = 0;
            for (var plane = 0; plane < frame.PlaneCount; plane++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subX = plane > 0 ? sequence.SubsamplingX : 0;
                var subY = plane > 0 ? sequence.SubsamplingY : 0;
                var buffer = frame.GetPlane(plane);
                var stride = buffer.Stride;
                var downscaledWidth = (header.FrameWidth + subX) >> subX;
                var upscaledWidth = (header.UpscaledWidth + subX) >> subX;
                var maxX = ((header.MiCols >> subX) * Av1Constants.MiSize) - 1;
                var stripeCount = GetStripeCount(buffer.Height, subY);
                var boundaries = _boundaries!.Span.Slice(boundaryOffset, stripeCount * BoundaryRows * stride);
                for (var i = BoundaryRows; i < stripeCount * BoundaryRows; i++)
                {
                    var destination = boundaries.Slice(i * stride, stride);
                    destination.CopyTo(row[..stride]);
                    Av1SuperRes.UpscaleRow(row[..stride], destination, downscaledWidth, upscaledWidth, maxX, sequence.BitDepth);
                }

                boundaryOffset += stripeCount * BoundaryRows * stride;
            }
        }

        // Section 7.17 "Loop restoration process", by stripes of 64 luma rows offset upwards by 8 rows: the stripes of each
        // plane. A stripe reads the rows of the frame that it modifies, and the saved rows of the
        // deblocked frame beyond it, so the stripes do not depend on each other
        private void Restore(Av1FrameBuffer frame, Av1FrameState state, Span<ushort> rows, Span<int> scratch, CancellationToken cancellationToken)
        {
            var header = frame.Header;
            var sequence = frame.Sequence;
            for (var plane = 0; plane < frame.PlaneCount; plane++)
            {
                if (header.FrameRestorationType[plane] is Av1Constants.RestoreNone)
                    continue;

                var subX = plane > 0 ? sequence.SubsamplingX : 0;
                var subY = plane > 0 ? sequence.SubsamplingY : 0;
                var buffer = frame.GetPlane(plane);
                var stride = buffer.Stride;
                var stripeCount = GetStripeCount(buffer.Height, subY);
                var planeWidth = (header.UpscaledWidth + subX) >> subX;
                var unitSize = header.LoopRestorationSize[plane];
                var unitRows = state.GetLoopRestorationUnitRows(plane);
                var unitCols = state.GetLoopRestorationUnitCols(plane);
                var units = state.GetLoopRestorationUnits(plane);
                var samples = buffer.Samples;
                for (var stripe = 0; stripe < stripeCount; stripe++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryGetStripe(frame, plane, stripe, rows, out var source, out var sourceStride, out var y0, out var h))
                        continue;

                    var unitRow = Math.Min(unitRows - 1, (((y0 << subY) + 8) >> subY) / unitSize);
                    for (var x = 0; x < planeWidth;)
                    {
                        var unitCol = Math.Min(unitCols - 1, x / unitSize);
                        var unitEnd = unitCol == unitCols - 1 ? planeWidth : (unitCol + 1) * unitSize;
                        var w = Math.Min(Av1LoopRestoration.MaxBlockWidth, unitEnd - x);
                        var unit = units.Slice(((unitRow * unitCols) + unitCol) * Av1FrameState.LoopRestorationUnitLength, Av1FrameState.LoopRestorationUnitLength);
                        var sourceOffset = (Av1LoopRestoration.Padding * sourceStride) + Av1LoopRestoration.Padding + x;
                        var destination = samples[((y0 * stride) + x)..];
                        if (unit[0] is Av1Constants.RestoreWiener)
                        {
                            Av1LoopRestoration.ApplyWiener(source, sourceStride, sourceOffset, destination, stride, w, h, unit, sequence.BitDepth, scratch);
                        }
                        else if (unit[0] is Av1Constants.RestoreSgrproj)
                        {
                            Av1LoopRestoration.ApplySelfGuided(source, sourceStride, sourceOffset, destination, stride, w, h, unit, sequence.BitDepth, scratch);
                        }

                        x += w;
                    }
                }
            }
        }

        public void Dispose()
        {
            _rows?.Dispose();
            _rows = null;
            _boundaries?.Dispose();
            _boundaries = null;
            _scratch?.Dispose();
            _scratch = null;
        }
    }
}

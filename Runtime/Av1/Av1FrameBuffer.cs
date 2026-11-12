#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
namespace AvifDecoding
{
    /// <summary>
    /// A decoded AV1 frame and the state that later frames load from it (AV1 specification, section 7.20 "Reference frame update
    /// process"): the sequence and frame headers of the frame that produced it, its planes and its cumulative distributions.
    /// A frame buffer is shared by the reference slots that hold it, the frame being decoded and the output frame: it is
    /// released when the last of them releases it.
    /// </summary>
    internal sealed class Av1FrameBuffer
    {
        private readonly Av1Plane?[] _planes = new Av1Plane?[3];
        private Av1PooledArray<sbyte>? _motionFieldRefFrames;
        private int _referenceCount = 1;

        public Av1FrameBuffer(Av1SequenceHeader sequence, Av1FrameHeader header)
        {
            Sequence = sequence;
            Header = header;
        }

        public Av1SequenceHeader Sequence { get; }

        public Av1FrameHeader Header { get; }

        /// <summary>Gets the number of planes: 1 for a monochrome frame, otherwise 3.</summary>
        public int PlaneCount => Sequence.PlaneCount;

        /// <summary>Gets the plane <paramref name="index"/>: 0 for Y, 1 for U, 2 for V.</summary>
        public Av1Plane GetPlane(int index) => _planes[index] ?? throw new ObjectDisposedException(nameof(Av1FrameBuffer));


        /// <summary>
        /// Rents the planes of the frame: the planes have the size of the frame after super-resolution
        /// upscaling, and at least the size of the coded frame.
        /// </summary>
        /// <exception cref="ImageResourceLimitException">The buffers exceed the live-allocation limit.</exception>
        public void Allocate(AllocationScope scope)
        {
            var alignment = Sequence.Use128x128Superblock ? 128 : 64;
            var width = Math.Max(Header.UpscaledWidth, Header.FrameWidth);
            try
            {
                for (var plane = 0; plane < PlaneCount; plane++)
                {
                    var subX = plane > 0 ? Sequence.SubsamplingX : 0;
                    var subY = plane > 0 ? Sequence.SubsamplingY : 0;
                    _planes[plane] = Av1Plane.Create(scope, (width + subX) >> subX, (Header.FrameHeight + subY) >> subY, alignment);
                }
            }
            catch
            {
                ReleaseBuffers();
                throw;
            }
        }

        /// <summary>Removes a holder of the frame buffer, and returns its buffers to the pool when it was the last one.</summary>
        public void Release()
        {
            if (--_referenceCount is 0)
                ReleaseBuffers();
        }

        private void ReleaseBuffers()
        {
            for (var i = 0; i < _planes.Length; i++)
            {
                _planes[i]?.Dispose();
                _planes[i] = null;
            }

            _motionFieldRefFrames?.Dispose();
            _motionFieldRefFrames = null;
        }
    }
}

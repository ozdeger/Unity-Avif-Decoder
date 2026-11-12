#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
namespace AvifDecoding
{
    /// <summary>
    /// The working buffers of the inter prediction process, of a size that does not depend on the frame: the two predictions of a
    /// block of up to 128x128 samples, the intermediate array of the interpolation filters and the blending mask. Rented by the
    /// decoder for the first frame that is predicted from another frame or from itself.
    /// </summary>
    internal sealed class Av1InterScratch : IDisposable
    {
        /// <summary>The number of values of a row of <see cref="GetPrediction"/> and of <see cref="Mask"/>.</summary>
        public const int Stride = Av1Constants.MaxSbSize;

        private const int PredictionLength = Stride * Stride;
        private const int IntermediateLength = Av1InterPredictor.MaxIntermediateHeight * Stride;

        private readonly Av1PooledArray<int> _values;

        public Av1InterScratch(AllocationScope scope)
        {
            _values = Av1PooledArray<int>.Rent(scope, PredictionLength + IntermediateLength, clear: false);
        }

        // An intra block copy makes one prediction: there is no second reference and no blending mask
        public Span<int> GetPrediction(int refList) => _values.Span.Slice(0, PredictionLength);

        public Span<int> Intermediate => _values.Span.Slice(PredictionLength, IntermediateLength);

        public void Dispose()
        {
            _values.Dispose();
        }
    }
}

#nullable enable
using System.Collections.Generic;
using System.Threading;
using System;
using System.Buffers;

namespace AvifDecoding
{
    /// <summary>
    /// An array of <typeparamref name="T"/> rented from the shared array pool. Ported: the original reinterprets a pooled byte
    /// buffer as <typeparamref name="T"/>; this one rents typed arrays, so every access stays bounds-checked.
    /// </summary>
    internal sealed class Av1PooledArray<T> : IDisposable
        where T : unmanaged
    {
        private T[]? _array;

        private Av1PooledArray(T[] array, int length)
        {
            _array = array;
            Length = length;
        }

        /// <summary>Gets the number of elements.</summary>
        public int Length { get; }

        /// <summary>Gets the elements.</summary>
        /// <exception cref="ObjectDisposedException">The array is disposed.</exception>
        public Span<T> Span
        {
            get
            {
                var array = _array ?? throw new ObjectDisposedException(nameof(Av1PooledArray<T>));
                return array.AsSpan(0, Length);
            }
        }

        /// <summary>Rents an array of <paramref name="length"/> elements.</summary>
        public static Av1PooledArray<T> Rent(AllocationScope scope, long length, bool clear = true, AllocationKind kind = AllocationKind.DecoderState)
        {
            if (scope == null)
                throw new ArgumentNullException(nameof(scope));
            if (length < 0 || length > int.MaxValue)
                throw CheckedSizes.CreateOverflowException(scope.Limits);

            var array = ArrayPool<T>.Shared.Rent((int)length);
            if (clear)
                Array.Clear(array, 0, (int)length);

            return new Av1PooledArray<T>(array, (int)length);
        }

        public void Dispose()
        {
            var array = _array;
            _array = null;
            if (array != null)
                ArrayPool<T>.Shared.Return(array);
        }
    }
}

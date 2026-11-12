#nullable enable
using System;

namespace AvifDecoding
{
    // Fixed-size value arrays standing in for the inline arrays of .NET 8: copying the struct copies the elements, as with
    // the originals, and every element access is checked. Elements are read and written by value through the indexer.
    internal struct InlineArray2<T>
    {
        private T _e0;
        private T _e1;

        public int Length => 2;

        public T this[int index]
        {
            readonly get
            {
                switch (index)
                {
                    case 0:
                        return _e0;
                    case 1:
                        return _e1;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }

            set
            {
                switch (index)
                {
                    case 0:
                        _e0 = value;
                        break;
                    case 1:
                        _e1 = value;
                        break;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }
        }

        public readonly void CopyTo(Span<T> destination)
        {
            if (destination.Length < 2)
                throw new ArgumentException("The destination is too short.", nameof(destination));

            destination[0] = _e0;
            destination[1] = _e1;
        }

        public void CopyFrom(ReadOnlySpan<T> source)
        {
            if (source.Length < 2)
                throw new ArgumentException("The source is too short.", nameof(source));

            _e0 = source[0];
            _e1 = source[1];
        }

        public void Fill(T value)
        {
            _e0 = value;
            _e1 = value;
        }

        public void Clear() => this = default;
    }

    internal struct InlineArray3<T>
    {
        private T _e0;
        private T _e1;
        private T _e2;

        public int Length => 3;

        public T this[int index]
        {
            readonly get
            {
                switch (index)
                {
                    case 0:
                        return _e0;
                    case 1:
                        return _e1;
                    case 2:
                        return _e2;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }

            set
            {
                switch (index)
                {
                    case 0:
                        _e0 = value;
                        break;
                    case 1:
                        _e1 = value;
                        break;
                    case 2:
                        _e2 = value;
                        break;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }
        }

        public readonly void CopyTo(Span<T> destination)
        {
            if (destination.Length < 3)
                throw new ArgumentException("The destination is too short.", nameof(destination));

            destination[0] = _e0;
            destination[1] = _e1;
            destination[2] = _e2;
        }

        public void CopyFrom(ReadOnlySpan<T> source)
        {
            if (source.Length < 3)
                throw new ArgumentException("The source is too short.", nameof(source));

            _e0 = source[0];
            _e1 = source[1];
            _e2 = source[2];
        }

        public void Fill(T value)
        {
            _e0 = value;
            _e1 = value;
            _e2 = value;
        }

        public void Clear() => this = default;
    }

    internal struct InlineArray4<T>
    {
        private T _e0;
        private T _e1;
        private T _e2;
        private T _e3;

        public int Length => 4;

        public T this[int index]
        {
            readonly get
            {
                switch (index)
                {
                    case 0:
                        return _e0;
                    case 1:
                        return _e1;
                    case 2:
                        return _e2;
                    case 3:
                        return _e3;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }

            set
            {
                switch (index)
                {
                    case 0:
                        _e0 = value;
                        break;
                    case 1:
                        _e1 = value;
                        break;
                    case 2:
                        _e2 = value;
                        break;
                    case 3:
                        _e3 = value;
                        break;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }
        }

        public readonly void CopyTo(Span<T> destination)
        {
            if (destination.Length < 4)
                throw new ArgumentException("The destination is too short.", nameof(destination));

            destination[0] = _e0;
            destination[1] = _e1;
            destination[2] = _e2;
            destination[3] = _e3;
        }

        public void CopyFrom(ReadOnlySpan<T> source)
        {
            if (source.Length < 4)
                throw new ArgumentException("The source is too short.", nameof(source));

            _e0 = source[0];
            _e1 = source[1];
            _e2 = source[2];
            _e3 = source[3];
        }

        public void Fill(T value)
        {
            _e0 = value;
            _e1 = value;
            _e2 = value;
            _e3 = value;
        }

        public void Clear() => this = default;
    }

    internal struct InlineArray6<T>
    {
        private T _e0;
        private T _e1;
        private T _e2;
        private T _e3;
        private T _e4;
        private T _e5;

        public int Length => 6;

        public T this[int index]
        {
            readonly get
            {
                switch (index)
                {
                    case 0:
                        return _e0;
                    case 1:
                        return _e1;
                    case 2:
                        return _e2;
                    case 3:
                        return _e3;
                    case 4:
                        return _e4;
                    case 5:
                        return _e5;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }

            set
            {
                switch (index)
                {
                    case 0:
                        _e0 = value;
                        break;
                    case 1:
                        _e1 = value;
                        break;
                    case 2:
                        _e2 = value;
                        break;
                    case 3:
                        _e3 = value;
                        break;
                    case 4:
                        _e4 = value;
                        break;
                    case 5:
                        _e5 = value;
                        break;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }
        }

        public readonly void CopyTo(Span<T> destination)
        {
            if (destination.Length < 6)
                throw new ArgumentException("The destination is too short.", nameof(destination));

            destination[0] = _e0;
            destination[1] = _e1;
            destination[2] = _e2;
            destination[3] = _e3;
            destination[4] = _e4;
            destination[5] = _e5;
        }

        public void CopyFrom(ReadOnlySpan<T> source)
        {
            if (source.Length < 6)
                throw new ArgumentException("The source is too short.", nameof(source));

            _e0 = source[0];
            _e1 = source[1];
            _e2 = source[2];
            _e3 = source[3];
            _e4 = source[4];
            _e5 = source[5];
        }

        public void Fill(T value)
        {
            _e0 = value;
            _e1 = value;
            _e2 = value;
            _e3 = value;
            _e4 = value;
            _e5 = value;
        }

        public void Clear() => this = default;
    }

    internal struct InlineArray8<T>
    {
        private T _e0;
        private T _e1;
        private T _e2;
        private T _e3;
        private T _e4;
        private T _e5;
        private T _e6;
        private T _e7;

        public int Length => 8;

        public T this[int index]
        {
            readonly get
            {
                switch (index)
                {
                    case 0:
                        return _e0;
                    case 1:
                        return _e1;
                    case 2:
                        return _e2;
                    case 3:
                        return _e3;
                    case 4:
                        return _e4;
                    case 5:
                        return _e5;
                    case 6:
                        return _e6;
                    case 7:
                        return _e7;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }

            set
            {
                switch (index)
                {
                    case 0:
                        _e0 = value;
                        break;
                    case 1:
                        _e1 = value;
                        break;
                    case 2:
                        _e2 = value;
                        break;
                    case 3:
                        _e3 = value;
                        break;
                    case 4:
                        _e4 = value;
                        break;
                    case 5:
                        _e5 = value;
                        break;
                    case 6:
                        _e6 = value;
                        break;
                    case 7:
                        _e7 = value;
                        break;
                    default:
                        throw new IndexOutOfRangeException();
                }
            }
        }

        public readonly void CopyTo(Span<T> destination)
        {
            if (destination.Length < 8)
                throw new ArgumentException("The destination is too short.", nameof(destination));

            destination[0] = _e0;
            destination[1] = _e1;
            destination[2] = _e2;
            destination[3] = _e3;
            destination[4] = _e4;
            destination[5] = _e5;
            destination[6] = _e6;
            destination[7] = _e7;
        }

        public void CopyFrom(ReadOnlySpan<T> source)
        {
            if (source.Length < 8)
                throw new ArgumentException("The source is too short.", nameof(source));

            _e0 = source[0];
            _e1 = source[1];
            _e2 = source[2];
            _e3 = source[3];
            _e4 = source[4];
            _e5 = source[5];
            _e6 = source[6];
            _e7 = source[7];
        }

        public void Fill(T value)
        {
            _e0 = value;
            _e1 = value;
            _e2 = value;
            _e3 = value;
            _e4 = value;
            _e5 = value;
            _e6 = value;
            _e7 = value;
        }

        public void Clear() => this = default;
    }

}

namespace AvifDecoding
{
    // Writes to an element of a nested fixed array: the inner array is read, changed and written back
    internal static class InlineArrayExtensions
    {
        public static void Set<T>(ref this InlineArray8<InlineArray6<T>> array, int i, int j, T value)
        {
            var inner = array[i];
            inner[j] = value;
            array[i] = inner;
        }

        public static void Set<T>(ref this InlineArray8<InlineArray8<T>> array, int i, int j, T value)
        {
            var inner = array[i];
            inner[j] = value;
            array[i] = inner;
        }

        public static void Set<T>(ref this InlineArray8<InlineArray4<T>> array, int i, int j, T value)
        {
            var inner = array[i];
            inner[j] = value;
            array[i] = inner;
        }

        public static void Set<T>(ref this InlineArray3<InlineArray2<T>> array, int i, int j, T value)
        {
            var inner = array[i];
            inner[j] = value;
            array[i] = inner;
        }

        public static void Set<T>(ref this InlineArray3<InlineArray2<InlineArray3<T>>> array, int i, int j, int k, T value)
        {
            var middle = array[i];
            var inner = middle[j];
            inner[k] = value;
            middle[j] = inner;
            array[i] = middle;
        }
    }
}

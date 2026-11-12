#nullable enable
using System;

namespace AvifDecoding
{
    // The .NET 5 to 8 APIs the decoder uses, which .NET Standard 2.1 does not have, written with checked array and span accesses
    internal static class BitOperations
    {
        private static readonly byte[] Log2DeBruijn =
        {
            0, 9, 1, 10, 13, 21, 2, 29, 11, 14, 16, 18, 22, 25, 3, 30,
            8, 12, 20, 28, 15, 17, 24, 7, 19, 27, 23, 6, 26, 5, 4, 31,
        };

        public static int Log2(uint value)
        {
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return Log2DeBruijn[(int)((value * 0x07C4ACDDu) >> 27)];
        }

        public static int Log2(ulong value)
        {
            var high = (uint)(value >> 32);
            return high != 0 ? 32 + Log2(high) : Log2((uint)value);
        }

        public static int LeadingZeroCount(uint value) => value == 0 ? 32 : 31 - Log2(value);

        public static int PopCount(uint value)
        {
            value -= (value >> 1) & 0x55555555u;
            value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
            value = (value + (value >> 4)) & 0x0F0F0F0Fu;
            return (int)((value * 0x01010101u) >> 24);
        }
    }

    internal static class SpanCompat
    {
        public static bool ContainsAnyExcept(this ReadOnlySpan<int> span, int value)
        {
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] != value)
                    return true;
            }

            return false;
        }

        public static bool ContainsAnyExcept(this Span<int> span, int value) => ((ReadOnlySpan<int>)span).ContainsAnyExcept(value);

        public static int LastIndexOfAnyExcept(this ReadOnlySpan<int> span, int value)
        {
            for (var i = span.Length - 1; i >= 0; i--)
            {
                if (span[i] != value)
                    return i;
            }

            return -1;
        }

        public static bool ContainsAnyExceptInRange(this ReadOnlySpan<int> span, int lowInclusive, int highInclusive)
        {
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] < lowInclusive || span[i] > highInclusive)
                    return true;
            }

            return false;
        }

        // The palettes have at most 8 colors
        public static void Sort(this Span<ushort> span)
        {
            for (var i = 1; i < span.Length; i++)
            {
                var value = span[i];
                var j = i - 1;
                while (j >= 0 && span[j] > value)
                {
                    span[j + 1] = span[j];
                    j--;
                }

                span[j + 1] = value;
            }
        }
    }
}

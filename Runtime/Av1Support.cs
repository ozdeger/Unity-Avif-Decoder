#nullable enable
using System.Collections.Generic;
using System.Threading;
using System;

namespace AvifDecoding
{
    // Stand-ins for the allocation accounting of Meziantou.Framework.Imaging: the AV1 decoder only needs a scope that rents
    // its buffers and refuses canvases and buffers past fixed limits.
    internal enum AllocationKind
    {
        Temporary,
        DecoderState,
    }

    internal sealed class ImageResourceLimits
    {
        public const long DefaultMaxPixels = 4096L * 4096L;

        public long MaxPixels { get; set; } = DefaultMaxPixels;

        public void EnsureCanvasWithinLimits(int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
                throw new ImageResourceLimitException($"The image of {width}x{height} pixels exceeds the limit of {MaxPixels} pixels.");
        }

        // The images that one output image is made of (its tiles, its alpha, its gain map, the inputs of its derivations and
        // their canvases) may hold TotalFactor times MaxPixels together, and each counts for at least MinimumImagePixels,
        // which bounds their number: a file cannot make the decoder work without end by nesting derived images.
        private const int TotalFactor = 16;
        private const long MinimumImagePixels = 128 * 128;
        private long _composedPixels;

        public void ChargeImage(int width, int height)
        {
            _composedPixels += Math.Max((long)width * height, MinimumImagePixels);
            var budget = MaxPixels > long.MaxValue / TotalFactor ? long.MaxValue : MaxPixels * TotalFactor;
            if (_composedPixels > budget)
                throw new ImageResourceLimitException($"The images that the file is built from exceed the limit of {budget} pixels in total.");
        }
    }

    internal sealed class AllocationScope
    {
        public AllocationScope(ImageResourceLimits limits)
        {
            Limits = limits;
        }

        public ImageResourceLimits Limits { get; }
    }


    internal static class CheckedSizes
    {

        public static ImageResourceLimitException CreateOverflowException(ImageResourceLimits limits)
        {
            return new ImageResourceLimitException("A buffer of the image exceeds the maximum buffer length.");
        }
    }

    internal enum ImageFormat
    {
        Avif,
    }

    public class ImageDecodeException : Exception
    {
        public ImageDecodeException(string message)
            : base(message)
        {
        }
    }

    public sealed class InvalidImageContentException : ImageDecodeException
    {
        internal InvalidImageContentException(string message, ImageFormat format)
            : base(message)
        {
        }
    }

    public sealed class UnsupportedImageFeatureException : ImageDecodeException
    {
        internal UnsupportedImageFeatureException(string message, ImageFormat format, string feature)
            : base(message)
        {
            Feature = feature;
        }

        public string Feature { get; }
    }

    public sealed class ImageResourceLimitException : ImageDecodeException
    {
        public ImageResourceLimitException(string message)
            : base(message)
        {
        }
    }
}

namespace System.Runtime.CompilerServices
{
    // The init accessors of the port need this type, which .NET Standard 2.1 does not have
    internal static class IsExternalInit
    {
    }
}

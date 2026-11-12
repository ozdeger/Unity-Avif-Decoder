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

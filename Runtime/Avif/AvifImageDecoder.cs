#nullable enable
// The AV1 decoder under Av1/ is ported from Meziantou.Framework.Imaging (MIT, see LICENSE.txt) at commit d1409b5f: decode
// only, key frames only, scalar paths only, C# 9 without unsafe code. The AVIF container reader and the RGB conversion are
// written for this port.
using System.Collections.Generic;
using System;
using System.Threading;

namespace AvifDecoding
{
    /// <summary>An 8-bit RGB image, top row first, three bytes per pixel.</summary>
    public sealed class AvifRgbImage
    {
        internal AvifRgbImage(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Pixels { get; }
    }

    /// <summary>
    /// Decodes the primary image of an AVIF file to 8-bit RGB, in managed code only. Every failure is an
    /// <see cref="ImageDecodeException"/>, whatever the file holds.
    /// </summary>
    public static class AvifImageDecoder
    {
        public static bool IsAvif(ReadOnlySpan<byte> data) => AvifContainer.IsAvif(data);

        /// <exception cref="ImageDecodeException">The file is not a valid AVIF still image, uses a feature that is not supported, or is too large.</exception>
        public static AvifRgbImage Decode(byte[] data, long maxPixels = ImageResourceLimits.DefaultMaxPixels)
        {
            try
            {
                return DecodeCore(data, maxPixels);
            }
            catch (ImageDecodeException)
            {
                throw;
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The decoder checks the bitstream, and every array access stays bounds-checked: a malformed file that slips
                // past the checks ends here instead of corrupting memory
                throw new InvalidImageContentException($"The AVIF file is invalid: {exception.GetType().Name}: {exception.Message}", ImageFormat.Avif);
            }
        }

        private static AvifRgbImage DecodeCore(byte[] data, long maxPixels)
        {
            var container = AvifContainer.Parse(data);
            var limits = new ImageResourceLimits { MaxPixels = maxPixels };
            if (container.Width > 0 && container.Height > 0)
                limits.EnsureCanvasWithinLimits(container.Width, container.Height);

            var scope = new AllocationScope(limits);
            using var decoder = new Av1Decoder(scope);
            decoder.SetInput(container.Payload, 0, container.Payload.Length);
            var frame = decoder.DecodeNextOutputFrame(CancellationToken.None) ?? throw new InvalidImageContentException("The AVIF file is invalid: the AV1 payload has no frame.", ImageFormat.Avif);
            try
            {
                var rgb = AvifRgbConverter.Convert(scope, frame, container);
                return AvifImageTransforms.Apply(rgb, container);
            }
            finally
            {
                frame.Release();
            }
        }
    }
}

#nullable enable
using System;

namespace AvifDecoding
{
    // The transformative properties of an image, in the order HEIF applies them: the clean aperture (clap), the rotation
    // (irot), then the mirroring (imir)
    internal static class AvifImageTransforms
    {
        public static AvifImage Apply(AvifImage image, AvifImageNode node)
        {
            if (node.HasCleanAperture && TryGetCrop(node.Crop, image.Width, image.Height, out var x, out var y, out var width, out var height))
                image = Crop(image, x, y, width, height);

            for (var i = 0; i < node.Rotation; i++)
                image = RotateAnticlockwise(image);

            // imir 0 exchanges the top and bottom of the image, 1 its left and right (HEIF 6.5.12, and libavif)
            if (node.MirrorAxis == 0)
                image = MirrorTopBottom(image);
            else if (node.MirrorAxis == 1)
                image = MirrorLeftRight(image);

            return image;
        }

        // The size of an image of a size once its transformative properties apply
        public static void GetTransformedSize(AvifImageNode node, int width, int height, out int outWidth, out int outHeight)
        {
            if (node.HasCleanAperture && TryGetCrop(node.Crop, width, height, out _, out _, out var cropWidth, out var cropHeight))
            {
                width = cropWidth;
                height = cropHeight;
            }

            outWidth = (node.Rotation & 1) != 0 ? height : width;
            outHeight = (node.Rotation & 1) != 0 ? width : height;
        }

        // A clean aperture whose size and position are not whole pixels inside the image is ignored: the image is shown uncropped
        private static bool TryGetCrop(CleanAperture clap, int imageWidth, int imageHeight, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            if (clap.WidthD <= 0 || clap.HeightD <= 0 || clap.HorizOffD <= 0 || clap.VertOffD <= 0)
                return false;
            if (clap.WidthN % clap.WidthD != 0 || clap.HeightN % clap.HeightD != 0)
                return false;

            width = clap.WidthN / clap.WidthD;
            height = clap.HeightN / clap.HeightD;
            if (width <= 0 || height <= 0 || width > imageWidth || height > imageHeight)
                return false;

            // The center of the aperture is horizOff + (imageWidth - 1) / 2; its left edge is center - (width - 1) / 2
            var left2 = ((long)clap.HorizOffN * 2 / clap.HorizOffD) + (imageWidth - 1) - (width - 1);
            var top2 = ((long)clap.VertOffN * 2 / clap.VertOffD) + (imageHeight - 1) - (height - 1);
            if ((long)clap.HorizOffN * 2 % clap.HorizOffD != 0 || (long)clap.VertOffN * 2 % clap.VertOffD != 0 || left2 % 2 != 0 || top2 % 2 != 0)
                return false;

            x = (int)(left2 / 2);
            y = (int)(top2 / 2);
            return x >= 0 && y >= 0 && x + width <= imageWidth && y + height <= imageHeight;
        }

        private static AvifImage Crop(AvifImage image, int x, int y, int width, int height)
        {
            var result = AvifImage.Create(width, height, image.PixelFormat);
            var channels = image.Channels;
            if (AvifImage.IsSixteenBit(image.PixelFormat))
                CropSamples(image.Pixels16, image.Width, result.Pixels16, x, y, width, height, channels);
            else
                CropSamples(image.Pixels, image.Width, result.Pixels, x, y, width, height, channels);

            return result;
        }

        private static AvifImage RotateAnticlockwise(AvifImage image)
        {
            var result = AvifImage.Create(image.Height, image.Width, image.PixelFormat);
            if (AvifImage.IsSixteenBit(image.PixelFormat))
                RotateSamples(image.Pixels16, image.Width, image.Height, result.Pixels16, image.Channels);
            else
                RotateSamples(image.Pixels, image.Width, image.Height, result.Pixels, image.Channels);

            return result;
        }

        private static AvifImage MirrorLeftRight(AvifImage image)
        {
            var result = AvifImage.Create(image.Width, image.Height, image.PixelFormat);
            if (AvifImage.IsSixteenBit(image.PixelFormat))
                MirrorSamples(image.Pixels16, image.Width, image.Height, result.Pixels16, image.Channels);
            else
                MirrorSamples(image.Pixels, image.Width, image.Height, result.Pixels, image.Channels);

            return result;
        }

        private static AvifImage MirrorTopBottom(AvifImage image)
        {
            var result = AvifImage.Create(image.Width, image.Height, image.PixelFormat);
            var rowLength = image.Width * image.Channels;
            for (var row = 0; row < image.Height; row++)
            {
                var target = (image.Height - 1 - row) * rowLength;
                if (AvifImage.IsSixteenBit(image.PixelFormat))
                    Array.Copy(image.Pixels16, row * rowLength, result.Pixels16, target, rowLength);
                else
                    Array.Copy(image.Pixels, row * rowLength, result.Pixels, target, rowLength);
            }

            return result;
        }

        private static void CropSamples<T>(T[] source, int sourceWidth, T[] target, int x, int y, int width, int height, int channels)
        {
            for (var row = 0; row < height; row++)
                Array.Copy(source, (((y + row) * sourceWidth) + x) * channels, target, row * width * channels, width * channels);
        }

        private static void RotateSamples<T>(T[] source, int sourceWidth, int sourceHeight, T[] target, int channels)
        {
            var width = sourceHeight;
            for (var row = 0; row < sourceHeight; row++)
            {
                for (var column = 0; column < sourceWidth; column++)
                {
                    var from = ((row * sourceWidth) + column) * channels;
                    var to = (((sourceWidth - 1 - column) * width) + row) * channels;
                    for (var c = 0; c < channels; c++)
                        target[to + c] = source[from + c];
                }
            }
        }

        private static void MirrorSamples<T>(T[] source, int width, int height, T[] target, int channels)
        {
            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    var from = ((row * width) + column) * channels;
                    var to = ((row * width) + (width - 1 - column)) * channels;
                    for (var c = 0; c < channels; c++)
                        target[to + c] = source[from + c];
                }
            }
        }
    }
}

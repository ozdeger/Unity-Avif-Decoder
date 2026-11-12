#nullable enable
using System.Collections.Generic;
using System.Threading;
using System;

namespace AvifDecoding
{
    // The transformative properties of the primary item, in the order HEIF applies them: the clean aperture (clap), the
    // rotation (irot), then the mirroring (imir)
    internal static class AvifImageTransforms
    {
        public static AvifRgbImage Apply(AvifRgbImage image, AvifContainer container)
        {
            if (container.HasCleanAperture && TryGetCrop(container.Crop, image.Width, image.Height, out var x, out var y, out var width, out var height))
                image = Crop(image, x, y, width, height);

            for (var i = 0; i < container.Rotation; i++)
                image = RotateAnticlockwise(image);

            // imir 0 exchanges the top and bottom of the image, 1 its left and right (HEIF 6.5.12, and libavif)
            if (container.MirrorAxis == 0)
                image = MirrorTopBottom(image);
            else if (container.MirrorAxis == 1)
                image = MirrorLeftRight(image);

            return image;
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

        private static AvifRgbImage Crop(AvifRgbImage image, int x, int y, int width, int height)
        {
            var pixels = new byte[width * height * 3];
            for (var row = 0; row < height; row++)
                Buffer.BlockCopy(image.Pixels, (((y + row) * image.Width) + x) * 3, pixels, row * width * 3, width * 3);

            return new AvifRgbImage(width, height, pixels);
        }

        private static AvifRgbImage RotateAnticlockwise(AvifRgbImage image)
        {
            var width = image.Height;
            var height = image.Width;
            var pixels = new byte[image.Pixels.Length];
            for (var row = 0; row < image.Height; row++)
            {
                for (var column = 0; column < image.Width; column++)
                {
                    var source = ((row * image.Width) + column) * 3;
                    var target = ((((image.Width - 1 - column) * width) + row)) * 3;
                    pixels[target] = image.Pixels[source];
                    pixels[target + 1] = image.Pixels[source + 1];
                    pixels[target + 2] = image.Pixels[source + 2];
                }
            }

            return new AvifRgbImage(width, height, pixels);
        }

        private static AvifRgbImage MirrorLeftRight(AvifRgbImage image)
        {
            var pixels = new byte[image.Pixels.Length];
            for (var row = 0; row < image.Height; row++)
            {
                for (var column = 0; column < image.Width; column++)
                {
                    var source = ((row * image.Width) + column) * 3;
                    var target = ((row * image.Width) + (image.Width - 1 - column)) * 3;
                    pixels[target] = image.Pixels[source];
                    pixels[target + 1] = image.Pixels[source + 1];
                    pixels[target + 2] = image.Pixels[source + 2];
                }
            }

            return new AvifRgbImage(image.Width, image.Height, pixels);
        }

        private static AvifRgbImage MirrorTopBottom(AvifRgbImage image)
        {
            var pixels = new byte[image.Pixels.Length];
            var rowLength = image.Width * 3;
            for (var row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Pixels, row * rowLength, pixels, (image.Height - 1 - row) * rowLength, rowLength);

            return new AvifRgbImage(image.Width, image.Height, pixels);
        }
    }
}

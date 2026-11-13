using System;
using System.IO;
using System.Linq;

namespace AvifDecoding.Tests
{
    // Every test image that has a PNG next to it: the PNG is what libavif with dav1d decodes (Images/make.py)
    public static class ReferenceTests
    {
        public static int Run(string images)
        {
            var failures = 0;
            foreach (var file in Directory.GetFiles(images, "*.avif").OrderBy(f => f, StringComparer.Ordinal))
            {
                var png = Path.ChangeExtension(file, ".png");
                if (!File.Exists(png))
                    continue;

                // Images with alpha are compared as RGBA by FeatureTests.Rgba: this check reads the colour as it is coded
                if (Png.ReadRgba(png).HasAlpha)
                    continue;

                var name = Path.GetFileName(file);
                var (width, height, expected) = Png.ReadRgb(png);
                AvifRgbImage image;
                try
                {
                    image = AvifImageDecoder.Decode(File.ReadAllBytes(file));
                }
                catch (ImageDecodeException e)
                {
                    failures++;
                    Console.WriteLine($"   FAIL {name}: {e.GetType().Name}: {e.Message}");
                    continue;
                }

                if (image.Width != width || image.Height != height)
                {
                    failures++;
                    Console.WriteLine($"   FAIL {name}: {image.Width}x{image.Height}, expected {width}x{height}");
                    continue;
                }

                int maxDiff = 0, exact = 0;
                for (var i = 0; i < expected.Length; i++)
                {
                    var diff = Math.Abs(image.Pixels[i] - expected[i]);
                    maxDiff = Math.Max(maxDiff, diff);
                    if (diff == 0)
                        exact++;
                }

                var ok = maxDiff <= 1;
                if (!ok)
                    failures++;
                Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {name,-24} {width,4}x{height,-4} max diff {maxDiff,3}   exact {100.0 * exact / expected.Length,6:F2}%");
            }

            return failures;
        }
    }
}

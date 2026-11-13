using System;
using System.IO;
using System.Linq;

namespace AvifDecoding.Tests
{
    // Every file in Images/unsupported uses a feature that the decoder does not decode, and must be refused with an
    // UnsupportedImageFeatureException rather than any other exception
    public static class UnsupportedTests
    {
        public static int Run(string images)
        {
            var failures = 0;
            foreach (var file in Directory.GetFiles(Path.Combine(images, "unsupported"), "*.avif").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                try
                {
                    var image = AvifImageDecoder.Decode(File.ReadAllBytes(file));
                    failures++;
                    Console.WriteLine($"   FAIL {name}: decoded to {image.Width}x{image.Height}");
                }
                catch (UnsupportedImageFeatureException e)
                {
                    Console.WriteLine($"   ok   {name}: {e.Message}");
                }
                catch (Exception e)
                {
                    failures++;
                    Console.WriteLine($"   FAIL {name}: {e.GetType().Name}: {e.Message}");
                }
            }

            return failures;
        }
    }
}

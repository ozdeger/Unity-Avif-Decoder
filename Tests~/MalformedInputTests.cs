using System;
using System.Diagnostics;
using System.IO;

namespace AvifDecoding.Tests
{
    // Truncated and corrupted copies of the test images: each must decode or throw an ImageDecodeException, and quickly.
    // Any other exception, or a decode slower than two seconds, fails.
    public static class MalformedInputTests
    {
        private const int VariantsPerFile = 120;

        public static int Run(string images)
        {
            var failures = 0;
            failures += Expect<ImageResourceLimitException>("pixel limit", () => AvifImageDecoder.Decode(File.ReadAllBytes(Path.Combine(images, "big2048.avif")), maxPixels: 1000 * 1000));
            failures += Expect<ImageDecodeException>("empty input", () => AvifImageDecoder.Decode(Array.Empty<byte>()));
            failures += Expect<ImageDecodeException>("a PNG", () => AvifImageDecoder.Decode(File.ReadAllBytes(Path.Combine(images, "tiny17x9.png"))));

            var rng = new Random(42);
            int decoded = 0, rejected = 0;
            foreach (var name in new[] { "photo320_q40", "screen_ibc", "d10_444", "tiles", "grain7", "superres", "clap_irot1_imir0", "tiny17x9_422" })
            {
                var source = File.ReadAllBytes(Path.Combine(images, name + ".avif"));
                for (var k = 0; k < VariantsPerFile; k++)
                {
                    var bytes = Mutate(source, k, rng);
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        AvifImageDecoder.Decode(bytes);
                        decoded++;
                    }
                    catch (ImageDecodeException)
                    {
                        rejected++;
                    }
                    catch (Exception e)
                    {
                        failures++;
                        Console.WriteLine($"   FAIL {name} variant {k}: {e.GetType().Name}: {e.Message}");
                    }

                    if (stopwatch.ElapsedMilliseconds > 2000)
                    {
                        failures++;
                        Console.WriteLine($"   FAIL {name} variant {k}: took {stopwatch.ElapsedMilliseconds} ms");
                    }
                }
            }

            Console.WriteLine($"   corrupted variants: {decoded} decoded, {rejected} rejected with an ImageDecodeException");
            return failures;
        }

        private static byte[] Mutate(byte[] source, int k, Random rng)
        {
            var bytes = (byte[])source.Clone();
            switch (k % 3)
            {
                case 0:
                    Array.Resize(ref bytes, rng.Next(1, bytes.Length));
                    break;
                case 1:
                    for (var flips = 1 + rng.Next(8); flips > 0; flips--)
                        bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
                    break;
                default:
                    var offset = rng.Next(bytes.Length);
                    var noise = new byte[Math.Min(rng.Next(1, 64), bytes.Length - offset)];
                    rng.NextBytes(noise);
                    Buffer.BlockCopy(noise, 0, bytes, offset, noise.Length);
                    break;
            }

            return bytes;
        }

        private static int Expect<T>(string what, Action action) where T : Exception
        {
            try
            {
                action();
                Console.WriteLine($"   FAIL {what}: decoded, expected {typeof(T).Name}");
                return 1;
            }
            catch (T e)
            {
                Console.WriteLine($"   ok   {what}: {e.GetType().Name}");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"   FAIL {what}: {e.GetType().Name}, expected {typeof(T).Name}");
                return 1;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AvifDecoding.Tests
{
    // The SHA-256 of the size and pixels of every test image, recorded in Images/expected-hashes.txt. Any change to the
    // output shows here; when a change is intended and the reference check still passes, record the new hashes with
    // --update-hashes.
    public static class HashTests
    {
        public static int Run(string images, bool update)
        {
            var path = Path.Combine(images, "expected-hashes.txt");
            var expected = File.Exists(path)
                ? File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1])
                : new Dictionary<string, string>();

            var actual = new List<string>();
            var failures = 0;
            foreach (var file in Directory.GetFiles(images, "*.avif").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var hash = Hash(File.ReadAllBytes(file));
                actual.Add(name + "\t" + hash);
                if (!update && (!expected.TryGetValue(name, out var recorded) || recorded != hash))
                {
                    failures++;
                    Console.WriteLine($"   FAIL {name}: {hash}, recorded {recorded ?? "nothing"}");
                }
            }

            if (update)
            {
                File.WriteAllLines(path, actual);
                Console.WriteLine($"   recorded {actual.Count} hashes in {path}");
            }
            else
            {
                Console.WriteLine($"   {actual.Count - failures} of {actual.Count} match");
            }

            return failures;
        }

        private static string Hash(byte[] data)
        {
            AvifRgbImage image;
            try
            {
                image = AvifImageDecoder.Decode(data);
            }
            catch (ImageDecodeException e)
            {
                return e.GetType().Name;
            }

            using var sha = SHA256.Create();
            var header = new byte[8];
            BitConverter.GetBytes(image.Width).CopyTo(header, 0);
            BitConverter.GetBytes(image.Height).CopyTo(header, 4);
            sha.TransformBlock(header, 0, 8, null, 0);
            sha.TransformFinalBlock(image.Pixels, 0, image.Pixels.Length);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }
}

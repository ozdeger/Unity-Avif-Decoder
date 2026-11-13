using System;
using System.IO;
using System.Linq;

namespace AvifDecoding.Tests
{
    // Runs every check and returns 1 when one fails. Usage: dotnet run -c Release [-- --update-hashes]
    public static class Program
    {
        public static int Main(string[] args)
        {
            var images = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Images");
            if (!Directory.Exists(images))
                images = Path.Combine(Directory.GetCurrentDirectory(), "Images");

            var failures = 0;
            failures += Section("Transform kernels against the reference transform", () => KernelTests.Run(trials: 400));
            failures += Section("Pixels against libavif + dav1d (at most 1 apart per channel)", () => ReferenceTests.Run(images));
            failures += Section("Pixels against the recorded hashes", () => HashTests.Run(images, args.Contains("--update-hashes")));
            failures += Section("Malformed and oversized input", () => MalformedInputTests.Run(images));
            failures += Section("Unsupported features", () => UnsupportedTests.Run(images));
            failures += Section("Decodes on several threads at once", () => ParallelTests.Run(images));

            Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static int Section(string title, Func<int> run)
        {
            Console.WriteLine($"== {title}");
            var failures = run();
            Console.WriteLine(failures == 0 ? "   ok" : $"   {failures} failed");
            Console.WriteLine();
            return failures;
        }
    }
}

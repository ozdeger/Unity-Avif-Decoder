using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AvifDecoding.Tests
{
    // The decoder keeps no shared state between calls: decodes running on several threads at once give the same pixels as
    // one at a time
    public static class ParallelTests
    {
        public static int Run(string images)
        {
            var files = Directory.GetFiles(images, "*.avif").Where(f => !Path.GetFileName(f).StartsWith("big", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var inputs = files.Select(File.ReadAllBytes).ToArray();
            var expected = inputs.Select(bytes => AvifImageDecoder.Decode(bytes).Pixels).ToArray();
            var failures = 0;
            Parallel.For(0, inputs.Length * 8, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, i =>
            {
                var index = i % inputs.Length;
                if (!AvifImageDecoder.Decode(inputs[index]).Pixels.AsSpan().SequenceEqual(expected[index]))
                {
                    System.Threading.Interlocked.Increment(ref failures);
                    Console.WriteLine($"   FAIL {Path.GetFileName(files[index])}: different pixels when decoded in parallel");
                }
            });

            Console.WriteLine($"   {inputs.Length * 8} decodes on {Math.Max(4, Environment.ProcessorCount)} threads");
            return failures;
        }
    }
}

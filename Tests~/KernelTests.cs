using System;

namespace AvifDecoding.Tests
{
    // Compares the 8-bit kernels with the reference transform on random and extreme coefficients, for every size and type
    public static class KernelTests
    {
        private static bool IsValid(Av1InverseTransform.Kind kind, int log2) => kind switch
        {
            Av1InverseTransform.Kind.Dct => true,
            Av1InverseTransform.Kind.Adst => log2 <= 4,
            _ => log2 <= 5,
        };

        public static int Run(int trials)
        {
            var rng = new Random(1234);
            long cases = 0, bad = 0, combos = 0;
            for (var txSz = 0; txSz < 19; txSz++)
            {
                for (var txType = 0; txType < 16; txType++)
                {
                    int log2W = Av1Tables.TxWidthLog2[txSz], log2H = Av1Tables.TxHeightLog2[txSz];
                    if (!IsValid(Av1InverseTransform.GetRowKind(txType), log2W) || !IsValid(Av1InverseTransform.GetColumnKind(txType), log2H))
                        continue;

                    combos++;
                    int w = 1 << log2W, h = 1 << log2H, tw = Math.Min(32, w), th = Math.Min(32, h);
                    var dequant = new int[tw * th];
                    var expected = new int[w * h];
                    var actual = new int[w * h];
                    for (var trial = 0; trial < trials; trial++)
                    {
                        Array.Clear(dequant, 0, dequant.Length);
                        int rows = 1 + rng.Next(th), columns = 1 + rng.Next(tw);
                        if (trial % 5 == 0)
                        {
                            rows = th;
                            columns = tw;
                        }
                        else if (trial % 5 == 1)
                        {
                            rows = 1 + rng.Next(Math.Min(4, th));
                            columns = 1 + rng.Next(Math.Min(4, tw));
                        }

                        var mode = trial % 4;
                        int lastRow = -1, lastColumn = -1;
                        for (var i = 0; i < rows; i++)
                        {
                            for (var j = 0; j < columns; j++)
                            {
                                var v = mode switch
                                {
                                    0 => rng.Next(-32768, 32768),
                                    1 => rng.Next(2) == 0 ? -32768 : 32767,
                                    2 => rng.Next(-64, 65),
                                    _ => rng.Next(6) == 0 ? rng.Next(-32768, 32768) : 0,
                                };
                                dequant[(i * tw) + j] = v;
                                if (v != 0)
                                {
                                    lastRow = Math.Max(lastRow, i);
                                    lastColumn = Math.Max(lastColumn, j);
                                }
                            }
                        }

                        // The bounds a caller gives: the exact extent of the coefficients, or a looser one
                        if ((trial & 8) == 0)
                        {
                            rows = lastRow + 1;
                            columns = lastColumn + 1;
                        }

                        for (var i = 0; i < expected.Length; i++)
                        {
                            expected[i] = rng.Next();
                            actual[i] = rng.Next();
                        }

                        Av1InverseTransform.TransformScalar(dequant, expected, txSz, txType, lossless: false, bitDepth: 8);
                        Av1InverseTransform.Transform(dequant, actual, txSz, txType, lossless: false, bitDepth: 8, rows, columns);
                        cases++;
                        if (!expected.AsSpan().SequenceEqual(actual))
                        {
                            if (bad++ < 12)
                                Console.WriteLine($"   MISMATCH {w}x{h} type {txType} rows {rows} columns {columns} mode {mode}");
                        }
                    }
                }
            }

            Console.WriteLine($"   {combos} size/type combinations, {cases} cases, {bad} mismatches");
            return bad == 0 ? 0 : 1;
        }
    }
}

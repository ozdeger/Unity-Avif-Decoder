#!/usr/bin/env python3
"""Usage: python3 generate_transform_kernels.py [Av1InverseTransform.cs] [output.g.cs] [namespace]

Generates straight-line 1-D inverse DCT/ADST kernels for 8-bit streams from the butterfly networks of the AV1
specification (section 7.13.2), as the reference code in Av1InverseTransform.cs runs them.

For 8-bit samples every value of a transform is a 16-bit input (the dequantization range and the row-to-column clamp) and the
Hadamard steps clamp to 16 bits, so every product and sum fits 32 bits: the generator tracks the interval of every value and
fails if one could overflow. Inputs known to be zero (the K first inputs can be non-zero) are folded away."""
import re, sys

import os
HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "Runtime", "Av1", "Av1InverseTransform.cs")
TARGET = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "..", "Runtime", "Av1", "Av1InverseTransform.Kernels.g.cs")
NAMESPACE = sys.argv[3] if len(sys.argv) > 3 else "AvifDecoding"
src = open(SOURCE).read()
COS = [int(x) for x in re.search(r"s_Cos128Values = new short\[\] \{(.*?)\};", src, re.S).group(1).replace("\n", " ").split(",") if x.strip()]
BR6 = [int(x) for x in re.search(r"BitReverse6 =>\s*new byte\[\] \{(.*?)\};", src, re.S).group(1).replace("\n", " ").split(",") if x.strip()]
assert len(COS) == 256 and len(BR6) == 64
LO, HI = -32768, 32767
I32 = 2 ** 31


class W:
    __slots__ = ("name", "lo", "hi", "sat")

    def __init__(self, name, lo, hi):
        self.name, self.lo, self.hi, self.sat = name, lo, hi, None


class Gen:
    def __init__(self, N, K):
        self.N, self.K, self.lines, self.n, self.peak = N, K, [], 0, 0
        self.t = [W(f"i{j}", LO, HI) if j < K else None for j in range(N)]

    def var(self, expr, lo, hi):
        assert -I32 <= lo and hi < I32
        w = W(f"v{self.n}", lo, hi)
        self.n += 1
        self.lines.append(f"var {w.name} = {expr};")
        return w

    def rot(self, terms):
        terms = [(w, c) for w, c in terms if w is not None and c != 0]
        if not terms:
            return None
        lo = hi = 0
        parts = []
        for w, c in terms:
            a, b = sorted((w.lo * c, w.hi * c))
            lo += a
            hi += b
            parts.append(f"({w.name} * {c})")
        assert -I32 <= lo and hi + 2048 < I32, "a rotation overflows 32 bits"
        self.peak = max(self.peak, -lo, hi + 2048)
        return self.var("(" + " + ".join(parts) + " + 2048) >> 12", (lo + 2048) >> 12, (hi + 2048) >> 12)

    def B(self, a, b, angle, flip):
        cos, sin = COS[angle & 255], COS[(angle - 64) & 255]
        ta, tb = self.t[a], self.t[b]
        x = self.rot([(ta, cos), (tb, -sin)])
        y = self.rot([(ta, sin), (tb, cos)])
        if flip:
            self.t[b], self.t[a] = x, y
        else:
            self.t[a], self.t[b] = x, y

    def clamp(self, w):
        if w.lo >= LO and w.hi <= HI:
            return w
        if w.sat is None:
            w.sat = self.var(f"Sat16({w.name})", max(w.lo, LO), min(w.hi, HI))
        return w.sat

    def clamped(self, expr, lo, hi):
        if lo >= LO and hi <= HI:
            return self.var(expr, lo, hi)
        return self.var(f"Sat16({expr})", max(lo, LO), min(hi, HI))

    def H(self, a, b, flip):
        if flip:
            a, b = b, a
        x, y = self.t[a], self.t[b]
        if x is None and y is None:
            return
        if y is None:
            self.t[a] = self.t[b] = self.clamp(x)
        elif x is None:
            self.t[a] = self.clamp(y)
            self.t[b] = self.clamped(f"-{y.name}", -y.hi, -y.lo)
        else:
            self.t[a] = self.clamped(f"{x.name} + {y.name}", x.lo + y.lo, x.hi + y.hi)
            self.t[b] = self.clamped(f"{x.name} - {y.name}", x.lo - y.hi, x.hi - y.lo)


def br(bits, x):
    return BR6[x] >> (6 - bits)


def dct(g, n):
    t = g.t
    for i in range(1 << n):
        j = br(n, i)
        if j > i:
            t[i], t[j] = t[j], t[i]
    if n == 6:
        for i in range(16): g.B(32 + i, 63 - i, 63 - (4 * br(4, i)), False)
    if n >= 5:
        for i in range(8): g.B(16 + i, 31 - i, 6 + (br(3, 7 - i) << 3), False)
    if n == 6:
        for i in range(16): g.H(32 + (i * 2), 33 + (i * 2), (i & 1) != 0)
    if n >= 4:
        for i in range(4): g.B(8 + i, 15 - i, 12 + (br(2, 3 - i) << 4), False)
    if n >= 5:
        for i in range(8): g.H(16 + (2 * i), 17 + (2 * i), (i & 1) != 0)
    if n == 6:
        for i in range(4):
            for j in range(2): g.B(62 - (i * 4) - j, 33 + (i * 4) + j, 60 - (16 * br(2, i)) + (64 * j), True)
    if n >= 3:
        for i in range(2): g.B(4 + i, 7 - i, 56 - (32 * i), False)
    if n >= 4:
        for i in range(4): g.H(8 + (2 * i), 9 + (2 * i), (i & 1) != 0)
    if n >= 5:
        for i in range(2):
            for j in range(2): g.B(30 - (4 * i) - j, 17 + (4 * i) + j, 24 + (j << 6) + ((1 - i) << 5), True)
    if n == 6:
        for i in range(8):
            for j in range(2): g.H(32 + (i * 4) + j, 35 + (i * 4) - j, (i & 1) != 0)
    for i in range(2): g.B(2 * i, (2 * i) + 1, 32 + (16 * i), i == 0)
    if n >= 3:
        for i in range(2): g.H(4 + (2 * i), 5 + (2 * i), i != 0)
    if n >= 4:
        for i in range(2): g.B(14 - i, 9 + i, 48 + (64 * i), True)
    if n >= 5:
        for i in range(4):
            for j in range(2): g.H(16 + (4 * i) + j, 19 + (4 * i) - j, (i & 1) != 0)
    if n == 6:
        for i in range(2):
            for j in range(4): g.B(61 - (i * 8) - j, 34 + (i * 8) + j, 56 - (i * 32) + ((j >> 1) * 64), True)
    for i in range(2): g.H(i, 3 - i, False)
    if n >= 3:
        g.B(6, 5, 32, True)
    if n >= 4:
        for i in range(2):
            for j in range(2): g.H(8 + (4 * i) + j, 11 + (4 * i) - j, i != 0)
    if n >= 5:
        for i in range(4): g.B(29 - i, 18 + i, 48 + ((i >> 1) * 64), True)
    if n == 6:
        for i in range(4):
            for j in range(4): g.H(32 + (8 * i) + j, 39 + (8 * i) - j, (i & 1) != 0)
    if n >= 3:
        for i in range(4): g.H(i, 7 - i, False)
    if n >= 4:
        for i in range(2): g.B(13 - i, 10 + i, 32, True)
    if n >= 5:
        for i in range(2):
            for j in range(4): g.H(16 + (i * 8) + j, 23 + (i * 8) - j, i != 0)
    if n == 6:
        for i in range(8): g.B(59 - i, 36 + i, 48 if i < 4 else 112, True)
    if n >= 4:
        for i in range(8): g.H(i, 15 - i, False)
    if n >= 5:
        for i in range(4): g.B(27 - i, 20 + i, 32, True)
    if n == 6:
        for i in range(8):
            g.H(32 + i, 47 - i, False)
            g.H(48 + i, 63 - i, True)
    if n >= 5:
        for i in range(16): g.H(i, 31 - i, False)
    if n == 6:
        for i in range(8): g.B(55 - i, 40 + i, 32, True)
        for i in range(32): g.H(i, 63 - i, False)


def adst_in(g, n):
    n0 = 1 << n
    old = list(g.t)
    for i in range(n0):
        g.t[i] = old[i - 1 if i & 1 else n0 - i - 1]


def adst_out(g, n):
    n0 = 1 << n
    old = list(g.t)
    for i in range(n0):
        a = (i >> 3) & 1
        b = ((i >> 2) & 1) ^ ((i >> 3) & 1)
        c = ((i >> 1) & 1) ^ ((i >> 2) & 1)
        d = (i & 1) ^ ((i >> 1) & 1)
        w = old[((d << 3) | (c << 2) | (b << 1) | a) >> (4 - n)]
        g.t[i] = g.var(f"-{w.name}", -w.hi, -w.lo) if (i & 1) and w is not None else w


def adst(g, n):
    adst_in(g, n)
    if n == 3:
        for i in range(4): g.B(2 * i, (2 * i) + 1, 60 - (16 * i), True)
        for i in range(4): g.H(i, 4 + i, False)
        for i in range(2): g.B(4 + (3 * i), 5 + i, 48 - (32 * i), True)
        for i in range(2):
            for j in range(2): g.H((4 * j) + i, 2 + (4 * j) + i, False)
        for i in range(2): g.B(2 + (4 * i), 3 + (4 * i), 32, True)
    else:
        for i in range(8): g.B(2 * i, (2 * i) + 1, 62 - (8 * i), True)
        for i in range(8): g.H(i, 8 + i, False)
        for i in range(2):
            g.B(8 + (2 * i), 9 + (2 * i), 56 - (32 * i), True)
            g.B(13 + (2 * i), 12 + (2 * i), 8 + (32 * i), True)
        for i in range(4):
            for j in range(2): g.H((8 * j) + i, 4 + (8 * j) + i, False)
        for i in range(2):
            for j in range(2): g.B(4 + (8 * j) + (3 * i), 5 + (8 * j) + i, 48 - (32 * i), True)
        for i in range(2):
            for j in range(4): g.H((4 * j) + i, 2 + (4 * j) + i, False)
        for i in range(4): g.B(2 + (4 * i), 3 + (4 * i), 32, True)
    adst_out(g, n)


def index(i):
    return "o" if i == 0 else "o + s" if i == 1 else f"o + ({i} * s)"


def kernel(kind, n, K):
    N = 1 << n
    g = Gen(N, K)
    (dct if kind == "Dct" else adst)(g, n)
    name = f"{kind}{N}" + ("" if K == N or (N == 64 and K == 32) else f"First{K}")
    out = [f"        // The inverse {kind.upper()} of {N} points" + ("" if K == N else f" whose inputs after the first {K} are zero"),
           f"        private static void {name}(Span<int> d, int o, int s)", "        {"]
    out += [f"            var i{j} = d[{index(j)}];" for j in range(K)]
    out += ["            " + l for l in g.lines]
    out += [f"            d[{index(j)}] = {g.t[j].name if g.t[j] is not None else '0'};" for j in range(N)]
    out.append("        }")
    return name, out, len(g.lines), g.peak


# The variants for inputs that are zero after the first 4 or 8 are kept where they save a third of the statements or more
VARIANTS = {("Dct", 2): [4], ("Dct", 3): [8], ("Dct", 4): [4, 16], ("Dct", 5): [4, 8, 32], ("Dct", 6): [8, 32],
            ("Adst", 3): [8], ("Adst", 4): [16]}
body, table, peak = [], {}, 0
for (kind, n), ks in VARIANTS.items():
    for K in ks:
        name, lines, ops, p = kernel(kind, n, K)
        peak = max(peak, p)
        body += lines + [""]
        table.setdefault((kind, n), []).append((K, name))
        print(f"{name:14} {ops:5} statements", file=sys.stderr)
print(f"largest magnitude before a shift: {peak} (limit {I32 - 1})", file=sys.stderr)

out = ["#nullable enable", "// <auto-generated/>",
       "// Generated by Tools~/generate_transform_kernels.py from the butterfly networks of Av1InverseTransform.cs: straight-line kernels",
       "// for 8-bit streams, where every value fits 32 bits (the generator tracks the range of each value and fails otherwise).",
       "using System;", "using System.Runtime.CompilerServices;", "", f"namespace {NAMESPACE}", "{",
       "    internal static partial class Av1InverseTransform", "    {",
       "        [MethodImpl(MethodImplOptions.AggressiveInlining)]",
       "        private static int Sat16(int value) => value < -32768 ? -32768 : value > 32767 ? 32767 : value;", ""]
for kind in ("Dct", "Adst"):
    out += [f"        // The inverse {kind.upper()} of 2^n points in place on d[o], d[o + s], ..., whose inputs after the first count are zero",
            f"        private static void {kind}Fast(int n, int count, Span<int> d, int o, int s)", "        {", "            switch (n)", "            {"]
    for (k2, n), variants in table.items():
        if k2 != kind:
            continue
        out.append(f"                case {n}:")
        for i, (K, name) in enumerate(variants):
            last = i == len(variants) - 1
            call = f"{name}(d, o, s);"
            if last:
                out += [f"                    {call}" if len(variants) == 1 else f"                    else\n                        {call}"]
            else:
                out += [f"                    {'if' if i == 0 else 'else if'} (count <= {K})", f"                        {call}"]
        out += ["", "                    break;"]
    out += ["                default:", '                    throw new InvalidOperationException("The transform size has no kernel.");', "            }", "        }", ""]
out += body
out = out[:-1] + ["    }", "}", ""]
open(TARGET, "w").write("\n".join(out))

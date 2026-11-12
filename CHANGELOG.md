# Changelog

## 1.0.0 - 2026-11-12

First release.

- The AV1 decoder of [Meziantou.Framework.Imaging](https://github.com/meziantou/Meziantou.Framework) at commit `d1409b5f`, ported to C# 9 and .NET Standard 2.1: decode only, key frames only, scalar code, no unsafe code.
- An AVIF container reader and an integer RGB conversion written for this package.
- Generated 32-bit inverse transform kernels for 8-bit streams.

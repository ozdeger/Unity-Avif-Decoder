# Changelog

## 1.1.0 - 2026-11-13

Everything AVIF holds is decoded, with the same pixels on every platform. `AvifImageDecoder.Decode` gives the same pixels as in 1.0.0.

- **Output:** `DecodeImage` with `AvifDecodeOptions`: 8 or 16-bit RGB or RGBA (`Rgb24`, `Rgba32`, `Rgb48`, `Rgba64`), straight or premultiplied alpha, the crop, rotation and mirroring optional, and a pixel limit.
- **Alpha:** alpha auxiliary images, also as grids or one per grid tile, premultiplied (`prem`) or straight, and limited-range alpha expanded as libavif does.
- **Image sequences:** `DecodeFrames` and `OpenSequence`, with inter frames, alpha tracks, frame times, the repetition count of the edit list, and movie fragments.
- **Layered images:** layered and progressive images (`a1lx`, `a1op`, `lsel`), and `DecodeLayers` for each layer.
- **Derived images:** grids, joined before the conversion to RGB as libavif does; overlays (`iovl`); identity derivations (`iden`); grids of derived images; sample transforms (`sato`), and their `altr` alternative with `UseSampleTransforms`.
- **Matrix coefficients:** YCgCo (8), YCgCo-Re and YCgCo-Ro (16, 17), chromaticity-derived non-constant luminance (12), and through the transfer characteristics BT.2020 constant luminance (10), SMPTE ST 2085 (11), chromaticity-derived constant luminance (13) and ICtCp (14); identity in limited range.
- **Gain maps (ISO 21496-1):** `DecodeGainMap`, the metadata in `Identify`, and the rendition for a display headroom with `GainMapHeadroom`.
- **Metadata:** `Identify` reads the size, depth, chroma format, alpha, colour description and ICC profile, Exif, XMP, content light level, mastering display, pixel aspect ratio, transformative properties, sequence timing, gain map, thumbnails and auxiliary images without decoding.
- **More items:** `DecodeThumbnail` and `DecodeAuxiliaryImage` (depth maps); item data built from other items (`iloc` construction method 2); an essential property that the decoder does not know makes the image unsupported, as HEIF requires.
- **Limits:** the pixel limit covers every canvas; the images one output is built from are budgeted together, so nested derived images cannot make the decoder work without end; `MaxTotalPixels` bounds `DecodeFrames`.
- The AV1 decoder decodes every frame type again (inter frames, reference scaling, warped and global motion, compound prediction, OBMC, show-existing frames), ported back from the upstream decoder.
- The operating point that an `a1op` property selects is decoded.

## 1.0.0 - 2026-11-12

First release.

- The AV1 decoder of [Meziantou.Framework.Imaging](https://github.com/meziantou/Meziantou.Framework) at commit `d1409b5f`, ported to C# 9 and .NET Standard 2.1: decode only, key frames only, scalar code, no unsafe code.
- An AVIF container reader and an integer RGB conversion written for this package.
- Generated 32-bit inverse transform kernels for 8-bit streams.

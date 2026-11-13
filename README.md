# AVIF Decoder for Unity

Decodes AVIF images to 8-bit RGB in plain C#. No native plugins, no `unsafe` code, no platform APIs: the decoder uses integer
arithmetic only, so it gives the same pixels on every platform, OS version and scripting backend.

Unity's `Texture2D.LoadImage` reads only PNG and JPEG. The operating systems decode AVIF only from Android 12 and iOS 16, and
the .NET AVIF libraries found when this was written (November 2026) either wrap native code (libheif, FFmpeg) or need .NET 10.
This package is a port of the AV1 decoder in [Meziantou.Framework.Imaging](https://github.com/meziantou/Meziantou.Framework)
to C# 9 and .NET Standard 2.1, so it runs in Unity 2021.2 and later (tested in Unity 6000.3) and in any .NET Standard 2.1
runtime.

## Install

In Unity, open **Window › Package Manager**, choose **+ › Install package from git URL…** and enter:

```
https://github.com/ozdeger/Unity-Avif-Decoder.git
```

Or add the line to `Packages/manifest.json`:

```json
"com.ozdeger.avif-decoder": "https://github.com/ozdeger/Unity-Avif-Decoder.git#1.0.1"
```

Outside Unity, build `Dotnet~/AvifDecoder.csproj` (.NET Standard 2.1) or add the `Runtime` folder to your project.

## Use

```csharp
using AvifDecoding;

AvifRgbImage image = AvifImageDecoder.Decode(bytes);   // throws ImageDecodeException for anything it cannot decode
// image.Width, image.Height, image.Pixels: RGB, 3 bytes per pixel, top row first
```

`AvifImageDecoder.IsAvif(bytes)` checks the file signature without decoding. `Decode` takes an optional `maxPixels` limit
(4096 × 4096 by default) and refuses larger images before allocating them.

Every failure is an `ImageDecodeException`, whatever the input holds: `InvalidImageContentException` for a broken file,
`UnsupportedImageFeatureException` for a feature listed below, `ImageResourceLimitException` for an image over the limit. The
decoder keeps no shared state, so it can run on any thread, several at a time.

### Into a texture

Unity textures start at the bottom row, so flip the rows first:

```csharp
using System;
using System.Threading.Tasks;
using AvifDecoding;
using UnityEngine;

public static class AvifTexture
{
    public static async Task<Texture2D> LoadAsync(byte[] avif)
    {
        // Decoding takes tens of milliseconds: keep it off the main thread
        var (width, height, pixels) = await Task.Run(() =>
        {
            AvifRgbImage image = AvifImageDecoder.Decode(avif);
            var flipped = new byte[image.Pixels.Length];
            var stride = image.Width * 3;
            for (var y = 0; y < image.Height; y++)
                Buffer.BlockCopy(image.Pixels, y * stride, flipped, (image.Height - 1 - y) * stride, stride);
            return (image.Width, image.Height, flipped);
        });

        var texture = new Texture2D(width, height, TextureFormat.RGB24, mipChain: false);
        texture.LoadRawTextureData(pixels);
        texture.Apply();
        return texture;
    }
}
```

To store the image in a format the rest of Unity reads, encode the flipped pixels with
`ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8_SRGB, (uint)width, (uint)height, 0, quality)`, which also
works off the main thread.

## What it decodes

- The primary image of an AVIF file, with its crop (`clap`), rotation (`irot`) and mirroring (`imir`) applied, at the
  operating point that `a1op` selects.
- 8, 10 and 12-bit images, in 4:2:0, 4:2:2, 4:4:4 and monochrome, lossy and lossless.
- Full and limited range, with the BT.601, BT.709, BT.2020 (non-constant luminance), FCC, SMPTE 240 and identity matrices.
- The AV1 coding tools of still images, among them filter intra, chroma from luma, palette, intra block copy, all transform
  sizes and types, quantizer matrices, delta quantizer, CDEF, loop restoration, super-resolution, film grain, tiles and
  128 × 128 superblocks.

Not supported, each with an `UnsupportedImageFeatureException`:

- Grid images (`grid` items, which some cameras use for large photos) and image sequences without a still primary image.
- The other matrices: YCgCo, BT.2020 constant luminance, ICtCp and the chromaticity-derived ones.
- Layered and progressive images: their upper layers are inter frames. One whose `a1op` selects the base layer alone
  decodes.
- AV1 large-scale tile streams.

Read but ignored:

- **Alpha:** the output is RGB; an alpha auxiliary image is not decoded.
- **Colour management:** ICC profiles, colour primaries and transfer characteristics are ignored, and 10 and 12-bit samples
  are scaled to 8 bits. HDR (PQ, HLG) images are not tone-mapped.

## Speed and size

Single-threaded, scalar code. Times for 30 procedural 720 × 720 4:2:0 photos at quality 30 to 70 on an Apple M5 Pro, best of
five runs each. The pictures are full of detail, so typical photos decode faster:

| Runtime | Median | Slowest |
|---|---|---|
| IL2CPP, macOS arm64 release player (Unity 6000.3) | 29 ms | 36 ms |
| .NET 10 | 50 ms | 81 ms |
| Mono 6.12, built with Unity's compiler flags | 85 ms | 118 ms |

A 320 × 320 image takes about a quarter of that. The assembly is about 420 KB.

The IL2CPP player gives the same pixels as .NET and Mono on every test image. It has not been run on iOS or Android devices
yet.

## How it is checked

`Tests~` holds a test program with no package dependencies. Run it with:

```
dotnet run --project "Tests~/AvifDecoder.Tests.csproj" -c Release
```

It checks:

- **Pixels:** 45 test images against libavif with dav1d. Every sample is within 1 and over 99.6% are exact in every image.
  The images cover the formats and tools listed above, odd sizes from 1 × 1, the crop, rotation and mirroring, and an
  operating point selected by `a1op`.
- **Hashes:** the decoded pixels of all 47 test images, recorded in `Tests~/Images/expected-hashes.txt`, so that any change
  to the output shows.
- **Transform kernels:** the generated 8-bit kernels against the reference transform for every size and type.
- **Malformed input:** 960 truncated and corrupted files, which must decode or throw an `ImageDecodeException` within two
  seconds.
- **Unsupported features:** a layered image must be refused with an `UnsupportedImageFeatureException`.
- **Threads:** decodes on many threads at once give the same pixels.

The test images are procedural, made by `Tests~/Images/make.py` with libavif's `avifenc` and `aomenc`, so they carry no
third-party content. The decoder was also checked against 1,460 real-world AVIF photos, which are not included: every one
decodes within 1 of dav1d.

It was then run on the 391 files of three public AVIF test sets, which are not included either:
[AOM's](https://github.com/AOMediaCodec/av1-avif/tree/main/testFiles), [Link-U's](https://github.com/link-u/avif-sample-images)
and [libavif's](https://github.com/AOMediaCodec/libavif/tree/main/tests/data). The 367 files it decodes match libavif within
1; the files that libavif itself rejects were compared with repaired copies. The other 24 (grids, layered images and the
unsupported matrices) are refused with an `UnsupportedImageFeatureException`.

`Tools~/generate_transform_kernels.py` regenerates `Runtime/Av1/Av1InverseTransform.Kernels.g.cs` and proves that no value
in the 8-bit kernels can overflow 32 bits.

## Credits and license

The AV1 decoder is ported from [Meziantou.Framework.Imaging](https://github.com/meziantou/Meziantou.Framework) by Gérald
Barré, commit `d1409b5f`, under the MIT license. It follows the
[AV1 Bitstream & Decoding Process Specification](https://aomediacodec.github.io/av1-spec/); AV1 is covered by the
royalty-free [AOMedia Patent License 1.0](https://aomedia.org/license/patent-license/).

MIT license; see [LICENSE.md](LICENSE.md).

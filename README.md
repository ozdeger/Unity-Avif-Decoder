# AVIF Decoder for Unity

Decodes AVIF images and image sequences in plain C#: still images, alpha, 8 to 16-bit samples, HDR, gain maps, animations,
layered images, grids and the other derived images, and their metadata. No native plugins, no `unsafe` code, no platform
APIs. The YCbCr to RGB conversion is integer arithmetic (the few matrices defined through a transfer function use floating
point), so the decoder gives the same pixels on every platform, OS version and scripting backend.

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
"com.ozdeger.avif-decoder": "https://github.com/ozdeger/Unity-Avif-Decoder.git#1.1.0"
```

Outside Unity, build `Dotnet~/AvifDecoder.csproj` (.NET Standard 2.1) or add the `Runtime` folder to your project.

## Use

```csharp
using AvifDecoding;

// RGBA, 8 bits, straight alpha, with the crop, rotation and mirroring of the file applied
AvifImage image = AvifImageDecoder.DecodeImage(bytes);
// image.Width, image.Height, image.Pixels: 4 bytes per pixel, top row first

// 16-bit samples for 10 and 12-bit and HDR images: image.Pixels16
AvifImage deep = AvifImageDecoder.DecodeImage(bytes, new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgba64 });

// Every frame of an animation, with its time and duration
AvifAnimation animation = AvifImageDecoder.DecodeFrames(bytes);
foreach (AvifFrame frame in animation.Frames) { /* frame.Image, frame.Duration, frame.Timescale */ }

// What the file holds, without decoding: size, depth, alpha, colour description, ICC, Exif, XMP, HDR metadata, gain map
AvifImageInfo info = AvifImageDecoder.Identify(bytes);
```

| Call | What it gives |
|---|---|
| `DecodeImage(bytes, options)` | The still image, or the first frame of a sequence without one |
| `DecodeFrames(bytes, options)` | Every frame of the image sequence, with times, durations and the repetition count |
| `OpenSequence(bytes, options)` | A decoder that gives the frames one at a time, holding one in memory |
| `DecodeLayers(bytes, options)` | Each layer of a layered (progressive) image, coarsest first |
| `Identify(bytes)` | `AvifImageInfo`: sizes, depth, chroma format, alpha, `AvifColorInfo` (CICP code points and ICC profile), Exif, XMP, content light level, mastering display, pixel aspect ratio, transformative properties, sequence timing, gain map metadata, thumbnails, auxiliary images |
| `DecodeGainMap(bytes, options)` | The gain map image |
| `DecodeThumbnail(bytes, options)` | The first thumbnail |
| `DecodeAuxiliaryImage(bytes, type, options)` | An auxiliary image such as a depth map |
| `Decode(bytes)` | 8-bit RGB without alpha: the call of 1.0.0, which gives the same pixels |

`AvifDecodeOptions`:

| Option | Default | What it does |
|---|---|---|
| `PixelFormat` | `Rgba32` | `Rgb24`, `Rgba32`, `Rgb48` or `Rgba64` |
| `AlphaMode` | `Straight` | `Straight`, `Premultiplied`, or `Ignore` (alpha not decoded). A format without alpha gets the image over black, as libavif does |
| `MaxPixels` | 4096 × 4096 | Larger images, canvases and frames throw an `ImageResourceLimitException` before they are allocated |
| `MaxFrames` | all | The most frames `DecodeFrames` decodes |
| `MaxTotalPixels` | 2^28 | The most pixels the frames of `DecodeFrames` hold together (about 1 GB of RGBA); `OpenSequence` has no such limit |
| `ApplyTransforms` | `true` | Applies the crop (`clap`), rotation (`irot`) and mirroring (`imir`) |
| `PreferSequence` | `false` | Decodes the sequence of a file that has both a still image and a sequence |
| `UseSampleTransforms` | `false` | Decodes the sample transform (`sato`) that an `altr` group offers instead of the still image, as `avifdec --sato` does |
| `GainMapHeadroom` | none | Applies the gain map for a display of this HDR headroom (log2 of its peak over SDR white; 0 for SDR) |
| `ToneMappedTransfer`, `ToneMappedPrimaries` | the base image's | The transfer characteristics and primaries of a gain-mapped rendition: 16 (PQ) or 18 (HLG) hold HDR |

Samples are given in the colour space of the file, as libavif gives them: the matrix coefficients and range are applied to
make RGB, and the colour primaries, transfer characteristics and ICC profile are labels in `Identify` for the renderer.

`AvifImageDecoder.IsAvif(bytes)` checks the file signature. Every failure is an `ImageDecodeException`, whatever the input
holds: `InvalidImageContentException` for a broken file, `UnsupportedImageFeatureException` for a feature listed below,
`ImageResourceLimitException` for an image over the limit. The decoder keeps no shared state, so it can run on any thread,
several at a time.

A file cannot make the decoder work or allocate without bound: besides `MaxPixels` for every image, canvas and frame, the
images that one output is built from (tiles, alpha, gain map, the inputs of derived images) may hold 16 times `MaxPixels`
together, an item that several derivations use is read once, and the items may not hold more data than twice the file.

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
            AvifImage image = AvifImageDecoder.DecodeImage(avif);
            var flipped = new byte[image.Pixels.Length];
            var stride = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
                Buffer.BlockCopy(image.Pixels, y * stride, flipped, (image.Height - 1 - y) * stride, stride);
            return (image.Width, image.Height, flipped);
        });

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
        texture.LoadRawTextureData(pixels);
        texture.Apply();
        return texture;
    }
}
```

`Rgba64` pixels load the same way into `TextureFormat.RGBA64` after `Buffer.BlockCopy` of `Pixels16` into bytes. To store the
image in a format the rest of Unity reads, encode the flipped pixels with `ImageConversion.EncodeArrayToPNG` or
`EncodeArrayToJPG`, which also work off the main thread.

## What it decodes

- **Images:** the primary item of the file, or the first frame of its image sequence; thumbnails and auxiliary images (depth
  maps) on request. Coded images (`av01`) in 8, 10 and 12 bits, 4:2:0, 4:2:2, 4:4:4 and monochrome, lossy and lossless,
  with every AV1 coding tool: intra and inter prediction, compound and masked prediction, OBMC, warped and global motion,
  reference scaling, intra block copy, palette, filter intra, chroma from luma, every transform, quantizer matrices, delta
  quantizer and loop filter, CDEF, loop restoration, super-resolution, film grain, tiles and 128 × 128 superblocks.
- **Derived images:** grids (`grid`), joined before the conversion to RGB as libavif does, with the tile rules of HEIF and
  MIAF that libavif checks; overlays (`iovl`), composited with source-over on straight alpha; identity derivations (`iden`);
  grids of derived images; sample transforms (`sato`, libavif's 16-bit images).
- **Layered images:** layers (`a1lx`), the operating point (`a1op`) and the layer to show (`lsel`); `DecodeLayers` gives
  every layer for progressive display.
- **Alpha:** alpha auxiliary images (`auxl` with an alpha `auxC`), also grids of them or one per grid tile; premultiplied
  colors (`prem`); limited-range alpha, which version 1.0.0 of AVIF allowed, expanded as libavif does.
- **Image sequences:** tracks of AV1 samples with their sample tables, alpha tracks, frame durations, sync samples, the
  repetition count of the edit list, the sequence header of `av1C`, and movie fragments (`moof`).
- **Colour:** full and limited range; the matrix coefficients identity (0), BT.709 (1), FCC (4), BT.601 (5, 6), SMPTE 240 (7),
  YCgCo (8), BT.2020 (9), chromaticity-derived (12, weights from the primaries), YCgCo-Re and YCgCo-Ro (16, 17); and
  through the transfer characteristics, BT.2020 constant luminance (10), SMPTE ST 2085 (11), chromaticity-derived constant
  luminance (13) and ICtCp (14), which libavif does not decode.
- **Gain maps (ISO 21496-1):** the gain map image and its metadata, and the rendition for a display headroom with libavif's
  formula, in linear light and in the primaries the metadata asks for.
- **Transformative properties:** the clean aperture (`clap`), rotation (`irot`) and mirroring (`imir`), in that order.
- **Metadata:** see `Identify` above.

Not supported, each with an `UnsupportedImageFeatureException`:

- Matrix coefficients 15 (IPT-C2) and the reserved ones, and the transfer characteristics that matrices 10, 13 and 14 need
  when the file does not give one.
- An image with an essential property that the decoder does not know (HEIF 9.3.1 forbids showing it), and item data in
  another file.
- AV1 large-scale tile streams, which carry no images of their own.

Colour management is left to the renderer: ICC profiles, colour primaries and transfer characteristics are reported, not
converted, and HDR samples are given as they are coded (PQ or HLG), except in a gain-map rendition.

## Speed and size

Single-threaded, scalar code. Times for 30 procedural 720 × 720 4:2:0 photos at quality 30 to 70 on an Apple M5 Pro, best of
five runs each. The pictures are full of detail, so typical photos decode faster:

| Runtime | Median | Slowest |
|---|---|---|
| IL2CPP, macOS arm64 release player (Unity 6000.3) | 29 ms | 36 ms |
| .NET 10 | 50 ms | 81 ms |
| Mono 6.12, built with Unity's compiler flags | 85 ms | 118 ms |

A 320 × 320 image takes about a quarter of that. Inter frames are cheaper than key frames: a 1280 × 720 sequence from aomenc
decodes at about 16 ms a frame on .NET 10 (dav1d, with SIMD, is about 10 times faster). The assembly is about 580 KB.

The IL2CPP times and the identity of its pixels with .NET and Mono were measured on 1.0.0; 1.1.0 gives the same pixels as
1.0.0 for the images that 1.0.0 decodes. It has not been run on iOS or Android devices yet.

## How it is checked

`Tests~` holds a test program with no package dependencies. Run it with:

```
dotnet run --project "Tests~/AvifDecoder.Tests.csproj" -c Release
```

It checks:

- **Pixels:** 60 test images against libavif with dav1d, within 1. They cover the formats and tools listed above, odd sizes
  from 1 × 1, the crop, rotation and mirroring, grids, alpha (limited-range alpha included), YCgCo, layered and
  progressive images.
- **RGBA and 16 bits:** the images with alpha as RGBA, premultiplied alpha included, and 16-bit output against
  `avifdec -d 16`, within 1 in 65535.
- **Image sequences:** every frame of key-frame and inter-coded sequences, with alpha tracks and 10-bit samples, against
  `avifdec --index`, with their times and repetition count; the same stream in movie fragments.
- **Layers:** each layer of a progressive image.
- **Derived images:** an overlay, an identity derivation and a grid of identity derivations, against the libavif decodes of
  their inputs composited as HEIF defines.
- **Matrices and ranges that avifenc does not write** (matrices 10, 11, 13, 14, which libavif does not decode; YCgCo-Ro;
  YCgCo and identity in limited range): pictures coded losslessly from the definitions of ITU-T H.273, against the exact
  inverse of those definitions.
- **Container:** an item built from the data of another item, the layer selector, a thumbnail and a depth map, against the
  images they are made of.
- **Resource limits:** overlays nested 7 deep with 12 inputs each (1.4 KB, 35 million decodes if every reference were
  followed), a grid inside an overlay over the pixel limit, a sequence over the frame budget and a gain map with invalid
  metadata are each refused at once with the right exception.
- **Gain map:** its metadata, and its renditions against libavif's formula on libavif's decodes.
- **Metadata:** Exif, XMP, ICC, content light level, the transformative properties and the other `Identify` fields.
- **Hashes:** the pixels of all 62 top-level test images, recorded in `Tests~/Images/expected-hashes.txt`.
- **Transform kernels:** the generated 8-bit kernels against the reference transform for every size and type.
- **Malformed input:** 2,400 truncated and corrupted files, through `Decode`, `DecodeImage`, `Identify` and `DecodeFrames`,
  which must decode or throw an `ImageDecodeException` within two seconds.
- **Unsupported features:** an unknown essential property and matrix coefficients 15 must be refused with an
  `UnsupportedImageFeatureException`.
- **Threads:** decodes on many threads at once give the same pixels.

The test images are procedural, made by `Tests~/Images/make.py` with libavif's `avifenc`, `aomenc`, ffmpeg and a small HEIF
writer (`heif.py`) for what avifenc does not write, so they carry no third-party content.

Beyond the test program:

- **AV1:** the inter-frame decoder was checked frame by frame against dav1d, exactly in Y, U and V, on 145 streams (2,832
  frames) from aomenc, SVT-AV1 and avifenc: 8, 10 and 12 bits, every chroma format, warped and global motion, OBMC, every
  compound type, scaled references, error-resilient and S-frames, film grain, show-existing frames and more; and on the
  layered AVIF files of the public test sets at every operating point. 48,600 corrupted streams threw only
  `ImageDecodeException`.
- **Mono and .NET** give identical pixels for every test image, every frame and every layer, matrices that use floating
  point included.
- **Real photos:** 1,460 real-world AVIF photos, which are not included, decode within 1 of dav1d.
- **Public test sets:** the 391 files of [AOM's](https://github.com/AOMediaCodec/av1-avif/tree/main/testFiles),
  [Link-U's](https://github.com/link-u/avif-sample-images) and [libavif's](https://github.com/AOMediaCodec/libavif/tree/main/tests/data)
  test sets, which are not included either. 389 decode and match libavif within 1 in 16-bit RGBA, every frame of their 14
  sequences included; the other 2 are invalid grids that libavif rejects too. Files that libavif 1.4 rejects were compared
  with repaired copies, and where it predates the rules of libavif's main branch (an alpha item shared by grid tiles, or
  listing two targets), the decoder follows the main branch.

`Tools~/generate_transform_kernels.py` regenerates `Runtime/Av1/Av1InverseTransform.Kernels.g.cs` and proves that no value
in the 8-bit kernels can overflow 32 bits.

## Credits and license

The AV1 decoder is ported from [Meziantou.Framework.Imaging](https://github.com/meziantou/Meziantou.Framework) by Gérald
Barré, commit `d1409b5f`, under the MIT license. It follows the
[AV1 Bitstream & Decoding Process Specification](https://aomediacodec.github.io/av1-spec/); AV1 is covered by the
royalty-free [AOMedia Patent License 1.0](https://aomedia.org/license/patent-license/).

MIT license; see [LICENSE.md](LICENSE.md).

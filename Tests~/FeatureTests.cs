using System;
using System.IO;
using System.Linq;

namespace AvifDecoding.Tests
{
    // The features beyond 8-bit RGB: alpha, 16-bit output, image sequences, metadata, derived images and the matrices that
    // libavif does not decode
    public static class FeatureTests
    {
        // Every top-level image with an alpha reference, as straight RGBA. A premultiplied file is compared after both decodes
        // are multiplied by their alpha again: libavif and this decoder each divide their own colors by alpha, which magnifies
        // a difference of 1 where alpha is small, and clamp the colors that lossy coding left above alpha.
        public static int Rgba(string images)
        {
            var failures = 0;
            foreach (var file in Directory.GetFiles(images, "*.avif").OrderBy(f => f, StringComparer.Ordinal))
            {
                var png = Path.ChangeExtension(file, ".png");
                if (!File.Exists(png))
                    continue;

                var reference = Png.ReadRgba(png);
                if (!reference.HasAlpha)
                    continue;

                var image = AvifImageDecoder.DecodeImage(File.ReadAllBytes(file), new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgba32 });
                var actual = image.Pixels.Select(v => (int)v).ToArray();
                var expected = reference.Rgba;
                var tolerance = 1;
                if (Path.GetFileName(file).Contains("premultiplied"))
                {
                    expected = (ushort[])expected.Clone();
                    for (var i = 0; i < expected.Length; i += 4)
                    {
                        for (var c = 0; c < 3; c++)
                        {
                            expected[i + c] = (ushort)(((expected[i + c] * expected[i + 3]) + 127) / 255);
                            actual[i + c] = ((actual[i + c] * actual[i + 3]) + 127) / 255;
                        }
                    }

                    tolerance = 2;
                }

                failures += Compare(Path.GetFileName(file), image.Width, image.Height, actual, reference.Width, reference.Height, expected, tolerance);
            }

            return failures;
        }

        // 16-bit output against avifdec -d 16
        public static int SixteenBit(string images)
        {
            var failures = 0;
            foreach (var png in Directory.GetFiles(Path.Combine(images, "16bit"), "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                var file = Path.Combine(images, Path.GetFileNameWithoutExtension(png) + ".avif");
                var reference = Png.ReadRgba(png);
                var image = AvifImageDecoder.DecodeImage(File.ReadAllBytes(file), new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgba64 });
                failures += Compare(Path.GetFileName(file) + " (16-bit)", image.Width, image.Height, image.Pixels16.Select(v => (int)v).ToArray(), reference.Width, reference.Height, reference.Rgba, 1);
            }

            return failures;
        }

        // Every frame against avifdec --index, with the timing that avifenc wrote: 10 frames per second, repeated twice more
        public static int Sequences(string images)
        {
            var failures = 0;
            foreach (var file in Directory.GetFiles(Path.Combine(images, "sequences"), "*.avif").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = File.ReadAllBytes(file);
                var animation = AvifImageDecoder.DecodeFrames(data);
                var info = AvifImageDecoder.Identify(data);
                if (animation.Frames.Count != 4 || animation.Timescale != 10 || animation.RepetitionCount != 2 || info.FrameCount != 4 || !info.HasSequence)
                {
                    failures++;
                    Console.WriteLine($"   FAIL {name}: {animation.Frames.Count} frames, timescale {animation.Timescale}, repetitions {animation.RepetitionCount}");
                    continue;
                }

                using var sequence = AvifImageDecoder.OpenSequence(data);
                for (var i = 0; i < animation.Frames.Count; i++)
                {
                    var frame = animation.Frames[i];
                    if (frame.Duration != 1 || frame.PresentationTime != i || frame.Index != i)
                    {
                        failures++;
                        Console.WriteLine($"   FAIL {name} frame {i}: time {frame.PresentationTime}, duration {frame.Duration}");
                    }

                    var reference = Png.ReadRgba(Path.Combine(images, "sequences", $"{name}.{i}.png"));
                    failures += Compare($"{name} frame {i}", frame.Image.Width, frame.Image.Height, frame.Image.Pixels.Select(v => (int)v).ToArray(), reference.Width, reference.Height, reference.Rgba, 1);
                    if (!sequence.TryDecodeNextFrame(out var again) || !again!.Image.Pixels.AsSpan().SequenceEqual(frame.Image.Pixels))
                    {
                        failures++;
                        Console.WriteLine($"   FAIL {name} frame {i}: OpenSequence gives other pixels");
                    }
                }
            }

            return failures;
        }

        public static int Metadata(string images)
        {
            var failures = 0;
            void Check(string what, bool ok)
            {
                if (ok)
                    return;

                failures++;
                Console.WriteLine($"   FAIL {what}");
            }

            var folder = Path.Combine(images, "metadata");
            var info = AvifImageDecoder.Identify(File.ReadAllBytes(Path.Combine(folder, "metadata.avif")));
            Check("Exif", info.Exif != null && info.Exif.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "exif.bin"))));
            Check("XMP", info.Xmp != null && info.Xmp.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "xmp.xml"))));
            Check("ICC profile", info.Color.IccProfile != null && info.Color.IccProfile.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "profile.icc"))));
            Check("content light level", info.ContentLightLevel?.MaxContentLightLevel == 1000 && info.ContentLightLevel.MaxFrameAverageLightLevel == 400);
            Check($"transforms: rotation {info.Rotation}, mirror {info.MirrorAxis}, clap {info.HasCleanAperture}", info.Rotation == 1 && info.MirrorAxis == 1 && info.HasCleanAperture);
            Check($"size {info.Width}x{info.Height} from {info.CodedWidth}x{info.CodedHeight}", info.Width == 240 && info.Height == 320 && info.CodedWidth == 512 && info.CodedHeight == 384);
            var image = AvifImageDecoder.DecodeImage(File.ReadAllBytes(Path.Combine(folder, "metadata.avif")));
            Check($"decoded size {image.Width}x{image.Height}", image.Width == 240 && image.Height == 320);

            info = AvifImageDecoder.Identify(File.ReadAllBytes(Path.Combine(images, "alpha_premultiplied.avif")));
            Check("alpha_premultiplied: premultiplied alpha", info.HasAlpha && info.IsAlphaPremultiplied);
            info = AvifImageDecoder.Identify(File.ReadAllBytes(Path.Combine(images, "alpha420.avif")));
            Check("alpha420: straight alpha", info.HasAlpha && !info.IsAlphaPremultiplied);
            info = AvifImageDecoder.Identify(File.ReadAllBytes(Path.Combine(images, "d10_444.avif")));
            Check($"d10_444: {info.BitDepth}-bit {info.ChromaFormat}", info.BitDepth == 10 && info.ChromaFormat == 444 && !info.HasAlpha && info.ImageType == "av01");
            info = AvifImageDecoder.Identify(File.ReadAllBytes(Path.Combine(images, "yuv400.avif")));
            Check("yuv400: monochrome", info.IsMonochrome && info.ChromaFormat == 400);
            info = AvifImageDecoder.Identify(File.ReadAllBytes(Path.Combine(images, "grid2x2_crop.avif")));
            Check($"grid2x2_crop: {info.ImageType} {info.Width}x{info.Height}", info.ImageType == "grid" && info.Width == 500 && info.Height == 380);
            info = AvifImageDecoder.Identify(File.ReadAllBytes(Path.Combine(images, "bt601.avif")));
            Check($"bt601: colour {info.Color.ColorPrimaries}/{info.Color.TransferCharacteristics}/{info.Color.MatrixCoefficients}", info.Color.MatrixCoefficients == 6 && info.Color.TransferCharacteristics == 13);
            return failures;
        }

        // Overlay, identity and grid of identities against the libavif decodes of their inputs, composited by the definitions
        // of HEIF; 1 more of tolerance for the compositing of two decodes that may each be 1 apart
        public static int Derived(string images)
        {
            var failures = 0;
            foreach (var file in Directory.GetFiles(Path.Combine(images, "derived"), "*.avif").OrderBy(f => f, StringComparer.Ordinal))
            {
                var reference = Png.ReadRgba(Path.ChangeExtension(file, ".png"));
                var image = AvifImageDecoder.DecodeImage(File.ReadAllBytes(file));
                failures += Compare(Path.GetFileName(file), image.Width, image.Height, image.Pixels.Select(v => (int)v).ToArray(), reference.Width, reference.Height, reference.Rgba, 2);
            }

            return failures;
        }

        // Matrices 10, 11, 13 and 14, YCgCo-Ro, and YCgCo and identity in limited range, against the exact inverse of their
        // definitions in ITU-T H.273, at 16 bits
        public static int NonLinear(string images)
        {
            var failures = 0;
            foreach (var file in Directory.GetFiles(Path.Combine(images, "nonlinear"), "*.avif").OrderBy(f => f, StringComparer.Ordinal))
            {
                var reference = Png.ReadRgba(Path.ChangeExtension(file, ".png"));
                var image = AvifImageDecoder.DecodeImage(File.ReadAllBytes(file), new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgba64 });
                failures += Compare(Path.GetFileName(file), image.Width, image.Height, image.Pixels16.Select(v => (int)v).ToArray(), reference.Width, reference.Height, reference.Rgba, 1);
            }

            return failures;
        }

        // A gain map: the metadata that Identify reads, and the renditions at headroom 1 (PQ) and 2 (sRGB) against the formula
        // of libavif on the libavif decodes of the base and the gain map. The gain map is half the size of the base and
        // tone mapping multiplies a difference of 1 in its inputs, so a few units in 65535 are allowed.
        public static int GainMap(string images)
        {
            var failures = 0;
            var file = Path.Combine(images, "gainmap", "gainmap.avif");
            var data = File.ReadAllBytes(file);
            var gainMap = AvifImageDecoder.Identify(data).GainMap;
            var metadataOk = gainMap != null && gainMap.Width == 128 && gainMap.Height == 128 && !gainMap.IsMultichannel && gainMap.UseBaseColorSpace
                && gainMap.BaseHdrHeadroom.ToDouble() == 0 && gainMap.AlternateHdrHeadroom.ToDouble() == 2 && gainMap.GainMapMin[0].ToDouble() == -0.5
                && gainMap.GainMapMax[0].ToDouble() == 2 && gainMap.GainMapGamma[0].ToDouble() == 1 && gainMap.BaseOffset[0].ToDouble() == 1.0 / 64
                && gainMap.AlternateColor?.TransferCharacteristics == 16;
            Console.WriteLine($"   {(metadataOk ? "ok  " : "FAIL")} gain map metadata");
            failures += metadataOk ? 0 : 1;

            var map = AvifImageDecoder.DecodeGainMap(data);
            var baseImage = AvifImageDecoder.DecodeImage(data, new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgba64 });
            var unchanged = AvifImageDecoder.DecodeImage(data, new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgba64, GainMapHeadroom = 0 });
            var sameAtZero = map != null && map.Width == 128 && unchanged.Pixels16.AsSpan().SequenceEqual(baseImage.Pixels16);
            Console.WriteLine($"   {(sameAtZero ? "ok  " : "FAIL")} gain map image 128x128, and headroom 0 gives the base image");
            failures += sameAtZero ? 0 : 1;

            foreach (var (headroom, transfer) in new[] { (1.0, 16), (2.0, 13) })
            {
                var reference = Png.ReadRgba(Path.Combine(images, "gainmap", $"gainmap.h{headroom}.tc{transfer}.png"));
                var image = AvifImageDecoder.DecodeImage(data, new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgba64, GainMapHeadroom = headroom, ToneMappedTransfer = transfer });
                failures += Compare($"gainmap at headroom {headroom}, transfer {transfer}", image.Width, image.Height, image.Pixels16.Select(v => (int)v).ToArray(), reference.Width, reference.Height, reference.Rgba, 4);
            }

            return failures;
        }

        // The same stream in a plain track and in movie fragments decodes to the same frames
        public static int Fragments(string images)
        {
            var plain = AvifImageDecoder.DecodeFrames(File.ReadAllBytes(Path.Combine(images, "fragments", "plain.avif")));
            var fragmented = AvifImageDecoder.DecodeFrames(File.ReadAllBytes(Path.Combine(images, "fragments", "fragmented.avif")));
            var same = plain.Frames.Count == 6 && fragmented.Frames.Count == 6;
            for (var i = 0; same && i < plain.Frames.Count; i++)
            {
                same = plain.Frames[i].Image.Pixels.AsSpan().SequenceEqual(fragmented.Frames[i].Image.Pixels)
                    && plain.Frames[i].Duration == fragmented.Frames[i].Duration && plain.Frames[i].PresentationTime == fragmented.Frames[i].PresentationTime;
            }

            Console.WriteLine($"   {(same ? "ok  " : "FAIL")} {fragmented.Frames.Count} fragmented frames against {plain.Frames.Count} plain ones");
            return same ? 0 : 1;
        }

        // The layers of a progressive image: the first is the base layer that a1op_base selects, the last the image itself
        public static int Layers(string images)
        {
            var data = File.ReadAllBytes(Path.Combine(images, "progressive.avif"));
            var layers = AvifImageDecoder.DecodeLayers(data);
            var image = AvifImageDecoder.DecodeImage(data);
            var baseLayer = AvifImageDecoder.DecodeImage(File.ReadAllBytes(Path.Combine(images, "a1op_base.avif")));
            var ok = layers.Count == 2 && layers[0].Width == 128 && layers[0].Pixels.AsSpan().SequenceEqual(baseLayer.Pixels)
                && layers[1].Width == 256 && layers[1].Pixels.AsSpan().SequenceEqual(image.Pixels);
            Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} progressive.avif: {layers.Count} layers, {string.Join(", ", layers.Select(l => $"{l.Width}x{l.Height}"))}");
            return ok ? 0 : 1;
        }

        // Container features that avifenc does not write, against the images they are made from
        public static int Container(string images)
        {
            var failures = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}");
                failures += ok ? 0 : 1;
            }

            byte[] Read(params string[] parts) => File.ReadAllBytes(Path.Combine(new[] { images }.Concat(parts).ToArray()));
            var photo = AvifImageDecoder.DecodeImage(Read("photo320_q75.avif"));
            Check("an item made of the data of another item (iloc construction method 2)",
                AvifImageDecoder.DecodeImage(Read("container", "item_from_item.avif")).Pixels.AsSpan().SequenceEqual(photo.Pixels));

            var baseLayer = AvifImageDecoder.DecodeImage(Read("a1op_base.avif"));
            var selected = AvifImageDecoder.DecodeImage(Read("container", "lsel_base.avif"));
            Check($"lsel 0 shows the base layer ({selected.Width}x{selected.Height})", selected.Width == 128 && selected.Pixels.AsSpan().SequenceEqual(baseLayer.Pixels));

            var data = Read("container", "thumbnail_depth.avif");
            var info = AvifImageDecoder.Identify(data);
            var thumbnail = AvifImageDecoder.DecodeThumbnail(data);
            Check("thumbnail", info.ThumbnailCount == 1 && thumbnail != null && thumbnail.Pixels.AsSpan().SequenceEqual(AvifImageDecoder.DecodeImage(Read("tiny65x33.avif")).Pixels));
            const string Depth = "urn:mpeg:mpegB:cicp:systems:auxiliary:depth";
            var depth = AvifImageDecoder.DecodeAuxiliaryImage(data, Depth);
            Check("depth map", info.AuxiliaryTypes.Count == 1 && info.AuxiliaryTypes[0] == Depth && !info.HasAlpha && depth != null
                && depth.Pixels.AsSpan().SequenceEqual(AvifImageDecoder.DecodeImage(Read("yuv400.avif")).Pixels));
            Check("the image itself is unchanged by its thumbnail and depth map", AvifImageDecoder.DecodeImage(data).Pixels.AsSpan().SequenceEqual(photo.Pixels));
            return failures;
        }

        // What a file may cost: each of these must be refused at once, with the exception that says why
        public static int Limits(string images)
        {
            var failures = 0;
            void Expect<T>(string what, Action action) where T : Exception
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string result;
                try
                {
                    action();
                    result = "no exception";
                }
                catch (Exception e)
                {
                    result = e.GetType().Name;
                }

                var ok = result == typeof(T).Name && watch.ElapsedMilliseconds < 2000;
                Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}: {result} in {watch.ElapsedMilliseconds} ms");
                failures += ok ? 0 : 1;
            }

            byte[] Read(params string[] parts) => File.ReadAllBytes(Path.Combine(new[] { images }.Concat(parts).ToArray()));
            var nested = Read("limits", "nested_overlays.avif");
            Expect<ImageResourceLimitException>("overlays nested 7 deep with 12 inputs each, DecodeImage", () => AvifImageDecoder.DecodeImage(nested));
            Expect<ImageResourceLimitException>("the same, Decode", () => AvifImageDecoder.Decode(nested));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var info = AvifImageDecoder.Identify(nested);
            var identified = info.ImageType == "iovl" && watch.ElapsedMilliseconds < 2000;
            Console.WriteLine($"   {(identified ? "ok  " : "FAIL")} the same, Identify: {info.ImageType} {info.Width}x{info.Height} in {watch.ElapsedMilliseconds} ms");
            failures += identified ? 0 : 1;

            var overlayOfGrid = Read("limits", "overlay_of_grid.avif");
            Expect<ImageResourceLimitException>("a 512x384 grid inside a 64x64 overlay, with a limit of 100,000 pixels",
                () => AvifImageDecoder.DecodeImage(overlayOfGrid, new AvifDecodeOptions { MaxPixels = 100_000 }));
            var inside = AvifImageDecoder.DecodeImage(overlayOfGrid);
            var decodes = inside.Width == 64 && inside.Height == 64;
            Console.WriteLine($"   {(decodes ? "ok  " : "FAIL")} the same within the default limit: {inside.Width}x{inside.Height}");
            failures += decodes ? 0 : 1;

            var sequence = Read("sequences", "keyframes.avif");
            Expect<ImageResourceLimitException>("4 frames of 96x64 with a total of 2 frames allowed",
                () => AvifImageDecoder.DecodeFrames(sequence, new AvifDecodeOptions { MaxTotalPixels = 96 * 64 * 2 }));
            Expect<ArgumentOutOfRangeException>("a negative HDR headroom",
                () => AvifImageDecoder.DecodeImage(Read("gainmap", "gainmap.avif"), new AvifDecodeOptions { GainMapHeadroom = -1 }));

            var zeroGamma = Read("limits", "gainmap_zero_gamma.avif");
            Expect<InvalidImageContentException>("a gain map with a gamma of 0, applied", () => AvifImageDecoder.DecodeImage(zeroGamma, new AvifDecodeOptions { GainMapHeadroom = 1 }));
            var ignored = AvifImageDecoder.Identify(zeroGamma).GainMap == null && AvifImageDecoder.DecodeGainMap(zeroGamma) == null
                && AvifImageDecoder.DecodeImage(zeroGamma).Pixels.AsSpan().SequenceEqual(AvifImageDecoder.DecodeImage(Read("gainmap", "gainmap.avif")).Pixels);
            Console.WriteLine($"   {(ignored ? "ok  " : "FAIL")} the same, not applied: no gain map reported, the base image decodes");
            failures += ignored ? 0 : 1;
            return failures;
        }

        private static int Compare(string name, int width, int height, int[] actual, int expectedWidth, int expectedHeight, ushort[] expected, int tolerance)
        {
            if (width != expectedWidth || height != expectedHeight || actual.Length != expected.Length)
            {
                Console.WriteLine($"   FAIL {name}: {width}x{height}, expected {expectedWidth}x{expectedHeight}");
                return 1;
            }

            int maxDiff = 0, exact = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                var diff = Math.Abs(actual[i] - expected[i]);
                maxDiff = Math.Max(maxDiff, diff);
                if (diff == 0)
                    exact++;
            }

            var ok = maxDiff <= tolerance;
            Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {name,-36} {width,4}x{height,-4} max diff {maxDiff,4}   exact {100.0 * exact / expected.Length,6:F2}%");
            return ok ? 0 : 1;
        }
    }
}

#nullable enable
// The AV1 decoder under Av1/ is ported from Meziantou.Framework.Imaging (MIT, see LICENSE.txt) at commit d1409b5f: decode
// only, scalar paths only, C# 9 without unsafe code. The AVIF container reader, the image graph and the RGB conversion are
// written for this port.
using System;
using System.Collections.Generic;
using System.Threading;

namespace AvifDecoding
{
    /// <summary>An 8-bit RGB image, top row first, three bytes per pixel.</summary>
    public sealed class AvifRgbImage
    {
        internal AvifRgbImage(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Pixels { get; }
    }

    /// <summary>
    /// Decodes AVIF files in managed code only: still images (coded, grids, overlays, identity derivations and sample
    /// transforms), alpha, image sequences, and their metadata. Every failure is an <see cref="ImageDecodeException"/>,
    /// whatever the file holds.
    /// </summary>
    public static class AvifImageDecoder
    {
        public static bool IsAvif(ReadOnlySpan<byte> data) => AvifContainer.IsAvif(data);

        /// <summary>
        /// Decodes the image to 8-bit RGB, with its crop, rotation and mirroring applied. The alpha image, when there is one,
        /// is not decoded. <see cref="DecodeImage"/> gives alpha, 16-bit samples and the other options.
        /// </summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF image, uses a feature that is not supported, or is too large.</exception>
        public static AvifRgbImage Decode(byte[] data, long maxPixels = ImageResourceLimits.DefaultMaxPixels)
        {
            var image = DecodeImage(data, new AvifDecodeOptions { PixelFormat = AvifPixelFormat.Rgb24, AlphaMode = AvifAlphaMode.Ignore, MaxPixels = maxPixels });
            return new AvifRgbImage(image.Width, image.Height, image.Pixels);
        }

        /// <summary>
        /// Decodes the still image of the file (its primary item), or the first frame of its image sequence when it has no
        /// still image or <see cref="AvifDecodeOptions.PreferSequence"/> is set.
        /// </summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF image, uses a feature that is not supported, or is too large.</exception>
        public static AvifImage DecodeImage(byte[] data, AvifDecodeOptions? options = null)
        {
            options ??= new AvifDecodeOptions();
            if (options.GainMapHeadroom.HasValue && !(options.GainMapHeadroom.Value >= 0))
                throw new ArgumentOutOfRangeException(nameof(options), "The HDR headroom must be 0 or more.");

            return Guard(() =>
            {
                var container = AvifContainer.Parse(data);
                var track = FindColorTrack(container);
                if (track != null && (!container.HasPrimaryItem || options.PreferSequence))
                {
                    using var sequence = new AvifSequenceDecoder(container, track, options);
                    if (!sequence.TryDecodeNextFrame(out var frame))
                        throw AvifContainer.Invalid("the image sequence has no frame.");

                    return frame!.Image;
                }

                var itemId = GetPrimaryItemId(container);
                if (options.UseSampleTransforms)
                    itemId = FindSampleTransformAlternative(container, itemId);

                if (options.GainMapHeadroom.HasValue && TryFindGainMap(container, itemId, out var tmap, out var gainMapItem))
                    return DecodeWithGainMap(container, options, itemId, tmap, gainMapItem);

                return DecodeItem(container, options, itemId);
            });
        }

        // An altr group lists alternatives in order of preference: a sato item listed with the primary item replaces it
        internal static uint FindSampleTransformAlternative(AvifContainer container, uint primaryId)
        {
            foreach (var group in container.EntityGroups)
            {
                if (group.Type != AvifFourCc.Altr || !group.Entities.Contains(primaryId))
                    continue;

                foreach (var entity in group.Entities)
                {
                    if (entity == primaryId)
                        break;
                    if (container.Items.TryGetValue(entity, out var item) && item.Type == AvifFourCc.Sato)
                        return entity;
                }
            }

            return primaryId;
        }

        /// <summary>
        /// Decodes every frame of the image sequence of the file, up to <see cref="AvifDecodeOptions.MaxFrames"/>. A file
        /// without an image sequence gives its still image as the only frame.
        /// </summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF image, uses a feature that is not supported, or is too large.</exception>
        public static AvifAnimation DecodeFrames(byte[] data, AvifDecodeOptions? options = null)
        {
            options ??= new AvifDecodeOptions();
            return Guard(() =>
            {
                var container = AvifContainer.Parse(data);
                var track = FindColorTrack(container);
                if (track == null)
                {
                    var image = DecodeItem(container, options, GetPrimaryItemId(container));
                    return new AvifAnimation(new List<AvifFrame> { new AvifFrame(image, 0, 0, 0, 1, true) }, 0, 1);
                }

                var frames = new List<AvifFrame>();
                long totalPixels = 0;
                using var sequence = new AvifSequenceDecoder(container, track, options);
                while (frames.Count < options.MaxFrames && sequence.TryDecodeNextFrame(out var frame))
                {
                    totalPixels += (long)frame!.Image.Width * frame.Image.Height;
                    if (totalPixels > options.MaxTotalPixels)
                        throw new ImageResourceLimitException($"The frames of the sequence exceed the limit of {options.MaxTotalPixels} pixels in total: decode fewer with MaxFrames, or one at a time with OpenSequence.");

                    frames.Add(frame);
                }

                return new AvifAnimation(frames, sequence.RepetitionCount, sequence.Timescale);
            });
        }

        /// <summary>
        /// Opens the image sequence of the file to decode its frames one at a time, which keeps one frame in memory. The
        /// decoder is not thread-safe; dispose it when done.
        /// </summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF file or has no image sequence.</exception>
        public static AvifSequenceDecoder OpenSequence(byte[] data, AvifDecodeOptions? options = null)
        {
            options ??= new AvifDecodeOptions();
            return Guard(() =>
            {
                var container = AvifContainer.Parse(data);
                var track = FindColorTrack(container) ?? throw AvifContainer.Unsupported("an image sequence in a file that has none");
                return new AvifSequenceDecoder(container, track, options);
            });
        }

        /// <summary>
        /// Decodes every layer of a layered image (a1lx, as made for progressive display), coarsest first: the last one is the
        /// image itself. Any other image gives one image, as <see cref="DecodeImage"/> does. Layers may be smaller than the
        /// image (spatial layers).
        /// </summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF image, uses a feature that is not supported, or is too large.</exception>
        public static IReadOnlyList<AvifImage> DecodeLayers(byte[] data, AvifDecodeOptions? options = null)
        {
            options ??= new AvifDecodeOptions();
            return Guard<IReadOnlyList<AvifImage>>(() =>
            {
                var container = AvifContainer.Parse(data);
                if (!container.HasMeta || !container.HasPrimaryItem)
                    return new[] { DecodeImage(data, options) };

                var node = AvifImageGraph.Build(container, container.PrimaryItemId);
                if (node.Kind != AvifImageKind.Coded)
                    return new[] { DecodeItem(container, options, node.ItemId) };

                var scope = new AllocationScope(new ImageResourceLimits { MaxPixels = options.MaxPixels });
                var alpha = node.Alpha != null && options.AlphaMode != AvifAlphaMode.Ignore ? AvifPlanarDecoder.Decode(node.Alpha, scope) : null;
                var layers = new List<AvifImage>();
                using var decoder = new Av1Decoder(scope, node.OperatingPoint) { OutputAllLayers = true };
                decoder.SetInput(node.Payload, 0, node.Payload.Length);
                while (true)
                {
                    var frame = decoder.DecodeNextOutputFrame(CancellationToken.None);
                    if (frame == null)
                        break;

                    AvifPlanes planes;
                    try
                    {
                        planes = AvifPlanes.FromFrame(scope, frame);
                    }
                    finally
                    {
                        frame.Release();
                    }

                    // The alpha image applies to the layers of its size
                    var layerAlpha = alpha != null && alpha.Width == planes.Width && alpha.Height == planes.Height ? alpha : null;
                    var image = AvifRgbConverter.Convert(planes, layerAlpha, node.AlphaPremultiplied, node.Color, options.PixelFormat, options.AlphaMode);
                    layers.Add(options.ApplyTransforms ? AvifImageTransforms.Apply(image, node) : image);
                }

                if (layers.Count == 0)
                    throw AvifContainer.Invalid("the AV1 payload has no frame.");

                return layers;
            });
        }

        /// <summary>Decodes the gain map image of the file (ISO 21496-1), or returns null when it has none.</summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF file, or its gain map cannot be decoded.</exception>
        public static AvifImage? DecodeGainMap(byte[] data, AvifDecodeOptions? options = null)
        {
            options ??= new AvifDecodeOptions();
            return Guard(() =>
            {
                var container = AvifContainer.Parse(data);
                if (!container.HasPrimaryItem || !TryFindGainMap(container, container.PrimaryItemId, out var tmap, out var gainMapItem))
                    return null;
                if (AvifIdentify.ReadGainMap(container, tmap, gainMapItem, throwIfUnusable: false) == null)
                    return null;

                var node = AvifImageGraph.Build(container, gainMapItem);
                var limits = new ImageResourceLimits { MaxPixels = options.MaxPixels };
                return AvifRenderer.Render(node, new AllocationScope(limits), options.PixelFormat, AvifAlphaMode.Ignore, applyTransforms: false);
            });
        }

        /// <summary>Decodes the first thumbnail of the still image (an item with a thmb reference to it), or returns null when it has none.</summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF file, or its thumbnail cannot be decoded.</exception>
        public static AvifImage? DecodeThumbnail(byte[] data, AvifDecodeOptions? options = null)
        {
            options ??= new AvifDecodeOptions();
            return Guard(() =>
            {
                var container = AvifContainer.Parse(data);
                if (!container.HasPrimaryItem)
                    return null;

                var thumbnails = container.GetItemsReferencing(container.PrimaryItemId, AvifFourCc.Thmb);
                return thumbnails.Count == 0 ? null : DecodeItem(container, options, thumbnails[0].Id);
            });
        }

        /// <summary>
        /// Decodes an auxiliary image of the still image (an item with an auxl reference to it) whose auxC type is
        /// <paramref name="auxiliaryType"/>, such as a depth map ("urn:mpeg:hevc:2015:auxid:2"), or returns null when it has none.
        /// <see cref="AvifImageInfo.AuxiliaryTypes"/> lists them. Alpha is decoded with the image.
        /// </summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF file, or the auxiliary image cannot be decoded.</exception>
        public static AvifImage? DecodeAuxiliaryImage(byte[] data, string auxiliaryType, AvifDecodeOptions? options = null)
        {
            options ??= new AvifDecodeOptions();
            return Guard(() =>
            {
                var container = AvifContainer.Parse(data);
                if (!container.HasPrimaryItem)
                    return null;

                foreach (var item in container.GetItemsReferencing(container.PrimaryItemId, AvifFourCc.Auxl))
                {
                    if (AvifImageGraph.GetAuxiliaryType(container, item) == auxiliaryType)
                        return DecodeItem(container, new AvifDecodeOptions { PixelFormat = options.PixelFormat, MaxPixels = options.MaxPixels, AlphaMode = AvifAlphaMode.Ignore, ApplyTransforms = false }, item.Id);
                }

                return null;
            });
        }

        /// <summary>Reads what the file holds, its metadata and its colour description, without decoding its images.</summary>
        /// <exception cref="ImageDecodeException">The file is not a valid AVIF file.</exception>
        public static AvifImageInfo Identify(byte[] data)
        {
            return Guard(() => AvifIdentify.Read(AvifContainer.Parse(data)));
        }

        internal static uint GetPrimaryItemId(AvifContainer container)
        {
            if (!container.HasMeta || !container.HasPrimaryItem)
                throw AvifContainer.Unsupported("an AVIF file without a primary image item or image sequence");

            return container.PrimaryItemId;
        }

        private static AvifImage DecodeItem(AvifContainer container, AvifDecodeOptions options, uint itemId)
        {
            var node = AvifImageGraph.Build(container, itemId);
            var limits = new ImageResourceLimits { MaxPixels = options.MaxPixels };
            var width = node.Kind == AvifImageKind.Coded ? node.Width : node.OutputWidth > 0 ? node.OutputWidth : node.Width;
            var height = node.Kind == AvifImageKind.Coded ? node.Height : node.OutputHeight > 0 ? node.OutputHeight : node.Height;
            if (width > 0 && height > 0)
                limits.EnsureCanvasWithinLimits(width, height);

            return AvifRenderer.Render(node, new AllocationScope(limits), options.PixelFormat, options.AlphaMode, options.ApplyTransforms);
        }

        private static AvifImage DecodeWithGainMap(AvifContainer container, AvifDecodeOptions options, uint itemId, AvifItem tmap, uint gainMapItem)
        {
            var node = AvifImageGraph.Build(container, itemId);
            var scope = new AllocationScope(new ImageResourceLimits { MaxPixels = options.MaxPixels });
            var baseImage = AvifRenderer.Render(node, scope, AvifPixelFormat.Rgba64, AvifAlphaMode.Straight, applyTransforms: false);
            var gainMap = AvifRenderer.Render(AvifImageGraph.Build(container, gainMapItem), scope, AvifPixelFormat.Rgba64, AvifAlphaMode.Ignore, applyTransforms: false);
            var metadata = AvifIdentify.ReadGainMap(container, tmap, gainMapItem, throwIfUnusable: true)!;
            var color = AvifIdentify.GetColor(node, AvifIdentify.FindFirstCoded(node));
            var outputPrimaries = options.ToneMappedPrimaries != 0 ? options.ToneMappedPrimaries : color.ColorPrimaries;
            var outputTransfer = options.ToneMappedTransfer != 0 ? options.ToneMappedTransfer : color.TransferCharacteristics;
            var mapped = AvifGainMapRenderer.Apply(baseImage, gainMap, metadata, color.ColorPrimaries, color.TransferCharacteristics, metadata.AlternateColor?.ColorPrimaries ?? 2,
                options.GainMapHeadroom!.Value, outputPrimaries, outputTransfer);
            if (options.ApplyTransforms)
                mapped = AvifImageTransforms.Apply(mapped, node);

            return AvifRenderer.ConvertFormat(mapped, options.PixelFormat, options.AlphaMode);
        }

        // The colour track of an image sequence: a picture track of AV1 samples that is not the auxiliary image of another
        internal static AvifTrack? FindColorTrack(AvifContainer container)
        {
            foreach (var track in container.Tracks)
            {
                if (track.Samples.Count == 0 || track.SampleEntries.Count == 0 || track.SampleEntries[0].Type != AvifFourCc.Av01)
                    continue;
                if (track.GetReferences(AvifFourCc.Auxl).Count > 0 || AvifImageGraph.IsAlphaTrack(container, track))
                    continue;

                return track;
            }

            return null;
        }

        // ISO 21496-1 in AVIF: an altr entity group whose first entity is a tmap item derived from [base image, gain map]
        internal static bool TryFindGainMap(AvifContainer container, uint baseItemId, out AvifItem tmapItem, out uint gainMapItemId)
        {
            foreach (var group in container.EntityGroups)
            {
                if (group.Type != AvifFourCc.Altr || group.Entities.Count < 2)
                    continue;
                if (!container.Items.TryGetValue(group.Entities[0], out var candidate) || candidate.Type != AvifFourCc.Tmap)
                    continue;

                var inputs = candidate.GetReferences(AvifFourCc.Dimg);
                if (inputs.Count == 2 && inputs[0] == baseItemId && group.Entities.Contains(baseItemId))
                {
                    tmapItem = candidate;
                    gainMapItemId = inputs[1];
                    return true;
                }
            }

            tmapItem = null!;
            gainMapItemId = 0;
            return false;
        }

        internal static T Guard<T>(Func<T> decode)
        {
            try
            {
                return decode();
            }
            catch (ImageDecodeException)
            {
                throw;
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (ArgumentNullException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The decoder checks the bitstream, and every array access stays bounds-checked: a malformed file that slips
                // past the checks ends here instead of corrupting memory
                throw new InvalidImageContentException($"The AVIF file is invalid: {exception.GetType().Name}: {exception.Message}", ImageFormat.Avif);
            }
        }
    }

    /// <summary>Decodes the frames of an image sequence one at a time, in order.</summary>
    public sealed class AvifSequenceDecoder : IDisposable
    {
        private readonly AvifContainer _container;
        private readonly AvifTrack _color;
        private readonly AvifTrack? _alpha;
        private readonly Dictionary<int, AvifImageNode> _colorNodes = new Dictionary<int, AvifImageNode>();
        private readonly Dictionary<int, AvifImageNode> _alphaNodes = new Dictionary<int, AvifImageNode>();
        private readonly bool _premultiplied;
        private readonly AvifDecodeOptions _options;
        private readonly AllocationScope _scope;
        private Av1Decoder? _colorDecoder;
        private Av1Decoder? _alphaDecoder;
        private int _colorEntry;
        private int _alphaEntry;
        private int _index;
        private long _time;

        internal AvifSequenceDecoder(AvifContainer container, AvifTrack color, AvifDecodeOptions options)
        {
            _container = container;
            _color = color;
            _options = options;
            _scope = new AllocationScope(new ImageResourceLimits { MaxPixels = options.MaxPixels });
            if (options.AlphaMode != AvifAlphaMode.Ignore)
            {
                foreach (var track in container.Tracks)
                {
                    if (track != color && track.GetReferences(AvifFourCc.Auxl).Contains(color.Id) && AvifImageGraph.IsAlphaTrack(container, track))
                    {
                        _alpha = track;
                        break;
                    }
                }
            }

            if (_alpha != null)
                _premultiplied = color.GetReferences(AvifFourCc.Prem).Contains(_alpha.Id) || _alpha.GetReferences(AvifFourCc.Prem).Contains(color.Id);

            _colorDecoder = new Av1Decoder(_scope, GetNode(color, _colorNodes, 1).OperatingPoint);
            if (_alpha != null)
                _alphaDecoder = new Av1Decoder(_scope, GetNode(_alpha, _alphaNodes, 1).OperatingPoint);
        }

        /// <summary>The number of frames (samples) of the sequence.</summary>
        public int FrameCount => _color.Samples.Count;

        /// <summary>The units of the frame times per second.</summary>
        public uint Timescale => _color.MediaTimescale;

        /// <summary>See <see cref="AvifAnimation.RepetitionCount"/>.</summary>
        public int RepetitionCount => _color.RepetitionCount;

        /// <summary>Whether the sequence has an alpha track that is decoded.</summary>
        public bool HasAlpha => _alpha != null;

        /// <summary>Decodes the next frame, or returns false after the last one.</summary>
        /// <exception cref="ImageDecodeException">A sample is invalid or uses a feature that is not supported.</exception>
        public bool TryDecodeNextFrame(out AvifFrame? frame)
        {
            frame = null;
            if (_colorDecoder == null)
                throw new ObjectDisposedException(nameof(AvifSequenceDecoder));
            if (_index >= _color.Samples.Count)
                return false;

            var index = _index;
            var sample = _color.Samples[index];
            var image = AvifImageDecoder.Guard(() =>
            {
                var node = GetNode(_color, _colorNodes, sample.DescriptionIndex);
                var planes = DecodeSample(_colorDecoder, node, sample, ref _colorEntry);
                AvifPlanes? alpha = null;
                if (_alphaDecoder != null && _alpha != null)
                {
                    if (index >= _alpha.Samples.Count)
                        throw AvifContainer.Invalid("the alpha track has fewer samples than the colour track.");

                    var alphaSample = _alpha.Samples[index];
                    alpha = DecodeSample(_alphaDecoder, GetNode(_alpha, _alphaNodes, alphaSample.DescriptionIndex), alphaSample, ref _alphaEntry);
                }

                var converted = AvifRgbConverter.Convert(planes, alpha, _premultiplied, node.Color, _options.PixelFormat, _options.AlphaMode);
                return _options.ApplyTransforms ? AvifImageTransforms.Apply(converted, node) : converted;
            });

            frame = new AvifFrame(image, index, _time, sample.Duration, Timescale, sample.IsSync);
            _time += sample.Duration;
            _index++;
            return true;
        }

        public void Dispose()
        {
            _colorDecoder?.Dispose();
            _alphaDecoder?.Dispose();
            _colorDecoder = null;
            _alphaDecoder = null;
        }

        // The image that a sample description of the track gives its samples: 1-based, as the samples refer to it
        private AvifImageNode GetNode(AvifTrack track, Dictionary<int, AvifImageNode> nodes, int descriptionIndex)
        {
            if (!nodes.TryGetValue(descriptionIndex, out var node))
            {
                node = AvifImageGraph.BuildTrackNode(_container, track, descriptionIndex);
                nodes[descriptionIndex] = node;
            }

            return node;
        }

        // When the sample description changes, and before the first sample, the configOBUs of its av1C (the sequence header)
        // go to the decoder, for samples that do not repeat it
        private AvifPlanes DecodeSample(Av1Decoder decoder, AvifImageNode node, AvifSample sample, ref int currentEntry)
        {
            if (sample.DescriptionIndex != currentEntry)
            {
                currentEntry = sample.DescriptionIndex;
                var config = node.Config?.ConfigObus;
                if (config != null && config.Length > 0)
                {
                    decoder.SetInput(config, 0, config.Length);
                    decoder.DecodeNextOutputFrame(CancellationToken.None)?.Release();
                }
            }

            decoder.SetInput(_container.Data, sample.Offset, sample.Size);
            var frame = decoder.DecodeNextOutputFrame(CancellationToken.None) ?? throw AvifContainer.Invalid("a sample of the image sequence shows no frame.");
            try
            {
                return AvifPlanes.FromFrame(_scope, frame);
            }
            finally
            {
                frame.Release();
            }
        }
    }
}

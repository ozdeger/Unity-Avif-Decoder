#nullable enable
using System;
using System.Collections.Generic;

namespace AvifDecoding
{
    /// <summary>The layout of decoded pixels. Rows are top first, channels in the order of the name.</summary>
    public enum AvifPixelFormat
    {
        /// <summary>8-bit red, green and blue, 3 bytes per pixel.</summary>
        Rgb24,

        /// <summary>8-bit red, green, blue and alpha, 4 bytes per pixel.</summary>
        Rgba32,

        /// <summary>16-bit red, green and blue, 3 values per pixel in <see cref="AvifImage.Pixels16"/>.</summary>
        Rgb48,

        /// <summary>16-bit red, green, blue and alpha, 4 values per pixel in <see cref="AvifImage.Pixels16"/>.</summary>
        Rgba64,
    }

    /// <summary>How the colors of an image with alpha are given.</summary>
    public enum AvifAlphaMode
    {
        /// <summary>
        /// Straight colors. A format without alpha gets the image over black, as libavif does; a premultiplied file is
        /// divided by its alpha.
        /// </summary>
        Straight,

        /// <summary>Colors multiplied by their alpha; a format without alpha gets the image over black.</summary>
        Premultiplied,

        /// <summary>The alpha image is not decoded: the colors are given as they are coded, and alpha is opaque.</summary>
        Ignore,
    }

    /// <summary>Options of <see cref="AvifImageDecoder.DecodeImage"/> and <see cref="AvifImageDecoder.DecodeFrames"/>.</summary>
    public sealed class AvifDecodeOptions
    {
        /// <summary>The layout of the pixels. The default, <see cref="AvifPixelFormat.Rgba32"/>, keeps alpha.</summary>
        public AvifPixelFormat PixelFormat { get; set; } = AvifPixelFormat.Rgba32;

        /// <summary>How alpha is applied to the colors.</summary>
        public AvifAlphaMode AlphaMode { get; set; } = AvifAlphaMode.Straight;

        /// <summary>The largest number of pixels of an image, of a grid canvas, or of a frame. Larger images throw an <see cref="ImageResourceLimitException"/>.</summary>
        public long MaxPixels { get; set; } = 4096L * 4096L;

        /// <summary>The largest number of frames that <see cref="AvifImageDecoder.DecodeFrames"/> returns; frames past it are not decoded.</summary>
        public int MaxFrames { get; set; } = int.MaxValue;

        /// <summary>
        /// The largest number of pixels that the frames of <see cref="AvifImageDecoder.DecodeFrames"/> may hold together
        /// (2^28 by default, about 1 GB of 8-bit RGBA): a longer sequence throws an <see cref="ImageResourceLimitException"/>.
        /// <see cref="AvifImageDecoder.OpenSequence"/> has no such limit, since it holds one frame at a time.
        /// </summary>
        public long MaxTotalPixels { get; set; } = 1L << 28;

        /// <summary>Whether the crop (clap), rotation (irot) and mirroring (imir) of the file are applied. They are by default, as the file asks.</summary>
        public bool ApplyTransforms { get; set; } = true;

        /// <summary>
        /// For a file with both a still image and an image sequence: <see langword="false"/> (the default) decodes the still
        /// image with <see cref="AvifImageDecoder.DecodeImage"/>; <see langword="true"/> decodes the first frame of the sequence.
        /// </summary>
        public bool PreferSequence { get; set; }

        /// <summary>
        /// Decodes the sample transform (sato) that an 'altr' group offers as an alternative to the still image, such as a
        /// 16-bit image built from two 8-bit ones, instead of the still image. libavif does it with --sato; it is off by default.
        /// </summary>
        public bool UseSampleTransforms { get; set; }

        /// <summary>
        /// Applies the gain map of the image (ISO 21496-1) for a display of this HDR headroom: log2 of its peak brightness over
        /// SDR white, 0 for an SDR display. Null (the default) gives the base image as it is coded. An image without a gain map
        /// is decoded as it is.
        /// </summary>
        public double? GainMapHeadroom { get; set; }

        /// <summary>The transfer characteristics of the pixels when a gain map is applied: 0 (the default) keeps those of the base image; 16 (PQ) or 18 (HLG) holds values above SDR white. Use a 16-bit format for HDR.</summary>
        public int ToneMappedTransfer { get; set; }

        /// <summary>The colour primaries of the pixels when a gain map is applied: 0 (the default) keeps those of the base image.</summary>
        public int ToneMappedPrimaries { get; set; }
    }

    /// <summary>Decoded pixels.</summary>
    public sealed class AvifImage
    {
        internal AvifImage(int width, int height, AvifPixelFormat format, byte[]? pixels, ushort[]? pixels16)
        {
            Width = width;
            Height = height;
            PixelFormat = format;
            Pixels = pixels ?? Array.Empty<byte>();
            Pixels16 = pixels16 ?? Array.Empty<ushort>();
        }

        public int Width { get; }

        public int Height { get; }

        public AvifPixelFormat PixelFormat { get; }

        /// <summary>The samples of an 8-bit format, top row first; empty for a 16-bit format.</summary>
        public byte[] Pixels { get; }

        /// <summary>The samples of a 16-bit format, top row first; empty for an 8-bit format.</summary>
        public ushort[] Pixels16 { get; }

        /// <summary>The number of channels of a pixel: 3 or 4.</summary>
        public int Channels => HasAlphaChannel(PixelFormat) ? 4 : 3;

        internal static bool HasAlphaChannel(AvifPixelFormat format) => format == AvifPixelFormat.Rgba32 || format == AvifPixelFormat.Rgba64;

        internal static bool IsSixteenBit(AvifPixelFormat format) => format == AvifPixelFormat.Rgb48 || format == AvifPixelFormat.Rgba64;

        internal static AvifImage Create(int width, int height, AvifPixelFormat format)
        {
            var count = checked((long)width * height * (HasAlphaChannel(format) ? 4 : 3));
            if (count > int.MaxValue)
                throw new ImageResourceLimitException($"The image of {width}x{height} pixels is too large to hold in one array.");

            return IsSixteenBit(format)
                ? new AvifImage(width, height, format, null, new ushort[count])
                : new AvifImage(width, height, format, new byte[count], null);
        }
    }

    /// <summary>A frame of an image sequence.</summary>
    public sealed class AvifFrame
    {
        internal AvifFrame(AvifImage image, int index, long presentationTime, long duration, uint timescale, bool isKeyFrame)
        {
            Image = image;
            Index = index;
            PresentationTime = presentationTime;
            Duration = duration;
            Timescale = timescale;
            IsKeyFrame = isKeyFrame;
        }

        public AvifImage Image { get; }

        /// <summary>The position of the frame in the sequence, from 0.</summary>
        public int Index { get; }

        /// <summary>When the frame is shown, in <see cref="Timescale"/> units from the start of the sequence.</summary>
        public long PresentationTime { get; }

        /// <summary>How long the frame is shown, in <see cref="Timescale"/> units.</summary>
        public long Duration { get; }

        /// <summary>The units of <see cref="PresentationTime"/> and <see cref="Duration"/> per second.</summary>
        public uint Timescale { get; }

        public TimeSpan DurationTime => Timescale == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Duration * TimeSpan.TicksPerSecond / Timescale);

        /// <summary>Whether the sample of the frame is a sync sample, which decoding can start from.</summary>
        public bool IsKeyFrame { get; }
    }

    /// <summary>The frames of a sequence and how often it repeats.</summary>
    public sealed class AvifAnimation
    {
        internal AvifAnimation(List<AvifFrame> frames, int repetitionCount, uint timescale)
        {
            Frames = frames;
            RepetitionCount = repetitionCount;
            Timescale = timescale;
        }

        public IReadOnlyList<AvifFrame> Frames { get; }

        /// <summary>How many times the sequence is played after the first time: 0 once, -1 forever, -2 unknown (libavif's convention).</summary>
        public int RepetitionCount { get; }

        public uint Timescale { get; }
    }

    /// <summary>Content light level information (clli, CTA-861.3), in candelas per square meter.</summary>
    public sealed class AvifContentLightLevel
    {
        internal AvifContentLightLevel(int maxContentLightLevel, int maxFrameAverageLightLevel)
        {
            MaxContentLightLevel = maxContentLightLevel;
            MaxFrameAverageLightLevel = maxFrameAverageLightLevel;
        }

        public int MaxContentLightLevel { get; }

        public int MaxFrameAverageLightLevel { get; }
    }

    /// <summary>Mastering display colour volume (mdcv, SMPTE ST 2086).</summary>
    public sealed class AvifMasteringDisplay
    {
        internal AvifMasteringDisplay(int[] primaries, int whiteX, int whiteY, uint maxLuminance, uint minLuminance)
        {
            DisplayPrimaries = primaries;
            WhitePointX = whiteX;
            WhitePointY = whiteY;
            MaxLuminance = maxLuminance;
            MinLuminance = minLuminance;
        }

        /// <summary>The x and y chromaticities of the three primaries, in units of 0.00002, as stored (G, B, R order in HEVC files, R, G, B in others).</summary>
        public IReadOnlyList<int> DisplayPrimaries { get; }

        public int WhitePointX { get; }

        public int WhitePointY { get; }

        /// <summary>In units of 0.0001 candelas per square meter.</summary>
        public uint MaxLuminance { get; }

        /// <summary>In units of 0.0001 candelas per square meter.</summary>
        public uint MinLuminance { get; }
    }

    /// <summary>The colour description of an image: its nclx code points (ITU-T H.273) and its ICC profile.</summary>
    public sealed class AvifColorInfo
    {
        internal AvifColorInfo(int primaries, int transfer, int matrix, bool fullRange, byte[]? iccProfile)
        {
            ColorPrimaries = primaries;
            TransferCharacteristics = transfer;
            MatrixCoefficients = matrix;
            FullRange = fullRange;
            IccProfile = iccProfile;
        }

        public int ColorPrimaries { get; }

        public int TransferCharacteristics { get; }

        public int MatrixCoefficients { get; }

        public bool FullRange { get; }

        /// <summary>The ICC profile of a colr property, or null.</summary>
        public byte[]? IccProfile { get; }
    }

    /// <summary>The gain map of an image (ISO 21496-1): the image, and the metadata that maps the base image to its alternate rendition.</summary>
    public sealed class AvifGainMapInfo
    {
        internal AvifGainMapInfo()
        {
        }

        public int Width { get; internal set; }

        public int Height { get; internal set; }

        public bool IsMultichannel { get; internal set; }

        public bool UseBaseColorSpace { get; internal set; }

        /// <summary>log2 of the HDR headroom of the base image, as a fraction.</summary>
        public AvifFraction BaseHdrHeadroom { get; internal set; }

        /// <summary>log2 of the HDR headroom of the alternate image, as a fraction.</summary>
        public AvifFraction AlternateHdrHeadroom { get; internal set; }

        /// <summary>Per channel (1 or 3): log2 of the smallest gain.</summary>
        public AvifFraction[] GainMapMin { get; internal set; } = Array.Empty<AvifFraction>();

        public AvifFraction[] GainMapMax { get; internal set; } = Array.Empty<AvifFraction>();

        public AvifFraction[] GainMapGamma { get; internal set; } = Array.Empty<AvifFraction>();

        public AvifFraction[] BaseOffset { get; internal set; } = Array.Empty<AvifFraction>();

        public AvifFraction[] AlternateOffset { get; internal set; } = Array.Empty<AvifFraction>();

        /// <summary>The colour description of the alternate rendition (the colr of the tmap item), when it has one.</summary>
        public AvifColorInfo? AlternateColor { get; internal set; }
    }

    /// <summary>A signed rational number as stored by the gain map metadata.</summary>
    public readonly struct AvifFraction
    {
        public AvifFraction(long numerator, long denominator)
        {
            Numerator = numerator;
            Denominator = denominator;
        }

        public long Numerator { get; }

        public long Denominator { get; }

        public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;

        public override string ToString() => $"{Numerator}/{Denominator}";
    }

    /// <summary>What an AVIF file holds, read from its boxes without decoding the images.</summary>
    public sealed class AvifImageInfo
    {
        internal AvifImageInfo()
        {
        }

        /// <summary>The size of the image as shown: after the crop and the rotation.</summary>
        public int Width { get; internal set; }

        public int Height { get; internal set; }

        /// <summary>The size of the image before its crop and rotation.</summary>
        public int CodedWidth { get; internal set; }

        public int CodedHeight { get; internal set; }

        /// <summary>8, 10, 12, or 16 for a sample transform.</summary>
        public int BitDepth { get; internal set; }

        public bool IsMonochrome { get; internal set; }

        /// <summary>The chroma subsampling: 444, 422, 420, or 400 for monochrome.</summary>
        public int ChromaFormat { get; internal set; }

        public bool HasAlpha { get; internal set; }

        public bool IsAlphaPremultiplied { get; internal set; }

        /// <summary>grid, iovl, iden or sato for a derived image, av01 for a coded one; empty for a sequence without a still image.</summary>
        public string ImageType { get; internal set; } = "";

        public bool HasStillImage { get; internal set; }

        public bool HasSequence { get; internal set; }

        public int FrameCount { get; internal set; }

        /// <summary>See <see cref="AvifAnimation.RepetitionCount"/>.</summary>
        public int RepetitionCount { get; internal set; }

        public uint Timescale { get; internal set; }

        /// <summary>The duration of the sequence in <see cref="Timescale"/> units.</summary>
        public long Duration { get; internal set; }

        public bool IsLayered { get; internal set; }

        /// <summary>The depth of the sample transform that an 'altr' group offers instead of the still image (see <see cref="AvifDecodeOptions.UseSampleTransforms"/>), or 0.</summary>
        public int SampleTransformBitDepth { get; internal set; }

        public AvifColorInfo Color { get; internal set; } = new AvifColorInfo(2, 2, 2, false, null);

        /// <summary>Rotation anticlockwise in quarter turns (irot), applied by the decoder unless <see cref="AvifDecodeOptions.ApplyTransforms"/> is false.</summary>
        public int Rotation { get; internal set; }

        /// <summary>-1 none, 0 top-bottom, 1 left-right (imir).</summary>
        public int MirrorAxis { get; internal set; } = -1;

        public bool HasCleanAperture { get; internal set; }

        public uint PixelAspectRatioH { get; internal set; } = 1;

        public uint PixelAspectRatioV { get; internal set; } = 1;

        public AvifContentLightLevel? ContentLightLevel { get; internal set; }

        public AvifMasteringDisplay? MasteringDisplay { get; internal set; }

        /// <summary>The Exif metadata of the image, from its TIFF header, or null.</summary>
        public byte[]? Exif { get; internal set; }

        /// <summary>The XMP packet of the image, or null.</summary>
        public byte[]? Xmp { get; internal set; }

        /// <summary>The number of thumbnails of the still image; <see cref="AvifImageDecoder.DecodeThumbnail"/> decodes the first.</summary>
        public int ThumbnailCount { get; internal set; }

        /// <summary>The auxC types of the auxiliary images of the still image other than alpha, such as depth maps; <see cref="AvifImageDecoder.DecodeAuxiliaryImage"/> decodes them.</summary>
        public IReadOnlyList<string> AuxiliaryTypes { get; internal set; } = Array.Empty<string>();

        /// <summary>The gain map of the image, or null.</summary>
        public AvifGainMapInfo? GainMap { get; internal set; }

        public uint MajorBrand { get; internal set; }

        public IReadOnlyList<string> CompatibleBrands { get; internal set; } = Array.Empty<string>();
    }
}

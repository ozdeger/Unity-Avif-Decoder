#nullable enable
using System;
using System.Collections.Generic;

namespace AvifDecoding
{
    // AvifImageDecoder.Identify: what the boxes say about the image, its sequence and its metadata
    internal static class AvifIdentify
    {
        private static readonly uint MimeXmp = AvifFourCc.Mime;

        public static AvifImageInfo Read(AvifContainer container)
        {
            var info = new AvifImageInfo { MajorBrand = container.MajorBrand };
            var brands = new List<string>();
            foreach (var brand in container.CompatibleBrands)
                brands.Add(AvifFourCc.ToText(brand));

            info.CompatibleBrands = brands;
            var track = AvifImageDecoder.FindColorTrack(container);
            if (track != null)
            {
                info.HasSequence = true;
                info.FrameCount = track.Samples.Count;
                info.Timescale = track.MediaTimescale;
                info.RepetitionCount = track.RepetitionCount;
                long duration = 0;
                foreach (var sample in track.Samples)
                    duration += sample.Duration;

                info.Duration = duration;
            }

            AvifImageNode node;
            if (container.HasMeta && container.HasPrimaryItem)
            {
                node = AvifImageGraph.Build(container, container.PrimaryItemId);
                info.HasStillImage = true;
                info.ImageType = AvifFourCc.ToText(container.Items[container.PrimaryItemId].Type);
                ReadMetadata(container, container.PrimaryItemId, info);
                info.ThumbnailCount = container.GetItemsReferencing(container.PrimaryItemId, AvifFourCc.Thmb).Count;
                var auxiliaryTypes = new List<string>();
                foreach (var item in container.GetItemsReferencing(container.PrimaryItemId, AvifFourCc.Auxl))
                {
                    var type = AvifImageGraph.GetAuxiliaryType(container, item);
                    if (type != null && !AvifImageGraph.IsAlphaAuxiliary(container, item) && !auxiliaryTypes.Contains(type))
                        auxiliaryTypes.Add(type);
                }

                info.AuxiliaryTypes = auxiliaryTypes;
                if (AvifImageDecoder.TryFindGainMap(container, container.PrimaryItemId, out var tmap, out var gainMapItem))
                    info.GainMap = ReadGainMap(container, tmap, gainMapItem, throwIfUnusable: false);

                var alternative = AvifImageDecoder.FindSampleTransformAlternative(container, container.PrimaryItemId);
                if (alternative != container.PrimaryItemId)
                    info.SampleTransformBitDepth = AvifRenderer.GetDeepestBitDepth(AvifImageGraph.Build(container, alternative));
            }
            else if (track != null)
            {
                node = AvifImageGraph.BuildTrackNode(container, track);
                foreach (var other in container.Tracks)
                {
                    if (other != track && other.GetReferences(AvifFourCc.Auxl).Contains(track.Id) && AvifImageGraph.IsAlphaTrack(container, other))
                    {
                        info.HasAlpha = true;
                        info.IsAlphaPremultiplied = track.GetReferences(AvifFourCc.Prem).Contains(other.Id);
                    }
                }
            }
            else
            {
                throw AvifContainer.Unsupported("an AVIF file without a primary image item or image sequence");
            }

            var coded = FindFirstCoded(node);
            var config = coded?.Config;
            info.CodedWidth = node.Kind == AvifImageKind.Grid || node.Kind == AvifImageKind.Overlay ? node.OutputWidth : node.Width;
            info.CodedHeight = node.Kind == AvifImageKind.Grid || node.Kind == AvifImageKind.Overlay ? node.OutputHeight : node.Height;
            if ((info.CodedWidth == 0 || info.CodedHeight == 0) && node.Kind == AvifImageKind.Identity && coded != null)
            {
                info.CodedWidth = coded.Width;
                info.CodedHeight = coded.Height;
            }

            AvifImageTransforms.GetTransformedSize(node, info.CodedWidth, info.CodedHeight, out var width, out var height);
            info.Width = width;
            info.Height = height;
            info.BitDepth = AvifRenderer.GetDeepestBitDepth(node);
            info.IsMonochrome = config?.Monochrome ?? false;
            info.ChromaFormat = config == null ? 420 : config.Monochrome ? 400 : config.SubsamplingX == 0 ? 444 : config.SubsamplingY == 0 ? 422 : 420;
            if (node.Alpha != null)
            {
                info.HasAlpha = true;
                info.IsAlphaPremultiplied = node.AlphaPremultiplied;
            }

            info.Rotation = node.Rotation;
            info.MirrorAxis = node.MirrorAxis;
            info.HasCleanAperture = node.HasCleanAperture;
            info.PixelAspectRatioH = node.PixelAspectH;
            info.PixelAspectRatioV = node.PixelAspectV;
            info.ContentLightLevel = node.ContentLightLevel;
            info.MasteringDisplay = node.MasteringDisplay;
            info.IsLayered = coded != null && (coded.LayerSizes != null || coded.LayerId >= 0 || coded.OperatingPoint != 0);
            info.Color = GetColor(node, coded);
            return info;
        }

        internal static AvifImageNode? FindFirstCoded(AvifImageNode node)
        {
            if (node.Kind == AvifImageKind.Coded)
                return node;

            foreach (var input in node.Inputs)
            {
                var coded = FindFirstCoded(input);
                if (coded != null)
                    return coded;
            }

            return null;
        }

        // The nclx of the image, else that of the sequence header in av1C, with the ICC profile of the image
        internal static AvifColorInfo GetColor(AvifImageNode node, AvifImageNode? coded)
        {
            var color = node.Color.HasNclx || coded == null ? node.Color : coded.Color.HasNclx ? coded.Color : null;
            var icc = node.Color.IccProfile ?? coded?.Color.IccProfile;
            if (color != null && color.HasNclx)
                return new AvifColorInfo(color.ColorPrimaries, color.TransferCharacteristics, color.MatrixCoefficients, color.FullRange, icc);

            // The sequence header of av1C, else the one at the start of the coded image
            var obus = coded?.Config?.ConfigObus;
            if (obus != null && obus.Length > 0 && TryReadSequenceColor(obus, out var primaries, out var transfer, out var matrix, out var fullRange))
                return new AvifColorInfo(primaries, transfer, matrix, fullRange, icc);
            if (coded != null && coded.Payload.Length > 0 && TryReadSequenceColor(coded.Payload, out primaries, out transfer, out matrix, out fullRange))
                return new AvifColorInfo(primaries, transfer, matrix, fullRange, icc);

            return new AvifColorInfo(2, 2, 2, false, icc);
        }

        private static bool TryReadSequenceColor(byte[] obus, out int primaries, out int transfer, out int matrix, out bool fullRange)
        {
            primaries = transfer = matrix = 2;
            fullRange = false;
            var position = 0;
            for (var units = 0; position < obus.Length && units < 16; units++)
            {
                var obu = Av1ObuHeader.Read(obus.AsSpan(position));
                if (obu.Type == Av1Constants.ObuSequenceHeader)
                {
                    var sequence = Av1SequenceHeader.Parse(obus.AsSpan(position + obu.HeaderLength, obu.PayloadLength), 0);
                    if (sequence.ColorDescriptionPresent)
                    {
                        primaries = sequence.ColorPrimaries;
                        transfer = sequence.TransferCharacteristics;
                        matrix = sequence.MatrixCoefficients;
                    }

                    fullRange = sequence.FullRange;
                    return true;
                }

                position += obu.TotalLength;
            }

            return false;
        }

        // Exif and XMP items that describe the image (cdsc). Exif starts at its TIFF header, past exif_tiff_header_offset.
        private static void ReadMetadata(AvifContainer container, uint itemId, AvifImageInfo info)
        {
            foreach (var item in container.GetItemsReferencing(itemId, AvifFourCc.Cdsc))
            {
                if (item.Type == AvifFourCc.Exif && info.Exif == null)
                {
                    var data = container.ReadItemData(item);
                    if (data.Length < 4)
                        continue;

                    var offset = AvifContainer.ReadUInt32(data, 0);
                    if (offset > (uint)(data.Length - 4))
                        continue;

                    var start = 4 + (int)offset;
                    var exif = new byte[data.Length - start];
                    Buffer.BlockCopy(data, start, exif, 0, exif.Length);
                    info.Exif = exif;
                }
                else if (item.Type == MimeXmp && item.ContentType == "application/rdf+xml" && info.Xmp == null)
                {
                    info.Xmp = container.ReadItemData(item);
                }
            }
        }

        // ToneMapImage (ISO 21496-1, as libavif reads it): version 0, minimum and writer versions, then the metadata
        // A gain map of a later version, or with metadata that libavif refuses (a zero denominator or gamma, a maximum below
        // the minimum), is not usable: Identify reports no gain map, and applying it is an error
        internal static AvifGainMapInfo? ReadGainMap(AvifContainer container, AvifItem tmap, uint gainMapItemId, bool throwIfUnusable)
        {
            var data = container.ReadItemData(tmap);
            var reader = new AvifReader(data, 0, data.Length);
            var version = reader.ReadByte();
            var minimumVersion = version == 0 ? reader.ReadUInt16() : 0;
            var writerVersion = version == 0 ? reader.ReadUInt16() : 0;
            if (version != 0 || minimumVersion > 0)
                return throwIfUnusable ? throw AvifContainer.Unsupported("a gain map of a later version") : (AvifGainMapInfo?)null;
            if (writerVersion < minimumVersion)
                return throwIfUnusable ? throw AvifContainer.Invalid("the gain map metadata has a writer version below its minimum version.") : (AvifGainMapInfo?)null;

            var flags = reader.ReadByte();
            var gainMap = new AvifGainMapInfo
            {
                IsMultichannel = (flags & 0x80) != 0,
                UseBaseColorSpace = (flags & 0x40) != 0,
                BaseHdrHeadroom = new AvifFraction(reader.ReadUInt32(), reader.ReadUInt32()),
                AlternateHdrHeadroom = new AvifFraction(reader.ReadUInt32(), reader.ReadUInt32()),
            };

            var channels = gainMap.IsMultichannel ? 3 : 1;
            var min = new AvifFraction[channels];
            var max = new AvifFraction[channels];
            var gamma = new AvifFraction[channels];
            var baseOffset = new AvifFraction[channels];
            var alternateOffset = new AvifFraction[channels];
            for (var c = 0; c < channels; c++)
            {
                min[c] = new AvifFraction(reader.ReadInt32(), reader.ReadUInt32());
                max[c] = new AvifFraction(reader.ReadInt32(), reader.ReadUInt32());
                gamma[c] = new AvifFraction(reader.ReadUInt32(), reader.ReadUInt32());
                baseOffset[c] = new AvifFraction(reader.ReadInt32(), reader.ReadUInt32());
                alternateOffset[c] = new AvifFraction(reader.ReadInt32(), reader.ReadUInt32());
            }

            var valid = gainMap.BaseHdrHeadroom.Denominator != 0 && gainMap.AlternateHdrHeadroom.Denominator != 0;
            for (var c = 0; c < channels; c++)
            {
                valid &= min[c].Denominator != 0 && max[c].Denominator != 0 && gamma[c].Denominator != 0 && gamma[c].Numerator != 0
                    && baseOffset[c].Denominator != 0 && alternateOffset[c].Denominator != 0
                    && max[c].Numerator * min[c].Denominator >= min[c].Numerator * max[c].Denominator;
            }

            if (!valid)
                return throwIfUnusable ? throw AvifContainer.Invalid("the gain map metadata has a zero denominator or gamma, or a maximum below its minimum.") : (AvifGainMapInfo?)null;

            gainMap.GainMapMin = min;
            gainMap.GainMapMax = max;
            gainMap.GainMapGamma = gamma;
            gainMap.BaseOffset = baseOffset;
            gainMap.AlternateOffset = alternateOffset;

            var tmapNode = new AvifImageNode(AvifImageKind.Identity, tmap.Id);
            foreach (var property in container.GetProperties(tmap))
                AvifImageGraph.ApplyProperty(container, tmapNode, property.Box);

            if (tmapNode.Color.HasNclx || tmapNode.Color.IccProfile != null)
                gainMap.AlternateColor = new AvifColorInfo(tmapNode.Color.ColorPrimaries, tmapNode.Color.TransferCharacteristics, tmapNode.Color.MatrixCoefficients, tmapNode.Color.FullRange, tmapNode.Color.IccProfile);

            var gainMapNode = AvifImageGraph.Build(container, gainMapItemId);
            gainMap.Width = gainMapNode.Kind == AvifImageKind.Grid ? gainMapNode.OutputWidth : gainMapNode.Width;
            gainMap.Height = gainMapNode.Kind == AvifImageKind.Grid ? gainMapNode.OutputHeight : gainMapNode.Height;
            return gainMap;
        }
    }
}

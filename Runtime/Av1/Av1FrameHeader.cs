#nullable enable
// The fixed arrays are written through their indexers, which the compiler does not count as assignments
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;

namespace AvifDecoding
{
    /// <summary>
    /// The uncompressed header of an AV1 frame (AV1 specification, section 5.9) and the variables derived from it. One instance is
    /// created per frame by <see cref="Av1FrameHeaderParser"/> and is not modified afterwards: a reference frame keeps the header of
    /// the frame that produced it, which is the state that later frames load from it (order hints, global motion, loop filter deltas,
    /// segmentation features, film grain).
    /// </summary>
    internal sealed class Av1FrameHeader
    {

        public bool DisableCdfUpdate { get; set; }

        public bool AllowScreenContentTools { get; set; }

        public bool ForceIntegerMv { get; set; }

        public bool FrameSizeOverrideFlag { get; set; }

        public bool AllowIntrabc { get; set; }

        /// <summary>Gets or sets the width of the coded frame, before super-resolution upscaling.</summary>
        public int FrameWidth { get; set; }

        public int FrameHeight { get; set; }

        /// <summary>Gets or sets the width of the frame after super-resolution upscaling.</summary>
        public int UpscaledWidth { get; set; }

        public bool UseSuperres { get; set; }

        public int SuperresDenom { get; set; }

        public int MiCols { get; set; }

        public int MiRows { get; set; }

        public bool DisableFrameEndUpdateCdf { get; set; }

        public int TileCols { get; set; }

        public int TileRows { get; set; }

        public int TileColsLog2 { get; set; }

        public int TileRowsLog2 { get; set; }

        /// <summary>Gets or sets <c>MiColStarts</c>: <see cref="TileCols"/> + 1 entries.</summary>
        public int[] MiColStarts { get; set; } = System.Array.Empty<int>();

        /// <summary>Gets or sets <c>MiRowStarts</c>: <see cref="TileRows"/> + 1 entries.</summary>
        public int[] MiRowStarts { get; set; } = System.Array.Empty<int>();

        public int ContextUpdateTileId { get; set; }

        public int TileSizeBytes { get; set; }

        public int BaseQIdx { get; set; }

        public int DeltaQYDc { get; set; }

        public int DeltaQUDc { get; set; }

        public int DeltaQUAc { get; set; }

        public int DeltaQVDc { get; set; }

        public int DeltaQVAc { get; set; }

        public bool UsingQmatrix { get; set; }

        public int QmY { get; set; }

        public int QmU { get; set; }

        public int QmV { get; set; }

        public bool SegmentationEnabled { get; set; }

        public bool SegmentationUpdateData { get; set; }

        public Av1SegmentationFeatures Segmentation;

        public bool SegIdPreSkip { get; set; }

        public int LastActiveSegId { get; set; }

        public bool DeltaQPresent { get; set; }

        public int DeltaQRes { get; set; }

        public bool DeltaLfPresent { get; set; }

        public int DeltaLfRes { get; set; }

        public bool DeltaLfMulti { get; set; }

        public bool CodedLossless { get; set; }

        public bool AllLossless { get; set; }

        /// <summary><c>LosslessArray[ segmentId ]</c>.</summary>
        public InlineArray8<bool> LosslessArray;

        /// <summary><c>loop_filter_level[ 0..3 ]</c>: luma vertical, luma horizontal, U, V.</summary>
        public InlineArray4<byte> LoopFilterLevel;

        public int LoopFilterSharpness { get; set; }

        public bool LoopFilterDeltaEnabled { get; set; }

        public bool LoopFilterDeltaUpdate { get; set; }

        public Av1LoopFilterDeltas LoopFilterDeltas;

        public int CdefDamping { get; set; }

        public int CdefBits { get; set; }

        public InlineArray8<byte> CdefYPriStrength;

        public InlineArray8<byte> CdefYSecStrength;

        public InlineArray8<byte> CdefUvPriStrength;

        public InlineArray8<byte> CdefUvSecStrength;

        /// <summary><c>FrameRestorationType[ plane ]</c>.</summary>
        public InlineArray3<byte> FrameRestorationType;

        public bool UsesLr { get; set; }

        /// <summary><c>LoopRestorationSize[ plane ]</c>.</summary>
        public InlineArray3<int> LoopRestorationSize;

        /// <summary>Gets or sets <c>TxMode</c>: <see cref="Av1Constants.Only4x4"/>, <see cref="Av1Constants.TxModeLargest"/> or <see cref="Av1Constants.TxModeSelect"/>.</summary>
        public int TxMode { get; set; }

        public bool ReducedTxSet { get; set; }

        /// <summary>Gets or sets the film grain parameters, <see langword="null"/> when no grain is applied to the frame.</summary>
        public Av1FilmGrainParameters? FilmGrain { get; set; }

        /// <summary>Gets or sets the temporal layer of the frame (0 without OBU extension).</summary>
        public int TemporalId { get; set; }

        /// <summary>Gets or sets the spatial layer of the frame (0 without OBU extension).</summary>
        public int SpatialId { get; set; }

        /// <summary>Gets the number of tiles of the frame.</summary>
        public int TileCount => TileCols * TileRows;

        /// <summary>Returns <c>seg_feature_active_idx( segmentId, feature )</c> (section 5.11.14).</summary>
        public bool IsSegmentationFeatureActive(int segmentId, int feature) => SegmentationEnabled && Segmentation.IsEnabled(segmentId, feature);

        /// <summary>
        /// Returns the quantizer index of a segment (<c>get_qindex</c>, section 7.12.2). <paramref name="currentQIndex"/> is the
        /// <c>CurrentQIndex</c> of the block, or -1 to ignore the block-level delta (<c>ignoreDeltaQ</c> equal to 1).
        /// </summary>
        public int GetQIndex(int segmentId, int currentQIndex)
        {
            var useDelta = currentQIndex >= 0 && DeltaQPresent;
            if (IsSegmentationFeatureActive(segmentId, Av1Constants.SegLvlAltQ))
            {
                var data = Segmentation.GetData(segmentId, Av1Constants.SegLvlAltQ);
                return Math.Clamp((useDelta ? currentQIndex : BaseQIdx) + data, 0, 255);
            }

            return useDelta ? currentQIndex : BaseQIdx;
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
namespace AvifDecoding
{
    /// <summary>
    /// The state that the frames of an AV1 sequence share, whichever side codes them (AV1 specification, sections 7.20 "Reference
    /// frame update process" and 7.21 "Reference frame loading process"): the 8 reference slots, the block-level state of the
    /// frame being coded, the working buffers of its tiles and the cumulative distributions of the frame, of the tile being coded
    /// and of the tile that updates the frame context.
    /// </summary>
    internal sealed class Av1FrameCodingState : IDisposable
    {
        private readonly AllocationScope _scope;
        private Av1PooledArray<ushort>? _contexts;

        public Av1FrameCodingState(AllocationScope scope)
        {
            if (scope == null)
                throw new ArgumentNullException(nameof(scope));
            _scope = scope;
        }

        /// <summary>Gets the block-level state of the frame being coded.</summary>
        public Av1FrameState State { get; } = new();

        /// <summary>Gets the post-processing filters and their working buffers.</summary>
        public Av1PostFilter PostFilter { get; } = new();

        /// <summary>Gets the working buffers of the tiles, rented by the first frame.</summary>
        public Av1TileScratch? Scratch { get; private set; }

        /// <summary>Gets the working buffers of the inter prediction, rented by the first frame that predicts from another frame.</summary>
        public Av1InterScratch? InterScratch { get; private set; }

        /// <summary>Gets the frame being coded, or <see langword="null"/> between two frames.</summary>
        public Av1FrameBuffer? CurrentFrame { get; private set; }

        /// <summary>Gets the frame context: the cumulative distributions that the tiles of the frame start from.</summary>
        public Span<ushort> FrameContext => _contexts!.Span[..Av1CdfLayout.TotalLength];

        /// <summary>Gets the context of the tile being coded.</summary>
        public Span<ushort> TileContext => _contexts!.Span.Slice(Av1CdfLayout.TotalLength, Av1CdfLayout.TotalLength);

        /// <summary>Gets the context saved by the tile that updates the frame context.</summary>
        public Span<ushort> SavedContext => _contexts!.Span.Slice(2 * Av1CdfLayout.TotalLength, Av1CdfLayout.TotalLength);

        /// <summary>
        /// Starts a frame: rents its planes, prepares the block-level state and loads the frame
        /// context (<c>init_non_coeff_cdfs</c> and <c>init_coeff_cdfs</c>, or <c>load_cdfs</c>).
        /// </summary>
        /// <returns>The frame, which stays <see cref="CurrentFrame"/> until <see cref="StoreFrame"/>.</returns>
        /// <exception cref="InvalidImageContentException">The frame refers to an empty reference slot.</exception>
        /// <exception cref="ImageResourceLimitException">The buffers exceed the live-allocation limit.</exception>
        public Av1FrameBuffer BeginFrame(Av1SequenceHeader sequence, Av1FrameHeader header)
        {
            Scratch ??= new Av1TileScratch(_scope);
            _contexts ??= Av1PooledArray<ushort>.Rent(_scope, 3L * Av1CdfLayout.TotalLength, clear: false);

            var frame = new Av1FrameBuffer(sequence, header);
            CurrentFrame?.Release();
            CurrentFrame = frame;
            frame.Allocate(_scope);

            // load_previous_segment_ids
            Av1FrameBuffer? segmentationSource = null;

            if (header.AllowIntrabc)
                InterScratch ??= new Av1InterScratch(_scope);

            Scratch.PrepareAboveContext(_scope, header.MiCols);
            State.Prepare(_scope, sequence, header, segmentationSource);

            // init_non_coeff_cdfs and init_coeff_cdfs, or load_cdfs
            var frameContext = FrameContext;
            {
                Av1CdfLayout.InitializeNonCoefficients(frameContext);
                var index = header.BaseQIdx switch
                {
                    <= 20 => 0,
                    <= 60 => 1,
                    <= 120 => 2,
                    _ => 3,
                };

                Av1CdfLayout.InitializeCoefficients(frameContext, index);
            }

            return frame;
        }

        /// <summary>
        /// Starts a tile (<c>init_symbol</c>, section 8.2.2): the tile is coded with a copy of the frame context, and with the
        /// default intra frame y mode distributions.
        /// </summary>
        /// <returns><see cref="TileContext"/>.</returns>
        public Span<ushort> BeginTile()
        {
            var tileContext = TileContext;
            BeginTile(tileContext);
            return tileContext;
        }

        /// <summary>
        /// Starts a tile with a context of its own, <paramref name="tileContext"/> (<see cref="Av1CdfLayout.TotalLength"/>
        /// values). The frame context is only read.
        /// </summary>
        public void BeginTile(Span<ushort> tileContext)
        {
            FrameContext.CopyTo(tileContext);
            Av1Tables.DefaultIntraFrameYModeCdf.CopyTo(tileContext[Av1CdfLayout.IntraFrameYMode..]);
        }

        /// <summary>Ends the tile <paramref name="tileNum"/> (<c>exit_symbol</c>, section 8.2.4): saves its context when it is the one that updates the frame context.</summary>
        public void EndTile(Av1FrameHeader header, int tileNum) => EndTile(header, tileNum, TileContext);

        /// <summary>
        /// Ends the tile <paramref name="tileNum"/> that was coded with <paramref name="tileContext"/>: saves the context when the
        /// tile is the one that updates the frame context, which only one tile of a frame is.
        /// </summary>
        public void EndTile(Av1FrameHeader header, int tileNum, ReadOnlySpan<ushort> tileContext)
        {
            if (!header.DisableFrameEndUpdateCdf && tileNum == header.ContextUpdateTileId)
                tileContext.CopyTo(SavedContext);
        }

        /// <summary>Ends the last tile of the frame: the saved context becomes the frame context (section 7.4).</summary>
        public void EndTiles(Av1FrameHeader header)
        {
            if (!header.DisableFrameEndUpdateCdf)
                SavedContext.CopyTo(FrameContext);
        }

        /// <summary>Ends the coding of the current frame: nothing refers to it once it is output, so it is not stored.</summary>
        /// <returns>The frame; the caller releases it.</returns>
        public Av1FrameBuffer StoreFrame()
        {
            var frame = CurrentFrame ?? throw new InvalidOperationException("No frame is being coded.");
            CurrentFrame = null;
            return frame;
        }

        public void Dispose()
        {
            CurrentFrame?.Release();
            CurrentFrame = null;
            State.Dispose();
            PostFilter.Dispose();
            Scratch?.Dispose();
            Scratch = null;
            InterScratch?.Dispose();
            InterScratch = null;
            _contexts?.Dispose();
            _contexts = null;
        }
    }
}

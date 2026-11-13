#nullable enable
using System;

namespace AvifDecoding
{
    // Turns an image node into pixels. Coded images, grids of coded tiles and sample transforms are converted from their
    // planes straight to the requested format. The other derived images (ISO/IEC 23008-12, 6.6) are built in straight RGBA,
    // at 16 bits when one of their coded images is deeper than 8 bits: each input is rendered with its own colour, alpha and
    // transformative properties, then copied (grid) or composited (overlay) with the source-over operator.
    internal static class AvifRenderer
    {
        public static AvifImage Render(AvifImageNode node, AllocationScope scope, AvifPixelFormat format, AvifAlphaMode mode, bool applyTransforms)
        {
            if (node.IsPlanar)
                return RenderPlanar(node, scope, format, mode, applyTransforms);

            var wide = GetDeepestBitDepth(node) > 8;
            var working = RenderWorking(node, scope, wide ? AvifPixelFormat.Rgba64 : AvifPixelFormat.Rgba32, applyTransforms);
            return ConvertFormat(working, format, mode);
        }

        private static AvifImage RenderPlanar(AvifImageNode node, AllocationScope scope, AvifPixelFormat format, AvifAlphaMode mode, bool applyTransforms)
        {
            var planes = AvifPlanarDecoder.Decode(node, scope);
            var alpha = mode == AvifAlphaMode.Ignore ? null : AvifPlanarDecoder.DecodeAlpha(node, scope);
            var image = AvifRgbConverter.Convert(planes, alpha, node.AlphaPremultiplied, node.Color, format, mode);
            return applyTransforms ? AvifImageTransforms.Apply(image, node) : image;
        }

        // Straight RGBA at the working depth, with the transformative properties of the node when asked
        private static AvifImage RenderWorking(AvifImageNode node, AllocationScope scope, AvifPixelFormat working, bool applyTransforms)
        {
            AvifImage image;
            if (node.IsPlanar)
            {
                return RenderPlanar(node, scope, working, AvifAlphaMode.Straight, applyTransforms);
            }

            switch (node.Kind)
            {
                case AvifImageKind.Identity:
                    image = RenderWorking(node.Inputs[0], scope, working, applyTransforms: true);
                    break;
                case AvifImageKind.Grid:
                    image = RenderGrid(node, scope, working);
                    break;
                case AvifImageKind.Overlay:
                    image = RenderOverlay(node, scope, working);
                    break;
                default:
                    throw new InvalidOperationException();
            }

            // The alpha auxiliary image of a derived image replaces the alpha of its rendering
            if (node.Alpha != null)
            {
                var alpha = AvifPlanarDecoder.Decode(node.Alpha, scope);
                if (alpha.Width != image.Width || alpha.Height != image.Height)
                    throw AvifContainer.Invalid("the alpha image and the color image differ in size.");

                SetAlpha(image, alpha);
            }

            return applyTransforms ? AvifImageTransforms.Apply(image, node) : image;
        }

        // A grid of derived tiles: each tile is rendered, and they are copied side by side, cropped to the output
        private static AvifImage RenderGrid(AvifImageNode grid, AllocationScope scope, AvifPixelFormat working)
        {
            scope.Limits.EnsureCanvasWithinLimits(grid.OutputWidth, grid.OutputHeight);
            scope.Limits.ChargeImage(grid.OutputWidth, grid.OutputHeight);
            var canvas = AvifImage.Create(grid.OutputWidth, grid.OutputHeight, working);
            int tileWidth = 0, tileHeight = 0;
            for (var index = 0; index < grid.Inputs.Count; index++)
            {
                var tile = RenderWorking(grid.Inputs[index], scope, working, applyTransforms: true);
                if (index == 0)
                {
                    tileWidth = tile.Width;
                    tileHeight = tile.Height;
                    if ((long)tileWidth * grid.Columns < grid.OutputWidth || (long)tileHeight * grid.Rows < grid.OutputHeight)
                        throw AvifContainer.Invalid("the grid tiles do not cover the output.");
                    if ((long)tileWidth * (grid.Columns - 1) >= grid.OutputWidth || (long)tileHeight * (grid.Rows - 1) >= grid.OutputHeight)
                        throw AvifContainer.Invalid("the last column or row of grid tiles is outside the output.");
                }
                else if (tile.Width != tileWidth || tile.Height != tileHeight)
                {
                    throw AvifContainer.Invalid("the grid tiles differ in size.");
                }

                var left = (index % grid.Columns) * tileWidth;
                var top = (index / grid.Columns) * tileHeight;
                var width = Math.Min(tileWidth, grid.OutputWidth - left);
                var height = Math.Min(tileHeight, grid.OutputHeight - top);
                for (var row = 0; row < height; row++)
                {
                    if (AvifImage.IsSixteenBit(working))
                        Array.Copy(tile.Pixels16, row * tileWidth * 4, canvas.Pixels16, (((top + row) * canvas.Width) + left) * 4, width * 4);
                    else
                        Array.Copy(tile.Pixels, row * tileWidth * 4, canvas.Pixels, (((top + row) * canvas.Width) + left) * 4, width * 4);
                }
            }

            return canvas;
        }

        // ISO/IEC 23008-12, 6.6.2.2: the canvas is filled with the fill colour (16-bit values), then each input is composited
        // at its offset, in the order of the references, with source-over on straight alpha:
        // A = sa max + da (max - sa), alpha = round(A / max), color = round((sc sa max + dc da (max - sa)) / A)
        private static AvifImage RenderOverlay(AvifImageNode overlay, AllocationScope scope, AvifPixelFormat working)
        {
            scope.Limits.EnsureCanvasWithinLimits(overlay.OutputWidth, overlay.OutputHeight);
            scope.Limits.ChargeImage(overlay.OutputWidth, overlay.OutputHeight);
            var canvas = AvifImage.Create(overlay.OutputWidth, overlay.OutputHeight, working);
            var wide = AvifImage.IsSixteenBit(working);
            long max = wide ? 65535 : 255;
            var fill = new long[4];
            for (var c = 0; c < 4; c++)
                fill[c] = ((overlay.FillColor[c] * max) + 32767) / 65535;

            var pixelCount = canvas.Width * canvas.Height;
            for (var i = 0; i < pixelCount; i++)
            {
                for (var c = 0; c < 4; c++)
                {
                    if (wide)
                        canvas.Pixels16[(i * 4) + c] = (ushort)fill[c];
                    else
                        canvas.Pixels[(i * 4) + c] = (byte)fill[c];
                }
            }

            for (var index = 0; index < overlay.Inputs.Count; index++)
            {
                var input = RenderWorking(overlay.Inputs[index], scope, working, applyTransforms: true);
                var offsetX = overlay.OffsetX[index];
                var offsetY = overlay.OffsetY[index];
                var x0 = Math.Max(0, offsetX);
                var y0 = Math.Max(0, offsetY);
                var x1 = (int)Math.Min(canvas.Width, (long)offsetX + input.Width);
                var y1 = (int)Math.Min(canvas.Height, (long)offsetY + input.Height);
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var source = (((y - offsetY) * input.Width) + (x - offsetX)) * 4;
                        var target = ((y * canvas.Width) + x) * 4;
                        long sa = wide ? input.Pixels16[source + 3] : input.Pixels[source + 3];
                        if (sa == 0)
                            continue;

                        long da = wide ? canvas.Pixels16[target + 3] : canvas.Pixels[target + 3];
                        var total = (sa * max) + (da * (max - sa));
                        for (var c = 0; c < 3; c++)
                        {
                            long sc = wide ? input.Pixels16[source + c] : input.Pixels[source + c];
                            long dc = wide ? canvas.Pixels16[target + c] : canvas.Pixels[target + c];
                            var value = ((sc * sa * max) + (dc * da * (max - sa)) + (total / 2)) / total;
                            if (wide)
                                canvas.Pixels16[target + c] = (ushort)value;
                            else
                                canvas.Pixels[target + c] = (byte)value;
                        }

                        var alpha = (total + (max / 2)) / max;
                        if (wide)
                            canvas.Pixels16[target + 3] = (ushort)alpha;
                        else
                            canvas.Pixels[target + 3] = (byte)alpha;
                    }
                }
            }

            return canvas;
        }

        private static void SetAlpha(AvifImage image, AvifPlanes alpha)
        {
            var max = AvifImage.IsSixteenBit(image.PixelFormat) ? 65535L : 255L;
            var alphaMax = (1L << alpha.BitDepth) - 1;
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var sample = AvifRgbConverter.ExpandAlpha(alpha.Y[(y * alpha.YStride) + x], alpha.BitDepth, alpha.SequenceFullRange);
                    var value = ((sample * max * 2) + alphaMax) / (2 * alphaMax);
                    var index = (((y * image.Width) + x) * 4) + 3;
                    if (max > 255)
                        image.Pixels16[index] = (ushort)value;
                    else
                        image.Pixels[index] = (byte)value;
                }
            }
        }

        // Straight RGBA at the working depth to the requested format and alpha mode
        internal static AvifImage ConvertFormat(AvifImage working, AvifPixelFormat format, AvifAlphaMode mode)
        {
            var wideIn = AvifImage.IsSixteenBit(working.PixelFormat);
            var wideOut = AvifImage.IsSixteenBit(format);
            var result = AvifImage.Create(working.Width, working.Height, format);
            var outChannels = result.Channels;
            long maxIn = wideIn ? 65535 : 255;
            long maxOut = wideOut ? 65535 : 255;
            var count = working.Width * working.Height;
            for (var i = 0; i < count; i++)
            {
                long a = wideIn ? working.Pixels16[(i * 4) + 3] : working.Pixels[(i * 4) + 3];
                a = maxIn == maxOut ? a : ((a * maxOut * 2) + maxIn) / (2 * maxIn);
                var multiply = mode != AvifAlphaMode.Ignore && (outChannels == 3 || mode == AvifAlphaMode.Premultiplied);
                for (var c = 0; c < 3; c++)
                {
                    long v = wideIn ? working.Pixels16[(i * 4) + c] : working.Pixels[(i * 4) + c];
                    v = maxIn == maxOut ? v : ((v * maxOut * 2) + maxIn) / (2 * maxIn);
                    if (multiply)
                        v = ((v * a) + (maxOut / 2)) / maxOut;

                    if (wideOut)
                        result.Pixels16[(i * outChannels) + c] = (ushort)v;
                    else
                        result.Pixels[(i * outChannels) + c] = (byte)v;
                }

                if (outChannels == 4)
                {
                    var alpha = mode == AvifAlphaMode.Ignore ? maxOut : a;
                    if (wideOut)
                        result.Pixels16[(i * 4) + 3] = (ushort)alpha;
                    else
                        result.Pixels[(i * 4) + 3] = (byte)alpha;
                }
            }

            return result;
        }

        // The depth of the deepest coded image that a derived image is made of
        public static int GetDeepestBitDepth(AvifImageNode node)
        {
            if (node.Kind == AvifImageKind.Coded)
                return node.Config?.BitDepth ?? 8;
            if (node.Kind == AvifImageKind.SampleTransform && node.Transform != null)
                return node.Transform.BitDepth;

            var depth = 8;
            foreach (var input in node.Inputs)
                depth = Math.Max(depth, GetDeepestBitDepth(input));

            if (node.Alpha != null)
                depth = Math.Max(depth, GetDeepestBitDepth(node.Alpha));

            return depth;
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;

namespace AvifDecoding
{
    /// <content>
    /// The prediction of a block from another frame, or from the frame itself with intra block copy (AV1 specification, sections
    /// 5.11.33 "Compute prediction syntax" and 7.11.3 "Inter prediction process").
    /// </content>
    internal ref partial struct Av1TileCoder
    {
        // Section 5.11.33 "Compute prediction syntax": the intra prediction of a block that is not inter is made for each
        // transform block
        private void ComputePrediction()
        {
            if (!_isInter)
                return;

            var subBlockMiRow = _miRow & _sbMask;
            var subBlockMiCol = _miCol & _sbMask;
            for (var plane = 0; plane < 1 + (_hasChroma ? 2 : 0); plane++)
            {
                var planeSz = GetPlaneResidualSize(_miSize, plane);
                int num4x4W = Av1Tables.Num4x4BlocksWide[planeSz];
                int num4x4H = Av1Tables.Num4x4BlocksHigh[planeSz];
                var subX = plane > 0 ? _subX : 0;
                var subY = plane > 0 ? _subY : 0;
                var baseX = (_miCol >> subX) * Av1Constants.MiSize;
                var baseY = (_miRow >> subY) * Av1Constants.MiSize;
                var candRow = (_miRow >> subY) << subY;
                var candCol = (_miCol >> subX) << subX;

                var predW = BlockWidth(_miSize) >> subX;
                var predH = BlockHeight(_miSize) >> subY;
                var someUseIntra = false;
                for (var r = 0; r < (num4x4H << subY) && candRow + r < _miRows; r++)
                {
                    for (var c = 0; c < (num4x4W << subX) && candCol + c < _miCols; c++)
                    {
                        if (_refFrames[MiIndex(candRow + r, candCol + c) * 2] is Av1Constants.IntraFrame)
                            someUseIntra = true;
                    }
                }


                if (someUseIntra)
                {
                    predW = num4x4W * 4;
                    predH = num4x4H * 4;
                    candRow = _miRow;
                    candCol = _miCol;
                }

                var rowOffset = 0;
                for (var y = 0; y < num4x4H * 4; y += predH)
                {
                    var colOffset = 0;
                    for (var x = 0; x < num4x4W * 4; x += predW)
                    {
                        PredictInter(plane, baseX + x, baseY + y, predW, predH, candRow + rowOffset, candCol + colOffset);
                        colOffset++;
                    }

                    rowOffset++;
                }
            }
        }

        // Section 7.11.3.1 "General" of the inter prediction process. In a key frame the inter blocks are intra block copies: one
        // prediction from the frame being decoded, with an integer motion vector and the bilinear filter, without warp, compound
        // or overlapped prediction
        private void PredictInter(int plane, int x, int y, int w, int h, int candRow, int candCol)
        {
            const int Stride = Av1InterScratch.Stride;
            var scratch = _interScratch!;
            var candIndex = MiIndex(candRow, candCol);
            var subX = plane > 0 ? _subX : 0;
            var subY = plane > 0 ? _subY : 0;

            // Section 7.11.3.2 "Rounding variables derivation process", for a prediction that is not compound
            var interRound0 = 3;
            var interRound1 = 11;
            if (_bitDepth is 12)
            {
                interRound0 += 2;
                interRound1 -= 2;
            }

            var filters = _interInfo[candIndex];
            var pred = scratch.GetPrediction(0);
            GetReferencePlane(plane, out var reference, out var lastX, out var lastY, out var xScale, out var yScale);
            ScaleMotionVector(x, y, _mvs[candIndex * 4], _mvs[(candIndex * 4) + 1], subX, subY, xScale, yScale, out var startX, out var startY, out var stepX, out var stepY);
            Av1InterPredictor.PredictBlock(reference.Samples, reference.Stride, lastX, lastY, startX, startY, stepX, stepY, w, h, (filters >> 2) & 3, filters & 3, interRound0, interRound1, scratch.Intermediate, pred, Stride);

            var planeBuffer = _frame.GetPlane(plane);
            var samples = planeBuffer.Samples;
            var stride = planeBuffer.Stride;
            var max = (1 << _bitDepth) - 1;
            for (var i = 0; i < h; i++)
            {
                var destination = samples.Slice(((y + i) * stride) + x, w);
                var source = pred.Slice(i * Stride, w);
                for (var j = 0; j < w; j++)
                {
                    destination[j] = (ushort)Math.Clamp(source[j], 0, max);
                }
            }
        }

        // The reference of an intra block copy: the frame being decoded, before its loop filters, and not cropped to the frame
        private readonly void GetReferencePlane(int plane, out Av1Plane reference, out int lastX, out int lastY, out int xScale, out int yScale)
        {
            var subX = plane > 0 ? _subX : 0;
            var subY = plane > 0 ? _subY : 0;
            reference = _frame.GetPlane(plane);
            lastX = (((_miCols * Av1Constants.MiSize) + subX) >> subX) - 1;
            lastY = (((_miRows * Av1Constants.MiSize) + subY) >> subY) - 1;
            xScale = 1 << Av1Constants.RefScaleShift;
            yScale = 1 << Av1Constants.RefScaleShift;
        }

        // Section 7.11.3.3 "Motion vector scaling process"
        private static void ScaleMotionVector(int x, int y, int mvRow, int mvCol, int subX, int subY, int xScale, int yScale, out int startX, out int startY, out int stepX, out int stepY)
        {
            const int HalfSample = 1 << (Av1Constants.SubpelBits - 1);
            const int Shift = Av1Constants.RefScaleShift + Av1Constants.SubpelBits - Av1Constants.ScaleSubpelBits;
            const int Off = (1 << (Av1Constants.ScaleSubpelBits - Av1Constants.SubpelBits)) / 2;
            long origX = (x << Av1Constants.SubpelBits) + ((2 * mvCol) >> subX) + HalfSample;
            long origY = (y << Av1Constants.SubpelBits) + ((2 * mvRow) >> subY) + HalfSample;
            var baseX = (origX * xScale) - (HalfSample << Av1Constants.RefScaleShift);
            var baseY = (origY * yScale) - (HalfSample << Av1Constants.RefScaleShift);
            startX = (int)Round2Signed(baseX, Shift) + Off;
            startY = (int)Round2Signed(baseY, Shift) + Off;
            stepX = (int)Round2Signed(xScale, Av1Constants.RefScaleShift - Av1Constants.ScaleSubpelBits);
            stepY = (int)Round2Signed(yScale, Av1Constants.RefScaleShift - Av1Constants.ScaleSubpelBits);
        }
    }
}

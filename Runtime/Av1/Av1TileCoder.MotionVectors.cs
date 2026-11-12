#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;

namespace AvifDecoding
{
    /// <content>
    /// The prediction of the motion vectors of a block from its neighbors and from the motion field of the frame
    /// (AV1 specification, sections 7.10.2 "Find MV stack process", 7.10.3 "Has overlappable candidates process" and 7.10.4 "Find
    /// warp samples process").
    /// </content>
    internal ref partial struct Av1TileCoder
    {
        // Section 7.10.2 "Find MV stack process", for an intra block copy. The matches and the new motion vectors that the
        // specification counts only give the contexts of the inter modes, which an intra block copy does not read
        private void FindMvStack()
        {
            var bw4 = Av1Tables.Num4x4BlocksWide[_miSize];
            var bh4 = Av1Tables.Num4x4BlocksHigh[_miSize];
            _numMvFound = 0;
            SetupGlobalMv(0);
            ScanRow(-1, false);
            ScanCol(-1, false);
            if (Math.Max(bw4, bh4) <= 16)
                ScanPoint(-1, bw4, false);

            var numNearest = _numMvFound;
            for (var idx = 0; idx < numNearest; idx++)
            {
                _weightStack[idx] += Av1Constants.RefCatLevel;
            }

            ScanPoint(-1, -1, false);
            ScanRow(-3, false);
            ScanCol(-3, false);
            if (bh4 > 1)
                ScanRow(-5, false);

            if (bw4 > 1)
                ScanCol(-5, false);

            SortStack(0, numNearest);
            SortStack(numNearest, _numMvFound);
            if (_numMvFound < 2)
                ExtraSearch();

            ContextAndClamping();
        }

        // Section 7.10.2.1 "Setup global MV process": the global motion of a key frame is the identity, so its motion vectors
        // are zero
        private void SetupGlobalMv(int refList)
        {
            _globalMvs[refList * 2] = 0;
            _globalMvs[(refList * 2) + 1] = 0;
        }

        private static long Round2Signed(long value, int bits) => value >= 0 ? (value + (1L << (bits - 1))) >> bits : -((-value + (1L << (bits - 1))) >> bits);

        // Section 7.10.2.2 "Scan row process"
        private void ScanRow(int deltaRow, bool isCompound)
        {
            int bw4 = Av1Tables.Num4x4BlocksWide[_miSize];
            var end4 = Math.Min(Math.Min(bw4, _miCols - _miCol), 16);
            var deltaCol = 0;
            var useStep16 = bw4 >= 16;
            var far = Math.Abs(deltaRow) > 1;
            if (far)
            {
                deltaRow += _miRow & 1;
                deltaCol = 1 - (_miCol & 1);
            }

            var i = 0;
            while (i < end4)
            {
                var mvRow = _miRow + deltaRow;
                var mvCol = _miCol + deltaCol + i;
                if (!IsInside(mvRow, mvCol))
                    break;

                var len = Math.Min(bw4, Av1Tables.Num4x4BlocksWide[_miSizes[MiIndex(mvRow, mvCol)]]);
                if (far)
                    len = Math.Max(2, len);

                if (useStep16)
                    len = Math.Max(4, len);

                AddRefMvCandidate(mvRow, mvCol, false, len * 2);
                i += len;
            }
        }

        // Section 7.10.2.3 "Scan col process"
        private void ScanCol(int deltaCol, bool isCompound)
        {
            int bh4 = Av1Tables.Num4x4BlocksHigh[_miSize];
            var end4 = Math.Min(Math.Min(bh4, _miRows - _miRow), 16);
            var deltaRow = 0;
            var useStep16 = bh4 >= 16;
            var far = Math.Abs(deltaCol) > 1;
            if (far)
            {
                deltaRow = 1 - (_miRow & 1);
                deltaCol += _miCol & 1;
            }

            var i = 0;
            while (i < end4)
            {
                var mvRow = _miRow + deltaRow + i;
                var mvCol = _miCol + deltaCol;
                if (!IsInside(mvRow, mvCol))
                    break;

                var len = Math.Min(bh4, Av1Tables.Num4x4BlocksHigh[_miSizes[MiIndex(mvRow, mvCol)]]);
                if (far)
                    len = Math.Max(2, len);

                if (useStep16)
                    len = Math.Max(4, len);

                AddRefMvCandidate(mvRow, mvCol, false, len * 2);
                i += len;
            }
        }

        // Section 7.10.2.4 "Scan point process"
        private void ScanPoint(int deltaRow, int deltaCol, bool isCompound)
        {
            var mvRow = _miRow + deltaRow;
            var mvCol = _miCol + deltaCol;
            if (IsInside(mvRow, mvCol) && _refFrames[MiIndex(mvRow, mvCol) * 2] is not Av1FrameState.RefFrameNotWritten)
                AddRefMvCandidate(mvRow, mvCol, false, weight: 4);
        }

        // Adds a candidate to RefStackMv, or adds its weight to the one of the entry that holds the same motion vectors
        private void AddToStack(int row0, int col0, int row1, int col1, bool isCompound, int weight)
        {
            for (var idx = 0; idx < _numMvFound; idx++)
            {
                var entry = _refStackMv[idx];
                if (row0 == entry[0] && col0 == entry[1])
                {
                    _weightStack[idx] += weight;
                    return;
                }
            }

            if (_numMvFound < Av1Constants.MaxRefMvStackSize)
            {
                var entry = _refStackMv[_numMvFound];
                entry[0] = row0;
                entry[1] = col0;
                entry[2] = row1;
                entry[3] = col1;
                _refStackMv[_numMvFound] = entry;
                _weightStack[_numMvFound] = weight;
                _numMvFound++;
            }
        }

        // Section 7.10.2.7 "Add reference motion vector process"
        private void AddRefMvCandidate(int mvRow, int mvCol, bool isCompound, int weight)
        {
            var index = MiIndex(mvRow, mvCol);
            if ((_flags[index] & Av1FrameState.IsInterFlag) is 0)
                return;

            {
                for (var candList = 0; candList < 2; candList++)
                {
                    if (_refFrames[(index * 2) + candList] == _refFrame0)
                        SearchStack(index, candList, weight);
                }
            }
        }

        // Section 7.10.2.8 "Search stack process"
        private void SearchStack(int index, int candList, int weight)
        {
            int candMode = _yModes[index];
            int candSize = _miSizes[index];
            var large = Math.Min(BlockWidth(candSize), BlockHeight(candSize)) >= 8;
            int candRow;
            int candCol;
            {
                candRow = _mvs[(index * 4) + (candList * 2)];
                candCol = _mvs[(index * 4) + (candList * 2) + 1];
            }

            LowerMvPrecision(ref candRow, ref candCol);
            AddToStack(candRow, candCol, 0, 0, isCompound: false, weight);
        }

        // Section 7.10.2.10 "Lower precision process"
        private readonly void LowerMvPrecision(ref int row, ref int col)
        {

            row = Lower(row, _header.ForceIntegerMv);
            col = Lower(col, _header.ForceIntegerMv);

            static int Lower(int value, bool forceIntegerMv)
            {
                if (forceIntegerMv)
                {
                    var aInt = (Math.Abs(value) + 3) >> 3;
                    return value > 0 ? aInt << 3 : -(aInt << 3);
                }

                if ((value & 1) is not 0)
                    return value > 0 ? value - 1 : value + 1;

                return value;
            }
        }

        // Section 7.10.2.11 "Sorting process"
        private void SortStack(int start, int end)
        {
            while (end > start)
            {
                var newEnd = start;
                for (var idx = start + 1; idx < end; idx++)
                {
                    if (_weightStack[idx - 1] < _weightStack[idx])
                    {
                        (_weightStack[idx - 1], _weightStack[idx]) = (_weightStack[idx], _weightStack[idx - 1]);
                        (_refStackMv[idx - 1], _refStackMv[idx]) = (_refStackMv[idx], _refStackMv[idx - 1]);
                        newEnd = idx;
                    }
                }

                end = newEnd;
            }
        }

        // Section 7.10.2.12 "Extra search process", for a key frame: every neighbour refers to the frame itself, so none adds a
        // candidate, and the stack is completed with the global motion vector, which is zero
        private void ExtraSearch()
        {
            for (var idx = _numMvFound; idx < 2; idx++)
            {
                _refStackMv.Set(idx, 0, _globalMvs[0]);
                _refStackMv.Set(idx, 1, _globalMvs[1]);
            }
        }

        // Section 7.10.2.14 "Context and clamping process"
        private void ContextAndClamping()
        {
            // The contexts of the inter modes are not needed: an intra block copy has no mode to read
            var bw = BlockWidth(_miSize);
            var bh = BlockHeight(_miSize);
            for (var idx = 0; idx < _numMvFound; idx++)
            {
                var entry = _refStackMv[idx];
                entry[0] = ClampMvRow(entry[0], Av1Constants.MvBorder + (bh * 8));
                entry[1] = ClampMvCol(entry[1], Av1Constants.MvBorder + (bw * 8));
                _refStackMv[idx] = entry;
            }
        }

        // Section 5.11.53 "Clamp MV row function"
        private readonly int ClampMvRow(int mvec, int border)
        {
            var bh4 = Av1Tables.Num4x4BlocksHigh[_miSize];
            var mbToTopEdge = -(_miRow * Av1Constants.MiSize * 8);
            var mbToBottomEdge = (_miRows - bh4 - _miRow) * Av1Constants.MiSize * 8;
            return Math.Clamp(mvec, mbToTopEdge - border, mbToBottomEdge + border);
        }

        // Section 5.11.54 "Clamp MV col function"
        private readonly int ClampMvCol(int mvec, int border)
        {
            var bw4 = Av1Tables.Num4x4BlocksWide[_miSize];
            var mbToLeftEdge = -(_miCol * Av1Constants.MiSize * 8);
            var mbToRightEdge = (_miCols - bw4 - _miCol) * Av1Constants.MiSize * 8;
            return Math.Clamp(mvec, mbToLeftEdge - border, mbToRightEdge + border);
        }
    }
}

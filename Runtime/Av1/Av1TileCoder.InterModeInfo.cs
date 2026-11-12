#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;

namespace AvifDecoding
{
    /// <content>
    /// The mode info of a block that is predicted from another frame, or from the frame itself with intra block copy
    /// (AV1 specification, sections 5.11.10, 5.11.17 to 5.11.23 and 5.11.25 to 5.11.33).
    /// </content>
    internal ref partial struct Av1TileCoder
    {
        /// <summary>The value of <c>RefFrame[ 1 ]</c> for a block with one reference (<c>NONE</c>).</summary>
        private const int NoReference = -1;

        // Section 5.11.26 "Assign MV syntax"
        private void AssignMv(bool isCompound)
        {
            for (var i = 0; i < (1); i++)
            {
                var compMode = _useIntrabc ? Av1Constants.Newmv : GetMode(i);
                if (_useIntrabc)
                {
                    SetIntrabcPredMv();
                }
                else
                {
                    SetPredMv(i, compMode);
                }

                if (compMode is Av1Constants.Newmv)
                {
                    ReadMv(i);
                }
                else
                {
                    _mv[i * 2] = _predMv[i * 2];
                    _mv[(i * 2) + 1] = _predMv[(i * 2) + 1];
                }
            }

            if (!IsMvValid(false))
                throw Av1Errors.Invalid("a motion vector is out of range.");
        }

        // PredMv of a block that is coded with intra block copy (section 5.11.26): the first motion vector of the stack that
        // is not zero, or else the superblock above, or the one to the left that the delay allows in the first row of a tile
        private void SetIntrabcPredMv()
        {
            _predMv[0] = _refStackMv[0][0];
            _predMv[1] = _refStackMv[0][1];
            if (_predMv[0] is 0 && _predMv[1] is 0)
            {
                _predMv[0] = _refStackMv[1][0];
                _predMv[1] = _refStackMv[1][1];
            }

            if (_predMv[0] is 0 && _predMv[1] is 0)
            {
                var sbSize4 = _sequence.Use128x128Superblock ? 32 : 16;
                if (_miRow - sbSize4 < _miRowStart)
                {
                    _predMv[0] = 0;
                    _predMv[1] = -((sbSize4 * Av1Constants.MiSize) + Av1Constants.IntrabcDelayPixels) * 8;
                }
                else
                {
                    _predMv[0] = -(sbSize4 * Av1Constants.MiSize * 8);
                    _predMv[1] = 0;
                }
            }
        }

        // PredMv of a reference list of a block that is not coded with intra block copy (section 5.11.26)
        private void SetPredMv(int refList, int compMode)
        {
            if (compMode is Av1Constants.Globalmv)
            {
                _predMv[refList * 2] = _globalMvs[refList * 2];
                _predMv[(refList * 2) + 1] = _globalMvs[(refList * 2) + 1];
                return;
            }

            var pos = compMode is Av1Constants.Nearestmv ? 0 : _refMvIdx;
            if (compMode is Av1Constants.Newmv && _numMvFound <= 1)
                pos = 0;

            _predMv[refList * 2] = _refStackMv[pos][refList * 2];
            _predMv[(refList * 2) + 1] = _refStackMv[pos][(refList * 2) + 1];
        }

        // The is_mv_valid function of section 6.10.25 "Assign mv semantics"
        private readonly bool IsMvValid(bool isCompound)
        {
            for (var i = 0; i < (2); i++)
            {
                if (Math.Abs(_mv[i]) >= 1 << 14)
                    return false;
            }

            if (!_useIntrabc)
                return true;

            var bw = BlockWidth(_miSize);
            var bh = BlockHeight(_miSize);
            if ((_mv[0] & 7) is not 0 || (_mv[1] & 7) is not 0)
                return false;

            var deltaRow = _mv[0] >> 3;
            var deltaCol = _mv[1] >> 3;
            var srcTopEdge = (_miRow * Av1Constants.MiSize) + deltaRow;
            var srcLeftEdge = (_miCol * Av1Constants.MiSize) + deltaCol;
            var srcBottomEdge = srcTopEdge + bh;
            var srcRightEdge = srcLeftEdge + bw;
            if (_hasChroma)
            {
                if (bw < 8 && _subX is 1)
                    srcLeftEdge -= 4;

                if (bh < 8 && _subY is 1)
                    srcTopEdge -= 4;
            }

            if (srcTopEdge < _miRowStart * Av1Constants.MiSize
                || srcLeftEdge < _miColStart * Av1Constants.MiSize
                || srcBottomEdge > _miRowEnd * Av1Constants.MiSize
                || srcRightEdge > _miColEnd * Av1Constants.MiSize)
            {
                return false;
            }

            var use128 = _sequence.Use128x128Superblock ? 1 : 0;
            var sbH = _sequence.Use128x128Superblock ? 128 : 64;
            var activeSbRow = _miRow * Av1Constants.MiSize / sbH;
            var activeSb64Col = (_miCol * Av1Constants.MiSize) >> 6;
            var srcSbRow = (srcBottomEdge - 1) / sbH;
            var srcSb64Col = (srcRightEdge - 1) >> 6;
            var totalSb64PerRow = ((_miColEnd - _miColStart - 1) >> 4) + 1;
            var activeSb64 = (activeSbRow * totalSb64PerRow) + activeSb64Col;
            var srcSb64 = (srcSbRow * totalSb64PerRow) + srcSb64Col;
            if (srcSb64 >= activeSb64 - Av1Constants.IntrabcDelaySb64)
                return false;

            var gradient = 1 + Av1Constants.IntrabcDelaySb64 + use128;
            var wfOffset = gradient * (activeSbRow - srcSbRow);
            return srcSbRow <= activeSbRow && srcSb64Col < activeSb64Col - Av1Constants.IntrabcDelaySb64 + wfOffset;
        }

        // Section 5.11.29 "Get mode function"
        private readonly int GetMode(int refList)
        {
            if (refList is 0)
            {
                return _yMode switch
                {
                    < Av1Constants.NearestNearestmv => _yMode,
                    Av1Constants.NewNewmv or Av1Constants.NewNearestmv or Av1Constants.NewNearmv => Av1Constants.Newmv,
                    Av1Constants.NearestNearestmv or Av1Constants.NearestNewmv => Av1Constants.Nearestmv,
                    Av1Constants.NearNearmv or Av1Constants.NearNewmv => Av1Constants.Nearmv,
                    _ => Av1Constants.Globalmv,
                };
            }

            return _yMode switch
            {
                Av1Constants.NewNewmv or Av1Constants.NearestNewmv or Av1Constants.NearNewmv => Av1Constants.Newmv,
                Av1Constants.NearestNearestmv or Av1Constants.NewNearestmv => Av1Constants.Nearestmv,
                Av1Constants.NearNearmv or Av1Constants.NewNearmv => Av1Constants.Nearmv,
                _ => Av1Constants.Globalmv,
            };
        }

        // Section 5.11.31 "MV syntax"
        private void ReadMv(int refList)
        {
            var mvCtx = _useIntrabc ? Av1Constants.MvIntrabcContext : 0;
            var diffRow = 0;
            var diffCol = 0;
            var mvJoint = ReadSymbol(Av1CdfLayout.MvJoint + (mvCtx * Av1CdfLayout.MvJointSize), Av1CdfLayout.MvJointSize);
            if (mvJoint is Av1Constants.MvJointHzvnz or Av1Constants.MvJointHnzvnz)
                diffRow = ReadMvComponent(mvCtx, comp: 0);

            if (mvJoint is Av1Constants.MvJointHnzvz or Av1Constants.MvJointHnzvnz)
                diffCol = ReadMvComponent(mvCtx, comp: 1);

            _mv[refList * 2] = _predMv[refList * 2] + diffRow;
            _mv[(refList * 2) + 1] = _predMv[(refList * 2) + 1] + diffCol;
        }

        // Section 5.11.32 "MV component syntax"
        private readonly int ReadMvComponent(int mvCtx, int comp)
        {
            var context = (mvCtx * 2) + comp;
            var mvSign = ReadSymbol(Av1CdfLayout.MvSign + (context * Av1CdfLayout.MvSignSize), Av1CdfLayout.MvSignSize);
            var mvClass = ReadSymbol(Av1CdfLayout.MvClass + (context * Av1CdfLayout.MvClassSize), Av1CdfLayout.MvClassSize);
            int mag;
            if (mvClass is Av1Constants.MvClass0)
            {
                var mvClass0Bit = ReadSymbol(Av1CdfLayout.MvClass0Bit + (context * Av1CdfLayout.MvClass0BitSize), Av1CdfLayout.MvClass0BitSize);
                var mvClass0Fr = 3;
                if (!_header.ForceIntegerMv)
                    mvClass0Fr = ReadSymbol(Av1CdfLayout.MvClass0Fr + (((context * Av1Constants.Class0Size) + mvClass0Bit) * Av1CdfLayout.MvClass0FrSize), Av1CdfLayout.MvClass0FrSize);

                var mvClass0Hp = 1;

                mag = ((mvClass0Bit << 3) | (mvClass0Fr << 1) | mvClass0Hp) + 1;
            }
            else
            {
                var d = 0;
                for (var i = 0; i < mvClass; i++)
                {
                    var mvBit = ReadSymbol(Av1CdfLayout.MvBit + (((context * Av1Constants.MvOffsetBits) + i) * Av1CdfLayout.MvBitSize), Av1CdfLayout.MvBitSize);
                    d |= mvBit << i;
                }

                mag = Av1Constants.Class0Size << (mvClass + 2);
                var mvFr = 3;
                if (!_header.ForceIntegerMv)
                    mvFr = ReadSymbol(Av1CdfLayout.MvFr + (context * Av1CdfLayout.MvFrSize), Av1CdfLayout.MvFrSize);

                var mvHp = 1;

                mag += ((d << 3) | (mvFr << 1) | mvHp) + 1;
            }

            return mvSign is not 0 ? -mag : mag;
        }

        private readonly int GetRelativeDistance(int a, int b) => Av1FrameHeaderParser.GetRelativeDistance(_sequence, a, b);

        // Section 5.11.17 "Var TX size syntax"
        private void ReadVarTxSize(int row, int col, int txSz, int depth)
        {
            if (row >= _miRows || col >= _miCols)
                return;

            var txfmSplit = false;
            if (txSz is not Av1Constants.Tx4x4 && depth is not Av1Constants.MaxVartxDepth)
            {
                txfmSplit = ReadSymbol(Av1CdfLayout.TxfmSplit + (GetTxfmSplitContext(row, col, txSz) * Av1CdfLayout.TxfmSplitSize), Av1CdfLayout.TxfmSplitSize) is not 0;
            }

            var w4 = Av1Tables.TxWidth[txSz] / Av1Constants.MiSize;
            var h4 = Av1Tables.TxHeight[txSz] / Av1Constants.MiSize;
            if (txfmSplit)
            {
                var subTxSz = Av1Tables.SplitTxSize[txSz];
                var stepW = Av1Tables.TxWidth[subTxSz] / Av1Constants.MiSize;
                var stepH = Av1Tables.TxHeight[subTxSz] / Av1Constants.MiSize;
                for (var i = 0; i < h4; i += stepH)
                {
                    for (var j = 0; j < w4; j += stepW)
                    {
                        ReadVarTxSize(row + i, col + j, subTxSz, depth + 1);
                    }
                }
            }
            else
            {
                var width = Math.Min(w4, _miCols - col);
                var height = Math.Min(h4, _miRows - row);
                for (var i = 0; i < height; i++)
                {
                    _interTxSizes.Slice(MiIndex(row + i, col), width).Fill((byte)txSz);
                }

                _txSize = txSz;
            }
        }

        // The CDF selection of txfm_split (section 8.3.2)
        private readonly int GetTxfmSplitContext(int row, int col, int txSz)
        {
            var above = GetAboveTxWidth(row, col) < Av1Tables.TxWidth[txSz] ? 1 : 0;
            var left = GetLeftTxHeight(row, col) < Av1Tables.TxHeight[txSz] ? 1 : 0;
            var size = Math.Min(64, Math.Max(BlockWidth(_miSize), BlockHeight(_miSize)));
            var maxTxSz = FindTxSize(size, size);
            var txSzSqrUp = Av1Tables.TxSizeSqrUp[txSz];
            return ((txSzSqrUp != maxTxSz ? 1 : 0) * 3) + ((Av1Constants.TxSizes - 1 - maxTxSz) * 6) + above + left;
        }

        // The find_tx_size function of section 5.11.36 "Transform tree syntax"
        private static int FindTxSize(int w, int h)
        {
            var txSz = 0;
            for (; txSz < Av1Constants.TxSizesAll; txSz++)
            {
                if (Av1Tables.TxWidth[txSz] == w && Av1Tables.TxHeight[txSz] == h)
                    break;
            }

            return txSz;
        }

        // Section 5.11.36 "Transform tree syntax"
        private void TransformTree(ResidualOperation operation, int startX, int startY, int w, int h)
        {
            var maxX = _miCols * Av1Constants.MiSize;
            var maxY = _miRows * Av1Constants.MiSize;
            if (startX >= maxX || startY >= maxY)
                return;

            var row = startY >> Av1Constants.MiSizeLog2;
            var col = startX >> Av1Constants.MiSizeLog2;
            var lumaTxSz = _interTxSizes[MiIndex(row, col)];
            var lumaW = Av1Tables.TxWidth[lumaTxSz];
            var lumaH = Av1Tables.TxHeight[lumaTxSz];
            if (w <= lumaW && h <= lumaH)
            {
                var txSz = FindTxSize(w, h);
                switch (operation)
                {
                    case ResidualOperation.Decode:
                        TransformBlock(0, startX, startY, txSz, 0, 0);
                        break;
                    default:
                        throw new InvalidOperationException("Only decoding is ported.");
                }
            }
            else if (w > h)
            {
                TransformTree(operation, startX, startY, w / 2, h);
                TransformTree(operation, startX + (w / 2), startY, w / 2, h);
            }
            else if (w < h)
            {
                TransformTree(operation, startX, startY, w, h / 2);
                TransformTree(operation, startX, startY + (h / 2), w, h / 2);
            }
            else
            {
                TransformTree(operation, startX, startY, w / 2, h / 2);
                TransformTree(operation, startX + (w / 2), startY, w / 2, h / 2);
                TransformTree(operation, startX, startY + (h / 2), w / 2, h / 2);
                TransformTree(operation, startX + (w / 2), startY + (h / 2), w / 2, h / 2);
            }
        }
    }
}

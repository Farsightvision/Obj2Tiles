// C# port of Sean Barrett's stb_rect_pack skyline-bottom-left packer
// (public domain, github.com/nothings/stb/blob/master/stb_rect_pack.h).

using Obj2Tiles.Library.Algos.Model;
using Rectangle = Obj2Tiles.Library.Algos.Model.Rectangle;

namespace Obj2Tiles.Library.Algos
{
    /// <summary>
    /// Skyline-bottom-left bin packer (no rotation). Insert returns a placed
    /// rectangle, or Width=0 on failure.
    /// </summary>
    public class SkylineBinPack
    {
        public int binWidth;
        public int binHeight;

        private struct Node
        {
            public int X;
            public int Y;
            public int Width;
        }

        private readonly List<Node> _skyline = new();

        public SkylineBinPack(int width, int height)
        {
            binWidth = width;
            binHeight = height;
            _skyline.Clear();
            _skyline.Add(new Node { X = 0, Y = 0, Width = width });
        }

        /// <summary>Inserts a rectangle; returns it placed, or Width=0 on no fit.</summary>
        public Rectangle Insert(int rectWidth, int rectHeight)
        {
            int bestY = int.MaxValue;
            int bestX = -1;
            int bestIdx = -1;
            int bestWidthRem = int.MaxValue;

            for (int i = 0; i < _skyline.Count; i++)
            {
                int y = FitY(i, rectWidth);
                if (y < 0) continue;
                if (y + rectHeight > binHeight) continue;
                int x = _skyline[i].X;
                if (x + rectWidth > binWidth) continue;
                int widthRem = _skyline[i].Width - rectWidth;
                if (y < bestY || (y == bestY && (x < bestX || (x == bestX && widthRem < bestWidthRem))))
                {
                    bestY = y;
                    bestX = x;
                    bestIdx = i;
                    bestWidthRem = widthRem;
                }
            }

            if (bestIdx < 0)
            {
                return new Rectangle { X = 0, Y = 0, Width = 0, Height = 0 };
            }

            int placedTop = bestY + rectHeight;
            int rightEnd = bestX + rectWidth;
            int idx = bestIdx;
            int newSegW = rectWidth;
            // Drop segments fully under the rect; trim the one straddling its right edge.
            int remaining = rectWidth;
            int newIdx = idx;
            while (remaining > 0 && idx < _skyline.Count)
            {
                var seg = _skyline[idx];
                if (seg.Width <= remaining)
                {
                    remaining -= seg.Width;
                    _skyline.RemoveAt(idx);
                }
                else
                {
                    _skyline[idx] = new Node { X = seg.X + remaining, Y = seg.Y, Width = seg.Width - remaining };
                    remaining = 0;
                }
            }
            _skyline.Insert(newIdx, new Node { X = bestX, Y = placedTop, Width = newSegW });

            MergeSkyline();

            return new Rectangle { X = bestX, Y = bestY, Width = rectWidth, Height = rectHeight };
        }

        /// <summary>Y the rect rests on if anchored at segIdx (max Y under its span), or -1 if it won't fit.</summary>
        private int FitY(int segIdx, int rectWidth)
        {
            var seg = _skyline[segIdx];
            int x = seg.X;
            if (x + rectWidth > binWidth) return -1;
            int y = seg.Y;
            int widthLeft = rectWidth;
            int i = segIdx;
            while (widthLeft > 0 && i < _skyline.Count)
            {
                var s = _skyline[i];
                if (s.Y > y) y = s.Y;
                widthLeft -= s.Width;
                i++;
            }
            if (widthLeft > 0) return -1;
            return y;
        }

        private void MergeSkyline()
        {
            for (int i = 0; i < _skyline.Count - 1;)
            {
                if (_skyline[i].Y == _skyline[i + 1].Y)
                {
                    _skyline[i] = new Node { X = _skyline[i].X, Y = _skyline[i].Y, Width = _skyline[i].Width + _skyline[i + 1].Width };
                    _skyline.RemoveAt(i + 1);
                }
                else
                {
                    i++;
                }
            }
        }
    }
}

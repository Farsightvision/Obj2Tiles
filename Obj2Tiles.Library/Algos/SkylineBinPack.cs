/*
 * SkylineBinPack: a deterministic skyline-bottom-left bin packer.
 *
 * C# port of Sean Barrett's stb_rect_pack algorithm
 * (public domain, github.com/nothings/stb/blob/master/stb_rect_pack.h).
 * Used as the fast-path packer in MeshT.TryPackClusterInfos when the cluster
 * count exceeds HighClusterFastPathThreshold (≈ 1000). MaxRectanglesBinPack's
 * RectangleBestAreaFit heuristic is O(N²) per Insert and stalls on
 * highdetailed-class tiles with 10k+ UV islands. The skyline algorithm is
 * O(N × W_avg) per insert where W_avg is the average skyline segment width;
 * for N=11767 this completes in seconds rather than minutes.
 *
 * Algorithm (skyline-bottom-left, stb_rect_pack default):
 *   - Maintain a sorted list of "skyline" segments (x, y, width) tracking the
 *     top horizontal edge of the packed region.
 *   - For each rectangle, scan the skyline left-to-right; for every possible
 *     x position (segment boundaries), find the highest y under the
 *     [x, x+rectW] span. Pick the position with the LOWEST top — ties broken
 *     by leftmost.
 *   - Place the rectangle there, then merge consecutive same-height segments.
 *
 * Compared to MaxRects: slightly lower packing efficiency (~5-10% worse on
 * varied-size sets), but consistent O(N × log N) for typical inputs, and no
 * pathological free-rect-list growth on heterogeneous large N.
 */

using Obj2Tiles.Library.Algos.Model;
using Rectangle = Obj2Tiles.Library.Algos.Model.Rectangle;

namespace Obj2Tiles.Library.Algos
{
    /// <summary>
    /// Skyline-bottom-left bin packer. Fast deterministic O(N × W_avg) Insert
    /// for high-N use cases. No rotation support; matches the MaxRectangles
    /// API surface used by TryPackClusterInfos (Insert returns a placed
    /// rectangle, Width=0 on failure).
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

        /// <summary>
        /// Insert a rectangle of the given width/height into the bin. Returns
        /// the placed rectangle (X, Y, Width, Height). On failure (no fit),
        /// returns a rectangle with Width=0. Matches MaxRectanglesBinPack.Insert's
        /// no-rotation, no-heuristic-arg overload (only one heuristic available
        /// here: skyline-bottom-left).
        /// </summary>
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
                // Pick lowest Y, then leftmost X, then narrowest segment-remainder
                // (best-fit tie-breaker per stb_rect_pack heuristic).
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

            // Place rectangle at (bestX, bestY) and update skyline.
            // Replace the affected segments with a new segment at the top of the rect.
            int placedTop = bestY + rectHeight;
            // Find the slice of skyline segments covered by [bestX, bestX+rectWidth].
            int rightEnd = bestX + rectWidth;
            // Replace segments at bestIdx..rightEndIdx with a new single segment.
            int idx = bestIdx;
            int newSegW = rectWidth;
            // The new segment starts at bestX with width rectWidth, top placedTop.
            // We must remove any segments fully covered and adjust the segment
            // straddling the right edge (it keeps the portion past rectWidth).
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
                    // The segment overlaps; trim its left side by `remaining`.
                    _skyline[idx] = new Node { X = seg.X + remaining, Y = seg.Y, Width = seg.Width - remaining };
                    remaining = 0;
                }
            }
            // Insert the new segment at newIdx (the original bestIdx position).
            _skyline.Insert(newIdx, new Node { X = bestX, Y = placedTop, Width = newSegW });

            // Merge adjacent segments with the same Y.
            MergeSkyline();

            return new Rectangle { X = bestX, Y = bestY, Width = rectWidth, Height = rectHeight };
        }

        /// <summary>
        /// Given a candidate skyline segment index, return the Y the rectangle
        /// will rest on if anchored at that segment's left edge — the MAX Y of
        /// all segments under the rect's [X, X+rectWidth] span. Returns -1 if
        /// the rectangle would extend past the right edge of the bin.
        /// </summary>
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
            if (widthLeft > 0) return -1; // not enough skyline coverage
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

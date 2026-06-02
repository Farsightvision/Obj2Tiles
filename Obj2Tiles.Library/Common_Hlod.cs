using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library;

/// <summary>
/// HLOD-only image utilities. Used exclusively by <see cref="Obj2Tiles.Library.Geometry.MeshT_Hlod"/>
/// and the hierarchical pipeline. Kept separate from <see cref="Common"/> so the legacy
/// (master-equivalent) pipeline runs against an unmodified Common.cs.
/// </summary>
public static class Common_Hlod
{
    // Perf telemetry (NOT dead — feeds the [perf:hlod:DilateMs] line in HierarchicalTilingStage):
    public static long DilateTicks; // total CPU time (Stopwatch ticks) summed across tiles in DilateAtlasBleed
    /// <summary>
    /// Dilate non-empty atlas pixels into surrounding empty (alpha=0 or RGB=000)
    /// regions, by `bleed` pixels. Used after all UV clusters are packed into the atlas
    /// to prevent bilinear filtering at cluster edges from sampling the empty atlas
    /// background — which renders as dark fringes along every triangle edge that
    /// touches a cluster boundary, manifesting as visible "cracks" at tile boundaries.
    ///
    /// Algorithm: repeated jump-flood-style dilation. In each pass, every empty pixel
    /// adjacent to a non-empty pixel inherits one of those neighbours' colours.
    /// After `bleed` passes, all empty pixels within `bleed` of a cluster have valid
    /// colours.
    ///
    /// Performance: O(W * H * bleed). For a 4096² atlas with bleed=4, ~67M reads —
    /// runs in under a second per atlas with the in-place implementation.
    /// </summary>
    public static void DilateAtlasBleed(Image<Rgba32> atlas, int bleed)
    {
        if (bleed <= 0) return;
        var _dt0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int w = atlas.Width, h = atlas.Height;
        var current = new Rgba32[w * h];
        atlas.CopyPixelDataTo(MemoryMarshal.Cast<Rgba32, byte>(current.AsSpan()));

        // G6 WIN: frontier/BFS bleed. Touches only the growing band (one O(W*H) seed scan + `bleed` waves
        // over the frontier) — 5–8× faster than the old 16-pass full-buffer ping-pong it replaced (total
        // small2 1.14× / hd 1.11× / vlrg 1.49×). Fills the same set (empty px within `bleed` Chebyshev of
        // non-empty), preserving the bilinear boundary-fringe guarantee.
        current = DilateFrontier(current, w, h, bleed);

        atlas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                    row[x] = current[y * w + x];
            }
        });
        Interlocked.Add(ref DilateTicks, System.Diagnostics.Stopwatch.GetTimestamp() - _dt0);
    }

    // Multi-source BFS from the non-empty boundary: one O(W*H) scan seeds the boundary
    // frontier, then `bleed` waves expand ONLY the frontier (the thin growing band) — no
    // per-pass full copy, no repeated full scans. Fills px in place; returns it. Avoids
    // within-pass over-propagation: a pixel is marked filled the instant it is claimed and
    // queued to the NEXT wave, so it cannot re-propagate in the wave that filled it.
    private static Rgba32[] DilateFrontier(Rgba32[] px, int w, int h, int bleed)
    {
        var filled = new bool[w * h];
        for (int i = 0; i < px.Length; i++) filled[i] = !IsEmpty(px[i]);

        var frontier = new List<int>();
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                if (!filled[idx]) continue;
                bool boundary = false;
                for (int dy = -1; dy <= 1 && !boundary; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        if (!filled[ny * w + nx]) { boundary = true; break; }
                    }
                }
                if (boundary) frontier.Add(idx);
            }
        }

        var next = new List<int>();
        for (int pass = 0; pass < bleed && frontier.Count > 0; pass++)
        {
            next.Clear();
            foreach (int idx in frontier)
            {
                int y = idx / w, x = idx - y * w;
                var color = px[idx];
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    int nRow = ny * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        int nidx = nRow + nx;
                        if (filled[nidx]) continue;
                        px[nidx] = color;
                        filled[nidx] = true;
                        next.Add(nidx);
                    }
                }
            }
            (frontier, next) = (next, frontier);
        }
        return px;
    }

    private static bool IsEmpty(Rgba32 c)
    {
        return c.A == 0 || (c.R == 0 && c.G == 0 && c.B == 0);
    }

    /// <summary>
    /// Copy a source rectangle into a destination rectangle of possibly-different size.
    /// When src and dest dimensions match, falls through to <see cref="Common.CopyImage"/>.
    /// Otherwise crops the source region and resizes it to fit dest (used by
    /// <see cref="Obj2Tiles.Library.Geometry.MeshT_Hlod.FillAtlases"/> when the natural
    /// per-tile atlas would exceed the per-depth cap and PackedRects have been scaled down).
    /// </summary>
    public static void CopyImageScaled(Image<Rgba32> sourceImage, Image<Rgba32> dest,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        int destX, int destY, int destWidth, int destHeight)
    {
        if (sourceWidth == destWidth && sourceHeight == destHeight)
        {
            Common.CopyImage(sourceImage, dest, sourceX, sourceY, sourceWidth, sourceHeight, destX, destY);
            return;
        }
        int sx = Math.Max(0, sourceX);
        int sy = Math.Max(0, sourceY);
        int sw = Math.Min(sourceWidth, sourceImage.Width - sx);
        int sh = Math.Min(sourceHeight, sourceImage.Height - sy);
        if (sw <= 0 || sh <= 0) return;
        using var sub = sourceImage.Clone(ctx => ctx
            .Crop(new Rectangle(sx, sy, sw, sh))
            .Resize(destWidth, destHeight));
        Common.CopyImage(sub, dest, 0, 0, destWidth, destHeight, destX, destY);
    }
}

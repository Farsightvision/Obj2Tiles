using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library;

/// <summary>HLOD-only image utilities for the hierarchical pipeline.</summary>
public static class Common_Hlod
{
    public static long DilateTicks;
    /// <summary>
    /// Dilate atlas pixels by `bleed` into surrounding empty regions so bilinear
    /// filtering at cluster edges never samples the empty background, which would
    /// otherwise render as dark fringes (visible cracks at tile boundaries).
    /// </summary>
    public static void DilateAtlasBleed(Image<Rgba32> atlas, int bleed)
    {
        if (bleed <= 0) return;
        var _dt0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int w = atlas.Width, h = atlas.Height;
        var current = new Rgba32[w * h];
        atlas.CopyPixelDataTo(MemoryMarshal.Cast<Rgba32, byte>(current.AsSpan()));

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

    // Multi-source BFS from the non-empty boundary, expanding the frontier one wave per
    // bleed pass. A pixel is marked filled when claimed and queued to the next wave, so it
    // cannot re-propagate within the wave that filled it.
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
    /// Copy a source rectangle into a possibly-different-sized destination rectangle,
    /// cropping and resizing the source when dimensions differ.
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

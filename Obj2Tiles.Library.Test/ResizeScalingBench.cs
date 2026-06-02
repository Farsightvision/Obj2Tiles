using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library.Test;

// THROWAWAY premise-measurement for TRACK-1-ALGO-RETHINK (Phase 1).
// Q: does ImageSharp bicubic downsample cost scale with SOURCE pixels
// (=> reading from a coarse mip saves kernel time => cross-depth mip-reuse wins)
// or with DEST pixels (=> mip reuse saves only memcpy, not kernel)?
// Also measures the per-call FLOOR (Wall A premise: per-call machinery cost).
// Results -> /tmp/bench-out.txt
[TestFixture]
public class ResizeScalingBench
{
    static Image<Rgba32> MakeImg(int w, int h)
    {
        var img = new Image<Rgba32>(w, h);
        img.ProcessPixelRows(a =>
        {
            for (int y = 0; y < h; y++)
            {
                var r = a.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                    r[x] = new Rgba32((byte)(x ^ y), (byte)(x * 3), (byte)(y * 5), 255);
            }
        });
        return img;
    }

    static double TimeOp(Action op, int iters)
    {
        op(); op(); // warm
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) op();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }

    [Test]
    public void DownsampleScaling()
    {
        Configuration.Default.MaxDegreeOfParallelism = 1; // per-tile resample is single-threaded under D
        var sb = new StringBuilder();
        void L(string s) { sb.AppendLine(s); TestContext.Out.WriteLine(s); }

        L("=== (1) fixed dest=1024, vary source — does cost scale with SOURCE? ===");
        int dest = 1024;
        foreach (int src in new[] { 1024, 2048, 4096, 8192 })
        {
            using var s = MakeImg(src, src);
            int iters = src >= 8192 ? 6 : (src >= 4096 ? 12 : 30);
            double ms = TimeOp(() => { using var o = s.Clone(c => c.Crop(new Rectangle(0, 0, src, src)).Resize(dest, dest)); }, iters);
            L($"  src={src}^2 (srcMpx={src * (double)src / 1e6,6:F1}) -> dest={dest}^2 : {ms,8:F2} ms/op   ms/srcMpx={ms / (src * (double)src / 1e6),7:F4}");
        }

        L("=== (2) per-call FLOOR — tiny ops (Wall A: per-call machinery) ===");
        using (var small = MakeImg(512, 512))
        {
            double tiny = TimeOp(() => { using var o = small.Clone(c => c.Crop(new Rectangle(0, 0, 256, 256)).Resize(200, 200)); }, 500);
            double cropOnly = TimeOp(() => { using var o = small.Clone(c => c.Crop(new Rectangle(0, 0, 256, 256))); }, 500);
            L($"  256->200 per-call floor: {tiny:F4} ms/op");
            L($"  256 crop-only (no resize): {cropOnly:F4} ms/op  (resize-share = {tiny - cropOnly:F4} ms)");
        }

        L("=== (3) PYRAMID premise — 8192->1024 direct vs from a prebuilt 1024 mip ===");
        using (var big = MakeImg(8192, 8192))
        {
            double direct = TimeOp(() => { using var o = big.Clone(c => c.Crop(new Rectangle(0, 0, 8192, 8192)).Resize(1024, 1024)); }, 6);
            using var mip = big.Clone(c => c.Resize(1024, 1024));
            double frommip = TimeOp(() => { using var o = mip.Clone(c => c.Crop(new Rectangle(0, 0, 1024, 1024)).Resize(1024, 1024)); }, 50);
            L($"  8192->1024 direct = {direct:F2} ms/op");
            L($"  from-1024-mip->1024 = {frommip:F3} ms/op");
            L($"  ==> read-bound speedup from sampling a matched mip = {direct / frommip:F1}x");
        }

        File.WriteAllText("/tmp/bench-out.txt", sb.ToString());
        Assert.Pass();
    }
}

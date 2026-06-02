using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library;

public static class TexturesCache
{
    private static readonly ConcurrentDictionary<string, Lazy<Image<Rgba32>>> Textures = new();
    private static readonly ConcurrentDictionary<string, Lazy<ImageInfo>> TextureInfos = new();

    // Perf telemetry (NOT dead — feeds the [perf:hlod:DecodeStats] line in HierarchicalTilingStage):
    public static long DecodeCount;   // total ACTUAL decodes (incl re-decodes after Clear)
    public static long DecodeTicks;   // total decode CPU time in Stopwatch ticks (no parallel-wait inflation)

    /// <summary>
    /// Phase-8 decode-once experiment. When &gt; 0, each source texture is decoded
    /// ONCE, immediately downsampled so its longest edge is &lt;= this value, and held
    /// resident for the whole bake (Clear() is suppressed). Bounds resident RAM far
    /// below the full-res-per-chunk peak and removes the ~7x re-decode the HLOD
    /// chunk-Clear forces. Set to the max atlas cap to keep all USABLE detail (no
    /// tile atlas exceeds the cap). 0 = legacy behaviour (full-res, re-decode/chunk).
    /// </summary>
    public static int MaxResidentEdge;
    public static bool PersistResident => MaxResidentEdge > 0;

    /// <summary>
    /// G2-SAFE scale-safety budget (bytes) for the resident decoded-texture set. 0 =
    /// unbounded (hold all — NOT scale-safe; only safe for tiny inputs). When &gt; 0, the
    /// between-chunk Clear() keeps the decode-once set resident ONLY while it fits the
    /// budget; once it would exceed the budget (huge / texture-diverse models) the set is
    /// dropped so peak stays bounded — graceful re-decode (original D behaviour), never OOM.
    /// Set from Program as a fraction of available RAM (or --source-cache-budget-mib).
    /// Peak resident ≈ min(Σ_materials·min(srcEdge,cap)²·4, MaxResidentBytes) + per-chunk decode.
    /// </summary>
    public static long MaxResidentBytes;
    private static long _residentBytes;
    public static long ResidentBytes => System.Threading.Interlocked.Read(ref _residentBytes);

    private static (int W, int H) CapDims(int w, int h)
    {
        int cap = MaxResidentEdge;
        if (cap <= 0 || System.Math.Max(w, h) <= cap) return (w, h);
        double s = (double)cap / System.Math.Max(w, h);
        return (System.Math.Max(1, (int)System.Math.Round(w * s)), System.Math.Max(1, (int)System.Math.Round(h * s)));
    }

    public static Image<Rgba32> GetTexture(string textureName)
    {
        // Use Lazy<T> to ensure exactly one thread loads each texture,
        // even under parallel access from multiple meshes/LODs.
        var lazy = Textures.GetOrAdd(textureName,
            key => new Lazy<Image<Rgba32>>(() =>
            {
                var _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var _img = Image.Load<Rgba32>(key);
                var (cw, ch) = CapDims(_img.Width, _img.Height);
                if (cw != _img.Width || ch != _img.Height)
                    _img.Mutate(c => c.Resize(cw, ch));   // downsample once; full-res buffer freed
                System.Threading.Interlocked.Add(ref _residentBytes, (long)_img.Width * _img.Height * 4);
                System.Threading.Interlocked.Increment(ref DecodeCount);
                System.Threading.Interlocked.Add(ref DecodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - _t0);
                return _img;
            }));
        return lazy.Value;
    }

    /// <summary>Source dims AFTER the MaxResidentEdge cap — used by atlas sizing so
    /// packing matches the (capped) image <see cref="GetTexture"/> returns.</summary>
    public static (int Width, int Height) GetCappedDims(string textureName)
    {
        var info = GetTextureInfo(textureName);
        var (w, h) = CapDims(info.Width, info.Height);
        return (w, h);
    }

    public static void EvictTexture(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return;
        if (Textures.TryRemove(textureName, out var lazy) && lazy.IsValueCreated)
        {
            var img = lazy.Value;
            System.Threading.Interlocked.Add(ref _residentBytes, -((long)img.Width * img.Height * 4));
            img.Dispose();
        }
    }

    public static void Clear()
    {
        // G2-SAFE: decode-once-resident keeps capped sources across chunks ONLY while the
        // resident set fits the RAM budget. On fixtures the whole set fits -> no clear ->
        // identical decode-once speed. On huge / texture-diverse models it exceeds budget ->
        // per-chunk clear -> bounded peak, graceful re-decode (original D behaviour), never OOM.
        // (MaxResidentBytes<=0 keeps the legacy unbounded hold — only for tiny inputs.)
        if (PersistResident && (MaxResidentBytes <= 0 || System.Threading.Interlocked.Read(ref _residentBytes) <= MaxResidentBytes))
            return;
        foreach (var kvp in Textures)
        {
            if (kvp.Value.IsValueCreated)
                kvp.Value.Value.Dispose();
        }
        Textures.Clear();
        TextureInfos.Clear();
        System.Threading.Interlocked.Exchange(ref _residentBytes, 0);
    }

    public static ImageInfo GetTextureInfo(string textureName)
    {
        var lazy = TextureInfos.GetOrAdd(textureName,
            key => new Lazy<ImageInfo>(() => Image.Identify(key)));
        return lazy.Value;
    }
}
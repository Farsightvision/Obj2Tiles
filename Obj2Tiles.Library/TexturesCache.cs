using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library;

public static class TexturesCache
{
    private static readonly ConcurrentDictionary<(string Path, int Cap), Lazy<Image<Rgba32>>> Textures = new();
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

    // ===== Parallel-safe per-material eviction (HLOD chunked Phase-1) =====
    // EvictTexture used to Dispose the source Image inline, which is a use-after-dispose
    // hazard when sibling tiles sample the SAME shared image concurrently — so eviction
    // was forced serial. Instead we track active readers per PATH: EvictTexture defers the
    // dispose while any reader holds a lease, and the last ReleaseRead performs it. Disposes
    // are done OUTSIDE this lock (Image.Dispose can be non-trivial). Output is byte-identical:
    // a reader never observes a disposed/partial image, and re-decode after eviction is
    // deterministic (same file + same cap → same pixels). Single lock ⇒ no lock-ordering risk;
    // GetTexture never takes it (the expensive decode/pixel-copy stays off the lock).
    private static readonly object _evictLock = new();
    private static readonly System.Collections.Generic.Dictionary<string, int> _activeReaders =
        new(System.StringComparer.Ordinal);
    private static readonly System.Collections.Generic.HashSet<string> _pendingEvict =
        new(System.StringComparer.Ordinal);

    /// <summary>
    /// Effective decode cap for a per-tile request. When the resident cache is OFF
    /// (MaxResidentEdge &lt;= 0, the small-model legacy path) the per-tile cap is IGNORED
    /// and decode is full-res — byte-identical to today. When the cache is ON, the
    /// effective cap is min(tileCap, MaxResidentEdge); tileCap &lt;= 0 means "use the global".
    /// </summary>
    private static int EffectiveCap(int tileCap)
    {
        if (MaxResidentEdge <= 0) return 0;                 // cache off -> uncapped (legacy)
        if (tileCap <= 0) return MaxResidentEdge;           // no per-tile cap -> global
        return System.Math.Min(tileCap, MaxResidentEdge);   // per-tile cap, bounded by global
    }

    private static (int W, int H) CapDims(int w, int h, int cap)
    {
        if (cap <= 0 || System.Math.Max(w, h) <= cap) return (w, h);
        double s = (double)cap / System.Math.Max(w, h);
        return (System.Math.Max(1, (int)System.Math.Round(w * s)), System.Math.Max(1, (int)System.Math.Round(h * s)));
    }

    public static Image<Rgba32> GetTexture(string textureName) => GetTexture(textureName, 0);

    // LEASE INVARIANT (Codex review b6248f24, hunt 2): when eviction can run concurrently
    // (the HLOD chunked Phase-1), the caller MUST hold a lease (AcquireRead) on textureName
    // around GetTexture AND all sampling of the returned image — otherwise EvictTexture could
    // dispose it under the reader. MeshT_Hlod.FillAtlases is the only such caller and brackets
    // both GetTexture calls in AcquireRead/finally-ReleaseRead. The other callers (legacy
    // MeshT.FillAtlases, the warm-predecode loop, GetCappedDims) run only OUTSIDE the
    // eviction-active path, so an unleased get there is safe.
    public static Image<Rgba32> GetTexture(string textureName, int tileCap)
    {
        int eff = EffectiveCap(tileCap);
        // Use Lazy<T> to ensure exactly one thread loads each (path,cap),
        // even under parallel access from multiple meshes/LODs.
        var lazy = Textures.GetOrAdd((textureName, eff),
            key => new Lazy<Image<Rgba32>>(() =>
            {
                try
                {
                    var _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var _img = Image.Load<Rgba32>(key.Path);
                    var (cw, ch) = CapDims(_img.Width, _img.Height, key.Cap);
                    if (cw != _img.Width || ch != _img.Height)
                        _img.Mutate(c => c.Resize(cw, ch));   // downsample once; full-res buffer freed
                    System.Threading.Interlocked.Add(ref _residentBytes, (long)_img.Width * _img.Height * 4);
                    System.Threading.Interlocked.Increment(ref DecodeCount);
                    System.Threading.Interlocked.Add(ref DecodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - _t0);
                    return _img;
                }
                catch
                {
                    // A FAULTED Lazy keeps IsValueCreated==false forever, and the eviction paths skip
                    // !IsValueCreated entries (to never detach an in-progress decode) — so without this
                    // self-removal a transient decode fault would leave a permanently unevictable,
                    // always-rethrowing entry (Codex review, hunt 4). A faulted entry holds no image,
                    // so removal is always safe; the next GetTexture retries fresh. Eviction cannot
                    // remove THIS entry mid-flight (it skips in-progress), so the TryRemove targets
                    // exactly this Lazy.
                    Textures.TryRemove(key, out _);
                    throw;
                }
            }));
        return lazy.Value;
    }

    /// <summary>Source dims AFTER the cap — used by atlas sizing so packing matches
    /// the (capped) image <see cref="GetTexture"/> returns for the SAME cap.</summary>
    public static (int Width, int Height) GetCappedDims(string textureName) => GetCappedDims(textureName, 0);

    public static (int Width, int Height) GetCappedDims(string textureName, int tileCap)
    {
        var info = GetTextureInfo(textureName);
        var (w, h) = CapDims(info.Width, info.Height, EffectiveCap(tileCap));
        return (w, h);
    }

    /// <summary>Register an active reader (lease) for <paramref name="textureName"/> before
    /// sampling the image it returns from <see cref="GetTexture(string,int)"/>. While any lease
    /// is held, a concurrent <see cref="EvictTexture"/> defers the dispose. MUST be balanced by
    /// <see cref="ReleaseRead"/> in a finally. No-op on null/empty (no source path).</summary>
    public static void AcquireRead(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return;
        lock (_evictLock)
        {
            _activeReaders.TryGetValue(textureName, out var c);
            _activeReaders[textureName] = c + 1;
        }
    }

    /// <summary>Release a lease taken by <see cref="AcquireRead"/>. If this is the last reader
    /// AND an eviction was deferred while it was held, the deferred dispose fires now (outside
    /// the lock).</summary>
    public static void ReleaseRead(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return;
        System.Collections.Generic.List<Image<Rgba32>>? toDispose = null;
        lock (_evictLock)
        {
            if (!_activeReaders.TryGetValue(textureName, out var c)) return;   // defensive: unbalanced release
            if (c > 1) { _activeReaders[textureName] = c - 1; return; }
            _activeReaders.Remove(textureName);
            if (_pendingEvict.Remove(textureName))
                toDispose = CollectForDispose(textureName);                    // deferred eviction fires now
        }
        DisposeAll(toDispose);
    }

    public static void EvictTexture(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return;
        System.Collections.Generic.List<Image<Rgba32>>? toDispose = null;
        lock (_evictLock)
        {
            if (_activeReaders.TryGetValue(textureName, out var c) && c > 0)
            {
                _pendingEvict.Add(textureName);   // a reader is sampling it — defer to the last ReleaseRead
                return;
            }
            _pendingEvict.Remove(textureName);    // no active reader → dispose now (drop any stale pending)
            toDispose = CollectForDispose(textureName);
        }
        DisposeAll(toDispose);
    }

    /// <summary>Under <see cref="_evictLock"/>: detach every (path, cap) entry for this path from
    /// the cache, decrement the resident-byte counter, and return the images to dispose. The actual
    /// <c>Dispose()</c> is done by the caller AFTER releasing the lock. Safe because the entries are
    /// removed from the only registry here, so no concurrent <see cref="GetTexture(string,int)"/> can
    /// hand the same instance out again (a later GetTexture re-decodes a fresh entry).</summary>
    private static System.Collections.Generic.List<Image<Rgba32>>? CollectForDispose(string textureName)
    {
        System.Collections.Generic.List<Image<Rgba32>>? imgs = null;
        foreach (var kvp in Textures)
        {
            if (!string.Equals(kvp.Key.Path, textureName, System.StringComparison.Ordinal)) continue;
            // Never detach an in-progress decode (Codex review, hunt 5): removing a Lazy whose
            // factory is still running would orphan the image it returns (undisposed) and leave
            // _residentBytes permanently overstated. Unreachable on the leased HLOD path
            // (in-progress ⇒ leased ⇒ eviction deferred), but harden the API: skip it; a later
            // eviction/Clear disposes it once published. (Faulted entries self-remove in the
            // factory's catch, so they cannot linger under this skip.)
            if (!kvp.Value.IsValueCreated) continue;
            if (Textures.TryRemove(kvp.Key, out var lazy) && lazy.IsValueCreated)
            {
                var img = lazy.Value;
                System.Threading.Interlocked.Add(ref _residentBytes, -((long)img.Width * img.Height * 4));
                (imgs ??= new System.Collections.Generic.List<Image<Rgba32>>()).Add(img);
            }
        }
        return imgs;
    }

    private static void DisposeAll(System.Collections.Generic.List<Image<Rgba32>>? imgs)
    {
        if (imgs == null) return;
        foreach (var img in imgs) img.Dispose();
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
        // Clear() is contracted to run only at a BARRIER (no reader leases in-flight): all three
        // callers — between-chunk (after the chunk's Parallel.ForEach), Program Phase-3 (after
        // Phase-1), and legacy SplitStage (after its per-material ForEach) — are post-barrier.
        // Defensive hardening (Codex review b6248f24, hunt 6): never dispose an image whose path
        // still has an active lease, so a mistaken non-barrier call degrades to "leave it
        // resident" instead of a use-after-dispose. At a true barrier _activeReaders is empty,
        // so this disposes everything exactly as before. Disposes run OUTSIDE the lock.
        System.Collections.Generic.List<Image<Rgba32>> toDispose = new();
        lock (_evictLock)
        {
            _pendingEvict.Clear();
            foreach (var kvp in Textures)
            {
                if (_activeReaders.ContainsKey(kvp.Key.Path)) continue;   // leased — leave resident
                if (!kvp.Value.IsValueCreated) continue;   // in-progress decode — never detach (hunt 5)
                if (Textures.TryRemove(kvp.Key, out var lazy) && lazy.IsValueCreated)
                {
                    var img = lazy.Value;
                    System.Threading.Interlocked.Add(ref _residentBytes, -((long)img.Width * img.Height * 4));
                    toDispose.Add(img);
                }
            }
            TextureInfos.Clear();   // metadata only (no Image to dispose)
        }
        foreach (var img in toDispose) img.Dispose();
    }

    public static ImageInfo GetTextureInfo(string textureName)
    {
        var lazy = TextureInfos.GetOrAdd(textureName,
            key => new Lazy<ImageInfo>(() => Image.Identify(key)));
        return lazy.Value;
    }
}
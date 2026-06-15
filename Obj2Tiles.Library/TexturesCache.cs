using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library;

public static class TexturesCache
{
    private static readonly ConcurrentDictionary<(string Path, int Cap), Lazy<Image<Rgba32>>> Textures = new();
    private static readonly ConcurrentDictionary<string, Lazy<ImageInfo>> TextureInfos = new();

    public static long DecodeCount;
    public static long DecodeTicks;

    /// <summary>
    /// When &gt; 0, each source texture is decoded once, downsampled so its longest edge is
    /// &lt;= this value, and held resident for the bake. 0 = full-res, re-decode per chunk.
    /// </summary>
    public static int MaxResidentEdge;
    public static bool PersistResident => MaxResidentEdge > 0;

    /// <summary>
    /// RAM budget (bytes) for the resident decoded-texture set; the set is dropped between
    /// chunks once it would exceed this, so peak stays bounded. 0 = unbounded.
    /// </summary>
    public static long MaxResidentBytes;
    private static long _residentBytes;
    public static long ResidentBytes => System.Threading.Interlocked.Read(ref _residentBytes);

    // Eviction defers Dispose while a reader holds a lease (AcquireRead) on the shared image,
    // avoiding a use-after-dispose when sibling tiles sample it concurrently; the last
    // ReleaseRead performs the deferred dispose, outside this lock.
    private static readonly object _evictLock = new();
    private static readonly System.Collections.Generic.Dictionary<string, int> _activeReaders =
        new(System.StringComparer.Ordinal);
    private static readonly System.Collections.Generic.HashSet<string> _pendingEvict =
        new(System.StringComparer.Ordinal);

    private static int EffectiveCap(int tileCap)
    {
        if (MaxResidentEdge <= 0) return 0;
        if (tileCap <= 0) return MaxResidentEdge;
        return System.Math.Min(tileCap, MaxResidentEdge);
    }

    private static (int W, int H) CapDims(int w, int h, int cap)
    {
        if (cap <= 0 || System.Math.Max(w, h) <= cap) return (w, h);
        double s = (double)cap / System.Math.Max(w, h);
        return (System.Math.Max(1, (int)System.Math.Round(w * s)), System.Math.Max(1, (int)System.Math.Round(h * s)));
    }

    public static Image<Rgba32> GetTexture(string textureName) => GetTexture(textureName, 0);

    // When eviction can run concurrently, the caller must hold a lease (AcquireRead) around
    // GetTexture and all sampling of the returned image, else EvictTexture could dispose it
    // under the reader.
    public static Image<Rgba32> GetTexture(string textureName, int tileCap)
    {
        int eff = EffectiveCap(tileCap);
        // Lazy<T> ensures exactly one thread loads each (path,cap) under parallel access.
        var lazy = Textures.GetOrAdd((textureName, eff),
            key => new Lazy<Image<Rgba32>>(() =>
            {
                try
                {
                    var _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var _img = Image.Load<Rgba32>(key.Path);
                    var (cw, ch) = CapDims(_img.Width, _img.Height, key.Cap);
                    if (cw != _img.Width || ch != _img.Height)
                        _img.Mutate(c => c.Resize(cw, ch));
                    System.Threading.Interlocked.Add(ref _residentBytes, (long)_img.Width * _img.Height * 4);
                    System.Threading.Interlocked.Increment(ref DecodeCount);
                    System.Threading.Interlocked.Add(ref DecodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - _t0);
                    return _img;
                }
                catch
                {
                    // A faulted Lazy stays IsValueCreated==false and eviction skips such entries, so
                    // remove it here or it would be permanently unevictable and always rethrow. The
                    // next GetTexture retries fresh.
                    Textures.TryRemove(key, out _);
                    throw;
                }
            }));
        return lazy.Value;
    }

    /// <summary>Source dims after the cap, so atlas packing matches the image
    /// <see cref="GetTexture"/> returns for the same cap.</summary>
    public static (int Width, int Height) GetCappedDims(string textureName) => GetCappedDims(textureName, 0);

    public static (int Width, int Height) GetCappedDims(string textureName, int tileCap)
    {
        var info = GetTextureInfo(textureName);
        var (w, h) = CapDims(info.Width, info.Height, EffectiveCap(tileCap));
        return (w, h);
    }

    /// <summary>Register a reader lease for <paramref name="textureName"/> before sampling its
    /// image; while held, <see cref="EvictTexture"/> defers the dispose. Must be balanced by
    /// <see cref="ReleaseRead"/> in a finally.</summary>
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
    /// and an eviction was deferred while it was held, the deferred dispose fires now.</summary>
    public static void ReleaseRead(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return;
        System.Collections.Generic.List<Image<Rgba32>>? toDispose = null;
        lock (_evictLock)
        {
            if (!_activeReaders.TryGetValue(textureName, out var c)) return;
            if (c > 1) { _activeReaders[textureName] = c - 1; return; }
            _activeReaders.Remove(textureName);
            if (_pendingEvict.Remove(textureName))
                toDispose = CollectForDispose(textureName);
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
                _pendingEvict.Add(textureName);   // a reader holds it — defer to the last ReleaseRead
                return;
            }
            _pendingEvict.Remove(textureName);
            toDispose = CollectForDispose(textureName);
        }
        DisposeAll(toDispose);
    }

    /// <summary>Under <see cref="_evictLock"/>: detach every (path, cap) entry for this path,
    /// decrement the resident-byte counter, and return the images for the caller to dispose
    /// after releasing the lock.</summary>
    private static System.Collections.Generic.List<Image<Rgba32>>? CollectForDispose(string textureName)
    {
        System.Collections.Generic.List<Image<Rgba32>>? imgs = null;
        foreach (var kvp in Textures)
        {
            if (!string.Equals(kvp.Key.Path, textureName, System.StringComparison.Ordinal)) continue;
            // Never detach an in-progress decode: removing it would orphan the image its factory
            // returns and leave _residentBytes overstated.
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
        // While the resident set fits the RAM budget, keep it across chunks; once it would
        // exceed the budget, clear so peak stays bounded.
        if (PersistResident && (MaxResidentBytes <= 0 || System.Threading.Interlocked.Read(ref _residentBytes) <= MaxResidentBytes))
            return;
        // Expected to run only at a barrier (no leases in flight); skip leased paths so a
        // mistaken non-barrier call leaves them resident rather than disposing under a reader.
        System.Collections.Generic.List<Image<Rgba32>> toDispose = new();
        lock (_evictLock)
        {
            _pendingEvict.Clear();
            foreach (var kvp in Textures)
            {
                if (_activeReaders.ContainsKey(kvp.Key.Path)) continue;
                if (!kvp.Value.IsValueCreated) continue;
                if (Textures.TryRemove(kvp.Key, out var lazy) && lazy.IsValueCreated)
                {
                    var img = lazy.Value;
                    System.Threading.Interlocked.Add(ref _residentBytes, -((long)img.Width * img.Height * 4));
                    toDispose.Add(img);
                }
            }
            TextureInfos.Clear();
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
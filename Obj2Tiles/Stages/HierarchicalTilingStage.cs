using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;
using SilentWave.Obj2Gltf;

namespace Obj2Tiles.Stages;

public static partial class HierarchicalTilingStage
{
    private const double WGS84_A  = 6378137.0;
    private const double WGS84_E2 = 6.69437999014e-3;

    /// <summary>
    /// Writes a 3D Tiles 1.1 tileset.json. The ENU→ECEF transform lives on a
    /// content-less wrapper root: loaders.gl tile-converter double-applies a
    /// root transform when that tile also carries content, so an SLPK built
    /// from it renders blank. Cesium/3DTilesRendererJS apply it once either way.
    /// </summary>
    public static void WriteTilesetJson(HierarchicalNode root, string outputDir,
        double latitude, double longitude, double altitude, SubdivisionShape shape)
    {
        Directory.CreateDirectory(outputDir);
        var transform = EnuToEcefTransform(latitude, longitude, altitude);
        bool isQuadtree = shape == SubdivisionShape.Quadtree;

        var contentTile = BuildTileObject(root, isQuadtree, includeTransform: false, null);
        var rootBox = new
        {
            box = OctreeSplitter.AabbBox(new[]
            {
                new Vertex3(root.Bounds.Min.X, root.Bounds.Min.Y, root.Bounds.Min.Z),
                new Vertex3(root.Bounds.Max.X, root.Bounds.Max.Y, root.Bounds.Max.Z),
            }),
        };
        var rootObj = new
        {
            transform,
            boundingVolume = rootBox,
            geometricError = root.GeometricError,
            refine = "REPLACE",
            children = new[] { contentTile },
        };

        var tileset = new
        {
            // gltfUpAxis="Z": positions stay in their source frame, so suppress
            // the renderer's default Y→Z rotation when ingesting GLB content.
            asset = new { version = "1.1", gltfUpAxis = "Z" },
            geometricError = root.GeometricError,
            root = rootObj,
        };
        File.WriteAllText(Path.Combine(outputDir, "tileset.json"),
            JsonConvert.SerializeObject(tileset, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
    }

    private static object BuildTileObject(HierarchicalNode n, bool isQuadtree, bool includeTransform, double[]? transform)
    {
        var children = new List<object>();
        foreach (var c in n.Children) children.Add(BuildTileObject(c, isQuadtree, includeTransform: false, null));
        return new
        {
            transform = includeTransform ? transform : null,
            boundingVolume = new
            {
                box = OctreeSplitter.AabbBox(new[]
                {
                    new Vertex3(n.Bounds.Min.X, n.Bounds.Min.Y, n.Bounds.Min.Z),
                    new Vertex3(n.Bounds.Max.X, n.Bounds.Max.Y, n.Bounds.Max.Z),
                })
            },
            geometricError = n.GeometricError,
            refine = "REPLACE",
            content = new { uri = n.Coord.ToContentUri(isQuadtree) },
            children = children.Count > 0 ? children : null,
        };
    }

    /// <summary>
    /// Local-ENU → ECEF 4×4 transform on the WGS84 ellipsoid, returned as 16
    /// doubles in the column-major order 3D Tiles' tile.transform expects.
    /// </summary>
    public static double[] EnuToEcefTransform(double lat, double lon, double alt)
    {
        double latR = lat * Math.PI / 180.0, lonR = lon * Math.PI / 180.0;
        double sl = Math.Sin(latR), cl = Math.Cos(latR);
        double so = Math.Sin(lonR), co = Math.Cos(lonR);
        double n = WGS84_A / Math.Sqrt(1 - WGS84_E2 * sl * sl);
        double ox = (n + alt) * cl * co;
        double oy = (n + alt) * cl * so;
        double oz = (n * (1 - WGS84_E2) + alt) * sl;
        double[] east  = { -so,        co,         0  };
        double[] north = { -sl * co,  -sl * so,   cl };
        double[] up    = {  cl * co,   cl * so,   sl };
        return new[]
        {
            east[0], east[1], east[2], 0,
            north[0], north[1], north[2], 0,
            up[0], up[1], up[2], 0,
            ox, oy, oz, 1,
        };
    }
}

public static partial class HierarchicalTilingStage
{
    /// <summary>Live available system memory in bytes, container-aware: the tighter of
    /// /proc/meminfo MemAvailable (host node) and the GC's cgroup headroom (limit - load).
    /// MemAvailable alone reports the host, not the pod cgroup, so it over-estimates in a
    /// memory-limited container; the GC term caps it to the actual limit.</summary>
    internal static long LiveAvailableBytes()
    {
        var gc = GC.GetGCMemoryInfo();
        long gcAvail = gc.TotalAvailableMemoryBytes > 0
            ? Math.Max(0L, gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes)
            : long.MaxValue;

        long osAvail = long.MaxValue;   // stays unbounded off-Linux / on parse miss
        try
        {
            foreach (var line in System.IO.File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Require the kB unit so a non-kB line can't be mis-scaled into an inflated budget.
                if (parts.Length >= 3 && parts[2] == "kB" && long.TryParse(parts[1], out var kb)) osAvail = kb * 1024L;
                break;
            }
        }
        catch { /* leave osAvail unbounded; the GC headroom still bounds the result */ }

        return Math.Min(osAvail, gcAvail);
    }

    // RGBA buffers a Phase-1 worker holds at once (decode + resample + atlas + dilation); ~2 GiB at 8192².
    private const long PerWorkerTexBuffersDiffuse = 8L;
    // Normal maps add a second decode, the normal atlas, and its dilation scratch.
    private const long PerWorkerTexBuffersWithNormals = 14L;
    // Clamp capEdge so capEdge² can't overflow long.
    private const int MaxModeledEdge = 32768;
    // Used when neither the resident-cache cap nor the atlas cap is set.
    private const int DefaultClampEdge = 4096;
    // Working set of one concurrent simplify depth: ~485 B/face measured, 512 with margin.
    public const long GeomSimplifyBytesPerFace = 512L;

    private static int Phase1AdaptiveMdop(int desiredMdop, long reserveBytes, int capEdge, bool hasNormalMaps)
        => ClampWorkersToBudget(desiredMdop, LiveAvailableBytes(), reserveBytes,
                                TexturePerWorkerBytes(capEdge, hasNormalMaps));

    // Resident-cache cap if the cache is on, else the atlas cap, so the clamp still applies cache-off.
    public static int EffectiveClampEdge(int maxResidentEdge, int maxAtlasSize)
        => maxResidentEdge > 0 ? maxResidentEdge
         : maxAtlasSize > 0 ? maxAtlasSize
         : DefaultClampEdge;

    public static long TexturePerWorkerBytes(int capEdge, bool hasNormalMaps)
    {
        long capE = Math.Min(Math.Max(0, (long)capEdge), MaxModeledEdge);
        long oneBuffer = capE * capE * 4L;
        return oneBuffer * (hasNormalMaps ? PerWorkerTexBuffersWithNormals : PerWorkerTexBuffersDiffuse);
    }

    // Largest N with reserveBytes + N×perWorkerBytes within ~75% of availBytes, clamped to [1, desiredMdop].
    public static int ClampWorkersToBudget(int desiredMdop, long availBytes, long reserveBytes, long perWorkerBytes)
    {
        if (desiredMdop <= 1 || perWorkerBytes <= 0) return Math.Max(1, desiredMdop);
        long forWorkers = (long)(availBytes * 0.75) - Math.Max(0L, reserveBytes);
        int memMdop = (int)Math.Max(1L, forWorkers / Math.Max(1L, perWorkerBytes));
        return Math.Max(1, Math.Min(desiredMdop, memMdop));
    }

    // Back-compat overload: resolve capEdge to a per-worker byte estimate, then defer to ClampWorkersToBudget.
    public static int ClampWorkersToMemory(int desiredMdop, long availBytes, long reserveBytes, int capEdge)
    {
        if (desiredMdop <= 1 || capEdge <= 0) return Math.Max(1, desiredMdop);
        return ClampWorkersToBudget(desiredMdop, availBytes, reserveBytes,
                                    TexturePerWorkerBytes(capEdge, hasNormalMaps: false));
    }

    /// <summary>
    /// Demote the decode-once fits path to the transient-eviction path when reserving
    /// the resident set clamps parallelism too hard. Holding the set is only worth it
    /// while workers remain, so demote once the fits clamp drops below half the desired
    /// DOP (or under 2): the transient path keeps full DOP at a bounded resident set.
    /// </summary>
    public static bool ShouldDemoteFitsPath(int desiredMdop, int fitsClampedMdop)
        => fitsClampedMdop < Math.Max(2, desiredMdop / 2);

    /// <summary>
    /// Resolve a runnable gltfpack binary: tries --gltfpack-path, then PATH, then $HOME/bin,
    /// $HOME/.local/bin, /usr/local/bin, /usr/bin. Returns the first that starts, or null.
    /// A non-interactive bake process often lacks the login PATH, so the $HOME fallbacks matter.
    /// </summary>
    private static string? ResolveGltfpack(string? explicitPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath);
        candidates.Add("gltfpack");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(System.IO.Path.Combine(home, "bin", "gltfpack"));
            candidates.Add(System.IO.Path.Combine(home, ".local", "bin", "gltfpack"));
        }
        candidates.Add("/usr/local/bin/gltfpack");
        candidates.Add("/usr/bin/gltfpack");
        foreach (var c in candidates)
        {
            try
            {
                var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = c, Arguments = "-h",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                if (probe == null) continue;
                probe.WaitForExit(5000);
                return c;
            }
            catch { /* not present at this candidate; try the next */ }
        }
        return null;
    }

    /// <summary>
    /// Walk the tree and emit a GLB per node: per-tile atlas pack, write
    /// OBJ/MTL/atlas, then convert OBJ → glTF → GLB.
    /// </summary>
    public static void WriteAllGlbs(
        HierarchicalNode root,
        string outputDir,
        bool isQuadtree,
        IReadOnlyList<Material> materials,
        AppConfig config,
        BuildReport? report = null)
    {
        // Phase 1 (atlas pack + write OBJ/MTL/atlas) and Phase 2 (OBJ → glTF → GLB).
        // Phase 2 reads each OBJ off disk, so its converters share no state.
        var tiles = new List<HierarchicalNode>();
        Walk(root, tiles);

        // Depth of the deepest leaf, drives the per-LOD density schedule.
        int maxDepth = 0;
        foreach (var n in tiles) if (n.Depth > maxDepth) maxDepth = n.Depth;

        var atlasEdges = new ConcurrentBag<(int Depth, int Edge)>();
        var glbBytes   = new ConcurrentBag<(int Depth, long Bytes)>();

        int parallelism = Math.Max(1, config.ThreadsCount > 0
            ? config.ThreadsCount
            : Environment.ProcessorCount);

        // Does the decode-once resident set fit the RAM budget? Computed before
        // phase1Mdop so the parallelism below can adapt to it.
        long _estResident = (long)materials.Count * Obj2Tiles.Library.TexturesCache.MaxResidentEdge
                            * Obj2Tiles.Library.TexturesCache.MaxResidentEdge * 4L;
        bool _predecodeFits = Obj2Tiles.Library.TexturesCache.MaxResidentBytes <= 0
                            || _estResident <= Obj2Tiles.Library.TexturesCache.MaxResidentBytes;

        int _clampEdge = EffectiveClampEdge(Obj2Tiles.Library.TexturesCache.MaxResidentEdge, config.MaxAtlasSize);
        bool _hasNormalMaps = false;
        foreach (var mat in materials) { if (!string.IsNullOrEmpty(mat.NormalMap)) { _hasNormalMaps = true; break; } }

        // Phase-1 defaults to all cores; the live-RAM clamp below scales it down
        // only if the projected peak won't fit. Override: --phase1-batches-per-material.
        int phase1Mdop = config.Phase1BatchesPerMaterial > 0
            ? config.Phase1BatchesPerMaterial
            : Environment.ProcessorCount;
        phase1Mdop = Math.Max(1, Math.Min(phase1Mdop, parallelism));

        // If reserving the resident set would clamp parallelism too hard, demote the
        // fits path to transient eviction (full DOP, bounded resident). Only the
        // auto-activated cache may be demoted: an explicit --source-cache-cap is the
        // operator's residency choice, so decode-once is kept (logged) even when clamped.
        if (_predecodeFits && Obj2Tiles.Library.TexturesCache.MaxResidentBytes > 0
                           && Obj2Tiles.Library.TexturesCache.PersistResident)
        {
            int fitsMdop = Phase1AdaptiveMdop(phase1Mdop, _estResident, _clampEdge, _hasNormalMaps);
            if (ShouldDemoteFitsPath(phase1Mdop, fitsMdop))
            {
                if (config.SourceCacheCapAutoEnabled)
                {
                    Console.WriteLine(
                        $" [perf]   Phase-1 fits-path demoted to transient-eviction: holding {_estResident >> 20} MiB resident " +
                        $"would clamp mdop {phase1Mdop} -> {fitsMdop} (liveAvail={LiveAvailableBytes() >> 20}MiB); " +
                        "trading decode-once for full parallelism (G14; explicit --source-cache-cap keeps decode-once)");
                    _predecodeFits = false;
                }
                else
                {
                    Console.WriteLine(
                        $" [perf]   Phase-1 fits-path STRANGLED (mdop {phase1Mdop} -> {fitsMdop} holding {_estResident >> 20} MiB) " +
                        "— explicit --source-cache-cap keeps decode-once (remove the flag to allow G14 demotion)");
                }
            }
        }

        // Over-budget case: tighten the resident budget to ~55% of live RAM so the
        // Phase-1 worker transients (cap-sized decode + resample + atlas, plus OS) keep
        // a never-OOM margin on a memory-bound host. The per-chunk Clear() evicts to it.
        if (!_predecodeFits && Obj2Tiles.Library.TexturesCache.MaxResidentBytes > 0)
        {
            long _maxBudget = (long)(LiveAvailableBytes() * 0.55);
            if (_maxBudget > 0 && Obj2Tiles.Library.TexturesCache.MaxResidentBytes > _maxBudget)
            {
                Console.WriteLine($" [perf]   Phase-1 over-budget native: resident budget {Obj2Tiles.Library.TexturesCache.MaxResidentBytes >> 20} -> {_maxBudget >> 20} MiB (live-RAM margin)");
                Obj2Tiles.Library.TexturesCache.MaxResidentBytes = _maxBudget;
            }
        }
        {
            // Reserve = RAM held from workers. The fits path holds the decode-once set;
            // the over-budget path evicts per-material so its resident is transient (reserve 0,
            // and per-worker still bounds the concurrent decode).
            long _reserve = _predecodeFits ? _estResident : 0L;
            int _reqMdop = phase1Mdop;
            phase1Mdop = Phase1AdaptiveMdop(phase1Mdop, _reserve, _clampEdge, _hasNormalMaps);
            if (phase1Mdop != _reqMdop)
                Console.WriteLine($" [perf]   Phase-1 mdop live-RAM clamp {_reqMdop} -> {phase1Mdop} (cap={_clampEdge}{(_hasNormalMaps ? "+norm" : "")}, reserve={_reserve >> 20}MiB, liveAvail={LiveAvailableBytes() >> 20}MiB)");
        }

        var swPhase1 = System.Diagnostics.Stopwatch.StartNew();
        // Reset per-step accumulators before Phase 1.
        HierarchicalAtlasStage.CtorTicks = 0;
        HierarchicalAtlasStage.PrepareRepackTicks = 0;
        HierarchicalAtlasStage.FillAtlasesTicks = 0;
        HierarchicalAtlasStage.SaveAtlasesTicks = 0;
        HierarchicalAtlasStage.WriteGeometryTicks = 0;
        HierarchicalAtlasStage.Ktx2EncodeTicks = 0;
        Obj2Tiles.Library.Common_Hlod.DilateTicks = 0;
        var preparedBag = new System.Collections.Concurrent.ConcurrentBag<TilePrepared>();
        bool runParallel = config.ParallelPhase1 && phase1Mdop > 1;
        // When sources are held resident and the set fits the budget, pre-decode all
        // materials upfront in parallel so Phase-1 tiles never block on a lazy first-decode.
        // Otherwise lazy decode + per-chunk eviction keeps peak bounded on huge models.
        if (Obj2Tiles.Library.TexturesCache.PersistResident && runParallel && _predecodeFits)
        {
            System.Threading.Tasks.Parallel.ForEach(materials,
                new ParallelOptions { MaxDegreeOfParallelism = phase1Mdop },
                m =>
                {
                    if (!string.IsNullOrEmpty(m.Texture)) Obj2Tiles.Library.TexturesCache.GetTexture(m.Texture);
                    if (!string.IsNullOrEmpty(m.NormalMap)) Obj2Tiles.Library.TexturesCache.GetTexture(m.NormalMap);
                });
        }
        int chunkCount = 0;
        if (runParallel)
        {
            // Material-aware sort: group tiles by their primary material so
            // adjacent tiles in the processing order share source textures.
            var sortedTiles = new List<HierarchicalNode>(tiles);
            sortedTiles.Sort((a, b) =>
            {
                int ma = PrimaryMaterialIndex(a);
                int mb = PrimaryMaterialIndex(b);
                if (ma != mb) return ma.CompareTo(mb);
                // Tie-break on depth then coord for determinism.
                if (a.Depth != b.Depth) return a.Depth.CompareTo(b.Depth);
                return string.CompareOrdinal(a.Coord.ToContentUri(isQuadtree), b.Coord.ToContentUri(isQuadtree));
            });
            if (Obj2Tiles.Library.TexturesCache.PersistResident && _predecodeFits)
            {
                // Fits the budget: every source is already resident, so run all tiles in
                // one Parallel.ForEach (no per-chunk barriers), heaviest-first so the slowest
                // tiles start first and the rest backfill. The chunked path below is for the
                // over-budget case.
                var loopTiles = new List<HierarchicalNode>(sortedTiles);
                loopTiles.Sort((a, b) =>
                {
                    int fa = a.TileContentT?.Faces.Length ?? 0;
                    int fb = b.TileContentT?.Faces.Length ?? 0;
                    if (fa != fb) return fb.CompareTo(fa);
                    return string.CompareOrdinal(a.Coord.ToContentUri(isQuadtree), b.Coord.ToContentUri(isQuadtree));
                });
                Parallel.ForEach(
                    loopTiles,
                    new ParallelOptions { MaxDegreeOfParallelism = phase1Mdop },
                    n =>
                    {
                        var p = PrepareTileForGlb(n, outputDir, isQuadtree, materials, config, maxDepth, evictPerMaterial: false);
                        atlasEdges.Add((n.Depth, p.AtlasEdge));
                        preparedBag.Add(p);
                    });
                chunkCount = 1;
            }
            else
            {
                // Over-budget path: one barrier-free pass over all tiles. Per-material eviction
                // bounds resident to ~mdop in-flight sources, so no per-chunk barriers are needed.
                // Re-clamp mdop once against post-load live RAM (resident is steady under eviction).
                int _passMdop = Phase1AdaptiveMdop(phase1Mdop, 0L, _clampEdge, _hasNormalMaps);
                if (_passMdop != phase1Mdop)
                    Console.WriteLine($" [perf]   Phase-1 over-budget pass mdop {phase1Mdop} -> {_passMdop} (live-RAM, reserve=0, liveAvail={LiveAvailableBytes() >> 20}MiB)");
                phase1Mdop = _passMdop;
                // Heavy-first so the slowest tiles start first and the rest backfill.
                var loopTiles = new List<HierarchicalNode>(sortedTiles);
                loopTiles.Sort((a, b) =>
                {
                    int fa = a.TileContentT?.Faces.Length ?? 0;
                    int fb = b.TileContentT?.Faces.Length ?? 0;
                    if (fa != fb) return fb.CompareTo(fa);
                    return string.CompareOrdinal(a.Coord.ToContentUri(isQuadtree), b.Coord.ToContentUri(isQuadtree));
                });
                Parallel.ForEach(
                    loopTiles,
                    new ParallelOptions { MaxDegreeOfParallelism = _passMdop },
                    n =>
                    {
                        var p = PrepareTileForGlb(n, outputDir, isQuadtree, materials, config, maxDepth, evictPerMaterial: true);
                        atlasEdges.Add((n.Depth, p.AtlasEdge));
                        preparedBag.Add(p);
                    });
                chunkCount = 1;
                // Release any tail sources before Phase-2/3.
                Obj2Tiles.Library.TexturesCache.Clear();
            }
        }
        else
        {
            foreach (var n in tiles)
            {
                var p = PrepareTileForGlb(n, outputDir, isQuadtree, materials, config, maxDepth, evictPerMaterial: true);
                atlasEdges.Add((n.Depth, p.AtlasEdge));
                preparedBag.Add(p);
            }
        }
        var prepared = preparedBag.ToList();
        swPhase1.Stop();
        Console.WriteLine($" [perf]   Phase-1 atlas/obj write ({(runParallel ? $"parallel mdop={phase1Mdop} chunks={chunkCount}" : "serial")}): {swPhase1.Elapsed.TotalSeconds:F2}s for {tiles.Count} tiles ({(tiles.Count > 0 ? swPhase1.Elapsed.TotalSeconds / tiles.Count : 0):F2}s/tile)");
        Console.WriteLine($"[perf:hlod:Phase1_AtlasWrite] elapsed={swPhase1.ElapsedMilliseconds}ms tiles={tiles.Count} mode={(runParallel ? $"parallel:{phase1Mdop}:chunks={chunkCount}" : "serial")}");
        Console.WriteLine($"[perf:hlod:DecodeStats] actualDecodes={Obj2Tiles.Library.TexturesCache.DecodeCount} totalDecodeMs={Obj2Tiles.Library.TexturesCache.DecodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0}");
        Console.WriteLine($"[perf:hlod:DilateMs] {Obj2Tiles.Library.Common_Hlod.DilateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} (CPU-sum across tiles)");

        // Per-step CPU-second breakdown (summed across parallel tasks).
        double TicksToSec(long t) => (double)t / System.Diagnostics.Stopwatch.Frequency;
        long TicksToMs(long t) => (long)(TicksToSec(t) * 1000);
        Console.WriteLine($" [perf]     ctor={TicksToSec(HierarchicalAtlasStage.CtorTicks):F2}s prepare={TicksToSec(HierarchicalAtlasStage.PrepareRepackTicks):F2}s fillAtlases={TicksToSec(HierarchicalAtlasStage.FillAtlasesTicks):F2}s saveAtlases={TicksToSec(HierarchicalAtlasStage.SaveAtlasesTicks):F2}s ktx2Encode={TicksToSec(HierarchicalAtlasStage.Ktx2EncodeTicks):F2}s writeGeom={TicksToSec(HierarchicalAtlasStage.WriteGeometryTicks):F2}s  [CPU-sec summed across {(config.ParallelPhase1 ? "parallel" : "serial")} tiles]");
        Console.WriteLine($"[perf:hlod:Phase1_Breakdown] ctor={TicksToMs(HierarchicalAtlasStage.CtorTicks)}ms prepare={TicksToMs(HierarchicalAtlasStage.PrepareRepackTicks)}ms fillAtlases={TicksToMs(HierarchicalAtlasStage.FillAtlasesTicks)}ms saveAtlases={TicksToMs(HierarchicalAtlasStage.SaveAtlasesTicks)}ms ktx2Encode={TicksToMs(HierarchicalAtlasStage.Ktx2EncodeTicks)}ms writeGeom={TicksToMs(HierarchicalAtlasStage.WriteGeometryTicks)}ms cpu_sum_ms");

        // Phase 2: parallel OBJ → glTF → GLB conversion.
        var swPhase2 = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(
            prepared,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            p =>
            {
                long size = ConvertObjToGlb(p, config);
                glbBytes.Add((p.Depth, size));
            });
        swPhase2.Stop();
        Console.WriteLine($" [perf]   Phase-2 obj→gltf→glb (parallel, MaxDOP={parallelism}): {swPhase2.Elapsed.TotalSeconds:F2}s for {prepared.Count} tiles ({(prepared.Count > 0 ? swPhase2.Elapsed.TotalSeconds / prepared.Count : 0):F2}s/tile avg)");
        Console.WriteLine($"[perf:hlod:Phase2_ObjToGlb] elapsed={swPhase2.ElapsedMilliseconds}ms tiles={prepared.Count} parallelism={parallelism}");

        // Post-process each emitted GLB through gltfpack (mesh quantization /
        // KTX2). Skips with a warning if no runnable gltfpack is found.
        if (config.QuantizeGlbs)
        {
            string gltfpackBin = ResolveGltfpack(config.GltfpackPath) ?? "";
            bool gltfpackOk = gltfpackBin.Length > 0;
            if (!gltfpackOk)
                Console.Error.WriteLine(" !! --quantize-glbs requested but NO runnable gltfpack found "
                    + "(tried --gltfpack-path, PATH, $HOME/bin, $HOME/.local/bin, /usr/local/bin, /usr/bin). "
                    + "Install a gltfpack-with-BasisU or pass --gltfpack-path. Skipping post-process (GLBs stay JPEG).");
            else
                Console.WriteLine($" -> gltfpack: '{gltfpackBin}' (KTX2/quantize)");
            if (gltfpackOk)
            {
                var swQuant = System.Diagnostics.Stopwatch.StartNew();
                long sizeBefore = 0, sizeAfter = 0;
                int okN = 0, failN = 0;
                var firstFailReason = "";
                int failReasonLock = 0;

                // Free the Phase-1 decode-once cache before Phase-3: gltfpack reads GLBs
                // off disk, so the resident source textures only starve Phase-3's RAM budget
                // and limit concurrent encodes.
                if (Obj2Tiles.Library.TexturesCache.PersistResident)
                {
                    long _prevBudget = Obj2Tiles.Library.TexturesCache.MaxResidentBytes;
                    Obj2Tiles.Library.TexturesCache.MaxResidentBytes = 1;   // make the next Clear() evict all
                    Obj2Tiles.Library.TexturesCache.Clear();
                    Obj2Tiles.Library.TexturesCache.MaxResidentBytes = _prevBudget;
                }
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();

                // The KTX2/ETC1S encode is memory-heavy, so cap concurrent gltfpack
                // processes by a memory budget to avoid OOM on constrained hosts; roomy
                // hosts relax back to `parallelism`. -tc applies only in the gltfpack
                // encoder path: basisu mode embeds the .ktx2 atlases before the GLB exists.
                bool ktx = config.Ktx2Hierarchical
                    && string.Equals(config.Ktx2Encoder, "gltfpack", StringComparison.OrdinalIgnoreCase);
                int ktxWorkers = parallelism;
                if (ktx)
                {
                    int maxAtlasEdge = 0;
                    foreach (var p in prepared) if (p.AtlasEdge > maxAtlasEdge) maxAtlasEdge = p.AtlasEdge;
                    if (maxAtlasEdge <= 0) maxAtlasEdge = config.MaxAtlasSize > 0 ? config.MaxAtlasSize : 4096;
                    // Per-worker cost scales with the encoded edge (atlas capped at
                    // MaxAtlasSize). Budget against live RAM so the concurrent processes fit.
                    int effEdge = config.MaxAtlasSize > 0 ? Math.Min(maxAtlasEdge, config.MaxAtlasSize) : maxAtlasEdge;
                    double scale = (double)effEdge / 4096.0;
                    long perWorkerMib = (long)(1300 * scale * scale);
                    if (perWorkerMib < 512) perWorkerMib = 512;
                    long availMib = LiveAvailableBytes() / (1024 * 1024);
                    long budgetMib = (long)(availMib * 0.55);
                    int byMem = (int)Math.Max(1, budgetMib / perWorkerMib);
                    ktxWorkers = Math.Clamp(byMem, 1, parallelism);
                    // HLOD_KTX2_WORKERS pins the concurrent-encode count if the heuristic misjudges a host.
                    var workerOverride = Environment.GetEnvironmentVariable("HLOD_KTX2_WORKERS");
                    if (int.TryParse(workerOverride, out int wo) && wo > 0)
                    {
                        ktxWorkers = Math.Clamp(wo, 1, parallelism);
                        Console.WriteLine($" [perf]   Phase-3 KTX2 workers pinned by HLOD_KTX2_WORKERS={wo} → {ktxWorkers}/{parallelism}");
                    }
                    else
                    {
                        Console.WriteLine($" [perf]   Phase-3 KTX2 memory-adaptive workers: {ktxWorkers}/{parallelism} (maxAtlas={maxAtlasEdge}², ~{perWorkerMib}MiB/worker, budget {budgetMib}MiB of {availMib} avail)");
                    }
                }
                // Largest-atlas-first so the heaviest encodes don't pile up at the tail.
                var quantTiles = new List<TilePrepared>(prepared);
                quantTiles.Sort((a, b) => b.AtlasEdge.CompareTo(a.AtlasEdge));
                Parallel.ForEach(
                    quantTiles,
                    new ParallelOptions { MaxDegreeOfParallelism = ktxWorkers },
                    p =>
                    {
                        if (!File.Exists(p.FinalGlbPath))
                        {
                            if (System.Threading.Interlocked.Exchange(ref failReasonLock, 1) == 0)
                                firstFailReason = $"FinalGlbPath missing: {p.FinalGlbPath}";
                            System.Threading.Interlocked.Increment(ref failN);
                            return;
                        }
                        long beforeBytes = new FileInfo(p.FinalGlbPath).Length;
                        // gltfpack requires output extension to be .gltf or .glb,
                        // so suffix .quant.glb instead of .quant.tmp.
                        string tmpOut = p.FinalGlbPath + ".quant.glb";
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = gltfpackBin,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        };
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add(p.FinalGlbPath);
                        psi.ArgumentList.Add("-o");
                        psi.ArgumentList.Add(tmpOut);
                        // EXT_meshopt_compression: deck.gl + 3DTilesRendererJS need
                        // MeshoptDecoder loaded externally (CesiumJS has it built-in).
                        if (config.MeshoptCompress) psi.ArgumentList.Add("-c");
                        if (ktx)
                        {
                            // Convert embedded JPEG atlases to KTX2/Basis ETC1S.
                            psi.ArgumentList.Add("-tc");
                            psi.ArgumentList.Add("-tq");
                            psi.ArgumentList.Add(Math.Clamp(config.Ktx2Quality, 1, 10).ToString());
                            // -tj 1: one encode thread per process. We already run ktxWorkers
                            // processes; default per-process BasisU threading would oversubscribe
                            // cores and bloat peak RSS. ETC1S output is unchanged by thread count.
                            psi.ArgumentList.Add("-tj");
                            psi.ArgumentList.Add("1");
                        }
                        try
                        {
                            var proc = System.Diagnostics.Process.Start(psi);
                            string stderr = proc!.StandardError.ReadToEnd();
                            string stdout = proc.StandardOutput.ReadToEnd();
                            proc.WaitForExit();
                            if (proc.ExitCode == 0 && File.Exists(tmpOut))
                            {
                                long afterBytes = new FileInfo(tmpOut).Length;
                                File.Delete(p.FinalGlbPath);
                                File.Move(tmpOut, p.FinalGlbPath);
                                System.Threading.Interlocked.Add(ref sizeBefore, beforeBytes);
                                System.Threading.Interlocked.Add(ref sizeAfter, afterBytes);
                                System.Threading.Interlocked.Increment(ref okN);
                            }
                            else
                            {
                                try { File.Delete(tmpOut); } catch { /* swallow */ }
                                if (System.Threading.Interlocked.Exchange(ref failReasonLock, 1) == 0)
                                    firstFailReason = $"exit={proc.ExitCode} tmpOutExists={File.Exists(tmpOut)} stderr=[{stderr.Trim()}] stdout=[{stdout.Trim().Substring(0, Math.Min(200, stdout.Length))}]";
                                System.Threading.Interlocked.Increment(ref failN);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (System.Threading.Interlocked.Exchange(ref failReasonLock, 1) == 0)
                                firstFailReason = $"Process.Start threw: {ex.GetType().Name}: {ex.Message}";
                            System.Threading.Interlocked.Increment(ref failN);
                        }
                    });
                swQuant.Stop();
                double bMB = sizeBefore / 1_048_576.0, aMB = sizeAfter / 1_048_576.0;
                double pct = sizeBefore > 0 ? (1.0 - (double)sizeAfter / sizeBefore) * 100.0 : 0;
                Console.WriteLine($" [perf]   Phase-3 gltfpack quantize (parallel, MaxDOP={parallelism}): {swQuant.Elapsed.TotalSeconds:F2}s, {okN} ok / {failN} fail, {bMB:F1} MB -> {aMB:F1} MB (-{pct:F1}%)");
                Console.WriteLine($"[perf:hlod:Phase3_GltfpackQuantize] elapsed={swQuant.ElapsedMilliseconds}ms ok={okN} fail={failN} bytes_before={sizeBefore} bytes_after={sizeAfter}");
                if (failN > 0 && !string.IsNullOrEmpty(firstFailReason))
                    Console.Error.WriteLine($" !! first quantize failure: {firstFailReason}");
                if (ktx && okN > 0)
                {
                    // A gltfpack built without BasisU accepts -tc and exits 0 but silently
                    // emits JPEG, which the exit code can't reveal. Verify one GLB embeds KTX2.
                    TilePrepared sample = null;
                    foreach (var p in prepared) { if (File.Exists(p.FinalGlbPath)) { sample = p; break; } }
                    bool hasKtx2 = true;   // default: assume OK (don't false-alarm if we can't read the sample)
                    if (sample != null)
                    {
                        try
                        {
                            var bytes = File.ReadAllBytes(sample.FinalGlbPath);
                            hasKtx2 = System.Text.Encoding.ASCII.GetString(bytes).Contains("KHR_texture_basisu");
                        }
                        catch { /* unreadable — skip the heuristic */ }
                    }
                    if (!hasKtx2)
                        Console.Error.WriteLine($" !! KTX2 requested but the converted GLBs have NO KHR_texture_basisu — the gltfpack at '{gltfpackBin}' likely lacks BasisU support; tiles are still JPEG despite --quantize-glbs. Use a gltfpack built WITH BasisU.");
                }
            }
        }

        if (report != null)
        {
            foreach (var depthGrp in GroupAtlasByDepth(atlasEdges))
            {
                var sorted = depthGrp.Value;
                sorted.Sort();
                report.AtlasSizeP50[depthGrp.Key] = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0;
            }
            foreach (var depthGrp in GroupBytesByDepth(glbBytes))
            {
                var sorted = depthGrp.Value;
                sorted.Sort();
                report.GlbBytesP50[depthGrp.Key] = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0;
            }
        }

        static void Walk(HierarchicalNode n, List<HierarchicalNode> sink)
        {
            if (n.TileContentT != null && n.TileContentT.Faces.Length > 0)
                sink.Add(n);
            foreach (var c in n.Children) Walk(c, sink);
        }
        // Primary material = the MaterialIndex in the most faces, used to order
        // tiles so neighbours share source textures (decode once, not per tile).
        static int PrimaryMaterialIndex(HierarchicalNode n)
        {
            var t = n.TileContentT;
            if (t == null || t.Faces.Length == 0) return -1;
            var counts = new Dictionary<int, int>();
            foreach (var f in t.Faces)
            {
                int mi = f.MaterialIndex;
                counts[mi] = counts.TryGetValue(mi, out var c) ? c + 1 : 1;
            }
            int bestIdx = -1, bestCnt = -1;
            foreach (var kv in counts)
                if (kv.Value > bestCnt) { bestCnt = kv.Value; bestIdx = kv.Key; }
            return bestIdx;
        }
        static Dictionary<int, List<int>> GroupAtlasByDepth(ConcurrentBag<(int Depth, int Edge)> bag)
        {
            var d = new Dictionary<int, List<int>>();
            foreach (var (depth, e) in bag)
            {
                if (!d.TryGetValue(depth, out var list)) { list = new List<int>(); d[depth] = list; }
                list.Add(e);
            }
            return d;
        }
        static Dictionary<int, List<long>> GroupBytesByDepth(ConcurrentBag<(int Depth, long Bytes)> bag)
        {
            var d = new Dictionary<int, List<long>>();
            foreach (var (depth, b) in bag)
            {
                if (!d.TryGetValue(depth, out var list)) { list = new List<long>(); d[depth] = list; }
                list.Add(b);
            }
            return d;
        }
    }

    /// <summary>Per-tile data shared between the serial prepare and the
    /// parallel glb-conversion phases.</summary>
    private sealed class TilePrepared
    {
        public int Depth;
        public int AtlasEdge;
        public string ObjPath = "";
        public string GltfPath = "";
        public string FinalGlbPath = "";
    }

    /// <summary>
    /// Phase-1 (serial) work: atlas pack + write OBJ/MTL/atlas to disk.
    /// </summary>
    private static TilePrepared PrepareTileForGlb(
        HierarchicalNode n,
        string outputDir,
        bool isQuadtree,
        IReadOnlyList<Material> materials,
        AppConfig config,
        int maxDepth,
        bool evictPerMaterial)
    {
        string uri = n.Coord.ToContentUri(isQuadtree);
        string finalGlbPath = Path.Combine(outputDir, uri);
        Directory.CreateDirectory(Path.GetDirectoryName(finalGlbPath)!);

        string tempRoot = Path.Combine(outputDir, ".temp", "tiles",
            $"L{n.Coord.Level}_X{n.Coord.X}_Y{n.Coord.Y}_Z{n.Coord.Z}");
        Directory.CreateDirectory(tempRoot);
        string tileName = $"tile_L{n.Coord.Level}_X{n.Coord.X}_Y{n.Coord.Y}_Z{n.Coord.Z}";
        string objPath = Path.Combine(tempRoot, $"{tileName}.obj");
        string gltfPath = Path.Combine(tempRoot, $"{tileName}.gltf");

        var (edge, packedMesh) = HierarchicalAtlasStage.PackAndWrite(
            n.TileContentT!, materials, config, objPath, tileName, n.Depth, maxDepth, n.IsLeaf, evictPerMaterial);
        // WriteGeometry emits the OBJ + MTL on disk for Obj2Gltf to read.
        long tw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        packedMesh.WriteGeometry();
        long tw1 = System.Diagnostics.Stopwatch.GetTimestamp();
        System.Threading.Interlocked.Add(ref HierarchicalAtlasStage.WriteGeometryTicks, tw1 - tw0);

        return new TilePrepared
        {
            Depth = n.Depth,
            AtlasEdge = edge,
            ObjPath = objPath,
            GltfPath = gltfPath,
            FinalGlbPath = finalGlbPath,
        };
    }

    /// <summary>
    /// Phase-2 (parallelizable) work: OBJ → glTF → GLB. Each call uses its
    /// own converter instances; inputs/outputs are read from / written to
    /// per-tile paths, so calls do not share in-memory state.
    /// </summary>
    private static long ConvertObjToGlb(TilePrepared p, AppConfig config)
    {
        var converter = Converter_Hlod.MakeDefault();
        var convOpts = new GltfConverterOptions_Hlod
        {
            // Meshopt optimize chain disabled: it remaps indices in a way that
            // produces spurious long-edge triangles.
            ApplyMeshoptOptimization = false,
            EncodeMeshoptCompression = false,
            LeafNoMips = config.LeafNoMips,
        };
        converter.Convert(p.ObjPath, p.GltfPath, convOpts);

        // Force doubleSided on every material: the pipeline can emit triangles
        // whose winding can't be recovered, showing as back-facing ribbons in
        // coarse LODs. Unlit photogrammetry makes this visually free.
        PatchGltfDoubleSided(p.GltfPath);

        var glbConverter = new Gltf2GlbConverter();
        glbConverter.Convert(new Gltf2GlbOptions(p.GltfPath, p.FinalGlbPath));

        long size = new FileInfo(p.FinalGlbPath).Length;

        return size;
    }

    private static void PatchGltfDoubleSided(string gltfPath)
    {
        var text = File.ReadAllText(gltfPath);
        var doc = Newtonsoft.Json.Linq.JObject.Parse(text);
        if (doc["materials"] is not Newtonsoft.Json.Linq.JArray materials) return;
        foreach (var mat in materials)
            mat["doubleSided"] = true;
        File.WriteAllText(gltfPath, doc.ToString(Newtonsoft.Json.Formatting.None));
    }

}

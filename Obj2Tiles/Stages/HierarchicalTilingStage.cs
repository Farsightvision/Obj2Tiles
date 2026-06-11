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
    /// Writes a 3D Tiles 1.1 tileset.json describing the hierarchical tree.
    /// The local-ENU → ECEF transform lives on a content-less wrapper root
    /// (root-only per spec §6.2); the first content-bearing tile is its child.
    ///
    /// Why the wrapper instead of putting <c>transform</c> directly on the
    /// content-bearing root: loaders.gl <c>tile-converter</c> (used by the SLPK
    /// pipeline in fsv_server) double-applies a root transform when the same
    /// tile also carries content, sending every I3S node ~an Earth radius
    /// off-planet so the resulting SLPK renders blank. Cesium and
    /// 3DTilesRendererJS apply the transform once regardless of which tile
    /// holds the content, so the web map is unaffected by the wrapper. This
    /// mirrors the legacy flat layout in <c>TilingStage</c>, which never
    /// tripped the converter for the same reason.
    /// </summary>
    public static void WriteTilesetJson(HierarchicalNode root, string outputDir,
        double latitude, double longitude, double altitude, SubdivisionShape shape)
    {
        Directory.CreateDirectory(outputDir);
        var transform = EnuToEcefTransform(latitude, longitude, altitude);
        bool isQuadtree = shape == SubdivisionShape.Quadtree;

        // Build the content tree WITHOUT a transform; we attach the transform
        // to a content-less wrapper above it (see <summary> for why).
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
            // glTF spec says GLB content is Y-up, and renderers auto-rotate
            // Y→Z when ingesting GLB into a 3D Tiles tile. Our pipeline
            // writes positions in their original frame (typically Z-up for
            // ODM exports), so we explicitly tell the renderer NOT to
            // rotate via gltfUpAxis="Z". Both Cesium and 3DTilesRendererJS
            // honor this hint.
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
    /// Local-ENU → ECEF 4×4 transform at (lat, lon, alt) on the WGS84 ellipsoid.
    /// Returns 16 doubles in column-major order (col0, col1, col2, col3) — the
    /// layout 3D Tiles' <c>tile.transform</c> expects.
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
        // Column-major flat (col0, col1, col2, col3)
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
    // ===== Obj2b: Phase-1 live-RAM graceful degradation (vlrg native-source OOM fix) =====
    /// <summary>Live available system memory in bytes, container-aware. Combines /proc/meminfo
    /// MemAvailable (host-live: reflects the resident decode-once cache + everything else AS IT GROWS
    /// mid-bake) with the GC's cgroup-aware headroom (limit - load), returning the TIGHTER of the two.
    /// /proc/meminfo alone reports the HOST node, not the pod cgroup, so in a memory-limited container
    /// it would over-estimate and under-clamp; the GC term caps it to the actual limit.</summary>
    private static long LiveAvailableBytes()
    {
        // GC view is cgroup/container-aware: TotalAvailableMemoryBytes = the limit,
        // MemoryLoadBytes = current usage against it, so the difference is LIVE headroom
        // inside the container limit.
        var gc = GC.GetGCMemoryInfo();
        long gcAvail = gc.TotalAvailableMemoryBytes > 0
            ? Math.Max(0L, gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes)
            : long.MaxValue;

        long osAvail = long.MaxValue;   // host-live signal; stays "unbounded" off-Linux / on parse miss
        try
        {
            foreach (var line in System.IO.File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Format is "MemAvailable:   <N> kB" — require the kB unit so a hypothetical non-kB
                // line can't be mis-scaled into a bogus (inflated) budget; else leave osAvail unbounded.
                if (parts.Length >= 3 && parts[2] == "kB" && long.TryParse(parts[1], out var kb)) osAvail = kb * 1024L;
                break;
            }
        }
        catch { /* leave osAvail unbounded; the GC headroom still bounds the result */ }

        // Tighter of the two: in a memory-limited pod the GC term wins; on bare metal /proc wins.
        return Math.Min(osAvail, gcAvail);
    }

    /// <summary>Clamp the desired Phase-1 worker count to fit LIVE available memory (reads
    /// <see cref="LiveAvailableBytes"/>); the pure, unit-tested core is <see cref="ClampWorkersToMemory"/>.</summary>
    private static int Phase1AdaptiveMdop(int desiredMdop, long reserveBytes, int capEdge)
        => ClampWorkersToMemory(desiredMdop, LiveAvailableBytes(), reserveBytes, capEdge);

    /// <summary>
    /// Pure core of the Phase-1 worker clamp (split out from <see cref="LiveAvailableBytes"/> so it is
    /// deterministically unit-testable): the largest worker count whose projected peak (reserveBytes +
    /// N × native per-worker working set) stays within ~75% of <paramref name="availBytes"/>, clamped to
    /// [1, desiredMdop]. Prevents the native-source OOM (large --source-cache-cap × full mdop on a
    /// memory-bound host) by degrading to fewer concurrent tiles — worst case 1, which always fits.
    /// Output-NEUTRAL: only sets MaxDegreeOfParallelism; per-tile output is scheduling-independent.
    /// reserveBytes = the resident cache the workers share (pass 0 for a mid-bake per-chunk re-check,
    /// where availBytes already reflects the loaded cache).
    ///
    /// perWorker = capEdge²×4×8 (≈ 2 GiB at 8192²): EMPIRICALLY calibrated — a measured vlrg --cap 8192
    /// --threads 8 bake peaked at 13.97 GiB with a 5.6 GiB resident budget at mdop 4, i.e. ~2.1 GiB of
    /// non-cache RSS per worker (native PNG decode + resample + atlas + chunk loads). capEdge clamped to
    /// 32768 so capEdge²×32 can't overflow long (real edges ≤ ~16384).
    /// </summary>
    public static int ClampWorkersToMemory(int desiredMdop, long availBytes, long reserveBytes, int capEdge)
    {
        if (desiredMdop <= 1 || capEdge <= 0) return Math.Max(1, desiredMdop);
        long capE = Math.Min(capEdge, 32768);
        long perWorker = capE * capE * 4L * 8L;
        long forWorkers = (long)(availBytes * 0.75) - Math.Max(0L, reserveBytes);
        int memMdop = (int)Math.Max(1L, forWorkers / Math.Max(1L, perWorker));
        return Math.Max(1, Math.Min(desiredMdop, memMdop));
    }

    /// <summary>
    /// G14 (dev-env strangulation fix): should the fits/pre-decode path be DEMOTED to
    /// the transient-eviction (over-budget) path? On the fits path the start clamp
    /// must reserve the whole resident set out of live RAM, so at envelopes where the
    /// set barely fits its budget (the dev 28300Mi pod: est 11840 MiB vs budget
    /// 12735 MiB) the reserve eats the worker headroom and clamps mdop 7 → 1..3 —
    /// at 1 the old runParallel gate even forced fully-SERIAL Phase-1 with
    /// per-material re-decode churn. HLOD baked slower than legacy flat-grid while
    /// 5+ CPUs idled. Holding the set is only worth it when workers remain: if the
    /// fits clamp yields less than half the desired DOP (or under 2), trading
    /// decode-once for the transient-eviction path (reserve=0 → full DOP; bounded
    /// resident; the empirically validated never-OOM path) is the better schedule.
    /// </summary>
    public static bool ShouldDemoteFitsPath(int desiredMdop, int fitsClampedMdop)
        => fitsClampedMdop < Math.Max(2, desiredMdop / 2);

    /// <summary>
    /// Resolve a runnable gltfpack binary so --quantize-glbs / KTX2 "just works" without the operator
    /// passing --gltfpack-path. Tries, in order: the explicit --gltfpack-path, then "gltfpack" on PATH,
    /// then common install locations ($HOME/bin, $HOME/.local/bin, /usr/local/bin, /usr/bin). Returns the
    /// first that starts (gltfpack -h prints help and exits non-zero but STARTS, which is the success
    /// signal), or null if none is runnable. A non-interactive process often lacks the user's login PATH,
    /// so $HOME/bin matters (that's where the gltfpack-with-BasisU symlink typically lives).
    /// </summary>
    private static string? ResolveGltfpack(string? explicitPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath);
        candidates.Add("gltfpack");   // PATH
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
                return c;   // started successfully — this gltfpack is runnable
            }
            catch { /* not present at this candidate; try the next */ }
        }
        return null;
    }

    /// <summary>
    /// Walk the tree and for each node emit a real GLB at
    /// <c>content/{level}/{x}/{y}.glb</c> (quadtree) or
    /// <c>content/{level}/{x}/{y}/{z}.glb</c> (octree).
    ///
    /// For each node we:
    ///   1. Run the per-tile atlas pack (positions/UVs/materials must already
    ///      have been threaded through OctreeSplitter's textured path).
    ///   2. Write a temp OBJ + MTL + atlas to a per-tile temp directory.
    ///   3. Convert OBJ → glTF via Obj2Gltf.
    ///   4. Convert glTF → GLB.
    /// </summary>
    public static void WriteAllGlbs(
        HierarchicalNode root,
        string outputDir,
        bool isQuadtree,
        IReadOnlyList<Material> materials,
        AppConfig config,
        BuildReport? report = null)
    {
        // Split each tile into two phases.
        //   Phase 1 (serial):  atlas pack + write OBJ/MTL/atlas to disk. This
        //                      stage uses ImageSharp + MeshT internals that
        //                      are not safe to call concurrently.
        //   Phase 2 (parallel): OBJ → glTF → GLB. Each thread reads its
        //                       own OBJ off disk, so the converters do not
        //                       share state. This is the dominant cost on
        //                       big fixtures (Obj2Gltf parses + reorders the
        //                       index buffer + writes a glTF).
        var tiles = new List<HierarchicalNode>();
        Walk(root, tiles);

        // Depth of the deepest leaf — drives the per-LOD density schedule
        // (r_d = LeafDensity / 2^(maxDepth - d)). Cheap to compute here
        // once after the walk and thread into the per-tile sizer.
        int maxDepth = 0;
        foreach (var n in tiles) if (n.Depth > maxDepth) maxDepth = n.Depth;

        var atlasEdges = new ConcurrentBag<(int Depth, int Edge)>();
        var glbBytes   = new ConcurrentBag<(int Depth, long Bytes)>();

        int parallelism = Math.Max(1, config.ThreadsCount > 0
            ? config.ThreadsCount
            : Environment.ProcessorCount);

        // Phase 1: atlas pack + obj/mtl/atlas write. Two paths:
        //
        //   * Serial — one tile at a time, with TexturesCache.EvictTexture
        //     after each tile's materials are consumed. Used when MaxDOP=1
        //     (e.g. --threads 1 audit runs).
        //
        //   * Parallel material-aware batching — tiles are sorted by primary
        //     material (the material index with the most face area in the tile),
        //     then partitioned into fixed-size chunks of phase1Mdop * 2 tiles.
        //     Within each chunk we Parallel.ForEach with MaxDOP=phase1Mdop;
        //     between chunks we Clear() the TexturesCache. Two effects:
        //       (a) Concurrent tiles in a chunk share decoded source textures
        //           via the lazy ConcurrentDictionary, so the same material
        //           PNG is decoded once across the chunk.
        //       (b) Peak RAM is bounded by one chunk's worth of resident
        //           materials, not by the whole model's texture footprint.
        //           This lets hd / vlrg take the parallel path that the old
        //           unconditional 500 MiB gate forced into single-core serial.
        // G2-SAFE: does the decode-once resident set fit the RAM budget? (memory headroom)
        // Computed here (before phase1Mdop) so the parallelism can adapt to it.
        long _estResident = (long)materials.Count * Obj2Tiles.Library.TexturesCache.MaxResidentEdge
                            * Obj2Tiles.Library.TexturesCache.MaxResidentEdge * 4L;
        bool _predecodeFits = Obj2Tiles.Library.TexturesCache.MaxResidentBytes <= 0
                            || _estResident <= Obj2Tiles.Library.TexturesCache.MaxResidentBytes;

        // G7-PARALLEL / DECODE-PARALLEL: Phase-1 default parallelism uses ALL cores; the live-RAM
        // clamp below scales it down only if the projected peak won't fit. Decode is the dominant
        // large-model cost and was running at mdop≈2 with most cores idle — purely because the clamp
        // reserved the whole resident budget (fixed below). Both the fits-budget AND over-budget paths
        // can use all cores: the over-budget path evicts per-material (concurrency-safe via the
        // b6248f24 lease) + Clears per chunk, so its resident set is TRANSIENT (~mdop in-flight
        // sources, already covered by the per-worker estimate), not the full budget.
        // Byte-identical regardless of mdop (per-tile output is scheduling-independent).
        // Operator override: --phase1-batches-per-material.
        int phase1Mdop = config.Phase1BatchesPerMaterial > 0
            ? config.Phase1BatchesPerMaterial
            : Environment.ProcessorCount;
        // Real DOP is also bounded by --threads (operator can audit at 1 core).
        phase1Mdop = Math.Max(1, Math.Min(phase1Mdop, parallelism));

        // G14 (dev-env strangulation fix): probe what the fits-path start clamp WOULD
        // grant (it must reserve the whole resident set out of live RAM). When the
        // model barely fits its budget — exactly the dev 28300Mi pod: est 11840 MiB vs
        // budget 12735 MiB — that reserve eats the worker headroom (mdop 7 → 1..3; at
        // 1 the runParallel gate forced fully-serial Phase-1 WITH per-material
        // re-decode churn → HLOD slower than legacy). Holding the set is only worth
        // it when workers remain: otherwise DEMOTE to the transient-eviction path
        // (reserve=0 → full DOP, bounded resident, the empirically validated
        // never-OOM machinery). Decision uses only start-time signals — no
        // post-allocation memory sampling (GC MemoryLoadBytes is a last-GC snapshot,
        // not live) and no optimistic per-worker estimates for a held-resident loop.
        // Output is byte-identical either way (same capped decode pixels; per-tile
        // output is scheduling-independent).
        // Provenance gate (Codex round-2/3): only the AUTO-activated cache may be
        // demoted. An explicit --source-cache-cap is the operator's residency choice —
        // decode-once is kept even when the clamp strangles (we log it so the slowdown
        // is never silent). The prod prefect flow passes no explicit cap → auto path.
        if (_predecodeFits && Obj2Tiles.Library.TexturesCache.MaxResidentBytes > 0
                           && Obj2Tiles.Library.TexturesCache.PersistResident)
        {
            int fitsMdop = Phase1AdaptiveMdop(phase1Mdop, _estResident, Obj2Tiles.Library.TexturesCache.MaxResidentEdge);
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

        // Obj2b graceful degradation (over-budget NATIVE-source case, e.g. vlrg --source-cache-cap 8192):
        // the startup G2-SAFE budget (60% of TOTAL RAM) leaves too little headroom for the Phase-1 worker
        // transients (a native cap-sized decode + resample + atlas per worker), so budget + mdop×perWorker
        // + OS can OOM in Phase-1 (confirmed: vlrg --cap 8192 --threads 8 OOMed at ~489s). Two HLOD-only,
        // output-neutral backoffs (legacy uses PersistResident=false → unaffected):
        //  (1) tighten the resident budget to ~40% of LIVE RAM so concurrent workers fit (the per-chunk
        //      Clear() then evicts to it); does NOT change _predecodeFits/path-selection (computed above).
        //  (2) clamp phase1Mdop by LIVE available RAM (re-checked per chunk below as the cache grows).
        // Tighten the resident budget for the over-budget native case so the Phase-1 peak (budget + the
        // mdop=1 worker + OS + LOH fragmentation from churning 256 MiB image buffers) keeps a safe margin
        // on a memory-bound host. ~55% of LIVE RAM for the cache leaves ~45% for worker(s)+OS: measured
        // budget 9.4 GiB/mdop1 peaked at 13.0 GiB (~1 GiB free — too thin under concurrent load); ~55%
        // (~7.5 GiB) → ~11.5 GiB peak (~2.5+ GiB free). A bigger budget is faster (fewer 8192² re-decodes)
        // but the operator's hard requirement is never-OOM > speed. Adapts to LIVE RAM (concurrent load
        // shrinks MemAvailable → tighter); the per-chunk Clear() then evicts to it. HLOD-only (legacy uses
        // PersistResident=false). Does NOT change _predecodeFits/path-selection (computed above).
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
            // Reserve = RAM HELD and unavailable to workers. The fits-budget path holds the decode-once
            // set (_estResident). The over-budget path does NOT hold the budget: per-material eviction +
            // per-chunk Clear keep its resident transient (~mdop sources, already in the per-worker
            // estimate), so reserving the full MaxResidentBytes here over-clamped mdop to ~2 and left
            // cores idle. Reserve 0 there lets the clamp scale mdop by real headroom (≈cores at 8 GiB,
            // fewer at 4 GiB); per-worker (capEdge²×32) still bounds the concurrent decode transient.
            long _reserve = _predecodeFits ? _estResident : 0L;
            int _reqMdop = phase1Mdop;
            phase1Mdop = Phase1AdaptiveMdop(phase1Mdop, _reserve, Obj2Tiles.Library.TexturesCache.MaxResidentEdge);
            if (phase1Mdop != _reqMdop)
                Console.WriteLine($" [perf]   Phase-1 mdop live-RAM clamp {_reqMdop} -> {phase1Mdop} (cap={Obj2Tiles.Library.TexturesCache.MaxResidentEdge}, reserve={_reserve >> 20}MiB, liveAvail={LiveAvailableBytes() >> 20}MiB)");
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
        // G2-M2: when sources are held resident for the whole bake (decode-once cache),
        // pre-decode all materials upfront in parallel so Phase-1 tiles never block on a
        // lazy first-decode (kills decode-wait stalls). Byte-identical (same decoded data,
        // just eager). No-op in legacy mode (PersistResident=false).
        // G2-SAFE: only pre-decode the whole set upfront when it fits the resident budget
        // (_predecodeFits, computed above); else lazy decode + budgeted per-chunk eviction
        // keeps peak bounded on huge models.
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
                // G8-NOCHUNK: when the decode-once set fits the budget, the inter-chunk
                // Clear() is already a no-op (G2-SAFE) AND every source is pre-decoded
                // resident — so the material-aware chunking serves no RAM or decode-dedup
                // purpose; it only imposes per-chunk Parallel.ForEach BARRIERS (the slowest
                // tile in each chunk stalls the next chunk's start). Run ALL tiles in ONE
                // Parallel.ForEach so giant-cluster tiles overlap all other work instead of
                // gating a chunk. Byte-identical (per-tile output is scheduling-independent).
                // The chunked+Clear path below is preserved for the over-budget case (RAM safety).
                // G13: heavy-first schedule. The tile-loop tail is bounded by the slowest tile;
                // material-order leaves heavy tiles (big atlas → more fill/dilate/encode) scattered,
                // so they can start late and tail the loop. Sort by face count DESC so the heaviest
                // tiles start first and the rest backfill. Byte-identical (per-tile output is
                // scheduling-independent; collections are order-independent ConcurrentBags / the
                // tree drives tileset.json).
                var loopTiles = new List<HierarchicalNode>(sortedTiles);
                loopTiles.Sort((a, b) =>
                {
                    int fa = a.TileContentT?.Faces.Length ?? 0;
                    int fb = b.TileContentT?.Faces.Length ?? 0;
                    if (fa != fb) return fb.CompareTo(fa); // heaviest first
                    return string.CompareOrdinal(a.Coord.ToContentUri(isQuadtree), b.Coord.ToContentUri(isQuadtree));
                });
                Parallel.ForEach(
                    loopTiles,
                    new ParallelOptions { MaxDegreeOfParallelism = phase1Mdop },
                    n =>
                    {
                        // No-chunk path runs only when the resident set fits the budget,
                        // so decode-once holds — no per-material eviction needed.
                        var p = PrepareTileForGlb(n, outputDir, isQuadtree, materials, config, maxDepth, evictPerMaterial: false);
                        atlasEdges.Add((n.Depth, p.AtlasEdge));
                        preparedBag.Add(p);
                    });
                chunkCount = 1;
            }
            else
            {
                // Over-budget path: ONE barrier-free pass over ALL tiles. The old code chunked into
                // (phase1Mdop*2)-tile groups with a Parallel.ForEach barrier + Clear() between each — on
                // the large model that was 21 barriers, each stalling on its slowest (shallow/root) tile,
                // throttling the mdop-N decode parallelism to ~3x effective. Per-material eviction
                // (concurrency-safe via the b6248f24 lease) bounds resident to ~mdop in-flight sources —
                // NOT the full budget — so the barriers were never needed for RAM. (A byte-budgeted LRU
                // residency was prototyped to cut the re-decode itself, but it thrashes on shallow tiles
                // whose per-tile material set exceeds the budget — so the win here is parallelism.)
                // Re-clamp mdop once against post-load live RAM (the resident set is steady under
                // per-material eviction, so a single check is the safe mdop — verified: the old per-chunk
                // re-clamp never degraded below the initial value on the large-model bake).
                int _passMdop = Phase1AdaptiveMdop(phase1Mdop, 0L, Obj2Tiles.Library.TexturesCache.MaxResidentEdge);
                if (_passMdop != phase1Mdop)
                    Console.WriteLine($" [perf]   Phase-1 over-budget pass mdop {phase1Mdop} -> {_passMdop} (live-RAM, reserve=0, liveAvail={LiveAvailableBytes() >> 20}MiB)");
                phase1Mdop = _passMdop;   // reflect the effective mdop in the downstream Phase-1 telemetry (Codex item 5)
                // Heavy-first so the slowest tiles start first and the rest backfill (kills the tail
                // stall the chunk barriers used to cause). Byte-identical (per-tile output is
                // scheduling-independent; collections are order-independent ConcurrentBags).
                var loopTiles = new List<HierarchicalNode>(sortedTiles);
                loopTiles.Sort((a, b) =>
                {
                    int fa = a.TileContentT?.Faces.Length ?? 0;
                    int fb = b.TileContentT?.Faces.Length ?? 0;
                    if (fa != fb) return fb.CompareTo(fa); // heaviest first
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
                // One final Clear: per-material eviction already freed sources as it went; this releases
                // any tail before Phase-2/3.
                Obj2Tiles.Library.TexturesCache.Clear();
            }
        }
        else
        {
            foreach (var n in tiles)
            {
                // Non-parallel serial path (config.ParallelPhase1 == false): evict per
                // material as before (single-threaded, dispose-safe).
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

        // KHR_mesh_quantization via gltfpack. Post-process each emitted GLB
        // through gltfpack which applies the configured bit-widths for
        // positions, UVs, normals. Skips with a warning if gltfpack isn't
        // findable.
        if (config.QuantizeGlbs)
        {
            // Auto-detect a runnable gltfpack (PATH, $HOME/bin, common locations) so KTX2 "just works"
            // without --gltfpack-path. A non-interactive bake process often lacks the login PATH, so a
            // bare "gltfpack" can fail even when it's installed — the $HOME/bin fallback covers that.
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

                // Obj2b: free the Phase-1 decode-once cache before Phase-3. gltfpack reads the written GLBs
                // from disk and does NOT use the resident source textures, but holding them (~the tightened
                // budget) starves Phase-3's live-RAM budget — measured only 2.9 GiB free → 1 KTX2 worker →
                // ~30-min vlrg. Evicting it (+ a one-shot LOH compaction) returns that RAM so Phase-3 runs
                // several concurrent gltfpack encodes. Output-neutral (frees decoded source pixels, not any
                // bake output); doesn't raise the overall peak (Phase-1's per-tile working set dominates).
                if (Obj2Tiles.Library.TexturesCache.PersistResident)
                {
                    long _prevBudget = Obj2Tiles.Library.TexturesCache.MaxResidentBytes;
                    Obj2Tiles.Library.TexturesCache.MaxResidentBytes = 1;   // make the next Clear() evict all
                    Obj2Tiles.Library.TexturesCache.Clear();
                    Obj2Tiles.Library.TexturesCache.MaxResidentBytes = _prevBudget;   // restore — don't leave the shared cache un-budgeted for any later/library use
                }
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();

                // GRACEFUL DEGRADATION (mirrors G7/G2-SAFE): the KTX2/ETC1S encode is memory-heavy
                // (BasisU working set ≈ 1.8 GB for a 4096² atlas). Running `parallelism` (= --threads)
                // concurrent gltfpack processes OOM-kills memory-constrained hosts (vlrg KTX2 @ threads 8
                // exceeded 15 GB). Cap the worker count by a memory budget so the bake COMPLETES instead of
                // OOMing; on roomy prod hosts (256-400 GB) the cap relaxes back to `parallelism`.
                // gltfpack -tc only applies when the gltfpack encoder is selected.
                // In basisu mode the .ktx2 atlases are already embedded
                // (KHR_texture_basisu) by the time the GLB exists, so the
                // gltfpack post-process (if it even runs) must NOT add -tc.
                bool ktx = config.Ktx2Hierarchical
                    && string.Equals(config.Ktx2Encoder, "gltfpack", StringComparison.OrdinalIgnoreCase);
                int ktxWorkers = parallelism;
                if (ktx)
                {
                    int maxAtlasEdge = 0;
                    foreach (var p in prepared) if (p.AtlasEdge > maxAtlasEdge) maxAtlasEdge = p.AtlasEdge;
                    if (maxAtlasEdge <= 0) maxAtlasEdge = config.MaxAtlasSize > 0 ? config.MaxAtlasSize : 4096;
                    // gltfpack embeds the atlas CAPPED at MaxAtlasSize, so the ETC1S encode works on the
                    // capped texture (p.AtlasEdge can be the larger natural pack edge). Per-worker cost is the
                    // gltfpack process's own RSS — MEASURED ~0.9 GiB for a 4096²-capped atlas (the old
                    // 1.8 GB/4096² × (natural/4096)² estimate was ~8× too high → it over-capped hd/vlrg to 1
                    // worker → 30-min hd KTX2). Budget against LIVE available RAM (reflects resident bake state
                    // still held into Phase-3) so the concurrent gltfpack processes fit. Output-neutral (per-tile).
                    int effEdge = config.MaxAtlasSize > 0 ? Math.Min(maxAtlasEdge, config.MaxAtlasSize) : maxAtlasEdge;
                    double scale = (double)effEdge / 4096.0;
                    long perWorkerMib = (long)(1300 * scale * scale);   // ~0.9 GiB measured + margin at 4096²
                    if (perWorkerMib < 512) perWorkerMib = 512;
                    long availMib = LiveAvailableBytes() / (1024 * 1024);
                    long budgetMib = (long)(availMib * 0.55);
                    int byMem = (int)Math.Max(1, budgetMib / perWorkerMib);
                    ktxWorkers = Math.Clamp(byMem, 1, parallelism);
                    // Operator escape hatch: HLOD_KTX2_WORKERS pins the concurrent-encode count if the
                    // measured heuristic above (~0.9 GiB/worker, 0.55×live-RAM budget) misjudges a host or an
                    // unusual atlas/texture profile.
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
                // Largest-atlas-first: schedule the heaviest (most memory + slowest) encodes first so they
                // don't pile up at the tail; small tiles backfill. Output is order-independent (per-tile).
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
                        // Also apply EXT_meshopt_compression when requested.
                        // Renderer needs the meshopt decoder (CesiumJS has it
                        // built-in; deck.gl + 3DTilesRendererJS need
                        // MeshoptDecoder loaded externally).
                        if (config.MeshoptCompress) psi.ArgumentList.Add("-c");
                        if (ktx)
                        {
                            // Convert embedded JPEG atlases to KTX2/Basis
                            // ETC1S via gltfpack -tc + -tq N (quality 1-10).
                            // `ktx` is true ONLY in the gltfpack encoder path;
                            // basisu mode embeds .ktx2 before the GLB exists.
                            psi.ArgumentList.Add("-tc");
                            psi.ArgumentList.Add("-tq");
                            psi.ArgumentList.Add(Math.Clamp(config.Ktx2Quality, 1, 10).ToString());
                            // -tj 1: single texture-encode thread PER gltfpack process. We run tiles in
                            // parallel (ktxWorkers processes); without this each process spawns
                            // hardware-concurrency BasisU threads → N×cores oversubscription that thrashes
                            // and bloats peak RSS. ETC1S output is bit-identical regardless of thread count,
                            // so this is a pure scheduling fix (no quality loss).
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
                    // Detect a gltfpack built WITHOUT BasisU: it starts, accepts -tc, exits 0, but silently
                    // emits plain JPEG instead of KTX2 — which the exit-code/temp-file success check above
                    // cannot distinguish. Verify one converted GLB actually embeds KTX2; warn loudly if not
                    // (tiles stay JPEG despite --quantize-glbs, e.g. a non-BasisU gltfpack resolved on PATH).
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
        // Primary material = the MaterialIndex appearing in the most faces in
        // this tile. Used to sort tiles for material-aware Phase-1 batching:
        // adjacent tiles in the processing order share at least their primary
        // source texture, so the lazy TexturesCache decodes that PNG once per
        // chunk instead of once per tile.
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
        // The MeshT writes its OBJ via WriteGeometry; PackAndWrite already
        // ran SaveAtlasesAndUpdateMaterial so we just need the OBJ + MTL on
        // disk to feed Obj2Gltf. WriteGeometry also calls WriteMaterial.
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
            // Meshopt optimize chain disabled. The chain reorders vertices
            // and remaps indices in a way that produces spurious long-edge
            // triangles. The build-report MaxLeafEdgeLength gate enforces
            // the invariant. Re-enabling this requires fixing the
            // underlying index/UV-split handling in Obj2Gltf's optimize
            // pipeline.
            ApplyMeshoptOptimization = false,
            EncodeMeshoptCompression = false,
            // Per-bake sampler-mip-disable opt-in.
            LeafNoMips = config.LeafNoMips,
        };
        converter.Convert(p.ObjPath, p.GltfPath, convOpts);

        // Force doubleSided=true on every material. The pipeline can emit
        // occasional triangles whose winding can't be recovered from input
        // topology — visible as back-facing "snake-crack" ribbons in coarse
        // LODs. Photogrammetry is unlit (baked colour) so doubleSided is
        // visually free; it just stops the renderer from culling the wrong
        // face.
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

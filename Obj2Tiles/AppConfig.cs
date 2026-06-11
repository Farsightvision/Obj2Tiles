using System.Collections.Generic;

namespace Obj2Tiles;

/// <summary>
/// Per-tile atlas-sizing strategy. Triangle count is the wrong normalizer for
/// texel density; world-space surface area is the canonical "texel density"
/// quantity used industry-wide.
/// </summary>
public enum AtlasStrategy
{
    /// <summary>Natural packing-driven, clamped at MaxAtlasSize.</summary>
    Natural = 0,
    /// <summary>side = clamp(NextPow2(sqrt(A_world * D_target)), Min, Max). Surface-area-normalized.</summary>
    AreaUniform = 1,
}

public class AppConfig
{
    public string Input { get; set; }
    public string Output { get; set; }
    public int MaxVerticesPerTile { get; set; }
    public double PackingThreshold { get; set; }
    public bool KeepIntermediateFiles { get; set; }
    public LodConfig[] LODs { get; set; }
    public int ThreadsCount { get; set; }
    public bool UseKtxTextures { get; set; }
    public int MaxTotalAtlasArea { get; set; }
    public double BaseError { get; set; } = 100.0;
    /// <summary>
    /// When true, the binary runs the hierarchical (HLOD) pipeline. Default
    /// false = flat-grid LOD (master behavior). Set via the
    /// <c>--hierarchical-lods</c> CLI flag.
    /// </summary>
    public bool HierarchicalLods { get; set; } = false;
    public bool ForceZSplit { get; set; } = false;
    public bool NoMeshoptCompression { get; set; } = false;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double Altitude { get; set; } = 0;
    public double Scale { get; set; } = 1;
    public bool YUpToZUp { get; set; } = false;

    /// <summary>
    /// Per-tile atlas size cap (pixels per edge) for the hierarchical pipeline.
    /// Default 4096 matches the flat-grid per-tile budget. Set via the
    /// <c>--max-atlas-size</c> CLI flag. Per-LOD override available via
    /// <c>--max-atlas-size-internal</c> for non-leaf tiles where the higher
    /// cap would waste bytes (interior nodes typically have heavily simplified
    /// geometry whose UV-area doesn't fill 4096²). The flat-grid pipeline
    /// ignores this and uses per-LOD <see cref="LodConfig.MaxAtlasSize"/>.
    /// </summary>
    public int MaxAtlasSize { get; set; } = 4096;

    /// <summary>
    /// Phase-8 decode-once experiment. &gt;0 = decode each source texture once,
    /// downsample to this edge cap, hold resident for the whole bake (see
    /// <see cref="Obj2Tiles.Library.TexturesCache.MaxResidentEdge"/>). 0 = off.
    /// </summary>
    public int SourceCacheCap { get; set; }

    /// <summary>True when the source cache was AUTO-activated (Phase1AutoCachePolicy)
    /// rather than via an explicit --source-cache-cap. G14 fits-path demotion is
    /// gated on this: an explicit cap is the operator's residency choice (decode-once
    /// kept even if the worker clamp strangles); the auto path may trade decode-once
    /// for parallelism.</summary>
    public bool SourceCacheCapAutoEnabled { get; set; }

    /// <summary>
    /// Per-tile atlas cap for INTERNAL (non-leaf) HLOD nodes. Leaves get
    /// <see cref="MaxAtlasSize"/>; internal nodes get this smaller cap because
    /// their simplified geometry has lower UV-density and a 4096² atlas there
    /// is mostly wasted/empty. 0 = use <see cref="MaxAtlasSize"/> for all depths.
    /// Superseded by <see cref="AtlasMaxDepthSchedule"/> when a schedule is
    /// provided; kept as the fallback when no per-depth entry matches.
    /// </summary>
    public int MaxAtlasSizeInternal { get; set; } = 2048;

    /// <summary>
    /// Per-depth atlas cap schedule. Shallow LODs are viewed from far away and
    /// their atlases are mostly downsample mips; sizing them at the same
    /// <see cref="MaxAtlasSize"/> as leaves wastes both download bytes and GPU
    /// decode memory. Default schedule:
    ///   depth 0 → 512²  (root, lowest detail; viewed at default zoom only)
    ///   depth 1 → 1024²
    ///   depth 2 → 1536²
    ///   depth 3 → 2048²
    ///   depth 4 → 4096²  (leaves; full detail for close-zoom)
    /// Set via CLI <c>--atlas-max-depth-schedule "0:512,1:1024,..."</c>. Missing
    /// entries fall back to <see cref="MaxAtlasSizeInternal"/> (internal) or
    /// <see cref="MaxAtlasSize"/> (leaf).
    /// </summary>
    public Dictionary<int, int> AtlasMaxDepthSchedule { get; set; } = new Dictionary<int, int>
    {
        { 0, 512 },
        { 1, 1024 },
        { 2, 1536 },
        { 3, 2048 },
        { 4, 4096 },
    };

    /// <summary>
    /// True when the user explicitly passed <c>--lods</c> on the CLI. Used by
    /// the hierarchical pipeline to decide whether to use the built-in LOD
    /// schedule (<c>false</c>) or honor the user's explicit override (<c>true</c>).
    /// </summary>
    public bool UserProvidedLods { get; set; }

    /// <summary>
    /// When true (default), the hierarchical pipeline selects maxDepth from
    /// the input mesh metrics (ModelMetrics + 2-level B_eff dry run +
    /// OptimalDepthsClosedForm). Set false via <c>--auto-depth=false</c> to
    /// opt back into the fixed-5 behavior for parity comparisons.
    /// </summary>
    public bool AutoDepth { get; set; } = true;

    /// <summary>
    /// Per-leaf triangle target for OptimalDepthsClosedForm. Default 25_000.
    /// Only consulted when <see cref="AutoDepth"/> is true and
    /// <see cref="MaxDepthOverride"/> == 0.
    /// </summary>
    public int TLeafTri { get; set; } = 25_000;

    /// <summary>
    /// Per-leaf source-texture-bytes target for OptimalDepthsClosedForm's
    /// texture axis. Default 50_000_000 (50 MB / leaf). Dense-texture fixtures
    /// get pushed to a deeper tree when this axis dominates the triangle axis.
    /// Only consulted when <see cref="AutoDepth"/> is true and
    /// <see cref="MaxDepthOverride"/> == 0. Set to 0 to disable the axis.
    /// </summary>
    public long TLeafTextureBytes { get; set; } = 50_000_000L;

    /// <summary>
    /// Skip the ExtendAdaptive pass when true. ExtendAdaptive deepens leaves
    /// whose ideal_side > MaxAtlasSize. With this flag set, tree shape stops
    /// at PruneAdaptive and atlas density at large leaves drops to whatever
    /// the cap supports.
    /// </summary>
    public bool NoAdaptiveExtend { get; set; } = false;

    /// <summary>
    /// Explicit hard ceiling for the ExtendAdaptive recursion. 0 = use the
    /// autoDepth+3 default. Trades off tile count vs per-leaf atlas size —
    /// lower values keep leaf cap small while spawning more leaves via
    /// ExtendAdaptive.
    /// </summary>
    public int AdaptiveExtendMaxDepth { get; set; } = 0;

    /// <summary>
    /// Auto-pick <see cref="MaxAtlasSize"/> from a per-tile decoded-RGBA budget
    /// (megabytes). 0 = manual <see cref="MaxAtlasSize"/>. When &gt; 0,
    /// cap = round_pow2(sqrt(MB × 1024² / 4)). Density px/m is preserved by
    /// ExtendAdaptive's idealSide-vs-cap predicate (more tiles at smaller
    /// cap). Designed for JPEG/RGBA clients where decoded VRAM is the
    /// bottleneck; KTX2 gets ~4× the same budget for free.
    /// </summary>
    public int LeafVramBudgetMb { get; set; } = 0;

    /// <summary>
    /// OPTIONAL safety abort. 0 (default) = unbounded; the bake always
    /// proceeds. When the operator sets &gt; 0, aborts the bake after
    /// ExtendAdaptive if the total tree node count exceeds the value. Used
    /// when there's a known disk / wall-clock budget. The tool WARNs (does
    /// not abort) on unusually deep trees by itself; this is the operator's
    /// hard ceiling. Refusing to bake is a bug, not a feature — the default
    /// must produce a tileset for any input.
    /// </summary>
    public int MaxTileCount { get; set; } = 0;

    /// <summary>
    /// Unsharp-mask sharpening strength applied to atlas images before JPEG
    /// encode. 0 = no sharpen; 0.5 = moderate; 1.0 = strong. A tunable middle
    /// between default-mips (soft at distance) and no-mips (sharper but
    /// corrugated surfaces alias in motion).
    /// </summary>
    public double AtlasUnsharpAmount { get; set; } = 0.0;

    /// <summary>
    /// When true, the Obj2Gltf converter writes minFilter=LINEAR (no mips) on
    /// emitted samplers. The renderer then samples the base atlas always —
    /// maximizes sharpness at the cost of aliasing high-frequency surfaces
    /// under motion.
    /// </summary>
    public bool LeafNoMips { get; set; } = false;

    /// <summary>
    /// Explicit override for hierarchical maxDepth (0 = honor
    /// <see cref="AutoDepth"/> selector). Any value &gt; 0 bypasses the
    /// dynamic selector and feeds N directly to BuildTreeConformal. Use 5 to
    /// reproduce the fixed-5 behavior.
    /// </summary>
    public int MaxDepthOverride { get; set; } = 0;

    /// <summary>
    /// Post-process every emitted GLB with gltfpack to apply
    /// KHR_mesh_quantization (14-bit positions, 12-bit UVs, 8-bit normals).
    /// Default false; opt-in via <c>--quantize-glbs</c>.
    /// </summary>
    public bool QuantizeGlbs { get; set; } = false;

    /// <summary>
    /// Explicit path to the gltfpack binary. If empty, the post-process step
    /// falls back to "gltfpack" on $PATH. Only consulted when
    /// <see cref="QuantizeGlbs"/> is true.
    /// </summary>
    public string GltfpackPath { get; set; } = "";

    /// <summary>
    /// Convert per-tile JPEG atlases to KTX2/Basis ETC1S via gltfpack -tc.
    /// Only effective with <see cref="QuantizeGlbs"/>. KTX2 + ETC1S is
    /// GPU-native (no RGBA8 decode at load time), reducing both disk size
    /// and GPU memory at peak. Opt-out via <c>--no-ktx2-hierarchical</c> or
    /// <c>--ktx2-hierarchical=false</c> (for clients without
    /// KHR_texture_basisu support).
    /// </summary>
    public bool Ktx2Hierarchical { get; set; } = true;

    /// <summary>
    /// gltfpack KTX2 quality (1-10, higher = larger but better). Default 8 —
    /// matches gltfpack's own default for ETC1S. Lower values shrink atlases
    /// further at quality cost; 10 is near-lossless ETC1S.
    /// </summary>
    public int Ktx2Quality { get; set; } = 8;

    /// <summary>
    /// Which encoder produces the per-tile KTX2 atlases under the hierarchical
    /// pipeline. <c>"basisu"</c> (default) encodes each atlas image to <c>.ktx2</c>
    /// with the standalone <c>basisu</c> binary BEFORE the GLB is built
    /// (embedded via KHR_texture_basisu by <c>Gltf2GlbConverter</c>) — needs
    /// NO gltfpack, so the prod image (basisu only) works unchanged, and
    /// quantize-glbs/meshopt-compress stay OFF (they require gltfpack).
    /// <c>"gltfpack"</c> runs <c>gltfpack -tc</c> as a GLB post-process and also
    /// enables KHR_mesh_quantization + EXT_meshopt_compression (future opt-in;
    /// needs gltfpack-with-BasisU on PATH). Set via <c>--ktx2-encoder</c>.
    /// </summary>
    public string Ktx2Encoder { get; set; } = "basisu";

    /// <summary>
    /// Pass -c to gltfpack so the post-process step also applies
    /// EXT_meshopt_compression on top of quantization. Renderer needs the
    /// meshopt decoder — CesiumJS ships it; deck.gl/3DTilesRendererJS need
    /// MeshoptDecoder loaded externally.
    /// </summary>
    public bool MeshoptCompress { get; set; } = false;

    /// <summary>
    /// Which atlas-sizing strategy to use. <see cref="AtlasStrategy.Natural"/>
    /// is the default packing-driven behavior. <see cref="AtlasStrategy.AreaUniform"/>
    /// sizes each tile's atlas from world-space surface area × density target
    /// (industry-standard texel density).
    /// </summary>
    public AtlasStrategy AtlasStrategy { get; set; } = AtlasStrategy.Natural;

    /// <summary>
    /// Linear texel density at the leaf LOD, in px/m. Coarser LODs halve
    /// linear density per level (D_d = (LeafDensity / 2^(maxDepth-d))²).
    /// 512 px/m is the Naughty Dog Uncharted tiling-texture spec and a
    /// near-master-quality target for drone photogrammetry (1–3 cm GSD).
    /// 256 px/m is the byte-conservative Beyond Extent environment default.
    /// </summary>
    public int AtlasLeafDensityPxPerM { get; set; } = 512;

    /// <summary>
    /// Enable the source-detail floor. For each face:
    /// <c>d_src = W·H·|U_face| / A_face_world</c> (texels/m²). Take the
    /// area-weighted Q75 across the tile's faces. If the source carries more
    /// detail than <c>D_target(depth)</c> would allocate, raise it up to this
    /// floor (capped at <see cref="AtlasSourceDetailCapPxPerM"/>). Prevents
    /// under-sizing tiles whose photogrammetry GSD is finer than the LOD
    /// baseline.
    /// </summary>
    public bool AtlasUseSourceDetailFloor { get; set; } = true;

    /// <summary>
    /// Hard floor on atlas side after the formula + Pow2 round. 256 px is the
    /// smallest size where dilate-bleed gutters (16 px) leave a useful content
    /// region.
    /// </summary>
    public int AtlasMinSize { get; set; } = 256;

    /// <summary>
    /// Ceiling on the source-detail estimate when the source-detail floor is
    /// active. Prevents a tile with anomalously fine source-density from
    /// blowing through <see cref="MaxAtlasSize"/>. 1024 px/m is the practical
    /// upper bound where drone-camera GSD still resolves real detail.
    /// </summary>
    public int AtlasSourceDetailCapPxPerM { get; set; } = 1024;

    /// <summary>
    /// Factor by which to scale the texture-error contribution to a non-leaf
    /// tile's emitted geometricError. Formula:
    ///   <c>effectiveGE(tile) = max(meshError, (worldExtent/atlasSide) × factor)</c>
    /// followed by strict bottom-up monotonicity (parent ≥ max child × 1.01).
    /// <c>worldExtent</c> is the longest horizontal axis of the tile's bbox;
    /// <c>atlasSide</c> is the predicted pow2 pack-time side. Default 16
    /// saturates refinement quality on the typical close-zoom blur case;
    /// higher values add no visible benefit. Set to 1 to disable.
    /// On fixtures where mesh error already dominates the formula's
    /// <c>max()</c>, this factor has no effect.
    /// </summary>
    public double TextureErrorFactor { get; set; } = 16.0;

    /// <summary>
    /// Parallelize Phase-1 atlas pack across cores. Set at runtime in Program
    /// based on <c>ModelMetrics.TextureBytes</c> — small inputs (≤ 500 MB
    /// compressed source textures) can afford to hold all source textures in
    /// <c>TexturesCache</c> simultaneously, so Phase 1 can run in
    /// <c>Parallel.ForEach</c> without the eviction race. Large inputs keep
    /// the serial Phase 1 + per-material <c>EvictTexture</c> path. Not a
    /// user-facing CLI flag.
    /// </summary>
    public bool ParallelPhase1 { get; set; } = false;

    /// <summary>
    /// Max degree of parallelism for HLOD Phase-1 (atlas pack + OBJ/MTL write).
    /// 0 = ProcessorCount / 2. The Phase-1 path now batches tiles with a
    /// shared <c>TexturesCache</c> so concurrent tiles reuse one decoded
    /// source per material; capping MaxDOP keeps peak RAM bounded under the
    /// host budget when the texture set is much larger than RAM÷ProcessorCount.
    /// </summary>
    public int Phase1BatchesPerMaterial { get; set; } = 0;
}

public class LodConfig
{
    public float Quality { get; set; }
    public bool SaveVertexColor { get; set; }
    public bool SaveUv { get; set; }
    public byte KtxQuality { get; set; }
    public byte KtxCompressionLevel { get; set; }

    /// <summary>JPEG quality for atlas textures (1-100). Default: 90.</summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>
    /// Flat-grid per-LOD atlas cap (pixels per edge). Default 0 →
    /// <c>MeshT</c> falls back to its built-in default (4096). The
    /// hierarchical pipeline ignores this in favor of
    /// <see cref="AppConfig.MaxAtlasSize"/>.
    /// </summary>
    public int MaxAtlasSize { get; set; } = 0;
}

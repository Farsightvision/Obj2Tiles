using CommandLine;

namespace Obj2Tiles;

public sealed class Options
{
    [Option("config", Required = false, HelpText = "Config file.")]
    public string Config { get; set; }
    
    [Option("input", Required = false, HelpText = "Input OBJ file.")]
    public string Input { get; set; }

    [Option("output", Required = false, HelpText = "Output folder.")]
    public string Output { get; set; }

    [Option("max-vertices", Required = false, HelpText = "Max vertex count per tile. Default: 4000 on the flat-grid pipeline (the default; master-parity), 1500 with --hierarchical-lods (drives the HLOD tree depth — lower = deeper tree = smaller per-tile atlases).", Default = 0)]
    public int MaxVerticesPerTile { get; set; }

    [Option("max-atlas-size", Required = false, HelpText = "Per-leaf-tile atlas size cap (pixels per edge), used by --hierarchical-lods. Default 4096. Internal HLOD nodes use --max-atlas-size-internal. The flat-grid pipeline ignores this and uses per-LOD MaxAtlasSize from --lods.", Default = 4096)]
    public int MaxAtlasSize { get; set; }

    [Option("max-atlas-size-internal", Required = false, HelpText = "Per-tile atlas cap for INTERNAL (non-leaf) HLOD nodes. Default 2048 — interior nodes have heavily simplified geometry whose UV-area doesn't usefully fill 4096². 0 = use --max-atlas-size for all depths. Superseded by --atlas-max-depth-schedule when a schedule is provided (kept as a fallback). Only consulted under --hierarchical-lods.", Default = 2048)]
    public int MaxAtlasSizeInternal { get; set; }

    [Option("atlas-max-depth-schedule", Required = false, HelpText = "Comma-separated per-depth atlas cap schedule, e.g. '0:512,1:1024,2:1536,3:2048,4:4096'. Shallow LODs use smaller caps (saves download + GPU memory at default zoom). Empty string preserves the --max-atlas-size / --max-atlas-size-internal split.", Default = "0:512,1:1024,2:1536,3:2048,4:4096")]
    public string AtlasMaxDepthSchedule { get; set; } = "0:512,1:1024,2:1536,3:2048,4:4096";

    [Option("packing-threshold", Required = false, HelpText = "Minimum fill ratio required to skip texture compression. If the atlas is less packed than this, compression is applied.", Default = 0.618)]
    public double PackingThreshold { get; set; }
    
    [Option('x', "use-ktx-textures", Required = false, HelpText = "Use ktx textures compression", Default = false)]
    public bool UseKtxTextures { get; set; }
    
    [Option("lat", Required = false, HelpText = "Latitude of the mesh", Default = null)]
    public double? Latitude { get; set; }
    
    [Option("lon", Required = false, HelpText = "Longitude of the mesh", Default = null)]
    public double? Longitude { get; set; }
    
    [Option("alt", Required = false, HelpText = "Altitude of the mesh (meters)", Default = 0)]
    public double Altitude { get; set; }
    
    [Option("scale", Required = false, HelpText = "Scale for data if using units other than meters (e.g. 1200.0/3937.0 for survey ft)", Default = 1.0)]
    public double Scale { get; set; }
    
    [Option('e',"error", Required = false, HelpText = "Base geometric error for root node.", Default = 100.0)]
    public double BaseError { get; set; }

    [Option("keep-intermediate", Required = false, HelpText = "Keeps the intermediate files (do not cleanup)", Default = false)]
    public bool KeepIntermediateFiles { get; set; }
    
    [Option("threads", Required = false, HelpText = "Count threads for parallel ktx compression", Default = 8)]
    public int ThreadsCount { get; set; }

    [Option("phase1-batches-per-material", Required = false, HelpText = "HLOD: max degree of parallelism for Phase-1 atlas-pack stage. 0 = ProcessorCount/2. Bounds peak RAM in the parallel path when the texture set is large.", Default = 0)]
    public int Phase1BatchesPerMaterial { get; set; }

    [Option("source-cache-cap", Required = false, HelpText = "HLOD: decode each source texture ONCE, downsample so its longest edge <= N px, and hold it resident for the whole bake (no per-chunk Clear). Eliminates the ~7x re-decode the HLOD chunk-Clear forces and uses LESS peak RAM than full-res. Set to --max-atlas-size to keep all usable detail (no atlas exceeds the cap). 0 = off (legacy full-res, re-decode per chunk).", Default = 0)]
    public int SourceCacheCap { get; set; }

    [Option("max-atlas-area", Required = false, HelpText = "Maximum total atlas area for batch processing (pixels squared)", Default = 8102 * 8102)]
    public int MaxTotalAtlasArea { get; set; }

    [Option('l', "lods", Required = false, HelpText = "LODs JSON. Required on the default flat-grid pipeline (one entry per LOD level). Under --hierarchical-lods this is optional — when omitted the hierarchical pipeline auto-derives the count and Q schedule from mesh size.")]
    public string LODs { get; set; }

    [Option('t', "y-up-to-z-up", Required = false, HelpText = "Convert the upward Y-axis to the upward Z-axis, which is used in some situations where the upward axis may be the Y-axis or the Z-axis after the obj is exported.", Default = false)]
    public bool YUpToZUp { get; set; }

    [Option("hierarchical-lods", Required = false, HelpText = "Opt in to the hierarchical (HLOD) pipeline. Default OFF — the default pipeline is the flat-grid LOD algorithm. When set, the binary builds a UV-aware HLOD tree with per-tile atlases (see --max-atlas-size, --max-depth, --auto-depth, --atlas-max-depth-schedule).", Default = false)]
    public bool HierarchicalLods { get; set; }

    [Option('z', "zsplit", Required = false, HelpText = "Force octree subdivision (default: auto-pick from input AABB aspect ratio per spec §4.1).", Default = false)]
    public bool ForceZSplit { get; set; }

    [Option("no-meshopt-compression", Required = false, HelpText = "Disable EXT_meshopt_compression (debugging or legacy clients).", Default = false)]
    public bool NoMeshoptCompression { get; set; }

    [Option("auto-depth", Required = false, HelpText = "Select hierarchical maxDepth from the input mesh (triangle/vertex count, estimated effective branching). Default ON. To override use --max-depth N (any value > 0 overrides).", Default = true)]
    public bool AutoDepth { get; set; }

    [Option("max-depth", Required = false, HelpText = "Explicit override for hierarchical maxDepth. 0 = honor --auto-depth (default). Any value > 0 bypasses the dynamic selector and uses N directly. Use 5 for the original fixed-5 behavior.", Default = 0)]
    public int MaxDepth { get; set; }

    [Option("t-leaf-tri", Required = false, HelpText = "Per-leaf triangle target for OptimalDepthsClosedForm. Default 25_000. Only meaningful with --auto-depth.", Default = 25_000)]
    public int TLeafTri { get; set; }

    [Option("t-leaf-texture-bytes", Required = false, HelpText = "Per-leaf source-texture-bytes target. Default 50_000_000 (50 MB / leaf). Dense-texture fixtures auto-deepen when this axis dominates the triangle axis. 0 disables the texture axis.", Default = 50_000_000L)]
    public long TLeafTextureBytes { get; set; }

    [Option("no-adaptive-extend", Required = false, HelpText = "Skip the ExtendAdaptive pass that subdivides any leaf whose ideal_side > MaxAtlasSize. With this flag the tree shape stops at PruneAdaptive; atlas density at large leaves drops to whatever the cap supports.", Default = false)]
    public bool NoAdaptiveExtend { get; set; }

    [Option("adaptive-extend-max-depth", Required = false, HelpText = "Explicit hard ceiling for the ExtendAdaptive recursion. 0 = use the autoDepth+3 default. Set to a value < (autoDepth + 3) to constrain growth and keep per-leaf atlas at ≤ --max-atlas-size while still gaining leaf count from finer tiling.", Default = 0)]
    public int AdaptiveExtendMaxDepth { get; set; }

    [Option("leaf-vram-budget-mb", Required = false, HelpText = "Format-agnostic per-tile decoded-texture VRAM budget (megabytes). When > 0, AUTO-picks --max-atlas-size: cap = round_pow2(sqrt(MB * 1024 * 1024 / 4)). 4 MB → 1024, 16 MB → 2048, 64 MB → 4096. JPEG/PNG decode to RGBA (4 bytes/texel); KTX2/BC1 is ~0.5 bytes/texel so the same budget fits a larger cap. Density px/m is preserved by ExtendAdaptive. Default 0 = manual --max-atlas-size; setting > 0 overrides it.", Default = 0)]
    public int LeafVramBudgetMb { get; set; }

    [Option("max-tile-count", Required = false, HelpText = "OPTIONAL safety abort. When > 0, aborts the bake immediately after ExtendAdaptive if the total tree node count exceeds this value. 0 (default) = unbounded; the bake always proceeds. Use this when you have a known disk / wall-clock budget and want a fail-fast guard against an unexpectedly deep tree.", Default = 0)]
    public int MaxTileCount { get; set; }

    [Option("atlas-unsharp-amount", Required = false, HelpText = "Apply unsharp-mask sharpening to atlas images before JPEG encode (per tile). 0 = no sharpen (default). 0.5 = moderate. 1.0 = strong. Sharpens the base atlas so the auto-generated mip chain inherits the boost — a tunable middle between default mips (soft at distance) and no mips (sharper but aliases on corrugated surfaces).", Default = 0.0)]
    public double AtlasUnsharpAmount { get; set; }

    [Option("leaf-no-mips", Required = false, HelpText = "Set sampler minFilter=LINEAR (9729) on the emitted glTF samplers so the renderer does not auto-generate (or use) mips. Maximizes sharpness but corrugated high-frequency surfaces (e.g. metal roofs) shimmer/alias under motion. Default false. Use --atlas-unsharp-amount for a tunable middle that avoids the shimmer.", Default = false)]
    public bool LeafNoMips { get; set; }

    [Option("quantize-glbs", Required = false, HelpText = "Post-process every emitted GLB with gltfpack to apply KHR_mesh_quantization (14-bit positions, 12-bit UVs, 8-bit normals). Default off. Requires gltfpack on PATH or via --gltfpack-path. Skips with warning if binary not found.", Default = false)]
    public bool QuantizeGlbs { get; set; }

    [Option("gltfpack-path", Required = false, HelpText = "Explicit path to the gltfpack binary. If empty, falls back to 'gltfpack' on PATH. Only consulted when --quantize-glbs is set.", Default = "")]
    public string GltfpackPath { get; set; } = "";

    [Option("meshopt-compress", Required = false, HelpText = "Pass -c to gltfpack to also apply EXT_meshopt_compression on top of quantization. Renderer must support the meshopt decoder — CesiumJS ships it; deck.gl/3DTilesRendererJS need MeshoptDecoder loaded. Default off; only effective with --quantize-glbs.", Default = false)]
    public bool MeshoptCompress { get; set; }

    // Per-default overrides for the HLOD profile; no effect on the flat pipeline.
    [Option("no-quantize-glbs", Required = false, HelpText = "Under --hierarchical-lods, disable the default KHR_mesh_quantization gltfpack post-process. No effect on the flat pipeline.", Default = false)]
    public bool NoQuantizeGlbs { get; set; }

    [Option("no-meshopt-compress", Required = false, HelpText = "Under --hierarchical-lods, disable the default EXT_meshopt_compression. No effect on the flat pipeline.", Default = false)]
    public bool NoMeshoptCompress { get; set; }

    [Option("leaf-mips", Required = false, HelpText = "Under --hierarchical-lods, re-enable mipmaps (undo the default leaf-no-mips). No effect on the flat pipeline.", Default = false)]
    public bool LeafMips { get; set; }

    [Option("adaptive-extend", Required = false, HelpText = "Under --hierarchical-lods, re-enable adaptive depth extension (undo the default no-adaptive-extend). No effect on the flat pipeline.", Default = false)]
    public bool AdaptiveExtend { get; set; }

    [Option("ktx2-hierarchical", Required = false, HelpText = "Convert per-tile atlases to KTX2/Basis ETC1S via gltfpack -tc. Only effective with --quantize-glbs. GPU-native (no RGBA8 decode at load time): smaller disk and dramatically lower GPU memory at peak. Default ON. Opt-out via --ktx2-hierarchical=false OR --no-ktx2 for legacy clients without KHR_texture_basisu support.", Default = true)]
    public bool Ktx2Hierarchical { get; set; } = true;

    [Option("no-ktx2", Required = false, HelpText = "Ergonomic JPEG-only HLOD mode. Equivalent to --ktx2-hierarchical=false. All other HLOD features (adaptive depth, per-depth atlas schedule, texture-aware geometric error, KHR_mesh_quantization, EXT_meshopt_compression) remain ON. Use for legacy mobile clients or browsers without KHR_texture_basisu.", Default = false)]
    public bool NoKtx2 { get; set; }

    [Option("ktx2-quality", Required = false, HelpText = "gltfpack KTX2 quality (1-10, higher = larger but better). Default 8 (gltfpack's own ETC1S default).", Default = 8)]
    public int Ktx2Quality { get; set; } = 8;

    [Option("ktx2-encoder", Required = false, HelpText = "Which encoder produces the per-tile KTX2 atlases under --hierarchical-lods. 'basisu' (default) encodes each atlas image to .ktx2 with the standalone basisu binary BEFORE the GLB is built (the converter embeds it via KHR_texture_basisu). basisu mode needs NO gltfpack, so quantize-glbs/meshopt-compress stay OFF (they require gltfpack); leaf-no-mips + adaptive-extend-off stay on. 'gltfpack' runs gltfpack -tc as a GLB post-process and also enables KHR_mesh_quantization + EXT_meshopt_compression (future opt-in; needs gltfpack-with-BasisU on PATH).", Default = "basisu")]
    public string Ktx2Encoder { get; set; } = "basisu";

    [Option("atlas-strategy", Required = false, HelpText = "Atlas-sizing strategy. 'Natural' (default, packing-driven), 'AreaUniform' (sqrt(A_world × D_target), industry texel-density).", Default = "Natural")]
    public string AtlasStrategy { get; set; } = "Natural";

    [Option("atlas-leaf-density", Required = false, HelpText = "Linear texel density at leaf LOD in px/m. 512 = Naughty Dog tiling spec; 256 = Beyond Extent environment default. Coarser LODs halve per level (D_d = (LeafDensity / 2^(maxDepth-d))²).", Default = 512)]
    public int AtlasLeafDensityPxPerM { get; set; } = 512;

    [Option("atlas-source-detail-floor", Required = false, HelpText = "Enable area-weighted Q75 source-detail floor. Prevents under-sizing tiles whose photogrammetry GSD is finer than the LOD baseline. Default ON.", Default = true)]
    public bool AtlasUseSourceDetailFloor { get; set; } = true;

    [Option("atlas-min-size", Required = false, HelpText = "Hard floor on atlas side after the formula + Pow2 round. 256 is the smallest size where dilate-bleed gutters (16 px) leave a useful content region.", Default = 256)]
    public int AtlasMinSize { get; set; } = 256;

    [Option("atlas-source-detail-cap", Required = false, HelpText = "Ceiling on the source-detail-floor's px/m estimate. 1024 px/m is the upper bound where drone-camera GSD still resolves real detail; clamps anomalous fine-density faces.", Default = 1024)]
    public int AtlasSourceDetailCapPxPerM { get; set; } = 1024;

    [Option("texture-error-factor", Required = false, HelpText = "Scale factor on the texture-error contribution to non-leaf geometricError. Formula: effectiveGE = max(meshError, (worldExtent/atlasSide) × factor), then strict bottom-up monotonicity. Default 16. Set to 1 to disable.", Default = 16.0)]
    public double TextureErrorFactor { get; set; } = 16.0;
}
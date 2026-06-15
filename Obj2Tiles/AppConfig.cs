using System.Collections.Generic;

namespace Obj2Tiles;

public enum AtlasStrategy
{
    /// <summary>Natural packing-driven, clamped at MaxAtlasSize.</summary>
    Natural = 0,
    /// <summary>Atlas side sized from world-space surface area × density target.</summary>
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
    /// <summary>Run the hierarchical (HLOD) pipeline instead of flat-grid LOD.</summary>
    public bool HierarchicalLods { get; set; } = false;
    public bool ForceZSplit { get; set; } = false;
    public bool NoMeshoptCompression { get; set; } = false;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double Altitude { get; set; } = 0;
    public double Scale { get; set; } = 1;
    public bool YUpToZUp { get; set; } = false;

    /// <summary>Per-tile atlas size cap (pixels per edge) for the hierarchical pipeline.</summary>
    public int MaxAtlasSize { get; set; } = 4096;

    /// <summary>&gt;0 = decode each source texture once, downsample to this edge cap, hold resident for the whole bake. 0 = off.</summary>
    public int SourceCacheCap { get; set; }

    /// <summary>True when the source cache was auto-activated rather than via an explicit --source-cache-cap.</summary>
    public bool SourceCacheCapAutoEnabled { get; set; }

    /// <summary>Atlas cap for non-leaf HLOD nodes; 0 = use <see cref="MaxAtlasSize"/>. Fallback when no <see cref="AtlasMaxDepthSchedule"/> entry matches.</summary>
    public int MaxAtlasSizeInternal { get; set; } = 2048;

    /// <summary>Per-depth atlas cap (pixels per edge). Missing entries fall back to <see cref="MaxAtlasSizeInternal"/> (internal) or <see cref="MaxAtlasSize"/> (leaf).</summary>
    public Dictionary<int, int> AtlasMaxDepthSchedule { get; set; } = new Dictionary<int, int>
    {
        { 0, 512 },
        { 1, 1024 },
        { 2, 1536 },
        { 3, 2048 },
        { 4, 4096 },
    };

    /// <summary>True when the user explicitly passed <c>--lods</c>; otherwise the hierarchical pipeline uses its built-in schedule.</summary>
    public bool UserProvidedLods { get; set; }

    /// <summary>When true, the hierarchical pipeline selects maxDepth from the input mesh metrics.</summary>
    public bool AutoDepth { get; set; } = true;

    /// <summary>Per-leaf triangle target for depth selection. Consulted only when <see cref="AutoDepth"/> is true and <see cref="MaxDepthOverride"/> == 0.</summary>
    public int TLeafTri { get; set; } = 25_000;

    /// <summary>Per-leaf source-texture-bytes target for depth selection; 0 disables the texture axis. Consulted only when <see cref="AutoDepth"/> is true and <see cref="MaxDepthOverride"/> == 0.</summary>
    public long TLeafTextureBytes { get; set; } = 50_000_000L;

    /// <summary>Skip the ExtendAdaptive pass (which deepens leaves whose ideal atlas side exceeds <see cref="MaxAtlasSize"/>).</summary>
    public bool NoAdaptiveExtend { get; set; } = false;

    /// <summary>Hard ceiling for ExtendAdaptive recursion; 0 = autoDepth+3 default.</summary>
    public int AdaptiveExtendMaxDepth { get; set; } = 0;

    /// <summary>Auto-pick <see cref="MaxAtlasSize"/> from a per-tile decoded-RGBA budget (MB); 0 = use manual <see cref="MaxAtlasSize"/>.</summary>
    public int LeafVramBudgetMb { get; set; } = 0;

    /// <summary>Optional safety abort: 0 = unbounded; &gt; 0 aborts after ExtendAdaptive if the tree node count exceeds the value.</summary>
    public int MaxTileCount { get; set; } = 0;

    /// <summary>Unsharp-mask strength applied to atlas images before JPEG encode (0 = none).</summary>
    public double AtlasUnsharpAmount { get; set; } = 0.0;

    /// <summary>Write minFilter=LINEAR (no mips) on emitted samplers, maximizing sharpness at the cost of motion aliasing.</summary>
    public bool LeafNoMips { get; set; } = false;

    /// <summary>Explicit hierarchical maxDepth override; 0 = honor the <see cref="AutoDepth"/> selector.</summary>
    public int MaxDepthOverride { get; set; } = 0;

    /// <summary>Post-process every emitted GLB with gltfpack to apply KHR_mesh_quantization.</summary>
    public bool QuantizeGlbs { get; set; } = false;

    /// <summary>Path to the gltfpack binary; empty falls back to "gltfpack" on $PATH. Consulted only when <see cref="QuantizeGlbs"/> is true.</summary>
    public string GltfpackPath { get; set; } = "";

    /// <summary>Convert per-tile JPEG atlases to KTX2/Basis ETC1S.</summary>
    public bool Ktx2Hierarchical { get; set; } = true;

    /// <summary>KTX2/ETC1S quality (1-10, higher = larger but better).</summary>
    public int Ktx2Quality { get; set; } = 8;

    /// <summary>
    /// Encoder for per-tile KTX2 atlases. <c>"basisu"</c> encodes each atlas to <c>.ktx2</c>
    /// before the GLB is built (needs no gltfpack). <c>"gltfpack"</c> runs <c>gltfpack -tc</c>
    /// as a GLB post-process and also enables KHR_mesh_quantization + EXT_meshopt_compression.
    /// </summary>
    public string Ktx2Encoder { get; set; } = "basisu";

    /// <summary>Pass -c to gltfpack to also apply EXT_meshopt_compression. The renderer must load the meshopt decoder.</summary>
    public bool MeshoptCompress { get; set; } = false;

    /// <summary>Atlas-sizing strategy; see <see cref="AtlasStrategy"/>.</summary>
    public AtlasStrategy AtlasStrategy { get; set; } = AtlasStrategy.Natural;

    /// <summary>Linear texel density at the leaf LOD, in px/m. Coarser LODs halve linear density per level.</summary>
    public int AtlasLeafDensityPxPerM { get; set; } = 512;

    /// <summary>Raise a tile's atlas density up to its measured source detail (capped at <see cref="AtlasSourceDetailCapPxPerM"/>) so finer-than-LOD photogrammetry is not under-sized.</summary>
    public bool AtlasUseSourceDetailFloor { get; set; } = true;

    /// <summary>Hard floor on atlas side after sizing and pow2 round.</summary>
    public int AtlasMinSize { get; set; } = 256;

    /// <summary>Ceiling on the source-detail estimate when <see cref="AtlasUseSourceDetailFloor"/> is active, so a tile cannot blow through <see cref="MaxAtlasSize"/>.</summary>
    public int AtlasSourceDetailCapPxPerM { get; set; } = 1024;

    /// <summary>
    /// Scales the texture-error contribution to a non-leaf tile's geometricError:
    /// <c>effectiveGE = max(meshError, (worldExtent/atlasSide) × factor)</c>, followed by
    /// bottom-up monotonicity (parent ≥ max child × 1.01). Set to 1 to disable.
    /// </summary>
    public double TextureErrorFactor { get; set; } = 16.0;

    /// <summary>Parallelize Phase-1 atlas pack across cores. Set at runtime; safe only when all source textures fit in <c>TexturesCache</c> at once.</summary>
    public bool ParallelPhase1 { get; set; } = false;

    /// <summary>Max degree of parallelism for HLOD Phase-1; 0 = ProcessorCount / 2.</summary>
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

    /// <summary>Flat-grid per-LOD atlas cap (pixels per edge); 0 = <c>MeshT</c>'s built-in default.</summary>
    public int MaxAtlasSize { get; set; } = 0;
}

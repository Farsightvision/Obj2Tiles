using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Obj2Tiles.Stages;

/// <summary>
/// Per-depth diagnostics emitted alongside tileset.json. Decision-driving
/// metrics only: each field is consumed by either CI thresholds or manual
/// tuning. Intentionally flat (no nested classes) for easy JSON inspection.
/// </summary>
public sealed class BuildReport
{
    public int TotalNodes { get; set; }
    public int MaxDepth { get; set; }
    public Dictionary<int, int> NodesPerDepth { get; set; } = new();
    /// <summary>p50 vertex count per depth — drives MaxVerticesPerTile tuning.</summary>
    public Dictionary<int, int> VerticesP50 { get; set; } = new();
    /// <summary>Achieved triangle-reduction ratio per depth (vs target). >0.5 indicates locked-border pinning.</summary>
    public Dictionary<int, double> AchievedSimplifyRatio { get; set; } = new();
    /// <summary>p50 atlas edge length per depth.</summary>
    public Dictionary<int, int> AtlasSizeP50 { get; set; } = new();
    /// <summary>Average downsample factor per depth — >1 means MaxAtlasSize cap fired.</summary>
    public Dictionary<int, double> DownsampleFactorAvg { get; set; } = new();
    /// <summary>p50 GLB bytes per depth — drives client streaming budget.</summary>
    public Dictionary<int, long> GlbBytesP50 { get; set; } = new();
    /// <summary>Root tile's measured Hausdorff geometric error (meters).</summary>
    public double RootGeometricError { get; set; }
    /// <summary>Count of non-leaf nodes per depth whose geometricError is zero
    /// AFTER zero-error pruning. Should be 0 — any non-zero count means a
    /// refinement chain did not collapse and is wasting tile slots.</summary>
    public Dictionary<int, int> ZeroErrorInteriorPerDepth { get; set; } = new();

    /// <summary>Longest triangle edge in the sanitized input mesh (meters).
    /// Set once after <c>MeshSanitizer</c> runs; used as the reference upper
    /// bound for downstream tile geometry quality. Triangle clipping can
    /// only shorten edges, so leaf-tile edges must not exceed this value.</summary>
    public double SourceMaxEdgeLength { get; set; }

    /// <summary>Longest triangle edge across all leaf tiles (meters).
    /// Should be ≤ <see cref="SourceMaxEdgeLength"/> (modulo float drift):
    /// the splitter clips triangles at cell planes, which can only produce
    /// shorter edges. A leaf max-edge much larger than the source
    /// indicates the splitter is creating spurious triangles (welding
    /// across cells, missed clip plane, etc.).</summary>
    public double MaxLeafEdgeLength { get; set; }

    /// <summary>
    /// Boundary-edge count in the welded root tile (edges used by exactly 1
    /// triangle). Should equal the source mesh's natural boundary count
    /// (perimeter of the photogrammetry scan). A value substantially higher
    /// indicates extra cracks introduced by the splitter — the conformal
    /// hierarchy gate.
    /// </summary>
    public int BoundaryEdgeCountRoot { get; set; }

    public void WriteTo(string outputDir)
    {
        File.WriteAllText(Path.Combine(outputDir, "report.json"),
            JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}

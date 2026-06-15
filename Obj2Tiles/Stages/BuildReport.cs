using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Obj2Tiles.Stages;

/// <summary>Per-depth diagnostics emitted as report.json alongside tileset.json.</summary>
public sealed class BuildReport
{
    public int TotalNodes { get; set; }
    public int MaxDepth { get; set; }
    public Dictionary<int, int> NodesPerDepth { get; set; } = new();
    public Dictionary<int, int> VerticesP50 { get; set; } = new();
    public Dictionary<int, double> AchievedSimplifyRatio { get; set; } = new();
    public Dictionary<int, int> AtlasSizeP50 { get; set; } = new();
    /// <summary>Average downsample factor per depth; >1 means the MaxAtlasSize cap fired.</summary>
    public Dictionary<int, double> DownsampleFactorAvg { get; set; } = new();
    public Dictionary<int, long> GlbBytesP50 { get; set; } = new();
    /// <summary>Root tile's Hausdorff geometric error (meters).</summary>
    public double RootGeometricError { get; set; }
    /// <summary>Non-leaf nodes per depth with zero geometricError after pruning; should be 0.</summary>
    public Dictionary<int, int> ZeroErrorInteriorPerDepth { get; set; } = new();

    /// <summary>Longest triangle edge in the sanitized input mesh (meters).</summary>
    public double SourceMaxEdgeLength { get; set; }

    /// <summary>Longest triangle edge across all leaf tiles (meters); clipping only
    /// shortens edges, so this should be ≤ <see cref="SourceMaxEdgeLength"/> modulo float drift.</summary>
    public double MaxLeafEdgeLength { get; set; }

    /// <summary>Boundary edges (used by exactly 1 triangle) in the welded root tile;
    /// substantially above the source's natural boundary count means the splitter added cracks.</summary>
    public int BoundaryEdgeCountRoot { get; set; }

    public void WriteTo(string outputDir)
    {
        File.WriteAllText(Path.Combine(outputDir, "report.json"),
            JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}

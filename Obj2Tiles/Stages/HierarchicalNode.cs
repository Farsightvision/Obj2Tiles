using System.Collections.Generic;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

/// <summary>One node in the hierarchical tree; leaves have no children.</summary>
public sealed class HierarchicalNode
{
    public CellCoord Coord { get; init; }
    public Box3 Bounds { get; set; }
    public ClipResult? TileContent { get; set; }
    public ClipResultT? TileContentT { get; set; }
    public string? ContentUri { get; set; }
    public List<HierarchicalNode> Children { get; init; } = new();
    public double GeometricError { get; set; }
    public ClipResultT? BudgetSplitSourceContentT { get; set; }
    public bool IsBudgetSplitContent { get; set; }
    public double? BudgetSplitGeometricError { get; set; }
    public bool IsLeaf => Children.Count == 0;
    public int Depth => Coord.Level;
}

using System.Collections.Generic;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

/// <summary>
/// One node in the hierarchical tree. Leaves have no children; their TileContent
/// is the full-resolution clipped geometry. Interior nodes have children, and
/// their TileContent is the concatenation of simplified children. The same field
/// serves both roles — the lifecycle (full-res vs. simplified) is implicit in
/// <c>Children.Count == 0</c>.
/// </summary>
public sealed class HierarchicalNode
{
    public CellCoord Coord { get; init; }
    /// <summary>The exact-bounds AABB of this node's geometry. Computed once when
    /// TileContent is set; serves as the tile's bounding volume in tileset.json.</summary>
    public Box3 Bounds { get; set; }
    /// <summary>The mesh (positions only) for this tile. Legacy/test path.</summary>
    public ClipResult? TileContent { get; set; }
    /// <summary>The textured (UV+material-aware) mesh for this tile. Hierarchical
    /// pipeline uses this; per-tile atlas pack and GLB write consume it.
    /// <see cref="TileContent"/> remains for tests that don't thread textures through.</summary>
    public ClipResultT? TileContentT { get; set; }
    /// <summary>Children (empty list for leaves).</summary>
    public List<HierarchicalNode> Children { get; init; } = new();
    /// <summary>Geometric error (Hausdorff, monotonic-corrected).</summary>
    public double GeometricError { get; set; }
    public bool IsLeaf => Children.Count == 0;
    public int Depth => Coord.Level;
}

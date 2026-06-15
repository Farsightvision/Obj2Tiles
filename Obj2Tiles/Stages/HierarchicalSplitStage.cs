using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public static class HierarchicalSplitStage
{
    public static HierarchicalNode BuildTree(
        IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, Box3 bbox,
        SubdivisionShape shape, int maxVertsPerTile, int? maxDepth,
        LodConfig[] lods)
    {
        var leafTiles = OctreeSplitter.RecursiveSplit(verts, faces, bbox, shape, maxVertsPerTile, maxDepth);

        int actualMaxDepth = leafTiles.Max(l => l.Coord.Level);

        var byCoord = new Dictionary<CellCoord, HierarchicalNode>();
        foreach (var leaf in leafTiles)
        {
            var node = new HierarchicalNode
            {
                Coord = leaf.Coord,
                Bounds = ComputeBounds(leaf.Mesh.Vertices),
                TileContent = leaf.Mesh,
            };
            byCoord[leaf.Coord] = node;
        }

        for (int depth = actualMaxDepth - 1; depth >= 0; depth--)
        {
            var atThisDepth = byCoord.Values.Where(n => n.Coord.Level == depth + 1).ToList();
            var byParent = atThisDepth.GroupBy(n => ParentCoord(n.Coord));
            foreach (var group in byParent)
            {
                var children = group
                    .OrderBy(c => c.Coord.X)
                    .ThenBy(c => c.Coord.Y)
                    .ThenBy(c => c.Coord.Z)
                    .ToList();
                int distFromLeaves = actualMaxDepth - depth;
                int lodIdx = System.Math.Min(distFromLeaves - 1, lods.Length - 1);
                if (lodIdx < 0) lodIdx = 0;
                float ratio = lods[lodIdx].Quality;
                var parentMesh = ConcatenateSimplifiedChildren(children, ratio);
                var parent = new HierarchicalNode
                {
                    Coord = group.Key,
                    Bounds = UnionBounds(children),
                    TileContent = parentMesh,
                    Children = children,
                };
                byCoord[parent.Coord] = parent;
            }
        }

        return byCoord[new CellCoord(0, 0, 0, 0)];
    }

    /// <summary>Textured BuildTree: threads UVs and material indices through every clip and simplify.</summary>
    public static HierarchicalNode BuildTreeT(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        Box3 bbox,
        SubdivisionShape shape,
        int maxVertsPerTile,
        int? maxDepth,
        LodConfig[] lods,
        Dictionary<CellCoord, SimplifyMetrics>? simplifyMetricsOut = null)
    {
        var leafTiles = OctreeSplitter.RecursiveSplitT(verts, tex, faces, bbox, shape, maxVertsPerTile, maxDepth);

        int actualMaxDepth = leafTiles.Max(l => l.Coord.Level);

        var byCoord = new Dictionary<CellCoord, HierarchicalNode>();
        foreach (var leaf in leafTiles)
        {
            var node = new HierarchicalNode
            {
                Coord = leaf.Coord,
                Bounds = ComputeBounds(leaf.Mesh.Vertices),
                TileContentT = leaf.Mesh,
            };
            byCoord[leaf.Coord] = node;
        }

        for (int depth = actualMaxDepth - 1; depth >= 0; depth--)
        {
            var atThisDepth = byCoord.Values.Where(n => n.Coord.Level == depth + 1).ToList();
            var byParent = atThisDepth.GroupBy(n => ParentCoord(n.Coord));
            foreach (var group in byParent)
            {
                var children = group
                    .OrderBy(c => c.Coord.X)
                    .ThenBy(c => c.Coord.Y)
                    .ThenBy(c => c.Coord.Z)
                    .ToList();
                int distFromLeaves = actualMaxDepth - depth;
                int lodIdx = System.Math.Min(distFromLeaves - 1, lods.Length - 1);
                if (lodIdx < 0) lodIdx = 0;
                float ratio = lods[lodIdx].Quality;
                var parentCoord = group.Key;
                // Lock the simplifier on children's UnionBounds, not theoretical cell bounds:
                // the data edge may fall short of the cell face, so theoretical bounds miss
                // the boundary verts and adjacent parents drop different subsets → T-junction
                // cracks. UnionBounds is symmetric across neighbors, so both lock the same verts.
                // Root (depth 0) has no neighbors; inverted bounds disable the lock.
                Box3 parentCellBounds = depth == 0
                    ? new Box3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                               double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity)
                    : UnionBounds(children);

                var parentMesh = ConcatenateAndSimplifyParentT(
                    children, parentCellBounds, ratio, out var metrics);
                if (simplifyMetricsOut != null)
                    simplifyMetricsOut[parentCoord] = metrics;

                // Parent bounds = union of children, not the simplified mesh's bounds:
                // simplification can pull the mesh inside a child's extent, and 3D Tiles
                // frustum culling requires the parent AABB to contain every child.
                var parent = new HierarchicalNode
                {
                    Coord = parentCoord,
                    Bounds = UnionBounds(children),
                    TileContentT = parentMesh,
                    Children = children,
                };
                byCoord[parent.Coord] = parent;
            }
        }

        return byCoord[new CellCoord(0, 0, 0, 0)];
    }

    /// <summary>Outer AABB of a cell; quadtree leaves Z at full scene extent, octree subdivides all axes.</summary>
    public static Box3 ComputeCellBounds(CellCoord coord, Box3 sceneBounds, SubdivisionShape shape)
    {
        int level = coord.Level;
        long divisions = 1L << level;
        double sw = sceneBounds.Max.X - sceneBounds.Min.X;
        double sh = sceneBounds.Max.Y - sceneBounds.Min.Y;
        double sd = sceneBounds.Max.Z - sceneBounds.Min.Z;
        double cellW = sw / divisions;
        double cellH = sh / divisions;
        double cellD = sd / divisions;

        double minX = sceneBounds.Min.X + cellW * coord.X;
        double maxX = minX + cellW;
        double minY = sceneBounds.Min.Y + cellH * coord.Y;
        double maxY = minY + cellH;
        double minZ, maxZ;
        if (shape == SubdivisionShape.Quadtree)
        {
            minZ = sceneBounds.Min.Z;
            maxZ = sceneBounds.Max.Z;
        }
        else
        {
            minZ = sceneBounds.Min.Z + cellD * coord.Z;
            maxZ = minZ + cellD;
        }
        return new Box3(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static CellCoord ParentCoord(CellCoord c)
        => new CellCoord(c.Level - 1, c.X / 2, c.Y / 2, c.Z / 2);

    private static Box3 UnionBounds(List<HierarchicalNode> children)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
        double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        foreach (var c in children)
        {
            var b = c.Bounds;
            if (b.Min.X < mnx) mnx = b.Min.X; if (b.Max.X > mxx) mxx = b.Max.X;
            if (b.Min.Y < mny) mny = b.Min.Y; if (b.Max.Y > mxy) mxy = b.Max.Y;
            if (b.Min.Z < mnz) mnz = b.Min.Z; if (b.Max.Z > mxz) mxz = b.Max.Z;
        }
        return new Box3(mnx, mny, mnz, mxx, mxy, mxz);
    }

    private static Box3 ComputeBounds(IReadOnlyList<Vertex3> verts)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
        double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        foreach (var v in verts)
        {
            if (v.X < mnx) mnx = v.X; if (v.X > mxx) mxx = v.X;
            if (v.Y < mny) mny = v.Y; if (v.Y > mxy) mxy = v.Y;
            if (v.Z < mnz) mnz = v.Z; if (v.Z > mxz) mxz = v.Z;
        }
        return new Box3(mnx, mny, mnz, mxx, mxy, mxz);
    }

    private static ClipResult ConcatenateSimplifiedChildren(List<HierarchicalNode> children, float targetRatio)
    {
        var simplified = new ClipResult[children.Count];
        Parallel.For(0, children.Count, i =>
        {
            simplified[i] = HierarchicalDecimationStage.SimplifyChild(children[i].TileContent!, targetRatio);
        });
        var allVerts = new List<Vertex3>();
        var allFaces = new List<Face>();
        var positionToIndex = new Dictionary<(double X, double Y, double Z), int>();
        foreach (var simp in simplified)
        {
            var localRemap = new int[simp.Vertices.Length];
            for (int vi = 0; vi < simp.Vertices.Length; vi++)
            {
                var v = simp.Vertices[vi];
                var key = (v.X, v.Y, v.Z);
                if (!positionToIndex.TryGetValue(key, out int existing))
                {
                    existing = allVerts.Count;
                    allVerts.Add(v);
                    positionToIndex[key] = existing;
                }
                localRemap[vi] = existing;
            }
            foreach (var f in simp.Faces)
                allFaces.Add(new Face(localRemap[f.IndexA], localRemap[f.IndexB], localRemap[f.IndexC]));
        }
        return new ClipResult { Vertices = allVerts.ToArray(), Faces = allFaces.ToArray() };
    }

    /// <summary>
    /// Concatenate children with position-only welding, then simplify once, locking
    /// only the parent's outer-AABB perimeter. UV seams survive because UV references
    /// stay per-face while the simplifier only touches position indices.
    /// </summary>
    private static ClipResultT ConcatenateAndSimplifyParentT(
        List<HierarchicalNode> children,
        Box3 parentCellBounds,
        float targetRatio,
        out SimplifyMetrics metrics)
    {
        var allVerts = new List<Vertex3>();
        var allTex = new List<Vertex2>();
        var allFaces = new List<MeshFace>();
        var positionToIndex = new Dictionary<(double X, double Y, double Z), int>();
        foreach (var child in children)
        {
            var c = child.TileContentT!;
            var posRemap = new int[c.Vertices.Length];
            for (int vi = 0; vi < c.Vertices.Length; vi++)
            {
                var v = c.Vertices[vi];
                var key = (v.X, v.Y, v.Z);
                if (!positionToIndex.TryGetValue(key, out int existing))
                {
                    existing = allVerts.Count;
                    allVerts.Add(v);
                    positionToIndex[key] = existing;
                }
                posRemap[vi] = existing;
            }
            int uvOffset = allTex.Count;
            allTex.AddRange(c.TexVertices);
            foreach (var f in c.Faces)
                allFaces.Add(new MeshFace(
                    posRemap[f.IndexA], posRemap[f.IndexB], posRemap[f.IndexC],
                    f.TexA + uvOffset, f.TexB + uvOffset, f.TexC + uvOffset,
                    f.MaterialIndex));
        }

        var welded = new ClipResultT
        {
            Vertices = allVerts.ToArray(),
            TexVertices = allTex.ToArray(),
            Faces = allFaces.ToArray(),
        };

        return HierarchicalDecimationStage.SimplifyParent(
            welded, parentCellBounds, targetRatio, out metrics);
    }
}

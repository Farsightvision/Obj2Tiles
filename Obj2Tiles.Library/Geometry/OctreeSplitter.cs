using System;
using System.Collections.Generic;

namespace Obj2Tiles.Library.Geometry;

public enum SubdivisionShape { Quadtree, Octree }

/// <summary>
/// Result of clipping a mesh at an axis-aligned plane. Boundary vertices are
/// guaranteed to have identical positions across the two outputs (epsilon = 0).
/// </summary>
public sealed class ClipResult
{
    public Vertex3[] Vertices { get; init; } = Array.Empty<Vertex3>();
    public Face[]    Faces    { get; init; } = Array.Empty<Face>();
}

/// <summary>
/// Recursively splits a mesh into octree/quadtree cells, clipping triangles at
/// cell-boundary planes so adjacent leaves share *exactly equal* boundary
/// vertex positions (the prerequisite for the locked-border crack invariant
/// in spec §3.1.4).
/// </summary>
public static partial class OctreeSplitter
{
    /// <summary>py3dtiles heuristic: Z / min(X, Y) &lt; 0.5 → quadtree.</summary>
    public static SubdivisionShape ChooseShape(Box3 bbox, bool forceOctree)
    {
        if (forceOctree) return SubdivisionShape.Octree;
        double sx = bbox.Width, sy = bbox.Height, sz = bbox.Depth;
        double minXY = Math.Min(sx, sy);
        if (minXY <= 0) return SubdivisionShape.Octree;
        return (sz / minXY) < 0.5 ? SubdivisionShape.Quadtree : SubdivisionShape.Octree;
    }

    /// <summary>
    /// Clip at x = xSplit. Returns left (x &lt; xSplit) and right (x &gt; xSplit)
    /// halves with shared boundary vertex positions. Triangles fully on one side
    /// pass through unchanged. Crossing triangles are split into 1 piece on the
    /// minority side and 2 pieces on the majority side.
    /// </summary>
    public static (ClipResult left, ClipResult right) ClipAtX(
        IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, double xSplit)
        => ClipAtAxis(verts, faces, axis: 0, split: xSplit);

    public static (ClipResult left, ClipResult right) ClipAtY(
        IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, double ySplit)
        => ClipAtAxis(verts, faces, axis: 1, split: ySplit);

    public static (ClipResult left, ClipResult right) ClipAtZ(
        IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, double zSplit)
        => ClipAtAxis(verts, faces, axis: 2, split: zSplit);

    private static (ClipResult, ClipResult) ClipAtAxis(
        IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, int axis, double split)
    {
        // Per-vertex side: -1 = left, +1 = right, 0 = on-plane
        int[] side = new int[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            double c = AxisOf(verts[i], axis);
            side[i] = c < split ? -1 : c > split ? +1 : 0;
        }

        var leftV  = new List<Vertex3>(verts);
        var rightV = new List<Vertex3>(verts);
        var leftF  = new List<Face>();
        var rightF = new List<Face>();

        // Cache: edge (a,b) -> (leftIndex, rightIndex) of new boundary vertex.
        // Both sides get the SAME position appended; cache by sorted (a,b).
        var cache = new Dictionary<(int a, int b), (int li, int ri)>();
        (int li, int ri) GetBoundary(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (cache.TryGetValue(key, out var hit)) return hit;
            // Linear interp along the axis
            var va = verts[a]; var vb = verts[b];
            double aA = AxisOf(va, axis), aB = AxisOf(vb, axis);
            double t = (split - aA) / (aB - aA);
            var newV = new Vertex3(
                va.X + t * (vb.X - va.X),
                va.Y + t * (vb.Y - va.Y),
                va.Z + t * (vb.Z - va.Z));
            // Snap exact axis coordinate to prevent FP drift
            newV = SetAxis(newV, axis, split);
            int li = leftV.Count;
            int ri = rightV.Count;
            leftV.Add(newV); rightV.Add(newV);
            cache[key] = (li, ri);
            return (li, ri);
        }

        foreach (var f in faces)
        {
            int a = f.IndexA, b = f.IndexB, c = f.IndexC;
            int sa = side[a], sb = side[b], sc = side[c];

            if (sa <= 0 && sb <= 0 && sc <= 0) { leftF.Add(f); continue; }
            if (sa >= 0 && sb >= 0 && sc >= 0) { rightF.Add(f); continue; }

            // Crossing — partition vertex indices by sign
            int[] vs = { a, b, c }; int[] ss = { sa, sb, sc };
            var L = new List<int>(); var R = new List<int>();
            for (int i = 0; i < 3; i++) { if (ss[i] < 0) L.Add(vs[i]); else if (ss[i] > 0) R.Add(vs[i]); }
            if (L.Count == 1 && R.Count == 2)
            {
                int l = L[0]; int r1 = R[0], r2 = R[1];
                var (P1l, P1r) = GetBoundary(l, r1);
                var (P2l, P2r) = GetBoundary(l, r2);
                leftF.Add(new Face(l, P1l, P2l));
                rightF.Add(new Face(r1, r2, P2r));
                rightF.Add(new Face(r1, P2r, P1r));
            }
            else if (L.Count == 2 && R.Count == 1)
            {
                int r = R[0]; int l1 = L[0], l2 = L[1];
                var (P1l, P1r) = GetBoundary(r, l1);
                var (P2l, P2r) = GetBoundary(r, l2);
                rightF.Add(new Face(r, P1r, P2r));
                leftF.Add(new Face(l1, l2, P2l));
                leftF.Add(new Face(l1, P2l, P1l));
            }
            else
            {
                // One or more vertices on the plane.
                // Case A: exactly one on plane, others on opposite sides — the triangle
                //         straddles the plane. Split into 2: each side gets a tri formed
                //         by the on-plane vertex, the off-plane vertex on that side, and
                //         a new boundary vertex at the crossing edge.
                // Case B: two on plane (the third either side) — assign whole triangle
                //         to the third's side; on-plane edge stays shared.
                // Case C: all three on plane — degenerate, skip.
                int onCount = (sa == 0 ? 1 : 0) + (sb == 0 ? 1 : 0) + (sc == 0 ? 1 : 0);
                if (onCount == 1 && L.Count == 1 && R.Count == 1)
                {
                    int onVert = vs[ss[0] == 0 ? 0 : ss[1] == 0 ? 1 : 2];
                    int leftVert = L[0]; int rightVert = R[0];
                    var (PleftL, PleftR) = GetBoundary(leftVert, rightVert);
                    leftF.Add(new Face(onVert, leftVert, PleftL));
                    rightF.Add(new Face(onVert, PleftR, rightVert));
                }
                else if (onCount == 2)
                {
                    // The single off-plane vertex's side gets the whole triangle.
                    int sgn = sa + sb + sc;  // = side of the lone off-plane vertex
                    if (sgn < 0) leftF.Add(f);
                    else if (sgn > 0) rightF.Add(f);
                }
                // else (onCount == 3, all on plane) — skip
            }
        }

        return (Prune(leftV, leftF), Prune(rightV, rightF));
    }

    private static double AxisOf(Vertex3 v, int axis) => axis switch
    {
        0 => v.X, 1 => v.Y, 2 => v.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    private static Vertex3 SetAxis(Vertex3 v, int axis, double value) => axis switch
    {
        0 => new Vertex3(value, v.Y, v.Z),
        1 => new Vertex3(v.X, value, v.Z),
        2 => new Vertex3(v.X, v.Y, value),
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    private static ClipResult Prune(List<Vertex3> verts, List<Face> faces)
    {
        if (faces.Count == 0)
            return new ClipResult { Vertices = Array.Empty<Vertex3>(), Faces = Array.Empty<Face>() };
        var used = new HashSet<int>();
        foreach (var f in faces) { used.Add(f.IndexA); used.Add(f.IndexB); used.Add(f.IndexC); }
        var sorted = new int[used.Count];
        used.CopyTo(sorted); Array.Sort(sorted);
        var remap = new Dictionary<int, int>(sorted.Length);
        var newVerts = new Vertex3[sorted.Length];
        for (int i = 0; i < sorted.Length; i++) { remap[sorted[i]] = i; newVerts[i] = verts[sorted[i]]; }
        var newFaces = new Face[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            newFaces[i] = new Face(remap[f.IndexA], remap[f.IndexB], remap[f.IndexC]);
        }
        return new ClipResult { Vertices = newVerts, Faces = newFaces };
    }
}

/// <summary>
/// A leaf cell produced by <see cref="OctreeSplitter.RecursiveSplit"/>.
/// <see cref="CellBounds"/> is the *tight* triangle bounds of the leaf's mesh
/// (tighter than the cell AABB, useful for culling and OBB construction).
/// </summary>
public sealed record LeafTile(CellCoord Coord, ClipResult Mesh, Box3 CellBounds);

public static class OctreeSplitterRecursive
{
    /// <summary>
    /// Slack factor over <c>maxVertsPerTile</c>: a leaf is accepted as long as
    /// its vertex count is &lt;= maxVertsPerTile × HeadroomFactor. Prevents
    /// runaway recursion when triangles densely cluster on a split plane.
    /// </summary>
    public const double HeadroomFactor = 1.5;
}

public static partial class OctreeSplitter
{
    /// <summary>
    /// Recursively split until each leaf has &lt;= maxVertsPerTile × HeadroomFactor
    /// vertices OR depth reaches maxDepth (if specified). Empty cells are pruned
    /// (no LeafTile is emitted for cells with zero faces).
    /// </summary>
    public static List<LeafTile> RecursiveSplit(
        IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, Box3 bbox,
        SubdivisionShape shape, int maxVertsPerTile, int? maxDepth)
    {
        var leaves = new List<LeafTile>();
        SplitInto(verts, faces, bbox, new CellCoord(0, 0, 0, 0), shape, maxVertsPerTile, maxDepth, leaves);
        return leaves;
    }

    private static void SplitInto(
        IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, Box3 bbox,
        CellCoord coord, SubdivisionShape shape, int maxVertsPerTile, int? maxDepth,
        List<LeafTile> leaves)
    {
        if (faces.Count == 0) return;

        bool tooBig = verts.Count > (int)(maxVertsPerTile * OctreeSplitterRecursive.HeadroomFactor);
        bool depthCapHit = maxDepth.HasValue && coord.Level >= maxDepth.Value;
        if (!tooBig || depthCapHit)
        {
            // Compute tight triangle bounds (tighter than the cell AABB)
            var tightBounds = ComputeTriangleBounds(verts, faces);
            leaves.Add(new LeafTile(coord, new ClipResult { Vertices = ToArray(verts), Faces = ToArray(faces) }, tightBounds));
            return;
        }

        double cx = (bbox.Min.X + bbox.Max.X) * 0.5;
        double cy = (bbox.Min.Y + bbox.Max.Y) * 0.5;
        double cz = (bbox.Min.Z + bbox.Max.Z) * 0.5;

        // Split X
        var (xLow, xHigh) = ClipAtX(verts, faces, cx);

        if (shape == SubdivisionShape.Quadtree)
        {
            // Then Y; ignore Z splits (full-Z column kept)
            var (xLowYLow, xLowYHigh)   = ClipAtY(xLow.Vertices,  xLow.Faces,  cy);
            var (xHighYLow, xHighYHigh) = ClipAtY(xHigh.Vertices, xHigh.Faces, cy);
            Recurse(xLowYLow,   new Box3(bbox.Min.X, bbox.Min.Y, bbox.Min.Z, cx,         cy,         bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 0, Y = coord.Y * 2 + 0 });
            Recurse(xHighYLow,  new Box3(cx,         bbox.Min.Y, bbox.Min.Z, bbox.Max.X, cy,         bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 1, Y = coord.Y * 2 + 0 });
            Recurse(xLowYHigh,  new Box3(bbox.Min.X, cy,         bbox.Min.Z, cx,         bbox.Max.Y, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 0, Y = coord.Y * 2 + 1 });
            Recurse(xHighYHigh, new Box3(cx,         cy,         bbox.Min.Z, bbox.Max.X, bbox.Max.Y, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 1, Y = coord.Y * 2 + 1 });
        }
        else
        {
            // Octree — split in all three axes (8 children)
            var halves = new (ClipResult half, double xMin, double xMax, int xi)[]
            {
                (xLow,  bbox.Min.X, cx,         0),
                (xHigh, cx,         bbox.Max.X, 1),
            };
            foreach (var (half, xMin, xMax, xi) in halves)
            {
                var (yL, yH)   = ClipAtY(half.Vertices, half.Faces, cy);
                var (yLzL, yLzH) = ClipAtZ(yL.Vertices, yL.Faces, cz);
                var (yHzL, yHzH) = ClipAtZ(yH.Vertices, yH.Faces, cz);
                Recurse(yLzL, new Box3(xMin, bbox.Min.Y, bbox.Min.Z, xMax, cy,         cz),         coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 0, Z = coord.Z * 2 + 0 });
                Recurse(yLzH, new Box3(xMin, bbox.Min.Y, cz,         xMax, cy,         bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 0, Z = coord.Z * 2 + 1 });
                Recurse(yHzL, new Box3(xMin, cy,         bbox.Min.Z, xMax, bbox.Max.Y, cz),         coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 1, Z = coord.Z * 2 + 0 });
                Recurse(yHzH, new Box3(xMin, cy,         cz,         xMax, bbox.Max.Y, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 1, Z = coord.Z * 2 + 1 });
            }
        }

        void Recurse(ClipResult sub, Box3 subBbox, CellCoord subCoord)
            => SplitInto(sub.Vertices, sub.Faces, subBbox, subCoord, shape, maxVertsPerTile, maxDepth, leaves);
    }

    private static T[] ToArray<T>(IReadOnlyList<T> list)
    {
        if (list is T[] arr) return arr;
        var copy = new T[list.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
        return copy;
    }

    private static Box3 ComputeTriangleBounds(IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
        double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        foreach (var f in faces)
        {
            for (int k = 0; k < 3; k++)
            {
                int i = k == 0 ? f.IndexA : k == 1 ? f.IndexB : f.IndexC;
                var v = verts[i];
                if (v.X < mnx) mnx = v.X; if (v.X > mxx) mxx = v.X;
                if (v.Y < mny) mny = v.Y; if (v.Y > mxy) mxy = v.Y;
                if (v.Z < mnz) mnz = v.Z; if (v.Z > mxz) mxz = v.Z;
            }
        }
        return new Box3(mnx, mny, mnz, mxx, mxy, mxz);
    }
}

public static partial class OctreeSplitter
{
    /// <summary>
    /// 12-element 3D Tiles axis-aligned bounding box from a vertex set:
    /// [cx, cy, cz, hx, 0, 0, 0, hy, 0, 0, 0, hz]
    /// </summary>
    public static double[] AabbBox(IReadOnlyList<Vertex3> verts)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
        double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        foreach (var v in verts)
        {
            if (v.X < mnx) mnx = v.X; if (v.X > mxx) mxx = v.X;
            if (v.Y < mny) mny = v.Y; if (v.Y > mxy) mxy = v.Y;
            if (v.Z < mnz) mnz = v.Z; if (v.Z > mxz) mxz = v.Z;
        }
        double cx = (mnx + mxx) * 0.5, cy = (mny + mxy) * 0.5, cz = (mnz + mxz) * 0.5;
        double hx = (mxx - mnx) * 0.5, hy = (mxy - mny) * 0.5, hz = (mxz - mnz) * 0.5;
        return new[] { cx, cy, cz, hx, 0, 0, 0, hy, 0, 0, 0, hz };
    }
}

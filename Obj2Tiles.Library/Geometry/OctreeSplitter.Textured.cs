using System;
using System.Collections.Generic;

namespace Obj2Tiles.Library.Geometry;

/// <summary>
/// UV+material-aware face. Distinct from <see cref="FaceT"/> (which has
/// MeshT-specific lifecycle assumptions) and from <see cref="Face"/>
/// (which has no UVs/materials). Used by <see cref="OctreeSplitter"/>'s
/// textured clipping path so per-tile atlas packing can run against the
/// hierarchical pipeline's output.
/// </summary>
public sealed class MeshFace
{
    public int IndexA;
    public int IndexB;
    public int IndexC;
    public int TexA;
    public int TexB;
    public int TexC;
    public int MaterialIndex;

    public MeshFace(int a, int b, int c, int ta, int tb, int tc, int materialIndex)
    {
        IndexA = a; IndexB = b; IndexC = c;
        TexA = ta; TexB = tb; TexC = tc;
        MaterialIndex = materialIndex;
    }
}

/// <summary>
/// Textured analogue of <see cref="ClipResult"/>: positions, UVs, and
/// UV+material-aware faces. Boundary positions remain bit-identical
/// between sibling outputs (axis snap on the clip plane); boundary UVs
/// are interpolated at the same parameter <c>t</c> as the position, so
/// per-tile atlases can keep UV seams without breaking the locked-border
/// crack invariant.
/// </summary>
public sealed class ClipResultT
{
    public Vertex3[] Vertices { get; init; } = Array.Empty<Vertex3>();
    public Vertex2[] TexVertices { get; init; } = Array.Empty<Vertex2>();
    public MeshFace[] Faces { get; init; } = Array.Empty<MeshFace>();
}

/// <summary>Leaf cell from <see cref="OctreeSplitter.RecursiveSplit"/>'s textured overload.</summary>
public sealed record LeafTileT(CellCoord Coord, ClipResultT Mesh, Box3 CellBounds);

public static partial class OctreeSplitter
{
    /// <summary>Textured X clip. UVs interpolate linearly at the same <c>t</c> as positions.</summary>
    public static (ClipResultT left, ClipResultT right) ClipAtXT(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        double xSplit)
        => ClipAtAxisT(verts, tex, faces, axis: 0, split: xSplit);

    public static (ClipResultT left, ClipResultT right) ClipAtYT(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        double ySplit)
        => ClipAtAxisT(verts, tex, faces, axis: 1, split: ySplit);

    public static (ClipResultT left, ClipResultT right) ClipAtZT(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        double zSplit)
        => ClipAtAxisT(verts, tex, faces, axis: 2, split: zSplit);

    private static (ClipResultT, ClipResultT) ClipAtAxisT(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        int axis,
        double split)
    {
        // fp-safety snap. Tolerance is 1e-9 RELATIVE to scene scale — captures
        // fp drift only (typically 1-2 ULPs at meter scale, ~1e-15 m absolute).
        // Real source verts are mm-scale at best; this snap never moves them.
        // Required because BoundarySkeleton.BuildAndEnrich computes plane
        // positions as `sceneBounds.Min + extent * i / divisions` while
        // PartitionAtDepth (called downstream) uses recursive midpoints
        // `(bbox.Min + bbox.Max) * 0.5` — the two formulas differ by ULPs on
        // non-integer bounds. Without the snap, skeleton verts end up on the
        // wrong side of the partition plane and adjacent cells disagree on
        // boundary verts.
        double fpSafetyTol = 1e-9 * Math.Max(1.0, Math.Abs(split));

        var snappedVerts = new Vertex3[verts.Count];
        int[] side = new int[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            double c = AxisOf(verts[i], axis);
            if (Math.Abs(c - split) <= fpSafetyTol)
            {
                snappedVerts[i] = SetAxis(verts[i], axis, split);
                side[i] = 0;
            }
            else
            {
                snappedVerts[i] = verts[i];
                side[i] = c < split ? -1 : +1;
            }
        }

        var leftV = new List<Vertex3>(snappedVerts);
        var rightV = new List<Vertex3>(snappedVerts);
        var leftT = new List<Vertex2>(tex);
        var rightT = new List<Vertex2>(tex);
        var leftF = new List<MeshFace>();
        var rightF = new List<MeshFace>();

        // Cache by the (sorted-edge, ta, tb) triple. Position is a function of
        // edge alone, but UVs are per-corner-of-this-triangle: two triangles
        // sharing an edge in 3D may use different UVs at the same corners
        // (UV seams are a real case in photogrammetry meshes), so we cannot
        // dedupe by position-edge alone or we'd collapse the seam.
        var posCache = new Dictionary<(int a, int b), (int li, int ri)>();
        var uvCache  = new Dictionary<(int ea, int eb, int ta, int tb), (int li, int ri)>();

        (int li, int ri) GetBoundaryPos(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (posCache.TryGetValue(key, out var hit)) return hit;
            var va = snappedVerts[a]; var vb = snappedVerts[b];
            double aA = AxisOf(va, axis), aB = AxisOf(vb, axis);
            double t = (split - aA) / (aB - aA);
            var newV = new Vertex3(
                va.X + t * (vb.X - va.X),
                va.Y + t * (vb.Y - va.Y),
                va.Z + t * (vb.Z - va.Z));
            newV = SetAxis(newV, axis, split);
            int li = leftV.Count, ri = rightV.Count;
            leftV.Add(newV); rightV.Add(newV);
            posCache[key] = (li, ri);
            return (li, ri);
        }

        (int li, int ri) GetBoundaryUv(int a, int b, int ta, int tb)
        {
            // Edge key sorted, but UV pair pinned to the (ta-on-a-side, tb-on-b-side)
            // ordering — flipping it would invert the interpolation.
            var keyEdge = a < b ? (a, b, ta, tb) : (b, a, tb, ta);
            if (uvCache.TryGetValue(keyEdge, out var hit)) return hit;
            var va = snappedVerts[a]; var vb = snappedVerts[b];
            double aA = AxisOf(va, axis), aB = AxisOf(vb, axis);
            double t = (split - aA) / (aB - aA);
            var ua = tex[ta]; var ub = tex[tb];
            var newUv = new Vertex2(
                ua.X + t * (ub.X - ua.X),
                ua.Y + t * (ub.Y - ua.Y));
            int li = leftT.Count, ri = rightT.Count;
            leftT.Add(newUv); rightT.Add(newUv);
            uvCache[keyEdge] = (li, ri);
            return (li, ri);
        }

        // Winding-preservation bookkeeping mirrors BoundarySkeleton.SplitAtPlane.
        // The L1R2/L2R1/L1R1 vertex orderings below preserve CCW for CCW source;
        // CW sources get flipped to back-facing without a corrective post-pass.
        // With multi-LOD's aggressive simplification, those flipped sub-triangles
        // become large enough to be visibly meter-scale "cracks" in coarse tiles.
        var srcNormals = new List<(double, double, double)>(faces.Count);
        var leftFaceSrc = new List<int>(faces.Count);
        var rightFaceSrc = new List<int>(faces.Count * 2);

        foreach (var f in faces)
        {
            int a = f.IndexA, b = f.IndexB, c = f.IndexC;
            int ta = f.TexA, tb = f.TexB, tc = f.TexC;
            int sa = side[a], sb = side[b], sc = side[c];
            int mat = f.MaterialIndex;
            int srcIdx = srcNormals.Count;
            {
                var va = snappedVerts[a]; var vb = snappedVerts[b]; var vc = snappedVerts[c];
                double ex = vb.X - va.X, ey = vb.Y - va.Y, ez = vb.Z - va.Z;
                double fx = vc.X - va.X, fy = vc.Y - va.Y, fz = vc.Z - va.Z;
                srcNormals.Add((ey*fz - ez*fy, ez*fx - ex*fz, ex*fy - ey*fx));
            }
            int lStart = leftF.Count, rStart = rightF.Count;

            if (sa <= 0 && sb <= 0 && sc <= 0) { leftF.Add(new MeshFace(a, b, c, ta, tb, tc, mat)); goto record; }
            if (sa >= 0 && sb >= 0 && sc >= 0) { rightF.Add(new MeshFace(a, b, c, ta, tb, tc, mat)); goto record; }

            // Crossing — collect indices+UVs by sign
            var V = new[] { a, b, c };
            var T = new[] { ta, tb, tc };
            var S = new[] { sa, sb, sc };
            var Lpos = new List<int>(); var Ltex = new List<int>();
            var Rpos = new List<int>(); var Rtex = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                if (S[i] < 0) { Lpos.Add(V[i]); Ltex.Add(T[i]); }
                else if (S[i] > 0) { Rpos.Add(V[i]); Rtex.Add(T[i]); }
            }

            if (Lpos.Count == 1 && Rpos.Count == 2)
            {
                int l = Lpos[0]; int r1 = Rpos[0], r2 = Rpos[1];
                int lT = Ltex[0]; int r1T = Rtex[0], r2T = Rtex[1];
                var (P1l, P1r) = GetBoundaryPos(l, r1);
                var (P2l, P2r) = GetBoundaryPos(l, r2);
                var (U1l, U1r) = GetBoundaryUv(l, r1, lT, r1T);
                var (U2l, U2r) = GetBoundaryUv(l, r2, lT, r2T);
                leftF.Add(new MeshFace(l, P1l, P2l, lT, U1l, U2l, mat));
                rightF.Add(new MeshFace(r1, r2, P2r, r1T, r2T, U2r, mat));
                rightF.Add(new MeshFace(r1, P2r, P1r, r1T, U2r, U1r, mat));
            }
            else if (Lpos.Count == 2 && Rpos.Count == 1)
            {
                int r = Rpos[0]; int l1 = Lpos[0], l2 = Lpos[1];
                int rT = Rtex[0]; int l1T = Ltex[0], l2T = Ltex[1];
                var (P1l, P1r) = GetBoundaryPos(r, l1);
                var (P2l, P2r) = GetBoundaryPos(r, l2);
                var (U1l, U1r) = GetBoundaryUv(r, l1, rT, l1T);
                var (U2l, U2r) = GetBoundaryUv(r, l2, rT, l2T);
                rightF.Add(new MeshFace(r, P1r, P2r, rT, U1r, U2r, mat));
                leftF.Add(new MeshFace(l1, l2, P2l, l1T, l2T, U2l, mat));
                leftF.Add(new MeshFace(l1, P2l, P1l, l1T, U2l, U1l, mat));
            }
            else
            {
                int onCount = (sa == 0 ? 1 : 0) + (sb == 0 ? 1 : 0) + (sc == 0 ? 1 : 0);
                if (onCount == 1 && Lpos.Count == 1 && Rpos.Count == 1)
                {
                    int onIdx = sa == 0 ? 0 : sb == 0 ? 1 : 2;
                    int onVert = V[onIdx]; int onUv = T[onIdx];
                    int leftVert = Lpos[0], leftUv = Ltex[0];
                    int rightVert = Rpos[0], rightUv = Rtex[0];
                    var (PleftL, PleftR) = GetBoundaryPos(leftVert, rightVert);
                    var (UleftL, UleftR) = GetBoundaryUv(leftVert, rightVert, leftUv, rightUv);
                    leftF.Add(new MeshFace(onVert, leftVert, PleftL, onUv, leftUv, UleftL, mat));
                    rightF.Add(new MeshFace(onVert, PleftR, rightVert, onUv, UleftR, rightUv, mat));
                }
                else if (onCount == 2)
                {
                    int sgn = sa + sb + sc;
                    if (sgn < 0) leftF.Add(new MeshFace(a, b, c, ta, tb, tc, mat));
                    else if (sgn > 0) rightF.Add(new MeshFace(a, b, c, ta, tb, tc, mat));
                }
                // onCount == 3: degenerate (all verts on plane within fp tolerance). Skip.
            }
            record:
            for (int k = lStart; k < leftF.Count; k++) leftFaceSrc.Add(srcIdx);
            for (int k = rStart; k < rightF.Count; k++) rightFaceSrc.Add(srcIdx);
        }

        // Winding-preservation post-pass: flip any sub-triangle whose normal
        // disagrees with its source's. Same pattern as in BoundarySkeleton.SplitAtPlane —
        // guards CW source from being emitted back-facing through the
        // CCW-preserving formulas above.
        FixWindingT(leftF, leftFaceSrc, srcNormals, leftV);
        FixWindingT(rightF, rightFaceSrc, srcNormals, rightV);

        return (PruneT(leftV, leftT, leftF), PruneT(rightV, rightT, rightF));
    }

    private static void FixWindingT(
        List<MeshFace> faces,
        List<int> srcIndices,
        List<(double, double, double)> srcNormals,
        List<Vertex3> verts)
    {
        for (int fi = 0; fi < faces.Count; fi++)
        {
            var nf = faces[fi];
            var (snx, sny, snz) = srcNormals[srcIndices[fi]];
            var na = verts[nf.IndexA]; var nb = verts[nf.IndexB]; var nc = verts[nf.IndexC];
            double ex = nb.X - na.X, ey = nb.Y - na.Y, ez = nb.Z - na.Z;
            double fx = nc.X - na.X, fy = nc.Y - na.Y, fz = nc.Z - na.Z;
            double nnx = ey*fz - ez*fy, nny = ez*fx - ex*fz, nnz = ex*fy - ey*fx;
            double dot = snx*nnx + sny*nny + snz*nnz;
            if (dot < 0)
            {
                faces[fi] = new MeshFace(nf.IndexA, nf.IndexC, nf.IndexB,
                                         nf.TexA, nf.TexC, nf.TexB, nf.MaterialIndex);
            }
        }
    }

    private static ClipResultT PruneT(List<Vertex3> verts, List<Vertex2> tex, List<MeshFace> faces)
    {
        if (faces.Count == 0)
            return new ClipResultT();

        // Compact positions: sorted set of referenced indices → contiguous remap.
        var usedV = new HashSet<int>();
        var usedT = new HashSet<int>();
        foreach (var f in faces)
        {
            usedV.Add(f.IndexA); usedV.Add(f.IndexB); usedV.Add(f.IndexC);
            usedT.Add(f.TexA); usedT.Add(f.TexB); usedT.Add(f.TexC);
        }
        var sortedV = new int[usedV.Count]; usedV.CopyTo(sortedV); Array.Sort(sortedV);
        var sortedT = new int[usedT.Count]; usedT.CopyTo(sortedT); Array.Sort(sortedT);
        var remapV = new Dictionary<int, int>(sortedV.Length);
        var remapT = new Dictionary<int, int>(sortedT.Length);
        var newV = new Vertex3[sortedV.Length];
        var newT = new Vertex2[sortedT.Length];
        for (int i = 0; i < sortedV.Length; i++) { remapV[sortedV[i]] = i; newV[i] = verts[sortedV[i]]; }
        for (int i = 0; i < sortedT.Length; i++) { remapT[sortedT[i]] = i; newT[i] = tex[sortedT[i]]; }

        var newFaces = new MeshFace[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            newFaces[i] = new MeshFace(
                remapV[f.IndexA], remapV[f.IndexB], remapV[f.IndexC],
                remapT[f.TexA], remapT[f.TexB], remapT[f.TexC],
                f.MaterialIndex);
        }
        return new ClipResultT { Vertices = newV, TexVertices = newT, Faces = newFaces };
    }

    /// <summary>
    /// Textured recursive split. Mirrors <see cref="RecursiveSplit"/> but
    /// threads UVs and material indices through every clip; emits
    /// <see cref="LeafTileT"/>s with attribute-preserving leaves.
    /// </summary>
    public static List<LeafTileT> RecursiveSplitT(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        Box3 bbox,
        SubdivisionShape shape,
        int maxVertsPerTile,
        int? maxDepth)
    {
        var leaves = new List<LeafTileT>();
        SplitIntoT(verts, tex, faces, bbox, new CellCoord(0, 0, 0, 0), shape, maxVertsPerTile, maxDepth, leaves);
        return leaves;
    }

    private static void SplitIntoT(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        Box3 bbox,
        CellCoord coord,
        SubdivisionShape shape,
        int maxVertsPerTile,
        int? maxDepth,
        List<LeafTileT> leaves)
    {
        if (faces.Count == 0) return;

        bool tooBig = verts.Count > (int)(maxVertsPerTile * OctreeSplitterRecursive.HeadroomFactor);
        bool depthCapHit = maxDepth.HasValue && coord.Level >= maxDepth.Value;
        if (!tooBig || depthCapHit)
        {
            var tightBounds = ComputeTriangleBoundsT(verts, faces);
            leaves.Add(new LeafTileT(
                coord,
                new ClipResultT
                {
                    Vertices = ToArrayLocal(verts),
                    TexVertices = ToArrayLocal(tex),
                    Faces = ToArrayLocal(faces),
                },
                tightBounds));
            return;
        }

        double cx = (bbox.Min.X + bbox.Max.X) * 0.5;
        double cy = (bbox.Min.Y + bbox.Max.Y) * 0.5;
        double cz = (bbox.Min.Z + bbox.Max.Z) * 0.5;

        var (xLow, xHigh) = ClipAtXT(verts, tex, faces, cx);

        if (shape == SubdivisionShape.Quadtree)
        {
            var (xLowYLow, xLowYHigh) = ClipAtYT(xLow.Vertices, xLow.TexVertices, xLow.Faces, cy);
            var (xHighYLow, xHighYHigh) = ClipAtYT(xHigh.Vertices, xHigh.TexVertices, xHigh.Faces, cy);
            Recurse(xLowYLow, new Box3(bbox.Min.X, bbox.Min.Y, bbox.Min.Z, cx, cy, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 0, Y = coord.Y * 2 + 0 });
            Recurse(xHighYLow, new Box3(cx, bbox.Min.Y, bbox.Min.Z, bbox.Max.X, cy, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 1, Y = coord.Y * 2 + 0 });
            Recurse(xLowYHigh, new Box3(bbox.Min.X, cy, bbox.Min.Z, cx, bbox.Max.Y, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 0, Y = coord.Y * 2 + 1 });
            Recurse(xHighYHigh, new Box3(cx, cy, bbox.Min.Z, bbox.Max.X, bbox.Max.Y, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + 1, Y = coord.Y * 2 + 1 });
        }
        else
        {
            var halves = new (ClipResultT half, double xMin, double xMax, int xi)[]
            {
                (xLow, bbox.Min.X, cx, 0),
                (xHigh, cx, bbox.Max.X, 1),
            };
            foreach (var (half, xMin, xMax, xi) in halves)
            {
                var (yL, yH) = ClipAtYT(half.Vertices, half.TexVertices, half.Faces, cy);
                var (yLzL, yLzH) = ClipAtZT(yL.Vertices, yL.TexVertices, yL.Faces, cz);
                var (yHzL, yHzH) = ClipAtZT(yH.Vertices, yH.TexVertices, yH.Faces, cz);
                Recurse(yLzL, new Box3(xMin, bbox.Min.Y, bbox.Min.Z, xMax, cy, cz), coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 0, Z = coord.Z * 2 + 0 });
                Recurse(yLzH, new Box3(xMin, bbox.Min.Y, cz, xMax, cy, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 0, Z = coord.Z * 2 + 1 });
                Recurse(yHzL, new Box3(xMin, cy, bbox.Min.Z, xMax, bbox.Max.Y, cz), coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 1, Z = coord.Z * 2 + 0 });
                Recurse(yHzH, new Box3(xMin, cy, cz, xMax, bbox.Max.Y, bbox.Max.Z), coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 1, Z = coord.Z * 2 + 1 });
            }
        }

        void Recurse(ClipResultT sub, Box3 subBbox, CellCoord subCoord)
            => SplitIntoT(sub.Vertices, sub.TexVertices, sub.Faces, subBbox, subCoord, shape, maxVertsPerTile, maxDepth, leaves);
    }

    private static T[] ToArrayLocal<T>(IReadOnlyList<T> list)
    {
        if (list is T[] arr) return arr;
        var copy = new T[list.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
        return copy;
    }

    private static Box3 ComputeTriangleBoundsT(IReadOnlyList<Vertex3> verts, IReadOnlyList<MeshFace> faces)
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

    /// <summary>
    /// Partition a textured mesh into 4^d (quadtree) or 8^d (octree) cells
    /// at exactly <paramref name="depth"/> by recursively clipping at axis
    /// midpoints. Unlike RecursiveSplitT, this does NOT short-circuit on
    /// vertex-count budgets — it always reaches the requested depth.
    /// </summary>
    public static Dictionary<CellCoord, ClipResultT> PartitionAtDepth(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        Box3 bbox,
        SubdivisionShape shape,
        int depth)
    {
        var result = new Dictionary<CellCoord, ClipResultT>();
        var input = new ClipResultT
        {
            Vertices = ToArrLocal(verts),
            TexVertices = ToArrLocal(tex),
            Faces = ToArrLocal(faces),
        };
        RecursePartition(input, bbox, new CellCoord(0, 0, 0, 0), depth, shape, result);
        return result;
    }

    private static void RecursePartition(
        ClipResultT input, Box3 bbox, CellCoord coord, int targetDepth,
        SubdivisionShape shape, Dictionary<CellCoord, ClipResultT> sink)
    {
        if (input.Faces.Length == 0) return;
        if (coord.Level >= targetDepth)
        {
            sink[coord] = input;
            return;
        }
        double cx = (bbox.Min.X + bbox.Max.X) * 0.5;
        double cy = (bbox.Min.Y + bbox.Max.Y) * 0.5;
        double cz = (bbox.Min.Z + bbox.Max.Z) * 0.5;
        var (xL, xH) = ClipAtXT(input.Vertices, input.TexVertices, input.Faces, cx);
        if (shape == SubdivisionShape.Quadtree)
        {
            var (xLyL, xLyH) = ClipAtYT(xL.Vertices, xL.TexVertices, xL.Faces, cy);
            var (xHyL, xHyH) = ClipAtYT(xH.Vertices, xH.TexVertices, xH.Faces, cy);
            RecursePartition(xLyL, new Box3(bbox.Min.X, bbox.Min.Y, bbox.Min.Z, cx, cy, bbox.Max.Z),
                coord with { Level = coord.Level + 1, X = coord.X * 2 + 0, Y = coord.Y * 2 + 0 }, targetDepth, shape, sink);
            RecursePartition(xHyL, new Box3(cx, bbox.Min.Y, bbox.Min.Z, bbox.Max.X, cy, bbox.Max.Z),
                coord with { Level = coord.Level + 1, X = coord.X * 2 + 1, Y = coord.Y * 2 + 0 }, targetDepth, shape, sink);
            RecursePartition(xLyH, new Box3(bbox.Min.X, cy, bbox.Min.Z, cx, bbox.Max.Y, bbox.Max.Z),
                coord with { Level = coord.Level + 1, X = coord.X * 2 + 0, Y = coord.Y * 2 + 1 }, targetDepth, shape, sink);
            RecursePartition(xHyH, new Box3(cx, cy, bbox.Min.Z, bbox.Max.X, bbox.Max.Y, bbox.Max.Z),
                coord with { Level = coord.Level + 1, X = coord.X * 2 + 1, Y = coord.Y * 2 + 1 }, targetDepth, shape, sink);
        }
        else
        {
            var halves = new (ClipResultT half, double xMin, double xMax, int xi)[]
            {
                (xL, bbox.Min.X, cx, 0), (xH, cx, bbox.Max.X, 1),
            };
            foreach (var (half, xMin, xMax, xi) in halves)
            {
                var (yL, yH) = ClipAtYT(half.Vertices, half.TexVertices, half.Faces, cy);
                var (yLzL, yLzH) = ClipAtZT(yL.Vertices, yL.TexVertices, yL.Faces, cz);
                var (yHzL, yHzH) = ClipAtZT(yH.Vertices, yH.TexVertices, yH.Faces, cz);
                RecursePartition(yLzL, new Box3(xMin, bbox.Min.Y, bbox.Min.Z, xMax, cy, cz),
                    coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 0, Z = coord.Z * 2 + 0 }, targetDepth, shape, sink);
                RecursePartition(yLzH, new Box3(xMin, bbox.Min.Y, cz, xMax, cy, bbox.Max.Z),
                    coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 0, Z = coord.Z * 2 + 1 }, targetDepth, shape, sink);
                RecursePartition(yHzL, new Box3(xMin, cy, bbox.Min.Z, xMax, bbox.Max.Y, cz),
                    coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 1, Z = coord.Z * 2 + 0 }, targetDepth, shape, sink);
                RecursePartition(yHzH, new Box3(xMin, cy, cz, xMax, bbox.Max.Y, bbox.Max.Z),
                    coord with { Level = coord.Level + 1, X = coord.X * 2 + xi, Y = coord.Y * 2 + 1, Z = coord.Z * 2 + 1 }, targetDepth, shape, sink);
            }
        }
    }

    private static T[] ToArrLocal<T>(IReadOnlyList<T> list)
    {
        if (list is T[] arr) return arr;
        var copy = new T[list.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
        return copy;
    }
}

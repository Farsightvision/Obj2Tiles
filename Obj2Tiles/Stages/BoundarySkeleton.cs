using System;
using System.Collections.Generic;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

/// <summary>
/// Per-depth set of source-mesh vertex indices the simplifier must not move.
/// </summary>
public sealed class BoundarySkeleton
{
    private readonly Dictionary<int, HashSet<int>> _byDepth = new();

    public void AddLockAt(int depth, int vertIndex)
    {
        if (!_byDepth.TryGetValue(depth, out var set))
            _byDepth[depth] = set = new HashSet<int>();
        set.Add(vertIndex);
    }

    public IReadOnlySet<int> LockedAt(int depth)
    {
        if (_byDepth.TryGetValue(depth, out var set)) return set;
        return new HashSet<int>();
    }

    public byte[] LockMaskFor(int depth, int vertCount)
    {
        var mask = new byte[vertCount];
        // Inherit shallower locks: depth-d cell planes are a superset of all shallower planes.
        for (int d = 0; d <= depth; d++)
        {
            if (_byDepth.TryGetValue(d, out var set))
                foreach (int i in set)
                    if (i >= 0 && i < vertCount) mask[i] = 1;
        }
        return mask;
    }

    /// <summary>
    /// Pre-insert intersection verts at every cell-boundary plane up to
    /// <paramref name="maxDepth"/>, returning the enriched mesh and skeleton.
    /// </summary>
    public static (
        List<Vertex3> verts,
        List<Vertex2> tex,
        List<MeshFace> faces,
        BoundarySkeleton skel
    ) BuildAndEnrich(
        IReadOnlyList<Vertex3> srcVerts,
        IReadOnlyList<Vertex2> srcTex,
        IReadOnlyList<MeshFace> srcFaces,
        Box3 sceneBounds,
        SubdivisionShape shape,
        int maxDepth)
    {
        var verts = new List<Vertex3>(srcVerts);
        var tex = new List<Vertex2>(srcTex);
        var faces = new List<MeshFace>(srcFaces);
        var skel = new BoundarySkeleton();

        for (int d = 1; d <= maxDepth; d++)
        {
            int divisions = 1 << d;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == 2 && shape == SubdivisionShape.Quadtree) continue;
                double minA = AxisOf(sceneBounds.Min, axis);
                double maxA = AxisOf(sceneBounds.Max, axis);
                double extent = maxA - minA;
                if (extent <= 0) continue;
                for (int i = 1; i < divisions; i++)
                {
                    double split = minA + extent * i / divisions;
                    SplitAtPlane(verts, tex, faces, axis, split);
                    for (int vi = 0; vi < verts.Count; vi++)
                    {
                        if (Math.Abs(AxisOf(verts[vi], axis) - split) < 1e-9)
                            skel.AddLockAt(d, vi);
                    }
                }
            }
        }

        return (verts, tex, faces, skel);
    }

    /// <summary>
    /// In-place split of faces crossing the <paramref name="axis"/>=<paramref name="split"/>
    /// plane, appending new intersection verts/UVs into a single combined mesh.
    /// </summary>
    private static void SplitAtPlane(
        List<Vertex3> verts,
        List<Vertex2> tex,
        List<MeshFace> faces,
        int axis,
        double split)
    {
        int n = verts.Count;
        int[] side = new int[n];
        for (int i = 0; i < n; i++)
        {
            double c = AxisOf(verts[i], axis);
            side[i] = c < split ? -1 : c > split ? +1 : 0;
        }

        var newFaces = new List<MeshFace>(faces.Count);
        var posCache = new Dictionary<(int a, int b), int>();
        var uvCache = new Dictionary<(int a, int b, int ta, int tb), int>();

        int GetIntersectVert(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (posCache.TryGetValue(key, out int hit)) return hit;
            var va = verts[a]; var vb = verts[b];
            double aA = AxisOf(va, axis), aB = AxisOf(vb, axis);
            double t = (split - aA) / (aB - aA);
            var newV = new Vertex3(
                va.X + t * (vb.X - va.X),
                va.Y + t * (vb.Y - va.Y),
                va.Z + t * (vb.Z - va.Z));
            newV = SetAxis(newV, axis, split);
            int idx = verts.Count;
            verts.Add(newV);
            posCache[key] = idx;
            return idx;
        }

        int GetIntersectUv(int a, int b, int ta, int tb)
        {
            var key = a < b ? (a, b, ta, tb) : (b, a, tb, ta);
            if (uvCache.TryGetValue(key, out int hit)) return hit;
            var va = verts[a]; var vb = verts[b];
            double aA = AxisOf(va, axis), aB = AxisOf(vb, axis);
            double t = (split - aA) / (aB - aA);
            var ua = tex[ta]; var ub = tex[tb];
            var newUv = new Vertex2(ua.X + t * (ub.X - ua.X), ua.Y + t * (ub.Y - ua.Y));
            int idx = tex.Count;
            tex.Add(newUv);
            uvCache[key] = idx;
            return idx;
        }

        // Record each source normal before splitting; the post-loop pass below flips
        // any sub-triangle whose normal disagrees, so CW sources aren't face-culled.
        var srcNormals = new List<(double, double, double)>(faces.Count);
        var newFaceSrcIndex = new List<int>(faces.Count * 3);

        foreach (var f in faces)
        {
            int a = f.IndexA, b = f.IndexB, c = f.IndexC;
            int ta = f.TexA, tb = f.TexB, tc = f.TexC;
            int sa = side[a], sb = side[b], sc = side[c];
            int mat = f.MaterialIndex;
            int srcIdx = srcNormals.Count;
            var va = verts[a]; var vb = verts[b]; var vc = verts[c];
            double ex = vb.X - va.X, ey = vb.Y - va.Y, ez = vb.Z - va.Z;
            double fx = vc.X - va.X, fy = vc.Y - va.Y, fz = vc.Z - va.Z;
            srcNormals.Add((ey*fz - ez*fy, ez*fx - ex*fz, ex*fy - ey*fx));

            int newFaceStart = newFaces.Count;
            if ((sa <= 0 && sb <= 0 && sc <= 0) || (sa >= 0 && sb >= 0 && sc >= 0))
            {
                newFaces.Add(f);
                for (int k = newFaceStart; k < newFaces.Count; k++) newFaceSrcIndex.Add(srcIdx);
                continue;
            }
            int[] V = { a, b, c };
            int[] T = { ta, tb, tc };
            int[] S = { sa, sb, sc };
            var L = new List<int>(); var Lt = new List<int>();
            var R = new List<int>(); var Rt = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                if (S[i] < 0) { L.Add(V[i]); Lt.Add(T[i]); }
                else if (S[i] > 0) { R.Add(V[i]); Rt.Add(T[i]); }
            }
            if (L.Count == 1 && R.Count == 2)
            {
                int l = L[0], r1 = R[0], r2 = R[1];
                int lT = Lt[0], r1T = Rt[0], r2T = Rt[1];
                int P1 = GetIntersectVert(l, r1);
                int P2 = GetIntersectVert(l, r2);
                int U1 = GetIntersectUv(l, r1, lT, r1T);
                int U2 = GetIntersectUv(l, r2, lT, r2T);
                newFaces.Add(new MeshFace(l, P1, P2, lT, U1, U2, mat));
                newFaces.Add(new MeshFace(r1, r2, P2, r1T, r2T, U2, mat));
                newFaces.Add(new MeshFace(r1, P2, P1, r1T, U2, U1, mat));
            }
            else if (L.Count == 2 && R.Count == 1)
            {
                int r = R[0], l1 = L[0], l2 = L[1];
                int rT = Rt[0], l1T = Lt[0], l2T = Lt[1];
                int P1 = GetIntersectVert(r, l1);
                int P2 = GetIntersectVert(r, l2);
                int U1 = GetIntersectUv(r, l1, rT, l1T);
                int U2 = GetIntersectUv(r, l2, rT, l2T);
                newFaces.Add(new MeshFace(r, P1, P2, rT, U1, U2, mat));
                newFaces.Add(new MeshFace(l1, l2, P2, l1T, l2T, U2, mat));
                newFaces.Add(new MeshFace(l1, P2, P1, l1T, U2, U1, mat));
            }
            else if (L.Count == 1 && R.Count == 1)
            {
                // One vertex is on the plane (apex); only the edge l-r crosses it.
                int onV = -1, onT = -1;
                for (int i = 0; i < 3; i++)
                    if (S[i] == 0) { onV = V[i]; onT = T[i]; break; }
                int l = L[0], r = R[0];
                int lT = Lt[0], rT = Rt[0];
                int P = GetIntersectVert(l, r);
                int U = GetIntersectUv(l, r, lT, rT);
                newFaces.Add(new MeshFace(l, P, onV, lT, U, onT, mat));
                newFaces.Add(new MeshFace(r, onV, P, rT, onT, U, mat));
            }
            else
            {
                // Degenerate or fully-on-plane: nothing to split.
                newFaces.Add(f);
            }
            for (int k = newFaceStart; k < newFaces.Count; k++) newFaceSrcIndex.Add(srcIdx);
        }

        // Flip any sub-triangle whose normal disagrees with its source's, so the
        // emitted winding matches the source and renderers don't cull it.
        for (int fi = 0; fi < newFaces.Count; fi++)
        {
            var nf = newFaces[fi];
            int srcIdx = newFaceSrcIndex[fi];
            var (snx, sny, snz) = srcNormals[srcIdx];
            var na = verts[nf.IndexA]; var nb = verts[nf.IndexB]; var nc = verts[nf.IndexC];
            double ex = nb.X - na.X, ey = nb.Y - na.Y, ez = nb.Z - na.Z;
            double fx = nc.X - na.X, fy = nc.Y - na.Y, fz = nc.Z - na.Z;
            double nnx = ey*fz - ez*fy, nny = ez*fx - ex*fz, nnz = ex*fy - ey*fx;
            double dot = snx*nnx + sny*nny + snz*nnz;
            if (dot < 0)
            {
                newFaces[fi] = new MeshFace(nf.IndexA, nf.IndexC, nf.IndexB,
                                            nf.TexA, nf.TexC, nf.TexB, nf.MaterialIndex);
            }
        }
        faces.Clear();
        faces.AddRange(newFaces);
    }

    private static double AxisOf(Vertex3 v, int axis)
        => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static Vertex3 SetAxis(Vertex3 v, int axis, double val)
        => axis == 0 ? new Vertex3(val, v.Y, v.Z)
        : axis == 1 ? new Vertex3(v.X, val, v.Z)
        : new Vertex3(v.X, v.Y, val);
}

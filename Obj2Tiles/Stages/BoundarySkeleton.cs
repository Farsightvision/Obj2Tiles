using System;
using System.Collections.Generic;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

/// <summary>
/// Set of source-mesh vertex indices that must remain unmoved by the
/// simplifier at each depth, indexed by depth. <see cref="AddLockAt"/>
/// adds verts at a specific depth; <see cref="LockedAt"/> returns only
/// the verts at that exact depth (no inheritance);
/// <see cref="LockMaskFor"/> returns a byte mask that DOES inherit
/// shallower-depth locks (depth-d cell-boundary planes are a superset
/// of all shallower depths' planes).
///
/// Used by ConformalHierarchyStage to build a per-depth `vertex_lock` byte
/// mask passed to <see cref="Obj2Tiles.Native.Meshopt.SimplifyWithAttributes"/>.
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
        // Inherit locks from depth 0 through `depth` — depth-d cell planes are
        // a superset of all shallower planes.
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
    /// <paramref name="maxDepth"/>. Returns the enriched (verts, tex,
    /// faces, skeleton). Each plane is processed by classifying every
    /// triangle by sign vs. plane, splitting crossing triangles into
    /// sub-triangles with new intersection verts and UVs.
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
                    // After this call, any vert whose `axis` coordinate equals
                    // `split` is on this plane → mark locked at depth d.
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
    /// In-place split-at-plane: classify each face by sign of `axis`-coord
    /// vs `split`. Faces fully on one side stay as-is. Crossing faces are
    /// split into 2 or 3 sub-faces with new intersection verts/UVs appended
    /// to the input lists. Same logic as OctreeSplitter.ClipAtAxisT but
    /// produces a single combined mesh instead of (left, right) halves.
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

        // Compute each source triangle's normal sign BEFORE splitting, then verify
        // every emitted sub-triangle's normal aligns. Source meshes from
        // photogrammetry have ~3% CW-relative-to-Z-up triangles (walls, undersides);
        // the L1R2/L2R1/L1R1 branches below assume CCW input, so CW sources would
        // otherwise get sub-triangles emitted with normal flipped — which Cesium's
        // face culling then drops, appearing as small green triangular holes
        // scattered across the rendered mesh. We keep the existing vertex-order
        // formulas and let the newFaces output go through a post-loop pass that
        // flips any sub-triangle whose normal disagrees with its source.
        var srcNormals = new List<(double, double, double)>(faces.Count);
        var newFaceSrcIndex = new List<int>(faces.Count * 3);

        foreach (var f in faces)
        {
            int a = f.IndexA, b = f.IndexB, c = f.IndexC;
            int ta = f.TexA, tb = f.TexB, tc = f.TexC;
            int sa = side[a], sb = side[b], sc = side[c];
            int mat = f.MaterialIndex;
            // Compute and store source-triangle normal for the post-loop winding check.
            int srcIdx = srcNormals.Count;
            var va = verts[a]; var vb = verts[b]; var vc = verts[c];
            double ex = vb.X - va.X, ey = vb.Y - va.Y, ez = vb.Z - va.Z;
            double fx = vc.X - va.X, fy = vc.Y - va.Y, fz = vc.Z - va.Z;
            srcNormals.Add((ey*fz - ez*fy, ez*fx - ex*fz, ex*fy - ey*fx));

            int newFaceStart = newFaces.Count;
            // Non-crossing: keep as-is.
            if ((sa <= 0 && sb <= 0 && sc <= 0) || (sa >= 0 && sb >= 0 && sc >= 0))
            {
                newFaces.Add(f);
                for (int k = newFaceStart; k < newFaces.Count; k++) newFaceSrcIndex.Add(srcIdx);
                continue;
            }
            // Crossing — produce sub-triangles.
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
            // All three crossing branches (L1R2, L2R1, L1R1) preserve CCW winding for
            // CCW source triangles (standard OBJ convention). Mixed/CW inputs will
            // have their sub-triangles emitted CCW, effectively flipping the normal.
            // ODM photogrammetry source is consistently CCW so this is acceptable;
            // fixing for CW would require detecting source winding and choosing a
            // different vertex order per branch.
            else if (L.Count == 1 && R.Count == 1)
            {
                // One vertex is exactly on the plane (S[i]==0); one is left, one right.
                // The on-plane vertex is the apex; only the edge l-r crosses the plane.
                // Find the on-plane vertex index and its UV.
                int onV = -1, onT = -1;
                for (int i = 0; i < 3; i++)
                    if (S[i] == 0) { onV = V[i]; onT = T[i]; break; }
                int l = L[0], r = R[0];
                int lT = Lt[0], rT = Rt[0];
                int P = GetIntersectVert(l, r);
                int U = GetIntersectUv(l, r, lT, rT);
                // Left triangle: (l, P, on)  — right triangle: (r, on, P)
                // Preserves CCW winding: source A→B→C, apex C is on-plane,
                // P is the intersection on edge A→B; left sub = A→P→C, right sub = B→C→P.
                newFaces.Add(new MeshFace(l, P, onV, lT, U, onT, mat));
                newFaces.Add(new MeshFace(r, onV, P, rT, onT, U, mat));
            }
            else
            {
                // All other on-plane combinations — preserve as-is (degenerate or
                // fully-on-plane triangles that don't need splitting).
                newFaces.Add(f);
            }
            for (int k = newFaceStart; k < newFaces.Count; k++) newFaceSrcIndex.Add(srcIdx);
        }

        // Post-loop winding-preservation fix: for each emitted sub-triangle,
        // compare its normal direction (sign of dot product) to its source's normal.
        // If they disagree, swap two indices to flip the winding. This corrects the
        // CW-source case where the fixed CCW-preserving vertex orderings in the
        // L1R2/L2R1/L1R1 branches above would otherwise emit a back-facing triangle
        // for CW source — which Cesium face-culls, producing the green-hole artifact.
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
                // Flip winding: swap IndexB <-> IndexC and TexB <-> TexC.
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

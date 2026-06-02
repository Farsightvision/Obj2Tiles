using System;
using System.Collections.Generic;
using System.Linq;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Native;

namespace Obj2Tiles.Stages;

/// <summary>
/// Per-tile metrics emitted by the simplifier. Surfaced to the build report
/// so we can confirm reduction actually happened.
/// </summary>
public struct SimplifyMetrics
{
    public int InputVerts;
    public int InputFaces;
    public int OutputVerts;
    public int OutputFaces;
    public int LockedVerts;
}

/// <summary>
/// Per-mesh simplification with <see cref="Meshopt.SimplifyOptions.LockBorder"/>.
/// Each child mesh is simplified independently; its outer perimeter (which
/// includes cell-boundary edges from clipping) is locked. Sibling tiles end up
/// with identical boundary triangulations because:
///   1. Triangle clipping in OctreeSplitter created exactly-shared positions on cell planes.
///   2. meshoptimizer never moves vertex positions (only removes them).
///   3. SIMPLIFY_LOCK_BORDER prevents collapse of perimeter edges.
/// </summary>
public static class HierarchicalDecimationStage
{
    /// <summary>
    /// Simplify a single child mesh with locked borders. Returns a new ClipResult
    /// whose vertex positions are a SUBSET of the input (no positions modified).
    /// </summary>
    /// <param name="input">The child mesh to simplify.</param>
    /// <param name="targetRatio">Fraction of original index count to retain (0..1).</param>
    public static ClipResult SimplifyChild(ClipResult input, float targetRatio)
    {
        if (targetRatio >= 1.0f || input.Faces.Length < 32)
            return input;

        // Flatten positions
        var flatPos = new float[input.Vertices.Length * 3];
        for (int i = 0; i < input.Vertices.Length; i++)
        {
            flatPos[i * 3 + 0] = (float)input.Vertices[i].X;
            flatPos[i * 3 + 1] = (float)input.Vertices[i].Y;
            flatPos[i * 3 + 2] = (float)input.Vertices[i].Z;
        }
        // Index buffer
        int totalIdx = input.Faces.Length * 3;
        var idx = new uint[totalIdx];
        for (int i = 0; i < input.Faces.Length; i++)
        {
            idx[i * 3 + 0] = (uint)input.Faces[i].IndexA;
            idx[i * 3 + 1] = (uint)input.Faces[i].IndexB;
            idx[i * 3 + 2] = (uint)input.Faces[i].IndexC;
        }
        var dst = new uint[totalIdx];

        int targetIdx = Math.Max(99, (int)(totalIdx * targetRatio));
        var n = Meshopt.Simplify(dst, idx, flatPos, targetIdx, targetError: 1.0f,
            options: Meshopt.SimplifyOptions.LockBorder, out _);

        // Compact: drop any vertex that no surviving face references
        var simpFaces = new List<Face>(n / 3);
        var used = new HashSet<int>();
        for (int i = 0; i < n; i += 3)
        {
            int a = (int)dst[i + 0], b = (int)dst[i + 1], c = (int)dst[i + 2];
            simpFaces.Add(new Face(a, b, c));
            used.Add(a); used.Add(b); used.Add(c);
        }
        if (used.Count == input.Vertices.Length)
            return new ClipResult { Vertices = input.Vertices, Faces = simpFaces.ToArray() };

        var sortedUsed = used.OrderBy(x => x).ToArray();
        var remap = new Dictionary<int, int>(sortedUsed.Length);
        var newVerts = new Vertex3[sortedUsed.Length];
        for (int i = 0; i < sortedUsed.Length; i++)
        {
            remap[sortedUsed[i]] = i;
            newVerts[i] = input.Vertices[sortedUsed[i]];
        }
        for (int i = 0; i < simpFaces.Count; i++)
        {
            var f = simpFaces[i];
            simpFaces[i] = new Face(remap[f.IndexA], remap[f.IndexB], remap[f.IndexC]);
        }
        return new ClipResult { Vertices = newVerts, Faces = simpFaces.ToArray() };
    }

    /// <summary>
    /// Textured-aware simplifier. Same locked-border guarantee as the
    /// position-only path; UVs and material indices survive untouched
    /// because meshopt_simplify only ever drops vertices/indices — it never
    /// invents new ones, so original per-vertex attributes (TexA/B/C and
    /// the face's MaterialIndex) carry through by index.
    /// </summary>
    public static ClipResultT SimplifyChild(ClipResultT input, float targetRatio)
    {
        if (targetRatio >= 1.0f || input.Faces.Length < 32)
            return input;

        var flatPos = new float[input.Vertices.Length * 3];
        for (int i = 0; i < input.Vertices.Length; i++)
        {
            flatPos[i * 3 + 0] = (float)input.Vertices[i].X;
            flatPos[i * 3 + 1] = (float)input.Vertices[i].Y;
            flatPos[i * 3 + 2] = (float)input.Vertices[i].Z;
        }
        int totalIdx = input.Faces.Length * 3;
        // meshopt operates on a single index buffer keyed by position; we feed
        // it position indices and recover the per-face attribute survivors by
        // walking the input faces. To do that we need to map each emitted
        // (a,b,c) triple back to its original face. We fan out per-face and
        // simplify on a synthetic index buffer that the original face set
        // exactly produces, so each output triple is the surviving form of
        // exactly one input face — its UVs and material index follow.
        var idx = new uint[totalIdx];
        for (int i = 0; i < input.Faces.Length; i++)
        {
            idx[i * 3 + 0] = (uint)input.Faces[i].IndexA;
            idx[i * 3 + 1] = (uint)input.Faces[i].IndexB;
            idx[i * 3 + 2] = (uint)input.Faces[i].IndexC;
        }
        var dst = new uint[totalIdx];

        int targetIdx = Math.Max(99, (int)(totalIdx * targetRatio));
        var n = Meshopt.Simplify(dst, idx, flatPos, targetIdx, targetError: 1.0f,
            options: Meshopt.SimplifyOptions.LockBorder, out _);

        // Pair up output positions with the closest input face's UVs/material.
        // meshoptimizer's simplify rewrites the index buffer to reference a
        // SUBSET of the original positions (LOCK_BORDER preserves perimeter
        // verts in place). For each output triple we look up the original
        // face that contained those three positions; if none does (the rare
        // case where simplify produced a never-existed triangle by collapse),
        // we fall back to the first input face that shared any of the three.
        // In the common case the lookup is exact.
        var faceLookup = new Dictionary<(int a, int b, int c), MeshFace>(input.Faces.Length);
        foreach (var f in input.Faces)
        {
            // store under sorted-tuple key so any rotation matches
            faceLookup[NormalizeTri(f.IndexA, f.IndexB, f.IndexC)] = f;
        }

        var simpFaces = new List<MeshFace>(n / 3);
        var usedV = new HashSet<int>();
        var usedT = new HashSet<int>();
        // Track UVs we've emitted for the SAME (position,uv) pair so we
        // don't duplicate the texture vertex pool.
        for (int i = 0; i < n; i += 3)
        {
            int a = (int)dst[i + 0], b = (int)dst[i + 1], c = (int)dst[i + 2];
            var key = NormalizeTri(a, b, c);
            if (!faceLookup.TryGetValue(key, out var src))
            {
                // Find any input face that contains all three positions.
                // (Falls through to a representative face's material.)
                src = input.Faces[0];
                foreach (var f in input.Faces)
                {
                    if ((f.IndexA == a || f.IndexB == a || f.IndexC == a) &&
                        (f.IndexA == b || f.IndexB == b || f.IndexC == b) &&
                        (f.IndexA == c || f.IndexB == c || f.IndexC == c))
                    { src = f; break; }
                }
            }
            // Map output position-corners back to the source face's UVs by
            // matching position indices.
            int taOut = MapUvForCorner(src, a);
            int tbOut = MapUvForCorner(src, b);
            int tcOut = MapUvForCorner(src, c);
            simpFaces.Add(new MeshFace(a, b, c, taOut, tbOut, tcOut, src.MaterialIndex));
            usedV.Add(a); usedV.Add(b); usedV.Add(c);
            usedT.Add(taOut); usedT.Add(tbOut); usedT.Add(tcOut);
        }

        // Compact unused positions/UVs.
        var sortedV = usedV.OrderBy(x => x).ToArray();
        var sortedT = usedT.OrderBy(x => x).ToArray();
        var remapV = new Dictionary<int, int>(sortedV.Length);
        var remapT = new Dictionary<int, int>(sortedT.Length);
        var newVerts = new Vertex3[sortedV.Length];
        var newTex = new Vertex2[sortedT.Length];
        for (int i = 0; i < sortedV.Length; i++) { remapV[sortedV[i]] = i; newVerts[i] = input.Vertices[sortedV[i]]; }
        for (int i = 0; i < sortedT.Length; i++) { remapT[sortedT[i]] = i; newTex[i] = input.TexVertices[sortedT[i]]; }
        for (int i = 0; i < simpFaces.Count; i++)
        {
            var f = simpFaces[i];
            f.IndexA = remapV[f.IndexA]; f.IndexB = remapV[f.IndexB]; f.IndexC = remapV[f.IndexC];
            f.TexA = remapT[f.TexA]; f.TexB = remapT[f.TexB]; f.TexC = remapT[f.TexC];
        }
        return new ClipResultT
        {
            Vertices = newVerts,
            TexVertices = newTex,
            Faces = simpFaces.ToArray()
        };
    }

    private static (int, int, int) NormalizeTri(int a, int b, int c)
    {
        // sort ascending — gives a rotation-invariant key for triangle position-triples
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static int MapUvForCorner(MeshFace src, int positionIdx)
    {
        if (src.IndexA == positionIdx) return src.TexA;
        if (src.IndexB == positionIdx) return src.TexB;
        if (src.IndexC == positionIdx) return src.TexC;
        // Position not in this face's corners (shouldn't happen for the
        // exact-match path; for the fallback we return TexA so the UV is at
        // least valid in the source mesh's UV pool).
        return src.TexA;
    }

    /// <summary>
    /// Parent-level simplification with selective vertex lock.
    /// Operates on a mesh that's already been welded across siblings (so
    /// sibling-shared cell-boundary verts are interior, NOT topological
    /// borders). Locks ONLY the verts that lie on the parent's outer-AABB
    /// faces — those become shared with adjacent parents at the next level
    /// up, so they must survive simplification.
    ///
    /// Naive per-child <c>SimplifyLockBorder</c> would over-lock by pinning
    /// every topological border of every child, including sibling-shared
    /// planes that should be free to simplify.
    /// </summary>
    /// <param name="input">Welded concatenation of the parent's children.</param>
    /// <param name="parentCellBounds">Parent cell's outer AABB (NOT the
    ///   tight triangle bounds; the actual cell box derived from level + coord).
    ///   Verts on these planes are locked.</param>
    /// <param name="targetRatio">Fraction of original index count to retain (0..1).</param>
    /// <param name="metrics">Diagnostic counters for the build report.</param>
    public static ClipResultT SimplifyParent(
        ClipResultT input,
        Box3 parentCellBounds,
        float targetRatio,
        out SimplifyMetrics metrics)
    {
        metrics = new SimplifyMetrics
        {
            InputVerts = input.Vertices.Length,
            InputFaces = input.Faces.Length,
        };

        if (targetRatio >= 1.0f || input.Faces.Length < 32)
        {
            metrics.OutputVerts = input.Vertices.Length;
            metrics.OutputFaces = input.Faces.Length;
            return input;
        }

        // Build the lock mask BEFORE simplify so we can report the locked count.
        // A vertex is locked iff it lies on one of the parent cell's six
        // outer-AABB face planes (within tolerance). The tolerance is keyed to
        // the cell size — clipping at exact axis snap should give bit-equality,
        // but we use 1e-9 * cell_extent to absorb any FP drift in the welding
        // dictionary key (we hash exact double tuples, so this is belt-and-
        // braces, not strictly required).
        //
        // Special case: callers can pass an "inverted" Box3 (Min > Max, e.g.
        // {+Inf,+Inf,+Inf} → {-Inf,-Inf,-Inf}) to mean "no lock at all"; this
        // is what the root level uses since the root has no neighbors and so
        // no boundary positions need to survive.
        var verts = input.Vertices;
        int n = verts.Length;
        var vertexLock = new byte[n];
        int lockedCount = 0;
        bool inverted = parentCellBounds.Min.X > parentCellBounds.Max.X
                     || parentCellBounds.Min.Y > parentCellBounds.Max.Y
                     || parentCellBounds.Min.Z > parentCellBounds.Max.Z;
        if (!inverted)
        {
            double sw = parentCellBounds.Max.X - parentCellBounds.Min.X;
            double sh = parentCellBounds.Max.Y - parentCellBounds.Min.Y;
            double sd = parentCellBounds.Max.Z - parentCellBounds.Min.Z;
            // Skip an axis if its extent is zero (degenerate cell) — every
            // vertex would be "on the plane" and we'd over-lock. This
            // matches the quadtree case at root where Z is full-scene
            // (sd > 0 always); the guard catches synthetic test inputs and
            // future octree edge cases.
            bool useX = sw > 0;
            bool useY = sh > 0;
            bool useZ = sd > 0;
            double tolX = Math.Max(1e-12, sw * 1e-9);
            double tolY = Math.Max(1e-12, sh * 1e-9);
            double tolZ = Math.Max(1e-12, sd * 1e-9);
            double cellMinX = parentCellBounds.Min.X, cellMaxX = parentCellBounds.Max.X;
            double cellMinY = parentCellBounds.Min.Y, cellMaxY = parentCellBounds.Max.Y;
            double cellMinZ = parentCellBounds.Min.Z, cellMaxZ = parentCellBounds.Max.Z;
            for (int i = 0; i < n; i++)
            {
                var v = verts[i];
                bool onX = useX && (Math.Abs(v.X - cellMinX) <= tolX || Math.Abs(v.X - cellMaxX) <= tolX);
                bool onY = useY && (Math.Abs(v.Y - cellMinY) <= tolY || Math.Abs(v.Y - cellMaxY) <= tolY);
                bool onZ = useZ && (Math.Abs(v.Z - cellMinZ) <= tolZ || Math.Abs(v.Z - cellMaxZ) <= tolZ);
                if (onX || onY || onZ)
                {
                    vertexLock[i] = MeshoptInteropLockByte;
                    lockedCount++;
                }
            }
        }
        metrics.LockedVerts = lockedCount;

        // Flatten positions into the contiguous layout meshopt wants.
        var flatPos = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            flatPos[i * 3 + 0] = (float)verts[i].X;
            flatPos[i * 3 + 1] = (float)verts[i].Y;
            flatPos[i * 3 + 2] = (float)verts[i].Z;
        }

        int totalIdx = input.Faces.Length * 3;
        var idx = new uint[totalIdx];
        for (int i = 0; i < input.Faces.Length; i++)
        {
            idx[i * 3 + 0] = (uint)input.Faces[i].IndexA;
            idx[i * 3 + 1] = (uint)input.Faces[i].IndexB;
            idx[i * 3 + 2] = (uint)input.Faces[i].IndexC;
        }
        var dst = new uint[totalIdx];

        int targetIdx = Math.Max(99, (int)(totalIdx * targetRatio));
        // SimplifyWithAttributes with attribute_count=0 gives us position-only
        // simplify with a vertex_lock mask. The empty-attributes variant is
        // documented as supported by meshoptimizer.
        var emptyAttrs = Array.Empty<float>();
        var emptyWeights = Array.Empty<float>();
        // No LockBorder flag here — sibling-shared interior verts are already
        // interior in the welded mesh, so meshopt won't touch them topologically;
        // outer-AABB verts are pinned via vertex_lock.
        var nOut = Meshopt.SimplifyWithAttributes(
            destinationIndices: dst,
            indices: idx,
            vertexPositionsXyz: flatPos,
            vertexAttributes: emptyAttrs,
            attributeWeights: emptyWeights,
            attributeCount: 0,
            vertexLock: vertexLock,
            targetIndexCount: targetIdx,
            targetError: 1.0f,
            options: Meshopt.SimplifyOptions.None,
            out _);

        // --- Map output triples back to input faces (UV/material survival).
        var faceLookup = new Dictionary<(int a, int b, int c), MeshFace>(input.Faces.Length);
        foreach (var f in input.Faces)
            faceLookup[NormalizeTri(f.IndexA, f.IndexB, f.IndexC)] = f;

        var simpFaces = new List<MeshFace>(nOut / 3);
        var usedV = new HashSet<int>();
        var usedT = new HashSet<int>();
        for (int i = 0; i < nOut; i += 3)
        {
            int a = (int)dst[i + 0], b = (int)dst[i + 1], c = (int)dst[i + 2];
            var key = NormalizeTri(a, b, c);
            if (!faceLookup.TryGetValue(key, out var src))
            {
                // Rare: meshopt produced a triangle that wasn't an input face
                // (shouldn't happen with vertex_lock + no LockBorder, but keep
                // the fallback to stay deterministic).
                src = input.Faces[0];
                foreach (var f in input.Faces)
                {
                    if ((f.IndexA == a || f.IndexB == a || f.IndexC == a) &&
                        (f.IndexA == b || f.IndexB == b || f.IndexC == b) &&
                        (f.IndexA == c || f.IndexB == c || f.IndexC == c))
                    { src = f; break; }
                }
            }
            int taOut = MapUvForCorner(src, a);
            int tbOut = MapUvForCorner(src, b);
            int tcOut = MapUvForCorner(src, c);
            simpFaces.Add(new MeshFace(a, b, c, taOut, tbOut, tcOut, src.MaterialIndex));
            usedV.Add(a); usedV.Add(b); usedV.Add(c);
            usedT.Add(taOut); usedT.Add(tbOut); usedT.Add(tcOut);
        }

        // Compact unused positions/UVs.
        var sortedV = usedV.OrderBy(x => x).ToArray();
        var sortedT = usedT.OrderBy(x => x).ToArray();
        var remapV = new Dictionary<int, int>(sortedV.Length);
        var remapT = new Dictionary<int, int>(sortedT.Length);
        var newVerts = new Vertex3[sortedV.Length];
        var newTex = new Vertex2[sortedT.Length];
        for (int i = 0; i < sortedV.Length; i++) { remapV[sortedV[i]] = i; newVerts[i] = input.Vertices[sortedV[i]]; }
        for (int i = 0; i < sortedT.Length; i++) { remapT[sortedT[i]] = i; newTex[i] = input.TexVertices[sortedT[i]]; }
        for (int i = 0; i < simpFaces.Count; i++)
        {
            var f = simpFaces[i];
            f.IndexA = remapV[f.IndexA]; f.IndexB = remapV[f.IndexB]; f.IndexC = remapV[f.IndexC];
            f.TexA = remapT[f.TexA]; f.TexB = remapT[f.TexB]; f.TexC = remapT[f.TexC];
        }

        metrics.OutputVerts = newVerts.Length;
        metrics.OutputFaces = simpFaces.Count;
        return new ClipResultT
        {
            Vertices = newVerts,
            TexVertices = newTex,
            Faces = simpFaces.ToArray(),
        };
    }

    // Lock byte value: meshoptimizer treats any non-zero as "locked".
    // We use 1 (which matches MeshoptInterop.VERTEX_LOCK).
    private const byte MeshoptInteropLockByte = 1;
}

using System;
using System.Collections.Generic;
using System.Linq;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Native;

namespace Obj2Tiles.Stages;

/// <summary>Per-tile simplification metrics for the build report.</summary>
public struct SimplifyMetrics
{
    public int InputVerts;
    public int InputFaces;
    public int OutputVerts;
    public int OutputFaces;
    public int LockedVerts;
}

/// <summary>
/// Per-mesh simplification with locked borders. meshopt never moves positions
/// and the border lock pins cell-boundary edges, so sibling tiles keep matching
/// boundary triangulations and don't crack.
/// </summary>
public static class HierarchicalDecimationStage
{
    /// <summary>Simplify a child mesh with locked borders; output positions are a subset of the input.</summary>
    public static ClipResult SimplifyChild(ClipResult input, float targetRatio)
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
    /// Textured simplifier with the same locked-border guarantee. meshopt only
    /// drops vertices/indices, so UVs and material indices carry through by index.
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

        // Map each output triple back to its source face to recover UVs/material;
        // fall back to a face sharing the positions if no exact match exists.
        var faceLookup = new Dictionary<(int a, int b, int c), MeshFace>(input.Faces.Length);
        foreach (var f in input.Faces)
        {
            faceLookup[NormalizeTri(f.IndexA, f.IndexB, f.IndexC)] = f;
        }

        var simpFaces = new List<MeshFace>(n / 3);
        var usedV = new HashSet<int>();
        var usedT = new HashSet<int>();
        for (int i = 0; i < n; i += 3)
        {
            int a = (int)dst[i + 0], b = (int)dst[i + 1], c = (int)dst[i + 2];
            var key = NormalizeTri(a, b, c);
            if (!faceLookup.TryGetValue(key, out var src))
            {
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
        return src.TexA;
    }

    /// <summary>
    /// Parent-level simplification on a sibling-welded mesh. Locks only the verts
    /// on the parent cell's outer-AABB planes (shared with adjacent parents one
    /// level up), leaving sibling-shared interior verts free to simplify.
    /// </summary>
    /// <param name="parentCellBounds">Parent cell's outer AABB (not the tight
    ///   triangle bounds). Verts on these planes are locked.</param>
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

        // A vertex is locked iff it lies on one of the parent cell's six
        // outer-AABB face planes, within a cell-size-scaled tolerance.
        // An inverted Box3 (Min > Max) means "no lock at all" — used by the
        // root level, which has no neighbors.
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
            // Skip a zero-extent axis (degenerate cell) — every vertex would
            // count as "on the plane" and we'd over-lock.
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
        // attribute_count=0 gives position-only simplify driven by the vertex_lock
        // mask. No LockBorder: sibling-shared verts are interior after welding,
        // and outer-AABB verts are pinned via the mask instead.
        var emptyAttrs = Array.Empty<float>();
        var emptyWeights = Array.Empty<float>();
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

        // Map output triples back to input faces to recover UVs/material.
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

    // meshoptimizer treats any non-zero byte as locked.
    private const byte MeshoptInteropLockByte = 1;
}

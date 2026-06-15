using System;
using System.Collections.Generic;
using System.IO;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;
using Obj2Tiles.Native;

namespace Obj2Tiles.Stages;

public static class ConformalHierarchyStage
{
    // meshopt vertex_lock byte semantics (MeshoptInterop is internal).
    private const byte VERTEX_LOCK    = 1 << 0;
    private const byte VERTEX_PROTECT = 1 << 1;


    /// <summary>
    /// Simplify <paramref name="faces"/> to <paramref name="targetRatio"/> of its
    /// face count, locking the supplied mask plus cluster/UV seam positions.
    /// Each face corner is fed as a distinct meshopt vertex so wedge attribute
    /// info at UV seams survives; seam wedges are marked VERTEX_PROTECT so the
    /// Permissive path refuses to collapse across the attribute discontinuity.
    /// </summary>
    public static MeshFace[] SimplifyLocked(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        byte[] lockMask,
        float targetRatio,
        out float shrinkRatio)
    {
        shrinkRatio = 1.0f;
        if (targetRatio >= 1.0f) return ToArr(faces);
        int n = verts.Count;
        if (faces.Count < 32) return ToArr(faces);

        // Per-position material set (≥2 ⇒ cross-material seam) plus
        // multi-UV-same-material seams (intra-material atlas boundaries).
        var posClusters = new Dictionary<int, HashSet<int>>(n);
        var posUvSeen = new Dictionary<int, Dictionary<int, int>>(n);
        var multiUvSameMatSeeds = new HashSet<int>();
        foreach (var f in faces)
        {
            AddClusterAndUv(posClusters, posUvSeen, multiUvSameMatSeeds, f.IndexA, f.TexA, f.MaterialIndex);
            AddClusterAndUv(posClusters, posUvSeen, multiUvSameMatSeeds, f.IndexB, f.TexB, f.MaterialIndex);
            AddClusterAndUv(posClusters, posUvSeen, multiUvSameMatSeeds, f.IndexC, f.TexC, f.MaterialIndex);
        }

        // Positions marked VERTEX_PROTECT: cross-material or multi-UV-same-material.
        var seamPositions = new HashSet<int>(multiUvSameMatSeeds);
        foreach (var kv in posClusters)
            if (kv.Value.Count > 1) seamPositions.Add(kv.Key);

        // BFS halo applies only to boundary-plane seeds; cluster-seam seeds
        // lock the seed position alone.
        const int BoundaryHaloHops = 2;

        int boundarySeedCount = 0;
        int seamSeedCount = 0;
        int overlapCount = 0;
        var boundarySeedSet = new HashSet<int>();
        var seamSeedSet = new HashSet<int>();
        int copyN = Math.Min(lockMask.Length, n);
        for (int i = 0; i < copyN; i++)
            if (lockMask[i] != 0) { boundarySeedSet.Add(i); boundarySeedCount++; }
        foreach (var kv in posClusters)
            if (kv.Value.Count >= 2) { seamSeedSet.Add(kv.Key); seamSeedCount++; }
        foreach (var s in seamSeedSet)
            if (boundarySeedSet.Contains(s)) overlapCount++;

        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = null;
        void AddEdge(int u, int v)
        {
            if (u == v) return;
            if (adj[u] == null) adj[u] = new List<int>(6);
            adj[u].Add(v);
        }
        foreach (var f in faces)
        {
            AddEdge(f.IndexA, f.IndexB); AddEdge(f.IndexB, f.IndexA);
            AddEdge(f.IndexB, f.IndexC); AddEdge(f.IndexC, f.IndexB);
            AddEdge(f.IndexC, f.IndexA); AddEdge(f.IndexA, f.IndexC);
        }
        var boundaryHalo = new HashSet<int>(boundarySeedSet);
        var frontier = new List<int>(boundarySeedSet);
        for (int hop = 0; hop < BoundaryHaloHops && frontier.Count > 0; hop++)
        {
            var next = new List<int>();
            foreach (var u in frontier)
            {
                var nbrs = adj[u];
                if (nbrs == null) continue;
                foreach (var v in nbrs)
                    if (boundaryHalo.Add(v)) next.Add(v);
            }
            frontier = next;
        }

        // effective lock = boundaryHalo ∪ seamSeeds.
        var effectiveLock = new byte[n];
        foreach (var v in boundaryHalo) if (v < n) effectiveLock[v] = 1;
        foreach (var v in seamSeedSet) effectiveLock[v] = 1;

        int lockedAfter = 0;
        for (int i = 0; i < n; i++) if (effectiveLock[i] != 0) lockedAfter++;
        double pct = n > 0 ? 100.0 * lockedAfter / n : 0.0;
        string warn = pct > 40.0 ? " WARN>40%" : "";
        Console.WriteLine(
            $" -> locks: n={n} boundarySeeds={boundarySeedCount} seamSeeds={seamSeedCount} " +
            $"overlap={overlapCount} haloK={BoundaryHaloHops} boundaryHalo={boundaryHalo.Count} " +
            $"lockedAfter={lockedAfter} pct={pct:F1}%{warn}");

        // Wedge-expansion: each face corner becomes a distinct meshopt vertex
        // so per-corner attribute info at UV seams survives into the simplifier.
        int nWedge = faces.Count * 3;
        var flatPosW = new float[nWedge * 3];
        var attrs = new float[nWedge * 2];
        var idx = new uint[nWedge];
        var wedgeLock = new byte[nWedge];
        var wedgeToPos = new int[nWedge];
        var wedgeToUv  = new int[nWedge];
        var wedgeToMat = new int[nWedge];

        for (int fi = 0; fi < faces.Count; fi++)
        {
            var face = faces[fi];
            int pA = face.IndexA, pB = face.IndexB, pC = face.IndexC;
            int uA = face.TexA,   uB = face.TexB,   uC = face.TexC;
            int wA = fi * 3 + 0, wB = fi * 3 + 1, wC = fi * 3 + 2;

            var va = verts[pA];
            flatPosW[wA * 3 + 0] = (float)va.X;
            flatPosW[wA * 3 + 1] = (float)va.Y;
            flatPosW[wA * 3 + 2] = (float)va.Z;
            var vb = verts[pB];
            flatPosW[wB * 3 + 0] = (float)vb.X;
            flatPosW[wB * 3 + 1] = (float)vb.Y;
            flatPosW[wB * 3 + 2] = (float)vb.Z;
            var vc = verts[pC];
            flatPosW[wC * 3 + 0] = (float)vc.X;
            flatPosW[wC * 3 + 1] = (float)vc.Y;
            flatPosW[wC * 3 + 2] = (float)vc.Z;

            var ta = tex[uA];
            attrs[wA * 2 + 0] = (float)ta.X;
            attrs[wA * 2 + 1] = (float)ta.Y;
            var tb = tex[uB];
            attrs[wB * 2 + 0] = (float)tb.X;
            attrs[wB * 2 + 1] = (float)tb.Y;
            var tc = tex[uC];
            attrs[wC * 2 + 0] = (float)tc.X;
            attrs[wC * 2 + 1] = (float)tc.Y;

            idx[wA] = (uint)wA;
            idx[wB] = (uint)wB;
            idx[wC] = (uint)wC;

            wedgeLock[wA] = BuildWedgeLockByte(pA, effectiveLock, seamPositions);
            wedgeLock[wB] = BuildWedgeLockByte(pB, effectiveLock, seamPositions);
            wedgeLock[wC] = BuildWedgeLockByte(pC, effectiveLock, seamPositions);

            wedgeToPos[wA] = pA; wedgeToPos[wB] = pB; wedgeToPos[wC] = pC;
            wedgeToUv[wA]  = uA; wedgeToUv[wB]  = uB; wedgeToUv[wC]  = uC;
            wedgeToMat[wA] = wedgeToMat[wB] = wedgeToMat[wC] = face.MaterialIndex;
        }

        // weights=[0,0] disables the attribute error term; seams are guarded by
        // VERTEX_PROTECT under the Permissive path instead.
        var weights = new[] { 0.0f, 0.0f };

        int totalIdx = nWedge;
        var dst = new uint[nWedge];
        int targetIdx = Math.Max(99, (int)(totalIdx * targetRatio));

        int nOut = Meshopt.SimplifyWithAttributes(
            destinationIndices: dst,
            indices: idx,
            vertexPositionsXyz: flatPosW,
            vertexAttributes: attrs,
            attributeWeights: weights,
            attributeCount: 2,
            vertexLock: wedgeLock,
            targetIndexCount: targetIdx,
            targetError: float.MaxValue,
            // Permissive is required: without it the wedge-expanded buffer makes
            // the default path refuse nearly all collapses. Permissive collapses
            // across attribute boundaries except where both endpoints are VERTEX_PROTECT.
            options: Meshopt.SimplifyOptions.Sparse | Meshopt.SimplifyOptions.ErrorAbsolute | Meshopt.SimplifyOptions.Permissive,
            out float resultError);

        shrinkRatio = (float)nOut / totalIdx;

        // Rebuild faces from the wedge back-maps, dropping degenerate or
        // cross-material output triangles.
        var outFaces = new List<MeshFace>(nOut / 3);
        int droppedDegenerate = 0;
        int droppedCrossMat = 0;
        for (int t = 0; t < nOut / 3; t++)
        {
            int a = (int)dst[t * 3 + 0];
            int b = (int)dst[t * 3 + 1];
            int c = (int)dst[t * 3 + 2];
            if (a == b || b == c || a == c) { droppedDegenerate++; continue; }
            int posA = wedgeToPos[a], posB = wedgeToPos[b], posC = wedgeToPos[c];
            if (posA == posB || posB == posC || posA == posC) { droppedDegenerate++; continue; }
            int matA = wedgeToMat[a], matB = wedgeToMat[b], matC = wedgeToMat[c];
            if (matA != matB || matB != matC) { droppedCrossMat++; continue; }
            int uvA = wedgeToUv[a], uvB = wedgeToUv[b], uvC = wedgeToUv[c];
            outFaces.Add(new MeshFace(posA, posB, posC, uvA, uvB, uvC, matA));
        }

        int wedgesLocked = 0, wedgesProtected = 0, wedgesBoth = 0, wedgesFree = 0;
        for (int wi = 0; wi < nWedge; wi++)
        {
            byte b = wedgeLock[wi];
            bool isLock = (b & VERTEX_LOCK) != 0;
            bool isProt = (b & VERTEX_PROTECT) != 0;
            if (isLock && isProt) wedgesBoth++;
            else if (isLock) wedgesLocked++;
            else if (isProt) wedgesProtected++;
            else wedgesFree++;
        }

        Console.WriteLine(
            $" -> wedge: in={faces.Count} wedges={nWedge} outTris={nOut / 3} " +
            $"emitted={outFaces.Count} droppedDegenerate={droppedDegenerate} " +
            $"droppedCrossMat={droppedCrossMat} ratio={shrinkRatio:F3} " +
            $"seamPositions={seamPositions.Count} " +
            $"wedges[free={wedgesFree} lock={wedgesLocked} prot={wedgesProtected} both={wedgesBoth}]");

        return outFaces.ToArray();
    }

    // Tracks per-position material set and flags positions that see >1 UV index
    // for the same material (intra-material atlas seam).
    private static void AddClusterAndUv(
        Dictionary<int, HashSet<int>> posClusters,
        Dictionary<int, Dictionary<int, int>> posUvSeen,
        HashSet<int> multiUvSameMatSeeds,
        int p, int uvIdx, int matIdx)
    {
        if (!posClusters.TryGetValue(p, out var s))
            posClusters[p] = s = new HashSet<int>();
        s.Add(matIdx);

        if (!posUvSeen.TryGetValue(p, out var byMat))
            posUvSeen[p] = byMat = new Dictionary<int, int>();
        if (!byMat.TryGetValue(matIdx, out int firstUv))
            byMat[matIdx] = uvIdx;
        else if (firstUv != uvIdx)
            multiUvSameMatSeeds.Add(p);
    }

    private static byte BuildWedgeLockByte(int p, byte[] effectiveLock, HashSet<int> seamPositions)
    {
        byte b = 0;
        if (p < effectiveLock.Length && effectiveLock[p] != 0)
            b |= VERTEX_LOCK;
        if (seamPositions.Contains(p))
            b |= VERTEX_PROTECT;
        return b;
    }

    private static MeshFace[] ToArr(IReadOnlyList<MeshFace> list)
    {
        var arr = new MeshFace[list.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = list[i];
        return arr;
    }

    /// <summary>
    /// Reject degenerate input early: zero faces (else the root CellCoord lookup
    /// throws KeyNotFoundException deep in tree-build) or a non-finite/zero scene
    /// diagonal (else NaN reaches tileset.json as "geometricError": NaN). Only an
    /// exactly-zero or non-finite diagonal is rejected, so flat models still build.
    /// </summary>
    public static void RequireTileableScene(int faceCount, double sceneDiagonal)
    {
        if (faceCount <= 0)
            throw new InvalidOperationException(
                "Hierarchical pipeline found no non-degenerate triangles after sanitization " +
                "(every face was zero-area / collinear). The input mesh has no tileable geometry — " +
                "check the source OBJ for valid, non-degenerate faces.");
        if (!double.IsFinite(sceneDiagonal) || sceneDiagonal <= 0.0)
            throw new InvalidOperationException(
                $"Hierarchical pipeline requires a finite, non-zero scene extent (bbox diagonal = {sceneDiagonal}). " +
                "The input geometry is a single point (all vertices coincident) or contains NaN/Inf coordinates.");
    }

    /// <summary>
    /// Top-down conformal hierarchy build: enrich the source with cell-boundary
    /// verts, then per depth simplify with that depth's skeleton lock and partition
    /// into cells, and wire parent/child nodes by CellCoord.
    /// </summary>
    public static HierarchicalNode BuildTreeConformal(
        IReadOnlyList<Vertex3> srcVerts,
        IReadOnlyList<Vertex2> srcTex,
        IReadOnlyList<MeshFace> srcFaces,
        Box3 sceneBounds,
        SubdivisionShape shape,
        int maxDepth,
        LodConfig[] lods)
    {
        double sceneDiagonal = Math.Sqrt(
            sceneBounds.Width * sceneBounds.Width +
            sceneBounds.Height * sceneBounds.Height +
            sceneBounds.Depth * sceneBounds.Depth);
        RequireTileableScene(srcFaces.Count, sceneDiagonal);

        var (enrichedVerts, enrichedTex, enrichedFaces, skel) =
            BoundarySkeleton.BuildAndEnrich(srcVerts, srcTex, srcFaces, sceneBounds, shape, maxDepth);

        var nodesByCoord = new Dictionary<CellCoord, HierarchicalNode>();

        // Per-depth simplify + partition. Each depth re-simplifies the same
        // immutable enriched mesh into its own results slot, so the compute is
        // independent and parallelizable; assembly below is serial in depth order.
        var perDepthCells = new Dictionary<CellCoord, ClipResultT>[maxDepth + 1];
        void ComputeDepth(int d)
        {
            // depth 0 (root) → most aggressive simplification; maxDepth → full detail.
            int lodIdx = Math.Min(maxDepth - d, lods.Length - 1);
            if (lodIdx < 0) lodIdx = 0;
            float ratio = lods[lodIdx].Quality;

            // REPLACE refinement can render a depth-d tile next to depth-(d+1)
            // children mid-transition, so the coarse mesh must preserve the next
            // finer cell-boundary planes too.
            int lockDepth = d < maxDepth ? d + 1 : d;
            byte[] mask = skel.LockMaskFor(lockDepth, enrichedVerts.Count);
            MeshFace[] simpFaces = SimplifyLocked(enrichedVerts, enrichedTex, enrichedFaces, mask, ratio, out float shrinkRatio);

            // Log-only: a depth can't be skipped without breaking parent-contains-children.
            if (shrinkRatio > 0.85f && d > 0 && ratio < 1.0f)
                Console.WriteLine($" -> stuck: depth={d} (shrink={shrinkRatio:F2}); target ratio={ratio:F2} but simplifier returned {shrinkRatio:F2}. Tiles still emitted; consider raising max depth if this fires repeatedly.");

            perDepthCells[d] = OctreeSplitter.PartitionAtDepth(enrichedVerts, enrichedTex, simpFaces, sceneBounds, shape, d);
        }
        System.Threading.Tasks.Parallel.For(0, maxDepth + 1, ComputeDepth);

        // Assemble serially in depth order to keep nodesByCoord insertion order stable.
        for (int d = 0; d <= maxDepth; d++)
        {
            foreach (var (coord, content) in perDepthCells[d])
            {
                if (content.Faces.Length == 0) continue;
                var node = new HierarchicalNode
                {
                    Coord = coord,
                    Bounds = ComputeBoundsLocal(content.Vertices),
                    TileContentT = content,
                };
                nodesByCoord[coord] = node;
            }
        }

        foreach (var n in nodesByCoord.Values)
        {
            if (n.Coord.Level == 0) continue;
            var parentCoord = ParentCoordLocal(n.Coord);
            if (nodesByCoord.TryGetValue(parentCoord, out var parent))
                parent.Children.Add(n);
        }

        var root = nodesByCoord[new CellCoord(0, 0, 0, 0)];

        // Widen parent bounds to contain every descendant AABB (3D Tiles requires
        // it); per-depth simplification can otherwise leave a parent tighter than
        // its children. Only the published bounding volume changes, not content.
        ExpandBoundsBottomUp(root);
        return root;
    }

    /// <summary>
    /// Source texture file size per material index; missing/unreachable files
    /// contribute 0.
    /// </summary>
    public static long[] ComputeMaterialTextureBytes(IReadOnlyList<Material> materials, string? objDirectory)
    {
        var result = new long[materials.Count];
        for (int i = 0; i < materials.Count; i++)
        {
            var m = materials[i];
            if (string.IsNullOrEmpty(m.Texture)) continue;
            try
            {
                var path = Path.IsPathRooted(m.Texture) || objDirectory == null
                    ? m.Texture
                    : Path.Combine(objDirectory, m.Texture);
                var fi = new FileInfo(path);
                if (fi.Exists) result[i] = fi.Length;
            }
            catch { /* best-effort; missing → 0 */ }
        }
        return result;
    }

    /// <summary>
    /// Source-texture bytes this tile claims via UV coverage:
    /// sum over faces of (uvArea × material texture-file bytes).
    /// </summary>
    public static long ComputeTileTextureBytes(ClipResultT tile, long[] texBytesPerMaterial)
    {
        if (tile == null || tile.Faces == null) return 0;
        double bytes = 0;
        for (int i = 0; i < tile.Faces.Length; i++)
        {
            var f = tile.Faces[i];
            if (f.MaterialIndex < 0 || f.MaterialIndex >= texBytesPerMaterial.Length) continue;
            long matBytes = texBytesPerMaterial[f.MaterialIndex];
            if (matBytes == 0) continue;
            // UV-area: |((tb-ta) × (tc-ta))/2| in normalized UV space.
            var ta = tile.TexVertices[f.TexA];
            var tb = tile.TexVertices[f.TexB];
            var tc = tile.TexVertices[f.TexC];
            double uvArea = 0.5 * Math.Abs((tb.X - ta.X) * (tc.Y - ta.Y) - (tc.X - ta.X) * (tb.Y - ta.Y));
            bytes += uvArea * matBytes;
        }
        return (long)bytes;
    }

    /// <summary>
    /// Collapse interior nodes whose content is too sparse to justify further
    /// subdivision (≤ tLeafTri triangles and ≤ tLeafTextureBytes claimed texture),
    /// dropping their descendants. Yields a non-uniform-depth tree.
    /// </summary>
    public static int PruneAdaptive(HierarchicalNode root, int hardCeiling, int tLeafTri,
        long tLeafTextureBytes, long[] texBytesPerMaterial)
    {
        int collapsedCount = 0;
        PruneAdaptiveImpl(root, hardCeiling, tLeafTri, tLeafTextureBytes, texBytesPerMaterial, ref collapsedCount);
        return collapsedCount;
    }

    private static void PruneAdaptiveImpl(HierarchicalNode node, int hardCeiling, int tLeafTri,
        long tLeafTextureBytes, long[] texBytesPerMaterial, ref int collapsedCount)
    {
        foreach (var c in node.Children) PruneAdaptiveImpl(c, hardCeiling, tLeafTri, tLeafTextureBytes, texBytesPerMaterial, ref collapsedCount);
        if (node.IsLeaf) return;
        if (node.Depth >= hardCeiling) return;
        if (node.TileContentT == null) return;
        int tri = node.TileContentT.Faces.Length;
        long tex = ComputeTileTextureBytes(node.TileContentT, texBytesPerMaterial);
        if (tri <= tLeafTri && tex <= tLeafTextureBytes)
        {
            int subtreeNodes = CountSubtree(node) - 1;
            node.Children.Clear();
            collapsedCount += subtreeNodes;
        }
    }

    private static int CountSubtree(HierarchicalNode node)
    {
        int n = 1;
        foreach (var c in node.Children) n += CountSubtree(c);
        return n;
    }

    public static int CountAllNodes(HierarchicalNode root) => CountSubtree(root);

    /// <summary>
    /// Predict the pow2 atlas side this node would get at pack time, clamped to
    /// the depth-appropriate cap. Mirrors HierarchicalAtlasStage but skips the
    /// source-detail floor (too expensive here).
    /// </summary>
    public static int PredictAtlasSide(HierarchicalNode node, AppConfig config, int maxDepth)
    {
        if (node.TileContentT == null || node.TileContentT.Faces.Length == 0)
            return config.AtlasMinSize;
        double aWorld = ComputeTileWorldArea(node.TileContentT);
        if (aWorld <= 0) return config.AtlasMinSize;

        int depth = node.Depth;
        double rD = LodDensitySchedule.DensityAtDepth(config.AtlasLeafDensityPxPerM, maxDepth, depth);

        int cap;
        if (node.IsLeaf)
        {
            cap = config.MaxAtlasSize;
        }
        else if (config.AtlasMaxDepthSchedule != null
                 && config.AtlasMaxDepthSchedule.TryGetValue(depth, out var sched))
        {
            cap = sched;
        }
        else
        {
            cap = config.MaxAtlasSizeInternal > 0 ? config.MaxAtlasSizeInternal : config.MaxAtlasSize;
        }

        double idealSide = Math.Sqrt(aWorld * rD * rD);
        int rounded = (int)Math.Max(1, Math.Round(idealSide));
        int pow2 = Obj2Tiles.Library.Common.NextPowerOfTwo(rounded);
        return Math.Clamp(pow2, config.AtlasMinSize, cap);
    }

    /// <summary>
    /// Total world-space surface area of a tile (sum of triangle areas in m²).
    /// </summary>
    public static double ComputeTileWorldArea(ClipResultT tile)
    {
        if (tile == null || tile.Faces == null) return 0;
        double a = 0;
        for (int i = 0; i < tile.Faces.Length; i++)
        {
            var f = tile.Faces[i];
            var va = tile.Vertices[f.IndexA];
            var vb = tile.Vertices[f.IndexB];
            var vc = tile.Vertices[f.IndexC];
            double abx = vb.X - va.X, aby = vb.Y - va.Y, abz = vb.Z - va.Z;
            double acx = vc.X - va.X, acy = vc.Y - va.Y, acz = vc.Z - va.Z;
            double cx = aby * acz - abz * acy;
            double cy = abz * acx - abx * acz;
            double cz = abx * acy - aby * acx;
            a += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }
        return a;
    }

    /// <summary>
    /// Deepening counterpart to PruneAdaptive: subdivide any leaf whose
    /// area-derived ideal side (sqrt(A_world) × r_d, r_d = leafDensity /
    /// 2^(maxDepth - depth)) exceeds <paramref name="maxAtlasSize"/>, recursing
    /// until it fits or hits <paramref name="hardCeiling"/>. Conservative: this
    /// lower bound under-fires rather than over-fires.
    /// </summary>
    public static int ExtendAdaptive(
        HierarchicalNode root,
        int autoDepth,
        int hardCeiling,
        int maxAtlasSize,
        int leafDensity,
        SubdivisionShape shape)
    {
        int added = 0;
        ExtendAdaptiveImpl(root, autoDepth, hardCeiling, maxAtlasSize, leafDensity, shape, ref added);
        return added;
    }

    private static void ExtendAdaptiveImpl(
        HierarchicalNode node,
        int autoDepth,
        int hardCeiling,
        int maxAtlasSize,
        int leafDensity,
        SubdivisionShape shape,
        ref int added)
    {
        if (!node.IsLeaf)
        {
            // Snapshot before iterating since deepening may add children.
            var snapshot = new List<HierarchicalNode>(node.Children);
            foreach (var c in snapshot)
                ExtendAdaptiveImpl(c, autoDepth, hardCeiling, maxAtlasSize, leafDensity, shape, ref added);
            return;
        }

        if (node.Depth >= hardCeiling) return;
        if (node.TileContentT == null || node.TileContentT.Faces.Length == 0) return;
        double aWorld = ComputeTileWorldArea(node.TileContentT);
        if (aWorld <= 0) return;
        double rD = LodDensitySchedule.DensityAtDepth(leafDensity, autoDepth, node.Depth);
        double idealSideLB = Math.Sqrt(aWorld) * rD;
        if (idealSideLB <= maxAtlasSize) return;  // already fits

        var bbox = node.Bounds;
        var cells = OctreeSplitter.PartitionAtDepth(
            node.TileContentT.Vertices,
            node.TileContentT.TexVertices,
            node.TileContentT.Faces,
            bbox, shape, depth: 1);

        if (cells.Count <= 1)
        {
            // Content doesn't subdivide along midpoints — leave as-is.
            return;
        }

        int parentX = node.Coord.X, parentY = node.Coord.Y, parentZ = node.Coord.Z;
        foreach (var (relCoord, content) in cells)
        {
            if (content.Faces.Length == 0) continue;
            var childCoord = new CellCoord(
                node.Depth + 1,
                parentX * 2 + relCoord.X,
                parentY * 2 + relCoord.Y,
                parentZ * 2 + relCoord.Z);
            var child = new HierarchicalNode
            {
                Coord = childCoord,
                Bounds = ComputeBoundsLocal(content.Vertices),
                TileContentT = content,
            };
            // Leaves get geometricError=0 per 3D Tiles spec.
            child.GeometricError = 0;
            node.Children.Add(child);
            added++;
        }

        // No longer a leaf: give it a non-zero geometric error (bbox diagonal)
        // so SSE refinement triggers.
        double bx = node.Bounds.Max.X - node.Bounds.Min.X;
        double by = node.Bounds.Max.Y - node.Bounds.Min.Y;
        double bz = node.Bounds.Max.Z - node.Bounds.Min.Z;
        node.GeometricError = Math.Sqrt(bx * bx + by * by + bz * bz);

        foreach (var c in node.Children)
            ExtendAdaptiveImpl(c, autoDepth, hardCeiling, maxAtlasSize, leafDensity, shape, ref added);
    }

    private static void ExpandBoundsBottomUp(HierarchicalNode node)
    {
        foreach (var c in node.Children) ExpandBoundsBottomUp(c);
        if (node.Children.Count == 0) return;
        double mnx = node.Bounds.Min.X, mny = node.Bounds.Min.Y, mnz = node.Bounds.Min.Z;
        double mxx = node.Bounds.Max.X, mxy = node.Bounds.Max.Y, mxz = node.Bounds.Max.Z;
        foreach (var c in node.Children)
        {
            if (c.Bounds.Min.X < mnx) mnx = c.Bounds.Min.X;
            if (c.Bounds.Min.Y < mny) mny = c.Bounds.Min.Y;
            if (c.Bounds.Min.Z < mnz) mnz = c.Bounds.Min.Z;
            if (c.Bounds.Max.X > mxx) mxx = c.Bounds.Max.X;
            if (c.Bounds.Max.Y > mxy) mxy = c.Bounds.Max.Y;
            if (c.Bounds.Max.Z > mxz) mxz = c.Bounds.Max.Z;
        }
        node.Bounds = new Box3(mnx, mny, mnz, mxx, mxy, mxz);
    }

    private static Box3 ComputeBoundsLocal(IReadOnlyList<Vertex3> verts)
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

    private static CellCoord ParentCoordLocal(CellCoord c)
        => new CellCoord(c.Level - 1, c.X / 2, c.Y / 2, c.Z / 2);
}

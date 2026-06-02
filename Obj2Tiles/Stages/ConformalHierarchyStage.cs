using System;
using System.Collections.Generic;
using System.IO;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;
using Obj2Tiles.Native;

namespace Obj2Tiles.Stages;

/// <summary>
/// Conformal hierarchy build. Pipeline: enrich source
/// (BoundarySkeleton.BuildAndEnrich), simplify per depth with locked skeleton
/// verts, partition each simplified mesh into cells via OctreeSplitter, emit
/// tiles.
/// </summary>
public static class ConformalHierarchyStage
{
    // Mirror meshopt vertex_lock byte semantics (MeshoptInterop is internal;
    // replicate the constants here verbatim — see meshoptimizer.h:472-478).
    private const byte VERTEX_LOCK    = 1 << 0;  // "should not be collapsed"
    private const byte VERTEX_PROTECT = 1 << 1;  // "preserve attribute discontinuity (Permissive only)"


    /// <summary>
    /// Run meshopt_simplifyWithAttributes with a wedge-expanded vertex buffer
    /// (each MeshFace's 3 corners becomes 3 DISTINCT meshopt vertices with
    /// their own pos + uv) plus the supplied per-vertex lock mask augmented
    /// with cluster-seam positions (positions touched by ≥2 distinct
    /// MaterialIndex values) and a BFS boundary halo. Returns the reduced
    /// face list. <paramref name="targetRatio"/> is the fraction of input
    /// faces to retain (1.0 = unchanged, 0.5 = half).
    ///
    /// Wedge-expansion rationale: a position-indexed attribute buffer
    /// (first-seen UV per position) destroys Hoppe-wedge info at UV-seam
    /// corners BEFORE meshopt can see it. By feeding each face-corner as a
    /// distinct vertex, meshopt's internal buildPositionRemap
    /// (simplifier.cpp:201) + wedge cyclic loop reconstructs the wedge groups
    /// automatically and refuses cross-wedge collapses. Each output corner
    /// directly carries (pos, uv, mat) via back-maps — no cluster-pick
    /// reconstruction needed.
    ///
    /// Seam protection has two interlocking parts:
    ///   1. Hard lock on multi-cluster positions + boundary-halo BFS —
    ///      collapse across material boundaries / chunk boundaries is
    ///      prohibited outright (lock propagated per-wedge).
    ///   2. Wedge-grouped UV attribute buffer — meshopt sees the true
    ///      per-corner (pos, uv) pair, not a single position-keyed
    ///      first-seen UV.
    /// </summary>
    public static MeshFace[] SimplifyLocked(
        IReadOnlyList<Vertex3> verts,
        IReadOnlyList<Vertex2> tex,
        IReadOnlyList<MeshFace> faces,
        byte[] lockMask,
        float targetRatio,
        out float shrinkRatio)
    {
        // shrinkRatio = nOut / totalIdx after simplification. 1.0 = simplifier
        // emitted as many indices as input (no-op); < targetRatio = met the
        // goal. clusterlod.h:628-633 marks groups terminal when ratio > 0.85 —
        // for our top-down pipeline we only LOG (caller decides), see
        // BuildTreeConformal.
        shrinkRatio = 1.0f;
        if (targetRatio >= 1.0f) return ToArr(faces);
        int n = verts.Count;
        if (faces.Count < 32) return ToArr(faces);

        // Per-position cluster set — materials touching position p (≥2 ⇒
        // seam vert). Used to build seamSeedSet for the hard lock. The
        // wedge-expanded position buffer is built later (flatPosW).
        // Also track multi-UV-same-material seams (intra-material atlas
        // boundaries) — these are Hoppe wedges meshopt's Permissive path will
        // happily collapse across UNLESS we mark them VERTEX_PROTECT.
        var posClusters = new Dictionary<int, HashSet<int>>(n);
        var posUvSeen = new Dictionary<int, Dictionary<int, int>>(n);
        var multiUvSameMatSeeds = new HashSet<int>();
        foreach (var f in faces)
        {
            AddClusterAndUv(posClusters, posUvSeen, multiUvSameMatSeeds, f.IndexA, f.TexA, f.MaterialIndex);
            AddClusterAndUv(posClusters, posUvSeen, multiUvSameMatSeeds, f.IndexB, f.TexB, f.MaterialIndex);
            AddClusterAndUv(posClusters, posUvSeen, multiUvSameMatSeeds, f.IndexC, f.TexC, f.MaterialIndex);
        }

        // Seam-position set: multi-material (cross-cluster) OR
        // multi-UV-same-mat. Marked VERTEX_PROTECT on each wedge instance so
        // meshopt's Permissive path refuses to collapse ACROSS the
        // attribute discontinuity (but does collapse interior verts freely).
        var seamPositions = new HashSet<int>(multiUvSameMatSeeds);
        foreach (var kv in posClusters)
            if (kv.Value.Count > 1) seamPositions.Add(kv.Key);

        // Differentiated BFS halo on lock seeds: cluster-seam seeds stay at k=0
        // (just the seed position is locked), boundary-plane seeds get haloed
        // with k=BoundaryHaloHops. Uniform k=3 over both seed kinds over-pins
        // 50-83% of verts because cluster-seam seeds dominate at deep depths.
        const int BoundaryHaloHops = 2;

        // Raw seed sets (distinct, telemetry-tracked).
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

        // BFS halo expansion ONLY on boundary-plane seeds.
        // Adjacency built from enriched face list (pre-simplification).
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

        // Compose effective lock = boundaryHalo ∪ seamSeeds.
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

        // Wedge-expansion. Each face's 3 corners becomes 3 DISTINCT meshopt
        // vertices in the buffer. This preserves Hoppe-wedge attribute info
        // that a position-indexed first-seen-UV buffer would destroy before
        // meshopt could see it. Per simplifier.cpp:201 (buildPositionRemap +
        // wedge cyclic loop) meshopt forms wedge groups automatically when
        // same-position-different-attribute vertices exist, and refuses
        // cross-wedge collapses.
        int nWedge = faces.Count * 3;
        var flatPosW = new float[nWedge * 3];
        var attrs = new float[nWedge * 2];
        var idx = new uint[nWedge];
        var wedgeLock = new byte[nWedge];
        var wedgeToPos = new int[nWedge];  // back-map: wedge index -> original posIdx
        var wedgeToUv  = new int[nWedge];  // back-map: wedge index -> original uvIdx
        var wedgeToMat = new int[nWedge];  // back-map: wedge index -> original matIdx

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

            // Wedge lock byte:
            //   VERTEX_LOCK (1)    — boundary-skeleton position (must remain in output
            //                        to keep partition coherent across siblings).
            //   VERTEX_PROTECT (2) — seam wedge (multi-mat OR multi-UV-same-mat).
            //                        Permissive collapses are refused specifically
            //                        ACROSS Protect wedges (preserves attr discontinuity).
            //   0                  — interior wedge, free to collapse under Permissive.
            // Both bits may be combined (clusterlod.h:383 OR's them).
            wedgeLock[wA] = BuildWedgeLockByte(pA, effectiveLock, seamPositions);
            wedgeLock[wB] = BuildWedgeLockByte(pB, effectiveLock, seamPositions);
            wedgeLock[wC] = BuildWedgeLockByte(pC, effectiveLock, seamPositions);

            wedgeToPos[wA] = pA; wedgeToPos[wB] = pB; wedgeToPos[wC] = pC;
            wedgeToUv[wA]  = uA; wedgeToUv[wB]  = uB; wedgeToUv[wC]  = uC;
            wedgeToMat[wA] = wedgeToMat[wB] = wedgeToMat[wC] = face.MaterialIndex;
        }

        // With SIMPLIFY_PERMISSIVE the principled seam guard is VERTEX_PROTECT
        // on the seam wedges themselves (see wedgeLock build above), not a
        // soft QEM bias on every collapse candidate. weights=[0,0] disables
        // the attribute term in the error metric; Permissive uses position
        // error and refuses Protect-Protect collapses outright.
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
            // Permissive is required: with every wedge-expanded edge candidate
            // hitting some attribute discontinuity, the default path refuses
            // 100% of collapses (ratio ~ 1.0 at every depth — no
            // simplification). Permissive allows collapses across attribute
            // boundaries EXCEPT where both endpoints carry VERTEX_PROTECT
            // (set on multi-mat / multi-UV-same-mat wedges).
            options: Meshopt.SimplifyOptions.Sparse | Meshopt.SimplifyOptions.ErrorAbsolute | Meshopt.SimplifyOptions.Permissive,
            out float resultError);

        // Record post-simplification shrink ratio (clusterlod.h:628-633).
        // Caller uses this to detect stuck groups (>0.85 = simplifier shaved
        // < 15% of triangles, suggesting deeper LOD recursion will produce
        // near-identical output).
        shrinkRatio = (float)nOut / totalIdx;

        // Each output triangle's 3 corners directly carry (pos, uv, mat) via
        // wedge back-maps. Permissive meshopt refuses cross-wedge collapses,
        // so the 3 wedges of an output triangle share a material with high
        // probability; the cross-material guard below is a belt-and-braces
        // telemetry counter.
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

        // Telemetry: count wedge bits actually set (independent diagnostic
        // for "Permissive engaged" sanity check).
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

    // Combined per-(pos, mat) UV-seen tracking — flags positions that see
    // >1 distinct UV index for the SAME material (intra-material atlas seam,
    // a Hoppe wedge). Plus cluster (multi-material) tracking.
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

    // Per-wedge lock byte construction. LOCK is the strong partition-preserving
    // constraint (boundary skeleton). PROTECT is the attribute-discontinuity
    // guard (seam wedge under Permissive). Both bits can coexist on a single
    // wedge (clusterlod.h:383 pattern).
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
    /// Guard the HLOD tree-build against degenerate input that yields no tileable
    /// geometry. Throws a clear, actionable <see cref="InvalidOperationException"/>
    /// (mirroring the no-UV guard in <c>RunHierarchicalPipeline</c>) instead of
    /// letting the pipeline fail opaquely or silently downstream:
    ///   • zero surviving triangles (every face zero-area / collinear after
    ///     sanitization) — otherwise <c>BuildTreeConformal</c> assembles an empty
    ///     node set and the root <c>CellCoord</c> lookup throws a bare
    ///     <c>KeyNotFoundException</c> deep in tree-build;
    ///   • a non-finite or zero scene diagonal (all vertices coincident, or NaN/Inf
    ///     coordinates) — otherwise NaN propagates into the measured geometric error
    ///     and is written to tileset.json as <c>"geometricError": NaN</c> (invalid
    ///     3D Tiles, emitted with a success exit code — silent garbage).
    /// Valid models pass unchanged: only an exactly-zero or non-finite diagonal is
    /// rejected, so flat (zero-thickness) and sub-millimetre models still build.
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
    /// Top-down conformal hierarchy build:
    ///   1. Enrich source: insert plane-intersection verts at every cell
    ///      boundary plane up to maxDepth (BoundarySkeleton.BuildAndEnrich).
    ///   2. For each depth d = 0..maxDepth:
    ///        simpFaces = SimplifyLocked(enriched, lockMask=skeleton@d, ratio=lods[d].Quality)
    ///        partition = OctreeSplitter.PartitionAtDepth(enrichedVerts, simpFaces, ...)
    ///        emit a HierarchicalNode per cell with TileContentT.
    ///   3. Wire parent/child relationships by CellCoord.
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
        // Fail fast on degenerate input (empty after sanitization, or a point /
        // NaN / Inf scene) with a clear diagnosis instead of an opaque
        // KeyNotFoundException at the root lookup or a silent "geometricError": NaN
        // in tileset.json. No-op for valid models, so byte-identical for real bakes.
        double sceneDiagonal = Math.Sqrt(
            sceneBounds.Width * sceneBounds.Width +
            sceneBounds.Height * sceneBounds.Height +
            sceneBounds.Depth * sceneBounds.Depth);
        RequireTileableScene(srcFaces.Count, sceneDiagonal);

        // 1. Enrich.
        var (enrichedVerts, enrichedTex, enrichedFaces, skel) =
            BoundarySkeleton.BuildAndEnrich(srcVerts, srcTex, srcFaces, sceneBounds, shape, maxDepth);

        var nodesByCoord = new Dictionary<CellCoord, HierarchicalNode>();

        // 2. Per-depth simplify + partition.
        // G12-BUILDTREE: each depth re-simplifies the SAME immutable enriched mesh with a
        // depth-specific lock mask, then partitions — so the per-depth COMPUTE (SimplifyLocked
        // + PartitionAtDepth) is INDEPENDENT across depths. Parallelize it into a per-depth
        // results array, then assemble nodesByCoord SERIALLY in depth order (the only shared
        // mutation). Byte-identical: the assembly insertion order is unchanged (depths
        // 0..maxDepth, each PartitionAtDepth dict in its own enumeration order) → identical
        // child-list order in the JSON. SimplifyLocked + PartitionAtDepth allocate fresh per
        // call (no shared buffers); meshopt uses per-call allocators.
        var perDepthCells = new Dictionary<CellCoord, ClipResultT>[maxDepth + 1];
        void ComputeDepth(int d)
        {
            // LOD index: depth 0 (root, farthest from leaves) → most aggressive
            // simplification; depth maxDepth (leaves) → full detail.
            int lodIdx = Math.Min(maxDepth - d, lods.Length - 1);
            if (lodIdx < 0) lodIdx = 0;
            float ratio = lods[lodIdx].Quality;

            // REPLACE refinement can render a depth-d tile next to depth-(d+1) children
            // mid-transition; the coarse mesh must preserve the next finer cell-boundary
            // planes too. Locks at d+1 are a superset of d's.
            int lockDepth = d < maxDepth ? d + 1 : d;
            byte[] mask = skel.LockMaskFor(lockDepth, enrichedVerts.Count);
            MeshFace[] simpFaces = SimplifyLocked(enrichedVerts, enrichedTex, enrichedFaces, mask, ratio, out float shrinkRatio);

            // clusterlod terminal signal (log only; can't skip a depth without breaking
            // parent-contains-children).
            if (shrinkRatio > 0.85f && d > 0 && ratio < 1.0f)
                Console.WriteLine($" -> stuck: depth={d} (shrink={shrinkRatio:F2}); target ratio={ratio:F2} but simplifier returned {shrinkRatio:F2}. Tiles still emitted; consider raising max depth if this fires repeatedly.");

            perDepthCells[d] = OctreeSplitter.PartitionAtDepth(enrichedVerts, enrichedTex, simpFaces, sceneBounds, shape, d);
        }
        // Parallel per-depth simplify+partition (G12) — output-identical to serial: each depth fills its own
        // perDepthCells slot (no shared buffers; meshopt uses per-call allocators), and assembly below is
        // serial in depth order, preserving nodesByCoord insertion order.
        System.Threading.Tasks.Parallel.For(0, maxDepth + 1, ComputeDepth);

        // Assemble serially in depth order — preserves nodesByCoord insertion order (byte-identical).
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

        // 3. Wire children.
        foreach (var n in nodesByCoord.Values)
        {
            if (n.Coord.Level == 0) continue;
            var parentCoord = ParentCoordLocal(n.Coord);
            if (nodesByCoord.TryGetValue(parentCoord, out var parent))
                parent.Children.Add(n);
        }

        var root = nodesByCoord[new CellCoord(0, 0, 0, 0)];

        // Expand parent bounds bottom-up so parent AABB contains every child
        // AABB. Required because per-depth simplification removes different
        // verts at each depth — a depth-0 parent's surviving verts can have a
        // tighter AABB than its depth-1 children, violating the 3D Tiles spec
        // (parent bounding volume must contain all descendants). The content
        // AABB stays the same; only the published bounding-volume is widened
        // for view culling.
        ExpandBoundsBottomUp(root);
        return root;
    }

    /// <summary>
    /// Compute per-material source texture file bytes once. Used by the
    /// adaptive-prune predicate to decide whether a tile is "dense enough" to
    /// keep splitting. Returns a parallel long[] indexed by material index;
    /// missing/unreachable files contribute 0.
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
    /// How many source-texture bytes does this tile "claim" via its UV
    /// coverage? Sum over faces of (uvArea × material texture-file bytes).
    /// Approximates the share of each source texture's bytes whose UV region
    /// lies inside this tile — the per-tile content-byte measure relevant for
    /// the 3D Tiles streaming budget.
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
    /// Adaptive-prune pass. After BuildTreeConformal has produced a
    /// uniform-depth tree, walk it and collapse interior nodes whose subtree
    /// has so-little content that further subdivision is wasted streaming
    /// budget. A node becomes a leaf when:
    ///   - it is already at <paramref name="hardCeiling"/> (no choice), OR
    ///   - its TileContent has ≤ tLeafTri triangles AND ≤ tLeafTextureBytes
    ///     of "claimed" source texture content (per-face UV-area × material
    ///     file size).
    /// Children of collapsed nodes are dropped. The collapsed node keeps its
    /// per-depth-simplified content (less detail than the leaf-LOD would have,
    /// but content density is low enough that this is acceptable; sparse tiles
    /// don't need the leaf-LOD level of detail).
    /// Result: tree depth is non-uniform — dense regions keep going to
    /// <paramref name="hardCeiling"/>, sparse regions collapse earlier.
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
        // Recurse children first so any sub-collapse is reflected.
        foreach (var c in node.Children) PruneAdaptiveImpl(c, hardCeiling, tLeafTri, tLeafTextureBytes, texBytesPerMaterial, ref collapsedCount);
        if (node.IsLeaf) return;
        if (node.Depth >= hardCeiling) return; // already at deepest allowed
        if (node.TileContentT == null) return;
        int tri = node.TileContentT.Faces.Length;
        long tex = ComputeTileTextureBytes(node.TileContentT, texBytesPerMaterial);
        if (tri <= tLeafTri && tex <= tLeafTextureBytes)
        {
            // Mark this node as a leaf — drop all descendants.
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

    /// <summary>
    /// Public wrapper for the recursive node count, used by the
    /// --max-tile-count guard after ExtendAdaptive.
    /// </summary>
    public static int CountAllNodes(HierarchicalNode root) => CountSubtree(root);

    /// <summary>
    /// Predict the atlas side this node would receive at pack time.
    /// Mirrors HierarchicalAtlasStage's area + cap logic but skips the
    /// source-detail floor (which requires TexturesCache I/O — too expensive
    /// for the geometric-error pass over every node). Returns the pow2-rounded
    /// side clamped to the depth-appropriate cap. Used by
    /// AmplifyGeometricErrorByTextureDeficit to compare parent vs. child
    /// texel density and force refinement on tiles whose atlas is the
    /// bottleneck (not their geometry).
    /// </summary>
    public static int PredictAtlasSide(HierarchicalNode node, AppConfig config, int maxDepth)
    {
        if (node.TileContentT == null || node.TileContentT.Faces.Length == 0)
            return config.AtlasMinSize;
        double aWorld = ComputeTileWorldArea(node.TileContentT);
        if (aWorld <= 0) return config.AtlasMinSize;

        int depth = node.Depth;
        // LOD density schedule r_d (single definition in LodDensitySchedule.DensityAtDepth).
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
    /// Compute the total world-space surface area of a tile (sum of triangle
    /// areas in m²). Cheap to derive — single pass over Faces. Used by
    /// ExtendAdaptive's "should this leaf deepen?" predicate.
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
    /// Mirror of PruneAdaptive but in the deepening direction. After
    /// PruneAdaptive has settled the upper bound, walk the leaf set and
    /// subdivide any leaf whose area-derived ideal_side exceeds the leaf cap
    /// (maxAtlasSize). New children are partitioned from the leaf's existing
    /// content via OctreeSplitter — using a fresh top-level call against only
    /// that leaf's geometry, so we don't touch unrelated branches. Recurses
    /// until either the leaf fits or hits <paramref name="hardCeiling"/>.
    ///
    /// Predicate (lower bound; the actual r_eff used at pack time may be
    /// higher when the source-detail floor lifts it past r_d):
    ///   ideal_side ≥ sqrt(A_world × r_d²)  where r_d = leafDensity / 2^(maxDepth - depth)
    /// If this lower bound already exceeds maxAtlasSize, the actual ideal
    /// must also exceed it — safe to deepen. Conservative: under-fires
    /// rather than over-fires (will miss some dense leaves that have a
    /// source-detail-floor lift but no lift from depth-derived r_d alone;
    /// those still hit the cap in the legacy clamp path and the user can
    /// raise leafDensity to deepen them).
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
        // Walk children FIRST (top-down so we don't repeatedly check the
        // same subtree as we deepen it).
        if (!node.IsLeaf)
        {
            // Copy list before iterating since ExtendAdaptive may add children.
            var snapshot = new List<HierarchicalNode>(node.Children);
            foreach (var c in snapshot)
                ExtendAdaptiveImpl(c, autoDepth, hardCeiling, maxAtlasSize, leafDensity, shape, ref added);
            return;
        }

        // Leaf: check whether it needs deepening.
        if (node.Depth >= hardCeiling) return;
        if (node.TileContentT == null || node.TileContentT.Faces.Length == 0) return;
        double aWorld = ComputeTileWorldArea(node.TileContentT);
        if (aWorld <= 0) return;
        // r_d at this leaf's depth. When depth > autoDepth (we're already in
        // deepened territory), r_d continues to double per step — matches
        // the LOD-density rule.
        double rD = LodDensitySchedule.DensityAtDepth(leafDensity, autoDepth, node.Depth);
        double idealSideLB = Math.Sqrt(aWorld) * rD;
        if (idealSideLB <= maxAtlasSize) return;  // already fits

        // Subdivide this leaf into 4 (quadtree) / 8 (octree) children.
        var bbox = node.Bounds;
        var cells = OctreeSplitter.PartitionAtDepth(
            node.TileContentT.Vertices,
            node.TileContentT.TexVertices,
            node.TileContentT.Faces,
            bbox, shape, depth: 1);

        if (cells.Count <= 1)
        {
            // Single non-empty cell — partitioning didn't help (content
            // doesn't subdivide along midpoints). Leave as-is.
            return;
        }

        // Map relative cell coords (Level=1, X/Y/Z=0|1) to global coords.
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

        // Parent is no longer a leaf — assign a reasonable geometric error
        // (the previous leaf was at the deepest LOD with err=0; promote
        // it to something > 0 so SSE refinement triggers). Use the leaf-
        // bbox diagonal as a coarse heuristic.
        double bx = node.Bounds.Max.X - node.Bounds.Min.X;
        double by = node.Bounds.Max.Y - node.Bounds.Min.Y;
        double bz = node.Bounds.Max.Z - node.Bounds.Min.Z;
        node.GeometricError = Math.Sqrt(bx * bx + by * by + bz * bz);

        // Recurse into the new children — they themselves may need deepening.
        foreach (var c in node.Children)
            ExtendAdaptiveImpl(c, autoDepth, hardCeiling, maxAtlasSize, leafDensity, shape, ref added);
    }

    // (ComputeBoundsLocal already exists below — reused by ExtendAdaptive.)

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

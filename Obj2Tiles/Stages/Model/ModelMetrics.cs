using System;
using System.Collections.Generic;
using System.IO;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;

namespace Obj2Tiles.Stages.Model;

// Input-mesh metrics used by the dynamic depth selector. Pure data;
// populated once at the start of the hierarchical pipeline.
public sealed record ModelMetrics(
    long Triangles,
    long Vertices,
    double BBoxDiag,
    long TextureBytes,
    long DecodedTextureBytes)
{
    public override string ToString() =>
        $"tris={Triangles:N0} verts={Vertices:N0} bbox_diag={BBoxDiag:F2}m " +
        $"tex={TextureBytes / 1_048_576.0:F1}MiB decoded={DecodedTextureBytes / 1_048_576.0:F1}MiB";

    public static ModelMetrics Compute(
        long triangleCount,
        long vertexCount,
        Box3 bounds,
        IReadOnlyList<Material> materials,
        string? objDirectory)
    {
        double w = bounds.Width, h = bounds.Height, d = bounds.Depth;
        double diag = Math.Sqrt(w * w + h * h + d * d);

        long texBytes = 0;
        long decodedBytes = 0;
        if (objDirectory != null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in materials)
            {
                if (string.IsNullOrEmpty(m.Texture)) continue;
                var path = Path.IsPathRooted(m.Texture)
                    ? m.Texture
                    : Path.Combine(objDirectory, m.Texture);
                if (!seen.Add(path)) continue;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists) texBytes += fi.Length;
                }
                catch { /* file may have moved; metrics are best-effort */ }
                try
                {
                    // Decoded RGBA32 footprint (Σ W*H*4) — the quantity that
                    // actually drives Phase-1 peak RAM. TextureBytes above is the
                    // COMPRESSED on-disk size (>50x smaller for big PNGs), so it
                    // must NOT be used for the memory gate. GetTextureInfo reads
                    // only the image header (Image.Identify, cached) — no decode.
                    var info = Obj2Tiles.Library.TexturesCache.GetTextureInfo(path);
                    decodedBytes += (long)info.Width * info.Height * 4L;
                }
                catch { /* unreadable header; best-effort */ }
            }
        }

        return new ModelMetrics(triangleCount, vertexCount, diag, texBytes, decodedBytes);
    }

    // 2-level centroid dry run → effective branching factor B_eff.
    // Runs a cheap simulated 2-level subdivision over triangle centroids;
    // counts non-empty cells at level 2 (4*4 = 16 quadtree max /
    // 4*4*4 = 64 octree max); derives B_eff = occupied^(1/leafLevel). For
    // surface-like photogrammetry meshes this typically returns ~4-6 (not
    // the theoretical 8) because vertical splits over a flat-ish surface
    // produce empty cells.
    //
    // O(faces) work, no allocations beyond the small HashSet — orders of
    // magnitude cheaper than the actual tree build.
    public static double EstimateEffectiveBranching(
        IReadOnlyList<Vertex3> meshVerts,
        IReadOnlyList<MeshFace> meshFaces,
        Box3 bounds,
        SubdivisionShape shape)
    {
        const int gridPerAxis = 4; // 2 levels of 2-way splits per axis
        bool isOctree = shape == SubdivisionShape.Octree;

        double bx = bounds.Min.X, by = bounds.Min.Y, bz = bounds.Min.Z;
        double w = Math.Max(bounds.Width, 1e-9);
        double h = Math.Max(bounds.Height, 1e-9);
        double d = Math.Max(bounds.Depth, 1e-9);

        var occupied = new HashSet<int>(64);
        foreach (var f in meshFaces)
        {
            var va = meshVerts[f.IndexA];
            var vb = meshVerts[f.IndexB];
            var vc = meshVerts[f.IndexC];
            double cx = (va.X + vb.X + vc.X) / 3.0;
            double cy = (va.Y + vb.Y + vc.Y) / 3.0;
            double cz = (va.Z + vb.Z + vc.Z) / 3.0;
            int ix = Math.Clamp((int)Math.Floor((cx - bx) / w * gridPerAxis), 0, gridPerAxis - 1);
            int iy = Math.Clamp((int)Math.Floor((cy - by) / h * gridPerAxis), 0, gridPerAxis - 1);
            int key;
            if (isOctree)
            {
                int iz = Math.Clamp((int)Math.Floor((cz - bz) / d * gridPerAxis), 0, gridPerAxis - 1);
                key = (ix * gridPerAxis + iy) * gridPerAxis + iz;
            }
            else
            {
                key = ix * gridPerAxis + iy;
            }
            occupied.Add(key);
        }

        // leafLevel = 2: solve B_eff^2 = occupied  →  B_eff = sqrt(occupied).
        // Clamp to the physically meaningful range [2, 4 for quad / 8 for oct]
        // so a sparse model can't produce e.g. B_eff = 1.4 which would push
        // the depth formula to absurd values.
        int maxFanout = isOctree ? 8 : 4;
        double bEff = Math.Sqrt(occupied.Count);
        return Math.Clamp(bEff, 2.0, (double)maxFanout);
    }

    // Closed-form depth selector.
    //   maxDepth = max(
    //     ceil(log_B(N_tri  / T_leaf_tri)),
    //     ceil(log_B(N_vert / T_leaf_vert)))   // clamped to [1, 6]
    //
    // The returned value is the `maxDepth` arg to BuildTreeConformal — the
    // total number of tree levels (root + child levels).
    //
    // Inputs:
    //   T_leaf_tri  = 25_000  (calibrated to hit a ~16-64-leaf target on
    //                          medium photogrammetry fixtures)
    //   T_leaf_vert ≈ T_leaf_tri × 0.6   (vertices-per-triangle for textured
    //                                     photogrammetry)
    //   B_eff       adaptive — passed in by caller from
    //               EstimateEffectiveBranching
    //
    // Clamped to [1, 6]. Depth 0 makes no sense (no root); depth 7+ enters
    // the implicit-tiling subtreeLevels overflow risk zone. A boundary-lock
    // ceiling (~3 for our pipeline) is enforced softly by the sampled-probe
    // refinement step downstream.
    public static int OptimalDepthsClosedForm(
        ModelMetrics m,
        double bEff,
        int triLeafTarget = 25_000,
        int vertLeafTarget = 15_000,
        long textureBytesLeafTarget = 0)
    {
        if (bEff <= 1.001) bEff = 4.0; // safety: avoid log(1.0) = 0 → div-by-zero
        double logB = Math.Log(bEff);

        double dTri = m.Triangles <= triLeafTarget ? 0.0
            : Math.Log((double)m.Triangles / triLeafTarget) / logB;
        double dVert = m.Vertices <= vertLeafTarget ? 0.0
            : Math.Log((double)m.Vertices / vertLeafTarget) / logB;

        // Texture-byte axis so dense-texture fixtures auto-pick a deeper
        // tree. The pipeline's uniform-depth partitioner still distributes
        // a single chosen depth across the tree; raising the chosen depth
        // when source-texture bytes exceed the per-leaf budget gives the
        // renderer more, smaller leaves whose individual texture demand is
        // bounded. textureBytesLeafTarget=0 disables this axis (legacy
        // behavior, e.g. for callers that don't yet plumb TextureBytes).
        double dTex = 0.0;
        if (textureBytesLeafTarget > 0 && m.TextureBytes > textureBytesLeafTarget)
        {
            dTex = Math.Log((double)m.TextureBytes / textureBytesLeafTarget) / logB;
        }

        int maxDepth = Math.Max(Math.Max((int)Math.Ceiling(dTri), (int)Math.Ceiling(dVert)),
                                (int)Math.Ceiling(dTex));
        return Math.Clamp(maxDepth, 1, 6);
    }
}

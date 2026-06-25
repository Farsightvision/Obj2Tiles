using System;
using System.Collections.Generic;
using System.IO;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;

namespace Obj2Tiles.Stages.Model;

// Input-mesh metrics used by the dynamic depth selector.
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

            void Account(string? texturePath, bool countCompressed)
            {
                if (string.IsNullOrEmpty(texturePath)) return;
                var path = Path.IsPathRooted(texturePath)
                    ? texturePath
                    : Path.Combine(objDirectory, texturePath);
                if (!seen.Add(path)) return;
                if (countCompressed)
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists) texBytes += fi.Length;
                    }
                    catch { /* best-effort */ }
                }
                try
                {
                    var info = Obj2Tiles.Library.TexturesCache.GetTextureInfo(path);
                    decodedBytes += (long)info.Width * info.Height * 4L;
                }
                catch { /* best-effort */ }
            }

            // Diffuse first so a shared normal-map path can't steal a later diffuse's dedup slot;
            // TextureBytes feeds the depth axis and must stay diffuse-only.
            foreach (var m in materials)
                Account(m.Texture, countCompressed: true);
            foreach (var m in materials)
                Account(m.NormalMap, countCompressed: false);
        }

        return new ModelMetrics(triangleCount, vertexCount, diag, texBytes, decodedBytes);
    }

    // Effective branching factor from a 2-level centroid dry run: bin
    // triangle centroids into a 4-per-axis grid and count occupied cells.
    // Flat-ish surfaces leave vertical cells empty, so this runs below the
    // theoretical 8.
    public static double EstimateEffectiveBranching(
        IReadOnlyList<Vertex3> meshVerts,
        IReadOnlyList<MeshFace> meshFaces,
        Box3 bounds,
        SubdivisionShape shape)
    {
        const int gridPerAxis = 4;
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

        // B_eff = sqrt(occupied), clamped to [2, max fanout] so a sparse
        // model can't drive the depth formula to absurd values.
        int maxFanout = isOctree ? 8 : 4;
        double bEff = Math.Sqrt(occupied.Count);
        return Math.Clamp(bEff, 2.0, (double)maxFanout);
    }

    // Closed-form maxDepth for BuildTreeConformal: max over the per-axis
    // ceil(log_B(N / leafTarget)) terms, clamped to [1, 6]. Depth 7+ risks
    // implicit-tiling subtreeLevels overflow.
    public static int OptimalDepthsClosedForm(
        ModelMetrics m,
        double bEff,
        int triLeafTarget = 25_000,
        int vertLeafTarget = 15_000,
        long textureBytesLeafTarget = 0)
    {
        if (bEff <= 1.001) bEff = 4.0; // avoid log(1)=0 → div-by-zero below
        double logB = Math.Log(bEff);

        double dTri = m.Triangles <= triLeafTarget ? 0.0
            : Math.Log((double)m.Triangles / triLeafTarget) / logB;
        double dVert = m.Vertices <= vertLeafTarget ? 0.0
            : Math.Log((double)m.Vertices / vertLeafTarget) / logB;

        // Texture-byte axis so dense-texture models pick a deeper tree,
        // bounding per-leaf texture demand. textureBytesLeafTarget=0 disables it.
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

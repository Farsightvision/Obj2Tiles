using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Obj2Tiles.Library;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;

namespace Obj2Tiles.Stages;

/// <summary>
/// Per-tile atlas pack: packs a node's per-cluster atlases into a single tight
/// atlas capped at <see cref="AppConfig.MaxAtlasSize"/>.
/// </summary>
public static class HierarchicalAtlasStage
{
    // Per-step timing accumulators (Stopwatch ticks, summed via Interlocked).
    internal static long CtorTicks;
    internal static long PrepareRepackTicks;
    internal static long FillAtlasesTicks;
    internal static long SaveAtlasesTicks;
    internal static long WriteGeometryTicks;
    internal static long Ktx2EncodeTicks;

    /// <summary>
    /// Convert a textured tile into a <see cref="MeshT_Hlod"/>, pack its atlas,
    /// and save the merged material + atlas next to <paramref name="outputObjPath"/>.
    /// </summary>
    public static (int atlasEdge, MeshT_Hlod mesh) PackAndWrite(
        ClipResultT tile,
        IReadOnlyList<Material> materials,
        AppConfig config,
        string outputObjPath,
        string tileName,
        int tileDepth,
        int maxDepth,
        bool isLeaf,
        bool evictPerMaterial)
    {
        var faceTs = new List<FaceT>(tile.Faces.Length);
        foreach (var f in tile.Faces)
        {
            faceTs.Add(new FaceT(
                f.IndexA, f.IndexB, f.IndexC,
                f.TexA, f.TexB, f.TexC,
                f.MaterialIndex));
        }
        // Per-depth atlas cap: leaves get the full MaxAtlasSize, internal
        // nodes use the smaller scheduled cap to save bytes on first-paint.
        int defaultCap;
        if (isLeaf)
        {
            defaultCap = config.MaxAtlasSize;
        }
        else if (config.AtlasMaxDepthSchedule != null
                 && config.AtlasMaxDepthSchedule.TryGetValue(tileDepth, out var scheduledCap))
        {
            defaultCap = scheduledCap;
        }
        else
        {
            defaultCap = config.MaxAtlasSizeInternal > 0
                ? config.MaxAtlasSizeInternal
                : config.MaxAtlasSize;
        }
        int cap = defaultCap;

        // AreaUniform: side = clamp(NextPow2(sqrt(A_world × D_target)),
        // AtlasMinSize, cap). The cap only ever downscales; a tile whose
        // natural pack is smaller keeps the smaller atlas.
        if (config.AtlasStrategy == AtlasStrategy.AreaUniform)
        {
            double aWorld = ConformalHierarchyStage.ComputeTileWorldArea(tile);

            // r_d = LeafDensity / 2^(maxDepth - d); D_target = r_d² (px²/m²).
            double rD = LodDensitySchedule.DensityAtDepth(config.AtlasLeafDensityPxPerM, maxDepth, tileDepth);

            double rEffective = rD;
            if (config.AtlasUseSourceDetailFloor && aWorld > 0)
            {
                var matDims = new Dictionary<int, (int W, int H)>();
                var faceR = new List<(double area, double r)>(tile.Faces.Length);
                foreach (var f in tile.Faces)
                {
                    if (!matDims.TryGetValue(f.MaterialIndex, out var wh))
                    {
                        var mat = materials[f.MaterialIndex];
                        var texPath = string.IsNullOrEmpty(mat.Texture) ? mat.NormalMap : mat.Texture;
                        if (string.IsNullOrEmpty(texPath)) { matDims[f.MaterialIndex] = (0, 0); continue; }
                        try
                        {
                            var info = TexturesCache.GetCappedDims(texPath);
                            wh = (info.Width, info.Height);
                        }
                        catch { wh = (0, 0); }
                        matDims[f.MaterialIndex] = wh;
                    }
                    if (wh.W == 0 || wh.H == 0) continue;

                    var va = tile.Vertices[f.IndexA];
                    var vb = tile.Vertices[f.IndexB];
                    var vc = tile.Vertices[f.IndexC];
                    var ta = tile.TexVertices[f.TexA];
                    var tb = tile.TexVertices[f.TexB];
                    var tc = tile.TexVertices[f.TexC];

                    double abx = vb.X - va.X, aby = vb.Y - va.Y, abz = vb.Z - va.Z;
                    double acx = vc.X - va.X, acy = vc.Y - va.Y, acz = vc.Z - va.Z;
                    double cx = aby * acz - abz * acy;
                    double cy = abz * acx - abx * acz;
                    double cz = abx * acy - aby * acx;
                    double aFace = 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
                    if (aFace <= 0) continue;

                    double uvArea = 0.5 * Math.Abs((tb.X - ta.X) * (tc.Y - ta.Y) - (tc.X - ta.X) * (tb.Y - ta.Y));
                    double dSrc = (double)wh.W * wh.H * uvArea / aFace;
                    if (dSrc <= 0) continue;
                    faceR.Add((aFace, Math.Sqrt(dSrc)));
                }

                if (faceR.Count > 0)
                {
                    // Area-weighted 75th percentile of r.
                    faceR.Sort((p, q) => p.r.CompareTo(q.r));
                    double totalArea = 0;
                    foreach (var (a, _) in faceR) totalArea += a;
                    double thresh = totalArea * 0.75;
                    double cum = 0;
                    double rTauSrc = faceR[^1].r;
                    foreach (var (a, r) in faceR)
                    {
                        cum += a;
                        if (cum >= thresh) { rTauSrc = r; break; }
                    }
                    rEffective = Math.Max(rD, Math.Min(rTauSrc, config.AtlasSourceDetailCapPxPerM));
                }
            }

            double dTarget = rEffective * rEffective;
            double idealSideDouble = Math.Sqrt(aWorld * dTarget);
            int idealSide = (int)Math.Max(1, Math.Round(idealSideDouble));
            int pow2 = Common.NextPowerOfTwo(idealSide);
            int clamped = Math.Clamp(pow2, config.AtlasMinSize, defaultCap);
            cap = clamped;
            Console.WriteLine($" [AreaUniform] {tileName} d={tileDepth}/{maxDepth} A_world={aWorld:F1}m² r_d={rD:F0}px/m r_eff={rEffective:F0}px/m ideal={idealSide}→pow2={pow2}→cap={cap}");
        }
        long t0 = Stopwatch.GetTimestamp();

        var mesh = new MeshT_Hlod(
            tile.Vertices,
            tile.TexVertices,
            faceTs,
            materials,
            saveVertexColor: false,
            saveUv: true,
            packingThreshold: config.PackingThreshold,
            textureQuality: 1.0,
            jpegQuality: 90,
            maxAtlasSize: cap)
        {
            FilePath = outputObjPath,
            Name = tileName,
            AtlasUnsharpAmount = config.AtlasUnsharpAmount,
            AtlasCapCeiling = config.MaxAtlasSize,
        };
        long t1 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref CtorTicks, t1 - t0);

        mesh.PrepareRepackTextures(removeUnused: true);
        long t2 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref PrepareRepackTicks, t2 - t1);

        // Fill at the natural size: PackedRect coords were assigned there, so
        // shrinking before fill would put cluster copies out of bounds.
        // Evicting each source after fill bounds the decoded-texture cache,
        // which would otherwise accumulate every source bitmap and OOM.
        foreach (var mat in materials)
        {
            if (string.IsNullOrEmpty(mat.Texture) && string.IsNullOrEmpty(mat.NormalMap))
                continue;
            mesh.FillAtlases(mat);
            // EvictTexture disposes the decoded image, so only evict when serial;
            // doing it while parallel siblings share the cache is a use-after-dispose.
            if (evictPerMaterial)
            {
                TexturesCache.EvictTexture(mat.Texture);
                TexturesCache.EvictTexture(mat.NormalMap);
            }
        }
        long t3 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref FillAtlasesTicks, t3 - t2);
        Console.WriteLine($" [filltime] {mesh.FilePath} fillMs={(t3 - t2) * 1000.0 / Stopwatch.Frequency:F1} prepMs={(t2 - t1) * 1000.0 / Stopwatch.Frequency:F1}");

        mesh.SaveAtlasesAndUpdateMaterial();
        long t4 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref SaveAtlasesTicks, t4 - t3);

        if (config.Ktx2Hierarchical
            && string.Equals(config.Ktx2Encoder, "basisu", StringComparison.OrdinalIgnoreCase))
        {
            long tk0 = Stopwatch.GetTimestamp();
            EncodeAtlasesToKtx2Basisu(mesh, config);
            Interlocked.Add(ref Ktx2EncodeTicks, Stopwatch.GetTimestamp() - tk0);
        }

        return (mesh.AtlasEdgeLength, mesh);
    }

    /// <summary>
    /// Encode the written atlas image(s) to .ktx2 via the standalone basisu
    /// binary and repoint the merged material, before the caller writes the MTL/OBJ.
    /// </summary>
    private static void EncodeAtlasesToKtx2Basisu(MeshT_Hlod mesh, AppConfig config)
    {
        if (mesh.Materials.Count == 0) return;
        string folder = Path.GetDirectoryName(mesh.FilePath) ?? string.Empty;

        // Map the 1-10 KTX2 quality knob onto basisu's ETC1S -q (1-255).
        byte basisuQuality = (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(config.Ktx2Quality, 1, 10) / 10.0 * 255.0), 1, 255);
        const byte basisuCompLevel = 0;

        foreach (var material in mesh.Materials)
        {
            if (!string.IsNullOrEmpty(material.Texture)
                && !material.Texture.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase))
            {
                var ktxTexture = Path.ChangeExtension(material.Texture, ".ktx2");
                var srcPath = Path.Combine(folder, material.Texture);
                var dstPath = Path.Combine(folder, ktxTexture);
                BasisuConverter.ConvertPngToKtx2(basisuQuality, basisuCompLevel, srcPath, dstPath);
                material.Texture = ktxTexture;
            }

            if (!string.IsNullOrEmpty(material.NormalMap)
                && !material.NormalMap.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase))
            {
                var ktxNormal = Path.ChangeExtension(material.NormalMap, ".ktx2");
                var srcPath = Path.Combine(folder, material.NormalMap);
                var dstPath = Path.Combine(folder, ktxNormal);
                BasisuConverter.ConvertPngToKtx2(basisuQuality, basisuCompLevel, srcPath, dstPath);
                material.NormalMap = ktxNormal;
            }
        }
    }
}

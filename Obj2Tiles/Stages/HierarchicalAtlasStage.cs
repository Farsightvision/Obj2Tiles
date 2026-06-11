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
/// Per-tile atlas pack: takes a node's textured ClipResultT and packs its
/// per-cluster atlases down to a single tight atlas, capped at
/// <see cref="AppConfig.MaxAtlasSize"/>. Every tile's atlas contains only
/// its own textures, sized tight; oversizing causes browser memory/perf
/// regressions.
/// </summary>
public static class HierarchicalAtlasStage
{
    // Accumulate per-step timings across all PackAndWrite calls so we can
    // identify bottlenecks. Reset by callers before WriteAllGlbs; dumped
    // after. Long is ticks (Stopwatch.GetTimestamp), summed via Interlocked.
    internal static long CtorTicks;
    internal static long PrepareRepackTicks;
    internal static long FillAtlasesTicks;
    internal static long SaveAtlasesTicks;
    internal static long WriteGeometryTicks;
    // basisu KTX2/ETC1S encode (in-Phase-1, basisu mode). Untracked until now; isolated so the
    // profile can attribute Phase-1 wall to encode vs decode/fill (the two optimization targets).
    internal static long Ktx2EncodeTicks;

    /// <summary>
    /// Convert a textured tile's <see cref="ClipResultT"/> into a
    /// <see cref="MeshT_Hlod"/>, run the three-phase atlas pack, and return the
    /// <see cref="MeshT_Hlod"/> with one merged material + one PNG/JPEG atlas
    /// already saved next to <paramref name="outputObjPath"/>.
    /// </summary>
    /// <param name="tile">The node's textured mesh.</param>
    /// <param name="materials">All materials referenced by this tile (must
    /// include every <c>materialIndex</c> used by any face). Indexes match
    /// the input <c>MeshFace.MaterialIndex</c>.</param>
    /// <param name="config">Source of <see cref="AppConfig.MaxAtlasSize"/>.</param>
    /// <param name="outputObjPath">Where the OBJ + MTL + atlas PNG/JPEG are written.</param>
    /// <param name="tileName">A short name used as the atlas filename prefix.</param>
    /// <returns>The capped atlas edge length and the resulting MeshT_Hlod (already disposed of atlas Image internals).</returns>
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
        // Build a MeshT_Hlod from the ClipResultT. We pass the FULL material list
        // so the cluster pipeline can resolve material.Texture paths even if
        // this tile only references a subset (PrepareRepackTextures groups
        // faces by material index, skips empty groups). saveUv=true is what
        // makes the cluster/atlas pipeline run.
        var faceTs = new List<FaceT>(tile.Faces.Length);
        foreach (var f in tile.Faces)
        {
            faceTs.Add(new FaceT(
                f.IndexA, f.IndexB, f.IndexC,
                f.TexA, f.TexB, f.TexC,
                f.MaterialIndex));
        }
        // Atlas cap selection (passed into MeshT_Hlod as maxAtlasSize so
        // SaveAtlasesAndUpdateMaterial enforces it when downscaling).
        //
        // Per-depth schedule via AtlasMaxDepthSchedule: shallow LODs use
        // smaller caps (e.g. LOD-0 = 512², ..., LOD-4 = 4096²) to save
        // download bytes and GPU memory on first-paint. Leaves always get
        // config.MaxAtlasSize as their cap (the per-depth schedule applies
        // ONLY to internal nodes); the actual leaf side is picked per-tile
        // by the AreaUniform sizing below (sqrt(A_world × D_target)).
        //
        // Cluster-count-aware override: if a shallow LOD ends up with very
        // many UV clusters (>30k faces at depth <= 1), the chosen cap may
        // trigger the gutter-floor safety throw. Bump cap up one rung in
        // the schedule so the bin-pack can converge.
        //
        // Adaptive subdivision marks isLeaf per-tile (PruneAdaptive can
        // collapse interior nodes to leaves at depths < maxDepth);
        // HierarchicalNode.IsLeaf is the source-of-truth and is threaded
        // through PrepareTileForGlb → PackAndWrite.
        int defaultCap;
        if (isLeaf)
        {
            defaultCap = config.MaxAtlasSize;
        }
        else if (config.AtlasMaxDepthSchedule != null
                 && config.AtlasMaxDepthSchedule.TryGetValue(tileDepth, out var scheduledCap))
        {
            defaultCap = scheduledCap;
            // Cluster-count aware override: at very-shallow depth with very
            // many faces, the scheduled cap may trigger the gutter-floor
            // safety throw. Bump cap up one rung so the bin-pack converges.
            if (tileDepth <= 1 && faceTs.Count > 30000 && defaultCap < 2048)
            {
                int newCap = Math.Min(2048, defaultCap * 2);
                Console.WriteLine($" -> cluster-aware cap bump (depth={tileDepth} faces={faceTs.Count}): {defaultCap} → {newCap}");
                defaultCap = newCap;
            }
        }
        else
        {
            // Legacy fallback (when caller has cleared the schedule).
            defaultCap = config.MaxAtlasSizeInternal > 0
                ? config.MaxAtlasSizeInternal
                : config.MaxAtlasSize;
        }
        int cap = defaultCap;

        // AreaUniform strategy: size the atlas from world-space surface area
        // × LOD density target. side = clamp(NextPow2(sqrt(A_world × D_target)),
        // AtlasMinSize, MaxAtlasSize). The optional source-detail floor
        // raises D_target up to the area-weighted Q75 of per-face source
        // density when the tile carries finer captured detail than the
        // LOD baseline. No force-grow above natural — the cap-only path
        // means side = min(side, Nat_side) by construction (natural pack
        // produces ≤ side, downscale only if natural exceeds side).
        if (config.AtlasStrategy == AtlasStrategy.AreaUniform)
        {
            // 1. World-space surface area of this tile in m² (single definition in
            //    ConformalHierarchyStage.ComputeTileWorldArea — identical face iteration + expression).
            double aWorld = ConformalHierarchyStage.ComputeTileWorldArea(tile);

            // 2. LOD density schedule: r_d = LeafDensity / 2^(maxDepth - d).
            //    D_target = r_d² (px²/m²).
            double rD = LodDensitySchedule.DensityAtDepth(config.AtlasLeafDensityPxPerM, maxDepth, tileDepth);

            // 3. Optional area-weighted Q75 source-detail floor.
            double rEffective = rD;
            if (config.AtlasUseSourceDetailFloor && aWorld > 0)
            {
                // Cache (W, H) per material to avoid hammering TexturesCache.
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
                    // texels per m² = W * H * uvArea / aFace
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
                    double rTauSrc = faceR[^1].r;  // fallback to max
                    foreach (var (a, r) in faceR)
                    {
                        cum += a;
                        if (cum >= thresh) { rTauSrc = r; break; }
                    }
                    // r_effective = max(r_d, min(r_src, cap))  — never below LOD baseline, never above cap.
                    rEffective = Math.Max(rD, Math.Min(rTauSrc, config.AtlasSourceDetailCapPxPerM));
                }
            }

            double dTarget = rEffective * rEffective;
            double idealSideDouble = Math.Sqrt(aWorld * dTarget);
            int idealSide = (int)Math.Max(1, Math.Round(idealSideDouble));
            int pow2 = Common.NextPowerOfTwo(idealSide);
            // Clamp to the per-depth cap (not the flat config.MaxAtlasSize)
            // so leaves keep their full budget while interior nodes stay
            // byte-thrifty.
            int clamped = Math.Clamp(pow2, config.AtlasMinSize, defaultCap);
            cap = clamped;
            // No force-grow — preserves Nat_side ceiling. If natural < cap,
            // packer settles at natural (smaller atlas). If natural > cap,
            // downscale to cap.
            Console.WriteLine($" [AreaUniform] {tileName} d={tileDepth}/{maxDepth} A_world={aWorld:F1}m² r_d={rD:F0}px/m r_eff={rEffective:F0}px/m ideal={idealSide}→pow2={pow2}→cap={cap}");
        }
        // else AtlasStrategy.Natural: cap = config.MaxAtlasSize already (set above).
        long t0 = Stopwatch.GetTimestamp();

        var mesh = new MeshT_Hlod(
            tile.Vertices,
            tile.TexVertices,
            faceTs,
            materials,
            saveVertexColor: false,
            saveUv: true,
            packingThreshold: config.PackingThreshold,
            // textureQuality=1: no separate quality-driven downscale. The
            // cap below is the only downscale knob — keeps behavior easy
            // to reason about.
            textureQuality: 1.0,
            jpegQuality: 90,
            // The CAP is enforced inside SaveAtlasesAndUpdateMaterial via
            // Common.PreviousPowerOfTwo(width) → Min(_maxAtlasSize). Pack
            // and fill at natural size; downscale once at save time.
            maxAtlasSize: cap)
        {
            FilePath = outputObjPath,
            Name = tileName,
            // Per-tile unsharp strength threaded from config so
            // SaveAtlasesAndUpdateMaterial can sharpen the base atlas
            // before JPEG encode.
            AtlasUnsharpAmount = config.AtlasUnsharpAmount,
        };
        long t1 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref CtorTicks, t1 - t0);

        // Phase 1: cluster + bin-pack at the natural (unbounded) size.
        mesh.PrepareRepackTextures(removeUnused: true);
        long t2 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref PrepareRepackTicks, t2 - t1);

        // Phase 2: fill the atlas image at natural size, one source
        // material at a time. Filling at the natural size is required —
        // PackedRect coordinates were assigned at the natural size, so
        // we can't shrink the atlas before fill or the cluster copies
        // would land out of bounds.
        //
        // TexturesCache is a static dictionary of decoded Rgba32 images.
        // Without per-material eviction it accumulates every source PNG of
        // a large fixture (hundreds of MB decoded RGBA32 each) and OOMs.
        // After FillAtlases copies the source's pixels into the per-tile
        // atlas buffer, the source bitmap is no longer needed, so evict
        // immediately. The next tile that references this material will
        // reload it from disk — bounded I/O cost in exchange for bounded
        // RAM. Per-material eviction in serial-Phase-1 mode bounds RAM at
        // ~1 source texture per tile. Parallel-Phase-1 mode (small inputs)
        // skips eviction here; WriteAllGlbs.Clear()s once after the loop.
        foreach (var mat in materials)
        {
            if (string.IsNullOrEmpty(mat.Texture) && string.IsNullOrEmpty(mat.NormalMap))
                continue;
            mesh.FillAtlases(mat);
            // Evict the just-packed source ONLY when execution is effectively serial
            // (evictPerMaterial). EvictTexture disposes the decoded image, so doing it
            // while sibling tiles share the static cache in parallel is a use-after-
            // dispose race — the caller passes true only for the serial / mdop==1 paths.
            if (evictPerMaterial)
            {
                TexturesCache.EvictTexture(mat.Texture);
                TexturesCache.EvictTexture(mat.NormalMap);
            }
        }
        long t3 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref FillAtlasesTicks, t3 - t2);
        Console.WriteLine($" [filltime] {mesh.FilePath} fillMs={(t3 - t2) * 1000.0 / Stopwatch.Frequency:F1} prepMs={(t2 - t1) * 1000.0 / Stopwatch.Frequency:F1}");

        // Phase 3: write the atlas (downscaled to cap if needed) + update UVs.
        mesh.SaveAtlasesAndUpdateMaterial();
        long t4 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref SaveAtlasesTicks, t4 - t3);

        // Phase 3b: in-binary basisu KTX2 (gltfpack-FREE path). When KTX2 is
        // requested AND the encoder is "basisu", encode the just-written atlas
        // image(s) to .ktx2 with the standalone basisu binary and repoint the
        // merged material so the MTL/OBJ written by the caller reference the
        // .ktx2. Gltf2GlbConverter then embeds it via KHR_texture_basisu — no
        // gltfpack -tc post-process required. Mirrors CompressionStage (the
        // flat-pipeline KTX2 path), so the prod image (basisu, no gltfpack)
        // produces KTX2-textured HLOD tiles with only an OBJ2TILES_VERSION bump.
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
    /// gltfpack-FREE per-tile KTX2 step. After
    /// <see cref="MeshT_Hlod.SaveAtlasesAndUpdateMaterial"/> has written the
    /// merged atlas image(s) and pointed the (single) merged material at them
    /// by filename, encode each written image to <c>.ktx2</c> with the
    /// standalone <c>basisu</c> binary and repoint
    /// <see cref="Material.Texture"/> / <see cref="Material.NormalMap"/> at the
    /// <c>.ktx2</c>. The caller (<c>PrepareTileForGlb</c>) writes the MTL/OBJ
    /// AFTER this, so the OBJ→glTF→GLB conversion picks up the <c>.ktx2</c> and
    /// <c>Gltf2GlbConverter</c> embeds it via KHR_texture_basisu. Identical in
    /// spirit to <c>StagesFacade.Compress</c> (the flat-pipeline KTX2 path).
    /// basisu accepts both .jpg (diffuse atlas) and .png (normal atlas) inputs.
    /// </summary>
    private static void EncodeAtlasesToKtx2Basisu(MeshT_Hlod mesh, AppConfig config)
    {
        if (mesh.Materials.Count == 0) return;
        string folder = Path.GetDirectoryName(mesh.FilePath) ?? string.Empty;

        // Map gltfpack's 1-10 KTX2 quality knob onto basisu's ETC1S -q (1-255).
        // basisu's own default is 128; Ktx2Quality default 8 → ~204 (closer to
        // gltfpack's stronger default). comp_level 0 = fastest RDO pass (the
        // per-tile atlases are small, so the extra comp_level passes buy little).
        byte basisuQuality = (byte)Math.Clamp(
            (int)Math.Round(Math.Clamp(config.Ktx2Quality, 1, 10) / 10.0 * 255.0), 1, 255);
        const byte basisuCompLevel = 0;

        // PackAndWrite merges every cluster into a single material, so in
        // practice this loops once. Loop the full list defensively.
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

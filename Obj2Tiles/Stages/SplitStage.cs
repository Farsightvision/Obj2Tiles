using System.Diagnostics;
using Obj2Tiles.Library;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static Dictionary<LodConfig, List<IMesh>> Split(
        string[] sourceFiles,
        string destFolder,
        int maxVerticesPerTile,
        Box3 bounds,
        double packingThreshold,
        LodConfig[] lodConfigs,
        int threadsCount,
        int maxTotalAtlasArea)
    {
        var results = new Dictionary<LodConfig, List<IMesh>>();
        var lod0File = sourceFiles[0];
        var mesh = MeshUtils.LoadMesh(lod0File, false, true, packingThreshold, lodConfigs[0].Quality, out _, lodConfigs[0].JpegQuality, lodConfigs[0].MaxAtlasSize);
        var tileSize = MeshUtils.CalculateOptimalTileSize(mesh, maxVerticesPerTile);

        for (var index = 0; index < sourceFiles.Length; index++)
        {
            var lod = lodConfigs[index];
            var file = sourceFiles[index];
            var dest = Path.Combine(destFolder, "LOD-" + index);

            var meshes = Split(file, dest, tileSize, packingThreshold, lod, bounds, SplitPointStrategy.VertexBaricenter, maxTotalAtlasArea);
            results.Add(lod, meshes);
        }

        TexturesCache.Clear();

        return results;
    }

    public static List<IMesh> Split(string sourcePath, string destPath, double tileSize,
        double packingThreshold, LodConfig lod, Box3? bounds, SplitPointStrategy splitPointStrategy, int maxTotalAtlasArea)
    {
        var sw = new Stopwatch();

        Directory.CreateDirectory(destPath);

        Console.WriteLine($" -> Loading OBJ file \"{sourcePath}\"");

        sw.Start();
        var sourceMesh = MeshUtils.LoadMesh(sourcePath, lod.SaveVertexColor, lod.SaveUv, packingThreshold, lod.Quality, out _, lod.JpegQuality, lod.MaxAtlasSize);

        Console.WriteLine(
            $" ?> Loaded {sourceMesh.VertexCount} vertices, {sourceMesh.FacesCount} faces in {sw.ElapsedMilliseconds}ms");

        Console.WriteLine($" -> Splitting by TileSize {tileSize}");

        sw.Restart();

        var meshes =  MeshUtils.SplitByTileSizeXYZ(sourceMesh, bounds, tileSize);

        sw.Stop();

        Console.WriteLine(
            $" ?> Done {meshes.Count} edge splits in {sw.ElapsedMilliseconds}ms ({(double)meshes.Count / sw.ElapsedMilliseconds:F2} split/ms)");

        sw.Restart();

        if (sourceMesh is MeshT sourceMeshT)
        {
            Console.WriteLine(" -> Prepare Repack Textures");

            var meshTs = meshes.Cast<MeshT>().ToList();

            for (var i = 0; i < meshTs.Count; i++)
            {
                var meshT = meshTs[i];
                meshT.FilePath = Path.Combine(destPath, $"{meshT.Name}.obj");
            }

            // Each mesh independently computes clusters and atlas sizing
            Parallel.ForEach(meshTs, meshT => meshT.PrepareRepackTextures());

            Console.WriteLine($" ?> Prepared {meshTs.Count} atlas layouts");

            var currentBatch = new List<MeshT>();
            long currentSize = 0;

            for (var i = 0; i < meshTs.Count; i++)
            {
                var meshT = meshTs[i];
                var atlasSize = meshT.AtlasEdgeLength * meshT.AtlasEdgeLength;

                if (currentSize + atlasSize > maxTotalAtlasArea && currentBatch.Count > 0)
                {
                    ProcessBatch(currentBatch, sourceMeshT.Materials);

                    currentBatch.Clear();
                    currentSize = 0;
                }

                currentBatch.Add(meshT);
                currentSize += atlasSize;
            }

            if (currentBatch.Count > 0)
            {
                ProcessBatch(currentBatch, sourceMeshT.Materials);
            }

            Console.WriteLine(" -> Write Geometry");

            // WriteGeometry in parallel - each mesh writes its own independent OBJ file
            Parallel.ForEach(meshTs, meshT => meshT.WriteGeometry());
        }
        else
        {
            Console.WriteLine(" -> Writing tiles");

            // Write OBJ files in parallel
            Parallel.ForEach(meshes, mesh =>
            {
                var path = Path.Combine(destPath, $"{mesh.Name}.obj");
                mesh.WriteObj(path);
            });
        }

        Console.WriteLine($" ?> {meshes.Count} tiles written in {sw.ElapsedMilliseconds}ms");
        return meshes;
    }

    private static void ProcessBatch(List<MeshT> meshTs, IReadOnlyList<Material> materials)
    {
        Console.WriteLine($"Fill and save Atlases count {meshTs.Count}");

        for (var i = 0; i < materials.Count; i++)
        {
            var material = materials[i];

            // FillAtlases in parallel per mesh - each mesh writes to its own private atlas,
            // source textures from cache are read-only (thread-safe ConcurrentDictionary reads)
            Parallel.ForEach(meshTs, meshT => meshT.FillAtlases(material));

            TexturesCache.EvictTexture(material.Texture);
            TexturesCache.EvictTexture(material.NormalMap);
        }

        Parallel.ForEach(meshTs, meshT => meshT.SaveAtlasesAndUpdateMaterial());
    }
}

public enum SplitPointStrategy
{
    AbsoluteCenter,
    VertexBaricenter
}
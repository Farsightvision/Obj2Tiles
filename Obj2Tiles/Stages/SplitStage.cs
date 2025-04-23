using System.Collections.Concurrent;
using System.Diagnostics;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static async Task<List<IMesh>> Split(
        string[] sourceFiles,
        string destFolder,
        int divisions,
        Box3 bounds,
        double packingThreshold,
        LodConfig[] lods,
        bool keepOriginalTextures,
        byte ktxQuality,
        byte ktxCompressionLevel)
    {
        var tasks = new List<Task<IMesh[]>>();
        var lod0File = sourceFiles[0];
        var mesh = MeshUtils.LoadMesh(lod0File, false, true, packingThreshold, lods[0].Quality, out _);
        var tileSize = await MeshUtils.CalculateOptimalTileSize(mesh, divisions);

        for (var index = 0; index < sourceFiles.Length; index++)
        {
            var lod = lods[index];
            var file = sourceFiles[index];
            var dest = Path.Combine(destFolder, "LOD-" + index);

            var splitTask = Split(file, dest, tileSize, packingThreshold, lod, bounds,
                keepOriginalTextures, SplitPointStrategy.VertexBaricenter);

            tasks.Add(splitTask);
        }

        await Task.WhenAll(tasks);
        return tasks.SelectMany(task => task.Result).ToList();
    }

    public static async Task<IMesh[]> Split(string sourcePath, string destPath, double tileSize,
        double packingThreshold, LodConfig lod, Box3? bounds,
        bool keepOriginalTextures, SplitPointStrategy splitPointStrategy)
    {
        var sw = new Stopwatch();

        Directory.CreateDirectory(destPath);

        Console.WriteLine($" -> Loading OBJ file \"{sourcePath}\"");

        sw.Start();
        var mesh = MeshUtils.LoadMesh(sourcePath, lod.SaveVertexColor, lod.SaveUv, packingThreshold, lod.Quality, out _);

        Console.WriteLine(
            $" ?> Loaded {mesh.VertexCount} vertices, {mesh.FacesCount} faces in {sw.ElapsedMilliseconds}ms");

        Console.WriteLine($" -> Splitting by TileSize {tileSize}");

        var meshes = new ConcurrentBag<IMesh>();

        sw.Restart();

        var count = await MeshUtils.SplitByTileSizeXY(mesh, bounds, tileSize, meshes);

        sw.Stop();

        Console.WriteLine(
            $" ?> Done {count} edge splits in {sw.ElapsedMilliseconds}ms ({(double)count / sw.ElapsedMilliseconds:F2} split/ms)");

        Console.WriteLine(" -> Writing tiles");

        sw.Restart();

        var semaphore = new SemaphoreSlim(8);
        var tasks = meshes.Select(async m =>
        {
            await semaphore.WaitAsync();

            try
            {
                if (m is MeshT t)
                    t.KeepOriginalTextures = keepOriginalTextures;

                var path = Path.Combine(destPath, $"{m.Name}.obj");
                await Task.Run(() => m.WriteObj(path));
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        Console.WriteLine($" ?> {meshes.Count} tiles written in {sw.ElapsedMilliseconds}ms");
        return meshes.ToArray();
    }
}

public enum SplitPointStrategy
{
    AbsoluteCenter,
    VertexBaricenter
}
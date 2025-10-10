using System.Collections.Concurrent;
using System.Diagnostics;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static async Task<Dictionary<LodConfig, IMesh[]>> Split(
        string[] sourceFiles,
        string destFolder,
        int divisions,
        Box3 bounds,
        double packingThreshold,
        LodConfig[] lodConfigs,
        int threadsCount)
    {
        var results = new Dictionary<LodConfig, IMesh[]>();
        var lod0File = sourceFiles[0];
        var mesh = MeshUtils.LoadMesh(lod0File, false, true, packingThreshold, lodConfigs[0].Quality, out _);
        var tileSize = MeshUtils.CalculateOptimalTileSize(mesh, divisions);
        var semaphore = new SemaphoreSlim(threadsCount);

        for (var index = 0; index < sourceFiles.Length; index++)
        {
            var lod = lodConfigs[index];
            var file = sourceFiles[index];
            var dest = Path.Combine(destFolder, "LOD-" + index);

            var result = await Split(file, dest, tileSize, packingThreshold, lod, bounds, semaphore, SplitPointStrategy.VertexBaricenter);
            results.Add(lod, result);
        }

        return results;
    }

    public static async Task<IMesh[]> Split(string sourcePath, string destPath, double tileSize,
        double packingThreshold, LodConfig lod, Box3? bounds, SemaphoreSlim semaphore, SplitPointStrategy splitPointStrategy)
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

        var count = await MeshUtils.SplitByTileSizeXYZ(mesh, bounds, tileSize, meshes);

        sw.Stop();

        Console.WriteLine(
            $" ?> Done {count} edge splits in {sw.ElapsedMilliseconds}ms ({(double)count / sw.ElapsedMilliseconds:F2} split/ms)");

        Console.WriteLine(" -> Writing tiles");

        sw.Restart();

        foreach (var m in meshes)
        {
            await semaphore.WaitAsync();

            try
            {
                var path = Path.Combine(destPath, $"{m.Name}.obj");
                await Task.Run(() => m.WriteObj(path));
            }
            finally
            {
                semaphore.Release();
            }
        }

        Console.WriteLine($" ?> {meshes.Count} tiles written in {sw.ElapsedMilliseconds}ms");
        return meshes.ToArray();
    }
}

public enum SplitPointStrategy
{
    AbsoluteCenter,
    VertexBaricenter
}
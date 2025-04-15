using System.Collections.Concurrent;
using System.Diagnostics;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static async Task<Dictionary<string, Box3>[]> Split(string[] sourceFiles, string destFolder, int divisions,
        Box3 bounds, double packingThreshold, bool keepOriginalTextures = false)
    {
        var tasks = new List<Task<Dictionary<string, Box3>>>();
        var lod0File = sourceFiles[0];
        var mesh = MeshUtils.LoadMesh(lod0File, packingThreshold, out _);
        var tileSize = await MeshUtils.CalculateOptimalTileSize(mesh, divisions);

        for (var index = 0; index < sourceFiles.Length; index++)
        {
            var file = sourceFiles[index];
            var dest = Path.Combine(destFolder, "LOD-" + index);
            
            // We compress textures except the first one (the original one)
            var textureStrategy = keepOriginalTextures ? TexturesStrategy.KeepOriginal :
                index == 0 ? TexturesStrategy.Repack : TexturesStrategy.RepackCompressed;

            var splitTask = Split(file, dest, tileSize, packingThreshold, bounds, textureStrategy);

            tasks.Add(splitTask);
        }

        await Task.WhenAll(tasks);

        return tasks.Select(task => task.Result).ToArray();
    }

    public static async Task<Dictionary<string, Box3>> Split(string sourcePath, string destPath, double tileSize,
        double packingThreshold, Box3? bounds = null,
        TexturesStrategy textureStrategy = TexturesStrategy.Repack,
        SplitPointStrategy splitPointStrategy = SplitPointStrategy.VertexBaricenter)
    {
        var sw = new Stopwatch();
        var tilesBounds = new Dictionary<string, Box3>();

        Directory.CreateDirectory(destPath);
        
        Console.WriteLine($" -> Loading OBJ file \"{sourcePath}\"");

        sw.Start();
        var mesh = MeshUtils.LoadMesh(sourcePath, packingThreshold,  out var deps);

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

        var ms = meshes.ToArray();
        for (var index = 0; index < ms.Length; index++)
        {
            var m = ms[index];

            if (m is MeshT t)
                t.TexturesStrategy = textureStrategy;

            m.WriteObj(Path.Combine(destPath, $"{m.Name}.obj"));

            tilesBounds.Add(m.Name, m.Bounds);
        }

        Console.WriteLine($" ?> {meshes.Count} tiles written in {sw.ElapsedMilliseconds}ms");

        return tilesBounds;
    }
}

public enum SplitPointStrategy
{
    AbsoluteCenter,
    VertexBaricenter
}
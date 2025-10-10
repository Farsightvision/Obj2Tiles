using System.Diagnostics;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static Dictionary<LodConfig, List<IMesh>> Split(
        string[] sourceFiles,
        string destFolder,
        int divisions,
        Box3 bounds,
        double packingThreshold,
        LodConfig[] lodConfigs,
        int threadsCount)
    {
        var results = new Dictionary<LodConfig, List<IMesh>>();
        var lod0File = sourceFiles[0];
        var mesh = MeshUtils.LoadMesh(lod0File, false, true, packingThreshold, lodConfigs[0].Quality, out _);
        var tileSize = MeshUtils.CalculateOptimalTileSize(mesh, divisions);

        for (var index = 0; index < sourceFiles.Length; index++)
        {
            var lod = lodConfigs[index];
            var file = sourceFiles[index];
            var dest = Path.Combine(destFolder, "LOD-" + index);

            var meshes = Split(file, dest, tileSize, packingThreshold, lod, bounds, SplitPointStrategy.VertexBaricenter);
            results.Add(lod, meshes);
        }

        return results;
    }

    public static List<IMesh> Split(string sourcePath, string destPath, double tileSize,
        double packingThreshold, LodConfig lod, Box3? bounds, SplitPointStrategy splitPointStrategy)
    {
        var sw = new Stopwatch();

        Directory.CreateDirectory(destPath);

        Console.WriteLine($" -> Loading OBJ file \"{sourcePath}\"");

        sw.Start();
        var sourceMesh = MeshUtils.LoadMesh(sourcePath, lod.SaveVertexColor, lod.SaveUv, packingThreshold, lod.Quality, out _);

        Console.WriteLine(
            $" ?> Loaded {sourceMesh.VertexCount} vertices, {sourceMesh.FacesCount} faces in {sw.ElapsedMilliseconds}ms");

        Console.WriteLine($" -> Splitting by TileSize {tileSize}");

        sw.Restart();

        var meshes =  MeshUtils.SplitByTileSizeXYZ(sourceMesh, bounds, tileSize);

        sw.Stop();

        Console.WriteLine(
            $" ?> Done {meshes.Count} edge splits in {sw.ElapsedMilliseconds}ms ({(double)meshes.Count / sw.ElapsedMilliseconds:F2} split/ms)");

        Console.WriteLine(" -> Writing tiles");

        sw.Restart();

        foreach (var mesh in meshes)
        {
            var path = Path.Combine(destPath, $"{mesh.Name}.obj");
            mesh.WriteObj(path);
        }

        Console.WriteLine($" ?> {meshes.Count} tiles written in {sw.ElapsedMilliseconds}ms");
        return meshes;
    }
}

public enum SplitPointStrategy
{
    AbsoluteCenter,
    VertexBaricenter
}
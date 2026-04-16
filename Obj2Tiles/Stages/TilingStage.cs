using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages.Model;
using Obj2Tiles.Tiles;
using SilentWave;
using SilentWave.Obj2Gltf;

namespace Obj2Tiles.Stages;

public static partial class StagesFacade
{
    public static void Tile(string destPath, int lods, double baseError, Dictionary<string, Box3>[] boundsMapper,
        GpsCoords? coords = null)
    {
        Console.WriteLine(" -> Generating tileset.json");

        if (coords == null)
        {
            Console.WriteLine(" ?> Using default coordinates");
            coords = DefaultGpsCoords;
        }

        var masterDescriptors = boundsMapper[0].Keys;

        // Accumulate global bounds
        var maxX = double.MinValue;
        var minX = double.MaxValue;
        var maxY = double.MinValue;
        var minY = double.MaxValue;
        var maxZ = double.MinValue;
        var minZ = double.MaxValue;

        foreach (var descriptor in masterDescriptors)
        {
            for (var lod = lods - 1; lod >= 0; lod--)
            {
                if (!boundsMapper[lod].ContainsKey(descriptor)) continue;

                var box3 = boundsMapper[lod][descriptor];
                if (box3.Min.X < minX) minX = box3.Min.X;
                if (box3.Max.X > maxX) maxX = box3.Max.X;
                if (box3.Min.Y < minY) minY = box3.Min.Y;
                if (box3.Max.Y > maxY) maxY = box3.Max.Y;
                if (box3.Min.Z < minZ) minZ = box3.Min.Z;
                if (box3.Max.Z > maxZ) maxZ = box3.Max.Z;
            }
        }

        var globalBox = new Box3(minX, minY, minZ, maxX, maxY, maxZ);

        // Scale root error to model size so LOD transitions work at any scale
        var modelDiagonal = Math.Sqrt(globalBox.Width * globalBox.Width + globalBox.Height * globalBox.Height + globalBox.Depth * globalBox.Depth);
        var rootError = Math.Max(baseError, modelDiagonal * 10);

        Console.WriteLine($" ?> Model diagonal: {modelDiagonal:F0}m, rootError: {rootError:F0}");

        // Build tileset hierarchy
        var tileset = new Tileset
        {
            Asset = new Asset { Version = "1.0" },
            GeometricError = rootError,
            Root = new TileElement
            {
                GeometricError = rootError,
                Refine = "REPLACE",
                Transform = coords.ToEcefTransform(),
                BoundingVolume = globalBox.ToBoundingVolume(),
                Children = new List<TileElement>()
            }
        };

        foreach (var descriptor in masterDescriptors)
        {
            var currentTileElement = tileset.Root;

            for (var lod = lods - 1; lod >= 0; lod--)
            {
                if (!boundsMapper[lod].ContainsKey(descriptor)) continue;

                var box3 = boundsMapper[lod][descriptor];

                var tile = new TileElement
                {
                    GeometricError = lod == 0 ? 0 : CalculateTileGeometricError(box3, lod, lods),
                    Refine = "REPLACE",
                    Children = new List<TileElement>(),
                    Content = new Content
                    {
                        Uri = $"LOD-{lod}/{Path.GetFileNameWithoutExtension(descriptor)}.glb"
                    },
                    BoundingVolume = box3.ToBoundingVolume()
                };

                currentTileElement.Children.Add(tile);
                currentTileElement = tile;
            }
        }

        File.WriteAllText(Path.Combine(destPath, "tileset.json"),
            JsonConvert.SerializeObject(tileset, Formatting.Indented));
    }

    /// <summary>
    /// Per-tile geometric error based on the tile's AABB half-diagonal.
    /// Ensures error is proportional to the tile's spatial size — small tiles
    /// only refine when the camera is close, large tiles refine sooner.
    /// </summary>
    private static double CalculateTileGeometricError(Box3 box, int lod, int totalLods)
    {
        var dx = box.Width;
        var dy = box.Height;
        var dz = box.Depth;
        var halfDiagonal = 0.5 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var fraction = (double)lod / (totalLods - 1);
        return halfDiagonal * fraction * fraction;
    }

    private static void ConvertAllB3dm(string sourcePath, string destPath, int lods)
    {
        var filesToConvert = new List<Tuple<string, string>>();

        for (var lod = 0; lod < lods; lod++)
        {
            var files = Directory.GetFiles(Path.Combine(sourcePath, "LOD-" + lod), "*.obj");

            foreach (var file in files)
            {
                var outputFolder = Path.Combine(destPath, "LOD-" + lod);
                Directory.CreateDirectory(outputFolder);

                var outputFile = Path.Combine(outputFolder, Path.ChangeExtension(Path.GetFileName(file), ".b3dm"));
                filesToConvert.Add(new Tuple<string, string>(file, outputFile));
            }
        }

        Parallel.ForEach(filesToConvert, (file) =>
        {
            Console.WriteLine($" -> Converting to b3dm '{file.Item1}'");
            Utils.ConvertB3dm(file.Item1, file.Item2);
        });
    }

    private static readonly GpsCoords DefaultGpsCoords = new()
    {
        Altitude = 0,
        Latitude = 45.46424200394995,
        Longitude = 9.190277486808588
    };

}

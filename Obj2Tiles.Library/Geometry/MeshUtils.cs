using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Obj2Tiles.Library.Materials;

namespace Obj2Tiles.Library.Geometry;

public class MeshUtils
{
    public static IMesh LoadMesh(string fileName, bool saveVertexColor, bool saveUv, double packingThreshold, double textureQuality,
        int jpegQuality = 90, int maxAtlasSize = 4096)
    {
        return LoadMesh(fileName, saveVertexColor, saveUv, packingThreshold, textureQuality, out _, jpegQuality, maxAtlasSize);
    }

    public static IMesh LoadMesh(string fileName, bool saveVertexColor, bool saveUv, double packingThreshold, double textureQuality, out string[] dependencies,
        int jpegQuality = 90, int maxAtlasSize = 4096)
    {
        using var reader = new StreamReader(fileName);

        var vertices = new List<Vertex3>();
        var textureVertices = new List<Vertex2>();
        var facesT = new List<FaceT>();
        var faces = new List<Face>();
        var materials = new List<Material>();
        var materialsDict = new Dictionary<string, int>();
        var currentMaterial = string.Empty;
        var deps = new List<string>();

        while (true)
        {
            var line = reader.ReadLine();

            if (line == null) break;

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;

            var segs = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (segs[0])
            {
                case "v" when segs.Length >= 4:
                    vertices.Add(new Vertex3(
                        double.Parse(segs[1], CultureInfo.InvariantCulture),
                        double.Parse(segs[2], CultureInfo.InvariantCulture),
                        double.Parse(segs[3], CultureInfo.InvariantCulture)));
                    break;
                case "vt" when segs.Length >= 3:

                    var vtx = new Vertex2(
                        double.Parse(segs[1], CultureInfo.InvariantCulture),
                        double.Parse(segs[2], CultureInfo.InvariantCulture));
                    
                    if (vtx.X < 0 || vtx.Y < 0)
                        throw new Exception("Invalid texture coordinates: " + vtx);
                    
                    textureVertices.Add(vtx);
                    break;
                case "vn" when segs.Length == 3:
                    // Skipping normals
                    break;
                case "usemtl" when segs.Length == 2:
                {
                    if (!materialsDict.ContainsKey(segs[1]))
                        throw new Exception($"Material {segs[1]} not found");

                    currentMaterial = segs[1];
                    break;
                }
                case "f" when segs.Length == 4:
                {
                    var first = segs[1].Split('/');
                    var second = segs[2].Split('/');
                    var third = segs[3].Split('/');

                    var hasTexture = first.Length > 1 && first[1].Length > 0 && second.Length > 1 &&
                                     second[1].Length > 0 && third.Length > 1 && third[1].Length > 0;

                    // We ignore this
                    // var hasNormals = vertexIndices[0][2] != null && vertexIndices[1][2] != null && vertexIndices[2][2] != null;

                    var v1 = int.Parse(first[0]);
                    var v2 = int.Parse(second[0]);
                    var v3 = int.Parse(third[0]);

                    if (hasTexture)
                    {
                        var vt1 = int.Parse(first[1]);
                        var vt2 = int.Parse(second[1]);
                        var vt3 = int.Parse(third[1]);

                        var faceT = new FaceT(
                            v1 - 1,
                            v2 - 1,
                            v3 - 1,
                            vt1 - 1,
                            vt2 - 1,
                            vt3 - 1,
                            materialsDict[currentMaterial]);

                        facesT.Add(faceT);
                    }
                    else
                    {
                        var face = new Face(
                            v1 - 1,
                            v2 - 1,
                            v3 - 1);

                        faces.Add(face);
                    }

                    break;
                }
                case "mtllib" when segs.Length == 2:
                {
                    var mtlFileName = segs[1];
                    var mtlFilePath = Path.Combine(Path.GetDirectoryName(fileName) ?? string.Empty, mtlFileName);
                    
                    var mats = Material.ReadMtl(mtlFilePath, out var mtlDeps);

                    deps.AddRange(mtlDeps);
                    deps.Add(mtlFilePath);
                    
                    foreach (var mat in mats)
                    {
                        materials.Add(mat);
                        materialsDict.Add(mat.Name, materials.Count - 1);
                    }

                    break;
                }
                case "l" or "cstype" or "deg" or "bmat" or "step" or "curv" or "curv2" or "surf" or "parm" or "trim"
                    or "end" or "hole" or "scrv" or "sp" or "con":

                    throw new NotSupportedException("Element not supported: '" + line + "'");
            }
        }

        dependencies = deps.ToArray();

        return textureVertices.Count != 0
            ? new MeshT(vertices, textureVertices, facesT, materials, saveVertexColor, saveUv, packingThreshold, textureQuality, jpegQuality, maxAtlasSize)
            : new Mesh(vertices, faces);
    }

    #region Splitters

    private static readonly IVertexUtils xutils3 = new VertexUtilsX();
    private static readonly IVertexUtils yutils3 = new VertexUtilsY();
    private static readonly IVertexUtils zutils3 = new VertexUtilsZ();

    public static double CalculateOptimalTileSize(IMesh mesh, int optimalVertices)
    {
        var bounds = mesh.Bounds;
        var volume = bounds.Width * bounds.Height * bounds.Depth;
        var vertexDensity = mesh.VertexCount / volume;
        var tileSize = Math.Pow(optimalVertices / vertexDensity, 1.0 / 3.0);
        return tileSize;
    }
    
    public static List<IMesh> SplitByTileSizeXYZ(IMesh mesh, Box3 bounds, double tileSize)
    {
        var result =  new List<IMesh>();
        var xMin = bounds.Min.X;
        var xMax = bounds.Max.X;
        var xSlices = new List<(IMesh mesh, int xIndex)>();
        var currentMesh = mesh;
        var xIndex = 0;

        for (double x = xMin + tileSize; x < xMax; x += tileSize)
        {
            currentMesh.Split(xutils3, x, out var left, out var right);

            if (left.FacesCount > 0)
            {
                xSlices.Add((left, xIndex));
            }

            currentMesh = right;
            xIndex++;
        }

        if (currentMesh.FacesCount > 0)
        {
            xSlices.Add((currentMesh, xIndex));
        }

        var yMin = bounds.Min.Y;
        var yMax = bounds.Max.Y;
        var zMin = bounds.Min.Z;
        var zMax = bounds.Max.Z;

        for (var i = 0; i < xSlices.Count; i++)
        {
            var xSlice = xSlices[i];
            var positionY = yMin + tileSize;
            RecurseSplitByYZ(xSlice.mesh, xSlice.xIndex, positionY, tileSize, yMax, zMin, zMax, result, 0);
        }

        return result;
    }

    private static void RecurseSplitByYZ(IMesh mesh, int xIndex, double positionY, double tileSize, double yMax, double zMin, double zMax, List<IMesh> meshes, int yIndex)
    {
        mesh.Split(yutils3, positionY, out var left, out var right);

        if (left.FacesCount > 0)
        {
            RecurseSplitByZ(left, xIndex, yIndex, zMin + tileSize, tileSize, zMax, meshes, 0);
        }

        yIndex++;
        var newPositionY = positionY + tileSize;

        if (newPositionY < yMax)
        {
            RecurseSplitByYZ(right, xIndex, newPositionY, tileSize, yMax, zMin, zMax, meshes, yIndex);
        }
        else
        {
            if (right.FacesCount > 0)
            {
                RecurseSplitByZ(right, xIndex, yIndex, zMin + tileSize, tileSize, zMax, meshes, 0);
            }
        }
    }

    private static void RecurseSplitByZ(IMesh mesh, int xIndex, int yIndex, double positionZ, double tileSize, double zMax, List<IMesh> meshes, int zIndex)
    {
        mesh.Split(zutils3, positionZ, out var left, out var right);

        if (left.FacesCount > 0)
        {
            left.Name = $"{xIndex}_{yIndex}_{zIndex}";
            meshes.Add(left);
        }

        zIndex++;
        var newPositionZ = positionZ + tileSize;

        if (newPositionZ < zMax)
        {
            RecurseSplitByZ(right, xIndex, yIndex, newPositionZ, tileSize, zMax, meshes, zIndex);
        }
        else
        {
            if (right.FacesCount > 0)
            {
                right.Name = $"{xIndex}_{yIndex}_{zIndex}";
                meshes.Add(right);
            }
        }
    }

    #endregion
}
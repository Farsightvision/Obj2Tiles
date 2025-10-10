using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Obj2Tiles.Library.Materials;

namespace Obj2Tiles.Library.Geometry;

public class MeshUtils
{
    public static IMesh LoadMesh(string fileName, bool saveVertexColor, bool saveUv, double packingThreshold, double textureQuality)
    {
        return LoadMesh(fileName, saveVertexColor, saveUv, packingThreshold, textureQuality, out _);
    }
    
    public static IMesh LoadMesh(string fileName, bool saveVertexColor, bool saveUv, double packingThreshold, double textureQuality, out string[] dependencies)
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
            ? new MeshT(vertices, textureVertices, facesT, materials, saveVertexColor, saveUv, packingThreshold, textureQuality)
            : new Mesh(vertices, faces);
    }

    #region Splitters

    private static readonly IVertexUtils xutils3 = new VertexUtilsX();
    private static readonly IVertexUtils yutils3 = new VertexUtilsY();
    private static readonly IVertexUtils zutils3 = new VertexUtilsZ();

    public static async Task<int> RecurseSplitXY(IMesh mesh, int depth, Box3 bounds, ConcurrentBag<IMesh> meshes)
    {
        Debug.WriteLine($"RecurseSplitXY('{mesh.Name}' {mesh.VertexCount}, {depth}, {bounds})");

        if (depth == 0)
        {
            if (mesh.FacesCount > 0)
                meshes.Add(mesh);
            return 0;
        }

        var center = bounds.Center;

        var count = mesh.Split(xutils3, center.X, out var left, out var right);
        count += left.Split(yutils3, center.Y, out var topleft, out var topright);
        count += right.Split(yutils3, center.Y, out var bottomleft, out var bottomright);

        var xbounds = bounds.Split(Axis.X);
        var ybounds1 = xbounds[0].Split(Axis.Y);
        var ybounds2 = xbounds[1].Split(Axis.Y);

        var nextDepth = depth - 1;

        var tasks = new List<Task<int>>();

        if (topleft.FacesCount > 0) tasks.Add(RecurseSplitXY(topleft, nextDepth, ybounds1[0], meshes));
        if (bottomleft.FacesCount > 0) tasks.Add(RecurseSplitXY(bottomleft, nextDepth, ybounds2[0], meshes));
        if (topright.FacesCount > 0) tasks.Add(RecurseSplitXY(topright, nextDepth, ybounds1[1], meshes));
        if (bottomright.FacesCount > 0) tasks.Add(RecurseSplitXY(bottomright, nextDepth, ybounds2[1], meshes));

        await Task.WhenAll(tasks);

        return count + tasks.Sum(t => t.Result);
    }

    public static async Task<int> RecurseSplitXY(IMesh mesh, int depth, Func<IMesh, Vertex3> getSplitPoint,
        ConcurrentBag<IMesh> meshes)
    {
        var center = getSplitPoint(mesh);

        var count = mesh.Split(xutils3, center.X, out var left, out var right);
        count += left.Split(yutils3, center.Y, out var topleft, out var bottomleft);
        count += right.Split(yutils3, center.Y, out var topright, out var bottomright);

        var nextDepth = depth - 1;

        if (nextDepth == 0)
        {
            if (topleft.FacesCount > 0) meshes.Add(topleft);
            if (bottomleft.FacesCount > 0) meshes.Add(bottomleft);
            if (topright.FacesCount > 0) meshes.Add(topright);
            if (bottomright.FacesCount > 0) meshes.Add(bottomright);

            return count;
        }

        var tasks = new List<Task<int>>();

        if (topleft.FacesCount > 0) tasks.Add(RecurseSplitXY(topleft, nextDepth, getSplitPoint, meshes));
        if (bottomleft.FacesCount > 0) tasks.Add(RecurseSplitXY(bottomleft, nextDepth, getSplitPoint, meshes));
        if (topright.FacesCount > 0) tasks.Add(RecurseSplitXY(topright, nextDepth, getSplitPoint, meshes));
        if (bottomright.FacesCount > 0) tasks.Add(RecurseSplitXY(bottomright, nextDepth, getSplitPoint, meshes));

        await Task.WhenAll(tasks);

        return count + tasks.Sum(t => t.Result);
    }

    public static async Task<int> RecurseSplitXYZ(IMesh mesh, int depth, Func<IMesh, Vertex3> getSplitPoint,
        ConcurrentBag<IMesh> meshes)
    {
        var center = getSplitPoint(mesh);

        var count = mesh.Split(xutils3, center.X, out var left, out var right);
        count += left.Split(yutils3, center.Y, out var topleft, out var bottomleft);
        count += right.Split(yutils3, center.Y, out var topright, out var bottomright);

        count += topleft.Split(zutils3, center.Z, out var topleftnear, out var topleftfar);
        count += bottomleft.Split(zutils3, center.Z, out var bottomleftnear, out var bottomleftfar);

        count += topright.Split(zutils3, center.Z, out var toprightnear, out var toprightfar);
        count += bottomright.Split(zutils3, center.Z, out var bottomrightnear, out var bottomrightfar);

        var nextDepth = depth - 1;

        if (nextDepth == 0)
        {
            if (topleftnear.FacesCount > 0) meshes.Add(topleftnear);
            if (topleftfar.FacesCount > 0) meshes.Add(topleftfar);
            if (bottomleftnear.FacesCount > 0) meshes.Add(bottomleftnear);
            if (bottomleftfar.FacesCount > 0) meshes.Add(bottomleftfar);

            if (toprightnear.FacesCount > 0) meshes.Add(toprightnear);
            if (toprightfar.FacesCount > 0) meshes.Add(toprightfar);
            if (bottomrightnear.FacesCount > 0) meshes.Add(bottomrightnear);
            if (bottomrightfar.FacesCount > 0) meshes.Add(bottomrightfar);

            return count;
        }

        var tasks = new List<Task<int>>();

        if (topleftnear.FacesCount > 0) tasks.Add(RecurseSplitXYZ(topleftnear, nextDepth, getSplitPoint, meshes));
        if (topleftfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(topleftfar, nextDepth, getSplitPoint, meshes));
        if (bottomleftnear.FacesCount > 0) tasks.Add(RecurseSplitXYZ(bottomleftnear, nextDepth, getSplitPoint, meshes));
        if (bottomleftfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(bottomleftfar, nextDepth, getSplitPoint, meshes));

        if (toprightnear.FacesCount > 0) tasks.Add(RecurseSplitXYZ(toprightnear, nextDepth, getSplitPoint, meshes));
        if (toprightfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(toprightfar, nextDepth, getSplitPoint, meshes));
        if (bottomrightnear.FacesCount > 0)
            tasks.Add(RecurseSplitXYZ(bottomrightnear, nextDepth, getSplitPoint, meshes));
        if (bottomrightfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(bottomrightfar, nextDepth, getSplitPoint, meshes));

        await Task.WhenAll(tasks);

        return count + tasks.Sum(t => t.Result);
    }

    public static async Task<int> RecurseSplitXYZ(IMesh mesh, int depth, Box3 bounds, ConcurrentBag<IMesh> meshes)
    {
        Debug.WriteLine($"RecurseSplitXYZ('{mesh.Name}' {mesh.VertexCount}, {depth}, {bounds})");

        if (depth == 0)
        {
            if (mesh.FacesCount > 0)
                meshes.Add(mesh);
            return 0;
        }

        var center = bounds.Center;

        var count = mesh.Split(xutils3, center.X, out var left, out var right);
        count += left.Split(yutils3, center.Y, out var topleft, out var bottomleft);
        count += right.Split(yutils3, center.Y, out var topright, out var bottomright);

        count += topleft.Split(zutils3, center.Z, out var topleftnear, out var topleftfar);
        count += bottomleft.Split(zutils3, center.Z, out var bottomleftnear, out var bottomleftfar);

        count += topright.Split(zutils3, center.Z, out var toprightnear, out var toprightfar);
        count += bottomright.Split(zutils3, center.Z, out var bottomrightnear, out var bottomrightfar);

        var xbounds = bounds.Split(Axis.X);
        var ybounds1 = xbounds[0].Split(Axis.Y);
        var ybounds2 = xbounds[1].Split(Axis.Y);

        var zbounds1 = ybounds1[0].Split(Axis.Z);
        var zbounds2 = ybounds1[1].Split(Axis.Z);

        var zbounds3 = ybounds2[0].Split(Axis.Z);
        var zbounds4 = ybounds2[1].Split(Axis.Z);

        var nextDepth = depth - 1;

        var tasks = new List<Task<int>>();

        if (topleftnear.FacesCount > 0) tasks.Add(RecurseSplitXYZ(topleftnear, nextDepth, zbounds1[0], meshes));
        if (topleftfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(topleftfar, nextDepth, zbounds1[1], meshes));
        if (bottomleftnear.FacesCount > 0) tasks.Add(RecurseSplitXYZ(bottomleftnear, nextDepth, zbounds2[0], meshes));
        if (bottomleftfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(bottomleftfar, nextDepth, zbounds2[1], meshes));
        if (toprightnear.FacesCount > 0) tasks.Add(RecurseSplitXYZ(toprightnear, nextDepth, zbounds3[0], meshes));
        if (toprightfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(toprightfar, nextDepth, zbounds3[1], meshes));
        if (bottomrightnear.FacesCount > 0) tasks.Add(RecurseSplitXYZ(bottomrightnear, nextDepth, zbounds4[0], meshes));
        if (bottomrightfar.FacesCount > 0) tasks.Add(RecurseSplitXYZ(bottomrightfar, nextDepth, zbounds4[1], meshes));

        await Task.WhenAll(tasks);

        return count + tasks.Sum(t => t.Result);
    }

    public static double CalculateOptimalTileSize(IMesh mesh, int optimalVertices)
    {
        var bounds = mesh.Bounds;
        var volume = bounds.Width * bounds.Height * bounds.Depth;
        var vertexDensity = mesh.VertexCount / volume;
        var tileSize = Math.Pow(optimalVertices / vertexDensity, 1.0 / 3.0);
        return tileSize;
    }
    
    public static async Task<int> SplitByTileSizeXYZ(IMesh mesh, Box3 bounds, double tileSize, ConcurrentBag<IMesh> meshes)
    {
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
        var tasks = new List<Task>();

        for (var i = 0; i < xSlices.Count; i++)
        {
            var xSlice = xSlices[i];
            var positionY = yMin + tileSize;
            tasks.Add(Task.Run(() => RecurseSplitByYZ(xSlice.mesh, xSlice.xIndex, positionY, tileSize, yMax, zMin, zMax, meshes, 0)));
        }

        await Task.WhenAll(tasks);
        return meshes.Count;
    }

    private static void RecurseSplitByYZ(IMesh mesh, int xIndex, double positionY, double tileSize, double yMax, double zMin, double zMax, ConcurrentBag<IMesh> meshes, int yIndex)
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

    private static void RecurseSplitByZ(IMesh mesh, int xIndex, int yIndex, double positionZ, double tileSize, double zMax, ConcurrentBag<IMesh> meshes, int zIndex)
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
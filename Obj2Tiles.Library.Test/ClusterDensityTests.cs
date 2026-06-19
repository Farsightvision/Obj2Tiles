using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library.Test;

[TestFixture]
public class ClusterDensityTests
{
    private string _tmpDir = string.Empty;
    private string _texPath = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "cluster-density-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _texPath = Path.Combine(_tmpDir, "tex.png");
        using var img = new Image<Rgba32>(64, 64);
        img.Mutate(x => x.BackgroundColor(Color.SandyBrown));
        img.Save(_texPath);
    }

    [TearDown]
    public void TearDown()
    {
        TexturesCache.Clear();
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    private static void BuildDisjointQuads(
        int k,
        List<Vertex3> verts, List<Vertex2> uvs,
        List<FaceT> faceTs, List<MeshFace> meshFaces,
        int materialIndex)
    {
        for (var i = 0; i < k; i++)
        {
            double x = i * 2.0;
            var vb = verts.Count;
            verts.Add(new Vertex3(x, 0, 0));
            verts.Add(new Vertex3(x + 1, 0, 0));
            verts.Add(new Vertex3(x + 1, 1, 0));
            verts.Add(new Vertex3(x, 1, 0));
            var tb = uvs.Count;
            uvs.Add(new Vertex2(0, 0));
            uvs.Add(new Vertex2(1, 0));
            uvs.Add(new Vertex2(1, 1));
            uvs.Add(new Vertex2(0, 1));
            faceTs.Add(new FaceT(vb, vb + 1, vb + 2, tb, tb + 1, tb + 2, materialIndex));
            faceTs.Add(new FaceT(vb, vb + 2, vb + 3, tb, tb + 2, tb + 3, materialIndex));
            meshFaces.Add(new MeshFace(vb, vb + 1, vb + 2, tb, tb + 1, tb + 2, materialIndex));
            meshFaces.Add(new MeshFace(vb, vb + 2, vb + 3, tb, tb + 2, tb + 3, materialIndex));
        }
    }

    [Test]
    public void MaxClustersForCeiling_IsLargestFittingCount()
    {
        foreach (var ceiling in new[] { 256, 512, 1024, 4096 })
        {
            int maxC = ClusterDensity.MaxClustersForCeiling(ceiling);
            Assert.That(ClusterDensity.NeededAtlasEdge(maxC), Is.LessThanOrEqualTo(ceiling), $"ceiling={ceiling}");
            Assert.That(ClusterDensity.NeededAtlasEdge(maxC + 1), Is.GreaterThan(ceiling), $"ceiling={ceiling}");
        }
    }

    [Test]
    public void OverDense_DropsSmallestIslands_FitsCapWithoutThrow()
    {
        // 200 islands can't fit a 256 cap (gutter floor), so the over-dense path must drop, not throw.
        var verts = new List<Vertex3>();
        var uvs = new List<Vertex2>();
        var faceTs = new List<FaceT>();
        var meshFaces = new List<MeshFace>();
        var materials = new List<Material> { new("mat", _texPath) };
        BuildDisjointQuads(200, verts, uvs, faceTs, meshFaces, 0);

        int cap = 256;
        int maxC = ClusterDensity.MaxClustersForCeiling(cap);
        Assert.That(ClusterDensity.NeededAtlasEdge(200), Is.GreaterThan(cap), "precondition: 200 islands overflow cap 256");

        var mesh = new MeshT_Hlod(verts, uvs, faceTs, materials,
            saveVertexColor: false, saveUv: true,
            packingThreshold: 0.618, textureQuality: 1.0,
            jpegQuality: 90, maxAtlasSize: cap)
        {
            FilePath = Path.Combine(_tmpDir, "tile.obj"),
            Name = "tile",
            AtlasCapCeiling = cap,
        };

        Assert.DoesNotThrow(() => mesh.PrepareRepackTextures(removeUnused: true),
            "over-dense tile must drop islands and fit, not throw");
        Assert.That(mesh.TextureBearingClusterCount, Is.GreaterThan(0));
        Assert.That(mesh.TextureBearingClusterCount, Is.LessThanOrEqualTo(maxC),
            "kept cluster count must fit the cap floor");
        Assert.That(mesh.FacesCount, Is.LessThan(faceTs.Count), "dropped islands' faces must be removed");
        Assert.That(ClusterDensity.NeededAtlasEdge(mesh.TextureBearingClusterCount), Is.LessThanOrEqualTo(cap));

        foreach (var m in materials) mesh.FillAtlases(m);
        Assert.DoesNotThrow(() => mesh.SaveAtlasesAndUpdateMaterial());
        Assert.That(File.Exists(Path.Combine(_tmpDir, "tile-texture-diffuse-atlas.jpg")), Is.True);
    }

    [Test]
    public void NeededAtlasEdge_MirrorsCapFloorMath()
    {
        Assert.That(ClusterDensity.NeededAtlasEdge(0), Is.EqualTo(0));
        Assert.That(ClusterDensity.NeededAtlasEdge(-3), Is.EqualTo(0));
        foreach (var c in new[] { 1, 50, 256, 1024, 4000, 20000 })
        {
            int g = MeshT_Hlod.EffectiveGutterPixels(c);
            double floorEdge = 1 + 2 * g;
            double minArea = c * floorEdge * floorEdge / 0.5;
            int expected = Common.NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(minArea)));
            Assert.That(ClusterDensity.NeededAtlasEdge(c), Is.EqualTo(expected), $"c={c}");
        }
    }
}

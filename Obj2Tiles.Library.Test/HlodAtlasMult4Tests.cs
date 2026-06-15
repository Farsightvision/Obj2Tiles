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

/// <summary>
/// KHR_texture_basisu requires KTX2 edges to be multiples of 4; s3tc-only WebGL
/// clients render whole tiles black otherwise, so saved atlas edges must be too.
/// </summary>
[TestFixture]
public class HlodAtlasMult4Tests
{
    private string _tmpDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "hlod-mult4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    [TearDown]
    public void TearDown()
    {
        TexturesCache.Clear();
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [TestCase(70)]
    [TestCase(1914)]
    public void SavedAtlas_EdgeIsMultipleOf4(int sourceTextureEdge)
    {
        var texPath = Path.Combine(_tmpDir, $"src-{sourceTextureEdge}.png");
        using (var img = new Image<Rgba32>(sourceTextureEdge, sourceTextureEdge))
        {
            img.Mutate(x => x.BackgroundColor(Color.SandyBrown));
            img.Save(texPath);
        }

        var vertices = new List<Vertex3>
        {
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        };
        var uvs = new List<Vertex2>
        {
            new(0, 0), new(1, 0), new(1, 1), new(0, 1),
        };
        var faces = new List<FaceT>
        {
            new(0, 1, 2, 0, 1, 2, 0),
            new(0, 2, 3, 0, 2, 3, 0),
        };
        var materials = new List<Material>
        {
            new("src-material", texPath),
        };

        var mesh = new MeshT_Hlod(vertices, uvs, faces, materials,
            saveVertexColor: false, saveUv: true,
            packingThreshold: 0.618, textureQuality: 1.0,
            jpegQuality: 90, maxAtlasSize: 4096)
        {
            FilePath = Path.Combine(_tmpDir, "tile.obj"),
            Name = "tile",
        };

        mesh.PrepareRepackTextures(removeUnused: true);
        mesh.FillAtlases(materials[0]);
        mesh.SaveAtlasesAndUpdateMaterial();

        var atlasPath = Path.Combine(_tmpDir, "tile-texture-diffuse-atlas.jpg");
        Assert.That(File.Exists(atlasPath), Is.True,
            $"atlas not written for source edge {sourceTextureEdge}");

        using var atlas = Image.Load(atlasPath);
        Assert.That(atlas.Width % 4, Is.EqualTo(0),
            $"atlas width {atlas.Width} is not a multiple of 4 (source {sourceTextureEdge}) — " +
            "non-mult-4 KTX2 violates KHR_texture_basisu and renders BLACK on s3tc-only clients");
        Assert.That(atlas.Height % 4, Is.EqualTo(0),
            $"atlas height {atlas.Height} is not a multiple of 4 (source {sourceTextureEdge})");
    }
}

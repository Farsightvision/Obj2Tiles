using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;
using Obj2Tiles.Stages.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Obj2Tiles.Test;

public class ModelMetricsNormalMapTests
{
    private static string WritePng(string dir, string name, int w, int h)
    {
        string path = Path.Combine(dir, name);
        using var img = new Image<Rgba32>(w, h);
        img.Save(path);
        return path;
    }

    [Test]
    public void DecodedTextureBytes_IncludesNormalMap()
    {
        string dir = Path.Combine(Path.GetTempPath(), "o2t-mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string diffuse = WritePng(dir, "d.png", 64, 64);
            string normal = WritePng(dir, "n.png", 32, 32);
            var mats = new List<Material> { new Material("m", texture: diffuse, normalMap: normal) };
            var bounds = new Box3(0, 0, 0, 1, 1, 1);

            var m = ModelMetrics.Compute(triangleCount: 1, vertexCount: 3, bounds, mats, dir);

            long expectedDecoded = (64L * 64 + 32L * 32) * 4;
            Assert.That(m.DecodedTextureBytes, Is.EqualTo(expectedDecoded));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void TextureBytes_ExcludesNormalMap_DepthAxisUnchanged()
    {
        string dir = Path.Combine(Path.GetTempPath(), "o2t-mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string diffuse = WritePng(dir, "d.png", 64, 64);
            string normal = WritePng(dir, "n.png", 32, 32);
            var matsWith = new List<Material> { new Material("m", texture: diffuse, normalMap: normal) };
            var matsWithout = new List<Material> { new Material("m", texture: diffuse) };
            var bounds = new Box3(0, 0, 0, 1, 1, 1);

            long withNormal = ModelMetrics.Compute(1, 3, bounds, matsWith, dir).TextureBytes;
            long withoutNormal = ModelMetrics.Compute(1, 3, bounds, matsWithout, dir).TextureBytes;

            Assert.That(withNormal, Is.EqualTo(withoutNormal));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void TextureBytes_NormalMapCollidingWithLaterDiffuse_DoesNotSuppressDiffuse()
    {
        // Parity: the shared path must not drop the later diffuse from TextureBytes (the depth axis).
        string dir = Path.Combine(Path.GetTempPath(), "o2t-mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string texA = WritePng(dir, "a.png", 64, 64);
            string shared = WritePng(dir, "shared.png", 48, 48); // A's normal map AND B's diffuse
            var mats = new List<Material>
            {
                new Material("A", texture: texA, normalMap: shared),
                new Material("B", texture: shared),
            };
            var bounds = new Box3(0, 0, 0, 1, 1, 1);

            long tex = ModelMetrics.Compute(1, 3, bounds, mats, dir).TextureBytes;

            long expected = new FileInfo(texA).Length + new FileInfo(shared).Length;
            Assert.That(tex, Is.EqualTo(expected));
        }
        finally { Directory.Delete(dir, true); }
    }
}

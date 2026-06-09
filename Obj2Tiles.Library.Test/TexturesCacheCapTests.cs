using System.IO;
using NUnit.Framework;
using Obj2Tiles.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Obj2Tiles.Library.Test;

[TestFixture]
public class TexturesCacheCapTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        // Reset static cache state between tests.
        TexturesCache.MaxResidentEdge = 0;
        TexturesCache.MaxResidentBytes = 0;
        TexturesCache.Clear();
        _dir = Path.Combine(Path.GetTempPath(), "o2t_cap_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        TexturesCache.MaxResidentEdge = 0;
        TexturesCache.Clear();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string WritePng(string name, int w, int h)
    {
        var path = Path.Combine(_dir, name);
        using var img = new Image<Rgba32>(w, h);
        img.SaveAsPng(path);
        return path;
    }

    [Test]
    public void GetCappedDims_WithExplicitCap_DownsizesToCap()
    {
        var p = WritePng("a.png", 2000, 1000);
        TexturesCache.MaxResidentEdge = 8192; // cache "on", high ceiling
        var dims = TexturesCache.GetCappedDims(p, 512);
        Assert.That(System.Math.Max(dims.Width, dims.Height), Is.EqualTo(512));
    }

    [Test]
    public void GetTexture_DifferentCaps_CoexistAtDistinctSizes()
    {
        var p = WritePng("b.png", 2000, 2000);
        TexturesCache.MaxResidentEdge = 8192;
        var small = TexturesCache.GetTexture(p, 512);
        var big = TexturesCache.GetTexture(p, 1024);
        Assert.That(small.Width, Is.EqualTo(512));
        Assert.That(big.Width, Is.EqualTo(1024));
        // Both remain valid (distinct cache entries, not the same object).
        Assert.That(ReferenceEquals(small, big), Is.False);
    }

    [Test]
    public void GetCappedDims_AndGetTexture_AgreeForSameCap()
    {
        // The consistency invariant: packer dims must equal the decoded image dims.
        var p = WritePng("c.png", 3000, 1500);
        TexturesCache.MaxResidentEdge = 8192;
        var dims = TexturesCache.GetCappedDims(p, 512);
        var img = TexturesCache.GetTexture(p, 512);
        Assert.That((img.Width, img.Height), Is.EqualTo((dims.Width, dims.Height)));
    }

    [Test]
    public void EvictTexture_RemovesAllCapsForPath()
    {
        var p = WritePng("d.png", 2000, 2000);
        TexturesCache.MaxResidentEdge = 8192;
        TexturesCache.GetTexture(p, 512);
        TexturesCache.GetTexture(p, 1024);
        Assert.That(TexturesCache.ResidentBytes, Is.GreaterThan(0));
        TexturesCache.EvictTexture(p);
        Assert.That(TexturesCache.ResidentBytes, Is.EqualTo(0));
    }

    [Test]
    public void NoCapOverload_UsesGlobalMaxResidentEdge_BackwardCompatible()
    {
        var p = WritePng("e.png", 2000, 2000);
        TexturesCache.MaxResidentEdge = 1024; // global cap
        var img = TexturesCache.GetTexture(p);          // no per-tile cap
        var dims = TexturesCache.GetCappedDims(p);      // no per-tile cap
        Assert.That(img.Width, Is.EqualTo(1024));
        Assert.That(dims.Width, Is.EqualTo(1024));
    }

    [Test]
    public void CacheOff_PerTileCapIgnored_DecodesFullRes()
    {
        // MaxResidentEdge == 0 (small-model legacy path): per-tile cap must be ignored.
        var p = WritePng("f.png", 2000, 2000);
        TexturesCache.MaxResidentEdge = 0;
        var img = TexturesCache.GetTexture(p, 512);
        Assert.That(img.Width, Is.EqualTo(2000)); // full res, unchanged
    }
}

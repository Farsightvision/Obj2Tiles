using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        TexturesCache.MaxResidentEdge = 8192;
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
        Assert.That(ReferenceEquals(small, big), Is.False);
    }

    [Test]
    public void GetCappedDims_AndGetTexture_AgreeForSameCap()
    {
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
        TexturesCache.MaxResidentEdge = 1024;
        var img = TexturesCache.GetTexture(p);
        var dims = TexturesCache.GetCappedDims(p);
        Assert.That(img.Width, Is.EqualTo(1024));
        Assert.That(dims.Width, Is.EqualTo(1024));
    }

    [Test]
    public void CacheOff_PerTileCapIgnored_DecodesFullRes()
    {
        // MaxResidentEdge == 0 disables the cache, so the per-tile cap is ignored.
        var p = WritePng("f.png", 2000, 2000);
        TexturesCache.MaxResidentEdge = 0;
        var img = TexturesCache.GetTexture(p, 512);
        Assert.That(img.Width, Is.EqualTo(2000));
    }

    [Test]
    public void EvictTexture_WhileReaderHoldsLease_DefersDisposeUntilRelease()
    {
        var p = WritePng("g.png", 2000, 2000);
        TexturesCache.MaxResidentEdge = 8192;
        TexturesCache.GetTexture(p, 512);
        Assert.That(TexturesCache.ResidentBytes, Is.GreaterThan(0), "precondition: texture resident");

        TexturesCache.AcquireRead(p);
        TexturesCache.EvictTexture(p);
        Assert.That(TexturesCache.ResidentBytes, Is.GreaterThan(0),
            "evict while a reader holds the lease must DEFER — image still resident");

        TexturesCache.ReleaseRead(p);
        Assert.That(TexturesCache.ResidentBytes, Is.EqualTo(0),
            "last ReleaseRead must perform the deferred dispose");
    }

    [Test]
    public void EvictTexture_DoesNotDisposeImageUnderActiveLease()
    {
        var p = WritePng("h.png", 1024, 1024);
        TexturesCache.MaxResidentEdge = 8192;

        TexturesCache.AcquireRead(p);
        var img = TexturesCache.GetTexture(p, 512);
        try
        {
            Task.Run(() => TexturesCache.EvictTexture(p)).Wait();
            Assert.DoesNotThrow(() =>
            {
                var px = img[0, 0];
                _ = px;
            }, "image sampled under an active lease must not be disposed");
        }
        finally
        {
            TexturesCache.ReleaseRead(p);
        }
        Assert.That(TexturesCache.ResidentBytes, Is.EqualTo(0));
    }

    [Test]
    public void AcquireRelease_WithoutEviction_KeepsTextureResident()
    {
        var p = WritePng("i.png", 1024, 1024);
        TexturesCache.MaxResidentEdge = 8192;
        TexturesCache.GetTexture(p, 512);
        var before = TexturesCache.ResidentBytes;

        TexturesCache.AcquireRead(p);
        TexturesCache.ReleaseRead(p);

        Assert.That(TexturesCache.ResidentBytes, Is.EqualTo(before),
            "acquire/release with no eviction must leave the cache untouched");
    }

    [Test]
    public void GetTexture_TransientFault_RetriesFresh()
    {
        // A faulted decode must not cache its exception; the next call must retry fresh.
        var p = Path.Combine(_dir, "ghost.png");
        TexturesCache.MaxResidentEdge = 8192;
        Assert.Catch<Exception>(() => TexturesCache.GetTexture(p, 512), "missing file must throw");
        WritePng("ghost.png", 256, 256);
        var img = TexturesCache.GetTexture(p, 512);
        Assert.That((img.Width, img.Height), Is.EqualTo((256, 256)),
            "after a transient fault, the next GetTexture must retry fresh (no cached exception)");
    }

    [Test]
    public void ConcurrentReadersAndEvictor_NeverDisposeInUseImage()
    {
        var p = WritePng("j.png", 1024, 1024);
        TexturesCache.MaxResidentEdge = 8192;

        Exception? failure = null;
        const int readers = 8;
        const int iters = 400;
        using var start = new ManualResetEventSlim(false);

        void Reader()
        {
            start.Wait();
            for (int k = 0; k < iters && failure == null; k++)
            {
                TexturesCache.AcquireRead(p);
                try
                {
                    var img = TexturesCache.GetTexture(p, 512);
                    var _ = img[k % img.Width, k % img.Height];
                }
                catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); }
                finally { TexturesCache.ReleaseRead(p); }
            }
        }

        void Evictor()
        {
            start.Wait();
            for (int k = 0; k < iters * 2 && failure == null; k++)
                TexturesCache.EvictTexture(p);
        }

        var tasks = new List<Task>();
        for (int r = 0; r < readers; r++) tasks.Add(Task.Run(Reader));
        tasks.Add(Task.Run(Evictor));
        start.Set();
        Task.WaitAll(tasks.ToArray());

        Assert.That(failure, Is.Null,
            failure == null ? "" : $"reader observed a disposed/torn image: {failure}");
    }
}

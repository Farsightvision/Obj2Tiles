using NUnit.Framework;
using Obj2Tiles.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Obj2Tiles.Library.Test;

/// <summary>
/// Edge-bleed dilation that stops bilinear filtering sampling empty atlas background as dark
/// cracks along tile-boundary triangles. Fills empty pixels within `bleed` Chebyshev distance.
/// </summary>
public class CommonHlodDilateTests
{
    private static bool IsEmpty(Rgba32 c) => c.A == 0 || (c.R == 0 && c.G == 0 && c.B == 0);

    [Test]
    public void DilateAtlasBleed_FillsExactlyTheChebyshevBandAroundANonEmptyPixel()
    {
        using var img = new Image<Rgba32>(5, 5);
        var red = new Rgba32(255, 0, 0, 255);
        img[2, 2] = red;

        Common_Hlod.DilateAtlasBleed(img, bleed: 1);

        Assert.That(img[2, 2], Is.EqualTo(red));
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            Assert.That(IsEmpty(img[2 + dx, 2 + dy]), Is.False,
                $"pixel ({2 + dx},{2 + dy}) within bleed=1 must be filled");
        Assert.That(IsEmpty(img[0, 2]), Is.True, "(0,2) at distance 2 must stay empty");
        Assert.That(IsEmpty(img[2, 0]), Is.True, "(2,0) at distance 2 must stay empty");
        Assert.That(IsEmpty(img[4, 4]), Is.True, "(4,4) corner must stay empty");
    }

    [Test]
    public void DilateAtlasBleed_ZeroBleed_IsNoOp()
    {
        using var img = new Image<Rgba32>(3, 3);
        var red = new Rgba32(255, 0, 0, 255);
        img[1, 1] = red;

        Common_Hlod.DilateAtlasBleed(img, bleed: 0);

        Assert.That(img[1, 1], Is.EqualTo(red), "seed must be preserved");
        Assert.That(IsEmpty(img[0, 0]), Is.True, "no dilation: neighbours stay empty");
        Assert.That(IsEmpty(img[1, 0]), Is.True, "no dilation: neighbours stay empty");
    }

    [Test]
    public void DilateAtlasBleed_DoesNotOverwriteExistingNonEmptyPixels()
    {
        using var img = new Image<Rgba32>(3, 3);
        var red = new Rgba32(255, 0, 0, 255);
        var blue = new Rgba32(0, 0, 255, 255);
        img[0, 1] = red;
        img[2, 1] = blue;

        Common_Hlod.DilateAtlasBleed(img, bleed: 1);

        Assert.That(img[0, 1], Is.EqualTo(red));
        Assert.That(img[2, 1], Is.EqualTo(blue));
        Assert.That(IsEmpty(img[1, 1]), Is.False);
    }
}

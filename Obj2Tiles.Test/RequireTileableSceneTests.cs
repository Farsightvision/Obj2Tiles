using System;
using NUnit.Framework;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

public class RequireTileableSceneTests
{
    [Test]
    public void ValidScene_DoesNotThrow()
        => Assert.DoesNotThrow(() => ConformalHierarchyStage.RequireTileableScene(10, 14.142));

    [Test]
    public void ZeroFaces_Throws_NoTileableGeometry()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(0, 14.142));
        Assert.That(ex!.Message, Does.Contain("no non-degenerate triangles"));
    }

    [Test]
    public void NaNDiagonal_Throws_NonFiniteExtent()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(10, double.NaN));
        Assert.That(ex!.Message, Does.Contain("finite, non-zero scene extent"));
    }

    [Test]
    public void ZeroDiagonal_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(10, 0.0));

    [Test]
    public void PositiveInfinityDiagonal_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(10, double.PositiveInfinity));

    [Test]
    public void NegativeFaceCount_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(-1, 14.142));

    [Test]
    public void TinyButFiniteDiagonal_DoesNotThrow()
        => Assert.DoesNotThrow(() => ConformalHierarchyStage.RequireTileableScene(1, 1.4142e-4));

    [Test]
    public void HugeDiagonal_DoesNotThrow()
        => Assert.DoesNotThrow(() => ConformalHierarchyStage.RequireTileableScene(1, 1.4142e9));

    [Test]
    public void ZeroFacesChecked_BeforeDiagonal()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(0, 0.0));
        Assert.That(ex!.Message, Does.Contain("no non-degenerate triangles"));
    }
}

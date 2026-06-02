using System;
using NUnit.Framework;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

/// <summary>
/// Pins ConformalHierarchyStage.RequireTileableScene — the Phase-4 tree-build guard
/// against degenerate/arbitrary input (robustness sweep, gen obj3-14). Contract: reject
/// input that produces no tileable geometry (zero surviving triangles, or a non-finite /
/// zero scene diagonal) with a clear InvalidOperationException — instead of the opaque
/// KeyNotFoundException (coincident verts) or silent "geometricError": NaN in tileset.json
/// (NaN/Inf coords) observed on the real bake. Valid models — including flat (zero-thickness)
/// and sub-millimetre — must pass unchanged (only exactly-zero / non-finite is rejected).
/// </summary>
public class RequireTileableSceneTests
{
    [Test]
    public void ValidScene_DoesNotThrow()
        => Assert.DoesNotThrow(() => ConformalHierarchyStage.RequireTileableScene(10, 14.142));

    [Test]
    public void ZeroFaces_Throws_NoTileableGeometry()
    {
        // t-coincident: the lone degenerate triangle is dropped by zero-area sanitization -> 0 faces.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(0, 14.142));
        Assert.That(ex!.Message, Does.Contain("no non-degenerate triangles"));
    }

    [Test]
    public void NaNDiagonal_Throws_NonFiniteExtent()
    {
        // t-nan: a NaN vertex propagates into the bbox -> diagonal NaN -> would emit "geometricError": NaN.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(10, double.NaN));
        Assert.That(ex!.Message, Does.Contain("finite, non-zero scene extent"));
    }

    [Test]
    public void ZeroDiagonal_Throws()
        // All vertices coincident -> zero-size bbox (the t-coincident extent, independent of the face count).
        => Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(10, 0.0));

    [Test]
    public void PositiveInfinityDiagonal_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(10, double.PositiveInfinity));

    [Test]
    public void NegativeFaceCount_Throws()
        // Defensive: face count can never be negative, but <= 0 must floor to the no-geometry error.
        => Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(-1, 14.142));

    [Test]
    public void TinyButFiniteDiagonal_DoesNotThrow()
        // t-tiny: a sub-millimetre model (diagonal ~1.4e-4) is valid and baked fine — must NOT be rejected.
        => Assert.DoesNotThrow(() => ConformalHierarchyStage.RequireTileableScene(1, 1.4142e-4));

    [Test]
    public void HugeDiagonal_DoesNotThrow()
        // t-huge: a 1e9-span model (diagonal ~1.4e9) produced finite geomError — valid, must pass.
        => Assert.DoesNotThrow(() => ConformalHierarchyStage.RequireTileableScene(1, 1.4142e9));

    [Test]
    public void ZeroFacesChecked_BeforeDiagonal()
    {
        // When BOTH degenerate (0 faces AND 0 diagonal, the real t-coincident state), the more
        // specific "no triangles" message wins — it points at the actual cause.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConformalHierarchyStage.RequireTileableScene(0, 0.0));
        Assert.That(ex!.Message, Does.Contain("no non-degenerate triangles"));
    }
}

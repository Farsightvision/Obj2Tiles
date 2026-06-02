using NUnit.Framework;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

/// <summary>
/// Pins the HLOD per-LOD texel-density schedule r_d = leafDensity / 2^clamp(referenceDepth-depth, 0, 16),
/// extracted from three duplicated sites (PredictAtlasSide, ExtendAdaptiveImpl, atlas-area sizer). This
/// drives atlas sizing, so a regression silently changes texture resolution per LOD.
/// </summary>
public class LodDensityScheduleTests
{
    [Test]
    public void DensityAtDepth_AtLeafReference_IsFullLeafDensity()
    {
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, referenceDepth: 5, depth: 5), Is.EqualTo(100.0).Within(1e-12));
    }

    [Test]
    public void DensityAtDepth_HalvesPerStepAboveLeaf()
    {
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 4), Is.EqualTo(50.0).Within(1e-12));        // 1 step up
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 3), Is.EqualTo(25.0).Within(1e-12));        // 2 steps up
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 1), Is.EqualTo(100.0 / 16).Within(1e-12));  // 4 steps up
    }

    [Test]
    public void DensityAtDepth_BelowReference_ClampsToFullLeafDensity()
    {
        // depth > referenceDepth (adaptively-deepened cells): up-shift clamps to 0 -> leaf density.
        // This is exactly the case the old inline `if (depth > ref) rD = leafDensity` handled.
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 7), Is.EqualTo(100.0).Within(1e-12));
    }

    [Test]
    public void DensityAtDepth_ClampsUpShiftAt16()
    {
        // referenceDepth - depth beyond 16 saturates at 2^16 (guards against integer-shift collapse).
        double atShift16 = LodDensitySchedule.DensityAtDepth(65536.0, 16, 0);   // 65536 / 2^16 = 1.0
        double beyond    = LodDensitySchedule.DensityAtDepth(65536.0, 100, 0);  // clamps to 2^16 -> also 1.0
        Assert.That(atShift16, Is.EqualTo(1.0).Within(1e-12));
        Assert.That(beyond, Is.EqualTo(1.0).Within(1e-12));
    }
}

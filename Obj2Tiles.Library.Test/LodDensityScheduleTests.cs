using NUnit.Framework;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

/// <summary>
/// Pins the per-LOD density schedule r_d = leafDensity / 2^clamp(referenceDepth-depth, 0, 16) that drives atlas sizing.
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
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 4), Is.EqualTo(50.0).Within(1e-12));
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 3), Is.EqualTo(25.0).Within(1e-12));
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 1), Is.EqualTo(100.0 / 16).Within(1e-12));
    }

    [Test]
    public void DensityAtDepth_BelowReference_ClampsToFullLeafDensity()
    {
        Assert.That(LodDensitySchedule.DensityAtDepth(100.0, 5, 7), Is.EqualTo(100.0).Within(1e-12));
    }

    [Test]
    public void DensityAtDepth_ClampsUpShiftAt16()
    {
        double atShift16 = LodDensitySchedule.DensityAtDepth(65536.0, 16, 0);
        double beyond    = LodDensitySchedule.DensityAtDepth(65536.0, 100, 0);
        Assert.That(atShift16, Is.EqualTo(1.0).Within(1e-12));
        Assert.That(beyond, Is.EqualTo(1.0).Within(1e-12));
    }
}

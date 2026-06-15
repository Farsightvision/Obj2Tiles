using NUnit.Framework;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

public class FitsPathDemotionTests
{
    private const long MiB = 1L << 20;

    [Test]
    public void DevPod28GiB_TypicalLoad_ClampsTo1_Demotes()
    {
        int fitsMdop = HierarchicalTilingStage.ClampWorkersToMemory(
            7, 16896 * MiB, 11840 * MiB, 4096);
        Assert.That(fitsMdop, Is.EqualTo(1));
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, fitsMdop), Is.True,
            "dev envelope must demote to the transient-eviction path");
    }

    [Test]
    public void DevPod28GiB_LightLoad_ClampsTo3_KeepsDecodeOnce()
    {
        int fitsMdop = HierarchicalTilingStage.ClampWorkersToMemory(
            7, 18432 * MiB, 11840 * MiB, 4096);
        Assert.That(fitsMdop, Is.EqualTo(3));
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, fitsMdop), Is.False);
    }

    [Test]
    public void RoomyRam_KeepsDecodeOnceFitsPath()
    {
        int fitsMdop = HierarchicalTilingStage.ClampWorkersToMemory(
            7, 49152 * MiB, 11840 * MiB, 4096);
        Assert.That(fitsMdop, Is.EqualTo(7));
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, fitsMdop), Is.False,
            "ample RAM keeps the decode-once fast path");
    }

    [Test]
    public void ThresholdBoundaries()
    {
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, 1), Is.True);
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, 2), Is.True);
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, 3), Is.False);
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(8, 3), Is.True);
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(8, 4), Is.False);
    }

    [Test]
    public void SmallDesired_ClampTo1_StillDemotes()
    {
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(2, 1), Is.True);
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(2, 2), Is.False);
    }
}

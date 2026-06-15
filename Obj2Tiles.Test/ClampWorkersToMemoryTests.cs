using NUnit.Framework;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

public class ClampWorkersToMemoryTests
{
    private const long GiB = 1L << 30;

    [Test]
    public void DesiredMdop1_Passthrough()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(1, 64 * GiB, 0, 8192), Is.EqualTo(1));

    [Test]
    public void CapEdgeZero_NoMemoryModel_ReturnsDesired()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 1 * GiB, 0, 0), Is.EqualTo(8));

    [Test]
    public void RoomyRam_ReturnsDesired()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 64 * GiB, 1 * GiB, 8192), Is.EqualTo(8));

    [Test]
    public void TightRam_VlrgNativeCase_DegradesTo1()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(4, 15 * GiB, 9 * GiB, 8192), Is.EqualTo(1));

    [Test]
    public void ReserveExceedsBudget_Floors1()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 10 * GiB, 9 * GiB, 8192), Is.EqualTo(1));

    [Test]
    public void MidPressure_DegradesProportionally()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 16 * GiB, 8 * GiB, 8192), Is.EqualTo(2));

    [Test]
    public void Cap4096CommonConfig_NotClamped()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(4, 14 * GiB, 5 * GiB, 4096), Is.EqualTo(4));

    [Test]
    public void ExtremeCapEdge_NoOverflow_Returns1()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 64 * GiB, 0, 1_000_000), Is.EqualTo(1));
}

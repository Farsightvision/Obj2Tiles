using NUnit.Framework;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

/// <summary>
/// Pins ClampWorkersToMemory — the pure core of the Phase-1 graceful-degradation clamp (the Obj2b OOM
/// fix). Contract: degrade the worker count as available RAM shrinks (worst case 1, which always fits),
/// never exceed the desired count, never return &lt; 1 or crash/overflow on an extreme capEdge.
/// perWorker = capEdge²×4×8 (2 GiB at 8192², 0.5 GiB at 4096²); budget = 0.75×availBytes − reserveBytes.
/// </summary>
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
        // 0.75×64 − 1 = 47 GiB / 2 GiB-per-worker = 23 → clamped to the desired 8.
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 64 * GiB, 1 * GiB, 8192), Is.EqualTo(8));

    [Test]
    public void TightRam_VlrgNativeCase_DegradesTo1()
        // 0.75×15 − 9 = 2.25 GiB / 2 = 1 → vlrg --cap 8192 on a 15 GiB box clamps mdop 4→1 (the OOM fix).
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(4, 15 * GiB, 9 * GiB, 8192), Is.EqualTo(1));

    [Test]
    public void ReserveExceedsBudget_Floors1()
        // reserve 9 > 0.75×10 = 7.5 → negative budget → must floor to 1 (never 0/negative).
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 10 * GiB, 9 * GiB, 8192), Is.EqualTo(1));

    [Test]
    public void MidPressure_DegradesProportionally()
        // 0.75×16 − 8 = 4 GiB / 2 = 2.
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 16 * GiB, 8 * GiB, 8192), Is.EqualTo(2));

    [Test]
    public void Cap4096CommonConfig_NotClamped()
        // 0.75×14 − 5 = 5.5 GiB / 0.5 = 11 → clamped to the desired 4 (the common production config is unaffected).
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(4, 14 * GiB, 5 * GiB, 4096), Is.EqualTo(4));

    [Test]
    public void ExtremeCapEdge_NoOverflow_Returns1()
        // capEdge clamps to 32768 → perWorker 32 GiB; 0.75×64 = 48 GiB / 32 = 1. No long overflow / negative.
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 64 * GiB, 0, 1_000_000), Is.EqualTo(1));
}

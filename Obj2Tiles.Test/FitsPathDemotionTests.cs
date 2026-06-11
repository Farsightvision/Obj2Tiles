using NUnit.Framework;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

/// <summary>
/// Pins the G14 fits-path demotion (the dev-env Phase-1 strangulation fix).
///
/// On the fits/pre-decode path the start clamp must reserve the whole resident set
/// out of live RAM. At envelopes where the set barely fits its budget — the dev
/// 28300Mi pod: GC avail 21225 MiB, est resident 11840 MiB ≤ budget 12735 MiB —
/// that reserve eats the worker headroom: the clamp returns mdop 1..3 (vs desired 7),
/// and at 1 the runParallel gate forced fully-SERIAL Phase-1 with per-material
/// re-decode churn. HLOD baked slower than legacy flat-grid (dev: legacy 2h46m,
/// HLOD unfinished >3h40m) while 5+ CPUs idled. Holding the set is only worth it
/// when meaningful parallelism remains — otherwise demote to the transient-eviction
/// path (reserve=0 → full DOP; the empirically validated never-OOM machinery).
/// Demotion decides from start-time signals only: no post-allocation GC sampling
/// (MemoryLoadBytes is a last-GC snapshot), no optimistic per-worker estimates for
/// a held-resident loop (Codex review items).
///
/// Contract: demote iff fitsClampedMdop &lt; max(2, desiredMdop / 2).
/// </summary>
public class FitsPathDemotionTests
{
    private const long MiB = 1L << 20;

    [Test]
    public void DevPod28GiB_TypicalLoad_ClampsTo1_Demotes()
    {
        // Live ≈ 16.5 GiB after mesh/tree load: 0.75×16896 − 11840 = 832 / 512 → 1.
        // This is the observed dev degenerate (serial + per-material re-decode churn).
        int fitsMdop = HierarchicalTilingStage.ClampWorkersToMemory(
            7, 16896 * MiB, 11840 * MiB, 4096);
        Assert.That(fitsMdop, Is.EqualTo(1));
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, fitsMdop), Is.True,
            "dev envelope must demote to the transient-eviction path");
    }

    [Test]
    public void DevPod28GiB_LightLoad_ClampsTo3_KeepsDecodeOnce()
    {
        // Light startup load → live ≈ 18 GiB → 0.75×18432 − 11840 = 1984 / 512 = 3.
        // 3 ≥ max(2, 7/2)=3 → keep decode-once at mdop 3 (break-even by CPU math,
        // and it avoids the ~2500 re-decodes of the transient path).
        int fitsMdop = HierarchicalTilingStage.ClampWorkersToMemory(
            7, 18432 * MiB, 11840 * MiB, 4096);
        Assert.That(fitsMdop, Is.EqualTo(3));
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, fitsMdop), Is.False);
    }

    [Test]
    public void RoomyRam_KeepsDecodeOnceFitsPath()
    {
        // 64 GiB-class box: 0.75×49152 − 11840 = 25024 / 512 = 48 → desired 7 → keep.
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
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, 2), Is.True);   // 2 < 3
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(7, 3), Is.False);  // 3 ≥ 3
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(8, 3), Is.True);   // 3 < 4
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(8, 4), Is.False);
    }

    [Test]
    public void SmallDesired_ClampTo1_StillDemotes()
    {
        // desired 2, clamp 1 → 1 < max(2, 1)=2 → demote (serial fits is never the
        // best schedule); clamp 2 == desired → keep.
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(2, 1), Is.True);
        Assert.That(HierarchicalTilingStage.ShouldDemoteFitsPath(2, 2), Is.False);
    }
}

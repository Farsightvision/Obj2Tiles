using NUnit.Framework;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

public class RamClampHardeningTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    [Test]
    public void EffectiveClampEdge_CacheOn_UsesResidentCap()
        => Assert.That(HierarchicalTilingStage.EffectiveClampEdge(8192, 4096), Is.EqualTo(8192));

    [Test]
    public void EffectiveClampEdge_CacheOff_FallsBackToAtlasCap()
        => Assert.That(HierarchicalTilingStage.EffectiveClampEdge(0, 4096), Is.EqualTo(4096));

    [Test]
    public void EffectiveClampEdge_NeitherSet_FallsBackToDefault()
        => Assert.That(HierarchicalTilingStage.EffectiveClampEdge(0, 0), Is.EqualTo(4096));

    [Test]
    public void TexturePerWorkerBytes_Diffuse_IsOneRgbaBufferTimesEight()
        => Assert.That(HierarchicalTilingStage.TexturePerWorkerBytes(4096, hasNormalMaps: false),
                       Is.EqualTo(4096L * 4096 * 4 * 8));

    [Test]
    public void TexturePerWorkerBytes_NormalMaps_RaiseEstimateAboveDiffuse()
        => Assert.That(HierarchicalTilingStage.TexturePerWorkerBytes(4096, hasNormalMaps: true),
                       Is.GreaterThan(HierarchicalTilingStage.TexturePerWorkerBytes(4096, hasNormalMaps: false)));

    [Test]
    public void TexturePerWorkerBytes_ClampsEdgeToAvoidOverflow()
        => Assert.That(HierarchicalTilingStage.TexturePerWorkerBytes(1_000_000, hasNormalMaps: false),
                       Is.EqualTo(32768L * 32768 * 4 * 8));

    [Test]
    public void ClampWorkersToBudget_RoomyRam_ReturnsDesired()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToBudget(8, 64 * GiB, 0, 256 * MiB), Is.EqualTo(8));

    [Test]
    public void ClampWorkersToBudget_TightRam_DegradesProportionally()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToBudget(8, 4 * GiB, 0, 1 * GiB), Is.EqualTo(3));

    [Test]
    public void ClampWorkersToBudget_NoPerWorkerEstimate_ReturnsDesired()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToBudget(8, 4 * GiB, 0, 0), Is.EqualTo(8));

    [Test]
    public void ClampWorkersToBudget_ReserveExceedsBudget_Floors1()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToBudget(8, 4 * GiB, 5 * GiB, 256 * MiB), Is.EqualTo(1));

    [Test]
    public void CacheOff_RawCapEdgeZero_StaysUnclamped_DefensiveGuard()
        => Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 3 * GiB, 0, 0), Is.EqualTo(8));

    [Test]
    public void CacheOff_ResolvedEdge_EngagesClamp()
    {
        int edge = HierarchicalTilingStage.EffectiveClampEdge(maxResidentEdge: 0, maxAtlasSize: 4096);
        Assert.That(HierarchicalTilingStage.ClampWorkersToMemory(8, 3 * GiB, 0, edge), Is.EqualTo(4));
    }

    [Test]
    public void GeometryDepths_ManyFaces_ClampUnderTightRam()
    {
        long faces = 8_000_000;
        long perDepth = faces * HierarchicalTilingStage.GeomSimplifyBytesPerFace;
        int clamped = HierarchicalTilingStage.ClampWorkersToBudget(6, 4 * GiB, 0, perDepth);
        Assert.That(clamped, Is.LessThan(6));
        Assert.That(clamped, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void GeometryDepths_RoomyRam_RunAllConcurrently()
    {
        long faces = 200_000;
        long perDepth = faces * HierarchicalTilingStage.GeomSimplifyBytesPerFace;
        Assert.That(HierarchicalTilingStage.ClampWorkersToBudget(6, 64 * GiB, 0, perDepth), Is.EqualTo(6));
    }
}

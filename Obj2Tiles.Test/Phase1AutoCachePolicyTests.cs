using NUnit.Framework;
using Obj2Tiles.Stages.Model;

namespace Obj2Tiles.Test;

[TestFixture]
public class Phase1AutoCachePolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Test]
    public void ShouldAutoEnable_OomModel_True()
    {
        // 185 x 8192^2 x 4 = ~46 GiB decoded vs a 4 GiB pod => auto-enable.
        long decoded = 185L * 8192 * 8192 * 4;
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: true, userCap: 0,
            decodedTextureBytes: decoded, availableBytes: 4 * GiB);
        Assert.That(on, Is.True);
    }

    [Test]
    public void ShouldAutoEnable_SmallModel_False()
    {
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: true, userCap: 0,
            decodedTextureBytes: 200L * 1024 * 1024, availableBytes: 4 * GiB);
        Assert.That(on, Is.False);
    }

    [Test]
    public void ShouldAutoEnable_UserCapSet_ExplicitFlagWins_False()
    {
        long decoded = 185L * 8192 * 8192 * 4;
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: true, userCap: 4096,
            decodedTextureBytes: decoded, availableBytes: 4 * GiB);
        Assert.That(on, Is.False);
    }

    [Test]
    public void ShouldAutoEnable_NotHierarchical_False()
    {
        long decoded = 185L * 8192 * 8192 * 4;
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: false, userCap: 0,
            decodedTextureBytes: decoded, availableBytes: 4 * GiB);
        Assert.That(on, Is.False);
    }

    [Test]
    public void ShouldAutoEnable_UnknownMemory_False()
    {
        // GC reported 0/unknown — cannot size a budget, do not surprise the bake.
        long decoded = 185L * 8192 * 8192 * 4;
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: true, userCap: 0,
            decodedTextureBytes: decoded, availableBytes: 0);
        Assert.That(on, Is.False);
    }

    [Test]
    public void ShouldAutoEnable_AtBoundary_False()
    {
        // decoded == avail * fraction => strict greater-than, so no trip.
        long avail = 8 * GiB;
        long decoded = 4 * GiB; // exactly 0.5 * avail
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: true, userCap: 0,
            decodedTextureBytes: decoded, availableBytes: avail, fraction: 0.5);
        Assert.That(on, Is.False);
    }
}

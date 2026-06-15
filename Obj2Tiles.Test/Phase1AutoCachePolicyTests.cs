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
        long decoded = 185L * 8192 * 8192 * 4;
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: true, userCap: 0,
            decodedTextureBytes: decoded, availableBytes: 0);
        Assert.That(on, Is.False);
    }

    [Test]
    public void ShouldAutoEnable_AtBoundary_False()
    {
        long avail = 8 * GiB;
        long decoded = 4 * GiB;
        bool on = Phase1AutoCachePolicy.ShouldAutoEnable(
            hierarchicalLods: true, userCap: 0,
            decodedTextureBytes: decoded, availableBytes: avail, fraction: 0.5);
        Assert.That(on, Is.False);
    }
}

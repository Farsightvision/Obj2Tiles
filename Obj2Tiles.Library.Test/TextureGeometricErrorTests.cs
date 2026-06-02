using NUnit.Framework;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

/// <summary>
/// Pins the TEXGE-V3 texture-resolution geometric-error formula (the RC's headline LOD-selection
/// feature): textureGE = metersPerTexel × (maxSse / pMax), with a fixed-factor fallback when pMax ≤ 0.
/// Extracted from Program.ApplyTextureAwareGeometricError so it is unit-testable; these tests would
/// catch a regression in the Nyquist relationship or the fallback that the bake md5 gate cannot localise.
/// </summary>
public class TextureGeometricErrorTests
{
    [Test]
    public void SseFactor_PositivePMax_IsMaxSseOverPMax()
    {
        Assert.That(TextureGeometricError.SseFactor(16.0, 0.5, fallbackFactor: 99.0), Is.EqualTo(32.0).Within(1e-12));
        Assert.That(TextureGeometricError.SseFactor(8.0, 0.5, fallbackFactor: 99.0), Is.EqualTo(16.0).Within(1e-12));
    }

    [Test]
    public void SseFactor_NyquistDefault_DoublesMaxSse()
    {
        // pMax = 0.5 (Nyquist, the shipped default) → factor = maxSse / 0.5 = 2 × maxSse.
        Assert.That(TextureGeometricError.SseFactor(16.0, 0.5, 1.0), Is.EqualTo(2.0 * 16.0).Within(1e-12));
    }

    [Test]
    public void SseFactor_NonPositivePMax_FallsBackToFixedFactor()
    {
        Assert.That(TextureGeometricError.SseFactor(16.0, 0.0, fallbackFactor: 16.0), Is.EqualTo(16.0).Within(1e-12));
        Assert.That(TextureGeometricError.SseFactor(16.0, -1.0, fallbackFactor: 7.5), Is.EqualTo(7.5).Within(1e-12));
    }

    [Test]
    public void FromTexelDensity_IsTexelDensityTimesSseFactor()
    {
        // textureGE = metersPerTexel × (maxSse/pMax) = 0.1 × 32 = 3.2.
        Assert.That(TextureGeometricError.FromTexelDensity(0.1, 16.0, 0.5, 99.0), Is.EqualTo(3.2).Within(1e-12));
    }

    [Test]
    public void FromTexelDensity_NonPositivePMax_UsesFallbackFactor()
    {
        // pMax ≤ 0 → factor = fallback; 0.5 × 16 = 8.0.
        Assert.That(TextureGeometricError.FromTexelDensity(0.5, 16.0, 0.0, 16.0), Is.EqualTo(8.0).Within(1e-12));
    }

    [Test]
    public void FromTexelDensity_MatchesManualProductOfSseFactor()
    {
        double mpt = 0.0123, maxSse = 16.0, pMax = 0.5, fb = 16.0;
        double expected = mpt * TextureGeometricError.SseFactor(maxSse, pMax, fb);
        Assert.That(TextureGeometricError.FromTexelDensity(mpt, maxSse, pMax, fb), Is.EqualTo(expected).Within(1e-15));
    }
}

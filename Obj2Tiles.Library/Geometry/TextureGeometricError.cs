namespace Obj2Tiles.Library.Geometry;

/// <summary>
/// Geometric error (world meters) at which a tile's texel density reaches <c>pMax</c>
/// screen-pixels-per-texel, so the renderer refines a tile whose texture is too coarse for the screen.
/// </summary>
public static class TextureGeometricError
{
    /// <summary>
    /// Scales meters-per-texel into a geometric error: <c>maxSse / pMax</c>, or
    /// <paramref name="fallbackFactor"/> when <paramref name="pMax"/> ≤ 0.
    /// </summary>
    public static double SseFactor(double maxSse, double pMax, double fallbackFactor)
        => pMax > 0 ? maxSse / pMax : fallbackFactor;

    public static double FromTexelDensity(double metersPerTexel, double maxSse, double pMax, double fallbackFactor)
        => metersPerTexel * SseFactor(maxSse, pMax, fallbackFactor);
}

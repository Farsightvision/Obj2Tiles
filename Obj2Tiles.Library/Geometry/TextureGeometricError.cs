namespace Obj2Tiles.Library.Geometry;

/// <summary>
/// TEXGE-V3 texture-resolution geometric error (extracted from the HLOD pipeline so it is unit-testable).
///
/// The geometric error, in world meters, at which a tile's texel density projects to <c>pMax</c>
/// screen-pixels-per-texel at the renderer's refinement distance — i.e. the GE that makes the renderer
/// refine a tile when its TEXTURE (not just its mesh) is too coarse for the screen, at the default
/// maxScreenSpaceError. The pipeline takes <c>effective = max(meshGE, textureGE)</c> so the binding
/// constraint (mesh deviation OR texture deficit) drives LOD selection.
///
/// Formula: <c>textureGE = metersPerTexel × (maxSse / pMax)</c>. Dimensional check (Qg57):
/// metersPerTexel [m/texel] × (screen-px ÷ screen-px/texel) collapses to meters. <c>pMax</c> is the one
/// principled dial (Nyquist 0.5 screen-px/texel). When <c>pMax ≤ 0</c> the factor falls back to the
/// legacy fixed <c>--texture-error-factor</c>.
/// </summary>
public static class TextureGeometricError
{
    /// <summary>
    /// The screen-space factor that scales meters-per-texel into a geometric error:
    /// <c>maxSse / pMax</c>, or <paramref name="fallbackFactor"/> when <paramref name="pMax"/> ≤ 0.
    /// </summary>
    public static double SseFactor(double maxSse, double pMax, double fallbackFactor)
        => pMax > 0 ? maxSse / pMax : fallbackFactor;

    /// <summary>
    /// textureGE = <paramref name="metersPerTexel"/> × <see cref="SseFactor"/>. The pipeline only calls
    /// this with a positive texel density (it guards worldExtent/atlasSide &gt; 0 upstream).
    /// </summary>
    public static double FromTexelDensity(double metersPerTexel, double maxSse, double pMax, double fallbackFactor)
        => metersPerTexel * SseFactor(maxSse, pMax, fallbackFactor);
}

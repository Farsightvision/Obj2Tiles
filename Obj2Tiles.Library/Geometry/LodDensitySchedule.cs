namespace Obj2Tiles.Library.Geometry;

/// <summary>
/// HLOD per-LOD texel-density schedule (extracted so it has ONE definition + unit tests; was
/// duplicated across PredictAtlasSide, ExtendAdaptiveImpl, and the atlas-area sizer).
///
/// Target texel density (pixels per world meter) for a tile at a given depth: it halves per step
/// UP from the leaf reference — coarser parents need fewer texels — so
/// <c>r_d = leafDensity / 2^(referenceDepth - depth)</c>. Atlas sizing squares it (r_d² = target px²/m²).
///
/// Implemented as an integer shift (2^k), clamped to k ≤ 16 to avoid integer-shift collapse for
/// unrealistically deep trees. Depths at or below the reference (referenceDepth - depth ≤ 0, e.g.
/// adaptively-deepened cells past the natural leaf) get the full leaf density.
/// </summary>
public static class LodDensitySchedule
{
    /// <summary>
    /// r_d = leafDensity / 2^clamp(referenceDepth - depth, 0, 16). Returns leafDensity when
    /// depth ≥ referenceDepth (the up-shift clamps to 0).
    /// </summary>
    public static double DensityAtDepth(double leafDensity, int referenceDepth, int depth)
    {
        int upShift = Math.Min(Math.Max(0, referenceDepth - depth), 16);
        return leafDensity / (double)(1 << upShift);
    }
}

namespace Obj2Tiles.Library.Geometry;

/// <summary>HLOD per-LOD texel-density schedule (pixels per world meter).</summary>
public static class LodDensitySchedule
{
    /// <summary>
    /// Target density at a depth: leafDensity halved per step up from the leaf reference,
    /// i.e. leafDensity / 2^clamp(referenceDepth - depth, 0, 16). Returns leafDensity when
    /// depth ≥ referenceDepth.
    /// </summary>
    public static double DensityAtDepth(double leafDensity, int referenceDepth, int depth)
    {
        int upShift = Math.Min(Math.Max(0, referenceDepth - depth), 16);
        return leafDensity / (double)(1 << upShift);
    }
}

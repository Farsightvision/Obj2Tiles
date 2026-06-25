using System;

namespace Obj2Tiles.Library.Geometry;

public static class ClusterDensity
{
    public static int NeededAtlasEdge(int clusterCount)
    {
        if (clusterCount <= 0)
            return 0;
        int g = MeshT_Hlod.EffectiveGutterPixels(clusterCount);
        double floorEdge = 1 + 2 * g;
        double minArea = clusterCount * floorEdge * floorEdge / 0.5;
        return Common.NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(minArea)));
    }

    public static int MaxClustersForCeiling(int ceiling)
    {
        if (ceiling <= 0)
            return int.MaxValue;
        int lo = 1, hi = 1;
        while (hi < 1_000_000_000 && NeededAtlasEdge(hi) <= ceiling)
        {
            lo = hi;
            hi *= 2;
        }
        while (lo + 1 < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (NeededAtlasEdge(mid) <= ceiling) lo = mid; else hi = mid;
        }
        return lo;
    }
}

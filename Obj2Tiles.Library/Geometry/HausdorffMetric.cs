using System.Collections.Generic;
using System.Linq;

namespace Obj2Tiles.Library.Geometry;

public static class HausdorffMetric
{
    /// <summary>
    /// One-direction Hausdorff: max distance from any original vertex to the
    /// surface of the simplified mesh. Per spec §6.1, this direction is
    /// required because the simplifier may delete features entirely.
    /// </summary>
    public static double Compute(
        IReadOnlyList<Vertex3> originalVerts,
        IReadOnlyList<Vertex3> simplifiedVerts,
        IReadOnlyList<Face> simplifiedFaces)
    {
        if (simplifiedFaces.Count == 0) return 0.0;
        var bvh = new TriangleBvh(simplifiedVerts, simplifiedFaces);
        return MaxNearestDistance(bvh, originalVerts, 1);
    }

    private const int ParallelSampleThreshold = 8192;

    /// <summary>
    /// G11: max of BVH nearest-point distance over the strided sample set
    /// {0, stride, 2·stride, …}. For giant shallow nodes (sample count ≥
    /// <see cref="ParallelSampleThreshold"/>) the loop is parallelized — those few
    /// nodes were single-core-bound under the per-node Parallel.ForEach and dominated
    /// the geomerr wall. OUTPUT-IDENTICAL: each strided index is visited exactly once
    /// (partition p handles {p·stride, (p+P)·stride, …}; their union is the serial set)
    /// and FP max is order-independent (no summation). Small nodes stay serial to avoid
    /// nested-parallel overhead. TriangleBvh is read-only after construction and
    /// NearestPointDistance allocates a local stack, so concurrent queries are safe.
    /// </summary>
    private static double MaxNearestDistance(TriangleBvh bvh, IReadOnlyList<Vertex3> verts, int stride)
    {
        int total = verts.Count;
        int sampleCount = (total + stride - 1) / stride;
        if (sampleCount < ParallelSampleThreshold)
        {
            double m = 0;
            for (int i = 0; i < total; i += stride)
            {
                double d = bvh.NearestPointDistance(verts[i]);
                if (d > m) m = d;
            }
            return m;
        }
        int P = System.Environment.ProcessorCount;
        var locals = new double[P];
        System.Threading.Tasks.Parallel.For(0, P, p =>
        {
            double m = 0;
            for (long i = (long)p * stride; i < total; i += (long)stride * P)
            {
                double d = bvh.NearestPointDistance(verts[(int)i]);
                if (d > m) m = d;
            }
            locals[p] = m;
        });
        double max = 0;
        for (int p = 0; p < P; p++) if (locals[p] > max) max = locals[p];
        return max;
    }

    /// <summary>
    /// Vertex-subsampled variant for performance on large meshes (per §10 risk).
    /// Deterministic stride sampling: every k-th vertex where k = ceil(N / maxSamples).
    /// Simpler than reservoir sampling; the exact sample distribution doesn't matter
    /// because we're computing the max over the sample set.
    /// </summary>
    public static double ComputeSampled(
        IReadOnlyList<Vertex3> originalVerts,
        IReadOnlyList<Vertex3> simplifiedVerts,
        IReadOnlyList<Face> simplifiedFaces,
        int maxSamples = 50_000)
    {
        if (originalVerts.Count <= maxSamples)
            return Compute(originalVerts, simplifiedVerts, simplifiedFaces);
        var bvh = new TriangleBvh(simplifiedVerts, simplifiedFaces);
        int stride = (originalVerts.Count + maxSamples - 1) / maxSamples;
        return MaxNearestDistance(bvh, originalVerts, stride);
    }

    /// <summary>
    /// Monotonicity correction (spec §6.1 step 4): error must be strictly greater
    /// than every child's error.
    ///
    /// ε = 1e-3 × scene diagonal. The old 1e-6 floor is below FP noise on
    /// photogrammetry meshes — adjacent same-depth tiles whose measured errors
    /// differed only in the FP-noise digits ended up with effectively equal
    /// parent errors, which the renderer then displayed at mismatched LOD.
    /// 1e-3 × diag guarantees the minimum parent step dominates measurement
    /// noise (e.g. ~10 cm on a 100 m scene, ~1.4 m on a 1400 m scene). The
    /// SSE swap-distance shift this introduces is sqrt((E+ε)/E), negligible
    /// when measured E >> ε at coarse depths.
    /// </summary>
    public static double MonotonicCorrection(double measured, IEnumerable<double> childErrors, double sceneDiagonal)
    {
        double maxChild = childErrors.DefaultIfEmpty(0).Max();
        double eps = 1e-3 * sceneDiagonal;
        return System.Math.Max(measured, maxChild + eps);
    }
}

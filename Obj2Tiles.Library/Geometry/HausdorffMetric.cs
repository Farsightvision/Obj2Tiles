using System.Collections.Generic;
using System.Linq;

namespace Obj2Tiles.Library.Geometry;

public static class HausdorffMetric
{
    /// <summary>
    /// One-direction Hausdorff: max distance from any original vertex to the
    /// surface of the simplified mesh.
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

    // Parallel partitioning is output-identical: each strided index is visited
    // exactly once and FP max is order-independent. BVH is read-only after
    // construction and queries allocate only a local stack, so this is safe.
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
    /// Vertex-subsampled variant for large meshes: every k-th vertex,
    /// k = ceil(N / maxSamples).
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
    /// Forces error strictly greater than every child's. ε = 1e-3 × scene
    /// diagonal keeps the parent step above FP measurement noise.
    /// </summary>
    public static double MonotonicCorrection(double measured, IEnumerable<double> childErrors, double sceneDiagonal)
    {
        double maxChild = childErrors.DefaultIfEmpty(0).Max();
        double eps = 1e-3 * sceneDiagonal;
        return System.Math.Max(measured, maxChild + eps);
    }
}

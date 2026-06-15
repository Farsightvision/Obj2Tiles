using System.Linq;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

public class TriangleBvhTests
{
    [Test]
    public void NearestPoint_OnTriangle_ReturnsZeroDistance()
    {
        var verts = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(0, 1, 0) };
        var faces = new[] { new Face(0, 1, 2) };
        var bvh = new TriangleBvh(verts, faces);
        var d = bvh.NearestPointDistance(new Vertex3(0.5, 0.0, 0.0));
        Assert.That(d, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void NearestPoint_AbovePlane_ReturnsHeight()
    {
        var verts = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(0, 1, 0) };
        var faces = new[] { new Face(0, 1, 2) };
        var bvh = new TriangleBvh(verts, faces);
        var d = bvh.NearestPointDistance(new Vertex3(0.25, 0.25, 0.5));
        Assert.That(d, Is.EqualTo(0.5).Within(1e-6));
    }

    // Zero-area triangles (repeated/collinear vertices) must never yield NaN/Inf or throw.
    [Test]
    public void NearestPoint_RepeatedVertexTriangle_IsFiniteAndNonNegative()
    {
        var verts = new[] { new Vertex3(0, 0, 0), new Vertex3(0, 0, 0), new Vertex3(1, 0, 0) };
        var faces = new[] { new Face(0, 1, 2) };
        var bvh = new TriangleBvh(verts, faces);
        Assert.That(bvh.NearestPointDistance(new Vertex3(0, 0, 0)), Is.EqualTo(0.0).Within(1e-9));
        var d = bvh.NearestPointDistance(new Vertex3(0.5, 0.0, 1.0));
        Assert.That(double.IsFinite(d), Is.True, "degenerate triangle must not yield NaN/Inf");
        Assert.That(d, Is.GreaterThanOrEqualTo(0.0));
    }

    [Test]
    public void NearestPoint_CollinearVertexTriangle_IsFiniteAndNonNegative()
    {
        var verts = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(2, 0, 0) };
        var faces = new[] { new Face(0, 1, 2) };
        var bvh = new TriangleBvh(verts, faces);
        Assert.That(bvh.NearestPointDistance(new Vertex3(1, 0, 0)), Is.EqualTo(0.0).Within(1e-9));
        var d = bvh.NearestPointDistance(new Vertex3(1.0, 0.0, 1.0));
        Assert.That(double.IsFinite(d), Is.True, "collinear triangle must not yield NaN/Inf");
        Assert.That(d, Is.GreaterThanOrEqualTo(0.0));
    }
}

public class HausdorffMetricTests
{
    [Test]
    public void Compute_ReturnsZeroForIdenticalMeshes()
    {
        var verts = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(0, 1, 0) };
        var faces = new[] { new Face(0, 1, 2) };
        var d = HausdorffMetric.Compute(originalVerts: verts, simplifiedVerts: verts, simplifiedFaces: faces);
        Assert.That(d, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void MonotonicityCorrection_GuaranteesStrictDecreaseTowardLeaves()
    {
        var childErrors = Enumerable.Range(0, 10).Select(i => (double)i).ToArray();
        double measured = 5.0;
        double sceneDiag = 1000.0;
        double e = HausdorffMetric.MonotonicCorrection(measured, childErrors, sceneDiag);
        Assert.That(childErrors.All(c => e > c), Is.True,
            $"e={e} must exceed every child error (max {childErrors.Max()})");
    }

    // Parent error sits ε = 1e-3 × sceneDiagonal above the largest child error.
    [Test]
    public void MonotonicCorrection_StepAboveMaxChild_EqualsOneThousandthOfSceneDiagonal()
    {
        double[] childErrors = { 5.0 };
        double sceneDiag = 1000.0;
        double e = HausdorffMetric.MonotonicCorrection(measured: 0.0, childErrors, sceneDiag);
        Assert.That(e, Is.EqualTo(5.0 + 1e-3 * sceneDiag).Within(1e-12));
        Assert.That(e - childErrors.Max(), Is.EqualTo(1e-3 * sceneDiag).Within(1e-12));
    }

    [Test]
    public void MonotonicCorrection_EpsilonScalesWithSceneDiagonal()
    {
        Assert.That(HausdorffMetric.MonotonicCorrection(0.0, new[] { 2.0 }, 100.0) - 2.0,
            Is.EqualTo(0.1).Within(1e-12));
        Assert.That(HausdorffMetric.MonotonicCorrection(0.0, new[] { 2.0 }, 1400.0) - 2.0,
            Is.EqualTo(1.4).Within(1e-12));
    }

    [Test]
    public void MonotonicCorrection_ReturnsMeasuredUnchanged_WhenItAlreadyDominates()
    {
        double e = HausdorffMetric.MonotonicCorrection(measured: 100.0, new[] { 5.0 }, sceneDiagonal: 1000.0);
        Assert.That(e, Is.EqualTo(100.0).Within(1e-12));
    }

    [Test]
    public void MonotonicCorrection_NoChildren_AppliesEpsilonFloorOverMeasured()
    {
        double e = HausdorffMetric.MonotonicCorrection(measured: 0.0, System.Array.Empty<double>(), sceneDiagonal: 1000.0);
        Assert.That(e, Is.EqualTo(1e-3 * 1000.0).Within(1e-12));
    }

    [Test]
    public void MonotonicCorrection_StepDominatesFpNoiseDifferenceBetweenSiblings()
    {
        double diag = 1000.0;
        double a = HausdorffMetric.MonotonicCorrection(3.0, new[] { 3.0 + 1e-9 }, diag);
        double b = HausdorffMetric.MonotonicCorrection(3.0, new[] { 3.0 + 2e-9 }, diag);
        Assert.That(a - (3.0 + 1e-9), Is.EqualTo(1e-3 * diag).Within(1e-9));
        Assert.That(System.Math.Abs(a - b), Is.LessThan(1e-6));
    }
}

public class HausdorffComputeSampledTests
{
    private static (Vertex3[] verts, Face[] faces) UnitTriangle() =>
        (new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(0, 1, 0) },
         new[] { new Face(0, 1, 2) });

    [Test]
    public void ComputeSampled_BelowMaxSamples_MatchesFullCompute()
    {
        var (sv, sf) = UnitTriangle();
        var original = new[] { new Vertex3(0.25, 0.25, 0.5), new Vertex3(0, 0, 0), new Vertex3(1, 0, 0) };
        double full = HausdorffMetric.Compute(original, sv, sf);
        double sampled = HausdorffMetric.ComputeSampled(original, sv, sf, maxSamples: 50_000);
        Assert.That(sampled, Is.EqualTo(full).Within(1e-9), "<= maxSamples must delegate to full Compute");
        Assert.That(sampled, Is.EqualTo(0.5).Within(1e-6));
    }

    [Test]
    public void ComputeSampled_AboveMaxSamples_FindsMaxLandingOnSampledStride()
    {
        var (sv, sf) = UnitTriangle();
        // maxSamples=2 over 4 verts strides to indices {0, 2}; the global max (0.9) sits at 0.
        var original = new[]
        {
            new Vertex3(0.25, 0.25, 0.9),
            new Vertex3(0.0, 0.0, 0.0),
            new Vertex3(0.25, 0.25, 0.5),
            new Vertex3(1.0, 0.0, 0.0),
        };
        double sampled = HausdorffMetric.ComputeSampled(original, sv, sf, maxSamples: 2);
        Assert.That(sampled, Is.EqualTo(0.9).Within(1e-6));
    }
}

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

    // Degenerate triangles (zero area: repeated or collinear vertices) occur in real
    // photogrammetry meshes. Pin the robustness contract: never NaN/Inf or throw; a point
    // coincident with the triangle gives distance 0.
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
        // 10 leaf children with errors 0..9
        var childErrors = Enumerable.Range(0, 10).Select(i => (double)i).ToArray();
        double measured = 5.0;
        double sceneDiag = 1000.0;
        double e = HausdorffMetric.MonotonicCorrection(measured, childErrors, sceneDiag);
        // e must exceed every child error
        Assert.That(childErrors.All(c => e > c), Is.True,
            $"e={e} must exceed every child error (max {childErrors.Max()})");
    }

    // The parent-error step must be ε = 1e-3 × sceneDiagonal above the largest child error,
    // NOT the old 1e-6 floor. This is the LOD-consistency fix: adjacent same-depth tiles whose
    // measured errors differ only in FP-noise digits must still get a parent step that DOMINATES
    // that noise, or the renderer displays them at mismatched LOD. These tests pin that contract
    // so a regression back to a noise-floor ε is caught.
    [Test]
    public void MonotonicCorrection_StepAboveMaxChild_EqualsOneThousandthOfSceneDiagonal()
    {
        double[] childErrors = { 5.0 };
        double sceneDiag = 1000.0;
        // measured (0) is below the children, so the result is driven by maxChild + ε.
        double e = HausdorffMetric.MonotonicCorrection(measured: 0.0, childErrors, sceneDiag);
        Assert.That(e, Is.EqualTo(5.0 + 1e-3 * sceneDiag).Within(1e-12)); // 5 + 1.0 = 6.0
        Assert.That(e - childErrors.Max(), Is.EqualTo(1e-3 * sceneDiag).Within(1e-12));
    }

    [Test]
    public void MonotonicCorrection_EpsilonScalesWithSceneDiagonal()
    {
        // ~10 cm step on a 100 m scene; ~1.4 m step on a 1400 m scene (examples from the spec note).
        Assert.That(HausdorffMetric.MonotonicCorrection(0.0, new[] { 2.0 }, 100.0) - 2.0,
            Is.EqualTo(0.1).Within(1e-12));
        Assert.That(HausdorffMetric.MonotonicCorrection(0.0, new[] { 2.0 }, 1400.0) - 2.0,
            Is.EqualTo(1.4).Within(1e-12));
    }

    [Test]
    public void MonotonicCorrection_ReturnsMeasuredUnchanged_WhenItAlreadyDominates()
    {
        // measured already exceeds maxChild + ε → returned as-is (correction is a floor, not a clamp).
        double e = HausdorffMetric.MonotonicCorrection(measured: 100.0, new[] { 5.0 }, sceneDiagonal: 1000.0);
        Assert.That(e, Is.EqualTo(100.0).Within(1e-12));
    }

    [Test]
    public void MonotonicCorrection_NoChildren_AppliesEpsilonFloorOverMeasured()
    {
        // Empty child set → maxChild defaults to 0, so result = max(measured, ε).
        double e = HausdorffMetric.MonotonicCorrection(measured: 0.0, System.Array.Empty<double>(), sceneDiagonal: 1000.0);
        Assert.That(e, Is.EqualTo(1e-3 * 1000.0).Within(1e-12)); // 1.0
    }

    [Test]
    public void MonotonicCorrection_StepDominatesFpNoiseDifferenceBetweenSiblings()
    {
        // Two sibling parents whose measured errors differ only in FP-noise digits: after correction
        // each sits ε above its (near-equal) child, so the ε step (1.0 on a 1000 m scene) dwarfs the
        // sub-micron measured difference — exactly the case the 1e-6 floor failed to separate.
        double diag = 1000.0;
        double a = HausdorffMetric.MonotonicCorrection(3.0, new[] { 3.0 + 1e-9 }, diag);
        double b = HausdorffMetric.MonotonicCorrection(3.0, new[] { 3.0 + 2e-9 }, diag);
        Assert.That(a - (3.0 + 1e-9), Is.EqualTo(1e-3 * diag).Within(1e-9));
        Assert.That(System.Math.Abs(a - b), Is.LessThan(1e-6)); // sibling parents stay effectively equal LOD
    }
}

public class HausdorffComputeSampledTests
{
    // Simplified reference surface: the unit triangle in the z=0 plane. A point above it
    // (projection inside the triangle) has nearest-distance == its height.
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
        // 4 verts, maxSamples=2 -> stride = ceil(4/2) = 2 -> samples indices {0, 2}. Put the global
        // max (height 0.9) at index 0 (sampled) and 0.5 at index 2 (sampled); the off-stride verts
        // sit on the surface. The strided max must equal 0.9.
        var original = new[]
        {
            new Vertex3(0.25, 0.25, 0.9), // idx 0 (sampled) — the max
            new Vertex3(0.0, 0.0, 0.0),   // idx 1 (skipped)
            new Vertex3(0.25, 0.25, 0.5), // idx 2 (sampled)
            new Vertex3(1.0, 0.0, 0.0),   // idx 3 (skipped)
        };
        double sampled = HausdorffMetric.ComputeSampled(original, sv, sf, maxSamples: 2);
        Assert.That(sampled, Is.EqualTo(0.9).Within(1e-6));
    }
}

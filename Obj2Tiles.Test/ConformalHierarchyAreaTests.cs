using NUnit.Framework;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

/// <summary>
/// Pins two correctness-critical pure predicates in ConformalHierarchyStage that were previously
/// untested: ComputeTileWorldArea (drives atlas sizing + the ExtendAdaptive deepen decision) and
/// ComputeTileTextureBytes (drives the PruneAdaptive collapse decision). Both are pure over a
/// ClipResultT, so they unit-test cleanly.
/// </summary>
public class ConformalHierarchyAreaTests
{
    [Test]
    public void ComputeTileWorldArea_RightTriangle_IsHalfBaseTimesHeight()
    {
        var tile = new ClipResultT
        {
            Vertices = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(0, 1, 0) },
            Faces = new[] { new MeshFace(0, 1, 2, 0, 0, 0, 0) },
        };
        Assert.That(ConformalHierarchyStage.ComputeTileWorldArea(tile), Is.EqualTo(0.5).Within(1e-12));
    }

    [Test]
    public void ComputeTileWorldArea_UnitSquareTwoTriangles_SumsToOne()
    {
        var tile = new ClipResultT
        {
            Vertices = new[]
            {
                new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(1, 1, 0), new Vertex3(0, 1, 0),
            },
            Faces = new[]
            {
                new MeshFace(0, 1, 2, 0, 0, 0, 0),
                new MeshFace(0, 2, 3, 0, 0, 0, 0),
            },
        };
        Assert.That(ConformalHierarchyStage.ComputeTileWorldArea(tile), Is.EqualTo(1.0).Within(1e-12));
    }

    [Test]
    public void ComputeTileWorldArea_DegenerateAndEmpty_AreZero()
    {
        var collinear = new ClipResultT
        {
            Vertices = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(2, 0, 0) },
            Faces = new[] { new MeshFace(0, 1, 2, 0, 0, 0, 0) },
        };
        Assert.That(ConformalHierarchyStage.ComputeTileWorldArea(collinear), Is.EqualTo(0.0).Within(1e-12));
        Assert.That(ConformalHierarchyStage.ComputeTileWorldArea(new ClipResultT()), Is.EqualTo(0.0).Within(1e-12));
    }

    [Test]
    public void ComputeTileTextureBytes_FullUvTriangle_IsUvAreaTimesMaterialBytes()
    {
        // UV triangle (0,0)-(1,0)-(0,1): uvArea = 0.5; material 0 = 1000 bytes -> 0.5 * 1000 = 500.
        var tile = new ClipResultT
        {
            TexVertices = new[] { new Vertex2(0, 0), new Vertex2(1, 0), new Vertex2(0, 1) },
            Faces = new[] { new MeshFace(0, 1, 2, 0, 1, 2, 0) },
        };
        Assert.That(ConformalHierarchyStage.ComputeTileTextureBytes(tile, new long[] { 1000 }),
            Is.EqualTo(500L));
    }

    [Test]
    public void ComputeTileTextureBytes_ZeroByteMaterialAndOutOfRangeIndex_AreSkipped()
    {
        var texVerts = new[] { new Vertex2(0, 0), new Vertex2(1, 0), new Vertex2(0, 1) };
        // Zero-byte material → contributes nothing.
        var zeroByte = new ClipResultT { TexVertices = texVerts, Faces = new[] { new MeshFace(0, 1, 2, 0, 1, 2, 0) } };
        Assert.That(ConformalHierarchyStage.ComputeTileTextureBytes(zeroByte, new long[] { 0 }), Is.EqualTo(0L));
        // Material index past the array → skipped (no crash).
        var badIndex = new ClipResultT { TexVertices = texVerts, Faces = new[] { new MeshFace(0, 1, 2, 0, 1, 2, 5) } };
        Assert.That(ConformalHierarchyStage.ComputeTileTextureBytes(badIndex, new long[] { 1000 }), Is.EqualTo(0L));
    }
}

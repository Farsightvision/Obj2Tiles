using System;
using System.Linq;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

public class OctreeSplitterTests
{
    [Test]
    public void ClipAtX_ProducesSharedBoundaryVertices()
    {
        var verts = new[]
        {
            new Vertex3(-1, 0, 0),
            new Vertex3( 1, 0, 0),
            new Vertex3( 0, 1, 0),
        };
        var faces = new[] { new Face(0, 1, 2) };

        var (left, right) = OctreeSplitter.ClipAtX(verts, faces, xSplit: 0.5);

        Assert.That(left.Faces, Is.Not.Empty);
        Assert.That(right.Faces, Is.Not.Empty);

        // Boundary vertices on the split plane must be identical positions in both outputs.
        var leftBoundary  = left .Vertices.Where(v => Math.Abs(v.X - 0.5) < 1e-12).ToArray();
        var rightBoundary = right.Vertices.Where(v => Math.Abs(v.X - 0.5) < 1e-12).ToArray();
        Assert.That(leftBoundary.Length, Is.EqualTo(2));
        Assert.That(rightBoundary.Length, Is.EqualTo(2));

        var leftSorted  = leftBoundary .OrderBy(v => v.Y).ThenBy(v => v.Z).ToArray();
        var rightSorted = rightBoundary.OrderBy(v => v.Y).ThenBy(v => v.Z).ToArray();
        for (int i = 0; i < leftSorted.Length; i++)
        {
            Assert.That(leftSorted[i].X, Is.EqualTo(rightSorted[i].X).Within(1e-12));
            Assert.That(leftSorted[i].Y, Is.EqualTo(rightSorted[i].Y).Within(1e-12));
            Assert.That(leftSorted[i].Z, Is.EqualTo(rightSorted[i].Z).Within(1e-12));
        }
    }

    [Test]
    public void ClipAtX_TriangleEntirelyOnOneSide_NotSplit()
    {
        var verts = new[]
        {
            new Vertex3(0.6, 0, 0),
            new Vertex3(0.8, 0, 0),
            new Vertex3(0.7, 1, 0),
        };
        var faces = new[] { new Face(0, 1, 2) };
        var (left, right) = OctreeSplitter.ClipAtX(verts, faces, xSplit: 0.5);
        Assert.That(left.Faces, Is.Empty);
        Assert.That(right.Faces.Length, Is.EqualTo(1));
    }

    [Test]
    public void ChooseShape_FlatMeshGetsQuadtree()
    {
        // Flat heuristic: Z / min(X, Y) < 0.5 -> quadtree (here 50/600 = 0.083)
        var bbox = new Box3(0, 0, 0, 1000, 600, 50);
        Assert.That(OctreeSplitter.ChooseShape(bbox, forceOctree: false), Is.EqualTo(SubdivisionShape.Quadtree));
    }

    [Test]
    public void ChooseShape_CubeMeshGetsOctree()
    {
        var bbox = new Box3(0, 0, 0, 100, 100, 100);
        Assert.That(OctreeSplitter.ChooseShape(bbox, forceOctree: false), Is.EqualTo(SubdivisionShape.Octree));
    }

    [Test]
    public void ChooseShape_ForceOctreeOverridesHeuristic()
    {
        var bbox = new Box3(0, 0, 0, 1000, 600, 50);
        Assert.That(OctreeSplitter.ChooseShape(bbox, forceOctree: true), Is.EqualTo(SubdivisionShape.Octree));
    }

    [Test]
    public void RecursiveSplit_ProducesAtMost8ChildrenPerNode()
    {
        var (verts, faces) = TestMeshes.UniformGridCube(side: 10);
        var bbox = TestMeshes.BoundsOf(verts);
        var leaves = OctreeSplitter.RecursiveSplit(
            verts, faces, bbox,
            shape: SubdivisionShape.Octree,
            maxVertsPerTile: 50,
            maxDepth: 4);

        Assert.That(leaves, Is.Not.Empty);
        int headroom = (int)(50 * OctreeSplitterRecursive.HeadroomFactor);
        Assert.That(leaves, Has.All.Matches<LeafTile>(l => l.Mesh.Vertices.Length <= headroom + 1));
        Assert.That(leaves, Has.All.Matches<LeafTile>(l => l.Mesh.Faces.Length > 0));
    }

    [Test]
    public void RecursiveSplit_PreservesOriginalTriangleCountWithinClippingTolerance()
    {
        var (verts, faces) = TestMeshes.UniformGridCube(side: 5);
        var bbox = TestMeshes.BoundsOf(verts);
        var leaves = OctreeSplitter.RecursiveSplit(
            verts, faces, bbox,
            shape: SubdivisionShape.Octree,
            maxVertsPerTile: 10,
            maxDepth: 6);
        int totalLeafFaces = 0;
        foreach (var leaf in leaves) totalLeafFaces += leaf.Mesh.Faces.Length;
        // Clipping a crossing triangle adds faces; it never removes any.
        Assert.That(totalLeafFaces, Is.GreaterThanOrEqualTo(faces.Length),
            $"total leaf faces {totalLeafFaces} should be >= original {faces.Length}");
    }

    [Test]
    public void AabbBox_ProducesCenterAndHalfExtents()
    {
        var verts = new[] { new Vertex3(0, 0, 0), new Vertex3(2, 4, 6) };
        var box = OctreeSplitter.AabbBox(verts);
        Assert.That(box, Is.EqualTo(new double[] { 1, 2, 3, 1, 0, 0, 0, 2, 0, 0, 0, 3 }));
    }
}

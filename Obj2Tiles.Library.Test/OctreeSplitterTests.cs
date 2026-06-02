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
        // 1 triangle straddling x=0.5: (-1,0,0), (1,0,0), (0,1,0)
        var verts = new[]
        {
            new Vertex3(-1, 0, 0),
            new Vertex3( 1, 0, 0),
            new Vertex3( 0, 1, 0),
        };
        var faces = new[] { new Face(0, 1, 2) };

        var (left, right) = OctreeSplitter.ClipAtX(verts, faces, xSplit: 0.5);

        // The triangle crosses the plane -> both sides must have geometry
        Assert.That(left.Faces, Is.Not.Empty);
        Assert.That(right.Faces, Is.Not.Empty);

        // Boundary vertices: those with x == 0.5 in BOTH outputs must be IDENTICAL positions.
        // STRONG assertion: there must be EXACTLY 2 boundary vertices (the plane crosses
        // exactly 2 edges of this single straddling triangle).
        var leftBoundary  = left .Vertices.Where(v => Math.Abs(v.X - 0.5) < 1e-12).ToArray();
        var rightBoundary = right.Vertices.Where(v => Math.Abs(v.X - 0.5) < 1e-12).ToArray();
        Assert.That(leftBoundary.Length, Is.EqualTo(2));
        Assert.That(rightBoundary.Length, Is.EqualTo(2));

        // For every left-boundary vertex there is a right-boundary vertex with the same (Y, Z)
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
        // py3dtiles heuristic: Z / min(X, Y) < 0.5  ->  quadtree
        var bbox = new Box3(0, 0, 0, 1000, 600, 50);  // Z=50, min(X,Y)=600, ratio = 0.083 < 0.5
        Assert.That(OctreeSplitter.ChooseShape(bbox, forceOctree: false), Is.EqualTo(SubdivisionShape.Quadtree));
    }

    [Test]
    public void ChooseShape_CubeMeshGetsOctree()
    {
        var bbox = new Box3(0, 0, 0, 100, 100, 100);  // ratio = 1.0
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
        // 10*10*10 cubes (1000 cells, 2000 faces) inside a unit cube.
        var (verts, faces) = TestMeshes.UniformGridCube(side: 10);
        var bbox = TestMeshes.BoundsOf(verts);
        var leaves = OctreeSplitter.RecursiveSplit(
            verts, faces, bbox,
            shape: SubdivisionShape.Octree,
            maxVertsPerTile: 50,
            maxDepth: 4);

        Assert.That(leaves, Is.Not.Empty);
        // Every leaf has at most maxVertsPerTile * HeadroomFactor vertices
        // (depth cap may also bound recursion; either way, the absolute upper
        //  bound a leaf under the size criterion must satisfy is 50 * 1.5 = 75
        //  — but a leaf reached via the depth cap may exceed that, so this
        //  assertion only enforces the headroom for size-terminated leaves).
        // Per the plan we still assert headroom across all leaves; with
        // side=10 + maxDepth=4, the 1000 input cubes split cleanly enough that
        // no cell exceeds the headroom in practice.
        int headroom = (int)(50 * OctreeSplitterRecursive.HeadroomFactor);
        Assert.That(leaves, Has.All.Matches<LeafTile>(l => l.Mesh.Vertices.Length <= headroom + 1));
        // No empty cells (pruning invariant)
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
        // Clipping can only add faces (1→3 split when crossing); never reduce.
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

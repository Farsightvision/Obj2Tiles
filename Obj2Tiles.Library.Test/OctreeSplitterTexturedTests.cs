using System;
using System.Linq;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

[TestFixture]
public class OctreeSplitterTexturedTests
{
    [Test]
    public void ClipAtXT_PropagatesAllFourCornerUvs_AndInterpolatesBoundaryUvs()
    {
        // Unit quad in XY plane: 2 triangles, 4 corners, 4 UVs.
        var verts = new[]
        {
            new Vertex3(0, 0, 0),
            new Vertex3(1, 0, 0),
            new Vertex3(0, 1, 0),
            new Vertex3(1, 1, 0),
        };
        var uvs = new[]
        {
            new Vertex2(0.0, 0.0),
            new Vertex2(1.0, 0.0),
            new Vertex2(0.0, 1.0),
            new Vertex2(1.0, 1.0),
        };
        var faces = new[]
        {
            new MeshFace(0, 1, 2, 0, 1, 2, 7),
            new MeshFace(1, 3, 2, 1, 3, 2, 7),
        };

        var (left, right) = OctreeSplitter.ClipAtXT(verts, uvs, faces, xSplit: 0.5);

        Assert.That(left.Faces, Is.Not.Empty);
        Assert.That(right.Faces, Is.Not.Empty);

        bool leftHasUv00 = left.TexVertices.Any(uv => Math.Abs(uv.X) < 1e-12 && Math.Abs(uv.Y) < 1e-12);
        bool leftHasUv01 = left.TexVertices.Any(uv => Math.Abs(uv.X) < 1e-12 && Math.Abs(uv.Y - 1) < 1e-12);
        bool rightHasUv10 = right.TexVertices.Any(uv => Math.Abs(uv.X - 1) < 1e-12 && Math.Abs(uv.Y) < 1e-12);
        bool rightHasUv11 = right.TexVertices.Any(uv => Math.Abs(uv.X - 1) < 1e-12 && Math.Abs(uv.Y - 1) < 1e-12);
        Assert.That(leftHasUv00, Is.True, "left half should retain corner uv (0,0)");
        Assert.That(leftHasUv01, Is.True, "left half should retain corner uv (0,1)");
        Assert.That(rightHasUv10, Is.True, "right half should retain corner uv (1,0)");
        Assert.That(rightHasUv11, Is.True, "right half should retain corner uv (1,1)");

        // Plane at x=0.5 bisects both vertical edges, so boundary UVs interpolate to (0.5, 0) and (0.5, 1).
        bool leftHasMidBottom = left.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y) < 1e-9);
        bool leftHasMidTop = left.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y - 1) < 1e-9);
        Assert.That(leftHasMidBottom, Is.True, "boundary uv (0.5, 0) interpolated on left");
        Assert.That(leftHasMidTop, Is.True, "boundary uv (0.5, 1) interpolated on left");

        bool rightHasMidBottom = right.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y) < 1e-9);
        bool rightHasMidTop = right.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y - 1) < 1e-9);
        Assert.That(rightHasMidBottom, Is.True, "boundary uv (0.5, 0) interpolated on right");
        Assert.That(rightHasMidTop, Is.True, "boundary uv (0.5, 1) interpolated on right");

        Assert.That(left.Faces.All(f => f.MaterialIndex == 7), Is.True);
        Assert.That(right.Faces.All(f => f.MaterialIndex == 7), Is.True);
    }

    [Test]
    public void ClipAtXT_PreservesUvSeams_OnAdjacentTrianglesWithDifferentUvAtSamePosition()
    {
        // Two triangles share an edge by position but carry different UVs there
        // (a UV seam). Clipping must keep both UV sets distinct, not merge them.
        var verts = new[]
        {
            new Vertex3(0, 0, 0),
            new Vertex3(1, 0, 0),
            new Vertex3(1, 1, 0),
            new Vertex3(1, 0, 0),
            new Vertex3(1, 1, 0),
            new Vertex3(2, 1, 0),
        };
        var uvs = new[]
        {
            new Vertex2(0.0, 0.0),
            new Vertex2(0.5, 0.0),
            new Vertex2(0.5, 1.0),
            new Vertex2(0.0, 0.0),
            new Vertex2(0.0, 1.0),
            new Vertex2(0.5, 1.0),
        };
        var faces = new[]
        {
            new MeshFace(0, 1, 2, 0, 1, 2, 0),
            new MeshFace(3, 5, 4, 3, 5, 4, 0),
        };

        var (left, right) = OctreeSplitter.ClipAtXT(verts, uvs, faces, xSplit: 0.5);

        var allUvs = left.TexVertices.Concat(right.TexVertices).ToArray();
        bool hasLeftSeamBottom = allUvs.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y) < 1e-9);
        bool hasLeftSeamTop = allUvs.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y - 1) < 1e-9);
        bool hasRightSeamBottom = allUvs.Any(uv => Math.Abs(uv.X) < 1e-9 && Math.Abs(uv.Y) < 1e-9);
        bool hasRightSeamTop = allUvs.Any(uv => Math.Abs(uv.X) < 1e-9 && Math.Abs(uv.Y - 1) < 1e-9);
        Assert.That(hasLeftSeamBottom, Is.True);
        Assert.That(hasLeftSeamTop, Is.True);
        Assert.That(hasRightSeamBottom, Is.True);
        Assert.That(hasRightSeamTop, Is.True);
    }

    [Test]
    public void ClipAtXT_BoundaryPositions_BitIdenticalAcrossSiblings()
    {
        // A triangle straddling the plane must emit bit-identical boundary
        // positions on both sides, or sibling tiles crack at the seam.
        var verts = new[]
        {
            new Vertex3(-1, 0, 0),
            new Vertex3( 1, 0, 0),
            new Vertex3( 0, 1, 0),
        };
        var uvs = new[]
        {
            new Vertex2(0.0, 0.0),
            new Vertex2(1.0, 0.0),
            new Vertex2(0.5, 1.0),
        };
        var faces = new[] { new MeshFace(0, 1, 2, 0, 1, 2, 3) };

        var (left, right) = OctreeSplitter.ClipAtXT(verts, uvs, faces, xSplit: 0.5);

        var leftBoundary = left.Vertices.Where(v => Math.Abs(v.X - 0.5) < 1e-12).ToArray();
        var rightBoundary = right.Vertices.Where(v => Math.Abs(v.X - 0.5) < 1e-12).ToArray();
        Assert.That(leftBoundary.Length, Is.EqualTo(2));
        Assert.That(rightBoundary.Length, Is.EqualTo(2));

        var leftSorted = leftBoundary.OrderBy(v => v.Y).ThenBy(v => v.Z).ToArray();
        var rightSorted = rightBoundary.OrderBy(v => v.Y).ThenBy(v => v.Z).ToArray();
        for (int i = 0; i < leftSorted.Length; i++)
        {
            Assert.That(leftSorted[i].X, Is.EqualTo(rightSorted[i].X).Within(1e-12));
            Assert.That(leftSorted[i].Y, Is.EqualTo(rightSorted[i].Y).Within(1e-12));
            Assert.That(leftSorted[i].Z, Is.EqualTo(rightSorted[i].Z).Within(1e-12));
        }
    }

    [Test]
    public void RecursiveSplitT_PreservesMaterialIndexThroughRecursion()
    {
        var verts = new System.Collections.Generic.List<Vertex3>();
        var uvs = new System.Collections.Generic.List<Vertex2>();
        var faces = new System.Collections.Generic.List<MeshFace>();
        const int N = 5;
        for (int j = 0; j < N; j++)
        for (int i = 0; i < N; i++)
        {
            verts.Add(new Vertex3((double)i / (N - 1), (double)j / (N - 1), 0));
            uvs.Add(new Vertex2((double)i / (N - 1), (double)j / (N - 1)));
        }
        for (int j = 0; j < N - 1; j++)
        for (int i = 0; i < N - 1; i++)
        {
            int a = j * N + i, b = a + 1, c = a + N, d = c + 1;
            int mat = j % 2;
            faces.Add(new MeshFace(a, b, c, a, b, c, mat));
            faces.Add(new MeshFace(b, d, c, b, d, c, mat));
        }
        var bbox = new Box3(0, 0, 0, 1, 1, 0);
        var leaves = OctreeSplitter.RecursiveSplitT(
            verts, uvs, faces, bbox,
            shape: SubdivisionShape.Quadtree,
            maxVertsPerTile: 8,
            maxDepth: 3);

        Assert.That(leaves, Is.Not.Empty);
        foreach (var leaf in leaves)
        foreach (var f in leaf.Mesh.Faces)
            Assert.That(f.MaterialIndex, Is.AnyOf(0, 1),
                $"unexpected material index {f.MaterialIndex} after splitting");
    }
}

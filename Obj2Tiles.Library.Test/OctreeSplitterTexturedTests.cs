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
        // Quad in XY plane: 2 triangles, 4 corners, 4 UVs.
        //   v0=(0,0)  v1=(1,0)
        //   v2=(0,1)  v3=(1,1)
        var verts = new[]
        {
            new Vertex3(0, 0, 0),  // v0
            new Vertex3(1, 0, 0),  // v1
            new Vertex3(0, 1, 0),  // v2
            new Vertex3(1, 1, 0),  // v3
        };
        var uvs = new[]
        {
            new Vertex2(0.0, 0.0),  // uv0
            new Vertex2(1.0, 0.0),  // uv1
            new Vertex2(0.0, 1.0),  // uv2
            new Vertex2(1.0, 1.0),  // uv3
        };
        var faces = new[]
        {
            new MeshFace(0, 1, 2, 0, 1, 2, 7),
            new MeshFace(1, 3, 2, 1, 3, 2, 7),
        };

        var (left, right) = OctreeSplitter.ClipAtXT(verts, uvs, faces, xSplit: 0.5);

        // Both halves have geometry
        Assert.That(left.Faces, Is.Not.Empty);
        Assert.That(right.Faces, Is.Not.Empty);

        // All 4 source UVs survive (they all appear in some face on at least
        // one side: UVs (0,0) and (0,1) are on the left half; (1,0) and (1,1)
        // are on the right; and the boundary UVs are new on both sides).
        bool leftHasUv00 = left.TexVertices.Any(uv => Math.Abs(uv.X) < 1e-12 && Math.Abs(uv.Y) < 1e-12);
        bool leftHasUv01 = left.TexVertices.Any(uv => Math.Abs(uv.X) < 1e-12 && Math.Abs(uv.Y - 1) < 1e-12);
        bool rightHasUv10 = right.TexVertices.Any(uv => Math.Abs(uv.X - 1) < 1e-12 && Math.Abs(uv.Y) < 1e-12);
        bool rightHasUv11 = right.TexVertices.Any(uv => Math.Abs(uv.X - 1) < 1e-12 && Math.Abs(uv.Y - 1) < 1e-12);
        Assert.That(leftHasUv00, Is.True, "left half should retain corner uv (0,0)");
        Assert.That(leftHasUv01, Is.True, "left half should retain corner uv (0,1)");
        Assert.That(rightHasUv10, Is.True, "right half should retain corner uv (1,0)");
        Assert.That(rightHasUv11, Is.True, "right half should retain corner uv (1,1)");

        // The clipping plane is at x=0.5 — exactly the midpoint of the
        // bottom edge (uv0→uv1) and of the top edge (uv2→uv3). t=0.5 → midpoint.
        // Boundary UVs must include (0.5, 0) and (0.5, 1) on each side.
        bool leftHasMidBottom = left.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y) < 1e-9);
        bool leftHasMidTop = left.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y - 1) < 1e-9);
        Assert.That(leftHasMidBottom, Is.True, "boundary uv (0.5, 0) interpolated on left");
        Assert.That(leftHasMidTop, Is.True, "boundary uv (0.5, 1) interpolated on left");

        bool rightHasMidBottom = right.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y) < 1e-9);
        bool rightHasMidTop = right.TexVertices.Any(uv => Math.Abs(uv.X - 0.5) < 1e-9 && Math.Abs(uv.Y - 1) < 1e-9);
        Assert.That(rightHasMidBottom, Is.True, "boundary uv (0.5, 0) interpolated on right");
        Assert.That(rightHasMidTop, Is.True, "boundary uv (0.5, 1) interpolated on right");

        // Material index is preserved on every output face.
        Assert.That(left.Faces.All(f => f.MaterialIndex == 7), Is.True);
        Assert.That(right.Faces.All(f => f.MaterialIndex == 7), Is.True);
    }

    [Test]
    public void ClipAtXT_PreservesUvSeams_OnAdjacentTrianglesWithDifferentUvAtSamePosition()
    {
        // Two triangles meeting at the edge (1,0)-(1,1), but with DIFFERENT
        // UVs at the shared positions (a UV seam — common in photogrammetry).
        // After clipping at x=0.5, both triangles' seams must survive: the
        // splitter must not collapse the two distinct UVs into one.
        var verts = new[]
        {
            new Vertex3(0, 0, 0),  // 0
            new Vertex3(1, 0, 0),  // 1   <- shared position with index 3
            new Vertex3(1, 1, 0),  // 2   <- shared position with index 4
            new Vertex3(1, 0, 0),  // 3   (same position as 1, but separate vertex)
            new Vertex3(1, 1, 0),  // 4   (same position as 2)
            new Vertex3(2, 1, 0),  // 5
        };
        var uvs = new[]
        {
            new Vertex2(0.0, 0.0),  // uv0   left-tri uv
            new Vertex2(0.5, 0.0),  // uv1   left-tri seam uv
            new Vertex2(0.5, 1.0),  // uv2   left-tri seam uv
            new Vertex2(0.0, 0.0),  // uv3   right-tri seam uv (DIFFERENT from uv1 at SAME position)
            new Vertex2(0.0, 1.0),  // uv4   right-tri seam uv (DIFFERENT from uv2)
            new Vertex2(0.5, 1.0),  // uv5   right-tri uv
        };
        var faces = new[]
        {
            new MeshFace(0, 1, 2, 0, 1, 2, 0),  // left triangle uses uv1, uv2 at the seam
            new MeshFace(3, 5, 4, 3, 5, 4, 0),  // right triangle uses uv3, uv4 at the seam
        };

        var (left, right) = OctreeSplitter.ClipAtXT(verts, uvs, faces, xSplit: 0.5);

        // Combine both halves and check that BOTH seam UVs (0.5,0)/(0.5,1)
        // AND (0,0)/(0,1) at the seam positions survive somewhere — they
        // were never the same point so the splitter must not have merged them.
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
        // Same straddling-triangle test as the position-only path: the
        // boundary positions must be EXACTLY equal across left/right outputs.
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
        // Build a 4x4 grid of triangles with material index alternating per row.
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
            int mat = j % 2;  // alternate by row
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
        // Every leaf's faces must have a material index in {0, 1} (the input set).
        foreach (var leaf in leaves)
        foreach (var f in leaf.Mesh.Faces)
            Assert.That(f.MaterialIndex, Is.AnyOf(0, 1),
                $"unexpected material index {f.MaterialIndex} after splitting");
    }
}

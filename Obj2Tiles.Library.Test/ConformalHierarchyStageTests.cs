using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Obj2Tiles;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Library.Test;

public class ConformalHierarchyStageTests
{
    [Test]
    public void Simplify_early_exit_preserves_all_faces()
    {
        // 2 faces is below the early-exit threshold, so all faces return unchanged.
        var verts = new[]
        {
            new Vertex3(0, 0, 0),
            new Vertex3(1, 0, 0),
            new Vertex3(1, 1, 0),
            new Vertex3(0, 1, 0),
        };
        var tex = new[]
        {
            new Vertex2(0, 0), new Vertex2(1, 0), new Vertex2(1, 1), new Vertex2(0, 1),
        };
        var faces = new[]
        {
            new MeshFace(0, 1, 2, 0, 1, 2, 0),
            new MeshFace(0, 2, 3, 0, 2, 3, 0),
        };
        var lockMask = new byte[] { 1, 0, 0, 0 };
        var simp = ConformalHierarchyStage.SimplifyLocked(verts, tex, faces, lockMask, targetRatio: 0.5f, out _);
        bool seen0 = false;
        foreach (var f in simp)
            if (f.IndexA == 0 || f.IndexB == 0 || f.IndexC == 0) seen0 = true;
        Assert.That(seen0, Is.True, "early exit must return all input faces (vert 0 must be present)");
    }

    [Test]
    public void Simplify_targetRatio_one_returns_input_unchanged()
    {
        var verts = new[]
        {
            new Vertex3(0, 0, 0),
            new Vertex3(1, 0, 0),
            new Vertex3(0, 1, 0),
        };
        var tex = new[] { new Vertex2(0, 0), new Vertex2(1, 0), new Vertex2(0, 1) };
        var faces = new[] { new MeshFace(0, 1, 2, 0, 1, 2, 0) };
        var lockMask = new byte[] { 0, 0, 0 };
        var simp = ConformalHierarchyStage.SimplifyLocked(verts, tex, faces, lockMask, targetRatio: 1.0f, out _);
        Assert.That(simp.Length, Is.EqualTo(1));
        Assert.That(simp[0].IndexA, Is.EqualTo(0));
        Assert.That(simp[0].IndexB, Is.EqualTo(1));
        Assert.That(simp[0].IndexC, Is.EqualTo(2));
    }

    [Test]
    public void BuildTreeConformal_synthetic_grid_produces_manifold_root()
    {
        var (verts, tex, faces) = MakeFlatQuadGrid(rows: 4, cols: 4);
        var bounds = ComputeBounds(verts);
        var lods = new[]
        {
            new LodConfig { Quality = 1.0f, JpegQuality = 90 },
        };

        var root = ConformalHierarchyStage.BuildTreeConformal(
            verts, tex, faces, bounds,
            SubdivisionShape.Quadtree,
            maxDepth: 2,
            lods: lods);

        Assert.That(root, Is.Not.Null);
        Assert.That(root.Depth, Is.EqualTo(0));
        Assert.That(root.Children, Has.Count.EqualTo(4), "depth-1 quadtree has 4 children");
        foreach (var c1 in root.Children)
            Assert.That(c1.Children, Has.Count.EqualTo(4), "each depth-1 node has 4 depth-2 children");

        Assert.That(root.TileContentT!.Faces.Length, Is.GreaterThanOrEqualTo(32),
            "root must have at least the source triangle count");

        var leftCell = root.Children.First(c => c.Coord.X == 0 && c.Coord.Y == 0);
        var rightCell = root.Children.First(c => c.Coord.X == 1 && c.Coord.Y == 0);
        double cxRoot = (bounds.Min.X + bounds.Max.X) * 0.5;
        var leftBoundary = leftCell.TileContentT!.Vertices
            .Where(v => System.Math.Abs(v.X - cxRoot) < 1e-9)
            .Select(v => (v.Y, v.Z))
            .OrderBy(p => p.Y).ThenBy(p => p.Z)
            .ToArray();
        var rightBoundary = rightCell.TileContentT!.Vertices
            .Where(v => System.Math.Abs(v.X - cxRoot) < 1e-9)
            .Select(v => (v.Y, v.Z))
            .OrderBy(p => p.Y).ThenBy(p => p.Z)
            .ToArray();
        Assert.That(rightBoundary, Is.EqualTo(leftBoundary).AsCollection,
            "adjacent depth-1 cells must share boundary verts on X=cx_root by construction");
    }

    [Test]
    public void BuildTreeConformal_parentBounds_contain_allChildBounds()
    {
        // 3D Tiles requires a node's bounding volume to contain every descendant's,
        // else a parent cull drops its subtree mid-transition.
        var (verts, tex, faces) = MakeFlatQuadGrid(rows: 4, cols: 4);
        var bounds = ComputeBounds(verts);
        var lods = new[] { new LodConfig { Quality = 1.0f, JpegQuality = 90 } };

        var root = ConformalHierarchyStage.BuildTreeConformal(
            verts, tex, faces, bounds, SubdivisionShape.Quadtree, maxDepth: 2, lods: lods);

        const double eps = 1e-9;
        void AssertContainsChildren(HierarchicalNode node)
        {
            foreach (var child in node.Children)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(child.Bounds.Min.X, Is.GreaterThanOrEqualTo(node.Bounds.Min.X - eps),
                        $"child {child.Coord} min.X must be >= parent {node.Coord} min.X");
                    Assert.That(child.Bounds.Min.Y, Is.GreaterThanOrEqualTo(node.Bounds.Min.Y - eps),
                        $"child {child.Coord} min.Y must be >= parent {node.Coord} min.Y");
                    Assert.That(child.Bounds.Min.Z, Is.GreaterThanOrEqualTo(node.Bounds.Min.Z - eps),
                        $"child {child.Coord} min.Z must be >= parent {node.Coord} min.Z");
                    Assert.That(child.Bounds.Max.X, Is.LessThanOrEqualTo(node.Bounds.Max.X + eps),
                        $"child {child.Coord} max.X must be <= parent {node.Coord} max.X");
                    Assert.That(child.Bounds.Max.Y, Is.LessThanOrEqualTo(node.Bounds.Max.Y + eps),
                        $"child {child.Coord} max.Y must be <= parent {node.Coord} max.Y");
                    Assert.That(child.Bounds.Max.Z, Is.LessThanOrEqualTo(node.Bounds.Max.Z + eps),
                        $"child {child.Coord} max.Z must be <= parent {node.Coord} max.Z");
                });
                AssertContainsChildren(child);
            }
        }
        AssertContainsChildren(root);
    }

    [Test]
    public void Simplify_multicluster_never_emits_cross_cluster_face()
    {
        var (verts, tex, faces) = MakeFlatQuadGridMultiCluster(rows: 8, cols: 8);
        var lockMask = new byte[verts.Count];

        var simp = ConformalHierarchyStage.SimplifyLocked(
            verts, tex, faces, lockMask, targetRatio: 0.5f, out _);

        Assert.That(simp.Length, Is.GreaterThan(0));
        var texMat = new Dictionary<int, int>();
        foreach (var f in faces)
        {
            texMat[f.TexA] = f.MaterialIndex;
            texMat[f.TexB] = f.MaterialIndex;
            texMat[f.TexC] = f.MaterialIndex;
        }
        foreach (var f in simp)
        {
            Assert.That(texMat[f.TexA], Is.EqualTo(f.MaterialIndex),
                "output TexA must belong to face's material cluster");
            Assert.That(texMat[f.TexB], Is.EqualTo(f.MaterialIndex),
                "output TexB must belong to face's material cluster");
            Assert.That(texMat[f.TexC], Is.EqualTo(f.MaterialIndex),
                "output TexC must belong to face's material cluster");
        }
    }

    [Test]
    public void Simplify_multicluster_preserves_seam_verts()
    {
        var (verts, tex, faces) = MakeFlatQuadGridMultiCluster(rows: 8, cols: 8);
        var lockMask = new byte[verts.Count];

        var posMats = new Dictionary<int, HashSet<int>>();
        foreach (var f in faces)
        {
            void Add(int p, int m)
            {
                if (!posMats.TryGetValue(p, out var s)) posMats[p] = s = new HashSet<int>();
                s.Add(m);
            }
            Add(f.IndexA, f.MaterialIndex);
            Add(f.IndexB, f.MaterialIndex);
            Add(f.IndexC, f.MaterialIndex);
        }
        var seamVerts = new HashSet<int>(posMats.Where(kv => kv.Value.Count >= 2).Select(kv => kv.Key));
        Assume.That(seamVerts.Count, Is.GreaterThan(0), "fixture must have seam verts to test against");

        var simp = ConformalHierarchyStage.SimplifyLocked(
            verts, tex, faces, lockMask, targetRatio: 0.3f, out _);

        var survivingVerts = new HashSet<int>();
        foreach (var f in simp)
        {
            survivingVerts.Add(f.IndexA);
            survivingVerts.Add(f.IndexB);
            survivingVerts.Add(f.IndexC);
        }
        foreach (var sv in seamVerts)
            Assert.That(survivingVerts.Contains(sv), Is.True,
                $"seam vert {sv} must survive simplification (multi-cluster vertex_lock)");
    }

    // 4-quadrant grid: one material per quadrant, seam verts along the centerlines.
    private static (List<Vertex3>, List<Vertex2>, List<MeshFace>) MakeFlatQuadGridMultiCluster(int rows, int cols)
    {
        var verts = new List<Vertex3>();
        for (int r = 0; r <= rows; r++)
        for (int c = 0; c <= cols; c++)
            verts.Add(new Vertex3(c, r, 0));

        var tex = new List<Vertex2>();
        var texByPosMat = new Dictionary<(int pos, int mat), int>();
        int GetTexFor(int pos, int mat, double u, double v)
        {
            var key = (pos, mat);
            if (texByPosMat.TryGetValue(key, out var hit)) return hit;
            int idx = tex.Count;
            tex.Add(new Vertex2(u, v));
            texByPosMat[key] = idx;
            return idx;
        }

        int midR = rows / 2, midC = cols / 2;
        int MatFor(int r, int c)
        {
            int top = r >= midR ? 1 : 0;
            int right = c >= midC ? 1 : 0;
            return top * 2 + right;
        }
        (double u, double v) UvFor(int r, int c, int mat)
        {
            int rBase = (mat >> 1) * midR;
            int cBase = (mat & 1) * midC;
            return ((double)(c - cBase) / midC, (double)(r - rBase) / midR);
        }

        var faces = new List<MeshFace>();
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            int i00 = r * (cols + 1) + c;
            int i01 = i00 + 1;
            int i10 = i00 + (cols + 1);
            int i11 = i10 + 1;
            int mat = MatFor(r, c);
            int t00 = GetTexFor(i00, mat, UvFor(r,     c    , mat).u, UvFor(r,     c    , mat).v);
            int t01 = GetTexFor(i01, mat, UvFor(r,     c + 1, mat).u, UvFor(r,     c + 1, mat).v);
            int t10 = GetTexFor(i10, mat, UvFor(r + 1, c    , mat).u, UvFor(r + 1, c    , mat).v);
            int t11 = GetTexFor(i11, mat, UvFor(r + 1, c + 1, mat).u, UvFor(r + 1, c + 1, mat).v);
            faces.Add(new MeshFace(i00, i01, i11, t00, t01, t11, mat));
            faces.Add(new MeshFace(i00, i11, i10, t00, t11, t10, mat));
        }
        return (verts, tex, faces);
    }

    private static (List<Vertex3>, List<Vertex2>, List<MeshFace>) MakeFlatQuadGrid(int rows, int cols)
    {
        var verts = new List<Vertex3>();
        var tex = new List<Vertex2>();
        for (int r = 0; r <= rows; r++)
        for (int c = 0; c <= cols; c++)
        {
            verts.Add(new Vertex3(c, r, 0));
            tex.Add(new Vertex2((double)c / cols, (double)r / rows));
        }
        var faces = new List<MeshFace>();
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            int i00 = r * (cols + 1) + c;
            int i01 = i00 + 1;
            int i10 = i00 + (cols + 1);
            int i11 = i10 + 1;
            faces.Add(new MeshFace(i00, i01, i11, i00, i01, i11, 0));
            faces.Add(new MeshFace(i00, i11, i10, i00, i11, i10, 0));
        }
        return (verts, tex, faces);
    }

    private static Box3 ComputeBounds(IReadOnlyList<Vertex3> verts)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
        double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        foreach (var v in verts)
        {
            if (v.X < mnx) mnx = v.X; if (v.X > mxx) mxx = v.X;
            if (v.Y < mny) mny = v.Y; if (v.Y > mxy) mxy = v.Y;
            if (v.Z < mnz) mnz = v.Z; if (v.Z > mxz) mxz = v.Z;
        }
        return new Box3(mnx, mny, mnz, mxx, mxy, mxz);
    }
}

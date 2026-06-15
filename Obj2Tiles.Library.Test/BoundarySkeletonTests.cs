using NUnit.Framework;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Library.Test;

[TestFixture]
public class BoundarySkeletonTests
{
    [Test]
    public void Empty_skeleton_has_no_locked_verts()
    {
        var skel = new BoundarySkeleton();
        Assert.That(skel.LockedAt(depth: 0), Is.Empty);
        Assert.That(skel.LockedAt(depth: 5), Is.Empty);
    }

    [Test]
    public void AddLockAt_adds_vert_to_specified_depth_only()
    {
        var skel = new BoundarySkeleton();
        skel.AddLockAt(depth: 2, vertIndex: 42);
        Assert.That(skel.LockedAt(0), Is.Empty);
        Assert.That(skel.LockedAt(1), Is.Empty);
        Assert.That(skel.LockedAt(2), Has.Member(42));
        Assert.That(skel.LockedAt(3), Is.Empty);
    }

    [Test]
    public void AddLockAt_multiple_verts_same_depth()
    {
        var skel = new BoundarySkeleton();
        skel.AddLockAt(2, 10);
        skel.AddLockAt(2, 11);
        skel.AddLockAt(2, 10);
        Assert.That(skel.LockedAt(2), Has.Count.EqualTo(2));
        Assert.That(skel.LockedAt(2), Has.Member(10).And.Member(11));
    }

    [Test]
    public void LockMaskFor_returns_byte_array_with_locked_indices_set()
    {
        var skel = new BoundarySkeleton();
        skel.AddLockAt(3, 0);
        skel.AddLockAt(3, 4);
        var mask = skel.LockMaskFor(depth: 3, vertCount: 6);
        Assert.That(mask.Length, Is.EqualTo(6));
        Assert.That(mask[0], Is.EqualTo((byte)1));
        Assert.That(mask[1], Is.EqualTo((byte)0));
        Assert.That(mask[4], Is.EqualTo((byte)1));
        Assert.That(mask[5], Is.EqualTo((byte)0));
    }

    [Test]
    public void LockMaskFor_inherits_shallower_locks()
    {
        var skel = new BoundarySkeleton();
        skel.AddLockAt(1, 5);
        skel.AddLockAt(2, 7);
        var maskAt2 = skel.LockMaskFor(2, vertCount: 10);
        Assert.That(maskAt2[5], Is.EqualTo((byte)1), "depth-1 lock must be inherited at depth 2");
        Assert.That(maskAt2[7], Is.EqualTo((byte)1));
        Assert.That(maskAt2[3], Is.EqualTo((byte)0));
    }

    [Test]
    public void BuildAndEnrich_one_plane_one_crossing_triangle()
    {
        var verts = new System.Collections.Generic.List<Vertex3>
        {
            new(0, 0, 0),
            new(10, 0, 0),
            new(5, 10, 0),
        };
        var tex = new System.Collections.Generic.List<Vertex2> { new(0, 0), new(1, 0), new(0.5, 1) };
        var faces = new System.Collections.Generic.List<MeshFace> { new(0, 1, 2, 0, 1, 2, 0) };
        var sceneBounds = new Box3(0, 0, 0, 10, 0, 0); // zero-Y skips the Y plane, isolating x=5

        var (enrichedVerts, enrichedTex, enrichedFaces, skel) =
            BoundarySkeleton.BuildAndEnrich(verts, tex, faces, sceneBounds, SubdivisionShape.Quadtree, maxDepth: 1);

        Assert.That(enrichedVerts.Count, Is.GreaterThan(3), "must have added intersection verts");
        Assert.That(enrichedFaces.Count, Is.GreaterThanOrEqualTo(2), "triangle must be split");
        var locked = skel.LockedAt(1);
        Assert.That(locked, Is.Not.Empty);
        foreach (int idx in locked)
            Assert.That(System.Math.Abs(enrichedVerts[idx].X - 5.0), Is.LessThan(1e-9),
                $"locked vert {idx} should be on x=5 plane, got X={enrichedVerts[idx].X}");
    }

    [Test]
    public void BuildAndEnrich_no_crossings_keeps_source_unchanged()
    {
        var verts = new System.Collections.Generic.List<Vertex3>
        {
            new(1, 1, 0),
            new(2, 1, 0),
            new(1.5, 2, 0),
        };
        var tex = new System.Collections.Generic.List<Vertex2> { new(0, 0), new(1, 0), new(0.5, 1) };
        var faces = new System.Collections.Generic.List<MeshFace> { new(0, 1, 2, 0, 1, 2, 0) };
        var sceneBounds = new Box3(0, 0, 0, 10, 10, 0);

        var (enrichedVerts, _, enrichedFaces, skel) =
            BoundarySkeleton.BuildAndEnrich(verts, tex, faces, sceneBounds, SubdivisionShape.Quadtree, maxDepth: 1);

        Assert.That(enrichedVerts.Count, Is.EqualTo(3));
        Assert.That(enrichedFaces.Count, Is.EqualTo(1));
        Assert.That(skel.LockedAt(1), Is.Empty);
    }

    [Test]
    public void BuildAndEnrich_preserves_triangle_winding()
    {
        var verts = new System.Collections.Generic.List<Vertex3>
        {
            new(0, 0, 0),
            new(10, 0, 0),
            new(5, 10, 0),
        };
        var tex = new System.Collections.Generic.List<Vertex2> { new(0, 0), new(1, 0), new(0.5, 1) };
        var faces = new System.Collections.Generic.List<MeshFace> { new(0, 1, 2, 0, 1, 2, 0) };
        var sceneBounds = new Box3(0, 0, 0, 10, 0, 0);

        var (eVerts, _, eFaces, _) =
            BoundarySkeleton.BuildAndEnrich(verts, tex, faces, sceneBounds, SubdivisionShape.Quadtree, maxDepth: 1);

        foreach (var f in eFaces)
        {
            var pa = eVerts[f.IndexA];
            var pb = eVerts[f.IndexB];
            var pc = eVerts[f.IndexC];
            double zCross = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            Assert.That(zCross, Is.GreaterThan(0),
                $"sub-triangle [{f.IndexA},{f.IndexB},{f.IndexC}] has flipped winding (zCross={zCross})");
        }
    }

    [Test]
    public void BuildAndEnrich_preserves_winding_L1R2_branch()
    {
        var verts = new System.Collections.Generic.List<Vertex3>
        {
            new(0, 0, 0),
            new(8, 0, 0),
            new(7, 10, 0),
        };
        var tex = new System.Collections.Generic.List<Vertex2> { new(0, 0), new(1, 0), new(0.5, 1) };
        var faces = new System.Collections.Generic.List<MeshFace> { new(0, 1, 2, 0, 1, 2, 0) };
        var sceneBounds = new Box3(0, 0, 0, 10, 0, 0);

        var (eVerts, _, eFaces, _) =
            BoundarySkeleton.BuildAndEnrich(verts, tex, faces, sceneBounds, SubdivisionShape.Quadtree, maxDepth: 1);

        Assert.That(eFaces.Count, Is.EqualTo(3), "L1R2 split = 1 left + 2 right = 3 sub-tris");
        foreach (var f in eFaces)
        {
            var pa = eVerts[f.IndexA]; var pb = eVerts[f.IndexB]; var pc = eVerts[f.IndexC];
            double zCross = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            Assert.That(zCross, Is.GreaterThan(0),
                $"L1R2 sub-tri [{f.IndexA},{f.IndexB},{f.IndexC}] flipped (zCross={zCross})");
        }
    }

    [Test]
    public void BuildAndEnrich_preserves_winding_L2R1_branch()
    {
        var verts = new System.Collections.Generic.List<Vertex3>
        {
            new(0, 0, 0),
            new(2, 0, 0),
            new(7, 10, 0),
        };
        var tex = new System.Collections.Generic.List<Vertex2> { new(0, 0), new(1, 0), new(0.5, 1) };
        var faces = new System.Collections.Generic.List<MeshFace> { new(0, 1, 2, 0, 1, 2, 0) };
        var sceneBounds = new Box3(0, 0, 0, 10, 0, 0);

        var (eVerts, _, eFaces, _) =
            BoundarySkeleton.BuildAndEnrich(verts, tex, faces, sceneBounds, SubdivisionShape.Quadtree, maxDepth: 1);

        Assert.That(eFaces.Count, Is.EqualTo(3), "L2R1 split = 2 left + 1 right = 3 sub-tris");
        foreach (var f in eFaces)
        {
            var pa = eVerts[f.IndexA]; var pb = eVerts[f.IndexB]; var pc = eVerts[f.IndexC];
            double zCross = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            Assert.That(zCross, Is.GreaterThan(0),
                $"L2R1 sub-tri [{f.IndexA},{f.IndexB},{f.IndexC}] flipped (zCross={zCross})");
        }
    }

    [Test]
    public void BuildAndEnrich_maxDepth2_locks_both_depths_and_inheritance()
    {
        var verts = new System.Collections.Generic.List<Vertex3>
        {
            new(0, 0, 0),
            new(10, 0, 0),
            new(5, 10, 0),
        };
        var tex = new System.Collections.Generic.List<Vertex2> { new(0, 0), new(1, 0), new(0.5, 1) };
        var faces = new System.Collections.Generic.List<MeshFace> { new(0, 1, 2, 0, 1, 2, 0) };
        var sceneBounds = new Box3(0, 0, 0, 10, 0, 0);

        var (eVerts, _, _, skel) =
            BoundarySkeleton.BuildAndEnrich(verts, tex, faces, sceneBounds, SubdivisionShape.Quadtree, maxDepth: 2);

        Assert.That(skel.LockedAt(1), Is.Not.Empty, "depth-1 (x=5) plane must produce locks");
        Assert.That(skel.LockedAt(2), Is.Not.Empty, "depth-2 (x=2.5 or x=7.5) plane must produce locks");

        var maskAt2 = skel.LockMaskFor(2, eVerts.Count);
        int locksFromD1 = 0, locksFromD2 = 0;
        foreach (int i in skel.LockedAt(1)) if (maskAt2[i] == 1) locksFromD1++;
        foreach (int i in skel.LockedAt(2)) if (maskAt2[i] == 1) locksFromD2++;
        Assert.That(locksFromD1, Is.EqualTo(skel.LockedAt(1).Count),
            "LockMaskFor(2) must inherit ALL depth-1 locks");
        Assert.That(locksFromD2, Is.EqualTo(skel.LockedAt(2).Count),
            "LockMaskFor(2) must include ALL depth-2 locks");
    }
}

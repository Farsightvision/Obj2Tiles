using System.Collections.Generic;
using NUnit.Framework;
using Obj2Tiles;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

public class PredictAtlasSideTests
{
    // Unit square in z=0 → 1 m² world surface area.
    private static ClipResultT UnitSquareTile() => new()
    {
        Vertices = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(1, 1, 0), new Vertex3(0, 1, 0) },
        Faces = new[] { new MeshFace(0, 1, 2, 0, 0, 0, 0), new MeshFace(0, 2, 3, 0, 0, 0, 0) },
    };

    private static HierarchicalNode Node(int depth, ClipResultT? content, bool internalNode = false)
    {
        var n = new HierarchicalNode { Coord = new CellCoord(depth, 0, 0, 0), TileContentT = content };
        if (internalNode) n.Children.Add(new HierarchicalNode { Coord = new CellCoord(depth + 1, 0, 0, 0) });
        return n;
    }

    [Test]
    public void EmptyTile_ReturnsAtlasMinSize()
    {
        var cfg = new AppConfig { AtlasMinSize = 64 };
        Assert.That(ConformalHierarchyStage.PredictAtlasSide(Node(0, null), cfg, 0), Is.EqualTo(64));
    }

    [Test]
    public void ZeroAreaTile_ReturnsAtlasMinSize()
    {
        var collinear = new ClipResultT
        {
            Vertices = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(2, 0, 0) },
            Faces = new[] { new MeshFace(0, 1, 2, 0, 0, 0, 0) },
        };
        var cfg = new AppConfig { AtlasMinSize = 64 };
        Assert.That(ConformalHierarchyStage.PredictAtlasSide(Node(0, collinear), cfg, 0), Is.EqualTo(64));
    }

    [Test]
    public void Leaf_SizesFromAreaTimesDensity_AsClampedPowerOfTwo()
    {
        var cfg = new AppConfig { AtlasLeafDensityPxPerM = 256, AtlasMinSize = 64, MaxAtlasSize = 4096 };
        Assert.That(ConformalHierarchyStage.PredictAtlasSide(Node(3, UnitSquareTile()), cfg, 3), Is.EqualTo(256));
    }

    [Test]
    public void InternalNode_CappedByMaxAtlasSizeInternal()
    {
        var cfg = new AppConfig
        {
            AtlasLeafDensityPxPerM = 256, AtlasMinSize = 64, MaxAtlasSize = 4096,
            MaxAtlasSizeInternal = 128, AtlasMaxDepthSchedule = new Dictionary<int, int>(),
        };
        Assert.That(ConformalHierarchyStage.PredictAtlasSide(Node(3, UnitSquareTile(), internalNode: true), cfg, 3),
            Is.EqualTo(128));
    }

    [Test]
    public void InternalNode_DepthScheduleOverridesInternalCap()
    {
        var cfg = new AppConfig
        {
            AtlasLeafDensityPxPerM = 256, AtlasMinSize = 64, MaxAtlasSize = 4096, MaxAtlasSizeInternal = 4096,
            AtlasMaxDepthSchedule = new Dictionary<int, int> { { 3, 128 } },
        };
        Assert.That(ConformalHierarchyStage.PredictAtlasSide(Node(3, UnitSquareTile(), internalNode: true), cfg, 3),
            Is.EqualTo(128));
    }

    [Test]
    public void TinyTile_ClampedToAtlasMinSize()
    {
        var tiny = new ClipResultT
        {
            Vertices = new[] { new Vertex3(0, 0, 0), new Vertex3(0.01, 0, 0), new Vertex3(0, 0.01, 0) },
            Faces = new[] { new MeshFace(0, 1, 2, 0, 0, 0, 0) },
        };
        var cfg = new AppConfig { AtlasLeafDensityPxPerM = 256, AtlasMinSize = 64, MaxAtlasSize = 4096 };
        Assert.That(ConformalHierarchyStage.PredictAtlasSide(Node(3, tiny), cfg, 3), Is.EqualTo(64));
    }
}

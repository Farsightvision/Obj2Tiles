using System.IO;
using System.Text.Json;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

/// <summary>Pins the 3D-Tiles tileset.json contract emitted by HierarchicalTilingStage.WriteTilesetJson.</summary>
public class TilesetJsonTests
{
    /// <summary>Minimal non-empty tile content; a node only gets a content URI when it carries faces.</summary>
    private static ClipResultT Content() => new()
    {
        Vertices = new[] { new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(0, 1, 0) },
        TexVertices = new[] { new Vertex2(0, 0), new Vertex2(1, 0), new Vertex2(0, 1) },
        Faces = new[] { new MeshFace(0, 1, 2, 0, 1, 2, 0) },
    };

    private static HierarchicalNode BuildTree()
    {
        var root = new HierarchicalNode
        {
            Coord = new CellCoord(0, 0, 0, 0),
            Bounds = new Box3(0, 0, 0, 100, 100, 50),
            GeometricError = 10.0,
            TileContentT = Content(),
        };
        root.Children.Add(new HierarchicalNode
        {
            Coord = new CellCoord(1, 0, 0, 0), Bounds = new Box3(0, 0, 0, 50, 100, 50), GeometricError = 5.0,
            TileContentT = Content(),
        });
        root.Children.Add(new HierarchicalNode
        {
            Coord = new CellCoord(1, 1, 0, 0), Bounds = new Box3(50, 0, 0, 100, 100, 50), GeometricError = 3.0,
            TileContentT = Content(),
        });
        return root;
    }

    private static JsonElement WriteAndParse(HierarchicalNode root)
    {
        string dir = Path.Combine(Path.GetTempPath(), "obj2tiles_tileset_chartest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        HierarchicalTilingStage.WriteTilesetJson(root, dir, 45.0, 9.0, 0.0, SubdivisionShape.Quadtree);
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tileset.json")));
        return doc.RootElement.Clone();
    }

    [Test]
    public void WriteTilesetJson_EmitsAssetAndRootMetadata()
    {
        var root = BuildTree();
        var ts = WriteAndParse(root);

        Assert.That(ts.GetProperty("asset").GetProperty("version").GetString(), Is.EqualTo("1.1"));
        Assert.That(ts.GetProperty("asset").GetProperty("gltfUpAxis").GetString(), Is.EqualTo("Z"));
        Assert.That(ts.GetProperty("geometricError").GetDouble(), Is.EqualTo(10.0).Within(1e-9));

        var wrapper = ts.GetProperty("root");
        Assert.That(wrapper.GetProperty("geometricError").GetDouble(), Is.EqualTo(10.0).Within(1e-9));
        Assert.That(wrapper.GetProperty("refine").GetString(), Is.EqualTo("REPLACE"));
        Assert.That(wrapper.GetProperty("transform").GetArrayLength(), Is.EqualTo(16), "wrapper root carries the ECEF transform");
        Assert.That(wrapper.GetProperty("boundingVolume").GetProperty("box").GetArrayLength(), Is.EqualTo(12),
            "3D-Tiles box = center + 3 half-axis vectors");
        Assert.That(wrapper.TryGetProperty("content", out _), Is.False, "wrapper root is content-less");

        var contentTile = wrapper.GetProperty("children")[0];
        Assert.That(contentTile.GetProperty("content").GetProperty("uri").GetString(),
            Is.EqualTo(root.Coord.ToContentUri(isQuadtree: true)));
    }

    [Test]
    public void WriteTilesetJson_ChildrenNestWithMonotonicGE_AndOnlyRootHasTransform()
    {
        var ts = WriteAndParse(BuildTree());

        var wrapper = ts.GetProperty("root");
        Assert.That(wrapper.GetProperty("transform").GetArrayLength(), Is.EqualTo(16));
        var wrapperChildren = wrapper.GetProperty("children");
        Assert.That(wrapperChildren.GetArrayLength(), Is.EqualTo(1));

        var contentTile = wrapperChildren[0];
        Assert.That(contentTile.TryGetProperty("transform", out _), Is.False, "content tiles must not carry a transform");
        double rootGe = contentTile.GetProperty("geometricError").GetDouble();

        var leaves = contentTile.GetProperty("children");
        Assert.That(leaves.GetArrayLength(), Is.EqualTo(2));
        foreach (var child in leaves.EnumerateArray())
        {
            Assert.That(child.GetProperty("geometricError").GetDouble(), Is.LessThan(rootGe));
            Assert.That(child.TryGetProperty("transform", out _), Is.False, "child tiles must not carry a transform");
            Assert.That(child.TryGetProperty("children", out _), Is.False, "leaf tiles have no children array");
        }
    }
}

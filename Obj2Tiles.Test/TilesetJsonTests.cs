using System.IO;
using System.Text.Json;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Stages;

namespace Obj2Tiles.Test;

/// <summary>
/// Characterization test for HierarchicalTilingStage.WriteTilesetJson — a regression net ABOVE the
/// byte-identical md5 gate: it pins the 3D-Tiles tileset.json CONTRACT the renderer depends on (root
/// transform, per-tile box / geometricError / refine / content.uri, child nesting, GE monotonicity),
/// so it survives intentional output changes that the md5 gate cannot. WriteTilesetJson emits URIs
/// only — it needs no GLBs on disk — so a synthetic node tree drives it cleanly.
/// </summary>
public class TilesetJsonTests
{
    // depth-0 root (GE 10) with two depth-1 children (GE 5, 3) — a minimal monotonic tree.
    private static HierarchicalNode BuildTree()
    {
        var root = new HierarchicalNode
        {
            Coord = new CellCoord(0, 0, 0, 0),
            Bounds = new Box3(0, 0, 0, 100, 100, 50),
            GeometricError = 10.0,
        };
        root.Children.Add(new HierarchicalNode
        {
            Coord = new CellCoord(1, 0, 0, 0), Bounds = new Box3(0, 0, 0, 50, 100, 50), GeometricError = 5.0,
        });
        root.Children.Add(new HierarchicalNode
        {
            Coord = new CellCoord(1, 1, 0, 0), Bounds = new Box3(50, 0, 0, 100, 100, 50), GeometricError = 3.0,
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

        var tileRoot = ts.GetProperty("root");
        Assert.That(tileRoot.GetProperty("geometricError").GetDouble(), Is.EqualTo(10.0).Within(1e-9));
        Assert.That(tileRoot.GetProperty("refine").GetString(), Is.EqualTo("REPLACE"));
        Assert.That(tileRoot.GetProperty("transform").GetArrayLength(), Is.EqualTo(16), "root carries the ECEF transform");
        Assert.That(tileRoot.GetProperty("boundingVolume").GetProperty("box").GetArrayLength(), Is.EqualTo(12),
            "3D-Tiles box = center + 3 half-axis vectors");
        Assert.That(tileRoot.GetProperty("content").GetProperty("uri").GetString(),
            Is.EqualTo(root.Coord.ToContentUri(isQuadtree: true)));
    }

    [Test]
    public void WriteTilesetJson_ChildrenNestWithMonotonicGE_AndOnlyRootHasTransform()
    {
        var ts = WriteAndParse(BuildTree());
        var tileRoot = ts.GetProperty("root");
        double rootGe = tileRoot.GetProperty("geometricError").GetDouble();

        var children = tileRoot.GetProperty("children");
        Assert.That(children.GetArrayLength(), Is.EqualTo(2));
        foreach (var child in children.EnumerateArray())
        {
            // Monotonicity: every child error is strictly below the parent (renderer refines in order).
            Assert.That(child.GetProperty("geometricError").GetDouble(), Is.LessThan(rootGe));
            // Only the root carries `transform`; children inherit it (NullValueHandling.Ignore drops the null).
            Assert.That(child.TryGetProperty("transform", out _), Is.False, "child tiles must not carry a transform");
            // Leaves emit no `children` array (null dropped).
            Assert.That(child.TryGetProperty("children", out _), Is.False, "leaf tiles have no children array");
        }
    }
}

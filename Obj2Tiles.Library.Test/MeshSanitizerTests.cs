using System;
using NUnit.Framework;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

public class MeshSanitizerTests
{
    [Test]
    public void DropsZeroAreaTriangles()
    {
        var verts = new[]
        {
            new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(0, 1, 0),
            new Vertex3(0, 0, 0), new Vertex3(1, 0, 0), new Vertex3(2, 0, 0),
        };
        var faces = new[] { new Face(0, 1, 2), new Face(3, 4, 5) };

        var (newFaces, droppedCount) = MeshSanitizer.DropZeroArea(verts, faces, epsilon: 1e-9);

        Assert.That(newFaces, Has.Length.EqualTo(1));
        Assert.That(droppedCount, Is.EqualTo(1));
    }

    [Test]
    public void RejectsUvOutOfUnitRange()
    {
        var uvs = new[]
        {
            new Vertex2(0, 0), new Vertex2(1, 0), new Vertex2(1.5, 0.5),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => MeshSanitizer.RequireUvsInUnitRange(uvs));

        Assert.That(ex!.Message, Does.Contain("UV"));
    }

    [Test]
    public void AcceptsUvsInsideUnitRange()
    {
        var uvs = new[]
        {
            new Vertex2(0, 0), new Vertex2(1, 0), new Vertex2(0.5, 0.5),
        };

        Assert.DoesNotThrow(() => MeshSanitizer.RequireUvsInUnitRange(uvs));
    }
}

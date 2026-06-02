using System;
using System.Collections.Generic;

namespace Obj2Tiles.Library.Geometry;

/// <summary>
/// New-pipeline-only post-load checks. Runs after the shared <see cref="MeshUtils.LoadMesh"/>
/// returns; legacy never invokes it (so legacy semantics stay byte-identical to today).
/// </summary>
public static class MeshSanitizer
{
    /// <summary>
    /// Drop triangles whose 3D area is below <paramref name="epsilon"/>.
    /// Returns the surviving faces + the dropped count.
    /// </summary>
    public static (Face[] survivors, int droppedCount) DropZeroArea(
        IReadOnlyList<Vertex3> vertices, IReadOnlyList<Face> faces, double epsilon = 1e-9)
    {
        var keep = new List<Face>(faces.Count);
        int dropped = 0;
        foreach (var f in faces)
        {
            var a = vertices[f.IndexA];
            var b = vertices[f.IndexB];
            var c = vertices[f.IndexC];
            // |AB x AC| -- twice the cross-product norm gives the area
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            double acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
            double cx = aby * acz - abz * acy;
            double cy = abz * acx - abx * acz;
            double cz = abx * acy - aby * acx;
            double area2 = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (area2 < epsilon) { dropped++; continue; }
            keep.Add(f);
        }
        return (keep.ToArray(), dropped);
    }

    /// <summary>
    /// Tiled / wrapping UVs are not supported in Phase 1 (see spec §5.2).
    /// Throws if any UV component is outside [0, 1].
    /// </summary>
    public static void RequireUvsInUnitRange(IReadOnlyList<Vertex2> uvs)
    {
        for (int i = 0; i < uvs.Count; i++)
        {
            var uv = uvs[i];
            if (uv.X < 0.0 || uv.X > 1.0 || uv.Y < 0.0 || uv.Y > 1.0)
                throw new InvalidOperationException(
                    $"UV out of [0,1] at index {i}: ({uv.X}, {uv.Y}). " +
                    "Tiled/wrapping textures are not supported by the hierarchical pipeline; the input must " +
                    "be a photogrammetry-style UV map. Drop --hierarchical-lods to use the default flat-grid pipeline (no UV-range constraint).");
        }
    }
}

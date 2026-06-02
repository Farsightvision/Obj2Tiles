using System.Collections.Generic;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Library.Test;

internal static class TestMeshes
{
    /// <summary>Generates side*side*side cubes inside a unit volume (one bottom face per cube).</summary>
    public static (Vertex3[] verts, Face[] faces) UniformGridCube(int side)
    {
        var verts = new List<Vertex3>();
        var faces = new List<Face>();
        double cellSize = 1.0 / side;
        for (int z = 0; z < side; z++)
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int @base = verts.Count;
            // 8 corners of the cube cell
            for (int dz = 0; dz <= 1; dz++)
            for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
                verts.Add(new Vertex3((x + dx) * cellSize, (y + dy) * cellSize, (z + dz) * cellSize));
            // 2 triangles for the bottom face — keeps the mesh light while stress-testing splits.
            faces.Add(new Face(@base + 0, @base + 1, @base + 2));
            faces.Add(new Face(@base + 1, @base + 3, @base + 2));
        }
        return (verts.ToArray(), faces.ToArray());
    }

    public static Box3 BoundsOf(Vertex3[] verts)
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

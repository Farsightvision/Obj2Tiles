namespace Obj2Tiles.Library.Geometry;

/// <summary>Octree/quadtree cell coordinates; quadtree leaves Z at 0.</summary>
public readonly record struct CellCoord(int Level, int X, int Y, int Z)
{
    public string ToContentUri(bool isQuadtree)
        => isQuadtree
            ? $"content/{Level}/{X}/{Y}.glb"
            : $"content/{Level}/{X}/{Y}/{Z}.glb";
}

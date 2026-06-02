namespace Obj2Tiles.Library.Geometry;

/// <summary>
/// Octree/quadtree cell coordinates. Quadtree omits Z (set to 0).
/// The (Level, X, Y, Z) tuple uniquely identifies a tile and maps to its
/// content URI: content/{Level}/{X}/{Y}/{Z}.glb (or content/{Level}/{X}/{Y}.glb for quadtree).
/// </summary>
public readonly record struct CellCoord(int Level, int X, int Y, int Z)
{
    public string ToContentUri(bool isQuadtree)
        => isQuadtree
            ? $"content/{Level}/{X}/{Y}.glb"
            : $"content/{Level}/{X}/{Y}/{Z}.glb";
}

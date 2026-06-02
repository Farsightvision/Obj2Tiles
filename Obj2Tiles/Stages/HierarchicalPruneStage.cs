namespace Obj2Tiles.Stages;

/// <summary>
/// Post-processing step that drops descendants of any non-leaf node whose
/// geometric error is zero. A zero-error parent means its simplified surface
/// matches the children's geometry — refining adds no detail, just extra GLBs
/// to load. Collapsing keeps the tree shape proportional to actual LOD
/// information and avoids wasted tile slots in the renderer.
/// </summary>
public static class HierarchicalPruneStage
{
    /// <summary>
    /// Walk the tree bottom-up; for any node with GeometricError == 0 and
    /// non-empty Children, clear its Children list. Returns the total
    /// descendant count removed (not counting nodes that were turned into
    /// leaves themselves).
    /// </summary>
    public static int PruneZeroErrorSubtrees(HierarchicalNode root)
    {
        int totalRemoved = 0;
        Walk(root);
        return totalRemoved;

        void Walk(HierarchicalNode n)
        {
            foreach (var c in n.Children) Walk(c);
            if (!n.IsLeaf && n.GeometricError == 0)
            {
                int count = 0;
                foreach (var c in n.Children) count += CountAllDescendants(c);
                totalRemoved += count;
                n.Children.Clear();
            }
        }
    }

    private static int CountAllDescendants(HierarchicalNode n)
    {
        int count = 1;
        foreach (var c in n.Children) count += CountAllDescendants(c);
        return count;
    }
}

namespace Obj2Tiles.Stages;

/// <summary>
/// Drops descendants of any non-leaf node with zero geometric error, since a
/// zero-error parent already matches its children and refining adds no detail.
/// </summary>
public static class HierarchicalPruneStage
{
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

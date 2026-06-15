namespace Obj2Tiles.Stages.Model;

/// <summary>
/// Decides whether to auto-activate the HLOD source-texture cache to bound
/// Phase-1 peak RAM when the operator did not set --source-cache-cap.
/// </summary>
public static class Phase1AutoCachePolicy
{
    /// <summary>Fraction of available memory the decoded source set may occupy
    /// before the cache is auto-activated.</summary>
    public const double DefaultFraction = 0.5;

    public static bool ShouldAutoEnable(
        bool hierarchicalLods,
        int userCap,
        long decodedTextureBytes,
        long availableBytes,
        double fraction = DefaultFraction)
    {
        if (!hierarchicalLods) return false;
        if (userCap > 0) return false;
        if (availableBytes <= 0) return false;
        return decodedTextureBytes > (long)(availableBytes * fraction);
    }
}

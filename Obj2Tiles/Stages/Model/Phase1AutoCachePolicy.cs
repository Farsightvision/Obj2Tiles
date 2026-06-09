namespace Obj2Tiles.Stages.Model;

/// <summary>
/// Decides whether to auto-activate the (otherwise-latent) HLOD source-texture
/// cache to bound Phase-1 peak RAM on large models. The prod HLOD pipeline ships
/// a full RAM-aware degradation system (downsample-on-decode + resident budget +
/// memory-clamped worker count + per-chunk eviction) that is entirely gated on
/// TexturesCache.PersistResident == (SourceCacheCap &gt; 0). When the operator does
/// not pass --source-cache-cap, that whole system stays dormant and a large model
/// (e.g. 185 x 8192² sources) decodes full-res into the shared cache during the
/// parallel Phase-1 chunk, OOMKilling a few-GB pod. This policy turns the system
/// on automatically for exactly those models, while leaving small models on the
/// byte-identical legacy fast path. Pure function — unit-testable without a bake.
/// </summary>
public static class Phase1AutoCachePolicy
{
    /// <summary>Fraction of available memory the decoded source set may occupy
    /// before the cache is auto-activated.</summary>
    public const double DefaultFraction = 0.5;

    /// <summary>
    /// True when the bake should auto-set source-cache-cap (to MaxAtlasSize).
    /// Fires only when: the HLOD pipeline is in use; the operator did NOT set an
    /// explicit --source-cache-cap (explicit flag always wins); a usable memory
    /// reading is available; and the decoded RGBA source footprint exceeds
    /// <paramref name="fraction"/> of that memory. An unknown memory reading
    /// (<paramref name="availableBytes"/> &lt;= 0) does NOT auto-enable — without a
    /// budget we cannot size the cache, and we must not surprise small bakes.
    /// </summary>
    public static bool ShouldAutoEnable(
        bool hierarchicalLods,
        int userCap,
        long decodedTextureBytes,
        long availableBytes,
        double fraction = DefaultFraction)
    {
        if (!hierarchicalLods) return false;
        if (userCap > 0) return false;          // explicit --source-cache-cap wins
        if (availableBytes <= 0) return false;   // no memory signal => leave legacy path
        return decodedTextureBytes > (long)(availableBytes * fraction);
    }
}

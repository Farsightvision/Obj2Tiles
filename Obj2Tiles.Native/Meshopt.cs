using System;

namespace Obj2Tiles.Native;

/// <summary>
/// High-level C# wrappers over <see cref="MeshoptInterop"/>. All inputs validated;
/// arrays sized to the meshoptimizer-provided bounds.
/// </summary>
public static class Meshopt
{
    [Flags]
    public enum SimplifyOptions : uint
    {
        None            = 0,
        LockBorder      = MeshoptInterop.SIMPLIFY_LOCK_BORDER,
        Sparse          = MeshoptInterop.SIMPLIFY_SPARSE,
        ErrorAbsolute   = MeshoptInterop.SIMPLIFY_ERROR_ABSOLUTE,
        Prune           = MeshoptInterop.SIMPLIFY_PRUNE,
        Permissive      = MeshoptInterop.SIMPLIFY_PERMISSIVE,
    }

    /// <summary>
    /// Simplify a triangle mesh. Returns the simplified index count and writes
    /// the new index buffer to <paramref name="destinationIndices"/>.
    /// Vertex positions are NEVER modified by this call.
    /// </summary>
    public static int Simplify(
        uint[] destinationIndices,
        uint[] indices,
        float[] vertexPositionsXyz,
        int targetIndexCount,
        float targetError,
        SimplifyOptions options,
        out float resultError)
    {
        if (destinationIndices.Length < indices.Length)
            throw new ArgumentException("destination buffer too small", nameof(destinationIndices));
        var n = MeshoptInterop.meshopt_simplify(
            destinationIndices,
            indices, (nuint)indices.Length,
            vertexPositionsXyz, (nuint)(vertexPositionsXyz.Length / 3), sizeof(float) * 3,
            (nuint)targetIndexCount, targetError, (uint)options, out resultError);
        return (int)n;
    }

    /// <summary>
    /// Attribute-aware simplify with optional vertex_lock array.
    /// vertexLock byte values: 0 = unlocked, non-zero = locked (positions remain in output).
    /// </summary>
    public static int SimplifyWithAttributes(
        uint[] destinationIndices,
        uint[] indices,
        float[] vertexPositionsXyz,
        float[] vertexAttributes,    // interleaved per vertex
        float[] attributeWeights,    // length = attribute_count
        int attributeCount,
        byte[]? vertexLock,
        int targetIndexCount,
        float targetError,
        SimplifyOptions options,
        out float resultError)
    {
        var n = MeshoptInterop.meshopt_simplifyWithAttributes(
            destinationIndices,
            indices, (nuint)indices.Length,
            vertexPositionsXyz, (nuint)(vertexPositionsXyz.Length / 3), sizeof(float) * 3,
            vertexAttributes, (nuint)(attributeCount * sizeof(float)),
            attributeWeights, (nuint)attributeCount,
            vertexLock,
            (nuint)targetIndexCount, targetError, (uint)options, out resultError);
        return (int)n;
    }

    public static void OptimizeVertexCache(uint[] destination, uint[] indices, int vertexCount)
        => MeshoptInterop.meshopt_optimizeVertexCache(destination, indices, (nuint)indices.Length, (nuint)vertexCount);

    public static void OptimizeOverdraw(uint[] destination, uint[] indices, float[] vertexPositionsXyz, float threshold = 1.05f)
        => MeshoptInterop.meshopt_optimizeOverdraw(destination, indices, (nuint)indices.Length,
            vertexPositionsXyz, (nuint)(vertexPositionsXyz.Length / 3), sizeof(float) * 3, threshold);

    /// <summary>
    /// Reorders vertex data to match the index access order. Mutates `indices` in place.
    /// `vertices` stride = vertexSize bytes. Returns the new (possibly reduced) vertex count.
    /// </summary>
    public static int OptimizeVertexFetch(byte[] destinationVertices, uint[] indices, byte[] vertices, int vertexCount, int vertexSize)
    {
        var n = MeshoptInterop.meshopt_optimizeVertexFetch(
            destinationVertices, indices, (nuint)indices.Length, vertices, (nuint)vertexCount, (nuint)vertexSize);
        return (int)n;
    }

    public static byte[] EncodeIndexBuffer(uint[] indices, int vertexCount)
    {
        var bound = MeshoptInterop.meshopt_encodeIndexBufferBound((nuint)indices.Length, (nuint)vertexCount);
        var buf = new byte[(int)bound];
        var n = MeshoptInterop.meshopt_encodeIndexBuffer(buf, bound, indices, (nuint)indices.Length);
        Array.Resize(ref buf, (int)n);
        return buf;
    }

    public static byte[] EncodeVertexBuffer(byte[] vertices, int vertexCount, int vertexSize)
    {
        var bound = MeshoptInterop.meshopt_encodeVertexBufferBound((nuint)vertexCount, (nuint)vertexSize);
        var buf = new byte[(int)bound];
        var n = MeshoptInterop.meshopt_encodeVertexBuffer(buf, bound, vertices, (nuint)vertexCount, (nuint)vertexSize);
        Array.Resize(ref buf, (int)n);
        return buf;
    }

    /// <summary>Reduce float precision by clamping mantissa to N significant bits (1..23).
    /// Used for position quantization in EXT_meshopt_compression (M9). Phase 1 doesn't use
    /// QuantizeUnorm/Snorm/Half; if needed later, add them then.</summary>
    public static float QuantizeFloat(float v, int bits) => MeshoptInterop.meshopt_quantizeFloat(v, bits);
}

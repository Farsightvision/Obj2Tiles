using System;

namespace Obj2Tiles.Native;

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

    /// <summary>Simplify a triangle mesh; vertex positions are never modified.</summary>
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

    /// <summary>Attribute-aware simplify; vertexLock byte 0 = unlocked, non-zero = locked.</summary>
    public static int SimplifyWithAttributes(
        uint[] destinationIndices,
        uint[] indices,
        float[] vertexPositionsXyz,
        float[] vertexAttributes,
        float[] attributeWeights,
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

    /// <summary>Reorders vertex data to index access order; mutates indices in place, returns new vertex count.</summary>
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

    /// <summary>Reduce float precision by clamping mantissa to N significant bits (1..23).</summary>
    public static float QuantizeFloat(float v, int bits) => MeshoptInterop.meshopt_quantizeFloat(v, bits);
}

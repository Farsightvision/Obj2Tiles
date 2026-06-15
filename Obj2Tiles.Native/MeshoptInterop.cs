using System;
using System.Runtime.InteropServices;

namespace Obj2Tiles.Native;

/// <summary>Raw P/Invoke declarations; consumers should call the <see cref="Meshopt"/> wrappers.</summary>
internal static partial class MeshoptInterop
{
    private const string Lib = "meshoptimizer";

    [LibraryImport(Lib, EntryPoint = "meshopt_simplify")]
    internal static partial nuint meshopt_simplify(
        [Out] uint[] destination,
        [In] uint[] indices, nuint index_count,
        [In] float[] vertex_positions, nuint vertex_count, nuint vertex_positions_stride,
        nuint target_index_count, float target_error,
        uint options, out float result_error);

    [LibraryImport(Lib, EntryPoint = "meshopt_simplifyWithAttributes")]
    internal static partial nuint meshopt_simplifyWithAttributes(
        [Out] uint[] destination,
        [In] uint[] indices, nuint index_count,
        [In] float[] vertex_positions, nuint vertex_count, nuint vertex_positions_stride,
        [In] float[] vertex_attributes, nuint vertex_attributes_stride,
        [In] float[] attribute_weights, nuint attribute_count,
        [In] byte[]? vertex_lock,
        nuint target_index_count, float target_error,
        uint options, out float result_error);

    [LibraryImport(Lib, EntryPoint = "meshopt_optimizeVertexCache")]
    internal static partial void meshopt_optimizeVertexCache(
        [Out] uint[] destination,
        [In] uint[] indices, nuint index_count, nuint vertex_count);

    [LibraryImport(Lib, EntryPoint = "meshopt_optimizeOverdraw")]
    internal static partial void meshopt_optimizeOverdraw(
        [Out] uint[] destination,
        [In] uint[] indices, nuint index_count,
        [In] float[] vertex_positions, nuint vertex_count, nuint vertex_positions_stride,
        float threshold);

    [LibraryImport(Lib, EntryPoint = "meshopt_optimizeVertexFetch")]
    internal static partial nuint meshopt_optimizeVertexFetch(
        [Out] byte[] destination,
        [In, Out] uint[] indices, nuint index_count,
        [In] byte[] vertices, nuint vertex_count, nuint vertex_size);

    // quantizeUnorm/quantizeSnorm are C++-only inline (not extern "C"), so they
    // can't be P/Invoked and are implemented in managed C# in Meshopt.cs.
    [LibraryImport(Lib, EntryPoint = "meshopt_quantizeFloat")]
    internal static partial float meshopt_quantizeFloat(float v, int N);

    [LibraryImport(Lib, EntryPoint = "meshopt_encodeIndexBufferBound")]
    internal static partial nuint meshopt_encodeIndexBufferBound(nuint index_count, nuint vertex_count);

    [LibraryImport(Lib, EntryPoint = "meshopt_encodeIndexBuffer")]
    internal static partial nuint meshopt_encodeIndexBuffer(
        [Out] byte[] buffer, nuint buffer_size,
        [In] uint[] indices, nuint index_count);

    [LibraryImport(Lib, EntryPoint = "meshopt_encodeVertexBufferBound")]
    internal static partial nuint meshopt_encodeVertexBufferBound(nuint vertex_count, nuint vertex_size);

    [LibraryImport(Lib, EntryPoint = "meshopt_encodeVertexBuffer")]
    internal static partial nuint meshopt_encodeVertexBuffer(
        [Out] byte[] buffer, nuint buffer_size,
        [In] byte[] vertices, nuint vertex_count, nuint vertex_size);

    public const uint SIMPLIFY_LOCK_BORDER         = 1u << 0;
    public const uint SIMPLIFY_SPARSE              = 1u << 1;
    public const uint SIMPLIFY_ERROR_ABSOLUTE      = 1u << 2;
    public const uint SIMPLIFY_PRUNE               = 1u << 3;
    public const uint SIMPLIFY_REGULARIZE          = 1u << 4;
    public const uint SIMPLIFY_PERMISSIVE          = 1u << 5;
    public const uint SIMPLIFY_REGULARIZE_LIGHT    = 1u << 6;
    public const byte VERTEX_LOCK    = 1 << 0;  // do not collapse
    public const byte VERTEX_PROTECT = 1 << 1;  // preserve attribute discontinuity (Permissive only)
    public const byte VERTEX_PRIORITY= 1 << 2;
}

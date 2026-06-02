using System;
using System.Collections.Generic;
using NUnit.Framework;
using Obj2Tiles.Native;

namespace Obj2Tiles.Library.Test;

public class MeshoptTests
{
    /// <summary>
    /// Smoke test: simplify a 4-triangle quad mesh by 50% and confirm the call
    /// returns a valid index count and a finite error. This is the minimal proof
    /// that the P/Invoke layer reaches the native binary.
    /// </summary>
    [Test]
    public void Simplify_RoundTripsThroughNativeLibrary()
    {
        // Two triangles forming a unit quad
        float[] verts =
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            1f, 1f, 0f,
            0f, 1f, 0f,
        };
        uint[] indices = { 0, 1, 2,  0, 2, 3 };
        var dst = new uint[indices.Length];

        var n = Meshopt.Simplify(dst, indices, verts,
            targetIndexCount: 3,
            targetError: 1.0f,
            options: Meshopt.SimplifyOptions.None,
            out var err);

        Assert.That(n, Is.GreaterThanOrEqualTo(3), $"expected at least 3 indices, got {n}");
        Assert.That(n, Is.LessThanOrEqualTo(indices.Length));
        Assert.That(float.IsFinite(err), Is.True);
    }

    [Test]
    public void OptimizeVertexCache_ReorderingProducesValidIndices()
    {
        uint[] indices = { 0, 1, 2,  0, 2, 3 };
        var dst = new uint[indices.Length];
        Meshopt.OptimizeVertexCache(dst, indices, vertexCount: 4);
        // Output is a permutation of the input set
        var inSet = new HashSet<uint>(indices);
        var outSet = new HashSet<uint>(dst);
        Assert.That(outSet, Is.EqualTo(inSet));
    }
}

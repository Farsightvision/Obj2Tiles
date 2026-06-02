using System;
using System.Collections.Generic;
using System.Linq;

namespace Obj2Tiles.Library.Geometry;

/// <summary>
/// AABB-tree (BVH) over triangle centroids for nearest-point-on-mesh queries.
/// Per spec §6.1, this drives Hausdorff geometric-error measurement.
/// </summary>
public sealed class TriangleBvh
{
    private readonly Vertex3[] _v;
    private readonly Face[] _f;
    private readonly int[] _triIndices;
    private readonly Box3[] _nodeBounds;
    private readonly (int left, int right)[] _nodeChildren;
    private readonly (int start, int count)[] _nodeRange;
    private int _nodeCount;

    public TriangleBvh(IReadOnlyList<Vertex3> verts, IReadOnlyList<Face> faces, int leafSize = 8)
    {
        _v = verts.ToArray();
        _f = faces.ToArray();
        _triIndices = Enumerable.Range(0, _f.Length).ToArray();
        int maxNodes = _f.Length * 2 + 1;
        _nodeBounds = new Box3[maxNodes];
        _nodeChildren = new (int, int)[maxNodes];
        _nodeRange = new (int, int)[maxNodes];
        _nodeCount = 0;
        Build(0, _f.Length, leafSize);
    }

    private int Build(int start, int count, int leafSize)
    {
        int idx = _nodeCount++;
        _nodeRange[idx] = (start, count);
        _nodeBounds[idx] = ComputeBounds(start, count);

        if (count <= leafSize) { _nodeChildren[idx] = (-1, -1); return idx; }

        // Split by largest axis at median
        var b = _nodeBounds[idx];
        double dx = b.Width, dy = b.Height, dz = b.Depth;
        int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;
        Array.Sort(_triIndices, start, count, Comparer<int>.Create((a, c) =>
        {
            double ca = AxisCentroid(a, axis); double cc = AxisCentroid(c, axis);
            return ca.CompareTo(cc);
        }));
        int mid = count / 2;
        int leftCount = mid; int rightCount = count - mid;
        int left = Build(start, leftCount, leafSize);
        int right = Build(start + leftCount, rightCount, leafSize);
        _nodeChildren[idx] = (left, right);
        return idx;
    }

    private double AxisCentroid(int triIdx, int axis)
    {
        var f = _f[triIdx];
        var a = _v[f.IndexA]; var b = _v[f.IndexB]; var c = _v[f.IndexC];
        return axis switch { 0 => (a.X + b.X + c.X) / 3, 1 => (a.Y + b.Y + c.Y) / 3, _ => (a.Z + b.Z + c.Z) / 3 };
    }

    private Box3 ComputeBounds(int start, int count)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
        double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            var f = _f[_triIndices[start + i]];
            foreach (var vi in new[] { f.IndexA, f.IndexB, f.IndexC })
            {
                var v = _v[vi];
                if (v.X < mnx) mnx = v.X; if (v.X > mxx) mxx = v.X;
                if (v.Y < mny) mny = v.Y; if (v.Y > mxy) mxy = v.Y;
                if (v.Z < mnz) mnz = v.Z; if (v.Z > mxz) mxz = v.Z;
            }
        }
        return new Box3(mnx, mny, mnz, mxx, mxy, mxz);
    }

    /// <summary>Distance from <paramref name="point"/> to the nearest triangle surface.</summary>
    public double NearestPointDistance(Vertex3 point)
    {
        double best = double.MaxValue;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            double d2 = DistanceSquaredToBox(point, _nodeBounds[n]);
            if (d2 >= best * best) continue;
            var (l, r) = _nodeChildren[n];
            if (l == -1)
            {
                var (start, count) = _nodeRange[n];
                for (int i = 0; i < count; i++)
                {
                    var f = _f[_triIndices[start + i]];
                    double td = DistanceToTriangle(point, _v[f.IndexA], _v[f.IndexB], _v[f.IndexC]);
                    if (td < best) best = td;
                }
            }
            else { stack.Push(l); stack.Push(r); }
        }
        return best;
    }

    private static double DistanceSquaredToBox(Vertex3 p, Box3 b)
    {
        double dx = System.Math.Max(0, System.Math.Max(b.Min.X - p.X, p.X - b.Max.X));
        double dy = System.Math.Max(0, System.Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
        double dz = System.Math.Max(0, System.Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    /// Closest-point-on-triangle distance using projection + barycentric clamping.
    /// Reference: Christer Ericson, "Real-Time Collision Detection" §5.1.5.
    /// </summary>
    private static double DistanceToTriangle(Vertex3 p, Vertex3 a, Vertex3 b, Vertex3 c)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
        double acx = c.X - a.X, acy = c.Y - a.Y, acz = c.Z - a.Z;
        double apx = p.X - a.X, apy = p.Y - a.Y, apz = p.Z - a.Z;
        double d1 = abx * apx + aby * apy + abz * apz;
        double d2 = acx * apx + acy * apy + acz * apz;
        if (d1 <= 0 && d2 <= 0) return Norm(apx, apy, apz);
        double bpx = p.X - b.X, bpy = p.Y - b.Y, bpz = p.Z - b.Z;
        double d3 = abx * bpx + aby * bpy + abz * bpz;
        double d4 = acx * bpx + acy * bpy + acz * bpz;
        if (d3 >= 0 && d4 <= d3) return Norm(bpx, bpy, bpz);
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { double t = d1 / (d1 - d3); return Norm(apx - t * abx, apy - t * aby, apz - t * abz); }
        double cpx = p.X - c.X, cpy = p.Y - c.Y, cpz = p.Z - c.Z;
        double d5 = abx * cpx + aby * cpy + abz * cpz;
        double d6 = acx * cpx + acy * cpy + acz * cpz;
        if (d6 >= 0 && d5 <= d6) return Norm(cpx, cpy, cpz);
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { double t = d2 / (d2 - d6); return Norm(apx - t * acx, apy - t * acy, apz - t * acz); }
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        {
            double t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return Norm(bpx + t * (cpx - bpx), bpy + t * (cpy - bpy), bpz + t * (cpz - bpz));
        }
        double denom = 1.0 / (va + vb + vc);
        double v2 = vb * denom, w2 = vc * denom;
        return Norm(apx - (abx * v2 + acx * w2), apy - (aby * v2 + acy * w2), apz - (abz * v2 + acz * w2));

        static double Norm(double x, double y, double z) => System.Math.Sqrt(x * x + y * y + z * z);
    }
}

using System;
using UnityEngine;

namespace MSML;

/// <summary>
/// Vector maths for mods, done in plain C#. In this game every Unity maths call made from a mod (Vector3 +, *,
/// Cross, normalized, Mathf.Lerp, Quaternion * Vector3, transform.TransformPoint...) is a call into the game that
/// costs ~80 ns and leaves ~32 bytes of garbage on its heap, which makes the game stop for collections. In code that
/// runs every frame or for every vertex, use these instead (constructing vectors and reading their x/y/z is free).
/// </summary>
public static class VMath
{
    public static Vector3 Add(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3 Sub(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vector3 Mul(Vector3 a, float s) => new(a.x * s, a.y * s, a.z * s);

    /// <summary>a + b * s.</summary>
    public static Vector3 Mad(Vector3 a, Vector3 b, float s) => new(a.x + b.x * s, a.y + b.y * s, a.z + b.z * s);

    /// <summary>a + b * s + c * t.</summary>
    public static Vector3 Mad(Vector3 a, Vector3 b, float s, Vector3 c, float t) =>
        new(a.x + b.x * s + c.x * t, a.y + b.y * s + c.y * t, a.z + b.z * s + c.z * t);

    public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    public static Vector3 Cross(Vector3 a, Vector3 b) => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    public static float SqrMagnitude(Vector3 a) => a.x * a.x + a.y * a.y + a.z * a.z;
    public static float Magnitude(Vector3 a) => MathF.Sqrt(SqrMagnitude(a));
    public static float SqrDistance(Vector3 a, Vector3 b) => SqrMagnitude(Sub(a, b));

    public static Vector3 Normalize(Vector3 a)
    {
        var m = Magnitude(a);
        return m > 1e-5f ? new Vector3(a.x / m, a.y / m, a.z / m) : default;
    }

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Mad(a, Sub(b, a), Clamp01(t));
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    public static float Clamp01(float t) => t < 0f ? 0f : t > 1f ? 1f : t;
    public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    /// <summary>Unity's Mathf.SmoothStep.</summary>
    public static float SmoothStep(float from, float to, float t)
    {
        t = Clamp01(t);
        t = -2f * t * t * t + 3f * t * t;
        return to * t + from * (1f - t);
    }

    /// <summary>A point through a matrix (e.g. a transform's worldToLocalMatrix, fetched once).</summary>
    public static Vector3 Point(in Matrix4x4 m, Vector3 p) => new(
        m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
        m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
        m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23);

    /// <summary>A direction through a matrix (no translation).</summary>
    public static Vector3 Direction(in Matrix4x4 m, Vector3 d) => new(
        m.m00 * d.x + m.m01 * d.y + m.m02 * d.z,
        m.m10 * d.x + m.m11 * d.y + m.m12 * d.z,
        m.m20 * d.x + m.m21 * d.y + m.m22 * d.z);

    /// <summary>A vector rotated by a quaternion.</summary>
    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f;
        float xx = q.x * x, yy = q.y * y, zz = q.z * z, xy = q.x * y, xz = q.x * z, yz = q.y * z;
        float wx = q.w * x, wy = q.w * y, wz = q.w * z;
        return new Vector3(
            (1f - (yy + zz)) * v.x + (xy - wz) * v.y + (xz + wy) * v.z,
            (xy + wz) * v.x + (1f - (xx + zz)) * v.y + (yz - wx) * v.z,
            (xz - wy) * v.x + (yz + wx) * v.y + (1f - (xx + yy)) * v.z);
    }

    /// <summary>The rotation looking along <paramref name="forward"/> with <paramref name="up"/> up (Unity's Quaternion.LookRotation).</summary>
    public static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        var f = Normalize(forward);
        var r = Normalize(Cross(up, f));
        if (SqrMagnitude(r) < 1e-10f) r = Normalize(Cross(Math.Abs(f.y) < 0.99f ? new Vector3(0f, 1f, 0f) : new Vector3(1f, 0f, 0f), f));
        var u = Cross(f, r);
        // Rotation matrix columns r, u, f to a quaternion.
        float m00 = r.x, m01 = u.x, m02 = f.x, m10 = r.y, m11 = u.y, m12 = f.y, m20 = r.z, m21 = u.z, m22 = f.z;
        var trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            var s = MathF.Sqrt(trace + 1f) * 2f;
            return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
        }
        if (m00 > m11 && m00 > m22)
        {
            var s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
            return new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        if (m11 > m22)
        {
            var s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
            return new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        var t = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
        return new Quaternion((m02 + m20) / t, (m12 + m21) / t, 0.25f * t, (m10 - m01) / t);
    }

    /// <summary>Spherical interpolation between rotations (Unity's Quaternion.Slerp, t clamped).</summary>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        t = Clamp01(t);
        var dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        if (dot < 0f) { b = new Quaternion(-b.x, -b.y, -b.z, -b.w); dot = -dot; }
        float wa, wb;
        if (dot > 0.9995f) { wa = 1f - t; wb = t; }
        else
        {
            var theta = MathF.Acos(dot);
            var sin = MathF.Sin(theta);
            wa = MathF.Sin((1f - t) * theta) / sin;
            wb = MathF.Sin(t * theta) / sin;
        }
        var q = new Quaternion(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb);
        var n = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return n > 1e-6f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : b;
    }
}

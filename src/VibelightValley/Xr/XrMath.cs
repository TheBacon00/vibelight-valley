using UnityEngine;

namespace VibelightValley.Xr;

/// <summary>
/// Conversions between OpenXR (right-handed, +Y up, -Z forward) and Unity (left-handed, +Y up, +Z forward).
/// Mirroring the Z axis: position (x, y, -z), rotation (-x, -y, z, w).
/// </summary>
public static class XrMath
{
    public static Vector3 ToUnity(XrVector3f v) => new(v.X, v.Y, -v.Z);
    public static Quaternion ToUnity(XrQuaternionf q) => new(-q.X, -q.Y, q.Z, q.W);
    public static XrVector3f ToXr(Vector3 v) => new() { X = v.x, Y = v.y, Z = -v.z };
    public static XrQuaternionf ToXr(Quaternion q) => new() { X = -q.x, Y = -q.y, Z = q.z, W = q.w };

    /// <summary>OpenGL-convention asymmetric projection from OpenXR tangent angles (Unity converts for D3D itself).</summary>
    public static Matrix4x4 Projection(XrFovf fov, float near, float far)
    {
        float l = Mathf.Tan(fov.AngleLeft) * near;
        float r = Mathf.Tan(fov.AngleRight) * near;
        float t = Mathf.Tan(fov.AngleUp) * near;
        float b = Mathf.Tan(fov.AngleDown) * near;
        var m = new Matrix4x4();
        m.m00 = 2f * near / (r - l);
        m.m02 = (r + l) / (r - l);
        m.m11 = 2f * near / (t - b);
        m.m12 = (t + b) / (t - b);
        m.m22 = -(far + near) / (far - near);
        m.m23 = -2f * far * near / (far - near);
        m.m32 = -1f;
        return m;
    }

    /// <summary>
    /// The smallest symmetric FOV that contains <paramref name="fov"/>: OpenXR eye frusta are off-centre, but some
    /// shaders assume a centred projection described by fieldOfView + aspect.
    /// </summary>
    public static XrFovf Symmetric(XrFovf fov)
    {
        float h = Mathf.Max(Mathf.Abs(fov.AngleLeft), Mathf.Abs(fov.AngleRight));
        float v = Mathf.Max(Mathf.Abs(fov.AngleUp), Mathf.Abs(fov.AngleDown));
        return new XrFovf { AngleLeft = -h, AngleRight = h, AngleUp = v, AngleDown = -v };
    }

    /// <summary>Yaw-only rotation (around Unity +Y) extracted from an arbitrary rotation.</summary>
    public static Quaternion YawOnly(Quaternion q)
    {
        Vector3 f = q * Vector3.forward;
        f.y = 0;
        if (f.sqrMagnitude < 1e-6f) f = q * Vector3.up * -1f;
        f.y = 0;
        return f.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(f.normalized, Vector3.up);
    }
}

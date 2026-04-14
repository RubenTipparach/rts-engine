using Silk.NET.Maths;

namespace RtsEngine.Core;

/// <summary>
/// Shared matrix utilities.
/// </summary>
public static class MatrixHelper
{
    /// <summary>
    /// Extract raw row-major floats from Matrix4X4.
    /// Memory layout: M11, M12, M13, M14, M21, M22, ...
    ///
    /// Both WGSL (WebGPU) and GLSL (OpenGL) interpret this as column-major,
    /// which auto-transposes — correct for column-vector shader convention.
    /// </summary>
    public static float[] ToRawFloats(Matrix4X4<float> m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    };

    /// <summary>
    /// OpenGL perspective: z maps to [-1, 1].
    /// Use this for desktop OpenGL. WebGPU uses [0,1] which matches
    /// Silk.NET's built-in CreatePerspectiveFieldOfView.
    /// </summary>
    public static Matrix4X4<float> PerspectiveOpenGL(float fovRadians, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovRadians * 0.5f);
        float nf = 1f / (near - far);
        return new Matrix4X4<float>(
            f / aspect, 0,  0,                     0,
            0,          f,  0,                     0,
            0,          0,  (far + near) * nf,     2f * far * near * nf,
            0,          0, -1,                     0);
    }
}

namespace RtsEngine.Game;

/// <summary>
/// Shared cube mesh data — vertices and indices used by all platforms.
/// </summary>
public static class CubeMesh
{
    // 6 faces × 4 verts, each = pos(3f) + color(3f) = 24 bytes/vert
    public static readonly float[] Vertices =
    {
        // Front (red)
        -1, -1,  1,   1.0f, 0.2f, 0.2f,
         1, -1,  1,   1.0f, 0.2f, 0.2f,
         1,  1,  1,   1.0f, 0.4f, 0.4f,
        -1,  1,  1,   1.0f, 0.4f, 0.4f,
        // Back (green)
        -1, -1, -1,   0.2f, 1.0f, 0.2f,
        -1,  1, -1,   0.2f, 1.0f, 0.4f,
         1,  1, -1,   0.4f, 1.0f, 0.4f,
         1, -1, -1,   0.4f, 1.0f, 0.2f,
        // Top (blue)
        -1,  1, -1,   0.2f, 0.2f, 1.0f,
        -1,  1,  1,   0.2f, 0.4f, 1.0f,
         1,  1,  1,   0.4f, 0.4f, 1.0f,
         1,  1, -1,   0.4f, 0.2f, 1.0f,
        // Bottom (yellow)
        -1, -1, -1,   1.0f, 1.0f, 0.2f,
         1, -1, -1,   1.0f, 1.0f, 0.4f,
         1, -1,  1,   1.0f, 1.0f, 0.4f,
        -1, -1,  1,   1.0f, 1.0f, 0.2f,
        // Right (magenta)
         1, -1, -1,   1.0f, 0.2f, 1.0f,
         1,  1, -1,   1.0f, 0.4f, 1.0f,
         1,  1,  1,   1.0f, 0.4f, 1.0f,
         1, -1,  1,   1.0f, 0.2f, 1.0f,
        // Left (cyan)
        -1, -1, -1,   0.2f, 1.0f, 1.0f,
        -1, -1,  1,   0.2f, 1.0f, 1.0f,
        -1,  1,  1,   0.4f, 1.0f, 1.0f,
        -1,  1, -1,   0.4f, 1.0f, 1.0f,
    };

    public static readonly ushort[] Indices =
    {
         0,  1,  2,   0,  2,  3,
         4,  5,  6,   4,  6,  7,
         8,  9, 10,   8, 10, 11,
        12, 13, 14,  12, 14, 15,
        16, 17, 18,  16, 18, 19,
        20, 21, 22,  20, 22, 23,
    };

    public const int IndexCount = 36;
    public const int VertexStride = 24; // 6 floats × 4 bytes
}

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// Sets up and draws a colored cube using the GL proxy.
/// All GPU resource creation and draw calls are plain C# GL.* calls —
/// identical to how you'd write sokol_gfx or raw OpenGL.
/// No JS, no platform awareness.
/// </summary>
public class CubeRenderer
{
    private int _program;
    private int _vbo;
    private int _ibo;
    private int _mvpLocation;

    private const string VertexShaderSrc = @"
        attribute vec3 aPosition;
        attribute vec3 aColor;
        uniform mat4 uMVP;
        varying vec3 vColor;
        void main() {
            gl_Position = uMVP * vec4(aPosition, 1.0);
            vColor = aColor;
        }";

    private const string FragmentShaderSrc = @"
        precision mediump float;
        varying vec3 vColor;
        void main() {
            gl_FragColor = vec4(vColor, 1.0);
        }";

    // Cube: 6 faces × 4 verts, each vert = pos(3) + color(3)
    private static readonly float[] Vertices =
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

    private static readonly ushort[] Indices =
    {
         0,  1,  2,   0,  2,  3,
         4,  5,  6,   4,  6,  7,
         8,  9, 10,   8, 10, 11,
        12, 13, 14,  12, 14, 15,
        16, 17, 18,  16, 18, 19,
        20, 21, 22,  20, 22, 23,
    };

    public async Task Setup()
    {
        // Compile shaders — same flow as native OpenGL / sokol_gfx
        var vs = await CompileShader(GL.VERTEX_SHADER, VertexShaderSrc);
        var fs = await CompileShader(GL.FRAGMENT_SHADER, FragmentShaderSrc);

        _program = await GL.CreateProgram();
        GL.AttachShader(_program, vs);
        GL.AttachShader(_program, fs);
        GL.LinkProgram(_program);
        GL.UseProgram(_program);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        // Create vertex buffer
        _vbo = await GL.CreateBuffer();
        GL.BindBuffer(GL.ARRAY_BUFFER, _vbo);
        GL.BufferDataFloat(GL.ARRAY_BUFFER, Vertices, GL.STATIC_DRAW);

        // Create index buffer
        _ibo = await GL.CreateBuffer();
        GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, _ibo);
        GL.BufferDataUshort(GL.ELEMENT_ARRAY_BUFFER, Indices, GL.STATIC_DRAW);

        // Vertex layout: position(3f) + color(3f), stride = 24 bytes
        var posAttr = await GL.GetAttribLocation(_program, "aPosition");
        GL.EnableVertexAttribArray(posAttr);
        GL.VertexAttribPointer(posAttr, 3, GL.FLOAT, false, 24, 0);

        var colAttr = await GL.GetAttribLocation(_program, "aColor");
        GL.EnableVertexAttribArray(colAttr);
        GL.VertexAttribPointer(colAttr, 3, GL.FLOAT, false, 24, 12);

        _mvpLocation = await GL.GetUniformLocation(_program, "uMVP");

        // GL state
        GL.Enable(GL.DEPTH_TEST);
        GL.ClearColor(0.05f, 0.05f, 0.12f, 1.0f);
    }

    public void Draw(float[] mvpColumnMajor)
    {
        GL.Clear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);
        GL.UniformMatrix4fv(_mvpLocation, false, mvpColumnMajor);
        GL.DrawElements(GL.TRIANGLES, 36, GL.UNSIGNED_SHORT, 0);
    }

    public void Dispose()
    {
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ibo);
        GL.DeleteProgram(_program);
    }

    private static async Task<int> CompileShader(int type, string source)
    {
        var shader = await GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        var ok = await GL.GetShaderParameter(shader, GL.COMPILE_STATUS);
        if (!ok)
        {
            var log = await GL.GetShaderInfoLog(shader);
            throw new Exception($"Shader compile failed: {log}");
        }
        return shader;
    }
}

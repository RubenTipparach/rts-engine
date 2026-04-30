using System.Globalization;

namespace RtsEngine.Game;

/// <summary>
/// Minimal Wavefront .obj parser tailored to the assets baked by
/// tools/gen-rts-models.py: vertex positions, vertex normals, and triangle
/// faces with <c>v//n</c> indexing. Texture coords (vt) and quad faces are
/// not handled; the generator never emits them.
///
/// Output is a flat (pos3 + normal3) interleaved vertex buffer + index
/// buffer matching <see cref="RtsRenderer.VertexFloats"/> so a parsed mesh
/// can be uploaded to the GPU as-is. Each face corner gets its own vertex
/// (no deduplication) so flat shading stays correct without normal-aware
/// merge logic — fine for boxy geometry, fast to parse.
/// </summary>
public static class ObjLoader
{
    public static (float[] verts, ushort[] indices32) Parse(string objText)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var verts = new List<float>();
        var idx = new List<ushort>();
        ushort next = 0;

        var ci = CultureInfo.InvariantCulture;
        foreach (var rawLine in objText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // Tokenize on whitespace.
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            switch (parts[0])
            {
                case "v":
                    if (parts.Length < 4) continue;
                    positions.Add(float.Parse(parts[1], ci));
                    positions.Add(float.Parse(parts[2], ci));
                    positions.Add(float.Parse(parts[3], ci));
                    break;
                case "vn":
                    if (parts.Length < 4) continue;
                    normals.Add(float.Parse(parts[1], ci));
                    normals.Add(float.Parse(parts[2], ci));
                    normals.Add(float.Parse(parts[3], ci));
                    break;
                case "f":
                    // Triangle (or fan-tri-strip the polygon by treating
                    // every subsequent corner as part of (v0, v_{i-1}, v_i).)
                    if (parts.Length < 4) continue;
                    var first = ParseCorner(parts[1]);
                    var prev  = ParseCorner(parts[2]);
                    for (int k = 3; k < parts.Length; k++)
                    {
                        var cur = ParseCorner(parts[k]);
                        EmitCorner(verts, positions, normals, first); idx.Add(next++);
                        EmitCorner(verts, positions, normals, prev);  idx.Add(next++);
                        EmitCorner(verts, positions, normals, cur);   idx.Add(next++);
                        prev = cur;
                    }
                    break;
            }
        }

        return (verts.ToArray(), idx.ToArray());
    }

    private static (int v, int n) ParseCorner(string s)
    {
        // v, v//n, v/vt/n. We only care about v and n.
        int slash1 = s.IndexOf('/');
        if (slash1 < 0)
            return (int.Parse(s, CultureInfo.InvariantCulture) - 1, -1);
        int v = int.Parse(s.AsSpan(0, slash1), CultureInfo.InvariantCulture) - 1;
        int slash2 = s.IndexOf('/', slash1 + 1);
        if (slash2 < 0) return (v, -1); // v/vt
        if (slash2 + 1 >= s.Length) return (v, -1);
        int n = int.Parse(s.AsSpan(slash2 + 1), CultureInfo.InvariantCulture) - 1;
        return (v, n);
    }

    private static void EmitCorner(List<float> verts, List<float> pos, List<float> nrm, (int v, int n) c)
    {
        int pi = c.v * 3;
        verts.Add(pos[pi]); verts.Add(pos[pi + 1]); verts.Add(pos[pi + 2]);
        if (c.n >= 0)
        {
            int ni = c.n * 3;
            verts.Add(nrm[ni]); verts.Add(nrm[ni + 1]); verts.Add(nrm[ni + 2]);
        }
        else
        {
            // No normal in source — emit a default; flat shading will still
            // look OK for static debug geometry.
            verts.Add(0f); verts.Add(1f); verts.Add(0f);
        }
    }
}

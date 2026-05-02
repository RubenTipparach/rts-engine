using RtsEngine.Core;

namespace RtsEngine.Desktop;

/// <summary>
/// Desktop IAssetSource — reads asset files from one or more root folders on
/// disk, mirroring what HttpClient does against wwwroot in the WASM build.
/// Multiple roots so dev runs (where the WASM csproj's runtime mirroring of
/// repo-root /assets → wwwroot/assets/ hasn't happened) can still find .obj
/// models by falling back to the repo root.
///
/// Special case: requests for `*.wgsl` shaders are silently rewritten to
/// `*.glsl` so the OpenGL backend gets the GLSL port. Game code keeps asking
/// for `shaders/terrain.wgsl` and doesn't need to know the difference.
/// </summary>
internal sealed class FileAssetSource : IAssetSource
{
    private readonly string[] _roots;

    public FileAssetSource(params string[] roots) => _roots = roots;

    public Task<string> GetTextAsync(string relativePath)
    {
        // Synchronous on purpose. The desktop GL context is bound to the
        // main thread; truly-async file I/O would yield, the await would
        // resume on a thread-pool thread, and the next GPU call would crash
        // with "no current OpenGL context".
        if (relativePath.EndsWith(".wgsl", StringComparison.OrdinalIgnoreCase))
            relativePath = relativePath.Substring(0, relativePath.Length - 5) + ".glsl";

        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in _roots)
        {
            var full = Path.Combine(root, rel);
            if (File.Exists(full)) return Task.FromResult(File.ReadAllText(full));
        }

        // Throw the same shape exception the WASM HttpClient would throw on
        // 404, so EngineBootstrap.TryBuildRtsAsync's catch clause still skips
        // missing optional assets cleanly.
        throw new FileNotFoundException($"asset not found in any root: {relativePath}");
    }
}

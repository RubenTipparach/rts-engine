namespace RtsEngine.Core;

/// <summary>
/// Platform-agnostic asset reader. WASM resolves paths via HttpClient against
/// wwwroot; Desktop resolves them against a folder on disk. Game code asks for
/// "shaders/terrain.wgsl" or "planets/earth.yaml" and doesn't know the difference.
/// </summary>
public interface IAssetSource
{
    Task<string> GetTextAsync(string relativePath);
}

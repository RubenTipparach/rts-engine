using RtsEngine.Core;

namespace RtsEngine.Wasm.Engine;

/// <summary>
/// WASM IAssetSource — fetches asset paths over HTTP against the Blazor base URL.
/// Mirrors FileAssetSource on desktop, so the same EngineBootstrap path works
/// on both platforms.
/// </summary>
public sealed class HttpAssetSource : IAssetSource
{
    private readonly HttpClient _http;
    public HttpAssetSource(HttpClient http) => _http = http;
    public Task<string> GetTextAsync(string relativePath) => _http.GetStringAsync(relativePath);
}

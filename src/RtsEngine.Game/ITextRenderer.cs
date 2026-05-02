using System.Numerics;

namespace RtsEngine.Game;

/// <summary>
/// Pluggable text renderer for EngineUI. WASM uses HTML overlay buttons
/// whose text is browser-native, so this is desktop-only — Desktop binds
/// FontStash here. EngineUI calls SetCanvasSize() once per frame, then
/// Draw() per visible label.
/// </summary>
public interface ITextRenderer
{
    /// <summary>Canvas dimensions in device pixels. Used to set the ortho
    /// projection so subsequent Draw() calls accept pixel coordinates.</summary>
    void SetCanvasSize(float w, float h);

    /// <summary>Draw a single line of text at (px, py) in pixel space, with
    /// the baseline anchored at py. <paramref name="size"/> is the cap height
    /// in pixels.</summary>
    void DrawLine(string text, float px, float py, float size, Vector4 color);
}

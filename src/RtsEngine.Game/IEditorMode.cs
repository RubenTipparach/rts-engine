namespace RtsEngine.Game;

/// <summary>
/// One scene/gameplay state. GameEngine routes input + the per-frame tick to
/// the active mode based on <see cref="GameEngine.Mode"/>; modes own their
/// own state, render their own frame, and emit events for cross-mode
/// transitions (the engine handles those by changing <see cref="GameEngine.Mode"/>
/// or kicking off a <see cref="ModeTransition"/>).
/// </summary>
public interface IEditorMode
{
    void OnClick(float canvasX, float canvasY, int button);
    void OnMove(float canvasX, float canvasY);
    void OnKey(string key);

    /// <summary>Render this mode's frame. Called from GameEngine's tick loop
    /// only when the mode is active and no transition is running.</summary>
    Task RenderTick(float elapsed);
}

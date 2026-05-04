using System.Numerics;
using RtsEngine.Core;
using RtsEngine.Game;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace RtsEngine.Desktop;

/// <summary>
/// Desktop IRenderBackend — Silk.NET windowing + input + an in-engine EngineUI
/// for buttons (the desktop equivalent of the browser's HTML overlay). Routes
/// raw mouse/keyboard input into the higher-level RTS-style events the engine
/// consumes:
///
///   left click           → PointerClick(button=0)
///   left drag            → BoxSelectUpdate per move + BoxSelectComplete on release
///   left drag + Alt held → OrbitDrag (camera orbit)
///   middle drag          → OrbitDrag
///   right click (short)  → PointerClick(button=2)        (move/attack order)
///   right hold (long)    → ContextMenuRequested          (unit context menu)
///
/// UI hit-test runs first on left clicks; if a button is hit, UIButtonClick
/// fires and the world click is consumed.
/// </summary>
internal sealed class DesktopAppBackend : IRenderBackend
{
    private const float ClickThreshold = 5f;
    private const float LongPressMs = 350f;

    private readonly IWindow _window;
    private Func<Task>? _onTick;
    private EngineUI? _ui;
    private DateTime _now = DateTime.UtcNow;

    public float CanvasWidth => _window.Size.X;
    public float CanvasHeight => _window.Size.Y;

    public event Action? PointerDown;
    public event Action<float, float>? PointerDrag;
    public event Action? PointerUp;
    public event Action<float, float, int>? PointerClick;
    public event Action<float>? Scroll;
    public event Action<float, float>? PointerMove;
    public event Action<string>? KeyDown;
    public event Action<string>? UIButtonClick;
    public event Action<float, float>? OrbitDrag;
    public event Action<float, float, float, float>? BoxSelectUpdate;
    public event Action<float, float, float, float>? BoxSelectComplete;
    public event Action<float, float>? ContextMenuRequested;

    public void CreateUIButton(string id, string text, string cssJson)
        => _ui?.AddOrUpdate(id, text, cssJson);

    public void ShowUIButton(string id, bool visible)
        => _ui?.SetVisible(id, visible);

    public void AttachEngineUI(EngineUI ui) => _ui = ui;

    public void RenderUI()
    {
        if (_ui == null) return;
        _ui.SetCanvasSize(CanvasWidth, CanvasHeight);
        _ui.SyncBuffers().GetAwaiter().GetResult();
        _ui.Draw();
    }

    public bool ConsumesPointerClick(float cx, float cy)
        => _ui?.HitTest(cx, cy) != null;

    // Per-button drag state. Each tracked independently so e.g. left held +
    // middle pressed wouldn't tangle.
    private struct ButtonState
    {
        public bool Down;
        public Vector2 LastPos;
        public Vector2 DownPos;
        public DateTime DownTime;
        public float TotalDragDist;
        public bool Dragging;     // crossed the click threshold this hold
        public bool ConsumedAsOrbit; // distinguishes alt+left orbit from box select
    }
    private ButtonState _left, _middle, _right;
    private bool _altHeld;

    public DesktopAppBackend(IWindow window)
    {
        _window = window;
        var input = window.CreateInput();
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, btn) => OnDown(btn, mouse.Position);
            mouse.MouseUp   += (m, btn) => OnUp(btn, mouse.Position);
            mouse.MouseMove += (m, pos) => OnMove(pos);
            mouse.Scroll    += (m, wheel) => Scroll?.Invoke(wheel.Y * 120f);
        }
        foreach (var kb in input.Keyboards)
        {
            kb.KeyDown += (k, key, _) =>
            {
                if (key == Key.AltLeft || key == Key.AltRight) _altHeld = true;
                KeyDown?.Invoke(key.ToString());
            };
            kb.KeyUp += (k, key, _) =>
            {
                if (key == Key.AltLeft || key == Key.AltRight) _altHeld = false;
            };
        }
    }

    private void OnDown(MouseButton btn, Vector2 pos)
    {
        ref var s = ref Pick(btn);
        s.Down = true;
        s.DownPos = pos;
        s.LastPos = pos;
        s.DownTime = DateTime.UtcNow;
        s.TotalDragDist = 0;
        s.Dragging = false;
        s.ConsumedAsOrbit = btn == MouseButton.Middle || (btn == MouseButton.Left && _altHeld);

        if (btn == MouseButton.Left) PointerDown?.Invoke();
    }

    private void OnUp(MouseButton btn, Vector2 pos)
    {
        ref var s = ref Pick(btn);
        if (!s.Down) return;
        s.Down = false;
        var heldMs = (float)(DateTime.UtcNow - s.DownTime).TotalMilliseconds;

        if (btn == MouseButton.Left)
        {
            PointerUp?.Invoke();
            if (s.Dragging && !s.ConsumedAsOrbit)
            {
                // Box-select drag finished — fire complete with normalised rect.
                float x0 = MathF.Min(s.DownPos.X, pos.X);
                float y0 = MathF.Min(s.DownPos.Y, pos.Y);
                float x1 = MathF.Max(s.DownPos.X, pos.X);
                float y1 = MathF.Max(s.DownPos.Y, pos.Y);
                BoxSelectComplete?.Invoke(x0, y0, x1, y1);
                return;
            }
            if (s.Dragging) return; // consumed as orbit; no click semantics

            // True click. UI gets first crack so buttons absorb it.
            if (_ui != null)
            {
                var hit = _ui.HitTest(pos.X, pos.Y);
                if (hit != null) { UIButtonClick?.Invoke(hit); return; }
            }
            PointerClick?.Invoke(pos.X, pos.Y, 0);
        }
        else if (btn == MouseButton.Right)
        {
            // Long press → context menu. Short press → fire as a click so the
            // engine's existing right-click move-order path stays intact.
            if (s.Dragging) return; // we don't drag-route the right button
            if (heldMs >= LongPressMs)
                ContextMenuRequested?.Invoke(pos.X, pos.Y);
            else
                PointerClick?.Invoke(pos.X, pos.Y, 2);
        }
        // Middle button up: nothing — its drag was already routed as OrbitDrag.
    }

    private void OnMove(Vector2 pos)
    {
        PointerMove?.Invoke(pos.X, pos.Y);

        // Update each held button's drag state. Drag detection has to be per-
        // button because left+middle could be held simultaneously, and we want
        // to keep their roles distinct.
        UpdateDrag(ref _left,   MouseButton.Left,   pos);
        UpdateDrag(ref _middle, MouseButton.Middle, pos);
        // Right has no drag semantics, but track for click-vs-long detection.
        if (_right.Down)
        {
            var d = pos - _right.LastPos;
            _right.TotalDragDist += MathF.Abs(d.X) + MathF.Abs(d.Y);
            _right.LastPos = pos;
            if (_right.TotalDragDist >= ClickThreshold) _right.Dragging = true;
        }
    }

    private void UpdateDrag(ref ButtonState s, MouseButton btn, Vector2 pos)
    {
        if (!s.Down) return;
        var d = pos - s.LastPos;
        s.TotalDragDist += MathF.Abs(d.X) + MathF.Abs(d.Y);
        s.LastPos = pos;
        if (s.TotalDragDist >= ClickThreshold) s.Dragging = true;
        if (!s.Dragging) return;

        if (s.ConsumedAsOrbit)
        {
            OrbitDrag?.Invoke(d.X, d.Y);
            // Also fire PointerDrag so any platform-agnostic listeners that
            // haven't migrated to OrbitDrag still get camera input. WASM still
            // routes orbit through PointerDrag.
            PointerDrag?.Invoke(d.X, d.Y);
        }
        else if (btn == MouseButton.Left)
        {
            BoxSelectUpdate?.Invoke(s.DownPos.X, s.DownPos.Y, pos.X, pos.Y);
        }
        else if (btn == MouseButton.Middle)
        {
            // Middle drag was already classified as orbit at down-time.
            OrbitDrag?.Invoke(d.X, d.Y);
            PointerDrag?.Invoke(d.X, d.Y);
        }
    }

    private ref ButtonState Pick(MouseButton btn)
    {
        // Switch expressions don't allow `ref` returns; use a plain branch.
        if (btn == MouseButton.Left)  return ref _left;
        if (btn == MouseButton.Right) return ref _right;
        return ref _middle;
    }

    public void StartLoop(Func<Task> onTick) => _onTick = onTick;
    public void StopLoop() => _onTick = null;
    public void Tick() => _onTick?.Invoke();
    public void Dispose() { }
}

using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// In-engine HUD controller — owns which buttons exist, when they're
/// visible, the zoom indicator, the box-select rectangle overlay, and the
/// unit context menu. <see cref="EngineUI"/> is the GPU-side renderer; this
/// class decides what to render and forwards UI clicks back as semantic
/// events. GameEngine subscribes to those events and stays out of CSS.
/// </summary>
public sealed class EngineHud
{
    private readonly IRenderBackend _app;
    private readonly PlanetCamera _camera;
    private readonly RtsState _state;
    private readonly RtsConfig? _rtsConfig;
    private readonly Func<float> _radiusProvider;
    private readonly Func<EditorMode> _modeProvider;
    private readonly Func<bool> _transitioningProvider;

    private const float ContextMenuItemHeight = 32f;
    private static readonly string[] ContextMenuItems =
        { "Cancel order", "Guard", "Move", "Attack" };

    /// <summary>Edit-mode toggle — true while the player has clicked "✏ Edit"
    /// to enable terrain raise/lower. Owned here because it's literally the
    /// state of the Edit button; GameEngine reads it to gate input policy.</summary>
    public bool EditMode { get; private set; }

    public event Action? BackSolarClicked;
    public event Action? EditToggled;
    public event Action<string>? BuildPlacementClicked;
    public event Action? PlacementCancelClicked;
    public event Action<string>? ProduceUnitClicked;
    public event Action<int>? ContextMenuPicked;

    public EngineHud(IRenderBackend app, PlanetCamera camera, RtsState state,
        RtsConfig? rtsConfig, Func<float> radiusProvider,
        Func<EditorMode> modeProvider, Func<bool> transitioningProvider)
    {
        _app = app;
        _camera = camera;
        _state = state;
        _rtsConfig = rtsConfig;
        _radiusProvider = radiusProvider;
        _modeProvider = modeProvider;
        _transitioningProvider = transitioningProvider;
    }

    public void Setup()
    {
        var css = @"{
            ""top"":""10px"",""left"":""10px"",
            ""padding"":""10px 20px"",""fontSize"":""16px"",
            ""background"":""rgba(10,40,60,0.9)"",""color"":""#0ff"",
            ""border"":""1px solid #0ff"",""borderRadius"":""6px"",
            ""cursor"":""pointer"",""display"":""none"",
            ""fontFamily"":""monospace""
        }";
        var editCss = @"{
            ""top"":""10px"",""left"":""200px"",
            ""padding"":""10px 20px"",""fontSize"":""16px"",
            ""background"":""rgba(40,30,10,0.9)"",""color"":""#fc6"",
            ""border"":""1px solid #fc6"",""borderRadius"":""6px"",
            ""cursor"":""pointer"",""display"":""none"",
            ""fontFamily"":""monospace""
        }";
        _app.CreateUIButton("back_solar", "⬅ Solar System", css);
        _app.CreateUIButton("edit_toggle", "✏ Edit", editCss);

        // RTS build bar — one button per building type, laid out along the
        // bottom edge. Produce buttons (per selected building) and the
        // cancel-placement button are created lazily on demand.
        if (_rtsConfig != null)
        {
            for (int i = 0; i < _rtsConfig.Buildings.Count; i++)
            {
                var b = _rtsConfig.Buildings[i];
                _app.CreateUIButton($"build_{b.Id}", $"🏗 {b.Name}", BuildButtonCss(i, active: false));
                _app.ShowUIButton($"build_{b.Id}", false);
            }
            _app.CreateUIButton("cancel_placement", "✕ Cancel Placement", CancelPlacementCss());
            _app.ShowUIButton("cancel_placement", false);

            // Pre-create produce buttons too so we can just toggle visibility.
            // Worst case = max(produces) buttons; create one per unit type and
            // re-label as needed. Simpler: one button per unique unit id.
            var unitIds = _rtsConfig.Units.Select(u => u.Id).Distinct().ToList();
            for (int i = 0; i < unitIds.Count; i++)
            {
                _app.CreateUIButton($"produce_{unitIds[i]}", "", ProduceButtonCss(i));
                _app.ShowUIButton($"produce_{unitIds[i]}", false);
            }
        }

        _app.UIButtonClick += OnUIButton;
    }

    /// <summary>Per-frame visibility + zoom HUD sync. Single entry point —
    /// GameEngine calls this once at the end of each tick. Internally gates
    /// on Mode==PlanetEdit && !transitioning.</summary>
    public void Sync()
    {
        bool inPlanet = _modeProvider() == EditorMode.PlanetEdit && !_transitioningProvider();
        _app.ShowUIButton("back_solar", inPlanet);
        _app.ShowUIButton("edit_toggle", inPlanet);
        UpdateRtsButtonVisibility(inPlanet);
        UpdateZoomIndicator(inPlanet);
    }

    /// <summary>Full refresh — rewrites build/produce labels + CSS to reflect
    /// placement highlights and the produce-bar slot order. Called from event
    /// handlers (build/produce/cancel clicks, building selection).</summary>
    public void RefreshRtsButtons()
    {
        if (_rtsConfig == null) return;

        for (int i = 0; i < _rtsConfig.Buildings.Count; i++)
        {
            var b = _rtsConfig.Buildings[i];
            bool active = _state.PlacementBuildingId == b.Id;
            _app.CreateUIButton($"build_{b.Id}", (active ? "▶ " : "🏗 ") + b.Name,
                BuildButtonCss(i, active));
        }

        var selected = _state.SelectedBuilding;
        var selectedDef = selected != null ? _rtsConfig.GetBuilding(selected.TypeId) : null;
        if (selectedDef != null)
        {
            for (int slot = 0; slot < selectedDef.Produces.Count; slot++)
            {
                var uid = selectedDef.Produces[slot];
                var unitDef = _rtsConfig.GetUnit(uid);
                _app.CreateUIButton($"produce_{uid}", $"⚙ {unitDef?.Name ?? uid}",
                    ProduceButtonCss(slot));
            }
        }

        Sync();
    }

    public void ShowBoxSelectOverlay(float x0, float y0, float x1, float y1)
    {
        float left = MathF.Min(x0, x1);
        float top = MathF.Min(y0, y1);
        float w = MathF.Abs(x1 - x0);
        float h = MathF.Abs(y1 - y0);
        // Fill: vivid green at 20% alpha for the interior so the world stays
        // readable through the rectangle. Border: opaque bright green outline
        // 2px wide so the edge of the selection is unmistakable even when the
        // fill blends into a similarly-coloured terrain patch.
        var css = "{" +
            "\"left\":\"" + (int)left + "px\",\"top\":\"" + (int)top + "px\"," +
            "\"width\":\"" + (int)w + "px\",\"height\":\"" + (int)h + "px\"," +
            "\"background\":\"rgba(50,220,100,0.20)\"," +
            "\"borderColor\":\"rgba(60,255,110,1.0)\"," +
            "\"borderWidth\":\"2px\"," +
            "\"pointerEvents\":\"none\",\"display\":\"block\"}";
        _app.CreateUIButton("box_select", "", css);
        _app.ShowUIButton("box_select", true);
    }

    public void HideBoxSelectOverlay() => _app.ShowUIButton("box_select", false);

    /// <summary>Pop up a 4-item context menu near the cursor. No-op if there
    /// are no selected units — caller is expected to check first.</summary>
    public void ShowContextMenu(float x, float y)
    {
        for (int i = 0; i < ContextMenuItems.Length; i++)
        {
            float top = y + i * ContextMenuItemHeight;
            var css = "{" +
                "\"left\":\"" + (int)x + "px\",\"top\":\"" + (int)top + "px\"," +
                "\"width\":\"140px\",\"height\":\"" + (int)ContextMenuItemHeight + "px\"," +
                "\"background\":\"rgba(20,25,35,0.95)\"," +
                "\"color\":\"#cde\"," +
                "\"display\":\"block\"}";
            _app.CreateUIButton($"ctx_{i}", ContextMenuItems[i], css);
            _app.ShowUIButton($"ctx_{i}", true);
        }
    }

    public void HideContextMenu()
    {
        for (int i = 0; i < ContextMenuItems.Length; i++)
            _app.ShowUIButton($"ctx_{i}", false);
    }

    private void OnUIButton(string id)
    {
        if (id == "back_solar") BackSolarClicked?.Invoke();
        else if (id == "edit_toggle")
        {
            EditMode = !EditMode;
            // Reuse CreateUIButton to update the label — JS overlay swaps the
            // textContent on the existing element.
            var editCss = @"{
                ""top"":""10px"",""left"":""200px"",
                ""padding"":""10px 20px"",""fontSize"":""16px"",""cursor"":""pointer"",
                ""border"":""1px solid #fc6"",""borderRadius"":""6px"",""fontFamily"":""monospace"",
                ""display"":""block"",
                ""background"":""" + (EditMode ? "rgba(80,40,10,0.95)" : "rgba(40,30,10,0.9)") + @""",
                ""color"":""" + (EditMode ? "#ffd080" : "#fc6") + @"""
            }";
            _app.CreateUIButton("edit_toggle", EditMode ? "✓ Editing" : "✏ Edit", editCss);
            EditToggled?.Invoke();
        }
        else if (id == "cancel_placement") PlacementCancelClicked?.Invoke();
        else if (id.StartsWith("build_") && _rtsConfig != null)
            BuildPlacementClicked?.Invoke(id.Substring("build_".Length));
        else if (id.StartsWith("produce_") && _rtsConfig != null)
            ProduceUnitClicked?.Invoke(id.Substring("produce_".Length));
        else if (id.StartsWith("ctx_"))
        {
            if (int.TryParse(id.Substring("ctx_".Length), out var idx))
                ContextMenuPicked?.Invoke(idx);
        }
    }

    /// <summary>Cheap visibility-only toggle — no label/CSS rewrites.</summary>
    private void UpdateRtsButtonVisibility(bool inPlanet)
    {
        if (_rtsConfig == null) return;

        foreach (var b in _rtsConfig.Buildings)
            _app.ShowUIButton($"build_{b.Id}", inPlanet);
        _app.ShowUIButton("cancel_placement", inPlanet && _state.PlacementBuildingId != null);

        var selected = _state.SelectedBuilding;
        var selectedDef = selected != null ? _rtsConfig.GetBuilding(selected.TypeId) : null;
        var enabled = inPlanet && selectedDef != null ? selectedDef.Produces : new List<string>();
        foreach (var u in _rtsConfig.Units)
            _app.ShowUIButton($"produce_{u.Id}", enabled.Contains(u.Id));
    }

    /// <summary>Heads-up zoom indicator on the left edge — vertical bar fills
    /// as we approach the surface, plus numerical distance / altitude / zoom%
    /// / tilt% / pitch° readouts so designers can dial in camera tuning by
    /// eye. Hidden outside PlanetEdit. Recreates the button each frame —
    /// cheap because the JS side reuses the existing DOM element and only
    /// touches text + style properties.</summary>
    private void UpdateZoomIndicator(bool show)
    {
        if (!show)
        {
            _app.ShowUIButton("zoom_indicator", false);
            return;
        }

        float dist = _camera.Distance;
        float altitude = dist - _radiusProvider();

        // Reuse the same helpers the camera does so the indicator is the
        // source of truth for tuning — what you read here is what the tilt
        // and the smooth-up basis are reacting to.
        float zoomPct = _camera.ZoomPercent();
        int zoomInt = (int)MathF.Round(zoomPct * 100f);
        float tiltBlend = _camera.TiltBlend();
        int tiltInt = (int)MathF.Round(tiltBlend * 100f);
        int pitchInt = (int)MathF.Round(_camera.PitchDegrees());

        // Multiline text on a button with white-space:pre. \n becomes a real
        // newline thanks to that style. P = pitch off the horizon — 0° at
        // grazing, 90° looking straight down at the surface.
        string label = $"ZOOM\nD {dist:F2}\nA {altitude:F3}\nZ {zoomInt,3}%\nT {tiltInt,3}%\nP {pitchInt,3}°";

        // Linear gradient with a hard color stop at zoomInt% gives a clean
        // bar look without needing two separate elements.
        string css = "{" +
            "\"left\":\"10px\",\"top\":\"80px\"," +
            "\"width\":\"80px\",\"height\":\"220px\"," +
            "\"padding\":\"8px 6px\"," +
            "\"background\":\"linear-gradient(to top, rgba(80,200,120,0.75) " + zoomInt + "%, rgba(20,40,30,0.85) " + zoomInt + "%)\"," +
            "\"color\":\"#dfe\",\"fontSize\":\"11px\"," +
            "\"border\":\"1px solid #4a8\",\"borderRadius\":\"6px\"," +
            "\"fontFamily\":\"monospace\"," +
            "\"textAlign\":\"left\"," +
            "\"whiteSpace\":\"pre\"," +
            "\"pointerEvents\":\"none\"," +
            "\"lineHeight\":\"1.45\"," +
            "\"display\":\"block\"" +
        "}";

        _app.CreateUIButton("zoom_indicator", label, css);
        _app.ShowUIButton("zoom_indicator", true);
    }

    private static string BuildButtonCss(int slot, bool active)
    {
        int left = 10 + slot * 180;
        var bg = active ? "rgba(80,40,10,0.95)" : "rgba(20,40,30,0.9)";
        var col = active ? "#ffd080" : "#9fc";
        return @"{""bottom"":""10px"",""left"":""" + left + @"px""," +
               @"""padding"":""10px 16px"",""fontSize"":""14px""," +
               @"""background"":""" + bg + @"""," +
               @"""color"":""" + col + @"""," +
               @"""border"":""1px solid #4a8"",""borderRadius"":""6px""," +
               @"""cursor"":""pointer"",""display"":""none""," +
               @"""fontFamily"":""monospace""}";
    }

    private static string ProduceButtonCss(int slot)
    {
        int top = 60 + slot * 50;
        return @"{""top"":""" + top + @"px"",""right"":""10px""," +
               @"""padding"":""8px 14px"",""fontSize"":""13px""," +
               @"""background"":""rgba(40,20,40,0.9)"",""color"":""#fac""," +
               @"""border"":""1px solid #c6a"",""borderRadius"":""6px""," +
               @"""cursor"":""pointer"",""display"":""none""," +
               @"""fontFamily"":""monospace""}";
    }

    private static string CancelPlacementCss()
    {
        return @"{""bottom"":""60px"",""left"":""10px""," +
               @"""padding"":""8px 14px"",""fontSize"":""13px""," +
               @"""background"":""rgba(60,20,20,0.9)"",""color"":""#fcc""," +
               @"""border"":""1px solid #c66"",""borderRadius"":""6px""," +
               @"""cursor"":""pointer"",""display"":""none""," +
               @"""fontFamily"":""monospace""}";
    }
}

using RtsEngine.Core;

namespace RtsEngine.Game;

/// <summary>
/// Platform-agnostic engine bootstrap. Loads YAML configs, shaders, and .obj
/// models via an IAssetSource, builds renderers, and returns a wired-up
/// GameEngine.
///
/// WASM and Desktop both call this — the only difference is which IAssetSource
/// they pass in (HttpAssetSource vs FileAssetSource).
///
/// Originally lived in WASM Home.razor as ~150 lines of inline @code; lifted
/// here so Desktop doesn't have to reimplement (or fall behind) the same setup.
/// </summary>
public sealed class EngineBootstrap
{
    private readonly IGPU _gpu;
    private readonly IRenderBackend _backend;
    private readonly IAssetSource _assets;

    public EngineConfig EngineConfig { get; private set; } = new();
    public PlanetRenderer? Planet { get; private set; }
    public StarMapRenderer? StarMap { get; private set; }
    public SolarSystemRenderer? SolarSystem { get; private set; }
    public RtsRenderer? Rts { get; private set; }
    public RtsConfig? RtsConfig { get; private set; }
    public EngineUI? UI { get; private set; }
    public GameEngine? Engine { get; private set; }

    /// <summary>If true, attempts to set up SolarSystem + StarMap as well as Planet.</summary>
    public bool BuildAllRenderers { get; init; } = true;

    /// <summary>If true, attempts to load rts.yaml + rts.wgsl + .obj models.
    /// Falls back gracefully (Rts = null) if any piece is missing.</summary>
    public bool BuildRtsLayer { get; init; } = true;

    /// <summary>If true, builds an EngineUI (in-engine GPU buttons) — desktop's
    /// equivalent of the browser HTML overlay. WASM defaults this off because
    /// it uses real DOM buttons via app-shell.js.</summary>
    public bool BuildEngineUI { get; init; } = false;

    /// <summary>
    /// Optional hook fired right after EngineUI is built but BEFORE
    /// GameEngine.SetupUI() runs. Desktop hosts wire EngineUI into their
    /// IRenderBackend here so the CreateUIButton calls SetupUI emits land
    /// in the EngineUI list — attaching after SetupAsync would miss them.
    /// </summary>
    public Action<EngineUI>? OnEngineUIBuilt { get; init; }

    /// <summary>Default planet config to load on startup.</summary>
    public string DefaultPlanetConfig { get; init; } = "planets/earth.yaml";

    public EngineBootstrap(IGPU gpu, IRenderBackend backend, IAssetSource assets)
    {
        _gpu = gpu;
        _backend = backend;
        _assets = assets;
    }

    /// <summary>
    /// Run the full bootstrap sequence: engine config → solar system + star
    /// map → planet (with slopes) → RTS layer → GameEngine. After this
    /// returns, callers should attach hot-swap callbacks (e.g. via
    /// <see cref="HookPlanetHotSwap"/>) and call <c>Engine.Run()</c>.
    /// </summary>
    public async Task SetupAsync()
    {
        Trace($"SetupAsync(buildAll={BuildAllRenderers}, rts={BuildRtsLayer}, planet={DefaultPlanetConfig})");

        // Engine config (camera tuning, slope density, RTS camera). Loaded
        // BEFORE the planet so slope generation has the density value at
        // mesh-build time.
        try
        {
            EngineConfig = EngineConfig.FromYaml(await _assets.GetTextAsync("config/engine.yaml"));
            Trace($"engine config loaded (debug.showUnitPaths={EngineConfig.Debug.ShowUnitPaths})");
        }
        catch (Exception e)
        {
            Trace($"engine config missing/invalid, using defaults: {e.Message}");
            EngineConfig = new EngineConfig();
        }

        if (BuildAllRenderers)
        {
            Trace("loading solar system + star map shaders");
            // Solar system layout — orbit radii, display radii, sun size,
            // noise params — comes from config/solarsystem.yaml. Per-planet
            // LevelColors get patched in afterwards by reading each
            // referenced planet config (so Glacius gets ice colors, Mars
            // gets red rock, etc., without duplicating them in the solar
            // system YAML).
            SolarSystemData solarData;
            try
            {
                var ssYaml = await _assets.GetTextAsync("config/solarsystem.yaml");
                solarData = SolarSystemData.FromYaml(ssYaml);
                await PatchSolarSystemLevelColors(solarData);
                Trace($"solar system config loaded ({solarData.Planets.Count} planets)");
            }
            catch (Exception e)
            {
                Trace($"solarsystem.yaml load failed, using built-in default: {e.Message}");
                solarData = SolarSystemData.CreateDefault();
            }
            var solarShader = await _assets.GetTextAsync("shaders/solarsystem.wgsl");
            var outlineShader = await _assets.GetTextAsync("shaders/outline.wgsl");
            var sunShader = await _assets.GetTextAsync("shaders/sun.wgsl");
            var starfieldShader = await _assets.GetTextAsync("shaders/starfield.wgsl");
            SolarSystem = new SolarSystemRenderer(_gpu, solarData, EngineConfig);
            await SolarSystem.Setup(solarShader, outlineShader, sunShader, starfieldShader);
            Trace("solar system ready");

            var galaxy = GalaxyData.Generate(seed: 42);
            var starmapShader = await _assets.GetTextAsync("shaders/starmap.wgsl");
            StarMap = new StarMapRenderer(_gpu, galaxy);
            await StarMap.Setup(starmapShader);
            Trace("star map ready");
        }

        Trace("building planet");
        Planet = await BuildPlanetAsync(DefaultPlanetConfig);
        Trace($"planet ready ({Planet.Mesh.CellCount} cells)");

        if (BuildRtsLayer)
            await TryBuildRtsAsync();

        if (BuildEngineUI)
        {
            try
            {
                var uiShader = await _assets.GetTextAsync("shaders/ui.wgsl");
                UI = new EngineUI(_gpu);
                await UI.Setup(uiShader);
                Trace("engine UI ready");
                // Attach to the host backend BEFORE the engine starts emitting
                // CreateUIButton calls — otherwise SetupUI's button definitions
                // hit a null sink and the EngineUI list ends up nearly empty.
                // Host also gets to bind a text renderer (FontStash on desktop)
                // via UI.Text in the OnEngineUIBuilt callback before SetupUI runs.
                OnEngineUIBuilt?.Invoke(UI);
            }
            catch (Exception e)
            {
                Trace($"engine UI skipped: {e.Message}");
                UI = null;
            }
        }

        Engine = new GameEngine(_backend, _gpu, Planet, StarMap, SolarSystem,
            EngineConfig, Rts, RtsConfig);
        Engine.Mode = BuildAllRenderers ? EditorMode.SolarSystem : EditorMode.PlanetEdit;
        Engine.SetupUI();
        Trace($"engine ready, mode={Engine.Mode}");
    }

    /// <summary>Walk every body in <paramref name="solar"/> (planets + their
    /// moons) and replace its <c>LevelColors</c> with the colors pulled from
    /// the body's per-planet YAML — keeps the solar-system noise sphere's
    /// terrain palette in sync with the actual planet without duplicating
    /// the colors in solarsystem.yaml.</summary>
    private async Task PatchSolarSystemLevelColors(SolarSystemData solar)
    {
        foreach (var p in solar.Planets)
        {
            await PatchOne(p);
            foreach (var m in p.Moons) await PatchOne(m);
        }

        async Task PatchOne(OrbitalBody body)
        {
            if (string.IsNullOrEmpty(body.ConfigFile)) return;
            try
            {
                var py = await _assets.GetTextAsync(body.ConfigFile);
                var cfg = PlanetConfig.FromYaml(py);
                var levels = cfg.Terrain.Levels;
                if (levels.Count == 0) return;
                var arr = new System.Numerics.Vector3[levels.Count];
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = levels[i].Color;
                    arr[i] = c.Count >= 3
                        ? new System.Numerics.Vector3(c[0], c[1], c[2])
                        : System.Numerics.Vector3.One;
                }
                body.LevelColors = arr;
            }
            catch { /* fall back to whatever LevelColors FromYaml left in place */ }
        }
    }

    /// <summary>
    /// RTS gameplay (buildings + units). Optional — falls back to a pure
    /// planet editor if the config, shader, or model files are missing.
    /// </summary>
    private async Task TryBuildRtsAsync()
    {
        try
        {
            var rtsYaml = await _assets.GetTextAsync("config/rts.yaml");
            RtsConfig = RtsConfig.FromYaml(rtsYaml);
            var rtsShader = await _assets.GetTextAsync("shaders/rts.wgsl");
            var uiShader = await _assets.GetTextAsync("shaders/ui.wgsl");
            var lineShader = await _assets.GetTextAsync("shaders/outline.wgsl");

            // Baked .obj models from assets/models/ replace the procedural
            // boxes when available. Missing files fall back to MakeBox but
            // we log a warning per-entity so a stray "everything is a cube"
            // bug is loud rather than silent — usually means the asset
            // pipeline didn't surface the .obj in the build output.
            var objs = new Dictionary<string, string>();
            var missing = new List<string>();
            foreach (var entityId in RtsConfig.Buildings.Select(b => b.Id)
                                      .Concat(RtsConfig.Units.Select(u => u.Id)))
            {
                try { objs[entityId] = await _assets.GetTextAsync($"assets/models/{entityId}.obj"); }
                catch (Exception modelEx) { missing.Add($"{entityId} ({modelEx.Message})"); }
            }
            if (missing.Count > 0)
                Trace($"WARNING: {missing.Count} model(s) missing, falling back to procedural box: {string.Join(", ", missing)}");

            Rts = new RtsRenderer(_gpu, RtsConfig, EngineConfig);
            await Rts.Setup(rtsShader, uiShader, lineShader, objs);
            Trace($"rts ready ({RtsConfig.Buildings.Count} buildings, {RtsConfig.Units.Count} units, {objs.Count} obj models)");
        }
        catch (Exception e)
        {
            Trace($"rts layer skipped: {e.Message}");
            Rts = null;
            RtsConfig = null;
        }
    }

    /// <summary>
    /// Wire the planet hot-swap callback so OnFrameRendered triggers a config
    /// load + renderer rebuild when the player picks a different planet from
    /// the solar system view. Same logic that previously lived in Home.razor.
    /// </summary>
    public void HookPlanetHotSwap()
    {
        if (Engine == null) return;

        string current = DefaultPlanetConfig;
        bool loading = false;

        Engine.OnFrameRendered += () =>
        {
            var wanted = Engine.SelectedPlanetConfig;
            if (wanted == null || loading) return;

            if (wanted == current)
            {
                Engine.SwitchToPlanetEdit();
            }
            else
            {
                Trace($"hot-swap planet: {current} → {wanted}");
                loading = true;
                _ = SwapAsync(wanted);
            }

            async Task SwapAsync(string configPath)
            {
                try
                {
                    var newPlanet = await BuildPlanetAsync(configPath);
                    var old = Planet;
                    Planet = newPlanet;
                    Engine.SetPlanetRenderer(newPlanet);
                    Engine.SwitchToPlanetEdit();
                    old?.Dispose();
                    current = configPath;
                    Trace($"hot-swap done: {configPath}");
                }
                catch (Exception e)
                {
                    Trace($"hot-swap FAILED for {configPath}: {e.Message}");
                    Trace(e.StackTrace ?? "");
                }
                finally { loading = false; }
            }
        };
    }

    /// <summary>
    /// Build a PlanetRenderer for the given YAML config path. Loads the
    /// config, generates the noise mesh, places procedural slope ramps, and
    /// sets up terrain + atmosphere + outline shaders.
    /// </summary>
    public async Task<PlanetRenderer> BuildPlanetAsync(string configPath)
    {
        var yaml = await _assets.GetTextAsync(configPath);
        var config = PlanetConfig.FromYaml(yaml);
        var terrainShader = await _assets.GetTextAsync("shaders/terrain.wgsl");

        var mesh = new PlanetMesh(
            subdivisions: config.Subdivisions,
            radius: config.Radius,
            stepHeight: config.StepHeight,
            chamferInset: EngineConfig.Terrain.ChamferInset,
            chamferDrop: EngineConfig.Terrain.ChamferDrop);
        mesh.GenerateFromNoise(
            seed: config.Generation.Seed,
            frequency: config.Generation.Frequency,
            thresholds: config.Generation.Thresholds.ToArray());

        // Procedural slope ramps at elevation borders so ground units have a
        // way down without needing to hop. Seeded by the planet seed combined
        // with a config-driven offset so terrain and slopes stay independent.
        SlopeGenerator.Generate(mesh,
            seed: config.Generation.Seed ^ EngineConfig.Slopes.SeedOffset,
            density: EngineConfig.Slopes.Density);

        var renderer = new PlanetRenderer(_gpu, mesh);
        renderer.ApplyConfig(config);
        await renderer.Setup(terrainShader);

        var atmoShader = await _assets.GetTextAsync("shaders/atmosphere.wgsl");
        await renderer.SetupAtmosphere(atmoShader);

        var outlineShader = await _assets.GetTextAsync("shaders/outline.wgsl");
        await renderer.SetupOutline(outlineShader);

        return renderer;
    }

    private static void Trace(string msg) => Console.Error.WriteLine($"[boot] {msg}");
}

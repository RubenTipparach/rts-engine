using RtsEngine.Game;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace RtsEngine.Desktop;

/// <summary>
/// Desktop host — the equivalent of WASM Home.razor. Brings up a Silk.NET
/// window + GL context, then defers to the shared EngineBootstrap so game
/// setup logic stays in one place.
/// </summary>
public static class Program
{
    public static void Main()
    {
        var opts = WindowOptions.Default;
        opts.Size = new Vector2D<int>(1280, 720);
        opts.Title = "RTS Engine - Silk.NET Desktop";
        // 4.2 lets us use `layout(binding = N)` qualifiers on UBOs and
        // sampler uniforms — matches WebGPU's binding-slot model 1:1 without
        // per-program glUniformBlockBinding/glUniform1i boilerplate.
        opts.API = new GraphicsAPI(ContextAPI.OpenGL, new APIVersion(4, 2));

        var window = Window.Create(opts);
        OpenGLGPU? gpu = null;
        DesktopAppBackend? backend = null;
        EngineBootstrap? boot = null;

        window.Load += async () =>
        {
            try
            {
                Console.Error.WriteLine("[boot] window.Load");
                var gl = window.CreateOpenGL();
                Console.Error.WriteLine($"[boot] GL ready, vendor={gl.GetStringS(StringName.Vendor)} renderer={gl.GetStringS(StringName.Renderer)} version={gl.GetStringS(StringName.Version)}");

                gpu = new OpenGLGPU(gl);
                backend = new DesktopAppBackend(window);

                // Asset roots: prefer wwwroot copied next to the exe (build-run.bat
                // does this), but fall back to the WASM project's wwwroot for
                // `dotnet run` from source. Add the repo-root /assets too because
                // .obj/.png models live there outside wwwroot — the WASM csproj
                // surfaces them at runtime via <Content Include="..\..\assets">,
                // which Desktop has to mimic at the asset-source level.
                var assetRoots = ResolveAssetRoots();
                foreach (var r in assetRoots) Console.Error.WriteLine($"[boot] asset root: {r}");
                var assets = new FileAssetSource(assetRoots);

                // FontStash needs a real TTF — try Consolas, fall back to a
                // few Windows monospace defaults. If none match we silently
                // run without text (backgrounds still render).
                FontStashTextRenderer? text = null;
                foreach (var p in new[]
                {
                    "C:/Windows/Fonts/consola.ttf",
                    "C:/Windows/Fonts/cour.ttf",
                    "C:/Windows/Fonts/segoeui.ttf",
                })
                {
                    if (File.Exists(p))
                    {
                        try { text = new FontStashTextRenderer(gl, p); Console.Error.WriteLine($"[boot] text font: {p}"); break; }
                        catch (Exception e) { Console.Error.WriteLine($"[boot] font {p} init failed: {e.Message}"); }
                    }
                }

                boot = new EngineBootstrap(gpu, backend, assets)
                {
                    // Desktop has no HTML overlay — render UI buttons via
                    // EngineUI's GPU pipeline. WASM keeps using DOM buttons.
                    BuildEngineUI = true,
                    // Wire EngineUI + text renderer the moment EngineUI is
                    // built, BEFORE GameEngine.SetupUI runs and starts
                    // emitting CreateUIButton calls.
                    OnEngineUIBuilt = ui =>
                    {
                        backend.AttachEngineUI(ui);
                        ui.Text = text;
                    },
                };
                Console.Error.WriteLine("[boot] EngineBootstrap.SetupAsync starting");
                await boot.SetupAsync();
                // Subscribes to Engine.OnFrameRendered so clicking a planet in
                // the solar system view flips _planetReady=true / hot-swaps the
                // PlanetRenderer to the new planet. Without this the zoom-in
                // transition never completes (t reaches 1 but _planetReady stays
                // false) and inputs appear to freeze because KeyDown is gated
                // on _transitioning.
                boot.HookPlanetHotSwap();
                Console.Error.WriteLine("[boot] SetupAsync done; engine.Run");
                boot.Engine!.Run();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[boot] FAILED: {e.Message}");
                Console.Error.WriteLine(e.StackTrace);
            }
        };

        long frame = 0;
        window.Render += _ =>
        {
            backend?.Tick();
            gpu?.EndFrame();
            if (frame == 0 || frame == 60 || frame == 300)
                Console.Error.WriteLine($"[host] frame {frame} rendered, size={window.Size.X}x{window.Size.Y}");
            frame++;
        };
        window.Closing += () => gpu?.Dispose();
        window.Run();
    }

    /// <summary>
    /// The Desktop csproj's Content links mirror the repo-root /assets
    /// tree into the build output (config/, planets/, shaders/, textures/
    /// at the binary root, with models/animations under assets/). So
    /// FileAssetSource just resolves "config/foo.yaml" against
    /// AppContext.BaseDirectory and finds the file directly.
    /// </summary>
    private static string[] ResolveAssetRoots() =>
        new[] { AppContext.BaseDirectory };
}

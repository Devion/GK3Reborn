using System.Diagnostics;
using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn;

/// <summary>The composition root and main loop.</summary>
/// <remarks>
/// Startup order is fixed by Plan/01-architecture.md section 3: paths and logging,
/// then content manifest and locale validation, then window, then renderer device and
/// feature tier, then audio endpoint, then game state. Each step must fail with an
/// actionable message rather than proceeding in a broken state.
/// </remarks>
public static class Application
{
    /// <summary>Runs the game.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="nativeLibraryRoot">
    /// Directory the host resolved native libraries from, for the startup report. Passed
    /// in because the dependency runs Bootstrap -> App and never the reverse.
    /// </param>
    /// <returns>Process exit code.</returns>
    public static int Run(string[] args, string? nativeLibraryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        Console.WriteLine("GK3Reborn 0.1.0");
        Console.WriteLine("Scaffold stage: subsystems are contracts only.");
        Console.WriteLine($"Native library root: {nativeLibraryRoot ?? "(not installed)"}");

        // The deterministic clock and RNG are live from the first commit so that no
        // subsystem is ever written against wall-clock time or ambient randomness.
        var clock = new GameClock();
        clock.AdvanceFixed(60);
        var random = new DeterministicRandom(seed: 0x6B33);

        Console.WriteLine($"Clock: tick {clock.Tick}, sim {clock.SimulationTimeSeconds:F3}s");
        Console.WriteLine($"RNG seed 0x{random.Seed:X}: first draw {random.NextUInt64():X16}");

        Console.WriteLine();
        ReportGraphics();

        if (Option(args, "--scene") is { } scene)
        {
            return RenderScene(
                Option(args, "--data") ?? DefaultDataDirectory(),
                scene,
                Option(args, "--timeblock"),
                Option(args, "--camera"),
                int.TryParse(Option(args, "--frames"), out int frames) ? frames : 0,
                Option(args, "--screenshot"),
                args.Contains("--verbose", StringComparer.OrdinalIgnoreCase),
                RayTracingSettings.Parse(Option(args, "--rt")) ?? RayTracingQuality.None,
                EnhancedTextureDirectory(args));
        }

        if (args.Contains("--offscreen", StringComparer.OrdinalIgnoreCase))
        {
            return RenderOffscreen();
        }

        if (args.Contains("--render", StringComparer.OrdinalIgnoreCase))
        {
            return RenderFrames(args.Contains("--headless-frames", StringComparer.OrdinalIgnoreCase) ? 60 : 0);
        }

        return 0;
    }

    /// <summary>
    /// Opens a window and shows a scene from the game's own archives.
    /// </summary>
    /// <param name="dataDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="sceneName">Which scene to load.</param>
    /// <param name="timeblock">Which time of day, or null for whichever exists.</param>
    /// <param name="cameraName">Which of the scene's cameras to start at.</param>
    /// <param name="frameLimit">Stop after this many frames, or zero to run until closed.</param>
    /// <param name="screenshotPath">Where to write the last frame, if anywhere.</param>
    /// <param name="verbose">Whether to list everything that could not be loaded.</param>
    /// <param name="quality">How much ray tracing to start with.</param>
    /// <param name="enhancedDirectory">Higher-resolution textures to prefer, if any.</param>
    /// <returns>Process exit code.</returns>
    /// <remarks>
    /// The camera starts at one of the scene's own viewpoints, which is what the player
    /// would see, and can then be flown around to check the parts a fixed camera never
    /// shows.
    /// </remarks>
    private static int RenderScene(
        string dataDirectory,
        string sceneName,
        string? timeblock,
        string? cameraName,
        int frameLimit,
        string? screenshotPath,
        bool verbose,
        RayTracingQuality quality,
        string? enhancedDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            Console.Error.WriteLine($"No content directory at {dataDirectory}.");
            Console.Error.WriteLine("Pass --data <dir> pointing at the game's Data directory.");
            return 2;
        }

        using GameArchives archives = GameArchives.Open(dataDirectory);
        Console.WriteLine($"Content: {archives.Count} archives in {dataDirectory}");

        using var window = Platform.SilkGameWindow.Open($"GK3Reborn - {sceneName}");
        using var renderer = VulkanRenderer.Create(window, window);

        Console.WriteLine($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        using SceneGeometry geometry = renderer.CreateGeometry();

        var diagnostics = new DiagnosticBag();
        SceneRequest request = SceneRequest.For(sceneName, timeblock);

        if (request.State is not null)
        {
            Console.WriteLine($"Story: {request.State.Timeblock} in {request.State.Location}");
        }

        var loader = new SceneLoader(archives, Console.WriteLine);

        if (enhancedDirectory is { Length: > 0 })
        {
            EnhancedTextures enhanced = EnhancedTextures.Open(enhancedDirectory);
            loader.Enhanced = enhanced;

            Console.WriteLine(enhanced.Count > 0
                ? $"Enhanced textures: {enhanced.Count} available in {enhancedDirectory}"
                : $"Enhanced textures: none found in {enhancedDirectory}");
        }

        LoadedScene? scene = loader.Load(geometry, request, diagnostics);

        if (scene is null)
        {
            foreach (Diagnostic diagnostic in diagnostics.Items)
            {
                Console.Error.WriteLine(diagnostic);
            }

            return 3;
        }

        renderer.SetLights(scene.Lights);
        renderer.Quality = renderer.SupportsRayTracing ? quality : RayTracingQuality.None;

        Console.WriteLine(renderer.SupportsRayTracing
            ? $"Ray tracing: {renderer.Quality} ({geometry.TraceableTriangleCount} opaque "
              + "triangles traced)"
            : "Ray tracing: unavailable on this device");

        Console.WriteLine($"Scene {scene.Name}: {geometry.TriangleCount} triangles in "
            + $"{geometry.BatchCount} batches, {geometry.TextureCount} textures"
            + (loader.EnhancedTexturesUsed > 0
                ? $" ({loader.EnhancedTexturesUsed} enhanced)"
                : string.Empty)
            + $", {scene.Lights.Count} authored lights");

        Diagnostic[] problems = diagnostics.Items
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .ToArray();

        if (problems.Length > 0)
        {
            Console.WriteLine(verbose
                ? $"{problems.Length} assets could not be loaded:"
                : $"({problems.Length} assets could not be loaded; --verbose lists them)");

            if (verbose)
            {
                foreach (Diagnostic problem in problems)
                {
                    Console.WriteLine($"  {problem}");
                }
            }
        }

        int result = FlyScene(window, renderer, geometry, scene, cameraName, frameLimit);

        if (screenshotPath is not null && renderer.Capture() is { } capture)
        {
            File.WriteAllBytes(screenshotPath, Formats.Bitmaps.PngWriter.Encode(capture));
            Console.WriteLine($"Wrote {screenshotPath}");
        }

        return result;
    }

    /// <summary>Runs the present loop with a camera the player can move.</summary>
    private static int FlyScene(
        Platform.SilkGameWindow window,
        VulkanRenderer renderer,
        SceneGeometry geometry,
        LoadedScene scene,
        string? cameraName,
        int frameLimit)
    {
        int cameraIndex = Math.Max(
            0,
            scene.Cameras.ToList().FindIndex(c => string.Equals(
                c.Name, cameraName ?? scene.CameraNamed(null)?.Name, StringComparison.OrdinalIgnoreCase)));

        Camera template = SceneLoader.CameraFor(scene, geometry, cameraName);

        var camera = new FreeCamera
        {
            Speed = MathF.Max(50f, (geometry.Maximum - geometry.Minimum).Length() * 0.15f),
        };

        camera.CopyFrom(template);

        Console.WriteLine();
        Console.WriteLine("WASD to move, E and Q for up and down, drag to look,");
        Console.WriteLine("Tab for the next camera, R to return to it, F2 for ray tracing,");
        Console.WriteLine("Escape to leave.");

        var stopwatch = Stopwatch.StartNew();
        double previous = 0;
        int presented = 0;

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();

            double now = stopwatch.Elapsed.TotalSeconds;
            float delta = (float)Math.Min(0.1, now - previous);
            previous = now;

            if (window.WasPressed(Platform.CameraAction.Quit))
            {
                break;
            }

            if (window.WasPressed(Platform.CameraAction.NextCamera) && scene.Cameras.Count > 0)
            {
                cameraIndex = (cameraIndex + 1) % scene.Cameras.Count;
                template = SceneLoader.CameraFor(scene, geometry, scene.Cameras[cameraIndex].Name);
                camera.CopyFrom(template);

                Console.WriteLine($"camera: {scene.Cameras[cameraIndex].Name}");
            }

            if (window.WasPressed(Platform.CameraAction.Reset))
            {
                camera.CopyFrom(template);
            }

            if (window.WasPressed(Platform.CameraAction.CycleRayTracing) && renderer.SupportsRayTracing)
            {
                RayTracingQuality[] levels = Enum.GetValues<RayTracingQuality>();

                renderer.Quality = levels[(Array.IndexOf(levels, renderer.Quality) + 1) % levels.Length];
                Console.WriteLine($"ray tracing: {renderer.Quality}");
            }

            camera.Update(window, delta);
            window.EndFrame();

            renderer.SetScene(geometry, camera.ToCamera(template));

            if (renderer.DrawFrame(0f, 0f, 0f))
            {
                presented++;
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Presented {presented} frames in {stopwatch.Elapsed.TotalSeconds:F1}s "
            + $"({presented / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0} fps)"));

        return 0;
    }

    /// <summary>Reads an option's value from the command line.</summary>
    private static string? Option(string[] args, string name)
    {
        int at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>Where the game is usually installed relative to the repository.</summary>
    /// <remarks>
    /// A convenience for development only. Anything shipped reads its content path from
    /// configuration rather than guessing.
    /// </remarks>
    /// <summary>
    /// Where the enhanced textures are, if the player wants them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--enhanced &lt;dir&gt;</c> names them outright. <c>--workspace &lt;dir&gt;</c> is
    /// enough on its own, since they always live in the same place inside one, and a bare
    /// <c>--enhanced</c> with nothing after it takes the workspace beside the repository —
    /// a flag that reads as "yes please" and quietly does nothing is worse than no flag.
    /// </para>
    /// <para>
    /// None of them means the game looks exactly as it shipped, which has to stay the
    /// default: this content is a draft until somebody has reviewed it, and nobody should
    /// be shown generated art without having asked for it.
    /// </para>
    /// </remarks>
    private static string? EnhancedTextureDirectory(string[] args)
    {
        bool asked = args.Contains("--enhanced", StringComparer.OrdinalIgnoreCase);

        if (Option(args, "--enhanced") is { Length: > 0 } named && !named.StartsWith('-'))
        {
            return Path.IsPathRooted(named) || Option(args, "--workspace") is not { } under
                ? named
                : Path.Combine(under, named);
        }

        if (Option(args, "--workspace") is { Length: > 0 } workspace)
        {
            return Path.Combine(workspace, "enhanced", "textures");
        }

        return asked ? Path.Combine(DefaultWorkspaceDirectory(), "enhanced", "textures") : null;
    }

    /// <summary>Where the content workspace usually sits relative to the repository.</summary>
    /// <remarks>A convenience for development, like <see cref="DefaultDataDirectory"/>.</remarks>
    private static string DefaultWorkspaceDirectory() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "ContentWorkspace"));

    private static string DefaultDataDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "GK3", "Data"));

    /// <summary>
    /// Renders one frame with no window and writes it to a file.
    /// </summary>
    /// <returns>Process exit code.</returns>
    /// <remarks>
    /// A windowed run proves the code does not crash. Only reading the pixels back proves
    /// something was drawn, and the two failure modes look identical from outside.
    /// </remarks>
    private static int RenderOffscreen()
    {
        using Rendering.Vulkan.OffscreenRenderer renderer = Rendering.Vulkan.OffscreenRenderer.Create();

        Formats.Bitmaps.DecodedImage image = renderer.RenderTriangle(640, 360, (0.05f, 0.06f, 0.09f));
        string path = Path.Combine(AppContext.BaseDirectory, "offscreen.png");
        File.WriteAllBytes(path, Formats.Bitmaps.PngWriter.Encode(image));

        Console.WriteLine($"Rendered {image.Width}x{image.Height} on {renderer.DeviceName}");
        Console.WriteLine($"Wrote {path}");

        return 0;
    }

    /// <summary>
    /// Opens a window and presents frames.
    /// </summary>
    /// <param name="frameLimit">Stop after this many frames, or zero to run until closed.</param>
    /// <returns>Process exit code.</returns>
    /// <remarks>
    /// A frame limit makes this usable as a smoke test: it proves a device, swapchain and
    /// present loop work on a machine without needing anyone to close a window.
    /// </remarks>
    private static int RenderFrames(int frameLimit)
    {
        using var window = Platform.SilkGameWindow.Open("GK3Reborn");
        using var renderer = Rendering.Vulkan.VulkanRenderer.Create(window, window);

        Console.WriteLine($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        int presented = 0;
        int attempts = 0;

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();

            // The clear colour walks so the window visibly animates rather than looking
            // like a still image that might be a frozen first frame.
            float t = presented / 120f;
            if (renderer.DrawFrame(0.05f + (0.05f * MathF.Sin(t)), 0.06f, 0.09f))
            {
                presented++;
            }

            if (++attempts > 100_000)
            {
                break;
            }
        }

        Console.WriteLine($"Presented {presented} frames at {renderer.SwapchainSize.Width}x"
            + $"{renderer.SwapchainSize.Height} across {renderer.SwapchainImageCount} swapchain images");

        return 0;
    }

    /// <summary>
    /// Prints what the machine's graphics hardware can do.
    /// </summary>
    /// <remarks>
    /// Runs before any window exists, so it doubles as a diagnostic on a machine that
    /// cannot run the game at all. A device that cannot present is reported rather than
    /// treated as an error, because saying why is more useful than failing.
    /// </remarks>
    private static void ReportGraphics()
    {
        Rendering.Vulkan.VulkanDeviceReport report = Rendering.Vulkan.VulkanDeviceSelector.Survey();

        if (!report.VulkanAvailable)
        {
            Console.WriteLine($"Vulkan unavailable: {report.Unavailable}");
            return;
        }

        Console.WriteLine($"Vulkan: {report.Devices.Count} device(s), "
            + $"validation layers {(report.ValidationAvailable ? "available" : "not installed")}");

        foreach (Rendering.Vulkan.VulkanDeviceInfo device in report.Devices)
        {
            bool selected = ReferenceEquals(device, report.Selected);
            Console.WriteLine($"  {(selected ? "*" : " ")} {device}");

            foreach (string note in device.TierNotes)
            {
                Console.WriteLine($"      {note}");
            }
        }

        if (report.Selected is null)
        {
            Console.WriteLine("  no device can present; the game cannot render here");
        }
    }
}

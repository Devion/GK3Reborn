using System.Diagnostics;
using GK3Reborn.Rendering.Geometry;
using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using GK3Reborn.Rendering.Materials;
using GK3Reborn.Rendering.Vulkan;
using GK3Reborn.Sheep;

namespace GK3Reborn;
/// <summary>The composition root and main loop.</summary>
public static partial class Application
{
    /// <summary>Runs the game.</summary>
    /// <returns>Process exit code.</returns>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="nativeLibraryRoot">Directory the host resolved native libraries from, for the startup report.</param>
    public static int Run(string[] args, string? nativeLibraryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (CommandLine.WantsHelp(args))
        {
            Console.Out.Write(CommandLine.Usage());
            return 0;
        }

        Log.Open();
        Log.Info("GK3Reborn 0.6.0");
        Log.Info($"Native library root: {nativeLibraryRoot ?? "(not installed)"}");

        StartupReport.Begin(nativeLibraryRoot);

        var clock = new GameClock();
        clock.AdvanceFixed(60);
        var random = new DeterministicRandom(seed: 0x6B33);

        Log.Info($"Clock: tick {clock.Tick}, sim {clock.SimulationTimeSeconds:F3}s");
        Log.Info($"RNG seed 0x{random.Seed:X}: first draw {random.NextUInt64():X16}");

        Log.Info();

        SceneLoader.NoSun = args.Contains("--no-sun", StringComparer.OrdinalIgnoreCase);
        VulkanPortability.ForceHostExpansion = args.Contains("--expand-blocks", StringComparer.OrdinalIgnoreCase);
        RayTracingQuality? asked = Option(args, "--rt") is { Length: > 0 } level ? RayTracingSettings.Parse(level) : null;

        if (args.Contains("--extract", StringComparer.OrdinalIgnoreCase))
        {
            return Extract(args);
        }

        if (Option(args, "--scene") is { } scene)
        {
            // A named scene is somebody looking at a room, so it opens in the room.
            return RenderScene( Option(args, "--data") ?? DefaultDataDirectory(), scene, Option(args, "--timeblock"), Option(args, "--camera"),
                int.TryParse(Option(args, "--frames"), out int frames) ? frames : 0, Option(args, "--screenshot"),
                args.Contains("--verbose", StringComparer.OrdinalIgnoreCase), asked, EnhancedTextureDirectory(args),
                args.Contains("--front", StringComparer.OrdinalIgnoreCase), args);
        }

        ReportGraphics();

        if (args.Contains("--offscreen", StringComparer.OrdinalIgnoreCase))
        {
            return RenderOffscreen();
        }

        if (args.Contains("--render", StringComparer.OrdinalIgnoreCase))
        {
            return RenderFrames(args.Contains("--headless-frames", StringComparer.OrdinalIgnoreCase) ? 60 : 0);
        }

        // Nothing asked for in particular: the game, as a player starts it.
        return RenderScene( Option(args, "--data") ?? DefaultDataDirectory(), Option(args, "--start") ?? OpeningScene,
            Option(args, "--timeblock") ?? OpeningTimeblock, null, 0, null, args.Contains("--verbose", StringComparer.OrdinalIgnoreCase), asked,
            EnhancedTextureDirectory(args), frontEnd: true, args);
    }

    /// <summary>Where the story starts.</summary>
    private const string OpeningScene = "R25";

    /// <summary>The file whose presence means St.</summary>
    private const string Bookshop = "SGB.SIF";

    /// <summary>The time of day the story starts at.</summary>
    private const string OpeningTimeblock = "110A";

    /// <summary>The films the game opens with, in order.</summary>
    private static readonly string[] IntroMovies = [SierraLogo, TheIntro];

    /// <summary>The publisher's logo, which the game opens with and nothing else wants.</summary>
    private const string SierraLogo = "SIERRA";

    /// <summary>The opening of the game itself.</summary>
    private const string TheIntro = "INTRO";
}

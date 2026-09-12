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
/// <summary>Finding the game, opening it and opening a renderer.</summary>
public static partial class Application
{
    /// <summary>Writes the game's content out as files, laid out for overrides/.</summary>
    /// <returns>Process exit code.</returns>
    /// <param name="args">The command line.</param>
    private static int Extract(string[] args)
    {
        string? name = Option(args, "--name");
        string? kindList = Option(args, "--kinds");
        string from = Option(args, "--from") ?? "packs";

        bool asPng = string.Equals(Option(args, "--as"), "png", StringComparison.OrdinalIgnoreCase);

        if (Option(args, "--as") is { Length: > 0 } form && !form.Equals("png", StringComparison.OrdinalIgnoreCase) &&
            !form.Equals("dds", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"--as {form}: the forms are png and dds.");
            return 2;
        }

        bool wantsPacks = from is "packs" or "all";
        bool wantsGame = from is "game" or "all";

        if (!wantsPacks && !wantsGame)
        {
            Log.Error($"--from {from}: the sources are packs, game and all.");
            return 2;
        }

        string named = Option(args, "--extract-to") ?? string.Empty;
        bool intoOverrides = named.Length == 0;
        string output = intoOverrides ? OverrideDirectory(args) : Path.GetFullPath(named);

        // Kinds, parsed before anything is opened so a typo costs nothing.
        List<Formats.Rebarn.RebarnKind>? kinds = null;

        if (kindList is { Length: > 0 })
        {
            kinds = [];

            foreach (string one in kindList.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Formats.Rebarn.RebarnFormat.KindOf(one) is not { } kind)
                {
                    Log.Error($"--kinds: {one} names no kind of content.");
                    Log.Error( "The kinds are textures, normals, orm, height, emissive, models, "
                        + "scene-geometry, video, menu, manifests and raw.");

                    return 2;
                }

                kinds.Add(kind);
            }
        }

        // Everything, into the directory the game reads.
        if (intoOverrides && kinds is null && name is null && wantsPacks)
        {
            Log.Error( "--extract with no --kinds and no --name would copy every packed file into "
                + $"{output}, where each one would then override itself.");
            Log.Error( "Say which content you want — --kinds textures, --name R25WALLS — or "
                + "--extract-to <dir> to unpack the lot somewhere it is only a copy.");

            return 2;
        }

        Log.Info($"Extracting to {output}");

        var total = new ContentExtract.Result();

        if (wantsPacks)
        {
            string packDirectory = PackDirectory(args);
            var packDiagnostics = new DiagnosticBag();
            using RebarnContent packs = RebarnContent.Open(packDirectory, packDiagnostics);

            foreach (Diagnostic diagnostic in packDiagnostics.Items)
            {
                Log.Report(diagnostic);
            }

            if (packs.VolumeCount == 0)
            {
                // Refused rather than reported as an empty success.
                Log.Error($"No .rebarn pack in {packDirectory}.");
                Log.Error("Pass --packs <dir> to say where they are, or --from game.");

                return 2;
            }

            Log.Info($"Packs: {packs.Describe()}");

            total += ContentExtract.FromPacks(packs, output, kinds, name, asPng, Log.Info);
        }

        if (wantsGame)
        {
            string dataDirectory = Option(args, "--data") ?? DefaultDataDirectory();

            if (!Directory.Exists(dataDirectory))
            {
                Log.Error($"No game archives at {dataDirectory}.");
                ExplainMissingArchives(dataDirectory);

                return 2;
            }

            using GameArchives archives = GameArchives.Open(dataDirectory);

            if (archives.Count == 0)
            {
                Log.Error($"No game archives in {dataDirectory}.");
                ExplainMissingArchives(dataDirectory);

                return 2;
            }

            // The 1999 assets go in a directory of their own.
            string game = Path.Combine(output, "game");

            // The extension list, which for these is what --kinds means: a barn holds SIF, NVC, BMP, MOD and WAV, and none of those is a ReBarn kind.
            string[]? extensions = kindList is { Length: > 0 }
                ? kindList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;

            if (intoOverrides && extensions is null && name is null)
            {
                Log.Error( $"--from game with no --kinds and no --name would copy every asset the "
                    + $"archives hold into {game}, where each one would then override itself.");
                Log.Error( "Say which — --kinds SIF,NVC, --name R25 — or --extract-to <dir>.");

                return 2;
            }

            ContentExtract.Result written = ContentExtract.FromGame(archives, game, extensions, name, Log.Info);

            Log.Info($"  {"game",-15} {written.Written,6} file(s), " + $"{written.Bytes / (1024.0 * 1024):F1} MB");

            total += written;
        }

        Log.Info(total.Written == 0 ? "Nothing matched, so nothing was written."
            : $"Wrote {total.Written} file(s), {total.Bytes / (1024.0 * 1024):F1} MB, to {output}"
                + (total.Failed > 0 ? $"; {total.Failed} could not be written." : "."));

        if (total.Written > 0 && intoOverrides)
        {
            Log.Info( "Every file there now stands in front of the one it came from. Delete the "
                + "ones you are not changing, or the game reads its own content back " + "through a slower door.");
        }

        return total.Written > 0 ? 0 : 1;
    }

    /// <summary>Where the player's own overriding files sit.</summary>
    /// <returns>The directory, whether or not it exists.</returns>
    /// <param name="args">Command line, for --overrides.</param>
    private static string OverrideDirectory(string[] args)
    {
        if (Option(args, "--overrides") is { Length: > 0 } named && !named.StartsWith('-'))
        {
            return named;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, ContentOverrides.DirectoryName);

        return Directory.Exists(beside) || InstallPaths.CanWrite(AppContext.BaseDirectory) ? beside
            : Path.Combine(InstallPaths.UserData, ContentOverrides.DirectoryName);
    }

    /// <summary>One of the archives' own bitmaps, decoded, or null when it is not there.</summary>
    /// <returns>The picture, or null.</returns>
    /// <param name="archives">The game's data.</param>
    /// <param name="name">The bitmap's name with extension.</param>
    private static Formats.Bitmaps.DecodedImage? Decoded(GameArchives archives, string name)
    {
        if (archives.Read(name) is not { } bytes)
        {
            return null;
        }

        try
        {
            return Formats.Bitmaps.BitmapDecoder.Decode(bytes, name);
        }
        catch (Formats.FormatParseException)
        {
            return null;
        }
    }

    /// <summary>One channel's loose picture layer: the workspace's set with the overrides over it.</summary>
    /// <returns>The layer, or null when neither source has anything for this channel.</returns>
    /// <param name="enabled">Whether the enhanced set itself is wanted.</param>
    /// <param name="packsOnly">Whether every other loose source is being ignored.</param>
    /// <param name="enhancedDirectory">The enhanced colour set, or null.</param>
    /// <param name="overrides">What the player has dropped in, or null.</param>
    /// <param name="kind">Which channel.</param>
    /// <param name="subdirectory">Where that channel sits beside the colour set.</param>
    /// <param name="language">The language whose own set goes over the shared one, or null for the shared set alone.</param>
    private static EnhancedTextures? Pictures( bool enabled, bool packsOnly, string? enhancedDirectory, ContentOverrides? overrides,
        Formats.Rebarn.RebarnKind kind = Formats.Rebarn.RebarnKind.Texture, string? subdirectory = null, GameLanguage? language = null)
    {
        string directory = enabled && !packsOnly && enhancedDirectory is { Length: > 0 }
            ? subdirectory is null ? enhancedDirectory : Beside(enhancedDirectory, subdirectory) : string.Empty;

        // enhanced/localtextures/<CODE> and its three material neighbours, beside the enhanced set rather than under it, because each is a parallel.
        string localised = language is not null && LocalChannel(kind) is { } channel && enabled && !packsOnly && enhancedDirectory is { Length: > 0 }
                ? Path.Combine(Beside(enhancedDirectory, channel), language.FileCode) : string.Empty;

        ContentOverrides? layer = overrides?.Images(kind).Count > 0 ? overrides : null;

        return directory.Length == 0 && localised.Length == 0 && layer is null ? null : EnhancedTextures.Open(directory, layer, kind, localised);
    }

    /// <summary>Which of a language's own directories holds a channel.</summary>
    /// <returns>The directory's name beside the enhanced set, or null for a kind no language has one of.</returns>
    /// <param name="kind">The channel.</param>
    private static string? LocalChannel(Formats.Rebarn.RebarnKind kind) => kind switch
    {
        Formats.Rebarn.RebarnKind.Texture => "localtextures", Formats.Rebarn.RebarnKind.Normal => "localnormals",
        Formats.Rebarn.RebarnKind.Orm => "localorm", Formats.Rebarn.RebarnKind.Height => "localheight", _ => null,
    };

    /// <summary>Where the block-compressed build of the enhanced textures sits.</summary>
    /// <returns>The first directory that holds a pack, or the executable's own.</returns>
    /// <param name="args">Command line, for --packs and --workspace.</param>
    private static string PackDirectory(string[] args)
    {
        if (Option(args, "--packs") is { Length: > 0 } named)
        {
            return named;
        }

        // Beside the executable first, because that is where a shipped game puts them and where a player would drop one.
        string[] candidates =
        [
            AppContext.BaseDirectory,
            // A macOS .app carries its pack in Contents/Resources, which is the only place inside a bundle that a signed, read-only install can put.
            InstallPaths.BundleResources ?? string.Empty,
            // And the user's own directory, which is where somebody with a read-only install drops a pack they downloaded separately.
            InstallPaths.UserData, Option(args, "--workspace") is { Length: > 0 } workspace ? workspace : string.Empty, DefaultWorkspaceDirectory(),
        ];

        foreach (string candidate in candidates)
        {
            if (candidate.Length > 0 && Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*" + Formats.Rebarn.RebarnFormat.Extension).Any())
            {
                StartupReport.Searched("Packs", candidates, candidate);

                return candidate;
            }
        }

        StartupReport.Searched("Packs", candidates, null);

        return AppContext.BaseDirectory;
    }

    private static string CompressedTextureDirectory(string[] args, string enhancedDirectory)
    {
        if (Option(args, "--workspace") is { Length: > 0 } workspace)
        {
            return Path.Combine(workspace, "build");
        }

        // Up out of enhanced/textures, which is where --enhanced points by default.
        string? enhancedRoot = Path.GetDirectoryName( enhancedDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'));

        string? root = enhancedRoot is null ? null : Path.GetDirectoryName(enhancedRoot);

        return Path.Combine(root ?? DefaultWorkspaceDirectory(), "build");
    }

    /// <summary>Where the content workspace usually sits relative to the repository.</summary>
    private static string DefaultWorkspaceDirectory() => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "ContentWorkspace"));

    /// <summary>The eight archives a retail installation of GK3 holds.</summary>
    private static readonly string[] RetailArchives =
    [
        "ambient.brn", "common.brn", "core.brn", "day1.brn", "day123.brn", "day2.brn", "day23.brn", "day3.brn", ];

    /// <summary>Says which of the eight archives are not there, and which are there unseen.</summary>
    /// <param name="dataDirectory">The directory that was searched.</param>
    /// <param name="found">How many archives were actually opened from it.</param>
    private static void ReportArchives(string dataDirectory, int found)
    {
        string[] present;

        try
        {
            present = [.. Directory.EnumerateFiles(dataDirectory).Select(Path.GetFileName)!];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Content: {dataDirectory} could not be listed. ({error.Message})");

            return;
        }

        string[] unseen = [.. present.Where(name => Path.GetExtension(name).Equals(".brn", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(name).Equals(".brn", StringComparison.Ordinal))];

        if (unseen.Length > 0)
        {
            Log.Error($"Content: {dataDirectory} holds {unseen.Length} archive(s) whose "
                + $"names are spelled differently: {string.Join("  ", unseen)}");

            Log.Error("Linux and macOS match file names exactly, so those are not found. " + "Rename them to lower case, extension included.");
        }

        if (found == 0)
        {
            // Every one of the eight is missing, which the message that follows this says better than a list would.
            return;
        }

        string[] absent = [.. RetailArchives.Where(archive => !present.Any(name => name.Equals(archive, StringComparison.OrdinalIgnoreCase)))];

        if (absent.Length > 0)
        {
            // A warning, not a refusal: a copy without day3.brn plays for two days, and stopping it from starting would be worse than saying what it.
            Log.Warning($"Content: {absent.Length} of the eight archives are not in " + $"{dataDirectory}: {string.Join("  ", absent)}");

            Log.Warning("The game will start, but the rooms and cutscenes in them cannot " + "be loaded.");
        }
    }

    /// <summary>Says what is missing and where it goes.</summary>
    /// <param name="dataDirectory">Where the archives were looked for.</param>
    private static void ExplainMissingArchives(string dataDirectory)
    {
        Log.Error();
        Log.Error( "GK3Reborn reads the original game's archives; it does not contain them.");

        Log.Error( $"Copy these from your installation's Data directory into {dataDirectory}:");

        Log.Error("    " + string.Join("  ", RetailArchives));
        Log.Error();
        Log.Error( "Nothing else from the original is needed: the .bik and .avi movies are " + "replaced by converted video in the .rebarn packs.");

        Log.Error( "Or pass --data <dir> to read them where they already are.");
    }

    /// <summary>Where the game's own archives are, when nobody has said.</summary>
    /// <returns>The first directory holding a .brn, or where one should be put.</returns>
    private static string DefaultDataDirectory()
    {
        string beside = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(beside, "Data"), beside,
            // A read-only install cannot be filled in place, so the same Data directory is looked for under the user's own: that is what a macOS.
            Path.Combine(InstallPaths.UserData, "Data"), InstallPaths.BundleResources is { Length: > 0 } resources ? Path.Combine(resources, "Data")
                : string.Empty, Path.GetFullPath(Path.Combine( beside, "..", "..", "..", "..", "..", "..", "GK3", "Data")), ];

        candidates = [.. candidates.Where(candidate => candidate.Length > 0)];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.brn").Any())
            {
                StartupReport.Searched("Content", candidates, candidate);

                return candidate;
            }
        }

        // Every place that was tried, since the message below names only one of them and "it is not where you say it is" is not an answer somebody.
        StartupReport.Searched("Content", candidates, null);

        // Nothing anywhere: name the place a player is meant to fill rather than the one a developer's checkout happens to have, because that is the.
        return InstallPaths.CanWrite(beside) ? candidates[0] : Path.Combine(InstallPaths.UserData, "Data");
    }

    /// <summary>Renders one frame with no window and writes it to a file.</summary>
    /// <returns>Process exit code.</returns>
    private static int RenderOffscreen()
    {
        using Rendering.Vulkan.OffscreenRenderer renderer = Rendering.Vulkan.OffscreenRenderer.Create();

        Formats.Bitmaps.DecodedImage image = renderer.RenderTriangle(640, 360, (0.05f, 0.06f, 0.09f));

        // Beside the executable, where somebody running the smoke test will look for it - unless the executable is inside a read-only .app bundle.
        string path = Path.Combine(InstallPaths.WritableRoot, "offscreen.png");
        File.WriteAllBytes(path, Formats.Bitmaps.PngWriter.Encode(image));

        Log.Info($"Rendered {image.Width}x{image.Height} on {renderer.DeviceName}");
        Log.Info($"Wrote {path}");

        return 0;
    }

    /// <summary>Opens a window and presents frames.</summary>
    /// <returns>Process exit code.</returns>
    /// <param name="frameLimit">Stop after this many frames, or zero to run until closed.</param>
    private static int RenderFrames(int frameLimit)
    {
        using var window = Platform.SilkGameWindow.Open("GK3Reborn");

        // The one caller that wants the bring-up triangle: there is no room to draw and the point is to prove the chain reaches the screen at all.
        using var renderer = Rendering.Vulkan.VulkanRenderer.Create(window, window, bringUp: true);

        Log.Info($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        int presented = 0;
        int attempts = 0;

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();

            // The clear colour walks so the window visibly animates rather than looking like a still image that might be a frozen first frame.
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

        Log.Info($"Presented {presented} frames at {renderer.SwapchainSize.Width}x"
            + $"{renderer.SwapchainSize.Height} across {renderer.SwapchainImageCount} swapchain images");

        return 0;
    }

    /// <summary>Works out which graphics API to draw through.</summary>
    /// <returns>The backend to open the window and the renderer for.</returns>
    /// <param name="asked">What was typed after --backend, or null for whichever suits.</param>
    /// <param name="settings">The player's, for the backend they chose.</param>
    private static Rendering.RenderBackend ChooseBackend(string? asked, Settings settings)
    {
        // The command line first, then the settings file, then whatever suits the machine.
        if (asked is null)
        {
            return Rendering.RenderBackends.Resolve(settings.Backend);
        }

        if (!Rendering.RenderBackends.TryParse(asked, out Rendering.RenderBackend wanted))
        {
            Log.Warning( $"WARNING GK3R3420: '{asked}' names no graphics API; using the usual one. " + "Expected vulkan or d3d12.");

            return Rendering.RenderBackends.Resolve(settings.Backend);
        }

        if (!Rendering.RenderBackends.IsPossible(wanted))
        {
            Log.Warning( $"WARNING GK3R3421: {wanted} cannot be used on this machine; using Vulkan.");

            return Rendering.RenderBackend.Vulkan;
        }

        return Rendering.RenderBackends.Resolve(wanted);
    }

    /// <summary>A window, the renderer drawing into it, and the loader that renderer needed.</summary>
    /// <param name="Window">The window.</param>
    /// <param name="Streamline">NVIDIA's loader, on Vulkan; Direct3D starts its own inside the renderer.</param>
    /// <param name="Renderer">The renderer.</param>
    /// <param name="Backend">Which API it is, which may not be the one that was asked for.</param>
    private readonly record struct OpenedRenderer( Platform.SilkGameWindow Window, Rendering.Upscaling.Streamline? Streamline,
        Rendering.IRenderer Renderer, Rendering.RenderBackend Backend);

    /// <summary>Opens a window and a renderer for it, falling back from Direct3D to Vulkan.</summary>
    /// <returns>The three, to be disposed by the caller in the reverse of this order.</returns>
    /// <param name="backend">The backend to try first.</param>
    /// <param name="insisted">Whether the backend was named on the command line.</param>
    /// <param name="title">The window title.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    /// <param name="runtimes">The upscaler runtimes that were found.</param>
    /// <param name="libsDirectory">Where --libs-dir pointed, for Direct3D's own Streamline.</param>
    private static OpenedRenderer OpenRenderer( Rendering.RenderBackend backend, bool insisted, string title, int width, int height,
        Rendering.Upscaling.UpscalerRuntimes runtimes, string? libsDirectory)
    {
        if (backend == Rendering.RenderBackend.Direct3D12)
        {
            Platform.SilkGameWindow window = Platform.SilkGameWindow.Open( title, width, height, Platform.WindowGraphics.None, visible: false);

            try
            {
                Rendering.IRenderer renderer = Rendering.Direct3D12.D3D12Renderer.Create( window, window, rayTracing: true, runtimes: libsDirectory);

                return new OpenedRenderer(window, null, renderer, backend);
            }
            catch (Exception error) when ( error is Rendering.Direct3D12.D3D12Exception or Rendering.Shaders.ShaderCompilationException)
            {
                window.Dispose();

                if (insisted)
                {
                    throw new Rendering.Direct3D12.D3D12Exception( $"{error.Message} Direct3D 12 was asked for; --vulkan is the other renderer.",
                        error);
                }

                Log.Warning( "WARNING GK3R3422: Direct3D 12 cannot run on this machine; using Vulkan " + $"instead. {error.Message}");
            }
            catch
            {
                window.Dispose();
                throw;
            }
        }

        Platform.SilkGameWindow vulkanWindow = Platform.SilkGameWindow.Open( title, width, height, Platform.WindowGraphics.Vulkan, visible: false);

        Rendering.Upscaling.Streamline? streamline = null;

        try
        {
            streamline = Rendering.Upscaling.Streamline.TryStart(runtimes);

            Rendering.IRenderer renderer = VulkanRenderer.Create( vulkanWindow, vulkanWindow, streamline: streamline);

            return new OpenedRenderer(vulkanWindow, streamline, renderer, Rendering.RenderBackend.Vulkan);
        }
        catch
        {
            streamline?.Dispose();
            vulkanWindow.Dispose();
            throw;
        }
    }

    /// <summary>Prints what the machine's graphics hardware can do.</summary>
    /// <param name="report">The survey, or null to make one.</param>
    private static void ReportGraphics(Rendering.DeviceReport? report = null) =>
        Log.Write(GraphicsReport(report ?? Rendering.Vulkan.VulkanDeviceSelector.Survey()));

    private static string GraphicsReport(Rendering.DeviceReport report)
    {
        var text = new System.Text.StringBuilder();

        if (!report.Available)
        {
            return text .AppendLine( CultureInfo.InvariantCulture, $"{report.Backend} unavailable: {report.Unavailable}") .ToString();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"{report.Backend}: {report.Adapters.Count} device(s), "
            + $"validation layers {(report.ValidationAvailable ? "available" : "not installed")}");

        foreach (Rendering.AdapterInfo device in report.Adapters)
        {
            bool selected = ReferenceEquals(device, report.Selected);
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(selected ? "*" : " ")} {device}");

            foreach (string note in device.Notes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"      {note}");
            }
        }

        if (report.Selected is null)
        {
            text.AppendLine("  no device can present; the game cannot render here");
        }

        return text.ToString();
    }
}

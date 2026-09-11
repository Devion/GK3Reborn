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
            // A named scene is somebody looking at a room, so it opens in the room. The
            // menu is still reachable from inside it, and --front asks for it first.
            return RenderScene(
                Option(args, "--data") ?? DefaultDataDirectory(),
                scene,
                Option(args, "--timeblock"),
                Option(args, "--camera"),
                int.TryParse(Option(args, "--frames"), out int frames) ? frames : 0,
                Option(args, "--screenshot"),
                args.Contains("--verbose", StringComparer.OrdinalIgnoreCase),
                asked,
                EnhancedTextureDirectory(args),
                args.Contains("--front", StringComparer.OrdinalIgnoreCase),
                args);
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

        // Nothing asked for in particular: the game, as a player starts it. The intro, the
        // menu, and then wherever the story begins.
        return RenderScene(
            Option(args, "--data") ?? DefaultDataDirectory(),
            Option(args, "--start") ?? OpeningScene,
            Option(args, "--timeblock") ?? OpeningTimeblock,
            null,
            0,
            null,
            args.Contains("--verbose", StringComparer.OrdinalIgnoreCase),
            asked,
            EnhancedTextureDirectory(args),
            frontEnd: true,
            args);
    }

    /// <summary>Where the story starts.</summary>
    private const string OpeningScene = "R25";

    /// <summary>
    /// The file whose presence means St. George's Books is installed: the scene file of
    /// the room behind RC1's bookstore door. See Assets/Story/Bookshop.txt.
    /// </summary>
    private const string Bookshop = "SGB.SIF";

    /// <summary>The time of day the story starts at.</summary>
    private const string OpeningTimeblock = "110A";

    /// <summary>
    /// The films the game opens with, in order.
    /// </summary>
    private static readonly string[] IntroMovies = [SierraLogo, TheIntro];

    /// <summary>The publisher's logo, which the game opens with and nothing else wants.</summary>
    private const string SierraLogo = "SIERRA";

    /// <summary>The opening of the game itself.</summary>
    private const string TheIntro = "INTRO";

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
    /// <param name="quality">
    /// How much ray tracing to start with, or null to use what the player has chosen.
    /// </param>
    /// <param name="enhancedDirectory">Higher-resolution textures to prefer, if any.</param>
    /// <param name="frontEnd">Whether to show the intro and the menu before the room.</param>
    /// <param name="args">The command line, for the options only the running scene reads.</param>
    /// <returns>Process exit code.</returns>
    private static int RenderScene(
        string dataDirectory,
        string sceneName,
        string? timeblock,
        string? cameraName,
        int frameLimit,
        string? screenshotPath,
        bool verbose,
        RayTracingQuality? quality,
        string? enhancedDirectory,
        bool frontEnd,
        string[] args)
    {
        // Named through the report rather than checked inline, so that a missing directory
        // says how far up the path does exist and whether the name is sitting there under
        // a different case - which is the whole of the difference between a Windows machine
        // and the Linux one somebody is reporting from.
        if (!StartupReport.Needed("Content", dataDirectory))
        {
            ExplainMissingArchives(dataDirectory);
            return 2;
        }

        using GameArchives archives = GameArchives.Open(dataDirectory);

        if (archives.Count == 0)
        {
            // The directory is there and empty, which is what a half-finished install looks
            // like. Said here rather than letting the first missing asset report it: a room
            // that cannot be found reads as a broken game, not as a copy nobody made.
            Log.Error($"No game archives in {dataDirectory}.");
            ReportArchives(dataDirectory, archives.Count);
            ExplainMissingArchives(dataDirectory);
            return 2;
        }

        Log.Info($"Content: {archives.Count} archives in {dataDirectory}");

        ReportArchives(dataDirectory, archives.Count);

        // Whatever the player has dropped into overrides/, which outranks everything: the
        // packs below, and these archives. Opened before the room is looked for, because a
        // replaced R25.SIF is a room the archives do not have and the check below would
        // refuse to start over it.
        //
        // One index of a directory that is usually not there, so it costs nothing in a game
        // nobody has modified.
        var overrideDiagnostics = new DiagnosticBag();
        string overrideDirectory = OverrideDirectory(args);

        ContentOverrides found = args.Contains("--no-overrides", StringComparer.OrdinalIgnoreCase)
            ? ContentOverrides.Open(string.Empty)
            : ContentOverrides.Open(overrideDirectory, overrideDiagnostics);

        foreach (Diagnostic diagnostic in overrideDiagnostics.Items)
        {
            Log.Report(diagnostic);
        }

        // Null when there is nothing, and null all the way down: every layer below tests
        // this to decide whether the override door exists at all, so an empty set handed
        // out instead would have each of them consulting a dictionary that can never
        // answer, on the critical path of every texture in every room.
        ContentOverrides? overrides = found.IsEmpty ? null : found;

        archives.Overrides = overrides;

        // Said out loud, because an override is invisible once it is on screen — that is
        // what it is for — and a run in which a forgotten file is standing in for the
        // shipped one looks exactly like a run without it.
        Log.Info(found.Describe() is { } overridden
            ? $"Overrides: {overridden}"
            : $"Overrides: none in {overrideDirectory}");

        StartupReport.Optional("Overrides", overrides is null ? null : overrideDirectory,
            "The game uses the content it shipped with.");



        // The remake's own content, in the one or two ReBarn volumes that ship beside the
        // executable. Opened once for the session rather than once a room: a pack is
        // memory-mapped, and every texture the loader takes from one is a window onto that
        // mapping rather than a copy of it.
        //
        // Before the window and the device on purpose, so that --rebarn with no pack fails
        // in a moment rather than after a Vulkan instance has been built for nothing.
        var packDiagnostics = new DiagnosticBag();
        string packDirectory = PackDirectory(args);
        using RebarnContent packs = RebarnContent.Open(packDirectory, packDiagnostics);

        // The same layer as the archives got, in front of the packs. Both doors, because a
        // name can be either: R25WALLS is a bitmap in an archive and a BC7 texture in a
        // pack, and somebody replacing it has no reason to care which.
        packs.Overrides = archives.Overrides;

        foreach (Diagnostic diagnostic in packDiagnostics.Items)
        {
            Log.Report(diagnostic);
        }

        // --rebarn: the packs and nothing else. Every loose source of enhanced content is
        // taken out of the way, which is the only way to measure what the shipped form
        // costs — with the loose sets in front of it, a run measures those instead.
        bool askedForPacks = args.Contains("--rebarn", StringComparer.OrdinalIgnoreCase);

        // And that is what a player gets without asking, because it is all a shipped
        // install has: packs beside the executable and no content workspace anywhere. The
        // loose sets are only ever wanted by somebody who named one, or who asked to see
        // the originals underneath — so naming any of those three is what turns it off.
        // A run with no packs is unaffected: there is nothing for this to prefer.
        bool namedSomethingLoose =
            enhancedDirectory is { Length: > 0 } ||
            Option(args, "--workspace") is { Length: > 0 } ||
            args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase);

        bool packsOnly = askedForPacks || (packs.VolumeCount > 0 && !namedSomethingLoose);

        if (askedForPacks && args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase))
        {
            // --rebarn says "the packs and nothing else", --uncompressed says "not the
            // compressed layer", and a pack holds nothing but compressed textures. Together
            // they ask for no enhanced content at all, which is what no flags already does.
            Log.Error(
                "--rebarn and --uncompressed contradict each other: a pack holds nothing "
                + "but compressed textures.");
            Log.Error(
                "Drop --uncompressed to measure the packs, or drop --rebarn to compare "
                + "against the loose sets.");

            return 2;
        }

        if (askedForPacks && packs.VolumeCount == 0)
        {
            // Refused rather than warned. Falling back would run the game on the original
            // textures and report perfectly good timings for something nobody asked to
            // measure, which is the shape of every expensive mistake in this project.
            Log.Error($"--rebarn: no .rebarn pack in {packDirectory}.");
            Log.Error(
                "Build one with `pack-content`, or pass --packs <dir> to say where they are.");

            return 2;
        }

        // Said either way. Silence about a missing pack is how a run comes to be measured
        // against the loose sets while everybody believes it was measured against the pack.
        Log.Info(packs.Describe() is { } packed
            ? packsOnly
                ? $"Packs: {packed} (loose enhanced content ignored)"
                : $"Packs: {packed}"
            : $"Packs: none in {packDirectory}");


        // What the player has chosen, read before anything that obeys it exists. A first
        // run has no file and gets the defaults, which is not a failure and is not reported
        // as one.
        //
        // --settings names a different file. For photographing a display setting without
        // editing the one the player is actually using: every row on the picture pages is
        // now something a screenshot can be taken of, and taking one should not cost
        // somebody their own choices.
        string settingsPath = Option(args, "--settings") is { Length: > 0 } elsewhere
            ? Path.GetFullPath(elsewhere)
            : Settings.DefaultPath;

        Settings settings = Settings.Load(settingsPath);

        // --language names one for this run without writing it back, so that a room can be
        // rendered in French to compare against the English one without editing anybody's
        // settings file. Unknown codes fall back to English rather than failing, the same
        // way the settings file's own value does.
        if (Option(args, "--language") is { Length: > 0 } wantedLanguage)
        {
            settings = settings with { Language = wantedLanguage };

            if (!GameLanguage.IsKnown(wantedLanguage))
            {
                Log.Warning(
                    $"--language {wantedLanguage} names no localisation GK3 was published in; "
                    + $"reading the game in {GameLanguage.Default.Name}.");
            }
        }

        // Which language the game is read in, and the pack that makes that possible. Opened
        // before the archives are read for anything, because every asset below comes
        // through this door: the string table, the fonts, Sidney's documents, every bitmap
        // with words in it, every recorded line and every YAK that lip-syncs one.
        //
        // Null when there is no pack for the chosen language, and that is the ordinary case
        // for English: the installation already answers to the English spellings under
        // every locale Sierra shipped, so English needs no pack to be playable.
        //
        // Neither is fixed for the life of the process: the Language row swaps both while
        // the game is running, and the room is reloaded underneath so that every picture
        // with a word painted into it comes back in the new language. See Relanguage.
        GameLanguage language = GameLanguage.Of(settings.Language);
        var languageDiagnostics = new DiagnosticBag();
        LocalizedContent? localized =
            LocalizedContent.Open(packDirectory, language, languageDiagnostics);

        foreach (Diagnostic diagnostic in languageDiagnostics.Items)
        {
            Log.Report(diagnostic);
        }

        // Whether the pack is telling the installation its own language back. Asked before
        // the layer is attached, because once it is, archives.Read answers out of the pack
        // and the comparison would be the pack against itself.
        //
        // Nearly every player is an English installation playing in English, and that is
        // exactly the case this catches: the English release sourced here is a dumped tree,
        // and a dump has thrown away which archive an entry came from — its WOODTILE.BMP is
        // a foliage card rather than the hotel lobby's floor. See RepeatsInstallation.
        if (localized?.RepeatsInstallation(archives.Read) == true)
        {
            Log.Info(
                $"Language: the installation is already {localized.Language.Name}, so the "
                + $"pack's {localized.AssetCount} 1999 assets are skipped and only what the "
                + "remake painted is read from it.");
        }

        archives.Localization = localized;
        IReadOnlyList<GameLanguage> languages = LocalizedContent.Available(packDirectory);

        // Said out loud whichever way it went. A French game running on the English
        // archives because the pack was not built looks exactly like a French game, until
        // somebody reads a word — so the line says both what was asked for and what is
        // actually answering.
        if (localized is not null)
        {
            Log.Info($"Language: {localized.Describe()}");
        }
        else if (language == GameLanguage.Default)
        {
            Log.Info($"Language: {language.Name}, from the installation");
        }
        else
        {
            // A warning rather than a note, because this is a run that will do the wrong
            // thing quietly: every word of it will be in whatever language the archives
            // hold, and nothing on screen will say the choice did not take.
            Log.Warning(
                $"Language: {language.Name} was asked for, but there is no "
                + $"{LocalizedContent.FileNameOf(language)} in {packDirectory}. The game is "
                + "read in whatever language the installation holds.");
        }

        StartupReport.Optional(
            "Language",
            localized is null ? null : packDirectory,
            "The game is read in the language the installation was made in.");

        // Two switches for the two rows on the Picture page that change what a room looks
        // like rather than how sharply it is drawn, so that the same room can be
        // photographed both ways without editing anybody's settings file. Both are
        // overrides for this run and neither is written back.
        if (args.Contains("--real-light", StringComparer.OrdinalIgnoreCase))
        {
            settings = settings with { RealisticLighting = true };
        }

        if (args.Contains("--no-real-light", StringComparer.OrdinalIgnoreCase))
        {
            settings = settings with { RealisticLighting = false };
        }

        if (args.Contains("--no-floor-reflections", StringComparer.OrdinalIgnoreCase))
        {
            settings = settings with { FloorReflections = false };
        }

        // And the third: the towns the port builds out where the game left them empty. An
        // override for this run, written back to nobody's settings file, for the same
        // reason as the two above — the picture that shows it working is TR1 photographed
        // with it and without it, one after the other.
        if (args.Contains("--towns", StringComparer.OrdinalIgnoreCase))
        {
            settings = settings with { RebuiltTowns = true };
        }

        if (args.Contains("--no-towns", StringComparer.OrdinalIgnoreCase))
        {
            settings = settings with { RebuiltTowns = false };
        }

        // Content the game shipped with and cannot reach. Off unless asked for, because it
        // is content the developers switched off — a player who did not ask for it should
        // get the game as it was released, and a bug report about a line nobody else hears
        // should be traceable to the switch that turned it on.
        //
        // Under the override layer on purpose: a file the player put in overrides/ is
        // theirs, and is the one thing this must not rewrite.
        // Scenery added to a room that shipped without enough of it — Couiza and
        // Rennes-les-Bains. Two gates, and they are different questions. Whether the
        // geometry is installed is asked of the disc, once, because a table naming forty
        // models nothing has would place nothing and warn forty times; whether the player
        // wants it is asked of the settings at every door, because it is a row on the
        // Picture page. See Content/SceneDressing.
        Content.DressedTowns installedTowns = SceneDressing.Installed(
            packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty
                : Beside(enhancedDirectory, "models"),
            packs);

        // What the run starts with. The room loop asks the same question again against
        // whatever the menu has since been told — see the CutContent.Open below it.
        Content.DressedTowns Dressed() =>
            settings.RebuiltTowns ? installedTowns : Content.DressedTowns.None;

        Content.DressedTowns dressing = Dressed();

        // The other half of a restoration: files no barn has and none can, for rooms that
        // were cut before there was anything to cut them from — and, since 2026-09-10,
        // for the one room that was never cut because it was never Sierra's: the bookshop.
        // Not gated on the restoration tier, as it was until then, because the layer only
        // answers for names the archives do not know and cannot put a player anywhere by
        // itself: what sends somebody to TE2 is a restored rule, which is behind the tier,
        // and what sends them to the bookshop is the door below, which is behind whether
        // the room is installed. Opened before the restoration table because that second
        // gate is a question about this layer.
        AddedAssets rebuilt = AddedAssets.Open(
            packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty
                : Beside(enhancedDirectory, "rooms"),
            packs);

        rebuilt.Overrides = overrides;

        if (!rebuilt.IsEmpty)
        {
            archives.Added = rebuilt;
            Log.Info($"Added assets: {rebuilt.Describe()}");
        }

        // Whether St. George's Books is installed, which is what decides whether RC1's
        // bookstore door can ever be opened. See Assets/Story/Bookshop.txt.
        bool bookshop = rebuilt.Has(Bookshop);

        var restoreDiagnostics = new DiagnosticBag();
        CutContent restored = CutContent.Open(RestorationTier(args, settings), dressing, bookshop);

        if (!restored.IsEmpty)
        {
            archives.Restoration = restored;
            archives.RestorationDiagnostics = restoreDiagnostics;

            // Applied here rather than when a room first asks for one of these files, so
            // that an edit which no longer matches the installation is reported at startup
            // beside everything else — not in the middle of a scene, once, as the only sign
            // that a line the player was told about will never be heard. The result is
            // cached, so the rooms themselves pay nothing for it.
            foreach (string name in restored.Names)
            {
                archives.Read(name);
            }

            foreach (Diagnostic diagnostic in restoreDiagnostics.Items)
            {
                Log.Report(diagnostic);
            }
        }

        // Said either way, and said here rather than left to be inferred from a fuller
        // room. A player wondering why Couiza is empty should be able to read the answer
        // off the first screen of log — and the two answers are not the same answer: a set
        // that is not installed and a set that is switched off look identical in the room
        // and are fixed in completely different places.
        Log.Info(!settings.RebuiltTowns
            ? "Scene dressing: switched off, so TR1, RL1 and RC3 are as the game shipped "
              + "them" + (installedTowns == Content.DressedTowns.None
                  ? string.Empty
                  : " — the geometry for them is installed and unused")
            : dressing == Content.DressedTowns.None
            ? $"Scene dressing: none — nothing has {SceneDressing.Sentinel}, so TR1, RL1 "
              + "and RC3 are as the game shipped them"
            : "Scene dressing: " + string.Join(
                " and ",
                new[]
                {
                    dressing.HasFlag(Content.DressedTowns.Couiza) ? "Couiza" : null,
                    dressing.HasFlag(Content.DressedTowns.RennesLesBains)
                        ? "Rennes-les-Bains" : null,
                    dressing.HasFlag(Content.DressedTowns.RennesLeChateau)
                        ? "Rennes-le-Château's cemetery gateway" : null,
                }.Where(name => name is not null))
              + ", from the installed geometry");

        // Said either way, like the dressing above and for the same reason: a door that
        // stays shut and a door that was never wired look the same from the square.
        Log.Info(bookshop
            ? "Bookshop: installed, so RC1's bookstore door gives on the fifth try"
            : $"Bookshop: not installed — nothing answers for {Bookshop}, so RC1's "
              + "bookstore door stays closed");

        // Before the window, the device and the menu. A room that is not in the archives
        // fails the same way whenever it is noticed, and noticing it here means the player
        // is told what is wrong instead of watching the game quit the moment they press
        // Play.
        //
        // After the added assets rather than before them, because two of the rooms this
        // build can open are in no archive: TE2 and the bookshop exist only in that layer,
        // and asking before it is attached would refuse to start in the one case the
        // layer is for.
        if (archives.Read(sceneName + ".SIF") is null)
        {
            Log.Error(
                $"No room called {sceneName}: the archives have no {sceneName}.SIF.");

            Log.Error(
                "Check what was passed to --scene or --start, or drop it and the game "
                + $"starts where it starts, in {OpeningScene}.");

            return 2;
        }

        Log.Info(restored.Describe() is { } putBack
            ? $"Cut content: {putBack}"
            : "Cut content: not restored.");



        Log.Info(File.Exists(settingsPath)
            ? $"Settings: {settingsPath}"
            : $"Settings: none yet, they will be written to {settingsPath}");

        // The three directories the game writes to, probed now rather than at the moment
        // somebody first tries to save. Each one has already chosen between beside the
        // executable and the user's own profile; what is being asked here is whether the
        // choice actually works. On Linux and macOS it is where an install run as the
        // wrong user, or unpacked into a read-only place, first shows itself - and a
        // player who cannot save finds out an hour later otherwise.
        StartupReport.Optional("Enhanced textures", enhancedDirectory,
            "The game will look as it originally shipped.");

        StartupReport.Writable("Settings", Path.GetDirectoryName(Settings.DefaultPath) ?? InstallPaths.UserData);
        StartupReport.Writable("Saves", Game.SaveStore.DefaultDirectory);
        StartupReport.Writable("Shader cache", Rendering.Shaders.ShaderCompiler.DefaultCacheDirectory);

        // Which graphics API to draw through. The window has to be opened for the right one
        // — Silk refuses to make a Vulkan window on a machine with no loader, and a Direct3D
        // machine should not need one — so this is decided before there is a window rather
        // than after there is a renderer.
        string? backendAsked = CommandLine.BackendAsked(args);
        Rendering.RenderBackend backend = ChooseBackend(backendAsked, settings);

        // What the player has dropped into libs/, and NVIDIA's loader started against it.
        //
        // Both before the renderer, and Streamline emphatically so on Vulkan: its features
        // ask for device extensions and for queues of their own, and there is no way to add
        // either to a device that already exists. Starting it here is what makes DLSS
        // selectable from the pause menu rather than only at the next launch. Direct3D is
        // the other way round — a device is made first and Streamline is told about it — so
        // that backend starts its own and this one is left alone.
        var runtimes = Rendering.Upscaling.UpscalerRuntimes.Find(Option(args, "--libs-dir"));
        Log.Info(runtimes.ToString());

        // Before Streamline, and it has to be: Streamline asks every feature it was told to
        // load for its requirements while it starts, and a feature the driver declined once
        // is not asked again. See NgxFeatureTable for what is being filled in and why the
        // driver cannot load the network without it.
        if (settings.NeuralUplift)
        {
            Rendering.Upscaling.NgxFeatureTable.TryEnable();
        }

        // --width and --height, for photographing the interface at a display size this
        // machine has not got. Everything about the interface's size is decided from the
        // framebuffer, so there is no other way to see what a 4K display would show.
        //
        // The window, the renderer and Streamline together, because which window to open
        // depends on which renderer is going to draw into it, and that is only settled once
        // the renderer exists: a Direct3D machine that turns out not to be one gets a Vulkan
        // window instead. See OpenRenderer.
        OpenedRenderer drawing = OpenRenderer(
            backend,
            insisted: backendAsked is not null,
            $"GK3Reborn - {sceneName}",
            int.TryParse(Option(args, "--width"), out int windowWidth) && windowWidth > 0
                ? windowWidth
                : 1280,
            int.TryParse(Option(args, "--height"), out int windowHeight) && windowHeight > 0
                ? windowHeight
                : 720,
            runtimes,
            Option(args, "--libs-dir"));

        using Platform.SilkGameWindow window = drawing.Window;
        using Rendering.Upscaling.Streamline? streamline = drawing.Streamline;
        using Rendering.IRenderer renderer = drawing.Renderer;

        renderer.Runtimes = runtimes;

        ReportGraphics(renderer.Survey());
        Log.Info($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        // What covers the gaps. Both of them here, at the first moment there is a device to
        // draw with, and emphatically not further down where they are first used: a window
        // that has never been presented to shows whatever the desktop last put there, and
        // everything between this line and the first room is blocking reads. Off a
        // mechanical disc with the enhanced packs to get through, that was a white
        // rectangle for the better part of a minute.
        //
        // The fade spans two passes of the room loop — the picture is caught at the end of
        // one and comes back once the next is standing — and the loading screen takes it
        // over when a load outlasts it. See UI.LoadingScreen.
        var fade = new Rendering.ScreenFade(window, renderer);
        var loading = new UI.LoadingScreen(window, renderer, fade);

        // Counted from the launch rather than from here, because the player has been
        // waiting since they double-clicked: bringing the device up and compiling the
        // shaders is seconds of a cold start, and it happens before there is anything that
        // could draw a bar. Judging this load from here would decide it was quick when the
        // whole of what made it slow had already happened.
        loading.Begin(Since.Elapsed);

        // And now there is a frame in it, the window goes up. Opened hidden on purpose —
        // see SilkGameWindow.Show — so what appears is black with a bar on it rather than
        // the white rectangle an unpainted window is.
        window.Show();

        var diagnostics = new DiagnosticBag();
        SceneRequest request = Playable(archives, sceneName, timeblock);
        Gk3SheepApi api = request.Api ?? new Gk3SheepApi(new GameState());

        // What the two of them set out with. Nothing in the shipped data hands these over,
        // so a player without them cannot use the pay phone — Prince James's card is where
        // the number comes from — and Day 1 10am cannot be finished at all. Loading a save
        // clears the bag first, so this is the start of a new game and nothing else.
        int pockets = Game.StartingItems.Fill(api.State.Inventory);

        // Here rather than only in Opening, because a scene file's [ACTORS], [AMBIENT] and
        // [MODELS] blocks are each guarded by a condition read at load: what --did says has
        // happened has to be true before the room decides who is standing in it.
        Already(args, api);

        Log.Info(
            $"Carrying: {pockets} items to begin with, " +
            $"{string.Join(", ", api.State.Inventory.ItemsOf(api.State.Ego))}");

        // What makes a waited call take time. Without it every line of dialogue in the
        // game is over in the frame it starts.
        //
        // The letter is the language's: a script writes StartVoiceOver("1LLJ644QR1") and
        // the file on a French disc is F1LLJ644QR1.YAK. It is set here rather than left at
        // its English default because this is where the language is known, and getting it
        // wrong is silent — every voice-over reports a length of zero and the game plays
        // the lines with no time to say them in.
        api.Animations = new AnimationLibrary(archives) { Language = language.Prefix };

        // Where saved games go. In the player's own profile beside the settings, and given
        // to the API rather than kept here because the console and the story reach saving
        // through the same door the interface does.
        api.Saves = new Game.SaveStore();

        // Who the player has been introduced to, which decides whether a label may use
        // somebody's name. The conditions are the action files' own; see
        // Assets/Story/Introductions.txt. Read before the imports below, which need it:
        // an original save records none of what those conditions ask about.
        Game.Story.Introductions introductions = Game.Story.Introductions.Open();

        Log.Info(
            $"Introductions: {introductions.Count} people are strangers until met");

        // The saves the 1999 game wrote, brought across once each. A save file this engine
        // has already imported is left alone, so deleting an import is how somebody asks
        // for it again, and the original .gk3 is never touched or moved.
        //
        // The game's own saves folder is searched first and always, whatever else is on
        // the command line. That is where somebody with a .gk3 file and no 1999 install
        // will put it, and it is the folder a deployed build keeps its games in — three
        // .gk3 files sitting beside the game's own saves is the obvious thing to expect to
        // work, and until this it was the one place nobody looked.
        var searched = new List<string> { api.Saves.Directory };

        // And the application's own, when the store has been put somewhere else: a
        // read-only install sends saves to the profile, and the .gk3 files would still be
        // beside the executable where they were dropped.
        string beside = Path.Combine(AppContext.BaseDirectory, "saves");

        if (!searched.Contains(beside, StringComparer.OrdinalIgnoreCase))
        {
            searched.Add(beside);
        }

        // Then both places the original itself wrote to: its install root, which is the
        // parent of the Data directory this engine was pointed at, and the "Save Games"
        // folder beside it that later installs used.
        if (Path.GetDirectoryName(Path.GetFullPath(
                Option(args, "--data") ?? DefaultDataDirectory())) is { Length: > 0 } installRoot)
        {
            searched.Add(installRoot);
            searched.Add(Path.Combine(installRoot, "Save Games"));
        }

        int broughtAcross = searched.Sum(
            where => Game.OriginalSaves.Import(where, api.Saves, api.Scores, introductions));

        if (broughtAcross > 0)
        {
            Log.Info(
                $"Imported {broughtAcross} save(s) written by the original game");
        }

        // What is left to do before the menu can be drawn, as fractions of the way there.
        // Hand-placed and roughly even, because the pieces are not comparable to each other
        // — a sound device that has to be opened, a folder of saves that has to be read,
        // a typeface that has to be rasterised — and there is nothing to count. What they
        // are is honest about their own footing: the bar says which of a known list of
        // steps the game is on, and the list does not change between machines. Anything
        // slower is the same step taking longer, which is what a bar that has stopped
        // moving is supposed to mean.
        loading.At(0.10);

        if (request.State is not null)
        {
            Log.Info($"Story: {request.State.Timeblock} in {request.State.Location}");
        }

        // Sound. The device may not open — a machine without one, or one already held —
        // and the game runs quietly rather than not at all.
        Audio.OpenAlBackend? audio = Audio.OpenAlBackend.Open(settings.Speakers, diagnostics);

        // Before anything plays, so the first sound of the session is already at the level
        // the player left it at rather than at full volume for a moment.
        settings.ApplyTo(audio);

        // The same precedence as the rest of the content stack: a player's loose
        // override, then restored audio in ReBarn, then the legally installed original.
        var sounds = new SoundLibrary(archives, packs);

        SceneAudio? room = audio is null
            ? null
            : new SceneAudio(sounds, api.Animations, audio);

        Log.Info(audio is null
            ? "Audio: none, the game runs silent"
            : $"Audio: {audio.DeviceName}");

        loading.At(0.20);

        // Movies. The packs hold them, and so does the workspace unless --rebarn says the
        // packs are the whole of the answer — the same rule as every other enhanced kind,
        // with the loose file winning where both have one.
        VideoLibrary videos = VideoLibrary.Open(
            packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty
                : Beside(enhancedDirectory, "video"),
            packs,
            localized,
            packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty
                : Path.Combine(
                    Beside(enhancedDirectory, "localized"), language.FileCode));

        using var movies = new Game.MoviePlayer(videos, audio)
        {
            // Every film passed over, the intro's and the story's alike. A room that is
            // entered through a cutscene cannot be reached in less than the cutscene
            // otherwise, and looking at TE4 costs 144 seconds of DAY3-5 before the room
            // is on screen even once.
            Skipping = args.Contains("--no-movies", StringComparer.OrdinalIgnoreCase),
        };

        // Fourteen of the films carry their own subtitles, in a YAK of the film's own name,
        // translated in every release. It matters most where a language never dubbed its
        // cutscenes: Spanish and Portuguese films are spoken in English, and this is the
        // whole of what those two have. Read through the animation library, which reads
        // through the archives, which read through the language pack.
        movies.Subtitles = name => api.Animations?.Read(name);

        loading.At(0.35);

        if (movies.Skipping)
        {
            Log.Info("Movies: skipped, every film passed over");
        }

        if (videos.Count > 0)
        {
            // The decoders are the engine's own, so there is nothing to find and nothing
            // that can be missing.
            Log.Info(
                $"Movies: {videos.Count} available ({videos.LooseCount} loose, " +
                $"{videos.PackedCount} packed), decoded in process");

            // Separately, because it is the half of a localised run that nobody can see. A
            // movie playing the wrong language's soundtrack looks exactly like one playing
            // the right one, so the counts are said before a single film has run.
            if (videos.LocalizedCount > 0 || videos.LocalizedSoundCount > 0)
            {
                Log.Info(
                    $"Movies: {language.Name} re-cuts {videos.LocalizedCount} of them and "
                    + $"supplies the soundtrack for {videos.LocalizedSoundCount} more");
            }
            else if (localized is not null)
            {
                Log.Warning(
                    $"Movies: the {language.Name} pack carries no soundtracks, so every "
                    + "film is heard in the language the installation holds.");
            }
        }

        // The host outlives the room. Its scripts and its registrations belong to the
        // story rather than to the room, and reloading them at every door would lose
        // whatever a script was in the middle of.
        // Who the cast are and how each of them walks. Read once: it describes the game's
        // people rather than any one room, and every room asks the same questions of it.
        Game.Actors.CharacterLibrary characters = Game.Actors.CharacterLibrary.Open(archives);

        // How each of their faces is put together. Read once for the same reason: it
        // describes the cast rather than any one room, and without it nobody in the game
        // blinks, and nobody's mouth moves while they speak.
        Game.Actors.FaceLibrary faces = Game.Actors.FaceLibrary.Open(archives);

        // Behaviour scripts named by other behaviour scripts, and by Sheep. Read once each
        // and kept: NEWIDLE and SetIdleGAS between them name about fifty, and a character
        // may be handed the same one many times over a session.
        Dictionary<string, Formats.Animation.GasFile?> behaviours =
            new(StringComparer.OrdinalIgnoreCase);

        Formats.Animation.GasFile? Behaviour(string name)
        {
            if (behaviours.TryGetValue(name, out Formats.Animation.GasFile? known))
            {
                return known;
            }

            // With the extension when the name does not carry one, which none of them
            // does: a scene file writes `idle=jeaIdle.gas` and a script writes
            // `SetIdleGAS("Emilio", "Eml110aBenchIdle")`, and all 168 names the scripts
            // pass are the second kind. Without the retry every one of them read nothing
            // and the character it belonged to stood perfectly still — Emilio walked to
            // his bench in the square and then never moved again.
            byte[]? bytes = archives.Read(name) ??
                (Path.HasExtension(name) ? null : archives.Read(name + ".GAS"));

            Formats.Animation.GasFile? read = bytes is not null
                ? Formats.Animation.GasFile.Parse(bytes)
                : null;

            behaviours[name] = read;
            return read;
        }

        // Which verbs are things to say rather than things to do. Without it a topic is
        // indistinguishable from a verb, every line of it is offered at once, and none of
        // them is ever used up.
        // Which floor is which, which shoes make which noise on it.
        Game.Actors.Footsteps footsteps = Game.Actors.Footsteps.Open(archives);

        if (footsteps.SurfaceCount > 0)
        {
            Log.Info(
                $"Footsteps: {footsteps.SurfaceCount} floor textures classified, " +
                $"{footsteps.SoundCount} shoe and ground pairings");
        }

        Game.Actions.VerbLibrary verbs = Game.Actions.VerbLibrary.Open(archives);

        // What the game calls places and times, in the player's own language. Without it
        // the corner of the screen reads "LBY - 110A", which is two codes and no help.
        GameStrings strings = GameStrings.Open(archives);

        // The port's own interface, in the language the game is being played in. GK3's own
        // strings come out of the archives through the pack; these never existed in 1999,
        // so they are carried per language and read here — see UI/UiText.cs.
        UiText words = UiText.Of(archives.Localization?.Language, archives.Localization);

        Log.Info($"Interface: {words.Count} phrase(s) from {words.Source}");

        // And in the player's own language from here on. Before this the screen has been
        // drawing a bar and no word, which is also what it does when the sheet of letters
        // is not cut yet — see UI.LoadingScreen.
        loading.Text = words;
        loading.At(0.55);

        if (strings.Count > 0)
        {
            Log.Info($"Names: {strings.Count} from {strings.File}");
        }
        else if (localized is not null)
        {
            // Worth a line of its own. A missing string table is not a crash and not a
            // blank screen — it is every place and every timeblock in the game showing its
            // code instead of its name, which reads as an unfinished port rather than as a
            // file that was not found.
            Log.Warning(
                $"Names: no {GameStrings.TableFor(archives)}, so places and times are "
                + "shown by their codes.");
        }

                var host = new ScriptHost(api);

        // Scripts wait for real here, unlike in the tools, because here there is a clock
        // for them to wait against.
        host.Scheduler = new SheepScheduler(host.Machine);

        var catalogue = new Sheep.SheepSignatures();

        Log.Info(
            $"Scripts: {LoadScripts(archives, host, catalogue)} loaded, " +
            $"{catalogue.Count} function signatures");

        // The rules that decide when a point in the story is over. Code rather than a
        // script, so there is nothing to compile and nothing that can fail to load; said
        // here anyway because the count is worth seeing beside the scripts that were.
        Log.Info(
            $"Story rules: {Game.Story.TimeblockRules.Known.Count} timeblocks");

        // The interface. GK3's own bitmap fonts rather than anything imported: they are in
        // the archives, they are the right size for the game's own screens, and reading one
        // is a smaller job than shaping a scalable typeface would be.
        // The shapes as well as the transforms: GK3's characters have no skeleton, so
        // without them a walk is mesh groups sliding about rather than anybody walking.
        var clips = new ClipLibrary(archives) { KeepVertices = true };
        var fonts = new FontLibrary(archives);
        GameHud? hud = null;
        ScreenPainter? screens = null;

        // Grace's computer, which the story runs through: parchments are scanned into it,
        // analysed and translated, and DoesSidneyFileExist is a real condition in the
        // game's own action files. One for the whole run, like the console — what has been
        // scanned is part of the game rather than part of a room.
        var sidney = new Game.Sidney.SidneyMachine(
            Game.Sidney.SidneyLibrary.Open(archives), api.State)
        {
            // 391 pages of encyclopedia and the 393 spellings that reach them. Grace looks
            // things up, and what she can find is a real puzzle rather than a menu.
            Search = Game.Sidney.SidneySearch.Open(archives),

            // What each verse of Le Serpent Rouge is worth when the map confirms it.
            Scores = api.Scores,

            // What the player's things are called, which is the one family of per-object
            // text GK3 localised, and which language the dozen phrases Sidney says that the
            // 1999 game has no string for should be said in.
            Names = strings,
            Language = archives.Localization?.Language.Code
                ?? Content.GameLanguage.Default.Code,
        };

        api.Sidney = sidney;

        // The map the moped is ridden around, and its road network.
        DrivingMap map = DrivingMap.Open(archives);

        // What can be seen from where, through the binoculars. Twenty-one vantage points
        // between the Armchair of the Devil and the tower at Blanchefort.
        Binoculars binoculars = Binoculars.Open(archives);

        loading.At(0.70);

        // One console for the whole run, not one per room. Its history and its scrollback
        // are the player's working notes, and losing them at every door would make it
        // useless for the one thing it is best at: watching something across a transition.
        var console = new GameConsole { Catalogue = catalogue };

        // The typeface. An outline is rasterised at whatever size the window is, so the
        // interface is crisp on a display of any size; GK3's own sheets are 640x480 art
        // that can only be magnified by whole numbers and look it.
        Formats.Fonts.TrueTypeFile? face =
            args.Contains("--bitmap-font", StringComparer.OrdinalIgnoreCase)
                ? null
                : InterfaceFont(Option(args, "--font-file"), enhancedDirectory, diagnostics);

        Log.Info(face is { } chosen
            ? $"Typeface: {chosen.Family}, {chosen.CharacterCount} characters, drawn from outlines"
            : "Typeface: GK3's own bitmap sheets");

        loading.At(0.80);

        int wantedGlyph = UI.TextSizing.Sheet(window.FramebufferHeight, settings.TextScale);

        // --font names one outright, for looking at a particular sheet.
        string[] ladder = Option(args, "--font") is { Length: > 0 } named
            ? [named]
            : CaptionFonts;

        // The atlas the room's interface draws with, and the larger one the menu does.
        // Two sizes rather than one magnified: an outline drawn at the size it is wanted
        // is the whole point of having one.
        //
        // The player's text size is read here rather than passed in, because this closes
        // over the settings the menu writes to: a row dragged in the pause menu is felt by
        // the next cut without anything having to hand the new value along.
        OverlayAtlas? Cut(bool menu)
        {
            int height = window.FramebufferHeight;
            float scale = settings.TextScale;

            // Every language's letters, not the caller's: see OverlayAtlas.Everything.
            // The bitmap ladder below needs no such argument — a .FON carries whatever
            // characters its own release drew, which for Polish is ą ć ę ł ń ś ź ż.
            if (face is not null &&
                OverlayAtlas.Build(
                    face, UI.TextSizing.Em(height, menu, scale), OverlayAtlas.Everything)
                    is { } drawn)
            {
                return drawn;
            }

            int wanted = menu
                ? Math.Max(
                    UI.TextSizing.Sheet(height, scale),
                    UI.TextSizing.Em(height, true, scale) * 2 / 3)
                : UI.TextSizing.Sheet(height, scale);

            return fonts.Nearest(wanted, ladder) is { } sheet ? OverlayAtlas.Build(sheet) : null;
        }

        if (Cut(menu: false) is { } atlas)
        {
            // A sheet has to be magnified to reach the size wanted; an outline was drawn
            // at it.
            int magnify = atlas.Scalable || atlas.Font is null
                ? 1
                : Magnification(atlas.Font, wantedGlyph);

            renderer.SetOverlayAtlas(atlas);

            // And the loading screen stops drawing with the block of white it has been
            // making do with. From here it can write the word as well as the bar; before
            // here there was no sheet of letters in the game to cut one from, which is
            // most of what the wait it covers is spent doing. See UI.LoadingScreen.
            loading.Atlas = atlas;
            loading.At(0.90);

            hud = new GameHud(new Overlay(atlas) { Magnify = magnify })
            {
                Names = strings,
                Text = words,
            };

            screens = new ScreenPainter(new Overlay(atlas) { Magnify = magnify })
            {
                // The game's own names for the player's things, in the player's own
                // language. Both painters get the same table: the inventory screen and the
                // strip along the top of the room name the same objects, and two of them
                // disagreeing would be worse than either name on its own.
                Names = strings,
                Text = words,
            };

            // Sidney's map, the survey the whole puzzle is played on. Beside the driving
            // map's art because both hang off the pipeline the atlas just rebuilt.
            //
            // <b>The enhanced set is preferred, and this is the strongest case for one in
            // the game.</b> The survey is 1,368 pixels shown in about 450, the places the
            // puzzle is played on are red crosses three pixels across, and the screen can
            // now be zoomed six times into it. The upscale is 2,736 square. Taking the
            // original here was why the crosses were barely visible.
            EnhancedTextures? better = Pictures(
                settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                language: language);

            // Loose files first, then the packs. Both, because the interface's own pictures
            // are enhanced content like any other and the shipped form of enhanced content
            // is a pack: reading only the loose set meant they were there in a development
            // tree with ContentWorkspace beside it and gone from every actual installation.
            // The map and the driving sprites survived that -- they fall back to the 1999
            // bitmap and merely lose the upscale -- but the portraits have no original to
            // fall back to and simply were not drawn.
            Formats.Bitmaps.DecodedImage? Enhanced(string name) =>
                better?.Read(name) ?? Packed(packs, localized, name);

            Formats.Bitmaps.DecodedImage? survey =
                Enhanced(Game.Sidney.SidneyMap.Picture) ??
                Decoded(archives, Game.Sidney.SidneyMap.Picture + ".BMP");

            if (survey is { } drawn)
            {
                renderer.AddOverlayPicture(Game.Sidney.SidneyMap.Picture, drawn);
            }

            // The suspects' faces, rendered from their own heads by the offline tool. They
            // are enhanced content and nothing else: an installation with neither the packs
            // nor the loose set draws the names alone, which is what the original does.
            //
            // Gabriel goes with them although he is on nobody's list, because the driving
            // map draws the player as a marker like everybody else's and the alternative is
            // the green square the original left him as. Loaded here rather than beside the
            // map's own art because it is a portrait, made the same way and framed the same
            // way, and the map takes the same square out of it.
            List<string> everybody =
            [
                .. Game.Sidney.SidneySuspect.Portraits,
                Game.DrivingTraffic.EgoFace,
                Game.DrivingTraffic.TwoMenFace,
            ];

            int portraits = 0;

            foreach (string portrait in everybody)
            {
                if (Enhanced(portrait) is { } likeness &&
                    renderer.AddOverlayPicture(portrait, likeness) > 0)
                {
                    portraits++;
                }
            }

            Log.Info(portraits > 0
                ? $"Sidney: {portraits} of {everybody.Count} portraits"
                : "Sidney: no portraits; the suspect list draws names alone.");

            // The driving map's own art. After the atlas, because setting an atlas rebuilds
            // the pipeline the pictures hang off and would throw them away.
            //
            // The enhanced set is preferred where it has one: the markers are upscaled
            // there and the map is drawn at whatever size the window affords, so the
            // 55-pixel original is exactly the case an upscale is for.
            LoadMapArt(archives, renderer, screens, Enhanced);

            Log.Info(
                $"Interface: {atlas.Name}, {atlas.Count} glyphs at {atlas.Height}px" +
                (magnify > 1 ? $" x{magnify}" : string.Empty) +
                $" (wanted {wantedGlyph} for a {window.FramebufferHeight}-line display), " +
                $"sheet {atlas.Image.Width}x{atlas.Image.Height}, " +
                $"{(renderer.HasOverlay ? "drawing" : "NOT drawing")}");
        }
        else
        {
            Log.Info("Interface: no font found, nothing is drawn over the room");
        }

        // What each thing in the player's pockets looks like. Read once; the pictures
        // themselves are loaded the first time an item is shown and kept after that,
        // because a game reaches perhaps a dozen items at a time out of the hundred and
        // thirty that exist, and which dozen is not knowable here.
        Game.InventoryArt itemArt = Game.InventoryArt.Open(archives);
        Dictionary<string, UI.ItemIcon> itemPictures = new(StringComparer.OrdinalIgnoreCase);

        UI.ItemIcon Icon(string item)
        {
            if (itemPictures.TryGetValue(item, out UI.ItemIcon already))
            {
                return already;
            }

            // Remembered whether or not there was anything to find: twenty of the items the
            // table names have no list picture, and looking again every frame for a file
            // that is not there is a search of every archive per frame.
            UI.ItemIcon icon = itemArt.Icon(archives, item) is { } picture &&
                renderer.AddOverlayPicture("item:" + item.ToUpperInvariant(), picture) is > 0 and { } number
                    ? new UI.ItemIcon(number, picture.Width, picture.Height)
                    : default;

            itemPictures[item] = icon;

            return icon;
        }

        // And the same thing held up to the light. A close-up is the picture the artists
        // painted of the thing itself — the book of the immortals is 606 by 314 and its
        // two pages are meant to be read — where the list picture beside it is a 94-pixel
        // square. Kept apart from the list pictures rather than replacing them: the strip
        // along the foot of the screen wants the square, and uploading a page of a book
        // per item to draw it at thumbnail size is the wrong way round.
        Dictionary<string, UI.ItemIcon> itemCloseUps = new(StringComparer.OrdinalIgnoreCase);

        UI.ItemIcon CloseUp(string item)
        {
            if (itemCloseUps.TryGetValue(item, out UI.ItemIcon already))
            {
                return already;
            }

            UI.ItemIcon art = itemArt.CloseUp(archives, item) is { } picture &&
                renderer.AddOverlayPicture("closeup:" + item.ToUpperInvariant(), picture) is > 0 and { } number
                    ? new UI.ItemIcon(number, picture.Width, picture.Height)
                    : default;

            itemCloseUps[item] = art;

            return art;
        }

        // And the game's own art by file name, for the pieces of the interface that are a
        // picture rather than a drawing — the handheld GPS is the whole device painted,
        // labels and all. Cached the same way and for the same reason: looking again every
        // frame for a file that is not there is a search of every archive per frame.
        Dictionary<string, UI.ItemIcon> artwork = new(StringComparer.OrdinalIgnoreCase);

        UI.ItemIcon Artwork(string file)
        {
            if (artwork.TryGetValue(file, out UI.ItemIcon already))
            {
                return already;
            }

            UI.ItemIcon art = default;

            if (archives.Read(file) is { } bytes)
            {
                try
                {
                    Formats.Bitmaps.DecodedImage picture =
                        Formats.Bitmaps.BitmapDecoder.Decode(bytes, file);

                    art = renderer.AddOverlayPicture("art:" + file.ToUpperInvariant(), picture)
                        is > 0 and { } number
                        ? new UI.ItemIcon(number, picture.Width, picture.Height)
                        : default;
                }
                catch (Formats.FormatParseException)
                {
                    // Drawn without it, which for everything here means not drawn at all.
                }
            }

            artwork[file] = art;

            return art;
        }

        // What each verb looks like. The original drew its verb ring as these and nothing
        // else, so they are the picture a returning player already reads faster than the
        // word beside them; VERBS.TXT names one for all but three of the 287.
        //
        // Held by file name rather than by verb, because the file is what the picture is,
        // and the game reuses one across several verbs — DIAL, DRIVE and eleven more all
        // draw i_operate_std, and holding them by verb would upload the same 32-pixel
        // square thirteen times.
        //
        // The archives' own art, not the enhanced set: there are no upscales of these yet.
        // When there are, this is the one place that has to learn to prefer them.
        Dictionary<string, UI.ItemIcon> verbPictures = new(StringComparer.OrdinalIgnoreCase);

        UI.ItemIcon VerbIcon(string verb, bool lit)
        {
            if (verbs.IconOf(verb, lit) is not { Length: > 0 } file)
            {
                return default;
            }

            if (verbPictures.TryGetValue(file, out UI.ItemIcon already))
            {
                return already;
            }

            UI.ItemIcon icon = default;

            // Remembered whether or not there was anything to find. Three of the names the
            // file gives are of pictures nobody shipped, and looking again every frame for
            // one of those is a search of every archive per frame.
            if (archives.Read(file) is { } bytes)
            {
                try
                {
                    Formats.Bitmaps.DecodedImage art =
                        Formats.Bitmaps.BitmapDecoder.Decode(bytes, file);

                    icon = renderer.AddOverlayPicture("verb:" + file, art) is > 0 and { } number
                        ? new UI.ItemIcon(number, art.Width, art.Height)
                        : default;
                }
                catch (Formats.FormatParseException)
                {
                    // A picture that will not decode is a verb drawn by its word alone,
                    // which is what a verb with no picture at all gets.
                }
            }

            verbPictures[file] = icon;

            return icon;
        }

        // A setting carried over from another machine, or from another card. DLSS is
        // NVIDIA's and runs on nothing else, so a settings file that asks for it on a Radeon
        // is answered here rather than by a menu row that can never be made to work.
        //
        // Not written back. The file keeps what it says until the player changes something
        // on that page themselves, so moving a profile between two machines does not cost
        // them the setting on the one that could use it.
        if (!renderer.OfferedUpscalers.Contains(settings.Upscaler))
        {
            Log.Info(
                $"Upscaling: {settings.Upscaler} needs an NVIDIA card and this is a " +
                $"{renderer.Vendor} one, so the built-in upscaler is used instead.");

            settings = settings with { Upscaler = Rendering.Upscaling.UpscalerKind.Spatial };
        }

        // The menu, and what changing something in it reaches. Everything below is set
        // live rather than at the next room: a volume that only takes effect after a door
        // is a volume the player cannot hear themselves setting.
        var front = new FrontEnd(settings)
        {
            Offered = renderer.OfferedUpscalers,
            StoredAt = settingsPath,

            // What the Language row may step through: the packs that are actually beside
            // the game, plus English, which every installation can read without one.
            Languages = languages,

            Text = words,
        };

        MenuPage? pages = hud is null
            ? null
            : new MenuPage(new Overlay(Cut(menu: true) ?? hud.Overlay.Atlas)
            {
                Magnify = hud.Overlay.Magnify,
            })
            {
                Text = words,
            };

        SceneUpdate? live = null;

        // Set when the Language row moves, cleared when the room has been reloaded for it.
        bool relanguage = false;

        // Whether the language has moved since this was last asked. A question rather than
        // a flag the frame loop can see, because the frame loop is another method and the
        // pack, the string table and the words are all locals of this one. Asking clears it,
        // so one change reloads one room.
        bool Relanguaged()
        {
            bool moved = relanguage;

            relanguage = false;

            return moved;
        }

        // Reads the game in another language, without restarting it.
        //
        // The pack is the door everything comes through, so swapping it is most of the job:
        // the string table, Sidney's documents, every recorded line, every YAK that
        // lip-syncs one and every bitmap with words painted into it are all read through
        // archives.Localization and answer differently the moment it moves.
        //
        // What has already been read has to be forgotten with it. The sounds and the fonts
        // are cached by name and a name means a different file now; the string table, the
        // port's own words and Sidney's text were parsed from the old pack; and the letter
        // in front of every voice-over is the language's own.
        //
        // The room is then loaded again, which is what brings the pictures back: a texture
        // is uploaded when its room loads, so the sign over the shop is the one that was on
        // the wall when the player walked in. The reload is the same door a restored save
        // goes through, so nothing of the story is lost by it.
        void Relanguage(GameLanguage wanted)
        {
            var bag = new DiagnosticBag();
            LocalizedContent? opened = LocalizedContent.Open(packDirectory, wanted, bag);

            foreach (Diagnostic diagnostic in bag.Items)
            {
                Log.Report(diagnostic);
            }

            localized?.Dispose();
            localized = opened;
            language = wanted;

            // Cleared first so the comparison below reads the installation rather than the
            // language that is being left. Same question as at startup, and it has to be
            // asked again: whether a pack repeats the installation depends on which pack it
            // is, and this is the one place the pack changes. See RepeatsInstallation.
            archives.Localization = null;
            opened?.RepeatsInstallation(archives.Read);

            archives.Localization = opened;
            sounds.Forget();
            fonts.Forget();

            api.Animations.Language = wanted.Prefix;

            strings = GameStrings.Open(archives);
            words = UiText.Of(wanted, opened);

            if (api.Sidney is { } machine)
            {
                machine.Library = Game.Sidney.SidneyLibrary.Open(archives);
                machine.Search = Game.Sidney.SidneySearch.Open(archives);
                machine.Names = strings;
                machine.Language = wanted.Code;
            }

            if (hud is not null)
            {
                hud.Names = strings;
                hud.Text = words;
            }

            if (screens is not null)
            {
                screens.Names = strings;
                screens.Text = words;
            }

            front.Text = words;

            if (pages is not null)
            {
                pages.Text = words;
            }

            Log.Info(
                $"Language: now {wanted.Name}; {words.Count} interface phrase(s) from "
                + $"{words.Source}, {strings.Count} names from {strings.File}");

            relanguage = true;
        }

        // The sun over the room the player is in, for the rays: remembered here because the
        // row that turns them on and off is pressed between rooms as well as in them.
        Rendering.SunRays daylight = Rendering.SunRays.None;

        bool RaysWanted(Settings chosen) =>
            chosen.SunRays && !args.Contains("--no-sun-rays", StringComparer.OrdinalIgnoreCase);

        // And how hot the room is: the heat haze over its far ground, decided with the
        // room and switched with the row.
        float heat = 0f;

        bool HazeWanted(Settings chosen) =>
            chosen.HeatHaze && !args.Contains("--no-shimmer", StringComparer.OrdinalIgnoreCase);

        void Apply(Settings chosen)
        {
            // Before the assignment, because `settings` is still the old answer here and
            // this is the only place the two can be compared. The room says "camera bounds"
            // when it loads and would otherwise say nothing at all about a switch thrown
            // halfway through it.
            if (chosen.FreeCamera != settings.FreeCamera)
            {
                Log.Info(chosen.FreeCamera
                    ? "Camera bounds: off, so the camera may leave the room"
                    : "Camera bounds: back on");
            }

            // Said here for the same reason the speaker layout is: the language decides
            // which pack the archives were opened through, which letter every voice-over
            // carries and which code page the text was decoded in, and all three were
            // settled before the window existed. A player who changes it and hears the
            // same voices would reasonably conclude the row is broken.
            if (!string.Equals(chosen.Language, settings.Language, StringComparison.Ordinal))
            {
                settings = chosen;
                Relanguage(GameLanguage.Of(chosen.Language));
            }

            settings = chosen;
            chosen.ApplyTo(audio);

            // Which key and which pad button do which job, and how fast a stick drives the
            // cursor. Handed over here rather than read by the window, because the window is
            // below the game and must not know what a settings file is.
            window.Bindings = Platform.InputBindings.Restore(chosen.Bindings);

            // Nought is the switch as well as the speed: a stick that moves the cursor no
            // pixels a second is a stick that does not move the cursor, and a second flag
            // saying the same thing is a second thing to keep in step.
            window.PointerSpeed = chosen.GamepadCursor ? chosen.GamepadCursorSpeed : 0f;
            window.PointerScale = chosen.CursorScale;

            if (renderer.SupportsRayTracing)
            {
                renderer.Quality = chosen.Quality;
            }

            // The picture's own two plans. Both are values, so handing over one that has
            // not changed does nothing at all, and handing over one that has takes effect
            // at the top of the next frame — which is what makes every row on those two
            // pages something the player can watch happen.
            renderer.Upscaling = chosen.Upscaling;
            renderer.Output = chosen.Output;

            // And what the two reflection rows on the Picture page ask for. A value, like
            // the two above, so handing over one that has not changed does nothing and one
            // that has takes effect at the top of the next frame.
            renderer.Reflections = new ReflectionPlan(
                chosen.Reflectivity,
                chosen.FloorReflections &&
                    !args.Contains("--no-floor-reflections", StringComparer.OrdinalIgnoreCase));
            renderer.VerticalSync = chosen.VerticalSync;

            // And the sun's rays, which are the room's sun with the row's answer on it.
            renderer.SetSunRays(daylight.Lit(RaysWanted(chosen)));
            renderer.Shimmer = HazeWanted(chosen) ? heat : 0f;

            window.Present(chosen.Display, chosen.DisplayWidth, chosen.DisplayHeight);

            api.State.CameraGliding = chosen.CameraGlide;
            api.State.CinematicsEnabled = chosen.Cinematics;
            api.State.EasterEggs = chosen.EasterEggs;
            api.State.PlotArmour = chosen.PlotArmour;
            api.State.CatchesPendulum = chosen.CatchesPendulum;

            // And the moustache, if the story has reached the afternoon it belongs to.
            // Here as well as on the way into each room, so that turning the assistance on
            // while standing in the middle of that afternoon hands it over at once rather
            // than at the next door.
            if (chosen.AlwaysWearsMoustache && Game.Assists.GiveMoustache(api.State))
            {
                Log.Info($"Assist: {Game.Assists.Owner} is given the {Game.Assists.Moustache}");
            }

            // He wears it whatever the clock says and whatever he is carrying, because that
            // is what the row promises. The faces in the room are composed once, when it is
            // built, so changing this from the pause menu has to compose them again — a
            // switch the player cannot see working is one they will take to be broken.
            if (live?.Faces is { } worn)
            {
                if (chosen.AlwaysWearsMoustache)
                {
                    worn.ComposedFrom[Game.Assists.PlainFace] = Game.Assists.MoustachedFace;
                }
                else
                {
                    worn.ComposedFrom.Remove(Game.Assists.PlainFace);
                }

                if (worn.Recompose() is > 0 and { } faces)
                {
                    Log.Info(chosen.AlwaysWearsMoustache
                        ? $"Assist: {faces} face(s) composed from {Game.Assists.MoustachedFace}"
                        : $"Assist: {faces} face(s) back to their own");
                }
            }

            if (live is not null)
            {
                live.HurryFactor = chosen.HurryFactor;
            }
        }

        // At the start, not only when something changes: a stored setting has to reach the
        // game on a run where the player never opens the menu at all.
        Apply(settings);

        if (frontEnd && pages is not null)
        {
            // The game's own title screen: the angel, with the name painted into it. This
            // is the one piece of GK3's interface art the port keeps, because it is a
            // picture rather than a widget — the rows over it are still drawn.
            // The enhanced set is opened here rather than borrowed from the room loop,
            // which has not run yet. It only lists a directory.
            // Three places it can come from and one of them is all a shipped game has.
            // The enhanced set is opened here rather than borrowed from the room loop,
            // which has not run yet; both it and the compressed set only list what is
            // there.
            TitleScreen title = TitleArt(
                archives,
                Pictures(
                    settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    language: language),
                settings.EnhancedTextures
                    ? CompressedTextures.Open(
                        packsOnly
                            ? string.Empty
                            : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty),
                        packs,
                        overrides,
                        localized)
                    : overrides is null && localized is null
                        ? null
                        : CompressedTextures.Open(string.Empty, null, overrides, localized),
                diagnostics);

            front.Illustrated = title.Exists;

            // The port's own title screen: six layers out of the pack, put on the device
            // as the interface's own pictures and drawn into the menu's display list. It
            // needs the whole set, so a game with no Reborn.rebarn gets null here and opens
            // on the picture above -- which is why the row that switches between them is
            // dead rather than absent when there is nothing to switch to.
            Content.MenuArt menuArt = Content.MenuArt.Open(
                packs,
                packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty
                    : Beside(enhancedDirectory, "menu"),
                overrides,
                diagnostics);

            UI.TitleScene? modern = settings.ModernMenu
                ? UI.TitleScene.Build(menuArt, renderer.AddOverlayPicture)
                : null;

            front.ModernMenuAvailable = menuArt.Complete;

            // The party behind the title, for whoever spells the word. Built when it is
            // asked for rather than here: it is a dozen models and their clips out of the
            // archives, and almost nobody asks. Its textures come in the way a room's do,
            // so the dancers wear the enhanced set where there is one. See UI.DiscoParty.
            if (modern is not null)
            {
                modern.PartyMaker = wall =>
                {
                    var dressing = new Game.SceneLoader(archives)
                    {
                        Enhanced = Pictures(
                            settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                            language: language),
                        Compressed = settings.EnhancedTextures
                            ? CompressedTextures.Open(
                                packsOnly
                                    ? string.Empty
                                    : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty),
                                packs,
                                overrides,
                                localized)
                            : null,
                    };

                    var partyDiagnostics = new DiagnosticBag();

                    UI.DiscoParty? party = UI.DiscoParty.Build(
                        new UI.DiscoPartyContent(
                            name => archives.Read(name + ".MOD") is { } bytes
                                ? Formats.Models.ModFile.Parse(bytes, name)
                                : null,
                            clips,
                            api.Animations,
                            (sink, texture) => dressing.LoadTextureLate(sink, texture, partyDiagnostics),
                            sounds,
                            audio,
                            wall)
                        {
                            // --dance-guests bar,gra photographs a guest or two on their own.
                            Only = Option(args, "--dance-guests") is { Length: > 0 } few
                                ? new HashSet<string>(
                                    few.Split(',', StringSplitOptions.RemoveEmptyEntries),
                                    StringComparer.OrdinalIgnoreCase)
                                : null,
                        },
                        renderer,
                        Log.Info);

                    if (party is not null)
                    {
                        Log.Info($"Disco: the word was spelled; {string.Join(", ", party.Guests)}");
                    }

                    foreach (Diagnostic complaint in partyDiagnostics.Items)
                    {
                        Log.Info($"Disco: {complaint}");
                    }

                    return party;
                };
            }

            // The port's own screen carries the game's name in its own lettering, so the
            // page must not write it out as well -- the same reason TITLE.BMP suppresses it.
            front.Illustrated = title.Exists || modern is not null;

            Log.Info(menuArt.Complete
                ? modern is not null
                    ? $"Title screen: the port's own, {menuArt.Count} layers from {menuArt.From}"
                    : "Title screen: the port's own is available and switched off, so the "
                        + $"original is drawn ({menuArt.From})"
                : menuArt.Count > 0
                    ? $"Title screen: the original; {string.Join(", ", menuArt.Missing)} "
                        + $"missing from {menuArt.From}"
                    : "Title screen: the original; the port's own layers are not installed");

            // Behind the loading screen from here on, and behind the menu after it. The
            // wait between pressing New Game and the first room is the longest one in the
            // game, and a bar over the title art reads as the game starting where a bar
            // over black reads as the game having gone away.
            //
            // Still the 1999 picture even when the modern screen is up: the modern one is
            // drawn by the menu, and the menu is not running yet. What the bar is over is
            // whichever of them the game has.
            title.Show(renderer);
            loading.At(0.97);

            // Which of them it took, because they are indistinguishable on screen until
            // somebody has actually upscaled the picture — and a run that quietly used the
            // 640x480 original looks exactly like one that used the new one.
            Log.Info(title.Exists
                ? $"Title: {TitlePicture} at {title.Width}x{title.Height}, {title.From}"
                : $"Title: no {TitlePicture} to be had, so the menu draws its own screen");

            // The theme, under the menu and nowhere else. Looped: it is a minute long and
            // somebody may sit on the title screen for longer than that.
            Audio.AudioVoice theme = Theme(audio, sounds);

            Log.Info(theme.Exists
                ? $"Theme: {ThemeMusic}, under the menu"
                : $"Theme: no {ThemeMusic} to play, so the menu is silent");

            // Everything the menu needs is now in hand, so the screen that covered getting
            // it comes down, over its own third of a second. What is under it is the title
            // art the menu is about to draw its rows over.
            loading.Done();

            void Films(IReadOnlyList<string> which)
            {
                // The film has its own soundtrack and the theme would play under it.
                audio?.Silence(theme);
                modern?.Hush();
                renderer.SetBackdrop(null);

                ShowIntro(window, renderer, movies, pages, which, front.Settings.MovieSubtitles);

                // The gesture that skipped the film is still on the frame's books, and the
                // menu is about to be drawn under the pointer that made it. Without this,
                // holding the mouse to skip the intro releases onto whichever row the
                // pointer happens to be over and the game starts, or quits.
                window.EndFrame();

                title.Show(renderer);

                // The party's music rather than the theme, where the party is on: it goes
                // on until the game starts or ends, and the intro is a detour.
                if (modern?.Party is not null)
                {
                    modern.Resume();
                    renderer.SetBackdrop(null);
                }
                else
                {
                    theme = Theme(audio, sounds);
                }
            }

            // The theme stops the moment the word is spelled. What follows is the ball
            // coming down to its own sound effects, and then the bar's music. And the 1999
            // picture comes down with it: the port's own screen paints black over it, and
            // the party paints nothing, so it would show through the dance floor.
            if (modern is not null)
            {
                modern.PartyStarted = () =>
                {
                    audio?.Silence(theme);
                    renderer.SetBackdrop(null);
                };
            }

            // --frames is a run that photographs something and ends, and no such run wants
            // to sit through two films first.
            if (settings.PlayIntro &&
                frameLimit == 0 &&
                !args.Contains("--skip-intro", StringComparer.OrdinalIgnoreCase))
            {
                Films(IntroMovies);
            }
            else
            {
                title.Show(renderer);
            }

            // --front-page opens on one of the settings pages, for the same reason
            // --frames exists here: a page three keystrokes in cannot be photographed by a
            // run that has no keyboard.
            if (Option(args, "--front-page") is { Length: > 0 } wantedPage &&
                Enum.TryParse(wantedPage, ignoreCase: true, out FrontEndPage opened))
            {
                front.Show(opened);
            }

            FrontEndOutcome asked;

            // What the slots hold, so the title screen's Restore has something to show. The
            // pause menu filled this in and the title screen never did, so Restore from the
            // first menu listed nothing while the same store held three saves.
            front.Saves = api.Saves?.List() ?? [];
            front.Illustrations = slot => Illustration(renderer, api.Saves, slot);

            // --dance spells the word before the first frame, for a run that photographs
            // the party rather than somebody who found it. After the picture has gone up,
            // because spelling the word takes it down again.
            if (modern is not null && args.Contains("--dance", StringComparer.OrdinalIgnoreCase))
            {
                modern.Spell();
            }

            // Round again for the Intro row, which is the one thing on the menu that goes
            // somewhere and comes back.
            do
            {
                asked = ShowMenu(
                    window,
                    renderer,
                    pages,
                    front,
                    Apply,
                    modern is not null
                        ? MenuBehind.Modern
                        : title.Exists ? MenuBehind.Picture : MenuBehind.Nothing,
                    () => Cut(menu: true),
                    frameLimit,
                    screenshotPath,
                    modern);

                if (asked == FrontEndOutcome.Intro)
                {
                    // The film, not the publisher's logo. Somebody who asked for the intro
                    // asked for the intro.
                    Films([TheIntro]);
                }
            }
            while (asked == FrontEndOutcome.Intro && !window.IsClosing);

            // The theme does not belong to the game about to start; the picture does, for
            // a little longer. The room fills the window itself and the backdrop comes down
            // when it is standing — until then it is what the loading screen's bar is drawn
            // over, and the alternative is the longest wait in the game spent looking at
            // black. See UI.LoadingScreen.
            audio?.Silence(theme);

            // The title screen's layers, on the other hand, are done with: eleven megabytes
            // of the interface's picture list, and the interface is about to become the
            // verb bar. They are not read again -- the pause menu is drawn over the room.
            if (modern is not null)
            {
                foreach (string layer in Content.MenuArt.Layers)
                {
                    renderer.DropOverlayPicture(UI.TitleScene.Named(layer));
                }

                // And the party, if there was one: its scene comes off the renderer before
                // the room's goes on, its music stops with it, and the 1999 picture goes
                // back up for the loading screen's bar to be drawn over.
                //
                // Frames are still in flight reading the party's buffers, and freeing
                // those underneath the device is a crash somewhere else entirely — on
                // Vulkan, a segmentation fault on the way out.
                if (modern.Party is not null)
                {
                    renderer.SetScene(null, null);
                    renderer.Idle();
                    title.Show(renderer);
                }

                modern.Dispose();
            }

            // Restoring from the title screen. The save says where the player was, and that
            // is the first room rather than the one the command line asked for. This used to
            // fall through to the quit below: a Load outcome was "not Play", and choosing a
            // save on the first menu closed the game.
            if (asked == FrontEndOutcome.Load &&
                front.Slot is { Length: > 0 } chosenSlot &&
                api.Saves?.Read(chosenSlot, out Game.SaveFault titleFault) is { } titleSave)
            {
                api.RestoreGame(titleSave);
                request = SceneRequest.Continuing(api, api.State.Location);
                Log.Info($"Restored {chosenSlot}: {titleSave.Title}");
                asked = FrontEndOutcome.Play;
            }

            if (asked != FrontEndOutcome.Play)
            {
                // Quit from the first menu, so nothing of the room is ever loaded. The
                // device and the archives go on the way out as they would anyway.
                audio?.Dispose();
                return 0;
            }
        }
        else if (frontEnd)
        {
            Log.Info("Front end: no font, so the game starts in the room");
        }

        int result = 0;
        bool first = true;

        // One pass a room. A door is a script that says SetLocation and nothing more, so
        // going through one is this loop coming round again rather than anything the room
        // itself knows how to do.
        var finishes = SurfaceFinishes.Empty;

        while (true)
        {
            // The first frame of the transition, before anything is read. What follows —
            // the material library, the enhanced sets, the packs — is opened before the
            // loader exists to offer frames of its own, and on a cold start it is long
            // enough to eat most of the fade.
            //
            // Beginning here rather than at the loader, for that reason: the clock this
            // starts is what decides whether the load was slow, and a load whose first half
            // second went on opening the packs was slow whatever the loader then took.
            //
            // A bar only on the way into the first room, which is the wait between pressing
            // New Game and the game existing. Every pass after that is a door, and what a
            // door should look like is the fade and nothing else — the screen still runs,
            // because it is what presents the fade's frames while the room is read.
            loading.Begin(bar: first);

            // On the way into every room rather than once, because the afternoon the
            // moustache belongs to is reached by walking through a door and can also be
            // arrived at by loading a save. Giving it is idempotent: everything about
            // whether it has happened already is in the state. See Game.Assists.
            if (settings.AlwaysWearsMoustache && Game.Assists.GiveMoustache(api.State))
            {
                Log.Info($"Assist: {Game.Assists.Owner} is given the {Game.Assists.Moustache}");
            }

            using SceneGeometry geometry = renderer.CreateGeometry();

            // What each texture's surface is like. Read once and shared by every room:
            // it is a property of the corpus, not of a scene, and it is what tells the
            // renderer that the church floor is polished and the pews are not.
            if (first)
            {
                finishes = SurfaceFinishes.Load(
                    Path.Combine(
                        Path.GetDirectoryName(
                            CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty)
                                .TrimEnd(Path.DirectorySeparatorChar, '/')) ?? ".",
                        "manifests",
                        "material-library.json"),

                    // And from the packs where there is no workspace to read it from,
                    // which is every installation that is not a development one. Without
                    // this the shipped game has no material library at all: every surface
                    // matte, no specular lobe anywhere, and no message to say why.
                    packs);

                if (finishes.Count > 0)
                {
                    Log.Info(
                        $"Surface finishes: {finishes.Count} textures measured, " +
                        $"{finishes.Reflective} smooth enough to reflect, " +
                        $"{finishes.Metallic} metal" +
                        (finishes.Mirrors > 0 ? $", {finishes.Mirrors} mirrors" : string.Empty) +
                        (finishes.Screens > 0 ? $", {finishes.Screens} lit screens" : string.Empty) +
                        (finishes.Corrected > 0
                            ? $", {finishes.Corrected} corrected by hand"
                            : string.Empty));
                }
            }

            geometry.Materials = finishes;

            // How far round things are rounded, so the same object can be photographed
            // both ways without editing anything.
            if (int.TryParse(
                    Option(args, "--round"), CultureInfo.InvariantCulture, out int levels) &&
                levels is >= 0 and <= 4)
            {
                geometry.RoundLevels = levels;
            }

            // Whether a railing, a fence or a chain gets the thickness of what is drawn on
            // it. Set here rather than at the room, because it gates the measurement too
            // and that happens as the room's textures are uploaded. A switch for the same
            // reason the others have one: so the same rail can be photographed both ways.
            geometry.ThickenCutoutCards =
                settings.ThickCutoutCards &&
                !args.Contains("--no-thick-cards", StringComparer.OrdinalIgnoreCase);

            // Whether the room is drawn one side at a time, which is what the original does
            // for all opaque world geometry. A switch for the usual reason: the picture that
            // shows it working is the same room with and without it.
            geometry.CullBackFaces =
                settings.CullBackFaces &&
                !args.Contains("--no-cull", StringComparer.OrdinalIgnoreCase);

            // And whether it stops the sun. Its own switch, because it is its own thing:
            // the thickness is geometry anybody can see and the shadow is an instance in
            // the acceleration structure, and a picture that shows one going wrong shows
            // nothing about the other.
            geometry.CardShadows =
                !args.Contains("--no-card-shadows", StringComparer.OrdinalIgnoreCase);

            // How many triangles a room's floor may be cut into. A switch because the right
            // number is a judgement about a picture: it buys the cell size, and whether a
            // cobble reads as a cobble or as a patch of ground is decided by how many cells
            // fit across one. Zero displaces nothing.
            if (int.TryParse(
                    Option(args, "--relief"), CultureInfo.InvariantCulture, out int budget))
            {
                geometry.Relief = budget > 0
                    ? ReliefSettings.Default with { TriangleBudget = budget }
                    : ReliefSettings.Off;
            }

            // A fresh loader each time: it carries the last room's glances and its count of
            // enhanced textures, and neither belongs to the next one.
            var loader = new SceneLoader(archives, Log.Info)
            {
                // The player's preference, with a command-line override so a screenshot can
                // be taken of the same room both ways without editing a settings file.
                SmoothHeads = HeadLevels(args, settings),

                // The same finishes the sink shades with, so the loader can say which of
                // an outdoor scene's textures deserve their relief cut beyond the floor.
                Finishes = finishes,

                // Already read, once, above. The loader would read it itself rather than
                // send anybody into a room undressed, and CHARACTERS.TXT at every door is
                // a cost with nothing to show for it.
                Characters = characters,
            };

            // What keeps the window drawing while the room is read: the transition's fade
            // while it has picture left to remove, and the loading screen after that. Set
            // here rather than in the initializer because it reads the loader's own account
            // of how far through it is — see SceneLoader.Through, and UI.LoadingScreen.
            loader.Progress = () => loading.At(loader.Through);

            {
                // The loose picture layer: the workspace's enhanced set with whatever the
                // player has put in overrides/ laid over it. Built even when there is no
                // workspace and even under --rebarn, because an override is the player's
                // own file and is not the enhanced content those turn off. Pictures
                // returns null when neither source has anything for a channel, so a game
                // with no overrides behaves exactly as it did.
                EnhancedTextures? enhanced = Pictures(
                    settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    language: language);

                loader.Enhanced = enhanced;

                // Normal maps sit beside the colour textures rather than among them: a
                // surface may have a better colour and no normal map, or the other way
                // round, and they are judged separately.
                EnhancedTextures? normals = Pictures(
                    settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    Formats.Rebarn.RebarnKind.Normal, "normals", language);

                // --flat leaves the colour textures enhanced and the surfaces smooth,
                // which is the only way to see what the normal pass alone is doing.
                bool flat = args.Contains("--flat", StringComparer.OrdinalIgnoreCase);

                loader.Normals = flat ? null : normals;

                // The other two generated sets, beside the normals for the same reason:
                // each is a separate pass and a separate judgement, and a surface may have
                // any combination of the three.
                loader.Orms = flat ? null : Pictures(
                    settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    Formats.Rebarn.RebarnKind.Orm, "orm", language);

                loader.Heights = flat ? null : Pictures(
                    settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    Formats.Rebarn.RebarnKind.Height, "height", language);

                if (first && normals is { Count: > 0 })
                {
                    Log.Info($"Normal maps: {normals.Count} available");
                }

                if (first && !packsOnly && settings.EnhancedTextures &&
                    enhancedDirectory is { Length: > 0 })
                {
                    Log.Info(enhanced is { Count: > 0 }
                        ? $"Enhanced textures: {enhanced.Count} available in {enhancedDirectory}"
                        : $"Enhanced textures: none found in {enhancedDirectory}");
                }
            }

            // The modelled trees, beside the textures and gated on their own setting. Not
            // inside the block above: this is geometry rather than a bitmap, it costs an
            // outdoor scene ten times its triangles, and somebody who wants the 1999
            // outline should be able to keep the rest of the enhancement.
            //
            // From the packs as well as from a workspace, and outside the --enhanced block
            // for the same reason the compressed textures are: a shipped game has packs and
            // no content workspace at all, so gating the trees on a loose directory would
            // mean nobody who installed the game ever saw one.
            // The grass, which is baked into the room like the trees are. Its own row and its
            // own switch, because it is the one addition here that costs a room triangles by
            // the hundred thousand.
            loader.Grass = settings.Grass &&
                !args.Contains("--no-grass", StringComparer.OrdinalIgnoreCase);

            if (settings.ModelledTrees)
            {
                TreeLibrary trees = TreeLibrary.Open(
                    packsOnly || enhancedDirectory is not { Length: > 0 }
                        ? string.Empty
                        : Beside(enhancedDirectory, "trees"),
                    packs);

                loader.Trees = trees;

                if (first && !trees.IsEmpty)
                {
                    Log.Info(
                        $"Modelled trees: {trees.Count} grown across {trees.SpeciesCount} " +
                        $"species, {(trees.Packed ? "packed" : "loose")}");
                }
            }

            // The restoration table follows its setting the same way the trees and the
            // improved geometry follow theirs: consulted every time a room is built, so
            // turning it on or off in the menu takes effect the next time the player walks
            // into one. The table itself is one per tier for the life of the process, so
            // this costs a dictionary lookup rather than a re-read and a re-apply.
            CutContent restoring = CutContent.Open(RestorationTier(args, settings), Dressed(), bookshop);
            archives.Restoration = restoring.IsEmpty ? null : restoring;
            archives.RestorationDiagnostics = restoring.IsEmpty ? null : restoreDiagnostics;

            // The improved room geometry, beside the trees and gated on its own setting for
            // the same reasons: it is geometry rather than a bitmap, it is optional at
            // every layer, and it comes from the packs as well as from a workspace because
            // a shipped game has packs and no content workspace at all.
            if (settings.ImprovedSceneGeometry)
            {
                EnhancedScenes rooms = EnhancedScenes.Open(
                    packsOnly || enhancedDirectory is not { Length: > 0 }
                        ? string.Empty
                        : Beside(enhancedDirectory, "scene-geometry"),
                    packs);

                loader.Scenes = rooms;

                if (first && !rooms.IsEmpty)
                {
                    Log.Info(
                        $"Improved scene geometry: {rooms.Count} room(s), " +
                        $"{(rooms.Packed ? "packed" : "loose")}");
                }
            }

            // Prop geometry that did not ship with the game. Not gated on a setting of its
            // own, and it does not need one: it answers only for names the archives have
            // no .MOD for, and the only reason a scene names one of those is that a
            // restoration put it there — which is behind a switch already. Nothing that
            // shipped can be replaced by it.
            ModelLibrary props = ModelLibrary.Open(
                packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty
                    : Beside(enhancedDirectory, "models"),
                packs);

            props.Overrides = overrides;
            loader.Models = props.IsEmpty ? null : props;

            if (first && props.Describe() is { } available)
            {
                Log.Info($"Prop models: {available}");
            }

            // Rooms the game never had, built from glTF. Beside the props and bounded the
            // same way: only a name the archives have no .BSP for reaches it, so no room
            // that shipped can be replaced by one.
            RoomLibrary builtRooms = RoomLibrary.Open(
                packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty
                    : Beside(enhancedDirectory, "rooms"),
                packs);

            builtRooms.Overrides = overrides;
            loader.Rooms = builtRooms.IsEmpty ? null : builtRooms;

            if (first && builtRooms.Describe() is { } roomsAvailable)
            {
                Log.Info($"Built rooms: {roomsAvailable}");
            }

            // The reconstructed horizon, beside the trees and gated on its own setting for
            // the same reason they are: it is geometry rather than a bitmap, and somebody
            // who wants the painted 1999 sky should be able to keep it with the rest of
            // the enhancement on. From the packs as well as from a workspace, like the
            // trees and for the same reason — a shipped game has packs and no content
            // workspace at all. A loose set wins over the packed one.
            if (settings.TerrainBackdrop)
            {
                string terrain = packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty
                    : Beside(enhancedDirectory, "terrain");

                loader.TerrainDirectory = Directory.Exists(terrain) ? terrain : null;
                loader.TerrainPacks = packs.VolumeCount > 0 ? packs : null;

                if (first && loader.TerrainDirectory is not null)
                {
                    Log.Info(
                        "Terrain horizon: " +
                        $"{Directory.EnumerateFiles(terrain, "*.heights.r32").Count()} sets, loose");
                }
                else if (first && loader.TerrainPacks is not null)
                {
                    int packedSets = packs.Names(Formats.Rebarn.RebarnKind.Raw)
                        .Count(n => n.EndsWith(".heights", StringComparison.OrdinalIgnoreCase));

                    if (packedSets > 0)
                    {
                        Log.Info($"Terrain horizon: {packedSets} sets, packed");
                    }
                }
            }

            // The block-compressed build of the same set, preferred over the originals
            // wherever it has an answer: nothing to decode, a mip chain already built, and
            // a quarter of the video memory. Outside the --enhanced block on purpose — a
            // shipped game has packs and no content workspace at all, and the packs are the
            // whole of its enhanced content.
            CompressedTextures compressed = CompressedTextures.Open(
                packsOnly
                    ? string.Empty
                    : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty),
                packs,
                overrides,
                localized);

            // The setting takes the compressed set out of the way as well as the loose one.
            // It is the same art in a smaller form, so leaving it in would answer "no" with
            // the enhanced textures still on screen.
            //
            // What survives it is the overrides on their own. A .dds a player put there is
            // not the remake's enhanced art and is not what either of these switches off,
            // for the same reason the picture layer above is built regardless.
            loader.Compressed =
                args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase) ||
                !settings.EnhancedTextures
                    ? overrides is null && localized is null
                        ? null
                        : CompressedTextures.Open(string.Empty, null, overrides, localized)
                    : compressed;

            // --flat means flat wherever the maps would have come from. It used to null
            // only the loose readers, which was the whole of the supply before the packs
            // could answer; now they can, it has to silence both or it silences nothing.
            if (args.Contains("--flat", StringComparer.OrdinalIgnoreCase))
            {
                loader.FlatSurfaces = true;
            }

            if (first && loader.Compressed is not null && compressed.Describe() is { } sets)
            {
                // Which set came from where, because the two are indistinguishable once a
                // texture is on screen: a run that quietly used a stale build/ directory
                // instead of the pack looks exactly like a run that used the pack.
                Log.Info($"Compressed textures: {sets}");
            }

            loading.Tick();

            var read = Stopwatch.StartNew();

            // Where the time goes, when somebody asked. Off unless --timings is given: the
            // stamps are cheap, but twenty lines of breakdown at every door is not what
            // anybody playing the game wants in their console.
            LoadTimeline? timeline = args.Contains("--timings", StringComparer.OrdinalIgnoreCase)
                ? new LoadTimeline()
                : null;

            loader.Timeline = timeline;
            geometry.Timeline = timeline;

            if (loader.Load(geometry, request, diagnostics) is not { } scene)
            {
                foreach (Diagnostic diagnostic in diagnostics.Items)
                {
                    Log.Report(diagnostic);
                }

                audio?.Dispose();
                loading.Done();
                fade.Cancel();
                return 3;
            }

            // Before the report, so that it describes something that exists. Finish is
            // idempotent and the renderer calls it again when the scene is set.
            geometry.Finish();
            timeline?.Stamp("upload to device (Finish)");
            loading.At(1);

            // The room's open flames, and the lights that stand in them. Nine of the
            // corpus's rooms have a fire in them and the other seventy-two get an empty
            // list and an unchanged rig. See Game.FlameLighting.
            IReadOnlyList<Game.Flame> fires =
                Game.Flames.In(scene.Models, api.Animations, scene.Bitmaps);
            IReadOnlyList<Formats.Scenes.AuthoredLight> burning =
                Game.FlameLighting.Rig(scene.Lights, fires);

            // And the painted flame cards out of the picture, because the fire is drawn as
            // a volume now and the card is the 1999 picture of one: opaque where it is lit,
            // writing depth, and a brown rectangle through the middle of anything drawn in
            // its place. Before the emitters are gathered, so that a hidden card is not
            // also counted as something in the room that glows.
            if (!args.Contains("--no-shader-fire", StringComparer.OrdinalIgnoreCase))
            {
                int cards = Game.Flames.Hide(fires, scene.Models);

                if (cards > 0)
                {
                    Log.Info(
                        $"Fire: {fires.Count} flame(s) drawn as burning gas, " +
                        $"{cards} painted card(s) taken out of the picture");
                }
            }

            // And the things in the room that glow. A self-lit surface is drawn at full
            // brightness and lights nothing, so every lamp shade, lit bulb, stained-glass
            // window and painted view in the game has been a bright object standing in a
            // room it did not light. Only the ones nobody put a light inside get one — see
            // Game.EmissiveLighting, which is FlameLighting's rule for the same reason.
            IReadOnlyList<Rendering.Geometry.EmissiveSurface> glows =
                args.Contains("--no-emissive", StringComparer.OrdinalIgnoreCase)
                    ? []
                    : geometry.Emitters();

            burning = Game.EmissiveLighting.Rig(burning, glows, out int glowing);

            // With the geometry's extent, so the rig can tell a lamp that decays from the
            // scene's key light — placed tens of thousands of units away with the two
            // hundred unit range 3ds Max left in the file and its attenuation switched off.
            // Honouring that range does not dim the sun, it deletes it. See
            // GpuLight.IsDistantKey.
            // And the room's daylight moved to the windows it is named for. A baker does
            // not care where a light stands and a tracer does: CS3's is above the roof, and
            // the attic gets no daylight at all until it is where the daylight comes in.
            // See Game.Daylight.
            var windows = new List<Game.Window>();

            foreach (string pane in scene.Geometry?.ObjectNames ?? [])
            {
                if (!Game.Daylight.IsWindow(pane) ||
                    SceneScripting.Bounds(scene, pane) is not var (low, high))
                {
                    continue;
                }

                windows.Add(new Game.Window(
                    pane,
                    (low + high) / 2f,
                    Vector3.Distance(low, high) / 2f));
            }

            burning = Game.Daylight.Rig(
                burning,
                windows,
                new SceneExtent(geometry.Minimum, geometry.Maximum),
                out int moved);

            if (moved > 0)
            {
                Log.Info(
                    $"Daylight: {moved} light(s) moved to {windows.Count} window(s), " +
                    "where a wall can shape them");
            }

            // And the shafts the daylight throws in at them, where the room is indoors and
            // has a sun this hour. Every object named for a pane, the church's stained glass
            // included; the roof test is what keeps a village's house windows from throwing
            // light out into the square. Kept on the scene: the dust that hangs in them is
            // lit by them in the frame loop. See Game.SceneShafts.
            scene.Shafts = [];
            heat = 0f;

            if (scene.Sun is not null)
            {
                bool roofed = Game.SceneShafts.IsRoofed(scene.Geometry, scene.Walkable);

                // The heat haze over the far ground, outdoors through the hot part of the
                // day: full at noon and two, less at ten and four, none at all after.
                heat = roofed ? 0f : Game.SceneShafts.Heat(api.State.Timeblock);

                var panes = new List<(string Name, Vector3 Minimum, Vector3 Maximum)>();

                foreach (string pane in scene.Geometry?.ObjectNames ?? [])
                {
                    if (Game.SceneShafts.IsPane(pane) &&
                        SceneScripting.Bounds(scene, pane) is var (low, high))
                    {
                        panes.Add((pane, low, high));
                    }
                }

                if (panes.Count > 0 && roofed)
                {
                    scene.Shafts = Game.SceneShafts.For(
                        panes,
                        scene.Sun,
                        (geometry.Minimum, geometry.Maximum),
                        scene.Ground is { } underfoot ? underfoot.Height : null);
                }

                if (scene.Shafts.Count > 0)
                {
                    Log.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Daylight: {scene.Shafts.Count} shaft(s) at {panes.Count} window(s), " +
                        $"{scene.Shafts.Count(s => s.Strength >= 1f)} of them the sun's own"));
                }
            }

            if (args.Contains("--daylight-list", StringComparer.OrdinalIgnoreCase))
            {
                foreach (Game.Window pane in windows)
                {
                    Log.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  window: {pane.Owner}, {pane.Radius * 2:F0} units across"));
                }
            }

            // And balanced for the amount of tracing it is about to be evaluated under.
            // These are baking rigs: a room's fills, ambients and bounce lights are the
            // 1999 stand-in for the global illumination the tracer now computes, and
            // running both is the same light twice. See Game.RigBalance.
            burning = Game.RigBalance.For(
                burning, renderer.Quality, out int dimmed, settings.RealisticLighting);


            renderer.SetLights(
                burning, new SceneExtent(geometry.Minimum, geometry.Maximum));
            timeline?.Stamp("light rig");

            if (dimmed > 0)
            {
                float keep = Game.RigBalance.Keep(
                    renderer.Quality, settings.RealisticLighting);

                Log.Info(keep <= 0f
                    ? $"Rig: {dimmed} of {burning.Count} lights are the bake's own fill, " +
                      "switched off — only real sources light this room"
                    : $"Rig: {dimmed} of {burning.Count} lights are the bake's own fill, " +
                      $"turned down to {keep * 100:F0}% " +
                      "against the traced occlusion that replaces them");
            }

            if (args.Contains("--emissive-list", StringComparer.OrdinalIgnoreCase))
            {
                foreach (Rendering.Geometry.EmissiveSurface glow in glows)
                {
                    Log.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  glows: {glow.Owner} ({glow.Texture}), {glow.Radius:F1} units across"));
                }
            }

            if (glowing > 0)
            {
                Log.Info(
                    $"Emissive: {glowing} glowing thing(s) lit that had no light of their own");
            }

            if (fires.Count > 0)
            {
                int wavering = burning.Count(l => l.Flicker is { Bias: > 0.5f });
                int lit = burning.Count - scene.Lights.Count;

                string added = lit > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" and {lit} lit that had none")
                    : string.Empty;

                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Fire: {fires.Count} open flame(s), {wavering} of the artists' lights " +
                    $"wavering with them{added}"));
            }

            if (scene.Sun is { } sun)
            {
                Log.Info(
                    $"Sun: elevation {MathF.Asin(-sun.Direction.Y) * 180f / MathF.PI:0}°, " +
                    $"the rig's other {scene.Lights.Count - 1} lights kept");
            }

            // And its rays, drawn through whatever stands between it and the eye: past the
            // trees outdoors, in at the windows indoors. A room with no sun — after dark —
            // hands over none, which is also what takes the last room's rays away.
            daylight = Rendering.SunRays.For(scene.Sun).Through(scene.Shafts);
            renderer.SetSunRays(daylight.Lit(RaysWanted(settings)));
            renderer.Shimmer = HazeWanted(settings) ? heat : 0f;

            if (heat > 0f)
            {
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Heat: haze over the far ground at {heat:F1}"));
            }

            // The air in the room, for the handful that have any. Set with the rig rather
            // than per frame: a layer of fog is a fact about the room, and what moves inside
            // it runs on the shader's own clock. Nothing is said for the two hundred rooms
            // with none, which is also what the renderer is told — a room the player walks
            // into from a foggy one has to have the fog taken off it again.
            //
            // The hour as well as the room, because four of the rooms below are outdoors and
            // are only foggy at two in the morning. This is read here rather than held with
            // the room for that reason: the same cemetery is a different place at two in the
            // afternoon, and the player reaches both by walking through the same gate.
            Rendering.FogVolume air = Game.SceneFog.For(scene.Name, api.State.Timeblock);
            renderer.SetFog(air);

            if (air.Any)
            {
                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Fog: lying to y={air.Top:0.#}, thinning over {air.Falloff:0.#} units, " +
                    $"{air.Density:0.####} a unit in {air.Steps} steps"));
            }

            // And whatever was in the air of the room before this one, taken off it for the
            // same reason the fog is. The blended list is held by the renderer until
            // something replaces it, and the frame loop only replaces it while there is
            // something to draw or something it drew last frame to clear — a latch that
            // starts fresh with every room. So a room whose particles were the last thing
            // set, left for a room that has none of its own, hands its own over: TE5's lit
            // swords stayed on screen after a save was restored into another room, burning
            // in the air where the board had been. Cleared here rather than left to the
            // frame loop, because this is the one point every room change goes through.
            renderer.SetParticles([]);

            renderer.Quality = renderer.SupportsRayTracing
                ? quality ?? settings.Quality
                : RayTracingQuality.None;

            if (first)
            {
                Log.Info(renderer.SupportsRayTracing
                    ? $"Ray tracing: {renderer.Quality} ({geometry.TraceableTriangleCount} opaque "
                      + $"triangles traced in {geometry.TraceablePartCount} movable part(s))"
                    : "Ray tracing: unavailable on this device");
            }

            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Loaded {scene.Name} in {read.Elapsed.TotalMilliseconds:F0} ms, " +
                $"{geometry.TextureCount} textures resident, {geometry.TexturesReused} reused, " +
                $"{geometry.TextureDeviceBytes / (1024.0 * 1024):F0} MB of them on the device"));

            // What this load actually read, rather than what was available to it. The counts
            // are cumulative over the session, so walking through a door adds to them.
            if (compressed.FromPacks > 0 || compressed.FromFiles > 0)
            {
                Log.Info(
                    $"Blocks read: {compressed.FromPacks} from packs, "
                    + $"{compressed.FromFiles} from {(compressed.Directory.Length > 0
                        ? compressed.Directory
                        : "loose files")}");
            }

            Log.Info($"Scene {scene.Name}: {geometry.TriangleCount} triangles in "
                + $"{geometry.BatchCount} batches, {geometry.TextureCount} textures"
                + (loader.EnhancedTexturesUsed > 0
                    ? $" ({loader.EnhancedTexturesUsed} enhanced"
                      + (loader.CompressedUsed > 0 ? $", {loader.CompressedUsed} compressed)" : ")")
                    : string.Empty)
                + (loader.NormalMapsUsed > 0
                    ? $", {loader.NormalMapsUsed} normal mapped"
                    : string.Empty)
                + (loader.OrmMapsUsed > 0
                    ? $", {loader.OrmMapsUsed} with a finish"
                    : string.Empty)
                + (loader.HeightMapsUsed > 0
                    ? $", {loader.HeightMapsUsed} with relief"
                    : string.Empty)
                + $", {scene.Lights.Count} authored lights");

            // What the floor cost, when it was displaced. Worth its own line because the
            // triangle count above jumps by an order of magnitude when this fires, and
            // without saying so it reads as something having gone wrong.
            if (geometry.DisplacedTriangles > 0)
            {
                string uncut = geometry.ReliefSetApart > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $", {geometry.ReliefSetApart} left uncut")
                    : string.Empty;

                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Relief: floor cut into {geometry.DisplacedTriangles} triangles at " +
                    $"{geometry.ReliefCell:0.#} units a cell, moved up to " +
                    $"{geometry.ReliefDepth:0.##} units ({geometry.ReliefTypically:0.##} typically), " +
                    $"{geometry.ReliefBoundary.Pinned} edges held down and " +
                    $"{geometry.ReliefBoundary.Continued} carried on " +
                    $"(expected {geometry.ReliefExpected}{uncut})"));
            }

            // What the round things cost, and — more to the point — that they happened at
            // all. A rounding that silently declines is invisible: the object is still
            // there, still drawn, still the shape it always was.
            if (geometry.RoundedObjects > 0)
            {
                Log.Info(
                    $"Rounded: {geometry.RoundedTriangles} triangles from " +
                    $"{string.Join(", ", geometry.Rounded.Order(StringComparer.OrdinalIgnoreCase))}");
            }

            // What the railings cost, and that they happened. Silent when it declines, in
            // exactly the way the rounding is: the rail is still there and still drawn.
            if (geometry.CardsThickened > 0)
            {
                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Railings: {geometry.CardsThickened} keyed cards thickened to " +
                    $"{geometry.CardThickness.Thinnest:0.##}-{geometry.CardThickness.Thickest:0.##} " +
                    $"units, {geometry.CardTriangles} triangles"));

                if (geometry.CardShadowTriangles > 0)
                {
                    Log.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Railing shadows: {geometry.CardShadowTriangles} opaque triangles " +
                        $"traced against"));
                }
            }

            // The floor, which is how an actor knows what height to walk at. Reported
            // because its absence is silent: a room that names no floor object, or names
            // one the geometry does not have, walks everybody at the height they set off
            // at and looks fine until the first ramp.
            if (args.Contains("--lights", StringComparer.OrdinalIgnoreCase))
            {
                foreach (Game.Flame flame in fires)
                {
                    string drawn = flame.Visible ? string.Empty : ", not yet drawn";

                    Log.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  flame {flame.Model} at {flame.Position.X:F0},{flame.Position.Y:F0}," +
                        $"{flame.Position.Z:F0} {flame.Height:F1} tall and " +
                        $"{flame.Width:F1} across, a {flame.Kind} burning " +
                        $"{flame.Plume:F1} tall and {flame.Radius * 2f:F1} across from " +
                        $"y {flame.Foot.Y:F1}, " +
                        $"swings {flame.Swing * 100:F0}% at {flame.Rate:F1} Hz{drawn}"));
                }

                foreach (Formats.Scenes.AuthoredLight light in burning)
                {
                    string wavers = light.Flicker is { } flicker
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $" flickers {flicker.Swing * 100:F0}% about {flicker.Bias:F0}")
                        : string.Empty;

                    Log.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  light {light.Name} r={light.Radius:F1} i={light.Intensity:F2} " +
                        $"reach={light.AttenuationEnd:F0} at {light.Position.X:F0}," +
                        $"{light.Position.Y:F0},{light.Position.Z:F0}{wavers}"));
                }
            }

            Log.Info(scene.Ground is { } ground
                ? $"Floor: {scene.Definition.FloorObject()}, {ground.Triangles} triangles"
                : $"Floor: none; {scene.Definition.FloorObject() ?? "the scene names one"}" +
                  " is not in the geometry, so actors hold the height they start at");

            Report(diagnostics, verbose);

            // Everything from here to the first presented frame is the room being made
            // ready rather than read, and it is inside the wait the player sees.
            timeline?.Stamp("scene report");

            // Whatever was waiting was waiting on the room that has gone.
            host.Scheduler.Clear();

            var update = new SceneUpdate(
                scene,
                api,
                loader.Glances,
                geometry,
                scene.Actions,
                new ActionRunner(api),
                host.Scheduler);

            // Registered again for the new room, and after the update exists because the
            // walking functions need something to walk in. The scene functions close over
            // the room they were given and the last registration wins.
            SceneScripting.Attach(api, scene, loader.Glances, room, update, Behaviour);
            Showing(api, movies);

            // What is under the pointer, and — for the rooms that fire something into
            // theirs — what is in front of a laser beam. Built here rather than at the
            // frame loop below so that both have the same one: a second copy would be a
            // second pass over every triangle in the room.
            var interaction = new SceneInteraction(scene, api)
            {
                Strings = strings,
                Text = words,
                Watcher = update,
                Introductions = introductions,
            };

            // The eleven rooms whose puzzles the original implemented in code rather than
            // in data. Built after the scripting is attached, because a mechanism reaches
            // the same calls a script does, and set on both the room and the host: the
            // room steps it every frame and the host both performs and prices
            // CallSceneFunction. See Game.Mechanisms.SceneMechanism.
            api.Mechanism = Game.Mechanisms.SceneMechanisms.For(
                scene.Definition.Mechanism(), update, api, archives);

            update.Mechanism = api.Mechanism;

            if (api.Mechanism is { } machinery)
            {
                Log.Info($"Mechanism: {scene.Name} is a {machinery.Name} room");

                // Where a beam ends is whatever the room puts in front of it, which is the
                // same question the pointer asks and is answered by the same picker rather
                // than by a second copy of the room's triangles. The beams themselves are
                // left out of it: the first pair to cross would otherwise stop each other.
                if (machinery is Game.Mechanisms.LaserHeads beams)
                {
                    beams.Cast = ray => interaction.Cast(ray, Game.Mechanisms.LaserHeads.Beams);
                }

                machinery.Begin();

                if (machinery.Report() is { Length: > 0 } parts)
                {
                    Log.Info($"  {parts}");
                }
            }

            // --movie NAME plays one straight away, which is how a cutscene is looked at
            // without finding the point in the story that plays it.
            if (first && Option(args, "--movie") is { Length: > 0 } wanted)
            {
                double seconds = movies.Play(wanted);

                Log.Info(seconds > 0
                    ? $"Movie: {wanted}, {seconds:F1}s"
                    : $"Movie: {wanted} could not be played");
            }

            // How impatient a double-click is. The room is new every time round this loop
            // and the setting is not, so it is handed over again here.
            live = update;
            update.HurryFactor = settings.HurryFactor;

            // What lets an animation actually move something. Vertex poses are left unread:
            // gab alone is 50.2 million samples and nothing deforms yet.
            update.Animations = api.Animations;
            update.Clips = clips;
            update.Characters = characters;

            // Where everybody stands, whenever a clip takes them or lets them go. Off
            // unless asked for: it is a line per clip per character, and a cutscene is
            // hundreds of them. See SceneUpdate.TraceActors for what it is for.
            if (args.Contains("--trace-actors", StringComparer.OrdinalIgnoreCase))
            {
                update.TraceActors = Log.Info;
            }

            // What a step sounds like. Three files decide it and none of them was read, so
            // every character in the game walked in silence over carpet, tile and gravel
            // alike — while the clips said, three or four times a stride, that a foot had
            // just gone down.
            update.Steps = footsteps;

            // What makes a texture an animation asks for resident. The scene loaded only
            // what its models were painted with, and 168 animations repaint one part-way
            // through — an alarm clock counting, a monitor changing what it shows.
            //
            // Through the loader rather than out of the archives, and that is the whole of
            // the fix: this used to read <name>.BMP straight from the 1999 barns, so no
            // picture an animation ever brought in could be enhanced — not the workspace's
            // PNG, not the packed BC7, not the player's own override, and no normal,
            // occlusion or height map with it. Larry's monitor is the plain case: an office
            // at 2048 texels a surface, with a 128-texel screen dropped into it the moment
            // he starts typing.
            SceneGeometry paint = geometry;
            SceneLoader late = loader;

            // The bag is thrown away on purpose. What the caller needs to know is whether
            // the picture arrived, which is the return value; the one thing that goes wrong
            // here — an animation naming a texture no archive has — is already reported by
            // whoever asked, as GK3R3345, with the surface and the name in it.
            update.Textures = name =>
                late.LoadTextureLate(paint, name, new Foundation.Diagnostics.DiagnosticBag());

            // What lets a script light the room a second way. The bake is named after the
            // scene asset rather than the geometry, which is the whole trick: several
            // timeblocks and both states of a light switch share one BSP and differ only in
            // their .MUL.
            string standing = scene.Asset?.BspName ?? scene.Name;

            update.Relight = name =>
            {
                Formats.Scenes.SceneAssetFile? asset =
                    archives.ReadText(name + ".SCN") is { } declared
                        ? Formats.Scenes.SceneAssetFile.Parse(declared, name + ".SCN")
                        : null;

                // The asset has to be baked for the geometry that is standing. One call in
                // the corpus is not — CEM's, at 106P, which names a whole different room —
                // and swapping only its bake would lay one room's lighting over another's.
                if (asset?.BspName is { Length: > 0 } named &&
                    !named.Equals(standing, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (archives.Read(name + ".MUL") is not { } baked ||
                    !geometry.SwapLightmaps(Formats.Lightmaps.MulFile.Parse(baked, name + ".MUL")))
                {
                    return false;
                }

                // And the rig with the bake, because they are two halves of one lighting.
                // The bake lights the room and the rig lights everything standing in it, so
                // swapping only the bake leaves the people lit by the scene the room has
                // just left — Gabriel under warm bar lamps on a floor gone blue. RL2's
                // disco asset is fifteen coloured omnis and a key over the ball, and none
                // of them reached anybody until this.
                if (asset is { Lights.Count: > 0 })
                {
                    var extent = new SceneExtent(geometry.Minimum, geometry.Maximum);

                    // With the same substitution the room was loaded under: on a daytime
                    // exterior the artists' key light is replaced by a synthesized sun, and
                    // a rig swapped in without that is a room that loses its sun the moment
                    // a script turns a light on. Every SetScene in the game is indoors, so
                    // this is a rule kept rather than a case seen.
                    IReadOnlyList<Formats.Scenes.AuthoredLight> rig = scene.Sun is { } daylight
                        ? [.. asset.Lights.Where(l => !Game.Sunlight.IsAuthoredSun(
                               l, geometry.Minimum, geometry.Maximum)), daylight]
                        : asset.Lights;

                    // And with the fires, which the swap would otherwise put out: the bar's
                    // fireplace burns under both of RL2's assets and a rig laid in without
                    // this one is the same room with the fire turned to a photograph.
                    rig = Game.FlameLighting.Rig(rig, fires);

                    renderer.SetLights(rig, extent);

                    Log.Info($"Relit: {name}, {rig.Count} lights and its bake");
                }
                else
                {
                    Log.Info($"Relit: {name}, its bake");
                }

                return true;
            };

            // The faces in this room. Everybody the scene placed who has an entry in
            // FACES.TXT and is actually painted with their own face bitmap, which is what
            // tells a person from a portrait of one.
            var moving = new Game.Actors.Faces(faces, archives, api.Animations, geometry);

            // And the moustache, when the player has asked for it: Gabriel's face composed
            // out of the game's own moustached Gabriel, GA3, and painted onto his own head.
            // Before anybody is added, because a face is composed the moment it is taken on.
            if (settings.AlwaysWearsMoustache)
            {
                moving.ComposedFrom[Game.Assists.PlainFace] = Game.Assists.MoustachedFace;
            }

            foreach (Game.PlacedModel person in scene.Models)
            {
                if (person.Kind == Game.PlacedModelKind.Actor)
                {
                    moving.Add(person);
                }
            }

            update.Faces = moving;

            // What an animation's own sound cues reach. Without it the game is silent
            // wherever the noise belongs to the animation rather than to a line of
            // dialogue — Gabriel's yawn on waking up is the first one in the game.
            update.Sound = room is null
                ? null
                : (cue, at) => room.PlayAt(cue.Name, at, cue.Gain);

            // Who is speaking, which is what decides whether a character runs their
            // talking script or their listening one. The line names its own actor, so the
            // faces know without being told.
            update.Speaking = () => moving.Speaking;

            // What a line of dialogue does to the speaker's mouth. Set per room because
            // the faces are the room's, while the audio outlives it.
            if (room is not null)
            {
                room.Speaking = moving.Say;

                // Whether a line comes from where its speaker stands or from the middle.
                room.Routing = new Audio.DialogueRoutingOptions
                {
                    CenterAllDialogue = settings.CenterAllDialogue,
                };

                // Where a sound that follows something has got to. A soundtrack may say
                // Follow=blk_sedan, meaning the emitter travels with that model, and where
                // the model is at any moment is the room's answer rather than the file's.
                room.Where = named => update.Where(named);

                // What PlaySoundTrack names: a .STK in the archives, which the audio layer
                // has no way to open on its own.
                // The extension is the caller's guess rather than the file's. Every script
                // in the corpus writes `PlaySoundTrack("R25Doors.STK")`, and half the
                // animation nodes that ask for one leave it off — `FightDrone`,
                // `LHIHandShakeTell`, `TE5Vamps` — so the name is tried as given and then
                // with .STK on the end. Without the second try, 24 of the 46 soundtracks
                // an animation starts are looked for under a name no archive has.
                room.Soundtracks = named =>
                {
                    // Named by the file that answered rather than by what was asked for,
                    // so that "FightDrone" and "FightDrone.STK" are one soundtrack: they
                    // are both in the corpus, and two names for one list would start it
                    // twice and stop only one of them.
                    string file = Path.HasExtension(named) ? named : named + ".STK";
                    string? text = archives.ReadText(file);

                    if (text is null && !Path.HasExtension(named))
                    {
                        text = archives.ReadText(named);
                        file = named;
                    }

                    return text is null
                        ? null
                        : Formats.Audio.SoundtrackFile.Parse(text, file, new DiagnosticBag());
                };
            }

            // The pose everything opens in, before anything runs. A door that starts open,
            // a character sitting down, a bag on the ground beside somebody: the scene
            // states each of those as an animation and means its first frame.
            if (update.Open() is > 0 and { } posed)
            {
                Log.Info(
                    $"Opening pose: {posed} clip(s) sampled" +
                    (update.Posed.Count > 0
                        ? ", " + string.Join(", ", update.Posed.Select(Described))
                        : string.Empty));
            }

            // What a room does when nobody is asking it to: the lobby's ceiling fans turn
            // because the scene gave them a script of their own. Started after the
            // animation libraries are attached, since that is what the scripts drive.
            update.StartScenery();

            if (scene.Actions is { } actions)
            {
                actions.Verbs = verbs;
            }

            if (update.Scenic > 0 || update.Fidgeting > 0)
            {
                Log.Info(
                    $"Behaviour: {update.Scenic} prop(s) move on their own, " +
                    $"{update.Fidgeting} character(s) idle, talk and listen");
            }

            Log.Info(
                $"Update: {update.Movable} actor(s) can turn their head, " +
                $"{characters.Count} character(s) know how to walk, " +
                $"{moving.Count} face(s) can talk and blink, " +
                $"{verbs.TopicCount} topic(s) can be raised");

            // What the room does when somebody walks into it, which is mostly deciding
            // where they are standing. A scene places its actors wherever its [ACTORS]
            // section says — usually START — and this is what moves the player to the spot
            // matching the door they came through. Without it every arrival is the front
            // door, however the player got in.
            // The arrival, counted now that the room is standing and not before. A scene
            // file asks whether this is the first visit and has to be read against the
            // number of previous ones; the scripts that run next ask the same question and
            // expect this one to be counted. Both are right, and this is the line between
            // them.
            if (request.State is not null && request.Counts)
            {
                request.State.EnterLocation(request.State.Ego, scene.Name);
            }

            // Nobody is standing in a room that is only being looked at. The port builds
            // the room being looked at from its own scene file, which the original never
            // reads — it has no scene to build — and that file puts the player in it. So
            // the player is taken back out of it: they are four miles away with a pair of
            // binoculars, and a second Gabriel in the middle of the shot is the tell.
            if (!request.Counts &&
                api.Leaning is not null &&
                api.Perform("HideModel", [Sheep.SheepValue.FromString(api.State.Ego)]) is not null)
            {
                Log.Info($"Binoculars: {api.State.Ego} is not in {scene.Name}");

                // Nor is their moped: a roadside's scene file parks it there for whoever
                // rides up, and nobody has.
                foreach (Game.PlacedModel parked in scene.Placed ?? [])
                {
                    if (string.Equals(parked.Noun, "GABES_MOPED", StringComparison.OrdinalIgnoreCase) &&
                        api.Perform("HideModel", [Sheep.SheepValue.FromString(parked.Name)]) is not null)
                    {
                        Log.Info($"Binoculars: {parked.Name} is not in {scene.Name} either");
                    }
                }
            }

            // And the room a look is being put down in is put back as it was: the player
            // standing where they raised the binoculars, facing the way they were facing.
            // The camera goes back with them — see WantedCamera, which the same look set on
            // the way in. Without it, lowering a pair of binoculars walks the player to the
            // front of the room and points the view at the door.
            if (api.Resuming is { } put)
            {
                api.Resuming = null;

                if (!update.Place(api.State.Ego, put.Standing, put.Facing))
                {
                    Log.Info($"Binoculars: {api.State.Ego} could not be put back in {scene.Name}");
                }
            }

            // What the binoculars ask of the room they are looking into, out of the game's
            // own BINOCS.SHP: hide its exits so it cannot be walked out of, and show
            // whoever is meant to be standing in it. Run here because it is written against
            // the room being looked at and that room has only just been built — in the
            // original there is nothing to build, and the call is made as the view opens.
            if (!request.Counts &&
                api.Leaning is { Sight.Entering.Length: > 0 } leaning &&
                api.Perform(
                    "CallSheep",
                    [
                        Sheep.SheepValue.FromString("binocs"),
                        Sheep.SheepValue.FromString(leaning.Sight.Entering),
                    ]) is not null)
            {
                Log.Info($"Binoculars: {leaning.Sight.Entering} staged {scene.Name}");
            }

            // Before the room is entered, not after. Everything the room being left was
            // saying belongs to that room; the entering script, on the other hand, may well
            // say something itself, and cutting it off a moment later is how a scripted
            // arrival loses its own first line.
            //
            // What the room sounded like stops here too, all of it: its bed, its
            // soundtracks and whatever they had playing. Nothing of a room is audible in
            // the next one.
            room?.Leave();

            if (request.Counts && scene.Actions?.Find("SCENE", "ENTER") is { } entering)
            {
                new ActionRunner(api).Run(entering);
                Log.Info($"entered: SCENE:ENTER [{entering.Case}]");
            }

            // Back from a room the game never had. After the entering script and not
            // before it, because that script's PlaceEgo is what stands the player at the
            // hotel door for want of a better answer; this is the better answer. A spot is
            // kept while the player is in the room it was kept for, and dropped the moment
            // they arrive anywhere else: whoever went through the shop and out the back of
            // the story did not come back this way.
            if (api.Returning is { } returning && request.Counts)
            {
                bool home = string.Equals(returning.Room, scene.Name, StringComparison.OrdinalIgnoreCase);

                if (home || !string.Equals(returning.Through, scene.Name, StringComparison.OrdinalIgnoreCase))
                {
                    api.Returning = null;
                }

                if (home &&
                    string.Equals(returning.Through, api.State.LastLocation, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info(update.Place(api.State.Ego, returning.Standing, returning.Facing)
                        ? $"Returned: {api.State.Ego} stood where they left {scene.Name} for {returning.Through}"
                        : $"Returned: {api.State.Ego} could not be stood where they left {scene.Name}");

                    // And the view they had, in place of the cut the entering script made
                    // toward the door it chose. The room loop takes WantedCamera as the
                    // opening view; the named angle is cleared so the update does not cut
                    // away from it on the first frame.
                    api.WantedCamera = (returning.Eye, returning.Look);
                    api.State.CameraAngle = string.Empty;
                }
            }

            // What the room sounds like when nothing is happening in it. A soundtrack is a
            // list being walked rather than a sound being held, so what is worth saying is
            // which lists are running and what, if anything, is audible this moment.
            string? bed = room?.StartAmbience(scene.AmbienceRead);

            if (room is { Running.Count: > 0 })
            {
                Log.Info(
                    $"Ambience: {string.Join(", ", room.Running)}" +
                    (bed is { Length: > 0 } ? $", opening with {bed}" : ", opening with a wait") +
                    (room.AmbienceAt is { } at
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $" at {at.Position:F0}, full within {at.Minimum:F0} units and " +
                            $"as quiet as it gets past {at.Maximum:F0}")
                        : string.Empty));
            }

            if (first)
            {
                Opening(args, api, scene);
            }
            else if (Option(args, "--then")?.Split(':') is [string n, string v] &&
                     scene.Actions?.Find(n.Trim(), v.Trim()) is { } follow)
            {
                // The same as --do, in the second room. It exists to measure a second
                // transition — and a return trip is the one that shows what the texture
                // cache is worth — without needing a mouse.
                new ActionRunner(api).Run(follow);

                Log.Info($"Then {n.Trim()}:{v.Trim()} [{follow.Case}]");
            }

            // Arriving somewhere is the moment the story is at rest: the room is built,
            // its opening script has run, and nothing is half-done. Saving here rather
            // than on leaving means the autosave is a place the player can be put back,
            // not a doorway they were passing through.
            //
            // Never on the first room of a run, which is the one the menu just started and
            // is nothing worth keeping, and never over a save the player made.
            if (!first && request.Counts)
            {
                api.Saves?.Write(Game.SaveStore.AutoSlot, api.State.Capture($"Arrived at {scene.Name}"));
            }

            // The console outlives the room. Its history and its scrollback are the
            // player's working notes, and losing them at every door would make it useless
            // for exactly the thing it is for: watching one thing across a transition.
            console.Knows(api.FunctionNames);
            console.Calls = api.Perform;

            // The quest log. Built per room like everything else here, and holding nothing
            // of its own: what is done is read from the score events the story records.
            //
            // Given the language twice over, because the two halves of what it draws come
            // from different places: its objectives and its hints are the port's own prose
            // and are keyed into interface-<code>.json, and the heading over each point in
            // the story is GK3's own Day110a line, already translated in every release. A
            // change of language reloads the room, so this is also where a journal in the
            // new one is built.
            var journal = new Game.Story.Journal(api.State)
            {
                Text = words,
                Names = strings,
            };

            // The room is standing and about to be drawn, so this is where the three
            // things that covered the wait finish: the loading screen goes, the picture
            // finishes going out, and the way back is armed for the room's own loop to run
            // — over a live room rather than over a still of one, so everything in it is
            // moving while the fade lifts.
            //
            // The loading screen first, because it may be holding the fade's own length for
            // it: a load that outlasted the fade finished it early and took the screen over,
            // and Done is what hands the way back over. A load that never showed it costs
            // nothing here at all.
            loading.Done();

            // And the fade only when there was something to go out from, and the loading
            // screen has not already seen it out. The first room of a run is loaded behind
            // the menu or behind nothing at all, and arming a fade there would make the
            // first frame of a headless render black.
            if (fade.Leaving)
            {
                fade.ArriveOver(fade.Black());
            }

            // And the title art comes down, now that there is a room to put in its place.
            // Only ever standing on the way into the first room — after that what the
            // backdrop holds is the photograph the fade took, which the fade takes down
            // itself.
            if (first)
            {
                renderer.SetBackdrop(null);
            }

            if (timeline is not null)
            {
                // The whole of it, and everything after this point is the room running.
                timeline.Stamp("room set up (scripts, audio, journal)");

                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Where {scene.Name}'s {timeline.TotalMilliseconds:F0} ms went:"));

                Log.Info(timeline.Report());
            }

            RoomExit exit = FlyScene(
                fade,
                window, renderer, geometry, scene, cameraName, frameLimit, update,
                interaction,
                room, movies, hud, Cut, api, screens, Icon, CloseUp, VerbIcon, Artwork,
                burning, sidney,
                map, binoculars, api.State, console,
                front, pages, Apply, Relanguaged, args, strings, journal);

            result = exit.Code;

            if (exit.Destination is not { Length: > 0 } next)
            {
                break;
            }

            // Into a room the game never had. Its scripts cannot bring the player back to
            // this door, because the room they will be coming back from is not one of the
            // rooms those scripts ask about, so where they stand now is kept and they are
            // stood there again on the way out. Asked here, while the room they are
            // leaving is still up. See Gk3SheepApi.Returning.
            if (api.Returning is { } spot &&
                string.Equals(spot.Room, scene.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(spot.Through, next, StringComparison.OrdinalIgnoreCase))
            {
                if (archives.IsAdded(next + ".SIF") && !archives.IsAdded(scene.Name + ".SIF"))
                {
                    Log.Info(
                        $"Returning: {api.State.Ego} will be stood back in {scene.Name} on " +
                        $"the way out of {next}");
                }
                else
                {
                    api.Returning = null;
                }
            }

            // Before the room is taken down, because what the fade darkens is a photograph
            // of it and the photograph comes off the swapchain. From here on the picture on
            // screen owes nothing to the geometry, which is what lets the next room be read
            // while this one is still going dark. See ScreenFade.
            fade.Begin();

            // The geometry is about to go. Frames are still in flight reading its buffers,
            // and freeing those underneath the device is a crash somewhere else entirely.
            renderer.SetScene(null, null);
            renderer.Idle();

            // Whether the point in the story is over. The check runs on every change of
            // location and after the new one is current, because the rules ask where the
            // player is — 110A's first line is "must be at RC1". If it moves the clock, it
            // also decides where the player ends up, so the room asked for is read back
            // rather than being the one the door named.
            // Unless the room being built is one the binoculars are showing, or the one
            // they are being lowered in. Neither is somewhere the player went, and moving
            // the story to the room being looked at is exactly the fault that put Gabriel
            // at L'Homme Mort with his moped four miles away.
            bool looking = Looking(api);

            if (!looking)
            {
                api.State.Location = next.ToUpperInvariant();
            }

            Timeblock was = api.State.Timeblock;

            if (!looking && Complete(api) is { Length: > 0 } instead)
            {
                next = instead;

                // And the room stops being audible here, rather than when the next one is
                // built. Ordinarily those are a moment apart and the difference does not
                // matter; at the end of a point in the story there is a film and a
                // timeblock card in between, and 212PEND is thirty-nine seconds of the
                // courtyard's fountain playing under a film set somewhere else. Reported
                // from the Château de Serres. Everything else the room was sounding like
                // goes with it — CSE's afternoon runs a room tone and the fountain, and
                // both are beds.
                if (room is { Running.Count: > 0 } sounding)
                {
                    Log.Info($"Room tone: {string.Join(", ", sounding.Running)} stopped with the timeblock");
                }

                room?.Leave();

                // The screen belongs to the film and the card from here, and both of them
                // are the picture rather than something drawn over it — a fade left
                // standing at black would draw black over the card. So the room finishes
                // going out now, and the card takes over from the black it leaves.
                fade.Black();
                fade.Clear();

                // The film the timeblock goes out on, where it has one. Four of the
                // sixteen do — the ones that end on something the player is meant to have
                // seen rather than on simply having finished the errands.
                if (movies.Play(was + "end") is > 0 and { } showing)
                {
                    Log.Info(
                        string.Create(CultureInfo.InvariantCulture, $"Closing film: {was}end, {showing:F1}s"));

                    // And watched to the end, or until the player stops it. **Starting it
                    // and carrying on was the bug.** Nothing advanced a frame of the film
                    // here, so the card below drew over a film that had not begun; the next
                    // room then loaded with it still playing, and from the first frame of
                    // that room the film was drawn over a scene that was running behind it.
                    //
                    // 212P is the case it was reported from. The Château de Serres block
                    // ends on 212PEND, and the room it hands over to is Gabriel's at two in
                    // the afternoon, whose SCENE:ENTER is Grace letting herself in. She did
                    // it behind the picture, and by the time the film was over the scene it
                    // opens on had played itself out.
                    if (frameLimit > 0)
                    {
                        // A run photographing something is not watching a film, and
                        // 212PEND is thirty-nine seconds long. Stopped rather than left
                        // playing, because leaving it is the fault above. The opening films
                        // are passed over for the same reason.
                        movies.Stop();
                        Log.Info($"Closing film: {was}end passed over");
                    }
                    else
                    {
                        double waited = 0;
                        bool pressed = false;

                        bool cut = Watch(
                            window,
                            renderer,
                            movies,
                            pages,
                            Stopwatch.StartNew(),
                            ref waited,
                            ref pressed,
                            SayForFilm,
                            front.Settings.MovieSubtitles);

                        if (cut)
                        {
                            Log.Info($"Closing film: {was}end skipped");
                        }
                    }
                }

                // And then say so. Before the next room is built, which is where the
                // original puts it and the only place it can go: after it the player is
                // standing somewhere new with no idea that two hours have passed.
                Announce(
                    window,
                    renderer,
                    pages,
                    strings,
                    api.State.Timeblock,
                    Art(
                        archives,
                        Pictures(
                    settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    language: language),
                        settings.EnhancedTextures
                            ? CompressedTextures.Open(
                                packsOnly
                                    ? string.Empty
                                    : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty),
                                packs,
                                overrides,
                                localized)
                            : overrides is null && localized is null
                                ? null
                                : CompressedTextures.Open(string.Empty, null, overrides, localized),
                        diagnostics,
                        $"TBT{api.State.Timeblock}.BMP"),

                    // Always out of the archives, whatever the paintings are being read
                    // from. The lettering is recovered by subtracting the painting it was
                    // blended into, and the picture it was blended into is the original —
                    // an upscale of it is a different set of pixels and would leave the
                    // letters full of the difference between the two.
                    Game.TimeblockCard.Read(archives, api.State.Timeblock.ToString()),
                    audio,
                    sounds);

                // And the card goes out into the next room the same way a room does, which
                // also gives the load that follows something to draw frames of.
                fade.Begin();
            }

            // Which of the three a room is: somewhere the player has gone, somewhere they
            // are looking at, or somewhere they are looking up from again. The last of the
            // three is not cleared here — the room it describes has not been built yet, and
            // putting the player back into it is the first thing done when it has.
            request = api.Resuming is not null
                ? SceneRequest.Resuming(api, next)
                : api.Leaning is null
                    ? SceneRequest.Continuing(api, next)
                    : SceneRequest.Peeking(api, next);

            // The next room has its own idea of where to stand; the camera the player named
            // belonged to the one they have left.
            cameraName = null;
            first = false;
        }

        audio?.Dispose();
        localized?.Dispose();

        // What a temporal filter would read. Reported rather than drawn: a motion vector is
        // not visible in the picture and is wrong in ways that look plausible, so the only
        // way to know it is right is to read the numbers. A still camera should give zero
        // everywhere, and a pan should move very nearly the whole frame.
        if (args.Contains("--motion", StringComparer.OrdinalIgnoreCase) &&
            renderer.CaptureMotion() is { } motion)
        {
            int pixels = motion.Length / 2;
            var mask = new byte[pixels];
            double total = 0;
            double most = 0;
            int moving = 0;

            for (int i = 0; i < motion.Length; i += 2)
            {
                double length = Math.Sqrt(
                    (motion[i] * motion[i]) + (motion[i + 1] * motion[i + 1]));

                total += length;
                most = Math.Max(most, length);
                mask[i / 2] = (byte)Math.Clamp(length * 24, 0, 255);

                if (length > 0.5)
                {
                    moving++;
                }
            }

            // Eight bits a pixel, the viewport's size, so that a run that reports something
            // odd can be looked at rather than only counted.
            File.WriteAllBytes("motion.raw", mask);

            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Motion: mean {total / pixels:F2} px, largest {most:F1} px, " +
                $"{100.0 * moving / pixels:F1}% of the frame moved more than half a pixel"));
        }

        if (screenshotPath is not null && renderer.Capture() is { } capture)
        {
            File.WriteAllBytes(screenshotPath, Formats.Bitmaps.PngWriter.Encode(capture));
            Log.Info($"Wrote {screenshotPath}");
        }

        return result;
    }

    /// <summary>
    /// The fonts the interface will draw with, best first.
    /// </summary>
    private static readonly string[] CaptionFonts =
    [
        "F_CAPTION_D_26", "F_CAPTION_D_20", "F_CAPTION_D_16", "F_CAPTION_DEFAULT",
        "F_ARIAL_T12", "F_ARIAL_T10", "F_ARIAL_T8",
    ];

    /// <summary>
    /// Lets the scripts play a movie.
    /// </summary>
    /// <param name="api">The host.</param>
    /// <param name="movies">What plays them.</param>
    private static void Showing(Gk3SheepApi api, Game.MoviePlayer movies)
    {
        double Start(IReadOnlyList<SheepValue> arguments)
        {
            if (arguments.Count == 0)
            {
                return 0;
            }

            string name = arguments[0].AsString();
            double seconds = movies.Play(name);

            Log.Info(seconds > 0
                ? $"Movie: {name}, {seconds:F1}s"
                : movies.Skipping
                    ? $"Movie: {name} skipped"
                    : $"Movie: {name} could not be played");

            return seconds;
        }

        foreach (string called in (string[])["PlayMovie", "PlayFullScreenMovie", "PlayFullScreenMovieX"])
        {
            api.Register(called, a => SheepValue.FromInt((int)Start(a)), waitable: true);
        }

        // Asked before the call is performed, which is why it opens the movie to find out
        // and the performing call then finds it already playing.
        api.MovieSeconds = name => movies.Playing && string.Equals(
            movies.Showing, name, StringComparison.OrdinalIgnoreCase)
                ? movies.Seconds - movies.At
                : movies.Play(name);
    }

    /// <summary>A sibling of the enhanced textures directory.</summary>
    /// <param name="enhanced">Where the enhanced colour textures are.</param>
    /// <param name="what">The sibling's name.</param>
    /// <returns>Its path.</returns>
    private static string Beside(string enhanced, string what) =>
        Path.Combine(
            Path.GetDirectoryName(enhanced.TrimEnd(Path.DirectorySeparatorChar, '/')) ??
                enhanced,
            what);

    /// <summary>
    /// Finds the typeface the interface draws with.
    /// </summary>
    /// <param name="named">A file named on the command line, or null.</param>
    /// <param name="enhancedDirectory">The content workspace's enhanced set, if any.</param>
    /// <param name="diagnostics">Where a font that will not read is reported.</param>
    /// <returns>The font, or null to fall back to GK3's own sheets.</returns>
    private static Formats.Fonts.TrueTypeFile? InterfaceFont(
        string? named, string? enhancedDirectory, DiagnosticBag diagnostics)
    {
        foreach (string path in Typefaces(named, enhancedDirectory))
        {
            try
            {
                if (Formats.Fonts.TrueTypeFile.Parse(
                        File.ReadAllBytes(path), Path.GetFileName(path), diagnostics) is { } read)
                {
                    return read;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"WARNING GK3R1201: {path} could not be read. ({error.Message})");
            }
        }

        // The one inside the assembly, which needs no path and cannot be missing.
        using Stream? carried = typeof(Application).Assembly.GetManifestResourceStream(
            "GK3Reborn.Assets.Fonts.NotoSerif-Regular.ttf");

        if (carried is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        carried.CopyTo(copy);

        return Formats.Fonts.TrueTypeFile.Parse(copy.ToArray(), "NotoSerif-Regular.ttf", diagnostics);
    }

    /// <summary>Where to look for a typeface, best first.</summary>
    private static IEnumerable<string> Typefaces(string? named, string? enhancedDirectory)
    {
        if (named is { Length: > 0 } && File.Exists(named))
        {
            yield return named;
        }

        if (enhancedDirectory is not { Length: > 0 })
        {
            yield break;
        }

        string beside = Beside(enhancedDirectory, "fonts");

        if (!Directory.Exists(beside))
        {
            yield break;
        }

        string[] found;

        try
        {
            found =
            [
                .. Directory.EnumerateFiles(beside)
                    .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in found)
        {
            yield return file;
        }
    }

    /// <summary>How many screen pixels one pixel of the chosen sheet should cover.</summary>
    /// <param name="font">The rung that was picked.</param>
    /// <param name="wanted">The height that was asked for.</param>
    /// <returns>A whole number, at least one.</returns>
    private static int Magnification(Formats.Ui.FontFile font, int wanted) =>
        font.Height <= 0 ? 1 : Math.Clamp((int)MathF.Round((float)wanted / font.Height), 1, 4);

    /// <summary>
    /// Asks whether this point in the story is over, and moves the clock on if it is.
    /// </summary>
    /// <param name="api">The game.</param>
    /// <returns>The room to open instead, or null to open the one that was asked for.</returns>
    private static string? Complete(Gk3SheepApi api)
    {
        if (Game.Story.TimeblockRules.Check(api.State) is not { } completion)
        {
            return null;
        }

        Timeblock was = api.State.Timeblock;

        // Through the same door SetTime and SetLocationTime went through, so a timeblock
        // the rules end and one a script ends are the same event downstream.
        api.State.ChangeTimeblock(completion.Next, completion.Location);

        if (!api.State.ChangingTimeblock)
        {
            return null;
        }

        api.State.StartedTimeblock();

        Log.Info($"Timeblock: {was} is over, starting {api.State.Timeblock}");

        return api.State.Location;
    }

    /// <summary>Loads every compiled script in the game.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="host">Where they go.</param>
    /// <param name="catalogue">
    /// Receives every function prototype the scripts name, for whatever wants to say how a
    /// call should be written. Optional: the loading works the same without one.
    /// </param>
    /// <returns>How many were loaded.</returns>
    private static int LoadScripts(
        GameArchives archives, ScriptHost host, Sheep.SheepSignatures? catalogue = null)
    {
        int loaded = 0;

        foreach (string name in archives.Names(".SHP"))
        {
            if (archives.Read(name) is not { } bytes)
            {
                continue;
            }

            try
            {
                Sheep.SheepScriptFile script = Sheep.SheepScriptFile.Parse(bytes, name);
                host.Add(script);
                loaded++;

                // Every compiled script carries the prototypes of everything it calls, so
                // reading the 224 of them is also how the console learns that
                // GetNounVerbCount takes two strings and answers an int. There is nowhere
                // else that is written down: the game shipped no header.
                foreach (Sheep.SheepImport import in script.Imports)
                {
                    catalogue?.Add(import, name);
                }
            }
            catch (Formats.FormatParseException)
            {
                // A script that will not parse is one call that does nothing, not a game
                // that will not start.
            }
        }

        return loaded;
    }

    /// <summary>Says what could not be loaded.</summary>
    /// <param name="diagnostics">What was raised while loading.</param>
    /// <param name="verbose">Whether to list them rather than count them.</param>
    private static void Report(DiagnosticBag diagnostics, bool verbose)
    {
        Diagnostic[] problems =
            [.. diagnostics.Items.Where(d => d.Severity >= DiagnosticSeverity.Warning)];

        if (problems.Length == 0)
        {
            return;
        }

        Log.Info(verbose
            ? $"{problems.Length} assets could not be loaded:"
            : $"({problems.Length} assets could not be loaded; --verbose lists them)");

        if (verbose)
        {
            foreach (Diagnostic problem in problems)
            {
                Log.Info($"  {problem}");
            }
        }
    }

    /// <summary>
    /// The switches that set something going before the player takes over.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room they act on.</param>
    private static void Opening(string[] args, Gk3SheepApi api, LoadedScene scene)
    {
        // Before any of them, because it says what has already happened and an action's
        // case is a question about exactly that. --do BARTENDER:EGG finds no rule at all
        // until --did EGG has set the flag the rule is written against. Run a second time
        // here: the first is before the room is built, for the conditions its own file asks.
        Already(args, api);

        // Several, separated by semicolons, because one action is often the setup for the
        // one worth looking at: inspecting a thing and then walking away from it needs both
        // to have happened before the picture is taken.
        foreach (string asked in Option(args, "--do")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            Do(asked, api, scene);
        }

        Opened(args, api, scene);
    }

    /// <summary>
    /// Writes into the story whatever <c>--did</c> says has already happened.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="api">The host.</param>
    private static void Already(string[] args, Gk3SheepApi api)
    {
        if (Option(args, "--did") is not { Length: > 0 } already)
        {
            return;
        }

        foreach (string done in already.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // A scene's own [ACTORS] blocks are conditional, and the conditions ask about
            // game variables and about where somebody is — not about flags. CEM at 306P
            // puts Emilio and Mesmi in the graveyard only for
            // IsActorAtLocation("Emilio","CEM") && GetGameVariableInt("EmilioPath") == 10,
            // so without these two forms the whole of that scene is unreachable headlessly:
            // --run sets them after the room has already decided who is in it.
            if (done.Trim().Split('=') is [string variable, string number] &&
                int.TryParse(number.Trim(), CultureInfo.InvariantCulture, out int value))
            {
                api.State.SetVariable(variable.Trim(), value);
                continue;
            }

            if (done.Trim().Split('@') is [string who, string where])
            {
                api.State.SetActorLocation(who.Trim(), where.Trim());
                continue;
            }

            switch (done.Trim().Split(':'))
            {
                case [string noun, string verb, string count]
                    when int.TryParse(count, CultureInfo.InvariantCulture, out int times):
                    api.State.SetNounVerbCount(noun, verb, times);
                    api.State.SetTopicCount(noun, verb, times);
                    break;

                case [string noun, string verb]:
                    api.State.SetNounVerbCount(noun, verb, 1);
                    api.State.SetTopicCount(noun, verb, 1);
                    break;

                case [string flag]:
                    api.State.SetFlag(flag);
                    break;

                default:
                    break;
            }
        }

        Log.Info($"Did: {already}");
    }

    /// <summary>Performs one <c>--do</c>.</summary>
    /// <param name="asked">The action, as <c>noun:verb</c>.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room it acts on.</param>
    private static void Do(string asked, Gk3SheepApi api, LoadedScene scene)
    {
        // As the player, not as Gabriel: half the rules in the game are written twice, once
        // for each of them, and asking with the wrong one silently performs the other's.
        if (asked.Split(':') is [string noun, string verb] &&
            scene.Actions?.Find(noun.Trim(), verb.Trim(), api.State.Ego) is { } rule)
        {
            ActionOutcome outcome = new ActionRunner(api).Run(rule);

            string did = outcome.Deferred
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"walking {outcome.Approaching:F1}s first, then " +
                    $"{outcome.Statements.Count} statement(s)")
                : $"{(outcome.Ran ? "ran" : "refused")} {outcome.Statements.Count} statement(s)";

            Log.Info($"Doing {noun.Trim()}:{verb.Trim()} [{rule.Case}]: {did}");
        }
    }

    /// <summary>The rest of the switches that set something going before the player takes over.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room they act on.</param>
    private static void Opened(string[] args, Gk3SheepApi api, LoadedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (Option(args, "--play") is { Length: > 0 } clip)
        {
            Sheep.SheepExpression.Evaluate($"StartAnimation(\"{clip}\")", api);
            Log.Info($"Playing {clip}");
        }

        // Things in the bag, for looking at what carrying them changes. Half of what the
        // action files offer is written about an item the player is holding, so a room
        // photographed with empty pockets is a room with half its menu missing.
        if (Option(args, "--carry") is { Length: > 0 } carrying)
        {
            foreach (string item in carrying.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                api.State.Inventory.Add(api.State.Ego, item.Trim());
            }

            Log.Info(
                $"Carrying: {string.Join(", ", api.State.Inventory.ItemsOf(api.State.Ego))}");
        }

        // Open a screen on the way in, for looking at one on purpose. The story opens all
        // of them itself; this is how a screenshot of one gets taken. A colon names what
        // the screen is about, which the ones about a single thing need — an item close-up
        // with no item is a frame of chrome.
        if (Option(args, "--screen") is { Length: > 0 } wanted &&
            wanted.Split(':') is [string named, ..] &&
            Enum.TryParse(named, ignoreCase: true, out ScreenKind kind))
        {
            // Everything after the kind, colons included: a subject may carry one of its
            // own, as ride:TR1 does.
            string? about = wanted.Split(':', 2) is [_, string subject] ? subject : null;

            if (about is { Length: > 0 })
            {
                // Carried, because a screen about something the player does not have is a
                // screen the action files answer differently about.
                api.State.Inventory.Add(api.State.Ego, about);
                api.State.Inventory.SetActive(api.State.Ego, about);
            }

            api.State.Screens.Show(new Screen(kind, about));
            Log.Info($"Screen: {kind}{(about is null ? string.Empty : $" ({about})")}");
        }

        // And put something into Sidney on the way in, for the same reason: its screens are
        // about files, and a screenshot of one with nothing in it shows nothing.
        if (Option(args, "--scan") is { Length: > 0 } scanning && api.Sidney is { } machine)
        {
            foreach (string item in scanning.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // The same path a click takes, so a still photographed from the command line
                // is of the state the game would actually be in.
                ScanIntoSidney(item.Trim(), api, scene, machine, console: null);
            }

            if (machine.Files.Count > 0)
            {
                machine.OpenFile(machine.Files[0]);
            }
        }

        // A colon opens something on the page as well, which is the only way a screen that
        // is about one thing — a message, a suspect's file — can be photographed with
        // anything on it.
        if (Option(args, "--sidney") is { Length: > 0 } page && api.Sidney is { } opened &&
            page.Split(':') is [string pageName, ..] &&
            Enum.TryParse(pageName, ignoreCase: true, out Game.Sidney.SidneyScreen which))
        {
            opened.Screen = which;

            if (page.Split(':') is [_, string about, ..] && about.Length > 0)
            {
                switch (which)
                {
                    case Game.Sidney.SidneyScreen.EMail:
                        opened.ReadMail(
                            opened.Mail().FirstOrDefault(
                                m => m.Id.Equals(about, StringComparison.OrdinalIgnoreCase)));

                        break;

                    case Game.Sidney.SidneyScreen.Suspects when int.TryParse(about, out int index):
                        opened.OpenSuspect(
                            opened.Suspects().FirstOrDefault(s => s.Index == index));

                        break;

                    case Game.Sidney.SidneyScreen.Search:
                        opened.Typed = about;
                        opened.Look();

                        break;

                    case Game.Sidney.SidneyScreen.Translate:
                        opened.OpenForTranslation(
                            opened.Files.FirstOrDefault(
                                f => f.Item.Equals(about, StringComparison.OrdinalIgnoreCase)));

                        break;

                    default:
                        opened.OpenFile(
                            opened.Files.FirstOrDefault(
                                f => f.Item.Equals(about, StringComparison.OrdinalIgnoreCase)));

                        break;
                }

                Log.Info($"Sidney: {which} ({about})");
            }
            else
            {
                Log.Info($"Sidney: {which}");
            }
        }

        // Operations run on Sidney's files before the first frame, as the analyze screen's
        // buttons would: ITEM:ACTION, so that a chain several files long — read a
        // parchment's geometry, then lay the shape it saved over the map — can be
        // photographed. The file is opened first, because every operation is about one.
        if (Option(args, "--analyse") is { Length: > 0 } chain && api.Sidney is { } analysing)
        {
            foreach (string step in chain.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = step.Split(':');
                string item = parts[0];

                analysing.OpenFile(
                    analysing.Files.FirstOrDefault(
                        f => f.Item.Equals(item, StringComparison.OrdinalIgnoreCase)));

                // An item on its own opens it and does nothing, which is how a chain ends
                // on the file that should be showing.
                if (parts is not [_, string operation] || operation.Length == 0)
                {
                    Log.Info($"Opened {item}");

                    continue;
                }

                if (!Enum.TryParse(operation, ignoreCase: true, out Game.Sidney.SidneyAction what))
                {
                    Log.Error($"--analyse: {operation} is not one of Sidney's operations.");

                    continue;
                }

                Log.Info($"{item} {what}: {analysing.Perform(what).Text}");
            }
        }

        // Files linked to whoever is open on the suspects screen, which is the only way a
        // still can be taken of a suspect whose vehicle has been worked out: linking is a
        // click on a file list that only exists once the screen is open. Runs after
        // --sidney, so the suspect it links to is the one that switch opened.
        if (Option(args, "--link") is { Length: > 0 } linking && api.Sidney is { } linker)
        {
            foreach (string item in linking.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (linker.Files.FirstOrDefault(
                        f => f.Item.Equals(item.Trim(), StringComparison.OrdinalIgnoreCase))
                    is not { } file)
                {
                    Log.Error($"--link: {item} is not a file Sidney holds; scan it first.");

                    continue;
                }

                Log.Info($"Linked {item}: {linker.LinkToSuspect(file).Text}");
            }
        }

        // Places marked on Sidney's map, for photographing the one screen whose whole
        // content the player puts there themselves. In the map's own 1,368 pixels, which is
        // what the marks are kept in and what a click is turned into.
        if (Option(args, "--mark") is { Length: > 0 } marks && api.Sidney is { } marking)
        {
            foreach (string place in marks.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (place.Split(',') is [string across, string down] &&
                    float.TryParse(across, CultureInfo.InvariantCulture, out float mx) &&
                    float.TryParse(down, CultureInfo.InvariantCulture, out float my))
                {
                    Log.Info($"Marked {mx}, {my}: {marking.Mark(new Vector2(mx, my)).Text}");
                }
            }
        }

        // And the figure laid over the country, which is the last of the map's states that a
        // still cannot otherwise be taken of: laying one is a click on a list that only
        // exists while the machine is asking.
        if (Option(args, "--shape") is { Length: > 0 } figures && api.Sidney is { } laying)
        {
            foreach (string figure in figures.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse(figure.Trim(), ignoreCase: true, out Game.Sidney.MapShape laid))
                {
                    Log.Info($"Shape {laid}: {laying.LayShape(laid).Text}");
                }
            }
        }

        // The ruling, for photographing the chessboard the map puzzle ends on. A negative
        // count rules the figure rather than the whole picture, which is the game's own
        // "fill shape" against its "fill entire screen".
        if (Option(args, "--grid") is { Length: > 0 } ruling && api.Sidney is { } ruled &&
            int.TryParse(ruling, CultureInfo.InvariantCulture, out int cells))
        {
            ruled.RuleInShape = cells < 0;

            Log.Info($"Grid {cells}: {ruled.Rule(Math.Abs(cells)).Text}");
        }

        // <b>The map puzzle is a sequence, so photographing it needs one.</b> Each switch is
        // read once, which cannot express "mark two places, lay a line, mark four more, lay
        // a circle" — and that ordering is the whole of what the puzzle is. Steps are
        // separated by semicolons and run in the order written.
        if (Option(args, "--map") is { Length: > 0 } steps && api.Sidney is { } plotting)
        {
            foreach (string step in steps.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] said = step.Trim().Split(' ', 2);
                string what = said[0].ToUpperInvariant();
                string with = said.Length > 1 ? said[1].Trim() : string.Empty;

                switch (what)
                {
                    case "MARK" when with.Split(',') is [string across, string down] &&
                        float.TryParse(across, CultureInfo.InvariantCulture, out float mx) &&
                        float.TryParse(down, CultureInfo.InvariantCulture, out float my):
                        Log.Info($"  mark {with}: {plotting.Mark(new Vector2(mx, my)).Text}");
                        break;

                    case "SHAPE" when Enum.TryParse(
                        with, ignoreCase: true, out Game.Sidney.MapShape figure):
                        Log.Info($"  shape {figure}: {plotting.LayShape(figure).Text}");
                        break;

                    case "GRID" when int.TryParse(
                        with, CultureInfo.InvariantCulture, out int squares):
                        plotting.RuleInShape = squares < 0;
                        Log.Info($"  grid {squares}: {plotting.Rule(Math.Abs(squares)).Text}");
                        break;

                    case "ASSIST":
                        plotting.Assist();
                        Log.Info($"  assist: {plotting.Finish(yes: true).Text.Split((char)10)[0]}");
                        break;

                    case "DO" when Enum.TryParse(
                        with, ignoreCase: true, out Game.Sidney.SidneyAction did):
                        Log.Info($"  do {did}: {plotting.Perform(did).Text}");
                        break;

                    default:
                        Log.Error($"--map: {step} is not a step.");
                        break;
                }
            }
        }

        // How far into the map the view is, for photographing it magnified.
        if (Option(args, "--zoom") is { Length: > 0 } closer && api.Sidney is { } looking &&
            float.TryParse(closer, CultureInfo.InvariantCulture, out float times))
        {
            looking.ZoomOn(looking.Focus, MathF.Log(MathF.Max(times, 1f)) / MathF.Log(1.2f));

            Log.Info($"Zoom {looking.Zoom:F2} on {looking.Focus.X:F0}, {looking.Focus.Y:F0}");
        }

        if (Option(args, "--glide") is { Length: > 0 } destination)
        {
            Sheep.SheepExpression.Evaluate(
                $"GlideToCameraAngle(\"{destination}\")", api);
            Log.Info($"Gliding to {destination}");
        }

        if (Option(args, "--glance")?.Split(':') is [string who, string at])
        {
            Sheep.SheepExpression.Evaluate(
                $"LookitActor(\"{who.Trim()}\", \"{at.Trim()}\", \"\", 0)",
                api);

            foreach (Diagnostic diagnostic in api.Diagnostics.Items)
            {
                Log.Info($"  {diagnostic}");
            }
        }
    }

    /// <summary>
    /// Whether the player is spelling something into Sidney rather than playing the game.
    /// </summary>
    /// <param name="story">The game, for what is on top of the screen stack.</param>
    /// <param name="sidney">Grace's computer, or null in a run that has none.</param>
    /// <returns>True while one of its two text boxes has the keyboard.</returns>
    private static bool Spelling(GameState story, Game.Sidney.SidneyMachine? sidney) =>
        sidney is { } machine &&
        story.Screens.Top?.Kind == ScreenKind.Sidney &&
        (machine.Screen == Game.Sidney.SidneyScreen.Search || machine.Appending);

    /// <summary>Hands a frame's keyboard to the console.</summary>
    /// <param name="input">Where the keys come from.</param>
    /// <param name="console">What reads them.</param>
    private static void Typing(Platform.SilkGameWindow input, GameConsole console)
    {
        if (input.WasPressed(Platform.EditKey.Escape))
        {
            console.Show(false);
            return;
        }

        if (input.Typed is { Length: > 0 } typed)
        {
            console.Type(typed);
        }

        if (input.WasPressed(Platform.EditKey.Backspace))
        {
            console.Backspace();
        }

        if (input.WasPressed(Platform.EditKey.Tab))
        {
            console.TakeCompletion();
        }

        if (input.WasPressed(Platform.EditKey.Up))
        {
            console.Move(-1);
        }

        if (input.WasPressed(Platform.EditKey.Down))
        {
            console.Move(1);
        }

        if (input.WasPressed(Platform.EditKey.Enter))
        {
            console.Submit();
        }
    }

    /// <summary>Runs the present loop with a camera the player can move.</summary>
    /// <param name="window">The window and its input.</param>
    /// <param name="renderer">The renderer.</param>
    /// <param name="geometry">The scene's geometry.</param>
    /// <param name="scene">The scene.</param>
    /// <param name="cameraName">Which camera to open on, if any.</param>
    /// <param name="frameLimit">Stop after this many frames, or zero for no limit.</param>
    /// <param name="update">The world going on by itself.</param>
    /// <param name="interaction">Turns pointing at the room into doing something to it.</param>
    /// <param name="room">What the room sounds like, if there is a device.</param>
    /// <param name="movies">What plays a cutscene when a script asks for one.</param>
    /// <param name="hud">The interface, if there is a font to draw it with.</param>
    /// <param name="cut">
    /// Cuts a sheet of letters for the window's current size, so one that changes size is
    /// drawn at the new one rather than at the old one stretched.
    /// </param>
    /// <param name="api">
    /// The script API, for the save store and for the room a load asks the game to move to.
    /// </param>
    /// <param name="screens">What draws the screens in front of the room, if anything can.</param>
    /// <param name="icons">The picture belonging to an inventory item, where it has one.</param>
    /// <param name="closeUps">
    /// The bigger picture the artists painted of an item, for the screen that shows one
    /// thing rather than a list of them.
    /// </param>
    /// <param name="verbIcons">
    /// The picture belonging to a verb, resting or picked out, where it has one.
    /// </param>
    /// <param name="artwork">The game's own art by file name, for the GPS.</param>
    /// <param name="rig">
    /// The room's lights as it was laid with them, so that a mechanism which adds lights of
    /// its own can have the whole rig laid again without rebuilding the room's half.
    /// </param>
    /// <param name="sidney">Grace's computer, which one of those screens is.</param>
    /// <param name="map">The driving map's art and roads.</param>
    /// <param name="binoculars">What can be seen from here, if anything.</param>
    /// <param name="story">Where the story stands, for the inventory strip.</param>
    /// <param name="console">The developer console, which outlives the room.</param>
    /// <param name="front">The menu, which Escape opens.</param>
    /// <param name="pages">What draws it, or null when there is no font to draw with.</param>
    /// <param name="apply">What to do with a setting the moment it changes.</param>
    /// <param name="relanguaged">
    /// Asks whether the language was changed since it was last asked, and forgets that it
    /// was. The room is loaded again when it says yes: everything read through the pack has
    /// already been swapped, and what is left is the pictures with words painted into them,
    /// which are uploaded when their room loads.
    /// </param>
    /// <param name="options">The command line, for the debugging switches.</param>
    /// <param name="strings">
    /// What the game calls places and times, for the corner of the screen.
    /// </param>
    /// <param name="journal">The quest log, which the journal screen draws and the hint button asks.</param>
    /// <param name="fade">
    /// The transition into this room, which the loop lifts one frame at a time so that the
    /// room is live underneath it rather than a still.
    /// </param>
    /// <returns>Why the room was left, and where for.</returns>
    private static RoomExit FlyScene(
        Rendering.ScreenFade fade,
        Platform.SilkGameWindow window,
        Rendering.IRenderer renderer,
        SceneGeometry geometry,
        LoadedScene scene,
        string? cameraName,
        int frameLimit,
        SceneUpdate update,
        SceneInteraction interaction,
        SceneAudio? room,
        Game.MoviePlayer movies,
        GameHud? hud,
        Func<bool, OverlayAtlas?> cut,
        Gk3SheepApi api,
        ScreenPainter? screens,
        Func<string, ItemIcon> icons,
        Func<string, ItemIcon> closeUps,
        Func<string, bool, ItemIcon> verbIcons,
        Func<string, ItemIcon> artwork,
        IReadOnlyList<Formats.Scenes.AuthoredLight> rig,
        Game.Sidney.SidneyMachine? sidney,
        DrivingMap map,
        Binoculars binoculars,
        GameState story,
        GameConsole console,
        FrontEnd front,
        MenuPage? pages,
        Action<Settings> apply,
        Func<bool> relanguaged,
        string[] options,
        GameStrings strings,
        Game.Story.Journal journal)
    {
        ArgumentNullException.ThrowIfNull(fade);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(cut);

        // The hose, while one is being aimed. Out here rather than in the screen stack
        // because the jet and the clock move every frame and the stack is game state; and
        // out here rather than in the frame because it has to survive one.
        Game.WaterAiming? aiming = null;

        // Who is out on the roads while the map is open, and how far along their roads they
        // have got. Beside the stack for the hose's reason: everyone on the map moves every
        // frame and the stack is part of the state hash.
        Game.DrivingTraffic? traffic = null;

        // Which screen the traffic was built for, so that opening the map, closing it and
        // opening it again starts everybody where they set out rather than where they were
        // when the player last looked.
        string? trafficFor = null;

        // Clicks the room has swallowed in a row for being busy, with nobody speaking.
        // Three is the player saying the game is stuck; see where it is counted.
        int refused = 0;

        // Whether the headset's list of topics is up, and which of them is picked out. Out
        // here for the reason the verb menu's index is: a list that is rebuilt from scratch
        // every frame still has to remember that it is open.
        bool radioOpen = false;
        int radioIndex = 0;

        // How many topics were said to be on offer last time it changed. Reported rather
        // than silent: the list is the whole feature, and a headset drawn dim because the
        // story has moved on looks exactly like one drawn dim because nothing was found.
        int radioSaid = -1;
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(closeUps);
        ArgumentNullException.ThrowIfNull(verbIcons);
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(rig);

        string here = scene.Name;

        // Putting the binoculars down from a zoomed view. Everything about it happens while
        // the room being looked at is still standing, because the game's own exit script is
        // written about that room's models; then the vantage point is asked for, and the
        // room loop puts it back without anybody having arrived anywhere.
        RoomExit Lower(Game.BinocularView looking)
        {
            if (looking.Sight.Leaving is { Length: > 0 } after &&
                api.Perform(
                    "CallSheep",
                    [
                        Sheep.SheepValue.FromString("binocs"),
                        Sheep.SheepValue.FromString(after),
                    ]) is not null)
            {
                Log.Info($"Binoculars: {after} put {scene.Name} back");
            }

            api.Leaning = null;
            api.Resuming = looking;
            api.WantedCamera = (looking.Eye, looking.Look);

            // Still through the eyepieces: leaning back out is not putting them down.
            story.Screens.Replace(new Screen(ScreenKind.Binoculars));
            update.Cancel();

            Log.Info($"Binoculars raised again at {looking.From}");

            return new RoomExit(0, looking.From);
        }

        // What rises off the room's fires. Found again here rather than handed in — the
        // parameter list above is long enough — and it is a walk over models the scene has
        // already parsed. Empty in the seventy-two rooms with no fire in them.
        IReadOnlyList<Game.Flame> burning =
            Game.Flames.In(scene.Models, api.Animations, scene.Bitmaps);

        var smoke = new Game.FlameParticles(
            burning,
            volumes: !options.Contains("--no-shader-fire", StringComparer.OrdinalIgnoreCase));

        smoke.Follow(scene.Models);

        // And what those fires are burning over. One thing in the game qualifies — the
        // stone at the bottom of TE4's bowl of fire — and it is given a glint because an
        // opaque flame card leaves it invisible from anywhere but straight overhead. See
        // Game.Flames.Holding, which says why this is a divergence and what it costs.
        smoke.Holds(Game.Flames.Holding(burning, geometry.SceneObjectBoxes()));

        if (smoke.Glints > 0)
        {
            Log.Info($"Fire: {smoke.Glints} thing(s) lying in a fire, glinting");
        }

        // And what is in the sky over it. Fifteen rooms in the game have any and the rest
        // get an empty flock that costs nothing; which rooms, and why it is a list rather
        // than something derived, is in Game.SceneBirds.
        //
        // A switch for the usual reason: the picture that shows it working is the same
        // square with and without it.
        bool noBirds = options.Contains("--no-birds", StringComparer.OrdinalIgnoreCase);

        Game.Flock overhead = Game.SceneBirds.For(here, story.Timeblock);

        var birds = new Game.BirdFlock(
            overhead, Game.SceneBirds.Over(overhead, scene.Geometry, scene.Walkable, scene.Cameras));

        if (birds.Count > 0)
        {
            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Birds: {birds.Count} over {here}, wheeling at " +
                $"({birds.Wheel.Centre.X:F0}, {birds.Wheel.Centre.Y:F0}, " +
                $"{birds.Wheel.Centre.Z:F0}) within {birds.Wheel.Radius:F0} of it, " +
                $"{overhead.Wingspan:F0} across at " +
                $"{Game.BirdFlock.FlapsPerSecond(overhead.Wingspan):F1} beats a second"));
        }

        // And what drifts through it: insects under its trees and dust in its air. Which
        // rooms have either is decided from the room — where its foliage cards are, and
        // whether it has a sun over it — rather than from a list. See Game.SceneDrift.
        bool noInsects = options.Contains("--no-insects", StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<Game.Crown> crowns = Game.SceneDrift.Crowns(scene.Geometry, scene.Models);

        var drifting = new Game.DriftField(
            Game.SceneDrift.For(story.Timeblock, crowns.Count, sunlit: scene.Sun is not null),
            crowns,
            Game.SceneDrift.Seed(here));

        if (drifting.Any)
        {
            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Drift: {crowns.Count} crown(s) over {here}, up to {drifting.InsectCount} " +
                $"insect(s), {drifting.MoteCount} specks of dust, wind bearing " +
                $"{MathF.Atan2(drifting.Wind.X, drifting.Wind.Z) * 180f / MathF.PI:F0}"));
        }

        // The dust hangs in the daylight at the windows, where there is any, and is lit
        // by it. Only while the shafts themselves are drawn: a speck shining in a shaft
        // nobody can see is a speck shining for no reason.
        bool noSunRays = options.Contains("--no-sun-rays", StringComparer.OrdinalIgnoreCase);

        // Whether the binoculars have been raised to the player's eyes this time round.
        bool throughEyes = false;

        // The last caption logged, so each line is said once.
        string? captioned = null;

        // Whether anything was handed to the blended pass last frame. Only so that a room
        // which stops having any — the lasers being switched off — is told once, rather
        // than every frame of the two hundred rooms that never have any at all.
        bool blending = false;

        // The room's smoke, embers, beams and birds, moved on and handed over sorted for
        // the eye they are about to be seen by. Before SetScene so the two describe one
        // instant. Shared with the binoculars, which are looked *through*: a frame under
        // that screen still has a sky with birds in it.
        void BlendAir(Camera view, float delta)
        {
            // And whatever the room's own machinery wants blended: CS2's laser beams are
            // drawn as light scattering in the air, which is the one thing in this renderer
            // that has to be see-through and so has to come through here. How much of the
            // picture is being paid for goes the other way at the same time — the beams are
            // drawn as light only where there is a lighting model to make that read.
            if (api.Mechanism is { } machine)
            {
                machine.Tracing = renderer.Quality;

                // And its own lights, where it has any that move. A self-lit surface is
                // drawn bright and lights nothing, so a laser beam that is to lay red
                // across the floor under it has to be in the rig — see
                // SceneMechanism.Lights. Laid only when it says so: laying a rig rebuilds
                // the scene's light grid, which is a per-room cost.
                if (machine.LightsMoved)
                {
                    renderer.SetLights(
                        [.. rig, .. machine.Lights],
                        new SceneExtent(geometry.Minimum, geometry.Maximum));
                }
            }

            IReadOnlyList<Rendering.Particle> blended =
                api.Mechanism?.Particles(view.Position) ?? [];

            if (smoke.Emitters > 0)
            {
                smoke.Advance(delta, view.Position);

                IReadOnlyList<Rendering.Particle> puffs = smoke.Facing(view.Position);

                // Both, where a room has both. Neither list is long and the pass takes one.
                blended = blended.Count == 0 ? puffs : [.. puffs, .. blended];
            }

            // And the birds, ahead of all of it. They are the furthest thing the pass
            // draws by a long way — the sky is over the room and everything else here is
            // in it — and the list is drawn in the order it arrives, so they go first.
            //
            // Advanced whatever the switch says and drawn only when it is on, so that
            // turning the birds off and on again does not teleport the flock: it is the
            // same room a moment later, not a new one.
            if (birds.Count > 0)
            {
                birds.Advance(delta);

                if (front.Settings.Birds && !noBirds)
                {
                    IReadOnlyList<Rendering.Particle> flying = birds.Facing(view);

                    blended = blended.Count == 0 ? flying : [.. flying, .. blended];
                }
            }

            // And the insects and the dust, after the rest. They hide a little of what is
            // behind them and so want sorting against the smoke, and get away without it:
            // there is no fire in any room that has them. Advanced whatever the switch
            // says, for the same reason the birds are.
            if (drifting.Any)
            {
                drifting.LitBy(front.Settings.SunRays && !noSunRays ? scene.Shafts : []);
                drifting.Advance(delta, view);

                if (front.Settings.InsectsAndDust && !noInsects)
                {
                    IReadOnlyList<Rendering.Particle> adrift = drifting.Facing(view);

                    if (adrift.Count > 0)
                    {
                        blended = blended.Count == 0 ? adrift : [.. blended, .. adrift];
                    }
                }
            }

            if (blended.Count > 0 || blending)
            {
                renderer.SetParticles(blended);
                blending = blended.Count > 0;
            }
        }

        int cameraIndex = Math.Max(
            0,
            scene.Cameras.ToList().FindIndex(c => string.Equals(
                c.Name, cameraName ?? scene.CameraNamed(null)?.Name, StringComparison.OrdinalIgnoreCase)));

        Camera template = SceneLoader.CameraFor(scene, geometry, cameraName);

        var camera = new FreeCamera
        {
            Speed = MathF.Max(50f, (geometry.Maximum - geometry.Minimum).Length() * 0.15f),
        };

        // Whoever asked for the shell to be turned off is looking at the room rather than
        // playing it, and the story is not allowed to take the camera off them either. It
        // is the same escape hatch GameCamera makes for Tools::Active.
        //
        // <b>Asked every frame, not decided here.</b> It is a row on the Playing page as
        // well as a switch on the command line, and a setting the player can only see work
        // by walking through a door is a setting they will take to be broken.
        bool onTheCommandLine =
            options.Contains("--free-camera", StringComparer.OrdinalIgnoreCase);

        bool Flying() => onTheCommandLine || front.Settings.FreeCamera;

        // The shell the scene's artists drew around the space the camera may occupy. Without
        // it the player can walk the view out through a wall and look at the room from
        // behind, which is a picture no part of the game was built to survive. The free
        // camera gives that back, because looking at the geometry from outside is exactly
        // how some of it gets checked.
        if (scene.CameraShell is not { IsEmpty: false } shell)
        {
            Log.Info("Camera bounds: none, so the camera may go anywhere");
        }
        else
        {
            // A script may turn the shell off for a shot that has to be outside it, and
            // the original only turns it off until the next room — so this asks the story
            // every frame rather than being decided once here. The player's own switch is
            // asked in the same breath and for the same reason.
            camera.Confine = (from, movement) =>
                story.CameraBoundaries && !Flying()
                    ? shell.Resolve(from, movement)
                    : from + movement;

            if (Flying())
            {
                Log.Info("Camera bounds: off, so the camera may leave the room");
            }

            // A viewpoint outside its own shell is not fatal — the way back in is always
            // open — but it is worth saying, because from out there the walls behave
            // backwards and there is nothing on screen to explain why.
            else if (!shell.Contains(template.Position))
            {
                Log.Info($"Camera bounds: {scene.Name}'s view starts outside them");
            }
        }

        camera.CopyFrom(template);

        // A room reached by leaning in through the binoculars starts at the camera the
        // binoculars named rather than at the room's own, and a room come back to from one
        // the game never had starts at the view the player left it with. Taken once,
        // because it describes an arrival rather than a place.
        if (api.WantedCamera is { } leaned)
        {
            api.WantedCamera = null;

            camera.Position = leaned.Position;
            camera.Aim = leaned.Angle;

            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Arrived at a view of the room's own choosing: {leaned.Position:F0} looking {leaned.Angle.X:F0}"));
        }

        // --eye and --aim put the camera where no authored camera stands. Held rather than
        // set: a scene's entry script may direct the view, and a shot asked for on the
        // command line has to outlast that or it photographs somewhere else.
        Vector3? standing = Standing(options);
        Vector2? looking = Aimed(options);

        void Place()
        {
            if (standing is { } eye)
            {
                camera.Position = eye;
            }

            if (looking is { } look)
            {
                camera.Aim = look;
            }
        }

        if (standing is not null || looking is not null)
        {
            Place();

            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Camera placed at {camera.Position:F0} looking {camera.Aim.X:F1}, {camera.Aim.Y:F1}"));
        }

        Log.Info();
        update.StartAt(template);

        Camera? directing = update.View;

        var stopwatch = Stopwatch.StartNew();
        double previous = 0;

        // Whether a movie was on screen last frame, so the renderer is told to stop drawing
        // one exactly once rather than every frame for the rest of the room.
        bool showingMovie = false;
        int saidAboutMovies = 0;
        int presented = 0;
        string? hovering = null;
        string? spoken = null;
        int said = 0;
        Hover? menu = null;
        Vector2 menuAt = Vector2.Zero;
        int menuIndex = 0;

        // The lobby's glass whose print is waiting on an answer; see Game.DirtyGlasses.
        GlassQuestion? glassAsked = null;

        // Whether Sidney was up last frame, so that putting it away can run what the
        // original runs then.
        bool sidneyWasUp = false;
        Vector2? pinned = Pinned(options);
        bool forceMenu = options.Contains("--menu", StringComparer.OrdinalIgnoreCase);

        // --console opens it and types into it, which is the only way to photograph it: a
        // headless run has no keyboard, and an interface nobody can render is an interface
        // whose layout nobody can check.
        if (Option(options, "--console") is { } typed)
        {
            console.Show(true);
            console.Type(typed);
        }

        // --run types a command and presses Enter, which is how a headless run drives the
        // game: a walk, a flag, a script function, anything the console can call. The
        // console itself is closed again so the frames that follow are of the room.
        //
        // A command may start with @N — "@600 DumpActor(\"EMILIO\")" — to run on frame N
        // rather than before the first one, which is how a headless run asks a question
        // after something has had time to happen.
        var deferred = new List<(int Frame, string Command)>();

        // --click 90;150@1232,35 presses the primary button on those frames. A click may
        // carry a point of its own, which moves the pointer there for that frame and every
        // frame after it; without one it lands wherever --pointer put the pointer.
        //
        // The interface a click lands on is drawn from the game's own state every frame, so
        // this is the only way a run with no mouse can reach any of it — and a sequence is
        // what most of the interface needs: the way into the binoculars, the sight to lean
        // in on, and the way back out are three clicks in three different places.
        var clicks = new List<(int Frame, Vector2? At)>();

        foreach (string press in Option(options, "--click")?.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            string[] parts = press.Trim().Split('@');

            if (int.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out int when))
            {
                clicks.Add((
                    when,
                    parts.Length > 1 && parts[1].Split(',') is [string cx, string cy] &&
                    float.TryParse(cx, CultureInfo.InvariantCulture, out float px) &&
                    float.TryParse(cy, CultureInfo.InvariantCulture, out float py)
                        ? new Vector2(px, py)
                        : null));
            }
        }

        if (Option(options, "--run") is { } command)
        {
            // Several calls, separated by semicolons, run in order in the same frame — a
            // teleport and then the question that depends on it. The console itself takes
            // one call at a time; this is the harness feeding it a script's worth.
            foreach (string one in command.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string call = one.Trim();

                if (call.StartsWith('@') &&
                    call.IndexOf(' ') is > 1 and var split &&
                    int.TryParse(call[1..split], out int at))
                {
                    deferred.Add((at, call[(split + 1)..].Trim()));
                }
                else
                {
                    deferred.Add((0, call));
                }
            }

            deferred.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            Run(0);
        }

        void Run(int frame)
        {
            // Before the commands, because a command is typed into the console and a click
            // is not: a frame that does both should read as the player having clicked while
            // the console was shut.
            if (clicks.FindIndex(c => c.Frame == frame) is int press and >= 0)
            {
                if (clicks[press].At is { } moved)
                {
                    pinned = moved;
                }

                clicks.RemoveAt(press);
                window.Press(Platform.PointerButton.Primary);

                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"click at frame {frame}, at {pinned?.X ?? -1:F0},{pinned?.Y ?? -1:F0}"));
            }

            while (deferred.Count > 0 && deferred[0].Frame <= frame)
            {
                console.Show(true);
                console.Type(deferred[0].Command);
                deferred.RemoveAt(0);

                int before = console.Lines.Count;
                console.Submit();

                // Mirrored to the terminal, because a headless run has no way to read the
                // console's own scrollback and an answer nobody can read is no answer.
                foreach (ConsoleLine line in console.Lines.Skip(before))
                {
                    Log.Info($"run: {line.Text}");
                }

                console.Show(false);
            }
        }

        if (pinned is { } spot)
        {
            Log.Info($"Pointer pinned at {spot.X}, {spot.Y}");
        }

        // What the interface was laid out for. A window that goes fullscreen doubles its
        // height, and a bitmap font cannot follow it by scaling — the sheet has one size —
        // so the ladder has to be re-picked and the atlas rebuilt.
        //
        // The text size is the second half of the same question, and is watched the same
        // way: dragging that row in the pause menu changes what the letters should be cut
        // at without the window having moved at all.
        int laidOutFor = window.FramebufferHeight;

        // Whether the pointer was down last frame, so that picking a marked place up can
        // happen on the edge of the press. See the drag below.
        bool heldLastFrame = false;
        var panFrom = Vector2.Zero;
        float laidOutAt = front.Settings.TextScale;

        bool flicker = options.Contains("--flicker", StringComparer.OrdinalIgnoreCase);
        byte[]? previousFrame = null;
        double flickerTotal = 0;
        int flickerFrames = 0;

        // Nothing has been clicked on in this room yet.
        //
        // A room is left in the middle of a frame — the click that opened the door returns
        // out of the loop below before the frame it belongs to has ended — so the click is
        // still on the books when the next room's first frame reads them. It was then acted
        // on a second time, in a room it was never aimed at, at whatever the pointer
        // happened to be over there: click the stairs down in the hallway and Gabriel is
        // standing on that spot in the lobby, so the player arrives to a voice-over of
        // Gabriel looking at himself.
        //
        // Forgotten on the way in rather than at each way out, because there are several
        // ways out — a door, a load, the menu, the end of a film — and every one of them
        // spends the input that took it.
        window.Forget();

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();
            Run(presented);

            double now = stopwatch.Elapsed.TotalSeconds;
            float delta = (float)Math.Min(0.1, now - previous);
            previous = now;

            // A window that goes fullscreen doubles in height. An outline is re-cut at
            // the new size; a bitmap sheet can only step up the ladder and be magnified.
            if (hud is not null &&
                (window.FramebufferHeight != laidOutFor ||
                 front.Settings.TextScale != laidOutAt))
            {
                laidOutFor = window.FramebufferHeight;
                laidOutAt = front.Settings.TextScale;

                if (cut(false) is { } grown)
                {
                    int magnify = grown.Scalable || grown.Font is null
                        ? 1
                        : Magnification(grown.Font, UI.TextSizing.Sheet(laidOutFor, laidOutAt));

                    if (!grown.Scalable &&
                        grown.Name.Equals(hud.Overlay.Atlas.Name, StringComparison.Ordinal))
                    {
                        // The sheet is right and only the magnification wrong, which costs
                        // a field rather than a rebuild.
                        hud.Overlay.Magnify = magnify;

                        if (screens is not null)
                        {
                            screens.Overlay.Magnify = magnify;
                        }
                    }
                    else
                    {
                        hud.Retarget(grown);
                        hud.Overlay.Magnify = magnify;

                        // The screens in front of the room are cut from the same sheet as
                        // the captions and were being left on the old one: the inventory
                        // and Sidney stayed the size the window started at, whatever it
                        // had since become. Invisible while this only followed the window
                        // — nobody resizes mid-game — and obvious the moment there is a
                        // row that changes it with the game standing still.
                        if (screens is not null)
                        {
                            screens.Retarget(grown);
                            screens.Overlay.Magnify = magnify;
                        }

                        Log.Info(
                            $"Interface: {grown.Name} at {grown.Height}px" +
                            (magnify > 1 ? $" x{magnify}" : string.Empty) +
                            $" for {laidOutFor} lines");
                    }
                }

                if (pages is not null && cut(true) is { } wider)
                {
                    pages.Retarget(wider);
                }
            }

            // The console first, and while it is open it has the keyboard: every key
            // below means something else to it. Escape closes it rather than leaving the
            // room, Tab completes rather than cutting to the next camera, and typing
            // SetFlag does not walk the camera across the room — W, A, S and D are all in
            // the word and every one of them is a movement key.
            //
            // Taken before the toggle, not after: Escape closes the console, and asking
            // afterwards would find it closed and take the same press as "leave the room".
            //
            // Sidney's two text boxes are the other place the keyboard is spoken for, and
            // for the same reason: the player is spelling a word, and every letter in it is
            // also a binding. Typing a subject into the search box opened the inventory on
            // the I, the quest log on the J, reset the camera on the R and drove it about
            // the room on the W, A, S and D — none of which is what somebody typing
            // "MEROVINGIAN" meant. Only while a box is actually showing: elsewhere in
            // Sidney there is nothing to type into and the bindings are the player's.
            bool typing = console.Open || Spelling(story, sidney);

            if (window.WasPressed(Platform.EditKey.Console))
            {
                console.Show(!console.Open);
            }
            else if (typing)
            {
                Typing(window, console);
            }

            if (movies.Playing)
            {
                // A movie has the screen and the keyboard. Escape ends it rather than the
                // room, which is what every game does and what a player will try first.
                if (window.WasPressed(Platform.CameraAction.Quit))
                {
                    movies.Stop();
                }
                else
                {
                    movies.Advance(delta);
                }

                renderer.SetMovieFrame(movies.Frame);
                showingMovie = true;

                for (; saidAboutMovies < movies.Diagnostics.Items.Count; saidAboutMovies++)
                {
                    Log.Report(movies.Diagnostics.Items[saidAboutMovies]);
                }
            }
            else if (showingMovie)
            {
                // Once, on the frame after it ended, rather than every frame afterwards.
                renderer.SetMovieFrame(null);
                showingMovie = false;
            }

            // A screen first. Escape means "out of whatever is in front of me", and with the
            // inventory open the thing in front of the player is the inventory — opening the
            // pause menu over it, which is what happened, answers a question nobody asked.
            if (!typing &&
                !movies.Playing &&
                story.Screens.Top is not null &&
                window.WasPressed(Platform.CameraAction.Quit))
            {
                // Leaning into another room through the binoculars is the one screen a
                // pop cannot close: the room under it is the looked-at one, with no ego in
                // it and the story saying the player is somewhere else. Popping left the
                // player stranded there. The vantage has to be asked for, the same way the
                // back button does it.
                if (api.Leaning is { } backing)
                {
                    return Lower(backing);
                }

                story.Screens.Back();
            }
            else if (!typing && !movies.Playing && window.WasPressed(Platform.CameraAction.Quit))
            {
                if (pages is null)
                {
                    // No font, so there is no menu to open and Escape means what it used to.
                    break;
                }

                // The same press is still on the frame's books as an editing key, and the
                // menu reads that one to close itself. Cleared here, or the menu opens and
                // shuts within the frame.
                window.EndFrame();

                front.InGame = true;

                // The room as the player last saw it, taken before the menu is drawn over
                // it. Captured here and not at the moment of saving: by then the pause menu
                // is on the screen, and a save slot showing a picture of the save menu is
                // worse than showing nothing at all.
                Formats.Bitmaps.DecodedImage? seen = renderer.Capture();

                // From the top, every time. The menu remembers which page it was on so that
                // going back from Picture lands on Settings — which is right inside one
                // visit and wrong between two. Pressing escape and finding yourself three
                // pages deep in a slot list from ten minutes ago is nobody's idea of a
                // pause menu.
                front.Show(FrontEndPage.Main);

                // What the slots hold, before the page can draw them. Read here rather
                // than kept, because a save written by another copy of the game running
                // beside this one is still a save this menu should show.
                front.Saves = api.Saves?.List() ?? [];
                front.Illustrations = slot => Illustration(renderer, api.Saves, slot);

                FrontEndOutcome chose = ShowMenu(
                    window, renderer, pages, front, apply, MenuBehind.Room, () => cut(true));

                if (chose is FrontEndOutcome.Save && front.Slot is { Length: > 0 } into)
                {
                    // Named for where the player is, when they have not named it
                    // themselves. "Hotel Lobby, Day 1 10am" is a better answer than "Slot 3"
                    // and costs the player nothing to get.
                    string called = front.Naming is { Length: > 0 } given
                        ? given
                        : strings.Where(scene.Name, story.Timeblock.ToString());

                    bool wrote = api.Saves?.Write(into, story.Capture(called)) ?? false;

                    // And a picture of the room, from the last frame drawn before the menu
                    // went up. Written after the save rather than with it: a save whose
                    // picture failed is still a save, and one without a picture is a row of
                    // words, which is what every save written before this was.
                    if (wrote && seen is { } photograph)
                    {
                        api.Saves?.Illustrate(into, Thumbnail(photograph));

                        // The renderer is holding the old picture for this slot under the
                        // same name. Dropped, so the menu reloads it rather than showing
                        // what used to be there.
                        renderer.DropOverlayPicture("save:" + into);
                    }

                    Log.Info(wrote
                        ? $"Saved to {into}: {called}"
                        : $"Could not save to {into}.");

                    front.Naming = string.Empty;
                    renderer.SetOverlay(null);
                    continue;
                }

                if (chose is FrontEndOutcome.Load && front.Slot is { Length: > 0 } from &&
                    api.Saves?.Read(from, out Game.SaveFault fault) is { } recovered)
                {
                    api.RestoreGame(recovered);

                    Log.Info($"Restored {from}: {recovered.Title}");

                    // The room the save was written in, which is very likely not this one.
                    //
                    // Returned rather than broken out of, and that is the whole of the bug
                    // this replaces. `break` leaves the frame loop, and the only thing after
                    // the frame loop is `return new RoomExit(0, null)` — which the room loop
                    // reads as "the player quit" and shuts the game down. Restoring a save
                    // from the pause menu closed the game instead of loading the save, which
                    // from the other side of the screen is indistinguishable from a crash.
                    // Quick-load never had the fault because it sets Wanted and falls through
                    // to the handler below, which does exactly this.
                    api.Wanted = null;
                    renderer.SetOverlay(null);

                    // A save that names no room leaves the player where they are with the
                    // story restored around them, which is odd but survivable. Returning
                    // an empty destination would not be: the room loop cannot tell it from
                    // quitting, which is the fault this whole branch is about.
                    if (story.Location is not { Length: > 0 } saved)
                    {
                        continue;
                    }

                    update.Cancel();

                    return new RoomExit(0, saved);
                }

                // The room has been standing still behind the menu and the clock has not.
                // Without this the first frame back advances everything by however long the
                // player spent in the settings.
                previous = stopwatch.Elapsed.TotalSeconds;

                // Whatever the story was holding, it is not holding it any more. Said out
                // loud and in full, because a player who reached for this has already spent
                // a while wondering whether the game was broken and deserves to be told
                // what was wrong with it.
                if (chose is FrontEndOutcome.Unstick)
                {
                    IReadOnlyList<string> let = update.Unstick();

                    if (let.Count == 0)
                    {
                        Log.Info("Unstuck: nothing was holding the room.");
                    }
                    else
                    {
                        Log.Info("Unstuck: let go of " + string.Join(", ", let) + ".");
                    }
                }

                if (chose is FrontEndOutcome.Quit)
                {
                    break;
                }

                // The language moved while the menu was up. Everything read through the
                // pack has already been swapped; what is left is the pictures, and a
                // texture is uploaded when its room loads — so the room loads again. The
                // same door a restored save goes through, which keeps the story, the
                // inventory and where the player is standing.
                if (relanguaged())
                {
                    if (story.Location is { Length: > 0 } again)
                    {
                        Log.Info($"Language: loading {again} again for it.");

                        update.Cancel();
                        renderer.SetOverlay(null);

                        return new RoomExit(0, again);
                    }
                }

                // Whatever the interface last drew belonged to the menu.
                renderer.SetOverlay(null);
                continue;
            }

            if (!typing &&
                window.WasPressed(Platform.CameraAction.NextCamera) && scene.Cameras.Count > 0)
            {
                cameraIndex = (cameraIndex + 1) % scene.Cameras.Count;
                template = SceneLoader.CameraFor(scene, geometry, scene.Cameras[cameraIndex].Name);
                camera.CopyFrom(template);

                Log.Info($"camera: {scene.Cameras[cameraIndex].Name}");
            }

            if (!typing && window.WasPressed(Platform.CameraAction.Reset))
            {
                camera.CopyFrom(template);
            }

            // Pockets, from a key rather than from a small target at the edge of the
            // screen. Not while driving: the player is somewhere else entirely.
            if (!typing &&
                window.WasPressed(Platform.CameraAction.Inventory) &&
                story.Screens.InventoryReachable)
            {
                if (story.Screens.IsOnTop(ScreenKind.Inventory))
                {
                    story.Screens.Back();
                }
                else
                {
                    story.Screens.Show(new Screen(ScreenKind.Inventory));
                }
            }

            // The quest log. Reachable wherever the inventory is, and for the same reason:
            // a player who has lost the thread needs it most in the room where they lost it.
            if (!typing &&
                window.WasPressed(Platform.CameraAction.Journal) &&
                story.Screens.InventoryReachable)
            {
                if (story.Screens.IsOnTop(ScreenKind.Journal))
                {
                    story.Screens.Back();
                }
                else
                {
                    story.Screens.Show(new Screen(ScreenKind.Journal));
                }
            }

            if (!typing && window.WasPressed(Platform.CameraAction.QuickSave))
            {
                bool wrote = api.Saves?.Write(
                    Game.SaveStore.QuickSlot, story.Capture("Quick save")) ?? false;

                Log.Info(wrote ? "Saved." : "Could not save.");
                console.Print(wrote ? "Saved." : "Could not save.");
            }

            if (!typing && window.WasPressed(Platform.CameraAction.QuickLoad))
            {
                Game.SaveGame? loaded =
                    api.Saves?.Read(Game.SaveStore.QuickSlot, out Game.SaveFault fault) is { } read &&
                    fault == Game.SaveFault.None
                        ? read
                        : null;

                if (loaded is null)
                {
                    Log.Info("No quick save to load.");
                    console.Print("No quick save to load.");
                }
                else
                {
                    api.RestoreGame(loaded);
                    api.Wanted = loaded.Location;

                    Log.Info($"Loaded: {loaded.Summary}");
                }
            }

            // A load names the room the save was taken in, and it may be this one — in
            // which case the ordinary "the story moved us" test below would not fire and
            // the room would keep the props and people of the game just thrown away.
            if (api.Wanted is { Length: > 0 } restored)
            {
                api.Wanted = null;
                update.Cancel();

                return new RoomExit(0, restored);
            }

            if (!typing &&
                window.WasPressed(Platform.CameraAction.CycleRayTracing) &&
                renderer.SupportsRayTracing)
            {
                RayTracingQuality[] levels = Enum.GetValues<RayTracingQuality>();

                renderer.Quality = levels[(Array.IndexOf(levels, renderer.Quality) + 1) % levels.Length];
                Log.Info($"ray tracing: {renderer.Quality}");
            }

            // What the world could not do, said once. Animation naming is the sort of thing
            // that fails by nothing happening, which is indistinguishable from nothing
            // having been asked for.
            for (; said < update.Diagnostics.Items.Count; said++)
            {
                Log.Info($"  {update.Diagnostics.Items[said]}");
            }

            foreach (string happened in update.Advance(delta))
            {
                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{stopwatch.Elapsed.TotalSeconds:F2}s] {happened}"));
            }

            // Every line as it starts, so a headless run can show that somebody spoke.
            if (room?.Caption is { Length: > 0 } line && !ReferenceEquals(line, captioned))
            {
                captioned = line;
                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{stopwatch.Elapsed.TotalSeconds:F2}s] {room.Speaker ?? "?"}: {line}"));
            }

            // The story moving the camera takes it back off the player, for as long as it
            // is moving. Letting them keep flying through a scripted glide would fight
            // them for the mouse, and letting the glide win afterwards would take the view
            // away from somebody who had gone to look at something.
            if (!ReferenceEquals(update.View, directing) && update.View is { } directed)
            {
                directing = update.View;
                template = directed;
                camera.CopyFrom(directed);

                Place();
            }

            // And while it is telling one, the camera is the story's rather than the
            // player's: see SceneUpdate.Directing, which is the whole rule. The free
            // camera is the exception, the same one GameCamera makes for Tools::Active.
            //
            // Leaving it out was reported as the view jumping. Nothing stopped a player
            // flying off during a cutscene, and the next thing the script cut to snapped
            // the view back across the room from wherever they had got to — which reads as
            // the camera losing its place rather than as the player having moved it.
            // Whoever has the keyboard, not just the console: W, A, S and D are movement
            // keys and they are also four letters, so a subject typed into Sidney's search
            // box flew the camera off across the room behind it and left it there.
            if (!typing && !(update.Directing && !Flying()))
            {
                camera.Update(window, delta);
            }

            Camera view = camera.ToCamera(template);

            // What GK3's billboard flag has always meant, done here because here is where
            // the frame's camera is finally known — the free camera and the story's own
            // both end up in `view`, and a billboard turned to either of them separately
            // would be facing the wrong way in half the screenshots this port is checked
            // with. See ISceneSink.FaceCamera.
            geometry.TurnBillboards(view.Position);

            // Where the player's ears are. Without this every sound plays at the origin
            // facing nowhere, so the fountain across the square is as loud as the one you
            // are standing in.
            room?.Listen(
                view.Position,
                Vector3.Normalize(view.Target - view.Position),
                view.Up);

            // What the pointer is over. Asked every frame and free of consequences by
            // design — the resolver evaluates conditions to answer, so anything that wrote
            // to the story here would advance the game by moving the mouse across it.
            // The pointer is in window pixels and the viewport is in framebuffer pixels,
            // which are not the same on a scaled display. Picking in the wrong one puts the
            // ray somewhere the player is not looking, and only on some machines.
            Vector2 aimed = pinned ?? new Vector2(
                window.PointerPosition.X * window.DpiScale,
                window.PointerPosition.Y * window.DpiScale);

            Hover hover = interaction.At(
                view,
                (int)aimed.X,
                (int)aimed.Y,
                window.FramebufferWidth,
                window.FramebufferHeight);

            // And the room's own machinery is told, where the room has any. One puzzle
            // needs it: the chessboard decides whether the tile under the pointer is a
            // legal knight's move before it is clicked, because that answer is what the
            // action file's case reads to choose which of three scripts the click runs.
            api.Mechanism?.Pointing(hover.Pick, update.Occupied || menu is not null);

            // And whether it wants the click outright, which is a different question from
            // TakesClick below: that one is asked once a click has failed to resolve an
            // action, and a thing the scene gives a noun to never fails. TE3's blade is
            // PENDULUM and the action files give PENDULUM a LOOK, so without this the only
            // way out of that room resolved to Gabriel remarking on it. Null means "not
            // mine"; a word means "mine, and this is what it does".
            string? claimed = api.Mechanism?.ClaimsClick(hover.Pick);

            // What the player sees, not the noun behind it: the numbered exits are drawn
            // as the place they lead to, and a log that says EXIT3 cannot be matched
            // against a screenshot that says "Outside Church".
            if (hover.Label != hovering)
            {
                hovering = hover.Label;

                if (hovering is { Length: > 0 })
                {
                    Log.Info(hover.Actionable
                        ? $"> {hovering} — click to {hover.Default}"
                        : $"> {hovering} — nothing to do with it here");
                }
            }

            // --pointer puts it somewhere fixed, which is the only way to photograph the
            // interface: the label follows the mouse, and a headless run has never moved it.
            Vector2 pointer = aimed;

            // Whether the verb bar was up when this frame began, and whether anything was
            // taken off it. The two together are how a conversation ends; see below.
            bool barWasShowing = menu is not null;
            bool barTookAVerb = false;

            // --menu opens it without a right-click, for the same reason --pointer exists.
            if (forceMenu && menu is null && hover.Actionable)
            {
                menu = hover;
                menuAt = pointer;
                menuIndex = 0;
            }

            // The poem keeps its page only while it is open; closed, it opens next time at
            // the verse in hand, as the retail engine has it.
            if (story.Screens.Top is not { Kind: ScreenKind.InventoryInspect } reading ||
                !Game.SerpentRouge.IsReader(reading.Subject))
            {
                Game.SerpentRouge.Close(story);
            }

            // What Sidney has to do outside its own screen: a line Grace says over it, a room
            // the story leaves for, Sidney put away — one at a time, each once the last is
            // over, which is the order the retail engine's callbacks give them.
            if (sidney is { HasCues: true } && !update.Acting && sidney.TakeCue() is { } cue)
            {
                switch (cue.Kind)
                {
                    case Game.Sidney.SerpentRougeCue.Say:
                        new ActionRunner(api).Run(new Formats.Actions.NvcAction
                        {
                            Noun = "SIDNEY",
                            Verb = "SAYS",
                            Case = "ALL",
                            Script = string.Create(
                                CultureInfo.InvariantCulture,
                                $"wait StartDialogue(\"{cue.Plate}\", {cue.Lines})"),
                            Source = "Sidney",
                        });

                        Log.Info($"Sidney: {cue.Plate}");
                        break;

                    case Game.Sidney.SerpentRougeCue.Leave:
                        // The end of the second afternoon: Gemini done, and Grace goes out
                        // to the hallway, where the rules end the timeblock.
                        story.Screens.Hide(ScreenKind.Sidney);
                        story.Location = cue.Plate;

                        Log.Info($"Sidney: leaving for {cue.Plate}");
                        break;

                    case Game.Sidney.SerpentRougeCue.Close:
                        story.Screens.Hide(ScreenKind.Sidney);

                        Log.Info("Sidney: put away");
                        break;

                    default:
                        break;
                }
            }

            // Putting Sidney away runs the room's own ExitSidney, as the original does
            // whenever Sidney is closed: it stands Grace up from the desk, and it is where
            // two of the timeblocks she works through at the computer are ended.
            bool sidneyUp = story.Screens.IsOpen(ScreenKind.Sidney);

            if (sidneyWasUp && !sidneyUp && !update.Acting &&
                string.Equals(here, "R25", StringComparison.OrdinalIgnoreCase))
            {
                new ActionRunner(api).Run(new Formats.Actions.NvcAction
                {
                    Noun = "SIDNEY",
                    Verb = "EXIT",
                    Case = "ALL",
                    Script = "wait CallSheep(\"R25_ALL\", \"ExitSidney\")",
                    Source = "Sidney",
                });

                Log.Info("Sidney: ExitSidney");
            }

            sidneyWasUp = sidneyUp;

            // Whose glass was that? The retail engine asks with a topic bar it opens
            // itself once Gabriel has wondered aloud, and reads the answer off which topic
            // was picked. Here the bar is the ordinary verb menu, opened on the question's
            // noun once the line is over; the answer is the topic count that moved, because
            // choosing a row performs the lobby's own rule and that is what a performed
            // topic leaves behind. A menu put away unanswered leaves the glass undusted.
            if (glassAsked is { } pending)
            {
                if (!pending.Opened)
                {
                    if (menu is null && !update.Acting)
                    {
                        menu = interaction.Ask(pending.Question, interaction.NameOf(pending.Glass));
                        menuAt = pointer;
                        menuIndex = 0;
                        radioOpen = false;

                        glassAsked = pending with
                        {
                            Opened = true,
                            Wilkes = story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidWilkes),
                            Buchelli = story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidBuchelli),
                        };
                    }
                }
                else if (menu is null)
                {
                    string? answer =
                        story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidWilkes) > pending.Wilkes
                            ? Game.DirtyGlasses.SaidWilkes
                        : story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidBuchelli) > pending.Buchelli
                            ? Game.DirtyGlasses.SaidBuchelli
                        : null;

                    if (answer is not null)
                    {
                        Dusted(Game.DirtyGlasses.Answer(pending.Glass, answer, story), pending.Glass, story, api);
                    }
                    else
                    {
                        Log.Info($"fingerprints: {pending.Glass} put away unanswered");
                    }

                    glassAsked = null;
                }
            }

            if (!console.Open && window.WasClicked(Platform.PointerButton.Secondary))
            {
                // The menu belongs to the thing it was opened over, not to wherever the
                // pointer wanders next, so what was under it is kept — and so is where it
                // was, because a menu that follows the pointer cannot be clicked.
                menu = menu is null && hover.Actionable ? hover : null;
                menuAt = pointer;
                menuIndex = 0;

                // One list at a time. Both are attached to a click and both cover the room,
                // and two of them open at once is two things a click could mean.
                radioOpen = false;

                // Asking and getting nothing has to look different from asking and being
                // ignored, or a room where nothing answers is indistinguishable from a
                // right-click that did not register.
                if (menu is null)
                {
                    Log.Info(hover.Noun is { Length: > 0 } asked
                        ? $"{asked} answers to nothing here and now"
                        : "nothing under the pointer");
                }
            }

            // What Gabriel can raise with Grace, here and now. Rebuilt every frame he is
            // wearing the headset — one timeblock in the game — because a topic appears and
            // disappears with the story: TE3's scales answer only once, and only from the
            // table. It is a resolve over the handful of nouns the room writes RADIO rules
            // for, so it costs nothing anywhere else and nothing at all in the 205 rooms
            // where he is not wearing it.
            List<Game.RadioTopic> topics = [];

            // Not while an action is playing and not while a line is playing, which is the
            // reference's rule for this button — <c>SetCanInteract(!actionActive)</c> — in
            // the terms this engine has. The button dims and the list closes itself for as
            // long as it lasts.
            //
            // <b>Three wrong signals were tried first.</b> <c>update.Occupied</c> is four
            // conditions, one of them "more scripts are parked than were parked when an
            // action last started", which in the temple is true from the moment the room
            // opens and never clears — TE3 offered nothing at all. <c>SceneAudio.Speaker</c>
            // is set by an animation's caption and is not cleared when that animation ends,
            // so TE1 read as Mosely speaking for the rest of the room. And
            // <c>Performing(ego)</c> plus <c>Talking</c>, which is what replaced them, is
            // about Gabriel rather than about the story: TE6's arrival is Montreaux
            // speaking over a script Gabriel is not in, so the headset lit up eight seconds
            // into it and Grace was radioed about a demon that had not woken up yet. That
            // is what <c>update.Acting</c> is for — it is <c>Occupied</c> with the term
            // that never clears replaced by the scripts an action actually waited on.
            if (Game.Radio.WornAt(story.Timeblock) &&
                !update.Acting &&
                room?.Talking != true &&
                scene.Actions is { } radioActions)
            {
                // The room's own general call first, where the room has one. It is what the
                // original's headset button did and the only way to some of what Grace
                // says: TE4's rules for the Solomon statue are commented out because this
                // covers them.
                if (api.Declares?.Invoke(here, Game.Radio.Call) == true)
                {
                    topics.Add(new Game.RadioTopic(
                        string.Empty,
                        (hud?.Text ?? UiText.English).Say("radio.ask", "Ask Grace")));
                }

                topics.AddRange(
                    Game.Radio.Topics(radioActions, story.Ego, interaction.NameOf));
            }

            if (radioIndex >= topics.Count)
            {
                radioIndex = 0;
            }

            if (Game.Radio.WornAt(story.Timeblock) && topics.Count > 0 && topics.Count != radioSaid)
            {
                radioSaid = topics.Count;

                Log.Info(topics.Count > 0
                    ? $"Radio: {topics.Count} thing(s) to ask Grace — " +
                      string.Join(", ", topics.Select(t => t.Label))
                    : "Radio: nothing to ask Grace about");
            }

            if (radioOpen)
            {
                if (topics.Count == 0)
                {
                    // A topic can stop being available while its list is open — a script
                    // running under it is enough — and a list of nothing cannot be closed
                    // by clicking a row that is not there.
                    radioOpen = false;
                }
                else if (hud?.TopicAt(pointer) is int over and >= 0)
                {
                    radioIndex = over;
                }
                else if (window.ScrollDelta != 0 && hud?.TopicCount > 0)
                {
                    int count = hud.TopicCount;

                    radioIndex = (((radioIndex - window.ScrollDelta) % count) + count) % count;
                }
            }

            if (menu is not null)
            {
                // One selection, three ways to move it. The wheel steps through the list
                // and wraps, because two or three verbs are not worth a dead end at either
                // end; putting the pointer on a row moves it there instead.
                //
                // Over the rows the menu actually drew rather than over the verbs it was
                // given: the row that opens the bag and the things inside it are rows too,
                // and a wheel that stops short of them cannot reach them.
                if (window.ScrollDelta != 0 && hud?.RowCount > 0)
                {
                    int count = hud.RowCount;

                    menuIndex = (((menuIndex - window.ScrollDelta) % count) + count) % count;
                }
                else if (hud?.RowAt(pointer) is int row and >= 0)
                {
                    menuIndex = row;
                }
            }

            // Whether the click below was swallowed by somebody talking rather than by the
            // room being busy. The two are the same branch and want opposite answers.
            bool cutALine = false;

            // A screen in front of the room takes the frame: it draws instead of the room's
            // interface and it takes the click. Nothing behind it is hovered, walked to or
            // acted on, which is what modal means and what stops a click on Sidney's menu
            // also being a click on the floor behind it.
            if (story.Screens.Top?.Kind != ScreenKind.Binoculars)
            {
                throughEyes = false;
            }

            if (screens is not null && story.Screens.Top is { } panel)
            {
                Panorama seen = binoculars.For(scene.Name, story.Timeblock.ToString());

                // The hose. Its state is not the screen stack's business — the jet and the
                // clock move every frame and the stack is part of the state hash — so it
                // lives beside it and lasts exactly as long as the screen does.
                if (panel.Kind == ScreenKind.Water)
                {
                    aiming ??= new Game.WaterAiming();

                    float span = MathF.Min(window.FramebufferWidth, window.FramebufferHeight) * 0.72f;

                    aiming.PointAt(new Vector2(
                        (pointer.X - ((window.FramebufferWidth - span) / 2f)) / span,
                        (pointer.Y - ((window.FramebufferHeight - span) / 2f)) / span));

                    if (aiming.Advance((float)delta))
                    {
                        // What the original's own rule does with it: the case that reads
                        // ten seconds on the nest is now true, and its script takes over.
                        Log.Info("water: the nest comes down");

                        if (scene.Actions?.Find("WATER_INTERFACE", "AIM", story.Ego) is { } soaked)
                        {
                            new ActionRunner(api).Run(soaked);
                        }

                        story.Screens.Back();
                    }
                }
                else
                {
                    aiming = null;
                }

                // The map's traffic, built once per opening and moved on every frame. A
                // chase carries its number in the screen's own subject — follow:2 — which
                // is what makes it survive a save and what tells this that the map showing
                // now is not the map that was showing a moment ago.
                if (panel.Kind == ScreenKind.Driving)
                {
                    string opened = panel.ToString();

                    // The map is a place, and opening it asks whether the point in the
                    // story is over the way walking through a door does. The original
                    // changed location to MAP to show it and ran the rules on that, and
                    // the first afternoon ends nowhere else: 102P is over when the map
                    // opens with both chases run, and its card shows over the map. Not
                    // while giving chase — the original skips the check there too, or the
                    // last chase of the afternoon ended it a room early — and not on the
                    // port's own ride, which is a map the player has already chosen from.
                    //
                    // The map is left open on purpose. The room is built again under it
                    // in the new timeblock, the outer loop's own check reads the map as
                    // where the player is (GameState.Whereabouts) and moves the clock,
                    // and the map is what the player sees after the card — which is where
                    // the original puts them.
                    if (panel.Subject is null &&
                        (traffic is null || trafficFor != opened) &&
                        !story.ChangingTimeblock &&
                        Game.Story.TimeblockRules.Check(story) is not null)
                    {
                        Log.Info($"Timeblock: {story.Timeblock} is over on the map");
                        update.Cancel();

                        return new RoomExit(0, here);
                    }

                    if (traffic is null || trafficFor != opened)
                    {
                        traffic = Game.DrivingTraffic.For(
                            story,
                            map,
                            panel.Subject?.Split(':') is ["follow", string chased] &&
                            int.TryParse(chased, NumberStyles.Integer, CultureInfo.InvariantCulture, out int which)
                                ? which
                                : 0,
                            panel.Subject?.Split(':') is ["ride", string going] ? going : null);

                        trafficFor = opened;

                        if (traffic.Destination is { } riding)
                        {
                            Log.Info($"Riding from {traffic.From} to {riding}");
                        }

                        if (traffic.Chase is { } quarry)
                        {
                            Log.Info(
                                $"Following {quarry.Noun} out of {traffic.From} " +
                                $"to {quarry.Arrives ?? "where they started"}");
                        }
                    }

                    traffic.Advance(delta);

                    // And the chase ends where the quarry stops. Everything it was for
                    // happens here: what they led the player to goes on the map for good,
                    // the count that says so is written, and the player rides after them.
                    //
                    // After what Gabriel has to say about it, said over the map the way
                    // the original says it, and waited out before the map closes. The
                    // line is the chase's whole verdict — Madeleine's and Wilkes's say
                    // where they went, Lady Howard's says she is going nowhere — and a
                    // chase that closed the map on the last frame of the ride said none
                    // of it, so Lady Howard's circuit back to where it began looked like
                    // a chase that had not worked. It runs as an action so that the room
                    // counts it down and Acting says when it is over.
                    if (traffic is { Following: true, Arrived: true } chase)
                    {
                        if (!chase.Said)
                        {
                            chase.Said = true;

                            if (chase.Chase?.Says is { Length: > 0 } verdict)
                            {
                                new ActionRunner(api).Run(new Formats.Actions.NvcAction
                                {
                                    Noun = chase.Chase.Noun,
                                    Verb = DrivingMap.Follow,
                                    Case = "ARRIVED",
                                    Script = string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"wait StartDialogue(\"{verdict}\", 1)"),
                                    Source = "the chase",
                                });

                                Log.Info($"Followed {chase.Chase.Noun}: {verdict}");
                            }
                        }

                        if (!update.Acting)
                        {
                            traffic = null;
                            trafficFor = null;

                            if (Arrive(chase, story) is { } rideOn)
                            {
                                story.Screens.CloseAll();
                                story.RideTo(rideOn);
                            }
                            else
                            {
                                // The map stays up for the player to choose from, as a
                                // plain map with whoever is still circling on it. Its
                                // traffic is built here, under the same name the next
                                // frame will read off the screen, so that the timeblock is
                                // not asked about until the map is next opened — the
                                // original asks on the way in, not after a chase.
                                var plain = new Screen(ScreenKind.Driving);

                                story.Screens.Replace(plain);

                                traffic = Game.DrivingTraffic.For(story, map);
                                trafficFor = plain.ToString();
                            }
                        }
                    }
                    else if (traffic is { Riding: true, Arrived: true, Destination: { } there })
                    {
                        traffic = null;
                        trafficFor = null;

                        story.Screens.CloseAll();
                        story.RideTo(there);
                    }
                }
                else if (traffic is not null)
                {
                    // A chase the player walked out of, which they are allowed to do: the
                    // map has the same way out every screen has. The count the room's own
                    // action wrote is put back, so the quarry goes back to circling and can
                    // be followed again — left at one they would neither be on the roads
                    // nor have led anywhere, and the puzzle would be gone.
                    if (traffic is { Following: true, Arrived: false, Chase: { } gaveUp })
                    {
                        story.SetNounVerbCount(gaveUp.Counted, DrivingMap.Follow, 0);

                        Log.Info($"Gave up following {gaveUp.Noun}");
                    }
                    else if (traffic is { Following: true, Arrived: true } caught)
                    {
                        // Closed while Gabriel was still saying what he made of it. The
                        // chase happened all the same, and what it earned is not lost
                        // with the rest of the sentence.
                        if (Arrive(caught, story) is { } rideOn)
                        {
                            story.RideTo(rideOn);
                        }
                    }

                    traffic = null;
                    trafficFor = null;
                }

                if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) &&
                    screens.HitAt(pointer) is { Length: > 0 } chose)
                {
                    // Leaning in is a camera and, often, another room, so it is handled
                    // here where both are in reach rather than in OnScreen.
                    // The fingerprint kit. Brushing counts what the surface has and keeps
                    // the count in the screen's own subject, so there is no state to clean
                    // up however the screen is left; lifting awards the prints — the score,
                    // the flag and the item each one carries — which is the step the
                    // original does from its own code and no script anywhere names.
                    if (chose == "fp:brush" &&
                        panel.Kind == ScreenKind.Fingerprint &&
                        panel.Subject is { Length: > 0 } dusting)
                    {
                        int found =
                            Game.FingerprintKit.On(dusting, story.Timeblock)?.Count ?? 0;

                        story.Screens.Replace(
                            new Screen(ScreenKind.Fingerprint, $"{dusting}|{found}"));
                    }
                    else if (chose == "fp:lift" &&
                             panel.Kind == ScreenKind.Fingerprint &&
                             panel.Subject is { Length: > 0 } lifting)
                    {
                        string bare = lifting.Split('|')[0];

                        // The lobby's two glasses are the one surface whose print is not
                        // simply whose the file says: see Game.DirtyGlasses.
                        if (Game.DirtyGlasses.Dust(bare, story) is { } glass)
                        {
                            Dusted(glass, bare, story, api);

                            if (glass.Asks is { } question)
                            {
                                glassAsked = new GlassQuestion(bare, question);
                            }
                        }
                        else
                        {
                            IReadOnlyList<string> gained =
                                Game.FingerprintKit.Lift(bare, story, api.Scores);

                            Log.Info(gained.Count > 0
                                ? $"fingerprints: {bare} gave {string.Join(", ", gained)}"
                                : $"fingerprints: {bare} lifted");
                        }

                        story.Screens.Back();
                    }
                    else
                    // Asking for help. One line of the walkthrough per press, always the
                    // next one, and never a word of it unasked — a player a little stuck
                    // needs the first, which says where to go, and the one that gives a
                    // puzzle away is further down.
                    if (chose.StartsWith("hint:", StringComparison.Ordinal))
                    {
                        string wanted = chose[5..];

                        if (journal.Find(wanted) is { } asking)
                        {
                            string? given = journal.Reveal(asking);

                            Log.Info(given is { Length: > 0 }
                                ? $"journal: {asking.Title} — {given}"
                                : $"journal: no more hints for {asking.Title}");
                        }
                    }
                    else if (chose.StartsWith("sidney:shape:", StringComparison.Ordinal) &&
                        sidney is not null &&
                        Enum.TryParse(chose[13..], ignoreCase: true, out Game.Sidney.MapShape picked))
                    {
                        console.Print(sidney.LayShape(picked).Text);
                    }
                    else if (chose == "sidney:mark" && sidney is not null &&
                        screens.MapBounds is { Z: > 0 } drawn)
                    {
                        // Back into the map's own 1,368 pixels, so a mark means the same
                        // place whatever size the window is and however far it is zoomed.
                        console.Print(sidney.Mark(screens.MapAt(pointer)).Text);
                    }
                    // Giving chase from the map itself, which the original could not do:
                    // there, the only way to follow anybody was to catch them going past
                    // in the room. Reported as the puzzle being lost — see
                    // Game.DrivingTraffic.
                    // A place the map will not take the player just now: Larry's driveway
                    // the evening the two men are in it and the night Gabriel goes over on
                    // foot, and the station on the days Grace has no business there. The
                    // retail map answers with a line and stays open, and so does this.
                    else if (chose.StartsWith("drive:", StringComparison.Ordinal) &&
                             panel.Kind == ScreenKind.Driving &&
                             DrivingMap.Refused(story, chose[6..]) is { } excuse)
                    {
                        new ActionRunner(api).Run(new Formats.Actions.NvcAction
                        {
                            Noun = chose[6..],
                            Verb = "DRIVE",
                            Case = "REFUSED",
                            Script = string.Create(
                                CultureInfo.InvariantCulture,
                                $"wait StartDialogue(\"{excuse}\", 1)"),
                            Source = "the map",
                        });

                        Log.Info($"The map will not go to {chose[6..]}: {excuse}");
                    }
                    else if (chose.StartsWith("follow:", StringComparison.Ordinal) &&
                             panel.Kind == ScreenKind.Driving)
                    {
                        if (chose == "follow:skip")
                        {
                            traffic?.Skip();
                        }
                        else
                        {
                            story.Screens.Replace(new Screen(ScreenKind.Driving, chose));
                        }
                    }
                    else if (chose.StartsWith("zoom:", StringComparison.Ordinal) &&
                        seen.Sights.FirstOrDefault(s => s.Location == chose[5..]) is { } sight)
                    {
                        Log.Info($"Binoculars: {sight.Location}");

                        if (!string.Equals(sight.Scene, scene.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // Another room, looked at rather than walked to. The binoculars
                            // stay up over it — that is what the player is looking through —
                            // and the story goes on saying they are standing here.
                            api.Leaning = new Game.BinocularView(
                                scene.Name,
                                sight,
                                update.Where(story.Ego) ?? Vector3.Zero,
                                update.Facing(story.Ego) ?? 0,
                                camera.Position,
                                camera.Aim);

                            api.Wanted = sight.Scene;
                            api.WantedCamera = (sight.Position, sight.Angle);

                            story.Screens.Replace(new Screen(
                                ScreenKind.Binoculars, $"{Screen.Zoomed}:{sight.Scene}"));
                        }
                        else
                        {
                            story.Screens.Back();

                            camera.Position = sight.Position;
                            camera.Aim = sight.Angle;
                        }
                    }

                    // And lowering them again, which is the room the player never left.
                    // Where there is no look in progress the button is only a way out: a
                    // save written while leaning in comes back with the screen on the stack
                    // and nothing behind it, and a button that did nothing would strand
                    // the player behind a pair of eyepieces.
                    else if (chose == "binocs:back")
                    {
                        if (api.Leaning is { } lowering)
                        {
                            return Lower(lowering);
                        }

                        story.Screens.Back();
                    }
                    else if (chose.StartsWith("verb:", StringComparison.Ordinal) &&
                             panel.Subject is { Length: > 0 } about &&
                             scene.Actions?.Find(about, chose[5..], story.Ego) is { } onItem)
                    {
                        // The item's own action, run where it was written to run: with the
                        // inventory still on top, because that is what its case asked about
                        // and a script may well close the screen itself.
                        ActionOutcome ran = new ActionRunner(api).Run(onItem);

                        Log.Info(
                            $"{about}:{chose[5..]} [{onItem.Case}] - " +
                            $"{(ran.Ran ? "ran" : "refused")} {ran.Statements.Count} statement(s)");
                    }
                    else if (chose.StartsWith("item:", StringComparison.Ordinal) &&
                             chose[5..] is { Length: > 0 } inHand &&
                             panel.Kind == ScreenKind.Inventory &&
                             !inHand.StartsWith("SIDNEY", StringComparison.OrdinalIgnoreCase))
                    {
                        story.Inventory.SetActive(story.Ego, inHand);

                        IReadOnlyList<string> offered = ItemVerbs(
                            new Screen(ScreenKind.Inventory, inHand), scene, story) ?? [];

                        // One thing to do, so it is done. Several, and they are offered
                        // where the item sits, exactly as a right click offers a noun's
                        // verbs in the room — rather than a page of its own to hold two
                        // words on.
                        Formats.Actions.NvcAction? single = offered is [string only]
                            ? scene.Actions?.Find(inHand, only, story.Ego)
                            : null;

                        // The words hanging beside an item belong to the item that was
                        // clicked, so every click moves them: to the thing just clicked
                        // when it has a list of its own, and away altogether when it has
                        // one action to perform or nothing to offer. Reported: clicking a
                        // second item left the first one's list open over the page, because
                        // only the branch that opens a list ever touched the subject.
                        //
                        // Before the action rather than after it. A script may put a screen
                        // of its own up, and replacing the top of the stack once it has
                        // would throw that away.
                        story.Screens.Replace(new Screen(
                            ScreenKind.Inventory,
                            single is null && offered.Count > 0 ? inHand : null));

                        if (single is { } act)
                        {
                            ActionOutcome ran = new ActionRunner(api).Run(act);

                            Log.Info(
                                $"{inHand}:{act.Verb} [{act.Case}] - " +
                                $"{(ran.Ran ? "ran" : "refused")} {ran.Statements.Count} statement(s)");
                        }
                    }
                    else
                    {
                        OnScreen(
                            chose,
                            story,
                            sidney,
                            update,
                            console,
                            sidney is null
                                ? null
                                : item => ScanIntoSidney(item, api, scene, sidney, console));
                    }
                }

                // <b>Dragging is a press, not a click.</b> A click is reported on the way
                // back up and only when the pointer has hardly moved, which is exactly what
                // a drag is not — so a place dragged across the map produced no click at
                // all, and a place merely pressed produced one with the button already
                // released. Picked up on the edge of the press instead.
                bool holding = !console.Open && window.IsHeld(Platform.PointerButton.Primary);

                if (holding && !heldLastFrame && sidney is { Dragging: < 0 } &&
                    panel.Kind == ScreenKind.Sidney &&
                    screens.HitAt(pointer) is { } under2 &&
                    under2.StartsWith("sidney:point:", StringComparison.Ordinal) &&
                    under2[13..].Split(':') is [string owner, string index] &&
                    int.TryParse(owner, out int belongs) &&
                    int.TryParse(index, out int lifted))
                {
                    sidney.StartDrag(belongs, lifted);
                }

                // <b>A drag on the map itself slides it.</b> Zoomed six times into a
                // 1,368-pixel survey there is more country off the glass than on it, and
                // the wheel alone can only look closer at the middle. A click still marks,
                // because a click is a press and release that hardly moved — which is
                // exactly what a drag is not.
                if (holding && sidney is { Dragging: < 0 } sliding &&
                    panel.Kind == ScreenKind.Sidney && sliding.Zoom > 1f &&
                    screens.MapBounds is { Z: > 0 } over &&
                    pointer.X >= over.X && pointer.X <= over.X + over.Z &&
                    pointer.Y >= over.Y && pointer.Y <= over.Y + over.W)
                {
                    if (heldLastFrame)
                    {
                        float across = Game.Sidney.SidneyMap.Extent / sliding.Zoom / over.Z;

                        sliding.PanBy(new System.Numerics.Vector2(
                            (panFrom.X - pointer.X) * across, (panFrom.Y - pointer.Y) * across));
                    }

                    panFrom = pointer;
                }

                heldLastFrame = holding;

                // And it follows the pointer until the button comes back up, which is the
                // whole of dragging one: the map is drawn from the marks every frame, so
                // moving one is all there is to do.
                if (sidney is { Dragging: >= 0 } && panel.Kind == ScreenKind.Sidney)
                {
                    if (holding && screens.MapBounds is { Z: > 0 })
                    {
                        sidney.DragTo(screens.MapAt(pointer));
                    }
                    else if (sidney.EndDrag() is { } settled)
                    {
                        console.Print(settled.Text);
                    }
                }

                // The wheel, before anything else looks at the pointer: a list inside
                // Sidney is what it means while one is under it.
                if (panel.Kind == ScreenKind.Sidney && window.ScrollDelta != 0)
                {
                    screens.SidneyWheel(pointer, window.ScrollDelta);
                }

                // Sidney's two text boxes are the only places in the game the player types
                // into that are not the console, so the keys go there while one is showing:
                // the search box, and the string that finishes the Arcadia inscription.
                if (!console.Open &&
                    sidney is { } typing2 &&
                    panel.Kind == ScreenKind.Sidney &&
                    (typing2.Screen == Game.Sidney.SidneyScreen.Search || typing2.Appending))
                {
                    if (window.Typed is { Length: > 0 } letters)
                    {
                        typing2.Typed += letters;
                    }

                    if (window.WasPressed(Platform.EditKey.Backspace) && typing2.Typed.Length > 0)
                    {
                        typing2.Typed = typing2.Typed[..^1];
                    }

                    if (window.WasPressed(Platform.EditKey.Enter))
                    {
                        if (typing2.Appending)
                        {
                            console.Print(typing2.Append().Text);
                        }
                        else
                        {
                            typing2.Look();
                        }
                    }
                }

                if (!console.Open && window.WasPressed(Platform.CameraAction.Quit))
                {
                    if (api.Leaning is { } backing)
                    {
                        return Lower(backing);
                    }

                    story.Screens.Back();
                }

                // The binoculars are the one screen the player still looks *through*, so
                // the camera keeps taking their input while it is raised. Not while they
                // are leaning into somewhere else: that view is the one the game's own data
                // framed, in a room whose floor and walls the player is not standing on.
                if (panel.Kind == ScreenKind.Binoculars &&
                    !(panel.Subject?.StartsWith(Screen.Zoomed, StringComparison.Ordinal) ?? false))
                {
                    // Raised to the player's own eyes, once: just in front of the face, at
                    // eye height, looking the way they stand. Not when the camera is
                    // already there, which is what leaning back out of a look restores.
                    if (!throughEyes)
                    {
                        throughEyes = true;

                        if (update.EyesOf(story.Ego) is { } eyes)
                        {
                            // --aim outranks the way they stand, so a run can be pointed at a
                            // sight without dragging a pointer it has not got.
                            float facing = looking is { } asked
                                ? asked.X * MathF.PI / 180f
                                : update.SettledFacing(story.Ego) ?? (camera.Aim.X * MathF.PI / 180f);
                            Vector3 ahead = new(MathF.Sin(facing), 0f, MathF.Cos(facing));
                            Vector3 at = eyes + (ahead * 10f);

                            if (Vector3.Distance(camera.Position, at) > 4f)
                            {
                                camera.Position = at;
                                camera.Aim = new Vector2(facing * 180f / MathF.PI, looking?.Y ?? 0f);
                            }
                        }
                    }

                    if (!console.Open && !typing)
                    {
                        camera.Update(window, (float)delta);
                    }
                }

                screens.Build(
                    new ScreenView(
                        panel,
                        story.Inventory.ItemsOf(story.Ego),
                        story.Inventory.ActiveItemOf(story.Ego),
                        sidney,
                        Reachable(panel, scene, story),
                        panel.Subject,
                        map,
                        DrivingMap.Open(story, scene.Name),
                        renderer.OverlayPicture,
                        seen,
                        camera.Aim,
                        ItemVerbs(panel, scene, story),
                        panel.Kind == ScreenKind.Journal ? journal.Read() : null,
                        panel.Kind == ScreenKind.Fingerprint &&
                        panel.Subject?.Split('|') is [_, string counted] &&
                        int.TryParse(counted, out int prints)
                            ? prints
                            : -1,
                        icons,
                        closeUps,
                        verbIcons,
                        artwork,
                        aiming,
                        traffic,
                        front.Settings.Captions ? room?.Caption : null,
                        front.Settings.Captions ? room?.Speaker : null,
                        panel.Kind == ScreenKind.InventoryInspect && Game.SerpentRouge.IsReader(panel.Subject)
                            ? Game.SerpentRouge.Show(story, Game.SerpentRouge.Page(story))
                            : null),
                    window.FramebufferWidth,
                    window.FramebufferHeight,
                    pointer);

                renderer.SetOverlay(screens.Overlay);

                // A screen in front of the room has no nouns, so the pointer is the arrow.
                window.PointerShape = Platform.PointerShape.Default;

                window.EndFrame();

                // The binoculars are looked through, so the room behind them goes on: the
                // birds keep flying and the fires keep burning. Every other screen covers
                // the room, and what it covers may stand still.
                if (panel.Kind == ScreenKind.Binoculars)
                {
                    BlendAir(view, delta);
                }

                renderer.SetScene(geometry, view);

                // Here as well as below: a player who opens the inventory on the frame they
                // arrive takes this branch instead, and a fade nobody advances is a screen
                // that stays black.
                fade.Advance();

                // Counted like any other frame, so a run with a frame limit still ends and
                // its screenshot is of the screen rather than of the room behind it.
                if (renderer.DrawFrame(0f, 0f, 0f))
                {
                    presented++;
                }

                continue;
            }

            // The headset, which is a list rather than a screen and so is answered before
            // the two that open one. Clicking it again puts it away, which is what every
            // button that opens something does.
            if (!console.Open &&
                window.WasClicked(Platform.PointerButton.Primary) &&
                hud?.ButtonAt(pointer) == GameHud.RadioButton)
            {
                radioOpen = !radioOpen && topics.Count > 0;
                radioIndex = 0;
                menu = null;

                if (!radioOpen && topics.Count == 0)
                {
                    // The button is drawn dim in this case, so this is a player checking
                    // rather than a player being ignored. Said out loud all the same: an
                    // empty list and a swallowed click look identical on screen.
                    Log.Info("Radio: nothing to ask Grace about here");
                }
            }

            // A topic, which performs the room's own RADIO rule for that noun — the same
            // rule, with the same conditions, that picking RADIO off the verb menu runs.
            else if (!console.Open &&
                     window.WasClicked(Platform.PointerButton.Primary) &&
                     radioOpen &&
                     hud?.TopicAt(pointer) is int picked and >= 0 &&
                     picked < topics.Count)
            {
                Game.RadioTopic topic = topics[picked];
                radioOpen = false;

                if (topic.IsGeneral)
                {
                    Log.Info($"Radio: calling Grace from {here}");
                    Sheep.SheepExpression.Evaluate(
                        $"CallSheep(\"{here}\", \"{Game.Radio.Call}\")", api);
                }
                else if (interaction.Do(topic.Noun, Game.Radio.Verb) is { } asked)
                {
                    Log.Info($"Radio: {asked.Noun}:{asked.Verb} [{asked.Case}]");
                }
            }

            // A click anywhere else while the list is up puts it away without doing
            // anything, which is what every menu does — and it must not also act on the
            // room behind it, so it is answered here rather than left to fall through.
            else if (!console.Open &&
                     window.WasClicked(Platform.PointerButton.Primary) &&
                     radioOpen)
            {
                radioOpen = false;
            }

            // The room's own button, which is a move the mechanism has asked for outright
            // because pointing at the room will not find it. Answered before the top bar's,
            // since it is the one the player is being told to press.
            else if (!console.Open &&
                window.WasClicked(Platform.PointerButton.Primary) &&
                hud?.ButtonAt(pointer) == GameHud.PromptButton)
            {
                api.Mechanism?.Press();
                menu = null;
            }

            // The top bar's two buttons, which are the only way in that a player who has not
            // read a key list will find.
            else if (!console.Open &&
                window.WasClicked(Platform.PointerButton.Primary) &&
                hud?.ButtonAt(pointer) is { Length: > 0 } opening &&
                story.Screens.InventoryReachable)
            {
                ScreenKind wanted = opening == "open:journal"
                    ? ScreenKind.Journal
                    : ScreenKind.Inventory;

                if (story.Screens.IsOnTop(wanted))
                {
                    story.Screens.Back();
                }
                else
                {
                    story.Screens.Show(new Screen(wanted));
                }

                menu = null;
            }

            // The strip along the foot of the screen is the inventory, so a click on it is
            // a click on what the player is carrying rather than on the room behind it.
            // Once to take a thing in hand, again to look at it closely — which is where
            // its own verbs live, because the action files guard every one of them behind
            // "the inventory is what you are looking at".
            else if (!console.Open &&
                window.WasClicked(Platform.PointerButton.Primary) &&
                hud?.ItemAt(pointer) is { Length: > 0 } clicked)
            {
                if (string.Equals(
                        story.Inventory.ActiveItemOf(story.Ego),
                        clicked,
                        StringComparison.OrdinalIgnoreCase))
                {
                    story.Screens.Show(new Screen(ScreenKind.InventoryInspect, clicked));
                    Log.Info($"inventory: looking at {clicked}");
                }
                else
                {
                    story.Inventory.SetActive(story.Ego, clicked);
                    Log.Info($"inventory: holding {clicked}");
                }

                menu = null;
            }
            else if (!console.Open &&
                     window.WasClicked(Platform.PointerButton.Primary) &&
                     menu is null &&
                     hud?.OverInterface(pointer) != true &&
                     // Assigned in the condition because Skip() silences the line it
                     // reports, so it must be called here and exactly once — and which of
                     // the two arms swallowed the click is what the counter below needs.
                     ((cutALine = room?.Skip() == true) || update.Occupied))
            {
                // Somebody is speaking, so the click reads the line rather than the room:
                // it cuts the recording short and the next one starts. Nothing else happens
                // — the player is not sent walking across the floor behind the conversation,
                // which was the complaint, and no verb is performed either, because a click
                // during dialogue is about the dialogue.
                //
                // And not only while a line is audibly playing. A conversation is lines,
                // silences between them, and scripts still running through both, and a
                // click in one of the silences used to fall through to the floor and send
                // Gabriel walking out of the middle of it. Occupied is the same signal the
                // trigger rectangles trust: deferred actions, an action's stated seconds,
                // the player performing, or the story's own scripts still outstanding.
                //
                // Not while a menu is open, and not on the interface: those clicks already
                // mean something, and a conversation is not a reason to take them away.

                // A click that went nowhere because the room said it was busy, with nobody
                // speaking. One of those is ordinary — the player clicked during a beat.
                // Three in a row is the player telling the game it is stuck, and they are
                // right often enough to believe them: Occupied is four separate things and
                // any one of them can wedge, at which point there is no camera, no walking
                // and no way to say so except through a menu they may not know is there.
                //
                // Skipping a line resets it, because a conversation the player is tapping
                // through is the game working.
                if (cutALine)
                {
                    refused = 0;
                }
                else if (++refused >= 3)
                {
                    refused = 0;

                    IReadOnlyList<string> let = update.Unstick();

                    Log.Info(let.Count == 0
                        ? "Unstuck by three clicks: nothing was holding the room."
                        : "Unstuck by three clicks: let go of " + string.Join(", ", let) + ".");
                }
            }
            // Leaning in, on a button of its own. Looking closely at a thing is not doing
            // something to it, and while it shared the left button it won every click:
            // the close-up is offered for nearly every noun in the game, so a click meant
            // to cross the room leaned in at a doorframe instead.
            else if (!console.Open &&
                     window.WasClicked(Platform.PointerButton.Middle) &&
                     menu is null &&
                     hud?.OverInterface(pointer) != true)
            {
                if (interaction.Do(hover, hover.Closer) is { } looked)
                {
                    Log.Info($"Did: {looked.Noun}:{looked.Verb}");
                }
            }
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary))
            {
                // The room took this one, so it was never stuck.
                refused = 0;

                // A click inside the open menu takes whatever is selected; a click anywhere
                // else dismisses it without doing anything, which is what every menu does.
                bool inside = menu is not null && hud?.RowAt(pointer) >= 0;

                // A double-click means the same thing as a click, more urgently: whatever
                // walking the action puts in front of itself is run rather than walked.
                bool hurry = window.WasDoubleClicked(Platform.PointerButton.Primary);

                // What the selected row means, which is a verb, an item to use, or the
                // row that only opens the bag. The last of those is not something to do,
                // so a click on it is left to the frame after, by which time the column
                // it opened is showing and has rows of its own.
                string? chosenRow = inside ? hud?.RowNamed(menuIndex) : null;
                bool openingBag = chosenRow == GameHud.UseRow;

                // Shift arrives at once. Three ways of saying how much of the walk you want
                // to watch: a click walks it, a double-click runs it, and shift skips it.
                // Asked for on the ways out of a room, which are the walks a player repeats
                // most and learns least from — and it costs nothing to mean the same thing
                // everywhere, including a click on open floor.
                update.WarpNextWalk = window.IsHeld(Platform.CameraAction.Fast);

                barTookAVerb = menu is not null && chosenRow is { Length: > 0 } && !openingBag;

                // The room's own machinery gets first refusal, ahead of the action files.
                // Only where it has said it wants this click — see ClaimsClick — and never
                // while the verb bar is up, because a click on the bar is a click on the
                // interface and belongs to whatever the player picked.
                bool roomTook = menu is null &&
                    claimed is not null &&
                    hud?.OverInterface(pointer) != true &&
                    api.Mechanism?.TakesClick(hover.Pick) == true;

                ActionOutcome? did = roomTook
                    ? null
                    : menu is { } open
                        ? chosenRow is { Length: > 0 } && !openingBag
                            ? interaction.Do(open, chosenRow, hurry)
                            : null
                        : interaction.Do(hover, hurry: hurry);

                if (roomTook)
                {
                    Log.Info($"{story.Ego}: the room claimed the click" +
                        (claimed is { Length: > 0 } what ? $" — {what}" : string.Empty));
                }

                // Nothing to do to the thing clicked, so ask whether it was the ground and
                // go there. Three things have to be true. No verb menu was open, because
                // the click that closes a menu means "not that after all" and would
                // otherwise be impossible to make without crossing the room. The pointer
                // was not on the interface: the inventory strip lies across the foot of
                // the screen, exactly where the floor at the player's feet is drawn, and a
                // click on it must not go through. And the ray reached the floor.
                // And nobody is in the middle of a scene. A clip a script started is the
                // story happening, and walking out of the middle of it leaves it playing to
                // an empty patch of floor — reported from the dining room, where a click
                // during the coffee sent Gabriel away while the scene carried on without
                // him. A character's own idle is not this and may be cut short freely.
                // Before the floor: a room may claim a click the action files leave
                // unanswered. TE1's tile floor carries no noun, so nothing resolves on it,
                // and the board wants it to mean "jump back off me" rather than "walk".
                if (roomTook)
                {
                    // Already dealt with above, before the action files were asked.
                }
                else if (did is null &&
                    menu is null &&
                    window.WasClicked(Platform.PointerButton.Primary) &&
                    hud?.OverInterface(pointer) != true &&
                    api.Mechanism?.TakesClick(hover.Pick) == true)
                {
                    Log.Info($"{story.Ego}: the room took the click");
                }
                else if (did is null &&
                    menu is null &&
                    !update.Performing(story.Ego) &&
                    hud?.OverInterface(pointer) != true &&
                    interaction.FloorTarget(hover) is { } ground)
                {
                    // Except where the room takes its own floor clicks. TE6 is the only
                    // one: Gabriel is circling a pentagram and moves a step at a time in
                    // the room's own animations, so a click there is a message to the
                    // script rather than a place to walk to.
                    if (api.Mechanism?.TakesFloorClick() == true)
                    {
                        Log.Info($"{story.Ego}: the room took the click on the floor");
                    }
                    else
                    {
                        // A click across the room runs, a click at the player's feet does not.
                        double crossing = update.Walk(
                            story.Ego, ground, hurry: hurry, mayRun: true);

                        Log.Info(crossing > 0
                            ? string.Create(
                                CultureInfo.InvariantCulture,
                                $"{story.Ego}: walking to {ground.X:F0}, {ground.Z:F0}, {crossing:F1}s")
                            : $"{story.Ego}: nowhere to walk from here");
                    }
                }


                // A menu the story has made modal stays up until something on it is
                // chosen. StopVerbCancel is a script saying the player does not get to
                // walk away from this one.
                if (menu is not null && !openingBag && !(story.MustChooseAnAction && did is null))
                {
                    menu = null;
                }

                if (did is { } outcome)
                {
                    Log.Info(
                        $"{outcome.Noun}:{outcome.Verb} [{outcome.Case}] - " +
                        (outcome.Deferred
                            ? string.Create(
                                CultureInfo.InvariantCulture,
                                $"walking {outcome.Approaching:F1}s first, then " +
                                $"{outcome.Statements.Count} statement(s)")
                            : $"{(outcome.Ran ? "ran" : "refused")} " +
                              $"{outcome.Statements.Count} statement(s)") +
                        (outcome.Seconds > 0 ? $", {outcome.Seconds:F1}s" : string.Empty));
                }
            }

            // <b>A conversation ends when the bar it was being held through goes away.</b>
            // Nothing in the game's own scripts ends the museum's, or the front desk's, or
            // any of the others a topic list is picked from: the original ends them from its
            // own code, and it does it here — ActionManager::OnActionBarCanceled runs
            // GLB_ALL's CodeCallEndConv$, whose whole body is EndConversation(), "every time
            // the action bar disables". Dismissed, or emptied of topics and dismissed for
            // you; taking a verb off it is not a cancel and must not end anything.
            //
            // Without it a conversation never ended. Its participants kept the talk and
            // listen scripts the [LISTENERS] section lends them and the pose its enter
            // animation put them in, and the camera kept framing the pair — reported from
            // the museum as Lady Howard and Estelle never leaving the conversation, with
            // Gabriel stuck in front of them until Get Unstuck was used.
            if (barWasShowing && menu is null && !barTookAVerb && api.State.Conversation is not null)
            {
                Log.Info($"conversation: {api.State.Conversation} ends with the verb bar");
                Sheep.SheepExpression.Evaluate(
                    "CallSheep(\"GLB_ALL\", \"CodeCallEndConv\")", api);
            }

            // The device is the clock for dialogue: the next line of a voice-over starts
            // when the last one's source stops, so they never overlap and never drift.
            room?.Update(delta);

            if (room?.Caption is { Length: > 0 } caption && caption != spoken)
            {
                spoken = caption;
                Log.Info($"  {room.Speaker}: {caption}");
            }

            // Nothing of the room is drawn over a movie: not the caption of whatever was
            // being said when it started, not the noun under the pointer, not the
            // inventory. The original stops for a cutscene and so does this.
            //
            // The film's own subtitles are the exception, and they are the film's rather
            // than the room's — its own YAK, on its own clock, and its own row in the menu.
            // A caption under a speaking character and a subtitle across a full-screen
            // picture are different enough that somebody may want one and not the other.
            if (movies.Playing)
            {
                window.PointerShape = Platform.PointerShape.Default;

                if (pages is not null && front.Settings.MovieSubtitles &&
                    movies.Caption is { Length: > 0 })
                {
                    pages.Film(
                        movies.Caption,
                        movies.Speaker,
                        null,
                        0f,
                        window.FramebufferWidth,
                        window.FramebufferHeight);

                    renderer.SetOverlay(pages.Overlay);
                }
                else
                {
                    renderer.SetOverlay(null);
                }
            }
            else if (hud is not null)
            {
                Hover showing = menu ?? hover;

                // What the room claims outranks what the action files offer, and it is the
                // only thing on the bar while it does. TE3's blade answers to LOOK all the
                // time and to a grab for about two seconds either side of the slot; a
                // player given LOOK during those two seconds has no way of knowing the
                // window is open, and the reference puts a grab cursor up for exactly this.
                // An empty claim advertises nothing: the room has taken the click, and
                // there is nothing useful to say about it.
                bool advertised = menu is null && claimed is { Length: > 0 };

                // The pointer says what a click would do, before the bar has to be read:
                // the arrow over nothing, a glass over what can be looked at, a bubble
                // over who can be talked to, a hand on a knob over the way out, and a
                // pointing hand over the rest.
                window.PointerShape = PointerChoice.For(hover, claimed, menu is not null, scene.Actions?.Verbs);

                hud.Build(
                    new HudState(
                        showing.Label,
                        advertised
                            ? [claimed!]
                            : [.. showing.Actions
                                .Where(a => !IsAnItem(a.LocalizedVerb, scene.Actions?.Verbs))
                                .Select(a => a.LocalizedVerb)],
                        advertised ? claimed : hover.Default,
                        pointer,
                        menu is not null,
                        menuIndex,
                        menuAt,
                        front.Settings.Captions ? room?.Speaker : null,
                        front.Settings.Captions ? room?.Caption : null,
                        story.Inventory.ItemsOf(story.Ego),
                        story.Inventory.ActiveItemOf(story.Ego),
                        InventoryOpen: true,
                        strings.Where(scene.Name, story.Timeblock.ToString()),
                        console,
                        strings.Score(story.Score, api.Scores.Maximum),
                        [.. showing.Actions
                            .Where(a => IsAnItem(a.LocalizedVerb, scene.Actions?.Verbs))
                            .Select(a => a.LocalizedVerb)],
                        window.IsHeld(Platform.CameraAction.ShowHotspots)
                            ? OnScreen(
                                interaction.Nouns(),
                                view,
                                window.FramebufferWidth,
                                window.FramebufferHeight)
                            : null,
                        icons,
                        verbIcons,
                        (api.Mechanism as Game.Mechanisms.CoordinateDevice)?.Reading(),
                        artwork,
                        Game.Radio.WornAt(story.Timeblock),
                        topics,
                        radioOpen,
                        radioIndex,
                        api.Mechanism?.Offers),
                    window.FramebufferWidth,
                    window.FramebufferHeight);

                renderer.SetOverlay(hud.Overlay);
            }

            // A door is a script that says SetLocation and nothing more. Noticing it here
            // rather than inside the action means it works however the story asked —
            // clicked, on a timer, or from a script three calls deep.
            if (api.Leaning is null &&
                !string.Equals(story.Location, here, StringComparison.OrdinalIgnoreCase) &&
                story.Location is { Length: > 0 } elsewhere)
            {
                Log.Info($"Leaving {here} for {elsewhere}");

                // Where they stood and what they saw, in case the room they are going to
                // is one the game never had. Kept here because the camera is here; whether
                // it is wanted is decided by the room loop, which knows the archives. Not
                // while one is already kept: that one is the way back, and this door is
                // the way back through it. See Gk3SheepApi.Returning.
                if (api.Returning is null && !Looking(api) && update.Where(story.Ego) is { } stood)
                {
                    api.Returning = new Game.ReturnSpot(
                        here,
                        elsewhere,
                        stood,
                        update.SettledFacing(story.Ego) ?? 0f,
                        camera.Position,
                        camera.Aim);
                }

                // Nothing this room was still holding back gets to happen in the next one.
                // What is queued is an action script belonging to the room being left, and
                // letting one run through a door is how it opens twice.
                update.Cancel();

                return new RoomExit(0, elsewhere);
            }

            window.EndFrame();

            BlendAir(view, delta);

            renderer.SetScene(geometry, view);

            // The other half of the transition, one frame at a time. A no-op once the
            // fade is up, and on every frame of a room nobody faded into.
            fade.Advance();

            if (renderer.DrawFrame(0f, 0f, 0f))
            {
                presented++;
            }

            // What Direct3D thought of that frame. Said once, after the first frame that
            // presented, because the debug layer repeats itself every frame and one copy of
            // a complaint is what a reader needs. Nothing is said when there is nothing to
            // say, and nothing at all on a backend that has no such queue.
            if (presented == 1 && renderer is Rendering.Direct3D12.D3D12Renderer direct3d)
            {
                foreach (string message in direct3d.Messages)
                {
                    Log.Warning("d3d: " + message);
                }
            }

            // How much the picture changes from one frame to the next, over frames where
            // the room itself is doing nothing. Anything a temporal filter gets wrong
            // shows here and nowhere else: a still picture that is quietly different every
            // frame is what the eye reads as a flicker, and no single screenshot of it
            // looks wrong.
            if (flicker && presented > 4)
            {
                if (renderer.Capture() is { } captured)
                {
                    byte[] frame = captured.Pixels;

                    if (previousFrame is { Length: > 0 } && previousFrame.Length == frame.Length)
                    {
                        long total = 0;

                        for (int i = 0; i < frame.Length; i++)
                        {
                            total += Math.Abs(frame[i] - previousFrame[i]);
                        }

                        flickerTotal += (double)total / frame.Length;
                        flickerFrames++;

                        // The last pair, as a picture. A number says how much moved; only
                        // this says what.
                        var picture = new byte[frame.Length / 4];

                        for (int i = 0; i < picture.Length; i++)
                        {
                            int at = i * 4;
                            int most = Math.Max(
                                Math.Abs(frame[at] - previousFrame[at]),
                                Math.Max(
                                    Math.Abs(frame[at + 1] - previousFrame[at + 1]),
                                    Math.Abs(frame[at + 2] - previousFrame[at + 2])));

                            picture[i] = (byte)Math.Min(255, most * 12);
                        }

                        File.WriteAllBytes("flicker.raw", picture);
                    }

                    previousFrame = frame;
                }
            }
        }

        if (flickerFrames > 0)
        {
            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Flicker: {flickerTotal / flickerFrames:F3} of an eight-bit step between " +
                $"frames, over {flickerFrames} frames"));
        }

        Log.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Presented {presented} frames in {stopwatch.Elapsed.TotalSeconds:F1}s "
            + $"({presented / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0} fps)"));

        return new RoomExit(0, null);
    }

    /// <summary>The game's own title screen.</summary>
    private const string TitlePicture = "TITLE.BMP";

    /// <summary>The music under the menu.</summary>
    private const string ThemeMusic = "THEME.WAV";

    /// <summary>How long the process has been running.</summary>
    private static readonly Stopwatch Since = Stopwatch.StartNew();

    /// <summary>Finds the title art.</summary>
    /// <param name="archives">The game's own.</param>
    /// <param name="enhanced">A higher-resolution set, or null.</param>
    /// <param name="compressed">The block-compressed set, packs included, or null.</param>
    /// <param name="diagnostics">Where a picture that will not decode is reported.</param>
    /// <returns>The picture and where it came from; empty when there is none to be had.</returns>
    private static TitleScreen TitleArt(
        GameArchives archives,
        EnhancedTextures? enhanced,
        CompressedTextures? compressed,
        DiagnosticBag diagnostics) =>
        Art(archives, enhanced, compressed, diagnostics, TitlePicture);

    /// <summary>
    /// Reads one of the game's full-screen pictures, from wherever it is to be had.
    /// </summary>
    /// <param name="archives">The game's own barns.</param>
    /// <param name="enhanced">A directory of upscaled pictures, if there is one.</param>
    /// <param name="compressed">The block-compressed build or a pack, if there is one.</param>
    /// <param name="diagnostics">Where a picture that will not decode is reported.</param>
    /// <param name="file">Its file name, with the extension.</param>
    /// <returns>The picture, or nothing when no source has it.</returns>
    private static TitleScreen Art(
        GameArchives archives,
        EnhancedTextures? enhanced,
        CompressedTextures? compressed,
        DiagnosticBag diagnostics,
        string file)
    {
        string bare = Path.GetFileNameWithoutExtension(file);

        if (enhanced?.Read(bare, diagnostics) is { } better)
        {
            return new TitleScreen(better, null, $"from {enhanced.Directory}");
        }

        if (compressed?.Read(bare, diagnostics) is { } blocks)
        {
            // A pack is what a shipped game has and is opened with no directory at all,
            // which is how the two are told apart without asking the pack.
            return new TitleScreen(
                null,
                blocks,
                compressed.Directory.Length > 0
                    ? $"from {compressed.Directory}"
                    : "from a pack");
        }

        try
        {
            return archives.Read(file) is { } bytes
                ? new TitleScreen(
                    Formats.Bitmaps.BitmapDecoder.Decode(bytes, file),
                    null,
                    "from the archives")
                : default;
        }
        catch (FormatException error)
        {
            // A menu without its picture is a menu; a game that will not start because a
            // decorative bitmap is malformed is not.
            Log.Warning($"WARNING GK3R3430: {file} would not decode. ({error.Message})");
            return default;
        }
    }

    /// <summary>How long the card stands there on its own, in seconds.</summary>
    private const double CardSeconds = 4.0;

    /// <summary>How long the lettering is left standing once it has finished typing.</summary>
    private const double CardHeldSeconds = 2.8;

    /// <summary>How long the ticking clock takes to go quiet at the end, in seconds.</summary>
    private const double CardFadeSeconds = 0.6;

    /// <summary>The ticking the original runs under its timeblock card.</summary>
    private const string TimeblockClock = "CLOCKTIMEBLOCK.WAV";

    /// <summary>
    /// Says that the story has moved on to another part of the day.
    /// </summary>
    /// <param name="window">The window, for the click or key that ends it.</param>
    /// <param name="renderer">What draws the picture and the words.</param>
    /// <param name="pages">The menu's own typeface, which is the big one.</param>
    /// <param name="strings">What the game calls this part of the day.</param>
    /// <param name="now">Where the clock has got to.</param>
    /// <param name="art">The painting for it, or nothing.</param>
    /// <param name="card">The lettering that types itself, or null when it cannot be had.</param>
    /// <param name="audio">The device, or null when there is none.</param>
    /// <param name="sounds">Where the ticking clock comes from.</param>
    private static void Announce(
        Platform.SilkGameWindow window,
        Rendering.IRenderer renderer,
        MenuPage? pages,
        GameStrings strings,
        Timeblock now,
        TitleScreen art,
        Game.TimeblockCard? card,
        Audio.OpenAlBackend? audio,
        SoundLibrary sounds)
    {
        art.Show(renderer);

        string name = strings.When(now.ToString()) is { Length: > 0 } called
            ? called
            : now.ToString();

        // The frames go on the device once. They are small — the widest is 433 by 69 — and
        // there are never more than eighteen of them. Nothing to place them against means
        // nothing to draw: the lettering belongs at a spot on the painting, and without the
        // painting, or without a page to draw it on, there is no spot.
        Game.TimeblockCard? typed = art.Exists && pages is not null ? card : null;

        int[] lettering = typed is null ? [] : Lettering(renderer, typed);

        if (lettering.Length == 0)
        {
            typed = null;
        }

        double typing = typed?.Seconds ?? 0;
        double stays = typed is not null ? typing + CardHeldSeconds : CardSeconds;

        string behind = art.Exists
            ? $", over {art.Width}x{art.Height} of painting"
            : ", with no painting";

        string written = typed is not null
            ? string.Create(
                CultureInfo.InvariantCulture,
                $", typed in {lettering.Length} frames over {typing:F1}s")
            : ", named in the port's own face";

        Log.Info($"Card: {name}{behind}{written}");

        Audio.AudioVoice ticking = audio is not null && sounds.Read(TimeblockClock) is { } clockwork
            ? audio.Play(clockwork, Audio.AudioBus.Effects)
            : Audio.AudioVoice.None;

        var clock = Stopwatch.StartNew();

        // A press that is still down from before does not count: the click that walked
        // through the door is what brought the player here.
        window.Forget();

        while (!window.IsClosing && clock.Elapsed.TotalSeconds < stays)
        {
            window.PumpEvents();

            if (window.WasClicked(Platform.PointerButton.Primary) ||
                window.WasPressed(Platform.EditKey.Enter) ||
                window.WasPressed(Platform.EditKey.Escape))
            {
                break;
            }

            double elapsed = clock.Elapsed.TotalSeconds;

            // Against the painting rather than against the window. Where the painting went
            // is the renderer's answer, because covering it crops it and only the renderer
            // knows by how much.
            if (typed is not null &&
                renderer.PictureRect(window.FramebufferWidth, window.FramebufferHeight)
                    is { Z: > 0, W: > 0 } painting)
            {
                pages!.Announcing(
                    lettering[typed.At(elapsed)],
                    typed.Over(painting),
                    window.FramebufferWidth,
                    window.FramebufferHeight);
            }
            else
            {
                pages?.Announcing(name, window.FramebufferWidth, window.FramebufferHeight);
            }

            renderer.SetOverlay(pages?.Overlay);

            // The clock goes quiet as the card does. It is the only sound there is at this
            // point, so cutting it mid-tick is heard as the game stalling rather than as
            // the story moving on.
            if (audio is not null && stays - elapsed < CardFadeSeconds)
            {
                audio.SetVoiceGain(
                    ticking, (float)Math.Clamp((stays - elapsed) / CardFadeSeconds, 0, 1));
            }

            window.EndFrame();
            renderer.SetScene(null, null);
            renderer.DrawFrame(0f, 0f, 0f);

        }

        audio?.Silence(ticking);

        for (int i = 0; typed is not null && i < lettering.Length; i++)
        {
            renderer.DropOverlayPicture(LetteringName(typed.Timeblock, i));
        }

        window.Forget();
        renderer.SetOverlay(null);
        renderer.SetBackdrop(null);
    }

    /// <summary>Puts a card's frames on the device.</summary>
    /// <param name="renderer">What holds them.</param>
    /// <param name="card">The lettering.</param>
    /// <returns>A picture number per frame, or nothing when the device refused one.</returns>
    private static int[] Lettering(Rendering.IRenderer renderer, Game.TimeblockCard card)
    {
        var numbers = new int[card.Frames.Count];

        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = renderer.AddOverlayPicture(
                LetteringName(card.Timeblock, i), card.Frames[i]);

            if (numbers[i] > 0)
            {
                continue;
            }

            for (int drop = 0; drop < i; drop++)
            {
                renderer.DropOverlayPicture(LetteringName(card.Timeblock, drop));
            }

            Log.Warning(
                "WARNING GK3R3458: the card's lettering would not go on the device, so the "
                + "name is written out instead.");

            return [];
        }

        return numbers;
    }

    /// <summary>What one frame of lettering is called on the device.</summary>
    /// <param name="timeblock">Which card it belongs to.</param>
    /// <param name="frame">Which frame.</param>
    /// <returns>The name.</returns>
    private static string LetteringName(string timeblock, int frame) =>
        string.Create(CultureInfo.InvariantCulture, $"card:{timeblock}:{frame:00}");

    /// <summary>The picture behind the menu, in whichever form it was found.</summary>
    /// <param name="Picture">Pixels, from a loose file or the archives.</param>
    /// <param name="Blocks">Or block-compressed, from the compressed build or a pack.</param>
    /// <param name="From">Where it came from, for the report.</param>
    private readonly record struct TitleScreen(
        Formats.Bitmaps.DecodedImage? Picture, Formats.Bitmaps.CompressedImage? Blocks, string From)
    {
        /// <summary>Whether there is a picture at all.</summary>
        public bool Exists => Picture is not null || Blocks is not null;

        /// <summary>How wide it is.</summary>
        public int Width => Picture?.Width ?? Blocks?.Width ?? 0;

        /// <summary>How tall it is.</summary>
        public int Height => Picture?.Height ?? Blocks?.Height ?? 0;

        /// <summary>Puts it behind the menu.</summary>
        /// <param name="renderer">What draws it.</param>
        public void Show(Rendering.IRenderer renderer)
        {
            ArgumentNullException.ThrowIfNull(renderer);

            if (Blocks is { } blocks)
            {
                renderer.SetBackdrop(blocks);
            }
            else
            {
                renderer.SetBackdrop(Picture);
            }
        }
    }

    /// <summary>Starts the theme under the menu.</summary>
    /// <param name="audio">The device, or null when there is none.</param>
    /// <param name="sounds">Where sounds come from.</param>
    /// <returns>The voice, so it can be stopped again.</returns>
    private static Audio.AudioVoice Theme(Audio.OpenAlBackend? audio, SoundLibrary sounds)
    {
        if (audio is null || sounds.Read(ThemeMusic) is not { } music)
        {
            return Audio.AudioVoice.None;
        }

        // On the music bus, so the music slider is the thing that turns it down.
        return audio.Play(music, Audio.AudioBus.Music, repeat: true);
    }

    /// <summary>
    /// Shows the menu until the player leaves it.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="renderer">What draws it.</param>
    /// <param name="pages">The drawn page.</param>
    /// <param name="front">What the pages hold and what choosing a row does.</param>
    /// <param name="apply">What to do with a setting the moment it changes.</param>
    /// <param name="behind">What is behind it, and so what it has to draw itself.</param>
    /// <param name="cut">Cuts a fresh sheet of letters when the window changes size.</param>
    /// <param name="frames">Leave after this many frames, or zero to wait for the player.</param>
    /// <param name="photograph">Where to write the last frame, if anywhere.</param>
    /// <param name="scene">
    /// The port's own title screen, drawn under the page, or null when the menu is over the
    /// 1999 picture or over the room.
    /// </param>
    /// <returns>What the player asked for.</returns>
    private static FrontEndOutcome ShowMenu(
        Platform.SilkGameWindow window,
        Rendering.IRenderer renderer,
        MenuPage pages,
        FrontEnd front,
        Action<Settings> apply,
        MenuBehind behind,
        Func<OverlayAtlas?> cut,
        int frames = 0,
        string? photograph = null,
        UI.TitleScene? scene = null)
    {
        FrontEndPage showing = front.Page;
        int laidOutFor = window.FramebufferHeight;
        float laidOutAt = front.Settings.TextScale;

        // The menu owns the screen while it is up, so nothing left over from a transition
        // gets to darken it: pausing on the frame a room was still fading in used to open
        // a menu somewhere between grey and black. The room's loop puts the fade back where
        // it belongs when it comes round again.
        renderer.Fade = 0f;

        pages.Behind = behind;

        // Into the page's own display list rather than behind it, so that the statue and
        // the rows over it are one frame. Null on every other menu there is.
        pages.Backdrop = scene is null ? null : scene.Draw;

        Place(pages, front, behind);
        pages.Reset(front.Items);

        int drawn = 0;

        // Which slider row a held pointer grabbed, and whether it was held last frame. A
        // drag has to have grabbed something, and what it grabbed is decided once.
        int grabbed = -1;
        bool heldLast = false;

        // How long the last frame took, which is all the page needs to slide rather than
        // jump. Read here rather than by the page itself, because reading the clock outside
        // the platform layer is what ADR 0004 forbids and a menu page is not the platform
        // layer.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        double previous = 0;

        while (!window.IsClosing)
        {
            window.PumpEvents();

            double now = elapsed.Elapsed.TotalSeconds;
            float seconds = (float)Math.Clamp(now - previous, 0, 0.1);
            previous = now;

            // The one thing on this screen that is not waiting for the player. Advanced
            // here rather than inside Draw, because Draw is called once a frame by the page
            // and would be called twice by anything that laid the page out to measure it.
            scene?.Advance(seconds);

            // What the picture pages need to be able to say, refreshed every frame because
            // every one of them can change while they are on screen: the window is
            // resizable, the upscaler is rebuilt at the top of a frame, and whether the
            // display took the HDR colour space is only known once the swapchain exists.
            front.Runtimes = renderer.Runtimes;
            front.Window = renderer.SwapchainSize;
            front.HighDynamicRangeActive = renderer.HighDynamicRangeActive;
            front.UpscalerRunning = renderer.UpscalerName;
            front.Offered = renderer.OfferedUpscalers;
            front.DlssAvailable = renderer.DlssAvailable;
            front.DlssRayReconstruction = renderer.DlssRayReconstruction;
            front.DlssRayReconstructionNote = renderer.DlssRayReconstructionNote;
            front.DlssFrameGeneration = renderer.DlssFrameGeneration;
            front.FrameGenerationMaximum = renderer.FrameGenerationMaximum;
            front.LatencyControl = renderer.LatencyControl;
            front.RunningBackend = renderer.Backend;
            front.HasGamepad = window.HasGamepad;

            // The list down the side, and which of it is highlighted. Only on the settings
            // screen: the title screen, the pause menu and the save slots are each a single
            // list and a sidebar over one list is a margin.
            pages.Sections = front.OnSettings ? Aside(front) : [];
            pages.Section = front.Section;

            IReadOnlyList<MenuItem> items = front.Items;

            // A window that goes fullscreen doubles in height, and a menu that stayed the
            // size it was laid out at would be a postage stamp in the middle of it. An
            // outline is re-cut for the new size; a sheet is magnified to reach it.
            //
            // And for the text size, which is the one row on these pages the player can
            // watch working on the page they are dragging it on.
            if (window.FramebufferHeight != laidOutFor ||
                front.Settings.TextScale != laidOutAt)
            {
                laidOutFor = window.FramebufferHeight;
                laidOutAt = front.Settings.TextScale;

                if (pages.Overlay.Atlas.Scalable && cut() is { } again)
                {
                    pages.Retarget(again);
                }
            }

            pages.Overlay.Magnify = pages.Overlay.Atlas.Scalable
                ? 1
                : UI.TextSizing.MenuMagnification(
                    window.FramebufferHeight, pages.Overlay.Atlas.Height, laidOutAt);

            Vector2 pointer = new(
                window.PointerPosition.X * window.DpiScale,
                window.PointerPosition.Y * window.DpiScale);

            MenuAction action = MenuAction.None;

            // A row on the Controls page that is waiting to be told what to answer to takes
            // the whole keyboard and the whole pad, because the answer may be any key on
            // either — including the arrows, which would otherwise be walking the list.
            if (front.Listening)
            {
                if (front.Captured(
                        window.AnyKey,
                        window.AnyButton,
                        window.WasPressed(Platform.EditKey.Backspace)))
                {
                    apply(front.Settings);
                }

                pages.Build(
                    front.Title,
                    front.Items,
                    window.FramebufferWidth,
                    window.FramebufferHeight,
                    pointer,
                    seconds);

                renderer.SetOverlay(pages.Overlay);
                window.EndFrame();
                renderer.DrawFrame(0f, 0f, 0f);

                continue;
            }

            if (window.WasPressed(Platform.EditKey.Up))
            {
                pages.Move(items, -1);
            }

            if (window.WasPressed(Platform.EditKey.Down))
            {
                pages.Move(items, 1);
            }

            // On a line of buttons across the window, left and right are what moves along
            // it. Everywhere else they step the value of the row the player is on, and a
            // key that walked the list on one page and changed the volume on another is a
            // key nobody can use -- which is why this asks the page how it is laid out
            // rather than which page it is.
            if (window.WasPressed(Platform.EditKey.Left))
            {
                if (pages.Horizontal)
                {
                    pages.Move(items, -1);
                }
                else
                {
                    action = pages.Chose(items, -1);
                }
            }

            if (window.WasPressed(Platform.EditKey.Right))
            {
                if (pages.Horizontal)
                {
                    pages.Move(items, 1);
                }
                else
                {
                    action = pages.Chose(items, 1);
                }
            }

            if (window.WasPressed(Platform.EditKey.Enter))
            {
                action = pages.Chose(items);
            }

            // Page Up and Page Down, and the shoulder buttons on a pad. Not the arrows:
            // those step the value of the row the player is on, and a key that changed the
            // volume on one row and the whole section on another is a key nobody can use.
            if (window.WasPressed(Platform.EditKey.PreviousSection))
            {
                front.StepSection(-1);
            }

            if (window.WasPressed(Platform.EditKey.NextSection))
            {
                front.StepSection(1);
            }

            // The wheel scrolls the page rather than stepping the selection. Turning it to
            // see what is further down a settings section should not change what pressing
            // Enter would do.
            if (window.ScrollDelta != 0)
            {
                pages.Wheel(window.ScrollDelta);
            }

            // What a drag grabbed, decided on the edge of the press and held until the
            // button comes up. Without it a drag moved whichever row the pointer had last
            // hovered, so pressing a tab in the sidebar with a volume row under the pointer
            // set that volume to nought on the way to the other page.
            bool holding = window.IsHeld(Platform.PointerButton.Primary);

            if (holding && !heldLast)
            {
                grabbed = pages.Grabbed(pointer, items);
            }
            else if (!holding)
            {
                grabbed = -1;
            }

            heldLast = holding;

            if (window.WasClicked(Platform.PointerButton.Primary))
            {
                action = pages.Click(pointer, items);

                // A click on no row of the first page goes to the title itself, whose
                // letters can be clicked. Only the first page: the settings pages are
                // panels drawn over the same picture, and a click beside a panel is a
                // missed click rather than a letter.
                if (!action.Happened && front.Page == FrontEndPage.Main)
                {
                    scene?.Click(pointer, window.FramebufferWidth, window.FramebufferHeight);
                }
            }
            else if (window.IsDragging &&
                pages.Drag(pointer, items, grabbed) is { Happened: true } dragged)
            {
                // Held rather than clicked: a volume is set by ear, which means hearing it
                // move rather than hearing where it landed.
                action = dragged;
            }

            // The one row the page draws that is not the front end's: the way out of the
            // settings, which the sidebar carries so that somebody using nothing but the
            // pointer has one.
            if (action.Id == "tab:back")
            {
                action = new MenuAction("back");
            }

            FrontEndOutcome outcome = front.Choose(action);

            if (action.Happened)
            {
                apply(front.Settings);
            }

            if (window.WasPressed(Platform.EditKey.Escape))
            {
                // Out of a settings page to the one before it, and out of the top of the
                // menu only when there is a room to go back to. From the first menu of all
                // it does nothing: leaving the game is a row somebody has to choose.
                if (!front.Back() && front.InGame)
                {
                    outcome = FrontEndOutcome.Resume;
                }
            }

            if (front.Page != showing)
            {
                showing = front.Page;

                Place(pages, front, behind);
                pages.Reset(front.Items);
            }

            if (outcome != FrontEndOutcome.Stay)
            {
                // On the way out rather than on every keystroke: dragging a volume slider
                // across a page is a hundred changes and none of them is worth a write.
                if (front.Commit())
                {
                    Log.Info($"Settings: written to {front.StoredAt ?? Settings.DefaultPath}");
                }

                // The click that chose Play is still on the frame's books, and this is the
                // one path out of the loop that does not reach the EndFrame at the bottom
                // of it. Without this the room reads the same click on its first frame and
                // acts on whatever the pointer happens to be over — which is how pressing
                // Play sent Gabriel to the wardrobe, the Play row and the wardrobe being
                // at the same place on the screen.
                window.EndFrame();

                return outcome;
            }

            pages.Build(
                front.Title,
                front.Items,
                window.FramebufferWidth,
                window.FramebufferHeight,
                pointer,
                seconds);

            renderer.SetOverlay(pages.Overlay);

            // The party, once there is one, is a scene the renderer draws under the page:
            // the title screen's own list has no black in it from then on.
            if (scene?.Party is { } party)
            {
                if (party.LightsMoved)
                {
                    renderer.SetLights(party.Lights, party.Extent);
                }

                renderer.SetScene(party.Geometry, party.Camera);
            }

            window.EndFrame();

            if (renderer.DrawFrame(0f, 0f, 0f))
            {
                drawn++;
            }

            // --frames, which is how the menu is photographed: a run with no keyboard would
            // otherwise sit on the first page until somebody closed the window.
            if (frames > 0 && drawn >= frames)
            {
                if (photograph is { Length: > 0 } && renderer.Capture() is { } picture)
                {
                    File.WriteAllBytes(
                        photograph, Formats.Bitmaps.PngWriter.Encode(picture));

                    Log.Info($"Wrote {photograph}");
                }

                return FrontEndOutcome.Quit;
            }
        }

        front.Commit();
        return FrontEndOutcome.Quit;
    }

    /// <summary>
    /// The settings screen's sections, and the way out under them.
    /// </summary>
    /// <param name="front">The front end, for what its sections are called.</param>
    /// <returns>The sections and the way out.</returns>
    private static MenuSection[] Aside(FrontEnd front) =>
        [.. front.Tabs, new MenuSection("back", front.Text.Say("menu.back", "Back"))];

    /// <summary>
    /// Puts the page where it does not cover what is behind it.
    /// </summary>
    /// <param name="pages">The page.</param>
    /// <param name="front">Which page is showing.</param>
    /// <param name="behind">What is behind it.</param>
    private static void Place(MenuPage pages, FrontEnd front, MenuBehind behind)
    {
        // The port's own screen, on its first page: one line of buttons in the black under
        // the wall, and the statue standing behind them. Every other page of it is an
        // ordinary panel drawn over the same picture.
        pages.Horizontal =
            behind == MenuBehind.Modern && front.Page == FrontEndPage.Main;

        if (pages.Horizontal)
        {
            pages.Down = 0.905f;
            pages.Across = 0.5f;

            return;
        }

        bool overArt = behind is MenuBehind.Picture or MenuBehind.Modern &&
            front.Page == FrontEndPage.Main;

        pages.Down = overArt ? 0.72f : 0.5f;
        pages.Across = overArt ? 0.17f : 0.5f;
    }

    /// <summary>
    /// Plays the films the game opens with.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="renderer">What draws them.</param>
    /// <param name="movies">What plays them.</param>
    /// <param name="hint">What draws the way out, or null when there is no font.</param>
    /// <param name="films">Which films, in order.</param>
    /// <param name="captioned">Whether to write out what is said in them.</param>
    private static void ShowIntro(
        Platform.SilkGameWindow window,
        Rendering.IRenderer renderer,
        Game.MoviePlayer movies,
        MenuPage? hint,
        IReadOnlyList<string> films,
        bool captioned)
    {
        var stopwatch = Stopwatch.StartNew();
        double held = 0;

        // Whether the button that skipped the last film is still down. Until it comes up
        // again it means nothing, or one long press would clear the whole sequence.
        bool spent = false;

        foreach (string name in films)
        {
            if (movies.Play(name) <= 0)
            {
                continue;
            }

            Log.Info(string.Create(
                CultureInfo.InvariantCulture, $"Intro: {name}, {movies.Seconds:F1}s"));

            bool skipped = Watch(
                window, renderer, movies, hint, stopwatch, ref held, ref spent, SayForFilm,
                captioned);

            if (window.IsClosing)
            {
                return;
            }

            if (skipped)
            {
                // Said, but not obeyed for the rest of them: the next film is a different
                // thing to have decided about.
                Log.Info($"Intro: {name} skipped");
            }
        }
    }

    /// <summary>How long a press has to be held to skip a film.</summary>
    private const double HoldToSkipFilm = 0.6;

    /// <summary>How long the way out stays on screen at the start of a film.</summary>
    private const double SayForFilm = 6.0;

    /// <summary>
    /// Watches a film that has already been started, until it ends or the player stops it.
    /// </summary>
    /// <param name="window">The window, which is where the keyboard and the frames are.</param>
    /// <param name="renderer">What draws it.</param>
    /// <param name="movies">The player, with a film already playing.</param>
    /// <param name="hint">Where to say how to skip, or null to say nothing.</param>
    /// <param name="stopwatch">A clock that is already running.</param>
    /// <param name="held">
    /// How long the skip has been held for. Carried in and out so that one long press
    /// cannot clear a whole sequence of films.
    /// </param>
    /// <param name="spent">Whether the press that skipped the last film is still down.</param>
    /// <param name="sayFor">How long the way out stays on screen at the start.</param>
    /// <param name="captioned">
    /// Whether to write out what is said in the film — <see cref="Settings.MovieSubtitles"/>
    /// rather than the row that governs the room's captions. Fourteen of the films carry
    /// their own subtitles, translated in every release, and a language that never dubbed its
    /// cutscenes has nothing else.
    /// </param>
    /// <returns>True when the player stopped it early.</returns>
    private static bool Watch(
        Platform.SilkGameWindow window,
        Rendering.IRenderer renderer,
        Game.MoviePlayer movies,
        MenuPage? hint,
        Stopwatch stopwatch,
        ref double held,
        ref bool spent,
        double sayFor,
        bool captioned)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(movies);
        ArgumentNullException.ThrowIfNull(stopwatch);

        double began = stopwatch.Elapsed.TotalSeconds;
        double previous = began;
        bool skipped = false;

        while (!window.IsClosing && movies.Playing)
        {
            window.PumpEvents();

            double now = stopwatch.Elapsed.TotalSeconds;
            double delta = Math.Min(0.1, now - previous);
            previous = now;

            bool down = window.IsHeld(Platform.PointerButton.Primary);

            if (!down)
            {
                spent = false;
            }

            held = down && !spent ? held + delta : 0;

            if (window.WasPressed(Platform.EditKey.Escape) ||
                window.WasPressed(Platform.EditKey.Enter) ||
                held >= HoldToSkipFilm)
            {
                movies.Stop();

                skipped = true;
                spent = down;
                held = 0;
            }
            else
            {
                movies.Advance(delta);
            }

            // The subtitle and the skip hint go through one call, because Overlay.Begin
            // throws away what was there and two calls would show whichever went second.
            bool saying = captioned && movies.Caption is { Length: > 0 };
            bool skipping = held > 0 || now - began < sayFor;

            if (hint is not null && (saying || skipping))
            {
                hint.Film(
                    saying ? movies.Caption : null,
                    saying ? movies.Speaker : null,
                    skipping
                        ? hint.Text.Say(
                            "film.skip", "Hold the mouse button or press Enter to skip")
                        : null,
                    (float)(held / HoldToSkipFilm),
                    window.FramebufferWidth,
                    window.FramebufferHeight);

                renderer.SetOverlay(hint.Overlay);
            }
            else
            {
                renderer.SetOverlay(null);
            }

            renderer.SetMovieFrame(movies.Frame);

            window.EndFrame();
            renderer.DrawFrame(0f, 0f, 0f);
        }

        renderer.SetMovieFrame(null);
        renderer.SetOverlay(null);

        foreach (Diagnostic diagnostic in movies.Diagnostics.Items)
        {
            Log.Report(diagnostic);
        }

        return skipped;
    }

    /// <summary>
    /// An interface picture out of the packs, expanded to something the overlay can draw.
    /// </summary>
    /// <param name="packs">The ReBarn volumes, which may hold none.</param>
    /// <param name="localized">The chosen language's pack, or null when there is none.</param>
    /// <param name="name">The texture's name, as the enhanced set keys it.</param>
    /// <returns>The picture, or null when no pack has it.</returns>
    private static Formats.Bitmaps.DecodedImage? Packed(
        Content.RebarnContent packs, Content.LocalizedContent? localized, string name)
    {
        ArgumentNullException.ThrowIfNull(packs);

        Formats.Bitmaps.CompressedImage? blocks =
            localized?.ReadTexture(Formats.Rebarn.RebarnKind.Texture, name)
            ?? packs.ReadTexture(Formats.Rebarn.RebarnKind.Texture, name);

        return blocks is { } found ? Formats.Bitmaps.BlockDecoder.Decode(found) : null;
    }

    /// <summary>
    /// Hands the driving map's own pictures to the interface.
    /// </summary>
    /// <param name="archives">The game's data, which is where the art is.</param>
    /// <param name="renderer">What holds the pictures.</param>
    /// <param name="screens">What draws them, and needs to know how big each one is.</param>
    /// <param name="enhanced">
    /// Asks for the upscaled form of a picture, from the loose set or the packs, and
    /// answers null where there is none -- in which case the archive's own is drawn.
    /// </param>
    private static void LoadMapArt(
        GameArchives archives,
        Rendering.IRenderer renderer,
        ScreenPainter screens,
        Func<string, Formats.Bitmaps.DecodedImage?> enhanced)
    {
        int loaded = 0;
        int upscaled = 0;

        foreach (string key in new[] { DrivingMap.Background }
                     .Concat(DrivingMap.All.Select(s => s.Sprite.ToUpperInvariant())))
        {
            if (archives.Read(key + ".BMP") is not { } bytes)
            {
                continue;
            }

            try
            {
                Formats.Bitmaps.DecodedImage original =
                    Formats.Bitmaps.BitmapDecoder.Decode(bytes, key);

                Formats.Bitmaps.DecodedImage? better = enhanced(key);

                if (better is not null)
                {
                    upscaled++;
                }

                if (renderer.AddOverlayPicture(key, better ?? original) > 0)
                {
                    screens.Sizes[key] = (original.Width, original.Height);
                    loaded++;
                }
            }
            catch (Formats.FormatParseException)
            {
                // A picture the archives do not have or cannot decode is a place that will
                // not be on the map. Worth nothing more than the count below.
            }
        }

        if (loaded > 0)
        {
            Log.Info(
                $"Driving map: {loaded} of {DrivingMap.All.Count + 1} pictures" +
                (upscaled > 0 ? $", {upscaled} enhanced" : string.Empty));
        }
    }

    /// <summary>
    /// What a click on one of the screens in front of the room means.
    /// </summary>
    /// <param name="chose">The painter's identifier for what was clicked.</param>
    /// <param name="story">The game.</param>
    /// <param name="sidney">Grace's computer.</param>
    /// <param name="update">The room, for anything that has to happen in it.</param>
    /// <param name="console">Where a screen says what it did.</param>
    /// <param name="scan">How to put an item into Sidney, which needs the room's rules.</param>
    private static void OnScreen(
        string chose,
        GameState story,
        Game.Sidney.SidneyMachine? sidney,
        SceneUpdate update,
        GameConsole console,
        Action<string>? scan)
    {
        ArgumentNullException.ThrowIfNull(update);

        string[] parts = chose.Split(':');

        switch (parts[0])
        {
            case "close":
                story.Screens.Back();
                break;

            // A verse of Le Serpent Rouge: the close-up becomes the verse's, so that the
            // verbs along the foot are the verse's own — READ, THINK, the turn of the page.
            case "verse" when parts.Length > 1 && Game.SerpentRouge.VerseOf(parts[1]) is { } verse:
                story.Screens.Replace(new Screen(ScreenKind.InventoryInspect, verse.Noun));
                break;

            // Putting the hose down. Whatever the puzzle wants to say about it is said by
            // the interface's own EXIT rule when the room performs it; what matters here is
            // that there is a way out at all.
            case "water:away":
                story.Screens.Back();
                break;

            // Click to hold, click again to look at it closely — which is the whole of the
            // inventory's interaction and the reason it does not need a verb menu of its
            // own.
            // Sidney is a thing in Grace's bag, so opening it is picking it up. The story
            // opens it too — ShowSidney — and both arrive at the same screen.
            case "item" when parts.Length > 1 &&
                             parts[1].StartsWith("SIDNEY", StringComparison.OrdinalIgnoreCase):
                story.Screens.Show(new Screen(ScreenKind.Sidney));
                break;

            case "item" when parts.Length > 1:
                if (string.Equals(
                        story.Inventory.ActiveItemOf(story.Ego), parts[1], StringComparison.OrdinalIgnoreCase))
                {
                    story.Screens.Show(new Screen(ScreenKind.InventoryInspect, parts[1]));
                }
                else
                {
                    story.Inventory.SetActive(story.Ego, parts[1]);
                }

                break;

            // Riding the moped, which is arriving from the map rather than from the room
            // the player left: scene files and scene scripts both ask which it was, and the
            // moped standing in the yard when they get there is one of the answers.
            // Watched down the roads first, the way a chase is; the arrival is the frame
            // loop's, when the marker gets there. See DrivingTraffic.For.
            case "drive" when parts.Length > 1:
                story.Screens.Replace(new Screen(ScreenKind.Driving, "ride:" + parts[1]));
                break;

            // Split into three at most, because what a command is *about* may itself carry
            // a colon and only the first two fields are the command.
            //
            // The subject is optional: SEARCH, MATCH PRINT and the power button are whole
            // commands on their own. Requiring one silently dropped every button that had
            // none — the search screen did nothing at all when its own button was clicked,
            // and the print match the fingerprint puzzle ends on did nothing either.
            case "sidney" when sidney is not null && parts.Length > 1:
                OnSidney(
                    parts[1],
                    chose.Split(':', 3) is [_, _, string about] ? about : string.Empty,
                    story,
                    sidney,
                    console,
                    scan);

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// What can be done to the item a close-up is showing.
    /// </summary>
    /// <param name="panel">The screen on top.</param>
    /// <param name="scene">The room, which is where the action files are.</param>
    /// <param name="story">The game, for who the player is and what they carry.</param>
    /// <returns>The verbs, or null when the screen is not about an item.</returns>
    private static IReadOnlyList<string>? ItemVerbs(
        Screen panel, LoadedScene scene, GameState story)
    {
        if (panel.Kind is not (ScreenKind.InventoryInspect or ScreenKind.Inventory) ||
            panel.Subject is not { Length: > 0 } item ||
            scene.Actions is not { } actions)
        {
            return null;
        }

        return [.. actions
            .Resolve(item, story.Ego, story.Inventory.ItemsOf(story.Ego))
            .Select(a => a.LocalizedVerb)
            .Where(v => !IsAboutTheRoom(v))];
    }

    /// <summary>Whether a verb only means anything for a thing still in the room.</summary>
    /// <param name="verb">The verb an action file wrote.</param>
    /// <returns>True when it has no meaning for something already in a pocket.</returns>
    private static bool IsAboutTheRoom(string verb) =>
        verb.Equals("PICKUP", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("TAKE", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("OPEN", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("CLOSE", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("ENTER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts each of the room's nouns where it appears on the screen.
    /// </summary>
    /// <param name="nouns">Each noun and the middle of what it occupies, in world space.</param>
    /// <param name="camera">Where the view is.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>The ones in front of the camera, nearest first.</returns>
    private static IReadOnlyList<(string Noun, Vector2 At)> OnScreen(
        IReadOnlyList<(string Noun, Vector3 Where)> nouns,
        Camera camera,
        int width,
        int height)
    {
        // Without the jitter. A hotspot label is placed for a reader rather than for an
        // accumulator, and a temporal upscaler's sub-pixel offset would make every label on
        // screen shiver by half a pixel in a different direction each frame.
        Matrix4x4 viewProjection =
            camera.View * camera.ProjectionWithoutJitter((float)width / Math.Max(1, height));

        List<(string Noun, Vector2 At, float Depth)> found = [];

        foreach ((string noun, Vector3 where) in nouns)
        {
            Vector4 clip = Vector4.Transform(new Vector4(where, 1f), viewProjection);

            if (clip.W <= 0.001f)
            {
                continue;
            }

            var screen = new Vector2(
                (clip.X / clip.W * 0.5f + 0.5f) * width,
                (clip.Y / clip.W * 0.5f + 0.5f) * height);

            if (screen.X < 0 || screen.X > width || screen.Y < 0 || screen.Y > height)
            {
                continue;
            }

            found.Add((noun, screen, clip.W));
        }

        return [.. found.OrderBy(f => f.Depth).Select(f => (f.Noun, f.At))];
    }

    /// <summary>An angle in degrees, for a line somebody has to read.</summary>
    private static double Degrees(float radians) => radians * 180.0 / Math.PI;

    /// <summary>One character an opening pose moved, as a line of the log.</summary>
    private static string Described((string Who, Vector3 Where, float Placed, float? Wanted) m)
    {
        string at = FormattableString.Invariant(
            $"{m.Who} at {m.Where.X:F0}, {m.Where.Z:F0} facing {Degrees(m.Placed):F0}");

        return m.Wanted is { } want
            ? at + FormattableString.Invariant(
                $" (the clip wants {Degrees(want):F0}, hips {Game.Actors.AnimationStart.Reading:F0}° off)")
            : at;
    }

    /// <summary>
    /// The interface's number for a slot's picture, loading it the first time it is asked for.
    /// </summary>
    /// <param name="renderer">What holds the interface's pictures.</param>
    /// <param name="saves">Where the saves are.</param>
    /// <param name="slot">Which slot.</param>
    /// <returns>The number, or nought when the slot has no picture.</returns>
    private static int Illustration(
        Rendering.IRenderer renderer, Game.SaveStore? saves, string slot)
    {
        if (saves is null)
        {
            return 0;
        }

        string name = "save:" + slot;

        if (renderer.OverlayPicture(name) is > 0 and { } already)
        {
            return already;
        }

        return saves.Picture(slot) is { } picture
            ? renderer.AddOverlayPicture(name, picture)
            : 0;
    }

    /// <summary>A frame reduced to something a menu row can hold.</summary>
    private static Formats.Bitmaps.DecodedImage Thumbnail(Formats.Bitmaps.DecodedImage frame)
    {
        const int Step = 4;

        int width = Math.Max(1, frame.Width / Step);
        int height = Math.Max(1, frame.Height / Step);
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int from = (((y * Step) * frame.Width) + (x * Step)) * 4;
                int to = ((y * width) + x) * 4;

                pixels[to] = frame.Pixels[from];
                pixels[to + 1] = frame.Pixels[from + 1];
                pixels[to + 2] = frame.Pixels[from + 2];
                pixels[to + 3] = 255;
            }
        }

        return new Formats.Bitmaps.DecodedImage(width, height, pixels, false, "save");
    }

    /// <summary>Whether a verb is a thing in the bag rather than something to do.</summary>
    /// <param name="verb">The verb an action file wrote.</param>
    /// <param name="verbs">What the game says each verb is.</param>
    /// <returns>True for an inventory item.</returns>
    private static bool IsAnItem(string verb, Game.Actions.VerbLibrary? verbs) =>
        verbs?.KindOf(verb) == Game.Actions.VerbKind.Inventory;

    /// <summary>What a click inside Sidney means.</summary>
    /// <summary>
    /// Scans an inventory item into Sidney the way the game does it.
    /// </summary>
    /// <param name="item">The item's noun.</param>
    /// <param name="api">The sheep machine, for running the item's own rule.</param>
    /// <param name="scene">The room, which holds the action rules.</param>
    /// <param name="sidney">Grace's computer.</param>
    /// <param name="console">Where to say what happened, if anywhere.</param>
    private static void ScanIntoSidney(
        string item,
        Gk3SheepApi api,
        LoadedScene scene,
        Game.Sidney.SidneyMachine sidney,
        GameConsole? console)
    {
        if (sidney.Scan(item) is not { } scanned)
        {
            return;
        }

        console?.Print(scanned.Text);
        Log.Info($"Scanned: {scanned.Text}");

        GameState story = api.State;
        bool wasScanning = story.GetFlag("UsingScanner");
        bool hadInventory = story.Screens.IsOpen(ScreenKind.Inventory);

        story.SetFlag("UsingScanner");
        story.Screens.Show(new Screen(ScreenKind.Inventory, item));

        try
        {
            if (scene.Actions?.Find(item, "SCANNER", story.Ego) is not { } rule)
            {
                return;
            }

            // Counted before the rule runs, which is the order the original works in:
            // Estelles_Print then sets the count outright, for both egos, and a bump
            // afterwards would undo what it decided.
            story.IncrementNounVerbCount(item, "SCANNER");

            ActionOutcome ran = new ActionRunner(api).Run(rule);

            Log.Info(
                $"{item}:SCANNER [{rule.Case}] - " +
                $"{(ran.Ran ? "ran" : "refused")} {ran.Statements.Count} statement(s)");
        }
        finally
        {
            // A script may well have closed the inventory itself; every one of these ends in
            // HideInventory.
            if (!hadInventory)
            {
                story.Screens.Hide(ScreenKind.Inventory);
            }

            if (!wasScanning)
            {
                story.ClearFlag("UsingScanner");
            }
        }
    }

    private static void OnSidney(
        string what,
        string which,
        GameState story,
        Game.Sidney.SidneyMachine sidney,
        GameConsole console,
        Action<string>? scan)
    {
        switch (what)
        {
            case "screen" when Enum.TryParse(which, out Game.Sidney.SidneyScreen screen):
                sidney.Show(screen);
                break;

            case "home":
                sidney.Home();
                break;

            // Opening a message marks it read, which is what turns the corner's
            // notification off. Nothing did before, so the original's NEW E-MAIL light
            // would have burned for the whole game.
            case "mail":
                sidney.ReadMail(sidney.Mail().FirstOrDefault(m => m.Id == which));
                break;

            // The translate screen keeps its own open file: analysing a parchment and
            // translating a tape are two things a player may have going at once, and one
            // list that meant both would close the other.
            case "open":
                sidney.OpenForTranslation(sidney.Files.FirstOrDefault(f => f.Id == which));
                break;

            // The analyze screen's four menus: one open at a time, and clicking the open
            // one shuts it.
            case "menu" when int.TryParse(which, out int menu):
                sidney.Menu = sidney.Menu == menu ? 0 : menu;
                break;

            // The ruling the map is divided into, and whether it fills the figure or the
            // whole picture.
            case "assist":
                console.Print(sidney.Assist().Text);
                break;

            // Yes and no arrive as their keys rather than as their words: the button says
            // OUI in French and JA in German, and reading the first letter of a translated
            // word is a rule that happens to hold for six languages and no more.
            case "solve":
                console.Print(sidney.Finish(Agreed(which)).Text);
                break;

            case "grid" when int.TryParse(which, out int cells):
                console.Print(sidney.Rule(cells).Text);
                break;

            case "fill":
                sidney.RuleInShape = !sidney.RuleInShape;
                break;

            case "from":
                sidney.From = which;
                break;

            case "translate":
                console.Print(sidney.Translate().Text);
                break;

            case "complete":
                sidney.Complete(Agreed(which));
                break;

            case "append":
                console.Print(sidney.Append().Text);
                break;

            // Scanning runs the game's own rule as well as making the file. The rule is what
            // marks the item used, sets SidScanner and calls whatever script hangs off it;
            // the file is what DoesSidneyFileExist reads, and nothing made one before this
            // existed.
            case "scan":
                scan?.Invoke(which);
                break;

            case "file":
                sidney.OpenFile(sidney.Files.FirstOrDefault(f => f.Id == which));
                break;

            case "look":
                sidney.Look();
                break;

            case "page":
                sidney.Follow(which);
                break;

            case "suspect" when int.TryParse(which, out int index):
                sidney.OpenSuspect(
                    sidney.Suspects().FirstOrDefault(s => s.Index == index));

                break;

            case "link" when sidney.Files.FirstOrDefault(f => f.Id == which) is { } linking:
                console.Print(sidney.LinkToSuspect(linking).Text);
                break;

            case "unlink" when sidney.Files.FirstOrDefault(f => f.Id == which) is { } unlinking:
                console.Print(sidney.UnlinkFromSuspect(unlinking).Text);
                break;

            case "match":
                console.Print(sidney.MatchPrint().Text);
                break;

            case "id" when sidney.Library.Identities()
                    .FirstOrDefault(i => i.Key == which) is { } identity:
                console.Print(sidney.PrintIdentity(identity).Text);
                break;

            case "do" when Enum.TryParse(which, out Game.Sidney.SidneyAction action):
                sidney.Perform(action);
                break;

            case "answer":
                sidney.Answer(which);
                break;

            default:
                break;
        }
    }

    /// <summary>Whether one of Sidney's yes-or-no answers was the yes.</summary>
    /// <param name="answer">The choice's key, which is <c>Yes</c> or <c>No</c>.</param>
    /// <returns>True for yes.</returns>
    private static bool Agreed(string answer) =>
        answer.StartsWith("Y", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a screen may take the player from here.
    /// </summary>
    /// <param name="screen">Which screen is asking.</param>
    /// <param name="scene">The room.</param>
    /// <param name="story">The game.</param>
    /// <returns>The places, which is empty where the screen is not about going anywhere.</returns>
    private static List<string> Reachable(Screen screen, LoadedScene scene, GameState story)
    {
        if (screen.Kind is not (ScreenKind.Driving or ScreenKind.Binoculars))
        {
            return [];
        }

        List<string> places = [];

        foreach (string location in story.VisitedLocations(story.Ego))
        {
            if (!string.Equals(location, scene.Name, StringComparison.OrdinalIgnoreCase))
            {
                places.Add(location.ToUpperInvariant());
            }
        }

        places.Sort(StringComparer.Ordinal);

        return places;
    }

    /// <summary>
    /// The end of a chase: what it put on the map, and where it leaves the player.
    /// </summary>
    /// <param name="traffic">The chase.</param>
    /// <param name="story">The game.</param>
    /// <returns>
    /// Where the player rides on to, or null when the map is left up for them to choose —
    /// which is every chase but Lady Howard's; see <see cref="Traveller.LeavesMapOpen"/>.
    /// </returns>
    private static string? Arrive(Game.DrivingTraffic traffic, GameState story)
    {
        if (traffic.Chase is not { } quarry)
        {
            return null;
        }

        // Two only where the chase led somewhere new. The original's map reads the count
        // as "followed them all the way" and puts their destination on it, so a chase that
        // reveals nothing — Lady Howard's ride round the valley and back — must leave it at
        // one, or Day 1 would hand over the dig that is not found until Day 2.
        story.SetNounVerbCount(
            quarry.Counted, DrivingMap.Follow, quarry.Reveals.Count > 0 ? 2 : 1);

        foreach (string place in quarry.Reveals)
        {
            if (DrivingMap.Reveal(story, place))
            {
                Log.Info($"The map now knows {place}");
            }
        }

        // And where they are now. The end of 102P asks IsActorAtLocation about both of
        // the afternoon's quarries, and this is the only thing in the game that answers.
        if (quarry.LeavesThemAt is { } at)
        {
            story.SetActorLocation(quarry.Noun, at);
        }

        string arrived = quarry.Arrives ?? traffic.From ?? story.Location;

        Log.Info(quarry.LeavesMapOpen
            ? $"Followed {quarry.Noun} to {arrived}; the map stays open"
            : $"Followed {quarry.Noun} to {arrived}");

        return quarry.LeavesMapOpen ? null : arrived;
    }

    /// <summary>Why a room was left.</summary>
    /// <param name="Code">Process exit code, if this is the end of it.</param>
    /// <param name="Destination">Where the story went, or null when the player quit.</param>
    private readonly record struct RoomExit(int Code, string? Destination);

    /// <summary>
    /// A dusted glass waiting on the player to say whose it was.
    /// </summary>
    /// <param name="Glass">Which glass.</param>
    /// <param name="Question">The noun whose topics ask.</param>
    private readonly record struct GlassQuestion(string Glass, string Question)
    {
        /// <summary>Whether the bar has been opened yet, or the line is still being said.</summary>
        public bool Opened { get; init; }

        /// <summary>How often Wilkes had been named before the bar opened.</summary>
        public int Wilkes { get; init; }

        /// <summary>How often Buchelli had been named before the bar opened.</summary>
        public int Buchelli { get; init; }
    }

    /// <summary>
    /// What dusting one of the lobby's glasses came to, done to the story.
    /// </summary>
    /// <param name="glass">What Gabriel made of it.</param>
    /// <param name="noun">Which glass.</param>
    /// <param name="story">The game.</param>
    /// <param name="api">The room, for the line and the score sheet.</param>
    private static void Dusted(Game.GlassDusting glass, string noun, GameState story, Gk3SheepApi api)
    {
        if (glass.Says is { Length: > 0 } line)
        {
            new ActionRunner(api).Run(new Formats.Actions.NvcAction
            {
                Noun = noun,
                Verb = "FINGERPRINT_KIT",
                Case = "DUSTED",
                Script = string.Create(
                    CultureInfo.InvariantCulture, $"wait StartDialogue(\"{line}\", 1)"),
                Source = "the fingerprint kit",
            });
        }

        if (glass.Lifts)
        {
            IReadOnlyList<string> gained = Game.FingerprintKit.Lift(noun, story, api.Scores);

            Log.Info($"fingerprints: {noun} gave {string.Join(", ", gained)}");
        }

        if (glass.Mislabels is { Length: > 0 } wrong)
        {
            story.Inventory.Add(story.Ego, wrong);

            Log.Info($"fingerprints: {noun} gave {wrong}, which is the wrong name");
        }

        Log.Info($"fingerprints: {Game.DirtyGlasses.Variable} = {story.GetVariable(Game.DirtyGlasses.Variable)}");
    }

    /// <summary>
    /// Makes sure the scene is loaded at a point in the story, not merely at a time of day.
    /// </summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    /// <param name="timeblock">What the player asked for.</param>
    /// <returns>A request with a story behind it, where the room has one.</returns>
    private static SceneRequest Playable(GameArchives archives, string scene, string? timeblock)
    {
        SceneRequest asked = SceneRequest.For(scene, timeblock);

        if (asked.State is not null)
        {
            return asked;
        }

        IReadOnlyList<string> known = Timeblocks(archives, scene);

        if (known.Count == 0)
        {
            Log.Info(
                $"Story: {scene} has no timeblock of its own, so its conditions stay " +
                "undecided and its objects answer to nothing.");

            return asked;
        }

        string chosen = known[0];

        Log.Info(timeblock is { Length: > 0 } asOfDay
            ? $"Story: '{asOfDay}' is a time of day, not a point in the story, so nothing " +
              $"in the room would answer to anything. Using {chosen} instead."
            : $"Story: no timeblock given, so nothing in the room would answer to " +
              $"anything. Using {chosen}.");

        Log.Info($"  {scene} knows: {string.Join(" ", known)}");

        return SceneRequest.For(scene, chosen);
    }

    /// <summary>The story timeblocks a scene has a file for.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    /// <returns>The codes, in order.</returns>
    private static IReadOnlyList<string> Timeblocks(GameArchives archives, string scene)
    {
        string prefix = scene.ToUpperInvariant();

        return
        [
            .. archives.Names(".SIF")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null &&
                            n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            n.Length > prefix.Length)
                .Select(n => n![prefix.Length..])
                .Where(c => Timeblock.TryParse(c, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Where <c>--pointer X,Y</c> says the pointer is.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>The point, or null to follow the mouse.</returns>
    private static Vector2? Pinned(string[] args) =>
        Option(args, "--pointer")?.Split(',') is [string x, string y] &&
        float.TryParse(x, CultureInfo.InvariantCulture, out float px) &&
        float.TryParse(y, CultureInfo.InvariantCulture, out float py)
            ? new Vector2(px, py)
            : null;

    /// <summary>Where <c>--eye x,y,z</c> asks the camera to stand.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The viewpoint, or null when the switch is absent or unreadable.</returns>
    private static Vector3? Standing(string[] args) =>
        Option(args, "--eye")?.Split(',') is [string x, string y, string z] &&
        float.TryParse(x, CultureInfo.InvariantCulture, out float ex) &&
        float.TryParse(y, CultureInfo.InvariantCulture, out float ey) &&
        float.TryParse(z, CultureInfo.InvariantCulture, out float ez)
            ? new Vector3(ex, ey, ez)
            : null;

    /// <summary>Which way <c>--aim heading,pitch</c> asks it to look, in degrees.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The aim, or null when the switch is absent or unreadable.</returns>
    private static Vector2? Aimed(string[] args) =>
        Option(args, "--aim")?.Split(',') is [string h, string p] &&
        float.TryParse(h, CultureInfo.InvariantCulture, out float heading) &&
        float.TryParse(p, CultureInfo.InvariantCulture, out float pitch)
            ? new Vector2(heading, pitch)
            : null;

    /// <summary>How far to subdivide a character's head.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="settings">What the player chose.</param>
    /// <returns>The number of levels, within range.</returns>
    private static int HeadLevels(string[] args, Settings settings)
    {
        if (args.Contains("--flat-heads", StringComparer.OrdinalIgnoreCase))
        {
            return 0;
        }

        return Option(args, "--heads") is { } value &&
               int.TryParse(value, CultureInfo.InvariantCulture, out int levels)
            ? Math.Clamp(levels, 0, Game.Actors.HeadRefinement.MaximumLevels)
            : settings.SmoothHeads;
    }

    /// <summary>Reads an option's value from the command line.</summary>
    private static string? Option(string[] args, string name) => CommandLine.Value(args, name);

    /// <summary>
    /// Whether the room being built is one the binoculars are showing, or the one they are
    /// being lowered in. Neither is somewhere the player went.
    /// </summary>
    private static bool Looking(Gk3SheepApi api) =>
        api.Leaning is not null || api.Resuming is not null;

    /// <summary>How much of the cut-content table the command line asks for.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="settings">The player's saved settings, which the menu writes.</param>
    /// <returns>Which tier to apply.</returns>
    private static CutContentTier RestorationTier(string[] args, Settings settings)
    {
        if (!args.Contains("--restore-cut-content", StringComparer.OrdinalIgnoreCase))
        {
            return settings.RestoredContent;
        }

        string? how = Option(args, "--restore-cut-content");

        return how?.ToUpperInvariant() switch
        {
            "ALL" => CutContentTier.All,
            "REBUILT" => CutContentTier.Reconstructed,
            _ => CutContentTier.Observation,
        };
    }

    /// <summary>Where the game is usually installed relative to the repository.</summary>
    /// <summary>
    /// Where the enhanced textures are, if the player wants them.
    /// </summary>
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

    /// <summary>
    /// Writes the game's content out as files, laid out for <c>overrides/</c>.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>Process exit code.</returns>
    private static int Extract(string[] args)
    {
        string? name = Option(args, "--name");
        string? kindList = Option(args, "--kinds");
        string from = Option(args, "--from") ?? "packs";

        bool asPng = string.Equals(Option(args, "--as"), "png", StringComparison.OrdinalIgnoreCase);

        if (Option(args, "--as") is { Length: > 0 } form &&
            !form.Equals("png", StringComparison.OrdinalIgnoreCase) &&
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

            foreach (string one in kindList.Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Formats.Rebarn.RebarnFormat.KindOf(one) is not { } kind)
                {
                    Log.Error($"--kinds: {one} names no kind of content.");
                    Log.Error(
                        "The kinds are textures, normals, orm, height, emissive, models, "
                        + "scene-geometry, video, menu, manifests and raw.");

                    return 2;
                }

                kinds.Add(kind);
            }
        }

        // Everything, into the directory the game reads. That is not an extract, it is a
        // fifteen-gigabyte copy of the game into its own override layer, where every file
        // then stands in front of the one it was copied from — and the first thing anybody
        // would notice is that rebuilding the packs stopped changing anything.
        if (intoOverrides && kinds is null && name is null && wantsPacks)
        {
            Log.Error(
                "--extract with no --kinds and no --name would copy every packed file into "
                + $"{output}, where each one would then override itself.");
            Log.Error(
                "Say which content you want — --kinds textures, --name R25WALLS — or "
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
                // Refused rather than reported as an empty success. "Wrote 0 files" from a
                // directory with no packs in it reads as "there was nothing in them".
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

            // The 1999 assets go in a directory of their own. They are matched by their
            // whole file name rather than by a kind, so no kind directory would mean
            // anything, and forty thousand files beside a dozen texture directories would
            // bury the ones somebody came for.
            string game = Path.Combine(output, "game");

            // The extension list, which for these is what --kinds means: a barn holds SIF,
            // NVC, BMP, MOD and WAV, and none of those is a ReBarn kind.
            string[]? extensions = kindList is { Length: > 0 }
                ? kindList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;

            if (intoOverrides && extensions is null && name is null)
            {
                Log.Error(
                    $"--from game with no --kinds and no --name would copy every asset the "
                    + $"archives hold into {game}, where each one would then override itself.");
                Log.Error(
                    "Say which — --kinds SIF,NVC, --name R25 — or --extract-to <dir>.");

                return 2;
            }

            ContentExtract.Result written =
                ContentExtract.FromGame(archives, game, extensions, name, Log.Info);

            Log.Info($"  {"game",-15} {written.Written,6} file(s), "
                + $"{written.Bytes / (1024.0 * 1024):F1} MB");

            total += written;
        }

        Log.Info(total.Written == 0
            ? "Nothing matched, so nothing was written."
            : $"Wrote {total.Written} file(s), {total.Bytes / (1024.0 * 1024):F1} MB, to {output}"
                + (total.Failed > 0 ? $"; {total.Failed} could not be written." : "."));

        if (total.Written > 0 && intoOverrides)
        {
            Log.Info(
                "Every file there now stands in front of the one it came from. Delete the "
                + "ones you are not changing, or the game reads its own content back "
                + "through a slower door.");
        }

        return total.Written > 0 ? 0 : 1;
    }

    /// <summary>Where the player's own overriding files sit.</summary>
    /// <param name="args">Command line, for <c>--overrides</c>.</param>
    /// <returns>The directory, whether or not it exists.</returns>
    private static string OverrideDirectory(string[] args)
    {
        if (Option(args, "--overrides") is { Length: > 0 } named && !named.StartsWith('-'))
        {
            return named;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, ContentOverrides.DirectoryName);

        return Directory.Exists(beside) || InstallPaths.CanWrite(AppContext.BaseDirectory)
            ? beside
            : Path.Combine(InstallPaths.UserData, ContentOverrides.DirectoryName);
    }

    /// <summary>One of the archives' own bitmaps, decoded, or null when it is not there.</summary>
    /// <param name="archives">The game's data.</param>
    /// <param name="name">The bitmap's name with extension.</param>
    /// <returns>The picture, or null.</returns>
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

    /// <summary>
    /// One channel's loose picture layer: the workspace's set with the overrides over it.
    /// </summary>
    /// <param name="enabled">Whether the enhanced set itself is wanted.</param>
    /// <param name="packsOnly">Whether every other loose source is being ignored.</param>
    /// <param name="enhancedDirectory">The enhanced colour set, or null.</param>
    /// <param name="overrides">What the player has dropped in, or null.</param>
    /// <param name="kind">Which channel.</param>
    /// <param name="subdirectory">Where that channel sits beside the colour set.</param>
    /// <param name="language">
    /// The language whose own set goes over the shared one, or null for the shared set
    /// alone. <b>Every channel, not only colour.</b> A normal map is derived from the
    /// colour texture it belongs to, so the shared <c>PANEL1</c> normal has the *English*
    /// words embossed in it — under a German <c>PANEL1</c> that reads as English lettering
    /// in relief beneath the German, lit from wherever the room is lit from. This was
    /// written the other way, on the reasoning that a sign's bumps are not language; the
    /// bumps are not, but a map derived from a picture of words is.
    /// </param>
    /// <returns>The layer, or null when neither source has anything for this channel.</returns>
    private static EnhancedTextures? Pictures(
        bool enabled,
        bool packsOnly,
        string? enhancedDirectory,
        ContentOverrides? overrides,
        Formats.Rebarn.RebarnKind kind = Formats.Rebarn.RebarnKind.Texture,
        string? subdirectory = null,
        GameLanguage? language = null)
    {
        string directory = enabled && !packsOnly && enhancedDirectory is { Length: > 0 }
            ? subdirectory is null ? enhancedDirectory : Beside(enhancedDirectory, subdirectory)
            : string.Empty;

        // enhanced/localtextures/<CODE> and its three material neighbours, beside the
        // enhanced set rather than under it, because each is a parallel set and not a
        // variant of one: the same names, repainted and re-derived.
        string localised =
            language is not null &&
            LocalChannel(kind) is { } channel &&
            enabled && !packsOnly && enhancedDirectory is { Length: > 0 }
                ? Path.Combine(Beside(enhancedDirectory, channel), language.FileCode)
                : string.Empty;

        ContentOverrides? layer = overrides?.Images(kind).Count > 0 ? overrides : null;

        return directory.Length == 0 && localised.Length == 0 && layer is null
            ? null
            : EnhancedTextures.Open(directory, layer, kind, localised);
    }

    /// <summary>Which of a language's own directories holds a channel.</summary>
    /// <param name="kind">The channel.</param>
    /// <returns>
    /// The directory's name beside the enhanced set, or null for a kind no language has
    /// one of.
    /// </returns>
    private static string? LocalChannel(Formats.Rebarn.RebarnKind kind) => kind switch
    {
        Formats.Rebarn.RebarnKind.Texture => "localtextures",
        Formats.Rebarn.RebarnKind.Normal => "localnormals",
        Formats.Rebarn.RebarnKind.Orm => "localorm",
        Formats.Rebarn.RebarnKind.Height => "localheight",
        _ => null,
    };

    /// <summary>Where the block-compressed build of the enhanced textures sits.</summary>
    /// <summary>Where the ReBarn packs are.</summary>
    /// <param name="args">Command line, for <c>--packs</c> and <c>--workspace</c>.</param>
    /// <returns>The first directory that holds a pack, or the executable's own.</returns>
    private static string PackDirectory(string[] args)
    {
        if (Option(args, "--packs") is { Length: > 0 } named)
        {
            return named;
        }

        // Beside the executable first, because that is where a shipped game puts them and
        // where a player would drop one. The workspace after it, because that is where the
        // packer writes during development and copying fifteen gigabytes to try a build is
        // not something anybody should have to do.
        string[] candidates =
        [
            AppContext.BaseDirectory,
            // A macOS .app carries its pack in Contents/Resources, which is the only place
            // inside a bundle that a signed, read-only install can put shipped data.
            InstallPaths.BundleResources ?? string.Empty,
            // And the user's own directory, which is where somebody with a read-only
            // install drops a pack they downloaded separately.
            InstallPaths.UserData,
            Option(args, "--workspace") is { Length: > 0 } workspace ? workspace : string.Empty,
            DefaultWorkspaceDirectory(),
        ];

        foreach (string candidate in candidates)
        {
            if (candidate.Length > 0 &&
                Directory.Exists(candidate) &&
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
        string? enhancedRoot = Path.GetDirectoryName(
            enhancedDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'));

        string? root = enhancedRoot is null ? null : Path.GetDirectoryName(enhancedRoot);

        return Path.Combine(root ?? DefaultWorkspaceDirectory(), "build");
    }

    /// <summary>Where the content workspace usually sits relative to the repository.</summary>
    private static string DefaultWorkspaceDirectory() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "ContentWorkspace"));

    /// <summary>The eight archives a retail installation of GK3 holds.</summary>
    private static readonly string[] RetailArchives =
    [
        "ambient.brn", "common.brn", "core.brn", "day1.brn",
        "day123.brn", "day2.brn", "day23.brn", "day3.brn",
    ];

    /// <summary>
    /// Says which of the eight archives are not there, and which are there unseen.
    /// </summary>
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

        string[] unseen = [.. present.Where(name =>
            Path.GetExtension(name).Equals(".brn", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(name).Equals(".brn", StringComparison.Ordinal))];

        if (unseen.Length > 0)
        {
            Log.Error($"Content: {dataDirectory} holds {unseen.Length} archive(s) whose "
                + $"names are spelled differently: {string.Join("  ", unseen)}");

            Log.Error("Linux and macOS match file names exactly, so those are not found. "
                + "Rename them to lower case, extension included.");
        }

        if (found == 0)
        {
            // Every one of the eight is missing, which the message that follows this says
            // better than a list would.
            return;
        }

        string[] absent = [.. RetailArchives.Where(archive =>
            !present.Any(name => name.Equals(archive, StringComparison.OrdinalIgnoreCase)))];

        if (absent.Length > 0)
        {
            // A warning, not a refusal: a copy without day3.brn plays for two days, and
            // stopping it from starting would be worse than saying what it will not reach.
            Log.Warning($"Content: {absent.Length} of the eight archives are not in "
                + $"{dataDirectory}: {string.Join("  ", absent)}");

            Log.Warning("The game will start, but the rooms and cutscenes in them cannot "
                + "be loaded.");
        }
    }

    /// <summary>Says what is missing and where it goes.</summary>
    /// <param name="dataDirectory">Where the archives were looked for.</param>
    private static void ExplainMissingArchives(string dataDirectory)
    {
        Log.Error();
        Log.Error(
            "GK3Reborn reads the original game's archives; it does not contain them.");

        Log.Error(
            $"Copy these from your installation's Data directory into {dataDirectory}:");

        Log.Error("    " + string.Join("  ", RetailArchives));
        Log.Error();
        Log.Error(
            "Nothing else from the original is needed: the .bik and .avi movies are "
            + "replaced by converted video in the .rebarn packs.");

        Log.Error(
            "Or pass --data <dir> to read them where they already are.");
    }

    /// <summary>Where the game's own archives are, when nobody has said.</summary>
    /// <returns>The first directory holding a <c>.brn</c>, or where one should be put.</returns>
    private static string DefaultDataDirectory()
    {
        string beside = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(beside, "Data"),
            beside,
            // A read-only install cannot be filled in place, so the same Data directory is
            // looked for under the user's own: that is what a macOS .app in /Applications
            // asks a player to make, and it is a sensible place on any platform for
            // somebody who does not own the install directory.
            Path.Combine(InstallPaths.UserData, "Data"),
            InstallPaths.BundleResources is { Length: > 0 } resources
                ? Path.Combine(resources, "Data")
                : string.Empty,
            Path.GetFullPath(Path.Combine(
                beside, "..", "..", "..", "..", "..", "..", "GK3", "Data")),
        ];

        candidates = [.. candidates.Where(candidate => candidate.Length > 0)];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*.brn").Any())
            {
                StartupReport.Searched("Content", candidates, candidate);

                return candidate;
            }
        }

        // Every place that was tried, since the message below names only one of them and
        // "it is not where you say it is" is not an answer somebody can act on.
        StartupReport.Searched("Content", candidates, null);

        // Nothing anywhere: name the place a player is meant to fill rather than the one a
        // developer's checkout happens to have, because that is the message they will read.
        // On a read-only install that place is not beside the executable - a player cannot
        // put anything inside a signed .app - so name the directory they can actually use.
        return InstallPaths.CanWrite(beside)
            ? candidates[0]
            : Path.Combine(InstallPaths.UserData, "Data");
    }

    /// <summary>
    /// Renders one frame with no window and writes it to a file.
    /// </summary>
    /// <returns>Process exit code.</returns>
    private static int RenderOffscreen()
    {
        using Rendering.Vulkan.OffscreenRenderer renderer = Rendering.Vulkan.OffscreenRenderer.Create();

        Formats.Bitmaps.DecodedImage image = renderer.RenderTriangle(640, 360, (0.05f, 0.06f, 0.09f));

        // Beside the executable, where somebody running the smoke test will look for it -
        // unless the executable is inside a read-only .app bundle, where writing there
        // would fail the test for a reason that has nothing to do with what it proves.
        string path = Path.Combine(InstallPaths.WritableRoot, "offscreen.png");
        File.WriteAllBytes(path, Formats.Bitmaps.PngWriter.Encode(image));

        Log.Info($"Rendered {image.Width}x{image.Height} on {renderer.DeviceName}");
        Log.Info($"Wrote {path}");

        return 0;
    }

    /// <summary>
    /// Opens a window and presents frames.
    /// </summary>
    /// <param name="frameLimit">Stop after this many frames, or zero to run until closed.</param>
    /// <returns>Process exit code.</returns>
    private static int RenderFrames(int frameLimit)
    {
        using var window = Platform.SilkGameWindow.Open("GK3Reborn");

        // The one caller that wants the bring-up triangle: there is no room to draw and the
        // point is to prove the chain reaches the screen at all.
        using var renderer = Rendering.Vulkan.VulkanRenderer.Create(window, window, bringUp: true);

        Log.Info($"Renderer: {renderer}");

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

        Log.Info($"Presented {presented} frames at {renderer.SwapchainSize.Width}x"
            + $"{renderer.SwapchainSize.Height} across {renderer.SwapchainImageCount} swapchain images");

        return 0;
    }

    /// <summary>Works out which graphics API to draw through.</summary>
    /// <param name="asked">What was typed after --backend, or null for whichever suits.</param>
    /// <param name="settings">The player's, for the backend they chose.</param>
    /// <returns>The backend to open the window and the renderer for.</returns>
    private static Rendering.RenderBackend ChooseBackend(string? asked, Settings settings)
    {
        // The command line first, then the settings file, then whatever suits the machine.
        // A backend typed for one run is meant for that run and does not become the setting.
        if (asked is null)
        {
            return Rendering.RenderBackends.Resolve(settings.Backend);
        }

        if (!Rendering.RenderBackends.TryParse(asked, out Rendering.RenderBackend wanted))
        {
            Log.Warning(
                $"WARNING GK3R3420: '{asked}' names no graphics API; using the usual one. " +
                "Expected vulkan or d3d12.");

            return Rendering.RenderBackends.Resolve(settings.Backend);
        }

        if (!Rendering.RenderBackends.IsPossible(wanted))
        {
            Log.Warning(
                $"WARNING GK3R3421: {wanted} cannot be used on this machine; using Vulkan.");

            return Rendering.RenderBackend.Vulkan;
        }

        return Rendering.RenderBackends.Resolve(wanted);
    }

    /// <summary>A window, the renderer drawing into it, and the loader that renderer needed.</summary>
    /// <param name="Window">The window. Opened for the renderer's API, which is why the two travel together.</param>
    /// <param name="Streamline">NVIDIA's loader, on Vulkan; Direct3D starts its own inside the renderer.</param>
    /// <param name="Renderer">The renderer.</param>
    /// <param name="Backend">Which API it is, which may not be the one that was asked for.</param>
    private readonly record struct OpenedRenderer(
        Platform.SilkGameWindow Window,
        Rendering.Upscaling.Streamline? Streamline,
        Rendering.IRenderer Renderer,
        Rendering.RenderBackend Backend);

    /// <summary>
    /// Opens a window and makes a renderer for it, falling back from Direct3D to Vulkan
    /// when the machine turns out not to be a Direct3D machine after all.
    /// </summary>
    /// <param name="backend">The backend to try first. Not <see cref="Rendering.RenderBackend.Automatic"/>.</param>
    /// <param name="insisted">
    /// Whether the backend was named on the command line. A named one is not fallen back
    /// from: somebody who typed it is finding out whether it works, and being handed the
    /// other renderer would tell them it does.
    /// </param>
    /// <param name="title">The window title.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    /// <param name="runtimes">The upscaler runtimes that were found.</param>
    /// <param name="libsDirectory">Where <c>--libs-dir</c> pointed, for Direct3D's own Streamline.</param>
    /// <returns>The three, to be disposed by the caller in the reverse of this order.</returns>
    private static OpenedRenderer OpenRenderer(
        Rendering.RenderBackend backend,
        bool insisted,
        string title,
        int width,
        int height,
        Rendering.Upscaling.UpscalerRuntimes runtimes,
        string? libsDirectory)
    {
        if (backend == Rendering.RenderBackend.Direct3D12)
        {
            Platform.SilkGameWindow window = Platform.SilkGameWindow.Open(
                title, width, height, Platform.WindowGraphics.None, visible: false);

            try
            {
                Rendering.IRenderer renderer = Rendering.Direct3D12.D3D12Renderer.Create(
                    window, window, rayTracing: true, runtimes: libsDirectory);

                return new OpenedRenderer(window, null, renderer, backend);
            }
            catch (Exception error) when (
                error is Rendering.Direct3D12.D3D12Exception
                    or Rendering.Shaders.ShaderCompilationException)
            {
                window.Dispose();

                if (insisted)
                {
                    throw new Rendering.Direct3D12.D3D12Exception(
                        $"{error.Message} Direct3D 12 was asked for; --vulkan is the other renderer.",
                        error);
                }

                Log.Warning(
                    "WARNING GK3R3422: Direct3D 12 cannot run on this machine; using Vulkan " +
                    $"instead. {error.Message}");
            }
            catch
            {
                window.Dispose();
                throw;
            }
        }

        Platform.SilkGameWindow vulkanWindow = Platform.SilkGameWindow.Open(
            title, width, height, Platform.WindowGraphics.Vulkan, visible: false);

        Rendering.Upscaling.Streamline? streamline = null;

        try
        {
            streamline = Rendering.Upscaling.Streamline.TryStart(runtimes);

            Rendering.IRenderer renderer = VulkanRenderer.Create(
                vulkanWindow, vulkanWindow, streamline: streamline);

            return new OpenedRenderer(vulkanWindow, streamline, renderer, Rendering.RenderBackend.Vulkan);
        }
        catch
        {
            streamline?.Dispose();
            vulkanWindow.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prints what the machine's graphics hardware can do.
    /// </summary>
    /// <summary>Prints what a device survey found.</summary>
    /// <param name="report">
    /// The survey, or null to make one. A caller that already has a renderer should pass its
    /// own: building an instance purely to look through it is 145 ms of the time to a first
    /// frame, and doing it on another thread to hide that lost a device about one run in six.
    /// </param>
    private static void ReportGraphics(Rendering.DeviceReport? report = null) =>
        Log.Write(GraphicsReport(report ?? Rendering.Vulkan.VulkanDeviceSelector.Survey()));

    private static string GraphicsReport(Rendering.DeviceReport report)
    {
        var text = new System.Text.StringBuilder();

        if (!report.Available)
        {
            return text
                .AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{report.Backend} unavailable: {report.Unavailable}")
                .ToString();
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

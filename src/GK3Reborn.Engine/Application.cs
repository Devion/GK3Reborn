using System.Diagnostics;
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

        // --rt says what the picture costs and outranks the player's own setting, which
        // is what a flag is for. Without one the settings decide, because nobody starting
        // the game to play it passes a ray-tracing level on a command line.
        RayTracingQuality? asked = Option(args, "--rt") is { Length: > 0 } level
            ? RayTracingSettings.Parse(level)
            : null;

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
    /// <remarks>
    /// Day one at ten in the morning, in the lobby of the Hôtel de Rennes-le-Château, which
    /// is where GK3 begins and the only room the game itself can open with. <c>--start</c>
    /// says otherwise for anybody who wants to begin somewhere else.
    /// </remarks>
    private const string OpeningScene = "R25";

    /// <summary>The time of day the story starts at.</summary>
    private const string OpeningTimeblock = "110A";

    /// <summary>
    /// The films the game opens with, in order.
    /// </summary>
    /// <remarks>
    /// Skipped in a breath if they are not there — an installation without the enhanced
    /// video, or a run with <c>--rebarn</c> and a pack that holds none, should reach the
    /// menu rather than stop at a missing file.
    /// </remarks>
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
        RayTracingQuality? quality,
        string? enhancedDirectory,
        bool frontEnd,
        string[] args)
    {
        if (!Directory.Exists(dataDirectory))
        {
            Console.Error.WriteLine($"No content directory at {dataDirectory}.");
            ExplainMissingArchives(dataDirectory);
            return 2;
        }

        using GameArchives archives = GameArchives.Open(dataDirectory);

        if (archives.Count == 0)
        {
            // The directory is there and empty, which is what a half-finished install looks
            // like. Said here rather than letting the first missing asset report it: a room
            // that cannot be found reads as a broken game, not as a copy nobody made.
            Console.Error.WriteLine($"No game archives in {dataDirectory}.");
            ExplainMissingArchives(dataDirectory);
            return 2;
        }

        Console.WriteLine($"Content: {archives.Count} archives in {dataDirectory}");

        // Before the window, the device and the menu. A room that is not in the archives
        // fails the same way whenever it is noticed, and noticing it here means the player
        // is told what is wrong instead of watching the game quit the moment they press
        // Play.
        if (archives.Read(sceneName + ".SIF") is null)
        {
            Console.Error.WriteLine(
                $"No room called {sceneName}: the archives have no {sceneName}.SIF.");

            Console.Error.WriteLine(
                "Check what was passed to --scene or --start, or drop it and the game "
                + $"starts where it starts, in {OpeningScene}.");

            return 2;
        }

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

        foreach (Diagnostic diagnostic in packDiagnostics.Items)
        {
            Console.Error.WriteLine(diagnostic);
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
            Console.Error.WriteLine(
                "--rebarn and --uncompressed contradict each other: a pack holds nothing "
                + "but compressed textures.");
            Console.Error.WriteLine(
                "Drop --uncompressed to measure the packs, or drop --rebarn to compare "
                + "against the loose sets.");

            return 2;
        }

        if (askedForPacks && packs.VolumeCount == 0)
        {
            // Refused rather than warned. Falling back would run the game on the original
            // textures and report perfectly good timings for something nobody asked to
            // measure, which is the shape of every expensive mistake in this project.
            Console.Error.WriteLine($"--rebarn: no .rebarn pack in {packDirectory}.");
            Console.Error.WriteLine(
                "Build one with `pack-content`, or pass --packs <dir> to say where they are.");

            return 2;
        }

        // Said either way. Silence about a missing pack is how a run comes to be measured
        // against the loose sets while everybody believes it was measured against the pack.
        Console.WriteLine(packs.Describe() is { } packed
            ? packsOnly
                ? $"Packs: {packed} (loose enhanced content ignored)"
                : $"Packs: {packed}"
            : $"Packs: none in {packDirectory}");


        // What the player has chosen, read before anything that obeys it exists. A first
        // run has no file and gets the defaults, which is not a failure and is not reported
        // as one.
        Settings settings = Settings.Load();

        Console.WriteLine(File.Exists(Settings.DefaultPath)
            ? $"Settings: {Settings.DefaultPath}"
            : $"Settings: none yet, they will be written to {Settings.DefaultPath}");

        // --width and --height, for photographing the interface at a display size this
        // machine has not got. Everything about the interface's size is decided from the
        // framebuffer, so there is no other way to see what a 4K display would show.
        using var window = Platform.SilkGameWindow.Open(
            $"GK3Reborn - {sceneName}",
            int.TryParse(Option(args, "--width"), out int windowWidth) && windowWidth > 0
                ? windowWidth
                : 1280,
            int.TryParse(Option(args, "--height"), out int windowHeight) && windowHeight > 0
                ? windowHeight
                : 720);
        using var renderer = VulkanRenderer.Create(window, window);

        ReportGraphics(renderer.Survey());
        Console.WriteLine($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        var diagnostics = new DiagnosticBag();
        SceneRequest request = Playable(archives, sceneName, timeblock);
        Gk3SheepApi api = request.Api ?? new Gk3SheepApi(new GameState());

        // What makes a waited call take time. Without it every line of dialogue in the
        // game is over in the frame it starts.
        api.Animations = new AnimationLibrary(archives);

        // Where saved games go. In the player's own profile beside the settings, and given
        // to the API rather than kept here because the console and the story reach saving
        // through the same door the interface does.
        api.Saves = new Game.SaveStore();

        if (request.State is not null)
        {
            Console.WriteLine($"Story: {request.State.Timeblock} in {request.State.Location}");
        }

        // Sound. The device may not open — a machine without one, or one already held —
        // and the game runs quietly rather than not at all.
        Audio.OpenAlBackend? audio = Audio.OpenAlBackend.Open(settings.Speakers, diagnostics);

        // Before anything plays, so the first sound of the session is already at the level
        // the player left it at rather than at full volume for a moment.
        settings.ApplyTo(audio);

        var sounds = new SoundLibrary(archives);

        SceneAudio? room = audio is null
            ? null
            : new SceneAudio(sounds, api.Animations, audio);

        Console.WriteLine(audio is null
            ? "Audio: none, the game runs silent"
            : $"Audio: {audio.DeviceName}");

        // Movies. The packs hold them, and so does the workspace unless --rebarn says the
        // packs are the whole of the answer — the same rule as every other enhanced kind,
        // with the loose file winning where both have one.
        VideoLibrary videos = VideoLibrary.Open(
            packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty
                : Beside(enhancedDirectory, "video"),
            packs);

        using var movies = new Game.MoviePlayer(videos, audio);

        if (videos.Count > 0)
        {
            // Null: the search walks up from the executable to find libs/<rid> on its
            // own, which is the only thing this could pass it anyway.
            bool decoder = Content.MoviePlayback.Prepare(null, diagnostics);

            Console.WriteLine(
                $"Movies: {videos.Count} available ({videos.LooseCount} loose, " +
                $"{videos.PackedCount} packed)" +
                (decoder
                    ? $", decoded by FFmpeg from {Content.MoviePlayback.LoadedFrom}"
                    : ", but there is no decoder"));

            foreach (Diagnostic diagnostic in diagnostics.Items)
            {
                if (diagnostic.Code == "GK3R1160")
                {
                    Console.Error.WriteLine(diagnostic);
                }
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

            Formats.Animation.GasFile? read = archives.Read(name) is { } bytes
                ? Formats.Animation.GasFile.Parse(bytes)
                : null;

            behaviours[name] = read;
            return read;
        }

        // Which verbs are things to say rather than things to do. Without it a topic is
        // indistinguishable from a verb, every line of it is offered at once, and none of
        // them is ever used up.
        Game.Actions.VerbLibrary verbs = Game.Actions.VerbLibrary.Open(archives);

        // What the game calls places and times, in the player's own language. Without it
        // the corner of the screen reads "LBY - 110A", which is two codes and no help.
        GameStrings strings = GameStrings.Open(archives);

        if (strings.Count > 0)
        {
            Console.WriteLine($"Names: {strings.Count} from ESTRINGS.TXT");
        }

                var host = new ScriptHost(api);

        // Scripts wait for real here, unlike in the tools, because here there is a clock
        // for them to wait against.
        host.Scheduler = new SheepScheduler(host.Machine);

        var catalogue = new Sheep.SheepSignatures();

        Console.WriteLine(
            $"Scripts: {LoadScripts(archives, host, catalogue)} loaded, " +
            $"{catalogue.Count} function signatures");

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
        };

        api.Sidney = sidney;

        // The map the moped is ridden around, and its road network.
        DrivingMap map = DrivingMap.Open(archives);

        // What can be seen from where, through the binoculars. Twenty-one vantage points
        // between the Armchair of the Devil and the tower at Blanchefort.
        Binoculars binoculars = Binoculars.Open(archives);

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

        Console.WriteLine(face is { } chosen
            ? $"Typeface: {chosen.Family}, {chosen.CharacterCount} characters, drawn from outlines"
            : "Typeface: GK3's own bitmap sheets");

        int wantedGlyph = WantedGlyphHeight(window.FramebufferHeight);

        // --font names one outright, for looking at a particular sheet.
        string[] ladder = Option(args, "--font") is { Length: > 0 } named
            ? [named]
            : CaptionFonts;

        // The atlas the room's interface draws with, and the larger one the menu does.
        // Two sizes rather than one magnified: an outline drawn at the size it is wanted
        // is the whole point of having one.
        OverlayAtlas? Cut(bool menu)
        {
            int height = window.FramebufferHeight;

            if (face is not null &&
                OverlayAtlas.Build(face, TextPixels(height, menu)) is { } drawn)
            {
                return drawn;
            }

            int wanted = menu
                ? Math.Max(WantedGlyphHeight(height), TextPixels(height, true) * 2 / 3)
                : WantedGlyphHeight(height);

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
            hud = new GameHud(new Overlay(atlas) { Magnify = magnify });
            screens = new ScreenPainter(new Overlay(atlas) { Magnify = magnify });

            // Sidney's map, the survey the whole puzzle is played on. Beside the driving
            // map's art because both hang off the pipeline the atlas just rebuilt.
            if (archives.Read(Game.Sidney.SidneyMap.Picture + ".BMP") is { } survey)
            {
                try
                {
                    renderer.AddOverlayPicture(
                        Game.Sidney.SidneyMap.Picture,
                        Formats.Bitmaps.BitmapDecoder.Decode(survey, Game.Sidney.SidneyMap.Picture));
                }
                catch (Formats.FormatParseException)
                {
                    // Without it the analyze screen draws a blank square and the marks
                    // still go where they are put.
                }
            }

            // The driving map's own art. After the atlas, because setting an atlas rebuilds
            // the pipeline the pictures hang off and would throw them away.
            //
            // The enhanced set is preferred where it has one: the markers are upscaled
            // there and the map is drawn at whatever size the window affords, so the
            // 55-pixel original is exactly the case an upscale is for.
            LoadMapArt(
                archives,
                renderer,
                screens,
                settings.EnhancedTextures && !packsOnly && enhancedDirectory is { Length: > 0 }
                    ? EnhancedTextures.Open(enhancedDirectory)
                    : null);

            Console.WriteLine(
                $"Interface: {atlas.Name}, {atlas.Count} glyphs at {atlas.Height}px" +
                (magnify > 1 ? $" x{magnify}" : string.Empty) +
                $" (wanted {wantedGlyph} for a {window.FramebufferHeight}-line display), " +
                $"sheet {atlas.Image.Width}x{atlas.Image.Height}, " +
                $"{(renderer.HasOverlay ? "drawing" : "NOT drawing")}");
        }
        else
        {
            Console.WriteLine("Interface: no font found, nothing is drawn over the room");
        }

        // The menu, and what changing something in it reaches. Everything below is set
        // live rather than at the next room: a volume that only takes effect after a door
        // is a volume the player cannot hear themselves setting.
        var front = new FrontEnd(settings);

        MenuPage? pages = hud is null
            ? null
            : new MenuPage(new Overlay(Cut(menu: true) ?? hud.Overlay.Atlas)
            {
                Magnify = hud.Overlay.Magnify,
            });

        SceneUpdate? live = null;

        void Apply(Settings chosen)
        {
            settings = chosen;
            chosen.ApplyTo(audio);

            if (renderer.SupportsRayTracing)
            {
                renderer.Quality = chosen.Quality;
            }

            api.State.CameraGliding = chosen.CameraGlide;
            api.State.CinematicsEnabled = chosen.Cinematics;

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
                settings.EnhancedTextures && !packsOnly && enhancedDirectory is { Length: > 0 }
                    ? EnhancedTextures.Open(enhancedDirectory)
                    : null,
                settings.EnhancedTextures
                    ? CompressedTextures.Open(
                        packsOnly
                            ? string.Empty
                            : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty),
                        packs)
                    : null,
                diagnostics);

            front.Illustrated = title.Exists;

            // Which of them it took, because they are indistinguishable on screen until
            // somebody has actually upscaled the picture — and a run that quietly used the
            // 640x480 original looks exactly like one that used the new one.
            Console.WriteLine(title.Exists
                ? $"Title: {TitlePicture} at {title.Width}x{title.Height}, {title.From}"
                : $"Title: no {TitlePicture} to be had, so the menu draws its own screen");

            // The theme, under the menu and nowhere else. Looped: it is a minute long and
            // somebody may sit on the title screen for longer than that.
            Audio.AudioVoice theme = Theme(audio, sounds);

            Console.WriteLine(theme.Exists
                ? $"Theme: {ThemeMusic}, under the menu"
                : $"Theme: no {ThemeMusic} to play, so the menu is silent");

            void Films(IReadOnlyList<string> which)
            {
                // The film has its own soundtrack and the theme would play under it.
                audio?.Silence(theme);
                renderer.SetBackdrop(null);

                ShowIntro(window, renderer, movies, pages, which);

                title.Show(renderer);
                theme = Theme(audio, sounds);
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
                    title.Exists ? MenuBehind.Picture : MenuBehind.Nothing,
                    () => Cut(menu: true),
                    frameLimit,
                    screenshotPath);

                if (asked == FrontEndOutcome.Intro)
                {
                    // The film, not the publisher's logo. Somebody who asked for the intro
                    // asked for the intro.
                    Films([TheIntro]);
                }
            }
            while (asked == FrontEndOutcome.Intro && !window.IsClosing);

            // Neither belongs to the game about to start: the room brings its own sound and
            // fills the window itself.
            audio?.Silence(theme);
            renderer.SetBackdrop(null);

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
            Console.WriteLine("Front end: no font, so the game starts in the room");
        }

        int result = 0;
        bool first = true;

        // One pass a room. A door is a script that says SetLocation and nothing more, so
        // going through one is this loop coming round again rather than anything the room
        // itself knows how to do.
        var finishes = SurfaceFinishes.Empty;

        while (true)
        {
            using SceneGeometry geometry = renderer.CreateGeometry();

            // What each texture's surface is like. Read once and shared by every room:
            // it is a property of the corpus, not of a scene, and it is what tells the
            // renderer that the church floor is polished and the pews are not.
            if (first)
            {
                finishes = SurfaceFinishes.Load(Path.Combine(
                    Path.GetDirectoryName(
                        CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty)
                            .TrimEnd(Path.DirectorySeparatorChar, '/')) ?? ".",
                    "manifests",
                    "material-library.json"));

                if (finishes.Count > 0)
                {
                    Console.WriteLine(
                        $"Surface finishes: {finishes.Count} textures measured, " +
                        $"{finishes.Reflective} smooth enough to reflect, " +
                        $"{finishes.Metallic} metal" +
                        (finishes.Corrected > 0
                            ? $", {finishes.Corrected} corrected by hand"
                            : string.Empty));
                }
            }

            geometry.Materials = finishes;

            // A fresh loader each time: it carries the last room's glances and its count of
            // enhanced textures, and neither belongs to the next one.
            var loader = new SceneLoader(archives, Console.WriteLine)
            {
                // The player's preference, with a command-line override so a screenshot can
                // be taken of the same room both ways without editing a settings file.
                SmoothHeads = HeadLevels(args, settings),
            };

            if (!packsOnly && settings.EnhancedTextures && enhancedDirectory is { Length: > 0 })
            {
                EnhancedTextures enhanced = EnhancedTextures.Open(enhancedDirectory);
                loader.Enhanced = enhanced;

                // Normal maps sit beside the colour textures rather than among them: a
                // surface may have a better colour and no normal map, or the other way
                // round, and they are judged separately.
                EnhancedTextures normals =
                    EnhancedTextures.Open(Beside(enhancedDirectory, "normals"));

                // --flat leaves the colour textures enhanced and the surfaces smooth,
                // which is the only way to see what the normal pass alone is doing.
                bool flat = args.Contains("--flat", StringComparer.OrdinalIgnoreCase);

                loader.Normals = flat ? null : normals;

                // The other two generated sets, beside the normals for the same reason:
                // each is a separate pass and a separate judgement, and a surface may have
                // any combination of the three.
                EnhancedTextures orms = EnhancedTextures.Open(Beside(enhancedDirectory, "orm"));
                EnhancedTextures heights =
                    EnhancedTextures.Open(Beside(enhancedDirectory, "height"));

                loader.Orms = flat ? null : orms;
                loader.Heights = flat ? null : heights;

                if (first && normals.Count > 0)
                {
                    Console.WriteLine($"Normal maps: {normals.Count} available");
                }

                if (first)
                {
                    Console.WriteLine(enhanced.Count > 0
                        ? $"Enhanced textures: {enhanced.Count} available in {enhancedDirectory}"
                        : $"Enhanced textures: none found in {enhancedDirectory}");
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
                packs);

            // The setting takes the compressed set out of the way as well as the loose one.
            // It is the same art in a smaller form, so leaving it in would answer "no" with
            // the enhanced textures still on screen.
            loader.Compressed =
                args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase) ||
                !settings.EnhancedTextures
                    ? null
                    : compressed;

            if (first && loader.Compressed is not null && compressed.Describe() is { } sets)
            {
                // Which set came from where, because the two are indistinguishable once a
                // texture is on screen: a run that quietly used a stale build/ directory
                // instead of the pack looks exactly like a run that used the pack.
                Console.WriteLine($"Compressed textures: {sets}");
            }

            var loading = Stopwatch.StartNew();

            if (loader.Load(geometry, request, diagnostics) is not { } scene)
            {
                foreach (Diagnostic diagnostic in diagnostics.Items)
                {
                    Console.Error.WriteLine(diagnostic);
                }

                audio?.Dispose();
                return 3;
            }

            // Before the report, so that it describes something that exists. Finish is
            // idempotent and the renderer calls it again when the scene is set.
            geometry.Finish();

            // With the geometry's extent, so the rig can tell a lamp that decays from the
            // scene's key light — placed tens of thousands of units away with the two
            // hundred unit range 3ds Max left in the file and its attenuation switched off.
            // Honouring that range does not dim the sun, it deletes it. See
            // GpuLight.IsDistantKey.
            renderer.SetLights(
                scene.Lights, new SceneExtent(geometry.Minimum, geometry.Maximum));
            renderer.Quality = renderer.SupportsRayTracing
                ? quality ?? settings.Quality
                : RayTracingQuality.None;

            if (first)
            {
                Console.WriteLine(renderer.SupportsRayTracing
                    ? $"Ray tracing: {renderer.Quality} ({geometry.TraceableTriangleCount} opaque "
                      + $"triangles traced in {geometry.TraceablePartCount} movable part(s))"
                    : "Ray tracing: unavailable on this device");
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Loaded {scene.Name} in {loading.Elapsed.TotalMilliseconds:F0} ms, " +
                $"{geometry.TextureCount} textures resident, {geometry.TexturesReused} reused, " +
                $"{geometry.TextureDeviceBytes / (1024.0 * 1024):F0} MB of them on the device"));

            // What this load actually read, rather than what was available to it. The counts
            // are cumulative over the session, so walking through a door adds to them.
            if (compressed.FromPacks > 0 || compressed.FromFiles > 0)
            {
                Console.WriteLine(
                    $"Blocks read: {compressed.FromPacks} from packs, "
                    + $"{compressed.FromFiles} from {(compressed.Directory.Length > 0
                        ? compressed.Directory
                        : "loose files")}");
            }

            Console.WriteLine($"Scene {scene.Name}: {geometry.TriangleCount} triangles in "
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
                Console.WriteLine(
                    $"Relief: floor cut into {geometry.DisplacedTriangles} triangles at " +
                    $"{geometry.ReliefCell:0.#} units a cell");
            }

            // The floor, which is how an actor knows what height to walk at. Reported
            // because its absence is silent: a room that names no floor object, or names
            // one the geometry does not have, walks everybody at the height they set off
            // at and looks fine until the first ramp.
            if (args.Contains("--lights", StringComparer.OrdinalIgnoreCase))
            {
                foreach (Formats.Scenes.AuthoredLight light in scene.Lights)
                {
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  light r={light.Radius:F1} i={light.Intensity:F2} " +
                        $"reach={light.AttenuationEnd:F0}"));
                }
            }

            Console.WriteLine(scene.Ground is { } ground
                ? $"Floor: {scene.Definition.FloorObject()}, {ground.Triangles} triangles"
                : $"Floor: none; {scene.Definition.FloorObject() ?? "the scene names one"}" +
                  " is not in the geometry, so actors hold the height they start at");

            Report(diagnostics, verbose);

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

            // --movie NAME plays one straight away, which is how a cutscene is looked at
            // without finding the point in the story that plays it.
            if (first && Option(args, "--movie") is { Length: > 0 } wanted)
            {
                double seconds = movies.Play(wanted);

                Console.WriteLine(seconds > 0
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

            // The faces in this room. Everybody the scene placed who has an entry in
            // FACES.TXT and is actually painted with their own face bitmap, which is what
            // tells a person from a portrait of one.
            var moving = new Game.Actors.Faces(faces, archives, api.Animations, geometry);

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
            }

            // The pose everything opens in, before anything runs. A door that starts open,
            // a character sitting down, a bag on the ground beside somebody: the scene
            // states each of those as an animation and means its first frame.
            if (update.Open() is > 0 and { } posed)
            {
                Console.WriteLine(
                    $"Opening pose: {posed} clip(s) sampled" +
                    (update.Posed.Count > 0
                        ? ", " + string.Join(", ", update.Posed.Select(m => string.Create(
                            CultureInfo.InvariantCulture,
                            $"{m.Who} at {m.Where.X:F0}, {m.Where.Z:F0}")))
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
                Console.WriteLine(
                    $"Behaviour: {update.Scenic} prop(s) move on their own, " +
                    $"{update.Fidgeting} character(s) idle, talk and listen");
            }

            Console.WriteLine(
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
            if (request.State is not null)
            {
                request.State.EnterLocation(request.State.Ego, scene.Name);
            }

            // Before the room is entered, not after. Everything the room being left was
            // saying belongs to that room; the entering script, on the other hand, may well
            // say something itself, and cutting it off a moment later is how a scripted
            // arrival loses its own first line.
            //
            // What the room sounded like is not stopped, only started on its way out, so
            // the next room's bed comes up underneath it.
            room?.Leave();

            if (scene.Actions?.Find("SCENE", "ENTER") is { } entering)
            {
                new ActionRunner(api).Run(entering);
                Console.WriteLine($"entered: SCENE:ENTER [{entering.Case}]");
            }

            // What the room sounds like when nothing is happening in it.
            if (room?.StartAmbience(scene.AmbienceRead) is { } bed)
            {
                Console.WriteLine(
                    $"Ambience: {bed}" +
                    (room.AmbienceAt is { } at
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $" at {at.Position:F0}, full within {at.Minimum:F0} units and " +
                            $"as quiet as it gets past {at.Maximum:F0}")
                        : ", at the listener"));
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

                Console.WriteLine($"Then {n.Trim()}:{v.Trim()} [{follow.Case}]");
            }

            // Arriving somewhere is the moment the story is at rest: the room is built,
            // its opening script has run, and nothing is half-done. Saving here rather
            // than on leaving means the autosave is a place the player can be put back,
            // not a doorway they were passing through.
            //
            // Never on the first room of a run, which is the one the menu just started and
            // is nothing worth keeping, and never over a save the player made.
            if (!first)
            {
                api.Saves?.Write(Game.SaveStore.AutoSlot, api.State.Capture($"Arrived at {scene.Name}"));
            }

            // The console outlives the room. Its history and its scrollback are the
            // player's working notes, and losing them at every door would make it useless
            // for exactly the thing it is for: watching one thing across a transition.
            console.Knows(api.FunctionNames);
            console.Calls = api.Perform;

            RoomExit exit = FlyScene(
                window, renderer, geometry, scene, cameraName, frameLimit, update,
                new SceneInteraction(scene, api) { Strings = strings },
                room, movies, hud, Cut, api, screens, sidney,
                map, binoculars, api.State, console,
                front, pages, Apply, args, strings);

            result = exit.Code;

            if (exit.Destination is not { Length: > 0 } next)
            {
                break;
            }

            // The geometry is about to go. Frames are still in flight reading its buffers,
            // and freeing those underneath the device is a crash somewhere else entirely.
            renderer.SetScene(null, null);
            renderer.Idle();

            request = SceneRequest.Continuing(api, next);

            // The next room has its own idea of where to stand; the camera the player named
            // belonged to the one they have left.
            cameraName = null;
            first = false;
        }

        audio?.Dispose();

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

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Motion: mean {total / pixels:F2} px, largest {most:F1} px, " +
                $"{100.0 * moving / pixels:F1}% of the frame moved more than half a pixel"));
        }

        if (screenshotPath is not null && renderer.Capture() is { } capture)
        {
            File.WriteAllBytes(screenshotPath, Formats.Bitmaps.PngWriter.Encode(capture));
            Console.WriteLine($"Wrote {screenshotPath}");
        }

        return result;
    }

    /// <summary>
    /// The fonts the interface will draw with, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order matters and so does the set.</b> The three <c>F_CAPTION</c> sizes are
    /// GK3's own caption font at 16, 20 and 26 point, which cut to 20, 26 and 33 pixel
    /// letters; <c>F_CAPTION_DEFAULT</c> is the 14-point Goudy the game used for its own
    /// subtitles. All four carry the full 181-character set, <b>including the 52 accented
    /// letters</b>.
    /// </para>
    /// <para>
    /// The <c>F_ARIAL</c> fonts at the end carry 94 characters and not one of them is
    /// accented, which is why the interface used to draw <c>H?tel de Rennes-le-Ch?teau</c>
    /// in a game set in France. They stay as a last resort for an installation missing the
    /// caption sheets; nothing else should reach them.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// All three are waitable, and what they wait for is the movie's own length — which is
    /// why <see cref="Gk3SheepApi.SecondsFor"/> has to answer for them as well as
    /// <see cref="Gk3SheepApi.Register"/> performing them. A script that plays a cutscene
    /// and then speaks would otherwise speak over it.
    /// </para>
    /// <para>
    /// A movie that will not play returns nothing to wait for, so the script carries on.
    /// The original does the same: its callback runs whether or not the video played, and
    /// a missing cutscene should cost the cutscene rather than the rest of the game.
    /// </para>
    /// <para>
    /// <c>PlayMovie</c> is the windowed form and the other two are full screen. Both are
    /// drawn the same way here — fitted to the window, letterboxed — because a window
    /// inside a window is a decision about the interface that nothing else in this port
    /// has made yet.
    /// </para>
    /// </remarks>
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

            Console.WriteLine(seconds > 0
                ? $"Movie: {name}, {seconds:F1}s"
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
    /// <remarks>
    /// The generated maps sit beside the colour textures rather than among them, because a
    /// surface may have a better colour and no normal map, or the other way round, and they
    /// are judged separately.
    /// </remarks>
    private static string Beside(string enhanced, string what) =>
        Path.Combine(
            Path.GetDirectoryName(enhanced.TrimEnd(Path.DirectorySeparatorChar, '/')) ??
                enhanced,
            what);

    /// <summary>How tall the interface's letters should be for a given display.</summary>
    /// <param name="framebufferHeight">The framebuffer's height in pixels.</param>
    /// <returns>A wanted glyph height, which the ladder is matched against.</returns>
    /// <remarks>
    /// Proportional to the display rather than fixed, because a bitmap font does not scale:
    /// the same 17-pixel sheet that is comfortable on a 480-line screen is a third the
    /// apparent size on a 1440-line one, which is exactly the complaint. 2.8% puts a
    /// 1080-line display on the 26-point rung and anything above it on the 26-point rung
    /// too, that being the largest the game shipped.
    /// </remarks>
    private static int WantedGlyphHeight(int framebufferHeight) =>
        Math.Max(12, (int)MathF.Round(Math.Max(1, framebufferHeight) * 0.028f));

    /// <summary>How tall an em should be, in pixels, for a window of a given height.</summary>
    /// <param name="framebufferHeight">How tall the window is.</param>
    /// <param name="menu">Whether this is the menu rather than the room's interface.</param>
    /// <returns>The em size to draw the outline font at.</returns>
    /// <remarks>
    /// A share of the window rather than a fixed size, so the interface is the same
    /// apparent size on every display. The menu is drawn larger than the room's captions
    /// on purpose: captions must not cover the room, and a menu is the only thing on
    /// screen.
    /// </remarks>
    private static int TextPixels(int framebufferHeight, bool menu) => Math.Max(
        menu ? 16 : 12,
        (int)MathF.Round(Math.Max(1, framebufferHeight) / (menu ? 26f : 33f)));

    /// <summary>
    /// Finds the typeface the interface draws with.
    /// </summary>
    /// <param name="named">A file named on the command line, or null.</param>
    /// <param name="enhancedDirectory">The content workspace's enhanced set, if any.</param>
    /// <param name="diagnostics">Where a font that will not read is reported.</param>
    /// <returns>The font, or null to fall back to GK3's own sheets.</returns>
    /// <remarks>
    /// <para>
    /// Three places, in the order somebody working on the game would want them:
    /// <c>--font-file</c>, then any <c>.ttf</c> or <c>.otf</c> in the workspace's
    /// <c>enhanced/fonts</c>, then the one carried inside the assembly. The last is what a
    /// shipped game uses and is why this never comes back empty on an installation that
    /// has no workspace at all.
    /// </para>
    /// <para>
    /// The embedded face is Noto Serif under the SIL Open Font Licence 1.1 — a serif,
    /// because GK3's own captions are one and a sans-serif menu in front of this game
    /// would look like somebody else's.
    /// </para>
    /// </remarks>
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
                Console.Error.WriteLine($"WARNING GK3R1201: {path} could not be read. ({error.Message})");
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
    /// <remarks>
    /// The ladder runs out at 33-pixel letters, which is the largest sheet the game
    /// shipped. Past about 1,600 lines that is small enough to be the original complaint
    /// again, and the only thing left is to draw each sheet pixel as more than one. Whole
    /// numbers only: a fractional one lands glyph edges between pixels and the sampler
    /// averages neighbouring letters into each other.
    /// </remarks>
    private static int Magnification(Formats.Ui.FontFile font, int wanted) =>
        font.Height <= 0 ? 1 : Math.Clamp((int)MathF.Round((float)wanted / font.Height), 1, 4);

    /// <summary>Loads every compiled script in the game.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="host">Where they go.</param>
    /// <param name="catalogue">
    /// Receives every function prototype the scripts name, for whatever wants to say how a
    /// call should be written. Optional: the loading works the same without one.
    /// </param>
    /// <returns>How many were loaded.</returns>
    /// <remarks>
    /// Once, before the first room. A fifth of the corpus's action statements are
    /// <c>CallSheep</c>, and a script that is not loaded is a call that does nothing — most
    /// visibly the ones that take the player from one room to the next.
    /// </remarks>
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

    /// <summary>
    /// The switches that set something going before the player takes over.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room they act on.</param>
    /// <remarks>
    /// Only in the first room. They exist for looking at one thing on purpose, and firing
    /// them again at every door would mean the player could never get away from them.
    /// </remarks>
    private static void Opening(string[] args, Gk3SheepApi api, LoadedScene scene)
    {
        // Several, separated by semicolons, because one action is often the setup for the
        // one worth looking at: inspecting a thing and then walking away from it needs both
        // to have happened before the picture is taken.
        foreach (string asked in Option(args, "--do")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            Do(asked, api, scene);
        }

        Opened(args, api, scene);
    }

    /// <summary>Performs one <c>--do</c>.</summary>
    /// <param name="asked">The action, as <c>noun:verb</c>.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room it acts on.</param>
    private static void Do(string asked, Gk3SheepApi api, LoadedScene scene)
    {
        if (asked.Split(':') is [string noun, string verb] &&
            scene.Actions?.Find(noun.Trim(), verb.Trim()) is { } rule)
        {
            ActionOutcome outcome = new ActionRunner(api).Run(rule);

            string did = outcome.Deferred
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"walking {outcome.Approaching:F1}s first, then " +
                    $"{outcome.Statements.Count} statement(s)")
                : $"{(outcome.Ran ? "ran" : "refused")} {outcome.Statements.Count} statement(s)";

            Console.WriteLine($"Doing {noun.Trim()}:{verb.Trim()} [{rule.Case}]: {did}");
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
            Console.WriteLine($"Playing {clip}");
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

            Console.WriteLine(
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
            string? about = wanted.Split(':') is [_, string subject, ..] ? subject : null;

            if (about is { Length: > 0 })
            {
                // Carried, because a screen about something the player does not have is a
                // screen the action files answer differently about.
                api.State.Inventory.Add(api.State.Ego, about);
                api.State.Inventory.SetActive(api.State.Ego, about);
            }

            api.State.Screens.Show(new Screen(kind, about));
            Console.WriteLine($"Screen: {kind}{(about is null ? string.Empty : $" ({about})")}");
        }

        // And put something into Sidney on the way in, for the same reason: its screens are
        // about files, and a screenshot of one with nothing in it shows nothing.
        if (Option(args, "--scan") is { Length: > 0 } scanning && api.Sidney is { } machine)
        {
            foreach (string item in scanning.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (machine.Scan(item.Trim()) is { } scanned)
                {
                    Console.WriteLine($"Scanned: {scanned.Text}");
                }
            }

            if (machine.Files.Count > 0)
            {
                machine.OpenFile(machine.Files[0]);
            }
        }

        if (Option(args, "--sidney") is { Length: > 0 } page && api.Sidney is { } opened &&
            Enum.TryParse(page, ignoreCase: true, out Game.Sidney.SidneyScreen which))
        {
            opened.Screen = which;
            Console.WriteLine($"Sidney: {which}");
        }

        if (Option(args, "--glide") is { Length: > 0 } destination)
        {
            Sheep.SheepExpression.Evaluate(
                $"GlideToCameraAngle(\"{destination}\")", api);
            Console.WriteLine($"Gliding to {destination}");
        }

        if (Option(args, "--glance")?.Split(':') is [string who, string at])
        {
            Sheep.SheepExpression.Evaluate(
                $"LookitActor(\"{who.Trim()}\", \"{at.Trim()}\", \"\", 0)",
                api);

            foreach (Diagnostic diagnostic in api.Diagnostics.Items)
            {
                Console.WriteLine($"  {diagnostic}");
            }
        }
    }

    /// <summary>Hands a frame's keyboard to the console.</summary>
    /// <param name="input">Where the keys come from.</param>
    /// <param name="console">What reads them.</param>
    /// <remarks>
    /// Everything the console does with a key is a method on it, so this is a routing table
    /// and nothing else. Which is the point: the console has no idea what a keyboard is, and
    /// a test can drive it without one.
    /// </remarks>
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
    /// <param name="sidney">Grace's computer, which one of those screens is.</param>
    /// <param name="map">The driving map's art and roads.</param>
    /// <param name="binoculars">What can be seen from here, if anything.</param>
    /// <param name="story">Where the story stands, for the inventory strip.</param>
    /// <param name="console">The developer console, which outlives the room.</param>
    /// <param name="front">The menu, which Escape opens.</param>
    /// <param name="pages">What draws it, or null when there is no font to draw with.</param>
    /// <param name="apply">What to do with a setting the moment it changes.</param>
    /// <param name="options">The command line, for the debugging switches.</param>
    /// <param name="strings">
    /// What the game calls places and times, for the corner of the screen.
    /// </param>
    /// <returns>Why the room was left, and where for.</returns>
    /// <remarks>
    /// The loop drives the world as well as the view: <see cref="SceneUpdate.Advance"/> is
    /// given the frame's elapsed time, so a head that was told to look at something turns
    /// while the player watches rather than having always been turned.
    /// </remarks>
    private static RoomExit FlyScene(
        Platform.SilkGameWindow window,
        VulkanRenderer renderer,
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
        Game.Sidney.SidneyMachine? sidney,
        DrivingMap map,
        Binoculars binoculars,
        GameState story,
        GameConsole console,
        FrontEnd front,
        MenuPage? pages,
        Action<Settings> apply,
        string[] options,
        GameStrings strings)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(cut);
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(apply);

        string here = scene.Name;

        int cameraIndex = Math.Max(
            0,
            scene.Cameras.ToList().FindIndex(c => string.Equals(
                c.Name, cameraName ?? scene.CameraNamed(null)?.Name, StringComparison.OrdinalIgnoreCase)));

        Camera template = SceneLoader.CameraFor(scene, geometry, cameraName);

        var camera = new FreeCamera
        {
            Speed = MathF.Max(50f, (geometry.Maximum - geometry.Minimum).Length() * 0.15f),
        };

        // The shell the scene's artists drew around the space the camera may occupy. Without
        // it the player can walk the view out through a wall and look at the room from
        // behind, which is a picture no part of the game was built to survive.
        // --free-camera gives that back, because looking at the geometry from outside is
        // exactly how some of it gets checked.
        if (scene.CameraShell is not { IsEmpty: false } shell)
        {
            Console.WriteLine("Camera bounds: none, so the camera may go anywhere");
        }
        else if (options.Contains("--free-camera", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Camera bounds: off, so the camera may leave the room");
        }
        else
        {
            camera.Confine = shell.Resolve;

            // A viewpoint outside its own shell is not fatal — the way back in is always
            // open — but it is worth saying, because from out there the walls behave
            // backwards and there is nothing on screen to explain why.
            if (!shell.Contains(template.Position))
            {
                Console.WriteLine($"Camera bounds: {scene.Name}'s view starts outside them");
            }
        }

        camera.CopyFrom(template);

        // A room reached by leaning in through the binoculars starts at the camera the
        // binoculars named rather than at the room's own. Taken once, because it describes
        // an arrival rather than a place.
        if (api.WantedCamera is { } leaned)
        {
            api.WantedCamera = null;

            camera.Position = leaned.Position;
            camera.Aim = leaned.Angle;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Arrived through the binoculars, at {leaned.Position:F0} looking {leaned.Angle.X:F0}"));
        }

        Console.WriteLine();
        Console.WriteLine("WASD to move, E and Q for up and down, drag to look,");
        Console.WriteLine("Tab for the next camera, R to return to it, F2 for ray tracing,");
        Console.WriteLine("click to act on what is under the pointer, right-click to see");
        Console.WriteLine("everything it answers to, Escape to leave.");
        Console.WriteLine("` opens the console; Tab completes, up and down move the list.");

        // Where the scene opened, so a glide has somewhere to leave from rather than
        // arriving the moment it is asked for.
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

        if (pinned is { } spot)
        {
            Console.WriteLine($"Pointer pinned at {spot.X}, {spot.Y}");
        }

        // What the interface was laid out for. A window that goes fullscreen doubles its
        // height, and a bitmap font cannot follow it by scaling — the sheet has one size —
        // so the ladder has to be re-picked and the atlas rebuilt.
        int laidOutFor = window.FramebufferHeight;

        bool flicker = options.Contains("--flicker", StringComparer.OrdinalIgnoreCase);
        byte[]? previousFrame = null;
        double flickerTotal = 0;
        int flickerFrames = 0;

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();

            double now = stopwatch.Elapsed.TotalSeconds;
            float delta = (float)Math.Min(0.1, now - previous);
            previous = now;

            // A window that goes fullscreen doubles in height. An outline is re-cut at
            // the new size; a bitmap sheet can only step up the ladder and be magnified.
            if (hud is not null && window.FramebufferHeight != laidOutFor)
            {
                laidOutFor = window.FramebufferHeight;

                if (cut(false) is { } grown)
                {
                    int magnify = grown.Scalable || grown.Font is null
                        ? 1
                        : Magnification(grown.Font, WantedGlyphHeight(laidOutFor));

                    if (!grown.Scalable &&
                        grown.Name.Equals(hud.Overlay.Atlas.Name, StringComparison.Ordinal))
                    {
                        // The sheet is right and only the magnification wrong, which costs
                        // a field rather than a rebuild.
                        hud.Overlay.Magnify = magnify;
                    }
                    else
                    {
                        hud.Retarget(grown);
                        hud.Overlay.Magnify = magnify;

                        Console.WriteLine(
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
            bool typing = console.Open;

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
                    Console.Error.WriteLine(movies.Diagnostics.Items[saidAboutMovies]);
                }
            }
            else if (showingMovie)
            {
                // Once, on the frame after it ended, rather than every frame afterwards.
                renderer.SetMovieFrame(null);
                showingMovie = false;
            }

            if (!typing && !movies.Playing && window.WasPressed(Platform.CameraAction.Quit))
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

                FrontEndOutcome chose = ShowMenu(
                    window, renderer, pages, front, apply, MenuBehind.Room, () => cut(true));

                // The room has been standing still behind the menu and the clock has not.
                // Without this the first frame back advances everything by however long the
                // player spent in the settings.
                previous = stopwatch.Elapsed.TotalSeconds;

                if (chose is FrontEndOutcome.Quit)
                {
                    break;
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

                Console.WriteLine($"camera: {scene.Cameras[cameraIndex].Name}");
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

            if (!typing && window.WasPressed(Platform.CameraAction.QuickSave))
            {
                bool wrote = api.Saves?.Write(
                    Game.SaveStore.QuickSlot, story.Capture("Quick save")) ?? false;

                Console.WriteLine(wrote ? "Saved." : "Could not save.");
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
                    Console.WriteLine("No quick save to load.");
                    console.Print("No quick save to load.");
                }
                else
                {
                    story.Restore(loaded);
                    api.Wanted = loaded.Location;

                    Console.WriteLine($"Loaded: {loaded.Summary}");
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
                Console.WriteLine($"ray tracing: {renderer.Quality}");
            }

            // What the world could not do, said once. Animation naming is the sort of thing
            // that fails by nothing happening, which is indistinguishable from nothing
            // having been asked for.
            for (; said < update.Diagnostics.Items.Count; said++)
            {
                Console.WriteLine($"  {update.Diagnostics.Items[said]}");
            }

            foreach (string happened in update.Advance(delta))
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{stopwatch.Elapsed.TotalSeconds:F2}s] {happened}"));
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
            }

            if (!console.Open)
            {
                camera.Update(window, delta);
            }

            Camera view = camera.ToCamera(template);

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

            // What the player sees, not the noun behind it: the numbered exits are drawn
            // as the place they lead to, and a log that says EXIT3 cannot be matched
            // against a screenshot that says "Outside Church".
            if (hover.Label != hovering)
            {
                hovering = hover.Label;

                if (hovering is { Length: > 0 })
                {
                    Console.WriteLine(hover.Actionable
                        ? $"> {hovering} — click to {hover.Default}"
                        : $"> {hovering} — nothing to do with it here");
                }
            }

            // --pointer puts it somewhere fixed, which is the only way to photograph the
            // interface: the label follows the mouse, and a headless run has never moved it.
            Vector2 pointer = aimed;

            // --menu opens it without a right-click, for the same reason --pointer exists.
            if (forceMenu && menu is null && hover.Actionable)
            {
                menu = hover;
                menuAt = pointer;
                menuIndex = 0;
            }

            if (!console.Open && window.WasClicked(Platform.PointerButton.Secondary))
            {
                // The menu belongs to the thing it was opened over, not to wherever the
                // pointer wanders next, so what was under it is kept — and so is where it
                // was, because a menu that follows the pointer cannot be clicked.
                menu = menu is null && hover.Actionable ? hover : null;
                menuAt = pointer;
                menuIndex = 0;

                // Asking and getting nothing has to look different from asking and being
                // ignored, or a room where nothing answers is indistinguishable from a
                // right-click that did not register.
                if (menu is null)
                {
                    Console.WriteLine(hover.Noun is { Length: > 0 } asked
                        ? $"{asked} answers to nothing here and now"
                        : "nothing under the pointer");
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

            // A screen in front of the room takes the frame: it draws instead of the room's
            // interface and it takes the click. Nothing behind it is hovered, walked to or
            // acted on, which is what modal means and what stops a click on Sidney's menu
            // also being a click on the floor behind it.
            if (screens is not null && story.Screens.Top is { } panel)
            {
                Panorama seen = binoculars.For(scene.Name, story.Timeblock.ToString());

                if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) &&
                    screens.HitAt(pointer) is { Length: > 0 } chose)
                {
                    // Leaning in is a camera and, often, another room, so it is handled
                    // here where both are in reach rather than in OnScreen.
                    if (chose.StartsWith("sidney:shape:", StringComparison.Ordinal) &&
                        sidney is not null &&
                        Enum.TryParse(chose[13..], ignoreCase: true, out Game.Sidney.MapShape picked))
                    {
                        console.Print(sidney.LayShape(picked).Text);
                    }
                    else if (chose == "sidney:mark" && sidney is not null &&
                        screens.MapBounds is { Z: > 0 } drawn)
                    {
                        // Back into the map's own 1,368 pixels, so a mark means the same
                        // place whatever size the window is.
                        float across = Game.Sidney.SidneyMap.Extent / drawn.Z;

                        console.Print(sidney.Mark(new System.Numerics.Vector2(
                            (pointer.X - drawn.X) * across,
                            (pointer.Y - drawn.Y) * across)).Text);
                    }
                    else if (chose.StartsWith("zoom:", StringComparison.Ordinal) &&
                        seen.Sights.FirstOrDefault(s => s.Location == chose[5..]) is { } sight)
                    {
                        story.Screens.Back();

                        Console.WriteLine($"Binoculars: {sight.Location}");

                        if (!string.Equals(sight.Scene, scene.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // Another room. Where the camera stands there travels with the
                            // request, because the room has not been built yet.
                            api.Wanted = sight.Scene;
                            api.WantedCamera = (sight.Position, sight.Angle);
                        }
                        else
                        {
                            camera.Position = sight.Position;
                            camera.Aim = sight.Angle;
                        }
                    }
                    else if (chose.StartsWith("verb:", StringComparison.Ordinal) &&
                             panel.Subject is { Length: > 0 } about &&
                             scene.Actions?.Find(about, chose[5..], story.Ego) is { } onItem)
                    {
                        // The item's own action, run where it was written to run: with the
                        // inventory still on top, because that is what its case asked about
                        // and a script may well close the screen itself.
                        ActionOutcome ran = new ActionRunner(api).Run(onItem);

                        Console.WriteLine(
                            $"{about}:{chose[5..]} [{onItem.Case}] - " +
                            $"{(ran.Ran ? "ran" : "refused")} {ran.Statements.Count} statement(s)");
                    }
                    else
                    {
                        OnScreen(chose, story, sidney, update, console);
                    }
                }

                // Sidney's search box is the one place in the game the player types into
                // that is not the console, so the keys go there while it is showing.
                if (!console.Open &&
                    sidney is { Screen: Game.Sidney.SidneyScreen.Search } typing2 &&
                    panel.Kind == ScreenKind.Sidney)
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
                        typing2.Look();
                    }
                }

                if (!console.Open && window.WasPressed(Platform.CameraAction.Quit))
                {
                    story.Screens.Back();
                }

                // The binoculars are the one screen the player still looks *through*, so
                // the camera keeps taking their input while it is raised.
                if (panel.Kind == ScreenKind.Binoculars && !console.Open && !typing)
                {
                    camera.Update(window, (float)delta);
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
                        ItemVerbs(panel, scene, story)),
                    window.FramebufferWidth,
                    window.FramebufferHeight);

                renderer.SetOverlay(screens.Overlay);

                window.EndFrame();
                renderer.SetScene(geometry, view);

                // Counted like any other frame, so a run with a frame limit still ends and
                // its screenshot is of the screen rather than of the room behind it.
                if (renderer.DrawFrame(0f, 0f, 0f))
                {
                    presented++;
                }

                continue;
            }

            // The strip along the foot of the screen is the inventory, so a click on it is
            // a click on what the player is carrying rather than on the room behind it.
            // Once to take a thing in hand, again to look at it closely — which is where
            // its own verbs live, because the action files guard every one of them behind
            // "the inventory is what you are looking at".
            if (!console.Open &&
                window.WasClicked(Platform.PointerButton.Primary) &&
                hud?.ItemAt(pointer) is { Length: > 0 } clicked)
            {
                if (string.Equals(
                        story.Inventory.ActiveItemOf(story.Ego),
                        clicked,
                        StringComparison.OrdinalIgnoreCase))
                {
                    story.Screens.Show(new Screen(ScreenKind.InventoryInspect, clicked));
                    Console.WriteLine($"inventory: looking at {clicked}");
                }
                else
                {
                    story.Inventory.SetActive(story.Ego, clicked);
                    Console.WriteLine($"inventory: holding {clicked}");
                }

                menu = null;
            }
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary))
            {
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

                ActionOutcome? did = menu is { } open
                    ? chosenRow is { Length: > 0 } && !openingBag
                        ? interaction.Do(open, chosenRow, hurry)
                        : null
                    : interaction.Do(hover, hurry: hurry);

                // Nothing to do to the thing clicked, so ask whether it was the ground and
                // go there. Three things have to be true. No verb menu was open, because
                // the click that closes a menu means "not that after all" and would
                // otherwise be impossible to make without crossing the room. The pointer
                // was not on the interface: the inventory strip lies across the foot of
                // the screen, exactly where the floor at the player's feet is drawn, and a
                // click on it must not go through. And the ray reached the floor.
                if (did is null &&
                    menu is null &&
                    hud?.OverInterface(pointer) != true &&
                    interaction.FloorTarget(hover) is { } ground)
                {
                    double crossing = update.Walk(story.Ego, ground, hurry: hurry);

                    Console.WriteLine(crossing > 0
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"{story.Ego}: walking to {ground.X:F0}, {ground.Z:F0}, {crossing:F1}s")
                        : $"{story.Ego}: nowhere to walk from here");
                }

                if (menu is not null && !openingBag)
                {
                    menu = null;
                }

                if (did is { } outcome)
                {
                    Console.WriteLine(
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

            // The device is the clock for dialogue: the next line of a voice-over starts
            // when the last one's source stops, so they never overlap and never drift.
            room?.Update(delta);

            if (room?.Caption is { Length: > 0 } caption && caption != spoken)
            {
                spoken = caption;
                Console.WriteLine($"  {room.Speaker}: {caption}");
            }

            // Nothing of the room is drawn over a movie: not the caption of whatever was
            // being said when it started, not the noun under the pointer, not the
            // inventory. The original stops for a cutscene and so does this.
            if (movies.Playing)
            {
                renderer.SetOverlay(null);
            }
            else if (hud is not null)
            {
                Hover showing = menu ?? hover;

                hud.Build(
                    new HudState(
                        showing.Label,
                        [.. showing.Actions
                            .Where(a => !IsAnItem(a.LocalizedVerb, scene.Actions?.Verbs))
                            .Select(a => a.LocalizedVerb)],
                        hover.Default,
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
                        [.. showing.Actions
                            .Where(a => IsAnItem(a.LocalizedVerb, scene.Actions?.Verbs))
                            .Select(a => a.LocalizedVerb)]),
                    window.FramebufferWidth,
                    window.FramebufferHeight);

                renderer.SetOverlay(hud.Overlay);
            }

            // A door is a script that says SetLocation and nothing more. Noticing it here
            // rather than inside the action means it works however the story asked —
            // clicked, on a timer, or from a script three calls deep.
            if (!string.Equals(story.Location, here, StringComparison.OrdinalIgnoreCase) &&
                story.Location is { Length: > 0 } elsewhere)
            {
                Console.WriteLine($"Leaving {here} for {elsewhere}");

                // Nothing this room was still holding back gets to happen in the next one.
                // What is queued is an action script belonging to the room being left, and
                // letting one run through a door is how it opens twice.
                update.Cancel();

                return new RoomExit(0, elsewhere);
            }

            window.EndFrame();

            renderer.SetScene(geometry, view);

            if (renderer.DrawFrame(0f, 0f, 0f))
            {
                presented++;
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
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Flicker: {flickerTotal / flickerFrames:F3} of an eight-bit step between " +
                $"frames, over {flickerFrames} frames"));
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Presented {presented} frames in {stopwatch.Elapsed.TotalSeconds:F1}s "
            + $"({presented / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0} fps)"));

        return new RoomExit(0, null);
    }

    /// <summary>The game's own title screen.</summary>
    /// <remarks>
    /// 640x480 in the archives, and the only piece of GK3's interface art this port keeps:
    /// it is a painting of an angel with the game's name in it, not a widget with a label
    /// baked into one language. A replacement in <c>enhanced/textures</c> is preferred if
    /// somebody makes one, exactly as for every other texture in the game.
    /// </remarks>
    private const string TitlePicture = "TITLE.BMP";

    /// <summary>The music under the menu.</summary>
    /// <remarks>
    /// The game's own theme, which is the largest sound in the archives and is played
    /// nowhere else: it belongs to the title screen and always has.
    /// </remarks>
    private const string ThemeMusic = "THEME.WAV";

    /// <summary>Finds the title art.</summary>
    /// <param name="archives">The game's own.</param>
    /// <param name="enhanced">A higher-resolution set, or null.</param>
    /// <param name="compressed">The block-compressed set, packs included, or null.</param>
    /// <param name="diagnostics">Where a picture that will not decode is reported.</param>
    /// <returns>The picture and where it came from; empty when there is none to be had.</returns>
    /// <remarks>
    /// In the order somebody working on the picture would want: the loose file they are
    /// editing, then the compressed build or the pack, then the original in the archives.
    /// A shipped game has only the last two, and <c>--rebarn</c> is that game.
    /// </remarks>
    private static TitleScreen TitleArt(
        GameArchives archives,
        EnhancedTextures? enhanced,
        CompressedTextures? compressed,
        DiagnosticBag diagnostics)
    {
        string bare = Path.GetFileNameWithoutExtension(TitlePicture);

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
            return archives.Read(TitlePicture) is { } bytes
                ? new TitleScreen(
                    Formats.Bitmaps.BitmapDecoder.Decode(bytes, TitlePicture),
                    null,
                    "from the archives")
                : default;
        }
        catch (FormatException error)
        {
            // A menu without its picture is a menu; a game that will not start because a
            // decorative bitmap is malformed is not.
            Console.Error.WriteLine($"WARNING GK3R3430: {TitlePicture} would not decode. ({error.Message})");
            return default;
        }
    }

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
        public void Show(VulkanRenderer renderer)
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
    /// <returns>What the player asked for.</returns>
    /// <remarks>
    /// <para>
    /// A loop of its own rather than a mode of the room's. Nothing of the room advances
    /// while it runs, which is what pausing means, and the frame it draws is the same
    /// picture the room left on screen with the menu over it.
    /// </para>
    /// <para>
    /// Three ways to work it, all live at once: the arrow keys and Enter, the pointer, and
    /// dragging a slider. A menu that can only be used one way is a menu somebody cannot
    /// use.
    /// </para>
    /// </remarks>
    private static FrontEndOutcome ShowMenu(
        Platform.SilkGameWindow window,
        VulkanRenderer renderer,
        MenuPage pages,
        FrontEnd front,
        Action<Settings> apply,
        MenuBehind behind,
        Func<OverlayAtlas?> cut,
        int frames = 0,
        string? photograph = null)
    {
        FrontEndPage showing = front.Page;
        int laidOutFor = window.FramebufferHeight;

        pages.Behind = behind;

        Place(pages, front, behind);
        pages.Reset(front.Items);

        int drawn = 0;

        while (!window.IsClosing)
        {
            window.PumpEvents();

            IReadOnlyList<MenuItem> items = front.Items;

            // A window that goes fullscreen doubles in height, and a menu that stayed the
            // size it was laid out at would be a postage stamp in the middle of it. An
            // outline is re-cut for the new size; a sheet is magnified to reach it.
            if (window.FramebufferHeight != laidOutFor)
            {
                laidOutFor = window.FramebufferHeight;

                if (pages.Overlay.Atlas.Scalable && cut() is { } again)
                {
                    pages.Retarget(again);
                }
            }

            pages.Overlay.Magnify = pages.Overlay.Atlas.Scalable
                ? 1
                : MenuMagnification(window.FramebufferHeight, pages.Overlay.Atlas.Height);

            Vector2 pointer = new(
                window.PointerPosition.X * window.DpiScale,
                window.PointerPosition.Y * window.DpiScale);

            MenuAction action = MenuAction.None;

            if (window.WasPressed(Platform.EditKey.Up))
            {
                pages.Move(items, -1);
            }

            if (window.WasPressed(Platform.EditKey.Down))
            {
                pages.Move(items, 1);
            }

            if (window.WasPressed(Platform.EditKey.Left))
            {
                action = pages.Chose(items, -1);
            }

            if (window.WasPressed(Platform.EditKey.Right))
            {
                action = pages.Chose(items, 1);
            }

            if (window.WasPressed(Platform.EditKey.Enter))
            {
                action = pages.Chose(items);
            }

            if (window.WasClicked(Platform.PointerButton.Primary))
            {
                action = pages.Click(pointer, items);
            }
            else if (window.IsDragging && pages.Drag(pointer, items) is { Happened: true } dragged)
            {
                // Held rather than clicked: a volume is set by ear, which means hearing it
                // move rather than hearing where it landed.
                action = dragged;
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
                    Console.WriteLine($"Settings: written to {Settings.DefaultPath}");
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
                pointer);

            renderer.SetOverlay(pages.Overlay);

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

                    Console.WriteLine($"Wrote {photograph}");
                }

                return FrontEndOutcome.Quit;
            }
        }

        front.Commit();
        return FrontEndOutcome.Quit;
    }

    /// <summary>How much to draw the menu's letters at.</summary>
    /// <param name="framebufferHeight">How tall the window is, in pixels.</param>
    /// <param name="glyphHeight">How tall the sheet's letters are.</param>
    /// <returns>A whole-number magnification, one or more.</returns>
    /// <remarks>
    /// A menu is not a caption. Captions are sized to be readable without covering the
    /// room; a menu is the only thing on screen, and one drawn at caption size on a large
    /// display reads as a dialogue box from another decade. A row comes out at about a
    /// twenty-second of the window's height, which is roughly what the original's own
    /// buttons were on the screen they were drawn for.
    /// </remarks>
    private static int MenuMagnification(int framebufferHeight, int glyphHeight) =>
        Math.Max(1, (int)MathF.Round(framebufferHeight / 22f / Math.Max(1, glyphHeight)));

    /// <summary>
    /// Puts the page where it does not cover what is behind it.
    /// </summary>
    /// <param name="pages">The page.</param>
    /// <param name="front">Which page is showing.</param>
    /// <param name="behind">What is behind it.</param>
    /// <remarks>
    /// Down in the left-hand corner over the title art, whose lettering is to the right of
    /// the angel: a menu that covers the name of the game it is the menu for is not a title
    /// screen. The settings pages are taller and wider, and centre themselves again.
    /// </remarks>
    private static void Place(MenuPage pages, FrontEnd front, MenuBehind behind)
    {
        bool overArt = behind == MenuBehind.Picture && front.Page == FrontEndPage.Main;

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
    /// <remarks>
    /// <para>
    /// Enter, or the left button <em>held</em>. A click is what somebody does by accident
    /// while the machine is still settling down, and losing the opening of the game to a
    /// stray mouse is worse than holding a button for half a second. Escape works too, on
    /// the grounds that it is the first thing half the world will try.
    /// </para>
    /// <para>
    /// <b>Skipping ends the film showing and not the sequence.</b> The logo and the intro
    /// are two different things to sit through: somebody who skips the publisher's logo has
    /// said nothing at all about whether they want to watch the opening of the game. So a
    /// cold start is two skips, and the button has to be let go between them — a hold that
    /// carried across the join would take the second film with the first.
    /// </para>
    /// <para>
    /// Missing films are passed over in silence, because an installation that has none
    /// should still reach the menu.
    /// </para>
    /// </remarks>
    private static void ShowIntro(
        Platform.SilkGameWindow window,
        VulkanRenderer renderer,
        Game.MoviePlayer movies,
        MenuPage? hint,
        IReadOnlyList<string> films)
    {
        // Long enough not to fire on a click, short enough that nobody wonders whether it
        // is working — and it says so on screen while it counts.
        const double HoldToSkip = 0.6;

        // How long the way out stays on screen at the start of each film. Said and then
        // out of the way: it is over the opening of the game.
        const double SayFor = 6.0;

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

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"Intro: {name}, {movies.Seconds:F1}s"));

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
                    held >= HoldToSkip)
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

                if (hint is not null && (held > 0 || now - began < SayFor))
                {
                    hint.Skipping(
                        "Hold the mouse button or press Enter to skip",
                        (float)(held / HoldToSkip),
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
                Console.Error.WriteLine(diagnostic);
            }

            if (window.IsClosing)
            {
                return;
            }

            if (skipped)
            {
                // Said, but not obeyed for the rest of them: the next film is a different
                // thing to have decided about.
                Console.WriteLine($"Intro: {name} skipped");
            }
        }
    }

    /// <summary>
    /// Hands the driving map's own pictures to the interface.
    /// </summary>
    /// <param name="archives">The game's data, which is where the art is.</param>
    /// <param name="renderer">What holds the pictures.</param>
    /// <param name="screens">What draws them, and needs to know how big each one is.</param>
    /// <param name="enhanced">Upscaled textures to prefer, or null to use the archives'.</param>
    /// <remarks>
    /// <para>
    /// Seventeen pictures — the map and its sixteen markers — read once at startup and
    /// kept. A 640-by-480 painting reloaded every time somebody opens the map would be a
    /// stall the player can feel, and together they are under a megabyte.
    /// </para>
    /// <para>
    /// <b>The size recorded is always the archive's, whatever is drawn.</b> The map is laid
    /// out in the 640-by-480 pixels the original was built in, and every marker's position
    /// is a coordinate in that space; an upscaled marker is the same marker at more
    /// samples, not a bigger one. Recording the enhanced size would put the markers in the
    /// wrong places by a factor of thirty-two.
    /// </para>
    /// </remarks>
    private static void LoadMapArt(
        GameArchives archives,
        VulkanRenderer renderer,
        ScreenPainter screens,
        EnhancedTextures? enhanced)
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

                Formats.Bitmaps.DecodedImage? better = enhanced?.Read(key);

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
            Console.WriteLine(
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
    /// <remarks>
    /// The painter knows where things are and this knows what they do. Keeping the two
    /// apart is what lets the screens be laid out fresh every frame without any rule about
    /// the game living in the drawing.
    /// </remarks>
    private static void OnScreen(
        string chose,
        GameState story,
        Game.Sidney.SidneyMachine? sidney,
        SceneUpdate update,
        GameConsole console)
    {
        ArgumentNullException.ThrowIfNull(update);

        string[] parts = chose.Split(':');

        switch (parts[0])
        {
            case "close":
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

            case "drive" when parts.Length > 1:
                story.Screens.CloseAll();
                story.Location = parts[1];
                break;

            case "sidney" when sidney is not null && parts.Length > 2:
                OnSidney(parts[1], parts[2], story, sidney, console);
                break;

            case "sidney" when sidney is not null && parts.Length > 1 && parts[1] == "home":
                sidney.Home();
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
    /// <remarks>
    /// Asked while the close-up is already on the stack, which is the whole trick: every
    /// one of these actions is guarded by <c>ALL_INV</c> or one of its two ego-specific
    /// forms, and all three are <c>IsTopLayerInventory()</c>. Resolving them from the room
    /// answers "no" to every one.
    /// </remarks>
    private static IReadOnlyList<string>? ItemVerbs(
        Screen panel, LoadedScene scene, GameState story)
    {
        if (panel.Kind != ScreenKind.InventoryInspect ||
            panel.Subject is not { Length: > 0 } item ||
            scene.Actions is not { } actions)
        {
            return null;
        }

        return [.. actions
            .Resolve(item, story.Ego, story.Inventory.ItemsOf(story.Ego))
            .Select(a => a.LocalizedVerb)];
    }

    /// <summary>Whether a verb is a thing in the bag rather than something to do.</summary>
    /// <param name="verb">The verb an action file wrote.</param>
    /// <param name="verbs">What the game says each verb is.</param>
    /// <returns>True for an inventory item.</returns>
    /// <remarks>
    /// The two are written identically — <c>BUTHANE, WALLET, MET_BUTHANE</c> looks exactly
    /// like <c>BUTHANE, LOOK, ALL</c> — and only <c>VERBS.TXT</c> tells them apart. Without
    /// it a menu offers "Wallet" beside "Look" as though they were the same kind of thing.
    /// </remarks>
    private static bool IsAnItem(string verb, Game.Actions.VerbLibrary? verbs) =>
        verbs?.KindOf(verb) == Game.Actions.VerbKind.Inventory;

    /// <summary>What a click inside Sidney means.</summary>
    private static void OnSidney(
        string what,
        string which,
        GameState story,
        Game.Sidney.SidneyMachine sidney,
        GameConsole console)
    {
        switch (what)
        {
            case "screen" when Enum.TryParse(which, out Game.Sidney.SidneyScreen screen):
                sidney.Screen = screen;
                sidney.OpenFile(null);
                break;

            case "home":
                sidney.Home();
                break;

            case "mail":
                sidney.Reading = sidney.Library.Mail().FirstOrDefault(m => m.Id == which);
                break;

            // Scanning runs the game's own action as well as making the file. The action is
            // what marks the item used and sets SidScanner; the file is what
            // DoesSidneyFileExist reads, and nothing made one before this existed.
            case "scan":
                if (sidney.Scan(which) is { } scanned)
                {
                    console.Print(scanned.Text);
                    story.IncrementNounVerbCount(which, "SCANNER");
                }

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
                    sidney.Library.Suspects().FirstOrDefault(s => s.Index == index));

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
                    .FirstOrDefault(i => i.Title == which) is { } identity:
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

    /// <summary>
    /// Where a screen may take the player from here.
    /// </summary>
    /// <param name="screen">Which screen is asking.</param>
    /// <param name="scene">The room.</param>
    /// <param name="story">The game.</param>
    /// <returns>The places, which is empty where the screen is not about going anywhere.</returns>
    /// <remarks>
    /// The driving map offers the rooms the player has already been to, which is the
    /// honest answer this engine can give: the original's map is a bitmap with its hotspots
    /// compiled into the executable, and inventing a list would be inventing where the
    /// story allows somebody to go.
    /// </remarks>
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

    /// <summary>Why a room was left.</summary>
    /// <param name="Code">Process exit code, if this is the end of it.</param>
    /// <param name="Destination">Where the story went, or null when the player quit.</param>
    private readonly record struct RoomExit(int Code, string? Destination);

    /// <summary>
    /// Makes sure the scene is loaded at a point in the story, not merely at a time of day.
    /// </summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    /// <param name="timeblock">What the player asked for.</param>
    /// <returns>A request with a story behind it, where the room has one.</returns>
    /// <remarks>
    /// <para>
    /// <c>202P</c> is a point in the story and <c>N</c> is an asset suffix meaning night.
    /// Both are legitimate — see <see cref="SceneRequest"/> — and the render tooling wants
    /// the second, because looking at a room's night lighting should not require inventing
    /// a story to justify it.
    /// </para>
    /// <para>
    /// A game is different. With no story state the scene's conditions go undecided, no
    /// action files come into scope and no soundtrack is chosen: every object in the room
    /// answers to nothing and nobody says a word. That is a room, not a game, and it looks
    /// exactly like a broken one. So the launcher takes a real timeblock instead and says
    /// which, rather than running a version of the game where nothing can be done.
    /// </para>
    /// </remarks>
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
            Console.WriteLine(
                $"Story: {scene} has no timeblock of its own, so its conditions stay " +
                "undecided and its objects answer to nothing.");

            return asked;
        }

        string chosen = known[0];

        Console.WriteLine(timeblock is { Length: > 0 } asOfDay
            ? $"Story: '{asOfDay}' is a time of day, not a point in the story, so nothing " +
              $"in the room would answer to anything. Using {chosen} instead."
            : $"Story: no timeblock given, so nothing in the room would answer to " +
              $"anything. Using {chosen}.");

        Console.WriteLine($"  {scene} knows: {string.Join(" ", known)}");

        return SceneRequest.For(scene, chosen);
    }

    /// <summary>The story timeblocks a scene has a file for.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    /// <returns>The codes, in order.</returns>
    /// <remarks>
    /// A scene's second file is named for the location and the timeblock together —
    /// <c>R25202P.SIF</c> — so the timeblocks a room has are what is left after its own
    /// name. The room's own <c>R25.SIF</c> leaves nothing and is skipped.
    /// </remarks>
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
    /// <remarks>
    /// For screenshots and for saying what is under a place without having to be there
    /// with a mouse. The rest of the loop cannot tell the difference, which is the point:
    /// what it photographs is what a player at that spot would see.
    /// </remarks>
    private static Vector2? Pinned(string[] args) =>
        Option(args, "--pointer")?.Split(',') is [string x, string y] &&
        float.TryParse(x, CultureInfo.InvariantCulture, out float px) &&
        float.TryParse(y, CultureInfo.InvariantCulture, out float py)
            ? new Vector2(px, py)
            : null;

    /// <summary>How far to subdivide a character's head.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="settings">What the player chose.</param>
    /// <returns>The number of levels, within range.</returns>
    /// <remarks>
    /// <c>--heads N</c> sets it and <c>--flat-heads</c> is <c>--heads 0</c>, which is the
    /// 1999 outline. Anything unreadable falls back to the setting rather than to a guess:
    /// a typo should not silently change what the picture is being compared against.
    /// </remarks>
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
    private static string? Option(string[] args, string name)
    {
        int at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        if (at < 0 || at + 1 >= args.Length)
        {
            return null;
        }

        string next = args[at + 1];

        // The next flag is not this one's value. `--start --rt high` means "start where the
        // game starts, and trace at high", not "open the room called --rt" — and taking it
        // as a room name is a failure a long way from the mistake, after a window has
        // opened and a menu has been sat through.
        return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
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

    /// <summary>Where the block-compressed build of the enhanced textures sits.</summary>
    /// <remarks>
    /// Beside the enhanced set rather than under it, because it is a build output and not a
    /// source: <c>build/textures</c> and <c>build/normals</c> in the same workspace. There
    /// is no separate flag to ask for it — anybody who has asked for enhanced textures wants
    /// the cheap form of them — but <c>--uncompressed</c> turns it off, which is what makes
    /// it possible to put the two side by side and see what the compression cost.
    /// </remarks>
    /// <summary>Where the ReBarn packs are.</summary>
    /// <param name="args">Command line, for <c>--packs</c> and <c>--workspace</c>.</param>
    /// <returns>The first directory that holds a pack, or the executable's own.</returns>
    /// <remarks>
    /// <c>--packs</c> wins outright when it is given. Otherwise the first place that holds
    /// one: beside the executable, then the content workspace, which is where
    /// <c>pack-content</c> writes during development.
    /// </remarks>
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
            Option(args, "--workspace") is { Length: > 0 } workspace ? workspace : string.Empty,
            DefaultWorkspaceDirectory(),
        ];

        foreach (string candidate in candidates)
        {
            if (candidate.Length > 0 &&
                Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*" + Formats.Rebarn.RebarnFormat.Extension).Any())
            {
                return candidate;
            }
        }

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
    /// <remarks>A convenience for development, like <see cref="DefaultDataDirectory"/>.</remarks>
    private static string DefaultWorkspaceDirectory() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "ContentWorkspace"));

    /// <summary>The eight archives a retail installation of GK3 holds.</summary>
    /// <remarks>
    /// Named in <c>Plan/02-content-pipeline.md</c> section 3. Listed here so that somebody
    /// who has the game but not this project's documentation is told what to copy, in a
    /// message rather than in a file they would have to go and find.
    /// </remarks>
    private static readonly string[] RetailArchives =
    [
        "ambient.brn", "common.brn", "core.brn", "day1.brn",
        "day123.brn", "day2.brn", "day23.brn", "day3.brn",
    ];

    /// <summary>Says what is missing and where it goes.</summary>
    /// <param name="dataDirectory">Where the archives were looked for.</param>
    private static void ExplainMissingArchives(string dataDirectory)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "GK3Reborn reads the original game's archives; it does not contain them.");

        Console.Error.WriteLine(
            $"Copy these from your installation's Data directory into {dataDirectory}:");

        Console.Error.WriteLine("    " + string.Join("  ", RetailArchives));
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "Nothing else from the original is needed: the .bik and .avi movies are "
            + "replaced by converted video in the .rebarn packs.");

        Console.Error.WriteLine(
            "Or pass --data <dir> to read them where they already are.");
    }

    /// <summary>Where the game's own archives are, when nobody has said.</summary>
    /// <returns>The first directory holding a <c>.brn</c>, or where one should be put.</returns>
    /// <remarks>
    /// <para>
    /// A published game is a directory somebody has copied the original archives into, so
    /// the executable's own <c>Data</c> is looked at first and the executable's directory
    /// after it, for anybody who dropped the barns straight in beside the game.
    /// </para>
    /// <para>
    /// The walk up the tree is the development convenience it always was: the checkout
    /// keeps the installation six directories above <c>bin/Debug</c>, and copying eight
    /// hundred megabytes into every project's output to save the walk would be the wrong
    /// trade. It is looked at last so that a published tree never reaches past itself and
    /// quietly runs on whatever an unrelated directory above it happens to hold.
    /// </para>
    /// <para>
    /// A directory only counts when it actually holds a barn. An empty <c>Data</c> beside
    /// the executable is the shape of an install somebody has started and not finished,
    /// and stopping there would report it as the answer.
    /// </para>
    /// </remarks>
    private static string DefaultDataDirectory()
    {
        string beside = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(beside, "Data"),
            beside,
            Path.GetFullPath(Path.Combine(
                beside, "..", "..", "..", "..", "..", "..", "GK3", "Data")),
        ];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "*.brn").Any())
            {
                return candidate;
            }
        }

        // Nothing anywhere: name the place a player is meant to fill rather than the one a
        // developer's checkout happens to have, because that is the message they will read.
        return candidates[0];
    }

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
    /// <summary>Prints what a device survey found.</summary>
    /// <param name="report">
    /// The survey, or null to make one. A caller that already has a renderer should pass its
    /// own: building an instance purely to look through it is 145 ms of the time to a first
    /// frame, and doing it on another thread to hide that lost a device about one run in six.
    /// </param>
    private static void ReportGraphics(Rendering.Vulkan.VulkanDeviceReport? report = null) =>
        Console.Write(GraphicsReport(report ?? Rendering.Vulkan.VulkanDeviceSelector.Survey()));

    private static string GraphicsReport(Rendering.Vulkan.VulkanDeviceReport report)
    {
        var text = new System.Text.StringBuilder();

        if (!report.VulkanAvailable)
        {
            return text.AppendLine(CultureInfo.InvariantCulture, $"Vulkan unavailable: {report.Unavailable}").ToString();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Vulkan: {report.Devices.Count} device(s), "
            + $"validation layers {(report.ValidationAvailable ? "available" : "not installed")}");

        foreach (Rendering.Vulkan.VulkanDeviceInfo device in report.Devices)
        {
            bool selected = ReferenceEquals(device, report.Selected);
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(selected ? "*" : " ")} {device}");

            foreach (string note in device.TierNotes)
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

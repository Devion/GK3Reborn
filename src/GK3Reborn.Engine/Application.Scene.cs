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
/// <summary>Opening a room and drawing it.</summary>
public static partial class Application
{
    /// <summary>Opens a window and shows a scene from the game's own archives.</summary>
    /// <returns>Process exit code.</returns>
    /// <param name="dataDirectory">The game's Data directory.</param>
    /// <param name="sceneName">Which scene to load.</param>
    /// <param name="timeblock">Which time of day, or null for whichever exists.</param>
    /// <param name="cameraName">Which of the scene's cameras to start at.</param>
    /// <param name="frameLimit">Stop after this many frames, or zero to run until closed.</param>
    /// <param name="screenshotPath">Where to write the last frame, if anywhere.</param>
    /// <param name="verbose">Whether to list everything that could not be loaded.</param>
    /// <param name="quality">How much ray tracing to start with, or null to use what the player has chosen.</param>
    /// <param name="enhancedDirectory">Higher-resolution textures to prefer, if any.</param>
    /// <param name="frontEnd">Whether to show the intro and the menu before the room.</param>
    /// <param name="args">The command line, for the options only the running scene reads.</param>
    private static int RenderScene( string dataDirectory, string sceneName, string? timeblock, string? cameraName, int frameLimit,
        string? screenshotPath, bool verbose, RayTracingQuality? quality, string? enhancedDirectory, bool frontEnd, string[] args)
    {
        // Named through the report rather than checked inline, so that a missing directory says how far up the path does exist and whether the name.
        if (!StartupReport.Needed("Content", dataDirectory))
        {
            ExplainMissingArchives(dataDirectory);
            return 2;
        }

        using GameArchives archives = GameArchives.Open(dataDirectory);

        if (archives.Count == 0)
        {
            // The directory is there and empty, which is what a half-finished install looks like.
            Log.Error($"No game archives in {dataDirectory}.");
            ReportArchives(dataDirectory, archives.Count);
            ExplainMissingArchives(dataDirectory);
            return 2;
        }

        Log.Info($"Content: {archives.Count} archives in {dataDirectory}");

        ReportArchives(dataDirectory, archives.Count);

        // Whatever the player has dropped into overrides/, which outranks everything: the packs below, and these archives.
        var overrideDiagnostics = new DiagnosticBag();
        string overrideDirectory = OverrideDirectory(args);

        ContentOverrides found = args.Contains("--no-overrides", StringComparer.OrdinalIgnoreCase) ? ContentOverrides.Open(string.Empty)
            : ContentOverrides.Open(overrideDirectory, overrideDiagnostics);

        foreach (Diagnostic diagnostic in overrideDiagnostics.Items)
        {
            Log.Report(diagnostic);
        }

        // Null when there is nothing, and null all the way down: every layer below tests this to decide whether the override door exists at all, so.
        ContentOverrides? overrides = found.IsEmpty ? null : found;

        archives.Overrides = overrides;

        // Said out loud, because an override is invisible once it is on screen — that is what it is for — and a run in which a forgotten file is.
        Log.Info(found.Describe() is { } overridden ? $"Overrides: {overridden}" : $"Overrides: none in {overrideDirectory}");

        StartupReport.Optional("Overrides", overrides is null ? null : overrideDirectory, "The game uses the content it shipped with.");



        // The remake's own content, in the one or two ReBarn volumes that ship beside the executable.
        var packDiagnostics = new DiagnosticBag();
        string packDirectory = PackDirectory(args);
        using RebarnContent packs = RebarnContent.Open(packDirectory, packDiagnostics);

        // The same layer as the archives got, in front of the packs.
        packs.Overrides = archives.Overrides;

        foreach (Diagnostic diagnostic in packDiagnostics.Items)
        {
            Log.Report(diagnostic);
        }

        // --rebarn: the packs and nothing else.
        bool askedForPacks = args.Contains("--rebarn", StringComparer.OrdinalIgnoreCase);

        // And that is what a player gets without asking, because it is all a shipped install has: packs beside the executable and no content.
        bool namedSomethingLoose = enhancedDirectory is { Length: > 0 } || Option(args, "--workspace") is { Length: > 0 } ||
            args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase);

        bool packsOnly = askedForPacks || (packs.VolumeCount > 0 && !namedSomethingLoose);

        if (askedForPacks && args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase))
        {
            // --rebarn says "the packs and nothing else", --uncompressed says "not the compressed layer", and a pack holds nothing but compressed.
            Log.Error( "--rebarn and --uncompressed contradict each other: a pack holds nothing " + "but compressed textures.");
            Log.Error( "Drop --uncompressed to measure the packs, or drop --rebarn to compare " + "against the loose sets.");

            return 2;
        }

        if (askedForPacks && packs.VolumeCount == 0)
        {
            // Refused rather than warned.
            Log.Error($"--rebarn: no .rebarn pack in {packDirectory}.");
            Log.Error( "Build one with `pack-content`, or pass --packs <dir> to say where they are.");

            return 2;
        }

        // Said either way.
        Log.Info(packs.Describe() is { } packed ? packsOnly ? $"Packs: {packed} (loose enhanced content ignored)" : $"Packs: {packed}"
            : $"Packs: none in {packDirectory}");


        // What the player has chosen, read before anything that obeys it exists.
        string settingsPath = Option(args, "--settings") is { Length: > 0 } elsewhere ? Path.GetFullPath(elsewhere) : Settings.DefaultPath;

        Settings settings = Settings.Load(settingsPath);

        // --language names one for this run without writing it back, so that a room can be rendered in French to compare against the English one.
        if (Option(args, "--language") is { Length: > 0 } wantedLanguage)
        {
            settings = settings with { Language = wantedLanguage };

            if (!GameLanguage.IsKnown(wantedLanguage))
            {
                Log.Warning( $"--language {wantedLanguage} names no localisation GK3 was published in; "
                    + $"reading the game in {GameLanguage.Default.Name}.");
            }
        }

        // Which language the game is read in, and the pack that makes that possible.
        GameLanguage language = GameLanguage.Of(settings.Language);
        var languageDiagnostics = new DiagnosticBag();
        LocalizedContent? localized = LocalizedContent.Open(packDirectory, language, languageDiagnostics);

        foreach (Diagnostic diagnostic in languageDiagnostics.Items)
        {
            Log.Report(diagnostic);
        }

        // Whether the pack is telling the installation its own language back.
        if (localized?.RepeatsInstallation(archives.Read) == true)
        {
            Log.Info( $"Language: the installation is already {localized.Language.Name}, so the "
                + $"pack's {localized.AssetCount} 1999 assets are skipped and only what the " + "remake painted is read from it.");
        }

        archives.Localization = localized;
        IReadOnlyList<GameLanguage> languages = LocalizedContent.Available(packDirectory);

        // Said out loud whichever way it went.
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
            // A warning rather than a note, because this is a run that will do the wrong thing quietly: every word of it will be in whatever.
            Log.Warning( $"Language: {language.Name} was asked for, but there is no "
                + $"{LocalizedContent.FileNameOf(language)} in {packDirectory}. The game is " + "read in whatever language the installation holds.");
        }

        StartupReport.Optional( "Language", localized is null ? null : packDirectory,
            "The game is read in the language the installation was made in.");

        // Two switches for the two rows on the Picture page that change what a room looks like rather than how sharply it is drawn, so that the same.
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

        // And the third: the towns the port builds out where the game left them empty.
        if (args.Contains("--towns", StringComparer.OrdinalIgnoreCase))
        {
            settings = settings with { RebuiltTowns = true };
        }

        if (args.Contains("--no-towns", StringComparer.OrdinalIgnoreCase))
        {
            settings = settings with { RebuiltTowns = false };
        }

        // Content the game shipped with and cannot reach.
        Content.DressedTowns installedTowns = SceneDressing.Installed( packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty : Beside(enhancedDirectory, "models"), packs);

        // What the run starts with.
        Content.DressedTowns Dressed() => settings.RebuiltTowns ? installedTowns : Content.DressedTowns.None;

        Content.DressedTowns dressing = Dressed();

        // The other half of a restoration: files no barn has and none can, for rooms that were cut before there was anything to cut them from — and.
        AddedAssets rebuilt = AddedAssets.Open( packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty : Beside(enhancedDirectory, "rooms"), packs);

        rebuilt.Overrides = overrides;

        if (!rebuilt.IsEmpty)
        {
            archives.Added = rebuilt;
            Log.Info($"Added assets: {rebuilt.Describe()}");
        }

        // Whether St.
        bool bookshop = rebuilt.Has(Bookshop);

        var restoreDiagnostics = new DiagnosticBag();
        CutContent restored = CutContent.Open(RestorationTier(args, settings), dressing, bookshop);

        if (!restored.IsEmpty)
        {
            archives.Restoration = restored;
            archives.RestorationDiagnostics = restoreDiagnostics;

            // Applied here rather than when a room first asks for one of these files, so that an edit which no longer matches the installation is.
            foreach (string name in restored.Names)
            {
                archives.Read(name);
            }

            foreach (Diagnostic diagnostic in restoreDiagnostics.Items)
            {
                Log.Report(diagnostic);
            }
        }

        // Said either way, and said here rather than left to be inferred from a fuller room.
        Log.Info(!settings.RebuiltTowns ? "Scene dressing: switched off, so TR1, RL1 and RC3 are as the game shipped "
              + "them" + (installedTowns == Content.DressedTowns.None ? string.Empty : " — the geometry for them is installed and unused")
            : dressing == Content.DressedTowns.None ? $"Scene dressing: none — nothing has {SceneDressing.Sentinel}, so TR1, RL1 "
              + "and RC3 are as the game shipped them" : "Scene dressing: " + string.Join( " and ", new[]
                {
                    dressing.HasFlag(Content.DressedTowns.Couiza) ? "Couiza" : null, dressing.HasFlag(Content.DressedTowns.RennesLesBains)
                        ? "Rennes-les-Bains" : null, dressing.HasFlag(Content.DressedTowns.RennesLeChateau)
                        ? "Rennes-le-Château's cemetery gateway" : null,
                }.Where(name => name is not null)) + ", from the installed geometry");

        // Said either way, like the dressing above and for the same reason: a door that stays shut and a door that was never wired look the same.
        Log.Info(bookshop ? "Bookshop: installed, so RC1's bookstore door gives on the fifth try"
            : $"Bookshop: not installed — nothing answers for {Bookshop}, so RC1's " + "bookstore door stays closed");

        // Before the window, the device and the menu.
        if (archives.Read(sceneName + ".SIF") is null)
        {
            Log.Error( $"No room called {sceneName}: the archives have no {sceneName}.SIF.");

            Log.Error( "Check what was passed to --scene or --start, or drop it and the game " + $"starts where it starts, in {OpeningScene}.");

            return 2;
        }

        Log.Info(restored.Describe() is { } putBack ? $"Cut content: {putBack}" : "Cut content: not restored.");



        Log.Info(File.Exists(settingsPath) ? $"Settings: {settingsPath}" : $"Settings: none yet, they will be written to {settingsPath}");

        // The three directories the game writes to, probed now rather than at the moment somebody first tries to save.
        StartupReport.Optional("Enhanced textures", enhancedDirectory, "The game will look as it originally shipped.");

        StartupReport.Writable("Settings", Path.GetDirectoryName(Settings.DefaultPath) ?? InstallPaths.UserData);
        StartupReport.Writable("Saves", Game.SaveStore.DefaultDirectory);
        StartupReport.Writable("Shader cache", Rendering.Shaders.ShaderCompiler.DefaultCacheDirectory);

        // Which graphics API to draw through.
        string? backendAsked = CommandLine.BackendAsked(args);
        Rendering.RenderBackend backend = ChooseBackend(backendAsked, settings);

        // What the player has dropped into libs/, and NVIDIA's loader started against it.
        var runtimes = Rendering.Upscaling.UpscalerRuntimes.Find(Option(args, "--libs-dir"));
        Log.Info(runtimes.ToString());

        // Before Streamline, and it has to be: Streamline asks every feature it was told to load for its requirements while it starts, and a feature.
        if (settings.NeuralUplift)
        {
            Rendering.Upscaling.NgxFeatureTable.TryEnable();
        }

        // --width and --height, for photographing the interface at a display size this machine has not got.
        OpenedRenderer drawing = OpenRenderer( backend, insisted: backendAsked is not null, $"GK3Reborn - {sceneName}",
            int.TryParse(Option(args, "--width"), out int windowWidth) && windowWidth > 0 ? windowWidth : 1280,
            int.TryParse(Option(args, "--height"), out int windowHeight) && windowHeight > 0 ? windowHeight : 720, runtimes,
            Option(args, "--libs-dir"));

        using Platform.SilkGameWindow window = drawing.Window;
        using Rendering.Upscaling.Streamline? streamline = drawing.Streamline;
        using Rendering.IRenderer renderer = drawing.Renderer;

        renderer.Runtimes = runtimes;

        ReportGraphics(renderer.Survey());
        Log.Info($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        // What covers the gaps.
        var fade = new Rendering.ScreenFade(window, renderer);
        var loading = new UI.LoadingScreen(window, renderer, fade);

        // Counted from the launch rather than from here, because the player has been waiting since they double-clicked: bringing the device up and.
        loading.Begin(Since.Elapsed);

        // And now there is a frame in it, the window goes up.
        window.Show();

        var diagnostics = new DiagnosticBag();
        SceneRequest request = Playable(archives, sceneName, timeblock);
        Gk3SheepApi api = request.Api ?? new Gk3SheepApi(new GameState());

        // What the two of them set out with.
        int pockets = Game.StartingItems.Fill(api.State.Inventory);

        // Here rather than only in Opening, because a scene file's [ACTORS], [AMBIENT] and [MODELS] blocks are each guarded by a condition read at.
        Already(args, api);

        Log.Info( $"Carrying: {pockets} items to begin with, " + $"{string.Join(", ", api.State.Inventory.ItemsOf(api.State.Ego))}");

        // What makes a waited call take time.
        api.Animations = new AnimationLibrary(archives) { Language = language.Prefix };

        // Where saved games go.
        api.Saves = new Game.SaveStore();

        // Who the player has been introduced to, which decides whether a label may use somebody's name.
        Game.Story.Introductions introductions = Game.Story.Introductions.Open();

        Log.Info( $"Introductions: {introductions.Count} people are strangers until met");

        // The saves the 1999 game wrote, brought across once each.
        var searched = new List<string> { api.Saves.Directory };

        // And the application's own, when the store has been put somewhere else: a read-only install sends saves to the profile, and the .gk3 files.
        string beside = Path.Combine(AppContext.BaseDirectory, "saves");

        if (!searched.Contains(beside, StringComparer.OrdinalIgnoreCase))
        {
            searched.Add(beside);
        }

        // Then both places the original itself wrote to: its install root, which is the parent of the Data directory this engine was pointed at, and.
        if (Path.GetDirectoryName(Path.GetFullPath( Option(args, "--data") ?? DefaultDataDirectory())) is { Length: > 0 } installRoot)
        {
            searched.Add(installRoot);
            searched.Add(Path.Combine(installRoot, "Save Games"));
        }

        int broughtAcross = searched.Sum( where => Game.OriginalSaves.Import(where, api.Saves, api.Scores, introductions));

        if (broughtAcross > 0)
        {
            Log.Info( $"Imported {broughtAcross} save(s) written by the original game");
        }

        // What is left to do before the menu can be drawn, as fractions of the way there.
        loading.At(0.10);

        if (request.State is not null)
        {
            Log.Info($"Story: {request.State.Timeblock} in {request.State.Location}");
        }

        // Sound.
        Audio.OpenAlBackend? audio = Audio.OpenAlBackend.Open(settings.Speakers, diagnostics);

        // Before anything plays, so the first sound of the session is already at the level the player left it at rather than at full volume for a.
        settings.ApplyTo(audio);

        // The same precedence as the rest of the content stack: a player's loose override, then restored audio in ReBarn, then the legally installed.
        var sounds = new SoundLibrary(archives, packs);

        SceneAudio? room = audio is null ? null : new SceneAudio(sounds, api.Animations, audio);

        Log.Info(audio is null ? "Audio: none, the game runs silent" : $"Audio: {audio.DeviceName}");

        loading.At(0.20);

        // Movies.
        VideoLibrary videos = VideoLibrary.Open( packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty : Beside(enhancedDirectory, "video"), packs, localized, packsOnly || enhancedDirectory is not { Length: > 0 }
                ? string.Empty : Path.Combine( Beside(enhancedDirectory, "localized"), language.FileCode));

        using var movies = new Game.MoviePlayer(videos, audio)
        {
            // Every film passed over, the intro's and the story's alike.
            Skipping = args.Contains("--no-movies", StringComparer.OrdinalIgnoreCase),
        };

        // Fourteen of the films carry their own subtitles, in a YAK of the film's own name, translated in every release.
        movies.Subtitles = name => api.Animations?.Read(name);

        loading.At(0.35);

        if (movies.Skipping)
        {
            Log.Info("Movies: skipped, every film passed over");
        }

        if (videos.Count > 0)
        {
            // The decoders are the engine's own, so there is nothing to find and nothing that can be missing.
            Log.Info( $"Movies: {videos.Count} available ({videos.LooseCount} loose, " + $"{videos.PackedCount} packed), decoded in process");

            // Separately, because it is the half of a localised run that nobody can see.
            if (videos.LocalizedCount > 0 || videos.LocalizedSoundCount > 0)
            {
                Log.Info( $"Movies: {language.Name} re-cuts {videos.LocalizedCount} of them and "
                    + $"supplies the soundtrack for {videos.LocalizedSoundCount} more");
            }
            else if (localized is not null)
            {
                Log.Warning( $"Movies: the {language.Name} pack carries no soundtracks, so every "
                    + "film is heard in the language the installation holds.");
            }
        }

        // The host outlives the room.
        Game.Actors.CharacterLibrary characters = Game.Actors.CharacterLibrary.Open(archives);

        // How each of their faces is put together.
        Game.Actors.FaceLibrary faces = Game.Actors.FaceLibrary.Open(archives);

        // Behaviour scripts named by other behaviour scripts, and by Sheep.
        Dictionary<string, Formats.Animation.GasFile?> behaviours = new(StringComparer.OrdinalIgnoreCase);

        Formats.Animation.GasFile? Behaviour(string name)
        {
            if (behaviours.TryGetValue(name, out Formats.Animation.GasFile? known))
            {
                return known;
            }

            // With the extension when the name does not carry one, which none of them does: a scene file writes `idle=jeaIdle.gas` and a script.
            byte[]? bytes = archives.Read(name) ?? (Path.HasExtension(name) ? null : archives.Read(name + ".GAS"));

            Formats.Animation.GasFile? read = bytes is not null ? Formats.Animation.GasFile.Parse(bytes) : null;

            behaviours[name] = read;
            return read;
        }

        // Which verbs are things to say rather than things to do.
        Game.Actors.Footsteps footsteps = Game.Actors.Footsteps.Open(archives);

        if (footsteps.SurfaceCount > 0)
        {
            Log.Info( $"Footsteps: {footsteps.SurfaceCount} floor textures classified, " + $"{footsteps.SoundCount} shoe and ground pairings");
        }

        Game.Actions.VerbLibrary verbs = Game.Actions.VerbLibrary.Open(archives);

        // What the game calls places and times, in the player's own language.
        GameStrings strings = GameStrings.Open(archives);

        // The port's own interface, in the language the game is being played in.
        UiText words = UiText.Of(archives.Localization?.Language, archives.Localization);

        Log.Info($"Interface: {words.Count} phrase(s) from {words.Source}");

        // And in the player's own language from here on.
        loading.Text = words;
        loading.At(0.55);

        if (strings.Count > 0)
        {
            Log.Info($"Names: {strings.Count} from {strings.File}");
        }
        else if (localized is not null)
        {
            // Worth a line of its own.
            Log.Warning( $"Names: no {GameStrings.TableFor(archives)}, so places and times are " + "shown by their codes.");
        }

                var host = new ScriptHost(api);

        // Scripts wait for real here, unlike in the tools, because here there is a clock for them to wait against.
        host.Scheduler = new SheepScheduler(host.Machine);

        var catalogue = new Sheep.SheepSignatures();

        Log.Info( $"Scripts: {LoadScripts(archives, host, catalogue)} loaded, " + $"{catalogue.Count} function signatures");

        // The rules that decide when a point in the story is over.
        Log.Info( $"Story rules: {Game.Story.TimeblockRules.Known.Count} timeblocks");

        // The interface.
        var clips = new ClipLibrary(archives) { KeepVertices = true };
        var fonts = new FontLibrary(archives);
        GameHud? hud = null;
        ScreenPainter? screens = null;

        // Grace's computer, which the story runs through: parchments are scanned into it, analysed and translated, and DoesSidneyFileExist is a real.
        var sidney = new Game.Sidney.SidneyMachine( Game.Sidney.SidneyLibrary.Open(archives), api.State)
        {
            // 391 pages of encyclopedia and the 393 spellings that reach them.
            Search = Game.Sidney.SidneySearch.Open(archives),

            // What each verse of Le Serpent Rouge is worth when the map confirms it.
            Scores = api.Scores,

            // What the player's things are called, which is the one family of per-object text GK3 localised, and which language the dozen phrases.
            Names = strings, Language = archives.Localization?.Language.Code ?? Content.GameLanguage.Default.Code,
        };

        api.Sidney = sidney;

        // The map the moped is ridden around, and its road network.
        DrivingMap map = DrivingMap.Open(archives);

        // What can be seen from where, through the binoculars.
        Binoculars binoculars = Binoculars.Open(archives);

        loading.At(0.70);

        // One console for the whole run, not one per room.
        var console = new GameConsole { Catalogue = catalogue };

        // The typeface.
        Formats.Fonts.TrueTypeFile? face = args.Contains("--bitmap-font", StringComparer.OrdinalIgnoreCase) ? null
                : InterfaceFont(Option(args, "--font-file"), enhancedDirectory, diagnostics);

        Log.Info(face is { } chosen ? $"Typeface: {chosen.Family}, {chosen.CharacterCount} characters, drawn from outlines"
            : "Typeface: GK3's own bitmap sheets");

        loading.At(0.80);

        int wantedGlyph = UI.TextSizing.Sheet(window.FramebufferHeight, settings.TextScale);

        // --font names one outright, for looking at a particular sheet.
        string[] ladder = Option(args, "--font") is { Length: > 0 } named ? [named] : CaptionFonts;

        // The atlas the room's interface draws with, and the larger one the menu does.
        OverlayAtlas? Cut(bool menu)
        {
            int height = window.FramebufferHeight;
            float scale = settings.TextScale;

            // Every language's letters, not the caller's: see OverlayAtlas.Everything.
            if (face is not null && OverlayAtlas.Build( face, UI.TextSizing.Em(height, menu, scale), OverlayAtlas.Everything) is { } drawn)
            {
                return drawn;
            }

            int wanted = menu ? Math.Max( UI.TextSizing.Sheet(height, scale), UI.TextSizing.Em(height, true, scale) * 2 / 3)
                : UI.TextSizing.Sheet(height, scale);

            return fonts.Nearest(wanted, ladder) is { } sheet ? OverlayAtlas.Build(sheet) : null;
        }

        if (Cut(menu: false) is { } atlas)
        {
            // A sheet has to be magnified to reach the size wanted; an outline was drawn at it.
            int magnify = atlas.Scalable || atlas.Font is null ? 1 : Magnification(atlas.Font, wantedGlyph);

            renderer.SetOverlayAtlas(atlas);

            // And the loading screen stops drawing with the block of white it has been making do with.
            loading.Atlas = atlas;
            loading.At(0.90);

            hud = new GameHud(new Overlay(atlas) { Magnify = magnify })
            {
                Names = strings, Text = words,
            };

            screens = new ScreenPainter(new Overlay(atlas) { Magnify = magnify })
            {
                // The game's own names for the player's things, in the player's own language.
                Names = strings, Text = words,
            };

            // Sidney's map, the survey the whole puzzle is played on.
            EnhancedTextures? better = Pictures( settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides, language: language);

            // Loose files first, then the packs.
            Formats.Bitmaps.DecodedImage? Enhanced(string name) => better?.Read(name) ?? Packed(packs, localized, name);

            Formats.Bitmaps.DecodedImage? survey = Enhanced(Game.Sidney.SidneyMap.Picture) ??
                Decoded(archives, Game.Sidney.SidneyMap.Picture + ".BMP");

            if (survey is { } drawn)
            {
                renderer.AddOverlayPicture(Game.Sidney.SidneyMap.Picture, drawn);
            }

            // The suspects' faces, rendered from their own heads by the offline tool.
            List<string> everybody =
            [
                .. Game.Sidney.SidneySuspect.Portraits, Game.DrivingTraffic.EgoFace, Game.DrivingTraffic.TwoMenFace, ];

            int portraits = 0;

            foreach (string portrait in everybody)
            {
                if (Enhanced(portrait) is { } likeness && renderer.AddOverlayPicture(portrait, likeness) > 0)
                {
                    portraits++;
                }
            }

            Log.Info(portraits > 0 ? $"Sidney: {portraits} of {everybody.Count} portraits"
                : "Sidney: no portraits; the suspect list draws names alone.");

            // The driving map's own art.
            LoadMapArt(archives, renderer, screens, Enhanced);

            Log.Info( $"Interface: {atlas.Name}, {atlas.Count} glyphs at {atlas.Height}px" + (magnify > 1 ? $" x{magnify}" : string.Empty) +
                $" (wanted {wantedGlyph} for a {window.FramebufferHeight}-line display), " + $"sheet {atlas.Image.Width}x{atlas.Image.Height}, " +
                $"{(renderer.HasOverlay ? "drawing" : "NOT drawing")}");
        }
        else
        {
            Log.Info("Interface: no font found, nothing is drawn over the room");
        }

        // What each thing in the player's pockets looks like.
        Game.InventoryArt itemArt = Game.InventoryArt.Open(archives);
        Dictionary<string, UI.ItemIcon> itemPictures = new(StringComparer.OrdinalIgnoreCase);

        UI.ItemIcon Icon(string item)
        {
            if (itemPictures.TryGetValue(item, out UI.ItemIcon already))
            {
                return already;
            }

            // Remembered whether or not there was anything to find: twenty of the items the table names have no list picture, and looking again.
            UI.ItemIcon icon = itemArt.Icon(archives, item) is { } picture &&
                renderer.AddOverlayPicture("item:" + item.ToUpperInvariant(), picture) is > 0 and { } number
                    ? new UI.ItemIcon(number, picture.Width, picture.Height) : default;

            itemPictures[item] = icon;

            return icon;
        }

        // And the same thing held up to the light.
        Dictionary<string, UI.ItemIcon> itemCloseUps = new(StringComparer.OrdinalIgnoreCase);

        UI.ItemIcon CloseUp(string item)
        {
            if (itemCloseUps.TryGetValue(item, out UI.ItemIcon already))
            {
                return already;
            }

            UI.ItemIcon art = itemArt.CloseUp(archives, item) is { } picture &&
                renderer.AddOverlayPicture("closeup:" + item.ToUpperInvariant(), picture) is > 0 and { } number
                    ? new UI.ItemIcon(number, picture.Width, picture.Height) : default;

            itemCloseUps[item] = art;

            return art;
        }

        // And the game's own art by file name, for the pieces of the interface that are a picture rather than a drawing — the handheld GPS is the.
        Dictionary<string, UI.ItemIcon> artwork = new(StringComparer.OrdinalIgnoreCase);

        // The loose picture layer, once a room has built one.
        EnhancedTextures? loose = null;

        UI.ItemIcon Artwork(string file)
        {
            if (artwork.TryGetValue(file, out UI.ItemIcon already))
            {
                return already;
            }

            UI.ItemIcon art = default;

            try
            {
                // The game's own, with its transparency put back where it keeps it apart.
                Formats.Bitmaps.DecodedImage? original = archives.Read(file) is { } bytes
                    ? Masked(archives, file, Formats.Bitmaps.BitmapDecoder.Decode(bytes, file)) : null;

                // A replacement out of enhanced/ or overrides/ is drawn instead, and carries its own transparency — laying the 1999 mask over it.
                if ((loose?.Read(file) ?? original) is { } picture)
                {
                    // Laid out at the game's own size whatever the replacement's resolution is: the kit places its prints in the art's pixels, and a.
                    art = renderer.AddOverlayPicture("art:" + file.ToUpperInvariant(), picture) is > 0 and { } number ? new UI.ItemIcon( number,
                            original?.Width ?? picture.Width, original?.Height ?? picture.Height) : default;
                }
            }
            catch (Formats.FormatParseException)
            {
                // Drawn without it, which for everything here means not drawn at all.
            }

            artwork[file] = art;

            return art;
        }

        // What each verb looks like.
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

            // Remembered whether or not there was anything to find.
            if (archives.Read(file) is { } bytes)
            {
                try
                {
                    Formats.Bitmaps.DecodedImage art = Formats.Bitmaps.BitmapDecoder.Decode(bytes, file);

                    icon = renderer.AddOverlayPicture("verb:" + file, art) is > 0 and { } number ? new UI.ItemIcon(number, art.Width, art.Height)
                        : default;
                }
                catch (Formats.FormatParseException)
                {
                    // A picture that will not decode is a verb drawn by its word alone, which is what a verb with no picture at all gets.
                }
            }

            verbPictures[file] = icon;

            return icon;
        }

        // A setting carried over from another machine, or from another card.
        if (!renderer.OfferedUpscalers.Contains(settings.Upscaler))
        {
            Log.Info( $"Upscaling: {settings.Upscaler} needs an NVIDIA card and this is a " +
                $"{renderer.Vendor} one, so the built-in upscaler is used instead.");

            settings = settings with { Upscaler = Rendering.Upscaling.UpscalerKind.Spatial };
        }

        // The menu, and what changing something in it reaches.
        var front = new FrontEnd(settings)
        {
            Offered = renderer.OfferedUpscalers, StoredAt = settingsPath,

            // What the Language row may step through: the packs that are actually beside the game, plus English, which every installation can read.
            Languages = languages,

            Text = words,
        };

        MenuPage? pages = hud is null ? null : new MenuPage(new Overlay(Cut(menu: true) ?? hud.Overlay.Atlas)
            {
                Magnify = hud.Overlay.Magnify,
            })
            {
                Text = words,
            };

        SceneUpdate? live = null;

        // Set when the Language row moves, cleared when the room has been reloaded for it.
        bool relanguage = false;

        // Whether the language has moved since this was last asked.
        bool Relanguaged()
        {
            bool moved = relanguage;

            relanguage = false;

            return moved;
        }

        // Reads the game in another language, without restarting it.
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

            // Cleared first so the comparison below reads the installation rather than the language that is being left.
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

            Log.Info( $"Language: now {wanted.Name}; {words.Count} interface phrase(s) from "
                + $"{words.Source}, {strings.Count} names from {strings.File}");

            relanguage = true;
        }

        // The sun over the room the player is in, for the rays: remembered here because the row that turns them on and off is pressed between rooms.
        Rendering.SunRays daylight = Rendering.SunRays.None;

        bool RaysWanted(Settings chosen) => chosen.SunRays && !args.Contains("--no-sun-rays", StringComparer.OrdinalIgnoreCase);

        // And how hot the room is: the heat haze over its far ground, decided with the room and switched with the row.
        float heat = 0f;

        bool HazeWanted(Settings chosen) => chosen.HeatHaze && !args.Contains("--no-shimmer", StringComparer.OrdinalIgnoreCase);

        void Apply(Settings chosen)
        {
            // Before the assignment, because `settings` is still the old answer here and this is the only place the two can be compared.
            if (chosen.FreeCamera != settings.FreeCamera)
            {
                Log.Info(chosen.FreeCamera ? "Camera bounds: off, so the camera may leave the room" : "Camera bounds: back on");
            }

            // Said here for the same reason the speaker layout is: the language decides which pack the archives were opened through, which letter.
            if (!string.Equals(chosen.Language, settings.Language, StringComparison.Ordinal))
            {
                settings = chosen;
                Relanguage(GameLanguage.Of(chosen.Language));
            }

            settings = chosen;
            chosen.ApplyTo(audio);

            // Which key and which pad button do which job, and how fast a stick drives the cursor.
            window.Bindings = Platform.InputBindings.Restore(chosen.Bindings);

            // Nought is the switch as well as the speed: a stick that moves the cursor no pixels a second is a stick that does not move the cursor.
            window.PointerSpeed = chosen.GamepadCursor ? chosen.GamepadCursorSpeed : 0f;
            window.PointerScale = chosen.CursorScale;

            if (renderer.SupportsRayTracing)
            {
                renderer.Quality = chosen.Quality;
            }

            // The picture's own two plans.
            renderer.Upscaling = chosen.Upscaling;
            renderer.Output = chosen.Output;

            // And what the two reflection rows on the Picture page ask for.
            renderer.Reflections = new ReflectionPlan( chosen.Reflectivity, chosen.FloorReflections &&
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
            if (chosen.AlwaysWearsMoustache && Game.Assists.GiveMoustache(api.State))
            {
                Log.Info($"Assist: {Game.Assists.Owner} is given the {Game.Assists.Moustache}");
            }

            // He wears it whatever the clock says and whatever he is carrying, because that is what the row promises.
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
                    Log.Info(chosen.AlwaysWearsMoustache ? $"Assist: {faces} face(s) composed from {Game.Assists.MoustachedFace}"
                        : $"Assist: {faces} face(s) back to their own");
                }
            }

            if (live is not null)
            {
                live.HurryFactor = chosen.HurryFactor;
            }
        }

        // At the start, not only when something changes: a stored setting has to reach the game on a run where the player never opens the menu at.
        Apply(settings);

        if (frontEnd && pages is not null)
        {
            // The game's own title screen: the angel, with the name painted into it.
            TitleScreen title = TitleArt( archives, Pictures( settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides, language: language),
                settings.EnhancedTextures ? CompressedTextures.Open( packsOnly ? string.Empty
                            : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty), packs, overrides, localized)
                    : overrides is null && localized is null ? null : CompressedTextures.Open(string.Empty, null, overrides, localized), diagnostics);

            front.Illustrated = title.Exists;

            // The port's own title screen: six layers out of the pack, put on the device as the interface's own pictures and drawn into the menu's.
            Content.MenuArt menuArt = Content.MenuArt.Open( packs, packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty : Beside(enhancedDirectory, "menu"), overrides, diagnostics);

            UI.TitleScene? modern = settings.ModernMenu ? UI.TitleScene.Build(menuArt, renderer.AddOverlayPicture) : null;

            front.ModernMenuAvailable = menuArt.Complete;

            // The party behind the title, for whoever spells the word.
            if (modern is not null)
            {
                modern.PartyMaker = wall =>
                {
                    var dressing = new Game.SceneLoader(archives)
                    {
                        Enhanced = Pictures( settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides, language: language),
                        Compressed = settings.EnhancedTextures ? CompressedTextures.Open( packsOnly ? string.Empty
                                    : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty), packs, overrides, localized) : null,
                    };

                    var partyDiagnostics = new DiagnosticBag();

                    UI.DiscoParty? party = UI.DiscoParty.Build( new UI.DiscoPartyContent( name => archives.Read(name + ".MOD") is { } bytes
                                ? Formats.Models.ModFile.Parse(bytes, name) : null, clips, api.Animations,
                            (sink, texture) => dressing.LoadTextureLate(sink, texture, partyDiagnostics), sounds, audio, wall)
                        {
                            // --dance-guests bar,gra photographs a guest or two on their own.
                            Only = Option(args, "--dance-guests") is { Length: > 0 } few ? new HashSet<string>(
                                    few.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase) : null,
                        }, renderer, Log.Info);

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

            // The port's own screen carries the game's name in its own lettering, so the page must not write it out as well -- the same reason.
            front.Illustrated = title.Exists || modern is not null;

            Log.Info(menuArt.Complete ? modern is not null ? $"Title screen: the port's own, {menuArt.Count} layers from {menuArt.From}"
                    : "Title screen: the port's own is available and switched off, so the " + $"original is drawn ({menuArt.From})"
                : menuArt.Count > 0 ? $"Title screen: the original; {string.Join(", ", menuArt.Missing)} " + $"missing from {menuArt.From}"
                    : "Title screen: the original; the port's own layers are not installed");

            // Behind the loading screen from here on, and behind the menu after it.
            title.Show(renderer);
            loading.At(0.97);

            // Which of them it took, because they are indistinguishable on screen until somebody has actually upscaled the picture — and a run that.
            Log.Info(title.Exists ? $"Title: {TitlePicture} at {title.Width}x{title.Height}, {title.From}"
                : $"Title: no {TitlePicture} to be had, so the menu draws its own screen");

            // The theme, under the menu and nowhere else.
            Audio.AudioVoice theme = Theme(audio, sounds);

            Log.Info(theme.Exists ? $"Theme: {ThemeMusic}, under the menu" : $"Theme: no {ThemeMusic} to play, so the menu is silent");

            // Everything the menu needs is now in hand, so the screen that covered getting it comes down, over its own third of a second.
            loading.Done();

            void Films(IReadOnlyList<string> which)
            {
                // The film has its own soundtrack and the theme would play under it.
                audio?.Silence(theme);
                modern?.Hush();
                renderer.SetBackdrop(null);

                ShowIntro(window, renderer, movies, pages, which, front.Settings.MovieSubtitles);

                // The gesture that skipped the film is still on the frame's books, and the menu is about to be drawn under the pointer that made it.
                window.EndFrame();

                title.Show(renderer);

                // The party's music rather than the theme, where the party is on: it goes on until the game starts or ends, and the intro is a.
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

            // The theme stops the moment the word is spelled.
            if (modern is not null)
            {
                modern.PartyStarted = () =>
                {
                    audio?.Silence(theme);
                    renderer.SetBackdrop(null);
                };
            }

            // --frames is a run that photographs something and ends, and no such run wants to sit through two films first.
            if (settings.PlayIntro && frameLimit == 0 && !args.Contains("--skip-intro", StringComparer.OrdinalIgnoreCase))
            {
                Films(IntroMovies);
            }
            else
            {
                title.Show(renderer);
            }

            // --front-page opens on one of the settings pages, for the same reason --frames exists here: a page three keystrokes in cannot be.
            if (Option(args, "--front-page") is { Length: > 0 } wantedPage && Enum.TryParse(wantedPage, ignoreCase: true, out FrontEndPage opened))
            {
                front.Show(opened);
            }

            FrontEndOutcome asked;

            // What the slots hold, so the title screen's Restore has something to show.
            front.Saves = api.Saves?.List() ?? [];
            front.Illustrations = slot => Illustration(renderer, api.Saves, slot);

            // --dance spells the word before the first frame, for a run that photographs the party rather than somebody who found it.
            if (modern is not null && args.Contains("--dance", StringComparer.OrdinalIgnoreCase))
            {
                modern.Spell();
            }

            // Round again for the Intro row, which is the one thing on the menu that goes somewhere and comes back.
            do
            {
                asked = ShowMenu( window, renderer, pages, front, Apply, modern is not null ? MenuBehind.Modern
                        : title.Exists ? MenuBehind.Picture : MenuBehind.Nothing, () => Cut(menu: true), frameLimit, screenshotPath, modern);

                if (asked == FrontEndOutcome.Intro)
                {
                    // The film, not the publisher's logo.
                    Films([TheIntro]);
                }
            }
            while (asked == FrontEndOutcome.Intro && !window.IsClosing);

            // The theme does not belong to the game about to start; the picture does, for a little longer.
            audio?.Silence(theme);

            // The title screen's layers, on the other hand, are done with: eleven megabytes of the interface's picture list, and the interface is.
            if (modern is not null)
            {
                foreach (string layer in Content.MenuArt.Layers)
                {
                    renderer.DropOverlayPicture(UI.TitleScene.Named(layer));
                }

                // And the party, if there was one: its scene comes off the renderer before the room's goes on, its music stops with it, and the 1999.
                if (modern.Party is not null)
                {
                    renderer.SetScene(null, null);
                    renderer.Idle();
                    title.Show(renderer);
                }

                modern.Dispose();
            }

            // Restoring from the title screen.
            if (asked == FrontEndOutcome.Load && front.Slot is { Length: > 0 } chosenSlot &&
                api.Saves?.Read(chosenSlot, out Game.SaveFault titleFault) is { } titleSave)
            {
                api.RestoreGame(titleSave);
                request = SceneRequest.Continuing(api, api.State.Location);
                Log.Info($"Restored {chosenSlot}: {titleSave.Title}");
                asked = FrontEndOutcome.Play;
            }

            if (asked != FrontEndOutcome.Play)
            {
                // Quit from the first menu, so nothing of the room is ever loaded.
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

        // One pass a room.
        var finishes = SurfaceFinishes.Empty;

        while (true)
        {
            // The first frame of the transition, before anything is read.
            loading.Begin(bar: first);

            // On the way into every room rather than once, because the afternoon the moustache belongs to is reached by walking through a door and.
            if (settings.AlwaysWearsMoustache && Game.Assists.GiveMoustache(api.State))
            {
                Log.Info($"Assist: {Game.Assists.Owner} is given the {Game.Assists.Moustache}");
            }

            using SceneGeometry geometry = renderer.CreateGeometry();

            // What each texture's surface is like.
            if (first)
            {
                finishes = SurfaceFinishes.Load( Path.Combine( Path.GetDirectoryName(
                            CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty) .TrimEnd(Path.DirectorySeparatorChar, '/')) ?? ".",
                        "manifests", "material-library.json"),

                    // And from the packs where there is no workspace to read it from, which is every installation that is not a development one.
                    packs);

                if (finishes.Count > 0)
                {
                    Log.Info( $"Surface finishes: {finishes.Count} textures measured, " + $"{finishes.Reflective} smooth enough to reflect, " +
                        $"{finishes.Metallic} metal" + (finishes.Mirrors > 0 ? $", {finishes.Mirrors} mirrors" : string.Empty) +
                        (finishes.Screens > 0 ? $", {finishes.Screens} lit screens" : string.Empty) + (finishes.Corrected > 0
                            ? $", {finishes.Corrected} corrected by hand" : string.Empty));
                }
            }

            geometry.Materials = finishes;

            // How far round things are rounded, so the same object can be photographed both ways without editing anything.
            if (int.TryParse( Option(args, "--round"), CultureInfo.InvariantCulture, out int levels) && levels is >= 0 and <= 4)
            {
                geometry.RoundLevels = levels;
            }

            // Whether a railing, a fence or a chain gets the thickness of what is drawn on it.
            geometry.ThickenCutoutCards = settings.ThickCutoutCards && !args.Contains("--no-thick-cards", StringComparer.OrdinalIgnoreCase);

            // Whether the room is drawn one side at a time, which is what the original does for all opaque world geometry.
            geometry.CullBackFaces = settings.CullBackFaces && !args.Contains("--no-cull", StringComparer.OrdinalIgnoreCase);

            // And whether it stops the sun.
            geometry.CardShadows = !args.Contains("--no-card-shadows", StringComparer.OrdinalIgnoreCase);

            // How many triangles a room's floor may be cut into.
            if (int.TryParse( Option(args, "--relief"), CultureInfo.InvariantCulture, out int budget))
            {
                geometry.Relief = budget > 0 ? ReliefSettings.Default with { TriangleBudget = budget }
                    : ReliefSettings.Off;
            }

            // And whether an outdoor room's ground is weathered at all.
            if (args.Contains("--no-weather", StringComparer.OrdinalIgnoreCase))
            {
                geometry.Relief = geometry.Relief with { Erosion = 0f };
            }

            // A fresh loader each time: it carries the last room's glances and its count of enhanced textures, and neither belongs to the next one.
            var loader = new SceneLoader(archives, Log.Info)
            {
                // The player's preference, with a command-line override so a screenshot can be taken of the same room both ways without editing a.
                SmoothHeads = HeadLevels(args, settings),

                // The same finishes the sink shades with, so the loader can say which of an outdoor scene's textures deserve their relief cut beyond.
                Finishes = finishes,

                // Already read, once, above.
                Characters = characters,
            };

            // What keeps the window drawing while the room is read: the transition's fade while it has picture left to remove, and the loading.
            loader.Progress = () => loading.At(loader.Through);

            {
                // The loose picture layer: the workspace's enhanced set with whatever the player has put in overrides/ laid over it.
                EnhancedTextures? enhanced = Pictures( settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides, language: language);

                loader.Enhanced = enhanced;

                // And the interface's own pictures come from the same layer, so the kit and its brush are replaceable like anything else.
                loose = enhanced;

                // Normal maps sit beside the colour textures rather than among them: a surface may have a better colour and no normal map, or the.
                EnhancedTextures? normals = Pictures( settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    Formats.Rebarn.RebarnKind.Normal, "normals", language);

                // --flat leaves the colour textures enhanced and the surfaces smooth, which is the only way to see what the normal pass alone is.
                bool flat = args.Contains("--flat", StringComparer.OrdinalIgnoreCase);

                loader.Normals = flat ? null : normals;

                // The other two generated sets, beside the normals for the same reason: each is a separate pass and a separate judgement, and a.
                loader.Orms = flat ? null : Pictures( settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    Formats.Rebarn.RebarnKind.Orm, "orm", language);

                loader.Heights = flat ? null : Pictures( settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides,
                    Formats.Rebarn.RebarnKind.Height, "height", language);

                if (first && normals is { Count: > 0 })
                {
                    Log.Info($"Normal maps: {normals.Count} available");
                }

                if (first && !packsOnly && settings.EnhancedTextures && enhancedDirectory is { Length: > 0 })
                {
                    Log.Info(enhanced is { Count: > 0 }
                        ? $"Enhanced textures: {enhanced.Count} available in {enhancedDirectory}"
                        : $"Enhanced textures: none found in {enhancedDirectory}");
                }
            }

            // The modelled trees, beside the textures and gated on their own setting.
            loader.Grass = settings.Grass && !args.Contains("--no-grass", StringComparer.OrdinalIgnoreCase);

            // Whether an outdoor room's ground may depart from the picture painted on it.
            loader.VariedGround = !args.Contains("--flat-ground", StringComparer.OrdinalIgnoreCase);

            if (settings.ModelledTrees)
            {
                TreeLibrary trees = TreeLibrary.Open( packsOnly || enhancedDirectory is not { Length: > 0 }
                        ? string.Empty : Beside(enhancedDirectory, "trees"), packs);

                loader.Trees = trees;

                if (first && !trees.IsEmpty)
                {
                    Log.Info( $"Modelled trees: {trees.Count} grown across {trees.SpeciesCount} " +
                        $"species, {(trees.Packed ? "packed" : "loose")}");
                }
            }

            // The restoration table follows its setting the same way the trees and the improved geometry follow theirs: consulted every time a room.
            CutContent restoring = CutContent.Open(RestorationTier(args, settings), Dressed(), bookshop);
            archives.Restoration = restoring.IsEmpty ? null : restoring;
            archives.RestorationDiagnostics = restoring.IsEmpty ? null : restoreDiagnostics;

            // The improved room geometry, beside the trees and gated on its own setting for the same reasons: it is geometry rather than a bitmap.
            if (settings.ImprovedSceneGeometry)
            {
                EnhancedScenes rooms = EnhancedScenes.Open( packsOnly || enhancedDirectory is not { Length: > 0 }
                        ? string.Empty : Beside(enhancedDirectory, "scene-geometry"), packs);

                loader.Scenes = rooms;

                if (first && !rooms.IsEmpty)
                {
                    Log.Info( $"Improved scene geometry: {rooms.Count} room(s), " + $"{(rooms.Packed ? "packed" : "loose")}");
                }
            }

            // Prop geometry that did not ship with the game.
            ModelLibrary props = ModelLibrary.Open( packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty : Beside(enhancedDirectory, "models"), packs);

            props.Overrides = overrides;
            loader.Models = props.IsEmpty ? null : props;

            if (first && props.Describe() is { } available)
            {
                Log.Info($"Prop models: {available}");
            }

            // Rooms the game never had, built from glTF.
            RoomLibrary builtRooms = RoomLibrary.Open( packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty : Beside(enhancedDirectory, "rooms"), packs);

            builtRooms.Overrides = overrides;
            loader.Rooms = builtRooms.IsEmpty ? null : builtRooms;

            if (first && builtRooms.Describe() is { } roomsAvailable)
            {
                Log.Info($"Built rooms: {roomsAvailable}");
            }

            // The reconstructed horizon, beside the trees and gated on its own setting for the same reason they are: it is geometry rather than a.
            if (settings.TerrainBackdrop)
            {
                string terrain = packsOnly || enhancedDirectory is not { Length: > 0 }
                    ? string.Empty : Beside(enhancedDirectory, "terrain");

                loader.TerrainDirectory = Directory.Exists(terrain) ? terrain : null;
                loader.TerrainPacks = packs.VolumeCount > 0 ? packs : null;

                if (first && loader.TerrainDirectory is not null)
                {
                    Log.Info( "Terrain horizon: " + $"{Directory.EnumerateFiles(terrain, "*.heights.r32").Count()} sets, loose");
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

            // The block-compressed build of the same set, preferred over the originals wherever it has an answer: nothing to decode, a mip chain.
            CompressedTextures compressed = CompressedTextures.Open( packsOnly ? string.Empty
                    : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty), packs, overrides, localized);

            // The setting takes the compressed set out of the way as well as the loose one.
            loader.Compressed = args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase) || !settings.EnhancedTextures
                    ? overrides is null && localized is null ? null : CompressedTextures.Open(string.Empty, null, overrides, localized) : compressed;

            // --flat means flat wherever the maps would have come from.
            if (args.Contains("--flat", StringComparer.OrdinalIgnoreCase))
            {
                loader.FlatSurfaces = true;
            }

            if (first && loader.Compressed is not null && compressed.Describe() is { } sets)
            {
                // Which set came from where, because the two are indistinguishable once a texture is on screen: a run that quietly used a stale.
                Log.Info($"Compressed textures: {sets}");
            }

            loading.Tick();

            var read = Stopwatch.StartNew();

            // Where the time goes, when somebody asked.
            LoadTimeline? timeline = args.Contains("--timings", StringComparer.OrdinalIgnoreCase) ? new LoadTimeline() : null;

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

            // Before the report, so that it describes something that exists.
            geometry.Finish();
            timeline?.Stamp("upload to device (Finish)");
            loading.At(1);

            // The room's open flames, and the lights that stand in them.
            IReadOnlyList<Game.Flame> fires = Game.Flames.In(scene.Models, api.Animations, scene.Bitmaps);
            IReadOnlyList<Formats.Scenes.AuthoredLight> burning = Game.FlameLighting.Rig(scene.Lights, fires);

            // And the painted flame cards out of the picture, because the fire is drawn as a volume now and the card is the 1999 picture of one.
            if (!args.Contains("--no-shader-fire", StringComparer.OrdinalIgnoreCase))
            {
                int cards = Game.Flames.Hide(fires, scene.Models);

                if (cards > 0)
                {
                    Log.Info( $"Fire: {fires.Count} flame(s) drawn as burning gas, " + $"{cards} painted card(s) taken out of the picture");
                }
            }

            // And the things in the room that glow.
            IReadOnlyList<Rendering.Geometry.EmissiveSurface> glows = args.Contains("--no-emissive", StringComparer.OrdinalIgnoreCase) ? []
                    : geometry.Emitters();

            burning = Game.EmissiveLighting.Rig(burning, glows, out int glowing);

            // With the geometry's extent, so the rig can tell a lamp that decays from the scene's key light — placed tens of thousands of units away.
            var windows = new List<Game.Window>();

            foreach (string pane in scene.Geometry?.ObjectNames ?? [])
            {
                if (!Game.Daylight.IsWindow(pane) || SceneScripting.Bounds(scene, pane) is not var (low, high))
                {
                    continue;
                }

                windows.Add(new Game.Window( pane, (low + high) / 2f, Vector3.Distance(low, high) / 2f));
            }

            burning = Game.Daylight.Rig( burning, windows, new SceneExtent(geometry.Minimum, geometry.Maximum), out int moved);

            if (moved > 0)
            {
                Log.Info( $"Daylight: {moved} light(s) moved to {windows.Count} window(s), " + "where a wall can shape them");
            }

            // And the shafts the daylight throws in at them, where the room is indoors and has a sun this hour.
            scene.Shafts = [];
            heat = 0f;

            if (scene.Sun is not null)
            {
                bool roofed = Game.SceneShafts.IsRoofed(scene.Geometry, scene.Walkable);

                // The heat haze over the far ground, outdoors through the hot part of the day: full at noon and two, less at ten and four, none at.
                heat = roofed ? 0f : Game.SceneShafts.Heat(api.State.Timeblock);

                var panes = new List<(string Name, Vector3 Minimum, Vector3 Maximum)>();

                foreach (string pane in scene.Geometry?.ObjectNames ?? [])
                {
                    if (Game.SceneShafts.IsPane(pane) && SceneScripting.Bounds(scene, pane) is var (low, high))
                    {
                        panes.Add((pane, low, high));
                    }
                }

                if (panes.Count > 0 && roofed)
                {
                    scene.Shafts = Game.SceneShafts.For( panes, scene.Sun, (geometry.Minimum, geometry.Maximum),
                        scene.Ground is { } underfoot ? underfoot.Height : null);
                }

                if (scene.Shafts.Count > 0)
                {
                    Log.Info(string.Create( CultureInfo.InvariantCulture, $"Daylight: {scene.Shafts.Count} shaft(s) at {panes.Count} window(s), " +
                        $"{scene.Shafts.Count(s => s.Strength >= 1f)} of them the sun's own"));
                }
            }

            if (args.Contains("--daylight-list", StringComparer.OrdinalIgnoreCase))
            {
                foreach (Game.Window pane in windows)
                {
                    Log.Info(string.Create( CultureInfo.InvariantCulture, $"  window: {pane.Owner}, {pane.Radius * 2:F0} units across"));
                }
            }

            // And balanced for the amount of tracing it is about to be evaluated under.
            burning = Game.RigBalance.For( burning, renderer.Quality, out int dimmed, settings.RealisticLighting);


            renderer.SetLights( burning, new SceneExtent(geometry.Minimum, geometry.Maximum));
            timeline?.Stamp("light rig");

            if (dimmed > 0)
            {
                float keep = Game.RigBalance.Keep( renderer.Quality, settings.RealisticLighting);

                Log.Info(keep <= 0f ? $"Rig: {dimmed} of {burning.Count} lights are the bake's own fill, " +
                      "switched off — only real sources light this room" : $"Rig: {dimmed} of {burning.Count} lights are the bake's own fill, " +
                      $"turned down to {keep * 100:F0}% " + "against the traced occlusion that replaces them");
            }

            if (args.Contains("--emissive-list", StringComparer.OrdinalIgnoreCase))
            {
                foreach (Rendering.Geometry.EmissiveSurface glow in glows)
                {
                    Log.Info(string.Create( CultureInfo.InvariantCulture, $"  glows: {glow.Owner} ({glow.Texture}), {glow.Radius:F1} units across"));
                }
            }

            if (glowing > 0)
            {
                Log.Info( $"Emissive: {glowing} glowing thing(s) lit that had no light of their own");
            }

            if (fires.Count > 0)
            {
                int wavering = burning.Count(l => l.Flicker is { Bias: > 0.5f });
                int lit = burning.Count - scene.Lights.Count;

                string added = lit > 0 ? string.Create(CultureInfo.InvariantCulture, $" and {lit} lit that had none") : string.Empty;

                Log.Info(string.Create( CultureInfo.InvariantCulture, $"Fire: {fires.Count} open flame(s), {wavering} of the artists' lights " +
                    $"wavering with them{added}"));
            }

            if (scene.Sun is { } sun)
            {
                Log.Info( $"Sun: elevation {MathF.Asin(-sun.Direction.Y) * 180f / MathF.PI:0}°, " +
                    $"the rig's other {scene.Lights.Count - 1} lights kept");
            }

            // And its rays, drawn through whatever stands between it and the eye: past the trees outdoors, in at the windows indoors.
            daylight = Rendering.SunRays.For(scene.Sun).Through(scene.Shafts);
            renderer.SetSunRays(daylight.Lit(RaysWanted(settings)));
            renderer.Shimmer = HazeWanted(settings) ? heat : 0f;

            if (heat > 0f)
            {
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Heat: haze over the far ground at {heat:F1}"));
            }

            // The air in the room, for the handful that have any.
            Rendering.FogVolume air = Game.SceneFog.For(scene.Name, api.State.Timeblock);
            renderer.SetFog(air);

            if (air.Any)
            {
                Log.Info(string.Create( CultureInfo.InvariantCulture, $"Fog: lying to y={air.Top:0.#}, thinning over {air.Falloff:0.#} units, " +
                    $"{air.Density:0.####} a unit in {air.Steps} steps"));
            }

            // And whatever was in the air of the room before this one, taken off it for the same reason the fog is.
            renderer.SetParticles([]);

            renderer.Quality = renderer.SupportsRayTracing ? quality ?? settings.Quality : RayTracingQuality.None;

            if (first)
            {
                Log.Info(renderer.SupportsRayTracing ? $"Ray tracing: {renderer.Quality} ({geometry.TraceableTriangleCount} opaque "
                      + $"triangles traced in {geometry.TraceablePartCount} movable part(s))" : "Ray tracing: unavailable on this device");
            }

            Log.Info(string.Create( CultureInfo.InvariantCulture, $"Loaded {scene.Name} in {read.Elapsed.TotalMilliseconds:F0} ms, " +
                $"{geometry.TextureCount} textures resident, {geometry.TexturesReused} reused, " +
                $"{geometry.TextureDeviceBytes / (1024.0 * 1024):F0} MB of them on the device"));

            // What this load actually read, rather than what was available to it.
            if (compressed.FromPacks > 0 || compressed.FromFiles > 0)
            {
                Log.Info( $"Blocks read: {compressed.FromPacks} from packs, " + $"{compressed.FromFiles} from {(compressed.Directory.Length > 0
                        ? compressed.Directory : "loose files")}");
            }

            Log.Info($"Scene {scene.Name}: {geometry.TriangleCount} triangles in "
                + $"{geometry.BatchCount} batches, {geometry.TextureCount} textures" + (loader.EnhancedTexturesUsed > 0
                    ? $" ({loader.EnhancedTexturesUsed} enhanced" + (loader.CompressedUsed > 0 ? $", {loader.CompressedUsed} compressed)" : ")")
                    : string.Empty) + (loader.NormalMapsUsed > 0 ? $", {loader.NormalMapsUsed} normal mapped" : string.Empty)
                + (loader.OrmMapsUsed > 0 ? $", {loader.OrmMapsUsed} with a finish" : string.Empty) + (loader.HeightMapsUsed > 0
                    ? $", {loader.HeightMapsUsed} with relief" : string.Empty) + $", {scene.Lights.Count} authored lights");

            // What the floor cost, when it was displaced.
            if (geometry.DisplacedTriangles > 0)
            {
                string uncut = geometry.ReliefSetApart > 0 ? string.Create( CultureInfo.InvariantCulture, $", {geometry.ReliefSetApart} left uncut")
                    : string.Empty;

                Log.Info(string.Create( CultureInfo.InvariantCulture, $"Relief: floor cut into {geometry.DisplacedTriangles} triangles at " +
                    $"{geometry.ReliefCell:0.#} units a cell, moved up to " +
                    $"{geometry.ReliefDepth:0.##} units ({geometry.ReliefTypically:0.##} typically), " +
                    $"{geometry.ReliefBoundary.Pinned} edges held down and " + $"{geometry.ReliefBoundary.Continued} carried on " +
                    $"(expected {geometry.ReliefExpected}{uncut})"));
            }

            // What the round things cost, and — more to the point — that they happened at all.
            if (geometry.RoundedObjects > 0)
            {
                Log.Info( $"Rounded: {geometry.RoundedTriangles} triangles from " +
                    $"{string.Join(", ", geometry.Rounded.Order(StringComparer.OrdinalIgnoreCase))}");
            }

            // What the railings cost, and that they happened.
            if (geometry.CardsThickened > 0)
            {
                Log.Info(string.Create( CultureInfo.InvariantCulture, $"Railings: {geometry.CardsThickened} keyed cards thickened to " +
                    $"{geometry.CardThickness.Thinnest:0.##}-{geometry.CardThickness.Thickest:0.##} " +
                    $"units, {geometry.CardTriangles} triangles"));

                if (geometry.CardShadowTriangles > 0)
                {
                    Log.Info(string.Create( CultureInfo.InvariantCulture, $"Railing shadows: {geometry.CardShadowTriangles} opaque triangles " +
                        $"traced against"));
                }
            }

            // The floor, which is how an actor knows what height to walk at.
            if (args.Contains("--lights", StringComparer.OrdinalIgnoreCase))
            {
                foreach (Game.Flame flame in fires)
                {
                    string drawn = flame.Visible ? string.Empty : ", not yet drawn";

                    Log.Info(string.Create( CultureInfo.InvariantCulture, $"  flame {flame.Model} at {flame.Position.X:F0},{flame.Position.Y:F0}," +
                        $"{flame.Position.Z:F0} {flame.Height:F1} tall and " + $"{flame.Width:F1} across, a {flame.Kind} burning " +
                        $"{flame.Plume:F1} tall and {flame.Radius * 2f:F1} across from " + $"y {flame.Foot.Y:F1}, " +
                        $"swings {flame.Swing * 100:F0}% at {flame.Rate:F1} Hz{drawn}"));
                }

                foreach (Formats.Scenes.AuthoredLight light in burning)
                {
                    string wavers = light.Flicker is { } flicker ? string.Create( CultureInfo.InvariantCulture,
                            $" flickers {flicker.Swing * 100:F0}% about {flicker.Bias:F0}") : string.Empty;

                    Log.Info(string.Create( CultureInfo.InvariantCulture, $"  light {light.Name} r={light.Radius:F1} i={light.Intensity:F2} " +
                        $"reach={light.AttenuationEnd:F0} at {light.Position.X:F0}," + $"{light.Position.Y:F0},{light.Position.Z:F0}{wavers}"));
                }
            }

            Log.Info(scene.Ground is { } ground ? $"Floor: {scene.Definition.FloorObject()}, {ground.Triangles} triangles"
                : $"Floor: none; {scene.Definition.FloorObject() ?? "the scene names one"}" +
                  " is not in the geometry, so actors hold the height they start at");

            Report(diagnostics, verbose);

            // Everything from here to the first presented frame is the room being made ready rather than read, and it is inside the wait the player.
            timeline?.Stamp("scene report");

            // Whatever was waiting was waiting on the room that has gone.
            host.Scheduler.Clear();

            // And so was whatever the last room's scripts had switched off.
            if (api.State.BlockedHitTests.Count > 0)
            {
                Log.Info( "Hit tests: live again, with the room that switched them off — " + string.Join(
                        ", ", api.State.BlockedHitTests.OrderBy(h => h, StringComparer.Ordinal)));

                api.State.BlockedHitTests.Clear();
            }

            var update = new SceneUpdate( scene, api, loader.Glances, geometry, scene.Actions, new ActionRunner(api), host.Scheduler);

            // Registered again for the new room, and after the update exists because the walking functions need something to walk in.
            SceneScripting.Attach(api, scene, loader.Glances, room, update, Behaviour);
            Showing(api, movies);

            // What is under the pointer, and — for the rooms that fire something into theirs — what is in front of a laser beam.
            var interaction = new SceneInteraction(scene, api)
            {
                Strings = strings, Text = words, Watcher = update, Introductions = introductions,
            };

            // The eleven rooms whose puzzles the original implemented in code rather than in data.
            api.Mechanism = Game.Mechanisms.SceneMechanisms.For( scene.Definition.Mechanism(), update, api, archives);

            update.Mechanism = api.Mechanism;

            if (api.Mechanism is { } machinery)
            {
                Log.Info($"Mechanism: {scene.Name} is a {machinery.Name} room");

                // Where a beam ends is whatever the room puts in front of it, which is the same question the pointer asks and is answered by the.
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

            // --movie NAME plays one straight away, which is how a cutscene is looked at without finding the point in the story that plays it.
            if (first && Option(args, "--movie") is { Length: > 0 } wanted)
            {
                double seconds = movies.Play(wanted);

                Log.Info(seconds > 0 ? $"Movie: {wanted}, {seconds:F1}s" : $"Movie: {wanted} could not be played");
            }

            // How impatient a double-click is.
            live = update;
            update.HurryFactor = settings.HurryFactor;

            // What lets an animation actually move something.
            update.Animations = api.Animations;
            update.Clips = clips;
            update.Characters = characters;

            // Where everybody stands, whenever a clip takes them or lets them go.
            if (args.Contains("--trace-actors", StringComparer.OrdinalIgnoreCase))
            {
                update.TraceActors = Log.Info;
            }

            // What a step sounds like.
            update.Steps = footsteps;

            // What makes a texture an animation asks for resident.
            SceneGeometry paint = geometry;
            SceneLoader late = loader;

            // The bag is thrown away on purpose.
            update.Textures = name => late.LoadTextureLate(paint, name, new Foundation.Diagnostics.DiagnosticBag());

            // What lets a script light the room a second way.
            string standing = scene.Asset?.BspName ?? scene.Name;

            update.Relight = name =>
            {
                Formats.Scenes.SceneAssetFile? asset = archives.ReadText(name + ".SCN") is { } declared
                        ? Formats.Scenes.SceneAssetFile.Parse(declared, name + ".SCN") : null;

                // The asset has to be baked for the geometry that is standing.
                if (asset?.BspName is { Length: > 0 } named && !named.Equals(standing, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (archives.Read(name + ".MUL") is not { } baked || !geometry.SwapLightmaps(Formats.Lightmaps.MulFile.Parse(baked, name + ".MUL")))
                {
                    return false;
                }

                // And the rig with the bake, because they are two halves of one lighting.
                if (asset is { Lights.Count: > 0 })
                {
                    var extent = new SceneExtent(geometry.Minimum, geometry.Maximum);

                    // With the same substitution the room was loaded under: on a daytime exterior the artists' key light is replaced by a.
                    IReadOnlyList<Formats.Scenes.AuthoredLight> rig = scene.Sun is { } daylight
                        ? [.. asset.Lights.Where(l => !Game.Sunlight.IsAuthoredSun( l, geometry.Minimum, geometry.Maximum)), daylight] : asset.Lights;

                    // And with the fires, which the swap would otherwise put out: the bar's fireplace burns under both of RL2's assets and a rig.
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

            // The faces in this room.
            var moving = new Game.Actors.Faces(faces, archives, api.Animations, geometry);

            // And the moustache, when the player has asked for it: Gabriel's face composed out of the game's own moustached Gabriel, GA3, and.
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

            // What an animation's own sound cues reach.
            update.Sound = room is null ? null : (cue, at) => room.PlayAt(cue.Name, at, cue.Gain);

            // Who is speaking, which is what decides whether a character runs their talking script or their listening one.
            update.Speaking = () => moving.Speaking;

            // What a line of dialogue does to the speaker's mouth.
            if (room is not null)
            {
                room.Speaking = moving.Say;

                // Whether a line comes from where its speaker stands or from the middle.
                room.Routing = new Audio.DialogueRoutingOptions
                {
                    CenterAllDialogue = settings.CenterAllDialogue,
                };

                // Where a sound that follows something has got to.
                room.Where = named => update.Where(named);

                // What PlaySoundTrack names: a .STK in the archives, which the audio layer has no way to open on its own.
                room.Soundtracks = named =>
                {
                    // Named by the file that answered rather than by what was asked for, so that "FightDrone" and "FightDrone.STK" are one.
                    string file = Path.HasExtension(named) ? named : named + ".STK";
                    string? text = archives.ReadText(file);

                    if (text is null && !Path.HasExtension(named))
                    {
                        text = archives.ReadText(named);
                        file = named;
                    }

                    return text is null ? null : Formats.Audio.SoundtrackFile.Parse(text, file, new DiagnosticBag());
                };
            }

            // The pose everything opens in, before anything runs.
            if (update.Open() is > 0 and { } posed)
            {
                Log.Info( $"Opening pose: {posed} clip(s) sampled" + (update.Posed.Count > 0
                        ? ", " + string.Join(", ", update.Posed.Select(Described)) : string.Empty));
            }

            // What a room does when nobody is asking it to: the lobby's ceiling fans turn because the scene gave them a script of their own.
            update.StartScenery();

            if (scene.Actions is { } actions)
            {
                actions.Verbs = verbs;
            }

            if (update.Scenic > 0 || update.Fidgeting > 0)
            {
                Log.Info( $"Behaviour: {update.Scenic} prop(s) move on their own, " + $"{update.Fidgeting} character(s) idle, talk and listen");
            }

            Log.Info( $"Update: {update.Movable} actor(s) can turn their head, " + $"{characters.Count} character(s) know how to walk, " +
                $"{moving.Count} face(s) can talk and blink, " + $"{verbs.TopicCount} topic(s) can be raised");

            // What the room does when somebody walks into it, which is mostly deciding where they are standing.
            if (request.State is not null && request.Counts)
            {
                request.State.EnterLocation(request.State.Ego, scene.Name);
            }

            // Nobody is standing in a room that is only being looked at.
            if (!request.Counts && api.Leaning is not null && api.Perform("HideModel", [Sheep.SheepValue.FromString(api.State.Ego)]) is not null)
            {
                Log.Info($"Binoculars: {api.State.Ego} is not in {scene.Name}");

                // Nor is their moped: a roadside's scene file parks it there for whoever rides up, and nobody has.
                foreach (Game.PlacedModel parked in scene.Placed ?? [])
                {
                    if (string.Equals(parked.Noun, "GABES_MOPED", StringComparison.OrdinalIgnoreCase) &&
                        api.Perform("HideModel", [Sheep.SheepValue.FromString(parked.Name)]) is not null)
                    {
                        Log.Info($"Binoculars: {parked.Name} is not in {scene.Name} either");
                    }
                }
            }

            // And the room a look is being put down in is put back as it was: the player standing where they raised the binoculars, facing the way.
            if (api.Resuming is { } put)
            {
                api.Resuming = null;

                if (!update.Place(api.State.Ego, put.Standing, put.Facing))
                {
                    Log.Info($"Binoculars: {api.State.Ego} could not be put back in {scene.Name}");
                }
            }

            // What the binoculars ask of the room they are looking into, out of the game's own BINOCS.SHP: hide its exits so it cannot be walked out.
            if (!request.Counts && api.Leaning is { Sight.Entering.Length: > 0 } leaning && api.Perform( "CallSheep",
                    [
                        Sheep.SheepValue.FromString("binocs"), Sheep.SheepValue.FromString(leaning.Sight.Entering), ]) is not null)
            {
                Log.Info($"Binoculars: {leaning.Sight.Entering} staged {scene.Name}");
            }

            // Before the room is entered, not after.
            room?.Leave();

            if (request.Counts && scene.Actions?.Find("SCENE", "ENTER") is { } entering)
            {
                new ActionRunner(api).Run(entering);
                Log.Info($"entered: SCENE:ENTER [{entering.Case}]");
            }

            // Back from a room the game never had.
            if (api.Returning is { } returning && request.Counts)
            {
                bool home = string.Equals(returning.Room, scene.Name, StringComparison.OrdinalIgnoreCase);

                if (home || !string.Equals(returning.Through, scene.Name, StringComparison.OrdinalIgnoreCase))
                {
                    api.Returning = null;
                }

                if (home && string.Equals(returning.Through, api.State.LastLocation, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info(update.Place(api.State.Ego, returning.Standing, returning.Facing)
                        ? $"Returned: {api.State.Ego} stood where they left {scene.Name} for {returning.Through}"
                        : $"Returned: {api.State.Ego} could not be stood where they left {scene.Name}");

                    // And the view they had, in place of the cut the entering script made toward the door it chose.
                    api.WantedCamera = (returning.Eye, returning.Look);
                    api.State.CameraAngle = string.Empty;
                }
            }

            // What the room sounds like when nothing is happening in it.
            string? bed = room?.StartAmbience(scene.AmbienceRead);

            if (room is { Running.Count: > 0 })
            {
                Log.Info( $"Ambience: {string.Join(", ", room.Running)}" +
                    (bed is { Length: > 0 } ? $", opening with {bed}" : ", opening with a wait") + (room.AmbienceAt is { } at ? string.Create(
                            CultureInfo.InvariantCulture, $" at {at.Position:F0}, full within {at.Minimum:F0} units and " +
                            $"as quiet as it gets past {at.Maximum:F0}") : string.Empty));
            }

            if (first)
            {
                Opening(args, api, scene);
            }
            else if (Option(args, "--then")?.Split(':') is [string n, string v] && scene.Actions?.Find(n.Trim(), v.Trim()) is { } follow)
            {
                // The same as --do, in the second room.
                new ActionRunner(api).Run(follow);

                Log.Info($"Then {n.Trim()}:{v.Trim()} [{follow.Case}]");
            }

            // Arriving somewhere is the moment the story is at rest: the room is built, its opening script has run, and nothing is half-done.
            if (!first && request.Counts)
            {
                api.Saves?.Write(Game.SaveStore.AutoSlot, api.State.Capture($"Arrived at {scene.Name}"));
            }

            // The console outlives the room.
            console.Knows(api.FunctionNames);
            console.Calls = api.Perform;

            // The quest log.
            var journal = new Game.Story.Journal(api.State)
            {
                Text = words, Names = strings,
            };

            // The room is standing and about to be drawn, so this is where the three things that covered the wait finish: the loading screen goes.
            loading.Done();

            // And the fade only when there was something to go out from, and the loading screen has not already seen it out.
            if (fade.Leaving)
            {
                fade.ArriveOver(fade.Black());
            }

            // And the title art comes down, now that there is a room to put in its place.
            if (first)
            {
                renderer.SetBackdrop(null);
            }

            if (timeline is not null)
            {
                // The whole of it, and everything after this point is the room running.
                timeline.Stamp("room set up (scripts, audio, journal)");

                Log.Info(string.Create( CultureInfo.InvariantCulture, $"Where {scene.Name}'s {timeline.TotalMilliseconds:F0} ms went:"));

                Log.Info(timeline.Report());
            }

            RoomExit exit = FlyScene( fade, window, renderer, geometry, scene, cameraName, frameLimit, update, interaction,
                room, movies, hud, Cut, api, screens, Icon, CloseUp, VerbIcon, Artwork, burning, sidney, map, binoculars, api.State, console,
                front, pages, Apply, Relanguaged, args, strings, journal);

            result = exit.Code;

            if (exit.Destination is not { Length: > 0 } next)
            {
                break;
            }

            // Into a room the game never had.
            if (api.Returning is { } spot && string.Equals(spot.Room, scene.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(spot.Through, next, StringComparison.OrdinalIgnoreCase))
            {
                if (archives.IsAdded(next + ".SIF") && !archives.IsAdded(scene.Name + ".SIF"))
                {
                    Log.Info( $"Returning: {api.State.Ego} will be stood back in {scene.Name} on " + $"the way out of {next}");
                }
                else
                {
                    api.Returning = null;
                }
            }

            // Before the room is taken down, because what the fade darkens is a photograph of it and the photograph comes off the swapchain.
            fade.Begin();

            // The geometry is about to go.
            renderer.SetScene(null, null);
            renderer.Idle();

            // Whether the point in the story is over.
            bool looking = Looking(api);

            if (!looking)
            {
                api.State.Location = next.ToUpperInvariant();
            }

            Timeblock was = api.State.Timeblock;

            if (!looking && Complete(api) is { Length: > 0 } instead)
            {
                next = instead;

                // And the room stops being audible here, rather than when the next one is built.
                if (room is { Running.Count: > 0 } sounding)
                {
                    Log.Info($"Room tone: {string.Join(", ", sounding.Running)} stopped with the timeblock");
                }

                room?.Leave();

                // The screen belongs to the film and the card from here, and both of them are the picture rather than something drawn over it — a.
                fade.Black();
                fade.Clear();

                // The film the timeblock goes out on, where it has one.
                if (movies.Play(was + "end") is > 0 and { } showing)
                {
                    Log.Info( string.Create(CultureInfo.InvariantCulture, $"Closing film: {was}end, {showing:F1}s"));

                    // And watched to the end, or until the player stops it.
                    if (frameLimit > 0)
                    {
                        // A run photographing something is not watching a film, and 212PEND is thirty-nine seconds long.
                        movies.Stop();
                        Log.Info($"Closing film: {was}end passed over");
                    }
                    else
                    {
                        double waited = 0;
                        bool pressed = false;

                        bool cut = Watch( window, renderer, movies, pages, Stopwatch.StartNew(), ref waited, ref pressed, SayForFilm,
                            front.Settings.MovieSubtitles);

                        if (cut)
                        {
                            Log.Info($"Closing film: {was}end skipped");
                        }
                    }
                }

                // And then say so.
                Announce( window, renderer, pages, strings, api.State.Timeblock, Art( archives, Pictures(
                    settings.EnhancedTextures, packsOnly, enhancedDirectory, overrides, language: language), settings.EnhancedTextures
                            ? CompressedTextures.Open( packsOnly ? string.Empty : CompressedTextureDirectory(args, enhancedDirectory ?? string.Empty),
                                packs, overrides, localized) : overrides is null && localized is null ? null
                                : CompressedTextures.Open(string.Empty, null, overrides, localized), diagnostics, $"TBT{api.State.Timeblock}.BMP"),

                    // Always out of the archives, whatever the paintings are being read from.
                    Game.TimeblockCard.Read(archives, api.State.Timeblock.ToString()), audio, sounds);

                // And the card goes out into the next room the same way a room does, which also gives the load that follows something to draw frames.
                fade.Begin();
            }

            // Which of the three a room is: somewhere the player has gone, somewhere they are looking at, or somewhere they are looking up from.
            request = api.Resuming is not null ? SceneRequest.Resuming(api, next) : api.Leaning is null ? SceneRequest.Continuing(api, next)
                    : SceneRequest.Peeking(api, next);

            // The next room has its own idea of where to stand; the camera the player named belonged to the one they have left.
            cameraName = null;
            first = false;
        }

        audio?.Dispose();
        localized?.Dispose();

        // What a temporal filter would read.
        if (args.Contains("--motion", StringComparer.OrdinalIgnoreCase) && renderer.CaptureMotion() is { } motion)
        {
            int pixels = motion.Length / 2;
            var mask = new byte[pixels];
            double total = 0;
            double most = 0;
            int moving = 0;

            for (int i = 0; i < motion.Length; i += 2)
            {
                double length = Math.Sqrt( (motion[i] * motion[i]) + (motion[i + 1] * motion[i + 1]));

                total += length;
                most = Math.Max(most, length);
                mask[i / 2] = (byte)Math.Clamp(length * 24, 0, 255);

                if (length > 0.5)
                {
                    moving++;
                }
            }

            // Eight bits a pixel, the viewport's size, so that a run that reports something odd can be looked at rather than only counted.
            File.WriteAllBytes("motion.raw", mask);

            Log.Info(string.Create( CultureInfo.InvariantCulture, $"Motion: mean {total / pixels:F2} px, largest {most:F1} px, " +
                $"{100.0 * moving / pixels:F1}% of the frame moved more than half a pixel"));
        }

        if (screenshotPath is not null && renderer.Capture() is { } capture)
        {
            File.WriteAllBytes(screenshotPath, Formats.Bitmaps.PngWriter.Encode(capture));
            Log.Info($"Wrote {screenshotPath}");
        }

        return result;
    }

    /// <summary>The fonts the interface will draw with, best first.</summary>
    private static readonly string[] CaptionFonts =
    [
        "F_CAPTION_D_26", "F_CAPTION_D_20", "F_CAPTION_D_16", "F_CAPTION_DEFAULT", "F_ARIAL_T12", "F_ARIAL_T10", "F_ARIAL_T8", ];

    /// <summary>Lets the scripts play a movie.</summary>
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

            Log.Info(seconds > 0 ? $"Movie: {name}, {seconds:F1}s" : movies.Skipping ? $"Movie: {name} skipped"
                    : $"Movie: {name} could not be played");

            return seconds;
        }

        foreach (string called in (string[])["PlayMovie", "PlayFullScreenMovie", "PlayFullScreenMovieX"])
        {
            api.Register(called, a => SheepValue.FromInt((int)Start(a)), waitable: true);
        }

        // Asked before the call is performed, which is why it opens the movie to find out and the performing call then finds it already playing.
        api.MovieSeconds = name => movies.Playing && string.Equals( movies.Showing, name, StringComparison.OrdinalIgnoreCase)
                ? movies.Seconds - movies.At : movies.Play(name);
    }

    /// <summary>A sibling of the enhanced textures directory.</summary>
    /// <returns>Its path.</returns>
    /// <param name="enhanced">Where the enhanced colour textures are.</param>
    /// <param name="what">The sibling's name.</param>
    private static string Beside(string enhanced, string what) => Path.Combine(
            Path.GetDirectoryName(enhanced.TrimEnd(Path.DirectorySeparatorChar, '/')) ?? enhanced, what);

    /// <summary>Finds the typeface the interface draws with.</summary>
    /// <returns>The font, or null to fall back to GK3's own sheets.</returns>
    /// <param name="named">A file named on the command line, or null.</param>
    /// <param name="enhancedDirectory">The content workspace's enhanced set, if any.</param>
    /// <param name="diagnostics">Where a font that will not read is reported.</param>
    private static Formats.Fonts.TrueTypeFile? InterfaceFont( string? named, string? enhancedDirectory, DiagnosticBag diagnostics)
    {
        foreach (string path in Typefaces(named, enhancedDirectory))
        {
            try
            {
                if (Formats.Fonts.TrueTypeFile.Parse( File.ReadAllBytes(path), Path.GetFileName(path), diagnostics) is { } read)
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
        using Stream? carried = typeof(Application).Assembly.GetManifestResourceStream( "GK3Reborn.Assets.Fonts.NotoSerif-Regular.ttf");

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
                .. Directory.EnumerateFiles(beside) .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) .OrderBy(f => f, StringComparer.OrdinalIgnoreCase), ];
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
    /// <returns>A whole number, at least one.</returns>
    /// <param name="font">The rung that was picked.</param>
    /// <param name="wanted">The height that was asked for.</param>
    private static int Magnification(Formats.Ui.FontFile font, int wanted) =>
        font.Height <= 0 ? 1 : Math.Clamp((int)MathF.Round((float)wanted / font.Height), 1, 4);

    /// <summary>Asks whether this point in the story is over, and moves the clock on if it is.</summary>
    /// <returns>The room to open instead, or null to open the one that was asked for.</returns>
    /// <param name="api">The game.</param>
    private static string? Complete(Gk3SheepApi api)
    {
        if (Game.Story.TimeblockRules.Check(api.State) is not { } completion)
        {
            return null;
        }

        Timeblock was = api.State.Timeblock;

        // Through the same door SetTime and SetLocationTime went through, so a timeblock the rules end and one a script ends are the same event.
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
    /// <returns>How many were loaded.</returns>
    /// <param name="archives">The game's archives.</param>
    /// <param name="host">Where they go.</param>
    /// <param name="catalogue">Receives every function prototype the scripts name, for whatever wants to say how a call should be written.</param>
    private static int LoadScripts( GameArchives archives, ScriptHost host, Sheep.SheepSignatures? catalogue = null)
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

                // Every compiled script carries the prototypes of everything it calls, so reading the 224 of them is also how the console learns.
                foreach (Sheep.SheepImport import in script.Imports)
                {
                    catalogue?.Add(import, name);
                }
            }
            catch (Formats.FormatParseException)
            {
                // A script that will not parse is one call that does nothing, not a game that will not start.
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

        Log.Info(verbose ? $"{problems.Length} assets could not be loaded:"
            : $"({problems.Length} assets could not be loaded; --verbose lists them)");

        if (verbose)
        {
            foreach (Diagnostic problem in problems)
            {
                Log.Info($"  {problem}");
            }
        }
    }

    /// <summary>The switches that set something going before the player takes over.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room they act on.</param>
    private static void Opening(string[] args, Gk3SheepApi api, LoadedScene scene)
    {
        // Before any of them, because it says what has already happened and an action's case is a question about exactly that.
        Already(args, api);

        // Several, separated by semicolons, because one action is often the setup for the one worth looking at: inspecting a thing and then walking.
        foreach (string asked in Option(args, "--do")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            Do(asked, api, scene);
        }

        Opened(args, api, scene);
    }

    /// <summary>Writes into the story whatever --did says has already happened.</summary>
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
            // A scene's own [ACTORS] blocks are conditional, and the conditions ask about game variables and about where somebody is — not about.
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
                case [string noun, string verb, string count] when int.TryParse(count, CultureInfo.InvariantCulture, out int times):
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

    /// <summary>Performs one --do.</summary>
    /// <param name="asked">The action, as noun:verb.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room it acts on.</param>
    private static void Do(string asked, Gk3SheepApi api, LoadedScene scene)
    {
        // As the player, not as Gabriel: half the rules in the game are written twice, once for each of them, and asking with the wrong one silently.
        if (asked.Split(':') is [string noun, string verb] && scene.Actions?.Find(noun.Trim(), verb.Trim(), api.State.Ego) is { } rule)
        {
            ActionOutcome outcome = new ActionRunner(api).Run(rule);

            string did = outcome.Deferred ? string.Create( CultureInfo.InvariantCulture, $"walking {outcome.Approaching:F1}s first, then " +
                    $"{outcome.Statements.Count} statement(s)") : $"{(outcome.Ran ? "ran" : "refused")} {outcome.Statements.Count} statement(s)";

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

        // Things in the bag, for looking at what carrying them changes.
        if (Option(args, "--carry") is { Length: > 0 } carrying)
        {
            foreach (string item in carrying.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                api.State.Inventory.Add(api.State.Ego, item.Trim());
            }

            Log.Info( $"Carrying: {string.Join(", ", api.State.Inventory.ItemsOf(api.State.Ego))}");
        }

        // Open a screen on the way in, for looking at one on purpose.
        if (Option(args, "--screen") is { Length: > 0 } wanted && wanted.Split(':') is [string named, ..] &&
            Enum.TryParse(named, ignoreCase: true, out ScreenKind kind))
        {
            // Everything after the kind, colons included: a subject may carry one of its own, as ride:TR1 does.
            string? about = wanted.Split(':', 2) is [_, string subject] ? subject : null;

            if (about is { Length: > 0 })
            {
                // Carried, because a screen about something the player does not have is a screen the action files answer differently about.
                api.State.Inventory.Add(api.State.Ego, about);
                api.State.Inventory.SetActive(api.State.Ego, about);
            }

            api.State.Screens.Show(new Screen(kind, about));
            Log.Info($"Screen: {kind}{(about is null ? string.Empty : $" ({about})")}");
        }

        // And put something into Sidney on the way in, for the same reason: its screens are about files, and a screenshot of one with nothing in it.
        if (Option(args, "--scan") is { Length: > 0 } scanning && api.Sidney is { } machine)
        {
            foreach (string item in scanning.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // The same path a click takes, so a still photographed from the command line is of the state the game would actually be in.
                ScanIntoSidney(item.Trim(), api, scene, machine, console: null);
            }

            if (machine.Files.Count > 0)
            {
                machine.OpenFile(machine.Files[0]);
            }
        }

        // A colon opens something on the page as well, which is the only way a screen that is about one thing — a message, a suspect's file — can be.
        if (Option(args, "--sidney") is { Length: > 0 } page && api.Sidney is { } opened && page.Split(':') is [string pageName, ..] &&
            Enum.TryParse(pageName, ignoreCase: true, out Game.Sidney.SidneyScreen which))
        {
            opened.Screen = which;

            if (page.Split(':') is [_, string about, ..] && about.Length > 0)
            {
                switch (which)
                {
                    case Game.Sidney.SidneyScreen.EMail:
                        opened.ReadMail( opened.Mail().FirstOrDefault( m => m.Id.Equals(about, StringComparison.OrdinalIgnoreCase)));

                        break;

                    case Game.Sidney.SidneyScreen.Suspects when int.TryParse(about, out int index):
                        opened.OpenSuspect( opened.Suspects().FirstOrDefault(s => s.Index == index));

                        break;

                    case Game.Sidney.SidneyScreen.Search:
                        opened.Typed = about;
                        opened.Look();

                        break;

                    case Game.Sidney.SidneyScreen.Translate:
                        opened.OpenForTranslation( opened.Files.FirstOrDefault( f => f.Item.Equals(about, StringComparison.OrdinalIgnoreCase)));

                        break;

                    default:
                        opened.OpenFile( opened.Files.FirstOrDefault( f => f.Item.Equals(about, StringComparison.OrdinalIgnoreCase)));

                        break;
                }

                Log.Info($"Sidney: {which} ({about})");
            }
            else
            {
                Log.Info($"Sidney: {which}");
            }
        }

        // Operations run on Sidney's files before the first frame, as the analyze screen's buttons would: ITEM:ACTION, so that a chain several files.
        if (Option(args, "--analyse") is { Length: > 0 } chain && api.Sidney is { } analysing)
        {
            foreach (string step in chain.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = step.Split(':');
                string item = parts[0];

                analysing.OpenFile( analysing.Files.FirstOrDefault( f => f.Item.Equals(item, StringComparison.OrdinalIgnoreCase)));

                // An item on its own opens it and does nothing, which is how a chain ends on the file that should be showing.
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

        // Files linked to whoever is open on the suspects screen, which is the only way a still can be taken of a suspect whose vehicle has been.
        if (Option(args, "--link") is { Length: > 0 } linking && api.Sidney is { } linker)
        {
            foreach (string item in linking.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (linker.Files.FirstOrDefault( f => f.Item.Equals(item.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } file)
                {
                    Log.Error($"--link: {item} is not a file Sidney holds; scan it first.");

                    continue;
                }

                Log.Info($"Linked {item}: {linker.LinkToSuspect(file).Text}");
            }
        }

        // Places marked on Sidney's map, for photographing the one screen whose whole content the player puts there themselves.
        if (Option(args, "--mark") is { Length: > 0 } marks && api.Sidney is { } marking)
        {
            foreach (string place in marks.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (place.Split(',') is [string across, string down] && float.TryParse(across, CultureInfo.InvariantCulture, out float mx) &&
                    float.TryParse(down, CultureInfo.InvariantCulture, out float my))
                {
                    Log.Info($"Marked {mx}, {my}: {marking.Mark(new Vector2(mx, my)).Text}");
                }
            }
        }

        // And the figure laid over the country, which is the last of the map's states that a still cannot otherwise be taken of: laying one is a.
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

        // The ruling, for photographing the chessboard the map puzzle ends on.
        if (Option(args, "--grid") is { Length: > 0 } ruling && api.Sidney is { } ruled &&
            int.TryParse(ruling, CultureInfo.InvariantCulture, out int cells))
        {
            ruled.RuleInShape = cells < 0;

            Log.Info($"Grid {cells}: {ruled.Rule(Math.Abs(cells)).Text}");
        }

        // The map puzzle is a sequence, so photographing it needs one.
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

                    case "SHAPE" when Enum.TryParse( with, ignoreCase: true, out Game.Sidney.MapShape figure):
                        Log.Info($"  shape {figure}: {plotting.LayShape(figure).Text}");
                        break;

                    case "GRID" when int.TryParse( with, CultureInfo.InvariantCulture, out int squares):
                        plotting.RuleInShape = squares < 0;
                        Log.Info($"  grid {squares}: {plotting.Rule(Math.Abs(squares)).Text}");
                        break;

                    case "ASSIST":
                        plotting.Assist();
                        Log.Info($"  assist: {plotting.Finish(yes: true).Text.Split((char)10)[0]}");
                        break;

                    case "DO" when Enum.TryParse( with, ignoreCase: true, out Game.Sidney.SidneyAction did):
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
            Sheep.SheepExpression.Evaluate( $"GlideToCameraAngle(\"{destination}\")", api);
            Log.Info($"Gliding to {destination}");
        }

        if (Option(args, "--glance")?.Split(':') is [string who, string at])
        {
            Sheep.SheepExpression.Evaluate( $"LookitActor(\"{who.Trim()}\", \"{at.Trim()}\", \"\", 0)", api);

            foreach (Diagnostic diagnostic in api.Diagnostics.Items)
            {
                Log.Info($"  {diagnostic}");
            }
        }
    }

    /// <summary>Whether the player is spelling something into Sidney rather than playing the game.</summary>
    /// <returns>True while one of its two text boxes has the keyboard.</returns>
    /// <param name="story">The game, for what is on top of the screen stack.</param>
    /// <param name="sidney">Grace's computer, or null in a run that has none.</param>
    private static bool Spelling(GameState story, Game.Sidney.SidneyMachine? sidney) => sidney is { } machine &&
        story.Screens.Top?.Kind == ScreenKind.Sidney && (machine.Screen == Game.Sidney.SidneyScreen.Search || machine.Appending);

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
}

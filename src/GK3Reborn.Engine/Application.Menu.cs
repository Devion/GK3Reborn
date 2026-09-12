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
/// <summary>The title screen, the pause menu and the films.</summary>
public static partial class Application
{
    /// <summary>The game's own title screen.</summary>
    private const string TitlePicture = "TITLE.BMP";

    /// <summary>The music under the menu.</summary>
    private const string ThemeMusic = "THEME.WAV";

    /// <summary>How long the process has been running.</summary>
    private static readonly Stopwatch Since = Stopwatch.StartNew();

    /// <summary>Finds the title art.</summary>
    /// <returns>The picture and where it came from; empty when there is none to be had.</returns>
    /// <param name="archives">The game's own.</param>
    /// <param name="enhanced">A higher-resolution set, or null.</param>
    /// <param name="compressed">The block-compressed set, packs included, or null.</param>
    /// <param name="diagnostics">Where a picture that will not decode is reported.</param>
    private static TitleScreen TitleArt( GameArchives archives, EnhancedTextures? enhanced, CompressedTextures? compressed,
        DiagnosticBag diagnostics) => Art(archives, enhanced, compressed, diagnostics, TitlePicture);

    /// <summary>Reads one of the game's full-screen pictures, from wherever it is to be had.</summary>
    /// <returns>The picture, or nothing when no source has it.</returns>
    /// <param name="archives">The game's own barns.</param>
    /// <param name="enhanced">A directory of upscaled pictures, if there is one.</param>
    /// <param name="compressed">The block-compressed build or a pack, if there is one.</param>
    /// <param name="diagnostics">Where a picture that will not decode is reported.</param>
    /// <param name="file">Its file name, with the extension.</param>
    private static TitleScreen Art( GameArchives archives, EnhancedTextures? enhanced, CompressedTextures? compressed, DiagnosticBag diagnostics,
        string file)
    {
        string bare = Path.GetFileNameWithoutExtension(file);

        if (enhanced?.Read(bare, diagnostics) is { } better)
        {
            return new TitleScreen(better, null, $"from {enhanced.Directory}");
        }

        if (compressed?.Read(bare, diagnostics) is { } blocks)
        {
            // A pack is what a shipped game has and is opened with no directory at all, which is how the two are told apart without asking the pack.
            return new TitleScreen( null, blocks, compressed.Directory.Length > 0 ? $"from {compressed.Directory}" : "from a pack");
        }

        try
        {
            return archives.Read(file) is { } bytes ? new TitleScreen( Formats.Bitmaps.BitmapDecoder.Decode(bytes, file), null, "from the archives")
                : default;
        }
        catch (FormatException error)
        {
            // A menu without its picture is a menu; a game that will not start because a decorative bitmap is malformed is not.
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

    /// <summary>Says that the story has moved on to another part of the day.</summary>
    /// <param name="window">The window, for the click or key that ends it.</param>
    /// <param name="renderer">What draws the picture and the words.</param>
    /// <param name="pages">The menu's own typeface, which is the big one.</param>
    /// <param name="strings">What the game calls this part of the day.</param>
    /// <param name="now">Where the clock has got to.</param>
    /// <param name="art">The painting for it, or nothing.</param>
    /// <param name="card">The lettering that types itself, or null when it cannot be had.</param>
    /// <param name="audio">The device, or null when there is none.</param>
    /// <param name="sounds">Where the ticking clock comes from.</param>
    private static void Announce( Platform.SilkGameWindow window, Rendering.IRenderer renderer, MenuPage? pages, GameStrings strings, Timeblock now,
        TitleScreen art, Game.TimeblockCard? card, Audio.OpenAlBackend? audio, SoundLibrary sounds)
    {
        art.Show(renderer);

        string name = strings.When(now.ToString()) is { Length: > 0 } called ? called : now.ToString();

        // The frames go on the device once.
        Game.TimeblockCard? typed = art.Exists && pages is not null ? card : null;

        int[] lettering = typed is null ? [] : Lettering(renderer, typed);

        if (lettering.Length == 0)
        {
            typed = null;
        }

        double typing = typed?.Seconds ?? 0;
        double stays = typed is not null ? typing + CardHeldSeconds : CardSeconds;

        string behind = art.Exists ? $", over {art.Width}x{art.Height} of painting" : ", with no painting";

        string written = typed is not null ? string.Create( CultureInfo.InvariantCulture, $", typed in {lettering.Length} frames over {typing:F1}s")
            : ", named in the port's own face";

        Log.Info($"Card: {name}{behind}{written}");

        Audio.AudioVoice ticking = audio is not null && sounds.Read(TimeblockClock) is { } clockwork ? audio.Play(clockwork, Audio.AudioBus.Effects)
            : Audio.AudioVoice.None;

        var clock = Stopwatch.StartNew();

        // A press that is still down from before does not count: the click that walked through the door is what brought the player here.
        window.Forget();

        while (!window.IsClosing && clock.Elapsed.TotalSeconds < stays)
        {
            window.PumpEvents();

            if (window.WasClicked(Platform.PointerButton.Primary) || window.WasPressed(Platform.EditKey.Enter) ||
                window.WasPressed(Platform.EditKey.Escape))
            {
                break;
            }

            double elapsed = clock.Elapsed.TotalSeconds;

            // Against the painting rather than against the window.
            if (typed is not null && renderer.PictureRect(window.FramebufferWidth, window.FramebufferHeight) is { Z: > 0, W: > 0 } painting)
            {
                pages!.Announcing( lettering[typed.At(elapsed)], typed.Over(painting), window.FramebufferWidth, window.FramebufferHeight);
            }
            else
            {
                pages?.Announcing(name, window.FramebufferWidth, window.FramebufferHeight);
            }

            renderer.SetOverlay(pages?.Overlay);

            // The clock goes quiet as the card does.
            if (audio is not null && stays - elapsed < CardFadeSeconds)
            {
                audio.SetVoiceGain( ticking, (float)Math.Clamp((stays - elapsed) / CardFadeSeconds, 0, 1));
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
    /// <returns>A picture number per frame, or nothing when the device refused one.</returns>
    /// <param name="renderer">What holds them.</param>
    /// <param name="card">The lettering.</param>
    private static int[] Lettering(Rendering.IRenderer renderer, Game.TimeblockCard card)
    {
        var numbers = new int[card.Frames.Count];

        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = renderer.AddOverlayPicture( LetteringName(card.Timeblock, i), card.Frames[i]);

            if (numbers[i] > 0)
            {
                continue;
            }

            for (int drop = 0; drop < i; drop++)
            {
                renderer.DropOverlayPicture(LetteringName(card.Timeblock, drop));
            }

            Log.Warning( "WARNING GK3R3458: the card's lettering would not go on the device, so the " + "name is written out instead.");

            return [];
        }

        return numbers;
    }

    /// <summary>What one frame of lettering is called on the device.</summary>
    /// <returns>The name.</returns>
    /// <param name="timeblock">Which card it belongs to.</param>
    /// <param name="frame">Which frame.</param>
    private static string LetteringName(string timeblock, int frame) => string.Create(CultureInfo.InvariantCulture, $"card:{timeblock}:{frame:00}");

    /// <summary>The picture behind the menu, in whichever form it was found.</summary>
    /// <param name="Picture">Pixels, from a loose file or the archives.</param>
    /// <param name="Blocks">Or block-compressed, from the compressed build or a pack.</param>
    /// <param name="From">Where it came from, for the report.</param>
    private readonly record struct TitleScreen( Formats.Bitmaps.DecodedImage? Picture, Formats.Bitmaps.CompressedImage? Blocks, string From)
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
    /// <returns>The voice, so it can be stopped again.</returns>
    /// <param name="audio">The device, or null when there is none.</param>
    /// <param name="sounds">Where sounds come from.</param>
    private static Audio.AudioVoice Theme(Audio.OpenAlBackend? audio, SoundLibrary sounds)
    {
        if (audio is null || sounds.Read(ThemeMusic) is not { } music)
        {
            return Audio.AudioVoice.None;
        }

        // On the music bus, so the music slider is the thing that turns it down.
        return audio.Play(music, Audio.AudioBus.Music, repeat: true);
    }

    /// <summary>Shows the menu until the player leaves it.</summary>
    /// <returns>What the player asked for.</returns>
    /// <param name="window">The window.</param>
    /// <param name="renderer">What draws it.</param>
    /// <param name="pages">The drawn page.</param>
    /// <param name="front">What the pages hold and what choosing a row does.</param>
    /// <param name="apply">What to do with a setting the moment it changes.</param>
    /// <param name="behind">What is behind it, and so what it has to draw itself.</param>
    /// <param name="cut">Cuts a fresh sheet of letters when the window changes size.</param>
    /// <param name="frames">Leave after this many frames, or zero to wait for the player.</param>
    /// <param name="photograph">Where to write the last frame, if anywhere.</param>
    /// <param name="scene">The port's own title screen, drawn under the page, or null when the menu is over the 1999 picture or over the.</param>
    private static FrontEndOutcome ShowMenu( Platform.SilkGameWindow window, Rendering.IRenderer renderer, MenuPage pages, FrontEnd front,
        Action<Settings> apply, MenuBehind behind, Func<OverlayAtlas?> cut, int frames = 0, string? photograph = null, UI.TitleScene? scene = null)
    {
        FrontEndPage showing = front.Page;
        int laidOutFor = window.FramebufferHeight;
        float laidOutAt = front.Settings.TextScale;

        // The menu owns the screen while it is up, so nothing left over from a transition gets to darken it: pausing on the frame a room was still.
        renderer.Fade = 0f;

        pages.Behind = behind;

        // Into the page's own display list rather than behind it, so that the statue and the rows over it are one frame.
        pages.Backdrop = scene is null ? null : scene.Draw;

        Place(pages, front, behind);
        pages.Reset(front.Items);

        int drawn = 0;

        // Which slider row a held pointer grabbed, and whether it was held last frame.
        int grabbed = -1;
        bool heldLast = false;

        // How long the last frame took, which is all the page needs to slide rather than jump.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        double previous = 0;

        while (!window.IsClosing)
        {
            window.PumpEvents();

            double now = elapsed.Elapsed.TotalSeconds;
            float seconds = (float)Math.Clamp(now - previous, 0, 0.1);
            previous = now;

            // The one thing on this screen that is not waiting for the player.
            scene?.Advance(seconds);

            // What the picture pages need to be able to say, refreshed every frame because every one of them can change while they are on screen.
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

            // The list down the side, and which of it is highlighted.
            pages.Sections = front.OnSettings ? Aside(front) : [];
            pages.Section = front.Section;

            IReadOnlyList<MenuItem> items = front.Items;

            // A window that goes fullscreen doubles in height, and a menu that stayed the size it was laid out at would be a postage stamp in the.
            if (window.FramebufferHeight != laidOutFor || front.Settings.TextScale != laidOutAt)
            {
                laidOutFor = window.FramebufferHeight;
                laidOutAt = front.Settings.TextScale;

                if (pages.Overlay.Atlas.Scalable && cut() is { } again)
                {
                    pages.Retarget(again);
                }
            }

            pages.Overlay.Magnify = pages.Overlay.Atlas.Scalable ? 1 : UI.TextSizing.MenuMagnification(
                    window.FramebufferHeight, pages.Overlay.Atlas.Height, laidOutAt);

            Vector2 pointer = new( window.PointerPosition.X * window.DpiScale, window.PointerPosition.Y * window.DpiScale);

            MenuAction action = MenuAction.None;

            // A row on the Controls page that is waiting to be told what to answer to takes the whole keyboard and the whole pad, because the answer.
            if (front.Listening)
            {
                if (front.Captured( window.AnyKey, window.AnyButton, window.WasPressed(Platform.EditKey.Backspace)))
                {
                    apply(front.Settings);
                }

                pages.Build( front.Title, front.Items, window.FramebufferWidth, window.FramebufferHeight, pointer, seconds);

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

            // On a line of buttons across the window, left and right are what moves along it.
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

            // Page Up and Page Down, and the shoulder buttons on a pad.
            if (window.WasPressed(Platform.EditKey.PreviousSection))
            {
                front.StepSection(-1);
            }

            if (window.WasPressed(Platform.EditKey.NextSection))
            {
                front.StepSection(1);
            }

            // The wheel scrolls the page rather than stepping the selection.
            if (window.ScrollDelta != 0)
            {
                pages.Wheel(window.ScrollDelta);
            }

            // What a drag grabbed, decided on the edge of the press and held until the button comes up.
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

                // A click on no row of the first page goes to the title itself, whose letters can be clicked.
                if (!action.Happened && front.Page == FrontEndPage.Main)
                {
                    scene?.Click(pointer, window.FramebufferWidth, window.FramebufferHeight);
                }
            }
            else if (window.IsDragging && pages.Drag(pointer, items, grabbed) is { Happened: true } dragged)
            {
                // Held rather than clicked: a volume is set by ear, which means hearing it move rather than hearing where it landed.
                action = dragged;
            }

            // The one row the page draws that is not the front end's: the way out of the settings, which the sidebar carries so that somebody using.
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
                // Out of a settings page to the one before it, and out of the top of the menu only when there is a room to go back to.
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
                // On the way out rather than on every keystroke: dragging a volume slider across a page is a hundred changes and none of them is.
                if (front.Commit())
                {
                    Log.Info($"Settings: written to {front.StoredAt ?? Settings.DefaultPath}");
                }

                // The click that chose Play is still on the frame's books, and this is the one path out of the loop that does not reach the EndFrame.
                window.EndFrame();

                return outcome;
            }

            pages.Build( front.Title, front.Items, window.FramebufferWidth, window.FramebufferHeight, pointer, seconds);

            renderer.SetOverlay(pages.Overlay);

            // The party, once there is one, is a scene the renderer draws under the page: the title screen's own list has no black in it from then.
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

            // --frames, which is how the menu is photographed: a run with no keyboard would otherwise sit on the first page until somebody closed.
            if (frames > 0 && drawn >= frames)
            {
                if (photograph is { Length: > 0 } && renderer.Capture() is { } picture)
                {
                    File.WriteAllBytes( photograph, Formats.Bitmaps.PngWriter.Encode(picture));

                    Log.Info($"Wrote {photograph}");
                }

                return FrontEndOutcome.Quit;
            }
        }

        front.Commit();
        return FrontEndOutcome.Quit;
    }

    /// <summary>The settings screen's sections, and the way out under them.</summary>
    /// <returns>The sections and the way out.</returns>
    /// <param name="front">The front end, for what its sections are called.</param>
    private static MenuSection[] Aside(FrontEnd front) =>
        [.. front.Tabs, new MenuSection("back", front.Text.Say("menu.back", "Back"))];

    /// <summary>Puts the page where it does not cover what is behind it.</summary>
    /// <param name="pages">The page.</param>
    /// <param name="front">Which page is showing.</param>
    /// <param name="behind">What is behind it.</param>
    private static void Place(MenuPage pages, FrontEnd front, MenuBehind behind)
    {
        // The port's own screen, on its first page: one line of buttons in the black under the wall, and the statue standing behind them.
        pages.Horizontal = behind == MenuBehind.Modern && front.Page == FrontEndPage.Main;

        if (pages.Horizontal)
        {
            pages.Down = 0.905f;
            pages.Across = 0.5f;

            return;
        }

        bool overArt = behind is MenuBehind.Picture or MenuBehind.Modern && front.Page == FrontEndPage.Main;

        pages.Down = overArt ? 0.72f : 0.5f;
        pages.Across = overArt ? 0.17f : 0.5f;
    }

    /// <summary>Plays the films the game opens with.</summary>
    /// <param name="window">The window.</param>
    /// <param name="renderer">What draws them.</param>
    /// <param name="movies">What plays them.</param>
    /// <param name="hint">What draws the way out, or null when there is no font.</param>
    /// <param name="films">Which films, in order.</param>
    /// <param name="captioned">Whether to write out what is said in them.</param>
    private static void ShowIntro( Platform.SilkGameWindow window, Rendering.IRenderer renderer, Game.MoviePlayer movies, MenuPage? hint,
        IReadOnlyList<string> films, bool captioned)
    {
        var stopwatch = Stopwatch.StartNew();
        double held = 0;

        // Whether the button that skipped the last film is still down.
        bool spent = false;

        foreach (string name in films)
        {
            if (movies.Play(name) <= 0)
            {
                continue;
            }

            Log.Info(string.Create( CultureInfo.InvariantCulture, $"Intro: {name}, {movies.Seconds:F1}s"));

            bool skipped = Watch( window, renderer, movies, hint, stopwatch, ref held, ref spent, SayForFilm, captioned);

            if (window.IsClosing)
            {
                return;
            }

            if (skipped)
            {
                // Said, but not obeyed for the rest of them: the next film is a different thing to have decided about.
                Log.Info($"Intro: {name} skipped");
            }
        }
    }

    /// <summary>How long a press has to be held to skip a film.</summary>
    private const double HoldToSkipFilm = 0.6;

    /// <summary>How long the way out stays on screen at the start of a film.</summary>
    private const double SayForFilm = 6.0;

    /// <summary>Watches a film that has already been started, until it ends or the player stops it.</summary>
    /// <returns>True when the player stopped it early.</returns>
    /// <param name="window">The window, which is where the keyboard and the frames are.</param>
    /// <param name="renderer">What draws it.</param>
    /// <param name="movies">The player, with a film already playing.</param>
    /// <param name="hint">Where to say how to skip, or null to say nothing.</param>
    /// <param name="stopwatch">A clock that is already running.</param>
    /// <param name="held">How long the skip has been held for.</param>
    /// <param name="spent">Whether the press that skipped the last film is still down.</param>
    /// <param name="sayFor">How long the way out stays on screen at the start.</param>
    /// <param name="captioned">Whether to write out what is said in the film — rather than the row that governs the room's captions.</param>
    private static bool Watch( Platform.SilkGameWindow window, Rendering.IRenderer renderer, Game.MoviePlayer movies, MenuPage? hint,
        Stopwatch stopwatch, ref double held, ref bool spent, double sayFor, bool captioned)
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

            if (window.WasPressed(Platform.EditKey.Escape) || window.WasPressed(Platform.EditKey.Enter) || held >= HoldToSkipFilm)
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

            // The subtitle and the skip hint go through one call, because Overlay.Begin throws away what was there and two calls would show.
            bool saying = captioned && movies.Caption is { Length: > 0 };
            bool skipping = held > 0 || now - began < sayFor;

            if (hint is not null && (saying || skipping))
            {
                hint.Film( saying ? movies.Caption : null, saying ? movies.Speaker : null, skipping ? hint.Text.Say(
                            "film.skip", "Hold the mouse button or press Enter to skip") : null, (float)(held / HoldToSkipFilm),
                    window.FramebufferWidth, window.FramebufferHeight);

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
}

using System.Globalization;
using GK3Reborn.Audio;
using GK3Reborn.Game;
using GK3Reborn.Platform;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Upscaling;

namespace GK3Reborn.UI;

/// <summary>Which page of the front end is showing.</summary>
public enum FrontEndPage
{
    /// <summary>The first thing the game shows.</summary>
    Main,

    /// <summary>The three kinds of setting.</summary>
    Options,

    /// <summary>What the picture costs.</summary>
    Video,

    /// <summary>The window, the monitor, and how bright the display goes.</summary>
    Display,

    /// <summary>Drawing the room small and enlarging it.</summary>
    Upscaling,

    /// <summary>How loud everything is.</summary>
    Audio,

    /// <summary>How the game plays.</summary>
    Gameplay,

    /// <summary>What the game will do for the player rather than ask of them.</summary>
    Assists,

    /// <summary>The slots a game can be written to.</summary>
    Save,

    /// <summary>The slots a game can be read back from.</summary>
    Load,
}

/// <summary>What the front end wants the host to do.</summary>
public enum FrontEndOutcome
{
    /// <summary>Nothing; go on showing the menu.</summary>
    Stay,

    /// <summary>Start playing.</summary>
    Play,

    /// <summary>Go back to the room that is already loaded.</summary>
    Resume,

    /// <summary>Play the films the game opens with, then come back here.</summary>
    Intro,

    /// <summary>Leave the game.</summary>
    Quit,

    /// <summary>Write the game to the slot the player chose.</summary>
    Save,

    /// <summary>Read the game back from the slot the player chose.</summary>
    Load,

    /// <summary>
    /// Let go of whatever the story is holding and give the room back to the player.
    /// </summary>
    Unstick,
}

/// <summary>
/// The menu in front of the game: what each page holds and what choosing a row does.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of any window, renderer or device. It turns settings into rows and
/// rows back into settings, so the whole of the front end's behaviour can be tested without
/// drawing anything — which is the only way to check that a slider moves the thing it says
/// it moves.
/// </para>
/// <para>
/// It doubles as the pause menu. The same pages serve both; the only difference is that
/// there is a room to go back to, so the first row says Resume rather than New Game and
/// leaving means leaving the game rather than the menu.
/// </para>
/// </remarks>
public sealed class FrontEnd
{
    private static readonly SpeakerLayout[] Layouts =
        [SpeakerLayout.Headphones, SpeakerLayout.Stereo, SpeakerLayout.Stereo21, SpeakerLayout.Surround51];

    private static readonly PictureQuality[] Pictures =
        [PictureQuality.Original, PictureQuality.Improved, PictureQuality.High, PictureQuality.Highest];

    private static readonly WindowMode[] Windows =
        [WindowMode.Windowed, WindowMode.BorderlessFullscreen, WindowMode.ExclusiveFullscreen];

    /// <summary>Every upscaler there is, which is the default when nobody has narrowed it.</summary>
    private static readonly UpscalerKind[] EveryUpscaler =
        [UpscalerKind.Off, UpscalerKind.Spatial, UpscalerKind.Fsr, UpscalerKind.Dlss];

    private static readonly UpscalerQuality[] Ratios =
    [
        UpscalerQuality.Native,
        UpscalerQuality.UltraQuality,
        UpscalerQuality.Quality,
        UpscalerQuality.Balanced,
        UpscalerQuality.Performance,
        UpscalerQuality.UltraPerformance,
    ];

    private static readonly FrameGeneration[] Generations =
        [FrameGeneration.Off, FrameGeneration.Interpolated];

    private static readonly HdrTransfer[] Transfers =
        [HdrTransfer.Automatic, HdrTransfer.PerceptualQuantiser, HdrTransfer.ExtendedLinear];

    private static readonly ToneMapping[] Curves =
        [ToneMapping.Clip, ToneMapping.Reinhard, ToneMapping.Filmic];

    /// <summary>The ends of the text-size slider.</summary>
    /// <remarks>
    /// Named from the settings rather than written again, so the row cannot offer a size
    /// the file will clamp away the moment it is saved.
    /// </remarks>
    private const float SmallestText = GK3Reborn.Game.Settings.SmallestText;

    /// <summary>The other end.</summary>
    private const float LargestText = GK3Reborn.Game.Settings.LargestText;

    /// <summary>
    /// The sizes the display page offers, plus whatever the monitor's own is.
    /// </summary>
    /// <remarks>
    /// A short list of the ones people actually use rather than everything the driver will
    /// enumerate. A monitor reports dozens of modes, most of them refresh variants of four
    /// or five sizes, and a settings page that lists all of them is a page nobody can find
    /// their resolution on. Anything not here is reachable by leaving it on the monitor's
    /// own and resizing the window.
    /// </remarks>
    private static readonly (int Width, int Height)[] Sizes =
    [
        (0, 0),
        (1280, 720),
        (1600, 900),
        (1920, 1080),
        (2560, 1440),
        (3440, 1440),
        (3840, 2160),
    ];

    /// <summary>Creates a front end over some settings.</summary>
    /// <param name="settings">What the player has chosen so far.</param>
    /// <param name="inGame">Whether there is a room to go back to.</param>
    public FrontEnd(Settings settings, bool inGame = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Settings = settings;
        InGame = inGame;
    }

    /// <summary>The settings as they now stand.</summary>
    public Settings Settings { get; private set; }

    /// <summary>Whether a room is already loaded behind the menu.</summary>
    public bool InGame { get; set; }

    /// <summary>Which page is showing.</summary>
    public FrontEndPage Page { get; private set; } = FrontEndPage.Main;

    /// <summary>Whether anything has been changed since the settings were last written.</summary>
    public bool Dirty { get; private set; }

    /// <summary>
    /// Whether the game's own title art is on screen behind the menu.
    /// </summary>
    /// <remarks>
    /// It carries the game's name, so the first page draws no heading of its own over it.
    /// Set by whoever found the picture, because whether it is there is a fact about the
    /// installation rather than about the menu.
    /// </remarks>
    public bool Illustrated { get; set; }

    /// <summary>The heading for the page showing.</summary>
    public string Title => Page switch
    {
        FrontEndPage.Main => InGame
            ? "Paused"
            : Illustrated ? string.Empty : "Gabriel Knight 3",
        FrontEndPage.Options => "Settings",
        FrontEndPage.Video => "Picture",
        FrontEndPage.Display => "Display",
        FrontEndPage.Upscaling => "Upscaling",
        FrontEndPage.Audio => "Sound",
        FrontEndPage.Save => "Save Game",
        FrontEndPage.Load => "Restore Game",
        FrontEndPage.Assists => "Made Easier",
        _ => "Playing",
    };

    /// <summary>The rows of the page showing.</summary>
    public IReadOnlyList<MenuItem> Items => Page switch
    {
        FrontEndPage.Main => Main(),
        FrontEndPage.Options => Options(),
        FrontEndPage.Video => Video(),
        FrontEndPage.Display => Display(),
        FrontEndPage.Upscaling => Upscaling(),
        FrontEndPage.Audio => Audio(),
        FrontEndPage.Save => Slots(writing: true),
        FrontEndPage.Load => Slots(writing: false),
        FrontEndPage.Assists => Easier(),
        _ => Gameplay(),
    };

    /// <summary>Acts on what the player chose.</summary>
    /// <param name="action">The row and how it was moved.</param>
    /// <returns>What the host should do about it.</returns>
    public FrontEndOutcome Choose(MenuAction action)
    {
        if (!action.Happened)
        {
            return FrontEndOutcome.Stay;
        }

        switch (action.Id)
        {
            case "play":
                return FrontEndOutcome.Play;

            case "intro":
                return FrontEndOutcome.Intro;

            case "resume":
                return FrontEndOutcome.Resume;

            case "unstick":
                return FrontEndOutcome.Unstick;

            case "quit":
                return FrontEndOutcome.Quit;

            case "options":
                Page = FrontEndPage.Options;
                return FrontEndOutcome.Stay;

            case "video":
                Page = FrontEndPage.Video;
                return FrontEndOutcome.Stay;

            case "display":
                Page = FrontEndPage.Display;
                return FrontEndOutcome.Stay;

            case "upscaling":
                Page = FrontEndPage.Upscaling;
                return FrontEndOutcome.Stay;

            case "audio":
                Page = FrontEndPage.Audio;
                return FrontEndOutcome.Stay;

            case "gameplay":
                Page = FrontEndPage.Gameplay;
                return FrontEndOutcome.Stay;

            case "assists":
                Page = FrontEndPage.Assists;
                return FrontEndOutcome.Stay;

            case "save":
                Page = FrontEndPage.Save;
                return FrontEndOutcome.Stay;

            case "load":
                Page = FrontEndPage.Load;
                return FrontEndOutcome.Stay;

            case "back":
                Back();
                return FrontEndOutcome.Stay;

            default:
                // A slot. Which one travels back with the outcome, because the front end
                // knows what the player pointed at and the host is the only thing that can
                // read or write a game.
                if (action.Id.StartsWith("slot:", StringComparison.Ordinal))
                {
                    Slot = action.Id[5..];

                    return Page == FrontEndPage.Save
                        ? FrontEndOutcome.Save
                        : FrontEndOutcome.Load;
                }

                Change(action);
                return FrontEndOutcome.Stay;
        }
    }

    /// <summary>Opens a page outright.</summary>
    /// <param name="page">Which one.</param>
    /// <remarks>
    /// For photographing one: a settings page three keystrokes into the menu cannot be
    /// reached by a run with no keyboard, and a page nobody can render is a page whose
    /// layout nobody can check.
    /// </remarks>
    public void Show(FrontEndPage page) => Page = page;

    /// <summary>Goes up one level, or out of the menu from the top.</summary>
    /// <returns>True while there is still a menu showing.</returns>
    public bool Back()
    {
        if (Page == FrontEndPage.Main)
        {
            return false;
        }

        // Each page says where it came from. This used to read "anything that is not Options
        // is a child of Options", which was true while the only pages below the top were the
        // three kinds of setting — and sent Back from the save slots to the settings screen
        // the moment saving was added.
        Page = Page switch
        {
            FrontEndPage.Video or FrontEndPage.Display or FrontEndPage.Upscaling
                or FrontEndPage.Audio or FrontEndPage.Gameplay or FrontEndPage.Assists =>
                FrontEndPage.Options,

            _ => FrontEndPage.Main,
        };

        return true;
    }

    /// <summary>Writes the settings if anything has changed.</summary>
    /// <param name="path">Where to write, or null for this user's own.</param>
    /// <returns>True when something was written.</returns>
    /// <remarks>
    /// On leaving a page rather than on every keystroke: dragging a volume slider is
    /// hundreds of changes and none of them is worth a write to disk.
    /// </remarks>
    public bool Commit(string? path = null)
    {
        if (!Dirty)
        {
            return false;
        }

        Dirty = false;
        return Settings.Save(path ?? StoredAt);
    }

    /// <summary>
    /// Where these settings came from, and where they go back to.
    /// </summary>
    /// <remarks>
    /// Null for the player's own profile, which is the ordinary case. Set when the host was
    /// pointed at another file, so that a run taking a photograph of a display setting
    /// writes its changes to that file rather than to the one somebody is playing with.
    /// </remarks>
    public string? StoredAt { get; set; }

    /// <summary>Which slot the player last pointed at.</summary>
    /// <remarks>
    /// Read by the host after a <see cref="FrontEndOutcome.Save"/> or
    /// <see cref="FrontEndOutcome.Load"/>. The front end deliberately owns no store: it turns
    /// rows into a choice, and reading or writing a game is the host's business.
    /// </remarks>
    public string? Slot { get; private set; }

    /// <summary>What each slot holds, for the host to fill in before the page is shown.</summary>
    /// <remarks>
    /// A list rather than a store, for the same reason. Empty until something sets it, which
    /// draws every slot as free — the honest answer for a menu that has not been told.
    /// </remarks>
    public IReadOnlyList<SaveSlot> Saves { get; set; } = [];

    /// <summary>The interface's number for a slot's picture, by slot.</summary>
    /// <remarks>
    /// Set by the host, which is the only thing that can hand a picture to the renderer. Nought
    /// or absent draws the row as words alone, which is what a slot with no picture is — every
    /// save written before the pictures existed, among others.
    /// </remarks>
    public Func<string, int>? Illustrations { get; set; }

    /// <summary>What the player is calling the game they are about to save.</summary>
    /// <remarks>
    /// Typed on the save page and offered as the title. Empty means the slot keeps whatever
    /// it was called, or is named for where the player is if it was free.
    /// </remarks>
    public string Naming { get; set; } = string.Empty;

    private IReadOnlyList<MenuItem> Main() => InGame

        // Paused. No intro from here: the player is in the middle of the game, and the row
        // they want first is the one that gives it back to them.
        ? [
            MenuItem.Button("resume", "Resume"),
            MenuItem.Button("save", "Save"),
            MenuItem.Button("load", "Restore"),

            // A room can wedge: an approach walk far longer than anybody will sit through,
            // a script that parked and never came back, a clip on the player that never
            // ends. Every one of those reads the same way from the player's chair — the
            // camera stops answering and clicks stop reaching the floor — and the player
            // has no way to say so from inside the room, because saying so is a click.
            //
            // So it is a row here rather than a setting: it is a thing done once, to the
            // room the player is stuck in, and the menu is the only place they can still
            // reach. It costs nothing of the story; see SceneUpdate.Unstick.
            MenuItem.Button("unstick", "Get Unstuck"),

            MenuItem.Button("options", "Settings"),
            MenuItem.Button("quit", "Leave the Game"),
        ]

        // The original's own five, in its own order. Intro first because it is what the
        // game opens with and somebody who skipped it may want it back.
        : [
            MenuItem.Button("intro", "Intro"),
            MenuItem.Button("play", "Play"),

            MenuItem.Button("load", "Restore"),

            MenuItem.Button("options", "Settings"),
            MenuItem.Button("quit", "Quit"),
        ];

    /// <summary>
    /// The slots, as rows.
    /// </summary>
    /// <param name="writing">Whether this is the page that saves or the page that restores.</param>
    /// <returns>One row per slot, and a way back.</returns>
    /// <remarks>
    /// <para>
    /// Twelve numbered slots, plus the two the game keeps for itself. A free slot is drawn as
    /// free rather than hidden, because a save menu that shows only what has been saved gives
    /// a new player nothing to aim at.
    /// </para>
    /// <para>
    /// Each row carries what the player called it and when it was written. The quick and
    /// automatic slots can be restored from and not written to by hand: they belong to the
    /// game, and a player who overwrites their own autosave has been given a way to lose
    /// something they did not know they had.
    /// </para>
    /// </remarks>
    private List<MenuItem> Slots(bool writing)
    {
        List<MenuItem> rows = [];

        foreach (string slot in Reserved)
        {
            if (!writing)
            {
                rows.Add(MenuItem.Button(
                    "slot:" + slot, Described(slot), enabled: Written(slot) is not null) with
                {
                    Picture = Illustrations?.Invoke(slot) ?? 0,
                });
            }
        }

        for (int at = 1; at <= SaveStore.NumberedSlots; at++)
        {
            string slot = at.ToString("00", CultureInfo.InvariantCulture);

            rows.Add(MenuItem.Button(
                "slot:" + slot,
                Described(slot),
                enabled: writing || Written(slot) is not null) with
            {
                Picture = Illustrations?.Invoke(slot) ?? 0,
            });
        }

        // Everything else the store holds, which is how a save the player did not write
        // gets on the page at all. The rows above are a fixed fourteen — quick, auto and
        // twelve numbered — so a save filed under any other name was invisible however
        // readable it was: three games imported from the 1999 original sat in the saves
        // folder, were listed by the store, restored perfectly when asked for by name, and
        // could not be reached from the menu.
        //
        // Reading only. These are not slots to write into: the numbered twelve are what a
        // player saves to, and overwriting an import would throw away the thing it was
        // brought across for.
        if (!writing)
        {
            foreach (SaveSlot save in Saves)
            {
                if (Reserved.Contains(save.Slot, StringComparer.OrdinalIgnoreCase) ||
                    IsNumbered(save.Slot))
                {
                    continue;
                }

                rows.Add(MenuItem.Button("slot:" + save.Slot, Described(save.Slot)) with
                {
                    Picture = Illustrations?.Invoke(save.Slot) ?? 0,
                });
            }
        }

        rows.Add(MenuItem.Button("back", "Back"));

        return rows;
    }

    /// <summary>Whether a slot is one of the twelve the player saves into.</summary>
    private static bool IsNumbered(string slot) =>
        int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out int at) &&
        at >= 1 && at <= SaveStore.NumberedSlots;

    /// <summary>The two slots the game writes for itself.</summary>
    private static readonly string[] Reserved = [SaveStore.QuickSlot, SaveStore.AutoSlot];

    /// <summary>What a slot has in it, or null when it is free.</summary>
    private SaveSlot? Written(string slot) =>
        Saves.FirstOrDefault(s => string.Equals(s.Slot, slot, StringComparison.OrdinalIgnoreCase));

    /// <summary>How a slot reads on the page.</summary>
    /// <remarks>
    /// What the player called it and when they wrote it. The date is the local one, short,
    /// because a save menu is read at a glance and nobody is looking for a timezone.
    /// </remarks>
    private string Described(string slot)
    {
        string name = slot switch
        {
            SaveStore.QuickSlot => "Quick save",
            SaveStore.AutoSlot => "Autosave",
            _ when IsNumbered(slot) => "Slot " + slot.TrimStart('0'),

            // A game the 1999 original wrote, brought across under its own file name.
            // Saying so is worth a word: it is why the row is there and not numbered.
            _ when slot.StartsWith("gk3-", StringComparison.OrdinalIgnoreCase) =>
                "Original save",

            // Anything else somebody has put in the folder, under whatever they called it.
            _ => slot,
        };

        if (Written(slot) is not { } save)
        {
            return name + "  -  empty";
        }

        string called = save.Title is { Length: > 0 } titled ? titled : save.Summary;

        // Trimmed, because the panel is as wide as its widest row and a window is only so
        // wide. A save is recognised by its first few words and by when it was written.
        if (called.Length > 28)
        {
            called = called[..27].TrimEnd() + "\u2026";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name}  -  {called}  -  {save.Written.LocalDateTime:dd/MM HH:mm}");
    }

    /// <summary>
    /// Which of the vendors' runtimes are installed, for the rows that need to say.
    /// </summary>
    /// <remarks>
    /// Set by the host. Null draws every upscaler as unavailable, which is the honest
    /// answer for a front end nobody has told: it has no way to look for a file itself and
    /// no business doing so.
    /// </remarks>
    public UpscalerRuntimes? Runtimes { get; set; }

    /// <summary>How big the window is, so the upscaling page can say what it will draw.</summary>
    public (int Width, int Height) Window { get; set; } = (1920, 1080);

    /// <summary>
    /// Which upscalers this machine may be offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by the host from the card the renderer chose. DLSS is not on the list on a card
    /// that is not NVIDIA's, and the row does not step onto it: a permanently unavailable
    /// option is worse than an absent one, because it reads as something the game has
    /// failed to do rather than as something this hardware cannot.
    /// </para>
    /// <para>
    /// FSR stays on every list. FidelityFX is compute and runs on anything, which is
    /// exactly why an NVIDIA player who has not installed NVIDIA's runtime still has a good
    /// temporal upscaler available to them.
    /// </para>
    /// </remarks>
    public IReadOnlyList<UpscalerKind> Offered { get; set; } = EveryUpscaler;

    /// <summary>Whether the display actually gave back a high dynamic range colour space.</summary>
    /// <remarks>
    /// Distinct from the setting. Asking for HDR on a monitor in SDR mode changes nothing,
    /// and a page that shows the switch on and says nothing else has told the player their
    /// display is the problem in the least useful way available.
    /// </remarks>
    public bool HighDynamicRangeActive { get; set; }

    /// <summary>What is actually upscaling, in the renderer's own words.</summary>
    public string UpscalerRunning { get; set; } = string.Empty;

    /// <summary>Whether DLSS started and this card can run it.</summary>
    /// <remarks>
    /// Not the same question as whether the files are installed, and the page says so
    /// differently: a missing file is a download, and a card that cannot run it is not.
    /// </remarks>
    public bool DlssAvailable { get; set; }

    /// <summary>Whether DLSS can denoise the traced light as well as upscale it.</summary>
    public bool DlssRayReconstruction { get; set; }

    /// <summary>Why it cannot, when the files for it are installed.</summary>
    public string DlssRayReconstructionNote { get; set; } = string.Empty;

    /// <summary>Whether DLSS can generate frames.</summary>
    public bool DlssFrameGeneration { get; set; }

    private static IReadOnlyList<MenuItem> Options() =>
    [
        MenuItem.Button("video", "Picture"),
        MenuItem.Button("display", "Display"),
        MenuItem.Button("upscaling", "Upscaling"),
        MenuItem.Button("audio", "Sound"),
        MenuItem.Button("gameplay", "Playing"),
        MenuItem.Button("assists", "Made Easier"),
        MenuItem.Button("back", "Back"),
    ];

    private IReadOnlyList<MenuItem> Video() =>
    [
        MenuItem.Choice("picture", "Lighting", Describe(Settings.Picture)),
        MenuItem.Toggle("enhanced", "Higher-resolution textures", Settings.EnhancedTextures),
        MenuItem.Toggle("trees", "Modelled trees", Settings.ModelledTrees),
        MenuItem.Toggle("terrain", "Reconstructed horizon", Settings.TerrainBackdrop),
        MenuItem.Toggle("rooms", "Rounded room objects", Settings.ImprovedSceneGeometry),

        // The one thing on this page a player cannot see for themselves: the room standing
        // round them was built from whichever set was chosen when it loaded, and rebuilding
        // it here would mean reloading the scene underneath them.
        MenuItem.Label("The last four take effect at the next door."),
        MenuItem.Button("back", "Back"),
    ];

    /// <summary>
    /// The window, the monitor, and how bright the display is allowed to go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from Picture because it is about the <em>display</em> rather than about the
    /// room: nothing on this page changes what is drawn, only how large it is shown and how
    /// bright the brightest part of it is allowed to be.
    /// </para>
    /// <para>
    /// The four luminances only appear once HDR is on. They are meaningless without it —
    /// there is nowhere above white to put anything on an 8-bit sRGB display — and four
    /// dead rows on a page is how a settings screen teaches somebody that rows can be dead.
    /// </para>
    /// <para>
    /// <b>No row explains itself.</b> A settings page is read by somebody looking for one
    /// thing, and a paragraph under every row is what they have to scroll past to find it.
    /// What is left is what the player cannot see for themselves: whether the display took
    /// the colour space it was asked for.
    /// </para>
    /// </remarks>
    private List<MenuItem> Display()
    {
        List<MenuItem> rows =
        [
            MenuItem.Choice("window", "Window", Describe(Settings.Display)),

            // Dead rather than explained. A borderless window is the size of the monitor
            // by definition, so there is no size to choose; a row the player cannot land
            // on says that in no words at all, where the sentence it replaces cost three
            // lines of the page.
            MenuItem.Choice("size", "Resolution", DescribeSize()) with
            {
                Enabled = Settings.Display != WindowMode.BorderlessFullscreen,
            },

            MenuItem.Slider(
                "textsize",
                "Text size",
                Fraction(Settings.TextScale, SmallestText, LargestText),
                DescribeTextScale()),

            MenuItem.Toggle("vsync", "Wait for the display", Settings.VerticalSync),
            MenuItem.Toggle("hdr", "High dynamic range", Settings.HighDynamicRange),
        ];

        if (Settings.HighDynamicRange)
        {
            // Kept, because it is the one row here that is not a preference: the display
            // either gave back the colour space or it did not, and a switch shown on over a
            // monitor in SDR mode is the least useful true statement available.
            rows.Add(MenuItem.Label(HighDynamicRangeActive
                ? "The display took it."
                : "Asked for, and this display did not offer it."));

            rows.Add(MenuItem.Choice("transfer", "Encoding", Describe(Settings.HdrTransfer)));

            rows.Add(MenuItem.Slider(
                "paperwhite",
                "Paper white",
                Fraction(Settings.PaperWhiteNits, 80f, 400f),
                Nits(Settings.PaperWhiteNits)));

            rows.Add(MenuItem.Slider(
                "peak",
                "Brightest the display goes",
                Fraction(Settings.PeakNits, 400f, 4000f),
                Nits(Settings.PeakNits)));

            rows.Add(MenuItem.Slider(
                "sun",
                "Sunlight",
                Fraction(Settings.SunNits, 200f, 4000f),
                Nits(Settings.SunNits)));

            rows.Add(MenuItem.Slider(
                "lights",
                "Lamps and windows",
                Fraction(Settings.LightNits, 200f, 4000f),
                Nits(Settings.LightNits)));
        }
        else
        {
            rows.Add(MenuItem.Choice("tonemap", "Tone curve", Describe(Settings.ToneMapping)));
        }

        rows.Add(MenuItem.Button("back", "Back"));

        return rows;
    }

    /// <summary>
    /// Drawing the room smaller than the window and enlarging it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every row here changes while the game is running, including the upscaler itself: the
    /// renderer rebuilds its targets at the top of the next frame, the same way it does for
    /// a resize. Somebody comparing two upscalers should be able to do it by pressing left
    /// and right, not by restarting the game twice.
    /// </para>
    /// <para>
    /// The two vendors' rows are drawn whether or not their runtimes are installed, and say
    /// which file is missing. Hiding a row the player has read about elsewhere teaches them
    /// that the game does not support it.
    /// </para>
    /// <para>
    /// Nothing here says what a row <em>does</em>. What is left is what the player cannot
    /// find out by trying it: which file to go and fetch, why a row is dead, the two
    /// resolutions the picture is drawn between, and what is actually running.
    /// </para>
    /// </remarks>
    private List<MenuItem> Upscaling()
    {
        RuntimeFiles files =
            Runtimes?.For(Settings.Upscaler) ?? UpscalerRuntimes.Unknown(Settings.Upscaler);

        List<MenuItem> rows =
        [
            MenuItem.Choice("upscaler", "Upscaler", Describe(Settings.Upscaler)),
        ];

        if (Settings.Upscaler is UpscalerKind.Fsr or UpscalerKind.Dlss && !files.Present)
        {
            rows.Add(MenuItem.Label(
                $"Not installed: copy {List(files.Missing)} into the game's libs folder."));
        }
        else if (Settings.Upscaler == UpscalerKind.Dlss && !DlssAvailable)
        {
            // Installed and refused, which is a different sentence: there is nothing to
            // download and nothing the player did wrong.
            rows.Add(MenuItem.Label("Installed, and this card cannot run it: DLSS needs a GeForce RTX."));
        }

        if (Settings.Upscaler != UpscalerKind.Off)
        {
            rows.Add(MenuItem.Choice(
                "ratio", "Quality", Describe(Settings.UpscalerQuality)));

            rows.Add(MenuItem.Label(Settings.Upscaling.Describe(Window.Width, Window.Height)));

            rows.Add(MenuItem.Toggle("sharpen", "Sharpen", Settings.Sharpening));

            if (Settings.Sharpening)
            {
                rows.Add(MenuItem.Slider(
                    "sharpness",
                    "How much",
                    Settings.Sharpness,
                    MenuPage.Percent(Settings.Sharpness)));
            }
        }

        if (Settings.Upscaler == UpscalerKind.Dlss)
        {
            rows.Add(MenuItem.Choice(
                "preset", "Model", DlssPresets.Describe(Settings.DlssPreset)));

            rows.Add(MenuItem.Toggle(
                "reconstruction", "Ray reconstruction", Settings.RayReconstruction) with
            {
                Enabled = DlssRayReconstruction,
            });

            // Only when it cannot be had. Why a row is dead is worth a line; what a row
            // does when it works is what the row itself says.
            if (!DlssRayReconstruction)
            {
                rows.Add(MenuItem.Label(
                    DlssRayReconstructionNote is { Length: > 0 } why
                        ? "Not available: " + why + "."
                        : "Needs sl.dlss_d.dll and nvngx_dlssnr.dll in the libs folder."));
            }
        }

        bool generation = Settings.Upscaler switch
        {
            UpscalerKind.Fsr => Runtimes?.Fsr.Present ?? false,
            UpscalerKind.Dlss => DlssFrameGeneration,
            _ => false,
        };

        rows.Add(MenuItem.Choice(
            "generation", "Frame generation", Describe(Settings.FrameGeneration)) with
        {
            Enabled = generation,
        });

        if (!generation)
        {
            rows.Add(MenuItem.Label(
                "Needs FSR or DLSS, and their frame-generation runtime, in the libs folder."));
        }

        if (UpscalerRunning is { Length: > 0 })
        {
            rows.Add(MenuItem.Label("Running: " + UpscalerRunning));
        }

        rows.Add(MenuItem.Button("back", "Back"));

        return rows;
    }

    private IReadOnlyList<MenuItem> Audio() =>
    [
        MenuItem.Slider("master", "Overall", Settings.MasterVolume, MenuPage.Percent(Settings.MasterVolume)),
        MenuItem.Slider("music", "Music and cutscenes", Settings.MusicVolume, MenuPage.Percent(Settings.MusicVolume)),
        MenuItem.Slider("ambience", "Room tone", Settings.AmbienceVolume, MenuPage.Percent(Settings.AmbienceVolume)),
        MenuItem.Slider("effects", "Effects", Settings.EffectsVolume, MenuPage.Percent(Settings.EffectsVolume)),
        MenuItem.Slider("dialogue", "Speech", Settings.DialogueVolume, MenuPage.Percent(Settings.DialogueVolume)),
        MenuItem.Choice("speakers", "Speakers", Describe(Settings.Speakers)),

        // Said rather than quietly not done. The device is opened once at startup, and a
        // player who changes this and hears no difference would reasonably conclude the
        // setting is broken. Every other row on this page is heard while it is dragged.
        MenuItem.Label("Speakers take effect at the next start."),
        MenuItem.Button("back", "Back"),
    ];

    private IReadOnlyList<MenuItem> Gameplay() =>
    [
        MenuItem.Slider(
            "hurry",
            "Hurrying pace",
            (Settings.HurryFactor - 1f) / 3f,
            string.Create(CultureInfo.InvariantCulture, $"{Settings.HurryFactor:F1}x")),

        MenuItem.Toggle("glide", "Camera travels between angles", Settings.CameraGlide),
        MenuItem.Toggle("cinematics", "Let the story move the camera", Settings.Cinematics),

        // Named for what it does rather than for what it is for. "Free camera" is a word
        // somebody already looking for it will find, and "leave the room" is the half that
        // tells everybody else what turning it on will look like.
        MenuItem.Toggle("freecamera", "Free camera, which may leave the room", Settings.FreeCamera),

        MenuItem.Toggle("captions", "Write out what is said", Settings.Captions),
        MenuItem.Toggle("intro", "Play the intro on starting", Settings.PlayIntro),
        MenuItem.Toggle("eggs", "Easter eggs", Settings.EasterEggs),
        MenuItem.Button("back", "Back"),
    ];

    /// <summary>
    /// The two things the game will do for the player rather than ask of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Their own page, and away from Playing, because these are not preferences about how
    /// the game is presented: each one changes what the story asks of the player, and a
    /// switch that quietly does that does not belong in the same list as the captions.
    /// </para>
    /// <para>
    /// Both off by default, and both name the puzzle they take away <em>in the row itself</em>
    /// rather than in a sentence under it. "Skip a puzzle" is no help to somebody who has
    /// not met it yet and no reassurance to somebody who has; "skip the cat-hair moustache"
    /// is both, and costs no second line.
    /// </para>
    /// </remarks>
    private IReadOnlyList<MenuItem> Easier() =>
    [
        MenuItem.Toggle("moustache", "Skip the cat-hair moustache", Settings.AlwaysWearsMoustache),
        MenuItem.Toggle("armour", "Gabriel cannot be killed", Settings.PlotArmour),
        MenuItem.Button("back", "Back"),
    ];

    private void Change(MenuAction action)
    {
        Settings before = Settings;

        Settings = action.Id switch
        {
            "master" => Settings with { MasterVolume = Level(Settings.MasterVolume, action) },
            "music" => Settings with { MusicVolume = Level(Settings.MusicVolume, action) },
            "ambience" => Settings with { AmbienceVolume = Level(Settings.AmbienceVolume, action) },
            "effects" => Settings with { EffectsVolume = Level(Settings.EffectsVolume, action) },
            "dialogue" => Settings with { DialogueVolume = Level(Settings.DialogueVolume, action) },

            "hurry" => Settings with
            {
                // One to four, which is a pace the game was authored at up to a sprint.
                HurryFactor = 1f + (3f * Level((Settings.HurryFactor - 1f) / 3f, action)),
            },

            "speakers" => Settings with { Speakers = Step(Layouts, Settings.Speakers, action.Step) },
            "picture" => Settings with { Picture = Step(Pictures, Settings.Picture, action.Step) },

            "window" => Settings with { Display = Step(Windows, Settings.Display, action.Step) },
            "size" => Size(action.Step),
            "vsync" => Settings with { VerticalSync = !Settings.VerticalSync },
            "textsize" => Settings with { TextScale = TextSize(Settings.TextScale, action) },

            "upscaler" => Settings with
            {
                Upscaler = Step(
                    Offered.Count > 0 ? [.. Offered] : EveryUpscaler,
                    Settings.Upscaler,
                    action.Step),
            },

            "ratio" => Settings with
            {
                UpscalerQuality = Step(Ratios, Settings.UpscalerQuality, action.Step),
            },

            "sharpen" => Settings with { Sharpening = !Settings.Sharpening },
            "sharpness" => Settings with { Sharpness = Level(Settings.Sharpness, action) },

            "generation" => Settings with
            {
                FrameGeneration = Step(Generations, Settings.FrameGeneration, action.Step),
            },

            "reconstruction" => Settings with { RayReconstruction = !Settings.RayReconstruction },

            // Round the letters rather than stopping at the ends, the same way every other
            // choice on these pages does, and past the ones with names as well: a preset a
            // future runtime adds is reachable without this file changing.
            "preset" => Settings with
            {
                DlssPreset = Wrapped(
                    Settings.DlssPreset + (action.Step == 0 ? 1 : action.Step),
                    DlssPresets.Highest + 1),
            },

            "hdr" => Settings with { HighDynamicRange = !Settings.HighDynamicRange },

            "transfer" => Settings with
            {
                HdrTransfer = Step(Transfers, Settings.HdrTransfer, action.Step),
            },

            "tonemap" => Settings with
            {
                ToneMapping = Step(Curves, Settings.ToneMapping, action.Step),
            },

            "paperwhite" => Settings with
            {
                PaperWhiteNits = Nits(Settings.PaperWhiteNits, 80f, 400f, action),
            },

            "peak" => Settings with { PeakNits = Nits(Settings.PeakNits, 400f, 4000f, action) },
            "sun" => Settings with { SunNits = Nits(Settings.SunNits, 200f, 4000f, action) },
            "lights" => Settings with { LightNits = Nits(Settings.LightNits, 200f, 4000f, action) },

            "enhanced" => Settings with { EnhancedTextures = !Settings.EnhancedTextures },
            "trees" => Settings with { ModelledTrees = !Settings.ModelledTrees },
            "terrain" => Settings with { TerrainBackdrop = !Settings.TerrainBackdrop },
            "rooms" => Settings with { ImprovedSceneGeometry = !Settings.ImprovedSceneGeometry },
            "glide" => Settings with { CameraGlide = !Settings.CameraGlide },
            "cinematics" => Settings with { Cinematics = !Settings.Cinematics },
            "freecamera" => Settings with { FreeCamera = !Settings.FreeCamera },
            "captions" => Settings with { Captions = !Settings.Captions },
            "intro" => Settings with { PlayIntro = !Settings.PlayIntro },
            "eggs" => Settings with { EasterEggs = !Settings.EasterEggs },

            "moustache" => Settings with
            {
                AlwaysWearsMoustache = !Settings.AlwaysWearsMoustache,
            },

            "armour" => Settings with { PlotArmour = !Settings.PlotArmour },

            _ => Settings,
        };

        if (Settings != before)
        {
            Dirty = true;
        }
    }

    /// <summary>The next resolution in the list, keeping the two dimensions together.</summary>
    /// <remarks>
    /// A width and a height are one decision and are stepped as one. The list holds the
    /// monitor's own size as a pair of noughts, which is the first entry, so a player who
    /// has never touched this row is already on it.
    /// </remarks>
    private Settings Size(int by)
    {
        int at = Array.FindIndex(
            Sizes,
            s => s.Width == Settings.DisplayWidth && s.Height == Settings.DisplayHeight);

        int next = Wrapped((at < 0 ? 0 : at) + (by == 0 ? 1 : by), Sizes.Length);

        return Settings with
        {
            DisplayWidth = Sizes[next].Width,
            DisplayHeight = Sizes[next].Height,
        };
    }

    /// <summary>Where the text-size slider ends up.</summary>
    /// <remarks>
    /// Rounded to a twentieth, so the row reads in fives and a player can get back to a
    /// hundred per cent by dragging. A slider that stopped at 97% would leave somebody
    /// unable to undo what they had just done to their menu.
    /// </remarks>
    private static float TextSize(float current, MenuAction action)
    {
        float part = Level(Fraction(current, SmallestText, LargestText), action);

        return MathF.Round(
            (SmallestText + ((LargestText - SmallestText) * part)) * 20f) / 20f;
    }

    /// <summary>How the text-size row reads.</summary>
    private string DescribeTextScale() => string.Create(
        CultureInfo.InvariantCulture, $"{Settings.TextScale * 100f:F0}%");

    /// <summary>How this page reads the resolution row.</summary>
    /// <remarks>
    /// A borderless window is the size of the monitor by definition, whatever size the file
    /// remembers, so the row reads that way and is not selectable. The stored pair is kept
    /// rather than cleared: it is what the player goes back to on choosing windowed again.
    /// </remarks>
    private string DescribeSize() =>
        Settings.Display == WindowMode.BorderlessFullscreen ||
        Settings.DisplayWidth <= 0 || Settings.DisplayHeight <= 0
            ? "The monitor's own"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Settings.DisplayWidth}x{Settings.DisplayHeight}");

    /// <summary>Where a luminance slider sits between its two ends.</summary>
    private static float Fraction(float value, float low, float high) =>
        Math.Clamp((value - low) / MathF.Max(high - low, 1f), 0f, 1f);

    /// <summary>Where a luminance slider ends up.</summary>
    /// <remarks>
    /// Rounded to ten candelas. A slider that reads 843 nits is a slider pretending to a
    /// precision nobody's eye or monitor has, and it makes two settings that look different
    /// and are not.
    /// </remarks>
    private static float Nits(float current, float low, float high, MenuAction action)
    {
        float part = Level(Fraction(current, low, high), action);

        return MathF.Round((low + ((high - low) * part)) / 10f) * 10f;
    }

    /// <summary>How a luminance reads.</summary>
    private static string Nits(float value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:F0} nits");

    /// <summary>The next index round a list of a given length, either way.</summary>
    private static int Wrapped(int at, int length) => ((at % length) + length) % length;

    /// <summary>Several file names, as a sentence rather than as a list.</summary>
    /// <remarks>
    /// Commas and a final "and". "a and b and c" is what joining on one separator gives and
    /// it reads like a machine wrote it, which on a page asking somebody to go and download
    /// three files is exactly the wrong impression.
    /// </remarks>
    private static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "nothing",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>Where a slider ends up: dragged outright, or stepped a twentieth.</summary>
    private static float Level(float current, MenuAction action) =>
        Math.Clamp(
            action.Dragged ? action.Fraction : current + (action.Step * 0.05f),
            0f,
            1f);

    /// <summary>The next one round the list, either way.</summary>
    private static T Step<T>(T[] all, T current, int by)
        where T : struct, Enum
    {
        int at = Array.IndexOf(all, current);
        int next = ((at < 0 ? 0 : at) + (by == 0 ? 1 : by)) % all.Length;

        return all[next < 0 ? next + all.Length : next];
    }

    private static string Describe(PictureQuality quality) => quality switch
    {
        PictureQuality.Original => "As it was",
        PictureQuality.Improved => "Shadows",
        PictureQuality.High => "Shadows and shading",
        _ => "Everything",
    };

    private static string Describe(WindowMode mode) => mode switch
    {
        WindowMode.BorderlessFullscreen => "Borderless, filling the monitor",
        WindowMode.ExclusiveFullscreen => "Fullscreen",
        _ => "A window",
    };

    private static string Describe(UpscalerKind kind) => kind switch
    {
        UpscalerKind.Spatial => "Built in",
        UpscalerKind.Fsr => "FSR (AMD)",
        UpscalerKind.Dlss => "DLSS (NVIDIA)",
        _ => "Off",
    };

    private static string Describe(UpscalerQuality quality) => quality switch
    {
        UpscalerQuality.Native => "Native (anti-aliasing only)",
        UpscalerQuality.UltraQuality => "Ultra quality",
        UpscalerQuality.Quality => "Quality",
        UpscalerQuality.Balanced => "Balanced",
        UpscalerQuality.Performance => "Performance",
        _ => "Ultra performance",
    };

    private static string Describe(FrameGeneration generation) =>
        generation == FrameGeneration.Interpolated ? "On" : "Off";

    private static string Describe(HdrTransfer transfer) => transfer switch
    {
        HdrTransfer.PerceptualQuantiser => "HDR10",
        HdrTransfer.ExtendedLinear => "scRGB",
        _ => "Whichever the display prefers",
    };

    private static string Describe(ToneMapping curve) => curve switch
    {
        ToneMapping.Reinhard => "Rolled off",
        ToneMapping.Filmic => "Filmic",
        _ => "Clipped, as it was",
    };

    private static string Describe(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Headphones => "Headphones",
        SpeakerLayout.Stereo21 => "Stereo and a subwoofer",
        SpeakerLayout.Surround51 => "Surround, 5.1",
        _ => "Stereo",
    };
}

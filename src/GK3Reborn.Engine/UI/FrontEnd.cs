using System.Globalization;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Audio;
using GK3Reborn.Game;
using GK3Reborn.Platform;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Upscaling;
using GK3Reborn.Content;

namespace GK3Reborn.UI;

/// <summary>
/// Which page of the front end is showing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The five settings pages are sections of one screen and not pages in their own
/// right.</b> They used to be reached by choosing a row on a Settings page and walking back
/// out of it again, which is seven keystrokes to compare a row on the Picture page against
/// a row on the Display one; now they are a list down the side of a single screen and the
/// comparison is one keystroke. They stay separate members here because what is showing is
/// still one of five things and the front end still has to say which.
/// </para>
/// <para>
/// Upscaling was a page and is now a group of rows on Picture, and Made Easier was a page
/// and is now a group of rows on Playing. Both were pages because a single column had no
/// other way to group anything; a two-column page with headings does, so a page apiece for
/// six rows and two rows was a page apiece too many.
/// </para>
/// </remarks>
public enum FrontEndPage
{
    /// <summary>The first thing the game shows.</summary>
    Main,

    /// <summary>What the picture costs, and how it is drawn and enlarged.</summary>
    Video,

    /// <summary>The window, the monitor, and how bright the display goes.</summary>
    Display,

    /// <summary>How loud everything is.</summary>
    Audio,

    /// <summary>How the game plays, and what it will do for the player.</summary>
    Gameplay,

    /// <summary>Which key and which pad button do which job.</summary>
    Controls,

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

    /// <summary>
    /// The frame-generation settings this machine can actually reach.
    /// </summary>
    /// <remarks>
    /// Trimmed to what the runtime says the card will do, rather than offered in full and
    /// refused. Asking for more generated frames than a card supports is not clamped — the
    /// runtime declines the whole call and generation goes off — so a menu that offers
    /// four-times on a card that does two is a menu with a setting in it that quietly means
    /// "off".
    ///
    /// Nought is not a card that will generate none: it is a front end nothing has told yet,
    /// which is what a test looks like and what the first frame of a run looks like. There
    /// the whole list stands, and the row is disabled by the runtime check instead.
    /// </remarks>
    private FrameGeneration[] Generations =>
        FrameGenerationMaximum <= 0
            ? [.. FrameGenerations.All]
            : [.. FrameGenerations.All.Where(g => g.Generated() <= FrameGenerationMaximum)];

    private static readonly RenderBackend[] Backends =
        [RenderBackend.Automatic, RenderBackend.Vulkan, RenderBackend.Direct3D12];

    private static readonly LatencyMode[] Latencies =
        [LatencyMode.Off, LatencyMode.On, LatencyMode.Boost];

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

    /// <summary>
    /// The settings screen's sections, in the order they are listed down its side.
    /// </summary>
    /// <remarks>
    /// Picture first because it is what most people came for, Controls last because it is
    /// the one people set once. Sound sits in the middle rather than at the end, where the
    /// original put it, on the grounds that a volume is the setting people come back to.
    /// </remarks>
    public static IReadOnlyList<MenuSection> Sections { get; } =
    [
        new("video", "Picture"),
        new("display", "Display"),
        new("audio", "Sound"),
        new("gameplay", "Playing"),
        new("controls", "Controls"),
    ];

    /// <summary>Which page each of those sections is.</summary>
    private static readonly FrontEndPage[] SectionPages =
    [
        FrontEndPage.Video,
        FrontEndPage.Display,
        FrontEndPage.Audio,
        FrontEndPage.Gameplay,
        FrontEndPage.Controls,
    ];

    /// <summary>Whether what is showing is one of the settings sections.</summary>
    public bool OnSettings => Array.IndexOf(SectionPages, Page) >= 0;

    /// <summary>Which section is showing, or -1 when none is.</summary>
    public int Section => Array.IndexOf(SectionPages, Page);

    /// <summary>Shows the section before or after this one, wrapping round.</summary>
    /// <param name="by">-1 for the one above, 1 for the one below.</param>
    /// <returns>True when the section changed.</returns>
    /// <remarks>
    /// Round rather than stopping at the ends, the same way every list in this interface
    /// does. Nothing at all when a settings section is not what is showing: the shoulder
    /// buttons on the save screen belong to the save screen.
    /// </remarks>
    public bool StepSection(int by)
    {
        int at = Section;

        if (at < 0 || by == 0)
        {
            return false;
        }

        int next = ((at + by) % SectionPages.Length + SectionPages.Length) %
                   SectionPages.Length;

        if (next == at)
        {
            return false;
        }

        Page = SectionPages[next];

        return true;
    }

    /// <summary>The heading for the page showing.</summary>
    /// <remarks>
    /// One word for the whole settings screen, because the section's own name is already
    /// down the side of it in the list the player just chose it from. A panel headed
    /// "Picture" with "Picture" highlighted beside it says the same thing twice.
    /// </remarks>
    public string Title => Page switch
    {
        FrontEndPage.Main => InGame
            ? "Paused"
            : Illustrated ? string.Empty : "Gabriel Knight 3",
        FrontEndPage.Save => "Save Game",
        FrontEndPage.Load => "Restore Game",
        _ => "Settings",
    };

    /// <summary>The rows of the page showing.</summary>
    public IReadOnlyList<MenuItem> Items => Page switch
    {
        FrontEndPage.Main => Main(),
        FrontEndPage.Video => Video(),
        FrontEndPage.Display => Display(),
        FrontEndPage.Audio => Audio(),
        FrontEndPage.Controls => Controls(),
        FrontEndPage.Save => Slots(writing: true),
        FrontEndPage.Load => Slots(writing: false),
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

            // The settings screen, opened at whichever section was last looked at. Coming
            // back to the row somebody left is worth more than being consistent about which
            // section is the first one: a player who has just turned the music down and
            // wants it down a little further should not have to find Sound again.
            case "options":
                Page = _lastSection;
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
                // A section down the side of the settings screen, chosen with the pointer.
                // The page reports it rather than deciding it, because which sections there
                // are is a fact about the settings and not about how they are drawn.
                if (action.Id.StartsWith("tab:", StringComparison.Ordinal))
                {
                    int which = IndexOfSection(action.Id[4..]);

                    if (which >= 0)
                    {
                        Page = SectionPages[which];
                    }

                    return FrontEndOutcome.Stay;
                }

                // A row on the Controls page, which is not a setting to be stepped but a
                // question to be answered by pressing something. See Listening.
                if (action.Id.StartsWith("key:", StringComparison.Ordinal) ||
                    action.Id.StartsWith("pad:", StringComparison.Ordinal) ||
                    action.Id.StartsWith("ptr:", StringComparison.Ordinal))
                {
                    Listen(action.Id);

                    return FrontEndOutcome.Stay;
                }

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
        // Listening for a key is a state to get out of, and Escape is what everybody will
        // press to do it. Answered before anything else, so that abandoning a rebind does
        // not also leave the settings screen.
        if (Listening)
        {
            Cancel();

            return true;
        }

        if (Page == FrontEndPage.Main)
        {
            return false;
        }

        // Out of whatever is showing and back to the top. There is no longer a level in
        // between: the settings are one screen with five sections rather than a page of
        // five buttons leading to five pages, so Back from a section is Back from the
        // settings.
        //
        // Which section it was is remembered, so that opening the settings again opens them
        // where they were left.
        if (Section >= 0)
        {
            _lastSection = Page;
        }

        Page = FrontEndPage.Main;

        return true;
    }

    /// <summary>Which section of the settings was last looked at.</summary>
    private FrontEndPage _lastSection = FrontEndPage.Video;

    /// <summary>Which section has a given name, or -1.</summary>
    private static int IndexOfSection(string id)
    {
        for (int i = 0; i < Sections.Count; i++)
        {
            if (string.Equals(Sections[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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

    /// <summary>
    /// How many frames the runtime will generate for each drawn one, or nought for none.
    /// </summary>
    /// <remarks>
    /// What the frame-generation row is trimmed to. Offering a factor the card will not do
    /// is worse than not offering it: the runtime refuses the whole call rather than
    /// clamping, so stepping to it turns generation off altogether and says nothing.
    /// </remarks>
    public int FrameGenerationMaximum { get; set; }

    /// <summary>Whether Reflex loaded and can be driven.</summary>
    public bool LatencyControl { get; set; }

    /// <summary>Whether a gamepad is plugged in.</summary>
    /// <remarks>
    /// Set by the host every frame, the same way the upscaler's runtime facts are, and for
    /// the same reason: it can change while the settings screen is open, because that is
    /// what a USB socket is. The Controls page says so rather than hiding its pad rows —
    /// a player setting up a pad they are about to plug in should be able to.
    /// </remarks>
    public bool HasGamepad { get; set; }

    /// <summary>Which graphics API is drawing, as against the one that is chosen.</summary>
    /// <remarks>
    /// The two differ from the moment somebody steps the row until the next time the game
    /// starts, and the row says so. Nought — <see cref="RenderBackend.Automatic"/> — is a
    /// front end nothing has told, which is what a test looks like; there the row says what
    /// was chosen and claims nothing about what is running.
    /// </remarks>
    public RenderBackend RunningBackend { get; set; }

    /// <summary>
    /// Everything about what is drawn: how it is lit, what it is built from, and how it is
    /// scaled up to the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upscaling used to be a page of its own, one level further in. It is here because it
    /// is a picture setting and because a page of its own cost two keystrokes each way to
    /// reach six rows — and because comparing an upscaler against the lighting quality it
    /// is being asked to reconstruct is a comparison somebody makes constantly and could
    /// not make on one screen.
    /// </para>
    /// <para>
    /// The headings are doing real work here rather than decorating. A page laid out in two
    /// columns has no single line for the eye to follow, so a reader has no way to tell
    /// where the lighting rows stop and the geometry rows begin without being told.
    /// </para>
    /// </remarks>
    private List<MenuItem> Video()
    {
        List<MenuItem> rows =
        [
            MenuItem.Heading("Lighting"),
            MenuItem.Choice("picture", "Lighting", Describe(Settings.Picture)),

            // Dead with no rays, because there is nothing for it to take away then: the
            // bake is the room's lighting at that tier and the rig only reaches the people
            // standing in it. A row that silently did nothing would be worse.
            MenuItem.Toggle(
                "realistic", "Only real light sources", Settings.RealisticLighting) with
            {
                Enabled = Settings.Quality != RayTracingQuality.None,
            },
        ];

        if (Settings.RealisticLighting && Settings.Quality != RayTracingQuality.None)
        {
            // What the player cannot find out by trying it in one room: the rooms this
            // changes most are the ones the artists were propping up hardest, and a room
            // going dark is the setting working rather than failing.
            rows.Add(MenuItem.Label(
                "The artists' fills, ambients and bounces are switched off. Rooms lit " +
                "mostly by them get darker."));
        }

        rows.AddRange(
        [
            MenuItem.Heading("Reflections"),

            MenuItem.Toggle(
                "floorreflect", "Floors reflect the room", Settings.FloorReflections),

            // A multiplier rather than a percentage, because one is the physical answer and
            // the row is about departing from it. "50%" on a slider whose default is the
            // middle reads as half of something; "1.0x" reads as what it is.
            MenuItem.Slider(
                "reflectivity",
                "How strongly",
                Settings.Reflectivity / GK3Reborn.Game.Settings.MostReflective,
                string.Create(
                    CultureInfo.InvariantCulture, $"{Settings.Reflectivity:F1}x")),

            MenuItem.Heading("Detail"),
            MenuItem.Toggle("enhanced", "Higher-resolution textures", Settings.EnhancedTextures),
            MenuItem.Toggle("trees", "Modelled trees", Settings.ModelledTrees),
            MenuItem.Toggle("terrain", "Reconstructed horizon", Settings.TerrainBackdrop),
            MenuItem.Toggle("rooms", "Rounded room objects", Settings.ImprovedSceneGeometry),
            MenuItem.Toggle("rails", "Solid railings and fences", Settings.ThickCutoutCards),

            // The one thing in this group a player cannot see for themselves: the room
            // standing round them was built from whichever set was chosen when it loaded,
            // and rebuilding it here would mean reloading the scene underneath them.
            MenuItem.Label("These five take effect at the next door."),
        ]);

        rows.AddRange(Upscaling());

        return rows;
    }

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
        List<MenuItem> rows = [];

        // Windows only, because it is the only machine where there is a choice: every other
        // one has Vulkan and nothing else, and a row with one value on it is a row that
        // teaches somebody the game has settings that do nothing.
        if (RenderBackends.IsPossible(RenderBackend.Direct3D12))
        {
            rows.Add(MenuItem.Choice("backend", "Graphics API", DescribeBackend()));
        }

        rows.AddRange(
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
        ]);

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
            MenuItem.Heading("Upscaling"),
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
            bool pinned = Settings.Upscaler == UpscalerKind.Dlss && Settings.NeuralUplift;

            rows.Add(MenuItem.Choice(
                "ratio", "Quality", Describe(Settings.UpscalerQuality)) with
            {
                Enabled = !pinned,
            });

            // What the player cannot find out by trying it: the row is dead because the
            // network will not scale, not because the setting stopped working.
            if (pinned)
            {
                rows.Add(MenuItem.Label(
                    "Neural uplift draws at the window's own size; it reworks the picture " +
                    "rather than enlarging it."));
            }

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
                        : "Needs sl.dlss_d.dll and nvngx_dlssd.dll in the libs folder."));
            }

            rows.AddRange(Neural());
        }

        bool generation = Settings.Upscaler switch
        {
            UpscalerKind.Fsr => Runtimes?.Fsr.Present ?? false,
            UpscalerKind.Dlss => DlssFrameGeneration,
            _ => false,
        };

        rows.Add(MenuItem.Choice(
            "generation", "Frame generation", Settings.FrameGeneration.Describe()) with
        {
            Enabled = generation,
        });

        if (!generation)
        {
            rows.Add(MenuItem.Label(
                "Needs FSR or DLSS, and their frame-generation runtime, in the libs folder."));
        }
        // No line of its own, and neither does the row above. What a card will generate
        // limits the row rather than being written under it: a factor that is not offered
        // needs no sentence explaining that it is not offered, and Reflex comes out of the
        // same bundle the line above already names.
        rows.Add(MenuItem.Choice("latency", "Low latency", Describe(Settings.Latency)) with
        {
            Enabled = LatencyControl,
        });

        if (UpscalerRunning is { Length: > 0 })
        {
            rows.Add(MenuItem.Label("Running: " + UpscalerRunning));
        }

        return rows;
    }

    /// <summary>The rows for the neural rendering network.</summary>
    /// <remarks>
    /// <para>
    /// Under DLSS because it stands in the same place — it scales the frame — but it needs
    /// only <c>nvngx_dlssnr.dll</c>, not Streamline and not the plugin that would ordinarily
    /// drive it. So the row is offered as soon as that one file is there, whatever the rest
    /// of the DLSS rows say about themselves.
    /// </para>
    /// <para>
    /// The strengths only appear once it is on. A page of sliders that do nothing is worse
    /// than a page that grows when there is something to set.
    /// </para>
    /// </remarks>
    private List<MenuItem> Neural()
    {
        bool installed = Runtimes?.NeuralRendering.Present ?? false;

        List<MenuItem> rows =
        [
            MenuItem.Toggle("neural", "Neural uplift", Settings.NeuralUplift) with
            {
                Enabled = installed,
            },
        ];

        if (!installed)
        {
            rows.Add(MenuItem.Label(
                "Needs nvngx_dlssnr.dll in the game's libs folder."));

            return rows;
        }

        if (!Settings.NeuralUplift)
        {
            return rows;
        }

        rows.Add(MenuItem.Slider(
            "nrstrength",
            "Strength",
            Settings.NeuralIntensity,
            MenuPage.Percent(Settings.NeuralIntensity)));

        rows.Add(MenuItem.Slider(
            "nrtone",
            "Local contrast",
            Settings.NeuralLocalTone,
            MenuPage.Percent(Settings.NeuralLocalTone)));

        rows.Add(MenuItem.Slider(
            "nrglobal",
            "Overall tone",
            Settings.NeuralGlobalTone,
            MenuPage.Percent(Settings.NeuralGlobalTone)));

        rows.Add(MenuItem.Slider(
            "nrstructure",
            "Fine detail",
            Settings.NeuralLocalStructure,
            MenuPage.Percent(Settings.NeuralLocalStructure)));

        rows.Add(MenuItem.Toggle(
            "nrskinfollow", "Skin follows detail", Settings.NeuralSkinFollowsStructure));

        if (!Settings.NeuralSkinFollowsStructure)
        {
            rows.Add(MenuItem.Slider(
                "nrskin",
                "Skin detail",
                Settings.NeuralSkinStructure,
                MenuPage.Percent(Settings.NeuralSkinStructure)));
        }

        rows.Add(MenuItem.Toggle("nrskinmask", "Find skin", Settings.NeuralAutoSkinMask));

        rows.Add(MenuItem.Choice(
            "nrpreset", "Network", NeuralUplift.Describe(Settings.NeuralPreset)));

        rows.Add(MenuItem.Choice(
            "nrstyle", "Look", NeuralUplift.Describe(Settings.NeuralStyle)));

        // What the player cannot find out by trying it: a network that ships one set of
        // weights answers both of those rows with the same picture, and there is no way to
        // tell that from a setting that is not working.
        rows.Add(MenuItem.Label(
            "Network and look do nothing unless the installed file carries more than one."));

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
    ];

    /// <summary>
    /// How the game plays, and the things it will do for the player rather than ask of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Made Easier used to be a page of its own, on the grounds that these two are not
    /// preferences about presentation — each one changes what the story asks of the player,
    /// and a switch that quietly does that should not sit in the same undifferentiated list
    /// as the captions. That reasoning was right and the page was the wrong answer to it: a
    /// heading says the same thing, in the same place, without a second screen to find.
    /// </para>
    /// <para>
    /// Both are off by default, and both name the puzzle they take away <em>in the row
    /// itself</em> rather than in a sentence under it. "Skip a puzzle" is no help to
    /// somebody who has not met it yet and no reassurance to somebody who has; "skip the
    /// cat-hair moustache" is both, and costs no second line.
    /// </para>
    /// </remarks>
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
        MenuItem.Toggle("freecamera", "Free camera (may leave the room)", Settings.FreeCamera),

        MenuItem.Toggle("captions", "Write out what is said", Settings.Captions),
        MenuItem.Toggle("intro", "Play the intro on starting", Settings.PlayIntro),
        MenuItem.Toggle("eggs", "Easter eggs", Settings.EasterEggs),

        // Named for what it gives rather than for what it is. "Cut content" is what
        // somebody looking for this will search for; the values say what turning it on
        // will actually mean, which "on" and "off" could not.
        MenuItem.Choice("restored", "Cut content", Describe(Settings.RestoredContent)),

        MenuItem.Heading("Made easier"),
        MenuItem.Toggle("moustache", "Skip the cat-hair moustache", Settings.AlwaysWearsMoustache),
        MenuItem.Toggle("armour", "Gabriel cannot be killed", Settings.PlotArmour),
    ];

    /// <summary>
    /// Which key and which pad button do which job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three groups, not one list of every control twice.</b> A row that offered a key
    /// and a pad button at once would need two targets on one row, and a page laid out in
    /// two columns has no room for that. Keys together and pad buttons together is also how
    /// people actually use the page: somebody is rebinding a keyboard or setting up a pad,
    /// almost never both in the same sitting.
    /// </para>
    /// <para>
    /// The pointer is first because it is what a gamepad most has to be able to do in this
    /// game. GK3 is played by pointing at things; a pad that cannot click is a pad that
    /// cannot play.
    /// </para>
    /// </remarks>
    private List<MenuItem> Controls()
    {
        InputBindings bound = Bindings;

        List<MenuItem> rows =
        [
            MenuItem.Heading("Gamepad"),

            MenuItem.Toggle(
                "padcursor", "Left stick moves the pointer", Settings.GamepadCursor),

            MenuItem.Slider(
                "padspeed",
                "How fast",
                Fraction(
                    Settings.GamepadCursorSpeed,
                    GK3Reborn.Game.Settings.SlowestCursor,
                    GK3Reborn.Game.Settings.FastestCursor),
                MenuPage.Percent(Fraction(
                    Settings.GamepadCursorSpeed,
                    GK3Reborn.Game.Settings.SlowestCursor,
                    GK3Reborn.Game.Settings.FastestCursor))) with
            {
                Enabled = Settings.GamepadCursor,
            },
        ];

        // Said rather than left to be guessed at. Every row below this does nothing without
        // a pad, and a page of dead-looking settings with no explanation is how somebody
        // concludes the game has no gamepad support.
        if (!HasGamepad)
        {
            rows.Add(MenuItem.Label("No gamepad is plugged in. These can still be set."));
        }

        rows.Add(MenuItem.Heading("Pointer, on the pad"));

        foreach (PointerButton pointer in Enum.GetValues<PointerButton>())
        {
            rows.Add(MenuItem.Binding(
                "ptr:" + pointer,
                InputBindings.Name(pointer),
                Waiting("ptr:" + pointer)
                    ? "Press a button…"
                    : GamepadButtons.Describe(bound.Button(pointer))));
        }

        rows.Add(MenuItem.Heading("Keys"));

        foreach (CameraAction action in InputBindings.Actions)
        {
            rows.Add(MenuItem.Binding(
                "key:" + action,
                InputBindings.Name(action),
                Waiting("key:" + action) ? "Press a key…" : bound.Describe(action)));
        }

        rows.Add(MenuItem.Heading("Buttons, on the pad"));

        foreach (CameraAction action in InputBindings.Actions)
        {
            rows.Add(MenuItem.Binding(
                "pad:" + action,
                InputBindings.Name(action),
                Waiting("pad:" + action)
                    ? "Press a button…"
                    : GamepadButtons.Describe(bound.Button(action))));
        }

        rows.Add(MenuItem.Button("bindreset", "Put every control back"));

        // What the player cannot find out by trying it: which way out of a rebind there is,
        // and that there is one at all. Everything else on this screen is a row that changes
        // when it is chosen; this is the one place the screen stops and waits.
        if (Listening)
        {
            rows.Add(MenuItem.Label(
                "Escape leaves it alone. Backspace clears it."));
        }

        return rows;
    }

    /// <summary>
    /// The bindings as they now stand, read back out of the settings.
    /// </summary>
    /// <remarks>
    /// Rebuilt from what is stored rather than kept beside it, so that there is one answer
    /// to what a key does and it is the one that was saved. Cached against the stored form
    /// it came from, because the Controls page asks for it once per row per frame and
    /// rebuilding a set of dictionaries fifty times a frame to draw a menu is not a trade
    /// worth making.
    /// </remarks>
    public InputBindings Bindings
    {
        get
        {
            if (!ReferenceEquals(_storedBindings, Settings.Bindings) || _bindings is null)
            {
                _storedBindings = Settings.Bindings;
                _bindings = InputBindings.Restore(Settings.Bindings);
            }

            return _bindings;
        }
    }

    private StoredBindings? _storedBindings;
    private InputBindings? _bindings;

    /// <summary>Which row is waiting to be told what to answer to, or empty for none.</summary>
    private string _listening = string.Empty;

    /// <summary>Whether the screen is waiting for a key or a button to be pressed.</summary>
    /// <remarks>
    /// Read by the host, which stops feeding the page arrow keys while it is true and feeds
    /// it whatever was pressed instead. A rebind that could be interrupted by the Up arrow
    /// moving the selection would be a rebind nobody could give the Up arrow to.
    /// </remarks>
    public bool Listening => _listening.Length > 0;

    /// <summary>Whether one particular row is the one waiting.</summary>
    private bool Waiting(string id) =>
        string.Equals(_listening, id, StringComparison.Ordinal);

    /// <summary>Starts waiting for a key or a button for one row.</summary>
    private void Listen(string id) => _listening = id;

    /// <summary>Stops waiting, and leaves the binding alone.</summary>
    public void Cancel() => _listening = string.Empty;

    /// <summary>
    /// Binds whatever the player just pressed to whatever they were rebinding.
    /// </summary>
    /// <param name="key">The key pressed, or <see cref="InputKey.None"/> for none.</param>
    /// <param name="button">The pad button pressed, or none.</param>
    /// <param name="clear">Whether Backspace was pressed, which unbinds it.</param>
    /// <returns>True when something was bound and the page should be redrawn.</returns>
    /// <remarks>
    /// <para>
    /// Takes both at once because the player may answer either question with either device
    /// and there is no reason to refuse them. A key row answered with a pad button binds the
    /// pad button; the row is a suggestion about which is likelier, not a rule.
    /// </para>
    /// <para>
    /// <b>Escape is not a bindable key here and neither is Backspace.</b> They are the way
    /// out and the way to clear, which are the two things somebody has to be able to do when
    /// the screen has stopped and is waiting for them. Escape is already bound to the menu
    /// and Backspace to nothing, so neither is a loss.
    /// </para>
    /// </remarks>
    public bool Captured(InputKey key, GamepadButton button, bool clear = false)
    {
        if (!Listening)
        {
            return false;
        }

        string id = _listening;

        if (key == InputKey.Escape)
        {
            Cancel();

            return true;
        }

        if (clear || key == InputKey.Backspace)
        {
            key = InputKey.None;
            button = GamepadButton.None;
        }
        else if (key == InputKey.None && button == GamepadButton.None)
        {
            return false;
        }

        InputBindings bound = Bindings;
        string what = id[..3];
        string named = id[4..];

        if (what == "ptr" && Enum.TryParse(named, out PointerButton pointer))
        {
            bound = bound.With(pointer, button);
        }
        else if (Enum.TryParse(named, out CameraAction action))
        {
            // A key row answered with a pad button, or the other way round, binds what was
            // actually pressed. Refusing it would be the page telling the player they had
            // pressed the wrong kind of thing, which is never true.
            bound = button != GamepadButton.None
                ? bound.With(action, button)
                : bound.With(action, key);
        }

        Adopt(bound);
        Cancel();

        return true;
    }

    /// <summary>Puts a set of bindings into the settings.</summary>
    private void Adopt(InputBindings bound)
    {
        Settings before = Settings;

        Settings = Settings with { Bindings = bound.Store() };
        _storedBindings = Settings.Bindings;
        _bindings = bound;

        if (Settings != before)
        {
            Dirty = true;
        }
    }

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

            "backend" => Settings with
            {
                Backend = Step(Backends, Settings.Backend, action.Step),
            },

            "latency" => Settings with
            {
                Latency = Step(Latencies, Settings.Latency, action.Step),
            },

            "reconstruction" => Settings with { RayReconstruction = !Settings.RayReconstruction },

            "neural" => Settings with { NeuralUplift = !Settings.NeuralUplift },

            "nrstrength" => Settings with
            {
                NeuralIntensity = Level(Settings.NeuralIntensity, action),
            },

            "nrtone" => Settings with
            {
                NeuralLocalTone = Level(Settings.NeuralLocalTone, action),
            },

            "nrglobal" => Settings with
            {
                NeuralGlobalTone = Level(Settings.NeuralGlobalTone, action),
            },

            "nrstructure" => Settings with
            {
                NeuralLocalStructure = Level(Settings.NeuralLocalStructure, action),
            },

            "nrskinfollow" => Settings with
            {
                NeuralSkinFollowsStructure = !Settings.NeuralSkinFollowsStructure,
            },

            "nrskin" => Settings with
            {
                NeuralSkinStructure = Level(Settings.NeuralSkinStructure, action),
            },

            "nrskinmask" => Settings with
            {
                NeuralAutoSkinMask = !Settings.NeuralAutoSkinMask,
            },

            "nrpreset" => Settings with
            {
                NeuralPreset = Wrapped(
                    Settings.NeuralPreset + (action.Step == 0 ? 1 : action.Step),
                    NeuralUplift.Highest + 1),
            },

            "nrstyle" => Settings with
            {
                NeuralStyle = Wrapped(
                    Settings.NeuralStyle + (action.Step == 0 ? 1 : action.Step),
                    NeuralUplift.Highest + 1),
            },

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
            "rails" => Settings with { ThickCutoutCards = !Settings.ThickCutoutCards },
            "glide" => Settings with { CameraGlide = !Settings.CameraGlide },
            "cinematics" => Settings with { Cinematics = !Settings.Cinematics },
            "freecamera" => Settings with { FreeCamera = !Settings.FreeCamera },
            "captions" => Settings with { Captions = !Settings.Captions },
            "intro" => Settings with { PlayIntro = !Settings.PlayIntro },
            "eggs" => Settings with { EasterEggs = !Settings.EasterEggs },

            "restored" => Settings with
            {
                RestoredContent = Settings.RestoredContent switch
                {
                    CutContentTier.None => CutContentTier.Observation,
                    CutContentTier.Observation => CutContentTier.All,
                    CutContentTier.All => CutContentTier.Reconstructed,
                    _ => CutContentTier.None,
                },
            },

            "moustache" => Settings with
            {
                AlwaysWearsMoustache = !Settings.AlwaysWearsMoustache,
            },

            "armour" => Settings with { PlotArmour = !Settings.PlotArmour },

            "realistic" => Settings with { RealisticLighting = !Settings.RealisticLighting },
            "floorreflect" => Settings with { FloorReflections = !Settings.FloorReflections },

            "reflectivity" => Settings with
            {
                Reflectivity = GK3Reborn.Game.Settings.MostReflective *
                    Level(Settings.Reflectivity / GK3Reborn.Game.Settings.MostReflective, action),
            },

            "padcursor" => Settings with { GamepadCursor = !Settings.GamepadCursor },

            "padspeed" => Settings with
            {
                GamepadCursorSpeed = Between(
                    Settings.GamepadCursorSpeed,
                    GK3Reborn.Game.Settings.SlowestCursor,
                    GK3Reborn.Game.Settings.FastestCursor,
                    action),
            },

            "bindreset" => Settings with { Bindings = null },

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

    /// <summary>Where a slider between two plain numbers ends up.</summary>
    /// <remarks>
    /// The luminances have <see cref="Nits(float, float, float, MenuAction)"/> of their own
    /// because they are rounded to ten candelas. Everything else that is a number rather
    /// than a fraction wants this.
    /// </remarks>
    private static float Between(float current, float low, float high, MenuAction action) =>
        low + ((high - low) * Level(Fraction(current, low, high), action));

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

    /// <summary>What each cut-content tier is called in the menu.</summary>
    /// <remarks>
    /// The names say what the player gets, not what the tier is. "Observation" is a word
    /// out of the implementation; "things to look at" is the row telling somebody who has
    /// never read the documentation what turning it on will do to their game.
    /// </remarks>
    private static string Describe(CutContentTier tier) => tier switch
    {
        CutContentTier.Observation => "Things to look at",
        CutContentTier.All => "Everything, puzzles included",
        CutContentTier.Reconstructed => "And objects rebuilt from scratch",
        _ => "Off",
    };

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

    /// <summary>The chosen graphics API, and whether it is the one drawing.</summary>
    /// <remarks>
    /// <para>
    /// The automatic answer says what it resolved to, because "Automatic" alone tells a
    /// player nothing about the machine in front of them — and what it resolves to is the
    /// whole reason somebody would look at this row.
    /// </para>
    /// <para>
    /// <b>The restart is said in the value rather than under the row.</b> A setting that
    /// waits is worth saying and this page allows itself no prose, so it is said where it is
    /// true: the moment the two agree again the words go away by themselves, which a line of
    /// explanation underneath would not.
    /// </para>
    /// </remarks>
    private string DescribeBackend()
    {
        RenderBackend chosen = RenderBackends.Resolve(Settings.Backend);

        string name = Settings.Backend == RenderBackend.Automatic
            ? $"Automatic ({Describe(chosen)})"
            : Describe(chosen);

        return RunningBackend != RenderBackend.Automatic && RunningBackend != chosen
            ? name + ", next start"
            : name;
    }

    private static string Describe(RenderBackend backend) => backend switch
    {
        RenderBackend.Direct3D12 => "Direct3D 12",
        _ => "Vulkan",
    };

    private static string Describe(LatencyMode latency) => latency switch
    {
        LatencyMode.On => "On",
        LatencyMode.Boost => "On + boost",
        _ => "Off",
    };

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

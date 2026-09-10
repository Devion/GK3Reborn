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
    private const float SmallestText = GK3Reborn.Game.Settings.SmallestText;

    /// <summary>The other end.</summary>
    private const float LargestText = GK3Reborn.Game.Settings.LargestText;

    /// <summary>
    /// The sizes the display page offers, plus whatever the monitor's own is.
    /// </summary>
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

    /// <summary>
    /// The port's own words, in the language the game is being played in.
    /// </summary>
    public UiText Text { get; set; } = UiText.English;

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
    public bool Illustrated { get; set; }

    /// <summary>
    /// Whether the port's own title screen could be drawn at all on this installation.
    /// </summary>
    /// <remarks>
    /// Its six layers travel in <c>Reborn.rebarn</c>. False says the game has not got them,
    /// which is what a 1999 disc plus the executable is; the row is still listed, dead, with
    /// a line under it saying why. A row that appears when a pack is dropped in and is
    /// absent until then is a row nobody knows to look for -- the same argument the language
    /// row is made with.
    /// </remarks>
    public bool ModernMenuAvailable { get; set; }

    /// <summary>
    /// The languages this installation can actually be played in.
    /// </summary>
    public IReadOnlyList<Content.GameLanguage> Languages { get; set; } =
        [Content.GameLanguage.Default];

    /// <summary>The language now chosen.</summary>
    private Content.GameLanguage Language => Content.GameLanguage.Of(Settings.Language);

    /// <summary>
    /// The settings screen's sections, in the order they are listed down its side.
    /// </summary>
    public static IReadOnlyList<MenuSection> Sections { get; } =
    [
        new("gameplay", "General"),
        new("video", "Picture"),
        new("display", "Display"),
        new("audio", "Sound"),
        new("controls", "Controls"),
    ];

    /// <summary>
    /// The same sections, named in the player's own language.
    /// </summary>
    public IReadOnlyList<MenuSection> Tabs =>
    [
        .. Sections.Select(section => section with
        {
            Text = Text.Say("settings.section." + section.Id, section.Text),
        }),
    ];

    /// <summary>Which page each of those sections is.</summary>
    private static readonly FrontEndPage[] SectionPages =
    [
        FrontEndPage.Gameplay,
        FrontEndPage.Video,
        FrontEndPage.Display,
        FrontEndPage.Audio,
        FrontEndPage.Controls,
    ];

    /// <summary>Whether what is showing is one of the settings sections.</summary>
    public bool OnSettings => Array.IndexOf(SectionPages, Page) >= 0;

    /// <summary>Which section is showing, or -1 when none is.</summary>
    public int Section => Array.IndexOf(SectionPages, Page);

    /// <summary>Shows the section before or after this one, wrapping round.</summary>
    /// <param name="by">-1 for the one above, 1 for the one below.</param>
    /// <returns>True when the section changed.</returns>
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
    public string Title => Page switch
    {
        // The game's own name is not translated, because it is a name.
        FrontEndPage.Main => InGame
            ? Text.Say("menu.title.paused", "Paused")
            : Illustrated ? string.Empty : "Gabriel Knight 3",
        FrontEndPage.Save => Text.Say("menu.title.save", "Save Game"),
        FrontEndPage.Load => Text.Say("menu.title.load", "Restore Game"),
        _ => Text.Say("menu.title.settings", "Settings"),
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
    private FrontEndPage _lastSection = FrontEndPage.Gameplay;

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
    public string? StoredAt { get; set; }

    /// <summary>Which slot the player last pointed at.</summary>
    public string? Slot { get; private set; }

    /// <summary>What each slot holds, for the host to fill in before the page is shown.</summary>
    public IReadOnlyList<SaveSlot> Saves { get; set; } = [];

    /// <summary>The interface's number for a slot's picture, by slot.</summary>
    public Func<string, int>? Illustrations { get; set; }

    /// <summary>What the player is calling the game they are about to save.</summary>
    public string Naming { get; set; } = string.Empty;

    /// <summary>
    /// A row that is on or off, reading in the player's own language.
    /// </summary>
    /// <param name="id">What a click on it answers to.</param>
    /// <param name="text">What it is called.</param>
    /// <param name="on">Whether it is on.</param>
    /// <returns>The row.</returns>
    private MenuItem Toggle(string id, string text, bool on) =>
        MenuItem.Toggle(id, text, on) with
        {
            Value = on ? Text.Say("value.on", "On") : Text.Say("value.off", "Off"),
        };

    private IReadOnlyList<MenuItem> Main() => InGame

        // Paused. No intro from here: the player is in the middle of the game, and the row
        // they want first is the one that gives it back to them.
        ? [
            MenuItem.Button("resume", Text.Say("menu.resume", "Resume")),
            MenuItem.Button("save", Text.Say("menu.save", "Save")),
            MenuItem.Button("load", Text.Say("menu.load", "Restore")),

            // A room can wedge: an approach walk far longer than anybody will sit through,
            // a script that parked and never came back, a clip on the player that never
            // ends. Every one of those reads the same way from the player's chair — the
            // camera stops answering and clicks stop reaching the floor — and the player
            // has no way to say so from inside the room, because saying so is a click.
            //
            // So it is a row here rather than a setting: it is a thing done once, to the
            // room the player is stuck in, and the menu is the only place they can still
            // reach. It costs nothing of the story; see SceneUpdate.Unstick.
            MenuItem.Button("unstick", Text.Say("menu.unstick", "Get Unstuck")),

            MenuItem.Button("options", Text.Say("menu.options", "Settings")),
            MenuItem.Button("quit", Text.Say("menu.leave", "Leave the Game")),
        ]

        // The original's own five, in its own order. Intro first because it is what the
        // game opens with and somebody who skipped it may want it back.
        : [
            MenuItem.Button("intro", Text.Say("menu.intro", "Intro")),
            MenuItem.Button("play", Text.Say("menu.play", "Play")),

            MenuItem.Button("load", Text.Say("menu.load", "Restore")),

            MenuItem.Button("options", Text.Say("menu.options", "Settings")),
            MenuItem.Button("quit", Text.Say("menu.quit", "Quit")),
        ];

    /// <summary>
    /// The slots, as rows.
    /// </summary>
    /// <param name="writing">Whether this is the page that saves or the page that restores.</param>
    /// <returns>One row per slot, and a way back.</returns>
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

        rows.Add(MenuItem.Button("back", Text.Say("menu.back", "Back")));

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
    private string Described(string slot)
    {
        string name = slot switch
        {
            SaveStore.QuickSlot => Text.Say("save.quick", "Quick save"),
            SaveStore.AutoSlot => Text.Say("save.auto", "Autosave"),
            _ when IsNumbered(slot) =>
                Text.Say("save.slot", "Slot {0}", slot.TrimStart('0')),

            // A game the 1999 original wrote, brought across under its own file name.
            // Saying so is worth a word: it is why the row is there and not numbered.
            _ when slot.StartsWith("gk3-", StringComparison.OrdinalIgnoreCase) =>
                Text.Say("save.original", "Original save"),

            // Anything else somebody has put in the folder, under whatever they called it.
            _ => slot,
        };

        if (Written(slot) is not { } save)
        {
            return name + Text.Say("save.empty", "  -  empty");
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
    public UpscalerRuntimes? Runtimes { get; set; }

    /// <summary>How big the window is, so the upscaling page can say what it will draw.</summary>
    public (int Width, int Height) Window { get; set; } = (1920, 1080);

    /// <summary>
    /// Which upscalers this machine may be offered.
    /// </summary>
    public IReadOnlyList<UpscalerKind> Offered { get; set; } = EveryUpscaler;

    /// <summary>Whether the display actually gave back a high dynamic range colour space.</summary>
    public bool HighDynamicRangeActive { get; set; }

    /// <summary>What is actually upscaling, in the renderer's own words.</summary>
    public string UpscalerRunning { get; set; } = string.Empty;

    /// <summary>Whether DLSS started and this card can run it.</summary>
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
    public int FrameGenerationMaximum { get; set; }

    /// <summary>Whether Reflex loaded and can be driven.</summary>
    public bool LatencyControl { get; set; }

    /// <summary>Whether a gamepad is plugged in.</summary>
    public bool HasGamepad { get; set; }

    /// <summary>Which graphics API is drawing, as against the one that is chosen.</summary>
    public RenderBackend RunningBackend { get; set; }

    /// <summary>
    /// Everything about what is drawn: how it is lit, what it is built from, and how it is
    /// scaled up to the window.
    /// </summary>
    private List<MenuItem> Video()
    {
        List<MenuItem> rows =
        [
            MenuItem.Heading(Text.Say("picture.lighting", "Lighting")),
            MenuItem.Choice(
                "picture", Text.Say("picture.lighting", "Lighting"), Describe(Settings.Picture)),

            // Dead with no rays, because there is nothing for it to take away then: the
            // bake is the room's lighting at that tier and the rig only reaches the people
            // standing in it. A row that silently did nothing would be worse.
            Toggle(
                "realistic",
                Text.Say("picture.realistic", "Only real light sources"),
                Settings.RealisticLighting) with
            {
                Enabled = Settings.Quality != RayTracingQuality.None,
            },
        ];

        if (Settings.RealisticLighting && Settings.Quality != RayTracingQuality.None)
        {
            // What the player cannot find out by trying it in one room: the rooms this
            // changes most are the ones the artists were propping up hardest, and a room
            // going dark is the setting working rather than failing.
            rows.Add(MenuItem.Label(Text.Say(
                "picture.realistic.note",
                "The artists' fills, ambients and bounces are switched off. Rooms lit "
                + "mostly by them get darker.")));
        }

        rows.AddRange(
        [
            MenuItem.Heading(Text.Say("picture.reflections", "Reflections")),

            Toggle(
                "floorreflect",
                Text.Say("picture.floorreflect", "Floors reflect the room"),
                Settings.FloorReflections),

            // A multiplier rather than a percentage, because one is the physical answer and
            // the row is about departing from it. "50%" on a slider whose default is the
            // middle reads as half of something; "1.0x" reads as what it is.
            MenuItem.Slider(
                "reflectivity",
                Text.Say("picture.reflectivity", "How strongly"),
                Settings.Reflectivity / GK3Reborn.Game.Settings.MostReflective,
                string.Create(
                    CultureInfo.InvariantCulture, $"{Settings.Reflectivity:F1}x")),

            MenuItem.Heading(Text.Say("picture.detail", "Detail")),

            Toggle(
                "enhanced",
                Text.Say("picture.enhanced", "Higher-resolution textures"),
                Settings.EnhancedTextures),

            Toggle(
                "trees", Text.Say("picture.trees", "Modelled trees"), Settings.ModelledTrees),

            Toggle(
                "terrain",
                Text.Say("picture.terrain", "Reconstructed horizon"),
                Settings.TerrainBackdrop),

            Toggle(
                "rooms",
                Text.Say("picture.rooms", "Rounded room objects"),
                Settings.ImprovedSceneGeometry),

            Toggle(
                "rails",
                Text.Say("picture.rails", "Solid railings and fences"),
                Settings.ThickCutoutCards),

            Toggle(
                "birds",
                Text.Say("picture.birds", "Birds in the sky"),
                Settings.Birds),

            // One row for two towns. They are installed separately and the engine checks
            // for them separately, but somebody switching this off is asking for the 1999
            // skyline and that is one decision rather than two. See Settings.RebuiltTowns.
            Toggle(
                "towns",
                Text.Say("picture.towns", "Rebuilt towns"),
                Settings.RebuiltTowns),

            // The one thing in this group a player cannot see for themselves: the room
            // standing round them was built from whichever set was chosen when it loaded,
            // and rebuilding it here would mean reloading the scene underneath them.
            MenuItem.Label(
                Text.Say("picture.detail.note", "These take effect at the next door.")),
        ]);

        rows.AddRange(Upscaling());

        return rows;
    }

    /// <summary>
    /// The window, the monitor, and how bright the display is allowed to go.
    /// </summary>
    private List<MenuItem> Display()
    {
        List<MenuItem> rows = [];

        // Windows only, because it is the only machine where there is a choice: every other
        // one has Vulkan and nothing else, and a row with one value on it is a row that
        // teaches somebody the game has settings that do nothing.
        if (RenderBackends.IsPossible(RenderBackend.Direct3D12))
        {
            rows.Add(MenuItem.Choice(
                "backend", Text.Say("display.backend", "Graphics API"), DescribeBackend()));
        }

        rows.AddRange(
        [
            MenuItem.Choice(
                "window", Text.Say("display.window", "Window"), Describe(Settings.Display)),

            // Dead rather than explained. A borderless window is the size of the monitor
            // by definition, so there is no size to choose; a row the player cannot land
            // on says that in no words at all, where the sentence it replaces cost three
            // lines of the page.
            MenuItem.Choice("size", Text.Say("display.size", "Resolution"), DescribeSize()) with
            {
                Enabled = Settings.Display != WindowMode.BorderlessFullscreen,
            },

            MenuItem.Slider(
                "textsize",
                Text.Say("display.textsize", "Text size"),
                Fraction(Settings.TextScale, SmallestText, LargestText),
                DescribeTextScale()),

            Toggle(
                "modernmenu",
                Text.Say("display.modernmenu", "Title screen"),
                Settings.ModernMenu) with
            {
                Enabled = ModernMenuAvailable,

                // Named for what it shows rather than for which of the two it is. "On" and
                // "Off" would be a row about a switch; this is a row about which of two
                // pictures the game opens with, and the reading is the answer.
                Value = Settings.ModernMenu && ModernMenuAvailable
                    ? Text.Say("display.modernmenu.new", "The port's own")
                    : Text.Say("display.modernmenu.old", "The original"),
            },

            Toggle(
                "vsync", Text.Say("display.vsync", "Wait for the display"), Settings.VerticalSync),

            Toggle(
                "hdr",
                Text.Say("display.hdr", "High dynamic range"),
                Settings.HighDynamicRange),
        ]);

        if (!ModernMenuAvailable)
        {
            rows.Add(MenuItem.Label(Text.Say(
                "display.modernmenu.missing",
                "The port's own title screen needs Reborn.rebarn beside the game.")));
        }

        if (Settings.HighDynamicRange)
        {
            // Kept, because it is the one row here that is not a preference: the display
            // either gave back the colour space or it did not, and a switch shown on over a
            // monitor in SDR mode is the least useful true statement available.
            rows.Add(MenuItem.Label(HighDynamicRangeActive
                ? Text.Say("display.hdr.took", "The display took it.")
                : Text.Say(
                    "display.hdr.refused",
                    "Asked for, and this display did not offer it.")));

            rows.Add(MenuItem.Choice(
                "transfer",
                Text.Say("display.transfer", "Encoding"),
                Describe(Settings.HdrTransfer)));

            rows.Add(MenuItem.Slider(
                "paperwhite",
                Text.Say("display.paperwhite", "Paper white"),
                Fraction(Settings.PaperWhiteNits, 80f, 400f),
                Nits(Settings.PaperWhiteNits)));

            rows.Add(MenuItem.Slider(
                "peak",
                Text.Say("display.peak", "Brightest the display goes"),
                Fraction(Settings.PeakNits, 400f, 4000f),
                Nits(Settings.PeakNits)));

            rows.Add(MenuItem.Slider(
                "sun",
                Text.Say("display.sun", "Sunlight"),
                Fraction(Settings.SunNits, 200f, 4000f),
                Nits(Settings.SunNits)));

            rows.Add(MenuItem.Slider(
                "lights",
                Text.Say("display.lights", "Lamps and windows"),
                Fraction(Settings.LightNits, 200f, 4000f),
                Nits(Settings.LightNits)));
        }
        else
        {
            rows.Add(MenuItem.Choice(
                "tonemap",
                Text.Say("display.tonemap", "Tone curve"),
                Describe(Settings.ToneMapping)));
        }

        return rows;
    }

    /// <summary>
    /// Drawing the room smaller than the window and enlarging it.
    /// </summary>
    private List<MenuItem> Upscaling()
    {
        RuntimeFiles files =
            Runtimes?.For(Settings.Upscaler) ?? UpscalerRuntimes.Unknown(Settings.Upscaler);

        List<MenuItem> rows =
        [
            MenuItem.Heading(Text.Say("picture.upscaling", "Upscaling")),
            MenuItem.Choice(
                "upscaler",
                Text.Say("picture.upscaler", "Upscaler"),
                Describe(Settings.Upscaler)),
        ];

        if (Settings.Upscaler is UpscalerKind.Fsr or UpscalerKind.Dlss && !files.Present)
        {
            rows.Add(MenuItem.Label(Text.Say(
                "picture.upscaler.missing",
                "Not installed: copy {0} into the game's libs folder.",
                List(files.Missing))));
        }
        else if (Settings.Upscaler == UpscalerKind.Dlss && !DlssAvailable)
        {
            // Installed and refused, which is a different sentence: there is nothing to
            // download and nothing the player did wrong.
            rows.Add(MenuItem.Label(Text.Say(
                "picture.upscaler.wrongcard",
                "Installed, and this card cannot run it: DLSS needs a GeForce RTX.")));
        }

        if (Settings.Upscaler != UpscalerKind.Off)
        {
            bool pinned = Settings.Upscaler == UpscalerKind.Dlss && Settings.NeuralUplift;

            rows.Add(MenuItem.Choice(
                "ratio",
                Text.Say("picture.ratio", "Quality"),
                Describe(Settings.UpscalerQuality)) with
            {
                Enabled = !pinned,
            });

            // What the player cannot find out by trying it: the row is dead because the
            // network will not scale, not because the setting stopped working.
            if (pinned)
            {
                rows.Add(MenuItem.Label(Text.Say(
                    "picture.ratio.pinned",
                    "Neural uplift draws at the window's own size; it reworks the picture "
                    + "rather than enlarging it.")));
            }

            rows.Add(MenuItem.Label(Between(Settings.Upscaling)));

            rows.Add(Toggle(
                "sharpen", Text.Say("picture.sharpen", "Sharpen"), Settings.Sharpening));

            if (Settings.Sharpening)
            {
                rows.Add(MenuItem.Slider(
                    "sharpness",
                    Text.Say("picture.sharpness", "How much"),
                    Settings.Sharpness,
                    MenuPage.Percent(Settings.Sharpness)));
            }
        }

        if (Settings.Upscaler == UpscalerKind.Dlss)
        {
            rows.Add(MenuItem.Choice(
                "preset",
                Text.Say("picture.preset", "Model"),
                DescribePreset(Settings.DlssPreset)));

            rows.Add(Toggle(
                "reconstruction",
                Text.Say("picture.reconstruction", "Ray reconstruction"),
                Settings.RayReconstruction) with
            {
                Enabled = DlssRayReconstruction,
            });

            // Only when it cannot be had. Why a row is dead is worth a line; what a row
            // does when it works is what the row itself says.
            if (!DlssRayReconstruction)
            {
                rows.Add(MenuItem.Label(
                    DlssRayReconstructionNote is { Length: > 0 } why
                        ? Text.Say("picture.reconstruction.no", "Not available: {0}.", why)
                        : Text.Say(
                            "picture.reconstruction.missing",
                            "Needs sl.dlss_d.dll and nvngx_dlssd.dll in the libs folder.")));
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
            "generation",
            Text.Say("picture.generation", "Frame generation"),
            DescribeGeneration(Settings.FrameGeneration)) with
        {
            Enabled = generation,
        });

        if (!generation)
        {
            rows.Add(MenuItem.Label(Text.Say(
                "picture.generation.missing",
                "Needs FSR or DLSS, and their frame-generation runtime, in the libs folder.")));
        }
        // No line of its own, and neither does the row above. What a card will generate
        // limits the row rather than being written under it: a factor that is not offered
        // needs no sentence explaining that it is not offered, and Reflex comes out of the
        // same bundle the line above already names.
        rows.Add(MenuItem.Choice(
            "latency", Text.Say("picture.latency", "Low latency"), Describe(Settings.Latency)) with
        {
            Enabled = LatencyControl,
        });

        if (UpscalerRunning is { Length: > 0 })
        {
            rows.Add(MenuItem.Label(
                Text.Say("picture.running", "Running: {0}", UpscalerRunning)));
        }

        return rows;
    }

    /// <summary>The rows for the neural rendering network.</summary>
    private List<MenuItem> Neural()
    {
        bool installed = Runtimes?.NeuralRendering.Present ?? false;

        List<MenuItem> rows =
        [
            Toggle(
                "neural", Text.Say("picture.neural", "Neural uplift"), Settings.NeuralUplift) with
            {
                Enabled = installed,
            },
        ];

        if (!installed)
        {
            rows.Add(MenuItem.Label(Text.Say(
                "picture.neural.missing",
                "Needs nvngx_dlssnr.dll in the game's libs folder.")));

            return rows;
        }

        if (!Settings.NeuralUplift)
        {
            return rows;
        }

        rows.Add(MenuItem.Slider(
            "nrstrength",
            Text.Say("picture.nrstrength", "Strength"),
            Settings.NeuralIntensity,
            MenuPage.Percent(Settings.NeuralIntensity)));

        rows.Add(MenuItem.Slider(
            "nrtone",
            Text.Say("picture.nrtone", "Local contrast"),
            Settings.NeuralLocalTone,
            MenuPage.Percent(Settings.NeuralLocalTone)));

        rows.Add(MenuItem.Slider(
            "nrglobal",
            Text.Say("picture.nrglobal", "Overall tone"),
            Settings.NeuralGlobalTone,
            MenuPage.Percent(Settings.NeuralGlobalTone)));

        rows.Add(MenuItem.Slider(
            "nrstructure",
            Text.Say("picture.nrstructure", "Fine detail"),
            Settings.NeuralLocalStructure,
            MenuPage.Percent(Settings.NeuralLocalStructure)));

        rows.Add(Toggle(
            "nrskinfollow",
            Text.Say("picture.nrskinfollow", "Skin follows detail"),
            Settings.NeuralSkinFollowsStructure));

        if (!Settings.NeuralSkinFollowsStructure)
        {
            rows.Add(MenuItem.Slider(
                "nrskin",
                Text.Say("picture.nrskin", "Skin detail"),
                Settings.NeuralSkinStructure,
                MenuPage.Percent(Settings.NeuralSkinStructure)));
        }

        rows.Add(Toggle(
            "nrskinmask",
            Text.Say("picture.nrskinmask", "Find skin"),
            Settings.NeuralAutoSkinMask));

        rows.Add(MenuItem.Choice(
            "nrpreset",
            Text.Say("picture.nrpreset", "Network"),
            DescribeNetwork(Settings.NeuralPreset)));

        rows.Add(MenuItem.Choice(
            "nrstyle",
            Text.Say("picture.nrstyle", "Look"),
            DescribeNetwork(Settings.NeuralStyle)));

        // What the player cannot find out by trying it: a network that ships one set of
        // weights answers both of those rows with the same picture, and there is no way to
        // tell that from a setting that is not working.
        rows.Add(MenuItem.Label(Text.Say(
            "picture.neural.oneweight",
            "Network and look do nothing unless the installed file carries more than one.")));

        return rows;
    }

    private IReadOnlyList<MenuItem> Audio() =>
    [
        MenuItem.Slider(
            "master",
            Text.Say("sound.master", "Overall"),
            Settings.MasterVolume,
            MenuPage.Percent(Settings.MasterVolume)),

        MenuItem.Slider(
            "music",
            Text.Say("sound.music", "Music and cutscenes"),
            Settings.MusicVolume,
            MenuPage.Percent(Settings.MusicVolume)),

        MenuItem.Slider(
            "ambience",
            Text.Say("sound.ambience", "Room tone"),
            Settings.AmbienceVolume,
            MenuPage.Percent(Settings.AmbienceVolume)),

        MenuItem.Slider(
            "effects",
            Text.Say("sound.effects", "Effects"),
            Settings.EffectsVolume,
            MenuPage.Percent(Settings.EffectsVolume)),

        MenuItem.Slider(
            "dialogue",
            Text.Say("sound.dialogue", "Speech"),
            Settings.DialogueVolume,
            MenuPage.Percent(Settings.DialogueVolume)),

        MenuItem.Choice("speakers", Text.Say("sound.speakers", "Speakers"), Describe(Settings.Speakers)),

        // Said rather than quietly not done. The device is opened once at startup, and a
        // player who changes this and hears no difference would reasonably conclude the
        // setting is broken. Every other row on this page is heard while it is dragged.
        MenuItem.Label(Text.Say("sound.speakers.note", "Speakers take effect at the next start.")),
    ];

    /// <summary>
    /// How the game plays, and the things it will do for the player rather than ask of them.
    /// </summary>
    private IReadOnlyList<MenuItem> Gameplay() =>
    [
        // First, and on this page rather than on Sound, because it is not a preference
        // about how the game sounds — it decides what is said, what is written, what is
        // painted on a road sign and which of Sidney's documents can be read. It is the
        // one row here that changes the words of the story.
        //
        // Offered even when there is only English to offer, with the sentence under it
        // saying why. A row that appears when a second pack is dropped in and is absent
        // until then is a row nobody knows to look for.
        MenuItem.Choice("language", Text.Say("general.language", "Language"), Describe(Language)),

        Languages.Count > 1
            ? MenuItem.Label(Text.Say(
                "general.language.note", "Language takes effect at the next start."))
            : MenuItem.Label(Text.Say(
                "general.language.only",
                "Only English is installed. Other languages need their own pack beside "
                + "the game.")),

        MenuItem.Slider(
            "hurry",
            Text.Say("general.hurry", "Hurrying pace"),
            (Settings.HurryFactor - 1f) / 3f,
            string.Create(CultureInfo.InvariantCulture, $"{Settings.HurryFactor:F1}x")),

        Toggle(
            "glide",
            Text.Say("general.glide", "Camera travels between angles"),
            Settings.CameraGlide),

        Toggle(
            "cinematics",
            Text.Say("general.cinematics", "Let the story move the camera"),
            Settings.Cinematics),

        // Named for what it does rather than for what it is for. "Free camera" is a word
        // somebody already looking for it will find, and "leave the room" is the half that
        // tells everybody else what turning it on will look like.
        Toggle(
            "freecamera",
            Text.Say("general.freecamera", "Free camera (may leave the room)"),
            Settings.FreeCamera),

        Toggle(
            "captions", Text.Say("general.captions", "Write out what is said"), Settings.Captions),

        // Its own row, immediately under the one it pairs with, because the two are
        // different decisions: a caption is small and beside whoever is speaking, and a
        // subtitle is across the bottom of a full-screen film. Somebody may well want one
        // and not the other. The parallel wording is what says they are related.
        Toggle(
            "filmcaptions",
            Text.Say("general.filmcaptions", "Write out what is said in films"),
            Settings.MovieSubtitles),

        Toggle(
            "intro", Text.Say("general.intro", "Play the intro on starting"), Settings.PlayIntro),

        Toggle("eggs", Text.Say("general.eggs", "Easter eggs"), Settings.EasterEggs),

        // Named for what it gives rather than for what it is. "Cut content" is what
        // somebody looking for this will search for; the values say what turning it on
        // will actually mean, which "on" and "off" could not.
        MenuItem.Choice(
            "restored", Text.Say("general.restored", "Cut content"), Describe(Settings.RestoredContent)),

        MenuItem.Heading(Text.Say("general.easier", "Made easier")),

        Toggle(
            "moustache",
            Text.Say("general.moustache", "Skip the cat-hair moustache"),
            Settings.AlwaysWearsMoustache),

        Toggle(
            "armour", Text.Say("general.armour", "Gabriel cannot be killed"), Settings.PlotArmour),

        Toggle(
            "catch",
            Text.Say("general.catch", "Gabriel catches the pendulum"),
            Settings.CatchesPendulum),
    ];

    /// <summary>
    /// Which key and which pad button do which job.
    /// </summary>
    private List<MenuItem> Controls()
    {
        InputBindings bound = Bindings;

        List<MenuItem> rows =
        [
            MenuItem.Heading(Text.Say("controls.mouse", "Mouse")),

            MenuItem.Slider(
                "cursorsize",
                Text.Say("controls.cursorsize", "Pointer size"),
                Fraction(
                    Settings.CursorScale,
                    GK3Reborn.Game.Settings.SmallestCursor,
                    GK3Reborn.Game.Settings.LargestCursor),
                Times(Settings.CursorScale)),

            MenuItem.Heading(Text.Say("controls.gamepad", "Gamepad")),

            Toggle(
                "padcursor",
                Text.Say("controls.padcursor", "Left stick moves the pointer"),
                Settings.GamepadCursor),

            MenuItem.Slider(
                "padspeed",
                Text.Say("controls.padspeed", "How fast"),
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
            rows.Add(MenuItem.Label(Text.Say(
                "controls.nopad", "No gamepad is plugged in. These can still be set.")));
        }

        rows.Add(MenuItem.Heading(Text.Say("controls.pointer", "Pointer, on the pad")));

        foreach (PointerButton pointer in Enum.GetValues<PointerButton>())
        {
            rows.Add(MenuItem.Binding(
                "ptr:" + pointer,
                Named(pointer),
                Waiting("ptr:" + pointer)
                    ? Text.Say("controls.pressbutton", "Press a button…")
                    : Named(bound.Button(pointer))));
        }

        rows.Add(MenuItem.Heading(Text.Say("controls.keys", "Keys")));

        foreach (CameraAction action in InputBindings.Actions)
        {
            rows.Add(MenuItem.Binding(
                "key:" + action,
                Named(action),
                Waiting("key:" + action)
                    ? Text.Say("controls.presskey", "Press a key…")
                    : Bound(bound, action)));
        }

        rows.Add(MenuItem.Heading(Text.Say("controls.buttons", "Buttons, on the pad")));

        foreach (CameraAction action in InputBindings.Actions)
        {
            rows.Add(MenuItem.Binding(
                "pad:" + action,
                Named(action),
                Waiting("pad:" + action)
                    ? Text.Say("controls.pressbutton", "Press a button…")
                    : Named(bound.Button(action))));
        }

        rows.Add(MenuItem.Button(
            "bindreset", Text.Say("controls.reset", "Put every control back")));

        // What the player cannot find out by trying it: which way out of a rebind there is,
        // and that there is one at all. Everything else on this screen is a row that changes
        // when it is chosen; this is the one place the screen stops and waits.
        if (Listening)
        {
            rows.Add(MenuItem.Label(Text.Say(
                "controls.listening", "Escape leaves it alone. Backspace clears it.")));
        }

        return rows;
    }

    /// <summary>
    /// The bindings as they now stand, read back out of the settings.
    /// </summary>
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

            // Through what is installed rather than through everything GK3 was published
            // in, so a player with English and French steps between two rows rather than
            // through six that do nothing when chosen.
            "language" => Settings with { Language = Next(Languages, Language, action.Step).Code },

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

            "filmcaptions" => Settings with { MovieSubtitles = !Settings.MovieSubtitles },

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

            // Takes effect the next time the menu is opened, which for the row itself is
            // the next start: the screen it changes is the one the player is standing on,
            // and its six pictures went on the device before this page existed.
            "modernmenu" => Settings with { ModernMenu = !Settings.ModernMenu },

            "enhanced" => Settings with { EnhancedTextures = !Settings.EnhancedTextures },
            "trees" => Settings with { ModelledTrees = !Settings.ModelledTrees },
            "terrain" => Settings with { TerrainBackdrop = !Settings.TerrainBackdrop },
            "rooms" => Settings with { ImprovedSceneGeometry = !Settings.ImprovedSceneGeometry },
            "rails" => Settings with { ThickCutoutCards = !Settings.ThickCutoutCards },
            "birds" => Settings with { Birds = !Settings.Birds },
            "towns" => Settings with { RebuiltTowns = !Settings.RebuiltTowns },
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
            "catch" => Settings with { CatchesPendulum = !Settings.CatchesPendulum },

            "realistic" => Settings with { RealisticLighting = !Settings.RealisticLighting },
            "floorreflect" => Settings with { FloorReflections = !Settings.FloorReflections },

            "reflectivity" => Settings with
            {
                Reflectivity = GK3Reborn.Game.Settings.MostReflective *
                    Level(Settings.Reflectivity / GK3Reborn.Game.Settings.MostReflective, action),
            },

            "padcursor" => Settings with { GamepadCursor = !Settings.GamepadCursor },

            // Rounded to a twentieth so the row reads as a round number and two players who
            // set it to the same thing get the same thing.
            "cursorsize" => Settings with
            {
                CursorScale = MathF.Round(
                    Between(
                        Settings.CursorScale,
                        GK3Reborn.Game.Settings.SmallestCursor,
                        GK3Reborn.Game.Settings.LargestCursor,
                        action) * 20f) / 20f,
            },

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
    private string DescribeSize() =>
        Settings.Display == WindowMode.BorderlessFullscreen ||
        Settings.DisplayWidth <= 0 || Settings.DisplayHeight <= 0
            ? Text.Say("display.size.monitor", "The monitor's own")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Settings.DisplayWidth}x{Settings.DisplayHeight}");

    /// <summary>Where a luminance slider sits between its two ends.</summary>
    private static float Fraction(float value, float low, float high) =>
        Math.Clamp((value - low) / MathF.Max(high - low, 1f), 0f, 1f);

    /// <summary>Where a luminance slider ends up.</summary>
    private static float Nits(float current, float low, float high, MenuAction action)
    {
        float part = Level(Fraction(current, low, high), action);

        return MathF.Round((low + ((high - low) * part)) / 10f) * 10f;
    }

    /// <summary>Where a slider between two plain numbers ends up.</summary>
    private static float Between(float current, float low, float high, MenuAction action) =>
        low + ((high - low) * Level(Fraction(current, low, high), action));

    /// <summary>How a multiplier reads: the usual size is 100%.</summary>
    private static string Times(float value) => string.Create(
        CultureInfo.InvariantCulture, $"{value * 100:F0}%");

    /// <summary>How a luminance reads.</summary>
    private string Nits(float value) => Text.Say(
        "display.nits",
        "{0} nits",
        value.ToString("F0", CultureInfo.InvariantCulture));

    /// <summary>The next index round a list of a given length, either way.</summary>
    private static int Wrapped(int at, int length) => ((at % length) + length) % length;

    /// <summary>Several file names, as a sentence rather than as a list.</summary>
    private string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => Text.Say("picture.upscaler.nothing", "nothing"),
        1 => names[0],
        _ => string.Join(Text.Say("list.comma", ", "), names.Take(names.Count - 1))
            + Text.Say("list.and", " and ")
            + names[^1],
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

    /// <summary>The same step, for a list of things that are not enumerations.</summary>
    private static T Next<T>(IReadOnlyList<T> all, T current, int by)
        where T : class
    {
        if (all.Count == 0)
        {
            return current;
        }

        int at = 0;

        for (int i = 0; i < all.Count; i++)
        {
            if (Equals(all[i], current))
            {
                at = i;
                break;
            }
        }

        int next = (at + (by == 0 ? 1 : by)) % all.Count;

        return all[next < 0 ? next + all.Count : next];
    }

    /// <summary>
    /// The two sizes the picture is drawn between.
    /// </summary>
    /// <param name="plan">What is upscaling, and by how much.</param>
    /// <returns>The reading, as "1280x720 to 1920x1080".</returns>
    private string Between(UpscalePlan plan)
    {
        string both = plan.Describe(Window.Width, Window.Height);

        return both.Split(" to ") is [string drawn, string shown]
            ? Text.Say("picture.upscaler.sizes", "{0} to {1}", drawn, shown)
            : both;
    }

    /// <summary>
    /// What one of NVIDIA's presets is called on the page.
    /// </summary>
    /// <param name="preset">Nought for the runtime's own choice, else 1 for A and up.</param>
    /// <returns>The label.</returns>
    private string DescribePreset(int preset)
    {
        if (preset is <= 0 or > DlssPresets.Highest)
        {
            return Text.Say("picture.preset.runtime", "Whatever the runtime prefers");
        }

        string letter = string.Create(
            CultureInfo.InvariantCulture, $"{(char)('A' + preset - 1)}");

        string note = preset switch
        {
            10 => Text.Say("picture.preset.transformer", " (transformer)"),
            11 => Text.Say("picture.preset.best", " (transformer, best picture)"),
            12 => Text.Say("picture.preset.steadiest", " (transformer, steadiest)"),
            13 => Text.Say("picture.preset.fastest", " (transformer, fastest)"),
            _ => string.Empty,
        };

        return Text.Say("picture.preset.letter", "Preset {0}", letter) + note;
    }

    /// <summary>What one of the neural network's weights is called on the page.</summary>
    /// <param name="ordinal">Nought for the network's own choice, else which one.</param>
    /// <returns>The label.</returns>
    private string DescribeNetwork(int ordinal) => ordinal <= 0
        ? Text.Say("picture.nrpreset.network", "Whatever the network prefers")
        : Text.Say(
            "picture.nrpreset.number",
            "Number {0}",
            ordinal.ToString(CultureInfo.InvariantCulture));

    /// <summary>How many frames are generated for each drawn one.</summary>
    /// <param name="generation">The setting.</param>
    /// <returns>The label, which is a multiplier or the word for none.</returns>
    private string DescribeGeneration(FrameGeneration generation) =>
        generation == FrameGeneration.Off
            ? Text.Say("value.off", "Off")
            : generation.Describe();

    /// <summary>What one of the game's actions is called on the Controls page.</summary>
    /// <param name="action">The action.</param>
    /// <returns>Its name.</returns>
    private string Named(CameraAction action) =>
        Text.Say("action." + action, InputBindings.Name(action));

    /// <summary>What clicking a mouse button means in this game.</summary>
    private string Named(PointerButton button) =>
        Text.Say("pointer." + button, InputBindings.Name(button));

    /// <summary>Where a gamepad button is on the pad.</summary>
    private string Named(GamepadButton button) =>
        Text.Say("pad." + button, GamepadButtons.Describe(button));

    /// <summary>
    /// Which keys an action is bound to.
    /// </summary>
    /// <param name="bound">The bindings.</param>
    /// <param name="action">The action.</param>
    /// <returns>The keys, joined, or a dash where there are none.</returns>
    private string Bound(InputBindings bound, CameraAction action)
    {
        IReadOnlyList<InputKey> keys = bound.Keys(action);

        return keys.Count == 0
            ? "—"
            : string.Join(
                Text.Say("list.comma", ", "),
                keys.Select(key => Text.Say("key." + key, InputKeys.Describe(key))));
    }

    /// <summary>What a language is called in the menu.</summary>
    private static string Describe(Content.GameLanguage language) =>
        string.Equals(language.Native, language.Name, StringComparison.Ordinal)
            ? language.Name
            : $"{language.Native} ({language.Name})";

    /// <summary>What each cut-content tier is called in the menu.</summary>
    private string Describe(CutContentTier tier) => tier switch
    {
        CutContentTier.Observation => Text.Say("general.restored.look", "Things to look at"),
        CutContentTier.All => Text.Say("general.restored.all", "Everything, puzzles included"),
        CutContentTier.Reconstructed =>
            Text.Say("general.restored.rebuilt", "And objects rebuilt from scratch"),
        _ => Text.Say("value.off", "Off"),
    };

    private string Describe(PictureQuality quality) => quality switch
    {
        PictureQuality.Original => Text.Say("picture.lighting.original", "As it was"),
        PictureQuality.Improved => Text.Say("picture.lighting.improved", "Shadows"),
        PictureQuality.High => Text.Say("picture.lighting.high", "Shadows and shading"),
        _ => Text.Say("picture.lighting.everything", "Everything"),
    };

    private string Describe(WindowMode mode) => mode switch
    {
        WindowMode.BorderlessFullscreen =>
            Text.Say("display.window.borderless", "Borderless, filling the monitor"),
        WindowMode.ExclusiveFullscreen => Text.Say("display.window.full", "Fullscreen"),
        _ => Text.Say("display.window.windowed", "A window"),
    };

    // The two vendors' names are names. What is translated is the third row, which is a
    // description of what the game itself does.
    private string Describe(UpscalerKind kind) => kind switch
    {
        UpscalerKind.Spatial => Text.Say("picture.upscaler.builtin", "Built in"),
        UpscalerKind.Fsr => "FSR (AMD)",
        UpscalerKind.Dlss => "DLSS (NVIDIA)",
        _ => Text.Say("value.off", "Off"),
    };

    private string Describe(UpscalerQuality quality) => quality switch
    {
        UpscalerQuality.Native => Text.Say("picture.ratio.native", "Native (anti-aliasing only)"),
        UpscalerQuality.UltraQuality => Text.Say("picture.ratio.ultraquality", "Ultra quality"),
        UpscalerQuality.Quality => Text.Say("picture.ratio.quality", "Quality"),
        UpscalerQuality.Balanced => Text.Say("picture.ratio.balanced", "Balanced"),
        UpscalerQuality.Performance => Text.Say("picture.ratio.performance", "Performance"),
        _ => Text.Say("picture.ratio.ultraperformance", "Ultra performance"),
    };

    /// <summary>The chosen graphics API, and whether it is the one drawing.</summary>
    private string DescribeBackend()
    {
        RenderBackend chosen = RenderBackends.Resolve(Settings.Backend);

        string name = Settings.Backend == RenderBackend.Automatic
            ? Text.Say("display.backend.automatic", "Automatic ({0})", Describe(chosen))
            : Describe(chosen);

        return RunningBackend != RenderBackend.Automatic && RunningBackend != chosen
            ? name + Text.Say("display.backend.nextstart", ", next start")
            : name;
    }

    private static string Describe(RenderBackend backend) => backend switch
    {
        RenderBackend.Direct3D12 => "Direct3D 12",
        _ => "Vulkan",
    };

    private string Describe(LatencyMode latency) => latency switch
    {
        LatencyMode.On => Text.Say("value.on", "On"),
        LatencyMode.Boost => Text.Say("picture.latency.boost", "On + boost"),
        _ => Text.Say("value.off", "Off"),
    };

    // HDR10 and scRGB are the colour spaces' own names and stay as they are.
    private string Describe(HdrTransfer transfer) => transfer switch
    {
        HdrTransfer.PerceptualQuantiser => "HDR10",
        HdrTransfer.ExtendedLinear => "scRGB",
        _ => Text.Say("display.transfer.either", "Whichever the display prefers"),
    };

    private string Describe(ToneMapping curve) => curve switch
    {
        ToneMapping.Reinhard => Text.Say("display.tonemap.rolled", "Rolled off"),
        ToneMapping.Filmic => Text.Say("display.tonemap.filmic", "Filmic"),
        _ => Text.Say("display.tonemap.clipped", "Clipped, as it was"),
    };

    private string Describe(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Headphones => Text.Say("sound.speakers.headphones", "Headphones"),
        SpeakerLayout.Stereo21 => Text.Say("sound.speakers.stereo21", "Stereo and a subwoofer"),
        SpeakerLayout.Surround51 => Text.Say("sound.speakers.surround", "Surround, 5.1"),
        _ => Text.Say("sound.speakers.stereo", "Stereo"),
    };
}

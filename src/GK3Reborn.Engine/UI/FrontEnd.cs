using System.Globalization;
using GK3Reborn.Audio;
using GK3Reborn.Game;

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

    /// <summary>How loud everything is.</summary>
    Audio,

    /// <summary>How the game plays.</summary>
    Gameplay,

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
        FrontEndPage.Audio => "Sound",
        FrontEndPage.Save => "Save Game",
        FrontEndPage.Load => "Restore Game",
        _ => "Playing",
    };

    /// <summary>The rows of the page showing.</summary>
    public IReadOnlyList<MenuItem> Items => Page switch
    {
        FrontEndPage.Main => Main(),
        FrontEndPage.Options => Options(),
        FrontEndPage.Video => Video(),
        FrontEndPage.Audio => Audio(),
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

            case "quit":
                return FrontEndOutcome.Quit;

            case "options":
                Page = FrontEndPage.Options;
                return FrontEndOutcome.Stay;

            case "video":
                Page = FrontEndPage.Video;
                return FrontEndOutcome.Stay;

            case "audio":
                Page = FrontEndPage.Audio;
                return FrontEndOutcome.Stay;

            case "gameplay":
                Page = FrontEndPage.Gameplay;
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
            FrontEndPage.Video or FrontEndPage.Audio or FrontEndPage.Gameplay =>
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
        return Settings.Save(path);
    }

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

    private static IReadOnlyList<MenuItem> Options() =>
    [
        MenuItem.Button("video", "Picture"),
        MenuItem.Button("audio", "Sound"),
        MenuItem.Button("gameplay", "Playing"),
        MenuItem.Button("back", "Back"),
    ];

    private IReadOnlyList<MenuItem> Video() =>
    [
        MenuItem.Choice("picture", "Lighting", Describe(Settings.Picture)),

        // Each explanation directly under the row it explains. A page whose notes are
        // collected at the bottom makes the reader work out which belongs to which.
        MenuItem.Label(Explain(Settings.Picture)),
        MenuItem.Toggle("enhanced", "Higher-resolution textures", Settings.EnhancedTextures),
        MenuItem.Toggle("trees", "Modelled trees", Settings.ModelledTrees),
        MenuItem.Toggle("terrain", "Reconstructed horizon", Settings.TerrainBackdrop),

        // The room standing round the player was built from whichever set was chosen when
        // it loaded. Rebuilding it here would mean reloading the scene underneath them.
        MenuItem.Label("Textures, trees and horizon change as you go through the next door."),
        MenuItem.Button("back", "Back"),
    ];

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
        // setting is broken.
        MenuItem.Label("Speakers take effect the next time the game starts."),
        MenuItem.Button("back", "Back"),
    ];

    private IReadOnlyList<MenuItem> Gameplay() =>
    [
        MenuItem.Slider(
            "hurry",
            "Hurrying pace",
            (Settings.HurryFactor - 1f) / 3f,
            string.Create(CultureInfo.InvariantCulture, $"{Settings.HurryFactor:F1}x")),

        MenuItem.Label("How much faster a double-click sends Gabriel."),
        MenuItem.Toggle("glide", "Camera travels between angles", Settings.CameraGlide),
        MenuItem.Toggle("cinematics", "Let the story move the camera", Settings.Cinematics),
        MenuItem.Toggle("captions", "Write out what is said", Settings.Captions),
        MenuItem.Toggle("intro", "Play the intro on starting", Settings.PlayIntro),
        MenuItem.Toggle("eggs", "Easter eggs", Settings.EasterEggs),

        // What it actually does, because "easter eggs" on its own could mean anything. The
        // switch is the game's own: EGG is a case an action file may be written against,
        // and the original left it hard-coded off.
        MenuItem.Label("Lets the game show the jokes its authors left switched off."),
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

            "enhanced" => Settings with { EnhancedTextures = !Settings.EnhancedTextures },
            "trees" => Settings with { ModelledTrees = !Settings.ModelledTrees },
            "terrain" => Settings with { TerrainBackdrop = !Settings.TerrainBackdrop },
            "glide" => Settings with { CameraGlide = !Settings.CameraGlide },
            "cinematics" => Settings with { Cinematics = !Settings.Cinematics },
            "captions" => Settings with { Captions = !Settings.Captions },
            "intro" => Settings with { PlayIntro = !Settings.PlayIntro },
            "eggs" => Settings with { EasterEggs = !Settings.EasterEggs },

            _ => Settings,
        };

        if (Settings != before)
        {
            Dirty = true;
        }
    }

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

    private static string Explain(PictureQuality quality) => quality switch
    {
        PictureQuality.Original => "The 1999 picture: the light the artists baked, and no rays.",
        PictureQuality.Improved => "Traced shadows, at the smallest ray budget.",
        PictureQuality.High => "Traced shadows, contact shading and reflections.",
        _ => "The same, with as many rays as the picture can use.",
    };

    private static string Describe(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Headphones => "Headphones",
        SpeakerLayout.Stereo21 => "Stereo and a subwoofer",
        SpeakerLayout.Surround51 => "Surround, 5.1",
        _ => "Stereo",
    };
}

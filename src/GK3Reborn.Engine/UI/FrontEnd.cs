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

    /// <summary>Leave the game.</summary>
    Quit,
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

    /// <summary>The heading for the page showing.</summary>
    public string Title => Page switch
    {
        FrontEndPage.Main => InGame ? "Paused" : "Gabriel Knight 3",
        FrontEndPage.Options => "Settings",
        FrontEndPage.Video => "Picture",
        FrontEndPage.Audio => "Sound",
        _ => "Playing",
    };

    /// <summary>The line along the bottom of the page showing.</summary>
    public string Footer => Page == FrontEndPage.Main
        ? "Arrows to move, Enter to choose"
        : "Left and right to change, Escape to go back";

    /// <summary>The rows of the page showing.</summary>
    public IReadOnlyList<MenuItem> Items => Page switch
    {
        FrontEndPage.Main => Main(),
        FrontEndPage.Options => Options(),
        FrontEndPage.Video => Video(),
        FrontEndPage.Audio => Audio(),
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

            case "back":
                Back();
                return FrontEndOutcome.Stay;

            default:
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

        Page = Page == FrontEndPage.Options ? FrontEndPage.Main : FrontEndPage.Options;
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

    private IReadOnlyList<MenuItem> Main() =>
    [
        InGame
            ? MenuItem.Button("resume", "Resume")
            : MenuItem.Button("play", "New Game"),

        // Drawn and disabled rather than hidden. Saving is not built, and a menu that
        // simply omits it leaves the player wondering where it went.
        MenuItem.Button("load", "Restore Game", enabled: false),

        MenuItem.Button("options", "Settings"),
        MenuItem.Button("quit", InGame ? "Leave the Game" : "Quit"),
    ];

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

        // The room standing round the player was built from whichever set was chosen when
        // it loaded. Rebuilding it here would mean reloading the scene underneath them.
        MenuItem.Label("Textures change as you go through the next door."),
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
            "glide" => Settings with { CameraGlide = !Settings.CameraGlide },
            "cinematics" => Settings with { Cinematics = !Settings.Cinematics },
            "captions" => Settings with { Captions = !Settings.Captions },
            "intro" => Settings with { PlayIntro = !Settings.PlayIntro },

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

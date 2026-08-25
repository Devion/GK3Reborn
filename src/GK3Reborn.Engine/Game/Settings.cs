using System.Text.Json;
using System.Text.Json.Serialization;
using GK3Reborn.Audio;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>How much of the picture the player wants paid for.</summary>
/// <remarks>
/// Separate from <see cref="RayTracingQuality"/> so the menu can offer one word — the
/// quality ladder is a rendering decision and this is a preference about it.
/// </remarks>
public enum PictureQuality
{
    /// <summary>The 1999 picture: baked light, no rays.</summary>
    Original,

    /// <summary>Shadows, cheaply.</summary>
    Improved,

    /// <summary>Shadows and occlusion.</summary>
    High,

    /// <summary>Everything, at the highest ray budget.</summary>
    Highest,
}

/// <summary>
/// What the player has chosen, and where it is kept.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these has somewhere real to go. The audio levels are the mixer's buses,
/// which nothing had ever set; the picture quality is the ray-tracing ladder; the walking
/// pace is what a double-click multiplies. A setting with no destination is a promise the
/// interface cannot keep, so there are none here.
/// </para>
/// <para>
/// Kept in the user's own profile rather than beside the executable. A game directory may
/// be read-only, shared between accounts, or replaced wholesale by an update, and none of
/// those should cost somebody their volume levels.
/// </para>
/// <para>
/// <b>Everything is clamped on the way in.</b> A settings file is a text file somebody may
/// edit, and a hand-typed volume of forty is not a reason to fail to start.
/// </para>
/// </remarks>
public sealed record Settings
{
    /// <summary>How loud everything is, over the top of the rest.</summary>
    public float MasterVolume { get; init; } = 1f;

    /// <summary>Music and the cutscenes' own soundtrack.</summary>
    public float MusicVolume { get; init; } = 1f;

    /// <summary>What a room sounds like when nothing is happening in it.</summary>
    public float AmbienceVolume { get; init; } = 1f;

    /// <summary>Doors, footsteps, everything that happens once.</summary>
    public float EffectsVolume { get; init; } = 1f;

    /// <summary>Speech.</summary>
    public float DialogueVolume { get; init; } = 1f;

    /// <summary>What the sound is being played through.</summary>
    public SpeakerLayout Speakers { get; init; } = SpeakerLayout.Stereo;

    /// <summary>How much of the picture to pay for.</summary>
    public PictureQuality Picture { get; init; } = PictureQuality.High;

    /// <summary>Whether to use the higher-resolution textures where they exist.</summary>
    public bool EnhancedTextures { get; init; } = true;

    /// <summary>
    /// How many times a character's head is subdivided, or zero to draw it as authored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the head. A character's body deforms and is addressed by vertex index, so it
    /// cannot be refined without invalidating that character's clips; the head is rigid and
    /// can. See <see cref="Actors.HeadRefinement"/>.
    /// </para>
    /// <para>
    /// Two by default, which is where a twenty-triangle hairdo stops reading as a polygon.
    /// Zero is a real answer for somebody who wants the 1999 outline.
    /// </para>
    /// </remarks>
    public int SmoothHeads { get; init; } = 2;

    /// <summary>Whether the camera travels between angles or cuts.</summary>
    public bool CameraGlide { get; init; } = true;

    /// <summary>Whether the story is allowed to move the camera for effect.</summary>
    public bool Cinematics { get; init; } = true;

    /// <summary>Whether what is said is also written.</summary>
    public bool Captions { get; init; } = true;

    /// <summary>
    /// How much faster a double-click sends Gabriel.
    /// </summary>
    /// <remarks>
    /// The stride is played faster by the same amount, or the feet slide. One means a
    /// double-click does nothing, which is a legitimate answer for somebody who wants the
    /// pace the game was authored at.
    /// </remarks>
    public float HurryFactor { get; init; } = 2f;

    /// <summary>Whether the intro plays on starting.</summary>
    public bool PlayIntro { get; init; } = true;

    /// <summary>
    /// Whether the game's easter-egg content is switched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EGG</c> is one of the built-in cases an action file may be written against, and
    /// the original hard-codes it false with a note saying it should return true when
    /// easter eggs are enabled — the switch itself never shipped, so nothing behind the
    /// case has ever been reachable in a playing game. This is that switch. It sets the
    /// story's <c>EGG</c> flag, which is what the case reads and what Sidney's sixth email
    /// is written against.
    /// </para>
    /// <para>
    /// Off by default, because the game as it shipped is the game as it shipped, and
    /// somebody playing GK3 for the first time should meet it that way.
    /// </para>
    /// </remarks>
    public bool EasterEggs { get; init; }

    /// <summary>Where the settings live for this user.</summary>
    /// <remarks>
    /// <c>%AppData%\GK3Reborn\settings.json</c> on Windows and
    /// <c>~/.config/GK3Reborn/settings.json</c> on Linux, which is what
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> gives on each.
    /// </remarks>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        "GK3Reborn",
        "settings.json");

    /// <summary>The ray-tracing level this picture quality asks for.</summary>
    public RayTracingQuality Quality => Picture switch
    {
        PictureQuality.Original => RayTracingQuality.None,
        PictureQuality.Improved => RayTracingQuality.Low,
        PictureQuality.High => RayTracingQuality.Medium,
        _ => RayTracingQuality.High,
    };

    /// <summary>Reads the settings, or returns the defaults.</summary>
    /// <param name="path">Where to read from, or null for this user's own.</param>
    /// <returns>The settings; never null and never out of range.</returns>
    /// <remarks>
    /// A missing file is the ordinary case — it is what a first run looks like — and an
    /// unreadable one is treated the same way. Refusing to start because a preferences file
    /// has a stray comma in it would be the worst possible trade.
    /// </remarks>
    public static Settings Load(string? path = null)
    {
        string file = path ?? DefaultPath;

        try
        {
            if (!File.Exists(file))
            {
                return new Settings();
            }

            return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json)
                    ?? new Settings())
                .Sane();
        }
        catch (Exception error) when (error is IOException
                                          or JsonException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return new Settings();
        }
    }

    /// <summary>Writes the settings.</summary>
    /// <param name="path">Where to write, or null for this user's own.</param>
    /// <returns>True when they were written.</returns>
    /// <remarks>
    /// Failure is reported rather than thrown. Somebody with a read-only profile should
    /// still be able to turn the music down for this session.
    /// </remarks>
    public bool Save(string? path = null)
    {
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
            File.WriteAllText(file, JsonSerializer.Serialize(Sane(), Json));
            return true;
        }
        catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The same settings with every value inside its range.</summary>
    public Settings Sane() => this with
    {
        MasterVolume = Level(MasterVolume),
        MusicVolume = Level(MusicVolume),
        AmbienceVolume = Level(AmbienceVolume),
        EffectsVolume = Level(EffectsVolume),
        DialogueVolume = Level(DialogueVolume),
        Speakers = Enum.IsDefined(Speakers) ? Speakers : SpeakerLayout.Stereo,
        Picture = Enum.IsDefined(Picture) ? Picture : PictureQuality.High,
        HurryFactor = float.IsFinite(HurryFactor) ? Math.Clamp(HurryFactor, 1f, 4f) : 2f,
        SmoothHeads = Math.Clamp(SmoothHeads, 0, Actors.HeadRefinement.MaximumLevels),
    };

    /// <summary>Hands the audio levels to the mixer.</summary>
    /// <param name="audio">The device, or null when there is none.</param>
    /// <remarks>
    /// <para>
    /// Called whenever a level changes as well as at startup, so a slider is heard while it
    /// is being dragged rather than after the menu is closed.
    /// </para>
    /// <para>
    /// Every bus is set, not only the ones something plays on today. There are nine buses
    /// and five sliders; a bus left out is a sound the player cannot turn down, and which
    /// one that is would depend on which of two near-identical names the code that plays it
    /// happened to pick. Speech is a case in point: it is played on
    /// <see cref="AudioBus.DialogueCentered"/> and not on <see cref="AudioBus.DialogueInWorld"/>.
    /// </para>
    /// </remarks>
    public void ApplyTo(IAudioBackend? audio)
    {
        if (audio is null)
        {
            return;
        }

        audio.SetBusGain(AudioBus.Master, MasterVolume);

        audio.SetBusGain(AudioBus.Music, MusicVolume);
        audio.SetBusGain(AudioBus.Cinematics, MusicVolume);

        audio.SetBusGain(AudioBus.Ambience, AmbienceVolume);

        audio.SetBusGain(AudioBus.Effects, EffectsVolume);
        audio.SetBusGain(AudioBus.Foley, EffectsVolume);
        audio.SetBusGain(AudioBus.UserInterface, EffectsVolume);

        audio.SetBusGain(AudioBus.DialogueInWorld, DialogueVolume);
        audio.SetBusGain(AudioBus.DialogueCentered, DialogueVolume);
    }

    private static float Level(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;

    private static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

using System.Numerics;
using GK3Reborn.Formats.Audio;

namespace GK3Reborn.Audio;

/// <summary>Mixer buses. See Plan/03-gameplay-ui-audio.md section 7.1.</summary>
public enum AudioBus
{
    /// <summary>Final output bus.</summary>
    Master,

    /// <summary>Score and licensed music.</summary>
    Music,

    /// <summary>Room tone and environmental beds.</summary>
    Ambience,

    /// <summary>General sound effects.</summary>
    Effects,

    /// <summary>Footsteps and foley.</summary>
    Foley,

    /// <summary>Dialogue placed in the world.</summary>
    DialogueInWorld,

    /// <summary>Dialogue routed to center.</summary>
    DialogueCentered,

    /// <summary>Cinematic audio.</summary>
    Cinematics,

    /// <summary>User-interface sounds.</summary>
    UserInterface,
}

/// <summary>
/// The audio device abstraction. Implemented over OpenAL Soft; kept behind an
/// interface so the spatializer can be swapped without touching game code.
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>The layout that was requested by the player.</summary>
    SpeakerLayout RequestedLayout { get; }

    /// <summary>
    /// The layout the device actually opened with.
    /// </summary>
    /// <remarks>
    /// Plan/03 section 7.2: never assume the device honored the request. Query the
    /// endpoint, log the difference, and fall back visibly.
    /// </remarks>
    SpeakerLayout ActualLayout { get; }

    /// <summary>Sets the linear gain of a bus.</summary>
    void SetBusGain(AudioBus bus, float gain);

    /// <summary>Sets how loud one voice is, on top of its bus.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="gain">Its own level, from zero to one.</param>
    /// <remarks>
    /// A bus is a setting and this is a moment: what it exists for is crossfading one
    /// room's music into the next, which needs two voices at different levels on the same
    /// bus at the same time. A voice that has finished is ignored rather than refused.
    /// </remarks>
    void SetVoiceGain(AudioVoice voice, float gain);

    /// <summary>Starts a sound.</summary>
    /// <param name="sound">The decoded sound.</param>
    /// <param name="bus">Which bus it is mixed on.</param>
    /// <param name="repeat">Whether it repeats until stopped.</param>
    /// <param name="at">
    /// Where it is coming from, or null to play it at the listener's head. A voice-over and
    /// a menu click belong at the head; a fountain does not.
    /// </param>
    /// <returns>A handle, or <see cref="AudioVoice.None"/> when nothing could play it.</returns>
    AudioVoice Play(WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null);

    /// <summary>Moves a sound that is already playing.</summary>
    /// <param name="voice">The handle.</param>
    /// <param name="position">Where it is now.</param>
    /// <remarks>For an emitter that follows something — a car going past, a person walking.</remarks>
    void Move(AudioVoice voice, Vector3 position);

    /// <summary>Puts the listener where the player is.</summary>
    /// <param name="position">Where they are.</param>
    /// <param name="forward">Which way they are looking.</param>
    /// <param name="up">Which way is up for them.</param>
    /// <remarks>
    /// Called once a frame from the camera. Without it every sound is at the origin facing
    /// nowhere, which is a room where the far fountain is as loud as the near one.
    /// </remarks>
    void Listen(Vector3 position, Vector3 forward, Vector3 up);

    /// <summary>Stops a sound.</summary>
    /// <param name="voice">The handle <see cref="Play"/> returned.</param>
    void Silence(AudioVoice voice);

    /// <summary>Stops everything on a bus.</summary>
    /// <param name="bus">The bus.</param>
    void StopBus(AudioBus bus);

    /// <summary>Whether a sound is still going.</summary>
    /// <param name="voice">The handle.</param>
    /// <returns>True while it plays.</returns>
    bool IsPlaying(AudioVoice voice);

    /// <summary>How many sounds are going at once.</summary>
    int Playing { get; }

    /// <summary>Reclaims whatever has finished. Called once a frame.</summary>
    void Update();
}

/// <summary>Where a sound is in the room, and how far it carries.</summary>
/// <param name="Position">Where it comes from, in world space.</param>
/// <param name="Minimum">
/// How near you can get before it stops getting louder, in scene units. The game's own
/// default is 200; a fountain is authored at 85 to 100.
/// </param>
/// <param name="Maximum">
/// How far it carries before it stops getting quieter. The game's default is 2000.
/// </param>
/// <remarks>
/// The two distances are the game's own, out of the <c>.STK</c> files, and they describe an
/// inverse rolloff clamped at both ends: full volume within <paramref name="Minimum"/>,
/// falling as the reciprocal of distance after that, and level again past
/// <paramref name="Maximum"/>.
/// </remarks>
public readonly record struct AudioPlacement(Vector3 Position, float Minimum, float Maximum)
{
    /// <summary>How near a sound has to be for full volume when nothing says.</summary>
    public const float DefaultMinimum = 200f;

    /// <summary>How far a sound carries when nothing says.</summary>
    public const float DefaultMaximum = 2000f;

    /// <summary>A placement with the game's own default distances.</summary>
    /// <param name="position">Where the sound is.</param>
    /// <returns>The placement.</returns>
    public static AudioPlacement At(Vector3 position) =>
        new(position, DefaultMinimum, DefaultMaximum);
}

/// <summary>A sound that is playing.</summary>
/// <param name="Id">Opaque handle; zero means nothing.</param>
public readonly record struct AudioVoice(int Id)
{
    /// <summary>No sound.</summary>
    public static AudioVoice None => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool Exists => Id != 0;
}

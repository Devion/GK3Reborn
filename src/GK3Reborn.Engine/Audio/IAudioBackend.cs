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

    /// <summary>Starts a sound.</summary>
    /// <param name="sound">The decoded sound.</param>
    /// <param name="bus">Which bus it is mixed on.</param>
    /// <param name="repeat">Whether it repeats until stopped.</param>
    /// <returns>A handle, or <see cref="AudioVoice.None"/> when nothing could play it.</returns>
    AudioVoice Play(WavFile sound, AudioBus bus, bool repeat = false);

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

/// <summary>A sound that is playing.</summary>
/// <param name="Id">Opaque handle; zero means nothing.</param>
public readonly record struct AudioVoice(int Id)
{
    /// <summary>No sound.</summary>
    public static AudioVoice None => default;

    /// <summary>Whether this refers to anything.</summary>
    public bool Exists => Id != 0;
}

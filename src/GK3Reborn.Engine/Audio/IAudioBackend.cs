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
}

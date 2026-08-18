namespace GK3Reborn.Audio;

/// <summary>Output speaker configurations the mixer supports.</summary>
public enum SpeakerLayout
{
    /// <summary>Binaural output for headphones, spatialized with an HRTF.</summary>
    Headphones,

    /// <summary>Two-channel stereo.</summary>
    Stereo,

    /// <summary>Stereo plus a subwoofer.</summary>
    Stereo21,

    /// <summary>Five full-range channels plus LFE.</summary>
    Surround51,

    /// <summary>Seven full-range channels plus LFE.</summary>
    Surround71,
}

/// <summary>How a spoken line is placed in the output field.</summary>
/// <remarks>
/// From the project brief: Gabriel's voice is always centered; other speakers in the
/// room are localized in 3D unless the player enables the centered-dialogue option.
/// Plan/03 section 7.2 adds that LFE is an endpoint and bass-management concern, so
/// dialogue is never routed into the LFE channel directly.
/// </remarks>
public enum DialogueRouting
{
    /// <summary>
    /// Routed to the center bus. On discrete 5.1/7.1 that means the physical center
    /// channel; on stereo or headphones it means a centered phantom source.
    /// </summary>
    Centered,

    /// <summary>Attached to a 3D emitter in the scene, with distance and occlusion.</summary>
    Spatialized,
}

/// <summary>Player-facing dialogue placement settings.</summary>
public sealed record DialogueRoutingOptions
{
    /// <summary>
    /// When true, every speaker is routed like Gabriel. Improves intelligibility and
    /// is listed as an accessibility option in Plan/03 section 8.
    /// </summary>
    public bool CenterAllDialogue { get; init; }

    /// <summary>Speaker ids that are always centered regardless of the option above.</summary>
    /// <remarks>Gabriel is the default member; see Plan/README.md item 6.</remarks>
    public IReadOnlySet<string> AlwaysCenteredSpeakers { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GABRIEL" };

    /// <summary>Decides how a line from <paramref name="speakerId"/> is placed.</summary>
    public DialogueRouting Resolve(string speakerId)
    {
        ArgumentNullException.ThrowIfNull(speakerId);
        return CenterAllDialogue || AlwaysCenteredSpeakers.Contains(speakerId)
            ? DialogueRouting.Centered
            : DialogueRouting.Spatialized;
    }
}

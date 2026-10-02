using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>Identifies one request for speech and the script or animation that started it.</summary>
/// <param name="Thread">The compiled script, if any.</param>
/// <param name="Animation">Playback whose dialogue node started these words, if any.</param>
public sealed class DialogueRun(SheepThread? Thread = null, SheepWaitWork? Animation = null)
{
    internal IReadOnlyList<DialogueRun> Preceding { get; set; } = [];

    internal bool Includes(DialogueRun run) => ReferenceEquals(this, run) || Preceding.Any(prior => prior.Includes(run));

    /// <summary>The script whose wait the words belong to.</summary>
    public SheepThread? Thread { get; } = Thread;

    /// <summary>The animation whose clock the words belong to.</summary>
    public SheepWaitWork? Animation { get; } = Animation;
}

/// <summary>Unplayed time removed from one run when the player skips a line or a chorus.</summary>
/// <param name="Run">Which request for speech it belongs to.</param>
/// <param name="Seconds">Time remaining in that line.</param>
/// <param name="LastLine">Whether no further lines remain in that run.</param>
public readonly record struct DialogueSkip(DialogueRun Run, double Seconds, bool LastLine);

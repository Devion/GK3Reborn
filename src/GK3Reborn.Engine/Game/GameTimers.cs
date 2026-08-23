using System.Globalization;

namespace GK3Reborn.Game;

/// <summary>An action waiting for its moment.</summary>
/// <param name="Noun">What it will be done to.</param>
/// <param name="Verb">What will be done.</param>
/// <param name="SecondsRemaining">How much longer.</param>
public readonly record struct GameTimer(string Noun, string Verb, double SecondsRemaining)
{
    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{Noun}:{Verb} in {SecondsRemaining:F2}s");
}

/// <summary>
/// Actions the story has asked for later.
/// </summary>
/// <remarks>
/// <para>
/// <c>SetGameTimer(noun, verb, ms)</c> is how a script arranges for something to happen
/// by itself: a phone that rings a minute after you walk in, a character who gives up
/// waiting. The action is an ordinary noun and verb, resolved and run when the time comes
/// exactly as a click would resolve and run it, which is why this holds a *name* rather
/// than a piece of work — the rule that applies is the one that applies then, not the one
/// that applied when the timer was set.
/// </para>
/// <para>
/// Timers outlive the room. The original keeps them in a global list and saves them,
/// because a minute set in the lobby has to still be counting in the hall, so they belong
/// to the story rather than to the scene.
/// </para>
/// <para>
/// Nothing here runs anything. <see cref="Advance"/> hands back what has come due and the
/// caller performs it, because performing needs a resolver and a runner and the story's
/// state has no business knowing about either.
/// </para>
/// </remarks>
public sealed class GameTimers
{
    private readonly List<GameTimer> _timers = [];

    /// <summary>What is waiting, in the order it was asked for.</summary>
    public IReadOnlyList<GameTimer> Pending => _timers;

    /// <summary>How many are waiting.</summary>
    public int Count => _timers.Count;

    /// <summary>Asks for an action to happen later.</summary>
    /// <param name="noun">What it will be done to.</param>
    /// <param name="verb">What will be done.</param>
    /// <param name="seconds">How long to wait.</param>
    /// <remarks>
    /// A wait of zero or less is kept at zero rather than thrown away, so the next time
    /// anything lets time pass — even by nothing at all — it comes due. The original fires
    /// such a timer there and then, which it can because setting one and running an action
    /// happen in the same place; here they do not, and waiting one step is closer to the
    /// truth than dropping it.
    /// </remarks>
    public void Set(string noun, string verb, double seconds)
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(verb);

        _timers.Add(new GameTimer(noun, verb, Math.Max(0, seconds)));
    }

    /// <summary>Lets time pass, and says what has come due.</summary>
    /// <param name="seconds">How much time.</param>
    /// <returns>The actions to perform, in the order they were asked for.</returns>
    /// <remarks>
    /// Everything due in one step comes back together rather than one per call, so a long
    /// step cannot lose a timer. The original will not fire one while another action is
    /// playing and lets it wait for the next tick; nothing here runs two things at once, so
    /// there is nothing yet for a timer to wait behind.
    /// </remarks>
    public IReadOnlyList<GameTimer> Advance(double seconds)
    {
        List<GameTimer> due = [];

        for (int i = _timers.Count - 1; i >= 0; i--)
        {
            GameTimer timer = _timers[i] with
            {
                SecondsRemaining = _timers[i].SecondsRemaining - Math.Max(0, seconds),
            };

            if (timer.SecondsRemaining <= 0)
            {
                _timers.RemoveAt(i);
                due.Add(timer);
            }
            else
            {
                _timers[i] = timer;
            }
        }

        // Walked backwards so removal is cheap; handed back forwards so the order two runs
        // fire in is the order the story asked for them.
        due.Reverse();
        return due;
    }

    /// <summary>Forgets everything waiting.</summary>
    public void Clear() => _timers.Clear();
}

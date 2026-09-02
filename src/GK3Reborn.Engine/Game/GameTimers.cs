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
/// Nothing here runs anything. <see cref="Advance"/> lets the clock move and
/// <see cref="TakeDue"/> hands back what has come due, one at a time, for the caller to
/// perform — because performing needs a resolver and a runner and the story's state has no
/// business knowing about either.
/// </para>
/// <para>
/// <b>Coming due and being performed are two things.</b> <c>GameTimers::Update</c> fires a
/// timer only <c>if(secondsRemaining &lt;= 0.0f &amp;&amp; !gActionManager.IsActionPlaying())</c>: one
/// that comes due in the middle of an action stays on the list and is offered again on the
/// next frame that finds the story free. That wait is not a nicety. CS3's attic sets a
/// timer for Montreaux climbing the stairs and then, when the player hides in the wardrobe,
/// runs a script that spends several seconds walking Grace across the room before it raises
/// the counts that make the timer's rule stop applying. Fire the timer in the middle of that
/// walk and its rule still applies, so Montreaux's arrival plays once from the timer and
/// again from the wardrobe, and every line after it is heard twice.
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

    /// <summary>Lets time pass.</summary>
    /// <param name="seconds">How much time.</param>
    /// <remarks>
    /// Every timer counts down, whatever else the story is doing, because the original's
    /// does: an action playing delays a timer being <em>performed</em> and never delays it
    /// coming due. What has run out is held at zero rather than going further negative, so
    /// that a timer waiting behind a long action is the same piece of state however many
    /// frames it waited — which is what the differential harness compares runs on.
    /// </remarks>
    public void Advance(double seconds)
    {
        for (int i = 0; i < _timers.Count; i++)
        {
            _timers[i] = _timers[i] with
            {
                SecondsRemaining = Math.Max(
                    0, _timers[i].SecondsRemaining - Math.Max(0, seconds)),
            };
        }
    }

    /// <summary>Takes the next action that has come due, if the caller can perform one.</summary>
    /// <returns>The action to perform, or null when nothing has come due.</returns>
    /// <remarks>
    /// One at a time, because performing one is the story becoming busy and a caller that
    /// took them all at once could not notice. The oldest first, so that two timers set in
    /// the same breath fire in the order the story asked for them.
    /// </remarks>
    public GameTimer? TakeDue()
    {
        for (int i = 0; i < _timers.Count; i++)
        {
            if (_timers[i].SecondsRemaining > 0)
            {
                continue;
            }

            GameTimer due = _timers[i];
            _timers.RemoveAt(i);
            return due;
        }

        return null;
    }

    /// <summary>Forgets everything waiting.</summary>
    public void Clear() => _timers.Clear();
}

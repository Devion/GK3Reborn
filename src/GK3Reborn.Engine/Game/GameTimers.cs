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
    public void Set(string noun, string verb, double seconds)
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(verb);

        _timers.Add(new GameTimer(noun, verb, Math.Max(0, seconds)));
    }

    /// <summary>Lets time pass.</summary>
    /// <param name="seconds">How much time.</param>
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

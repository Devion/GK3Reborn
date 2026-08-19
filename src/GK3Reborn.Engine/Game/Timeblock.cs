using System.Globalization;

namespace GK3Reborn.Game;

/// <summary>
/// A day-and-hour block of the story, the game's coarsest progression unit.
/// </summary>
/// <remarks>
/// GK3 runs over three days divided into timeblocks such as "110A" (day 1, 10 AM).
/// Location availability, actor schedules, dialogue and score all key off the current
/// block, so it is a first-class saved value rather than a derived one.
/// </remarks>
public readonly record struct Timeblock(int Day, int Hour, bool IsAfternoon) : IComparable<Timeblock>
{
    /// <summary>Parses the original "1 10A" style code, e.g. <c>110A</c>.</summary>
    /// <param name="code">The code to parse.</param>
    /// <param name="timeblock">Receives the parsed value.</param>
    /// <returns>True when the code was well formed.</returns>
    public static bool TryParse(string? code, out Timeblock timeblock)
    {
        timeblock = default;
        if (string.IsNullOrWhiteSpace(code) || code.Length < 3)
        {
            return false;
        }

        ReadOnlySpan<char> span = code.AsSpan().Trim();
        char meridiem = char.ToUpperInvariant(span[^1]);
        if (meridiem is not ('A' or 'P'))
        {
            return false;
        }

        if (!int.TryParse(span[..1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day) ||
            !int.TryParse(span[1..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour))
        {
            return false;
        }

        timeblock = new Timeblock(day, hour, meridiem == 'P');
        return true;
    }

    /// <summary>Renders the original code form.</summary>
    /// <remarks>
    /// The hour is two digits, always: the codes are four characters and the game writes
    /// <c>102P</c> rather than <c>12P</c>. Getting this wrong is quiet and total — scene
    /// files and scripts ask <c>IsCurrentTime("202p")</c>, which compares against this
    /// string, so an unpadded hour makes every such condition false and a scene loads in
    /// whichever state its unconditional block happens to describe.
    /// </remarks>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Day}{Hour:00}{(IsAfternoon ? 'P' : 'A')}");

    /// <summary>Orders timeblocks chronologically.</summary>
    public int CompareTo(Timeblock other)
    {
        int byDay = Day.CompareTo(other.Day);
        if (byDay != 0)
        {
            return byDay;
        }

        int self = (IsAfternoon && Hour != 12 ? Hour + 12 : Hour) % 24;
        int them = (other.IsAfternoon && other.Hour != 12 ? other.Hour + 12 : other.Hour) % 24;
        return self.CompareTo(them);
    }

    /// <summary>Less-than comparison.</summary>
    public static bool operator <(Timeblock left, Timeblock right) => left.CompareTo(right) < 0;

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(Timeblock left, Timeblock right) => left.CompareTo(right) > 0;

    /// <summary>Less-than-or-equal comparison.</summary>
    public static bool operator <=(Timeblock left, Timeblock right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-than-or-equal comparison.</summary>
    public static bool operator >=(Timeblock left, Timeblock right) => left.CompareTo(right) >= 0;
}

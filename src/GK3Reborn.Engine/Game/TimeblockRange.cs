namespace GK3Reborn.Game;

/// <summary>
/// The span of the story an asset applies to, as written in its own name.
/// </summary>
/// <remarks>
/// <para>
/// GK3 files say when they are for. A location's action files are
/// <c>R25_ALL.NVC</c>, <c>R25_1ALL.NVC</c>, <c>R25_23ALL.NVC</c> and
/// <c>R25202P.NVC</c>: every timeblock, all of day one, days two and three, and that
/// afternoon alone. The scene file lists all of them and the engine is expected to load
/// only the ones that apply, so the name is not decoration — it is the condition.
/// </para>
/// <para>
/// The grammar, from G-Engine's <c>Timeblock::ParseTimeblockRange</c>: three letters of
/// location, an optional underscore, then either <c>ALL</c> preceded by the digits of the
/// days it covers, or a timeblock code optionally followed by a second one giving the end
/// of a range — <c>HAL110A04P</c> runs from day one at ten in the morning to four that
/// afternoon, the end borrowing its day from the start.
/// </para>
/// <para>
/// A name that does not parse covers nothing, which is the original's behaviour and the
/// safe direction: loading an action file at the wrong point in the story puts verbs on
/// objects that should not have them yet.
/// </para>
/// </remarks>
/// <param name="Start">First timeblock it applies to.</param>
/// <param name="End">Last timeblock it applies to.</param>
public readonly record struct TimeblockRange(Timeblock Start, Timeblock End)
{
    /// <summary>The shortest name that can carry a range: three letters and a code.</summary>
    private const int Shortest = 7;

    /// <summary>Whether a point in the story falls inside.</summary>
    /// <param name="at">The timeblock.</param>
    /// <returns>True when the asset applies then.</returns>
    public bool Covers(Timeblock at) => Start <= at && at <= End;

    /// <summary>Reads the range out of an asset's name.</summary>
    /// <param name="name">The name, with or without an extension.</param>
    /// <param name="range">Receives the range.</param>
    /// <returns>False when the name says nothing about when it applies.</returns>
    public static bool TryParse(string? name, out TimeblockRange range)
    {
        range = default;

        if (name is null)
        {
            return false;
        }

        string text = name.ToUpperInvariant();
        int dot = text.IndexOf('.', StringComparison.Ordinal);

        if (dot >= 0)
        {
            text = text[..dot];
        }

        if (text.Length < Shortest)
        {
            return false;
        }

        // Three letters of location, and an underscore if the name uses one.
        int at = text[3] == '_' ? 4 : 3;
        int all = text.IndexOf("ALL", at, StringComparison.Ordinal);

        return all >= 0 ? ForDays(text, at, all, out range) : ForTimeblocks(text, at, out range);
    }

    /// <summary>Whether an asset applies at a point in the story.</summary>
    /// <param name="name">The asset's name.</param>
    /// <param name="at">The timeblock.</param>
    /// <returns>True when the name parses and covers that timeblock.</returns>
    public static bool Applies(string? name, Timeblock at) =>
        TryParse(name, out TimeblockRange range) && range.Covers(at);

    /// <summary>Reads the <c>ALL</c> forms: every day, or the days whose digits are given.</summary>
    private static bool ForDays(string text, int at, int all, out TimeblockRange range)
    {
        if (all == at)
        {
            range = new TimeblockRange(Timeblock.StartOfDay(1), Timeblock.EndOfDay(9));
            return true;
        }

        int first = int.MaxValue;
        int last = int.MinValue;

        for (int i = at; i < all; i++)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                int day = text[i] - '0';
                first = Math.Min(first, day);
                last = Math.Max(last, day);
            }
        }

        if (first == int.MaxValue)
        {
            range = default;
            return false;
        }

        range = new TimeblockRange(Timeblock.StartOfDay(first), Timeblock.EndOfDay(last));
        return true;
    }

    /// <summary>Reads one timeblock code, or two making a range.</summary>
    private static bool ForTimeblocks(string text, int at, out TimeblockRange range)
    {
        range = default;

        if (at + 4 > text.Length || !Timeblock.TryParse(text[at..(at + 4)], out Timeblock start))
        {
            return false;
        }

        string rest = text[(at + 4)..];

        // A three-character tail is the end of a range with its day left off, because a
        // range never spans two days: HAL110A04P is day one, ten in the morning until four.
        if (rest.Length == 3 && Timeblock.TryParse(text[at] + rest, out Timeblock end))
        {
            range = new TimeblockRange(start, end);
            return true;
        }

        range = new TimeblockRange(start, start);
        return rest.Length == 0;
    }
}

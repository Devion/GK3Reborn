// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>One word the parser found in the letters.</summary>
/// <param name="Index">Which of the list's words, from one, as the text file numbers them.</param>
/// <param name="Latin">The word itself.</param>
/// <param name="English">What it means, which is the only part of the row that is translated.</param>
public sealed record AnagramWord(int Index, string Latin, string English);

/// <summary>
/// Sidney's anagram parser, working on the Arcadia inscription.
///
/// <para>The one puzzle in the game it is for: Poussin's tomb reads ET IN ARCADIA EGO, the
/// translate screen adds the missing SUM, and these seventeen letters rearrange into TANGO
/// ARCAM DEI IESU — "I touch the tomb of God, Jesus". The parser offers every Latin word
/// that can be spelt from the letters still unused; choosing three that use all of them but
/// the last four is the answer, and IESU is the word it supplies itself.</para>
///
/// <para>None of this is in the game's data beyond the word list. Which three are right is
/// held in the retail engine as three positions in that list, and is read here the same
/// way: the Latin is the same in every release but the gloss beside it is translated, so
/// the row is the only thing that means anything in all eight.</para>
/// </summary>
public sealed class SidneyAnagram
{
    /// <summary>What the screen calls the section these strings live in.</summary>
    public const string Section = "Analyze Screen";

    /// <summary>
    /// The three rows of the full word list that solve it: <c>Arcam</c>, <c>Dei</c> and
    /// <c>Tango</c>, in whatever order the player picks them.
    /// </summary>
    private static readonly int[] Answer = [13, 41, 147];

    /// <summary>
    /// How far up the numbered keys to look. The full list runs to 161 and the short one to
    /// 36 in every release; a few spare is cheaper than a word silently lost.
    /// </summary>
    private const int Rows = 200;

    private readonly List<AnagramWord> _chosen = [];

    private SidneyAnagram(
        string phrase, string letters, IReadOnlyList<AnagramWord> words, AnagramWord? last)
    {
        Phrase = phrase;
        Letters = letters;
        Words = words;
        Last = last;
    }

    /// <summary>The inscription being parsed, spaces and all.</summary>
    public string Phrase { get; }

    /// <summary>Its letters, without the spaces and in capitals.</summary>
    public string Letters { get; }

    /// <summary>Every word the parser found, whether or not it can still be spelt.</summary>
    public IReadOnlyList<AnagramWord> Words { get; }

    /// <summary>The word the parser supplies once the other three are right.</summary>
    public AnagramWord? Last { get; }

    /// <summary>What the player has moved into the phrase, in the order they did it.</summary>
    public IReadOnlyList<AnagramWord> Chosen => _chosen;

    /// <summary>The letters nothing chosen has used yet.</summary>
    public string Remaining => Left(_chosen);

    /// <summary>
    /// The words that can still be spelt from what is left, which is what the screen lists.
    /// </summary>
    public IReadOnlyList<AnagramWord> Available
    {
        get
        {
            string left = Remaining;

            return [.. Words.Where(w => !_chosen.Contains(w) && Spells(left, w.Latin))];
        }
    }

    /// <summary>Whether the three chosen words are the ones that solve it.</summary>
    public bool Solved =>
        _chosen.Count == Answer.Length && Answer.All(i => _chosen.Any(w => w.Index == i));

    /// <summary>
    /// Whether the player has run out of words without solving it, which is the point the
    /// original says so and offers to start again.
    /// </summary>
    public bool Stuck => !Solved && _chosen.Count > 0 && Available.Count == 0;

    /// <summary>
    /// Opens the parser over a phrase.
    /// </summary>
    /// <param name="library">The game's own Sidney text.</param>
    /// <param name="complete">
    /// Whether the translate screen has supplied the missing SUM. Without it the phrase is
    /// four letters shorter, the parser finds a different and much shorter list of words,
    /// and — as in the original — none of them can finish it.
    /// </param>
    /// <returns>The parser, or null when the text file has no word list.</returns>
    public static SidneyAnagram? Open(SidneyLibrary library, bool complete)
    {
        ArgumentNullException.ThrowIfNull(library);

        string phrase = library.Say(complete ? "ArcadiaText" : "ArcadiaText2", Section);

        if (phrase.Length == 0)
        {
            return null;
        }

        // Read row by row rather than as a run, and numbered by the key rather than by
        // position. Which three words solve it is three row numbers and nothing else, so a
        // release with a row missing must not shift every word after it by one: it would
        // change the answer without changing anything a reader could see.
        string prefix = complete ? "Word" : "WordB";
        List<AnagramWord> words = [];

        for (int number = 1; number <= Rows; number++)
        {
            if (Split(library.Say(prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture), Section))
                is { } row)
            {
                words.Add(new AnagramWord(number, row.Latin, row.English));
            }
        }

        if (words.Count == 0)
        {
            return null;
        }

        // The list the short phrase gets is keyed WordB and numbered from one again, so a
        // row number out of the full list would name a different word in it. Nothing is
        // lost by leaving the answer out: the original cannot be solved from that list
        // either, because two of the three words are not in it.
        AnagramWord? last = complete && Split(library.Say("FinalWord", Section)) is { } final
            ? new AnagramWord(0, final.Latin, final.English)
            : null;

        string letters = string.Concat(phrase.Where(char.IsLetter)).ToUpperInvariant();

        return new SidneyAnagram(phrase, letters, words, last);
    }

    /// <summary>
    /// Moves a word into the phrase.
    /// </summary>
    /// <param name="index">Its row in the list.</param>
    /// <returns>True when it was taken.</returns>
    public bool Choose(int index)
    {
        if (_chosen.Count >= Answer.Length ||
            Words.FirstOrDefault(w => w.Index == index) is not { } word ||
            _chosen.Contains(word) ||
            !Spells(Remaining, word.Latin))
        {
            return false;
        }

        _chosen.Add(word);

        return true;
    }

    /// <summary>Takes the last word back out again.</summary>
    /// <returns>True when there was one to take.</returns>
    public bool Erase()
    {
        if (_chosen.Count == 0)
        {
            return false;
        }

        _chosen.RemoveAt(_chosen.Count - 1);

        return true;
    }

    /// <summary>
    /// The finished phrase, once it is solved: the three words in the order they were
    /// chosen, and then the one the parser supplies.
    /// </summary>
    /// <returns>The words, or an empty list while it is unsolved.</returns>
    public IReadOnlyList<AnagramWord> Reading() =>
        Solved && Last is { } last ? [.. _chosen, last] : [];

    /// <summary>What is left of the letters once these words have taken theirs.</summary>
    private string Left(IEnumerable<AnagramWord> words)
    {
        List<char> left = [.. Letters];

        foreach (char c in words.SelectMany(w => w.Latin.ToUpperInvariant()))
        {
            left.Remove(c);
        }

        return new string([.. left]);
    }

    /// <summary>Whether a word can be spelt from a pool of letters.</summary>
    private static bool Spells(string pool, string word)
    {
        List<char> left = [.. pool];

        foreach (char c in word.ToUpperInvariant())
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (!left.Remove(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a row into the word and its gloss. The file writes them as one string —
    /// <c>Arcam (Tomb)</c> — and the bracket is the only reliable divider: several of the
    /// words and more of the glosses have spaces in them.
    /// </summary>
    /// <param name="row">The row as the file writes it.</param>
    /// <returns>The two halves, or null when the row is not in that shape.</returns>
    private static (string Latin, string English)? Split(string row)
    {
        int open = row.IndexOf('(', StringComparison.Ordinal);

        if (open <= 0)
        {
            return null;
        }

        string latin = row[..open].Trim();
        string english = row[(open + 1)..].Trim().TrimEnd(')').Trim();

        return latin.Length > 0 ? (latin, english) : null;
    }
}

using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using GK3Reborn.Sheep;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Walks the game from the first morning to the last night and asks whether it can be
/// finished.
/// </summary>
public sealed class StoryCheckStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public StoryCheckStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Runs the check.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="diagnostics">Receives what it finds.</param>
    /// <returns>True when the story can be finished.</returns>
    public bool Run(string sourceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Walkthrough guide = Walkthrough.Open();
        Quests quests = Quests.Open();
        ScoreEvents points = ScoreEvents.Open();

        bool ok = true;

        _log($"walkthrough: {guide.Steps.Count} steps across {guide.Timeblocks.Count} " +
             $"points in the story, worth {guide.Points}");

        if (!guide.Adds(out string? fault))
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3400", DiagnosticSeverity.Error,
                "The walkthrough's running totals no longer add up.",
                "Walkthrough.txt", null, "a total that follows", fault,
                "A step was read twice or missed; the parse and the file disagree."));

            ok = false;
        }

        _log($"journal: {quests.All.Count} objectives across {quests.Timeblocks.Count} " +
             $"points in the story");

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        HashSet<string> awarded = Awardable(archives);

        _log($"scripts: {awarded.Count} score events the shipped scripts can award, " +
             $"of {points.Count} the engine knows");

        _log(string.Empty);
        _log("point   objectives  events  unawardable");

        foreach (Timeblock timeblock in quests.Timeblocks)
        {
            IReadOnlyList<Quest> here = quests.Of(timeblock);

            List<string> named = [.. here.SelectMany(q => q.Scores).Distinct()];
            List<string> unknown = [.. named.Where(n => points.Worth(n) is null)];
            List<string> unreachable = [.. named.Where(n => !awarded.Contains(n))];

            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"{timeblock,-8}{here.Count,10}{named.Count,8}{unreachable.Count,13}"));

            foreach (string name in unknown)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R3401", DiagnosticSeverity.Error,
                    "The journal is measured by a score event the engine does not know.",
                    "Quests.txt", null, "a name in Scores.txt", name,
                    $"{timeblock}: the objective can never be completed."));

                ok = false;
            }

            foreach (string name in unreachable.Where(n => !unknown.Contains(n)))
            {
                bool byTheEngine = ByTheFingerprintKit(name);

                diagnostics.Add(new Diagnostic(
                    byTheEngine ? "GK3R3404" : "GK3R3402",
                    DiagnosticSeverity.Warning,
                    byTheEngine
                        ? "A score the fingerprint screen awards, which this engine has not built yet."
                        : "No shipped script awards a score event the journal is measured by.",
                    "Quests.txt", null, "a ChangeScore call naming it", name,
                    byTheEngine
                        ? $"{timeblock}: the original awards this from its own code rather than " +
                          "from data, so no script names it and none is missing. Until the " +
                          "screen exists the objective cannot complete. See known-issues."
                        : $"{timeblock}: either the objective is measured by the wrong event, " +
                          "or the action that awards it is not reachable and the story cannot " +
                          "move on."));
            }
        }

        _log(string.Empty);

        foreach (Timeblock timeblock in guide.Timeblocks.Except(quests.Timeblocks))
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3403", DiagnosticSeverity.Error,
                "A point in the story the walkthrough covers has no objectives.",
                "Quests.txt", null, "a section for it", timeblock.ToString(),
                "The journal would have nothing to say for that stretch of the game."));

            ok = false;
        }

        List<string> orphaned =
        [
            .. points.Names
                .Where(n => !awarded.Contains(n))
                .Where(n => ScoreEvents.TimeblockOf(n) is not null),
        ];

        _log($"{orphaned.Count} of the engine's {points.Count} score events are named by no " +
             "script. Most are the original's own dead entries; the ones the journal uses " +
             "are reported above.");

        ok &= Cards(archives, diagnostics);

        return ok;
    }

    /// <summary>
    /// Whether every part of the day can letter its own card.
    /// </summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="diagnostics">Receives what it finds.</param>
    /// <returns>True when every one of them can.</returns>
    private bool Cards(GameArchives archives, DiagnosticBag diagnostics)
    {
        List<string> blocks =
        [
            .. archives.Names(".SEQ")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is { Length: 5 } && n[0] is 'D' or 'd')
                .Select(n => n![1..].ToUpperInvariant())
                .Where(n => Timeblock.TryParse(n, out _))
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

        _log(string.Empty);
        _log("point   frames  seconds  lettering        at");

        bool ok = true;

        foreach (string block in blocks)
        {
            if (TimeblockCard.Read(archives, block) is not { } card)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R3458", DiagnosticSeverity.Warning,
                    "A part of the day cannot letter its own card.",
                    $"D{block}.SEQ", null, "frames cut from that card's painting", "neither",
                    $"{block}: the card names itself in the port's own face instead, which "
                    + "is legible but is not the original's animation."));

                _log($"{block,-8}{"-",6}{"-",9}  {"-",-15}  written out");

                ok = false;
                continue;
            }

            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"{block,-8}{card.Frames.Count,6}{card.Seconds,9:F2}  "
                + $"{$"{card.Width}x{card.Height}",-15}  {card.Left},{card.Top}"));
        }

        return ok;
    }

    /// <summary>
    /// Every score event the shipped scripts are able to award.
    /// </summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The names.</returns>
    private static HashSet<string> Awardable(GameArchives archives)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The fingerprint kit's own awards, which no script names because the original
        // awarded them from its screen's code. The engine now does the same from its table.
        found.UnionWith(FingerprintKit.Scores);

        foreach (string name in archives.Names(".SHP"))
        {
            if (archives.Read(name) is not { } bytes)
            {
                continue;
            }

            SheepScriptFile script;

            try
            {
                script = SheepScriptFile.Parse(bytes, name);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                // GK3.SHP is not a Sheep script at all despite its extension, and a corpus
                // has a right to hold a file that is not what its name says. Skipping it
                // costs nothing; refusing to check the story because of it costs everything.
                continue;
            }

            foreach (string constant in script.StringConstants.Values)
            {
                if (constant.StartsWith("e_", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(constant);
                }
            }
        }

        foreach (string name in archives.Names(".NVC"))
        {
            if (archives.ReadText(name) is not { } text)
            {
                continue;
            }

            NvcFile actions;

            try
            {
                actions = NvcFile.Parse(text, name, new DiagnosticBag());
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                continue;
            }

            foreach (NvcAction action in actions.Actions)
            {
                foreach (string token in Tokens(action.Script))
                {
                    found.Add(token);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a score is one the fingerprint screen awards rather than a script.
    /// </summary>
    private static bool ByTheFingerprintKit(string name) =>
        name.Contains("fingerprint_kit", StringComparison.OrdinalIgnoreCase);

    /// <summary>The score names in a line of Sheep source.</summary>
    private static IEnumerable<string> Tokens(string? script)
    {
        if (script is not { Length: > 0 } text)
        {
            yield break;
        }

        int at = 0;

        while ((at = text.IndexOf("e_", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = at;

            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }

            // A word beginning e_ and not merely a word with e_ inside it, which is what
            // "the_" and "one_" would otherwise offer.
            if (at == 0 || (!char.IsLetterOrDigit(text[at - 1]) && text[at - 1] != '_'))
            {
                yield return text[at..end];
            }

            at = Math.Max(end, at + 1);
        }
    }
}

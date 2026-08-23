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
/// <remarks>
/// <para>
/// The other sweep, <see cref="SceneCheckStage"/>, asks whether every room loads and whether
/// every function the scripts call exists. Both can be true of a game that cannot be
/// completed: a room loads perfectly well when the one action that lets the story move on is
/// missing from it.
/// </para>
/// <para>
/// This asks the other question, and the walkthrough is what makes it possible to ask. It is
/// a record of a game somebody finished, step by step, so every score event the journal's
/// objectives are measured by has to be one the shipped scripts can actually award. An event
/// no script names is an objective that can never complete, which is a player stuck with a
/// journal telling them to do something the game has no way of noticing.
/// </para>
/// <para>
/// Four questions, in order of how badly a "no" hurts:
/// </para>
/// <list type="number">
/// <item>Does the walkthrough still parse, and do its own running totals add up?</item>
/// <item>Is every score event the journal names a real one?</item>
/// <item>Can the shipped scripts award it? — the story-breaking one.</item>
/// <item>Does every point in the story have objectives, and do they add up to the game?</item>
/// </list>
/// </remarks>
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

        return ok;
    }

    /// <summary>
    /// Every score event the shipped scripts are able to award.
    /// </summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The names.</returns>
    /// <remarks>
    /// <para>
    /// Two places, because the game awards points from both. A compiled <c>.SHP</c> carries
    /// its score names in its string table; an action file carries a line of Sheep source per
    /// action, and 20 of the journal's events are awarded only from there — every fingerprint
    /// in the game among them. Reading the scripts alone reported those as unreachable, which
    /// would have been an alarming and entirely wrong answer.
    /// </para>
    /// <para>
    /// A score name only ever appears as the argument to <c>ChangeScore</c>, so a token
    /// beginning <c>e_</c> is one wherever it is found. Following the calls would be more
    /// precise and would also have to decide what "reachable" means in a language with no
    /// entry point.
    /// </para>
    /// </remarks>
    private static HashSet<string> Awardable(GameArchives archives)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
    /// <remarks>
    /// Thirteen of them, and the reason every fingerprint in the game came back as
    /// unreachable. The original hardcodes these in its own fingerprint screen the way it
    /// hardcodes the score table and the starting inventory — so no script names them, and
    /// nothing about the shipped data is wrong. What is missing is on this side.
    /// </remarks>
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

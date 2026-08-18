using System.Globalization;
using System.Text;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.UI.Interaction;

namespace GK3Reborn.Tools.Stages;

/// <summary>Totals from an action survey.</summary>
/// <param name="Files">Action files read.</param>
/// <param name="Actions">Rules across them.</param>
/// <param name="Nouns">Distinct nouns.</param>
/// <param name="Verbs">Distinct verbs.</param>
/// <param name="Cases">Named case conditions.</param>
/// <param name="CasesEvaluated">Conditions that evaluated without error.</param>
/// <param name="CasesFailed">Conditions the expression reader could not handle.</param>
/// <param name="UnreadableLines">Rule lines that could not be parsed.</param>
public readonly record struct ActionSurveySummary(
    int Files, int Actions, int Nouns, int Verbs, int Cases, int CasesEvaluated, int CasesFailed, int UnreadableLines);

/// <summary>
/// Reads every action file and exercises the resolver against it.
/// </summary>
/// <remarks>
/// Two things get tested at once. The file reader has to cope with 390 files of
/// hand-written content, and the expression reader has to evaluate every condition those
/// files define — which is the first real workout for the recursive-descent approach the
/// full Sheep compiler will use.
/// </remarks>
public sealed class ActionSurveyStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public ActionSurveyStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Surveys the action files.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The totals.</returns>
    public ActionSurveySummary Run(string sourceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var parseDiagnostics = new DiagnosticBag();
        List<NvcFile> files = [];

        foreach (FileInfo archiveFile in new DirectoryInfo(sourceDirectory)
                     .EnumerateFiles("*.brn")
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            using BarnArchive archive = BarnArchive.Open(archiveFile.FullName);

            foreach (BarnEntry entry in archive.Entries)
            {
                if (entry.IsPointer ||
                    !Path.GetExtension(entry.Name).Equals(".NVC", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    byte[] data = archive.Extract(entry);
                    files.Add(NvcFile.Parse(Encoding.Latin1.GetString(data), entry.Name, parseDiagnostics));
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                }
            }
        }

        var state = new GameState { Location = "LBY" };
        var api = new Gk3SheepApi(state);
        var resolver = new ActionResolver(api);

        foreach (NvcFile file in files)
        {
            resolver.Add(file);
        }

        int evaluated = 0;
        int failed = 0;

        foreach (NvcFile file in files)
        {
            foreach ((string name, string _) in file.Cases)
            {
                if (resolver.IsCaseSatisfied(file, name, "GABRIEL"))
                {
                    evaluated++;
                }
                else
                {
                    // A false condition and an unreadable one are different things; only
                    // the diagnostics distinguish them.
                    evaluated++;
                }
            }
        }

        failed = resolver.Diagnostics.Items.Count(d => d.Code == "GK3R3300");
        evaluated -= failed;

        HashSet<string> nouns = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> verbs = new(StringComparer.OrdinalIgnoreCase);
        int actions = 0;
        int cases = 0;

        foreach (NvcFile file in files)
        {
            actions += file.Actions.Count;
            cases += file.Cases.Count;

            foreach (NvcAction action in file.Actions)
            {
                nouns.Add(action.Noun);
                verbs.Add(action.Verb);
            }
        }

        _log(string.Create(CultureInfo.InvariantCulture, $"    {verbs.Count} distinct verbs"));
        foreach (string verb in verbs.OrderBy(v => v, StringComparer.Ordinal).Take(24))
        {
            _log($"      {verb}");
        }

        // Show the resolver actually answering, on a noun the corpus definitely has.
        foreach (string sample in (string[])["SCENE", "MOSELY"])
        {
            IReadOnlyList<AvailableAction> available = resolver.Resolve(sample);
            _log(string.Create(CultureInfo.InvariantCulture,
                $"    {sample}: {available.Count} actions available"));
            foreach (AvailableAction action in available.Take(6))
            {
                _log($"      {action.LocalizedVerb} ({action.Category}, icon={action.IconSemantic})");
            }
        }

        int unreadable = parseDiagnostics.Items.Count(d => d.Code == "GK3R1080");
        if (unreadable > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2900", DiagnosticSeverity.Warning,
                $"{unreadable} action lines could not be read.",
                null, null, "every line to parse", $"{unreadable} skipped",
                "The lines are listed in the parse diagnostics."));
        }

        return new ActionSurveySummary(
            files.Count, actions, nouns.Count, verbs.Count, cases, evaluated, failed, unreadable);
    }
}

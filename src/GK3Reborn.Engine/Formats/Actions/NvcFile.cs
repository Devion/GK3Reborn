using System.Text;
using System.Text.RegularExpressions;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Actions;

/// <summary>One noun/verb/case rule.</summary>
/// <remarks>
/// The four columns are the whole interaction model: doing <see cref="Verb"/> to
/// <see cref="Noun"/> runs <see cref="Script"/>, but only when <see cref="Case"/> holds.
/// </remarks>
public sealed record NvcAction
{
    /// <summary>The thing being acted on, such as <c>STAIRS_LEFT</c> or <c>SCENE</c>.</summary>
    public required string Noun { get; init; }

    /// <summary>The action, such as <c>LOOK</c>, <c>TALK</c> or <c>GO_UP</c>.</summary>
    public required string Verb { get; init; }

    /// <summary>
    /// The condition under which this rule applies, named in the file's logic section or
    /// one of the built-in cases.
    /// </summary>
    public required string Case { get; init; }

    /// <summary>How the actor gets into position: <c>WalkTo</c>, <c>WalkToSee</c>, <c>ANIM</c>.</summary>
    public string? Approach { get; init; }

    /// <summary>Where the approach takes the actor.</summary>
    public string? Target { get; init; }

    /// <summary>Inline Sheep run when the action fires.</summary>
    public string? Script { get; init; }

    /// <summary>Which file and line this came from.</summary>
    public required string Source { get; init; }
}

/// <summary>
/// Reader for GK3's action files.
/// </summary>
/// <remarks>
/// <para>
/// 390 files defining everything the player can do. Each line is
/// <c>noun, verb, case</c> followed by optional <c>approach=</c>, <c>target=</c> and
/// <c>script={…}</c> fields, and a trailing <c>[LOGIC]</c> section names the cases as
/// Sheep expressions.
/// </para>
/// <para>
/// This is the file the modern interaction model is built from. Asking "what can the
/// player do to this object right now" means taking every rule for that noun and
/// evaluating its case — which is exactly what the original engine did to decide whether
/// a verb appeared on its verb wheel.
/// </para>
/// </remarks>
public sealed partial class NvcFile
{
    private NvcFile(string name, IReadOnlyList<NvcAction> actions, IReadOnlyDictionary<string, string> cases)
    {
        Name = name;
        Actions = actions;
        Cases = cases;
    }

    /// <summary>Name this file was read under.</summary>
    public string Name { get; }

    /// <summary>Every rule, in file order.</summary>
    public IReadOnlyList<NvcAction> Actions { get; }

    /// <summary>Named case conditions, as Sheep expressions.</summary>
    public IReadOnlyDictionary<string, string> Cases { get; }

    /// <summary>
    /// Cases the engine answers itself rather than reading from a file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ALL</c> always applies. <c>GABE_ALL</c> and the rest depend on who the player
    /// currently is, which matters because GK3 switches between Gabriel and Grace. The
    /// <c>_TIME</c> family counts how often the player has already done this to this, and
    /// the two <c>DIALOGUE_TOPICS_LEFT</c> forms ask whether there is anything left to say.
    /// </para>
    /// <para>
    /// <c>TIME_BLOCK</c> and <c>TIME_BLOCK_OVERRIDE</c> are simply true. They mark an
    /// action a timeblock's own file writes to override one the location's general file
    /// gives, and the second outranks the first where both could apply. Missing them is
    /// expensive and silent: <c>TIME_BLOCK_OVERRIDE</c> is used by 90 of the corpus's
    /// action files and written into the logic section of exactly one, so treating it as
    /// an undefined case takes 918 actions out of the game.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> BuiltInCases { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "GABE_ALL", "GRACE_ALL", "DEFAULT", "NOT_GABE_ALL", "NOT_GRACE_ALL",
        "TIME_BLOCK", "TIME_BLOCK_OVERRIDE",
        "1ST_TIME", "2CD_TIME", "2ND_TIME", "3RD_TIME", "OTR_TIME",
        "DIALOGUE_TOPICS_LEFT", "NOT_DIALOGUE_TOPICS_LEFT",
        "EGG",
    };

    /// <summary>Parses an action file.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives warnings about lines that could not be read.</param>
    /// <returns>The parsed file.</returns>
    public static NvcFile Parse(string text, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(diagnostics);

        List<NvcAction> actions = [];
        Dictionary<string, string> cases = new(StringComparer.OrdinalIgnoreCase);
        bool inLogic = false;
        int lineNumber = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            lineNumber++;
            string line = StripComment(rawLine).Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                // Sections other than LOGIC exist in some files; anything that is not a
                // rule list is skipped rather than guessed at.
                inLogic = line.StartsWith("[LOGIC]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inLogic)
            {
                int equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    continue;
                }

                string caseName = line[..equals].Trim();

                // The braces are the field, not the expression. The semicolon inside them
                // is a statement terminator the original's compiler tolerates because it
                // compiles the case as a snippet of Sheep; read as an expression it is
                // trailing rubbish, and three cases in the corpus are written that way —
                // LBY110A02P's {!DoesEgoHaveInvItem("Candy");} among them.
                string expression = line[(equals + 1)..]
                    .Trim()
                    .Trim('{', '}')
                    .Trim()
                    .TrimEnd(';')
                    .Trim();

                cases[caseName] = expression;
                continue;
            }

            if (TryParseAction(line, $"{name}:{lineNumber}", out NvcAction? action))
            {
                actions.Add(action);
            }
            else
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R1080", DiagnosticSeverity.Warning,
                    "Could not read an action line.",
                    name, lineNumber, "noun, verb, case and optional fields",
                    line.Length > 80 ? line[..80] + "…" : line,
                    "The line is skipped; check it against the surrounding rules."));
            }
        }

        return new NvcFile(name, actions, cases);
    }

    private static bool TryParseAction(
        string line, string source, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NvcAction? action)
    {
        action = null;

        // The script field can contain commas and braces, so it is lifted out before the
        // rest of the line is split on commas.
        string? script = null;
        Match scriptMatch = ScriptField().Match(line);
        string remainder = line;

        if (scriptMatch.Success)
        {
            script = scriptMatch.Groups["body"].Value.Trim();
            remainder = line.Remove(scriptMatch.Index, scriptMatch.Length);
        }

        List<string> parts = [.. remainder.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)];
        if (parts.Count < 3)
        {
            return false;
        }

        string? approach = null;
        string? target = null;
        List<string> positional = [];

        foreach (string part in parts)
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                positional.Add(part);
                continue;
            }

            string key = part[..equals].Trim();
            string value = part[(equals + 1)..].Trim();

            if (key.Equals("approach", StringComparison.OrdinalIgnoreCase))
            {
                approach = value;
            }
            else if (key.Equals("target", StringComparison.OrdinalIgnoreCase))
            {
                target = value;
            }
        }

        if (positional.Count < 3)
        {
            return false;
        }

        action = new NvcAction
        {
            Noun = positional[0],
            Verb = positional[1],
            Case = positional[2],
            Approach = approach,
            Target = target,
            Script = script,
            Source = source,
        };

        return true;
    }

    /// <summary>Removes a trailing line comment, leaving text inside strings alone.</summary>
    private static string StripComment(string line)
    {
        bool inString = false;

        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"')
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '/' && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
    }

    [GeneratedRegex(@"script\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptField();
}

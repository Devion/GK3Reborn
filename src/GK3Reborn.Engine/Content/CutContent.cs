using System.Globalization;
using System.Text;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>Which of the restorations are wanted.</summary>
public enum CutContentTier
{
    /// <summary>None. The game is exactly what it shipped as.</summary>
    None = 0,

    /// <summary>
    /// Things to look at, read, listen to and talk about. Nothing that can change what an
    /// action does.
    /// </summary>
    Observation = 1,

    /// <summary>Those, and the restored rules whose verb can do something.</summary>
    All = 2,

    /// <summary>
    /// Those, and the objects that were written and recorded but never modelled, rebuilt
    /// from scratch.
    /// </summary>
    /// <remarks>
    /// A step further than the rest of this and labelled apart from it on purpose.
    /// Everything below is the developers' own data switched back on; this puts geometry in
    /// their rooms that nobody at Sierra ever made. See <c>tools/blender/make_props.py</c>
    /// for what each one is and what it is skinned with.
    /// </remarks>
    Reconstructed = 3,
}

/// <summary>
/// Puts back content the game shipped with and cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// A great deal of GK3 is on the disc and unreachable: rules the developers commented out
/// with their recordings still in the archives, noun bindings commented out rather than
/// corrected when a model was renamed, and models folded into a neighbouring noun. See
/// <c>docs/cut-content.md</c> for the survey, and for the credit — the catalogue that
/// started it is Bonny Ploeg's.
/// </para>
/// <para>
/// This is a table of edits rather than a set of replacement files, for two reasons. A
/// rewritten <c>R23210A.SIF</c> would be a derivative of Sierra's asset, and this project
/// ships none; and an edit that has to find what it is about to change can say so when the
/// installation underneath is not what it expected, where a wholesale replacement would
/// silently impose 1999's file on a different release.
/// </para>
/// <para>
/// Nothing is written to the player's installation. The archives are opened read-only and
/// the edit is applied to the bytes on their way past.
/// </para>
/// </remarks>
public sealed class CutContent
{
    private readonly Dictionary<string, List<Edit>> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _done = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<CutContentTier, CutContent> Tables = [];
    private int _applied;
    private int _failed;
    private int _unreadable;

    private CutContent()
    {
    }

    /// <summary>How many assets this table edits.</summary>
    public int Count => _edits.Count;

    /// <summary>How many individual edits it holds.</summary>
    public int EditCount => _edits.Values.Sum(list => list.Count);

    /// <summary>How many edits have been applied so far.</summary>
    public int Applied => _applied;

    /// <summary>How many could not be, because the asset did not look as expected.</summary>
    public int Failed => _failed;

    /// <summary>How many lines of the table itself could not be read.</summary>
    /// <remarks>
    /// Should be nought, and a test holds it there. A mistyped operation is a restoration
    /// that never happens, and the whole point of this file is that such things stop being
    /// silent.
    /// </remarks>
    public int Unreadable => _unreadable;

    /// <summary>Whether there is nothing to do.</summary>
    public bool IsEmpty => _edits.Count == 0;

    /// <summary>Every asset name this table touches, in a stable order.</summary>
    public IReadOnlyList<string> Names =>
        [.. _edits.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The table the engine ships.</summary>
    /// <param name="tier">How much of it to take.</param>
    /// <returns>The restorations, or an empty set for <see cref="CutContentTier.None"/>.</returns>
    public static CutContent Open(CutContentTier tier)
    {
        if (tier == CutContentTier.None)
        {
            return new CutContent();
        }

        // One table per tier for the life of the process. A restoration is applied to an
        // asset once and the result kept; handing out a fresh table each time a room asks
        // whether to restore anything would apply every edit again, count it again, and
        // report a failure once per room rather than once.
        lock (Tables)
        {
            if (Tables.TryGetValue(tier, out CutContent? already))
            {
                return already;
            }
        }

        CutContent table = Read(tier);

        lock (Tables)
        {
            if (Tables.TryGetValue(tier, out CutContent? raced))
            {
                return raced;
            }

            Tables[tier] = table;
        }

        return table;
    }

    private static CutContent Read(CutContentTier tier)
    {
        using Stream? stream = typeof(CutContent).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Story.CutContent.txt");

        if (stream is null)
        {
            return new CutContent();
        }

        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd(), tier);
    }

    /// <summary>Reads a table.</summary>
    /// <param name="text">Its contents.</param>
    /// <param name="tier">How much of it to take.</param>
    /// <returns>The restorations.</returns>
    public static CutContent Parse(string text, CutContentTier tier)
    {
        ArgumentNullException.ThrowIfNull(text);

        var table = new CutContent();

        if (tier == CutContentTier.None)
        {
            return table;
        }

        bool wanted = true;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                string section = line.Trim('[', ']').Trim();

                // An unknown section is skipped rather than refused: a table from a later
                // version may name a tier this build has no opinion about, and taking its
                // other sections is better than taking none of it.
                // Cumulative: each tier is everything below it and one section more.
                wanted = section.ToUpperInvariant() switch
                {
                    "OBSERVATION" => true,
                    "PUZZLE" => tier >= CutContentTier.All,
                    "RECONSTRUCTED" => tier >= CutContentTier.Reconstructed,
                    _ => false,
                };

                continue;
            }

            if (!wanted)
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Edit? edit = parts[0].ToUpperInvariant() switch
            {
                "RULE" when parts.Length == 5 =>
                    new Edit(EditKind.Rule, parts[2], parts[3], parts[4], null, null),

                "BIND" when parts.Length is 5 or 6 =>
                    new Edit(
                        EditKind.Bind,
                        parts[2],
                        parts[3],
                        parts[4],
                        parts.Length == 6 && parts[5].StartsWith("after=", StringComparison.OrdinalIgnoreCase)
                            ? parts[5]["after=".Length..]
                            : null,
                        null),

                "NOUN" when parts.Length == 4 =>
                    new Edit(EditKind.Noun, parts[2], parts[3], null, null, null),

                // append <FILE> <SECTION|-> <LINE...>
                //
                // The line is the rest of the line, spaces and all, because the files this
                // reaches are not all comma-separated rules: "v_hose = Garden hose" is one
                // of them.
                "APPEND" when parts.Length >= 4 =>
                    new Edit(
                        EditKind.Append,
                        parts[2],
                        string.Join(' ', parts[3..]),
                        null,
                        null,
                        null),

                // place <FILE.SIF> <MODEL> <NOUN> <x,y,z[,heading]> after=<MODEL>
                "PLACE" when parts.Length == 6 =>
                    new Edit(
                        EditKind.Bind,
                        parts[2],
                        parts[3],
                        "prop",
                        parts[5].StartsWith("after=", StringComparison.OrdinalIgnoreCase)
                            ? parts[5]["after=".Length..]
                            : null,
                        parts[4]),

                _ => null,
            };

            // A line this cannot read is a restoration that would otherwise go missing in
            // silence -- which is the failure this whole table exists to undo. Counted, so
            // that a table with a typo in it says so rather than quietly doing less.
            if (edit is null)
            {
                table._unreadable++;
                continue;
            }

            if (!table._edits.TryGetValue(parts[1], out List<Edit>? list))
            {
                list = [];
                table._edits[parts[1]] = list;
            }

            list.Add(edit);
        }

        return table;
    }

    /// <summary>Whether this table has anything to say about an asset.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>True when it does.</returns>
    /// <remarks>
    /// A dictionary lookup, because every asset the game reads passes through it.
    /// </remarks>
    public bool Handles(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _edits.ContainsKey(name);
    }

    /// <summary>Applies every edit this table holds for an asset.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <param name="original">The archive's own bytes.</param>
    /// <param name="diagnostics">Where edits that did not apply are reported.</param>
    /// <returns>
    /// The edited bytes, or <paramref name="original"/> when there is nothing to do or
    /// nothing applied.
    /// </returns>
    /// <remarks>
    /// The text assets are Windows-1252 rather than UTF-8 — they were authored in 1999 and
    /// carry accented French names — so the round trip is Latin-1 in and Latin-1 out. Line
    /// endings are left exactly as they were found: a file rewritten with different ones
    /// still parses, but every later diff of it is noise.
    /// </remarks>
    public byte[] Apply(string name, byte[] original, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(original);

        if (!_edits.TryGetValue(name, out List<Edit>? edits))
        {
            return original;
        }

        // These files are read again every time a room loads, and the edit is the same
        // every time. Caching it also keeps the counters honest: an edit is applied once,
        // not once per visit to the room.
        if (_done.TryGetValue(name, out byte[]? already))
        {
            return already;
        }

        string text = Encoding.Latin1.GetString(original);
        List<string> lines = [.. SplitKeepingEndings(text)];
        List<string?> sections = Sections(lines);

        bool changed = false;

        foreach (Edit edit in edits)
        {
            if (ApplyOne(name, edit, lines, sections, diagnostics))
            {
                changed = true;
                _applied++;
            }
            else
            {
                _failed++;
            }
        }

        byte[] result = changed ? Encoding.Latin1.GetBytes(string.Concat(lines)) : original;
        _done[name] = result;

        return result;
    }

    /// <summary>A line for the startup log.</summary>
    /// <returns>What was restored, or null when nothing was.</returns>
    public string? Describe()
    {
        if (_applied == 0 && _failed == 0)
        {
            return null;
        }

        return _failed == 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{_applied} restored in {_edits.Count} file(s)")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{_applied} restored in {_edits.Count} file(s), {_failed} did not apply");
    }

    private static bool ApplyOne(
        string name,
        Edit edit,
        List<string> lines,
        List<string?> sections,
        DiagnosticBag? diagnostics)
    {
        switch (edit.Kind)
        {
            case EditKind.Rule:
                return ApplyRule(name, edit, lines, diagnostics);

            case EditKind.Bind:
                return ApplyBind(name, edit, lines, sections, diagnostics);

            case EditKind.Noun:
                return ApplyNoun(name, edit, lines, sections, diagnostics);

            case EditKind.Append:
                return ApplyAppend(name, edit, lines, sections, diagnostics);

            default:
                return false;
        }
    }

    private static bool ApplyRule(string name, Edit edit, List<string> lines, DiagnosticBag? diagnostics)
    {
        List<int> found = [];

        for (int i = 0; i < lines.Count; i++)
        {
            if (Uncommented(lines[i]) is not { } body)
            {
                continue;
            }

            string[] fields = body.Split(',');

            if (fields.Length < 3)
            {
                continue;
            }

            if (Same(fields[0], edit.First) &&
                Same(fields[1], edit.Second!) &&
                Same(fields[2], edit.Third!))
            {
                found.Add(i);
            }
        }

        if (found.Count != 1)
        {
            Report(
                diagnostics,
                "GK3R1190",
                $"{name}: no single commented-out rule for {edit.First} {edit.Second} "
                + $"{edit.Third}, so it is left alone.",
                name,
                "exactly one commented rule",
                found.Count == 0 ? "none" : $"{found.Count}");

            return false;
        }

        lines[found[0]] = RemoveComment(lines[found[0]]);

        return true;
    }

    private static bool ApplyBind(
        string name,
        Edit edit,
        List<string> lines,
        List<string?> sections,
        DiagnosticBag? diagnostics)
    {
        // The developers' own line first, wherever they commented one out: it carries
        // whatever else they wrote on it, and it is already in the right section.
        List<int> disabled = [];

        for (int i = 0; i < lines.Count; i++)
        {
            if (!IsModelSection(sections[i]))
            {
                continue;
            }

            if (Uncommented(lines[i]) is not { } body)
            {
                continue;
            }

            if (Same(Field(body, "model"), edit.First) && Same(Field(body, "noun"), edit.Second!))
            {
                disabled.Add(i);
            }
        }

        if (disabled.Count == 1)
        {
            lines[disabled[0]] = RemoveComment(lines[disabled[0]]);
            return true;
        }

        if (edit.After is null)
        {
            Report(
                diagnostics,
                "GK3R1191",
                $"{name}: {edit.First} has no commented-out binding to {edit.Second} and the "
                + "table names no line to write one after, so nothing is bound.",
                name,
                "a commented binding, or after=",
                disabled.Count == 0 ? "neither" : $"{disabled.Count} commented bindings");

            return false;
        }

        List<int> anchors = [];

        for (int i = 0; i < lines.Count; i++)
        {
            if (IsModelSection(sections[i]) &&
                !IsCommented(lines[i]) &&
                Same(Field(Body(lines[i]), "model"), edit.After))
            {
                anchors.Add(i);
            }
        }

        if (anchors.Count != 1)
        {
            Report(
                diagnostics,
                "GK3R1192",
                $"{name}: {edit.After} is not a single live binding, so there is nowhere "
                + $"unambiguous to bind {edit.First} as {edit.Second}.",
                name,
                $"exactly one binding of {edit.After}",
                anchors.Count == 0 ? "none" : $"{anchors.Count}");

            return false;
        }

        int at = anchors[0];
        string indent = lines[at][..(lines[at].Length - lines[at].TrimStart().Length)];
        string ending = Ending(lines[at]);

        string placed = Placement(edit.Where);

        lines.Insert(
            at + 1,
            $"{indent}model={edit.First}, noun={edit.Second}, type={edit.Third}{placed}{ending}");

        // The section map is indexed by line, so it grows with them. A list rather than an
        // array because more than one edit can insert after the same anchor -- five models
        // go in after one line in RC2 -- and a resized array would leave the caller holding
        // the old one, one shorter than the lines it indexes, from the second insert on.
        sections.Insert(at + 1, sections[at]);

        return true;
    }

    private static bool ApplyNoun(
        string name,
        Edit edit,
        List<string> lines,
        List<string?> sections,
        DiagnosticBag? diagnostics)
    {
        int changed = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            if (!IsModelSection(sections[i]) || IsCommented(lines[i]))
            {
                continue;
            }

            string body = Body(lines[i]);

            if (!Same(Field(body, "model"), edit.First))
            {
                continue;
            }

            lines[i] = Field(body, "noun") is null
                ? InsertNoun(lines[i], edit.Second!)
                : ReplaceNoun(lines[i], edit.Second!);

            changed++;
        }

        if (changed == 0)
        {
            Report(
                diagnostics,
                "GK3R1193",
                $"{name}: no live binding of {edit.First}, so it cannot be pointed at "
                + $"{edit.Second}.",
                name,
                $"at least one binding of {edit.First}",
                "none");

            return false;
        }

        return true;
    }

    /// <summary>Adds a line to the end of a named section.</summary>
    /// <remarks>
    /// For the one thing the other three cannot do: bringing a whole new action file into a
    /// room's scope. A restored rule can be uncommented and a restored object bound, but a
    /// rule that was never written at all has to live in a file of its own, and a file
    /// nothing lists is a file nothing reads.
    /// <para>
    /// Refused when the line is already there, so applying the table twice to one file --
    /// which the cache makes unlikely and a future caller may make certain -- cannot list
    /// it twice.
    /// </para>
    /// </remarks>
    private static bool ApplyAppend(
        string name,
        Edit edit,
        List<string> lines,
        List<string?> sections,
        DiagnosticBag? diagnostics)
    {
        // "-" is the region before the first [section] header, which is where a flat
        // key-and-value file keeps everything and where the inventory's own two do.
        bool unnamed = edit.First == "-";
        int last = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            bool here = unnamed
                ? sections[i] is null
                : string.Equals(sections[i], edit.First, StringComparison.OrdinalIgnoreCase);

            if (!here)
            {
                continue;
            }

            if (Body(lines[i]).Equals(edit.Second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            last = i;
        }

        if (last < 0)
        {
            Report(
                diagnostics,
                "GK3R1201",
                unnamed
                    ? $"{name}: there is nothing before its first section to add {edit.Second} to."
                    : $"{name}: there is no [{edit.First}] section to add {edit.Second} to.",
                name,
                unnamed ? "a line before the first section" : $"a [{edit.First}] section",
                "none");

            return false;
        }

        // The last line of a section is often the last line of the file, and a file need
        // not end with a newline. Inserting after one that does not would run the two
        // together into a single name that no archive has -- which is what happened, and
        // which the loader reported as nothing at all rather than as a bad line.
        string ending = Ending(lines[last]);

        if (ending.Length == 0)
        {
            ending = Ending(lines.Find(l => Ending(l).Length > 0) ?? string.Empty);
            ending = ending.Length == 0 ? "\r\n" : ending;
            lines[last] += ending;
        }

        lines.Insert(last + 1, edit.Second + ending);
        sections.Insert(last + 1, sections[last]);

        return true;
    }

    private static void Report(
        DiagnosticBag? diagnostics,
        string code,
        string message,
        string file,
        string expected,
        string actual)
    {
        diagnostics?.Add(new Diagnostic(
            code,
            DiagnosticSeverity.Warning,
            message,
            file,
            null,
            expected,
            actual,
            "The installation is not the one this table was written against. The rest of "
            + "the restorations still apply; run with --no-restore-cut-content to take "
            + "none of them."));
    }

    /// <summary>Renders a placement as the scene file writes one.</summary>
    /// <param name="where">Three or four numbers: x, y, z and an optional heading.</param>
    /// <returns>The rest of the model line, or nothing when there is no placement.</returns>
    /// <remarks>
    /// A prop borrowed or built for a room it was not modelled for has to be told where it
    /// goes, because a .MOD's vertices are in the coordinates of whichever room it came
    /// from. <c>pos</c> means <em>stand here</em>; see <see cref="Formats.Scenes.SceneModel.Position"/>.
    /// </remarks>
    private static string Placement(string? where)
    {
        if (where is null)
        {
            return string.Empty;
        }

        string[] numbers = where.Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (numbers.Length is not (3 or 4))
        {
            return string.Empty;
        }

        string heading = numbers.Length == 4 ? $", heading={numbers[3]}" : string.Empty;

        return $", pos={{{numbers[0]},{numbers[1]},{numbers[2]}}}{heading}";
    }

    private static List<string?> Sections(List<string> lines)
    {
        var sections = new List<string?>(lines.Count);
        string? current = null;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();

            if (trimmed.StartsWith('['))
            {
                // [MODELS={...}] is still a models section; the condition decides when it
                // applies, not what it is.
                current = trimmed[1..].Split('=', ']')[0].Trim().ToUpperInvariant();
            }

            sections.Add(current);
        }

        return sections;
    }

    private static bool IsModelSection(string? section) =>
        section is "MODELS" or "ACTORS";

    private static IEnumerable<string> SplitKeepingEndings(string text)
    {
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            yield return text[start..(i + 1)];
            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static string Ending(string line) =>
        line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
        : line.EndsWith('\n') ? "\n"
        : string.Empty;

    private static string Body(string line) => line.Trim().TrimEnd('\r', '\n').Trim();

    private static bool IsCommented(string line) =>
        line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    /// <summary>The body of a commented line, or null when the line is not one.</summary>
    private static string? Uncommented(string line)
    {
        string trimmed = Body(line);

        return trimmed.StartsWith("//", StringComparison.Ordinal)
            ? trimmed[2..].Trim()
            : null;
    }

    private static string RemoveComment(string line)
    {
        int at = line.IndexOf("//", StringComparison.Ordinal);

        return at < 0 ? line : line.Remove(at, 2);
    }

    private static string? Field(string body, string key)
    {
        int at = 0;

        while (true)
        {
            at = body.IndexOf(key, at, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
            {
                return null;
            }

            // "noun" must not match inside another word, and the value must follow an '='.
            bool wordStart = at == 0 || !char.IsLetterOrDigit(body[at - 1]) && body[at - 1] != '_';
            int after = at + key.Length;

            while (after < body.Length && body[after] == ' ')
            {
                after++;
            }

            if (wordStart && after < body.Length && body[after] == '=')
            {
                int start = after + 1;

                while (start < body.Length && body[start] == ' ')
                {
                    start++;
                }

                int end = start;

                while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] == '_'))
                {
                    end++;
                }

                return end > start ? body[start..end] : null;
            }

            at = after;
        }
    }

    private static string ReplaceNoun(string line, string noun)
    {
        string? existing = Field(Body(line), "noun");

        if (existing is null)
        {
            return line;
        }

        int at = line.IndexOf(existing, line.IndexOf("noun", StringComparison.OrdinalIgnoreCase),
            StringComparison.Ordinal);

        return at < 0 ? line : line.Remove(at, existing.Length).Insert(at, noun);
    }

    private static string InsertNoun(string line, string noun)
    {
        string? model = Field(Body(line), "model");

        if (model is null)
        {
            return line;
        }

        int at = line.IndexOf(model, line.IndexOf("model", StringComparison.OrdinalIgnoreCase),
            StringComparison.Ordinal) + model.Length;

        return line.Insert(at, $", noun={noun}");
    }

    private static bool Same(string? a, string b) =>
        a is not null && a.Trim().Equals(b, StringComparison.OrdinalIgnoreCase);

    private enum EditKind
    {
        Rule,
        Bind,
        Noun,
        Append,
    }

    private sealed record Edit(
        EditKind Kind,
        string First,
        string? Second,
        string? Third,
        string? After,
        string? Where);
}

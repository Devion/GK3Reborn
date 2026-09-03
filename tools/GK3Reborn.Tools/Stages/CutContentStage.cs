using GK3Reborn.Content;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Applies the cut-content restoration table to a real installation and says what
/// happened to every edit in it.
/// </summary>
/// <remarks>
/// The table is a set of edits that have to find what they are about to change, so the
/// only thing that proves one is right is running it against the archives. A binding that
/// names an object a room does not contain fails silently, which is how most of this
/// content was lost in the first place; this is what stops the restoration losing it the
/// same way.
/// </remarks>
public static class CutContentStage
{
    /// <summary>Runs the check.</summary>
    /// <param name="source">The game's <c>Data</c> directory.</param>
    /// <param name="tier">How much of the table to apply.</param>
    /// <param name="verbose">Whether to print each edited file's changed lines.</param>
    /// <returns>Process exit code: 0 when every edit applied.</returns>
    public static int Run(string source, CutContentTier tier, bool verbose)
    {
        ArgumentNullException.ThrowIfNull(source);

        using GameArchives archives = GameArchives.Open(source);

        if (archives.Count == 0)
        {
            Console.Error.WriteLine($"No barn archives in {source}.");
            return 2;
        }

        CutContent table = CutContent.Open(tier);

        if (table.IsEmpty)
        {
            Console.Error.WriteLine("The restoration table is empty.");
            return 2;
        }

        var diagnostics = new DiagnosticBag();
        archives.Restoration = table;
        archives.RestorationDiagnostics = diagnostics;

        Console.WriteLine(
            $"{table.EditCount} restoration(s) in {table.Count} file(s), tier {tier}.");

        Console.WriteLine();

        foreach (string name in table.Names)
        {
            // Read the original first, then the restored one. Reading it twice is what
            // makes the difference visible; the table caches its result, so the second
            // read is the edited copy of the first.
            archives.Restoration = null;
            byte[]? before = archives.Read(name);
            archives.Restoration = table;
            byte[]? after = archives.Read(name);

            if (before is null || after is null)
            {
                Console.WriteLine($"  {name,-20} MISSING from this installation");
                continue;
            }

            int changed = ChangedLines(before, after, verbose, name);

            Console.WriteLine(
                changed == 0
                    ? $"  {name,-20} unchanged"
                    : $"  {name,-20} {changed} line(s) changed");
        }

        Console.WriteLine();
        Console.WriteLine($"applied {table.Applied}, did not apply {table.Failed}");

        foreach (Diagnostic diagnostic in diagnostics.Items)
        {
            Console.Error.WriteLine(diagnostic.ToString());
        }

        return table.Failed == 0 ? 0 : 1;
    }

    private static int ChangedLines(byte[] before, byte[] after, bool verbose, string name)
    {
        string[] a = System.Text.Encoding.Latin1.GetString(before).Split('\n');
        string[] b = System.Text.Encoding.Latin1.GetString(after).Split('\n');

        // A whole-file diff would be more than this needs. Every edit either rewrites a
        // line in place or inserts one, so counting the lines of the restored file that
        // are not in the original counts the edits and nothing else.
        var original = new HashSet<string>(a, StringComparer.Ordinal);
        int changed = 0;

        foreach (string line in b)
        {
            if (original.Contains(line))
            {
                continue;
            }

            changed++;

            if (verbose)
            {
                Console.WriteLine($"      {name}: {line.Trim()}");
            }
        }

        return changed;
    }
}

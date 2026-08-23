using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Tools.Stages;

/// <summary>Totals from a disassembly run.</summary>
/// <param name="Scripts">Scripts read.</param>
/// <param name="Functions">Functions across them.</param>
/// <param name="Instructions">Instructions decoded.</param>
/// <param name="FullyDecoded">Scripts whose bytecode decoded end to end.</param>
/// <param name="Partial">Scripts where decoding stopped before the end.</param>
/// <param name="Failed">Scripts that could not be parsed at all.</param>
/// <param name="DistinctImports">Distinct API functions the corpus calls.</param>
/// <param name="RoundTripped">Scripts that survived being written back out and read again.</param>
public readonly record struct SheepDisassemblySummary(
    int Scripts,
    int Functions,
    int Instructions,
    int FullyDecoded,
    int Partial,
    int Failed,
    int DistinctImports,
    int RoundTripped);

/// <summary>
/// Disassembles every compiled Sheep script.
/// </summary>
/// <remarks>
/// <para>
/// The listings are the first readable form of the game's logic, and the run doubles as a
/// check on the instruction set: an unknown opcode or a miscounted operand desynchronises
/// the stream, so a script that decodes end to end is evidence the decoding is right.
/// Scripts that stop early are counted separately rather than quietly truncated.
/// </para>
/// <para>
/// It also checks the <em>writer</em>, which is the only way to check it: every script is
/// written back out and read again, and everything about the two has to agree. A container
/// half-understood reads the game's own files perfectly well and produces something nothing
/// else can open, and there is no way to notice that from the reader alone.
/// </para>
/// <para>
/// And it gathers the signature catalogue the compiler needs. The game's import tables say
/// what every function it calls takes and returns, which is the one authoritative source
/// for that in the content: the specification has it too, but the specification is a Word
/// document.
/// </para>
/// </remarks>
public sealed class SheepDisassembleStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SheepDisassembleStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Disassembles the corpus.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The totals.</returns>
    public SheepDisassemblySummary Run(
        string sourceDirectory, string workspaceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        string outputRoot = Path.Combine(workspaceDirectory, "normalized", "scripts-disassembled");
        Directory.CreateDirectory(outputRoot);

        int scripts = 0;
        int functions = 0;
        int instructions = 0;
        int complete = 0;
        int partial = 0;
        int failed = 0;
        int roundTripped = 0;
        HashSet<string> imports = new(StringComparer.OrdinalIgnoreCase);
        var signatures = new SheepSignatures();

        foreach (FileInfo archiveFile in new DirectoryInfo(sourceDirectory)
                     .EnumerateFiles("*.brn")
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            using BarnArchive archive = BarnArchive.Open(archiveFile.FullName);

            foreach (BarnEntry entry in archive.Entries)
            {
                if (entry.IsPointer ||
                    !Path.GetExtension(entry.Name).Equals(".SHP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] data;
                try
                {
                    data = archive.Extract(entry);
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                    failed++;
                    continue;
                }

                if (!SheepScriptFile.IsSheep(data))
                {
                    continue;
                }

                try
                {
                    SheepScriptFile script = SheepScriptFile.Parse(data, entry.Name);
                    IReadOnlyList<SheepInstruction> decoded = SheepDisassembler.Decode(script);

                    scripts++;
                    functions += script.Functions.Count;
                    instructions += decoded.Count;

                    foreach (SheepImport import in script.Imports)
                    {
                        imports.Add(import.Name);
                        signatures.Add(import, entry.Name);
                    }

                    if (Survives(script, entry.Name, diagnostics))
                    {
                        roundTripped++;
                    }

                    int consumed = decoded.Count == 0 ? 0 : decoded[^1].Address + 1;
                    if (consumed >= script.Bytecode.Length)
                    {
                        complete++;
                    }
                    else
                    {
                        partial++;
                    }

                    AtomicFile.WriteAllText(
                        Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(entry.Name) + ".sheep"),
                        SheepDisassembler.Render(script));
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                    failed++;
                }
            }
        }

        _log($"wrote listings to {outputRoot}");

        foreach (Diagnostic disagreement in signatures.Diagnostics.Items)
        {
            diagnostics.Add(disagreement);
        }

        string catalogue = Path.Combine(outputRoot, "signatures.txt");

        AtomicFile.WriteAllText(
            catalogue,
            string.Join(
                Environment.NewLine,
                signatures.Names.Select(n =>
                    signatures.TryGet(n, out SheepImport found)
                        ? SheepSignatures.Describe(found)
                        : n)));

        _log($"{signatures.Count} function signatures gathered into {catalogue}");

        if (roundTripped < scripts)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2701", DiagnosticSeverity.Warning,
                $"{scripts - roundTripped} scripts did not survive being written back out.",
                null, null, "every script to write and read identically",
                $"{scripts - roundTripped} differed",
                "The writer and the reader disagree about the container."));
        }

        if (partial > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2700", DiagnosticSeverity.Warning,
                $"{partial} scripts did not decode to the end of their bytecode.",
                null, null, "every byte to decode as an instruction", $"{partial} stopped early",
                "Each listing records where it stopped. An unknown opcode or a miscounted "
                + "operand would cause this."));
        }

        return new SheepDisassemblySummary(
            scripts, functions, instructions, complete, partial, failed,
            imports.Count, roundTripped);
    }

    /// <summary>
    /// Writes a script back out, reads it again, and says whether the two agree.
    /// </summary>
    /// <remarks>
    /// Not byte-for-byte against the original file: the offset tables the reader skips are
    /// free to differ, and matching them would be reproducing something nothing reads. What
    /// has to agree is everything a script <em>is</em> — its imports and their signatures,
    /// its string pool at the offsets the bytecode names, its variables, where each function
    /// starts, and the code.
    /// </remarks>
    private static bool Survives(SheepScriptFile script, string name, DiagnosticBag diagnostics)
    {
        SheepScriptFile again;

        try
        {
            again = SheepScriptFile.Parse(SheepScriptWriter.Write(script), name);
        }
        catch (FormatParseException ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return false;
        }

        string? difference =
            !script.Bytecode.AsSpan().SequenceEqual(again.Bytecode) ? "the code" :
            !script.Functions.SequenceEqual(again.Functions) ? "the functions" :
            !script.Variables.SequenceEqual(again.Variables) ? "the variables" :
            !Same(script.Imports, again.Imports) ? "the imports" :
            !Same(script.StringConstants, again.StringConstants) ? "the strings" :
            null;

        if (difference is null)
        {
            return true;
        }

        diagnostics.Add(new Diagnostic(
            "GK3R2702", DiagnosticSeverity.Warning,
            "A script did not survive being written back out.",
            name, null, "the same script read back", $"{difference} differ",
            "The writer and the reader disagree about that section."));

        return false;
    }

    private static bool Same(IReadOnlyList<SheepImport> left, IReadOnlyList<SheepImport> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            pair.First.ReturnType == pair.Second.ReturnType &&
            pair.First.ArgumentTypes.SequenceEqual(pair.Second.ArgumentTypes));

    private static bool Same(
        IReadOnlyDictionary<int, string> left, IReadOnlyDictionary<int, string> right) =>
        left.Count == right.Count &&
        left.All(entry =>
            right.TryGetValue(entry.Key, out string? text) &&
            string.Equals(entry.Value, text, StringComparison.Ordinal));
}

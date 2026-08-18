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
public readonly record struct SheepDisassemblySummary(
    int Scripts,
    int Functions,
    int Instructions,
    int FullyDecoded,
    int Partial,
    int Failed,
    int DistinctImports);

/// <summary>
/// Disassembles every compiled Sheep script.
/// </summary>
/// <remarks>
/// The listings are the first readable form of the game's logic, and the run doubles as a
/// check on the instruction set: an unknown opcode or a miscounted operand desynchronises
/// the stream, so a script that decodes end to end is evidence the decoding is right.
/// Scripts that stop early are counted separately rather than quietly truncated.
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
        HashSet<string> imports = new(StringComparer.OrdinalIgnoreCase);

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
            scripts, functions, instructions, complete, partial, failed, imports.Count);
    }
}

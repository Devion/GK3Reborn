using System.Globalization;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Tools.Stages;

/// <summary>What compiling one script produced.</summary>
/// <param name="Functions">How many functions it defines.</param>
/// <param name="Instructions">How many instructions it compiled to.</param>
/// <param name="Bytes">How long the bytecode is.</param>
/// <param name="Imports">How many distinct system functions it calls.</param>
/// <param name="Strings">How many string constants it holds.</param>
/// <param name="Variables">How many symbols it declares.</param>
public readonly record struct SheepCompileSummary(
    int Functions, int Instructions, int Bytes, int Imports, int Strings, int Variables);

/// <summary>
/// Compiles Sheep source into the bytecode the game's own machine runs.
/// </summary>
/// <remarks>
/// <para>
/// The end of P4's front end, and the thing that makes the rest of it checkable: source in,
/// a <c>.SHP</c> out, disassembled beside it so that what was emitted can be read rather
/// than assumed.
/// </para>
/// <para>
/// Signatures come from the game when it is to hand. Every compiled script carries an
/// import table saying what each function it calls takes and returns, so the 224 shipped
/// scripts describe all 139 functions the game uses — which is the difference between
/// <c>SetTimerSeconds(2)</c> compiling to a converted int, as the original does, and
/// compiling to an integer the callee reads as a float.
/// </para>
/// </remarks>
public sealed class SheepCompileStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SheepCompileStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Compiles one source file.</summary>
    /// <param name="inputPath">The source.</param>
    /// <param name="outputPath">Where to write the compiled script, or null to write nothing.</param>
    /// <param name="sourceDirectory">The game's data, for the signature catalogue, or null.</param>
    /// <param name="diagnostics">Receives what went wrong.</param>
    /// <returns>What was produced, or null when it did not compile.</returns>
    public SheepCompileSummary? Run(
        string inputPath, string? outputPath, string? sourceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!File.Exists(inputPath))
        {
            _log($"no such file: {inputPath}");
            return null;
        }

        SheepSignatures signatures = Catalogue(sourceDirectory, diagnostics);
        string name = Path.GetFileNameWithoutExtension(inputPath).ToUpperInvariant() + ".SHP";

        SheepScriptFile compiled;

        try
        {
            compiled = SheepCompiler.Compile(File.ReadAllText(inputPath), name, signatures);
        }
        catch (FormatParseException ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return null;
        }

        IReadOnlyList<SheepInstruction> decoded = SheepDisassembler.Decode(compiled);

        foreach ((string function, int offset) in compiled.Functions)
        {
            _log(string.Create(
                CultureInfo.InvariantCulture, $"  {function} at {offset}"));
        }

        // Unknown functions are worth saying out loud rather than compiling silently: the
        // arity is right either way and the types are a guess, which is exactly the case
        // where a float argument goes in as an int.
        foreach (SheepImport import in compiled.Imports)
        {
            if (!signatures.TryGet(import.Name, out _))
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R2703", DiagnosticSeverity.Info,
                    "A call names a function the catalogue does not describe.",
                    name, null, "a function the game itself calls", import.Name,
                    "Its argument types are assumed to be whatever they were written as."));
            }
        }

        if (outputPath is { Length: > 0 })
        {
            // Sheep source and compiled Sheep share an extension, and Windows does not
            // distinguish demo.shp from DEMO.SHP. Writing the output over the input is
            // therefore one careless invocation away, and it destroys the only copy.
            if (Path.GetFullPath(outputPath).Equals(
                    Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R2704", DiagnosticSeverity.Error,
                    "The compiled script would be written over its own source.",
                    inputPath, null, "an output path of its own", outputPath,
                    "Source and bytecode share an extension; give the output another name."));

                return null;
            }

            byte[] bytes = SheepScriptWriter.Write(compiled);

            AtomicFile.WriteAllBytes(outputPath, bytes);
            _log($"wrote {bytes.Length} bytes to {outputPath}");

            // Read straight back, because a compiler whose output only its own reader
            // understands has not been checked against anything.
            SheepScriptFile.Parse(bytes, name);

            AtomicFile.WriteAllText(
                Path.ChangeExtension(outputPath, ".sheep"), SheepDisassembler.Render(compiled));
        }

        return new SheepCompileSummary(
            compiled.Functions.Count,
            decoded.Count,
            compiled.Bytecode.Length,
            compiled.Imports.Count,
            compiled.StringConstants.Count,
            compiled.Variables.Count);
    }

    /// <summary>Reads what every function takes and returns, out of the game's own scripts.</summary>
    private SheepSignatures Catalogue(string? sourceDirectory, DiagnosticBag diagnostics)
    {
        var signatures = new SheepSignatures();

        if (sourceDirectory is not { Length: > 0 } || !Directory.Exists(sourceDirectory))
        {
            _log("no --source, so argument types are taken from how each call was written");
            return signatures;
        }

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

                try
                {
                    byte[] data = archive.Extract(entry);

                    if (!SheepScriptFile.IsSheep(data))
                    {
                        continue;
                    }

                    foreach (SheepImport import in SheepScriptFile.Parse(data, entry.Name).Imports)
                    {
                        signatures.Add(import, entry.Name);
                    }
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                }
            }
        }

        _log($"{signatures.Count} function signatures read from the game's own scripts");
        return signatures;
    }
}

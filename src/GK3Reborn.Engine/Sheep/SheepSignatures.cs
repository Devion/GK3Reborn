using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Sheep;

/// <summary>
/// What each system function takes and returns.
/// </summary>
/// <remarks>
/// <para>
/// A compiler needs this and the language reference does not carry it in a form the engine
/// can read: the specification is a Word document, and its machine-extracted index lives in
/// the content workspace rather than the repository because it is derived from copyrighted
/// documentation.
/// </para>
/// <para>
/// The game answers the question itself. Every compiled script carries an import table
/// giving the return type and argument types of every function it calls, so the 224 shipped
/// scripts between them describe the signature of every function the game uses — 139 of
/// them. Reading the catalogue out of the content is both self-contained and authoritative:
/// it is what the original compiler actually emitted.
/// </para>
/// <para>
/// Disagreements are worth hearing about rather than resolving quietly. Two scripts giving
/// one function two signatures would mean either the reader is wrong or the assumption that
/// a name has one signature is, and both matter more than whichever one happens to win.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "The constants are named for the Sheep language's own types.")]
public sealed class SheepSignatures
{
    /// <summary>The type code for a function that returns nothing.</summary>
    public const sbyte Void = 0;

    /// <summary>The type code for an int.</summary>
    public const sbyte Int = 1;

    /// <summary>The type code for a float.</summary>
    public const sbyte Float = 2;

    /// <summary>The type code for a string.</summary>
    public const sbyte String = 3;

    private readonly Dictionary<string, SheepImport> _functions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many functions the catalogue describes.</summary>
    public int Count => _functions.Count;

    /// <summary>Names in it, in a stable order.</summary>
    public IReadOnlyList<string> Names => [.. _functions.Values.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Diagnostics raised while gathering it.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Reads the catalogue out of compiled scripts.</summary>
    /// <param name="scripts">The scripts, in any order.</param>
    /// <returns>The catalogue.</returns>
    public static SheepSignatures From(IEnumerable<SheepScriptFile> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        var catalogue = new SheepSignatures();

        foreach (SheepScriptFile script in scripts)
        {
            foreach (SheepImport import in script.Imports)
            {
                catalogue.Add(import, script.Name);
            }
        }

        return catalogue;
    }

    /// <summary>Adds one signature, or checks it against the one already known.</summary>
    /// <param name="import">The signature.</param>
    /// <param name="source">Where it came from, for a diagnostic.</param>
    public void Add(SheepImport import, string source = "<memory>")
    {
        if (import.Name is not { Length: > 0 } name)
        {
            return;
        }

        if (!_functions.TryGetValue(name, out SheepImport known))
        {
            _functions[name] = import;
            return;
        }

        if (known.ReturnType == import.ReturnType &&
            known.ArgumentTypes.SequenceEqual(import.ArgumentTypes))
        {
            return;
        }

        Diagnostics.Add(new Diagnostic(
            "GK3R1085", DiagnosticSeverity.Warning,
            $"Two scripts give '{name}' different signatures.",
            source, null, Describe(known), Describe(import),
            "The first one read is kept. One of the two scripts disagrees with the rest."));
    }

    /// <summary>Finds a function's signature.</summary>
    /// <param name="name">Its name, matched without regard to case.</param>
    /// <param name="signature">Its signature, when it is known.</param>
    /// <returns>True when the catalogue knows it.</returns>
    public bool TryGet(string name, out SheepImport signature)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _functions.TryGetValue(name, out signature);
    }

    /// <summary>Writes a signature the way a prototype is written.</summary>
    /// <param name="import">The signature.</param>
    /// <returns>Something like <c>int GetNounVerbCount(string, string)</c>.</returns>
    public static string Describe(SheepImport import) =>
        $"{Name(import.ReturnType)} {import.Name}(" +
        string.Join(", ", import.ArgumentTypes.Select(Name)) + ")";

    private static string Name(sbyte type) => type switch
    {
        Int => "int",
        Float => "float",
        String => "string",
        _ => "void",
    };
}

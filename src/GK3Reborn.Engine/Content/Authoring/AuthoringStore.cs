using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content.Authoring;

/// <summary>
/// Loads and saves the two halves of an authorable document.
/// </summary>
/// <remarks>
/// <para>
/// Every authorable document is a pair: a generated baseline the converter owns and
/// rewrites freely, and an edits file the humans own and the converter never touches.
/// Keeping them in separate files is what makes "regenerate everything" safe.
/// </para>
/// <para>
/// The naming convention is <c>&lt;name&gt;.json</c> and <c>&lt;name&gt;.edits.json</c> side by
/// side, so an artist opening the content folder can see at a glance which scenes
/// have been corrected and which are still running on the generator's guesses.
/// </para>
/// </remarks>
public static class AuthoringStore
{
    /// <summary>The suffix that marks a human-owned edits file.</summary>
    public const string EditsSuffix = ".edits.json";

    /// <summary>Returns the edits path that pairs with a baseline document path.</summary>
    /// <param name="baselinePath">Path to the generated baseline.</param>
    /// <returns>Path to its edits file, which may not exist yet.</returns>
    public static string EditsPathFor(string baselinePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePath);

        string directory = Path.GetDirectoryName(baselinePath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(baselinePath);
        return Path.Combine(directory, name + EditsSuffix);
    }

    /// <summary>Loads a document, or returns null when the file does not exist.</summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="path">File to read.</param>
    /// <param name="diagnostics">Receives an error when the file exists but cannot be parsed.</param>
    /// <returns>The document, or null.</returns>
    public static T? TryLoad<T>(string path, DiagnosticBag diagnostics)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ManifestJson.Options);
        }
        catch (JsonException ex)
        {
            // A malformed edits file is a hand-editing mistake, and silently ignoring it
            // would look exactly like the corrections having no effect.
            diagnostics.Add(new Diagnostic(
                "GK3R3010",
                DiagnosticSeverity.Error,
                $"Could not parse {typeof(T).Name}: {ex.Message}",
                path,
                ex.BytePositionInLine,
                "well-formed JSON matching the document schema",
                "a parse error",
                "Fix the JSON syntax. The generated baseline beside it is a working example."));
            return null;
        }
    }

    /// <summary>Writes a document, replacing any existing file atomically.</summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="path">File to write.</param>
    /// <param name="document">Document to serialize.</param>
    public static void Save<T>(string path, T document) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(document, ManifestJson.Options) + "\n");
}

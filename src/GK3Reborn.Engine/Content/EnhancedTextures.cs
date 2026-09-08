using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Higher-resolution textures, standing in front of the archives.
/// </summary>
public sealed class EnhancedTextures
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names this language repainted, out of the whole set above.</summary>
    private readonly HashSet<string> _localized = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names the player's own <c>overrides/</c> answer for.</summary>
    private readonly HashSet<string> _overridden = new(StringComparer.OrdinalIgnoreCase);

    private EnhancedTextures(string directory) => Directory = directory;

    /// <summary>Where the textures were read from.</summary>
    public string Directory { get; }

    /// <summary>How many textures are available.</summary>
    public int Count => _files.Count;

    /// <summary>The names, in a stable order.</summary>
    public IReadOnlyList<string> Names =>
        [.. _files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>How many of the textures came from <c>overrides/</c>.</summary>
    public int OverriddenCount { get; private set; }

    /// <summary>How many of them this language redoes.</summary>
    public int LocalizedCount { get; private set; }

    /// <summary>Indexes a directory of enhanced textures.</summary>
    /// <param name="directory">Where they are.</param>
    /// <returns>The set, empty when the directory does not exist.</returns>
    public static EnhancedTextures Open(string directory) => Open(directory, null);

    /// <summary>Indexes a directory of enhanced textures, with overrides in front.</summary>
    /// <param name="directory">Where they are. May be empty for the overrides alone.</param>
    /// <param name="overrides">Files dropped into <c>overrides/</c>, or null for none.</param>
    /// <param name="kind">Which of the overrides' sets to take, colour by default.</param>
    /// <returns>The set, empty when neither has anything.</returns>
    public static EnhancedTextures Open(
        string directory, ContentOverrides? overrides, RebarnKind kind = RebarnKind.Texture) =>
        Open(directory, overrides, kind, string.Empty);

    /// <summary>
    /// Indexes a directory of enhanced textures, with a language's own and the overrides
    /// laid over it.
    /// </summary>
    /// <param name="directory">Where the shared set is. May be empty.</param>
    /// <param name="overrides">Files dropped into <c>overrides/</c>, or null for none.</param>
    /// <param name="kind">Which of the overrides' sets to take, colour by default.</param>
    /// <param name="localizedDirectory">
    /// The workspace's <c>enhanced/localtextures/&lt;CODE&gt;</c>, or empty for none. The
    /// loose form of what the language pack holds, laid over the shared set for the same
    /// reason the overrides are laid over both: there is one answer per name and this is
    /// it.
    /// </param>
    /// <returns>The set, empty when none of them has anything.</returns>
    public static EnhancedTextures Open(
        string directory,
        ContentOverrides? overrides,
        RebarnKind kind,
        string localizedDirectory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(localizedDirectory);

        var set = new EnhancedTextures(directory);

        Index(directory, set._files);

        // Over the shared set: a French sign where there is one, and the shared picture
        // everywhere else. Counted, because a language whose set indexed nothing looks on
        // screen exactly like one whose set indexed everything.
        set.LocalizedCount = Index(localizedDirectory, set._files, set._localized);

        if (overrides is not null)
        {
            foreach ((string name, string file) in overrides.Images(kind))
            {
                set._files[name] = file;
                set._overridden.Add(name);
                set.OverriddenCount++;
            }
        }

        return set;
    }

    /// <summary>Indexes one directory's PNGs into a name table, and says how many.</summary>
    private static int Index(
        string directory, Dictionary<string, string> into, HashSet<string>? note = null)
    {
        if (directory.Length == 0 || !System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        int found = 0;

        foreach (string file in System.IO.Directory.EnumerateFiles(directory))
        {
            if (Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);

                into[name] = file;
                note?.Add(name);
                found++;
            }
        }

        return found;
    }

    /// <summary>Whether this set's answer for a name was repainted for the language.</summary>
    /// <param name="name">Texture name, with or without an extension.</param>
    /// <returns>
    /// True when the picture came from <c>enhanced/localtextures/&lt;CODE&gt;</c> rather
    /// than from the shared set.
    /// </returns>
    public bool IsLocalized(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _localized.Contains(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Whether this set's answer for a name came from <c>overrides/</c>.</summary>
    /// <param name="name">Texture name, with or without an extension.</param>
    /// <returns>True when the player's own file answers for it.</returns>
    public bool IsOverridden(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _overridden.Contains(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Whether there is an enhanced version of a texture.</summary>
    /// <param name="name">Texture name, with or without an extension.</param>
    /// <returns>True when there is one.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _files.ContainsKey(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Reads an enhanced texture.</summary>
    /// <param name="name">Texture name, with or without an extension.</param>
    /// <param name="diagnostics">Receives a diagnostic when one will not decode.</param>
    /// <returns>The image, or null when there is no enhanced version or it is unreadable.</returns>
    public DecodedImage? Read(string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_files.TryGetValue(Path.GetFileNameWithoutExtension(name), out string? file))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(file);

            // A PNG unless it is one of the other things a player may drop in. Decided from
            // the bytes rather than from the extension, because an override named .png that
            // is really a bitmap is a mistake worth surviving, and the two decoders each
            // recognise their own header anyway.
            return BitmapDecoder.CanDecode(bytes)
                ? BitmapDecoder.Decode(bytes, file)
                : PngReader.Decode(bytes, file);
        }
        catch (Exception ex) when (ex is FormatParseException or IOException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1093",
                DiagnosticSeverity.Warning,
                $"The enhanced {name} will not load, so the original is used: {ex.Message}",
                file,
                null,
                "a readable PNG",
                ex.GetType().Name,
                "Produce the texture again, or take it out of the enhanced set."));

            return null;
        }
    }
}

using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Higher-resolution textures, standing in front of the archives.
/// </summary>
/// <remarks>
/// <para>
/// The originals are 213 megapixels across 6,657 textures, and the surfaces a player looks
/// at most — tier 0 in <c>docs/texture-enhancement.md</c> — are 5.8 megapixels of that
/// between them. Replacing one is a matter of putting a PNG under the name the geometry
/// already uses: the UV layout does not change, so nothing else has to.
/// </para>
/// <para>
/// A layer rather than a rewrite. The archives stay exactly as they are, and asking for a
/// texture that has no enhanced version gets the original, so a partial set is a perfectly
/// good set. That also makes it possible to render a scene with and without and put the
/// two side by side, which is the only way to judge this work.
/// </para>
/// <para>
/// Names are matched without their extension and without regard to case, because a
/// surface refers to <c>R25WALLS</c> while the archive holds <c>R25WALLS.BMP</c> and the
/// enhanced set holds <c>R25WALLS.PNG</c>.
/// </para>
/// </remarks>
public sealed class EnhancedTextures
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Indexes a directory of enhanced textures.</summary>
    /// <param name="directory">Where they are.</param>
    /// <returns>The set, empty when the directory does not exist.</returns>
    /// <remarks>
    /// A missing directory is not an error. Enhanced content is optional by design — the
    /// game runs from a legally obtained installation and these are an addition to it.
    /// </remarks>
    public static EnhancedTextures Open(string directory) => Open(directory, null);

    /// <summary>Indexes a directory of enhanced textures, with overrides in front.</summary>
    /// <param name="directory">Where they are. May be empty for the overrides alone.</param>
    /// <param name="overrides">Files dropped into <c>overrides/</c>, or null for none.</param>
    /// <param name="kind">Which of the overrides' sets to take, colour by default.</param>
    /// <returns>The set, empty when neither has anything.</returns>
    /// <remarks>
    /// The overrides are laid over the directory rather than searched after it, because
    /// there is only ever one answer per name and the override is it. This layer is the one
    /// every caller in the loader asks <em>before</em> the compressed set, so putting a
    /// player's PNG here is what makes it beat a packed BC7 of the same name — which is the
    /// thing an override has to do to be worth having.
    /// </remarks>
    public static EnhancedTextures Open(
        string directory, ContentOverrides? overrides, RebarnKind kind = RebarnKind.Texture)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var set = new EnhancedTextures(directory);

        // Matched here rather than by a "*.png" search pattern, which is case-sensitive on
        // Linux and would make R25WALLS.PNG invisible there while finding it on Windows and
        // macOS. The game's own names are upper case throughout, so generated content
        // carries that extension as often as not.
        if (directory.Length > 0 && System.IO.Directory.Exists(directory))
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(directory))
            {
                if (Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    set._files[Path.GetFileNameWithoutExtension(file)] = file;
                }
            }
        }

        if (overrides is not null)
        {
            foreach ((string name, string file) in overrides.Images(kind))
            {
                set._files[name] = file;
                set.OverriddenCount++;
            }
        }

        return set;
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
    /// <remarks>
    /// A texture that will not decode falls back to the original rather than failing the
    /// load. Enhanced content is a draft until somebody has looked at it, and one bad file
    /// in a set of hundreds should cost that texture and nothing else.
    /// </remarks>
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

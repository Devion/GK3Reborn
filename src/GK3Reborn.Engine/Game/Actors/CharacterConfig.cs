using GK3Reborn.Content;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game.Actors;

/// <summary>What the game records about one character.</summary>
/// <param name="Identifier">The three-letter code the file lists them under.</param>
/// <param name="WalkerHeight">How tall they are, in scene units.</param>
/// <param name="StartAnimation">The animation that gets them moving from standing.</param>
/// <param name="WalkAnimation">The stride, looped for as long as they are walking.</param>
/// <param name="StopAnimation">The animation that brings them to a halt.</param>
public sealed record CharacterConfig(
    string Identifier,
    float WalkerHeight,
    string? StartAnimation,
    string? WalkAnimation,
    string? StopAnimation);

/// <summary>
/// <c>CHARACTERS.TXT</c> — who the game's people are and how they move.
/// </summary>
/// <remarks>
/// <para>
/// An INI file, one section a character, keyed by the three-letter code the models use:
/// <c>GAB</c>, <c>GRA</c>, <c>ABE</c>. Most of what it holds is for things not built yet —
/// blink timings, mouth coordinates, the shoe and hip vertices a walker's ground is
/// measured from — so only the walk is read, and the rest is left in the file rather than
/// parsed into fields nothing reads.
/// </para>
/// <para>
/// The walk animations are the point. Without them an actor crosses a room in whatever
/// pose they were standing in, which is the single most obviously wrong thing about a
/// character in motion.
/// </para>
/// </remarks>
public sealed class CharacterLibrary
{
    private readonly Dictionary<string, CharacterConfig> _characters =
        new(StringComparer.OrdinalIgnoreCase);

    private CharacterLibrary()
    {
    }

    /// <summary>How many characters the file described.</summary>
    public int Count => _characters.Count;

    /// <summary>Reads the file out of the archives.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The set, empty when there is no such file.</returns>
    public static CharacterLibrary Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        var library = new CharacterLibrary();

        return archives.ReadText("CHARACTERS.TXT") is { } text ? Parse(text) : library;
    }

    /// <summary>Reads the file's text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>The set.</returns>
    public static CharacterLibrary Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var library = new CharacterLibrary();

        // One entry a line here, unlike the scene files: a walk animation is named
        // "Gabwalk" and a comma in a value would be part of the name, not a separator.
        foreach (IniSection section in IniDocument
                     .Parse(text, "CHARACTERS.TXT", multipleEntriesPerLine: false).Sections)
        {
            IniLine? Line(string key) => section.Lines.FirstOrDefault(
                l => string.Equals(l.Head.Key, key, StringComparison.OrdinalIgnoreCase));

            string? Value(string key) => Line(key)?.Head.Value;

            library._characters[section.Name] = new CharacterConfig(
                section.Name,
                Line("WalkerHeight")?.Head.AsNumber() ?? 0f,
                Value("StartAnim"),
                Value("ContAnim"),
                Value("StopAnim"));
        }

        return library;
    }

    /// <summary>Finds a character by the name a model or a scene uses.</summary>
    /// <param name="name">A model name, which may carry more than the character's code.</param>
    /// <returns>Their configuration, or null.</returns>
    /// <remarks>
    /// A scene places <c>gab</c> and the file lists <c>GAB</c>, so most names match outright.
    /// Where they do not, the first three characters are the code: <c>gabclothes110a</c> and
    /// the other clothing variants are all Gabriel, and each has its own section only when
    /// it walks differently.
    /// </remarks>
    public CharacterConfig? Of(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        if (_characters.TryGetValue(name, out CharacterConfig? exact))
        {
            return exact;
        }

        return name.Length >= 3 && _characters.TryGetValue(name[..3], out CharacterConfig? code)
            ? code
            : null;
    }
}

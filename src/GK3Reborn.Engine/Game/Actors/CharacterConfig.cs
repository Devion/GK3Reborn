using GK3Reborn.Content;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game.Actors;

/// <summary>One of the axis triads standing in for a bone.</summary>
/// <param name="Mesh">Which of the model's meshes it is.</param>
/// <param name="Group">Which group within that mesh.</param>
/// <param name="Point">Which vertex of that group.</param>
/// <remarks>
/// GK3's characters have no skeleton — they are a dozen meshes each posed outright — so
/// there is nothing to ask where a hip or a foot is. What there is instead is three small
/// triads of vertices, one at the hips and one under each shoe, carried along by the same
/// vertex animation as the body and sitting some sixty units out from it. They are the
/// only handle on where a character is standing and which way they are facing.
/// </remarks>
public readonly record struct CharacterAxes(int Mesh, int Group, int Point);

/// <summary>What the game records about one character.</summary>
/// <param name="Identifier">The three-letter code the file lists them under.</param>
/// <param name="WalkerHeight">How tall they are, in scene units.</param>
/// <param name="StartAnimation">The animation that gets them moving from standing.</param>
/// <param name="WalkAnimation">The stride, looped for as long as they are walking.</param>
/// <param name="StopAnimation">The animation that brings them to a halt.</param>
/// <param name="Hips">The triad at the hips, which is where the character stands.</param>
/// <param name="LeftShoe">The triad under the left shoe.</param>
/// <param name="RightShoe">The triad under the right shoe.</param>
/// <param name="ShoeType">
/// What they have on their feet — "Male Leather", "Female Heels" — which with the floor
/// underfoot decides what a step sounds like. See <see cref="Footsteps"/>.
/// </param>
public sealed record CharacterConfig(
    string Identifier,
    float WalkerHeight,
    string? StartAnimation,
    string? WalkAnimation,
    string? StopAnimation,
    CharacterAxes? Hips = null,
    CharacterAxes? LeftShoe = null,
    CharacterAxes? RightShoe = null,
    string? ShoeType = null)
{
    /// <summary>Whether the game records this character as a woman.</summary>
    /// <remarks>
    /// Out of <see cref="ShoeType"/>, which every one of the file's 45 characters gives and
    /// which begins with the word: "Female Leather", "Male Boot". It is there to pick a
    /// footstep sound and it is also the only thing in the shipped data that says which of
    /// them is a man and which a woman — so it is what the interface calls somebody the
    /// player has not been introduced to yet. Null when the file says nothing.
    /// </remarks>
    public bool? IsWoman =>
        ShoeType is not { Length: > 0 } shoes
            ? null
            : shoes.StartsWith("Female", StringComparison.OrdinalIgnoreCase)
                ? true
                : shoes.StartsWith("Male", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : null;
}

/// <summary>
/// <c>CHARACTERS.TXT</c> — who the game's people are and how they move.
/// </summary>
/// <remarks>
/// <para>
/// An INI file, one section a character, keyed by the three-letter code the models use:
/// <c>GAB</c>, <c>GRA</c>, <c>ABE</c>. Much of what it holds is for things not built yet —
/// blink timings, mouth coordinates, the field of view a head turns within — and that is
/// left in the file rather than parsed into fields nothing reads.
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

            CharacterAxes? Axes(string prefix)
            {
                if (Line(prefix + "AxesMeshIndex")?.Head.AsNumber() is not { } mesh)
                {
                    return null;
                }

                return new CharacterAxes(
                    (int)mesh,
                    (int)(Line(prefix + "AxesGroupIndex")?.Head.AsNumber() ?? 0f),
                    (int)(Line(prefix + "AxesPointIndex")?.Head.AsNumber() ?? 0f));
            }

            library._characters[section.Name] = new CharacterConfig(
                section.Name,
                Line("WalkerHeight")?.Head.AsNumber() ?? 0f,
                Value("StartAnim"),
                Value("ContAnim"),
                Value("StopAnim"),
                Axes("Hip"),
                Axes("LShoe"),
                Axes("RShoe"),
                Value("ShoeType"));
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

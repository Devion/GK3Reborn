using GK3Reborn.Content;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game.Actors;

/// <summary>One of the axis triads standing in for a bone.</summary>
/// <param name="Mesh">Which of the model's meshes it is.</param>
/// <param name="Group">Which group within that mesh.</param>
/// <param name="Point">Which vertex of that group.</param>
public readonly record struct CharacterAxes(int Mesh, int Group, int Point);

/// <summary>One of a character's changes of clothes.</summary>
/// <param name="When">
/// The timeblock code the change happens at — <c>207a</c> — or <c>Default</c> for what
/// they are wearing before any of the dated changes apply.
/// </param>
/// <param name="Animation">
/// The animation that dresses them. It is one frame long and holds nothing but
/// <c>[MTEXTURES]</c> lines, which is how a change of clothes is expressed: the model is
/// the same and its surfaces are repainted.
/// </param>
public readonly record struct CharacterClothes(string When, string Animation);

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
/// <param name="ShoeThickness">
/// How far the shoe triads sit above the ground the character is standing on, in scene
/// units. Between a quarter of a unit and eighteen and a half across the cast; the
/// reference's default for a character who does not say is 0.75, which is Gabriel's.
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
    string? ShoeType = null,
    float ShoeThickness = 0.75f)
{
    /// <summary>The thickness assumed for a character whose entry omits it.</summary>
    public const float DefaultShoeThickness = 0.75f;

    /// <summary>Whether the game records this character as a woman.</summary>
    public bool? IsWoman =>
        ShoeType is not { Length: > 0 } shoes
            ? null
            : shoes.StartsWith("Female", StringComparison.OrdinalIgnoreCase)
                ? true
                : shoes.StartsWith("Male", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : null;

    /// <summary>
    /// What they change into and when, in the order the file lists it.
    /// </summary>
    public IReadOnlyList<CharacterClothes> Clothes { get; init; } = [];

    /// <summary>
    /// Which animation dresses this character at a point in the story.
    /// </summary>
    /// <param name="now">The story's timeblock, or null when nothing has said.</param>
    /// <returns>The animation's name, or null when the file gives them no clothes.</returns>
    public string? ClothingFor(Timeblock? now)
    {
        string? chosen = null;

        foreach (CharacterClothes clothes in Clothes)
        {
            if (clothes.When.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                chosen ??= clothes.Animation;
            }
            else if (now is { } today &&
                     Timeblock.TryParse(clothes.When, out Timeblock when) &&
                     today >= when)
            {
                chosen = clothes.Animation;
            }
        }

        return chosen;
    }
}

/// <summary>
/// <c>CHARACTERS.TXT</c> — who the game's people are and how they move.
/// </summary>
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

            // In the order they are written, because that is what decides between two
            // that both apply. See CharacterConfig.ClothingFor.
            List<CharacterClothes> clothes = [.. section.Lines
                .Select(l => l.Head)
                .Where(e => e.Key is { Length: > 7 } key &&
                            key.StartsWith("Clothes", StringComparison.OrdinalIgnoreCase) &&
                            e.Value is { Length: > 0 })
                .Select(e => new CharacterClothes(e.Key[7..], e.Value))];

            library._characters[section.Name] = new CharacterConfig(
                section.Name,
                Line("WalkerHeight")?.Head.AsNumber() ?? 0f,
                Value("StartAnim"),
                Value("ContAnim"),
                Value("StopAnim"),
                Axes("Hip"),
                Axes("LShoe"),
                Axes("RShoe"),
                Value("ShoeType"),
                Line("ShoeThickness")?.Head.AsNumber()
                    ?? CharacterConfig.DefaultShoeThickness)
            {
                Clothes = clothes,
            };
        }

        return library;
    }

    /// <summary>Finds a character by the name a model or a scene uses.</summary>
    /// <param name="name">A model name, which may carry more than the character's code.</param>
    /// <returns>Their configuration, or null.</returns>
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

using GK3Reborn.Content;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game.Actions;

/// <summary>What kind of thing a verb in an action file is.</summary>
public enum VerbKind
{
    /// <summary>Something to do to a thing: look, open, take.</summary>
    Normal,

    /// <summary>An inventory item used on a thing.</summary>
    Inventory,

    /// <summary>Something to say to somebody. Its name begins <c>T_</c>.</summary>
    Topic,

    /// <summary>A topic that may be raised again after it has been said.</summary>
    RecurringTopic,

    /// <summary>Small talk. One verb, <c>Z_CHAT</c>, and it behaves like an ordinary one.</summary>
    Chat,
}

/// <summary>
/// <c>VERBS.TXT</c> — every verb the game knows, and which kind each one is.
/// </summary>
public sealed class VerbLibrary
{
    private readonly Dictionary<string, VerbKind> _verbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Up, string Hover)> _art =
        new(StringComparer.OrdinalIgnoreCase);

    private VerbLibrary()
    {
    }

    /// <summary>How many verbs the file described.</summary>
    public int Count => _verbs.Count;

    /// <summary>How many of them it gave a picture.</summary>
    public int IconCount => _art.Count;

    /// <summary>How many of them are topics, recurring or not.</summary>
    public int TopicCount => _verbs.Values.Count(v => v is VerbKind.Topic or VerbKind.RecurringTopic);

    /// <summary>Reads the file out of the archives.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The set, empty when there is no such file.</returns>
    public static VerbLibrary Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return archives.ReadText("VERBS.TXT") is { } text ? Parse(text) : new VerbLibrary();
    }

    /// <summary>Reads the file's text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>The set.</returns>
    public static VerbLibrary Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var library = new VerbLibrary();

        foreach (IniLine line in IniDocument.Parse(text, "VERBS.TXT").LinesOf("VERBS"))
        {
            if (line.Head.Key is not { Length: > 0 } verb)
            {
                continue;
            }

            // The type is one of the entries after the name, and most lines leave it out —
            // a verb with no type is an ordinary one.
            library._verbs[verb] = line.Value("type")?.ToUpperInvariant() switch
            {
                "INVENTORY" => VerbKind.Inventory,
                "TOPIC" => VerbKind.Topic,
                "RECURRINGTOPIC" => VerbKind.RecurringTopic,
                "CHAT" => VerbKind.Chat,
                _ => VerbKind.Normal,
            };

            // The lit picture falls back to the resting one. Three verbs give no pictures
            // at all — CLICK, SELECT and WRITE — and WALK_DOWN names a resting one and no
            // hover, so a verb without the second is drawn resting rather than not drawn.
            if (line.Value("up") is { Length: > 0 } up)
            {
                library._art[verb] = (up, line.Value("hover") is { Length: > 0 } hover ? hover : up);
            }
        }

        return library;
    }

    /// <summary>What kind of thing a verb is.</summary>
    /// <param name="verb">The verb, as an action file writes it.</param>
    /// <returns>Its kind; <see cref="VerbKind.Normal"/> for one the file does not list.</returns>
    public VerbKind KindOf(string? verb) =>
        verb is { Length: > 0 } && _verbs.TryGetValue(verb, out VerbKind kind)
            ? kind
            : VerbKind.Normal;

    /// <summary>The file holding a verb's picture.</summary>
    /// <param name="verb">The verb, as an action file writes it.</param>
    /// <param name="lit">Whether the player has the verb picked out.</param>
    /// <returns>The bitmap's name, or null when the file gives the verb no picture.</returns>
    public string? IconOf(string? verb, bool lit = false)
    {
        if (verb is not { Length: > 0 } || !_art.TryGetValue(verb, out (string Up, string Hover) art))
        {
            return null;
        }

        return string.Concat((lit ? art.Hover : art.Up).ToUpperInvariant(), ".BMP");
    }

    /// <summary>
    /// Whether the file lists a verb at all.
    /// </summary>
    /// <param name="verb">The verb, as an action file writes it.</param>
    /// <returns>True when <c>VERBS.TXT</c> names it.</returns>
    /// <remarks>
    /// An action file writes rules for things the player can never pick — <c>TIMER_EXP</c>,
    /// <c>EMILIO_TIMER</c>, <c>ENTER</c>, <c>WALK</c> — which scripts and the engine fire by
    /// name. The only thing separating those from a real verb is that the file does not list
    /// them, so anything it does not list is not on the menu.
    /// </remarks>
    public bool Knows(string? verb) => verb is { Length: > 0 } && _verbs.ContainsKey(verb);

    /// <summary>Whether a verb is something to say rather than something to do.</summary>
    /// <param name="verb">The verb.</param>
    /// <returns>True for a topic, recurring or not.</returns>
    public bool IsTopic(string? verb) =>
        KindOf(verb) is VerbKind.Topic or VerbKind.RecurringTopic;

    /// <summary>Whether a topic may be raised again once it has been said.</summary>
    /// <param name="verb">The verb.</param>
    /// <returns>True for a recurring topic.</returns>
    public bool IsRecurring(string? verb) => KindOf(verb) == VerbKind.RecurringTopic;
}

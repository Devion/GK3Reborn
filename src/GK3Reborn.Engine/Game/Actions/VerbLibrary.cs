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
}

/// <summary>
/// <c>VERBS.TXT</c> — every verb the game knows, and which kind each one is.
/// </summary>
/// <remarks>
/// <para>
/// One INI section holding 287 lines, each naming a verb and the icons that draw it. What
/// matters here is the <c>type</c> at the end: <c>Normal</c>, <c>Inventory</c>,
/// <c>Topic</c> or <c>RecurringTopic</c>. There are 88 topics and one recurring one.
/// </para>
/// <para>
/// The distinction is the whole of what separates talking from doing. An action file makes
/// no distinction — a topic is written exactly like a verb, <c>BUTHANE, T_TOUR_GROUP,
/// CASE, script={...}</c> — so without this file there is no way to tell "ask her about the
/// tour group" from "open her". Guessing by the <c>T_</c> prefix gets close and gets the
/// recurring ones wrong, which is the difference between a topic that is used up and one
/// that may be raised again.
/// </para>
/// </remarks>
public sealed class VerbLibrary
{
    private readonly Dictionary<string, VerbKind> _verbs = new(StringComparer.OrdinalIgnoreCase);

    private VerbLibrary()
    {
    }

    /// <summary>How many verbs the file described.</summary>
    public int Count => _verbs.Count;

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
                _ => VerbKind.Normal,
            };
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

    /// <summary>Whether a verb is something to say rather than something to do.</summary>
    /// <param name="verb">The verb.</param>
    /// <returns>True for a topic, recurring or not.</returns>
    public bool IsTopic(string? verb) =>
        KindOf(verb) is VerbKind.Topic or VerbKind.RecurringTopic;

    /// <summary>Whether a topic may be raised again once it has been said.</summary>
    /// <param name="verb">The verb.</param>
    /// <returns>True for a recurring topic.</returns>
    /// <remarks>
    /// One verb in the game is declared this way. The original hard-codes a handful more —
    /// the handshakes and <c>T_DEAD_GUYS_X</c> — with a note saying it is unclear how the
    /// original told them apart. Those are not reproduced here: a list of exceptions nobody
    /// can derive is a list that will be wrong in a way nobody can check.
    /// </remarks>
    public bool IsRecurring(string? verb) => KindOf(verb) == VerbKind.RecurringTopic;
}

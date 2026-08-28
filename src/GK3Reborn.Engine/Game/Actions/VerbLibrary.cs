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
    /// <remarks>
    /// A kind of its own only so that the interface can call it something a player would
    /// recognise. <c>Z_CHAT</c> is what the data calls it, and a menu that offers "Z Chat"
    /// beside "Talk" is the internal name leaking onto the screen.
    /// </remarks>
    Chat,
}

/// <summary>
/// <c>VERBS.TXT</c> — every verb the game knows, and which kind each one is.
/// </summary>
/// <remarks>
/// <para>
/// One INI section holding 287 lines, each naming a verb and the icons that draw it. Two
/// things matter here. The <c>type</c> at the end — <c>Normal</c>, <c>Inventory</c>,
/// <c>Topic</c> or <c>RecurringTopic</c>; there are 88 topics and one recurring one. And
/// the <c>up</c> and <c>hover</c> pictures, which are the whole of what the original ever
/// showed for a verb: its ring was icons and no words, so this file is the only place that
/// says what "look" looks like.
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
    /// <remarks>
    /// <para>
    /// The names in the file are written lowercase and without an extension —
    /// <c>i_look_std</c> — and what is in the archives is <c>I_LOOK_STD.BMP</c>, a 32-pixel
    /// square in GK3's own container. So the name is finished here rather than at every
    /// call site that wants one.
    /// </para>
    /// <para>
    /// Three verbs name a picture that is not in the archives — <c>COIL</c>,
    /// <c>WALK_DOWN</c> and <c>ZOOM</c> — which this cannot tell and does not try to: a
    /// name that resolves to nothing is a verb drawn by its words alone, the same as one
    /// the file never named.
    /// </para>
    /// </remarks>
    public string? IconOf(string? verb, bool lit = false)
    {
        if (verb is not { Length: > 0 } || !_art.TryGetValue(verb, out (string Up, string Hover) art))
        {
            return null;
        }

        return string.Concat((lit ? art.Hover : art.Up).ToUpperInvariant(), ".BMP");
    }

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

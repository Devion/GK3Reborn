using GK3Reborn.Game.Actions;
using GK3Reborn.Platform;

namespace GK3Reborn.Game;

/// <summary>
/// Which pointer to show over what is under it: the shape says what a click would do.
/// </summary>
public static class PointerChoice
{
    /// <summary>
    /// The verbs that leave a room, for a game whose VERBS.TXT is missing or says nothing
    /// about cursors. The shipped file marks every one of these with a <c>c_exit_*</c>
    /// cursor, and that column is what is read when it is there.
    /// </summary>
    private static readonly HashSet<string> Departures = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXIT", "EXIT_UP", "EXIT_DOWN", "EXIT_LEFT", "EXIT_RIGHT", "GO_UP", "GO_DOWN", "ENTER",
    };

    /// <summary>Decides the pointer.</summary>
    /// <param name="hover">What is under it, and what that answers to.</param>
    /// <param name="claimed">
    /// What the room's own machinery says a click does, or null when it has no claim on
    /// it. Any claim at all is a click that will be taken, whether or not the room found
    /// a word for it.
    /// </param>
    /// <param name="menuOpen">Whether the verb bar is up, in which case it takes the click.</param>
    /// <param name="verbs">The game's verbs, for telling a topic from a deed. May be null.</param>
    /// <returns>The shape.</returns>
    public static PointerShape For(Hover hover, string? claimed, bool menuOpen, VerbLibrary? verbs)
    {
        if (menuOpen)
        {
            return PointerShape.Default;
        }

        if (claimed is not null)
        {
            return PointerShape.Interact;
        }

        if (!hover.Actionable || hover.Default is not { Length: > 0 } verb)
        {
            return PointerShape.Default;
        }

        if (GameStrings.IsNumberedExit(hover.Noun) || LeadsOut(verb, verbs))
        {
            return PointerShape.Exit;
        }

        if (IsTalk(verb, verbs))
        {
            return PointerShape.Talk;
        }

        return verb.Equals("LOOK", StringComparison.OrdinalIgnoreCase)
            ? PointerShape.Look
            : PointerShape.Interact;
    }

    /// <summary>Whether a verb takes the player out of the room.</summary>
    private static bool LeadsOut(string verb, VerbLibrary? verbs) =>
        verbs?.LeadsOut(verb) == true || Departures.Contains(verb);

    /// <summary>Whether a verb is something said rather than something done.</summary>
    private static bool IsTalk(string verb, VerbLibrary? verbs)
    {
        if (verb.Equals("TALK", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Z_CHAT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (verbs is not null)
        {
            return verbs.IsTopic(verb) || verbs.KindOf(verb) == VerbKind.Chat;
        }

        // No file to ask, so the naming rule it enforces is applied by hand: every topic
        // begins T_.
        return verb.StartsWith("T_", StringComparison.OrdinalIgnoreCase);
    }
}

namespace GK3Reborn.Game;

/// <summary>
/// The moments where a conversation topic is not a question.
/// </summary>
/// <remarks>
/// A topic's words belong to the verb and are the same wherever it is raised, which is
/// right almost everywhere and wrong in the denouement: day 3 at 303P is a quiz, its
/// answers are topics, and naming the thief read "Ask about Mosely". Reported as such.
/// A moment that reads differently names a set of its own, and the interface looks there
/// before the ordinary words.
/// </remarks>
public static class TopicWording
{
    /// <summary>
    /// The dining room at 303P: Gabriel names who took the manuscript and what became of
    /// it, by picking topics on Grace.
    /// </summary>
    public const string Denouement = "DENOUEMENT";

    /// <summary>The room the denouement is played in.</summary>
    private const string DiningRoom = "DIN";

    /// <summary>
    /// The game's own counter for which of the four questions is open, one to four.
    /// Nought before the first and five after the last, which is when the topics go back
    /// to being questions.
    /// </summary>
    private const string Question = "DonutCurrentQuestion";

    /// <summary>
    /// Which set of words the moment wants, if it wants one of its own.
    /// </summary>
    /// <param name="story">The story as it stands.</param>
    /// <param name="room">The room being played, by its three-letter code.</param>
    /// <returns>The set's name, or null for the ordinary words.</returns>
    public static string? For(GameState? story, string? room)
    {
        if (story is null || room is not { Length: > 0 } ||
            !room.Equals(DiningRoom, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Timeblock now = story.Timeblock;

        if (now.Day != 3 || now.Hour != 3 || !now.IsAfternoon)
        {
            return null;
        }

        return story.GetVariable(Question) is >= 1 and <= 4 ? Denouement : null;
    }
}

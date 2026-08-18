using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GK3Reborn.Game;

/// <summary>
/// The game's observable state: what scripts read and write.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is what the plan's differential harness compares between
/// implementations. Presentation — camera cuts, animations, dialogue — is recorded as
/// events rather than held as state, because two engines can draw a scene differently
/// and still be equivalent, while disagreeing about a flag means the game diverges.
/// </para>
/// <para>
/// Names are case-insensitive throughout, matching the language: the specification says
/// upper and lower case are the same, and scripts spell the same flag several ways.
/// </para>
/// </remarks>
public sealed class GameState
{
    private readonly Dictionary<string, int> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _nounVerbCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _topicCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _actorLocations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current timeblock, such as <c>110A</c>.</summary>
    public Timeblock Timeblock { get; set; } = new(1, 10, IsAfternoon: false);

    /// <summary>Three-letter code of the current location.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Name of the actor the player controls.</summary>
    public string Ego { get; set; } = "GABRIEL";

    /// <summary>The player's score.</summary>
    public int Score { get; private set; }

    /// <summary>Reads a game variable. Unset variables read as zero.</summary>
    public int GetVariable(string name) => _variables.GetValueOrDefault(Key(name));

    /// <summary>Writes a game variable.</summary>
    public void SetVariable(string name, int value) => _variables[Key(name)] = value;

    /// <summary>Adds to a game variable and returns the new value.</summary>
    public int IncrementVariable(string name, int by)
    {
        string key = Key(name);
        int value = _variables.GetValueOrDefault(key) + by;
        _variables[key] = value;
        return value;
    }

    /// <summary>Whether a flag is set.</summary>
    public bool GetFlag(string name) => _flags.Contains(Key(name));

    /// <summary>Sets a flag.</summary>
    public void SetFlag(string name) => _flags.Add(Key(name));

    /// <summary>Clears a flag.</summary>
    public void ClearFlag(string name) => _flags.Remove(Key(name));

    /// <summary>
    /// How many times a noun/verb pair has been used.
    /// </summary>
    /// <remarks>
    /// GK3 gates a great deal of dialogue on these counts — the second time you ask about
    /// something you get a different answer — so they are game state, not statistics.
    /// </remarks>
    public int GetNounVerbCount(string noun, string verb) =>
        _nounVerbCounts.GetValueOrDefault(Pair(noun, verb));

    /// <summary>Sets a noun/verb count.</summary>
    public void SetNounVerbCount(string noun, string verb, int value) =>
        _nounVerbCounts[Pair(noun, verb)] = value;

    /// <summary>Adds one to a noun/verb count.</summary>
    public void IncrementNounVerbCount(string noun, string verb) =>
        _nounVerbCounts[Pair(noun, verb)] = GetNounVerbCount(noun, verb) + 1;

    /// <summary>How many times a conversation topic has come up.</summary>
    public int GetTopicCount(string noun, string topic) =>
        _topicCounts.GetValueOrDefault(Pair(noun, topic));

    /// <summary>Sets a topic count.</summary>
    public void SetTopicCount(string noun, string topic, int value) =>
        _topicCounts[Pair(noun, topic)] = value;

    /// <summary>Where an actor currently is.</summary>
    public string GetActorLocation(string actor) =>
        _actorLocations.GetValueOrDefault(Key(actor), string.Empty);

    /// <summary>Moves an actor to a location.</summary>
    public void SetActorLocation(string actor, string location) =>
        _actorLocations[Key(actor)] = location;

    /// <summary>Adds to the score.</summary>
    public void ChangeScore(int by) => Score += by;

    /// <summary>
    /// A hash of everything observable, for comparing runs.
    /// </summary>
    /// <remarks>
    /// Ordering is made explicit before hashing. Dictionary enumeration order is not
    /// guaranteed, and a state hash that changed between runs of the same build would be
    /// useless for exactly the comparison it exists to support.
    /// </remarks>
    public string ComputeHash()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"timeblock={Timeblock}\n");
        builder.Append(CultureInfo.InvariantCulture, $"location={Location}\n");
        builder.Append(CultureInfo.InvariantCulture, $"ego={Ego}\n");
        builder.Append(CultureInfo.InvariantCulture, $"score={Score}\n");

        Append(builder, "flag", _flags.OrderBy(f => f, StringComparer.Ordinal).Select(f => (f, "1")));
        Append(builder, "var", Ordered(_variables));
        Append(builder, "nounverb", Ordered(_nounVerbCounts));
        Append(builder, "topic", Ordered(_topicCounts));
        Append(builder, "actor", _actorLocations
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value)));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static IEnumerable<(string, string)> Ordered(Dictionary<string, int> values) =>
        values.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture)));

    private static void Append(StringBuilder builder, string prefix, IEnumerable<(string Key, string Value)> items)
    {
        foreach ((string key, string value) in items)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{prefix}:{key}={value}\n");
        }
    }

    private static string Key(string name) => name.Trim().ToUpperInvariant();

    private static string Pair(string first, string second) => $"{Key(first)}|{Key(second)}";
}

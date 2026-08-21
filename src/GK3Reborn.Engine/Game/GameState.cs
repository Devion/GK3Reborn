using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GK3Reborn.Foundation;
using GK3Reborn.UI;

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
    private readonly HashSet<string> _saidTopics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _actorLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _locationCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _chatCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sidneyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeterministicRandom _random = new(DefaultRandomSeed);

    /// <summary>
    /// Where the game's luck starts.
    /// </summary>
    /// <remarks>
    /// Fixed, and the sequence is reproducible from it, because ADR 0004 forbids ambient
    /// nondeterminism in engine code and the differential harness compares two runs of the
    /// same story. A script asking for a random number is asking the state for one, and how
    /// many it has asked for is part of what makes two runs comparable.
    /// </remarks>
    private const ulong DefaultRandomSeed = 0x9E3779B97F4A7C15;

    /// <summary>What each character is carrying.</summary>
    public Inventory Inventory { get; } = new();

    /// <summary>
    /// Which of the scene's cameras the view is at, or empty for the scene's default.
    /// </summary>
    /// <remarks>
    /// A name rather than a position, because that is what the scripts deal in and what
    /// survives a scene being rebuilt. Cleared on changing location, where the names belong
    /// to a room that is no longer there.
    /// </remarks>
    public string CameraAngle { get; set; } = string.Empty;

    /// <summary>
    /// Whether the last camera move was asked to take a moment.
    /// </summary>
    /// <remarks>
    /// A cut and a glide end in the same place, so this is the only thing that separates
    /// them once <see cref="CameraAngle"/> has changed. Not in the state hash: where the
    /// view ends up is a fact about the story, how it got there is not, and two runs that
    /// disagree only about that have not diverged.
    /// </remarks>
    public bool CameraGliding { get; set; }

    /// <summary>
    /// Whether the story may cut the camera about even with cinematics turned off.
    /// </summary>
    /// <remarks>
    /// <c>SetForcedCameraCuts</c>: a script about to show something the player has to see
    /// says so, and the preference gives way for as long as it holds.
    /// </remarks>
    public bool ForcedCameraCuts { get; set; }

    /// <summary>
    /// Whether the player wants the story moving the camera at all.
    /// </summary>
    /// <remarks>
    /// A preference the original also has, and one that changes what a script does:
    /// <c>CutToCameraAngle</c> only cuts when this or <see cref="ForcedCameraCuts"/> is on,
    /// while <c>ForceCutToCameraAngle</c> ignores both. It is in the state hash for that
    /// reason — two runs made with different answers to it will diverge, and the harness
    /// should see why rather than wonder.
    /// </remarks>
    public bool CinematicsEnabled { get; set; } = true;

    /// <summary>Actions the story has asked for later.</summary>
    /// <remarks>
    /// Story state rather than scene state: a minute set in the lobby has to still be
    /// counting in the hall, which is why the original saves them.
    /// </remarks>
    public GameTimers Timers { get; } = new();

    /// <summary>What is in front of the room.</summary>
    /// <remarks>
    /// State rather than presentation: scripts ask what is showing — <c>IsTopLayerInventory</c>
    /// is a real question in the data — and behave differently by the answer, so two runs
    /// that disagree about it have diverged.
    /// </remarks>
    public ScreenLayers Screens { get; } = new();

    /// <summary>The current timeblock, such as <c>110A</c>.</summary>
    public Timeblock Timeblock { get; set; } = new(1, 10, IsAfternoon: false);

    /// <summary>
    /// The camera a conversation falls back to, or null for whatever the scene names.
    /// </summary>
    /// <remarks>
    /// A scene's <c>[DIALOGUE_CAMERAS]</c> marks one camera per conversation as the
    /// <c>initial</c> one, and a script may override it for the exchange it is about to
    /// start — <c>SetDefaultDialogueCamera("GabMadWide")</c> before a chat with Madeline.
    /// Clearing it puts the scene's own choice back.
    /// </remarks>
    public string? DefaultDialogueCamera { get; set; }

    /// <summary>
    /// A field of view a script has asked for, in radians, or null for the scene's own.
    /// </summary>
    /// <remarks>
    /// The original renders at sixty degrees and the scene files override it per camera —
    /// <c>fov=20</c> on a close-up. This is the other override: a script narrowing the view
    /// for a moment. Null rather than sixty degrees, so that "nobody has asked" and "somebody
    /// asked for the default" stay distinguishable.
    /// </remarks>
    public float? CameraFieldOfView { get; set; }

    /// <summary>
    /// Hit tests a script has switched off, by name.
    /// </summary>
    /// <remarks>
    /// A hit test is a volume the player can click and never see — a doorway's clickable
    /// area, the patch of desk a note lies on. Scripts turn them off while something else
    /// is happening and on again afterwards, which is how a scene stops the player clicking
    /// through a cutscene.
    /// </remarks>
    public ISet<string> BlockedHitTests { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Three-letter code of the current location.</summary>
    /// <remarks>
    /// Moving remembers where you moved from, whoever does the moving. A script says
    /// <c>SetLocation("mop")</c> and the room loop follows with the arrival, so two
    /// separate places set this — and each of them treating the other as the one that
    /// records the change is how <see cref="LastLocation"/> stayed empty for ever.
    /// </remarks>
    public string Location
    {
        get => _location;

        set
        {
            if (!string.Equals(_location, value, StringComparison.OrdinalIgnoreCase))
            {
                LastLocation = _location;
            }

            _location = value;
        }
    }

    /// <summary>Three-letter code of the location before this one.</summary>
    /// <remarks>
    /// Scenes are built differently depending on where the player came from — which door
    /// stands open, which backdrop is visible through it — so this is state a scene reads,
    /// not a breadcrumb. It is also what decides <em>where the player is standing</em> on
    /// arrival: a room's <c>SCENE:ENTER</c> asks <c>WasLastLocation</c> and stands them at
    /// the matching spot. Left empty, every arrival is the room's default.
    /// </remarks>
    public string LastLocation { get; private set; } = string.Empty;

    private string _location = string.Empty;

    /// <summary>Name of the actor the player controls.</summary>
    public string Ego { get; set; } = "GABRIEL";

    /// <summary>The player's score.</summary>
    public int Score { get; private set; }

    /// <summary>
    /// Whether the action chooser is insisting on an answer.
    /// </summary>
    /// <remarks>
    /// <c>SetVerbModal</c> in the data: the story has asked a question and wants one of the
    /// offered actions rather than a shrug. The flag is kept faithfully because scripts set
    /// and clear it around moments that depend on it.
    /// </remarks>
    /// <seealso href="Plan/03-gameplay-ui-audio.md">
    /// Section 2.1 requires that no puzzle action fire because the engine guessed, so a
    /// chooser the player cannot leave must still offer a way out that chooses <em>nothing</em>
    /// — a modal question is a reason to keep asking, never a reason to trap somebody in a
    /// menu. Which of the offered actions is right is the player's to decide; whether they
    /// may walk away and come back is not the script's.
    /// </seealso>
    public bool MustChooseAnAction { get; set; }

    /// <summary>How many random numbers scripts have drawn.</summary>
    /// <remarks>
    /// Observable state, not a statistic: two runs that have drawn a different number of
    /// times will disagree about everything random from then on, and a differential run
    /// should see that immediately rather than at the first visible consequence.
    /// </remarks>
    public int RandomDraws { get; private set; }

    /// <summary>The files the player has gathered in Sidney, in a stable order.</summary>
    /// <remarks>
    /// Sidney is the in-game computer, and its files are evidence: a photograph scanned in,
    /// a shape traced off a map, a text translated. Scripts ask whether one is there before
    /// letting the story move on. Nothing puts them there yet — that is the analysis screen
    /// — so this reads as an investigation nobody has started.
    /// </remarks>
    public IReadOnlyList<string> SidneyFiles =>
        [.. _sidneyFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];

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
    /// How many times the player has done a verb to a noun.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GK3 gates a great deal of dialogue on these counts — the second time you ask about
    /// something you get a different answer — so they are game state, not statistics.
    /// </para>
    /// <para>
    /// Counted <em>per character</em>. Gabriel and Grace investigate the same places and
    /// what one of them has already looked at says nothing about the other, so
    /// <c>1ST_TIME</c> means the first time for whoever is being played. The game has a
    /// function whose only purpose is to set both at once, <c>SetNounVerbCountBoth</c>,
    /// which is what gives the distinction away.
    /// </para>
    /// </remarks>
    public int GetNounVerbCount(string noun, string verb) => GetNounVerbCount(Ego, noun, verb);

    /// <summary>How many times one character has done a verb to a noun.</summary>
    /// <param name="actor">Whose count to read.</param>
    /// <param name="noun">The thing.</param>
    /// <param name="verb">What was done to it.</param>
    /// <returns>The count, zero if it has never been done.</returns>
    public int GetNounVerbCount(string actor, string noun, string verb) =>
        _nounVerbCounts.GetValueOrDefault(Triple(actor, noun, verb));

    /// <summary>Sets the current character's noun/verb count.</summary>
    public void SetNounVerbCount(string noun, string verb, int value) =>
        SetNounVerbCount(Ego, noun, verb, value);

    /// <summary>Sets one character's noun/verb count.</summary>
    /// <param name="actor">Whose count to write.</param>
    /// <param name="noun">The thing.</param>
    /// <param name="verb">What was done to it.</param>
    /// <param name="value">The new count.</param>
    public void SetNounVerbCount(string actor, string noun, string verb, int value) =>
        _nounVerbCounts[Triple(actor, noun, verb)] = value;

    /// <summary>Adds one to the current character's noun/verb count.</summary>
    public void IncrementNounVerbCount(string noun, string verb) =>
        SetNounVerbCount(Ego, noun, verb, GetNounVerbCount(Ego, noun, verb) + 1);

    /// <summary>How many times a conversation topic has come up.</summary>
    public int GetTopicCount(string noun, string topic) =>
        _topicCounts.GetValueOrDefault(Pair(noun, topic));

    /// <summary>Sets a topic count.</summary>
    public void SetTopicCount(string noun, string topic, int value) =>
        _topicCounts[Pair(noun, topic)] = value;

    /// <summary>Whether one particular line of a topic has already been said.</summary>
    /// <param name="noun">Who it was said to.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="condition">The case under which that line applies.</param>
    /// <returns>True when it has been said before.</returns>
    /// <remarks>
    /// Keyed by the case as well as the topic, because a topic is written as several lines
    /// under different conditions and each is said once. The count alone cannot say which:
    /// two conditions may both hold, and asking again should give the one not yet heard
    /// rather than the first one again.
    /// </remarks>
    public bool HasSaid(string noun, string topic, string condition) =>
        _saidTopics.Contains(Line(noun, topic, condition));

    /// <summary>Records that a line of a topic has been said.</summary>
    /// <param name="noun">Who it was said to.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="condition">The case under which that line applied.</param>
    public void Said(string noun, string topic, string condition) =>
        _saidTopics.Add(Line(noun, topic, condition));

    private static string Line(string noun, string topic, string condition) =>
        $"{noun}\u0001{topic}\u0001{condition}";

    /// <summary>The conversation the player is in, or null when they are not in one.</summary>
    /// <remarks>
    /// Set by <c>SetConversation</c> and cleared by <c>EndConversation</c>. While it is set
    /// the interface offers topics rather than verbs, and the scene may use its dialogue
    /// cameras.
    /// </remarks>
    public string? Conversation { get; set; }

    /// <summary>Where an actor currently is.</summary>
    public string GetActorLocation(string actor) =>
        _actorLocations.GetValueOrDefault(Key(actor), string.Empty);

    /// <summary>Moves an actor to a location.</summary>
    public void SetActorLocation(string actor, string location) =>
        _actorLocations[Key(actor)] = location;

    /// <summary>How many times an actor has been somewhere during this timeblock.</summary>
    /// <param name="actor">The actor.</param>
    /// <param name="location">Three-letter location code.</param>
    /// <returns>The count.</returns>
    public int GetLocationCount(string actor, string location) =>
        _locationCounts.GetValueOrDefault(LocationKey(actor, location, Timeblock.ToString()));

    /// <summary>Sets how many times an actor has been somewhere during this timeblock.</summary>
    /// <param name="actor">The actor.</param>
    /// <param name="location">Three-letter location code.</param>
    /// <param name="value">The count.</param>
    public void SetLocationCount(string actor, string location, int value) =>
        _locationCounts[LocationKey(actor, location, Timeblock.ToString())] = value;

    /// <summary>Whether an actor has ever been somewhere, in any timeblock.</summary>
    /// <param name="actor">The actor.</param>
    /// <param name="location">Three-letter location code.</param>
    /// <returns>True if the count for any timeblock is above zero.</returns>
    public bool WasEverInLocation(string actor, string location)
    {
        string prefix = LocationKey(actor, location, string.Empty);

        return _locationCounts.Any(
            kv => kv.Value > 0 && kv.Key.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Records an actor arriving somewhere, and makes it the current location for ego.
    /// </summary>
    /// <param name="actor">The actor arriving.</param>
    /// <param name="location">Three-letter location code.</param>
    /// <remarks>
    /// Call this <em>after</em> the scene has been built, not before. The original does the
    /// same and says why: a SIF asks <c>GetEgoCurrentLocationCount() &lt; 1</c> to mean "the
    /// first time here", so while the scene is being assembled the count must still be the
    /// number of <em>previous</em> visits. Scripts that run once the scene is up check for
    /// one instead. Incrementing first turns every first visit into a second.
    /// </remarks>
    public void EnterLocation(string actor, string location)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(location);

        string key = LocationKey(actor, location, Timeblock.ToString());
        _locationCounts[key] = _locationCounts.GetValueOrDefault(key) + 1;
        _actorLocations[Key(actor)] = location;

        if (IsEgo(actor))
        {
            // Which remembers where they came from, if this is a move at all. A script
            // that already called SetLocation has moved them; this is then the arrival
            // being counted rather than a second move.
            Location = location;
        }
    }

    /// <summary>How many times the player has chatted about a noun.</summary>
    public int GetChatCount(string noun) => _chatCounts.GetValueOrDefault(Key(noun));

    /// <summary>Sets a chat count.</summary>
    public void SetChatCount(string noun, int value) => _chatCounts[Key(noun)] = value;

    /// <summary>Adds one to a chat count.</summary>
    public void IncrementChatCount(string noun) => _chatCounts[Key(noun)] = GetChatCount(noun) + 1;

    /// <summary>Adds to the score.</summary>
    public void ChangeScore(int by) => Score += by;

    /// <summary>Whether a file has been gathered in Sidney.</summary>
    /// <param name="file">The file's name.</param>
    /// <returns>True when the player has it.</returns>
    public bool HasSidneyFile(string file) => _sidneyFiles.Contains(Key(file));

    /// <summary>Records that a file has been gathered in Sidney.</summary>
    /// <param name="file">The file's name.</param>
    public void AddSidneyFile(string file) => _sidneyFiles.Add(Key(file));

    /// <summary>Draws a random number, both ends included.</summary>
    /// <param name="lower">Smallest value it may take.</param>
    /// <param name="upper">Largest value it may take.</param>
    /// <returns>The number.</returns>
    /// <remarks>
    /// Both ends are inclusive because the original's documentation says so, which is worth
    /// stating because the generator underneath is upper-exclusive like every other. A
    /// range the wrong way round yields its lower bound rather than throwing: scripts pass
    /// computed bounds, and a crash is a worse answer than a dull one.
    /// </remarks>
    public int NextRandom(int lower, int upper)
    {
        RandomDraws++;

        return upper <= lower ? lower : _random.NextInt32(lower, upper + 1);
    }

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
        builder.Append(CultureInfo.InvariantCulture, $"lastlocation={LastLocation}\n");
        builder.Append(CultureInfo.InvariantCulture, $"ego={Ego}\n");
        builder.Append(CultureInfo.InvariantCulture, $"score={Score}\n");
        builder.Append(CultureInfo.InvariantCulture, $"randomdraws={RandomDraws}\n");
        builder.Append(CultureInfo.InvariantCulture, $"mustchoose={MustChooseAnAction}\n");
        builder.Append(CultureInfo.InvariantCulture, $"camera={CameraAngle}\n");
        builder.Append(CultureInfo.InvariantCulture, $"forcedcuts={ForcedCameraCuts}\n");
        builder.Append(CultureInfo.InvariantCulture, $"cinematics={CinematicsEnabled}\n");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"screens={string.Join(">", Screens.Open)}\n");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"timers={string.Join(",", Timers.Pending)}\n");

        Append(builder, "flag", _flags.OrderBy(f => f, StringComparer.Ordinal).Select(f => (f, "1")));
        Append(builder, "var", Ordered(_variables));
        Append(builder, "nounverb", Ordered(_nounVerbCounts));
        Append(builder, "topic", Ordered(_topicCounts));
        Append(builder, "said", _saidTopics.OrderBy(t => t, StringComparer.Ordinal).Select(t => (t, "1")));
        Append(builder, "chat", Ordered(_chatCounts));
        Append(builder, "visited", Ordered(_locationCounts));
        Append(builder, "actor", _actorLocations
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value)));

        Append(builder, "sidney", SidneyFiles.Select(f => (f, "1")));

        // Inventory is part of the comparable state: which character holds what decides
        // whether puzzles can be solved, and which of it is in hand decides what using it
        // does.
        foreach (string owner in Inventory.Owners)
        {
            Append(builder, $"inv:{owner}", Inventory.ItemsOf(owner).Select(i => (i, "1")));
        }

        Append(builder, "active", Inventory.ActiveItems.Select(a => (a.Owner, a.Item)));

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

    /// <summary>Whether a name refers to the actor the player is controlling.</summary>
    /// <remarks>
    /// Scripts spell ego several ways — <c>GABRIEL</c>, <c>GAB</c>, <c>Gabe</c> — so this
    /// matches on the prefix the two names share rather than on equality.
    /// </remarks>
    private bool IsEgo(string actor) =>
        Key(actor).StartsWith(Key(Ego)[..Math.Min(3, Key(Ego).Length)], StringComparison.Ordinal);

    private static string Key(string name) => name.Trim().ToUpperInvariant();

    /// <summary>
    /// The key a visit is counted under.
    /// </summary>
    /// <remarks>
    /// Counts are per timeblock, because that is the question the scripts ask: a SIF wants
    /// to know whether this is the first time here <em>this afternoon</em>, not the first
    /// time in the game. <see cref="WasEverInLocation"/> is the across-all-timeblocks form
    /// and matches on the prefix, which is why the timeblock goes last.
    /// </remarks>
    private static string LocationKey(string actor, string location, string timeblock) =>
        $"{Key(actor)}|{Key(location)}|{timeblock}";

    private static string Pair(string first, string second) => $"{Key(first)}|{Key(second)}";

    private static string Triple(string first, string second, string third) =>
        $"{Key(first)}|{Key(second)}|{Key(third)}";
}

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

    /// <summary>Which inventory items have been through the scanner.</summary>
    /// <remarks>
    /// Beside the file names rather than derived from them. The story asks about the
    /// <em>file</em> — <c>DoesSidneyFileExist("fileParchment1")</c> — and Sidney's own store
    /// has to show the <em>item</em> it came from, with its name and what may be done to it.
    /// Reversing a file name back to an item would mean the naming rule had to stay
    /// invertible for ever, which is a promise not worth making for one set of strings.
    /// </remarks>
    private readonly HashSet<string> _sidneyScans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// People the player is to be treated as having met, whatever the story can show.
    /// </summary>
    /// <remarks>
    /// Only ever filled from a save, and only where the save cannot answer the question
    /// itself: the labels normally read the game's own conditions, and a game played
    /// through leaves the topic counts those conditions ask about. A save the 1999 game
    /// wrote leaves none of them, so what it does say — the point in the story it stands at
    /// — is turned into names here. See <see cref="Story.Introductions.MetBy"/>.
    /// </remarks>
    private readonly HashSet<string> _introduced = new(StringComparer.OrdinalIgnoreCase);

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
    /// What the view is looking at closely, or empty when it is looking at the room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inspecting is a camera, not a screen. <c>InspectObject</c> moves the view to the
    /// close-up the scene declares for a thing and <c>UnInspect</c> brings it back —
    /// there is nothing drawn over the room and nothing modal about it, which is why
    /// modelling it as one of <see cref="ScreenLayers"/> left the verb doing nothing
    /// visible at all.
    /// </para>
    /// <para>
    /// It sits beside <see cref="CameraAngle"/> rather than replacing it, and that is what
    /// makes coming back free: the angle the story left the view at is still there
    /// underneath, so clearing this returns to it without anything having to remember it.
    /// </para>
    /// </remarks>
    public string Inspecting { get; set; } = string.Empty;

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

    /// <summary>The flag the game's own easter-egg content is written against.</summary>
    public const string EasterEggFlag = "EGG";

    /// <summary>
    /// Whether the game's easter-egg content is switched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A player's preference kept as a story flag, because a flag is where the game itself
    /// looks: <c>EGG</c> is a built-in action case, and Sidney's sixth email is written
    /// against <c>GetFlag("Egg")</c>. Reading it through a property rather than leaving
    /// everybody to spell the flag gives it one name and one place.
    /// </para>
    /// <para>
    /// Which means it can arrive in a save, and it must not: it is what this player asked
    /// for, not something the story earned. <see cref="Restore"/> puts the current answer
    /// back over whatever the save had, so loading somebody else's game does not turn it on.
    /// </para>
    /// </remarks>
    public bool EasterEggs
    {
        get => GetFlag(EasterEggFlag);

        set
        {
            if (value)
            {
                SetFlag(EasterEggFlag);
            }
            else
            {
                ClearFlag(EasterEggFlag);
            }
        }
    }

    /// <summary>
    /// Whether nothing the story does is allowed to kill Gabriel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A preference, and one that changes what a script does — which is why it sits here
    /// beside <see cref="CinematicsEnabled"/> rather than being read out of the settings
    /// wherever it is wanted, and why it is in the state hash. Two runs made with different
    /// answers to it diverge, and the harness should be able to see why.
    /// </para>
    /// <para>
    /// Not a story flag, unlike <see cref="EasterEggs"/>: nothing in the game's own data
    /// asks about it, because the game's own data has never heard of it. And like the
    /// easter eggs it must not arrive in a save — it is what this player asked for, not
    /// something the story earned — so <see cref="Restore"/> puts the current answer back.
    /// </para>
    /// </remarks>
    public bool PlotArmour { get; set; }

    /// <summary>Whether Gabriel catches TE3's blade himself.</summary>
    /// <remarks>
    /// A preference, held and restored exactly like <see cref="PlotArmour"/>: the player
    /// asked for it, the story did not earn it, and a save made with it on must not turn it
    /// on for somebody who loads that save with it off. See
    /// <see cref="Settings.CatchesPendulum"/> for what it costs the puzzle.
    /// </remarks>
    public bool CatchesPendulum { get; set; }

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

    /// <summary>
    /// Rides the moped somewhere, arriving from the driving map.
    /// </summary>
    /// <param name="location">The room the chosen place loads.</param>
    /// <remarks>
    /// <para>
    /// The map is a location in the original rather than a panel over one, so a ride is two
    /// moves: out of the room, onto the map, and off it again into the next room. Doing it
    /// as two moves rather than one is the whole of this method, and it is what leaves
    /// <see cref="LastLocation"/> saying <c>MAP</c> — which is the question the game's own
    /// data asks about a ride.
    /// </para>
    /// <para>
    /// <b>What went wrong without it.</b> Riding to Larry Chester's house set the location
    /// straight to <c>LHE</c> from wherever the player had been, so the room was built as
    /// though they had walked in from that room instead. <c>LHE.SIF</c> declares Gabriel's
    /// moped under <c>WasLastLocation("Map")</c> and the yard had no moped in it; the
    /// scene's only way back to the map is an <c>EXIT</c> guarded by the moped being
    /// there, so there was no way out; and the room names no <c>FR_MOP</c>, so the player
    /// stood at the origin rather than at <c>FR_MAP</c>. Ten more of the game's scene
    /// scripts place the player by the same question.
    /// </para>
    /// <para>
    /// <b>And the moped is now parked there.</b> Six of the game's scene files draw it from
    /// <c>BikeLocation</c> and three of its action files let the player leave on it only
    /// when that number is the room they are standing in, so a ride that does not move it
    /// strands them: Blanchefort was reported exactly that way. See
    /// <see cref="DrivingMap.ParkedAt"/> for what the number is and why the original never
    /// wrote it.
    /// </para>
    /// <para>
    /// Written before the room is built, which is what makes it count: a scene file's
    /// conditions are decided as it is read, and the two scripts that set this variable
    /// themselves do it from an arrival script that runs afterwards.
    /// </para>
    /// <para>
    /// Passing through is not visiting: only the room loop records a location as somewhere
    /// the player has been, so a ride does not put the map itself into the places they have
    /// been to.
    /// </para>
    /// </remarks>
    public void RideTo(string location)
    {
        ArgumentNullException.ThrowIfNull(location);

        Location = DrivingMap.Location;
        Location = location;

        if (DrivingMap.ParkedAt(location) is { } parked)
        {
            SetVariable(DrivingMap.Parked, parked);
        }
    }

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

    /// <summary>
    /// Whether an exchange is under way, so that its camera is chosen once.
    /// </summary>
    /// <remarks>
    /// A conversation is many calls — a topic's script says several lines and each is its
    /// own <c>StartDialogue</c> — and cutting on every one of them would make the camera
    /// jump each time somebody drew breath. Cleared when the conversation ends and when the
    /// player does anything else.
    /// </remarks>
    public bool Talking { get; set; }

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

    /// <summary>Everywhere an actor has ever been, in any timeblock.</summary>
    /// <param name="actor">The actor.</param>
    /// <returns>The location codes, without repeats.</returns>
    /// <remarks>
    /// Read out of the visit counts rather than kept separately, so there is one answer to
    /// "has this actor been here" and the driving map cannot offer somewhere the story does
    /// not think they have been.
    /// </remarks>
    public IReadOnlyList<string> VisitedLocations(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        string prefix = Key(actor) + "|";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, int count) in _locationCounts)
        {
            if (count <= 0 || !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = key.Split('|');

            if (parts.Length > 1 && parts[1].Length > 0)
            {
                found.Add(parts[1]);
            }
        }

        return [.. found];
    }

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

    /// <summary>
    /// Awards a named score event, once.
    /// </summary>
    /// <param name="name">The event, as a script names it.</param>
    /// <param name="worth">What it is worth, or null when nothing knows.</param>
    /// <returns>True when it scored, false when it had already been earned or is unknown.</returns>
    /// <remarks>
    /// <para>
    /// <c>ChangeScore</c> takes a name and not a number — <c>ChangeScore("e_110a_lby_read_register")</c>
    /// — which is easy to misread, and reading it as a number awards zero every time.
    /// </para>
    /// <para>
    /// The set of events earned is part of the state and part of a save. It is what makes
    /// the score stable across a reload, and it is a record of what the player has actually
    /// done rather than only of how many points they have.
    /// </para>
    /// </remarks>
    public bool AwardScore(string name, int? worth)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (worth is not { } points || !_scored.Add(name))
        {
            return false;
        }

        Score += points;
        return true;
    }

    /// <summary>
    /// Whether the clock is being moved on, and to when.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by <c>SetTime</c> and cleared once the new timeblock has been started. It exists
    /// because a timeblock change is not a room change with a different clock: the room
    /// being left is unloaded, the timeblock's closing film plays, the player is shown where
    /// they have got to, and only then is the next room built. Whatever asked to change
    /// rooms has to stand aside for all of that, which is what the original's
    /// <c>IsChangingTimeblock</c> is for.
    /// </para>
    /// <para>
    /// Not in the state hash: it is true for the length of a transition and never while the
    /// game is sitting still.
    /// </para>
    /// </remarks>
    public Timeblock? ChangingTo { get; private set; }

    /// <summary>Whether the clock is on its way somewhere.</summary>
    public bool ChangingTimeblock => ChangingTo is not null;

    /// <summary>
    /// Moves the story on to another point in the day.
    /// </summary>
    /// <param name="timeblock">Where the clock is going.</param>
    /// <param name="location">Where the player will be, or null to leave that to the caller.</param>
    /// <returns>True when the clock actually moved.</returns>
    /// <remarks>
    /// Asking for the timeblock the game is already in does nothing, which is what the
    /// original does and what keeps a completion rule that fires twice from playing the
    /// closing film twice.
    /// </remarks>
    public bool ChangeTimeblock(Timeblock timeblock, string? location = null)
    {
        if (timeblock == Timeblock)
        {
            return false;
        }

        ChangingTo = timeblock;

        if (location is { Length: > 0 } named)
        {
            Location = named.ToUpperInvariant();
        }

        return true;
    }

    /// <summary>Finishes a timeblock change, once the next room is being built.</summary>
    public void StartedTimeblock()
    {
        if (ChangingTo is { } wanted)
        {
            Timeblock = wanted;
            ChangingTo = null;
        }
    }

    /// <summary>
    /// Whether the camera is fenced in by the room's shell.
    /// </summary>
    /// <remarks>
    /// On unless a script says otherwise, and a script saying otherwise means it only
    /// until the next room: the original notes that turning them off does not survive a
    /// scene load, and 35 calls rely on that rather than turning them back on.
    /// </remarks>
    public bool CameraBoundaries { get; set; } = true;

    /// <summary>The expression somebody is wearing, or null.</summary>
    /// <param name="actor">Their model name.</param>
    /// <returns>The mood.</returns>
    /// <remarks>
    /// State rather than presentation: a mood is worn until something clears it, and what
    /// clears it is an animation that has to know which one to play. It survives a save for
    /// the same reason — a character reloaded mid-scene should still look how they looked.
    /// </remarks>
    public string? MoodOf(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _moods.GetValueOrDefault(actor);
    }

    /// <summary>Records the expression somebody is wearing.</summary>
    /// <param name="actor">Their model name.</param>
    /// <param name="mood">The mood, or null for none.</param>
    public void SetMood(string actor, string? mood)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (mood is { Length: > 0 })
        {
            _moods[actor] = mood;
        }
        else
        {
            _moods.Remove(actor);
        }
    }

    /// <summary>Everyone wearing an expression, and which, in a stable order.</summary>
    public IReadOnlyList<(string Actor, string Mood)> Moods =>
        [.. _moods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))];

    private readonly Dictionary<string, string> _moods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a score event has been earned.</summary>
    /// <param name="name">The event.</param>
    /// <returns>True when it has.</returns>
    public bool HasScored(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _scored.Contains(name);
    }

    /// <summary>Every score event earned, in a stable order.</summary>
    public IReadOnlyList<string> Scored =>
        [.. _scored.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    private readonly HashSet<string> _scored = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _hints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What is on Sidney's map: the places marked, the figures laid over them and the
    /// ruling.
    /// </summary>
    /// <remarks>
    /// <b>Here rather than on the map itself, because the story is what a save records.</b>
    /// The map puzzle runs over several sittings — mark a village, go and read a painting's
    /// geometry, come back and lay the figure it saved — and a map that forgot itself when
    /// the game was closed would make the whole of it one sitting long. The machine writes
    /// through to this after every change and reads it back when a save is loaded.
    /// </remarks>
    public SavedMap SidneyMap { get; set; } = new([], [], 0);

    /// <summary>How many hints the player has asked for about one objective.</summary>
    /// <param name="objective">What it is filed under.</param>
    /// <returns>The count, nought when they have never asked.</returns>
    /// <remarks>
    /// The journal's one piece of state. Everything else it shows is read from the score
    /// events the story already records, so it cannot drift out of step with the game — but
    /// how much of the answer somebody has asked to be told is theirs, and it has to survive
    /// closing the game or the hint button starts again from the top every session.
    /// </remarks>
    public int HintsAsked(string objective)
    {
        ArgumentNullException.ThrowIfNull(objective);

        return _hints.GetValueOrDefault(objective);
    }

    /// <summary>Records that the player asked for one more hint.</summary>
    /// <param name="objective">What it is filed under.</param>
    public void AskedForHint(string objective)
    {
        ArgumentNullException.ThrowIfNull(objective);

        _hints[objective] = _hints.GetValueOrDefault(objective) + 1;
    }

    /// <summary>Every hint asked for, in a stable order.</summary>
    public IReadOnlyDictionary<string, int> Hints =>
        new Dictionary<string, int>(_hints);

    /// <summary>Whether a file has been gathered in Sidney.</summary>
    /// <param name="file">The file's name.</param>
    /// <returns>True when the player has it.</returns>
    public bool HasSidneyFile(string file) => _sidneyFiles.Contains(Key(file));

    /// <summary>Records that a file has been gathered in Sidney.</summary>
    /// <param name="file">The file's name.</param>
    public void AddSidneyFile(string file) => _sidneyFiles.Add(Key(file));

    /// <summary>The inventory items that have been scanned into Sidney.</summary>
    public IReadOnlyList<string> SidneyScans =>
        [.. _sidneyScans.OrderBy(s => s, StringComparer.Ordinal)];

    /// <summary>Records that an item has been through Sidney's scanner.</summary>
    /// <param name="item">The item's noun.</param>
    public void RecordSidneyScan(string item) => _sidneyScans.Add(Key(item));

    /// <summary>Everybody the player is taken to have met, in a stable order.</summary>
    public IReadOnlyList<string> Introduced =>
        [.. _introduced.OrderBy(noun => noun, StringComparer.Ordinal)];

    /// <summary>Takes somebody as met, whatever the story can still show.</summary>
    /// <param name="noun">The noun a scene gives them.</param>
    /// <remarks>
    /// For a save that cannot answer the question the labels normally ask; see
    /// <see cref="_introduced"/>. Nothing in the game's own data calls this, and nothing
    /// should: an introduction that happens in front of the player is recorded by the topic
    /// or the verb the game itself counts, and inventing a second record of it would give
    /// the two ways to be wrong.
    /// </remarks>
    public void Introduce(string noun)
    {
        ArgumentNullException.ThrowIfNull(noun);

        _introduced.Add(Key(noun));
    }

    /// <summary>Whether a save has said the player already knows somebody.</summary>
    /// <param name="noun">The noun a scene gives them.</param>
    /// <returns>True when they are to be treated as met without asking the story.</returns>
    public bool WasIntroduced(string? noun) =>
        noun is { Length: > 0 } && _introduced.Contains(Key(noun));

    /// <summary>
    /// Writes everything observable down, so that it can be put back.
    /// </summary>
    /// <param name="title">What to call it.</param>
    /// <returns>The save.</returns>
    /// <remarks>
    /// <para>
    /// The composite keys go across whole rather than being taken apart and rebuilt. They
    /// are this class's own private encoding — an actor, a noun and a verb joined by a
    /// separator no name contains — and a save that decomposed them would have to agree
    /// with that encoding for ever. Copying them keeps the round trip exact by
    /// construction, which is what makes the hash test meaningful rather than circular.
    /// </para>
    /// <para>
    /// Compare with <see cref="ComputeHash"/>: the two enumerate the same things, and they
    /// have to. Anything the hash counts as part of the game and this leaves out is
    /// something a loaded game would have lost.
    /// </para>
    /// </remarks>
    public SaveGame Capture(string title = "")
    {
        (ulong s0, ulong s1, ulong s2, ulong s3) = _random.CaptureState();

        return new SaveGame
        {
            SchemaVersion = SaveGame.CurrentSchema,
            Written = DateTimeOffset.UtcNow,
            Title = title,
            Day = Timeblock.Day,
            Hour = Timeblock.Hour,
            Afternoon = Timeblock.IsAfternoon,
            Location = Location,
            LastLocation = LastLocation,
            CameraAngle = CameraAngle,
            Ego = Ego,
            Score = Score,
            RandomDraws = RandomDraws,
            RandomState = [s0, s1, s2, s3],
            Flags = [.. _flags.OrderBy(f => f, StringComparer.Ordinal)],
            Variables = new Dictionary<string, int>(_variables),
            NounVerbCounts = new Dictionary<string, int>(_nounVerbCounts),
            TopicCounts = new Dictionary<string, int>(_topicCounts),
            SaidTopics = [.. _saidTopics.OrderBy(t => t, StringComparer.Ordinal)],
            ChatCounts = new Dictionary<string, int>(_chatCounts),
            LocationCounts = new Dictionary<string, int>(_locationCounts),
            ActorLocations = new Dictionary<string, string>(_actorLocations),
            Scored = [.. _scored.OrderBy(e => e, StringComparer.Ordinal)],
            Hints = new Dictionary<string, int>(_hints),
            SidneyFiles = [.. _sidneyFiles.OrderBy(f => f, StringComparer.Ordinal)],
            SidneyScans = [.. _sidneyScans.OrderBy(s => s, StringComparer.Ordinal)],
            SidneyMarks = [.. SidneyMap.Marks],
            SidneyFigures = [.. SidneyMap.Figures],
            SidneyGrid = SidneyMap.Grid,
            Introduced = Introduced,
            BlockedHitTests = [.. BlockedHitTests.OrderBy(h => h, StringComparer.Ordinal)],
            Inventories =
            [
                .. Inventory.Owners.Select(owner => new SavedInventory(
                    owner,
                    [.. Inventory.ItemsOf(owner)],
                    Inventory.ActiveItemOf(owner))),
            ],
            Timers =
            [
                .. Timers.Pending.Select(t => new SavedTimer(t.Noun, t.Verb, t.SecondsRemaining)),
            ],
        };
    }

    /// <summary>
    /// Puts a saved game back, throwing away whatever was here.
    /// </summary>
    /// <param name="save">The save.</param>
    /// <remarks>
    /// <b>Everything is cleared first.</b> Loading into a state that still holds the
    /// previous game's flags is the classic save bug: the story reads a flag nobody set in
    /// this run and takes a branch the player never earned, and it only shows up hours
    /// later. Setting is not enough; unsetting has to happen too, and a save records only
    /// what is set.
    /// </remarks>
    public void Restore(SaveGame save)
    {
        ArgumentNullException.ThrowIfNull(save);

        // Preferences rather than facts about the story, so they survive the load: see
        // EasterEggs and PlotArmour.
        bool eggs = EasterEggs;
        bool armour = PlotArmour;
        bool catches = CatchesPendulum;

        _variables.Clear();
        _flags.Clear();
        _nounVerbCounts.Clear();
        _topicCounts.Clear();
        _saidTopics.Clear();
        _actorLocations.Clear();
        _locationCounts.Clear();
        _chatCounts.Clear();
        _sidneyFiles.Clear();
        _sidneyScans.Clear();
        _introduced.Clear();
        _scored.Clear();
        _hints.Clear();
        BlockedHitTests.Clear();
        Timers.Clear();
        Inventory.Clear();
        Screens.CloseAll();

        Timeblock = new Timeblock(save.Day, save.Hour, save.Afternoon);
        Ego = save.Ego;
        CameraAngle = save.CameraAngle;
        Conversation = null;
        Inspecting = string.Empty;
        MustChooseAnAction = false;
        DefaultDialogueCamera = null;
        CameraFieldOfView = null;

        // And the camera goes back to the player. Both of these belong to the script that
        // set them and are cleared by the same script a moment later — and a load throws
        // that script away, so nothing is left to clear them. Loading during a cutscene
        // came back with the view still held by a story that was no longer running: see
        // SceneUpdate.Directing, which reads ForcedCameraCuts and takes the mouse for as
        // long as it is on. A save records neither, so a restore may not assume either.
        ForcedCameraCuts = false;
        CameraGliding = false;

        // Straight to the fields: the Location setter keeps a history and counts a visit,
        // and a load is neither. Where the player was is what the save says, and so is
        // where they were before that.
        _location = save.Location;
        LastLocation = save.LastLocation;
        Score = save.Score;
        RandomDraws = save.RandomDraws;

        if (save.RandomState.Count == 4)
        {
            _random.RestoreState(
                (save.RandomState[0], save.RandomState[1], save.RandomState[2], save.RandomState[3]));
        }

        foreach (string flag in save.Flags)
        {
            _flags.Add(Key(flag));
        }

        EasterEggs = eggs;
        PlotArmour = armour;
        CatchesPendulum = catches;

        Fill(_variables, save.Variables);
        Fill(_nounVerbCounts, save.NounVerbCounts);
        Fill(_topicCounts, save.TopicCounts);
        Fill(_chatCounts, save.ChatCounts);
        Fill(_locationCounts, save.LocationCounts);

        foreach (string said in save.SaidTopics)
        {
            _saidTopics.Add(said);
        }

        foreach ((string actor, string where) in save.ActorLocations)
        {
            _actorLocations[actor] = where;
        }

        foreach (string file in save.SidneyFiles)
        {
            _sidneyFiles.Add(Key(file));
        }

        foreach (string scan in save.SidneyScans)
        {
            _sidneyScans.Add(Key(scan));
        }

        SidneyMap = new SavedMap(
            [.. save.SidneyMarks], [.. save.SidneyFigures], save.SidneyGrid);

        // Who this save says the player already knows. Empty for a game played through in
        // this engine, which answers the question out of its own topic counts, and filled
        // for one brought across from the original, which cannot: see
        // Story.Introductions.MetBy.
        foreach (string noun in save.Introduced)
        {
            _introduced.Add(Key(noun));
        }

        // Which score events have been earned. A save written before the journal existed has
        // none of these, and there is no honest way to work out which of 382 events a player
        // had — so they are taken from where they are recoverable and guessed nowhere. See
        // SaveGame.Recovered.
        foreach (string earned in save.Scored)
        {
            _scored.Add(earned);
        }

        foreach ((string objective, int asked) in save.Hints)
        {
            _hints[objective] = asked;
        }

        foreach (string hit in save.BlockedHitTests)
        {
            BlockedHitTests.Add(hit);
        }

        foreach (SavedInventory pockets in save.Inventories)
        {
            foreach (string item in pockets.Items)
            {
                Inventory.Add(pockets.Owner, item);
            }

            Inventory.SetActive(pockets.Owner, pockets.Active);
        }

        foreach (SavedTimer timer in save.Timers)
        {
            Timers.Set(timer.Noun, timer.Verb, timer.Seconds);
        }
    }

    /// <summary>Copies a saved map in, as it was written.</summary>
    private static void Fill(Dictionary<string, int> into, IReadOnlyDictionary<string, int> from)
    {
        foreach ((string key, int value) in from)
        {
            into[key] = value;
        }
    }

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
        builder.Append(CultureInfo.InvariantCulture, $"plotarmour={PlotArmour}\n");
        builder.Append(CultureInfo.InvariantCulture, $"catchespendulum={CatchesPendulum}\n");
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
        Append(builder, "scanned", SidneyScans.Select(s => (s, "1")));

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

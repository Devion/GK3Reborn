using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GK3Reborn.Foundation;
using GK3Reborn.UI;

namespace GK3Reborn.Game;

/// <summary>
/// The game's observable state: what scripts read and write.
/// </summary>
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
    private readonly HashSet<string> _sidneyScans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// People the player is to be treated as having met, whatever the story can show.
    /// </summary>
    private readonly HashSet<string> _introduced = new(StringComparer.OrdinalIgnoreCase);

    private readonly DeterministicRandom _random = new(DefaultRandomSeed);

    /// <summary>
    /// Where the game's luck starts.
    /// </summary>
    private const ulong DefaultRandomSeed = 0x9E3779B97F4A7C15;

    /// <summary>What each character is carrying.</summary>
    public Inventory Inventory { get; } = new();

    /// <summary>
    /// Which of the scene's cameras the view is at, or empty for the scene's default.
    /// </summary>
    public string CameraAngle { get; set; } = string.Empty;

    /// <summary>
    /// What the view is looking at closely, or empty when it is looking at the room.
    /// </summary>
    public string Inspecting { get; set; } = string.Empty;

    /// <summary>
    /// Whether the last camera move was asked to take a moment.
    /// </summary>
    public bool CameraGliding { get; set; }

    /// <summary>
    /// Whether the story may cut the camera about even with cinematics turned off.
    /// </summary>
    public bool ForcedCameraCuts { get; set; }

    /// <summary>
    /// Whether the player wants the story moving the camera at all.
    /// </summary>
    public bool CinematicsEnabled { get; set; } = true;

    /// <summary>The flag the game's own easter-egg content is written against.</summary>
    public const string EasterEggFlag = "EGG";

    /// <summary>
    /// Whether the game's easter-egg content is switched on.
    /// </summary>
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
    public bool PlotArmour { get; set; }

    /// <summary>Whether Gabriel catches TE3's blade himself.</summary>
    public bool CatchesPendulum { get; set; }

    /// <summary>Actions the story has asked for later.</summary>
    public GameTimers Timers { get; } = new();

    /// <summary>What is in front of the room.</summary>
    public ScreenLayers Screens { get; } = new();

    /// <summary>The current timeblock, such as <c>110A</c>.</summary>
    public Timeblock Timeblock { get; set; } = new(1, 10, IsAfternoon: false);

    /// <summary>
    /// The camera a conversation falls back to, or null for whatever the scene names.
    /// </summary>
    public string? DefaultDialogueCamera { get; set; }

    /// <summary>
    /// A field of view a script has asked for, in radians, or null for the scene's own.
    /// </summary>
    public float? CameraFieldOfView { get; set; }

    /// <summary>
    /// Hit tests a script has switched off, by name.
    /// </summary>
    public ISet<string> BlockedHitTests { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Three-letter code of the current location.</summary>
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
    public string LastLocation { get; private set; } = string.Empty;

    /// <summary>
    /// Where the story's rules find the player: the map while it is open, and otherwise
    /// the room.
    /// </summary>
    public string Whereabouts =>
        Screens.IsOnTop(ScreenKind.Driving) ? DrivingMap.Location : Location;

    /// <summary>
    /// Rides the moped somewhere, arriving from the driving map.
    /// </summary>
    /// <param name="location">The room the chosen place loads.</param>
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
    /// <seealso href="Plan/03-gameplay-ui-audio.md">
    /// Section 2.1 requires that no puzzle action fire because the engine guessed, so a
    /// chooser the player cannot leave must still offer a way out that chooses <em>nothing</em>
    /// — a modal question is a reason to keep asking, never a reason to trap somebody in a
    /// menu. Which of the offered actions is right is the player's to decide; whether they
    /// may walk away and come back is not the script's.
    /// </seealso>
    public bool MustChooseAnAction { get; set; }

    /// <summary>How many random numbers scripts have drawn.</summary>
    public int RandomDraws { get; private set; }

    /// <summary>The files the player has gathered in Sidney, in a stable order.</summary>
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
    public string? Conversation { get; set; }

    /// <summary>
    /// Whether an exchange is under way, so that its camera is chosen once.
    /// </summary>
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

    /// <summary>
    /// Sets how many times an actor has been somewhere during a named point in the story.
    /// </summary>
    /// <param name="actor">The actor.</param>
    /// <param name="location">Three-letter location code.</param>
    /// <param name="when">Which point in the story the visits belong to.</param>
    /// <param name="value">The count.</param>
    public void SetLocationCount(string actor, string location, Timeblock when, int value) =>
        _locationCounts[LocationKey(actor, location, when.ToString())] = value;

    /// <summary>Everywhere an actor has ever been, in any timeblock.</summary>
    /// <param name="actor">The actor.</param>
    /// <returns>The location codes, without repeats.</returns>
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
    public Timeblock? ChangingTo { get; private set; }

    /// <summary>Whether the clock is on its way somewhere.</summary>
    public bool ChangingTimeblock => ChangingTo is not null;

    /// <summary>
    /// Moves the story on to another point in the day.
    /// </summary>
    /// <param name="timeblock">Where the clock is going.</param>
    /// <param name="location">Where the player will be, or null to leave that to the caller.</param>
    /// <returns>True when the clock actually moved.</returns>
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
    public bool CameraBoundaries { get; set; } = true;

    /// <summary>The expression somebody is wearing, or null.</summary>
    /// <param name="actor">Their model name.</param>
    /// <returns>The mood.</returns>
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
    public SavedMap SidneyMap { get; set; } = new([], [], 0);

    /// <summary>How many hints the player has asked for about one objective.</summary>
    /// <param name="objective">What it is filed under.</param>
    /// <returns>The count, nought when they have never asked.</returns>
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
            SidneyGridFixed = SidneyMap.GridFixed,
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
            [.. save.SidneyMarks], [.. save.SidneyFigures], save.SidneyGrid, save.SidneyGridFixed);

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
    public int NextRandom(int lower, int upper)
    {
        RandomDraws++;

        return upper <= lower ? lower : _random.NextInt32(lower, upper + 1);
    }

    /// <summary>
    /// A hash of everything observable, for comparing runs.
    /// </summary>
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
    private bool IsEgo(string actor) =>
        Key(actor).StartsWith(Key(Ego)[..Math.Min(3, Key(Ego).Length)], StringComparison.Ordinal);

    private static string Key(string name) => name.Trim().ToUpperInvariant();

    /// <summary>
    /// The key a visit is counted under.
    /// </summary>
    private static string LocationKey(string actor, string location, string timeblock) =>
        $"{Key(actor)}|{Key(location)}|{timeblock}";

    private static string Pair(string first, string second) => $"{Key(first)}|{Key(second)}";

    private static string Triple(string first, string second, string third) =>
        $"{Key(first)}|{Key(second)}|{Key(third)}";
}

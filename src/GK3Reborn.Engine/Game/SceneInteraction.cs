using System.Numerics;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Rendering;
using GK3Reborn.UI.Interaction;

namespace GK3Reborn.Game;

/// <summary>What the pointer is over and what can be done to it.</summary>
/// <param name="Pick">The thing itself, or null when the pointer is over nothing.</param>
/// <param name="Actions">The verbs it answers to, here and now, most likely first.</param>
/// <param name="Called">
/// What to call it on screen, when that is not its noun. Only the exits use it, and they
/// need it: the artists numbered RC1's ways out <c>EXIT</c>, <c>EXIT1</c> to <c>EXIT5</c>,
/// in no order anybody could infer, and the interface was drawing "Exit3" at a player who
/// has no way of knowing what three means.
/// </param>
public readonly record struct Hover(
    ScenePick? Pick, IReadOnlyList<AvailableAction> Actions, string? Called = null)
{
    /// <summary>Nothing under the pointer.</summary>
    public static Hover Nothing => new(null, []);

    /// <summary>What the scene calls the thing, or null.</summary>
    public string? Noun => Pick?.Noun;

    /// <summary>What to show the player, which is the noun unless something better is known.</summary>
    public string? Label => Called is { Length: > 0 } named ? named : Noun;

    /// <summary>Whether there is anything to do.</summary>
    public bool Actionable => Noun is { Length: > 0 } && Actions.Count > 0;

    /// <summary>The verb a plain click performs.</summary>
    public string? Default =>
        Pick?.Verb is { Length: > 0 } named && !IsCloseUp(named)
            ? named
            : Actions.FirstOrDefault(a =>
                a.Category != ActionCategory.Item &&
                !IsCloseUp(a.LocalizedVerb))?.LocalizedVerb;

    /// <summary>The verb the middle button performs.</summary>
    public string? Closer =>
        Actions.FirstOrDefault(a => IsCloseUp(a.LocalizedVerb))?.LocalizedVerb;

    /// <summary>Whether a verb is one of the two close-up verbs.</summary>
    private static bool IsCloseUp(string verb) =>
        verb.Equals("INSPECT", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("INSPECT_UNDO", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns pointing at the room into doing something to it.
/// </summary>
public sealed class SceneInteraction
{
    private readonly LoadedScene _scene;
    private readonly ScenePicker _picker;
    private readonly ActionResolver? _actions;
    private readonly ActionRunner _runner;
    private readonly Gk3SheepApi _api;
    private readonly string? _floor;

    /// <summary>Creates the interaction over a loaded scene.</summary>
    /// <param name="scene">The room.</param>
    /// <param name="api">The host its scripts run against.</param>
    public SceneInteraction(LoadedScene scene, Gk3SheepApi api)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(api);

        _scene = scene;
        _picker = new ScenePicker(scene) { Blocked = api.State.BlockedHitTests };
        _actions = scene.Actions;
        _runner = new ActionRunner(api);
        _api = api;
        _floor = scene.Definition.FloorObject();
    }

    /// <summary>What the last click did, for whoever wants to say so.</summary>
    public ActionOutcome? Last { get; private set; }

    /// <summary>Asks what a ray meets, for something that is not the pointer.</summary>
    /// <param name="ray">Where from and which way.</param>
    /// <param name="ignoring">What it passes straight through, by name.</param>
    /// <returns>The nearest thing it met, or null.</returns>
    public Interaction.ScenePick? Cast(
        Rendering.Ray ray, IReadOnlySet<string>? ignoring = null) =>
        _picker.Pick(ray, ignoring);

    /// <summary>Asks what is under a point on the screen.</summary>
    /// <param name="camera">The view.</param>
    /// <param name="x">Pixels from the left.</param>
    /// <param name="y">Pixels from the top.</param>
    /// <param name="width">Width of the viewport.</param>
    /// <param name="height">Height of the viewport.</param>
    /// <returns>What is there and what it answers to.</returns>
    public Hover At(Camera camera, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (_picker.Pick(camera, x, y, width, height) is not { } pick)
        {
            return Hover.Nothing;
        }

        if (pick.Noun is not { Length: > 0 } noun || _actions is null)
        {
            return new Hover(pick, []);
        }

        IReadOnlyList<AvailableAction> offered =
            WithInspect(noun, _actions.Resolve(noun, _api.State.Ego, Carrying));

        return new Hover(pick, offered, Called(noun, pick, offered));
    }

    /// <summary>
    /// A question put to the player as a verb bar, for a noun that is nowhere in the room.
    /// </summary>
    /// <param name="noun">The noun the action files answer for.</param>
    /// <param name="label">What the bar is headed, since the noun itself means nothing to a player.</param>
    /// <returns>A hover to open the bar on, with no rows when the files offer none.</returns>
    public Hover Ask(string noun, string label)
    {
        ArgumentNullException.ThrowIfNull(noun);

        var pick = new ScenePick(noun, noun, null, 0f, Vector3.Zero, PickKind.HitTest);

        return _actions is null
            ? new Hover(pick, [], label)
            : new Hover(pick, _actions.Resolve(noun, _api.State.Ego, Carrying), label);
    }

    /// <summary>The verb that looks closely at something, and the one that stops.</summary>
    private const string Inspect = "INSPECT";

    /// <summary>Stops looking closely at something.</summary>
    private const string Undo = "INSPECT_UNDO";

    /// <summary>
    /// Adds the close-up verb to what a thing answers to, and the way back out of it.
    /// </summary>
    /// <param name="noun">The thing under the pointer.</param>
    /// <param name="offered">What the action files say about it.</param>
    /// <returns>The same list, with at most one of the two close-up verbs on the front.</returns>
    private List<AvailableAction> WithInspect(
        string noun, IReadOnlyList<AvailableAction> offered)
    {
        bool looking = _api.State.Inspecting.Equals(noun, StringComparison.OrdinalIgnoreCase);

        List<AvailableAction> all =
            [.. offered.Where(a =>
                !a.LocalizedVerb.Equals(looking ? Inspect : Undo, StringComparison.OrdinalIgnoreCase))];

        // Nothing to look at closely, so neither the way in nor the way out is offered. The
        // way out is still reachable while it is being looked at, because a close-up the
        // player is already inside has to be leaveable whatever the room says now.
        if (!looking && Watcher is { } world && !world.Inspectable(noun))
        {
            return all;
        }

        string verb = looking ? Undo : Inspect;

        if (all.Exists(a => a.LocalizedVerb.Equals(verb, StringComparison.OrdinalIgnoreCase)))
        {
            return all;
        }

        all.Insert(0, new AvailableAction
        {
            ActionId = $"{noun}:{verb}",
            NvcProvenance = "the engine",
            LocalizedVerb = verb,
            IconSemantic = "eye",
            Category = ActionCategory.Inspect,
            Enabled = true,
        });

        return all;
    }

    /// <summary>
    /// What the player has to use on things.
    /// </summary>
    private IReadOnlyCollection<string> Carrying => _api.State.Inventory.ItemsOf(_api.State.Ego);

    /// <summary>
    /// What to call a thing whose noun is not worth showing.
    /// </summary>
    /// <param name="noun">What the scene calls it.</param>
    /// <param name="pick">The thing itself, for the default verb its model declares.</param>
    /// <param name="offered">What it answers to, for when the model declares no verb.</param>
    /// <returns>A better name, or null to use the noun.</returns>
    private string? Called(string noun, ScenePick pick, IReadOnlyList<AvailableAction> offered)
    {
        if (Stranger(noun) is { Length: > 0 } unmet)
        {
            return unmet;
        }

        if (Unidentified(noun) is { Length: > 0 } anybodys)
        {
            return anybodys;
        }

        if (Numbered(noun, pick.Name) is { Length: > 0 } room)
        {
            return room;
        }

        if (OneOfSeveral(noun) is { Length: > 0 } together)
        {
            return together;
        }

        if (!GameStrings.IsNumberedExit(noun) || _actions is null)
        {
            return null;
        }

        string? verb = pick.Verb is { Length: > 0 } named
            ? named
            : offered.Count > 0 ? offered[0].LocalizedVerb : null;

        return Strings.ExitName(
            verb is { Length: > 0 } chosen
                ? _actions.Find(noun, chosen, _api.State.Ego)?.Script
                : null);
    }

    /// <summary>
    /// A hotel door, called by its number rather than by who is behind it.
    /// </summary>
    /// <param name="noun">The noun the scene gives it.</param>
    /// <param name="model">The model's own name, which carries the room number.</param>
    /// <returns>The label, or null when this is not a numbered door.</returns>
    private string? Numbered(string noun, string model)
    {
        if (!noun.EndsWith("_DOOR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int at = 0;

        while (at < model.Length && !char.IsAsciiDigit(model[at]))
        {
            at++;
        }

        int end = at;

        while (end < model.Length && char.IsAsciiDigit(model[end]))
        {
            end++;
        }

        // Two digits, which every room in this hotel has and no other part of a model's name
        // does. One digit is a suffix and three is something else entirely.
        return end - at == 2
            ? Text.Say("noun.ROOM", "Room {0}").Replace(
                "{0}", model[at..end], StringComparison.Ordinal)
            : null;
    }

    /// <summary>
    /// One of several copies of a thing, called by what the thing is.
    /// </summary>
    /// <param name="noun">The noun the scene gives it.</param>
    /// <returns>The name without its copy number, or null when the number belongs.</returns>
    private string? OneOfSeveral(string noun)
    {
        int end = noun.Length;

        while (end > 0 && char.IsAsciiDigit(noun[end - 1]))
        {
            end--;
        }

        if (end == noun.Length || end == 0 || !char.IsAsciiLetter(noun[end - 1]))
        {
            return null;
        }

        string stem = noun[..end];

        return _scene.Definition.Models().Any(
            m => string.Equals(m.Noun, stem, StringComparison.OrdinalIgnoreCase))
            ? stem
            : null;
    }

    /// <summary>
    /// The port's own words, for the two labels it makes up rather than reads.
    /// </summary>
    public UI.UiText Text { get; set; } = UI.UiText.English;

    /// <summary>What the game's own names for things are, when anything read them.</summary>
    public GameStrings Strings { get; set; } = GameStrings.None;

    /// <summary>Who the player has been introduced to, when anything read the table.</summary>
    public Story.Introductions Introductions { get; set; } = Story.Introductions.None;

    /// <summary>The room as it stands, for questions only it can answer.</summary>
    public SceneUpdate? Watcher { get; set; }

    /// <summary>Every noun in the room the player can act on, and where it is.</summary>
    /// <returns>Each noun once, with the middle of what it occupies in world space.</returns>
    public IReadOnlyList<(string Noun, Vector3 Where)> Nouns() =>
        [.. _picker.Interactive().Select(spot => (Labelled(spot.Noun, spot.Name), spot.Where))];

    /// <summary>What to call a noun on screen when nothing is under the pointer.</summary>
    /// <param name="noun">What the scene calls it.</param>
    /// <returns>The label.</returns>
    public string NameOf(string noun)
    {
        ArgumentNullException.ThrowIfNull(noun);

        return Labelled(noun, ModelOf(noun)?.Name ?? string.Empty);
    }

    /// <summary>What a noun is called when there is no pointer resting on it.</summary>
    /// <param name="noun">What the scene calls it.</param>
    /// <param name="model">The object it was found on, which carries a door's number.</param>
    /// <returns>The label.</returns>
    private string Labelled(string noun, string model) =>
        Stranger(noun)
        ?? Unidentified(noun)
        ?? Numbered(noun, model)
        ?? OneOfSeveral(noun)
        ?? noun;

    /// <summary>The scene's own model for a noun, when it places one.</summary>
    private PlacedModel? ModelOf(string noun) =>
        _scene.Models.FirstOrDefault(
            m => string.Equals(m.Noun, noun, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What to call somebody the player has not been introduced to.
    /// </summary>
    /// <param name="noun">The noun the scene gives them.</param>
    /// <returns>What they look like, or null when the player already knows their name.</returns>
    private string? Stranger(string noun)
    {
        if (Introductions.Knows(noun, _api) ||
            ModelOf(noun) is not { Kind: PlacedModelKind.Actor } person ||
            Watcher?.Characters?.Of(person.Name)?.IsWoman is not { } woman)
        {
            return null;
        }

        return woman
            ? Text.Say("noun.WOMAN", "Woman")
            : Text.Say("noun.MAN", "Man");
    }

    /// <summary>
    /// A thing the artists named after its owner, and the game's own rule for whether the
    /// player has worked out that it is theirs.
    /// </summary>
    /// <param name="noun">The noun the scene gives it.</param>
    /// <returns>What it is, or null when the player knows whose it is.</returns>
    private string? Unidentified(string noun)
    {
        foreach ((string[] nouns, string key, string fallback, string knows) in Owned)
        {
            if (!nouns.Contains(noun, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // A condition the story cannot answer leaves the name alone, which is the same
            // way round Introductions.Knows fails: a label shown early is a small spoiler,
            // and one withheld for ever is a thing with no name.
            try
            {
                return Sheep.SheepExpression.IsTrue(knows, _api) ? null : Text.Say(key, fallback);
            }
            catch (Formats.FormatParseException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>What each owner's machine and its plate are called before anybody knows.</summary>
    private static readonly (string[] Nouns, string Key, string Fallback, string Knows)[] Owned =
    [
        // Gabriel's own Harley, which is his only once he has hired it. The hire shop's
        // 102P models carry GABES_MOPED from the moment he walks in, so the label was
        // handing him a bike he has not paid for and has no key to. HARLEY_FOR_RENT is
        // the original's own noun for it, translated in every language and used nowhere
        // else; MOPED_KEYS is the game's own test, out of MOP102P.NVC's NOT_RENTED_BIKE.
        // Either ego's keys count, because Grace rides it on day 3 with Gabriel's.
        (["GABES_MOPED"], "noun.HARLEY_FOR_RENT", "Harley for hire",
            "DoesGabeHaveInvItem(\"MOPED_KEYS\") || DoesGraceHaveInvItem(\"MOPED_KEYS\")"),

        (["MOSELYS_MOPED"], "noun.MOPED", "Moped",
            "GetFlag(\"SeenMoselyMop\") || GetFlag(\"IDedMoselyVehicle\")"),
        (["MOSELYS_MOPED_LICENSE", "MOSELY_LICENSE_PLATE"], "noun.MOPED_LICENSE", "Moped number plate",
            "GetFlag(\"SeenMoselyMop\") || GetFlag(\"IDedMoselyVehicle\")"),

        // Lady Howard and Estelle share one. FOLLOW is the game's own second half of
        // SEEN_LADYH_ON_ROAD: following her out of the square is seeing whose it is.
        (["LADY_H_MOPED"], "noun.MOPED", "Moped",
            "GetFlag(\"SeenLadyHMop\") || GetNounVerbCount(\"LADY_HOWARD\",\"FOLLOW\") || " +
            "GetFlag(\"IDedHowardVehicle\") || GetFlag(\"IDedEstelleVehicle\")"),
        (["LADY_H_MOPED_LICENSE"], "noun.MOPED_LICENSE", "Moped number plate",
            "GetFlag(\"SeenLadyHMop\") || GetNounVerbCount(\"LADY_HOWARD\",\"FOLLOW\") || " +
            "GetFlag(\"IDedHowardVehicle\") || GetFlag(\"IDedEstelleVehicle\")"),

        (["WILKES_MOPED"], "noun.MOPED", "Moped", "GetFlag(\"IDedWilkesVehicle\")"),
        (["WILKES_MOPED_LICENSE", "WILKES_LICENSE_PLATE"], "noun.MOPED_LICENSE",
            "Moped number plate", "GetFlag(\"IDedWilkesVehicle\")"),

        (["BUCHELLIS_MOPED"], "noun.MOPED", "Moped", "GetFlag(\"IDedBuchelliVehicle\")"),
        (["BUCHELLIS_LICENSE"], "noun.MOPED_LICENSE", "Moped number plate",
            "GetFlag(\"IDedBuchelliVehicle\")"),

        (["EMILIOS_MOPED"], "noun.MOPED", "Moped", "GetFlag(\"IDedEmilioVehicle\")"),
        (["EMILIOS_LICENSE_PLATE"], "noun.MOPED_LICENSE", "Moped number plate",
            "GetFlag(\"IDedEmilioVehicle\")"),
    ];

    /// <summary>Does something to what is under the pointer.</summary>
    /// <param name="hover">What was under it, from <see cref="At"/>.</param>
    /// <param name="verb">Which verb, or null for the default one.</param>
    /// <param name="hurry">
    /// Whether the player asked twice. A double-click means the same thing as a click and
    /// means it more urgently, so the walk in front of the action is run rather than walked.
    /// </param>
    /// <returns>What happened, or null when there was nothing to do.</returns>
    public ActionOutcome? Do(Hover hover, string? verb = null, bool hurry = false) =>
        hover.Noun is { Length: > 0 } noun
            ? Do(noun, verb ?? hover.Default, hurry)
            : null;

    /// <summary>Does something to a noun the pointer is not on.</summary>
    /// <param name="noun">What the action files call it.</param>
    /// <param name="verb">The verb to perform.</param>
    /// <param name="hurry">Whether the walk in front of it is run rather than walked.</param>
    /// <returns>What happened, or null when nothing applies.</returns>
    public ActionOutcome? Do(string noun, string? verb, bool hurry = false)
    {
        ArgumentNullException.ThrowIfNull(noun);

        if (noun.Length == 0 || _actions is null)
        {
            return null;
        }

        if (verb is not { Length: > 0 } chosen)
        {
            return null;
        }

        // The two close-up verbs are the engine's own and no file writes them down, so
        // they are performed here — as the original does, by running a script it makes up
        // on the spot. A file that does declare INSPECT still wins: 40 rules do, and some
        // of them do more than move the camera.
        if (_actions.Find(noun, chosen, _api.State.Ego) is not { } rule)
        {
            if (chosen.Equals(Inspect, StringComparison.OrdinalIgnoreCase))
            {
                _api.ActingOn = noun;
                _api.State.Inspecting = noun;

                return Last = new ActionOutcome(noun, chosen, "ALL", [], Ran: true);
            }

            if (chosen.Equals(Undo, StringComparison.OrdinalIgnoreCase))
            {
                _api.State.Inspecting = string.Empty;

                return Last = new ActionOutcome(noun, chosen, "ALL", [], Ran: true);
            }

            return null;
        }

        Last = _runner.Run(rule, hurry);
        return Last;
    }

    /// <summary>
    /// Where a click would send the player, when it landed on the floor and nothing else.
    /// </summary>
    /// <param name="hover">What was under the pointer, from <see cref="At"/>.</param>
    /// <returns>
    /// A spot to walk to, or null when the click was not a click on open floor.
    /// </returns>
    public Vector3? FloorTarget(Hover hover)
    {
        if (hover.Pick is not { Kind: PickKind.Geometry } pick ||
            _floor is not { Length: > 0 } floor ||
            !string.Equals(pick.Name, floor, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A floor the scene names is a floor the player can act on, and the noun wins: one
        // object cannot be both the thing clicked and the ground under it. The noun rather
        // than whether it answers to anything right now, because a thing that answers to
        // nothing here and now is still a thing rather than somewhere to stand. TE3's
        // floor is declared <c>noclick</c> for exactly this reason and so carries no noun at
        // all — it is the floor, and the player is meant to walk on it.
        if (pick.Noun is { Length: > 0 })
        {
            return null;
        }

        if (_scene.Walkable is not { } boundary)
        {
            return pick.Point;
        }

        return boundary.NearestWalkable(pick.Point) is { } stand
            ? new Vector3(stand.X, pick.Point.Y, stand.Z)
            : null;
    }
}

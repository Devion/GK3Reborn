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
    /// <remarks>
    /// The scene's own default when it names one — a door says <c>OPEN</c> — and otherwise
    /// the first verb the resolver offers, which is the order the action files put them in.
    /// Choosing for the player is the point: <c>docs/screens.md</c> and the brief both ask
    /// for one click to do the obvious thing, with the full list a right-click away, rather
    /// than the original's two-step through a verb ring.
    /// </remarks>
    public string? Default =>
        Pick?.Verb is { Length: > 0 } named
            ? named
            : Actions.Count > 0 ? Actions[0].LocalizedVerb : null;
}

/// <summary>
/// Turns pointing at the room into doing something to it.
/// </summary>
/// <remarks>
/// <para>
/// Three pieces already existed and nothing joined them up: <see cref="ScenePicker"/> says
/// what is under a point on the screen, <see cref="ActionResolver"/> says what a noun
/// answers to at this moment in the story, and <see cref="ActionRunner"/> performs the one
/// that is chosen. This is the join, and it is deliberately thin — no state of its own, so
/// that hovering can be asked every frame without ever changing anything.
/// </para>
/// <para>
/// Hovering must be free of consequences. It happens on every mouse move, and the resolver
/// evaluates case conditions to answer, so anything that wrote to the story here would
/// advance the game by moving the mouse across it.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <para>
    /// Both verbs are in <c>VERBS.TXT</c> and neither is in the action files: 40 rules in
    /// the corpus name <c>INSPECT</c> and none names <c>INSPECT_UNDO</c>, because the
    /// original engine put both on the bar itself rather than reading them —
    /// <c>Scene::OnClicked</c> adds one or the other to every noun it shows a bar for.
    /// </para>
    /// <para>
    /// <b>The way out is the part that was missing.</b> Inspecting the register moved the
    /// view to a close-up of it and nothing could move it back: not walking away, not
    /// clicking elsewhere, and not leaving the room, so the phone room and every room after
    /// it opened pointing at a register that was not in them.
    /// </para>
    /// <para>
    /// <b>And neither verb is offered for something that cannot be looked at.</b> Reported
    /// as "Inspect / Inspect Undo, and inspect didn't even inspect": the close-up was
    /// offered for every noun in the game, most of which no scene declares a camera for, so
    /// choosing it moved nothing — and because it still counted as having happened, the menu
    /// then offered to undo the thing that had not occurred. <see cref="Watcher"/> is asked
    /// first, and it can now frame a close-up from the object's own bounds, so what is
    /// refused here is only what has no geometry at all.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// An action file writes "use the wallet on Buthane" as a rule whose verb is
    /// <c>WALLET</c>, so an item in the bag is a verb the world answers to and an item that
    /// is not is a verb nobody may choose. Offering all of them regardless is offering the
    /// player every puzzle's solution as a menu item from the first room.
    /// </remarks>
    private IReadOnlyCollection<string> Carrying => _api.State.Inventory.ItemsOf(_api.State.Ego);

    /// <summary>
    /// What to call a thing whose noun is not worth showing.
    /// </summary>
    /// <param name="noun">What the scene calls it.</param>
    /// <param name="pick">The thing itself, for the default verb its model declares.</param>
    /// <param name="offered">What it answers to, for when the model declares no verb.</param>
    /// <returns>A better name, or null to use the noun.</returns>
    /// <remarks>
    /// Only the numbered exits, and the name comes out of the game's own data: the rule
    /// behind the door says where it goes and <see cref="GameStrings.ExitName"/> turns that
    /// into what the place is called.
    /// </remarks>
    private string? Called(string noun, ScenePick pick, IReadOnlyList<AvailableAction> offered)
    {
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

    /// <summary>What the game's own names for things are, when anything read them.</summary>
    /// <remarks>
    /// Settable rather than read here, because the archives belong to the launcher and this
    /// is built per room. Left alone it knows nothing and every numbered exit is called
    /// "Exit", which is still better than a number.
    /// </remarks>
    public GameStrings Strings { get; set; } = GameStrings.None;

    /// <summary>The room as it stands, for questions only it can answer.</summary>
    /// <remarks>
    /// Whether a thing can be looked at closely depends on where it is and what it occupies,
    /// which is the live room's business rather than the action files'. Optional: without
    /// one the close-up verb is offered as it always was, which is what the tests that build
    /// an interaction with no room expect.
    /// </remarks>
    public SceneUpdate? Watcher { get; set; }

    /// <summary>Does something to what is under the pointer.</summary>
    /// <param name="hover">What was under it, from <see cref="At"/>.</param>
    /// <param name="verb">Which verb, or null for the default one.</param>
    /// <param name="hurry">
    /// Whether the player asked twice. A double-click means the same thing as a click and
    /// means it more urgently, so the walk in front of the action is run rather than walked.
    /// </param>
    /// <returns>What happened, or null when there was nothing to do.</returns>
    public ActionOutcome? Do(Hover hover, string? verb = null, bool hurry = false)
    {
        if (hover.Noun is not { Length: > 0 } noun || _actions is null)
        {
            return null;
        }

        if ((verb ?? hover.Default) is not { Length: > 0 } chosen)
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
    /// <remarks>
    /// <para>
    /// The room's floor is one named object among a hundred and the scene says which —
    /// the same <c>floor=</c> line <see cref="LoadedScene.Ground"/> reads for heights. So
    /// a floor click is a pick that reached that object and nothing nearer: a rug, a bed
    /// or a doorway standing in front of it is a click on the rug, the bed or the doorway,
    /// which is what the original does and what the player means.
    /// </para>
    /// <para>
    /// The answer is the nearest spot the boundary allows rather than the point itself.
    /// The floor mesh runs under the furniture and out through the doorways, so aiming at
    /// where the ray landed would send an actor into a wardrobe; the boundary is the
    /// authority on where a person may stand and it puts them against it instead. A point
    /// out of reach still walks — <see cref="Navigation.WalkPath"/> gets as near as the
    /// floor allows — because getting closer beats refusing to move.
    /// </para>
    /// <para>
    /// The clicked height is kept while the boundary decides the ground plan, because the
    /// boundary is a bitmap seen from above and has no storeys: on a staircase its answer
    /// alone cannot say which of the two floors above one another was meant.
    /// </para>
    /// </remarks>
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

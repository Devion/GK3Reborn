using System.Numerics;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Rendering;
using GK3Reborn.UI.Interaction;

namespace GK3Reborn.Game;

/// <summary>What the pointer is over and what can be done to it.</summary>
/// <param name="Pick">The thing itself, or null when the pointer is over nothing.</param>
/// <param name="Actions">The verbs it answers to, here and now, most likely first.</param>
public readonly record struct Hover(ScenePick? Pick, IReadOnlyList<AvailableAction> Actions)
{
    /// <summary>Nothing under the pointer.</summary>
    public static Hover Nothing => new(null, []);

    /// <summary>What the scene calls the thing, or null.</summary>
    public string? Noun => Pick?.Noun;

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

        return new Hover(pick, _actions.Resolve(noun));
    }

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

        if (_actions.Find(noun, chosen) is not { } rule)
        {
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

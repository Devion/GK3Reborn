using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>
/// What happens to a scene while nobody is doing anything to it.
/// </summary>
/// <remarks>
/// <para>
/// Everything built so far happens because something asked: a click resolves an action, a
/// script cuts the camera. This is the other half — the part of a game that runs on its
/// own. A timer set a minute ago goes off; a head that was told to look at somebody
/// arrives there rather than being there already.
/// </para>
/// <para>
/// It is deliberately the only thing that touches the clock. <c>ADR 0004</c> forbids
/// reading wall time anywhere but the platform layer, so the caller says how much time has
/// passed and everything downstream is a function of that number: two runs stepped the
/// same way do the same thing.
/// </para>
/// <para>
/// A head turns rather than snapping because that is the whole difference between a
/// character glancing at you and a character who was always facing you. The rate is fixed
/// rather than taken from the duration the scripts pass, because that argument's units are
/// not established — <c>lookitscenemodel("grace","cs2mntprthittest","h",100)</c> is not a
/// hundred seconds — and a plausible speed is better than a confident wrong one.
/// </para>
/// </remarks>
public sealed class SceneUpdate
{
    /// <summary>How long a glide takes, in seconds.</summary>
    /// <remarks>
    /// Fixed, like the turn rate and for the same reason: the scripts pass a duration whose
    /// units are not established. A second and a half is long enough to read as the camera
    /// moving rather than jumping, and short enough not to hold the player up.
    /// </remarks>
    public const double GlideSeconds = 1.5;

    /// <summary>How fast a head turns, in radians a second.</summary>
    /// <remarks>
    /// About 170 degrees a second: fast enough to read as noticing something, slow enough
    /// to read as a person doing it. A <see cref="Glance.Quick"/> glance skips it.
    /// </remarks>
    public const float TurnRate = 3f;

    private readonly List<Turning> _actors = [];
    private readonly Dictionary<string, Walking> _walking =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PlacedModel> _standing =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Gk3SheepApi _api;
    private readonly Glances _glances;
    private readonly ISceneSink _geometry;
    private readonly ActionResolver? _actions;
    private readonly ActionRunner? _runner;
    private readonly SheepScheduler? _scripts;
    private readonly LoadedScene _scene;

    private string _angle = string.Empty;
    private Camera? _from;
    private Camera? _to;
    private double _glided;

    /// <summary>Creates an update for one standing scene.</summary>
    /// <param name="scene">The scene, already loaded.</param>
    /// <param name="api">The story host, for the timers it keeps.</param>
    /// <param name="glances">Who is looking at what.</param>
    /// <param name="geometry">Where the scene was put, so heads can move in it.</param>
    /// <param name="actions">What may be done to things, for timers coming due.</param>
    /// <param name="runner">How to do it.</param>
    /// <param name="scripts">Scripts that are waiting for something, if anything is.</param>
    public SceneUpdate(
        LoadedScene scene,
        Gk3SheepApi api,
        Glances glances,
        ISceneSink geometry,
        ActionResolver? actions = null,
        ActionRunner? runner = null,
        SheepScheduler? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(glances);
        ArgumentNullException.ThrowIfNull(geometry);

        _api = api;
        _scene = scene;
        _glances = glances;
        _geometry = geometry;
        _actions = actions;
        _runner = runner;
        _scripts = scripts;

        foreach (PlacedModel placed in scene.Models)
        {
            if (placed.Kind != PlacedModelKind.Actor ||
                !placed.Placement.Exists ||
                CharacterHead.Find(placed.Model) is not { } head)
            {
                continue;
            }

            _actors.Add(new Turning(placed, head));
        }

        // Under both names. A scene places `gab` and calls him GABRIEL, and scripts use
        // whichever they feel like — the state's ego is the noun, an action's target is
        // usually the model. Keying by one of them means half the walks find nobody.
        foreach (PlacedModel placed in scene.Models)
        {
            if (placed.Kind != PlacedModelKind.Actor || !placed.Placement.Exists)
            {
                continue;
            }

            _standing[placed.Name] = placed;

            if (placed.Noun is { Length: > 0 } noun)
            {
                _standing[noun] = placed;
            }
        }
    }

    /// <summary>How many actors are crossing the room.</summary>
    public int OnTheMove => _walking.Count;

    /// <summary>Where an actor is now, if the scene has one by that name.</summary>
    /// <param name="actor">The actor's model name.</param>
    /// <returns>Their position, or null.</returns>
    public Vector3? Where(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _walking.TryGetValue(actor, out Walking? walking)
            ? walking.Walker.Position
            : _standing.TryGetValue(actor, out PlacedModel? placed)
                ? placed.Transform.Translation
                : null;
    }

    /// <summary>
    /// Sets an actor walking to a place on the floor.
    /// </summary>
    /// <param name="actor">Their model name.</param>
    /// <param name="destination">Where to go, in world space.</param>
    /// <returns>How long the walk will take, or zero when there is no walking to do.</returns>
    /// <remarks>
    /// <para>
    /// The route is found across the walk boundary rather than aimed straight at the
    /// destination, so an actor asked to cross a room goes round the bed rather than
    /// through it. A boundary that cannot reach the destination gives the closest it can,
    /// which is what the original does: getting as near as the floor allows beats refusing
    /// to move.
    /// </para>
    /// <para>
    /// Asking again replaces the walk in progress. A script that changes its mind means it.
    /// </para>
    /// </remarks>
    public double Walk(string actor, Vector3 destination)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3310", DiagnosticSeverity.Warning,
                "A script asked an actor to walk who is not in the room.",
                _scene.Name, null, "an actor the scene placed", actor,
                "Check the name against the scene's [ACTORS] section."));

            return 0;
        }

        Vector3 from = Where(actor) ?? placed.Transform.Translation;
        float facing = _walking.TryGetValue(actor, out Walking? already)
            ? already.Walker.Facing
            : MathF.Atan2(placed.Transform.M31, placed.Transform.M33);

        WalkRoute route = _scene.Walkable is { } boundary
            ? WalkPath.Find(boundary, from, destination)

            // No boundary is no obstacles, so the straight line is the route.
            : new WalkRoute(true, [destination]);

        var walker = new Walker(actor, route, from, facing);

        if (!walker.Walking)
        {
            _walking.Remove(actor);
            return 0;
        }

        _walking[actor] = new Walking(placed, walker);
        return walker.Seconds;
    }

    /// <summary>Stops everyone where they stand.</summary>
    /// <remarks>For leaving the room, where a walk in progress has nowhere to arrive.</remarks>
    public void StopWalking()
    {
        foreach (Walking walking in _walking.Values)
        {
            walking.Walker.Stop();
        }

        _walking.Clear();
    }

    /// <summary>Diagnostics raised while the world went on by itself.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>How many actors in the scene have a head that can turn.</summary>
    public int Movable => _actors.Count;

    /// <summary>
    /// Where the story wants the view, or null while it has not moved it.
    /// </summary>
    /// <remarks>
    /// A cut arrives at once and a glide takes <see cref="GlideSeconds"/> to get there, so
    /// during one this is somewhere between the two cameras rather than at either. The
    /// player's own camera is a separate thing: this says where the story put the view, and
    /// what happens to a player who was flying it around is the caller's decision.
    /// </remarks>
    public Camera? View { get; private set; }

    /// <summary>Whether the view is on its way somewhere.</summary>
    public bool Gliding => _to is not null && _glided < GlideSeconds;

    /// <summary>Says where the view already is, so a glide has somewhere to leave from.</summary>
    /// <param name="camera">Where the scene opened.</param>
    /// <remarks>
    /// Without this the first move the story makes is always a cut, however it was asked
    /// for, because there is nothing to interpolate away from. The caller knows where the
    /// scene started and this does not. It deliberately does not take note of where the
    /// story currently wants the view: a glide asked for before the first frame is still a
    /// glide, and swallowing it here would turn it into a cut.
    /// </remarks>
    public void StartAt(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        View = camera;
    }

    /// <summary>Lets time pass.</summary>
    /// <param name="seconds">How much.</param>
    /// <returns>What the world did on its own, for whoever wants to say so.</returns>
    public IReadOnlyList<string> Advance(double seconds)
    {
        if (seconds <= 0)
        {
            return [];
        }

        List<string> happened = [];

        // The scripts first: one carrying on from a wait may cut the camera or set a
        // timer, and it should take effect in the frame it happened rather than the next.
        foreach (string carried in _scripts?.Advance(seconds) ?? [])
        {
            happened.Add($"{carried} carried on");
        }

        MoveView(seconds);

        foreach (GameTimer timer in _api.State.Timers.Advance(seconds))
        {
            happened.Add(Fire(timer));
        }

        // Walking before turning heads: a head that is looking at something has to be
        // aimed from where its owner is now, not from where they were a frame ago.
        foreach (string who in _walking.Keys.ToList())
        {
            Walking walking = _walking[who];

            if (!walking.Walker.Advance((float)seconds))
            {
                _walking.Remove(who);
                happened.Add($"{who} arrived");
            }

            _geometry.MoveModel(walking.Placement, walking.Walker.Transform(walking.Scale));

            foreach (Turning actor in _actors)
            {
                actor.MovedTo(who, walking.Walker.Position, walking.Walker.Facing);
            }
        }

        foreach (Turning actor in _actors)
        {
            if (actor.Step(_glances, (float)seconds))
            {
                _geometry.TurnMesh(actor.Placement, actor.Head, actor.Turn());
            }
        }

        return happened;
    }

    /// <summary>Takes the view wherever the story has put it.</summary>
    /// <remarks>
    /// The story moving the camera is a change of <see cref="GameState.CameraAngle"/> and
    /// nothing else, so this watches for one. Position and target are eased separately and
    /// linearly: an arc would look better and would also invent framing the artists did not
    /// author, which <c>Plan/03</c> section 5 says to leave alone.
    /// </remarks>
    private void MoveView(double seconds)
    {
        string wanted = _api.State.CameraAngle;

        if (!string.Equals(wanted, _angle, StringComparison.OrdinalIgnoreCase))
        {
            _angle = wanted;
            _from = View;
            _to = wanted.Length > 0 ? SceneLoader.CameraFor(_scene, _geometry, wanted) : null;
            _glided = _api.State.CameraGliding && _from is not null ? 0 : GlideSeconds;
        }

        if (_to is null)
        {
            return;
        }

        _glided += seconds;

        if (_from is null || _glided >= GlideSeconds)
        {
            View = _to;
            return;
        }

        float part = (float)(_glided / GlideSeconds);

        View = new Camera
        {
            Position = Vector3.Lerp(_from.Position, _to.Position, part),
            Target = Vector3.Lerp(_from.Target, _to.Target, part),
            Up = _to.Up,
            FieldOfView = float.Lerp(_from.FieldOfView, _to.FieldOfView, part),
            NearPlane = _to.NearPlane,
            FarPlane = _to.FarPlane,
        };
    }

    /// <summary>Performs an action that has come due.</summary>
    private string Fire(GameTimer timer)
    {
        if (_actions is null || _runner is null)
        {
            return $"{timer.Noun}:{timer.Verb} came due and there is nothing here to run it";
        }

        if (_actions.Find(timer.Noun, timer.Verb) is not { } rule)
        {
            return $"{timer.Noun}:{timer.Verb} came due and nothing applies to it now";
        }

        ActionOutcome outcome = _runner.Run(rule);

        foreach (Diagnostic diagnostic in _runner.Diagnostics.Items)
        {
            Diagnostics.Add(diagnostic);
        }

        return $"{timer.Noun}:{timer.Verb} [{rule.Case}] " +
               (outcome.Ran ? "ran" : "was refused");
    }

    /// <summary>One actor crossing the room, and what to move when they do.</summary>
    private sealed class Walking
    {
        public Walking(PlacedModel placed, Walker walker)
        {
            Placement = placed.Placement;
            Walker = walker;

            // The placement is scale, then a turn, then a move, so the scale comes back out
            // as the length of a basis vector. Rebuilding the transform without it would
            // resize the actor the moment they took a step.
            Scale = new Vector3(
                placed.Transform.M11, placed.Transform.M12, placed.Transform.M13).Length();

            if (Scale <= 0)
            {
                Scale = 1f;
            }
        }

        public ModelPlacement Placement { get; }

        public Walker Walker { get; }

        public float Scale { get; }
    }

    /// <summary>One actor's head, and where it is on its way to.</summary>
    private sealed class Turning
    {
        private readonly string _name;
        private readonly float _eyes;

        private Vector3 _standing;
        private float _facing;

        private float _yaw;
        private float _pitch;

        public Turning(PlacedModel placed, int head)
        {
            _name = placed.Name;
            Placement = placed.Placement;
            Head = head;

            _standing = placed.Transform.Translation;

            // The placement is a turn about the up axis and then a move, so the way the
            // actor faces can be read straight back out of it.
            _facing = MathF.Atan2(placed.Transform.M31, placed.Transform.M33);
            _eyes = CharacterHead.PivotOf(placed.Model, head).Y;
        }

        public ModelPlacement Placement { get; }

        public int Head { get; }

        /// <summary>Tells a head where its owner has got to.</summary>
        /// <remarks>
        /// A glance is worked out from where the looker is standing, so an actor who walks
        /// while looking at something would go on aiming their head at where the thing was
        /// relative to where they set off from.
        /// </remarks>
        public void MovedTo(string actor, Vector3 standing, float facing)
        {
            if (!string.Equals(actor, _name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _standing = standing;
            _facing = facing;
        }

        /// <summary>Moves the head towards wherever it is meant to be looking.</summary>
        /// <returns>True when it moved, and the geometry needs telling.</returns>
        public bool Step(Glances glances, float seconds)
        {
            (float yaw, float pitch) = glances.Of(_name) is { } glance
                ? Glances.Turn(_standing, _facing, _eyes, glance.Point)
                : (0f, 0f);

            bool quick = glances.Of(_name)?.Quick ?? false;
            float most = quick ? float.MaxValue : SceneUpdate.TurnRate * seconds;

            float wasYaw = _yaw;
            float wasPitch = _pitch;

            _yaw = Toward(_yaw, yaw, most);
            _pitch = Toward(_pitch, pitch, most);

            return MathF.Abs(_yaw - wasYaw) > 1e-5f || MathF.Abs(_pitch - wasPitch) > 1e-5f;
        }

        /// <summary>Where the head is now.</summary>
        /// <remarks>
        /// Pitch about the mesh's own sideways axis inside the yaw, which is nodding within
        /// a turn rather than turning a nodded head — what a neck does.
        /// </remarks>
        public Matrix4x4 Turn() =>
            Matrix4x4.CreateRotationX(-_pitch) * Matrix4x4.CreateRotationY(_yaw);

        private static float Toward(float from, float to, float most)
        {
            float step = to - from;

            return MathF.Abs(step) <= most ? to : from + (MathF.Sign(step) * most);
        }
    }
}

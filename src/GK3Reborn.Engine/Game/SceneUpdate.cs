using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
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
    /// <summary>How fast a head turns, in radians a second.</summary>
    /// <remarks>
    /// About 170 degrees a second: fast enough to read as noticing something, slow enough
    /// to read as a person doing it. A <see cref="Glance.Quick"/> glance skips it.
    /// </remarks>
    public const float TurnRate = 3f;

    private readonly List<Turning> _actors = [];
    private readonly Gk3SheepApi _api;
    private readonly Glances _glances;
    private readonly ISceneSink _geometry;
    private readonly ActionResolver? _actions;
    private readonly ActionRunner? _runner;

    /// <summary>Creates an update for one standing scene.</summary>
    /// <param name="scene">The scene, already loaded.</param>
    /// <param name="api">The story host, for the timers it keeps.</param>
    /// <param name="glances">Who is looking at what.</param>
    /// <param name="geometry">Where the scene was put, so heads can move in it.</param>
    /// <param name="actions">What may be done to things, for timers coming due.</param>
    /// <param name="runner">How to do it.</param>
    public SceneUpdate(
        LoadedScene scene,
        Gk3SheepApi api,
        Glances glances,
        ISceneSink geometry,
        ActionResolver? actions = null,
        ActionRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(glances);
        ArgumentNullException.ThrowIfNull(geometry);

        _api = api;
        _glances = glances;
        _geometry = geometry;
        _actions = actions;
        _runner = runner;

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
    }

    /// <summary>Diagnostics raised while the world went on by itself.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>How many actors in the scene have a head that can turn.</summary>
    public int Movable => _actors.Count;

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

        foreach (GameTimer timer in _api.State.Timers.Advance(seconds))
        {
            happened.Add(Fire(timer));
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

    /// <summary>One actor's head, and where it is on its way to.</summary>
    private sealed class Turning
    {
        private readonly string _name;
        private readonly Vector3 _standing;
        private readonly float _facing;
        private readonly float _eyes;

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

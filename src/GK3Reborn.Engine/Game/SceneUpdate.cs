using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Formats.Animation;
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

    private readonly Dictionary<string, PlacedModel> _models =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Playing> _playing = [];
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

        // Everything that stands in the room, so a clip can find what it animates. A clip
        // names its target model in its own header, which is the only reliable pairing.
        foreach (PlacedModel placed in scene.Models)
        {
            if (placed.Placement.Exists)
            {
                _models[placed.Name] = placed;
            }
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

    /// <summary>Where the clips come from, when anything is to be played.</summary>
    /// <remarks>
    /// Optional. Without it <see cref="Play"/> finds nothing and animation calls go on
    /// being recorded, which is what every tool wants and what the launcher wanted until
    /// there was a reader.
    /// </remarks>
    public Content.ClipLibrary? Clips { get; set; }

    /// <summary>Where the animations that name those clips come from.</summary>
    public Content.AnimationLibrary? Animations { get; set; }

    /// <summary>How many clips are running.</summary>
    public int Animating => _playing.Count;

    /// <summary>
    /// Starts an animation.
    /// </summary>
    /// <param name="name">What the script called it, such as <c>GraCs3WrdbOpen</c>.</param>
    /// <param name="repeat">Whether it starts again when it ends.</param>
    /// <param name="moves">
    /// Whether the actor keeps the ground the clip covered. GK3 calls these move
    /// animations: an ordinary one leaves the <em>pose</em> where the clip finished but
    /// puts the actor's position and heading back where they were, so a character who
    /// mimes walking has not actually gone anywhere.
    /// </param>
    /// <returns>How long it will take, or zero when there is nothing to play.</returns>
    /// <remarks>
    /// <para>
    /// A script names an <c>.ANM</c>, whose <c>[ACTIONS]</c> section names one or more
    /// <c>.ACT</c> clips and the frame each starts on. Each clip names the model it moves.
    /// None of those three names is the one the script said.
    /// </para>
    /// <para>
    /// Only the rigid part plays: a clip's mesh transforms are applied, its vertex poses are
    /// not. That covers 2,188 of the corpus's 5,796 clips outright — doors, drawers, a
    /// telephone — and moves a character's mesh groups about without deforming any of them,
    /// which is wrong-looking but is where the geometry actually goes.
    /// </para>
    /// </remarks>
    public double Play(string name, bool repeat = false, bool moves = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Clips is null || Animations is null)
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3315", DiagnosticSeverity.Warning,
                "Nothing can play animations here.",
                _scene.Name, null, "a clip library and an animation library",
                $"clips={Clips is not null}, animations={Animations is not null}",
                "The launcher sets both once the scene is standing."));

            return 0;
        }

        if (Animations.Read(name) is not { } animation)
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3312", DiagnosticSeverity.Warning,
                "A script asked for an animation the archives do not have.",
                _scene.Name, null, "an .ANM of that name", name,
                "Check the name against the animation-scripts directory."));

            return 0;
        }

        if (animation.Actions.Count == 0)
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3313", DiagnosticSeverity.Info,
                "An animation names no clips, so it moves nothing.",
                name, null, "an [ACTIONS] section", "none",
                "Some animations are only sounds and captions."));

            return 0;
        }

        double longest = 0;

        foreach (AnimationAction action in animation.Actions)
        {
            if (Clips.Read(action.Name) is not { } clip)
            {
                Diagnostics.Add(new Diagnostic(
                    "GK3R3314", DiagnosticSeverity.Warning,
                    "An animation names a clip the archives do not have.",
                    name, null, "an .ACT of that name", action.Name,
                    "Check the [ACTIONS] line against the animations directory."));

                continue;
            }

            if (!_models.TryGetValue(clip.ModelName, out PlacedModel? target))
            {
                Diagnostics.Add(new Diagnostic(
                    "GK3R3311", DiagnosticSeverity.Info,
                    "An animation moves a model that is not in this room.",
                    name, null, "a model the scene placed", clip.ModelName,
                    "Common and usually harmless: clips are shared between rooms."));

                continue;
            }

            // The move flag is carried but not yet spent. Committing the ground a clip
            // covered means writing the actor's position, and Walker already owns that —
            // the two have to be reconciled before either may write it.
            _playing.Add(new Playing(clip, target, action, repeat, moves));
            longest = Math.Max(longest, clip.Duration + (action.Frame / 15.0));
        }

        return longest;
    }

    /// <summary>Stops everything a model is doing.</summary>
    /// <param name="model">Its name, or null for everything in the room.</param>
    public void StopAnimating(string? model = null)
    {
        if (model is not { Length: > 0 })
        {
            _playing.Clear();
            return;
        }

        _playing.RemoveAll(p =>
            p.Clip.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
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

        // Animation before walking: a clip poses a model's meshes in the model's own space
        // and walking moves the model, so doing it the other way round would apply this
        // frame's poses to last frame's position.
        for (int i = _playing.Count - 1; i >= 0; i--)
        {
            if (!_playing[i].Step(_geometry, (float)seconds))
            {
                happened.Add($"{_playing[i].Clip.Name} finished");
                _playing.RemoveAt(i);
            }
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

    /// <summary>One clip running on one model.</summary>
    private sealed class Playing
    {
        private readonly bool _repeat;
        private readonly bool _moves;
        private readonly double _delay;
        private readonly Matrix4x4 _correction;

        private double _elapsed;

        public Playing(
            ActFile clip, PlacedModel target, AnimationAction action, bool repeat, bool moves)
        {
            Clip = clip;
            Target = target;
            _repeat = repeat;
            _moves = moves;
            _delay = action.Frame / (double)AnimationFile.FramesPerSecond;
            _correction = Correction(clip, target, action.Placement);
        }

        public ActFile Clip { get; }

        public PlacedModel Target { get; }

        /// <summary>
        /// Where the clip's own space has to be moved to for it to play here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A clip's mesh transforms are wherever the animator authored them, which for a
        /// walk is halfway across some other room. Played as written, the character
        /// disappears.
        /// </para>
        /// <para>
        /// So a <b>relative</b> clip — 92% of them — is shifted once, at the start, by
        /// however far its first frame sits from where the model rests. Root motion within
        /// the clip then still happens, measured from where the actor was standing. The
        /// shift is computed once and held, because recomputing it per frame would cancel
        /// exactly the movement it is meant to preserve.
        /// </para>
        /// <para>
        /// An <b>absolute</b> clip carries its own spot and heading and is put there.
        /// </para>
        /// <para>
        /// The reference point is the average of the mesh groups' origins. The original
        /// uses the shoes, named per character in <c>CHARACTERS.TXT</c>, which is not read
        /// yet; the average moves with the same rigid motion and differs only by a constant,
        /// which a difference of two averages cancels.
        /// </para>
        /// </remarks>
        private static Matrix4x4 Correction(
            ActFile clip, PlacedModel target, AnimationPlacement? placement)
        {
            if (placement is { } spot)
            {
                return Matrix4x4.CreateRotationY(spot.Heading) *
                       Matrix4x4.CreateTranslation(spot.Position);
            }

            Vector3 rest = Average(target.Model.Meshes.Select(m => m.MeshToLocal.Translation));

            Vector3 opens = Average(Enumerable
                .Range(0, clip.MeshCount)
                .Select(m => clip.PoseOf(m, 0))
                .Where(p => p is not null)
                .Select(p => p!.Value.Translation));

            return Matrix4x4.CreateTranslation(rest - opens);
        }

        private static Vector3 Average(IEnumerable<Vector3> points)
        {
            Vector3 total = Vector3.Zero;
            int count = 0;

            foreach (Vector3 point in points)
            {
                total += point;
                count++;
            }

            return count > 0 ? total / count : Vector3.Zero;
        }

        /// <summary>Poses the model for this moment.</summary>
        /// <returns>True while the clip is still running.</returns>
        /// <remarks>
        /// The frame is worked out from elapsed time rather than counted, so a dropped frame
        /// skips a pose instead of slowing the animation down. Fifteen frames a second is
        /// the original's rate and a large part of why its animation reads as stiff.
        /// </remarks>
        public bool Step(ISceneSink geometry, float seconds)
        {
            _elapsed += seconds;

            if (_elapsed < _delay)
            {
                return true;
            }

            double running = _elapsed - _delay;
            int frame = (int)(running * AnimationFile.FramesPerSecond);

            if (frame >= Clip.FrameCount)
            {
                if (!_repeat)
                {
                    // The last frame first. A frame long enough to run past the end should
                    // still leave the model where the clip finished, which is the whole of
                    // what a move animation means; skipping to the stop would leave it
                    // wherever the previous frame happened to be.
                    Pose(geometry, Clip.FrameCount - 1);
                    return false;
                }

                _elapsed = _delay;
                frame = 0;
            }

            Pose(geometry, frame);
            return true;
        }

        /// <summary>Puts the model into one frame of the clip.</summary>
        private void Pose(ISceneSink geometry, int frame)
        {
            for (int mesh = 0; mesh < Clip.MeshCount; mesh++)
            {
                if (Clip.PoseOf(mesh, frame) is { } pose)
                {
                    geometry.PoseMesh(Target.Placement, mesh, pose * _correction);
                }

                // The shapes, where the clip has them. Without these a character is mesh
                // groups sliding about: 3,085 of the corpus's 3,086 character clips deform.
                foreach (int submesh in Clip.ShapedSubmeshes(mesh))
                {
                    if (Clip.ShapeOf(mesh, submesh, frame) is { } shape)
                    {
                        geometry.ShapeMesh(Target.Placement, mesh, submesh, shape);
                    }
                }
            }
        }
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

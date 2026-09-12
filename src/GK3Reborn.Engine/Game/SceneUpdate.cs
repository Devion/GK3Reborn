using System.Numerics;
using System.Globalization;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>One of the three things a character does when nobody is telling them to do anything.</summary>
public enum FidgetKind
{
    /// <summary>Waiting: breathing, shifting weight, looking about.</summary>
    Idle,

    /// <summary>Speaking: the gestures that go with a line.</summary>
    Talk,

    /// <summary>Being spoken to.</summary>
    Listen,
}

/// <summary>What happens to a scene while nobody is doing anything to it.</summary>
public sealed class SceneUpdate
{
    /// <summary>How long a glide takes, in seconds.</summary>
    public const double GlideSeconds = 1.5;

    /// <summary>How fast a head turns, in radians a second.</summary>
    public const float TurnRate = 3f;

    /// <summary>How much faster an actor moves when the player is in a hurry.</summary>
    public float RunBeyond { get; set; } = 250f;

    /// <summary>Whether the next walk arrives at once instead of being walked.</summary>
    public bool WarpNextWalk { get; set; }

    public float HurryFactor
    {
        get => _hurryFactor;

        // Clamped where it is set rather than trusted: a pace of zero is an actor who never arrives, and every wait in the game is measured against.
        set => _hurryFactor = float.IsFinite(value) ? Math.Clamp(value, 1f, 4f) : DefaultHurryFactor;
    }

    /// <summary>The pace a double-click asks for unless the player has said otherwise.</summary>
    public const float DefaultHurryFactor = 2f;

    private float _hurryFactor = DefaultHurryFactor;

    private readonly List<Cue> _cues = [];

    private readonly List<Showing> _showings = [];

    private readonly List<Footfall> _steps = [];

    private readonly List<Swap> _swaps = [];

    /// <summary>What animations are about to repaint and reveal about the room itself.</summary>
    private readonly List<Scheduled<AnimationSceneTexture>> _roomSwaps = [];

    private readonly List<Scheduled<AnimationSceneVisibility>> _roomShowings = [];

    /// <summary>What animations are about to say, frame and film as they run.</summary>
    private readonly List<Scheduled<AnimationDialogue>> _lines = [];

    private readonly List<Scheduled<AnimationShot>> _shots = [];
    private readonly List<Scheduled<AnimationMood>> _moods = [];
    private readonly List<Scheduled<AnimationMusic>> _music = [];
    private readonly List<Turning> _actors = [];
    private readonly Dictionary<string, Walking> _walking = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PlacedModel> _standing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each actor logically is, as against where their model is drawn.</summary>
    private readonly Dictionary<string, Vector3> _logical = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PlacedModel> _models = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Playing> _playing = [];

    /// <summary>A model whose clip is authored in somebody else's space, and whose space that is.</summary>
    private readonly Dictionary<string, (PlacedModel Held, PlacedModel Holder)> _carried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last shift a clip asked of each model, kept for as long as the room stands.</summary>
    private readonly Dictionary<string, Matrix4x4> _space = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which of each model's mesh groups are the arms its owner can see, worked out once per model.</summary>
    private readonly Dictionary<string, int[]> _arms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>And which one is the head, for a model whose head is not refined and so carries no rig.</summary>
    private readonly Dictionary<string, int?> _heads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Models whose behaviour script is held while something else animates them.</summary>
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

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
    public SceneUpdate( LoadedScene scene, Gk3SheepApi api, Glances glances, ISceneSink geometry, ActionResolver? actions = null,
        ActionRunner? runner = null, SheepScheduler? scripts = null)
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
        _triggers.AddRange(scene.Definition.Triggers());

        foreach (PlacedModel placed in scene.Models)
        {
            if (placed.Kind != PlacedModelKind.Actor || !placed.Placement.Exists || CharacterHead.Find(placed.Model) is not { } head)
            {
                continue;
            }

            _actors.Add(new Turning(placed, head));
        }

        // Everything that stands in the room, so a clip can find what it animates.
        foreach (PlacedModel placed in scene.Models)
        {
            if (placed.Placement.Exists)
            {
                _models[placed.Name] = placed;
            }
        }

        // Under both names.
        foreach (PlacedModel placed in scene.Models)
        {
            if (placed.Kind != PlacedModelKind.Actor || !placed.Placement.Exists)
            {
                continue;
            }

            _standing[placed.Name] = placed;
            _logical[placed.Name] = placed.Transform.Translation;

            if (placed.Noun is { Length: > 0 } noun)
            {
                _standing[noun] = placed;
                _logical[noun] = placed.Transform.Translation;
            }
        }
    }

    /// <summary>Where the clips come from, when anything is to be played.</summary>
    private readonly List<Behaviour> _scenery = [];

    private readonly Dictionary<string, Fidget> _fidgets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the cosmetic choices are drawn from.</summary>
    private readonly Foundation.DeterministicRandom _chance = new(0xA5F1D2C3B4E59687);

    public Content.ClipLibrary? Clips { get; set; }

    /// <summary>Where the animations that name those clips come from.</summary>
    public Content.AnimationLibrary? Animations { get; set; }

    /// <summary>Told where every actor stands, whenever a clip takes one or lets one go.</summary>
    public Action<string>? TraceActors { get; set; }

    /// <summary>Reports where an actor stands, for .</summary>
    /// <param name="what">What just happened, such as plays or ends.</param>
    /// <param name="clip">The clip it happened to.</param>
    /// <param name="target">Whose it is.</param>
    /// <param name="note">Anything else worth saying, or empty.</param>
    private void Trace(string what, string clip, PlacedModel target, string note = "")
    {
        if (TraceActors is not { } tell || target.Kind != PlacedModelKind.Actor)
        {
            return;
        }

        Matrix4x4 standing = _geometry.TransformOf(target.Placement);
        Vector3 where = standing.Translation;
        float heading = Navigation.Walker.Wrapped( Navigation.Walker.Rotation(MathF.Atan2(standing.M31, standing.M33)) +
            (target.BuiltFacing ?? MathF.PI) - MathF.PI);

        tell(string.Create( CultureInfo.InvariantCulture, $"{target.Name} {what} {clip}: placed ({where.X:0.#}, {where.Z:0.#}) " +
            $"facing {heading * 180f / MathF.PI:0.#}°{(note.Length > 0 ? ", " + note : string.Empty)}"));
    }

    /// <summary>The faces in the room, when there is anything that can move one.</summary>
    public Actors.Faces? Faces { get; set; }

    /// <summary>How many clips are running.</summary>
    public int Animating => _playing.Count;

    /// <summary>Starts an animation.</summary>
    /// <returns>How long it will take, or zero when there is nothing to play.</returns>
    /// <param name="name">What the script called it, such as GraCs3WrdbOpen.</param>
    /// <param name="repeat">Whether it starts again when it ends.</param>
    /// <param name="moves">Whether the actor keeps the ground the clip covered.</param>
    /// <param name="fromBehaviour">Whether a model's own behaviour script asked for it rather than the story.</param>
    public double Play( string name, bool repeat = false, bool moves = false, bool fromBehaviour = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Clips is null || Animations is null)
        {
            Diagnostics.Add(new Diagnostic( "GK3R3315", DiagnosticSeverity.Warning, "Nothing can play animations here.",
                _scene.Name, null, "a clip library and an animation library", $"clips={Clips is not null}, animations={Animations is not null}",
                "The launcher sets both once the scene is standing."));

            return 0;
        }

        if (Animations.Read(name) is not { } animation)
        {
            Diagnostics.Add(new Diagnostic( "GK3R3312", DiagnosticSeverity.Warning, "A script asked for an animation the archives do not have.",
                _scene.Name, null, "an .ANM of that name", name, "Check the name against the animation-scripts directory."));

            return 0;
        }

        // The sounds first, before anything that can return.
        foreach (AnimationSound cue in animation.Sounds)
        {
            _cues.Add(new Cue(cue, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        // And the feet.
        foreach (AnimationStep step in animation.Steps)
        {
            _steps.Add(new Footfall(step, repeat ? animation.Duration : 0, animation.Rate));
        }

        // And what it repaints as it runs.
        foreach (AnimationTexture swap in animation.Textures)
        {
            _swaps.Add(new Swap(swap, repeat ? animation.Duration : 0, animation.Rate));
        }

        Repaint(animation.Textures.Where(t => t.Frame <= 0));

        // And what it repaints about the room rather than about a model.
        foreach (AnimationSceneTexture swap in animation.SceneTextures)
        {
            _roomSwaps.Add(new Scheduled<AnimationSceneTexture>( swap, swap.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        PaintRoom(animation.SceneTextures.Where(t => t.Frame <= 0));

        foreach (AnimationSceneVisibility change in animation.SceneVisibility)
        {
            _roomShowings.Add(new Scheduled<AnimationSceneVisibility>( change, change.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        RevealRoom(animation.SceneVisibility.Where(v => v.Frame <= 0));

        // And what it says, frames and puts on people's faces.
        foreach (AnimationDialogue spoken in animation.Dialogue)
        {
            _lines.Add(new Scheduled<AnimationDialogue>( spoken, spoken.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        foreach (AnimationShot shot in animation.Shots)
        {
            _shots.Add(new Scheduled<AnimationShot>( shot, shot.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        foreach (AnimationMood mood in animation.Moods)
        {
            _moods.Add(new Scheduled<AnimationMood>( mood, mood.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        // And what it does to the music under it.
        foreach (AnimationMusic change in animation.Music)
        {
            _music.Add(new Scheduled<AnimationMusic>( change, change.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        // Frame zero is now, as it is for the repaints and the reveals above.
        Say(animation.Dialogue.Where(d => d.Frame <= 0));
        Film(animation.Shots.Where(s => s.Frame <= 0));
        Wear(animation.Moods.Where(m => m.Frame <= 0));
        Score(animation.Music.Where(m => m.Frame <= 0));

        // Then what it shows and hides, for the same reason and one of its own: an animation that brings somebody into the room does it here, and.
        foreach (AnimationVisibility change in animation.Visibility)
        {
            _showings.Add(new Showing(change, repeat ? animation.Duration : 0, animation.Rate));
        }

        // Frame zero is now rather than in a frame's time.
        Reveal(animation.Visibility.Where(v => v.Frame <= 0));

        // Faces next, because an animation that only moves a face moves no geometry at all: ABEANGRY is two frames of eyebrow and nothing else.
        bool onAFace = (animation.Faces.Count > 0 || animation.Mouths.Count > 0) && Faces?.Perform(animation) == true;

        if (animation.Actions.Count == 0)
        {
            // A face or a sound is still something happening, and a script that waits on one is waiting for it to finish rather than for nothing.
            if (onAFace || animation.Sounds.Count > 0 || animation.Visibility.Count > 0 || animation.Textures.Count > 0 ||
                animation.SceneTextures.Count > 0 || animation.SceneVisibility.Count > 0 || animation.Steps.Count > 0 ||
                animation.Dialogue.Count > 0 || animation.Shots.Count > 0 || animation.Moods.Count > 0 || animation.Music.Count > 0)
            {
                return animation.Duration;
            }

            Diagnostics.Add(new Diagnostic( "GK3R3313", DiagnosticSeverity.Info, "An animation names no clips, so it moves nothing.",
                name, null, "an [ACTIONS] section", "none", "Some animations are only sounds and captions."));

            return 0;
        }

        double longest = 0;

        // Whose space this file's other clips are authored in — the man with the binoculars, and see _carried.
        PlacedModel? holder = Holder(name, animation);

        foreach (AnimationAction action in animation.Actions)
        {
            if (Clips.Read(action.Name) is not { } clip)
            {
                Diagnostics.Add(new Diagnostic( "GK3R3314", DiagnosticSeverity.Warning, "An animation names a clip the archives do not have.",
                    name, null, "an .ACT of that name", action.Name, "Check the [ACTIONS] line against the animations directory."));

                continue;
            }

            if (!_models.TryGetValue(clip.ModelName, out PlacedModel? target))
            {
                Diagnostics.Add(new Diagnostic( "GK3R3311", DiagnosticSeverity.Info, "An animation moves a model that is not in this room.",
                    name, null, "a model the scene placed", clip.ModelName, "Common and usually harmless: clips are shared between rooms."));

                continue;
            }

            // An idle never talks over the story.
            if (fromBehaviour && _playing.Any(p => Drives(p, target) && !p.FromBehaviour))
            {
                continue;
            }

            // And one clip at a time either way: starting one on a model stops whatever that model was doing, which is what VertexAnimator::Start.
            if (!fromBehaviour && _playing.Any(p => Drives(p, target) && p.FromBehaviour) && BehaviourOf(target.Name) is { } interrupted)
            {
                interrupted.Interrupted = true;
            }

            // Cut short is still as far as it got.
            foreach (Playing stopped in _playing.Where(p => Drives(p, target) && !p.Reverts))
            {
                Adopt(stopped);
            }

            _playing.RemoveAll(p => Drives(p, target));

            // The move flag is carried but not yet spent.
            if (!fromBehaviour)
            {
                _walking.Remove(clip.ModelName);
                _walking.Remove(target.Name);
            }

            // Whatever the model does on its own stops here, and does not start again by itself.
            if (!fromBehaviour)
            {
                _held.Add(target.Name);
                Quieten(target);
            }

            // And whose space it is played in, before the clip is handed anything about where the model stands: binding it moves the model, and what.
            PlacedModel? carrier = action.Placement is null && holder is not null && !ReferenceEquals(holder, target) ? holder : null;

            if (carrier is not null)
            {
                _carried[target.Name] = (target, carrier);
                Carry(target, carrier);
            }
            else
            {
                // Starting a clip that names no holder is the original assigning fresh parameters over the old ones, and the parent goes with them.
                _carried.Remove(target.Name);
            }

            var started = new Playing( clip, target, action, repeat, moves, Where(target.Name),
                _geometry.TransformOf(target.Placement), fromBehaviour, animation.Rate, Characters?.Of(target.Name), carrier is not null);

            Trace( "plays", clip.Name, target, (started.Absolute ? "absolute" : "relative") + (started.Reverts ? ", reverts" : ", keeps the ground") +
                (fromBehaviour ? ", from its own script" : string.Empty));

            _playing.Add(started);
            longest = Math.Max( longest, ((double)clip.FrameCount + action.Frame) / Math.Max(1, animation.Rate));
        }

        return longest;
    }

    /// <summary>What plays a sound an animation asks for, or null when there is no device.</summary>
    public Func<AnimationSound, Vector3?, bool>? Sound { get; set; }

    /// <summary>What speaks a line an animation asks for, or null when there is no device.</summary>
    public Action<AnimationDialogue>? Line { get; set; }

    /// <summary>What puts the view on a camera an animation names, or null in a tool.</summary>
    public Action<AnimationShot>? Shot { get; set; }

    /// <summary>What puts a mood or an expression on a face an animation names.</summary>
    public Action<AnimationMood>? Mood { get; set; }

    /// <summary>What starts and stops the soundtracks an animation names.</summary>
    public Action<AnimationMusic>? Music { get; set; }

    /// <summary>Speaks the lines that are due.</summary>
    private void Say(IEnumerable<AnimationDialogue> due)
    {
        if (Line is null)
        {
            return;
        }

        foreach (AnimationDialogue spoken in due)
        {
            Line(spoken);
        }
    }

    /// <summary>Cuts to the cameras that are due.</summary>
    private void Film(IEnumerable<AnimationShot> due)
    {
        if (Shot is null)
        {
            return;
        }

        foreach (AnimationShot shot in due)
        {
            Shot(shot);
        }
    }

    /// <summary>Puts on the moods and expressions that are due.</summary>
    private void Wear(IEnumerable<AnimationMood> due)
    {
        if (Mood is null)
        {
            return;
        }

        foreach (AnimationMood mood in due)
        {
            Mood(mood);
        }
    }

    /// <summary>Starts and stops the soundtracks that are due.</summary>
    private void Score(IEnumerable<AnimationMusic> due)
    {
        if (Music is null)
        {
            return;
        }

        foreach (AnimationMusic change in due)
        {
            Music(change);
        }
    }

    /// <summary>Advances a schedule and says what it has reached, oldest frame first.</summary>
    /// <param name="schedule">The things waiting for their frame.</param>
    /// <param name="seconds">How long since the last frame.</param>
    /// <param name="frame">Which frame one of them is authored on.</param>
    /// <typeparam name="T">What is due.</typeparam>
    private static List<T> Due<T>( List<Scheduled<T>> schedule, double seconds, Func<T, int> frame) where T : struct
    {
        if (schedule.Count == 0)
        {
            return [];
        }

        List<T> due = [];

        foreach (Scheduled<T> waiting in schedule)
        {
            if (waiting.Step(seconds) is { } what)
            {
                due.Add(what);
            }
        }

        schedule.RemoveAll(s => s.Finished);

        return due.Count > 1 ? [.. due.OrderBy(frame)] : due;
    }

    /// <summary>Starts every behaviour script the scene named.</summary>
    public void StartScenery()
    {
        _scenery.Clear();
        _fidgets.Clear();

        foreach (PlacedModel model in _scene.Models)
        {
            if (model.Kind == PlacedModelKind.Actor)
            {
                if (model.Idle is not null || model.Talk is not null || model.Listen is not null)
                {
                    _fidgets[model.Name] = new Fidget(model);
                }

                continue;
            }

            if (model.Idle is { Steps.Count: > 0 } script)
            {
                _scenery.Add(new Behaviour(script, model));
            }
        }
    }

    /// <summary>Puts everything the scene declared an opening pose for into it.</summary>
    /// <returns>How many were posed.</returns>
    public int Open()
    {
        if (Clips is null || Animations is null)
        {
            return 0;
        }

        _posed.Clear();
        int posed = 0;

        foreach (PlacedModel model in _scene.Models)
        {
            if (model.InitialAnimation is not { Length: > 0 } name || !model.Placement.Exists || Animations.Read(name) is not { } animation)
            {
                continue;
            }

            // A performance is not a pose, and an actor the scene has already stood somewhere does not need one.
            if (model.Kind == PlacedModelKind.Actor && model.Spotted && animation.IsPerformance)
            {
                continue;
            }

            // Whatever the pose says about what is drawn, before the pose itself — but only about this model, for the same reason the clips below.
            Reveal(animation.Visibility .Where(v => v.Frame <= 0 && Names(model, v.Model)));

            foreach (AnimationAction action in animation.Actions)
            {
                if (Clips.Read(action.Name) is not { } clip || !_models.TryGetValue(clip.ModelName, out PlacedModel? target))
                {
                    continue;
                }

                // Only the clip belonging to the model that declared the pose.
                if (!ReferenceEquals(target, model))
                {
                    continue;
                }

                // Not turned to what the clip's opening frame faces.
                var pose = new Playing( clip, target, action with { Frame = 0 }, repeat: false, moves: true, Where(target.Name),
                    _geometry.TransformOf(target.Placement), character: Characters?.Of(target.Name));

                pose.Open(_geometry);

                // Where the pose leaves them is where they now are.
                if (target.Kind == PlacedModelKind.Actor)
                {
                    Vector3 settled = pose.Settled(_geometry.TransformOf(target.Placement));

                    // What the clip says the character is facing, beside what the scene file said.
                    float? wanted = Clips is { } clips && Characters?.Of(target.Name) is { } who ? Actors.AnimationStart.Of(
                                animation, clips, target.Name, who, target.BuiltFacing)?.Heading : null;

                    // And where it leaves them has to become their placement, not only a note of where they are.
                    if (!target.Spotted)
                    {
                        Reseat( target, settled, wanted ?? Navigation.Walker.HeadingOf( _geometry.TransformOf(target.Placement)));

                        // Sampled again, against the placement they now have.
                        pose = new Playing( clip, target, action with { Frame = 0 }, repeat: false, moves: true, Where(target.Name),
                            _geometry.TransformOf(target.Placement), character: Characters?.Of(target.Name));

                        pose.Open(_geometry);
                        settled = pose.Settled(_geometry.TransformOf(target.Placement));
                    }

                    Follow(target.Name, settled);

                    _posed.Add(( target.Noun ?? target.Name, settled, Navigation.Walker.HeadingOf(_geometry.TransformOf(target.Placement)), wanted));
                }

                posed++;
            }
        }

        return posed;
    }

    /// <summary>Who an opening pose moved, where to, and which way they ended up facing.</summary>
    public IReadOnlyList<(string Who, Vector3 Where, float Placed, float? Wanted)> Posed => _posed;

    private readonly List<(string Who, Vector3 Where, float Placed, float? Wanted)> _posed = [];

    /// <summary>Gives a character a different stride.</summary>
    /// <returns>True when the room has such a character.</returns>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="start">The animation that gets them moving.</param>
    /// <param name="loop">The stride itself, looped while they walk.</param>
    public bool SetStride(string actor, string start, string loop)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (ModelNamed(actor) is not { Kind: PlacedModelKind.Actor } model)
        {
            return false;
        }

        _strides[model.Name] = (start, loop);
        return true;
    }

    /// <summary>What a character walks with now, when a script has changed it.</summary>
    private readonly Dictionary<string, (string Start, string Loop)> _strides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts a prop's own script again.</summary>
    /// <returns>True when the room has such a prop with a script.</returns>
    /// <param name="model">Its model name or noun.</param>
    public bool StartScenery(string model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (ModelNamed(model) is not { Idle: { Steps.Count: > 0 } script } prop)
        {
            return false;
        }

        StopScenery(model);
        _scenery.Add(new Behaviour(script, prop));

        return true;
    }

    /// <summary>Stops a prop's own script.</summary>
    /// <returns>True when one was running.</returns>
    /// <param name="model">Its model name or noun.</param>
    public bool StopScenery(string model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (ModelNamed(model) is not { } prop)
        {
            return false;
        }

        return _scenery.RemoveAll(b => ReferenceEquals(b.Owner, prop)) > 0;
    }

    /// <summary>Gives a character a different script for one of the three things they do.</summary>
    /// <returns>True when the room has such a character.</returns>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="mode">Which of the three.</param>
    /// <param name="script">The script, or null to leave them with nothing to do.</param>
    public bool SetBehaviour(string actor, FidgetKind mode, Formats.Animation.GasFile? script)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (ModelNamed(actor) is not { Kind: PlacedModelKind.Actor } model)
        {
            return false;
        }

        switch (mode)
        {
            case FidgetKind.Talk:
                model.Talk = script;
                break;

            case FidgetKind.Listen:
                model.Listen = script;
                break;

            default:
                model.Idle = script;
                break;
        }

        // Started rather than merely stored: a script that hands somebody a new idle means it to take effect, and the one they were running belongs.
        _fidgets[model.Name] = new Fidget(model);
        return true;
    }

    /// <summary>Stops one model's behaviour script, because the story has taken it over.</summary>
    /// <param name="model">The model.</param>
    private void Quieten(PlacedModel model)
    {
        if (_fidgets.TryGetValue(model.Name, out Fidget? fidget))
        {
            Tidy(fidget);
            fidget.Stopped = true;
        }
    }

    /// <summary>Stops a character fidgeting, or everybody.</summary>
    /// <param name="actor">Their name, or null for everyone in the room.</param>
    public void StopFidget(string? actor = null)
    {
        if (actor is not { Length: > 0 })
        {
            foreach (Fidget fidget in _fidgets.Values)
            {
                Tidy(fidget);
                fidget.Stopped = true;
            }

            return;
        }

        if (ModelNamed(actor) is { } model && _fidgets.TryGetValue(model.Name, out Fidget? one))
        {
            Tidy(one);
            one.Stopped = true;
        }
    }

    /// <summary>Puts a prop back where it lives, when the idle that was moving it is cut short.</summary>
    /// <param name="running">The clip being stopped.</param>
    private void Rest(Playing running)
    {
        if (!running.FromBehaviour || running.Target.Kind != PlacedModelKind.Prop)
        {
            return;
        }

        IReadOnlyList<Formats.Models.ModMesh> meshes = running.Target.Model.Meshes;

        for (int mesh = 0; mesh < meshes.Count; mesh++)
        {
            _geometry.PoseMesh(running.Target.Placement, mesh, meshes[mesh].MeshToLocal);
            running.Target.Pose(mesh, meshes[mesh].MeshToLocal);
        }
    }

    /// <summary>Puts right whatever a behaviour script was in the middle of.</summary>
    /// <returns>True when there was something to put right.</returns>
    /// <param name="fidget">The character's scripts.</param>
    private bool Tidy(Fidget fidget)
    {
        if (fidget.Stopped || fidget.Running is not { } running)
        {
            return false;
        }

        bool did = false;

        for (int guard = 0; guard < 8; guard++)
        {
            if (running.Playing is not { Length: > 0 } was || running.Script.CleanupFor(was) is not { Length: > 0 } tidied)
            {
                break;
            }

            Play(tidied, fromBehaviour: true);
            running.Playing = tidied;
            did = true;
        }

        return did;
    }

    /// <summary>Sets a character fidgeting again.</summary>
    /// <param name="actor">Their name.</param>
    /// <param name="mode">Which of the three to run.</param>
    public void StartFidget(string actor, FidgetKind mode)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (ModelNamed(actor) is not { Kind: PlacedModelKind.Actor } model)
        {
            return;
        }

        Fidget fidget = _fidgets.TryGetValue(model.Name, out Fidget? known) ? known : _fidgets[model.Name] = new Fidget(model);

        fidget.Stopped = false;
        fidget.Forced = mode;
        fidget.Enter(mode, model);
    }

    /// <summary>Told who is speaking, so that talking and listening can be told apart.</summary>
    public Func<string?>? Speaking { get; set; }

    /// <summary>One step of every behaviour script that is running.</summary>
    private void StepBehaviours(double seconds)
    {
        foreach (Behaviour running in _scenery)
        {
            if (running.Owner is { } driven && _held.Contains(driven.Name))
            {
                continue;
            }

            Step(running, seconds);
        }

        string? speaker = Speaking?.Invoke();

        foreach (Fidget fidget in _fidgets.Values)
        {
            // Told to stand still, standing still because the story is animating them, or busy walking.
            if (fidget.Stopped || _held.Contains(fidget.Model.Name) || Crossing(fidget.Model))
            {
                continue;
            }

            // Who is talking decides which of the three scripts a character runs.
            FidgetKind wanted = fidget.Forced ?? (speaker is null ? FidgetKind.Idle
                : Same(fidget.Model, speaker) ? FidgetKind.Talk : FidgetKind.Listen);

            if (wanted != fidget.Mode)
            {
                // Out of whatever the last one left them holding, before the next begins.
                Tidy(fidget);
                fidget.Enter(wanted, fidget.Model);
            }

            if (fidget.Running is { } behaviour)
            {
                Step(behaviour, seconds);
            }
        }
    }

    /// <summary>Which conversation is being held, or null between them.</summary>
    public string? Conversation { get; private set; }

    /// <summary>What each actor's own scripts were before a conversation replaced them.</summary>
    private readonly Dictionary<string, (Formats.Animation.GasFile? Talk, Formats.Animation.GasFile? Listen)> _lent =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hands the actors in a conversation the scripts written for that conversation.</summary>
    /// <returns>How long the actors take to get into it, in seconds.</returns>
    /// <param name="name">The conversation, as SetConversation names it.</param>
    public double EnterConversation(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // One at a time.
        LeaveConversation();

        Conversation = name;

        double longest = 0;

        foreach (SceneConversation setting in _scene.Definition.Conversations())
        {
            if (!setting.Conversation.Equals(name, StringComparison.OrdinalIgnoreCase) || ModelNamed(setting.Actor) is not { } model)
            {
                continue;
            }

            _lent[model.Name] = (model.Talk, model.Listen);

            if (setting.Talk is { Length: > 0 } talk)
            {
                model.Talk = Behaviours?.Invoke(talk) ?? model.Talk;
            }

            if (setting.Listen is { Length: > 0 } listen)
            {
                model.Listen = Behaviours?.Invoke(listen) ?? model.Listen;
            }

            if (setting.Enter is { Length: > 0 } entering)
            {
                longest = Math.Max(longest, Play(entering));
            }

            Restart(model);
        }

        return longest;
    }

    /// <summary>Gives the actors their own scripts back, and undoes the poses.</summary>
    /// <returns>How long the actors take to come out of it, in seconds.</returns>
    public double LeaveConversation()
    {
        if (Conversation is not { Length: > 0 } name)
        {
            return 0;
        }

        double longest = 0;

        foreach (SceneConversation setting in _scene.Definition.Conversations())
        {
            if (!setting.Conversation.Equals(name, StringComparison.OrdinalIgnoreCase) || ModelNamed(setting.Actor) is not { } model)
            {
                continue;
            }

            if (setting.Exit is { Length: > 0 } leaving)
            {
                longest = Math.Max(longest, Play(leaving));
            }

            if (_lent.Remove(model.Name, out (Formats.Animation.GasFile? Talk, Formats.Animation.GasFile? Listen) theirs))
            {
                model.Talk = theirs.Talk;
                model.Listen = theirs.Listen;
            }

            Restart(model);
        }

        _lent.Clear();
        Conversation = null;

        return longest;
    }

    /// <summary>Starts an actor's fidget again, so a replaced script takes effect.</summary>
    private void Restart(PlacedModel model)
    {
        if (_fidgets.TryGetValue(model.Name, out Fidget? fidget) && fidget.Mode is { } mode)
        {
            fidget.Enter(mode, model);
        }
    }

    /// <summary>Makes the noise a foot landing makes.</summary>
    /// <param name="fell">Whose foot, and whether it was dragged.</param>
    private void Tread(AnimationStep fell)
    {
        if (Sound is null || Steps is null || ModelNamed(fell.Actor) is not { } who)
        {
            return;
        }

        if (Characters?.Of(who.Name)?.ShoeType is not { Length: > 0 } shoes)
        {
            return;
        }

        Vector3 at = Where(who.Name) ?? who.Standing.Translation;

        if (Steps.Sounds(shoes, _scene.Ground?.Surface(at), fell.Scuff) is not { Count: > 0 } choices)
        {
            return;
        }

        string heard = choices[_chance.NextInt32(0, choices.Count)];

        if (!Sound(new AnimationSound(0, heard, 100, who.Name), at))
        {
            Diagnostics.Add(new Diagnostic( "GK3R3343", DiagnosticSeverity.Info, "A footstep names a sound the archives do not have.",
                heard, null, "a .WAV of that name", heard, "The step is silent; the walk is unaffected."));
        }
    }

    /// <summary>Whether a name is one of a model's own.</summary>
    private static bool Names(PlacedModel model, string name) => model.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (model.Noun is { Length: > 0 } noun && noun.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies an animation's texture swaps.</summary>
    /// <param name="swaps">The changes due now.</param>
    private void Repaint(IEnumerable<AnimationTexture> swaps)
    {
        foreach (AnimationTexture swap in swaps)
        {
            if (ModelNamed(swap.Model) is not { } model || swap.Mesh < 0 || swap.Mesh >= model.Model.Meshes.Count)
            {
                continue;
            }

            IReadOnlyList<Formats.Models.ModSubmesh> parts = model.Model.Meshes[swap.Mesh].Submeshes;

            if (swap.Submesh < 0 || swap.Submesh >= parts.Count)
            {
                continue;
            }

            if (Textures?.Invoke(swap.Texture) == false)
            {
                Diagnostics.Add(new Diagnostic( "GK3R3344", DiagnosticSeverity.Info,
                    "An animation repaints a surface with a texture the archives do not have.", swap.Model, null, "a .BMP of that name", swap.Texture,
                    "The surface keeps the picture it had."));

                continue;
            }

            _geometry.Repaint(model.Placement, parts[swap.Submesh].TextureName, swap.Texture);
        }
    }

    /// <summary>Applies an animation's repaints of the room itself.</summary>
    /// <param name="swaps">The changes due now.</param>
    private void PaintRoom(IEnumerable<AnimationSceneTexture> swaps)
    {
        foreach (AnimationSceneTexture swap in swaps)
        {
            if (Textures?.Invoke(swap.Texture) == false)
            {
                Diagnostics.Add(new Diagnostic( "GK3R3345", DiagnosticSeverity.Info,
                    "An animation repaints part of a room with a texture the archives do not have.",
                    swap.ObjectName, null, "a .BMP of that name", swap.Texture, "The surface keeps the picture it had."));

                continue;
            }

            if (!_geometry.PaintSceneObject(swap.ObjectName, swap.Texture))
            {
                Diagnostics.Add(new Diagnostic( "GK3R3346", DiagnosticSeverity.Info, "An animation repaints part of a room that has no such part.",
                    _scene.Name, null, "an object in the geometry", swap.ObjectName,
                    "Common and usually harmless: animations are shared between rooms."));
            }
        }
    }

    /// <summary>Applies an animation's visibility changes to the room itself.</summary>
    /// <param name="changes">The changes due now.</param>
    private void RevealRoom(IEnumerable<AnimationSceneVisibility> changes)
    {
        foreach (AnimationSceneVisibility change in changes)
        {
            ShowObject(change.ObjectName, change.Visible);
        }
    }

    /// <summary>Makes a texture resident, and says whether it could be.</summary>
    public Func<string, bool>? Textures { get; set; }

    /// <summary>Gives the room a different bake of its own lighting, and says whether it could be.</summary>
    public Func<string, bool>? Relight { get; set; }

    /// <summary>Hands the room a second bake of its lighting, by scene-asset name.</summary>
    /// <returns>True when the room is now lit by it.</returns>
    /// <param name="asset">The scene asset — rl2_disco_a, gri_b.</param>
    public bool Relit(string asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (Relight?.Invoke(asset) == true)
        {
            return true;
        }

        Diagnostics.Add(new Diagnostic( "GK3R3347", DiagnosticSeverity.Info, "A script asked for a different bake of the room and did not get one.",
            _scene.Name, null, "a scene asset baked for this geometry", asset, "The room keeps the lighting it had."));

        return false;
    }

    /// <summary>Whether a model is walking somewhere, under either of its names.</summary>
    private bool Crossing(PlacedModel model) => _walking.ContainsKey(model.Name) ||
        (model.Noun is { Length: > 0 } noun && _walking.ContainsKey(noun));

    /// <summary>Whether a name is one of a model's two.</summary>
    private static bool Same(PlacedModel model, string name) => model.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (model.Noun is { Length: > 0 } noun && noun.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs one behaviour script forward by however much time has passed.</summary>
    private void Step(Behaviour running, double seconds)
    {
        Notice(running);

        running.Remaining -= seconds;

        for (int guard = 0; guard < 16 && running.Remaining <= 0; guard++)
        {
            if (running.Position >= running.Script.Steps.Count)
            {
                running.Position = 0;

                // A script with no loop and nothing left to do simply stops.
                if (!running.Repeats)
                {
                    running.Remaining = double.MaxValue;
                    break;
                }
            }

            GasStep step = running.Script.Steps[running.Position++];

            switch (step.Action)
            {
                // A script that is one animation and a jump back to it is a thing that simply turns: a fan, a fountain, a clock.
                case GasAction.Animate when step.Name is { Length: > 0 } spun && running.Script.Continuous:
                    Play(spun, repeat: true, fromBehaviour: true);
                    running.Playing = spun;
                    running.Remaining = double.MaxValue;
                    break;

                case GasAction.Animate when step.Name is { Length: > 0 } clip:
                    if (Draws(step.Chance))
                    {
                        running.Remaining += Math.Max( Play(clip, step.Repeats, fromBehaviour: true), 1.0 / 60);

                        running.Playing = clip;
                    }

                    break;

                // A run of these is one choice, not several.
                case GasAction.OneOf:
                    running.Position--;
                    Choose(running);
                    break;

                case GasAction.Wait when Draws(step.Chance):
                    running.Remaining += step.To > step.Seconds ? step.Seconds + (_chance.NextDouble() * (step.To - step.Seconds)) : step.Seconds;

                    break;

                case GasAction.Goto when step.Name is { Length: > 0 } label:
                    running.Position = running.Script.LabelAt(label) ?? 0;
                    break;

                case GasAction.Loop:
                    running.Position = 0;
                    break;

                case GasAction.Set when step.Name is { Length: > 0 } register:
                    running.Registers[register] = step.Value;
                    break;

                case GasAction.Increment when step.Name is { Length: > 0 } counted:
                    running.Registers[counted] = running.Registers.GetValueOrDefault(counted) + 1;

                    break;

                case GasAction.If when step.Name is { Length: > 0 } tested && step.Other is { Length: > 0 } target:
                    if (Holds(running.Registers.GetValueOrDefault(tested), step))
                    {
                        running.Position = running.Script.LabelAt(target) ?? running.Position;
                    }

                    break;

                // A character handing themselves a different idle.
                case GasAction.NewIdle when step.Name is { Length: > 0 } named:
                    Rescript(running, named);
                    break;

                // Walking and looking go through the room's own hooks rather than being done again here: the route, the stride and the head-turn.
                case GasAction.WalkTo when step.Name is { Length: > 0 } spot && running.Owner is { } walker:
                    running.Remaining += Send(walker.Name, spot);
                    break;

                case GasAction.ChooseWalk when step.Names is { Count: > 0 } spots && running.Owner is { } wanderer:
                    running.Remaining += Send( wanderer.Name, spots[_chance.NextInt32(0, spots.Count)]);

                    break;

                case GasAction.LookAt when step.Name is { Length: > 0 } at && running.Owner is { } looker:
                    _api.Invoke( "LookitModel",
                        [
                            Sheep.SheepValue.FromString(looker.Name), Sheep.SheepValue.FromString(at),
                            Sheep.SheepValue.FromFloat((float)step.Seconds), ]);

                    break;

                // Where the character now is.
                case GasAction.AtLocation when step.Name is { Length: > 0 } moved && running.Owner is { } who:
                    _api.State.SetActorLocation(who.Noun ?? who.Name, moved);
                    break;

                // A line, said by whoever the script drives.
                case GasAction.Speak when step.Name is { Length: > 0 } plate:
                    running.Remaining += Say(plate);
                    break;

                // An expression worn until something takes it off.
                case GasAction.SetMood when step.Name is { Length: > 0 } mood && running.Owner is { } wearer:
                    _api.Invoke( "SetMood",
                        [
                            Sheep.SheepValue.FromString(wearer.Noun ?? wearer.Name), Sheep.SheepValue.FromString(mood), ]);

                    break;

                // Back to where the scene put them.
                case GasAction.ResetPosition when running.Owner is { } strayed:
                    Restore(strayed);
                    break;

                // Everything else is parsed and not run: labels and declarations, which are read where they are needed rather than stepped through.
                default:
                    break;
            }
        }
    }

    /// <summary>Lets a behaviour notice somebody coming near, or leaving.</summary>
    /// <param name="running">The script.</param>
    private void Notice(Behaviour running)
    {
        IReadOnlyList<Formats.Animation.GasStep> steps = running.Script.Steps;

        for (int index = 0; index < steps.Count; index++)
        {
            Formats.Animation.GasStep step = steps[index];

            bool watching = step.Action is GasAction.WhenNear or GasAction.WhenNoLongerNear;
            bool seeing = step.Action == GasAction.WhenInView;

            if ((!watching && !seeing) || step.Name is not { Length: > 0 } noun || step.Other is not { Length: > 0 } label ||
                running.Owner is not { } owner)
            {
                continue;
            }

            // From this script's own actor, unless the condition names somebody else to measure from — Estelle's whisper idle watches Gabriel.
            string from = step.Between is { Length: > 0 } other ? other : owner.Name;

            bool met;

            if (seeing)
            {
                met = Sees(owner.Name, noun, step.Value);
            }
            else
            {
                bool near = Where(from) is { } here && Where(noun) is { } them && Flat(here - them) < step.Value * (float)step.Value;

                met = step.Action == GasAction.WhenNear ? near : !near;
            }

            bool was = running.Noticed.Contains(index);

            if (met && !was)
            {
                running.Noticed.Add(index);

                // The chance is spent when the condition fires rather than tested every frame, which is the difference between "sometimes notices".
                if (Draws(step.Chance) && running.Script.LabelAt(label) is { } at)
                {
                    running.Position = at;
                    running.Remaining = 0;
                }
            }
            else if (!met && was)
            {
                running.Noticed.Remove(index);
            }
        }
    }

    /// <summary>Whether one actor has another in front of them.</summary>
    /// <returns>True when the second is inside the first's field of view.</returns>
    /// <param name="looker">Whose sight, by either of their names.</param>
    /// <param name="seen">Who they might see.</param>
    /// <param name="degrees">How wide their sight is, in degrees, as the script states it.</param>
    private bool Sees(string looker, string seen, int degrees)
    {
        if (degrees <= 0 || Where(looker) is not { } here || Where(seen) is not { } them || Looking(looker) is not { } gaze)
        {
            return false;
        }

        var ahead = new Vector2(gaze.X, gaze.Z);
        var towards = new Vector2(them.X - here.X, them.Z - here.Z);

        if (ahead.LengthSquared() <= 0 || towards.LengthSquared() <= 0)
        {
            return false;
        }

        float cosine = Vector2.Dot( Vector2.Normalize(ahead), Vector2.Normalize(towards));

        return cosine >= MathF.Cos(float.DegreesToRadians(degrees) / 2f);
    }

    /// <summary>Says one line, and answers how long it lasts.</summary>
    /// <returns>Seconds the line takes, or zero when nothing can play it.</returns>
    /// <param name="plate">The licence plate the line is filed under.</param>
    private double Say(string plate)
    {
        Sheep.SheepValue[] arguments =
        [
            Sheep.SheepValue.FromString(plate), Sheep.SheepValue.FromInt(1), ];

        _api.Invoke("StartVoiceOver", arguments);

        return _api.SecondsFor("StartVoiceOver", arguments);
    }

    /// <summary>Puts an actor back where the scene placed them.</summary>
    /// <param name="actor">The model to move.</param>
    private void Restore(PlacedModel actor)
    {
        _walking.Remove(actor.Name);

        if (actor.Noun is { Length: > 0 } noun)
        {
            _walking.Remove(noun);
        }

        StopAnimating(actor.Name);

        _geometry.MoveModel(actor.Placement, actor.Transform);

        Follow(actor.Name, actor.Transform.Translation);
    }

    /// <summary>A distance squared, measured across the ground plan.</summary>
    private static float Flat(Vector3 apart) => (apart.X * apart.X) + (apart.Z * apart.Z);

    /// <summary>Takes one of the choices in the run starting where the script is.</summary>
    private void Choose(Behaviour running)
    {
        int first = running.Position;
        int last = first;
        int total = 0;

        while (last < running.Script.Steps.Count && running.Script.Steps[last].Action == GasAction.OneOf)
        {
            total += Math.Max(1, running.Script.Steps[last].Weight);
            last++;
        }

        running.Position = last;

        int draw = _chance.NextInt32(0, Math.Max(1, total));

        for (int i = first; i < last; i++)
        {
            draw -= Math.Max(1, running.Script.Steps[i].Weight);

            if (draw < 0 && running.Script.Steps[i].Name is { Length: > 0 } chosen)
            {
                running.Remaining += Math.Max( Play(chosen, fromBehaviour: true), 1.0 / 60);
                return;
            }
        }
    }

    /// <summary>Puts a different script in a running behaviour's place.</summary>
    private void Rescript(Behaviour running, string named)
    {
        if (Behaviours?.Invoke(named) is not { Steps.Count: > 0 } replacement)
        {
            return;
        }

        running.Script = replacement;
        running.Position = 0;
        running.Registers.Clear();

        if (running.Owner is { Kind: PlacedModelKind.Actor } actor)
        {
            actor.Idle = replacement;
        }
    }

    /// <summary>Where a behaviour script named by another one is read from.</summary>
    public Func<string, Formats.Animation.GasFile?>? Behaviours { get; set; }

    /// <summary>Sends somebody to a named spot, and says how long it takes.</summary>
    private double Send(string actor, string spot) => _api.Walks?.Invoke(actor, spot, Approaching.Walk, false, false) ?? 0;

    /// <summary>Whether something with a percentage chance happens this time.</summary>
    private bool Draws(int chance) => chance is <= 0 or >= 100 || _chance.NextInt32(0, 100) < chance;

    /// <summary>Whether a register compares as the instruction says.</summary>
    private static bool Holds(int value, GasStep step) => step.Comparison switch
    {
        "=" or "==" => value == step.Value, "!=" or "<>" => value != step.Value, ">" => value > step.Value, "<" => value < step.Value,
        ">=" => value >= step.Value, "<=" => value <= step.Value, _ => false,
    };

    /// <summary>How many scenery scripts are running.</summary>
    public int Scenic => _scenery.Count;

    /// <summary>How many characters have something to do when nobody is asking.</summary>
    public int Fidgeting => _fidgets.Count;

    /// <summary>The code this room needs that its data cannot express, where it declares any.</summary>
    public Mechanisms.SceneMechanism? Mechanism { get; set; }

    /// <summary>Stands an actor somewhere without dropping them onto the floor.</summary>
    /// <returns>True when there was somebody of that name to move.</returns>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="position">Where to stand them, in world space.</param>
    /// <param name="heading">Which way to face, as the game's data measures a heading.</param>
    public bool Carry(string actor, Vector3 position, float heading)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            return false;
        }

        _geometry.MoveModel(placed.Placement, Standing(placed, position, heading));
        Record(placed, position);

        return true;
    }

    /// <summary>Makes a model its own light source, or stops.</summary>
    /// <param name="model">The model.</param>
    /// <param name="selfLit">Whether it is drawn at full brightness and never shaded.</param>
    public void SelfLit(PlacedModel model, bool selfLit)
    {
        ArgumentNullException.ThrowIfNull(model);

        _geometry.SetSelfLit(model.Placement, selfLit);
    }

    /// <summary>One of the spots the scene marks out, by name.</summary>
    /// <returns>The spot, or null when the room has no such name.</returns>
    /// <param name="name">What the scene file calls it.</param>
    public Formats.Scenes.ScenePosition? PositionNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _scene.Definition.PositionNamed(name);
    }

    /// <summary>The middle of anything the room names, prop or geometry.</summary>
    /// <returns>The centre of its box in world space, or null when there is no such thing.</returns>
    /// <param name="objectName">What it is called.</param>
    public Vector3? MiddleOf(string objectName)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        return SceneScripting.Bounds(_scene, objectName) is var (low, high) ? (low + high) * 0.5f : null;
    }

    /// <summary>Repaints one of the room's own surfaces.</summary>
    /// <returns>True when the room has such an object and the picture was found.</returns>
    /// <param name="objectName">The object in the geometry.</param>
    /// <param name="texture">What to paint it with; null puts its own picture back.</param>
    public bool PaintObject(string objectName, string? texture)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        if (texture is { Length: > 0 } picture && Textures?.Invoke(picture) == false)
        {
            return false;
        }

        return _geometry.PaintSceneObject(objectName, texture);
    }

    /// <summary>Where actors may stand, when the room declares a boundary.</summary>
    public Navigation.WalkBoundary? Boundary => _scene.Walkable;

    /// <summary>Poses named models on an animation's last frame, without running it.</summary>
    /// <returns>How many were posed.</returns>
    /// <param name="animation">What the animation is called.</param>
    /// <param name="models">Which of its models to pose; others in it are left alone.</param>
    /// <param name="atEnd">Whether to take the closing frame rather than the opening one.</param>
    public int Pose(string animation, IReadOnlyCollection<string> models, bool atEnd = true)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(models);

        if (Clips is null || Animations is null || Animations.Read(animation) is not { } read)
        {
            return 0;
        }

        int posed = 0;

        foreach (AnimationAction action in read.Actions)
        {
            if (Clips.Read(action.Name) is not { } clip || !models.Contains(clip.ModelName, StringComparer.OrdinalIgnoreCase) ||
                !_models.TryGetValue(clip.ModelName, out PlacedModel? target))
            {
                continue;
            }

            var pose = new Playing( clip, target, action with { Frame = 0 }, repeat: false, moves: true, Where(target.Name),
                _geometry.TransformOf(target.Placement), character: Characters?.Of(target.Name));

            if (atEnd)
            {
                pose.Last(_geometry);
            }
            else
            {
                pose.Open(_geometry);
            }

            posed++;
        }

        return posed;
    }

    /// <summary>Where the room was put, for a mechanism that moves its own props.</summary>
    public ISceneSink Geometry => _geometry;

    /// <summary>Finds a model the room places, by either of its names.</summary>
    /// <returns>The model, or null when the room has nothing by that name.</returns>
    /// <param name="name">Its model name or the noun the scene gives it.</param>
    public PlacedModel? ModelNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_models.TryGetValue(name, out PlacedModel? model))
        {
            return model;
        }

        foreach (PlacedModel placed in _scene.Models)
        {
            if (placed.Placement.Exists && placed.Noun is { Length: > 0 } noun && noun.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return placed;
            }
        }

        return null;
    }

    /// <summary>Draws a model, or stops drawing it.</summary>
    /// <param name="model">The model.</param>
    /// <param name="visible">Whether it is drawn.</param>
    public void Show(PlacedModel model, bool visible)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Visible = visible;
        _geometry.SetVisible(model.Placement, visible);
    }

    /// <summary>Keeps a body out of the eyes of whoever is standing in it, all of it or all but the arms.</summary>
    /// <param name="model">The model.</param>
    /// <param name="standingIn">Whether the player is behind this body's eyes.</param>
    /// <param name="keepArms">Whether their own arms are left in their view.</param>
    public void Embody(PlacedModel model, bool standingIn, bool keepArms)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Not something they can click: a body drawn at the camera puts its own shirt across the crosshair, and the picker asks this. It is still
        // drawn, still traced and still in the mirror — being stood in is not the same as being taken out of the room.
        model.Visible = !standingIn;

        if (!standingIn)
        {
            _geometry.SetUnseenBySelf(model.Placement, []);

            return;
        }

        int[] arms = keepArms ? ArmsOf(model) : [];
        List<int> unseen = [];

        for (int mesh = 0; mesh < model.Model.Meshes.Count; mesh++)
        {
            if (Array.IndexOf(arms, mesh) < 0)
            {
                unseen.Add(mesh);
            }
        }

        _geometry.SetUnseenBySelf(model.Placement, unseen);
    }

    /// <summary>Which of a model's mesh groups are its arms.</summary>
    private int[] ArmsOf(PlacedModel model)
    {
        if (!_arms.TryGetValue(model.Model.Name, out int[]? found))
        {
            // Without the shoulders: what is kept is the arm from the elbow down, because an eye of their own is inside the shoulder. See
            // CharacterArms.Shoulders.
            IReadOnlyList<int> shoulders = Actors.CharacterArms.Shoulders(model.Model);

            found = [.. Actors.CharacterArms.Find(model.Model).Where(mesh => !shoulders.Contains(mesh))];
            _arms[model.Model.Name] = found;
        }

        return found;
    }

    /// <summary>Which of a model's mesh groups is its head.</summary>
    private int? HeadOf(PlacedModel model)
    {
        if (model.Head is { } rig)
        {
            return rig.Mesh;
        }

        // A head is only refined when the setting asks for it, and the head is wanted either way.
        if (!_heads.TryGetValue(model.Model.Name, out int? found))
        {
            found = Actors.CharacterHead.Find(model.Model);
            _heads[model.Model.Name] = found;
        }

        return found;
    }

    /// <summary>Applies an animation's visibility changes.</summary>
    /// <param name="changes">The changes due now.</param>
    private void Reveal(IEnumerable<AnimationVisibility> changes)
    {
        foreach (AnimationVisibility change in changes)
        {
            if (ModelNamed(change.Model) is not { } model)
            {
                continue;
            }

            if (change.Mesh >= 0 && change.Submesh >= 0)
            {
                _geometry.SetPartVisible( model.Placement, change.Mesh, change.Submesh, change.Visible);

                continue;
            }

            Show(model, change.Visible);
        }
    }

    /// <summary>Draws one of the room's own named objects, or stops drawing it.</summary>
    /// <returns>True when the room has an object by that name.</returns>
    /// <param name="objectName">The object's name, as the geometry records it.</param>
    /// <param name="visible">Whether it is drawn.</param>
    public bool ShowObject(string objectName, bool visible)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        if (!_geometry.SetSceneObjectVisible(objectName, visible))
        {
            return false;
        }

        if (IsHitTest(objectName))
        {
            return true;
        }

        if (visible)
        {
            _api.State.BlockedHitTests.Remove(objectName);
        }
        else
        {
            _api.State.BlockedHitTests.Add(objectName);
        }

        return true;
    }

    /// <summary>The room's hit tests by name, read once because the scene files are merged afresh on every call.</summary>
    private HashSet<string>? _hitTests;

    /// <summary>Whether the room declares this object as a hit test rather than geometry.</summary>
    private bool IsHitTest(string objectName)
    {
        _hitTests ??= _scene.Definition.Models() .Where(m => string.Equals(m.Type, "hittest", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name) .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _hitTests.Contains(objectName);
    }

    /// <summary>Whose space the clips of an animation are authored in, when it is not the room's.</summary>
    /// <returns>The model to play its other clips relative to, or null for the room.</returns>
    /// <param name="animation">The .ANM name, whose first three letters name them.</param>
    /// <param name="file">That animation, which has to agree that they are its subject.</param>
    private PlacedModel? Holder(string animation, AnimationFile file)
    {
        if (animation.Length < 3 || Clips is null || !_models.TryGetValue(animation[..3], out PlacedModel? owner))
        {
            return null;
        }

        foreach (AnimationAction action in file.Actions)
        {
            // An absolute clip says where in the room it happens, so it is nobody's passenger and it makes nobody else one either.
            if (action.Placement is null && Clips.Read(action.Name) is { } clip &&
                clip.ModelName.Equals(owner.Name, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    /// <summary>Puts a held model into the space of whoever is holding it.</summary>
    /// <param name="held">The prop, or occasionally the person, being carried.</param>
    /// <param name="holder">Whose space its clip is authored in.</param>
    private void Carry(PlacedModel held, PlacedModel holder)
    {
        if (!held.Placement.Exists || !holder.Placement.Exists)
        {
            return;
        }

        _geometry.MoveModel(held.Placement, ModelSpace(holder));
    }

    /// <summary>Where a model's own space sits in the room, with whatever is animating it.</summary>
    /// <returns>The transform a clip authored in that space is played through.</returns>
    /// <param name="model">The model whose space is wanted.</param>
    private Matrix4x4 ModelSpace(PlacedModel model)
    {
        Matrix4x4 standing = _geometry.TransformOf(model.Placement);

        if (_playing.Find(p => Drives(p, model)) is { } driving)
        {
            _space[model.Name] = driving.Space;

            return driving.Space * standing;
        }

        return _space.TryGetValue(model.Name, out Matrix4x4 last) ? last * standing : standing;
    }

    /// <summary>Whether a clip that is playing is the one animating a model.</summary>
    private static bool Drives(Playing playing, PlacedModel model) => ReferenceEquals(playing.Target, model) ||
        playing.Target.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops everything a model is doing.</summary>
    /// <param name="model">Its name, or null for everything in the room.</param>
    public void StopAnimating(string? model = null)
    {
        if (model is not { Length: > 0 })
        {
            foreach (Playing running in _playing)
            {
                Rest(running);
            }

            _playing.Clear();
            _held.Clear();
            _showings.Clear();
            _steps.Clear();
            _swaps.Clear();
            _roomSwaps.Clear();
            _roomShowings.Clear();
            _lines.Clear();
            _shots.Clear();
            _moods.Clear();
            _music.Clear();
            _cues.Clear();
            return;
        }

        foreach (Playing running in _playing.Where(p => p.Clip.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            Rest(running);

            // And an actor keeps whatever ground a move clip had covered by the time it was stopped, as it would have if the clip had been left to.
            if (!running.Reverts)
            {
                Adopt(running);
            }
        }

        _playing.RemoveAll(p => p.Clip.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase));

        // And the noises it was going to make.
        _cues.RemoveAll(c => c.Owner.Equals(model, StringComparison.OrdinalIgnoreCase) || c.Model.Equals(model, StringComparison.OrdinalIgnoreCase));

        // And whatever it was about to be shown or hidden by.
        _showings.RemoveAll(v => v.Concerns(model));

        // And whatever it was about to do to the room.
        _roomSwaps.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _roomShowings.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));

        // And whatever it was about to say, frame or put on a face, for the same reason and by the same name.
        _lines.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _shots.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _moods.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _music.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));

        // Whatever it does on its own is its own again.
        Release(model);
    }

    /// <summary>Whether a script is animating somebody right now.</summary>
    /// <returns>True while a clip the story asked for is playing on them.</returns>
    /// <param name="actor">Their model name or noun.</param>
    public bool Performing(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        string model = ModelNamed(actor)?.Name ?? actor;

        return _playing.Any(p => !p.FromBehaviour && p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gives a model back to its own script, once nothing else is animating it.</summary>
    private void Release(string model)
    {
        if (_playing.Any(p => !p.FromBehaviour && p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _held.Remove(model);

        if (BehaviourOf(model) is { Interrupted: true } waiting)
        {
            waiting.Interrupted = false;
            waiting.Remaining = 0;
        }
    }

    /// <summary>The script a model runs on its own, whichever kind of thing it is.</summary>
    private Behaviour? BehaviourOf(string model)
    {
        if (_fidgets.TryGetValue(model, out Fidget? fidget))
        {
            return fidget.Running;
        }

        foreach (Behaviour running in _scenery)
        {
            if (running.Owner is { } owner && owner.Name.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                return running;
            }
        }

        return null;
    }

    /// <summary>Who the game's characters are, and how each of them walks.</summary>
    public Actors.CharacterLibrary? Characters { get; set; }

    /// <summary>What a step sounds like, when the three files that decide were read.</summary>
    public Actors.Footsteps? Steps { get; set; }

    /// <summary>How many actors are crossing the room.</summary>
    public int OnTheMove => _walking.Count;

    /// <summary>Where an actor is now, if the scene has one by that name.</summary>
    /// <returns>Their position, or null.</returns>
    /// <param name="actor">The actor's model name.</param>
    public Vector3? Where(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _logical.TryGetValue(actor, out Vector3 where) ? where : null;
    }

    /// <summary>Where an actor is walking to, if they are walking anywhere.</summary>
    /// <returns>The end of their route, or null when they are standing still.</returns>
    /// <param name="actor">Their model name or noun.</param>
    public Vector3? Heading(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (_walking.TryGetValue(actor, out Walking? walking))
        {
            return walking.Walker.Destination;
        }

        // Under the other name.
        return _standing.TryGetValue(actor, out PlacedModel? placed) && _walking.TryGetValue(placed.Name, out Walking? theirs)
            ? theirs.Walker.Destination : null;
    }

    /// <summary>Which way somebody is looking.</summary>
    /// <returns>A unit vector along their line of sight, or null when nobody answers.</returns>
    /// <param name="actor">Their model name or noun.</param>
    public Vector3? Looking(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (_walking.TryGetValue(actor, out Walking? walking))
        {
            return Ahead(walking.Walker.Facing);
        }

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            return null;
        }

        return _walking.TryGetValue(placed.Name, out Walking? theirs) ? Ahead(theirs.Walker.Facing)
            : Ahead(Navigation.Walker.HeadingOf(placed.Standing));
    }

    /// <summary>Which way somebody is facing, as the game's data measures a heading.</summary>
    /// <returns>The heading in degrees, or null when nobody of that name is in the room.</returns>
    /// <param name="actor">Who, by either of their names.</param>
    public float? Facing(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _standing.TryGetValue(actor, out PlacedModel? placed) ? Navigation.Walker.HeadingOf(placed.Standing) : null;
    }

    /// <summary>Which way an actor faces, measured the way their placement was written.</summary>
    /// <returns>The heading in radians, or null when nobody of that name is in the room.</returns>
    /// <param name="actor">Who, by either of their names.</param>
    public float? Turned(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _standing.TryGetValue(actor, out PlacedModel? placed) ? Actors.FacingArrow.HeadingOf(placed.Standing, placed.BuiltFacing) : null;
    }

    /// <summary>The way an actor faces once they have stopped: the facing a walk under way was asked to end on, else the way they stand.</summary>
    public float? SettledFacing(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _walking.TryGetValue(actor, out Walking? walking) && walking.Walker.ArrivalFacing is { } arriving ? arriving : Facing(actor);
    }

    /// <summary>Where an actor's eyes are, or null when they are not standing here.</summary>
    public Vector3? EyesOf(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _standing.TryGetValue(actor, out PlacedModel? placed) && Where(actor) is { } feet ? feet + (Vector3.UnitY * Eyes(placed)) : null;
    }

    /// <summary>How far a clip has carried an actor's head from where their own standing pose has it.</summary>
    /// <returns>The shift, in world units, or null when there is no head to measure or nobody by that name.</returns>
    /// <param name="actor">Their model name or the noun the scene gives them.</param>
    public Vector3? HeadShift(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (ModelNamed(actor) is not { } placed || (Where(actor) ?? Where(placed.Name)) is not { } feet || HeadOf(placed) is not { } head ||
            head < 0 || head >= placed.Model.Meshes.Count)
        {
            return null;
        }

        Matrix4x4 world = _geometry.TransformOf(placed.Placement);

        Vector3 now = Vector3.Transform(placed.PoseOf(head).Translation, world);
        Vector3 rest = Vector3.Transform(placed.Model.Meshes[head].MeshToLocal.Translation, world);

        // Measured from the feet at each end, so a clip that walks the character carries their head along for nothing: what is left is what it does
        // to the head that their standing does not — a kneel, a lean, a reach down — which is the part an eye over their feet cannot see.
        return now - feet - (rest - world.Translation);
    }

    /// <summary>The direction a heading looks along.</summary>
    private static Vector3 Ahead(float heading) => new(MathF.Sin(heading), 0f, MathF.Cos(heading));

    /// <summary>Sets an actor walking to a place on the floor.</summary>
    /// <returns>How long the walk will take, or zero when there is no walking to do.</returns>
    /// <param name="actor">Their model name.</param>
    /// <param name="destination">Where to go, in world space.</param>
    /// <param name="arriveFacing">Which way to face on arrival, in radians, or null to keep the direction of travel.</param>
    /// <param name="arriveLookingAt">What to be looking at on arrival, if not a fixed heading.</param>
    /// <param name="hurry">Whether to go at times the usual pace.</param>
    /// <param name="mayRun">Whether a long walk may pick up the pace by itself.</param>
    /// <param name="untilSeen">The bounds of what the walk is to see, or null for an ordinary walk.</param>
    public double Walk( string actor, Vector3 destination, float? arriveFacing = null, Vector3? arriveLookingAt = null, bool hurry = false,
        bool mayRun = false, SightTarget? untilSeen = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            Diagnostics.Add(new Diagnostic( "GK3R3310", DiagnosticSeverity.Warning, "A script asked an actor to walk who is not in the room.",
                _scene.Name, null, "an actor the scene placed", actor, "Check the name against the scene's [ACTORS] section."));

            return 0;
        }

        Vector3 from = Where(actor) ?? placed.Transform.Translation;

        // Where they are facing now, not where the scene first put them.
        float facing = _walking.TryGetValue(actor, out Walking? already) ? already.Walker.Facing
            : Actors.FacingArrow.HeadingOf(placed.Standing, placed.BuiltFacing);

        // Already able to see it is not a walk at all — turn where you stand and look.
        if (untilSeen is { } wanted && Sight is { } sight && sight.InView(from + (Vector3.UnitY * Eyes(placed)), wanted))
        {
            return Turn(actor, (wanted.Minimum + wanted.Maximum) * 0.5f);
        }

        WalkRoute route = _scene.Walkable is { } boundary ? WalkPath.Find(boundary, from, destination)

            // No boundary is no obstacles, so the straight line is the route.
            : new WalkRoute(true, [destination]);

        // A walk the player asked for stops where it walks onto something that acts on whoever stands there, rather than crossing it and carrying on.
        route = ShortenedAtTrigger(actor, route);

        // And a walk to see something stops where it can see it.
        if (untilSeen is { } thing)
        {
            WalkRoute whole = route;

            route = ShortenedWhenSeen(route, placed, thing);

            string outcome = route.Points.Count < whole.Points.Count ? string.Create( CultureInfo.InvariantCulture,
                    $"seen from point {route.Points.Count} at ({route.Points[^1].X:0.#}, {route.Points[^1].Z:0.#})") : "never in view";

            TraceActors?.Invoke(string.Create( CultureInfo.InvariantCulture,
                $"{placed.Name} walks to see ({thing.Minimum.X:0.#}, {thing.Minimum.Y:0.#}, " +
                $"{thing.Minimum.Z:0.#})-({thing.Maximum.X:0.#}, {thing.Maximum.Y:0.#}, " +
                $"{thing.Maximum.Z:0.#}) from ({from.X:0.#}, {from.Z:0.#}) aimed " +
                $"({destination.X:0.#}, {destination.Z:0.#}): {whole.Points.Count} point(s) " +
                $"ending ({whole.Points[^1].X:0.#}, {whole.Points[^1].Z:0.#}), {outcome}"));
        }

        // Asked for at once rather than walked.
        if (WarpNextWalk)
        {
            WarpNextWalk = false;

            if (route.Points.Count > 0)
            {
                Place( actor, route.Points[^1], arriveFacing ?? (arriveLookingAt is { } look ? Walker.Heading(look - route.Points[^1]) : facing));

                return 0;
            }
        }

        // Far enough to be worth running.
        if (mayRun && !hurry && route.Points.Count > 0)
        {
            Vector3 first = route.Points[0] - from;

            hurry = MathF.Sqrt((first.X * first.X) + (first.Z * first.Z)) + route.Length() >= RunBeyond;
        }

        // The stride first, because its pace is what the walk is measured at.
        WalkCycle? stride = WalkCycle.For( placed, Characters, Animations, Clips,
            _strides.TryGetValue(placed.Name, out (string Start, string Loop) given) ? given.Loop : null);
        float rate = hurry ? HurryFactor : 1f;

        if (stride is not null)
        {
            stride.Rate = rate;
        }

        var walker = new Walker( actor, route, Standing(from), facing, arriveFacing, arriveLookingAt,

            // Both multiplied by the same number, which is the whole point: the ground covered and the feet covering it have to stay in agreement.
            (stride?.Pace ?? Walker.Speed) * rate)
        {
            // The room's floor, not the actor's.
            Ground = _scene.Ground is { } ground ? ground.Height : null,
        };

        if (!walker.Walking)
        {
            _walking.Remove(actor);
            return 0;
        }

        // Whatever they were doing, they are walking now.
        StopAnimating(placed.Name);

        _walking[actor] = new Walking(placed, walker, stride);
        return walker.Seconds;
    }

    /// <summary>The room's sight tester, built the first time anybody asks.</summary>
    private SceneSight? Sight => _sight ??= SceneSight.For(_scene.Geometry);

    private SceneSight? _sight;

    /// <summary>How high an actor's eyes are above their feet.</summary>
    /// <returns>The character's own height, or the walker's default.</returns>
    /// <param name="actor">The model.</param>
    private float Eyes(PlacedModel actor) => Characters?.Of(actor.Name)?.WalkerHeight is { } height && height > 0 ? height : Walker.StandOff;

    /// <summary>Cuts a walk short at the point where what it is going to look at comes into view.</summary>
    /// <returns>The route, cut where the thing is first visible.</returns>
    /// <param name="route">The route as the boundary found it.</param>
    /// <param name="actor">Who is walking, for how tall they are.</param>
    /// <param name="thing">The bounds of what they are walking to see.</param>
    private WalkRoute ShortenedWhenSeen(WalkRoute route, PlacedModel actor, SightTarget thing)
    {
        if (Sight is not { } sight || route.Points.Count == 0)
        {
            return route;
        }

        float eyes = Eyes(actor);

        for (int i = 0; i < route.Points.Count; i++)
        {
            Vector3 corner = route.Points[i];

            bool seen = sight.InView(corner + (Vector3.UnitY * eyes), thing);

            if (!seen && i > 0)
            {
                Vector3 previous = route.Points[i - 1];

                for (float along = 0.25f; along < 1f && !seen; along += 0.25f)
                {
                    Vector3 between = Vector3.Lerp(previous, corner, along);

                    seen = sight.InView(between + (Vector3.UnitY * eyes), thing);
                }
            }

            if (!seen)
            {
                continue;
            }

            int stop = Math.Min(i + 1, route.Points.Count - 1);

            return stop >= route.Points.Count - 1 ? route : new WalkRoute(false, [.. route.Points.Take(stop + 1)]);
        }

        return route;
    }

    /// <summary>Drops a point onto the room's floor, when the room has one.</summary>
    private Vector3 Standing(Vector3 at) => _scene.Ground?.Height(at) is { } height ? new Vector3(at.X, height, at.Z) : at;

    /// <summary>Writes an actor's logical position from where their pose has put them, under every name they answer to.</summary>
    private void Follow(string actor, Vector3? position)
    {
        if (position is not { } where || !_standing.TryGetValue(actor, out PlacedModel? placed) || IsDriven(placed))
        {
            return;
        }

        Record(placed, where);
    }

    /// <summary>Writes it down whoever is asking, for a move the player or a script made.</summary>
    private void Record(PlacedModel placed, Vector3 where)
    {
        _logical[placed.Name] = where;

        if (placed.Noun is { Length: > 0 } noun)
        {
            _logical[noun] = where;
        }
    }

    /// <summary>Stands an actor at a spot outright, without walking them there.</summary>
    /// <returns>True when there was somebody of that name to move.</returns>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="position">Where to stand them, in world space.</param>
    /// <param name="heading">Which way to face, as the game's data measures a heading.</param>
    public bool Place(string actor, Vector3 position, float heading)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            return false;
        }

        // A scene's spots carry a height, but not always the floor's: several are authored at zero and rely on the room being flat there.
        position = Standing(position);

        // Whatever they were doing, they are standing here now.
        _walking.Remove(actor);
        _walking.Remove(placed.Name);
        StopAnimating(placed.Name);

        // The placement is scale, then a turn, then a move, and the scale has to survive.
        float scale = new Vector3( placed.Transform.M11, placed.Transform.M12, placed.Transform.M13).Length();

        _geometry.MoveModel( placed.Placement, Matrix4x4.CreateScale(scale <= 0 ? 1f : scale) * Matrix4x4.CreateRotationY(
                Actors.FacingArrow.Rotation(heading, placed.BuiltFacing)) * Matrix4x4.CreateTranslation(position));

        Record(placed, position);

        // Nothing to tell the heads: they read the model's own transform, which this has just written, on the frame that follows.
        return true;
    }

    /// <summary>Moves an actor the player is driving themselves, leaving whatever they are doing alone: no walk is cancelled and no.</summary>
    /// <returns>True when there was somebody of that name to move.</returns>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="position">Where their feet are, already on the floor.</param>
    /// <param name="heading">Which way they face, as the game's data measures a heading.</param>
    public bool Step(string actor, Vector3 position, float heading)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            return false;
        }

        _geometry.MoveModel(placed.Placement, Standing(placed, position, heading));
        Record(placed, position);

        return true;
    }

    /// <summary>Makes the noise a foot landing makes, for a walk nothing is animating.</summary>
    /// <param name="actor">Whose foot.</param>
    public void Footstep(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Tread(new AnimationStep(0, actor, false));
    }

    /// <summary>Puts a character into their default standing pose.</summary>
    /// <returns>True when there was such a character and their walk-start clip was found.</returns>
    /// <param name="actor">Their model name or noun.</param>
    public bool Stand(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (ModelNamed(actor) is not { Kind: PlacedModelKind.Actor } placed ||
            Characters?.Of(placed.Name)?.StartAnimation is not { Length: > 0 } upright)
        {
            return false;
        }

        // Only this character's own clip out of it, and its first frame.
        return Pose(upright, [placed.Name], atEnd: false) > 0;
    }

    /// <summary>Moves an actor's placement to where their opening pose left them.</summary>
    /// <param name="actor">The actor, as the room placed them.</param>
    /// <param name="position">Where the pose has them standing, in world space.</param>
    /// <param name="heading">Which way it has them facing.</param>
    private void Reseat(PlacedModel actor, Vector3 position, float heading)
    {
        _geometry.MoveModel(actor.Placement, Standing(actor, position, heading));
    }

    /// <summary>A placement that stands an actor at a spot, facing a heading.</summary>
    /// <param name="actor">Whose placement it is, for its scale and its built facing.</param>
    /// <param name="position">Where to stand them.</param>
    /// <param name="heading">Which way to face, as the game's data measures a heading.</param>
    private static Matrix4x4 Standing(PlacedModel actor, Vector3 position, float heading)
    {
        float scale = new Vector3( actor.Transform.M11, actor.Transform.M12, actor.Transform.M13).Length();

        return Matrix4x4.CreateScale(scale <= 0 ? 1f : scale) * Matrix4x4.CreateRotationY( Actors.FacingArrow.Rotation(heading, actor.BuiltFacing)) *
            Matrix4x4.CreateTranslation(position);
    }

    /// <summary>Takes where a clip has left an actor standing as where that actor now stands.</summary>
    /// <param name="actor">Whoever the clip was posing.</param>
    /// <param name="position">Where its last frame put their feet, in the room.</param>
    /// <param name="heading">And which way it left them facing.</param>
    private void Settle(PlacedModel actor, Vector3 position, float heading)
    {
        if (!actor.Placement.Exists)
        {
            return;
        }

        Matrix4x4 was = _geometry.TransformOf(actor.Placement);
        Matrix4x4 now = Standing(actor, position, heading);

        if (!Matrix4x4.Invert(now, out Matrix4x4 back))
        {
            return;
        }

        Matrix4x4 keep = was * back;

        _geometry.MoveModel(actor.Placement, now);

        for (int mesh = 0; mesh < actor.Model.Meshes.Count; mesh++)
        {
            Matrix4x4 local = actor.PoseOf(mesh) * keep;

            _geometry.PoseMesh(actor.Placement, mesh, local);
            actor.Pose(mesh, local);
        }
    }

    /// <summary>The actor the player is moving themselves, or null when nobody is.</summary>
    public string? Driven { get; set; }

    /// <summary>Whether a model is the one the player is moving themselves.</summary>
    private bool IsDriven(PlacedModel model) => Driven is { Length: > 0 } who && (model.Name.Equals(who, StringComparison.OrdinalIgnoreCase) ||
         (model.Noun is { Length: > 0 } noun && noun.Equals(who, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Hands a clip's last frame to the actor it was posing.</summary>
    /// <param name="playing">The clip, on the frame it stopped.</param>
    private void Adopt(Playing playing)
    {
        if (playing.Target.Kind != PlacedModelKind.Actor || IsDriven(playing.Target))
        {
            return;
        }

        // Not for a clip the model's own behaviour script asked for.
        if (playing.FromBehaviour && (playing.Target.Spotted || !playing.Absolute))
        {
            return;
        }

        Matrix4x4 standing = _geometry.TransformOf(playing.Target.Placement);

        if (playing.Facing(standing) is not { } heading)
        {
            Trace("keeps", playing.Clip.Name, playing.Target, "the clip says nothing about its hips");
            return;
        }

        Settle(playing.Target, playing.Now(standing), heading);

        Trace( "keeps", playing.Clip.Name, playing.Target, Actors.AnimationStart.Stance ? "taken from the stance"
                : "from the hip mesh — this clip poses no shoes");
    }

    /// <summary>Turns an actor on the spot to face something.</summary>
    /// <returns>How long the turn will take, or zero when there is nobody to turn.</returns>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="target">What to face, in world space.</param>
    public double Turn(string actor, Vector3 target)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Where(actor) is not { } from)
        {
            return 0;
        }

        Vector3 towards = target - from;

        return Walk(actor, from, Walker.Heading(towards));
    }

    /// <summary>Stops everyone where they stand.</summary>
    public void StopWalking()
    {
        foreach (Walking walking in _walking.Values)
        {
            walking.Walker.Stop();
        }

        _walking.Clear();
    }

    /// <summary>Something that has not happened yet.</summary>
    /// <param name="remaining">How much longer to hold it.</param>
    /// <param name="work">What to do then.</param>
    private sealed class Held(double remaining, Action work)
    {
        /// <summary>How much of the wait is left.</summary>
        public double Remaining { get; set; } = remaining;

        /// <summary>What to do when it is over.</summary>
        public Action Work { get; } = work;

        /// <summary>Scripts it cannot happen until, when it is waiting on scripts rather than on a clock.</summary>
        public IReadOnlyList<SheepThread>? Until { get; init; }
    }

    private readonly List<Held> _later = [];

    /// <summary>How many things are waiting to happen.</summary>
    public int Later => _later.Count;

    /// <summary>Holds something back for a while.</summary>
    /// <returns>True when it was taken, false when there was nothing to wait for.</returns>
    /// <param name="seconds">How long to hold it.</param>
    /// <param name="work">What to do then.</param>
    public bool After(double seconds, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (seconds <= 0)
        {
            return false;
        }

        _later.Add(new Held(seconds, work));
        return true;
    }

    /// <summary>Holds something back until the next frame, however short that is.</summary>
    /// <param name="work">What to do then.</param>
    public void Next(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _later.Add(new Held(double.Epsilon, work));
    }

    /// <summary>Holds something back until the scripts a call started have finished.</summary>
    /// <returns>True when it was taken, false when there was nothing to wait for.</returns>
    /// <param name="scripts">The threads it started.</param>
    /// <param name="work">What to do once none of them is still running.</param>
    public bool Until(IReadOnlyList<SheepThread> scripts, Action work)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(work);

        if (_scripts?.Outstanding(scripts) != true)
        {
            return false;
        }

        _later.Add(new Held(0, work) { Until = scripts });
        return true;
    }

    /// <summary>Forgets everything that was waiting to happen.</summary>
    public void Cancel()
    {
        _later.Clear();
        _awaited.Clear();
    }

    /// <summary>Runs whatever has waited long enough.</summary>
    private void StepLater(double seconds, List<string> happened)
    {
        List<Held>? due = null;

        foreach (Held held in _later)
        {
            held.Remaining -= seconds;

            // The clock first because it is the cheap half, and then the scripts: a wait on a call into a script is over when nothing it started is.
            if (held.Remaining > 0 || _scripts?.Outstanding(held.Until) == true)
            {
                continue;
            }

            (due ??= []).Add(held);
        }

        if (due is null)
        {
            return;
        }

        foreach (Held held in due)
        {
            // Gone already, so there is nothing to run: one of the earlier ones left the room, and Cancel forgets everything it was still holding —.
            if (!_later.Remove(held))
            {
                continue;
            }

            try
            {
                held.Work();
            }
            catch (Formats.FormatParseException ex)
            {
                Diagnostics.Add(ex.Diagnostic);
                happened.Add("an action held back for a walk could not be run");
            }
        }
    }

    /// <summary>Diagnostics raised while the world went on by itself.</summary>
    private sealed class Behaviour(Formats.Animation.GasFile script, PlacedModel? owner)
    {
        /// <summary>The script.</summary>
        public Formats.Animation.GasFile Script { get; set; } = script;

        /// <summary>What it drives, or null when it drives nothing in particular.</summary>
        public PlacedModel? Owner { get; } = owner;

        public int Position { get; set; }

        /// <summary>Seconds until the next step.</summary>
        public double Remaining { get; set; }

        /// <summary>Which of the script's standing conditions are currently met.</summary>
        public HashSet<int> Noticed { get; } = [];

        /// <summary>Whether the clip it was waiting out was stopped for something else.</summary>
        public bool Interrupted { get; set; }

        /// <summary>The animation it last asked for, which is what a cleanup is looked up by.</summary>
        public string? Playing { get; set; }

        /// <summary>The language's whole state: one integer per name.</summary>
        public Dictionary<string, int> Registers { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether it says to start again, rather than stopping at the end.</summary>
        public bool Repeats => Script.Steps.Any(s => s.Action is Formats.Animation.GasAction.Loop or Formats.Animation.GasAction.Goto);
    }

    /// <summary>One character's three scripts, and which of them is running.</summary>
    private sealed class Fidget(PlacedModel model)
    {
        public PlacedModel Model { get; } = model;

        /// <summary>Which of the three is running, if any.</summary>
        public FidgetKind? Mode { get; private set; }

        /// <summary>One a script asked for by name, which overrides who is speaking.</summary>
        public FidgetKind? Forced { get; set; }

        /// <summary>Whether they have been told to stand still.</summary>
        public bool Stopped { get; set; }

        /// <summary>The script currently running, or null when there is none for this mode.</summary>
        public Behaviour? Running { get; private set; }

        /// <summary>Switches to one of the three and starts it from the top.</summary>
        public void Enter(FidgetKind mode, PlacedModel owner)
        {
            Mode = mode;

            Formats.Animation.GasFile? script = mode switch
            {
                FidgetKind.Talk => owner.Talk ?? owner.Idle, FidgetKind.Listen => owner.Listen ?? owner.Idle, _ => owner.Idle,
            };

            Running = script is { Steps.Count: > 0 } ? new Behaviour(script, owner) : null;
        }
    }

    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>How many actors in the scene have a head that can turn.</summary>
    public int Movable => _actors.Count;

    /// <summary>Where the story wants the view, or null while it has not moved it.</summary>
    public Camera? View { get; private set; }

    /// <summary>Whether the view is on its way somewhere.</summary>
    public bool Gliding => _to is not null && _glided < GlideSeconds;

    /// <summary>Whether the story has pointed a camera of its own since the last thing it began.</summary>
    public bool Framed { get; private set; }

    /// <summary>Says where the view already is, so a glide has somewhere to leave from.</summary>
    /// <param name="camera">Where the scene opened.</param>
    public void StartAt(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        View = camera;
    }

    /// <summary>Lets time pass.</summary>
    /// <returns>What the world did on its own, for whoever wants to say so.</returns>
    /// <param name="seconds">How much.</param>
    public IReadOnlyList<string> Advance(double seconds)
    {
        if (seconds <= 0)
        {
            return [];
        }

        List<string> happened = [];

        // What is left of the action that is running.
        _api.ActionSeconds = Math.Max(0, _api.ActionSeconds - seconds);

        // What an action was waiting on, forgotten as soon as none of it is running.
        if (_awaited.Count > 0 && _scripts?.Outstanding(_awaited) != true)
        {
            _awaited.Clear();
        }

        // Nothing is running, so nothing is holding the camera either.
        if (!Directing)
        {
            Framed = false;
        }

        // The scripts first: one carrying on from a wait may cut the camera or set a timer, and it should take effect in the frame it happened.
        foreach (string carried in _scripts?.Advance(seconds) ?? [])
        {
            happened.Add($"{carried} carried on");
        }

        // Whatever machinery the room has of its own: the laser heads swinging round to the angle they were sent to, the beams stretching to.
        Mechanism?.Advance(seconds);

        StepBehaviours(seconds);
        MoveView(seconds);

        // Faces before anything that moves anybody: what a face is doing depends on the clock and not on where its owner is standing, and a mouth.
        Faces?.Advance(seconds);

        // Anything that was waiting for the player to get somewhere.
        StepLater(seconds, happened);

        // The clock moves for every timer whatever else is happening; what waits on the story being free is performing one.
        _api.State.Timers.Advance(seconds);

        while (!Occupied && _api.State.Timers.TakeDue() is { } timer)
        {
            happened.Add(Fire(timer));
        }

        // The noises an animation makes, at the frames it says.
        for (int i = _cues.Count - 1; i >= 0; i--)
        {
            if (_cues[i].Step(seconds) is not { } due)
            {
                continue;
            }

            // Where it comes from: the model the cue names, if the room has it standing somewhere.
            Vector3? at = due.Model.Length > 0 ? Where(due.Model) : null;

            if (Sound?.Invoke(due, at) == false)
            {
                Diagnostics.Add(new Diagnostic( "GK3R3316", DiagnosticSeverity.Info, "An animation asks for a sound the archives do not have.",
                    _scene.Name, null, "a .WAV of that name", due.Name, "Common in the corpus: some cues name sounds that were cut."));
            }

            if (_cues[i].Finished)
            {
                _cues.RemoveAt(i);
            }
        }

        // The feet, which need where the actor is now rather than where the clip was authored: the sound is the floor under them at the moment the.
        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            if (_steps[i].Step(seconds) is { } fell)
            {
                Tread(fell);
            }

            if (_steps[i].Finished)
            {
                _steps.RemoveAt(i);
            }
        }

        // And what it repaints.
        for (int i = _swaps.Count - 1; i >= 0; i--)
        {
            if (_swaps[i].Step(seconds) is { } swap)
            {
                Repaint([swap]);
            }

            if (_swaps[i].Finished)
            {
                _swaps.RemoveAt(i);
            }
        }

        // And what it repaints about the room.
        for (int i = _roomSwaps.Count - 1; i >= 0; i--)
        {
            if (_roomSwaps[i].Step(seconds) is { } swap)
            {
                PaintRoom([swap]);
            }

            if (_roomSwaps[i].Finished)
            {
                _roomSwaps.RemoveAt(i);
            }
        }

        for (int i = _roomShowings.Count - 1; i >= 0; i--)
        {
            if (_roomShowings[i].Step(seconds) is { } change)
            {
                RevealRoom([change]);
            }

            if (_roomShowings[i].Finished)
            {
                _roomShowings.RemoveAt(i);
            }
        }

        // What a moment frames, puts on faces, scores and says — in that order, and each of them in frame order rather than in the order the nodes.
        Film(Due(_shots, seconds, s => s.Frame));
        Wear(Due(_moods, seconds, m => m.Frame));
        Score(Due(_music, seconds, m => m.Frame));
        Say(Due(_lines, seconds, d => d.Frame));

        // What an animation shows and hides as it runs, on the frames it names.
        for (int i = _showings.Count - 1; i >= 0; i--)
        {
            if (_showings[i].Step(seconds) is { } change)
            {
                Reveal([change]);
            }

            if (_showings[i].Finished)
            {
                _showings.RemoveAt(i);
            }
        }

        // Animation before walking: a clip poses a model's meshes in the model's own space and walking moves the model, so doing it the other way.
        for (int i = _playing.Count - 1; i >= 0; i--)
        {
            Playing playing = _playing[i];
            bool running = playing.Step(_geometry, (float)seconds);

            // The actor's position follows the model, every frame, as the original syncs it in LateUpdate.
            Follow( playing.Target.Name, playing.Target.Kind == PlacedModelKind.Actor ? playing.Now(_geometry.TransformOf(playing.Target.Placement))
                    : playing.Carried);

            if (!running)
            {
                // A non-move animation puts the actor back where it found them: the pose stays, the ground does not count.
                if (playing.Reverts)
                {
                    Follow(playing.Target.Name, playing.Began);
                    Trace("reverts after", playing.Clip.Name, playing.Target);
                }
                else
                {
                    // And keeping it means writing it down.
                    Adopt(playing);
                }

                happened.Add($"{playing.Clip.Name} finished");
                _playing.RemoveAt(i);

                // Back to whatever it does when nobody is asking.
                if (!playing.FromBehaviour)
                {
                    Release(playing.Target.Name);
                }
            }
        }

        // Walking before turning heads: a head that is looking at something has to be aimed from where its owner is now, not from where they were a.
        foreach (string who in _walking.Keys.ToList())
        {
            Walking walking = _walking[who];

            if (!walking.Walker.Advance((float)seconds))
            {
                _walking.Remove(who);
                happened.Add($"{who} arrived");
            }

            _geometry.MoveModel( walking.Placement, walking.Walker.Transform(walking.Scale, walking.Built));

            // The legs, in the model's own space, on top of wherever the model now is.
            walking.Stride?.Step(_geometry, (float)seconds);

            foreach (AnimationStep fell in walking.Stride?.Landed ?? [])
            {
                Tread(fell);
            }

            if (_standing.TryGetValue(who, out PlacedModel? walker))
            {
                Record(walker, walking.Walker.Position);
            }
        }

        // What somebody is holding goes where they are — after their own clip has posed them and after they have walked, which is why it is here and.
        foreach ((PlacedModel held, PlacedModel holder) in _carried.Values)
        {
            Carry(held, holder);
        }

        // The timed glances run out here, before the heads move: LOOKAT's five seconds are five seconds, not for ever.
        _glances.Tick(seconds);

        foreach (Turning actor in _actors)
        {
            // Where the model is now, whatever put it there.
            Matrix4x4 now = _geometry.TransformOf(actor.Placement);

            // And while a clip has the body, from the clip.
            Playing? driving = _playing.Find(p => p.Target.Kind == PlacedModelKind.Actor && p.Target.Placement.Id == actor.Placement.Id);

            if (driving is not null)
            {
                actor.Stands(driving.Now(now), driving.Facing(now) ?? actor.HeadingOf(now));
            }
            else
            {
                actor.Stands(now.Translation, actor.HeadingOf(now));
            }

            actor.Step(_glances, (float)seconds);

            // Written every frame rather than only on the frames the head moved. The clips are posed first and a pose replaces the head's whole
            // transform, turn and all, so a glance that had settled was wiped out by the next clip frame and put back by the next frame that
            // happened to move it — a head jumping on and off its own neck for as long as anybody was looking at anything.
            _geometry.TurnMesh(actor.Placement, actor.Head, actor.Turn());
        }

        // Last, because it asks where the player is standing and this is the frame in which they have finished moving.
        Tripped(happened);

        return happened;
    }

    /// <summary>The patches of floor that act on whoever walks onto them.</summary>
    private readonly List<SceneTrigger> _triggers = [];

    /// <summary>Runs the action for any trigger the player is standing in.</summary>
    /// <param name="happened">What to report it as.</param>
    private void Tripped(List<string> happened)
    {
        if (_triggers.Count == 0 || Occupied || Where(_api.State.Ego) is not { } standing)
        {
            return;
        }

        foreach (SceneTrigger trigger in _triggers)
        {
            // Nothing written about the noun is not a refusal to report; it is a rectangle that does nothing at this point in the story, and the.
            if (!trigger.Rect.Contains(standing.X, standing.Z) || _actions?.Find(trigger.Noun, Walked) is null)
            {
                continue;
            }

            happened.Add(Fire(trigger.Noun, Walked));
            return;
        }
    }

    /// <summary>Cuts a walk short where it would step onto a trigger.</summary>
    /// <returns>The route, cut at the edge of the first trigger it enters.</returns>
    /// <param name="actor">Whose walk it is.</param>
    /// <param name="route">The route the boundary found.</param>
    private WalkRoute ShortenedAtTrigger(string actor, WalkRoute route)
    {
        if (_triggers.Count == 0 || Occupied || route.Points.Count == 0 || !string.Equals( ModelNamed(actor)?.Name ?? actor,
                ModelNamed(_api.State.Ego)?.Name ?? _api.State.Ego, StringComparison.OrdinalIgnoreCase))
        {
            return route;
        }

        for (int i = 0; i < route.Points.Count; i++)
        {
            Vector3 point = route.Points[i];

            foreach (SceneTrigger trigger in _triggers)
            {
                // A rectangle nothing is written about does nothing, so walking over it is walking over floor.
                if (!trigger.Rect.Contains(point.X, point.Z) || _actions?.Find(trigger.Noun, Walked) is null)
                {
                    continue;
                }

                Vector3[] cut = [.. route.Points.Take(i + 1)];

                // Where the walk crosses the edge rather than the corner the boundary happened to put inside it, so the player stops on the line.
                if (i > 0 && Entry(trigger.Rect, route.Points[i - 1], point) is { } edge)
                {
                    cut[i] = new Vector3(edge.X, point.Y, edge.Y);
                }

                return new WalkRoute(false, cut);
            }
        }

        return route;
    }

    /// <summary>Where a segment first crosses into a rectangle.</summary>
    /// <returns>The crossing point on X and Z, or null when the segment is degenerate.</returns>
    /// <param name="rect">The rectangle, on the ground plan.</param>
    /// <param name="before">The end of the segment outside it.</param>
    /// <param name="inside">The end of the segment within it.</param>
    private static Vector2? Entry(SceneRect rect, Vector3 before, Vector3 inside)
    {
        var start = new Vector2(before.X, before.Z);
        Vector2 along = new Vector2(inside.X, inside.Z) - start;

        float enters = 0f;
        float leaves = 1f;

        if (!Clip(-along.X, start.X - rect.MinX, ref enters, ref leaves) || !Clip(along.X, rect.MaxX - start.X, ref enters, ref leaves) ||
            !Clip(-along.Y, start.Y - rect.MinZ, ref enters, ref leaves) || !Clip(along.Y, rect.MaxZ - start.Y, ref enters, ref leaves))
        {
            return null;
        }

        return start + (along * enters);
    }

    /// <summary>One side of the Liang-Barsky clip.</summary>
    /// <returns>False when the segment misses the rectangle entirely.</returns>
    /// <param name="denominator">How fast the segment approaches the edge.</param>
    /// <param name="numerator">How far outside the edge the segment begins.</param>
    /// <param name="enters">The parameter at which it is inside so far.</param>
    /// <param name="leaves">The parameter at which it leaves.</param>
    private static bool Clip(float denominator, float numerator, ref float enters, ref float leaves)
    {
        if (denominator == 0)
        {
            return numerator >= 0;
        }

        float at = numerator / denominator;

        if (denominator < 0)
        {
            if (at > leaves)
            {
                return false;
            }

            enters = MathF.Max(enters, at);
        }
        else
        {
            if (at < enters)
            {
                return false;
            }

            leaves = MathF.Min(leaves, at);
        }

        return true;
    }

    /// <summary>The verb a trigger's noun is looked up with.</summary>
    private const string Walked = "WALK";

    /// <summary>How many scripts the room was left holding, as of the last action.</summary>
    private int _quiet = -1;

    /// <summary>Notes what was already running, before an action adds to it.</summary>
    public void Starting()
    {
        // A new thing to do is a new chance for the story to point a camera.
        Framed = false;

        int waiting = _scripts?.Count ?? 0;

        if (_quiet < 0 || waiting <= _quiet)
        {
            _quiet = waiting;
        }
    }

    /// <summary>Notes what the room is left holding, once an action is through.</summary>
    public void Ended() => _quiet = _scripts?.Count ?? 0;

    /// <summary>Whether the story is in the middle of something.</summary>
    public bool Occupied => Acting || (_quiet >= 0 && (_scripts?.Count ?? 0) > _quiet);

    /// <summary>The scripts an action has said it is waiting on.</summary>
    private readonly List<SheepThread> _awaited = [];

    /// <summary>Whether an action is playing, by the signals that go away again.</summary>
    public bool Acting => _later.Count > 0 || _api.ActionSeconds > 0 || Performing(_api.State.Ego) || _scripts?.Outstanding(_awaited) == true;

    /// <summary>Notes the scripts an action has waited on.</summary>
    /// <param name="scripts">The threads its call started.</param>
    public void Awaiting(IReadOnlyList<SheepThread> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        _awaited.AddRange(scripts);
    }

    /// <summary>Whether the story is doing something to one person that they may not walk out of.</summary>
    /// <returns>True while an action is running on them, a clip is posing them, or they are being walked somewhere.</returns>
    /// <param name="actor">Their model name or noun.</param>
    public bool Busy(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // Asked about one person rather than about the room, because Occupied and OnTheMove are both true of a room where somebody else is doing
        // something: RC1 has Emilio and Madeline moving about on scripts of their own all day, and taking the controls away for those left the
        // player standing still whenever either of them changed animation.
        string model = ModelNamed(actor)?.Name ?? actor;

        return _later.Count > 0 || _api.ActionSeconds > 0 || Performing(actor) || _walking.ContainsKey(actor) || _walking.ContainsKey(model);
    }

    /// <summary>Whether the story is holding the camera rather than the player.</summary>
    public bool Directing => _api.State.ForcedCameraCuts || (Occupied && _api.State.CinematicsEnabled);

    /// <summary>Gives the room back to the player when something has wedged it.</summary>
    /// <returns>What was let go of, one line each, or empty when nothing was holding it.</returns>
    public IReadOnlyList<string> Unstick()
    {
        List<string> let = [];

        if (_later.Count > 0)
        {
            let.Add($"{_later.Count} action(s) held back for a walk");
            _later.Clear();
        }

        if (_api.ActionSeconds > 0)
        {
            let.Add(string.Create( System.Globalization.CultureInfo.InvariantCulture, $"{_api.ActionSeconds:F0}s an action said it still needed"));

            _api.ActionSeconds = 0;
        }

        if (_walking.Count > 0)
        {
            let.Add($"{_walking.Count} walk(s) under way");
            StopWalking();
        }

        if (Performing(_api.State.Ego))
        {
            let.Add("a clip the story was playing on the player");
            StopAnimating(ModelNamed(_api.State.Ego)?.Name ?? _api.State.Ego);
        }

        if (_quiet >= 0 && (_scripts?.Count ?? 0) > _quiet)
        {
            let.Add($"{(_scripts?.Count ?? 0) - _quiet} script(s) the room never finished");

            // Reset rather than cleared.
            _quiet = _scripts?.Count ?? 0;
        }

        if (_api.State.ForcedCameraCuts)
        {
            let.Add("the camera a script was holding");
            _api.State.ForcedCameraCuts = false;
        }

        if (_api.State.Inspecting is { Length: > 0 })
        {
            let.Add("a close-up the view was pinned to");
            _api.State.Inspecting = string.Empty;
        }

        _api.State.Talking = false;
        WarpNextWalk = false;

        // And onto ground they can walk on.
        if (_scene.Walkable is { } boundary && Where(_api.State.Ego) is { } standing && !boundary.IsWalkable(standing) &&
            boundary.NearestWalkable(standing) is { } open && ModelNamed(_api.State.Ego) is { } player)
        {
            let.Add(string.Create( System.Globalization.CultureInfo.InvariantCulture,
                $"the player {Vector3.Distance(standing, open):F0} units off the floor"));

            Place( _api.State.Ego, open, Walker.HeadingOf(_geometry.TransformOf(player.Placement)));
        }

        return let;
    }

    /// <summary>Where the view actually is while somebody other than the story is holding it, or null when the story's own answer is.</summary>
    public Camera? Elsewhere { get; set; }

    /// <summary>A shot worked out for the moment rather than named by the scene.</summary>
    private Camera? _staged;

    /// <summary>Which staged shot it is, so replacing one counts as a change of view.</summary>
    private int _stagings;

    /// <summary>Puts a shot nobody authored in front of the cameras the story names, or takes it away.</summary>
    /// <param name="shot">The view, or null to go back to the camera the story named.</param>
    public void Stage(Camera? shot)
    {
        if (shot is null && _staged is null)
        {
            return;
        }

        _staged = shot;
        _stagings++;
    }

    /// <summary>Slow out of one shot and into the next, rather than a constant sweep.</summary>
    private static float Eased(float part) => part * part * (3f - (2f * part));

    /// <summary>Whether the view belongs to the player rather than to the camera the story has named.</summary>
    /// <returns>True when the shot is to be refused and the view left in the player's own eyes.</returns>
    /// <param name="wanted">The camera key <see cref="MoveView"/> is about to move to.</param>
    private bool Theirs(string wanted)
    {
        // Nothing named is nothing to refuse, the view is only theirs on foot, and a close-up is the player's own doing.
        if (wanted.Length == 0 || wanted[0] == ' ' || !_api.State.FirstPerson)
        {
            return false;
        }

        // A shot this port composed itself is refused outright: it exists to stand in for a scene camera that did not hold the pair, and in first
        // person the player's own head holds them perfectly well.
        if (wanted[0] == '')
        {
            return true;
        }

        // GK3 cuts to a room camera to watch the player open a wardrobe and to a dialogue camera for every conversation, and in first person each
        // of those throws them out of their own body for something they are doing with their own hands. Only a cutscene still takes the view: a
        // script that turned the forced cuts on, or one that insisted on this cut rather than merely offering it.
        return !_api.State.ForcedCameraCuts && !_api.State.CameraForced;
    }

    /// <summary>Takes the view wherever the story has put it.</summary>
    private void MoveView(double seconds)
    {
        // What is being looked at closely outranks where the story left the view, and the two are kept apart so that letting go of the first returns.
        string wanted = _api.State.Inspecting is { Length: > 0 } close ? "\u0000" + close : _staged is not null
                ? "\u0001" + _stagings.ToString(CultureInfo.InvariantCulture) : _api.State.CameraAngle;

        // On foot the view is the player's own head, and a shot the story merely names is not taken out of it.
        if (Theirs(wanted))
        {
            _angle = wanted;
            _from = null;
            _to = null;
            Framed = false;

            return;
        }

        if (!string.Equals(wanted, _angle, StringComparison.OrdinalIgnoreCase))
        {
            _angle = wanted;
            _from = Elsewhere ?? View;
            _to = Pointing(wanted);
            _glided = _api.State.CameraGliding && _from is not null ? 0 : GlideSeconds;

            Framed = _to is not null;
        }

        if (_to is null)
        {
            return;
        }

        _glided += seconds;

        if (_from is null || _glided >= GlideSeconds)
        {
            View = Narrowed(_to);
            return;
        }

        // Eased rather than linear.
        float part = Eased((float)(_glided / GlideSeconds));

        View = Narrowed(new Camera
        {
            Position = Vector3.Lerp(_from.Position, _to.Position, part), Target = Vector3.Lerp(_from.Target, _to.Target, part), Up = _to.Up,
            FieldOfView = float.Lerp(_from.FieldOfView, _to.FieldOfView, part), NearPlane = _to.NearPlane, FarPlane = _to.FarPlane,
        });
    }

    /// <summary>Works out the view a camera key describes.</summary>
    private Camera? Pointing(string wanted)
    {
        if (wanted.Length == 0)
        {
            return null;
        }

        if (wanted[0] == '\u0001')
        {
            return _staged;
        }

        if (wanted[0] != '\u0000')
        {
            return SceneLoader.CameraFor(_scene, _geometry, wanted);
        }

        string key = wanted[1..];

        string? model = _scene.Models .FirstOrDefault(m => string.Equals(m.Noun, key, StringComparison.OrdinalIgnoreCase)) ?.Name;

        if (_scene.Definition.AnyCameraNamed(key) is { } named)
        {
            return SceneLoader.CameraAt(named, _geometry);
        }

        if (_scene.Definition.InspectCameraFor(key, model) is { } close)
        {
            return SceneLoader.CameraAt(close, _geometry);
        }

        if (Framing(key) is { } derived)
        {
            return derived;
        }

        Diagnostics.Add(new Diagnostic( "GK3R3204", DiagnosticSeverity.Info,
            "Nothing declares a close-up of this and it has no geometry to frame one from.", _scene.Name, null, "an [INSPECT_CAMERAS] entry", key,
            "Inspect is not offered for it, so the player is not shown a verb that does nothing."));

        return null;
    }

    /// <summary>A close-up worked out from what the thing actually occupies.</summary>
    /// <returns>A camera framing it, or null when the room has no such thing to frame.</returns>
    /// <param name="noun">What is being looked at.</param>
    private Camera? Framing(string noun)
    {
        if (Occupies(noun) is not var (minimum, maximum))
        {
            return null;
        }

        Vector3 centre = (minimum + maximum) * 0.5f;
        float across = MathF.Max((maximum - minimum).Length(), 1f);

        // Far enough that the whole of it sits inside the frame, with a quarter again for air.
        float back = across * 0.5f / MathF.Tan(CloseUpFieldOfView * 0.5f) * 1.25f;

        Vector3 from = View is { } standing ? standing.Position : centre + new Vector3(0, 0, back);
        Vector3 line = from - centre;

        if (line.LengthSquared() < 1f)
        {
            line = View is { } facing ? Vector3.Normalize(facing.Position - facing.Target) : Vector3.UnitZ;
        }

        Vector3 eye = centre + (Vector3.Normalize(line) * back);
        float reach = MathF.Max(1f, (_geometry.Maximum - _geometry.Minimum).Length());

        return new Camera
        {
            Position = eye, Target = centre, Up = Vector3.UnitY, FieldOfView = CloseUpFieldOfView,

            // As far out as the framing allows, for the depth precision the room camera's eight units buy — but no further, because this close-up is.
            NearPlane = MathF.Min(8f, MathF.Max(0.25f, back * 0.25f)), FarPlane = reach * 4f,
        };
    }

    /// <summary>How wide a derived close-up sees, in radians.</summary>
    private const float CloseUpFieldOfView = 40f * MathF.PI / 180f;

    /// <summary>The box a noun's geometry fills, in the room's space.</summary>
    /// <returns>The corners, or null when nothing in the room answers to that noun.</returns>
    private (Vector3 Minimum, Vector3 Maximum)? Occupies(string noun)
    {
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);
        bool found = false;

        foreach (PlacedModel placed in _scene.Models)
        {
            if (!string.Equals(placed.Noun, noun, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(placed.Name, noun, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Matrix4x4 placement = placed.Standing;

            foreach (ModMesh mesh in placed.Model.Meshes)
            {
                Matrix4x4 toWorld = mesh.MeshToLocal * placement;

                // Every corner of the mesh's own box, because a rotated box's extremes are not the transforms of the two corners that described it.
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 at = Vector3.Transform( new Vector3( (corner & 1) == 0 ? mesh.BoundsMin.X : mesh.BoundsMax.X,
                            (corner & 2) == 0 ? mesh.BoundsMin.Y : mesh.BoundsMax.Y, (corner & 4) == 0 ? mesh.BoundsMin.Z : mesh.BoundsMax.Z),
                        toWorld);

                    minimum = Vector3.Min(minimum, at);
                    maximum = Vector3.Max(maximum, at);
                    found = true;
                }
            }
        }

        return found ? (minimum, maximum) : null;
    }

    /// <summary>Whether anything in the room can be looked at closely.</summary>
    /// <returns>True when inspecting it would move the view.</returns>
    /// <param name="noun">What the player is pointing at.</param>
    public bool Inspectable(string noun)
    {
        ArgumentNullException.ThrowIfNull(noun);

        string? model = _scene.Models .FirstOrDefault(m => string.Equals(m.Noun, noun, StringComparison.OrdinalIgnoreCase)) ?.Name;

        return _scene.Definition.AnyCameraNamed(noun) is not null || _scene.Definition.InspectCameraFor(noun, model) is not null ||
               Occupies(noun) is not null;
    }

    /// <summary>Applies whatever field of view a script has asked for.</summary>
    private Camera Narrowed(Camera camera)
    {
        if (_api.State.CameraFieldOfView is not { } wanted || wanted == camera.FieldOfView)
        {
            return camera;
        }

        return new Camera
        {
            Position = camera.Position, Target = camera.Target, Up = camera.Up, FieldOfView = wanted, NearPlane = camera.NearPlane,
            FarPlane = camera.FarPlane,
        };
    }

    /// <summary>Performs an action that has come due.</summary>
    private string Fire(GameTimer timer) => Fire(timer.Noun, timer.Verb);

    /// <summary>Performs an action the room itself asked for.</summary>
    /// <returns>What to report happened.</returns>
    /// <param name="noun">What it is about.</param>
    /// <param name="verb">What is being done to it.</param>
    private string Fire(string noun, string verb)
    {
        if (_actions is null || _runner is null)
        {
            return $"{noun}:{verb} came due and there is nothing here to run it";
        }

        if (_actions.Find(noun, verb) is not { } rule)
        {
            return $"{noun}:{verb} came due and nothing applies to it now";
        }

        ActionOutcome outcome = _runner.Run(rule);

        foreach (Diagnostic diagnostic in _runner.Diagnostics.Items)
        {
            Diagnostics.Add(diagnostic);
        }

        return $"{noun}:{verb} [{rule.Case}] " + (outcome.Ran ? "ran" : "was refused");
    }

    /// <summary>One clip running on one model.</summary>
    private sealed class Cue
    {
        private readonly AnimationSound _sound;
        private readonly double _at;
        private readonly double _period;

        private double _elapsed;

        public Cue(AnimationSound sound, double period, int rate, string owner)
        {
            _sound = sound;
            _at = Math.Max(0, sound.Frame) / (double)Math.Max(1, rate);
            _period = period;
            Owner = owner;
        }

        /// <summary>Which model the cue names, if it names one at all.</summary>
        public string Model => _sound.Model;

        /// <summary>The animation this came out of, so stopping that can stop this.</summary>
        public string Owner { get; }

        /// <summary>Whether it has been played and will not come round again.</summary>
        public bool Finished { get; private set; }

        /// <summary>Advances the clock and says whether the sound is now due.</summary>
        /// <returns>The cue when it is due this frame, and null otherwise.</returns>
        /// <param name="seconds">How long since the last frame.</param>
        public AnimationSound? Step(double seconds)
        {
            if (Finished)
            {
                return null;
            }

            double before = _elapsed;
            _elapsed += seconds;

            if (before > _at || _elapsed < _at)
            {
                return null;
            }

            // A looping animation makes its noise again every time round; anything else makes it once.
            if (_period > 0)
            {
                _elapsed -= _period;
            }
            else
            {
                Finished = true;
            }

            return _sound;
        }
    }

    /// <summary>A model an animation shows or hides, waiting for its frame.</summary>
    private sealed class Footfall
    {
        private readonly AnimationStep _step;
        private readonly double _at;
        private readonly double _period;

        private double _elapsed;

        public Footfall(AnimationStep step, double period, int rate)
        {
            _step = step;
            _at = Math.Max(0, step.Frame) / (double)Math.Max(1, rate);
            _period = period;
        }

        /// <summary>Whether it has landed and will not come round again.</summary>
        public bool Finished { get; private set; }

        /// <summary>Advances the clock and says whether the foot is down this frame.</summary>
        public AnimationStep? Step(double seconds)
        {
            if (Finished)
            {
                return null;
            }

            double before = _elapsed;
            _elapsed += seconds;

            if (before > _at || _elapsed < _at)
            {
                return null;
            }

            if (_period > 0)
            {
                _elapsed -= _period;
            }
            else
            {
                Finished = true;
            }

            return _step;
        }
    }

    /// <summary>A surface an animation is about to repaint.</summary>
    private sealed class Swap
    {
        private readonly AnimationTexture _swap;
        private readonly double _at;
        private readonly double _period;

        private double _elapsed;

        public Swap(AnimationTexture swap, double period, int rate)
        {
            _swap = swap;
            _at = Math.Max(0, swap.Frame) / (double)Math.Max(1, rate);
            _period = period;
            Finished = _period <= 0 && swap.Frame <= 0;
        }

        /// <summary>Whether it has happened and will not come round again.</summary>
        public bool Finished { get; private set; }

        /// <summary>Advances the clock and says whether the swap is now due.</summary>
        public AnimationTexture? Step(double seconds)
        {
            if (Finished)
            {
                return null;
            }

            double before = _elapsed;
            _elapsed += seconds;

            if (before > _at || _elapsed < _at)
            {
                return null;
            }

            if (_period > 0)
            {
                _elapsed -= _period;
            }
            else
            {
                Finished = true;
            }

            return _swap;
        }
    }

    /// <summary>Something an animation does to the room, waiting for its frame.</summary>
    /// <typeparam name="T">What is due.</typeparam>
    private sealed class Scheduled<T> where T : struct
    {
        private readonly T _what;
        private readonly double _at;
        private readonly double _period;

        private double _elapsed;

        public Scheduled(T what, int frame, double period, int rate, string owner)
        {
            _what = what;
            _at = Math.Max(0, frame) / (double)Math.Max(1, rate);
            _period = period;
            Owner = owner;
            Finished = _period <= 0 && frame <= 0;
        }

        /// <summary>The animation that scheduled it.</summary>
        public string Owner { get; }

        /// <summary>Whether it has happened and will not come round again.</summary>
        public bool Finished { get; private set; }

        /// <summary>Advances the clock and says whether it is due this frame.</summary>
        public T? Step(double seconds)
        {
            if (Finished)
            {
                return null;
            }

            double before = _elapsed;
            _elapsed += seconds;

            if (before > _at || _elapsed < _at)
            {
                return null;
            }

            if (_period > 0)
            {
                _elapsed -= _period;
            }
            else
            {
                Finished = true;
            }

            return _what;
        }
    }

    private sealed class Showing
    {
        private readonly AnimationVisibility _change;
        private readonly double _at;
        private readonly double _period;

        private double _elapsed;

        public Showing(AnimationVisibility change, double period, int rate)
        {
            _change = change;
            _at = Math.Max(0, change.Frame) / (double)Math.Max(1, rate);
            _period = period;

            // Frame zero is applied by the caller the moment the animation starts, so this one is already spent and exists only to come round again.
            Finished = _period <= 0 && change.Frame <= 0;
        }

        /// <summary>Whether it has happened and will not come round again.</summary>
        public bool Finished { get; private set; }

        /// <summary>Whether this is about a named model.</summary>
        public bool Concerns(string model) => _change.Model.Equals(model, StringComparison.OrdinalIgnoreCase);

        /// <summary>Advances the clock and says whether the change is now due.</summary>
        public AnimationVisibility? Step(double seconds)
        {
            if (Finished)
            {
                return null;
            }

            double before = _elapsed;
            _elapsed += seconds;

            if (before > _at || _elapsed < _at)
            {
                return null;
            }

            if (_period > 0)
            {
                _elapsed -= _period;
            }
            else
            {
                Finished = true;
            }

            return _change;
        }
    }

    private sealed class Playing
    {
        private readonly Actors.CharacterConfig? _character;
        private readonly bool _repeat;
        private readonly bool _moves;
        private readonly bool _absolute;
        private readonly int _rate;
        private readonly double _delay;
        private readonly Matrix4x4 _correction;
        private readonly Vector3 _opened;

        private double _elapsed;

        public Playing( ActFile clip, PlacedModel target, AnimationAction action, bool repeat, bool moves, Vector3? began, Matrix4x4 standing,
            bool fromBehaviour = false, int rate = AnimationFile.FramesPerSecond, Actors.CharacterConfig? character = null, bool carried = false)
        {
            _character = character;
            Clip = clip;
            Target = target;
            FromBehaviour = fromBehaviour;
            _repeat = repeat;
            _moves = moves;
            _rate = Math.Max(1, rate);
            _delay = action.Frame / (double)_rate;
            _absolute = action.Placement is not null;
            _correction = Correction(clip, target, action.Placement, standing, character, carried);
            _opened = Opens(clip);
            Began = began;
            Carried = began;
        }

        /// <summary>Where the actor stood when this started.</summary>
        public Vector3? Began { get; }

        /// <summary>Where the clip has carried the actor to.</summary>
        public Vector3? Carried { get; private set; }

        /// <summary>Whether the actor gives back the ground the clip covered.</summary>
        public bool Reverts => !_moves && !_absolute;

        public ActFile Clip { get; }

        public PlacedModel Target { get; }

        /// <summary>Whether the model's own behaviour script asked for it.</summary>
        public bool FromBehaviour { get; }

        /// <summary>Where the clip's own space has to be moved to for it to play here.</summary>
        private static Matrix4x4 Correction( ActFile clip, PlacedModel target, AnimationPlacement? placement, Matrix4x4 standing,
            Actors.CharacterConfig? character, bool carried)
        {
            if (placement is { } spot)
            {
                Matrix4x4 authored = Matrix4x4.CreateRotationY(spot.Heading) * Matrix4x4.CreateTranslation(spot.Position);

                return Matrix4x4.Invert(standing, out Matrix4x4 back) ? authored * back : authored;
            }

            // A carried clip is already in the space it is played in — the holder's, which the model is pinned to for as long as the binding lasts —.
            if (carried || target.Kind != PlacedModelKind.Actor)
            {
                return Matrix4x4.Identity;
            }

            // A relative clip plays facing whichever way the actor already faces, and the turn the clip was authored with is taken back out.
            Matrix4x4 turn = Matrix4x4.Identity;

            if (character is { Hips: { } hips, LeftShoe: { } left, RightShoe: { } right } && clip.PoseOf(hips.Mesh, 0) is { } hipPose &&
                clip.PoseOf(left.Mesh, 0) is { } leftPose && clip.PoseOf(right.Mesh, 0) is { } rightPose)
            {
                Vector3 across = rightPose.Translation - leftPose.Translation;
                Vector3 up = hipPose.Translation - leftPose.Translation;
                Vector3 facing = Vector3.Cross(across, up) with { Y = 0 };

                if (facing.LengthSquared() > 1e-6f)
                {
                    // Which way the model is built to face, which is what the placement's rotation assumes it is looking along.
                    float built = target.BuiltFacing ?? MathF.PI;
                    float authored = Navigation.Walker.Heading(facing);

                    turn = Matrix4x4.CreateRotationY(Navigation.Walker.Wrapped(built - authored));
                }
            }

            // Where the clip's opening frame stands the character, once the turn is out of it, against where the model's own rest stands them: the.
            if (character is not null && Actors.Footing.Of(target.Model, character) is { } feet &&
                Actors.AnimationStart.Standing(clip, 0f, repeat: false, character, turn) is { } opened)
            {
                return turn * Matrix4x4.CreateTranslation(feet - opened);
            }

            // No triads to read, so the body cannot be measured and the mesh groups as a whole are what is left.
            Vector3 rest = Average(target.Model.Meshes.Select(m => m.MeshToLocal.Translation));

            Vector3 opens = Average(Enumerable .Range(0, clip.MeshCount) .Select(m => clip.PoseOf(m, 0)) .Where(p => p is not null)
                .Select(p => Vector3.Transform(p!.Value.Translation, turn)));

            return turn * Matrix4x4.CreateTranslation(rest - opens);
        }


        /// <summary>Poses the model for this moment.</summary>
        /// <returns>True while the clip is still running.</returns>
        public bool Step(ISceneSink geometry, float seconds)
        {
            _elapsed += seconds;

            if (_elapsed < _delay)
            {
                return true;
            }

            double running = _elapsed - _delay;
            double frame = running * _rate;

            if (frame >= Clip.FrameCount)
            {
                if (!_repeat)
                {
                    // The last frame first.
                    Pose(geometry, Clip.FrameCount - 1);
                    return false;
                }

                // Back to the top, keeping whatever is left over rather than resetting to zero.
                frame %= Clip.FrameCount;
                _elapsed = _delay + (frame / _rate);
            }

            Pose(geometry, frame);
            return true;
        }

        /// <summary>Poses the model on the clip's opening frame, without running it.</summary>
        /// <param name="geometry">Where the model stands.</param>
        public void Open(ISceneSink geometry) => Pose(geometry, 0);

        /// <summary>Poses the model on the clip's closing frame, without running it.</summary>
        /// <param name="geometry">Where the model stands.</param>
        public void Last(ISceneSink geometry) => Pose(geometry, Math.Max(0, Clip.FrameCount - 1));

        /// <summary>Where in the room the opening pose leaves the model standing.</summary>
        /// <returns>The world position of its mesh groups' average origin.</returns>
        /// <param name="standing">The model's own placement, which is applied on top.</param>
        public Vector3 Settled(Matrix4x4 standing) => Standing(standing, 0f);

        /// <summary>Where the clip has the character's feet on the frame it is on.</summary>
        /// <returns>The spot, in the room.</returns>
        /// <param name="standing">The model's placement.</param>
        public Vector3 Now(Matrix4x4 standing) => Standing(standing, Frame);

        /// <summary>Which way the clip has the character facing on the frame it is on.</summary>
        /// <returns>The heading, or null when the clip does not pose the hips.</returns>
        /// <param name="standing">The model's placement.</param>
        public float? Facing(Matrix4x4 standing) => _character is null ? null : Actors.AnimationStart.FacingAt(
                    Clip, Frame, _repeat, _character, _correction * standing, Target.BuiltFacing);

        private Vector3 Standing(Matrix4x4 standing, float frame)
        {
            Matrix4x4 world = _correction * standing;

            // The hips, for the same reason the running pose uses them: this is a place read out of a clip rather than a distance measured across.
            return (_character is null ? null : Actors.AnimationStart.Standing(Clip, frame, _repeat, _character, world))
                ?? Vector3.Transform(_opened, world);
        }

        /// <summary>Whether the clip says where in the room it happens.</summary>
        public bool Absolute => _absolute;

        /// <summary>The shift settled on, so a held model can follow it.</summary>
        public Matrix4x4 Space => _correction;

        /// <summary>Puts one mesh group where the clip says, in the picture and on the model.</summary>
        private void Put(ISceneSink geometry, int mesh, Matrix4x4 meshToLocal)
        {
            geometry.PoseMesh(Target.Placement, mesh, meshToLocal);
            Target.Pose(mesh, meshToLocal);
        }

        /// <summary>Where the clip's mesh groups sit on its opening frame.</summary>
        private static Vector3 Opens(ActFile clip) => Average(Enumerable .Range(0, clip.MeshCount) .Select(m => clip.PoseOf(m, 0))
            .Where(p => p is not null) .Select(p => p!.Value.Translation));

        /// <summary>The frame the model was last posed on.</summary>
        public float Frame { get; private set; }

        /// <summary>Puts the model into one moment of the clip.</summary>
        /// <param name="geometry">Where the model stands.</param>
        /// <param name="frame">Which frame, with the fraction of the way to the next one.</param>
        private void Pose(ISceneSink geometry, double frame)
        {
            float at = (float)frame;
            Frame = at;

            // Where the clip has carried the model to, in the world's terms rather than the model's.
            if (Began is { } from)
            {
                Vector3 here = Average(Enumerable .Range(0, Clip.MeshCount) .Select(m => Clip.PoseAt(m, at, _repeat)) .Where(p => p is not null)
                    .Select(p => p!.Value.Translation));

                // An absolute clip says where in the room it happens, so where it has got to is a place rather than a distance from wherever the.
                Matrix4x4 world = _correction * geometry.TransformOf(Target.Placement);

                // The hips where the character has them, and the average of the mesh origins only where nothing says.
                Carried = _absolute ? (_character is null ? null : Actors.AnimationStart.Standing(Clip, at, _repeat, _character, world))
                      ?? Vector3.Transform(here, world) : from + Vector3.TransformNormal(here - _opened, Target.Transform);
            }

            for (int mesh = 0; mesh < Clip.MeshCount; mesh++)
            {
                Matrix4x4? pose = Clip.PoseAt(mesh, at, _repeat);

                // A refined head is drawn from geometry the clip has never heard of, so the clip's vertices are read as a motion and applied to the.
                if (Target.Head is { } rig && rig.Mesh == mesh)
                {
                    // Whatever happens, a refined head is never reshaped: the buffer being drawn holds thousands of vertices and the clip has a few.
                    if (_head.Of(Clip, rig, at, _repeat) is { } turn)
                    {
                            if (pose is { } placed)
                        {
                            Put(geometry, mesh, turn * placed * _correction);
                        }
                        else
                        {
                            // No transform track for the head in this clip, so the mesh keeps its own and the fit goes on top of it.
                            geometry.TurnMesh(Target.Placement, mesh, turn);
                        }
                    }
                    else if (pose is { } carried)
                    {
                        Put(geometry, mesh, carried * _correction);
                    }

                    continue;
                }

                if (pose is { } value)
                {
                    Put(geometry, mesh, value * _correction);
                }

                // The shapes, where the clip has them.
                foreach (int submesh in Clip.ShapedSubmeshes(mesh))
                {
                    if (Clip.ShapeAt(mesh, submesh, at, _repeat) is { } shape)
                    {
                        geometry.ShapeMesh(Target.Placement, mesh, submesh, shape);
                    }
                }
            }
        }

        /// <summary>Reads the head's motion out of the clip, once per clip.</summary>
        private readonly Actors.HeadMotion _head = new();
    }

    /// <summary>The middle of a set of points, or the origin when there are none.</summary>
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

    /// <summary>One actor crossing the room, and what to move when they do.</summary>
    private sealed class Walking
    {
        public Walking(PlacedModel placed, Walker walker, WalkCycle? stride = null)
        {
            Placement = placed.Placement;
            Walker = walker;
            Stride = stride;
            Built = placed.BuiltFacing;

            // The placement is scale, then a turn, then a move, so the scale comes back out as the length of a basis vector.
            Scale = new Vector3( placed.Transform.M11, placed.Transform.M12, placed.Transform.M13).Length();

            if (Scale <= 0)
            {
                Scale = 1f;
            }
        }

        public ModelPlacement Placement { get; }

        public Walker Walker { get; }

        /// <summary>The stride to play while they cross, if this character has one.</summary>
        public WalkCycle? Stride { get; }

        public float Scale { get; }

        /// <summary>Which way this model is built to face, when its arrow says.</summary>
        public float? Built { get; }
    }

    /// <summary>A walking character's legs.</summary>
    private sealed class WalkCycle
    {
        private readonly ActFile _clip;
        private readonly PlacedModel _target;
        private readonly Matrix4x4 _rest;
        private readonly float _opens;

        private double _elapsed;

        private readonly int _period;

        /// <summary>The feet the stride puts down, and where it had got to when it was last asked.</summary>
        private readonly IReadOnlyList<AnimationStep> _steps;

        private int _lastFrame = -1;

        private WalkCycle( ActFile clip, PlacedModel target, Matrix4x4 rest, float opens, float pace, IReadOnlyList<AnimationStep> steps)
        {
            _clip = clip;
            _target = target;
            _rest = rest;
            _opens = opens;
            _steps = steps;
            Pace = pace;

            // A stride is authored so that its last frame repeats its first — Gabriel's twenty-first frame is his first, agreeing to two thousandths.
            _period = Closes(clip) ? clip.FrameCount - 1 : clip.FrameCount;
        }

        /// <summary>Which animation this character walks with.</summary>
        private static string? Named( PlacedModel target, Actors.CharacterLibrary? characters, string? replaced) =>
            replaced is { Length: > 0 } ? replaced : characters?.Of(target.Name)?.WalkAnimation;

        /// <summary>Whether the clip's last frame is its first again.</summary>
        private static bool Closes(ActFile clip)
        {
            Vector3 first = Mean(clip, 0);
            Vector3 last = Mean(clip, clip.FrameCount - 1);

            return clip.FrameCount > 2 && MathF.Abs(first.X - last.X) < 0.05f && MathF.Abs(first.Y - last.Y) < 0.05f;
        }

        /// <summary>Where the clip's mesh groups sit on a frame.</summary>
        private static Vector3 Mean(ActFile clip, int frame) => Average(Enumerable .Range(0, clip.MeshCount) .Select(m => clip.PoseOf(m, frame))
            .Where(p => p is not null) .Select(p => p!.Value.Translation));

        /// <summary>Where they sit at a moment between two frames.</summary>
        private static Vector3 MeanAt(ActFile clip, float frame) => Average(Enumerable .Range(0, clip.MeshCount)
            .Select(m => clip.PoseAt(m, frame, cycles: true)) .Where(p => p is not null) .Select(p => p!.Value.Translation));

        /// <summary>How fast the stride carries its owner, in scene units a second.</summary>
        public float Pace { get; }

        /// <summary>How fast to play it, as a multiple of the authored speed.</summary>
        public float Rate { get; set; } = 1f;

        /// <summary>Reads the head's motion out of the stride, once.</summary>
        private readonly Actors.HeadMotion _head = new();

        /// <summary>Finds the stride a character walks with.</summary>
        /// <returns>The cycle, or null when this character has no walk animation here.</returns>
        public static WalkCycle? For( PlacedModel target, Actors.CharacterLibrary? characters, Content.AnimationLibrary? animations,
            Content.ClipLibrary? clips, string? replaced = null)
        {
            if (Named(target, characters, replaced) is not { Length: > 0 } named || animations is null || clips is null)
            {
                return null;
            }

            // CHARACTERS.TXT names an .ANM, which names the .ACT that holds the geometry.
            if (animations.Read(named) is not { } animation)
            {
                return null;
            }

            foreach (AnimationAction action in animation.Actions)
            {
                if (clips.Read(action.Name) is not { } clip || !clip.ModelName.Equals(target.Name, StringComparison.OrdinalIgnoreCase) ||
                    clip.FrameCount < 2 || clip.Duration <= 0)
                {
                    continue;
                }

                float opens = Forward(clip, 0);
                float travel = MathF.Abs(Forward(clip, clip.FrameCount - 1) - opens);

                Matrix4x4 rest = Matrix4x4.CreateTranslation( Average(target.Model.Meshes.Select(m => m.MeshToLocal.Translation)) - Mean(clip, 0));

                return new WalkCycle( clip, target, rest, opens, (float)(travel / clip.Duration), animation.Steps);
            }

            return null;
        }

        /// <summary>Which feet went down on the frame just stepped, if any.</summary>
        public IReadOnlyList<AnimationStep> Landed { get; private set; } = Nothing;

        private static readonly List<AnimationStep> Nothing = [];

        /// <summary>The footstep nodes between the last frame shown and this one.</summary>
        private List<AnimationStep> Feet(int frame)
        {
            if (_steps.Count == 0 || frame == _lastFrame)
            {
                return Nothing;
            }

            List<AnimationStep> fell = [];

            foreach (AnimationStep step in _steps)
            {
                bool inside = _lastFrame < frame ? step.Frame > _lastFrame && step.Frame <= frame : step.Frame > _lastFrame || step.Frame <= frame;

                if (inside)
                {
                    fell.Add(step);
                }
            }

            return fell;
        }

        /// <summary>Poses the model for however long the walk has been going.</summary>
        /// <param name="geometry">Where the poses go.</param>
        /// <param name="seconds">Time since the last frame.</param>
        public void Step(ISceneSink geometry, float seconds)
        {
            _elapsed += Math.Max(0, seconds) * Math.Max(0.01f, Rate);

            // Looped, and seamlessly: with the forward travel removed, the last frame sits exactly where the first does, so the join is invisible.
            float at = (float)(_elapsed * AnimationFile.FramesPerSecond % _period);
            int frame = (int)at;

            Landed = Feet(frame);
            _lastFrame = frame;

            Matrix4x4 correction = Matrix4x4.CreateTranslation(0, 0, _opens - ForwardAt(_clip, at)) * _rest;

            for (int mesh = 0; mesh < _clip.MeshCount; mesh++)
            {
                Matrix4x4? pose = _clip.PoseAt(mesh, at, cycles: true);

                // The head, exactly as Playing.Pose treats it: a refined head is drawn from geometry the clip has never heard of, so the clip's.
                if (_target.Head is { } rig && rig.Mesh == mesh)
                {
                    if (_head.Of(_clip, rig, at, repeat: true) is { } turn)
                    {
                        if (pose is { } placed)
                        {
                            geometry.PoseMesh(_target.Placement, mesh, turn * placed * correction);
                        }
                        else
                        {
                            geometry.TurnMesh(_target.Placement, mesh, turn);
                        }
                    }
                    else if (pose is { } carried)
                    {
                        geometry.PoseMesh(_target.Placement, mesh, carried * correction);
                    }

                    continue;
                }

                if (pose is { } value)
                {
                    geometry.PoseMesh(_target.Placement, mesh, value * correction);
                }

                foreach (int submesh in _clip.ShapedSubmeshes(mesh))
                {
                    if (_clip.ShapeAt(mesh, submesh, at, cycles: true) is { } shape)
                    {
                        geometry.ShapeMesh(_target.Placement, mesh, submesh, shape);
                    }
                }
            }
        }

        /// <summary>How far along the model's forward axis the body sits on a frame.</summary>
        private static float Forward(ActFile clip, int frame) => Mean(clip, frame).Z;

        /// <summary>The same, at a moment between two frames.</summary>
        private static float ForwardAt(ActFile clip, float frame) => MeanAt(clip, frame).Z;
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

            // The placement is a turn about the up axis and then a move, so the way the actor faces can be read straight back out of it — as a.
            _built = placed.BuiltFacing;
            _facing = HeadingOf(placed.Transform);
            _eyes = CharacterHead.PivotOf(placed.Model, head).Y;
        }

        public ModelPlacement Placement { get; }

        public int Head { get; }

        /// <summary>Tells a head where its owner has got to.</summary>
        private readonly float? _built;

        /// <summary>The heading a placement of this model amounts to.</summary>
        public float HeadingOf(Matrix4x4 placement)
        {
            float turned = MathF.Atan2(placement.M31, placement.M33);

            return _built is { } forward ? Navigation.Walker.Wrapped(turned + forward) : Navigation.Walker.Rotation(turned);
        }

        /// <summary>Where the model is standing and which way it faces, this frame.</summary>
        /// <param name="standing">Its position.</param>
        /// <param name="facing">Its heading.</param>
        public void Stands(Vector3 standing, float facing)
        {
            _standing = standing;
            _facing = facing;
        }

        /// <summary>Moves the head towards wherever it is meant to be looking.</summary>
        /// <returns>True when it moved, and the geometry needs telling.</returns>
        public bool Step(Glances glances, float seconds)
        {
            (float yaw, float pitch) = glances.Of(_name) is { } glance ? Glances.Turn(_standing, _facing, _eyes, glance.Point) : (0f, 0f);

            bool quick = glances.Of(_name)?.Quick ?? false;
            float most = quick ? float.MaxValue : SceneUpdate.TurnRate * seconds;

            float wasYaw = _yaw;
            float wasPitch = _pitch;

            _yaw = Toward(_yaw, yaw, most);
            _pitch = Toward(_pitch, pitch, most);

            return MathF.Abs(_yaw - wasYaw) > 1e-5f || MathF.Abs(_pitch - wasPitch) > 1e-5f;
        }

        /// <summary>Where the head is now.</summary>
        public Matrix4x4 Turn() => Matrix4x4.CreateRotationX(-_pitch) * Matrix4x4.CreateRotationY(_yaw);

        private static float Toward(float from, float to, float most)
        {
            float step = to - from;

            return MathF.Abs(step) <= most ? to : from + (MathF.Sign(step) * most);
        }
    }
}

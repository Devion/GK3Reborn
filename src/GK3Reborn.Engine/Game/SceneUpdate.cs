using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>
/// One of the three things a character does when nobody is telling them to do anything.
/// </summary>
/// <remarks>
/// A scene's actor line names a script for each — <c>idle=</c>, <c>talk=</c>,
/// <c>listen=</c> — and which of them is running is decided by who is speaking, unless a
/// script has asked for one by name.
/// </remarks>
public enum FidgetKind
{
    /// <summary>Waiting: breathing, shifting weight, looking about.</summary>
    Idle,

    /// <summary>Speaking: the gestures that go with a line.</summary>
    Talk,

    /// <summary>Being spoken to.</summary>
    Listen,
}

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

    /// <summary>How much faster an actor moves when the player is in a hurry.</summary>
    /// <remarks>
    /// <para>
    /// Gabriel's stride covers 49.9 units in 1.40 seconds — 35.6 units a second — and a
    /// walk plays at the stride's own pace so that his feet and the ground agree. That is
    /// what the game was authored at and it is genuinely slow to sit through when the
    /// player already knows where they are going.
    /// </para>
    /// <para>
    /// So a double-click doubles it, and plays the stride at double speed to match. Not a
    /// separate run animation: only Gabriel has one in the archives — <c>GABERUN</c>, which
    /// belongs to a cutscene — and <c>CHARACTERS.TXT</c> names no run for anybody, so there
    /// is nothing to give the rest of the cast. A stride played faster reads as hurrying;
    /// a stride played at walking speed while the body slides along at twice it does not.
    /// </para>
    /// <para>
    /// A setting rather than a constant: how impatient a double-click is belongs to the
    /// player, not to the game. One means a double-click does nothing but skip the walk's
    /// leisure, which is a legitimate answer for somebody who wants the pace the game was
    /// authored at.
    /// </para>
    /// </remarks>
    public float HurryFactor
    {
        get => _hurryFactor;

        // Clamped where it is set rather than trusted: a pace of zero is an actor who never
        // arrives, and every wait in the game is measured against a walk that ends.
        set => _hurryFactor = float.IsFinite(value) ? Math.Clamp(value, 1f, 4f) : DefaultHurryFactor;
    }

    /// <summary>The pace a double-click asks for unless the player has said otherwise.</summary>
    public const float DefaultHurryFactor = 2f;

    private float _hurryFactor = DefaultHurryFactor;

    private readonly List<Cue> _cues = [];

    private readonly List<Showing> _showings = [];
    private readonly List<Turning> _actors = [];
    private readonly Dictionary<string, Walking> _walking =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PlacedModel> _standing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where each actor logically is, as against where their model is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original keeps these apart and syncs one from the other every frame:
    /// <c>GKActor::OnLateUpdate</c> calls <c>SyncActorToModelPositionAndRotation</c>. So the
    /// model is the authority while something is driving it, and the actor's position is a
    /// follower that walking and scripts read.
    /// </para>
    /// <para>
    /// That settles who owns an actor's position, which walking and animation both wanted
    /// to. Neither: the model does, and whichever of them is driving the model at the time
    /// gets it. Starting an animation stops a walk, so only one ever is.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, Vector3> _logical =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PlacedModel> _models =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Playing> _playing = [];

    /// <summary>
    /// Models whose behaviour script is held while something else animates them.
    /// </summary>
    /// <remarks>
    /// A character has one animation at a time and a script asking for one outranks the
    /// idle they were running. The original pauses the idle rather than stopping it —
    /// <c>GKProp::StartAnimation</c> pauses and <c>OnVertexAnimationStop</c> resumes — so
    /// a character goes back to breathing where they left off once the scripted moment is
    /// over. Without it Gabriel's idle goes on choosing fidgets all the way through the
    /// coffee scene and every one of them fights the scene for his mesh groups.
    /// </remarks>
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
            _logical[placed.Name] = placed.Transform.Translation;

            if (placed.Noun is { Length: > 0 } noun)
            {
                _standing[noun] = placed;
                _logical[noun] = placed.Transform.Translation;
            }
        }
    }

    /// <summary>Where the clips come from, when anything is to be played.</summary>
    /// <remarks>
    /// Optional. Without it <see cref="Play"/> finds nothing and animation calls go on
    /// being recorded, which is what every tool wants and what the launcher wanted until
    /// there was a reader.
    /// </remarks>
    private readonly List<Behaviour> _scenery = [];

    private readonly Dictionary<string, Fidget> _fidgets =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the cosmetic choices are drawn from.
    /// </summary>
    /// <remarks>
    /// Its own generator rather than the story's. <c>GameState.NextRandom</c> counts its
    /// draws into the state hash on purpose — two runs that have drawn a different number
    /// of times should disagree at once — and an idle picks a fidget every couple of
    /// seconds for as long as somebody stands in a room. Drawing those from the story would
    /// make the hash depend on how long the player loitered.
    /// </remarks>
    private readonly Foundation.DeterministicRandom _chance = new(0xA5F1D2C3B4E59687);

    public Content.ClipLibrary? Clips { get; set; }

    /// <summary>Where the animations that name those clips come from.</summary>
    public Content.AnimationLibrary? Animations { get; set; }

    /// <summary>
    /// The faces in the room, when there is anything that can move one.
    /// </summary>
    /// <remarks>
    /// Optional, like the clips. Without it lip sync, blinking and expressions are all
    /// simply absent, and every character wears the bitmap they were modelled with — which
    /// is what a tool wants and what the launcher wanted before there was a compositor.
    /// </remarks>
    public Actors.Faces? Faces { get; set; }

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
    /// <param name="fromBehaviour">
    /// Whether a model's own behaviour script asked for it rather than the story. An idle
    /// gives way to the story and never the other way about: it is dropped where the story
    /// is already animating that model, and it is held while the story does.
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
    public double Play(
        string name, bool repeat = false, bool moves = false, bool fromBehaviour = false)
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

        // The sounds first, before anything that can return. A third of the game's
        // animations move nothing at all and exist to make a noise — a door, a match, a
        // yawn — and the branches below let those go without playing a thing.
        foreach (AnimationSound cue in animation.Sounds)
        {
            _cues.Add(new Cue(cue, repeat ? animation.Duration : 0, animation.Rate));
        }

        // Then what it shows and hides, for the same reason and one of its own: an
        // animation that brings somebody into the room does it here, and the clip that
        // opens the door in front of them is a separate line of the same file. Emilio
        // walking out of the hotel is exactly that, and without this the door swings and
        // makes its noise with nobody behind it.
        foreach (AnimationVisibility change in animation.Visibility)
        {
            _showings.Add(new Showing(change, repeat ? animation.Duration : 0, animation.Rate));
        }

        // Frame zero is now rather than in a frame's time. A visibility change on the
        // opening frame states what is true while the animation runs, so waiting a tick
        // for it shows one frame of the old state — which for a character being brought
        // into the room is one frame of them standing at the origin.
        Reveal(animation.Visibility.Where(v => v.Frame <= 0));

        // Faces next, because an animation that only moves a face moves no geometry at
        // all: ABEANGRY is two frames of eyebrow and nothing else. Asking about the clips
        // first would report a third of the game's expressions as animations that do
        // nothing.
        bool onAFace = (animation.Faces.Count > 0 || animation.Mouths.Count > 0) &&
                       Faces?.Perform(animation) == true;

        if (animation.Actions.Count == 0)
        {
            // A face or a sound is still something happening, and a script that waits on
            // one is waiting for it to finish rather than for nothing.
            if (onAFace || animation.Sounds.Count > 0 || animation.Visibility.Count > 0)
            {
                return animation.Duration;
            }

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

            // An idle never talks over the story. GK3 gives a model one animator, and a
            // behaviour script asking it for a clip while something else is animating it
            // is the request being dropped — GKActor::StartAnimation returns without
            // starting anything. Without this, Gabriel's idle and the coffee scene both
            // pose him, every frame, and he flickers between the two.
            if (fromBehaviour && _playing.Any(p => Drives(p, target) && !p.FromBehaviour))
            {
                continue;
            }

            // And one clip at a time either way: starting one on a model stops whatever
            // that model was doing, which is what VertexAnimator::Start does before it
            // does anything else. Two clips on one model is two answers to where its mesh
            // groups are, decided by whichever was added last.
            // A script left waiting out a clip that is about to be stopped has to be told,
            // or it goes on waiting for something that is no longer playing.
            if (!fromBehaviour &&
                _playing.Any(p => Drives(p, target) && p.FromBehaviour) &&
                BehaviourOf(target.Name) is { } interrupted)
            {
                interrupted.Interrupted = true;
            }

            _playing.RemoveAll(p => Drives(p, target));

            // The move flag is carried but not yet spent. Committing the ground a clip
            // covered means writing the actor's position, and Walker already owns that —
            // the two have to be reconciled before either may write it.
            //
            // The original does this explicitly: starting a vertex animation on a
            // character cancels whatever walk was in progress. It is also what keeps the
            // model to one driver at a time.
            //
            // <b>But not for a clip the model's own behaviour asked for.</b> The original
            // exempts those by name — "we don't want to cancel the turn part of a walk due
            // to a breathing anim", GKActor::StartAnimation — and without the exemption an
            // idle firing mid-stride stopped the walk dead and then, being a clip that
            // gives back the ground it covered, put the walker back where the idle started.
            // That is what a player sees as their character resetting halfway across a room.
            if (!fromBehaviour)
            {
                _walking.Remove(clip.ModelName);
                _walking.Remove(target.Name);
            }

            // Whatever the model does on its own waits until this is over.
            if (!fromBehaviour)
            {
                _held.Add(target.Name);
            }

            _playing.Add(new Playing(
                clip, target, action, repeat, moves, Where(target.Name),
                _geometry.TransformOf(target.Placement), fromBehaviour, animation.Rate));
            longest = Math.Max(
                longest,
                ((double)clip.FrameCount + action.Frame) / Math.Max(1, animation.Rate));
        }

        return longest;
    }

    /// <summary>
    /// What plays a sound an animation asks for, or null when there is no device.
    /// </summary>
    /// <remarks>
    /// A function rather than the audio object, so the world can be tested without one —
    /// the same shape as the clip and animation libraries. It is handed the cue and where
    /// in the room it comes from, and says whether anything was heard.
    /// </remarks>
    public Func<AnimationSound, Vector3?, bool>? Sound { get; set; }

    /// <summary>Starts every behaviour script the scene named.</summary>
    /// <remarks>
    /// <para>
    /// Two kinds run. A <c>gasprop</c> carries one that lasts as long as the scene does —
    /// the lobby's ceiling fans turn because of one. An <b>actor</b> carries three:
    /// <c>idle=</c> for when nobody is telling them to do anything, <c>talk=</c> for while
    /// they are speaking and <c>listen=</c> for while somebody else is. Which of the three
    /// is running is decided every frame by who is talking.
    /// </para>
    /// <para>
    /// Called once the scene is standing and its animation libraries are attached.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Puts everything the scene declared an opening pose for into it.
    /// </summary>
    /// <returns>How many were posed.</returns>
    /// <remarks>
    /// <para>
    /// A SIF line may carry <c>initanim=</c>, and 316 of them across the corpus do. It is
    /// not something that happens — it says where the thing <em>rests</em>. RC1's copy of
    /// the hotel door is placed by <c>Rc1PlaceLbyDoor</c>; Madeline is stood by the van by
    /// <c>MadRc1FigM</c>; Emilio is sat in the lobby by <c>EmlLbyBreathe</c>. So the
    /// opening frame is sampled and the animation is not played, which is what
    /// <c>Animator::Sample(anim, 0)</c> does in the reference implementation.
    /// </para>
    /// <para>
    /// The difference matters most for the ones that carry an absolute placement: playing
    /// them would take seven seconds to arrive at a pose that is meant to be true from the
    /// first frame, with the sounds and the footsteps of a door being opened by nobody.
    /// </para>
    /// <para>
    /// Called once the clip and animation libraries are attached and before the room's
    /// <c>SCENE:ENTER</c> script runs, because that script asks where people are.
    /// </para>
    /// </remarks>
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
            if (model.InitialAnimation is not { Length: > 0 } name ||
                !model.Placement.Exists ||
                Animations.Read(name) is not { } animation)
            {
                continue;
            }

            // Whatever the pose says about what is drawn, before the pose itself — but
            // only about this model, for the same reason the clips below are filtered.
            Reveal(animation.Visibility
                .Where(v => v.Frame <= 0 && Names(model, v.Model)));

            foreach (AnimationAction action in animation.Actions)
            {
                if (Clips.Read(action.Name) is not { } clip ||
                    !_models.TryGetValue(clip.ModelName, out PlacedModel? target))
                {
                    continue;
                }

                // <b>Only the clip belonging to the model that declared the pose.</b> An
                // animation is a schedule for as many models as it likes, and an opening
                // pose is one model's statement about itself: the lobby's black marker
                // opens with GabLbyGetMarker, which is a clip for the marker and a clip
                // for Gabriel picking it up. Sampling both put the player at the front
                // desk before the scene had begun, and then the room's own entry script
                // moved him again — which is what a player sees as their character
                // starting in the wrong place. The reference passes the model's name to
                // Animator::Sample for exactly this.
                if (!ReferenceEquals(target, model))
                {
                    continue;
                }

                var pose = new Playing(
                    clip,
                    target,
                    action with { Frame = 0 },
                    repeat: false,
                    moves: true,
                    Where(target.Name),
                    _geometry.TransformOf(target.Placement));

                pose.Open(_geometry);

                // Where the pose leaves them is where they now are. An opening animation
                // is the only thing that says where several of the game's characters
                // stand, and leaving the position the scene never set would have anything
                // that asks — a walk, a glance, IsActorNear — answer about the origin
                // while the character is sitting in a chair on the other side of the room.
                if (target.Kind == PlacedModelKind.Actor)
                {
                    Vector3 settled = pose.Settled(_geometry.TransformOf(target.Placement));

                    Follow(target.Name, settled);
                    _posed.Add((target.Noun ?? target.Name, settled));
                }

                posed++;
            }
        }

        return posed;
    }

    /// <summary>Who an opening pose moved, and where to, for whoever wants to say so.</summary>
    public IReadOnlyList<(string Who, Vector3 Where)> Posed => _posed;

    private readonly List<(string Who, Vector3 Where)> _posed = [];

    /// <summary>Gives a character a different script for one of the three things they do.</summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="mode">Which of the three.</param>
    /// <param name="script">The script, or null to leave them with nothing to do.</param>
    /// <returns>True when the room has such a character.</returns>
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

        // Started rather than merely stored: a script that hands somebody a new idle means
        // it to take effect, and the one they were running belongs to whatever they were
        // doing before.
        _fidgets[model.Name] = new Fidget(model);
        return true;
    }

    /// <summary>Stops a character fidgeting, or everybody.</summary>
    /// <param name="actor">Their name, or null for everyone in the room.</param>
    public void StopFidget(string? actor = null)
    {
        if (actor is not { Length: > 0 })
        {
            foreach (Fidget fidget in _fidgets.Values)
            {
                fidget.Stopped = true;
            }

            return;
        }

        if (ModelNamed(actor) is { } model && _fidgets.TryGetValue(model.Name, out Fidget? one))
        {
            one.Stopped = true;
        }
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

        Fidget fidget = _fidgets.TryGetValue(model.Name, out Fidget? known)
            ? known
            : _fidgets[model.Name] = new Fidget(model);

        fidget.Stopped = false;
        fidget.Forced = mode;
        fidget.Enter(mode, model);
    }

    /// <summary>Told who is speaking, so that talking and listening can be told apart.</summary>
    /// <remarks>
    /// Set by the launcher from the faces, which know because the line being spoken names
    /// its own actor. Null leaves everybody idling, which is what a room with no audio
    /// should look like.
    /// </remarks>
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
            // Told to stand still, standing still because the story is animating them, or
            // busy walking. All three are a pause rather than a stop: the script is left
            // where it is and goes on from there afterwards, which is what the original
            // does — Walker::OnWalkToFinished starts the idle again when the walk ends.
            //
            // Walking is on this list because an idle and a walk are two answers to where
            // a character's feet are. The reference keeps them apart by playing the walk
            // through the same animator, so a fidget cannot be in the middle of one; here
            // the stride is its own thing, and letting a fidget pose the same model at the
            // same time is the two of them writing over each other every frame.
            if (fidget.Stopped || _held.Contains(fidget.Model.Name) || Crossing(fidget.Model))
            {
                continue;
            }

            // Who is talking decides which of the three scripts a character runs. A named
            // fidget — StartTalkFidget and its relatives — overrides that until something
            // sets them idling again, because the script asking for one means it.
            FidgetKind wanted = fidget.Forced ?? (speaker is null
                ? FidgetKind.Idle
                : Same(fidget.Model, speaker) ? FidgetKind.Talk : FidgetKind.Listen);

            if (wanted != fidget.Mode)
            {
                fidget.Enter(wanted, fidget.Model);
            }

            if (fidget.Running is { } behaviour)
            {
                Step(behaviour, seconds);
            }
        }
    }

    /// <summary>Whether a name is one of a model's own.</summary>
    private static bool Names(PlacedModel model, string name) =>
        model.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (model.Noun is { Length: > 0 } noun && noun.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a model is walking somewhere, under either of its names.</summary>
    /// <remarks>
    /// A walk is filed under whichever name the caller used — a script says
    /// <c>WalkTo("Gabriel", ...)</c> and an action's approach says <c>gab</c> — so asking
    /// about one of the two answers "no" about half the walks in the game.
    /// </remarks>
    private bool Crossing(PlacedModel model) =>
        _walking.ContainsKey(model.Name) ||
        (model.Noun is { Length: > 0 } noun && _walking.ContainsKey(noun));

    /// <summary>Whether a name is one of a model's two.</summary>
    private static bool Same(PlacedModel model, string name) =>
        model.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (model.Noun is { Length: > 0 } noun &&
         noun.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs one behaviour script forward by however much time has passed.
    /// </summary>
    /// <remarks>
    /// An animation is played and the script waits out its length before going on, which is
    /// what makes a fan turn continuously rather than restarting every frame. Sixteen
    /// instructions at once bounds a script that loops without ever waiting; the corpus has
    /// several, and without the bound one of them spins here for ever.
    /// </remarks>
    private void Step(Behaviour running, double seconds)
    {
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
                // A script that is one animation and a jump back to it is a thing that
                // simply turns: a fan, a fountain, a clock. Those are looped by the clip
                // rather than by restarting the script, so the last recorded pose runs into
                // the first instead of being held for the fifteenth of a second the script
                // would take to come round. Started once and left.
                case GasAction.Animate when step.Name is { Length: > 0 } spun &&
                                            running.Script.Continuous:
                    Play(spun, repeat: true, fromBehaviour: true);
                    running.Remaining = double.MaxValue;
                    break;

                case GasAction.Animate when step.Name is { Length: > 0 } clip:
                    if (Draws(step.Chance))
                    {
                        running.Remaining += Math.Max(
                            Play(clip, step.Repeats, fromBehaviour: true), 1.0 / 60);
                    }

                    break;

                // A run of these is one choice, not several. Reading them as separate
                // instructions plays every fidget a character has, in order, for ever.
                case GasAction.OneOf:
                    running.Position--;
                    Choose(running);
                    break;

                case GasAction.Wait when Draws(step.Chance):
                    running.Remaining += step.To > step.Seconds
                        ? step.Seconds + (_chance.NextDouble() * (step.To - step.Seconds))
                        : step.Seconds;

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
                    running.Registers[counted] =
                        running.Registers.GetValueOrDefault(counted) + 1;

                    break;

                case GasAction.If when step.Name is { Length: > 0 } tested &&
                                       step.Other is { Length: > 0 } target:
                    if (Holds(running.Registers.GetValueOrDefault(tested), step))
                    {
                        running.Position = running.Script.LabelAt(target) ?? running.Position;
                    }

                    break;

                // A character handing themselves a different idle. The script is replaced
                // and started from the top, which is what the instruction means.
                case GasAction.NewIdle when step.Name is { Length: > 0 } named:
                    Rescript(running, named);
                    break;

                // Walking and looking go through the room's own hooks rather than being
                // done again here: the route, the stride and the head-turn limits all
                // belong to whatever the scene attached, and a second implementation of any
                // of them would be a second answer.
                case GasAction.WalkTo when step.Name is { Length: > 0 } spot &&
                                           running.Owner is { } walker:
                    running.Remaining += Send(walker.Name, spot);
                    break;

                case GasAction.ChooseWalk when step.Names is { Count: > 0 } spots &&
                                               running.Owner is { } wanderer:
                    running.Remaining += Send(
                        wanderer.Name, spots[_chance.NextInt32(0, spots.Count)]);

                    break;

                case GasAction.LookAt when step.Name is { Length: > 0 } at &&
                                           running.Owner is { } looker:
                    _api.Invoke(
                        "LookitModel",
                        [Sheep.SheepValue.FromString(looker.Name), Sheep.SheepValue.FromString(at)]);

                    break;

                // Everything else is parsed and not run: the perception layer, which adds
                // ways for a script to be interrupted rather than deciding what it does.
                default:
                    break;
            }
        }
    }

    /// <summary>Takes one of the choices in the run starting where the script is.</summary>
    /// <remarks>
    /// Weighted, and the weights do not have to add to anything: the corpus writes
    /// <c>100, 100, 50, 50</c> and means a half chance each of the first two. The whole run
    /// is stepped over afterwards, however long it is, because it was one decision.
    /// </remarks>
    private void Choose(Behaviour running)
    {
        int first = running.Position;
        int last = first;
        int total = 0;

        while (last < running.Script.Steps.Count &&
               running.Script.Steps[last].Action == GasAction.OneOf)
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
                running.Remaining += Math.Max(
                    Play(chosen, fromBehaviour: true), 1.0 / 60);
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
    /// <remarks>
    /// Only <c>NEWIDLE</c> needs it, and only a caller with the archives can answer. Null
    /// leaves the character running what they were running, which is what a tool wants.
    /// </remarks>
    public Func<string, Formats.Animation.GasFile?>? Behaviours { get; set; }

    /// <summary>Sends somebody to a named spot, and says how long it takes.</summary>
    private double Send(string actor, string spot) =>
        _api.Walks?.Invoke(actor, spot, Approaching.Walk, false) ?? 0;

    /// <summary>Whether something with a percentage chance happens this time.</summary>
    private bool Draws(int chance) =>
        chance is <= 0 or >= 100 || _chance.NextInt32(0, 100) < chance;

    /// <summary>Whether a register compares as the instruction says.</summary>
    private static bool Holds(int value, GasStep step) => step.Comparison switch
    {
        "=" or "==" => value == step.Value,
        "!=" or "<>" => value != step.Value,
        ">" => value > step.Value,
        "<" => value < step.Value,
        ">=" => value >= step.Value,
        "<=" => value <= step.Value,
        _ => false,
    };

    /// <summary>How many scenery scripts are running.</summary>
    public int Scenic => _scenery.Count;

    /// <summary>How many characters have something to do when nobody is asking.</summary>
    public int Fidgeting => _fidgets.Count;

    /// <summary>Finds a model the room places, by either of its names.</summary>
    /// <param name="name">Its model name or the noun the scene gives it.</param>
    /// <returns>The model, or null when the room has nothing by that name.</returns>
    public PlacedModel? ModelNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_models.TryGetValue(name, out PlacedModel? model))
        {
            return model;
        }

        foreach (PlacedModel placed in _scene.Models)
        {
            if (placed.Placement.Exists &&
                placed.Noun is { Length: > 0 } noun &&
                noun.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return placed;
            }
        }

        return null;
    }

    /// <summary>Draws a model, or stops drawing it.</summary>
    /// <param name="model">The model.</param>
    /// <param name="visible">Whether it is drawn.</param>
    /// <remarks>
    /// Both halves matter: the geometry stops drawing it, and the model itself remembers,
    /// so that the picker does not go on offering a noun for something nobody can see.
    /// </remarks>
    public void Show(PlacedModel model, bool visible)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Visible = visible;
        _geometry.SetVisible(model.Placement, visible);
    }

    /// <summary>
    /// Applies an animation's visibility changes.
    /// </summary>
    /// <param name="changes">The changes due now.</param>
    /// <remarks>
    /// <para>
    /// A model the room does not have is skipped in silence. Animations are shared between
    /// rooms and an <c>[MVISIBILITY]</c> line naming something that is not here is as
    /// ordinary as an <c>[ACTIONS]</c> line doing the same.
    /// </para>
    /// <para>
    /// The per-part form — <c>&lt;frame&gt;,&lt;model&gt;,&lt;mesh&gt;,&lt;submesh&gt;,on</c> —
    /// is not distinguished from the whole-model form here: the sink draws a placement, not
    /// a submesh of one, and hiding the whole thing is the closer of the two answers. Only
    /// a handful of the corpus's lines use it.
    /// </para>
    /// </remarks>
    private void Reveal(IEnumerable<AnimationVisibility> changes)
    {
        foreach (AnimationVisibility change in changes)
        {
            if (ModelNamed(change.Model) is { } model)
            {
                Show(model, change.Visible);
            }
        }
    }

    /// <summary>Whether a clip that is playing is the one animating a model.</summary>
    private static bool Drives(Playing playing, PlacedModel model) =>
        ReferenceEquals(playing.Target, model) ||
        playing.Target.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops everything a model is doing.</summary>
    /// <param name="model">Its name, or null for everything in the room.</param>
    public void StopAnimating(string? model = null)
    {
        if (model is not { Length: > 0 })
        {
            _playing.Clear();
            _held.Clear();
            _showings.Clear();
            return;
        }

        _playing.RemoveAll(p =>
            p.Clip.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase));

        // And whatever it was about to be shown or hidden by. A clip that is stopped
        // half-way should not still turn its model off four seconds later.
        _showings.RemoveAll(v => v.Concerns(model));

        // Whatever it does on its own is its own again. A hold outliving the clip that
        // asked for it leaves a character standing perfectly still for the rest of the
        // scene.
        Release(model);
    }

    /// <summary>Gives a model back to its own script, once nothing else is animating it.</summary>
    private void Release(string model)
    {
        if (_playing.Any(p =>
                !p.FromBehaviour &&
                p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
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
    /// <remarks>
    /// A prop carries one script for as long as the scene stands; a character carries
    /// three and runs whichever suits who is talking. Both end up as the same thing here.
    /// </remarks>
    private Behaviour? BehaviourOf(string model)
    {
        if (_fidgets.TryGetValue(model, out Fidget? fidget))
        {
            return fidget.Running;
        }

        foreach (Behaviour running in _scenery)
        {
            if (running.Owner is { } owner &&
                owner.Name.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                return running;
            }
        }

        return null;
    }

    /// <summary>Who the game's characters are, and how each of them walks.</summary>
    /// <remarks>
    /// Null leaves everybody sliding: an actor with no stride still crosses the room, in
    /// whatever pose they were standing in. That is what a partial set has to do — the
    /// walk is read from <c>CHARACTERS.TXT</c>, and not every model in the game is in it.
    /// </remarks>
    public Actors.CharacterLibrary? Characters { get; set; }

    /// <summary>How many actors are crossing the room.</summary>
    public int OnTheMove => _walking.Count;

    /// <summary>Where an actor is now, if the scene has one by that name.</summary>
    /// <param name="actor">The actor's model name.</param>
    /// <returns>Their position, or null.</returns>
    public Vector3? Where(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _logical.TryGetValue(actor, out Vector3 where) ? where : null;
    }

    /// <summary>Where an actor is walking to, if they are walking anywhere.</summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <returns>The end of their route, or null when they are standing still.</returns>
    /// <remarks>
    /// The end of the route rather than what was asked for: a walk the boundary cannot
    /// complete stops as near as it can, and a script asking whether somebody is on their
    /// way somewhere means where they will actually arrive.
    /// </remarks>
    public Vector3? Heading(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (_walking.TryGetValue(actor, out Walking? walking))
        {
            return walking.Walker.Destination;
        }

        // Under the other name. A scene places `eml` and calls him EMILIO, and a script
        // uses whichever it feels like.
        return _standing.TryGetValue(actor, out PlacedModel? placed) &&
               _walking.TryGetValue(placed.Name, out Walking? theirs)
            ? theirs.Walker.Destination
            : null;
    }

    /// <summary>
    /// Sets an actor walking to a place on the floor.
    /// </summary>
    /// <param name="actor">Their model name.</param>
    /// <param name="destination">Where to go, in world space.</param>
    /// <param name="arriveFacing">
    /// Which way to face on arrival, in radians, or null to keep the direction of travel.
    /// </param>
    /// <param name="arriveLookingAt">What to be looking at on arrival, if not a fixed heading.</param>
    /// <param name="hurry">
    /// Whether to go at <see cref="HurryFactor"/> times the usual pace. The player's own
    /// impatience and nothing else: a script's timings are written against the pace the
    /// game walks at.
    /// </param>
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
    public double Walk(
        string actor,
        Vector3 destination,
        float? arriveFacing = null,
        Vector3? arriveLookingAt = null,
        bool hurry = false)
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
            : Walker.HeadingOf(placed.Transform);

        WalkRoute route = _scene.Walkable is { } boundary
            ? WalkPath.Find(boundary, from, destination)

            // No boundary is no obstacles, so the straight line is the route.
            : new WalkRoute(true, [destination]);

        // The stride first, because its pace is what the walk is measured at.
        WalkCycle? stride = WalkCycle.For(placed, Characters, Animations, Clips);
        float rate = hurry ? HurryFactor : 1f;

        if (stride is not null)
        {
            stride.Rate = rate;
        }

        var walker = new Walker(
            actor,
            route,
            Standing(from),
            facing,
            arriveFacing,
            arriveLookingAt,

            // Both multiplied by the same number, which is the whole point: the ground
            // covered and the feet covering it have to stay in agreement, and an actor with
            // no stride at all still has to get there faster.
            (stride?.Pace ?? Walker.Speed) * rate)
        {
            // The room's floor, not the actor's. Held as a hook rather than looked up
            // inside the walker so that a scene which names no floor object costs nothing
            // and behaves exactly as it did before.
            Ground = _scene.Ground is { } ground ? ground.Height : null,
        };

        if (!walker.Walking)
        {
            _walking.Remove(actor);
            return 0;
        }

        // Whatever they were doing, they are walking now. Without this a character keeps
        // the pose of the clip that was playing and slides across the room in it.
        StopAnimating(placed.Name);

        _walking[actor] = new Walking(placed, walker, stride);
        return walker.Seconds;
    }

    /// <summary>Drops a point onto the room's floor, when the room has one.</summary>
    /// <remarks>
    /// The point's own height goes in and decides which storey is meant, so a spot authored
    /// on the gallery stays on the gallery and one authored at zero in a room whose floor is
    /// at zero does not move at all.
    /// </remarks>
    private Vector3 Standing(Vector3 at) =>
        _scene.Ground?.Height(at) is { } height ? new Vector3(at.X, height, at.Z) : at;

    /// <summary>Writes an actor's logical position, under every name they answer to.</summary>
    /// <remarks>
    /// A scene places <c>gab</c> and calls him GABRIEL, and either name may be asked for.
    /// </remarks>
    private void Follow(string actor, Vector3? position)
    {
        if (position is not { } where || !_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            return;
        }

        _logical[placed.Name] = where;

        if (placed.Noun is { Length: > 0 } noun)
        {
            _logical[noun] = where;
        }
    }

    /// <summary>
    /// Stands an actor at a spot outright, without walking them there.
    /// </summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="position">Where to stand them, in world space.</param>
    /// <param name="heading">Which way to face, as the game's data measures a heading.</param>
    /// <returns>True when there was somebody of that name to move.</returns>
    /// <remarks>
    /// This is how a room decides where the player is standing when they walk into it.
    /// A scene places its actors at whatever its <c>[ACTORS]</c> section says — usually
    /// <c>START</c> — and then its <c>SCENE:ENTER</c> action moves the player to the spot
    /// that matches the door they came through. Without it, every arrival is the front door.
    /// </remarks>
    public bool Place(string actor, Vector3 position, float heading)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            return false;
        }

        // A scene's spots carry a height, but not always the floor's: several are authored
        // at zero and rely on the room being flat there. Dropping onto the floor first
        // means an arrival never starts a walk from the wrong storey, which is the one
        // mistake the height query cannot recover from later.
        position = Standing(position);

        // Whatever they were doing, they are standing here now.
        _walking.Remove(actor);
        _walking.Remove(placed.Name);
        StopAnimating(placed.Name);

        // The placement is scale, then a turn, then a move, and the scale has to survive.
        float scale = new Vector3(
            placed.Transform.M11, placed.Transform.M12, placed.Transform.M13).Length();

        _geometry.MoveModel(
            placed.Placement,
            Matrix4x4.CreateScale(scale <= 0 ? 1f : scale) *
            Matrix4x4.CreateRotationY(Navigation.Walker.Rotation(heading)) *
            Matrix4x4.CreateTranslation(position));

        Follow(actor, position);

        foreach (Turning turning in _actors)
        {
            turning.MovedTo(actor, position, heading);
        }

        return true;
    }

    /// <summary>
    /// Turns an actor on the spot to face something.
    /// </summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="target">What to face, in world space.</param>
    /// <returns>How long the turn will take, or zero when there is nobody to turn.</returns>
    /// <remarks>
    /// 394 of the corpus's approaches are <c>TurnToModel</c>, which means turn where you
    /// stand rather than go anywhere. Walking to the thing instead puts the actor on top of
    /// whatever they were meant to be looking at.
    /// </remarks>
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
    /// <remarks>For leaving the room, where a walk in progress has nowhere to arrive.</remarks>
    public void StopWalking()
    {
        foreach (Walking walking in _walking.Values)
        {
            walking.Walker.Stop();
        }

        _walking.Clear();
    }

    private readonly List<(double Remaining, Action Work)> _later = [];

    /// <summary>How many things are waiting to happen.</summary>
    public int Later => _later.Count;

    /// <summary>
    /// Holds something back for a while.
    /// </summary>
    /// <param name="seconds">How long to hold it.</param>
    /// <param name="work">What to do then.</param>
    /// <returns>True when it was taken, false when there was nothing to wait for.</returns>
    /// <remarks>
    /// <para>
    /// What makes an action's <c>approach</c> mean anything. The original walks the player
    /// to the thing and <em>then</em> runs the action's script: <c>BUTHANE, TALK,
    /// approach=WalkTo, target=TALK_BUTHANE</c> means go and stand there before saying a
    /// word. Running the script straight away is what made Gabriel talk to somebody from
    /// across the square, and open a door from the far side of the room.
    /// </para>
    /// <para>
    /// A delay of nothing is refused rather than queued, so an action with no approach —
    /// or one whose walk found nowhere to go — still runs in the frame it was asked for.
    /// </para>
    /// </remarks>
    public bool After(double seconds, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (seconds <= 0)
        {
            return false;
        }

        _later.Add((seconds, work));
        return true;
    }

    /// <summary>Forgets everything that was waiting to happen.</summary>
    /// <remarks>
    /// For leaving the room. What is queued is nearly always an action script belonging to
    /// the room being left, and letting one run into the next room is how a door opens
    /// twice.
    /// </remarks>
    public void Cancel() => _later.Clear();

    /// <summary>Runs whatever has waited long enough.</summary>
    private void StepLater(double seconds, List<string> happened)
    {
        for (int i = _later.Count - 1; i >= 0; i--)
        {
            (double remaining, Action work) = _later[i];
            remaining -= seconds;

            if (remaining > 0)
            {
                _later[i] = (remaining, work);
                continue;
            }

            _later.RemoveAt(i);

            try
            {
                work();
            }
            catch (Formats.FormatParseException ex)
            {
                Diagnostics.Add(ex.Diagnostic);
                happened.Add("an action held back for a walk could not be run");
            }
        }
    }

    /// <summary>Diagnostics raised while the world went on by itself.</summary>
    /// <summary>One scenery script, and where it has got to.</summary>
    /// <summary>One behaviour script, and where it has got to.</summary>
    private sealed class Behaviour(Formats.Animation.GasFile script, PlacedModel? owner)
    {
        /// <summary>The script. Settable, because <c>NEWIDLE</c> replaces it.</summary>
        public Formats.Animation.GasFile Script { get; set; } = script;

        /// <summary>What it drives, or null when it drives nothing in particular.</summary>
        public PlacedModel? Owner { get; } = owner;

        public int Position { get; set; }

        /// <summary>Seconds until the next step.</summary>
        public double Remaining { get; set; }

        /// <summary>Whether the clip it was waiting out was stopped for something else.</summary>
        /// <remarks>
        /// A script that has asked for an animation waits out its length before going on,
        /// and a continuous one — a fan — waits for ever. Either way the wait is now
        /// counting down something that is not playing any more, so the script carries on
        /// as soon as it has its model back. The original reaches the same place: stopping
        /// the animation asks the player for its next node, and a paused player runs that
        /// the moment it resumes.
        /// </remarks>
        public bool Interrupted { get; set; }

        /// <summary>The language's whole state: one integer per name.</summary>
        public Dictionary<string, int> Registers { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether it says to start again, rather than stopping at the end.</summary>
        public bool Repeats =>
            Script.Steps.Any(s => s.Action is Formats.Animation.GasAction.Loop
                                            or Formats.Animation.GasAction.Goto);
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
                FidgetKind.Talk => owner.Talk ?? owner.Idle,
                FidgetKind.Listen => owner.Listen ?? owner.Idle,
                _ => owner.Idle,
            };

            Running = script is { Steps.Count: > 0 } ? new Behaviour(script, owner) : null;
        }
    }

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

        StepBehaviours(seconds);
        MoveView(seconds);

        // Faces before anything that moves anybody: what a face is doing depends on the
        // clock and not on where its owner is standing, and a mouth that is a frame behind
        // the words is the one thing lip sync must never be.
        Faces?.Advance(seconds);

        // Anything that was waiting for the player to get somewhere. Before the timers, so
        // that an action which sets one is not a frame late in doing it.
        StepLater(seconds, happened);

        foreach (GameTimer timer in _api.State.Timers.Advance(seconds))
        {
            happened.Add(Fire(timer));
        }

        // The noises an animation makes, at the frames it says. Before the clips only so
        // that a sound and the pose it belongs to land in the same frame.
        for (int i = _cues.Count - 1; i >= 0; i--)
        {
            if (_cues[i].Step(seconds) is not { } due)
            {
                continue;
            }

            // Where it comes from: the model the cue names, if the room has it standing
            // somewhere. Everything else is played at the listener.
            Vector3? at = due.Model.Length > 0 ? Where(due.Model) : null;

            if (Sound?.Invoke(due, at) == false)
            {
                Diagnostics.Add(new Diagnostic(
                    "GK3R3316", DiagnosticSeverity.Info,
                    "An animation asks for a sound the archives do not have.",
                    _scene.Name, null, "a .WAV of that name", due.Name,
                    "Common in the corpus: some cues name sounds that were cut."));
            }

            if (_cues[i].Finished)
            {
                _cues.RemoveAt(i);
            }
        }

        // What an animation shows and hides as it runs, on the frames it names. Same
        // clock as the sounds and for the same reason: a character is brought into the
        // room by one of these and the door in front of them by a sound cue, and the two
        // have to land together.
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

        // Animation before walking: a clip poses a model's meshes in the model's own space
        // and walking moves the model, so doing it the other way round would apply this
        // frame's poses to last frame's position.
        for (int i = _playing.Count - 1; i >= 0; i--)
        {
            Playing playing = _playing[i];
            bool running = playing.Step(_geometry, (float)seconds);

            // The actor's position follows the model, every frame, as the original syncs
            // it in LateUpdate.
            Follow(playing.Target.Name, playing.Carried);

            if (!running)
            {
                // A non-move animation puts the actor back where it found them: the pose
                // stays, the ground does not count. A move animation keeps it.
                if (playing.Reverts)
                {
                    Follow(playing.Target.Name, playing.Began);
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

            // The legs, in the model's own space, on top of wherever the model now is.
            walking.Stride?.Step(_geometry, (float)seconds);

            Follow(who, walking.Walker.Position);

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
        // What is being looked at closely outranks where the story left the view, and the
        // two are kept apart so that letting go of the first returns to the second.
        string wanted = _api.State.Inspecting is { Length: > 0 } close
            ? "\u0000" + close
            : _api.State.CameraAngle;

        if (!string.Equals(wanted, _angle, StringComparison.OrdinalIgnoreCase))
        {
            _angle = wanted;
            _from = View;
            _to = Pointing(wanted);
            _glided = _api.State.CameraGliding && _from is not null ? 0 : GlideSeconds;
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

        float part = (float)(_glided / GlideSeconds);

        View = Narrowed(new Camera
        {
            Position = Vector3.Lerp(_from.Position, _to.Position, part),
            Target = Vector3.Lerp(_from.Target, _to.Target, part),
            Up = _to.Up,
            FieldOfView = float.Lerp(_from.FieldOfView, _to.FieldOfView, part),
            NearPlane = _to.NearPlane,
            FarPlane = _to.FarPlane,
        });
    }

    /// <summary>
    /// Works out the view a camera key describes.
    /// </summary>
    /// <remarks>
    /// A close-up is looked for by three names in turn, which is the original's order: a
    /// camera the scene actually names — which is what <c>InspectModelUsingAngle</c> hands
    /// over — then the noun in the <c>[INSPECT_CAMERAS]</c> section, then the model
    /// standing behind that noun, because several rooms frame a thing only under the name
    /// of the mesh drawn there.
    /// </remarks>
    private Camera? Pointing(string wanted)
    {
        if (wanted.Length == 0)
        {
            return null;
        }

        if (wanted[0] != '\u0000')
        {
            return SceneLoader.CameraFor(_scene, _geometry, wanted);
        }

        string key = wanted[1..];

        string? model = _scene.Models
            .FirstOrDefault(m => string.Equals(m.Noun, key, StringComparison.OrdinalIgnoreCase))
            ?.Name;

        if (_scene.Definition.AnyCameraNamed(key) is { } named)
        {
            return SceneLoader.CameraAt(named, _geometry);
        }

        if (_scene.Definition.InspectCameraFor(key, model) is { } close)
        {
            return SceneLoader.CameraAt(close, _geometry);
        }

        Diagnostics.Add(new Diagnostic(
            "GK3R3204", DiagnosticSeverity.Info,
            "Nothing declares a close-up of this, so the view stays where it was.",
            _scene.Name, null, "an [INSPECT_CAMERAS] entry", key,
            "The original works one out from the object's bounds; this does not yet."));

        return null;
    }

    /// <summary>Applies whatever field of view a script has asked for.</summary>
    /// <remarks>
    /// A story override rather than a camera's own: the scene files set one per camera and
    /// this is a script narrowing the view for a moment on top of that. Nothing asking
    /// leaves the camera exactly as the scene framed it.
    /// </remarks>
    private Camera Narrowed(Camera camera)
    {
        if (_api.State.CameraFieldOfView is not { } wanted || wanted == camera.FieldOfView)
        {
            return camera;
        }

        return new Camera
        {
            Position = camera.Position,
            Target = camera.Target,
            Up = camera.Up,
            FieldOfView = wanted,
            NearPlane = camera.NearPlane,
            FarPlane = camera.FarPlane,
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
    /// <summary>
    /// A sound an animation asked for, waiting for its frame.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Playing"/> because a sound is not a pose: it belongs to
    /// the animation rather than to a model, it is played once rather than stepped, and an
    /// animation that moves nothing at all still has sounds to make.
    /// </remarks>
    private sealed class Cue
    {
        private readonly AnimationSound _sound;
        private readonly double _at;
        private readonly double _period;

        private double _elapsed;

        public Cue(AnimationSound sound, double period, int rate)
        {
            _sound = sound;
            _at = Math.Max(0, sound.Frame) / (double)Math.Max(1, rate);
            _period = period;
        }

        /// <summary>Whether it has been played and will not come round again.</summary>
        public bool Finished { get; private set; }

        /// <summary>Advances the clock and says whether the sound is now due.</summary>
        /// <param name="seconds">How long since the last frame.</param>
        /// <returns>The cue when it is due this frame, and null otherwise.</returns>
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

            // A looping animation makes its noise again every time round; anything else
            // makes it once.
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

    /// <summary>
    /// A model an animation shows or hides, waiting for its frame.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="Cue"/> and for the same reason: it belongs to the
    /// animation rather than to any one clip, and an animation whose whole content is an
    /// <c>[MVISIBILITY]</c> line still has something to do.
    /// </remarks>
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

            // Frame zero is applied by the caller the moment the animation starts, so
            // this one is already spent and exists only to come round again on a loop.
            Finished = _period <= 0 && change.Frame <= 0;
        }

        /// <summary>Whether it has happened and will not come round again.</summary>
        public bool Finished { get; private set; }

        /// <summary>Whether this is about a named model.</summary>
        public bool Concerns(string model) =>
            _change.Model.Equals(model, StringComparison.OrdinalIgnoreCase);

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
        private readonly bool _repeat;
        private readonly bool _moves;
        private readonly int _rate;
        private readonly double _delay;
        private readonly Matrix4x4 _correction;
        private readonly Vector3 _opened;

        private double _elapsed;

        public Playing(
            ActFile clip,
            PlacedModel target,
            AnimationAction action,
            bool repeat,
            bool moves,
            Vector3? began,
            Matrix4x4 standing,
            bool fromBehaviour = false,
            int rate = AnimationFile.FramesPerSecond)
        {
            Clip = clip;
            Target = target;
            FromBehaviour = fromBehaviour;
            _repeat = repeat;
            _moves = moves;
            _rate = Math.Max(1, rate);
            _delay = action.Frame / (double)_rate;
            _correction = Correction(clip, target, action.Placement, standing);
            _opened = Opens(clip);
            Began = began;
            Carried = began;
        }

        /// <summary>Where the actor stood when this started.</summary>
        public Vector3? Began { get; }

        /// <summary>Where the clip has carried the actor to.</summary>
        public Vector3? Carried { get; private set; }

        /// <summary>Whether the actor gives back the ground the clip covered.</summary>
        public bool Reverts => !_moves;

        public ActFile Clip { get; }

        public PlacedModel Target { get; }

        /// <summary>Whether the model's own behaviour script asked for it.</summary>
        /// <remarks>
        /// What separates an idle from the story. One of these may be dropped or stopped
        /// for the other's sake; two clips from the story are the story contradicting
        /// itself, and the later one simply wins.
        /// </remarks>
        public bool FromBehaviour { get; }

        /// <summary>
        /// Where the clip's own space has to be moved to for it to play here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A clip's mesh transforms replace the model's own, and the model's placement is
        /// applied on top. What that means depends entirely on what is being animated.
        /// </para>
        /// <para>
        /// A <b>prop</b> is placed by the identity: the room's coordinates <em>are</em> the
        /// model's coordinates, so a clip written for that room plays exactly as authored
        /// and nothing has to be done to it. That is what the original does — it swaps the
        /// mesh transforms and stops there — and it is what the data expects: 1,635 of the
        /// game's prop clips move the thing away from where its model rests, because moving
        /// it is the whole point. A book being picked up is 59 units above the shelf; the
        /// moped that rides past RC1 crosses seventeen hundred units of it. Correcting
        /// those back to the model's rest is what left Wilkes riding past the world origin
        /// while Gabriel watched an empty square.
        /// </para>
        /// <para>
        /// An <b>actor</b> is placed where the scene stands them, so their clip — authored
        /// wherever the animator built it, which for a walk is halfway across some other
        /// room — is shifted once, at the start, by however far its first frame sits from
        /// where the model rests. Root motion within the clip then still happens, measured
        /// from where the actor was standing. The shift is computed once and held, because
        /// recomputing it per frame would cancel exactly the movement it is meant to
        /// preserve.
        /// </para>
        /// <para>
        /// An <b>absolute</b> clip carries its own spot and heading and is put there,
        /// whatever it belongs to. The heading is used as it stands: it is a transform, not
        /// a character's heading, and the half turn that turns one into the other — see
        /// <see cref="Navigation.Walker.Rotation"/> — left RC1's fountain spraying water
        /// two hundred and fifty units from the fountain.
        /// </para>
        /// <para>
        /// And because the model's placement is applied on top, <b>the placement has to be
        /// taken back off</b> for the clip to land where it was authored. A prop stands at
        /// the identity, so for years there was nothing to take off and nothing said there
        /// was; an actor stands wherever the scene put them or wherever they last walked
        /// to, and leaving that on moves the clip by the whole of it. That is the
        /// difference between Gabriel pouring the coffee at the counter and Gabriel
        /// pouring it somewhere out past the wall.
        /// </para>
        /// <para>
        /// The reference point is the average of the mesh groups' origins. The original
        /// uses the shoes, named per character in <c>CHARACTERS.TXT</c>, which is not read
        /// yet; the average moves with the same rigid motion and differs only by a constant,
        /// which a difference of two averages cancels.
        /// </para>
        /// </remarks>
        private static Matrix4x4 Correction(
            ActFile clip, PlacedModel target, AnimationPlacement? placement, Matrix4x4 standing)
        {
            if (placement is { } spot)
            {
                Matrix4x4 authored =
                    Matrix4x4.CreateRotationY(spot.Heading) *
                    Matrix4x4.CreateTranslation(spot.Position);

                return Matrix4x4.Invert(standing, out Matrix4x4 back)
                    ? authored * back
                    : authored;
            }

            if (target.Kind != PlacedModelKind.Actor)
            {
                return Matrix4x4.Identity;
            }

            Vector3 rest = Average(target.Model.Meshes.Select(m => m.MeshToLocal.Translation));

            Vector3 opens = Average(Enumerable
                .Range(0, clip.MeshCount)
                .Select(m => clip.PoseOf(m, 0))
                .Where(p => p is not null)
                .Select(p => p!.Value.Translation));

            return Matrix4x4.CreateTranslation(rest - opens);
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
            double frame = running * _rate;

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

                // Back to the top, keeping whatever is left over rather than resetting to
                // zero. Dropping the remainder loses up to a sixtieth of a second every
                // time round, which on a loop as short as a fan's is a hitch every four
                // seconds — exactly what this is here to get rid of.
                frame %= Clip.FrameCount;
                _elapsed = _delay + (frame / _rate);
            }

            Pose(geometry, frame);
            return true;
        }

        /// <summary>Poses the model on the clip's opening frame, without running it.</summary>
        /// <param name="geometry">Where the model stands.</param>
        /// <remarks>
        /// What an <c>initanim=</c> means. The pose is applied and the clock is never
        /// started, so nothing is scheduled, nothing sounds and nothing finishes.
        /// </remarks>
        public void Open(ISceneSink geometry) => Pose(geometry, 0);

        /// <summary>Where in the room the opening pose leaves the model standing.</summary>
        /// <param name="standing">The model's own placement, which is applied on top.</param>
        /// <returns>The world position of its mesh groups' average origin.</returns>
        /// <remarks>
        /// <b>Not where the scene said it stands.</b> An opening pose can put a character
        /// somewhere else entirely — Emilio's seats him in the lobby's loveseat, and his
        /// line in the scene file gives no position at all — so anything that asks where he
        /// is has to ask the pose rather than the placement. It is the same measure
        /// <see cref="Correction"/> works from, so the two cannot disagree.
        /// </remarks>
        public Vector3 Settled(Matrix4x4 standing) =>
            Vector3.Transform(_opened, _correction * standing);

        /// <summary>Where the clip's mesh groups sit on its opening frame.</summary>
        private static Vector3 Opens(ActFile clip) => Average(Enumerable
            .Range(0, clip.MeshCount)
            .Select(m => clip.PoseOf(m, 0))
            .Where(p => p is not null)
            .Select(p => p!.Value.Translation));

        /// <summary>Puts the model into one moment of the clip.</summary>
        /// <param name="geometry">Where the model stands.</param>
        /// <param name="frame">
        /// Which frame, with the fraction of the way to the next one. The clip records
        /// fifteen poses a second and the screen shows sixty, so a whole number here is
        /// four identical frames in a row; see <see cref="ActFile.PoseAt"/>.
        /// </param>
        private void Pose(ISceneSink geometry, double frame)
        {
            float at = (float)frame;

            // How far the clip has carried the model since it opened, in the world's terms
            // rather than the model's. This is what the actor's position follows.
            if (Began is { } from)
            {
                Vector3 moved = Average(Enumerable
                    .Range(0, Clip.MeshCount)
                    .Select(m => Clip.PoseAt(m, at, _repeat))
                    .Where(p => p is not null)
                    .Select(p => p!.Value.Translation)) - _opened;

                Carried = from + Vector3.TransformNormal(moved, Target.Transform);
            }

            for (int mesh = 0; mesh < Clip.MeshCount; mesh++)
            {
                Matrix4x4? pose = Clip.PoseAt(mesh, at, _repeat);

                // A refined head is drawn from geometry the clip has never heard of, so the
                // clip's vertices are read as a motion and applied to the mesh instead of
                // being written into it. Everything else about the frame is unchanged.
                if (Target.Head is { } rig && rig.Mesh == mesh)
                {
                    // Whatever happens, a refined head is never reshaped: the buffer being
                    // drawn holds thousands of vertices and the clip has a few hundred to
                    // say about them. Falling through to the ordinary path would rely on the
                    // renderer noticing the size mismatch and dropping the write, which is a
                    // long way from here and silent when it happens.
                    if (Turn(rig, at) is { } turn)
                    {
                        if (pose is { } placed)
                        {
                            geometry.PoseMesh(Target.Placement, mesh, turn * placed * _correction);
                        }
                        else
                        {
                            // No transform track for the head in this clip, so the mesh keeps
                            // its own and the fit goes on top of it. TurnMesh is exactly that.
                            geometry.TurnMesh(Target.Placement, mesh, turn);
                        }
                    }
                    else if (pose is { } carried)
                    {
                        geometry.PoseMesh(Target.Placement, mesh, carried * _correction);
                    }

                    continue;
                }

                if (pose is { } value)
                {
                    geometry.PoseMesh(Target.Placement, mesh, value * _correction);
                }

                // The shapes, where the clip has them. Without these a character is mesh
                // groups sliding about: 3,085 of the corpus's 3,086 character clips deform.
                foreach (int submesh in Clip.ShapedSubmeshes(mesh))
                {
                    if (Clip.ShapeAt(mesh, submesh, at, _repeat) is { } shape)
                    {
                        geometry.ShapeMesh(Target.Placement, mesh, submesh, shape);
                    }
                }
            }
        }

        /// <summary>The rigid motion a clip is asking a refined head to make.</summary>
        /// <param name="rig">The head's authored vertices, which is what the clip addresses.</param>
        /// <param name="at">Which frame, with a fraction of the way to the next.</param>
        /// <returns>The transform, or null when this clip does not move the head.</returns>
        /// <remarks>
        /// <para>
        /// Every submesh the clip shapes on this frame is used, all at once: the fit wants as
        /// many points spread as widely as possible, and a hairline on its own is a poor
        /// lever arm for a rotation. A submesh whose vertex count disagrees with the model's
        /// is skipped rather than trusted — that is what a clip belonging to a different
        /// character looks like, and 12.9% of the corpus is filed under the wrong name.
        /// </para>
        /// <para>
        /// <b>A fit that comes back badly is not used.</b> The corpus survey says every one
        /// of the fifty-six models with head clips is rigid — 1.0% of head width at the median
        /// of medians — so this is a guard rather than a routine path. What it guards against
        /// is the handful of clips that genuinely do deform a head: <c>GAB_GABTE3HDOFF</c>,
        /// the worst frame in the game at 17%, is Gabriel's head coming off. Where the fit is
        /// refused the vertex track is dropped and the head is carried by the clip's own
        /// transform track instead. It is decided once per clip rather than per frame, so a
        /// character near the threshold cannot flicker between the two answers.
        /// <c>GK3Reborn.Tools head-solve</c> is where the corpus's error is measured.
        /// </para>
        /// </remarks>
        private Matrix4x4? Turn(HeadRig rig, float at)
        {
            if (_fitsHead is false)
            {
                return null;
            }

            _from.Clear();
            _to.Clear();

            foreach (int submesh in Clip.ShapedSubmeshes(rig.Mesh))
            {
                if (submesh < 0 || submesh >= rig.Rest.Length ||
                    Clip.ShapeAt(rig.Mesh, submesh, at, _repeat) is not { } shape ||
                    shape.Count != rig.Rest[submesh].Length)
                {
                    continue;
                }

                // By sample rather than wholesale: the three axis markers every mesh group
                // carries sit sixty units out and do not move with the head, and a fit that
                // includes them is decided by them.
                foreach (int vertex in rig.Sample[submesh])
                {
                    _from.Add(rig.Rest[submesh][vertex]);
                    _to.Add(shape[vertex]);
                }
            }

            if (_from.Count < 3)
            {
                return null;
            }

            Matrix4x4? fit = RigidFit.Solve(
                CollectionsMarshal.AsSpan(_from),
                CollectionsMarshal.AsSpan(_to),
                out float residual);

            // Above every model's ninety-ninth percentile but two — ma2 at 8.3% and glb at
            // 15.5% — and below the frames that are actually somebody's head coming off.
            const float limit = 0.08f;

            _fitsHead ??= fit is not null && rig.Span > 0f && residual <= limit * rig.Span;

            return _fitsHead is true ? fit : null;
        }

        /// <summary>
        /// Whether this clip's head vertices and this model's head are the same head.
        /// </summary>
        /// <remarks>
        /// Decided from the first frame that shapes the head and then kept, so the answer
        /// cannot change under a character mid-clip. Null until there has been a frame to
        /// decide it on.
        /// </remarks>
        private bool? _fitsHead;

        /// <summary>Scratch for the head fit, kept so a frame does not allocate.</summary>
        private readonly List<Vector3> _from = [];

        /// <summary>Scratch for the head fit, kept so a frame does not allocate.</summary>
        private readonly List<Vector3> _to = [];
    }

    /// <summary>The middle of a set of points, or the origin when there are none.</summary>
    /// <remarks>
    /// Both the clip playback and the walk cycle line a clip up against the model it belongs
    /// to by comparing where the mesh groups sit, so the two share one answer to where that
    /// is. The original uses the shoes, named per character in <c>CHARACTERS.TXT</c>; the
    /// average moves with the same rigid motion and differs only by a constant, which a
    /// difference of two averages cancels.
    /// </remarks>
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

        /// <summary>The stride to play while they cross, if this character has one.</summary>
        public WalkCycle? Stride { get; }

        public float Scale { get; }
    }

    /// <summary>
    /// A walking character's legs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clip is played for its <b>pose</b> and not for its ground. GK3 authors a walk as
    /// root motion — Gabriel's stride carries his hips 49.9 units along the model's −Z over
    /// 1.40 seconds — and the original lets that motion move the actor. Here
    /// <see cref="Walker"/> owns the position, because it is what knows the route, the
    /// boundary and where the walk is supposed to end. Two things writing one position is
    /// the fault this avoids.
    /// </para>
    /// <para>
    /// So the clip's forward travel is taken back out, frame by frame, and the character
    /// walks on the spot while the walker carries them. <b>Only the forward travel.</b> The
    /// hips also sway sideways and rise and fall — X returns to where it started every
    /// stride and Y bobs twice a stride — and removing those would flatten the walk into a
    /// glide with moving legs. What accumulates is Z, and Z is what comes out.
    /// </para>
    /// <para>
    /// The pace comes from the same measurement, so the ground covered and the feet
    /// covering it agree. Get that wrong and the walk reads as a character being dragged.
    /// </para>
    /// </remarks>
    private sealed class WalkCycle
    {
        private readonly ActFile _clip;
        private readonly PlacedModel _target;
        private readonly Matrix4x4 _rest;
        private readonly float _opens;

        private double _elapsed;

        private readonly int _period;

        private WalkCycle(ActFile clip, PlacedModel target, Matrix4x4 rest, float opens, float pace)
        {
            _clip = clip;
            _target = target;
            _rest = rest;
            _opens = opens;
            Pace = pace;

            // A stride is authored so that its last frame repeats its first — Gabriel's
            // twenty-first frame is his first, agreeing to two thousandths of a unit in
            // sway and exactly in bob. Looping over all of them shows that pose twice and
            // the walk hitches once a stride.
            _period = Closes(clip) ? clip.FrameCount - 1 : clip.FrameCount;
        }

        /// <summary>Whether the clip's last frame is its first again.</summary>
        private static bool Closes(ActFile clip)
        {
            Vector3 first = Mean(clip, 0);
            Vector3 last = Mean(clip, clip.FrameCount - 1);

            return clip.FrameCount > 2 &&
                   MathF.Abs(first.X - last.X) < 0.05f &&
                   MathF.Abs(first.Y - last.Y) < 0.05f;
        }

        /// <summary>Where the clip's mesh groups sit on a frame.</summary>
        private static Vector3 Mean(ActFile clip, int frame) => Average(Enumerable
            .Range(0, clip.MeshCount)
            .Select(m => clip.PoseOf(m, frame))
            .Where(p => p is not null)
            .Select(p => p!.Value.Translation));

        /// <summary>How fast the stride carries its owner, in scene units a second.</summary>
        public float Pace { get; }

        /// <summary>How fast to play it, as a multiple of the authored speed.</summary>
        /// <remarks>
        /// One unless the player is in a hurry. Whatever this is, the walker's pace is
        /// multiplied by the same number, or the feet stop matching the ground.
        /// </remarks>
        public float Rate { get; set; } = 1f;

        /// <summary>Finds the stride a character walks with.</summary>
        /// <returns>The cycle, or null when this character has no walk animation here.</returns>
        public static WalkCycle? For(
            PlacedModel target,
            Actors.CharacterLibrary? characters,
            Content.AnimationLibrary? animations,
            Content.ClipLibrary? clips)
        {
            if (characters?.Of(target.Name) is not { WalkAnimation: { Length: > 0 } named } ||
                animations is null ||
                clips is null)
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
                if (clips.Read(action.Name) is not { } clip ||
                    !clip.ModelName.Equals(target.Name, StringComparison.OrdinalIgnoreCase) ||
                    clip.FrameCount < 2 ||
                    clip.Duration <= 0)
                {
                    continue;
                }

                float opens = Forward(clip, 0);
                float travel = MathF.Abs(Forward(clip, clip.FrameCount - 1) - opens);

                Matrix4x4 rest = Matrix4x4.CreateTranslation(
                    Average(target.Model.Meshes.Select(m => m.MeshToLocal.Translation)) -
                    Mean(clip, 0));

                return new WalkCycle(
                    clip, target, rest, opens, (float)(travel / clip.Duration));
            }

            return null;
        }

        /// <summary>Poses the model for however long the walk has been going.</summary>
        /// <param name="geometry">Where the poses go.</param>
        /// <param name="seconds">Time since the last frame.</param>
        public void Step(ISceneSink geometry, float seconds)
        {
            _elapsed += Math.Max(0, seconds) * Math.Max(0.01f, Rate);

            // Looped, and seamlessly: with the forward travel removed, the last frame sits
            // exactly where the first does, so the join is invisible.
            int frame = (int)(_elapsed * AnimationFile.FramesPerSecond) % _period;

            Matrix4x4 correction =
                Matrix4x4.CreateTranslation(0, 0, _opens - Forward(_clip, frame)) * _rest;

            for (int mesh = 0; mesh < _clip.MeshCount; mesh++)
            {
                if (_clip.PoseOf(mesh, frame) is { } pose)
                {
                    geometry.PoseMesh(_target.Placement, mesh, pose * correction);
                }

                foreach (int submesh in _clip.ShapedSubmeshes(mesh))
                {
                    if (_clip.ShapeOf(mesh, submesh, frame) is { } shape)
                    {
                        geometry.ShapeMesh(_target.Placement, mesh, submesh, shape);
                    }
                }
            }
        }

        /// <summary>How far along the model's forward axis the body sits on a frame.</summary>
        private static float Forward(ActFile clip, int frame) => Mean(clip, frame).Z;
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
            // actor faces can be read straight back out of it — as a heading rather than as
            // the rotation itself, because a glance is worked out from a heading.
            _facing = Navigation.Walker.HeadingOf(placed.Transform);
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

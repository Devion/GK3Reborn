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
    /// <summary>How far a walk has to be before it is run, in scene units.</summary>
    /// <remarks>
    /// <para>
    /// A GK3 unit is roughly two and a half centimetres and a character stands about seventy
    /// tall, so this is a little over six metres — far enough that crossing it at a stroll is
    /// the player waiting rather than the player watching. Anything shorter is a step across
    /// a room and reads as fussy if it is taken at a trot.
    /// </para>
    /// <para>
    /// It uses the same <see cref="HurryFactor"/> a double-click does, so a player who has
    /// turned that down to one has turned this off with it, which is the right thing for it
    /// to mean.
    /// </para>
    /// </remarks>
    public float RunBeyond { get; set; } = 250f;

    /// <summary>Whether the next walk arrives at once instead of being walked.</summary>
    /// <remarks>
    /// <para>
    /// Held with shift on a way out of the room. A player who knows where they are going has
    /// already watched that walk, and a second-floor corridor crossed for the ninth time is
    /// not the part of the game anybody came for.
    /// </para>
    /// <para>
    /// One walk, and it clears itself. It is set immediately before an action is performed
    /// and consumed by the approach that action puts in front of itself, so a script's own
    /// walks are never affected — a cutscene that teleports its actor is a cutscene with a
    /// hole in it.
    /// </para>
    /// </remarks>
    public bool WarpNextWalk { get; set; }

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

    private readonly List<Footfall> _steps = [];

    private readonly List<Swap> _swaps = [];

    /// <summary>What animations are about to repaint and reveal about the room itself.</summary>
    /// <remarks>
    /// Separate from <see cref="_swaps"/> and <see cref="_showings"/> because the subject is
    /// different: those name a model the scene loaded from a file, these name a run of
    /// surfaces inside the room's own geometry. See <see cref="AnimationSceneTexture"/>.
    /// </remarks>
    private readonly List<Scheduled<AnimationSceneTexture>> _roomSwaps = [];

    private readonly List<Scheduled<AnimationSceneVisibility>> _roomShowings = [];

    /// <summary>What animations are about to say, frame and film as they run.</summary>
    /// <remarks>
    /// The <em>moments</em>, and nothing else in the corpus, carry these: a scripted beat
    /// that speaks its own lines, cuts its own camera and sets its own faces, because the
    /// timing belongs to the animation rather than to the script that started it. Fifty
    /// lines, eighteen cuts and twelve expressions across 36 files. See
    /// <see cref="AnimationDialogue"/>.
    /// </remarks>
    private readonly List<Scheduled<AnimationDialogue>> _lines = [];

    private readonly List<Scheduled<AnimationShot>> _shots = [];
    private readonly List<Scheduled<AnimationMood>> _moods = [];
    private readonly List<Scheduled<AnimationMusic>> _music = [];
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
    /// A model whose clip is authored in somebody else's space, and whose space that is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What puts the binoculars in the Abbé's hands.</b> A prop somebody is holding is
    /// not placed in the room and is not animated in the room's coordinates. It is a second
    /// model exported from the same scene the character was, so its clip is authored around
    /// the character's own origin: <c>AbeBinocUp.ANM</c> is two clips, one for <c>abe</c>
    /// and one for <c>abebinocs</c>, and POU's second morning declares
    /// <c>model=abebinocs, type=prop, hidden</c> with no position at all, because the
    /// position is meant to come from the man holding it.
    /// </para>
    /// <para>
    /// The original works out who that is from the animation's <em>name</em>: the first
    /// three letters name the model everything else in the file belongs to, and that
    /// model's own space is copied onto the others every frame —
    /// <c>VertexAnimNode::Play</c> picks the holder and <c>VertexAnimator::OnLateUpdate</c>
    /// copies the transform. Across the whole corpus that binds 314 action lines, and every
    /// one of them is authored within 94 units of the character it accompanies, at a median
    /// of 27.6 — arm's length. Not one is in room coordinates, which is why leaving them
    /// unbound puts them all at the origin: the Abbé's binoculars, Buchelli's magnifier and
    /// his notepad and pencil, Lady Howard's camera and its lens.
    /// </para>
    /// <para>
    /// <b>The binding outlives the clip that made it</b>, which is also what the original
    /// does: <c>VertexAnimator::Stop</c> clears the animation and leaves the parent behind.
    /// <c>AbeBinocIdle.gas</c> is a loop of eight separate animations, and dropping the
    /// binding between each pair would blink the binoculars back to the origin between
    /// every one of them. It is replaced when a clip that names no holder starts on the
    /// same model, exactly as assigning fresh parameters replaces it there.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, (PlacedModel Held, PlacedModel Holder)> _carried =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The last shift a clip asked of each model, kept for as long as the room stands.
    /// </summary>
    /// <remarks>
    /// A character's space is their placement with their clip's own correction in front of
    /// it, and the correction lives on the clip — so when the clip ends there is nothing
    /// left to ask. The original has no such gap, because there the space is a transform on
    /// the model actor rather than something recomputed, and stopping an animation does not
    /// touch it. Without this the binoculars jump by the whole of the Abbé's correction in
    /// the frames between one clip of his idle and the next.
    /// </remarks>
    private readonly Dictionary<string, Matrix4x4> _space =
        new(StringComparer.OrdinalIgnoreCase);

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
        _triggers.AddRange(scene.Definition.Triggers());

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

    /// <summary>Told where every actor stands, whenever a clip takes one or lets one go.</summary>
    /// <remarks>
    /// <para>
    /// Null unless somebody asked — <c>--trace-actors</c> — because it is a line per clip
    /// per character and a cutscene is hundreds of them.
    /// </para>
    /// <para>
    /// <b>Where somebody is standing is not something to reason about from a screenshot.</b>
    /// A character drawn in the wrong place and a character whose <em>placement</em> is in
    /// the wrong place look identical for as long as one clip is playing and diverge the
    /// moment the next one starts, and the whole family of defects this exists for is of
    /// that shape: an actor who steps towards somebody and steps back, a pair who turn to
    /// face the player and are facing the wall a line later. The clip's name beside the two
    /// numbers, at the frame it starts and the frame it ends, is what tells the two apart.
    /// </para>
    /// </remarks>
    public Action<string>? TraceActors { get; set; }

    /// <summary>Reports where an actor stands, for <see cref="TraceActors"/>.</summary>
    /// <param name="what">What just happened, such as <c>plays</c> or <c>ends</c>.</param>
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
        float heading = Navigation.Walker.Wrapped(
            Navigation.Walker.Rotation(MathF.Atan2(standing.M31, standing.M33)) +
            (target.BuiltFacing ?? MathF.PI) - MathF.PI);

        tell(string.Create(
            CultureInfo.InvariantCulture,
            $"{target.Name} {what} {clip}: placed ({where.X:0.#}, {where.Z:0.#}) " +
            $"facing {heading * 180f / MathF.PI:0.#}°{(note.Length > 0 ? ", " + note : string.Empty)}"));
    }

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
            _cues.Add(new Cue(cue, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        // And the feet. A clip says when one lands and whose it is; what it sounds like is
        // decided when it lands, from the floor the actor is standing on by then.
        foreach (AnimationStep step in animation.Steps)
        {
            _steps.Add(new Footfall(step, repeat ? animation.Duration : 0, animation.Rate));
        }

        // And what it repaints as it runs. Like the visibility changes, frame zero is
        // applied now rather than in a frame's time.
        foreach (AnimationTexture swap in animation.Textures)
        {
            _swaps.Add(new Swap(swap, repeat ? animation.Duration : 0, animation.Rate));
        }

        Repaint(animation.Textures.Where(t => t.Frame <= 0));

        // And what it repaints about the room rather than about a model. The bar's dance
        // floor is nine of these, cycling three checker patterns on a loop; the light
        // coming on in Grace's office is one.
        foreach (AnimationSceneTexture swap in animation.SceneTextures)
        {
            _roomSwaps.Add(new Scheduled<AnimationSceneTexture>(
                swap, swap.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        PaintRoom(animation.SceneTextures.Where(t => t.Frame <= 0));

        foreach (AnimationSceneVisibility change in animation.SceneVisibility)
        {
            _roomShowings.Add(new Scheduled<AnimationSceneVisibility>(
                change, change.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        RevealRoom(animation.SceneVisibility.Where(v => v.Frame <= 0));

        // And what it says, frames and puts on people's faces. A moment is the only kind
        // of animation that carries these, and it is the reason it exists: the beat is a
        // whole scripted exchange whose timing is the artist's rather than the story's.
        //
        // Without them the dining room's spit take played as mime — Gabriel drank, the
        // camera stayed on the wide shot, and "Mosely? Is that YOU?" and the reply to it
        // were never spoken, because neither line is a call in DIN110A. Both are nodes
        // in ECOFFEEPOT.MOM, and the ContinueDialogue the script makes afterwards is a
        // continuation *of them*, so the exchange lost its next line as well.
        foreach (AnimationDialogue spoken in animation.Dialogue)
        {
            _lines.Add(new Scheduled<AnimationDialogue>(
                spoken, spoken.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        foreach (AnimationShot shot in animation.Shots)
        {
            _shots.Add(new Scheduled<AnimationShot>(
                shot, shot.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        foreach (AnimationMood mood in animation.Moods)
        {
            _moods.Add(new Scheduled<AnimationMood>(
                mood, mood.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        // And what it does to the music under it. Two nodes in the corpus reach here —
        // EHANDSHAKE.MOM swaps the hotel's daytime bed for its evening one across frames
        // 665 and 666 — where 79 more are inside lines of dialogue and reach SceneAudio.
        foreach (AnimationMusic change in animation.Music)
        {
            _music.Add(new Scheduled<AnimationMusic>(
                change, change.Frame, repeat ? animation.Duration : 0, animation.Rate, name));
        }

        // Frame zero is now, as it is for the repaints and the reveals above. Eighteen of
        // the corpus's fifty lines open their moment, and a line a frame late is a line
        // that starts after the camera has already cut away from whoever says it.
        Say(animation.Dialogue.Where(d => d.Frame <= 0));
        Film(animation.Shots.Where(s => s.Frame <= 0));
        Wear(animation.Moods.Where(m => m.Frame <= 0));
        Score(animation.Music.Where(m => m.Frame <= 0));

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
            if (onAFace ||
                animation.Sounds.Count > 0 ||
                animation.Visibility.Count > 0 ||
                animation.Textures.Count > 0 ||
                animation.SceneTextures.Count > 0 ||
                animation.SceneVisibility.Count > 0 ||
                animation.Steps.Count > 0 ||
                animation.Dialogue.Count > 0 ||
                animation.Shots.Count > 0 ||
                animation.Moods.Count > 0 ||
                animation.Music.Count > 0)
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

        // Whose space this file's other clips are authored in — the man with the
        // binoculars, and see _carried. Worked out once, because it is a property of the
        // animation rather than of any one of its lines.
        PlacedModel? holder = Holder(name, animation);

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

            // Cut short is still as far as it got. The reference commits a move clip's
            // ground every frame, so stopping one part-way leaves the actor wherever it had
            // carried them by then; here the commit happens as a clip lets go of a model,
            // and this is one of the three ways that happens. See Adopt.
            foreach (Playing stopped in _playing.Where(p => Drives(p, target) && !p.Reverts))
            {
                Adopt(stopped);
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

            // Whatever the model does on its own stops here, and does not start again
            // by itself.
            //
            // <b>A stop rather than a pause</b>, which is the difference between the two
            // rules. A walk pauses an idle and gives it back on arrival — the reference
            // says so in Walker::OnWalkToFinished — but a story animation calls
            // StopFidget, and nothing in GKActor::OnVertexAnimationStop turns it back on
            // again. What turns it back on is the script, by hand, once it has finished
            // with the character: PourCoffee$ ends with StartIdleFidget("Gabriel") and
            // that line is there for exactly this reason.
            //
            // Pausing instead leaves a gap between every pair of clips in a sequence, and
            // the idle fires into it. Reported from the dining room, where Gabriel walks
            // to the kitchen for coffee and snaps back to the table between clips — a
            // breath is a non-move clip, so it gives back all the ground the story had
            // just covered — and from the museum, where Lady Howard does the same after
            // each of hers. The hold below stops an idle that is already running; this
            // stops the script that keeps starting them.
            if (!fromBehaviour)
            {
                _held.Add(target.Name);
                Quieten(target);
            }

            // And whose space it is played in, before the clip is handed anything about
            // where the model stands: binding it moves the model, and what a carried clip
            // is corrected against is the holder rather than its own rest.
            PlacedModel? carrier =
                action.Placement is null && holder is not null && !ReferenceEquals(holder, target)
                    ? holder
                    : null;

            if (carrier is not null)
            {
                _carried[target.Name] = (target, carrier);
                Carry(target, carrier);
            }
            else
            {
                // Starting a clip that names no holder is the original assigning fresh
                // parameters over the old ones, and the parent goes with them.
                _carried.Remove(target.Name);
            }

            var started = new Playing(
                clip, target, action, repeat, moves, Where(target.Name),
                _geometry.TransformOf(target.Placement), fromBehaviour, animation.Rate,
                Characters?.Of(target.Name), carrier is not null);

            Trace(
                "plays",
                clip.Name,
                target,
                (started.Absolute ? "absolute" : "relative") +
                (started.Reverts ? ", reverts" : ", keeps the ground") +
                (fromBehaviour ? ", from its own script" : string.Empty));

            _playing.Add(started);
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

    /// <summary>
    /// What speaks a line an animation asks for, or null when there is no device.
    /// </summary>
    /// <remarks>
    /// The same shape and the same reason as <see cref="Sound"/>: the world schedules the
    /// node and something else knows how to say it. What it is handed is a licence plate,
    /// usually with the language letter already on the front — the file writes it that way
    /// and the animation library resolves a name either with or without one.
    /// </remarks>
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

    /// <summary>
    /// Advances a schedule and says what it has reached, oldest frame first.
    /// </summary>
    /// <param name="schedule">The things waiting for their frame. Spent ones are dropped.</param>
    /// <param name="seconds">How long since the last frame.</param>
    /// <param name="frame">Which frame one of them is authored on.</param>
    /// <typeparam name="T">What is due.</typeparam>
    /// <remarks>
    /// <para>
    /// <b>Frame order, not list order.</b> A frame of the game is several frames of a
    /// fifteen-a-second animation, so nodes authored one frame apart come due together —
    /// and what they mean depends on which happens first. EHANDSHAKE.MOM stops every
    /// soundtrack on frame 665 and starts the evening's on 666; performed the other way
    /// round, the beat starts the new bed and then silences it.
    /// </para>
    /// <para>
    /// The older schedules beside this one walk backwards so that a spent entry can be
    /// removed as it is passed, which reverses them within a frame. It does not matter for
    /// a repaint or a footfall, where the entries are independent of one another. It
    /// matters for every one of these.
    /// </para>
    /// </remarks>
    private static List<T> Due<T>(
        List<Scheduled<T>> schedule, double seconds, Func<T, int> frame)
        where T : struct
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

            // A performance is not a pose, and an actor the scene has already stood
            // somewhere does not need one.
            //
            // POU's second morning is the case this is about. The eight people on the tour
            // are given marks to stand on and `initanim=VanPouIN`, which is the van
            // arriving: two hundred frames, an engine, a door, and a soundtrack under it.
            // Sampling its first frame put every one of them in the pose they hold *inside
            // the van* — seated, thighs horizontal — while standing upright on their marks,
            // and nothing afterwards put them back. Their idle scripts then animated the
            // upper body only, so the top of each of them stood and talked while the legs
            // stayed in the van. It reads as characters cut off at the hip.
            //
            // Told apart by the soundtrack. It is the one thing in an animation file that
            // only something happening has, and across the whole corpus it picks out
            // **nine actor declarations**: these eight and Lady Howard's driver getting out
            // of the car at LHE. Everything else keeps the behaviour exactly — Madeline
            // stood by the van at RC1, Emilio sat in the lobby, and every prop placed by
            // its own opening animation, none of which carry one.
            if (model.Kind == PlacedModelKind.Actor &&
                model.Spotted &&
                animation.IsPerformance)
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

                // Not turned to what the clip's opening frame faces. A relative clip is
                // played facing whichever way the actor already faces, with the turn it was
                // authored with taken back out — see Correction — so the scene file's
                // heading is the heading, exactly as the reference has it: it samples the
                // init anim with the model at the scene position and syncs the actor to the
                // result afterwards.
                var pose = new Playing(
                    clip,
                    target,
                    action with { Frame = 0 },
                    repeat: false,
                    moves: true,
                    Where(target.Name),
                    _geometry.TransformOf(target.Placement),
                    character: Characters?.Of(target.Name));

                pose.Open(_geometry);

                // Where the pose leaves them is where they now are. An opening animation
                // is the only thing that says where several of the game's characters
                // stand, and leaving the position the scene never set would have anything
                // that asks — a walk, a glance, IsActorNear — answer about the origin
                // while the character is sitting in a chair on the other side of the room.
                if (target.Kind == PlacedModelKind.Actor)
                {
                    Vector3 settled = pose.Settled(_geometry.TransformOf(target.Placement));

                    // What the clip says the character is facing, beside what the scene
                    // file said. The two disagree wherever a scene records where somebody
                    // ends up and states the beginning as an animation, which is most of
                    // the game's opening poses.
                    float? wanted =
                        Clips is { } clips && Characters?.Of(target.Name) is { } who
                            ? Actors.AnimationStart.Of(
                                animation, clips, target.Name, who, target.BuiltFacing)?.Heading
                            : null;

                    // And where it leaves them has to become their <b>placement</b>, not
                    // only a note of where they are. A placement is the whole of what a
                    // later <em>relative</em> clip is played through — the idle fidgets
                    // first of all, on the frame after this one — so an actor the scene
                    // stood nowhere was posed correctly and then drawn at the world origin
                    // for the rest of the room's life. RC3's cat is the case reported: sat
                    // on its ledge for a single frame, and thereafter drawn, walked to and
                    // clicked on out past the courtyard wall.
                    //
                    // Only for an actor the scene named no spot for. One the room did place
                    // is standing where the room says; moving them would let a clip
                    // authored somewhere else overrule the mark an artist gave them, which
                    // is exactly the disagreement <see cref="Posed"/> exists to record.
                    if (!target.Spotted)
                    {
                        Reseat(
                            target,
                            settled,
                            wanted ?? Navigation.Walker.HeadingOf(
                                _geometry.TransformOf(target.Placement)));

                        // Sampled again, against the placement they now have. An absolute
                        // clip is put where it was authored by a correction worked out from
                        // the placement at the time — see Correction — so moving the
                        // placement out from under a finished pose would carry the drawing
                        // with it. Recomputing lands the same pixels on a model that also
                        // knows where it is standing.
                        pose = new Playing(
                            clip,
                            target,
                            action with { Frame = 0 },
                            repeat: false,
                            moves: true,
                            Where(target.Name),
                            _geometry.TransformOf(target.Placement),
                            character: Characters?.Of(target.Name));

                        pose.Open(_geometry);
                        settled = pose.Settled(_geometry.TransformOf(target.Placement));
                    }

                    Follow(target.Name, settled);

                    _posed.Add((
                        target.Noun ?? target.Name,
                        settled,
                        Navigation.Walker.HeadingOf(_geometry.TransformOf(target.Placement)),
                        wanted));
                }

                posed++;
            }
        }

        return posed;
    }

    /// <summary>Who an opening pose moved, where to, and which way they ended up facing.</summary>
    /// <remarks>
    /// <c>Placed</c> is the heading the model is standing at, out of the scene file.
    /// <c>Wanted</c> is the one the clip's opening frame implies, or null where the clip says
    /// nothing about it. Where the two disagree, the scene file is recording where the
    /// character ends up and the animation is stating where they begin.
    /// </remarks>
    public IReadOnlyList<(string Who, Vector3 Where, float Placed, float? Wanted)> Posed =>
        _posed;

    private readonly List<(string Who, Vector3 Where, float Placed, float? Wanted)> _posed = [];

    /// <summary>
    /// Gives a character a different stride.
    /// </summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="start">The animation that gets them moving.</param>
    /// <param name="loop">The stride itself, looped while they walk.</param>
    /// <returns>True when the room has such a character.</returns>
    /// <remarks>
    /// <c>SetWalkAnim</c>, 42 calls: somebody walking differently for a while — carrying
    /// something, hurt, sneaking. It replaces what <c>CHARACTERS.TXT</c> said, so a walk
    /// begun after this uses the new one. The two turn animations the call also carries
    /// are read past: turning on the spot is done by the walker rather than by a clip.
    /// </remarks>
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
    private readonly Dictionary<string, (string Start, string Loop)> _strides =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts a prop's own script again.</summary>
    /// <param name="model">Its model name or noun.</param>
    /// <returns>True when the room has such a prop with a script.</returns>
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
    /// <param name="model">Its model name or noun.</param>
    /// <returns>True when one was running.</returns>
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

    /// <summary>Stops one model's behaviour script, because the story has taken it over.</summary>
    /// <param name="model">The model.</param>
    /// <remarks>
    /// <c>GKActor::StartAnimation</c> does this on the way in to any animation that did not
    /// come from the behaviour script itself. The prop the idle was holding goes back where
    /// it lives, which is what <see cref="Tidy"/> is for.
    /// </remarks>
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

    /// <summary>
    /// Puts a prop back where it lives, when the idle that was moving it is cut short.
    /// </summary>
    /// <param name="running">The clip being stopped.</param>
    /// <remarks>
    /// <para>
    /// Reported as Emilio's newspaper and then as Mosely's: stop a character mid-idle and the
    /// paper stays in the air where his hands were. A GAS file may declare what to play if it
    /// is interrupted — <c>USES CLEANUP</c>, and 328 lines of the corpus are those — but
    /// <c>mosPaperIdle.gas</c> declares none, so there is nothing to play and no amount of
    /// looking for one will find it.
    /// </para>
    /// <para>
    /// <b>Only a prop, and only an idle.</b> A character keeps whatever pose they were cut
    /// off in, which is right — a person stopped mid-gesture is a person standing oddly, not
    /// a person snapping to attention. And only an animation a behaviour started: an idle is
    /// decoration and may be interrupted at any moment, where a script's animation is the
    /// story and a door it left open is meant to stay open.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Puts right whatever a behaviour script was in the middle of.
    /// </summary>
    /// <param name="fidget">The character's scripts.</param>
    /// <returns>True when there was something to put right.</returns>
    /// <remarks>
    /// <para>
    /// A GAS file may declare a cleanup per animation — <c>USES CLEANUP EmlLbyOpnPaper
    /// emllbyclspaper</c> — meaning "if you stop me while I am doing the first, do the
    /// second". 328 of the corpus's 341 <c>USE</c> lines are these, and they exist because
    /// an idle can leave a character holding something: Emilio reads a newspaper in the
    /// lobby, and stopping him to shake Gabriel's hand without the cleanup leaves the paper
    /// hanging in the air where his hands used to be.
    /// </para>
    /// <para>
    /// A cleanup may have a cleanup of its own, which is why this loops rather than playing
    /// one. Bounded, because a file that cleans up in a circle would otherwise not stop.
    /// </para>
    /// </remarks>
    private bool Tidy(Fidget fidget)
    {
        if (fidget.Stopped || fidget.Running is not { } running)
        {
            return false;
        }

        bool did = false;

        for (int guard = 0; guard < 8; guard++)
        {
            if (running.Playing is not { Length: > 0 } was ||
                running.Script.CleanupFor(was) is not { Length: > 0 } tidied)
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

    /// <summary>
    /// Hands the actors in a conversation the scripts written for that conversation.
    /// </summary>
    /// <param name="name">The conversation, as <c>SetConversation</c> names it.</param>
    /// <returns>How long the actors take to get into it, in seconds.</returns>
    /// <remarks>
    /// <para>
    /// A scene's <c>[LISTENERS]</c> section says what each participant does while speaking
    /// and while listening <em>in this conversation</em>, which is not what they do in
    /// general: Mosely leans on the counter of the Armorer's for two of its conversations
    /// and stands straight for the rest of the afternoon. 237 lines across 75 rooms say so.
    /// </para>
    /// <para>
    /// The enter animation is what puts them into that pose, and its exit undoes it — so
    /// they are a pair and both have to run, or a character stays leaning on a counter for
    /// the rest of the day. What the actor had before is kept here rather than looked up
    /// again afterwards, because a second conversation may have replaced it in between.
    /// </para>
    /// </remarks>
    public double EnterConversation(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // One at a time. Setting a second without ending the first leaves the first
        // conversation's poses on everybody it named.
        LeaveConversation();

        Conversation = name;

        double longest = 0;

        foreach (SceneConversation setting in _scene.Definition.Conversations())
        {
            if (!setting.Conversation.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                ModelNamed(setting.Actor) is not { } model)
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
            if (!setting.Conversation.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                ModelNamed(setting.Actor) is not { } model)
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
    /// <remarks>
    /// Swapping the file on the model changes what the <em>next</em> mode change runs, and
    /// a character already listening would go on running the script they were handed
    /// before the conversation began until somebody else spoke.
    /// </remarks>
    private void Restart(PlacedModel model)
    {
        if (_fidgets.TryGetValue(model.Name, out Fidget? fidget) && fidget.Mode is { } mode)
        {
            fidget.Enter(mode, model);
        }
    }

    /// <summary>
    /// Makes the noise a foot landing makes.
    /// </summary>
    /// <param name="fell">Whose foot, and whether it was dragged.</param>
    /// <remarks>
    /// <para>
    /// Three things have to agree and none of them is in the animation: which shoes the
    /// character is wearing (<c>CHARACTERS.TXT</c>), what the floor under them is made of
    /// (the texture, through <c>FLOORMAP.TXT</c>) and which of the three sounds for that
    /// pairing to use (<c>FOOTSTEPS.TXT</c>). The last is drawn from the room's own
    /// generator, so the same walk sounds the same twice.
    /// </para>
    /// <para>
    /// Silent rather than wrong where any of the three is missing. A floor texture nothing
    /// classifies is most of the game's ceilings and walls, and a step on one should make
    /// no noise rather than a guessed one.
    /// </para>
    /// </remarks>
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
            Diagnostics.Add(new Diagnostic(
                "GK3R3343", DiagnosticSeverity.Info,
                "A footstep names a sound the archives do not have.",
                heard, null, "a .WAV of that name", heard,
                "The step is silent; the walk is unaffected."));
        }
    }

    /// <summary>Whether a name is one of a model's own.</summary>
    private static bool Names(PlacedModel model, string name) =>
        model.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (model.Noun is { Length: > 0 } noun && noun.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies an animation's texture swaps.
    /// </summary>
    /// <param name="swaps">The changes due now.</param>
    /// <remarks>
    /// <para>
    /// The node names a mesh group and a submesh; the sink repaints by the <em>texture</em>
    /// the model was built with, because that is the thing it can find without knowing how
    /// a particular model is cut up. So the original is looked up from the model and used
    /// as the handle. A model the room does not have, or an index it does not go up to, is
    /// skipped in silence — animations are shared between rooms.
    /// </para>
    /// <para>
    /// The replacement has to be resident before it can be drawn, and the scene loaded only
    /// what its models were painted with. So it is read and uploaded on first use and kept:
    /// Larry's alarm clock swaps through ten digits and would otherwise decode them again
    /// every second.
    /// </para>
    /// </remarks>
    private void Repaint(IEnumerable<AnimationTexture> swaps)
    {
        foreach (AnimationTexture swap in swaps)
        {
            if (ModelNamed(swap.Model) is not { } model ||
                swap.Mesh < 0 || swap.Mesh >= model.Model.Meshes.Count)
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
                Diagnostics.Add(new Diagnostic(
                    "GK3R3344", DiagnosticSeverity.Info,
                    "An animation repaints a surface with a texture the archives do not have.",
                    swap.Model, null, "a .BMP of that name", swap.Texture,
                    "The surface keeps the picture it had."));

                continue;
            }

            _geometry.Repaint(model.Placement, parts[swap.Submesh].TextureName, swap.Texture);
        }
    }

    /// <summary>
    /// Applies an animation's repaints of the room itself.
    /// </summary>
    /// <param name="swaps">The changes due now.</param>
    /// <remarks>
    /// <para>
    /// The scene name each line carries is read past. It records the variant the artist had
    /// open when they wrote it — every one of the bar's says <c>rl2_disco_a</c> — and an
    /// animation is only ever played by the room that owns it, so matching on it would
    /// reject the same lines the room is currently asking for.
    /// </para>
    /// <para>
    /// An object the room does not have is skipped in silence, as an <c>[MTEXTURES]</c>
    /// line naming an absent model is: the lobby's window animations are played by three
    /// timeblocks' worth of scenes and name whichever one the artist was in.
    /// </para>
    /// </remarks>
    private void PaintRoom(IEnumerable<AnimationSceneTexture> swaps)
    {
        foreach (AnimationSceneTexture swap in swaps)
        {
            if (Textures?.Invoke(swap.Texture) == false)
            {
                Diagnostics.Add(new Diagnostic(
                    "GK3R3345", DiagnosticSeverity.Info,
                    "An animation repaints part of a room with a texture the archives do not have.",
                    swap.ObjectName, null, "a .BMP of that name", swap.Texture,
                    "The surface keeps the picture it had."));

                continue;
            }

            if (!_geometry.PaintSceneObject(swap.ObjectName, swap.Texture))
            {
                Diagnostics.Add(new Diagnostic(
                    "GK3R3346", DiagnosticSeverity.Info,
                    "An animation repaints part of a room that has no such part.",
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

    /// <summary>
    /// Makes a texture resident, and says whether it could be.
    /// </summary>
    /// <remarks>
    /// A function rather than the archives, the same shape as the clip and animation
    /// libraries: without one nothing repaints, which is what a test with no device wants.
    /// </remarks>
    public Func<string, bool>? Textures { get; set; }

    /// <summary>
    /// Gives the room a different bake of its own lighting, and says whether it could be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What <c>SetScene</c> asks for. A function rather than the archives, for the same
    /// reason <see cref="Textures"/> is one: reading a scene asset and its bake is the
    /// launcher's business, and a test with no archives wants the call to be answered
    /// rather than to fail.
    /// </para>
    /// <para>
    /// The argument is the scene asset's name — <c>rl2_disco_a</c> — and the answer is
    /// whether the room now has that bake on. False covers all of "no such asset", "it
    /// belongs to different geometry" and "this room was never baked", which are the same
    /// thing to a caller: the room looks how it looked.
    /// </para>
    /// </remarks>
    public Func<string, bool>? Relight { get; set; }

    /// <summary>Hands the room a second bake of its lighting, by scene-asset name.</summary>
    /// <param name="asset">The scene asset — <c>rl2_disco_a</c>, <c>gri_b</c>.</param>
    /// <returns>True when the room is now lit by it.</returns>
    /// <remarks>
    /// The whole of what <c>SetScene</c> does that anybody can see. The original reloads
    /// the named asset's geometry as well, which matters for the one call in the game that
    /// names a different BSP — CEM's, at 106P; every other call in the corpus is the same
    /// room lit a second way, which is the case the bar's disco and Grace's light switch
    /// both are.
    /// </remarks>
    public bool Relit(string asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (Relight?.Invoke(asset) == true)
        {
            return true;
        }

        Diagnostics.Add(new Diagnostic(
            "GK3R3347", DiagnosticSeverity.Info,
            "A script asked for a different bake of the room and did not get one.",
            _scene.Name, null, "a scene asset baked for this geometry", asset,
            "The room keeps the lighting it had."));

        return false;
    }

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
                // A script that is one animation and a jump back to it is a thing that
                // simply turns: a fan, a fountain, a clock. Those are looped by the clip
                // rather than by restarting the script, so the last recorded pose runs into
                // the first instead of being held for the fifteenth of a second the script
                // would take to come round. Started once and left.
                case GasAction.Animate when step.Name is { Length: > 0 } spun &&
                                            running.Script.Continuous:
                    Play(spun, repeat: true, fromBehaviour: true);
                    running.Playing = spun;
                    running.Remaining = double.MaxValue;
                    break;

                case GasAction.Animate when step.Name is { Length: > 0 } clip:
                    if (Draws(step.Chance))
                    {
                        running.Remaining += Math.Max(
                            Play(clip, step.Repeats, fromBehaviour: true), 1.0 / 60);

                        running.Playing = clip;
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
                        [
                            Sheep.SheepValue.FromString(looker.Name),
                            Sheep.SheepValue.FromString(at),
                            Sheep.SheepValue.FromFloat((float)step.Seconds),
                        ]);

                    break;

                // Where the character now is. Not a guard on the script, which is what the
                // name suggests and what this was filed under: the reference's
                // LocationGasNode calls SetActorLocation outright.
                //
                // Emilio's bench script ends with LOCATION LBY, a get-up animation and a
                // walk to the hotel door — he leaves when Gabriel comes near, and the whole
                // of what tells the rest of the game he has gone is that one line. Dropped,
                // the lobby goes on believing he is outside on a bench, and every
                // IsActorAtLocation about him answers wrongly for the rest of the morning.
                case GasAction.AtLocation when step.Name is { Length: > 0 } moved &&
                                               running.Owner is { } who:
                    _api.State.SetActorLocation(who.Noun ?? who.Name, moved);
                    break;

                // A line, said by whoever the script drives. Two of Mosely's are the whole
                // of what he says when he notices Gabriel in the lobby, and a fidget that
                // speaks has to wait the line out — a script that runs on takes the model
                // back mid-sentence.
                case GasAction.Speak when step.Name is { Length: > 0 } plate:
                    running.Remaining += Say(plate);
                    break;

                // An expression worn until something takes it off. The Sheep function is
                // where the pairing of an "on" animation with its "off" lives, and a
                // second implementation here would be a second answer to which one is on.
                case GasAction.SetMood when step.Name is { Length: > 0 } mood &&
                                            running.Owner is { } wearer:
                    _api.Invoke(
                        "SetMood",
                        [
                            Sheep.SheepValue.FromString(wearer.Noun ?? wearer.Name),
                            Sheep.SheepValue.FromString(mood),
                        ]);

                    break;

                // Back to where the scene put them. Three scripts end with it, and what
                // they have in common is a character who wanders — Mosely round the lobby,
                // Vittorio round the chapel — and must not have drifted by the time the
                // story next wants them somewhere in particular.
                case GasAction.ResetPosition when running.Owner is { } strayed:
                    Restore(strayed);
                    break;

                // Everything else is parsed and not run: labels and declarations, which
                // are read where they are needed rather than stepped through.
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Lets a behaviour notice somebody coming near, or leaving.
    /// </summary>
    /// <param name="running">The script.</param>
    /// <remarks>
    /// <para>
    /// <c>WHENNEAR Gabriel, 100, END</c> is a standing condition, not an instruction: it
    /// holds for the whole of the script and jumps to its label on the frame its answer
    /// turns true, wherever execution happens to be — including the middle of a wait. This
    /// was parsed and never run, which is why Emilio sat on his bench for ever with Gabriel
    /// standing over him, and why the museum's whispering carried on however close Gabriel
    /// came: the interruption each script wrote for exactly that moment could never fire.
    /// </para>
    /// <para>
    /// On the edge, exactly as the reference has it — <c>GasPlayer::CheckDistanceConditions</c>
    /// fires a condition when it goes from false to true and arms it again when it goes back.
    /// Level-triggered, a WHENNEAR would jump every frame for as long as anybody stood in
    /// the circle, and the label's own animation would restart sixty times a second.
    /// </para>
    /// <para>
    /// Distance is flat, like every other nearness in the game: the spots carry heights the
    /// floor disagrees with, and a radius through the vertical answers "far away" about
    /// somebody standing on the mark.
    /// </para>
    /// </remarks>
    private void Notice(Behaviour running)
    {
        IReadOnlyList<Formats.Animation.GasStep> steps = running.Script.Steps;

        for (int index = 0; index < steps.Count; index++)
        {
            Formats.Animation.GasStep step = steps[index];

            bool watching = step.Action is GasAction.WhenNear or GasAction.WhenNoLongerNear;
            bool seeing = step.Action == GasAction.WhenInView;

            if ((!watching && !seeing) ||
                step.Name is not { Length: > 0 } noun ||
                step.Other is not { Length: > 0 } label ||
                running.Owner is not { } owner)
            {
                continue;
            }

            // From this script's own actor, unless the condition names somebody else to
            // measure from — Estelle's whisper idle watches Gabriel against LADY_HOWARD,
            // so the pair notice him together.
            string from = step.Between is { Length: > 0 } other ? other : owner.Name;

            bool met;

            if (seeing)
            {
                met = Sees(owner.Name, noun, step.Value);
            }
            else
            {
                bool near = Where(from) is { } here &&
                            Where(noun) is { } them &&
                            Flat(here - them) < step.Value * (float)step.Value;

                met = step.Action == GasAction.WhenNear ? near : !near;
            }

            bool was = running.Noticed.Contains(index);

            if (met && !was)
            {
                running.Noticed.Add(index);

                // The chance is spent when the condition fires rather than tested every
                // frame, which is the difference between "sometimes notices" and "notices
                // after a random number of frames of standing there".
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
    /// <param name="looker">Whose sight, by either of their names.</param>
    /// <param name="seen">Who they might see.</param>
    /// <param name="degrees">How wide their sight is, in degrees, as the script states it.</param>
    /// <returns>True when the second is inside the first's field of view.</returns>
    /// <remarks>
    /// <para>
    /// <c>WHENINVIEW Gabriel, 90, INSULT</c>: Mosely insults Gabriel when Gabriel comes
    /// into his view. The number is an angle rather than a distance — the two conditions
    /// that are about distance carry one, and these carry 90 and 70, which are fields of
    /// view and not room-sized radii.
    /// </para>
    /// <para>
    /// Read as the <em>whole</em> field rather than a half-angle, so 90 means 45 degrees
    /// either side of the way the actor is facing. Both readings are defensible from the
    /// data and neither corpus use is load-bearing — Mosely's insult and Gabriel's yawn —
    /// so this takes the reading that matches what a field of view usually means.
    /// </para>
    /// <para>
    /// Flat, like every other sight line here: a character looking across a room is not
    /// looking up or down, and heights the floor disagrees with would answer "behind me"
    /// about somebody standing on a step.
    /// </para>
    /// </remarks>
    private bool Sees(string looker, string seen, int degrees)
    {
        if (degrees <= 0 ||
            Where(looker) is not { } here ||
            Where(seen) is not { } them ||
            Looking(looker) is not { } gaze)
        {
            return false;
        }

        var ahead = new Vector2(gaze.X, gaze.Z);
        var towards = new Vector2(them.X - here.X, them.Z - here.Z);

        if (ahead.LengthSquared() <= 0 || towards.LengthSquared() <= 0)
        {
            return false;
        }

        float cosine = Vector2.Dot(
            Vector2.Normalize(ahead), Vector2.Normalize(towards));

        return cosine >= MathF.Cos(float.DegreesToRadians(degrees) / 2f);
    }

    /// <summary>Says one line, and answers how long it lasts.</summary>
    /// <param name="plate">The licence plate the line is filed under.</param>
    /// <returns>Seconds the line takes, or zero when nothing can play it.</returns>
    /// <remarks>
    /// Through the same API a script uses, rather than through the animation library
    /// directly: a line is a run of animations, a caption and a voice, and which of those
    /// happen is the dialogue layer's business and not a behaviour script's.
    /// </remarks>
    private double Say(string plate)
    {
        Sheep.SheepValue[] arguments =
        [
            Sheep.SheepValue.FromString(plate),
            Sheep.SheepValue.FromInt(1),
        ];

        _api.Invoke("StartVoiceOver", arguments);

        return _api.SecondsFor("StartVoiceOver", arguments);
    }

    /// <summary>Puts an actor back where the scene placed them.</summary>
    /// <param name="actor">The model to move.</param>
    /// <remarks>
    /// The authored transform, which the record still carries: moving a model writes the
    /// geometry rather than the placement, so what the scene said is never lost and this
    /// restores the facing as well as the spot. Whatever they were doing stops, because
    /// they are not where they were doing it any more.
    /// </remarks>
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
        _api.Walks?.Invoke(actor, spot, Approaching.Walk, false, false) ?? 0;

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

    /// <summary>
    /// The code this room needs that its data cannot express, where it declares any.
    /// </summary>
    /// <remarks>
    /// Eleven scenes do — <c>custom=Laser</c> and its ten siblings. Set by the launcher
    /// after the room is built; null in every other room and in every tool, where the
    /// scene functions go on being recorded. See <see cref="Mechanisms.SceneMechanism"/>.
    /// </remarks>
    public Mechanisms.SceneMechanism? Mechanism { get; set; }

    /// <summary>
    /// Stands an actor somewhere without dropping them onto the floor.
    /// </summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <param name="position">Where to stand them, in world space.</param>
    /// <param name="heading">Which way to face, as the game's data measures a heading.</param>
    /// <returns>True when there was somebody of that name to move.</returns>
    /// <remarks>
    /// <see cref="Place"/> without the drop, and without stopping what is animating them.
    /// For the one room where the floor is not where the player is: TE3's platforms turn
    /// around a shaft with nothing under them, and a player put down onto the floor there
    /// is a player at the bottom of it.
    /// </remarks>
    public bool Carry(string actor, Vector3 position, float heading)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_standing.TryGetValue(actor, out PlacedModel? placed))
        {
            return false;
        }

        _geometry.MoveModel(placed.Placement, Standing(placed, position, heading));
        Follow(actor, position);

        return true;
    }

    /// <summary>Makes a model its own light source, or stops.</summary>
    /// <param name="model">The model.</param>
    /// <param name="selfLit">Whether it is drawn at full brightness and never shaded.</param>
    /// <remarks>
    /// The room's geometry carries this per surface and a model carries it nowhere, so it
    /// is something a mechanism asks for. One does: a laser beam is light, and light is not
    /// shaded by the room it crosses. See <see cref="ISceneSink.SetSelfLit"/>.
    /// </remarks>
    public void SelfLit(PlacedModel model, bool selfLit)
    {
        ArgumentNullException.ThrowIfNull(model);

        _geometry.SetSelfLit(model.Placement, selfLit);
    }

    /// <summary>One of the spots the scene marks out, by name.</summary>
    /// <param name="name">What the scene file calls it.</param>
    /// <returns>The spot, or null when the room has no such name.</returns>
    /// <remarks>
    /// For a mechanism that stands the player somewhere the artists marked rather than
    /// somewhere it worked out: TE3's doorway and the top of its altar are both spots.
    /// </remarks>
    public Formats.Scenes.ScenePosition? PositionNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _scene.Definition.PositionNamed(name);
    }

    /// <summary>The middle of anything the room names, prop or geometry.</summary>
    /// <param name="objectName">What it is called.</param>
    /// <returns>The centre of its box in world space, or null when there is no such thing.</returns>
    /// <remarks>
    /// For a mechanism that has to stand somebody on one of the room's own surfaces. The
    /// chessboard is the caller: its sixty-four tiles are part of the geometry rather than
    /// props, so there is no placement to ask and the triangles are the only answer.
    /// </remarks>
    public Vector3? MiddleOf(string objectName)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        return SceneScripting.Bounds(_scene, objectName) is var (low, high)
            ? (low + high) * 0.5f
            : null;
    }

    /// <summary>Repaints one of the room's own surfaces.</summary>
    /// <param name="objectName">The object in the geometry.</param>
    /// <param name="texture">What to paint it with; null puts its own picture back.</param>
    /// <returns>True when the room has such an object and the picture was found.</returns>
    /// <remarks>
    /// The same call an animation's <c>[STEXTURES]</c> line makes, reached directly for a
    /// mechanism that repaints a surface on its own account: the chessboard lights and
    /// unlights its sixteen sword tiles this way.
    /// </remarks>
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
    /// <remarks>
    /// Reachable so that a mechanism can shut a stretch of floor the way a script does. One
    /// room needs it: CSE has a one-pixel gap in its boundary that counts as a path, and
    /// Montreaux takes it and walks through a door.
    /// </remarks>
    public Navigation.WalkBoundary? Boundary => _scene.Walkable;

    /// <summary>
    /// Poses named models on an animation's <em>last</em> frame, without running it.
    /// </summary>
    /// <param name="animation">What the animation is called.</param>
    /// <param name="models">Which of its models to pose; others in it are left alone.</param>
    /// <param name="atEnd">Whether to take the closing frame rather than the opening one.</param>
    /// <returns>How many were posed.</returns>
    /// <remarks>
    /// <see cref="Open"/>'s opposite end, and for the same kind of job: stating where a
    /// thing rests rather than showing it get there. The one caller is the lobby's patch,
    /// where the wine glass has to end up where an animation would have left it without
    /// anybody watching Buchelli put it down.
    /// </remarks>
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
            if (Clips.Read(action.Name) is not { } clip ||
                !models.Contains(clip.ModelName, StringComparer.OrdinalIgnoreCase) ||
                !_models.TryGetValue(clip.ModelName, out PlacedModel? target))
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
                _geometry.TransformOf(target.Placement),
                character: Characters?.Of(target.Name));

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
    /// <remarks>
    /// The sink owns every live transform — see <see cref="PlacedModel.Standing"/> — so a
    /// mechanism that stands five laser heads on a circle has to write through it rather
    /// than through the placements the scene file gave them.
    /// </remarks>
    public ISceneSink Geometry => _geometry;

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

    /// <summary>Draws one of the room's own named objects, or stops drawing it.</summary>
    /// <param name="objectName">The object's name, as the geometry records it.</param>
    /// <param name="visible">Whether it is drawn.</param>
    /// <returns>True when the room has an object by that name.</returns>
    /// <remarks>
    /// The room rather than a model standing in it: see
    /// <see cref="Rendering.ISceneSink.SetSceneObjectVisible"/>. The picker is told as
    /// well, because a doorway a script has just closed off must stop answering to clicks
    /// — otherwise the noun goes on being offered for something nobody can see.
    /// </remarks>
    public bool ShowObject(string objectName, bool visible)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        if (!_geometry.SetSceneObjectVisible(objectName, visible))
        {
            return false;
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

    /// <summary>
    /// Whose space the clips of an animation are authored in, when it is not the room's.
    /// </summary>
    /// <param name="animation">The <c>.ANM</c> name, whose first three letters name them.</param>
    /// <param name="file">That animation, which has to agree that they are its subject.</param>
    /// <returns>The model to play its other clips relative to, or null for the room.</returns>
    /// <remarks>
    /// <para>
    /// The original looks the three-letter prefix up among the scene's models and stops
    /// there. This also asks that the animation carry a clip for that model, which is a
    /// narrower rule by exactly four action lines in the whole corpus: <c>GabJmpOffPen</c>
    /// and <c>GabJmpPndulm</c>, the two Gabriel plays on the pendulum. The original excludes
    /// those by hand — <c>noParenting</c>, set in <c>Pendulum</c> — so the two rules agree
    /// on every line of the game's data, and this one says so without the pendulum ported.
    /// </para>
    /// <para>
    /// It is also the rule that makes the prefix mean something. Three letters is a short
    /// name and a room whose code happens to match one would otherwise pin every prop in an
    /// animation to it; requiring the animation to actually move that model is what tells a
    /// subject from a coincidence.
    /// </para>
    /// </remarks>
    private PlacedModel? Holder(string animation, AnimationFile file)
    {
        if (animation.Length < 3 ||
            Clips is null ||
            !_models.TryGetValue(animation[..3], out PlacedModel? owner))
        {
            return null;
        }

        foreach (AnimationAction action in file.Actions)
        {
            // An absolute clip says where in the room it happens, so it is nobody's
            // passenger and it makes nobody else one either.
            if (action.Placement is null &&
                Clips.Read(action.Name) is { } clip &&
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

    /// <summary>
    /// Where a model's own space sits in the room, with whatever is animating it.
    /// </summary>
    /// <param name="model">The model whose space is wanted.</param>
    /// <returns>The transform a clip authored in that space is played through.</returns>
    /// <remarks>
    /// <b>Not the placement.</b> A clip replaces a model's mesh transforms and the placement
    /// is applied on top, so for a character the space their clip plays in is the placement
    /// with that clip's own correction in front of it — see <see cref="Playing.Space"/>. The
    /// original keeps exactly that as a transform of its own, <c>GKActor</c>'s model actor,
    /// and a held prop is pinned to it rather than to where the scene stood the character.
    /// </remarks>
    private Matrix4x4 ModelSpace(PlacedModel model)
    {
        Matrix4x4 standing = _geometry.TransformOf(model.Placement);

        if (_playing.Find(p => Drives(p, model)) is { } driving)
        {
            _space[model.Name] = driving.Space;

            return driving.Space * standing;
        }

        return _space.TryGetValue(model.Name, out Matrix4x4 last)
            ? last * standing
            : standing;
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

        foreach (Playing running in _playing.Where(p =>
            p.Clip.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            Rest(running);

            // And an actor keeps whatever ground a move clip had covered by the time it was
            // stopped, as it would have if the clip had been left to finish. See Adopt.
            if (!running.Reverts)
            {
                Adopt(running);
            }
        }

        _playing.RemoveAll(p =>
            p.Clip.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase));

        // And the noises it was going to make. Reported from the museum: Estelle and Lady
        // Howard stop whispering the moment they notice Gabriel, and the whispering went on
        // being audible — the clip was stopped and its sound cues, which live in a list of
        // their own, were not.
        //
        // By either name. A script stops an animation by the animation's name, and an actor
        // is stopped by their model's; a cue can be reached from both because it remembers
        // the first and may carry the second.
        _cues.RemoveAll(c =>
            c.Owner.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            c.Model.Equals(model, StringComparison.OrdinalIgnoreCase));

        // And whatever it was about to be shown or hidden by. A clip that is stopped
        // half-way should not still turn its model off four seconds later.
        _showings.RemoveAll(v => v.Concerns(model));

        // And whatever it was about to do to the room. These are stopped by the
        // animation's own name rather than by a model's, because they name no model:
        // `disco_flashdance_a` is nothing but a floor flashing on a loop, and
        // `StopAnimation("disco_flashdance_a")` is the only thing that ever ends it.
        _roomSwaps.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _roomShowings.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));

        // And whatever it was about to say, frame or put on a face, for the same reason
        // and by the same name. A moment that is cut short should not go on speaking.
        _lines.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _shots.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _moods.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));
        _music.RemoveAll(s => s.Owner.Equals(model, StringComparison.OrdinalIgnoreCase));

        // Whatever it does on its own is its own again. A hold outliving the clip that
        // asked for it leaves a character standing perfectly still for the rest of the
        // scene.
        Release(model);
    }

    /// <summary>
    /// Whether a script is animating somebody right now.
    /// </summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <returns>True while a clip the story asked for is playing on them.</returns>
    /// <remarks>
    /// <para>
    /// A story's animation and a character's own idle are not the same thing and must not be
    /// interrupted the same way. An idle is decoration and a click may cut it short at any
    /// moment; a clip a script started is the scene happening, and walking out of the middle
    /// of it leaves the story mid-sentence with nobody in the room it is addressed to.
    /// </para>
    /// <para>
    /// Reported from the dining room: a click on the floor during the coffee scene sent
    /// Gabriel away while the scene went on around where he had been standing.
    /// </para>
    /// </remarks>
    public bool Performing(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        string model = ModelNamed(actor)?.Name ?? actor;

        return _playing.Any(p =>
            !p.FromBehaviour &&
            p.Target.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>What a step sounds like, when the three files that decide were read.</summary>
    /// <remarks>
    /// Optional, like the clips: without it nobody makes a noise walking, which is what the
    /// game did before this and what a test with no audio wants.
    /// </remarks>
    public Actors.Footsteps? Steps { get; set; }

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
    /// Which way somebody is looking.
    /// </summary>
    /// <param name="actor">Their model name or noun.</param>
    /// <returns>A unit vector along their line of sight, or null when nobody answers.</returns>
    /// <remarks>
    /// From the walk in progress where there is one, because that is the live answer, and
    /// otherwise out of the model's own placement. Not to be confused with
    /// <see cref="Heading(string)"/> beside it, which answers where somebody is <em>going</em>
    /// — the two names are the game's own and they mean different things.
    /// </remarks>
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

        return _walking.TryGetValue(placed.Name, out Walking? theirs)
            ? Ahead(theirs.Walker.Facing)
            : Ahead(Navigation.Walker.HeadingOf(placed.Standing));
    }

    /// <summary>The direction a heading looks along.</summary>
    private static Vector3 Ahead(float heading) =>
        new(MathF.Sin(heading), 0f, MathF.Cos(heading));

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
    /// <param name="mayRun">
    /// Whether a long walk may pick up the pace by itself. True for a walk the player asked
    /// for and false for one a script asked for, because a script's timings assume the pace
    /// the game was authored at and this would run out from under them.
    /// </param>
    /// <param name="untilSeen">
    /// The bounds of what the walk is to see, or null for an ordinary walk. Given one,
    /// the walk stops where the thing comes into view rather than where it was aimed —
    /// which is what <c>WalkToSee</c> means, and 2,120 of the corpus's approaches are one.
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
        bool hurry = false,
        bool mayRun = false,
        (Vector3 Minimum, Vector3 Maximum)? untilSeen = null)
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

        // Already able to see it is not a walk at all — turn where you stand and look. The
        // whole of what "walk to see" asks for is a line of sight, and somebody who has one
        // crossing the room to get a better one is the behaviour this replaces.
        if (untilSeen is { } wanted &&
            Sight is { } sight &&
            sight.InView(from + (Vector3.UnitY * Eyes(placed)), wanted.Minimum, wanted.Maximum))
        {
            return Turn(actor, (wanted.Minimum + wanted.Maximum) * 0.5f);
        }

        WalkRoute route = _scene.Walkable is { } boundary
            ? WalkPath.Find(boundary, from, destination)

            // No boundary is no obstacles, so the straight line is the route.
            : new WalkRoute(true, [destination]);

        // A walk the player asked for stops where it walks onto something that acts on
        // whoever stands there, rather than crossing it and carrying on.
        route = ShortenedAtTrigger(actor, route);

        // And a walk to see something stops where it can see it.
        if (untilSeen is { } thing)
        {
            route = ShortenedWhenSeen(route, placed, thing);
        }

        // Asked for at once rather than walked. The route is still found, because where the
        // walk would have *ended* is where the player belongs — the boundary may stop it
        // short of what was asked for, and arriving somewhere the floor does not reach is
        // worse than the walk it replaced.
        if (WarpNextWalk)
        {
            WarpNextWalk = false;

            if (route.Points.Count > 0)
            {
                Place(
                    actor,
                    route.Points[^1],
                    arriveFacing ?? (arriveLookingAt is { } look
                        ? Walker.Heading(look - route.Points[^1])
                        : facing));

                return 0;
            }
        }

        // Far enough to be worth running. Measured along the route rather than straight at
        // the destination, because a walk that goes round the bed is the walk being taken —
        // and the first leg is added because the route begins at the nearest walkable texel
        // rather than under the actor's feet.
        if (mayRun && !hurry && route.Points.Count > 0)
        {
            Vector3 first = route.Points[0] - from;

            hurry = MathF.Sqrt((first.X * first.X) + (first.Z * first.Z)) + route.Length()
                    >= RunBeyond;
        }

        // The stride first, because its pace is what the walk is measured at.
        WalkCycle? stride = WalkCycle.For(
            placed,
            Characters,
            Animations,
            Clips,
            _strides.TryGetValue(placed.Name, out (string Start, string Loop) given)
                ? given.Loop
                : null);
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

    /// <summary>The room's sight tester, built the first time anybody asks.</summary>
    /// <remarks>
    /// Lazily, because most scenes are loaded without anybody walking to see anything in
    /// them — a corpus sweep loads all 143 — and building it walks every triangle of the
    /// room.
    /// </remarks>
    private SceneSight? Sight => _sight ??= SceneSight.For(_scene.Geometry);

    private SceneSight? _sight;

    /// <summary>How high an actor's eyes are above their feet.</summary>
    /// <param name="actor">The model.</param>
    /// <returns>The character's own height, or the walker's default.</returns>
    private float Eyes(PlacedModel actor) =>
        Characters?.Of(actor.Name)?.WalkerHeight is { } height && height > 0
            ? height
            : Walker.StandOff;

    /// <summary>
    /// Cuts a walk short at the point where what it is going to look at comes into view.
    /// </summary>
    /// <param name="route">The route as the boundary found it.</param>
    /// <param name="actor">Who is walking, for how tall they are.</param>
    /// <param name="thing">The bounds of what they are walking to see.</param>
    /// <returns>The route, cut where the thing is first visible.</returns>
    /// <remarks>
    /// <para>
    /// Each corner is tested, and three points along the leg leading to it, because a
    /// doorway is often crossed between two corners and testing only the corners walks the
    /// actor a whole leg past the moment they could see. That is the reference's own
    /// sampling.
    /// </para>
    /// <para>
    /// Then one corner further, also as the reference has it: stopping on the exact frame a
    /// sliver of the thing appears round a corner reads as a character noticing something
    /// impossible, where a step or two more puts them in the open looking at it.
    /// </para>
    /// <para>
    /// A route that never sees it is walked in full. That is the old behaviour, and it is
    /// the right fallback: the thing may be inside a cupboard the walk is meant to end at.
    /// </para>
    /// </remarks>
    private WalkRoute ShortenedWhenSeen(
        WalkRoute route, PlacedModel actor, (Vector3 Minimum, Vector3 Maximum) thing)
    {
        if (Sight is not { } sight || route.Points.Count == 0)
        {
            return route;
        }

        float eyes = Eyes(actor);

        for (int i = 0; i < route.Points.Count; i++)
        {
            Vector3 corner = route.Points[i];

            bool seen = sight.InView(corner + (Vector3.UnitY * eyes), thing.Minimum, thing.Maximum);

            if (!seen && i > 0)
            {
                Vector3 previous = route.Points[i - 1];

                for (float along = 0.25f; along < 1f && !seen; along += 0.25f)
                {
                    Vector3 between = Vector3.Lerp(previous, corner, along);

                    seen = sight.InView(
                        between + (Vector3.UnitY * eyes), thing.Minimum, thing.Maximum);
                }
            }

            if (!seen)
            {
                continue;
            }

            int stop = Math.Min(i + 1, route.Points.Count - 1);

            return stop >= route.Points.Count - 1
                ? route
                : new WalkRoute(false, [.. route.Points.Take(stop + 1)]);
        }

        return route;
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
            Matrix4x4.CreateRotationY(
                Actors.FacingArrow.Rotation(heading, placed.BuiltFacing)) *
            Matrix4x4.CreateTranslation(position));

        Follow(actor, position);

        // Nothing to tell the heads: they read the model's own transform, which this has
        // just written, on the frame that follows.
        return true;
    }

    /// <summary>Moves an actor's placement to where their opening pose left them.</summary>
    /// <param name="actor">The actor, as the room placed them.</param>
    /// <param name="position">Where the pose has them standing, in world space.</param>
    /// <param name="heading">Which way it has them facing.</param>
    /// <remarks>
    /// <para>
    /// <see cref="Place"/>'s move without anything else <see cref="Place"/> does. It does
    /// not drop them onto the floor — a pose has already decided the height, and the cat on
    /// RC3's ledge is above the floor on purpose — and it does not stop what is animating
    /// them, because the only caller is in the middle of posing them.
    /// </para>
    /// <para>
    /// It is a sync rather than a move: the actor is drawn in exactly the same place
    /// afterwards, once the pose is sampled again against the placement this writes.
    /// </para>
    /// </remarks>
    private void Reseat(PlacedModel actor, Vector3 position, float heading)
    {
        _geometry.MoveModel(actor.Placement, Standing(actor, position, heading));
    }

    /// <summary>A placement that stands an actor at a spot, facing a heading.</summary>
    /// <param name="actor">Whose placement it is, for its scale and its built facing.</param>
    /// <param name="position">Where to stand them.</param>
    /// <param name="heading">Which way to face, as the game's data measures a heading.</param>
    /// <remarks>
    /// The placement is scale, then a turn, then a move, and the scale has to survive.
    /// </remarks>
    private static Matrix4x4 Standing(PlacedModel actor, Vector3 position, float heading)
    {
        float scale = new Vector3(
            actor.Transform.M11, actor.Transform.M12, actor.Transform.M13).Length();

        return
            Matrix4x4.CreateScale(scale <= 0 ? 1f : scale) *
            Matrix4x4.CreateRotationY(
                Actors.FacingArrow.Rotation(heading, actor.BuiltFacing)) *
            Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// Takes where a clip has left an actor standing as where that actor now stands.
    /// </summary>
    /// <param name="actor">Whoever the clip was posing.</param>
    /// <param name="position">Where its last frame put their feet, in the room.</param>
    /// <param name="heading">And which way it left them facing.</param>
    /// <remarks>
    /// <para>
    /// <b>The rule this is: an actor's position and heading follow their model.</b> The
    /// reference does it every frame — <c>GKActor::OnLateUpdate</c> ends in
    /// <c>SyncActorToModelPositionAndRotation</c>, which reads the posed model's floor
    /// position and facing direction and moves the actor to them — and this engine did not
    /// do it at all. A clip poses the meshes and the placement underneath them was never
    /// written to, so the placement stayed wherever the scene file put the character for
    /// the whole life of the room.
    /// </para>
    /// <para>
    /// <b>What that looks like is a cutscene that keeps snapping its cast back.</b> A
    /// relative clip is played through the placement — see <see cref="Playing.Correction"/>
    /// — so every clip after the first began again at the spot and the heading the scene
    /// opened with, however far the one before it had carried them. Reported from the
    /// museum, where <c>LHETurn2Gab</c> turns Lady Howard to face Gabriel and walks Estelle
    /// a step towards him with <c>EstOneStep</c>: Estelle took her step and turned straight
    /// back round, and the pair reset between every line of the introduction.
    /// </para>
    /// <para>
    /// <b>It is a sync and not a move</b>, so the picture is identical on the frame it
    /// happens. The placement is what a mesh's own transform is drawn through, so writing a
    /// new one would shift the model by the whole of what the clip had just carried it. Each
    /// mesh's transform is therefore rewritten by the same amount the other way, which
    /// leaves every vertex exactly where it was and moves only the frame the next clip will
    /// be played in — which is the whole point, and is what the reference gets for free by
    /// keeping the actor and its model as two transforms rather than one.
    /// </para>
    /// <para>
    /// Only for a clip that <em>keeps</em> the ground it covered. A non-move animation puts
    /// the actor back where it found them, which is where the placement already is, so there
    /// is nothing to write: see <see cref="Playing.Reverts"/>.
    /// </para>
    /// </remarks>
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

    /// <summary>Hands a clip's last frame to the actor it was posing.</summary>
    /// <param name="playing">The clip, on the frame it stopped.</param>
    /// <remarks>
    /// Where and which way come from the pose rather than from a running total of how far
    /// the clip travelled, for the same reason <c>Follow</c> reads them there: a placement
    /// that was wrong to begin with makes every total measured from it wrong too. Only an
    /// actor has a heading to keep — a prop's clip is authored in the room's own space and
    /// its placement is the identity — and only a clip that says something about the hips
    /// can answer at all, so one that does not leaves the actor as they were rather than
    /// moving them to a guess.
    /// </remarks>
    private void Adopt(Playing playing)
    {
        if (playing.Target.Kind != PlacedModelKind.Actor)
        {
            return;
        }

        // <b>Not for a clip the model's own behaviour script asked for.</b> This is a
        // deliberate narrowing of the reference's rule, which syncs the actor to the model
        // every frame whatever is posing it, and the difference is that this happens once,
        // as a clip lets go. For a story clip the two agree at the only moment that matters.
        // For an <em>idle</em> they do not: the reference tracks a fidget continuously and
        // re-snaps the model to the actor as each new relative clip starts, so the drift is
        // bounded and cancels; taking one snapshot at the end of a fidget bakes it in
        // permanently.
        //
        // And a fidget is decoration, which this engine already says everywhere else — an
        // idle is dropped where the story is animating, paused for a walk, and cleaned up
        // when interrupted. Letting one relocate an actor for the rest of the room is the
        // same mistake in a new place. Madeline Buthane is the case: she is placed at
        // BUTHANE_TALK facing Gabriel, and her idle is madMapIdle.gas, whose clips are
        // authored absolutely at the back of her van with her turned to the map — 128° from
        // him. Adopting that left her talking to the whole conversation over her shoulder,
        // because the talk script that plays through her placement is relative.
        if (playing.FromBehaviour)
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

        Trace(
            "keeps",
            playing.Clip.Name,
            playing.Target,
            Actors.AnimationStart.Stance
                ? "taken from the stance"
                : "from the hip mesh — this clip poses no shoes");
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

    /// <summary>Something that has not happened yet.</summary>
    /// <param name="remaining">How much longer to hold it.</param>
    /// <param name="work">What to do then.</param>
    private sealed class Held(double remaining, Action work)
    {
        /// <summary>How much of the wait is left.</summary>
        public double Remaining { get; set; } = remaining;

        /// <summary>What to do when it is over.</summary>
        public Action Work { get; } = work;

        /// <summary>
        /// Scripts it cannot happen until, when it is waiting on scripts rather than on a
        /// clock.
        /// </summary>
        /// <remarks>
        /// The two gates are and-ed, not chosen between, so a wait can be both — though
        /// nothing asks for both yet. <see cref="Remaining"/> is zero for a wait that is
        /// only on scripts, which is why the clock alone is not enough to say it is over.
        /// </remarks>
        public IReadOnlyList<SheepThread>? Until { get; init; }
    }

    private readonly List<Held> _later = [];

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

        _later.Add(new Held(seconds, work));
        return true;
    }

    /// <summary>
    /// Holds something back until the next frame, however short that is.
    /// </summary>
    /// <param name="work">What to do then.</param>
    /// <remarks>
    /// <para>
    /// <see cref="After"/> refuses a delay of nothing on purpose — a script's <c>wait</c>
    /// with no length is a statement that nothing is being waited for. This is the other
    /// meaning of zero: <em>not in this frame</em>. A mechanism called from inside an
    /// action needs it, because starting a line of dialogue inside the action that asked
    /// for it cuts the first one off; so does one chaining animations, where a clip the
    /// archives are missing is worth no seconds and must not stall the chain for ever.
    /// </para>
    /// <para>
    /// Ordered with the timed ones and stepped by the same loop, so nothing here is a
    /// second kind of clock.
    /// </para>
    /// </remarks>
    public void Next(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _later.Add(new Held(double.Epsilon, work));
    }

    /// <summary>
    /// Holds something back until the scripts a call started have finished.
    /// </summary>
    /// <param name="scripts">The threads it started.</param>
    /// <param name="work">What to do once none of them is still running.</param>
    /// <returns>True when it was taken, false when there was nothing to wait for.</returns>
    /// <remarks>
    /// <para>
    /// The other half of <see cref="After"/>, for the wait whose length is another script.
    /// An action's <c>wait CallSheep("cs6_all", "Old_Grace$")</c> is over when that
    /// function is, which is forty seconds of camera cuts, animation and dialogue — and
    /// the statement after it is <c>SetLocation("cse")</c>. Answering "no time at all"
    /// and running them together is a room that leaves for the courtyard in the frame the
    /// cutscene starts, which is how it was reported.
    /// </para>
    /// <para>
    /// A call that started nothing still waiting is refused rather than queued, the same
    /// way a delay of nothing is: the ordinary <c>CallSheep</c> is a script that ran to
    /// completion inline, and its caller carries straight on in the frame it asked.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// For leaving the room. What is queued is nearly always an action script belonging to
    /// the room being left, and letting one run into the next room is how a door opens
    /// twice.
    /// </remarks>
    public void Cancel() => _later.Clear();

    /// <summary>Runs whatever has waited long enough.</summary>
    /// <remarks>
    /// <b>Oldest first.</b> Two things held for the same moment are two things asked for in
    /// that order, and running them backwards runs the second one's setup after it: four
    /// angels touched while the walk to them was still going traced the square from the
    /// last one back. Which means the clock is stepped over the whole list before any of it
    /// is run, because a wait that is over is allowed to hold something else back and that
    /// belongs to the next frame, not this one — which is what taking them from the end
    /// used to get right by accident.
    /// </remarks>
    private void StepLater(double seconds, List<string> happened)
    {
        List<Held>? due = null;

        foreach (Held held in _later)
        {
            held.Remaining -= seconds;

            // The clock first because it is the cheap half, and then the scripts: a wait
            // on a call into a script is over when nothing it started is parked any more.
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
            // Gone already, so there is nothing to run: one of the earlier ones left the
            // room, and Cancel forgets everything it was still holding — the rest of this
            // list included.
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

        /// <summary>Which of the script's standing conditions are currently met.</summary>
        /// <remarks>
        /// By step index, so two conditions on the same noun stay apart. What makes the
        /// jumps edge-triggered: a condition fires the frame it turns true and not again
        /// until it has turned false in between.
        /// </remarks>
        public HashSet<int> Noticed { get; } = [];

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

        /// <summary>
        /// The animation it last asked for, which is what a cleanup is looked up by.
        /// </summary>
        /// <remarks>
        /// A GAS file may declare <c>USES CLEANUP EmlLbyOpnPaper emllbyclspaper</c>: if the
        /// script is stopped while the first is what it is doing, the second puts it right.
        /// So which animation is in effect has to be remembered — a stopped script cannot
        /// be asked what it was in the middle of afterwards.
        /// </remarks>
        public string? Playing { get; set; }

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

        // What is left of the action that is running. Counted down here so that everything
        // below sees one answer for the frame.
        _api.ActionSeconds = Math.Max(0, _api.ActionSeconds - seconds);

        // The scripts first: one carrying on from a wait may cut the camera or set a
        // timer, and it should take effect in the frame it happened rather than the next.
        foreach (string carried in _scripts?.Advance(seconds) ?? [])
        {
            happened.Add($"{carried} carried on");
        }

        // Whatever machinery the room has of its own: the laser heads swinging round to
        // the angle they were sent to, the beams stretching to whatever is in front of
        // them. Before the actors, because a script that reads a head's angle back this
        // frame should read the one it has now.
        Mechanism?.Advance(seconds);

        StepBehaviours(seconds);
        MoveView(seconds);

        // Faces before anything that moves anybody: what a face is doing depends on the
        // clock and not on where its owner is standing, and a mouth that is a frame behind
        // the words is the one thing lip sync must never be.
        Faces?.Advance(seconds);

        // Anything that was waiting for the player to get somewhere. Before the timers, so
        // that an action which sets one is not a frame late in doing it.
        StepLater(seconds, happened);

        // The clock moves for every timer whatever else is happening; what waits on the
        // story being free is performing one. Taken one at a time and with the story asked
        // again between them, because performing one is the story becoming busy — which is
        // the whole of GameTimers::Update's own rule. See GameTimers for what firing one
        // into the middle of an action does to CS3's attic.
        _api.State.Timers.Advance(seconds);

        while (!Occupied && _api.State.Timers.TakeDue() is { } timer)
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

        // The feet, which need where the actor is now rather than where the clip was
        // authored: the sound is the floor under them at the moment the foot lands.
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

        // And what it repaints about the room. Told apart from the models' swaps above
        // because a room object is reached by name rather than through a placement.
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

        // What a moment frames, puts on faces, scores and says — in that order, and each
        // of them in frame order rather than in the order the nodes happen to sit in the
        // file. The camera holding the shot a line is spoken in has to be on it by the time
        // the line starts, and a beat that swaps the bed under itself stops the old one
        // before it starts the new: EHANDSHAKE.MOM does exactly that across frames 665 and
        // 666, which land in the same frame of anything but a sixty-hertz clock.
        Film(Due(_shots, seconds, s => s.Frame));
        Wear(Due(_moods, seconds, m => m.Frame));
        Score(Due(_music, seconds, m => m.Frame));
        Say(Due(_lines, seconds, d => d.Frame));

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
            //
            // For a character, from where the pose actually puts their feet rather than by
            // adding up how far the clip has carried them. The difference matters wherever
            // the placement they started from was wrong: the dining room names Mosely's spot
            // MOSTALK and defines TALK_MOSELY, so his placement is the origin, and a running
            // total from there keeps him at the origin however convincingly he is drawn
            // sitting in a chair. Every IsActorNear about him then answers about a corner of
            // the room. The reference has no such total — GKActor::SyncActorToModelPosition-
            // AndRotation reads the model's floor position outright.
            Follow(
                playing.Target.Name,
                playing.Target.Kind == PlacedModelKind.Actor
                    ? playing.Now(_geometry.TransformOf(playing.Target.Placement))
                    : playing.Carried);

            if (!running)
            {
                // A non-move animation puts the actor back where it found them: the pose
                // stays, the ground does not count. A move animation keeps it.
                if (playing.Reverts)
                {
                    Follow(playing.Target.Name, playing.Began);
                    Trace("reverts after", playing.Clip.Name, playing.Target);
                }
                else
                {
                    // And keeping it means writing it down. See Settle: until it did, the
                    // next clip through this actor's placement began at the spot the scene
                    // file named, whatever this one had just done with them.
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

            _geometry.MoveModel(
                walking.Placement,
                walking.Walker.Transform(walking.Scale, walking.Built));

            // The legs, in the model's own space, on top of wherever the model now is.
            walking.Stride?.Step(_geometry, (float)seconds);

            foreach (AnimationStep fell in walking.Stride?.Landed ?? [])
            {
                Tread(fell);
            }

            Follow(who, walking.Walker.Position);
        }

        // What somebody is holding goes where they are — after their own clip has posed
        // them and after they have walked, which is why it is here and not in either loop.
        // The original syncs it in LateUpdate for exactly that reason. See _carried.
        foreach ((PlacedModel held, PlacedModel holder) in _carried.Values)
        {
            Carry(held, holder);
        }

        // The timed glances run out here, before the heads move: LOOKAT's five seconds
        // are five seconds, not for ever.
        _glances.Tick(seconds);

        foreach (Turning actor in _actors)
        {
            // Where the model is now, whatever put it there. Asked of the geometry rather
            // than remembered, so a character moved by an animation or by a script is as
            // correct as one moved by walking.
            Matrix4x4 now = _geometry.TransformOf(actor.Placement);

            // And while a clip has the body, from the clip. The reference turns no heads at
            // all — every Lookit call in it is a stub — so this is the port's own feature
            // and has to be right on its own terms: a glance is a yaw off the body, and
            // the body is wherever the clip says, not wherever the placement is.
            Playing? driving = _playing.Find(p =>
                p.Target.Kind == PlacedModelKind.Actor &&
                p.Target.Placement.Id == actor.Placement.Id);

            if (driving is not null)
            {
                actor.Stands(driving.Now(now), driving.Facing(now) ?? actor.HeadingOf(now));
            }
            else
            {
                actor.Stands(now.Translation, actor.HeadingOf(now));
            }

            if (actor.Step(_glances, (float)seconds))
            {
                _geometry.TurnMesh(actor.Placement, actor.Head, actor.Turn());
            }
        }

        // Last, because it asks where the player is standing and this is the frame in
        // which they have finished moving.
        Tripped(happened);

        return happened;
    }

    /// <summary>The patches of floor that act on whoever walks onto them.</summary>
    private readonly List<SceneTrigger> _triggers = [];

    /// <summary>
    /// Runs the action for any trigger the player is standing in.
    /// </summary>
    /// <param name="happened">What to report it as.</param>
    /// <remarks>
    /// <para>
    /// <b>How the game says "and then you overhear them".</b> A scene file marks out a
    /// rectangle on the floor and names a noun, and standing in it does that noun's
    /// <c>WALK</c> — a verb no file writes beside the rectangle because
    /// <c>Scene::Update</c> hard-codes it. Thirty-four rectangles across twenty-nine files,
    /// and they carry the museum's eavesdrop, the front desk of the lobby, the window into
    /// Arnaud's office and the two lectures on the Blanchefort tour.
    /// </para>
    /// <para>
    /// <b>Not edge-triggered.</b> The original tests every frame and relies on the action's
    /// own case to stop it happening twice — the museum's is <c>GetNounVerbCount("GET_CLOSE",
    /// "WALK")==0</c> and its script increments that before it waits on anything. Firing
    /// only on the way in would instead lose the ones written to happen again, so the rule
    /// here is the reference's: while something is playing, nothing new is started.
    /// </para>
    /// </remarks>
    private void Tripped(List<string> happened)
    {
        if (_triggers.Count == 0 || Occupied || Where(_api.State.Ego) is not { } standing)
        {
            return;
        }

        foreach (SceneTrigger trigger in _triggers)
        {
            // Nothing written about the noun is not a refusal to report; it is a rectangle
            // that does nothing at this point in the story, and the player walks over it
            // as they would over any other patch of floor.
            if (!trigger.Rect.Contains(standing.X, standing.Z) ||
                _actions?.Find(trigger.Noun, Walked) is null)
            {
                continue;
            }

            happened.Add(Fire(trigger.Noun, Walked));
            return;
        }
    }

    /// <summary>
    /// Cuts a walk short where it would step onto a trigger.
    /// </summary>
    /// <param name="actor">Whose walk it is.</param>
    /// <param name="route">The route the boundary found.</param>
    /// <returns>The route, cut at the edge of the first trigger it enters.</returns>
    /// <remarks>
    /// <para>
    /// <c>Walker::FindEarliestPathNodeInsideActiveTriggerRegion</c>, whose own comment names
    /// the case: in the lobby on the first morning, the way to the front door goes through
    /// Jean's rectangle. Without this the player walks over the trigger, the action fires
    /// behind them, and the conversation it starts plays to somebody already at the door.
    /// </para>
    /// <para>
    /// Only a walk the player asked for, and only one for the player. A script that sends
    /// somebody somewhere means all the way there — the museum's own eavesdrop ends by
    /// walking Gabriel into the rectangle it was fired by.
    /// </para>
    /// </remarks>
    private WalkRoute ShortenedAtTrigger(string actor, WalkRoute route)
    {
        if (_triggers.Count == 0 ||
            Occupied ||
            route.Points.Count == 0 ||
            !string.Equals(
                ModelNamed(actor)?.Name ?? actor,
                ModelNamed(_api.State.Ego)?.Name ?? _api.State.Ego,
                StringComparison.OrdinalIgnoreCase))
        {
            return route;
        }

        for (int i = 0; i < route.Points.Count; i++)
        {
            Vector3 point = route.Points[i];

            foreach (SceneTrigger trigger in _triggers)
            {
                // A rectangle nothing is written about does nothing, so walking over it is
                // walking over floor.
                if (!trigger.Rect.Contains(point.X, point.Z) ||
                    _actions?.Find(trigger.Noun, Walked) is null)
                {
                    continue;
                }

                Vector3[] cut = [.. route.Points.Take(i + 1)];

                // Where the walk crosses the edge rather than the corner the boundary
                // happened to put inside it, so the player stops on the line.
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
    /// <param name="rect">The rectangle, on the ground plan.</param>
    /// <param name="before">The end of the segment outside it.</param>
    /// <param name="inside">The end of the segment within it.</param>
    /// <returns>The crossing point on X and Z, or null when the segment is degenerate.</returns>
    private static Vector2? Entry(SceneRect rect, Vector3 before, Vector3 inside)
    {
        var start = new Vector2(before.X, before.Z);
        Vector2 along = new Vector2(inside.X, inside.Z) - start;

        float enters = 0f;
        float leaves = 1f;

        if (!Clip(-along.X, start.X - rect.MinX, ref enters, ref leaves) ||
            !Clip(along.X, rect.MaxX - start.X, ref enters, ref leaves) ||
            !Clip(-along.Y, start.Y - rect.MinZ, ref enters, ref leaves) ||
            !Clip(along.Y, rect.MaxZ - start.Y, ref enters, ref leaves))
        {
            return null;
        }

        return start + (along * enters);
    }

    /// <summary>One side of the Liang-Barsky clip.</summary>
    /// <param name="denominator">How fast the segment approaches the edge.</param>
    /// <param name="numerator">How far outside the edge the segment begins.</param>
    /// <param name="enters">The parameter at which it is inside so far.</param>
    /// <param name="leaves">The parameter at which it leaves.</param>
    /// <returns>False when the segment misses the rectangle entirely.</returns>
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

    /// <summary>How many scripts were waiting before the last action started.</summary>
    /// <remarks>
    /// Minus one until one has. A room's own background scripts sit in the scheduler for as
    /// long as the room stands — the dining room and the third-floor hall each keep two
    /// parked permanently — so "any script is waiting" is not a usable answer to whether
    /// something is happening. The number of them when an action last started is, because
    /// what that action starts is on top of that and what it started going away is the
    /// action being over.
    /// </remarks>
    private int _quiet = -1;

    /// <summary>Notes what was already running, before an action adds to it.</summary>
    /// <remarks>
    /// <para>
    /// Called as every action begins, whoever asked for it. <c>IsActionPlaying</c> is one
    /// signal in the original and covers the lot: a click, a rectangle on the floor, a timer
    /// coming due. Armed only from the room's own two, it answered "nothing is happening"
    /// through the whole of a clicked action whose script was still running — which is a
    /// timer firing over the top of it, a trigger going off underneath it, and the camera
    /// handed back to the player mid-scene.
    /// </para>
    /// <para>
    /// An action starting while an earlier one's scripts are still parked leaves the mark
    /// where it is. Moving it up would count those as the room being quiet again and lose
    /// the first action, and the first action is the one still speaking.
    /// </para>
    /// </remarks>
    public void Starting()
    {
        int waiting = _scripts?.Count ?? 0;

        if (_quiet < 0 || waiting <= _quiet)
        {
            _quiet = waiting;
        }
    }


    /// <summary>
    /// Whether the story is in the middle of something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ActionManager::IsActionPlaying</c>, which is true from the moment an action starts
    /// until its script has finished waiting. Nothing here keeps a current action, so it is
    /// assembled out of four things that each cover part of one: an action held back for its
    /// approach walk, the waits an action reported when it ran, a clip the story is playing
    /// on the player, and any script still outstanding from the last action the room started
    /// itself.
    /// </para>
    /// <para>
    /// The last of those is what covers <c>wait CallSheep(…)</c>, whose length is another
    /// script rather than a number of seconds — which is most of what the triggers run.
    /// </para>
    /// </remarks>
    public bool Occupied =>
        _later.Count > 0 ||
        _api.ActionSeconds > 0 ||
        Performing(_api.State.Ego) ||
        (_quiet >= 0 && (_scripts?.Count ?? 0) > _quiet);

    /// <summary>
    /// Whether the story is holding the camera rather than the player.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GameCamera::SceneUpdateMovement</c>'s rule, and the whole of it: never while a
    /// script has asked for forced camera cuts, and never while an action is playing —
    /// unless the player has turned cinematics off, which is what that switch is for. A
    /// player who has turned them off keeps the camera through everything, because with the
    /// cuts gone there is nothing directing the view for them.
    /// </para>
    /// <para>
    /// It reads rather than does: what happens to the player's own camera while this is true
    /// is the caller's to decide, in the same way <see cref="View"/> is. Leaving them the
    /// controls is what made a cutscene's next cut look like the camera losing its place —
    /// they had flown halfway across the room by the time it came.
    /// </para>
    /// </remarks>
    public bool Directing =>
        _api.State.ForcedCameraCuts || (Occupied && _api.State.CinematicsEnabled);

    /// <summary>Gives the room back to the player when something has wedged it.</summary>
    /// <returns>What was let go of, one line each, or empty when nothing was holding it.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="Occupied"/> is assembled out of four things and <see cref="Directing"/>
    /// turns any of them into a camera the player does not have and clicks that do not reach
    /// the floor. That is right while the story is telling something and wrong the moment
    /// one of the four is stuck — a walk of ninety seconds, a script that parked and never
    /// came back, a clip on the player that never ends — and a player with no camera and no
    /// clicks has no way to say so.
    /// </para>
    /// <para>
    /// So this releases all four, plus the two pieces of camera state a script sets and
    /// clears a moment later, and stands the player back on ground they can walk on. It is
    /// deliberately not a reload: the story's flags, counts and inventory are untouched, so
    /// what the player had done is still done and only what was <em>happening</em> is
    /// dropped. A script left parked stays parked; it simply stops counting as the story
    /// being busy, which is the difference between abandoning a moment and abandoning a
    /// save.
    /// </para>
    /// </remarks>
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
            let.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{_api.ActionSeconds:F0}s an action said it still needed"));

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

            // Reset rather than cleared. The room's own background scripts sit in the
            // scheduler for as long as the room stands, and dropping those would stop the
            // room living rather than unstick it; what this forgets is only that the story
            // was counting them as something happening.
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

        // And onto ground they can walk on. Standing off the boundary is the other way a
        // room ends, and it has the same symptom from the player's chair: every click on
        // the floor finds no route and nothing moves.
        if (_scene.Walkable is { } boundary &&
            Where(_api.State.Ego) is { } standing &&
            !boundary.IsWalkable(standing) &&
            boundary.NearestWalkable(standing) is { } open &&
            ModelNamed(_api.State.Ego) is { } player)
        {
            let.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"the player {Vector3.Distance(standing, open):F0} units off the floor"));

            Place(
                _api.State.Ego,
                open,
                Walker.HeadingOf(_geometry.TransformOf(player.Placement)));
        }

        return let;
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

        if (Framing(key) is { } derived)
        {
            return derived;
        }

        Diagnostics.Add(new Diagnostic(
            "GK3R3204", DiagnosticSeverity.Info,
            "Nothing declares a close-up of this and it has no geometry to frame one from.",
            _scene.Name, null, "an [INSPECT_CAMERAS] entry", key,
            "Inspect is not offered for it, so the player is not shown a verb that does nothing."));

        return null;
    }

    /// <summary>
    /// A close-up worked out from what the thing actually occupies.
    /// </summary>
    /// <param name="noun">What is being looked at.</param>
    /// <returns>A camera framing it, or null when the room has no such thing to frame.</returns>
    /// <remarks>
    /// <para>
    /// Only 111 close-ups are authored across the corpus, against the thousands of nouns a
    /// player can point at, so for most things <c>[INSPECT_CAMERAS]</c> has nothing to say.
    /// The view used to stay exactly where it was — and because inspecting still counted as
    /// having happened, the verb flipped to "Inspect Undo" and the player was offered a way
    /// out of something that never started.
    /// </para>
    /// <para>
    /// So one is worked out instead, which is what the original does too. The box the thing
    /// occupies is measured in the room's own space, and the camera is put in front of it
    /// far enough back that the box fits the frame with a little air around it. An authored
    /// close-up still wins wherever there is one: the artists chose an angle, and this only
    /// chooses a distance.
    /// </para>
    /// <para>
    /// From where the view already is, rather than from a fixed side. Cutting to the far
    /// side of a thing shows the player a face of it they were not looking at and reads as
    /// the room having turned around; approaching along the line they were already on reads
    /// as leaning in. Where the view is on top of the thing there is no line to keep, and
    /// the room's own default camera decides instead.
    /// </para>
    /// </remarks>
    private Camera? Framing(string noun)
    {
        if (Occupies(noun) is not var (minimum, maximum))
        {
            return null;
        }

        Vector3 centre = (minimum + maximum) * 0.5f;
        float across = MathF.Max((maximum - minimum).Length(), 1f);

        // Far enough that the whole of it sits inside the frame, with a quarter again for
        // air. Half the box over the tangent of half the field of view is the distance at
        // which it exactly fills the view.
        float back = across * 0.5f / MathF.Tan(CloseUpFieldOfView * 0.5f) * 1.25f;

        Vector3 from = View is { } standing ? standing.Position : centre + new Vector3(0, 0, back);
        Vector3 line = from - centre;

        if (line.LengthSquared() < 1f)
        {
            line = View is { } facing
                ? Vector3.Normalize(facing.Position - facing.Target)
                : Vector3.UnitZ;
        }

        Vector3 eye = centre + (Vector3.Normalize(line) * back);
        float reach = MathF.Max(1f, (_geometry.Maximum - _geometry.Minimum).Length());

        return new Camera
        {
            Position = eye,
            Target = centre,
            Up = Vector3.UnitY,
            FieldOfView = CloseUpFieldOfView,
            NearPlane = 1f,
            FarPlane = reach * 4f,
        };
    }

    /// <summary>How wide a derived close-up sees, in radians.</summary>
    /// <remarks>
    /// Narrower than the room's sixty degrees. A close-up is a lens change as much as a
    /// move, and holding the room's own angle while standing this near flattens the thing
    /// being looked at into the wall behind it.
    /// </remarks>
    private const float CloseUpFieldOfView = 40f * MathF.PI / 180f;

    /// <summary>The box a noun's geometry fills, in the room's space.</summary>
    /// <returns>The corners, or null when nothing in the room answers to that noun.</returns>
    /// <remarks>
    /// Every mesh of every model the noun names, at the transform it is standing at now
    /// rather than the one the scene first gave it — a thing being carried, opened or
    /// animated is somewhere else by the time anybody looks closely at it.
    /// </remarks>
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

                // Every corner of the mesh's own box, because a rotated box's extremes are
                // not the transforms of the two corners that described it.
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 at = Vector3.Transform(
                        new Vector3(
                            (corner & 1) == 0 ? mesh.BoundsMin.X : mesh.BoundsMax.X,
                            (corner & 2) == 0 ? mesh.BoundsMin.Y : mesh.BoundsMax.Y,
                            (corner & 4) == 0 ? mesh.BoundsMin.Z : mesh.BoundsMax.Z),
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
    /// <param name="noun">What the player is pointing at.</param>
    /// <returns>True when inspecting it would move the view.</returns>
    /// <remarks>
    /// Asked before the verb is offered rather than after it is chosen, so that a thing with
    /// no close-up simply has no Inspect on its menu — instead of one that does nothing and
    /// then offers to undo itself.
    /// </remarks>
    public bool Inspectable(string noun)
    {
        ArgumentNullException.ThrowIfNull(noun);

        string? model = _scene.Models
            .FirstOrDefault(m => string.Equals(m.Noun, noun, StringComparison.OrdinalIgnoreCase))
            ?.Name;

        return _scene.Definition.AnyCameraNamed(noun) is not null ||
               _scene.Definition.InspectCameraFor(noun, model) is not null ||
               Occupies(noun) is not null;
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
    private string Fire(GameTimer timer) => Fire(timer.Noun, timer.Verb);

    /// <summary>Performs an action the room itself asked for.</summary>
    /// <param name="noun">What it is about.</param>
    /// <param name="verb">What is being done to it.</param>
    /// <returns>What to report happened.</returns>
    /// <remarks>
    /// Nobody clicked on this: a timer came due, or the player walked onto a patch of floor
    /// that does something. Either way the rule is chosen and run exactly as a click's would
    /// be, so its approach is walked and its case is scored against the story as it stands.
    /// </remarks>
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
        /// <remarks>
        /// The animation's name rather than a model's. A sound cue may name a model of its
        /// own and most do not — a door, a match, a yawn belong to the animation and to
        /// nothing standing in the room — so the animation is the only handle that always
        /// exists.
        /// </remarks>
        public string Owner { get; }

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
    /// <summary>
    /// A foot an animation is about to put down.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="Cue"/>, and separate from it for the same reason plus
    /// one of its own: a sound cue names the sound, and this one does not — what it sounds
    /// like cannot be decided until the foot lands, because it depends on where the actor
    /// has walked to by then.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The same clock as <see cref="Cue"/>, <see cref="Swap"/> and <see cref="Showing"/>,
    /// written once because the two things it carries — a repaint of a room object and a
    /// showing of one — differ in nothing but their payload. A period of zero is a change
    /// that happens once; anything else is a looping animation coming round again.
    /// </para>
    /// <para>
    /// It remembers which animation asked, which the older three do not all do. Room
    /// changes name no model, so the animation's own name is the only handle
    /// <c>StopAnimation</c> has on them.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">What is due.</typeparam>
    private sealed class Scheduled<T>
        where T : struct
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
        private readonly Actors.CharacterConfig? _character;
        private readonly bool _repeat;
        private readonly bool _moves;
        private readonly bool _absolute;
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
            int rate = AnimationFile.FramesPerSecond,
            Actors.CharacterConfig? character = null,
            bool carried = false)
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

        /// <summary>
        /// Whether the actor gives back the ground the clip covered.
        /// </summary>
        /// <remarks>
        /// <b>An absolute clip never does.</b> The original writes it as one line —
        /// <c>allowMove = allowMove || absolute</c> in <c>AnimationNodes</c> — and it
        /// follows from what absolute means: the clip says where in the room it happens, so
        /// putting the actor back where they were is undoing the only thing the clip was
        /// for. Emilio walks out of the hotel through an absolute clip, and without this he
        /// was returned to the spot he was standing on before he opened the door.
        /// </remarks>
        public bool Reverts => !_moves && !_absolute;

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
            ActFile clip,
            PlacedModel target,
            AnimationPlacement? placement,
            Matrix4x4 standing,
            Actors.CharacterConfig? character,
            bool carried)
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

            // A <b>carried</b> clip is already in the space it is played in — the holder's,
            // which the model is pinned to for as long as the binding lasts — so it plays
            // exactly as authored and there is nothing to correct. It matters only for the
            // handful of clips that carry a person rather than a prop, <c>DemTe6KillGabe</c>
            // among them: a prop's correction is the identity anyway, but an actor's would
            // shift the clip to their own rest and undo the binding.
            if (carried || target.Kind != PlacedModelKind.Actor)
            {
                return Matrix4x4.Identity;
            }

            // A relative clip plays facing whichever way the actor already faces, and the
            // turn the clip was authored with is taken back out. That is the reference's
            // rule — GKActor::StartAnimation and SampleAnimation both end in
            // SetModelRotationToActorRotation, which measures the posed model's facing and
            // rotates it to the actor's heading — and applying the clip's rotation raw on
            // top of the placement is what stood the museum's Estelle and Lady Howard back
            // to back: their opening clip is authored with a turn in it.
            //
            // Measured the way the reference measures it when nothing is animating the
            // facing helper: the triangle of the hip and shoe mesh origins, whose normal is
            // the facing outright. No dot product and no rare branch.
            Matrix4x4 turn = Matrix4x4.Identity;

            if (character is { Hips: { } hips, LeftShoe: { } left, RightShoe: { } right } &&
                clip.PoseOf(hips.Mesh, 0) is { } hipPose &&
                clip.PoseOf(left.Mesh, 0) is { } leftPose &&
                clip.PoseOf(right.Mesh, 0) is { } rightPose)
            {
                Vector3 across = rightPose.Translation - leftPose.Translation;
                Vector3 up = hipPose.Translation - leftPose.Translation;
                Vector3 facing = Vector3.Cross(across, up) with { Y = 0 };

                if (facing.LengthSquared() > 1e-6f)
                {
                    // Which way the model is built to face, which is what the placement's
                    // rotation assumes it is looking along. The clip is turned so that its
                    // opening frame looks that way too, and the placement then turns both
                    // together to the actor's heading.
                    float built = target.BuiltFacing ?? MathF.PI;
                    float authored = Navigation.Walker.Heading(facing);

                    turn = Matrix4x4.CreateRotationY(Navigation.Walker.Wrapped(built - authored));
                }
            }

            Vector3 rest = Average(target.Model.Meshes.Select(m => m.MeshToLocal.Translation));

            // Where the clip's meshes open once the turn is taken out of them, so the
            // translation that brings them to the model's own rest lands them there and not
            // where they would have been before the turn.
            Vector3 opens = Average(Enumerable
                .Range(0, clip.MeshCount)
                .Select(m => clip.PoseOf(m, 0))
                .Where(p => p is not null)
                .Select(p => Vector3.Transform(p!.Value.Translation, turn)));

            return turn * Matrix4x4.CreateTranslation(rest - opens);
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

        /// <summary>Poses the model on the clip's closing frame, without running it.</summary>
        /// <param name="geometry">Where the model stands.</param>
        /// <remarks>
        /// The other end of <see cref="Open"/>: where a thing has been put, rather than
        /// where it started. Same rule — the pose is applied and the clock never starts.
        /// </remarks>
        public void Last(ISceneSink geometry) => Pose(geometry, Math.Max(0, Clip.FrameCount - 1));

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
        public Vector3 Settled(Matrix4x4 standing) => Standing(standing, 0f);

        /// <summary>Where the clip has the character's feet on the frame it is on.</summary>
        /// <param name="standing">The model's placement.</param>
        /// <returns>The spot, in the room.</returns>
        /// <remarks>
        /// <see cref="Settled"/> at the frame last posed rather than at the opening one. What
        /// the actor's position follows while a move clip plays: reading the opening frame
        /// every frame left Emilio logically at the lobby door for the whole of his walk to
        /// the bench, however far along it he was drawn.
        /// </remarks>
        public Vector3 Now(Matrix4x4 standing) => Standing(standing, Frame);

        /// <summary>Which way the clip has the character facing on the frame it is on.</summary>
        /// <param name="standing">The model's placement.</param>
        /// <returns>The heading, or null when the clip does not pose the hips.</returns>
        /// <remarks>
        /// What a head's glance measures against. The placement's own heading is wrong for
        /// as long as an absolute clip has the body somewhere else, and a glance worked out
        /// against the wrong body direction turns the head the wrong way by exactly that
        /// difference.
        /// </remarks>
        public float? Facing(Matrix4x4 standing) =>
            _character is null
                ? null
                : Actors.AnimationStart.FacingAt(
                    Clip, Frame, _repeat, _character, _correction * standing, Target.BuiltFacing);

        private Vector3 Standing(Matrix4x4 standing, float frame)
        {
            Matrix4x4 world = _correction * standing;

            // The hips, for the same reason the running pose uses them: this is a place
            // read out of a clip rather than a distance measured across one.
            return (_character is null
                ? null
                : Actors.AnimationStart.Standing(Clip, frame, _repeat, _character, world))
                ?? Vector3.Transform(_opened, world);
        }

        /// <summary>Whether the clip says where in the room it happens.</summary>
        public bool Absolute => _absolute;

        /// <summary>
        /// The shift <see cref="Correction"/> settled on, so a held model can follow it.
        /// </summary>
        /// <remarks>
        /// The model's placement is where the scene stood it; this is the rest of what its
        /// clip is being played through, and the two together are the space anything the
        /// model is holding is pinned to. See <c>SceneUpdate.ModelSpace</c>.
        /// </remarks>
        public Matrix4x4 Space => _correction;

        /// <summary>
        /// Puts one mesh group where the clip says, in the picture and on the model.
        /// </summary>
        /// <remarks>
        /// Both, because they answer different questions and both are asked. The sink draws
        /// it; the model is what the picker reads to know where the thing the player is
        /// pointing at has got to. Writing only the first left Emilio sitting in the
        /// loveseat with his hotspot still standing where his model file put him.
        /// </remarks>
        private void Put(ISceneSink geometry, int mesh, Matrix4x4 meshToLocal)
        {
            geometry.PoseMesh(Target.Placement, mesh, meshToLocal);
            Target.Pose(mesh, meshToLocal);
        }

        /// <summary>Where the clip's mesh groups sit on its opening frame.</summary>
        private static Vector3 Opens(ActFile clip) => Average(Enumerable
            .Range(0, clip.MeshCount)
            .Select(m => clip.PoseOf(m, 0))
            .Where(p => p is not null)
            .Select(p => p!.Value.Translation));

        /// <summary>The frame the model was last posed on.</summary>
        public float Frame { get; private set; }

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
            Frame = at;

            // Where the clip has carried the model to, in the world's terms rather than the
            // model's. This is what the actor's position follows.
            if (Began is { } from)
            {
                Vector3 here = Average(Enumerable
                    .Range(0, Clip.MeshCount)
                    .Select(m => Clip.PoseAt(m, at, _repeat))
                    .Where(p => p is not null)
                    .Select(p => p!.Value.Translation));

                // An absolute clip says where in the room it happens, so where it has got
                // to is a place rather than a distance from wherever the actor happened to
                // be standing. Measuring it as a distance is what left Emilio's position at
                // the spot he was hidden at while his model walked out of the hotel — and
                // the walk to his bench then set off from there and found no route.
                Matrix4x4 world = _correction * geometry.TransformOf(Target.Placement);

                // The hips where the character has them, and the average of the mesh
                // origins only where nothing says. The two move together, so a difference
                // of averages is exact and free — but a single average is that answer plus
                // the constant between a torso's middle and the floor, and an absolute clip
                // is read as a place rather than differenced.
                Carried = _absolute
                    ? (_character is null
                        ? null
                        : Actors.AnimationStart.Standing(Clip, at, _repeat, _character, world))
                      ?? Vector3.Transform(here, world)
                    : from + Vector3.TransformNormal(here - _opened, Target.Transform);
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
                            Put(geometry, mesh, turn * placed * _correction);
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
                        Put(geometry, mesh, carried * _correction);
                    }

                    continue;
                }

                if (pose is { } value)
                {
                    Put(geometry, mesh, value * _correction);
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
            Built = placed.BuiltFacing;

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

        /// <summary>Which way this model is built to face, when its arrow says.</summary>
        public float? Built { get; }
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

        /// <summary>
        /// The feet the stride puts down, and where it had got to when it was last asked.
        /// </summary>
        /// <remarks>
        /// A walk is played here rather than through <see cref="Play"/> — the stride is
        /// looped by frame and carries no schedule of its own — so nothing else can notice
        /// the animation's footstep nodes. Which meant that of all the things in the game
        /// that make a footstep noise, walking was the one that did not.
        /// </remarks>
        private readonly IReadOnlyList<AnimationStep> _steps;

        private int _lastFrame = -1;

        private WalkCycle(
            ActFile clip,
            PlacedModel target,
            Matrix4x4 rest,
            float opens,
            float pace,
            IReadOnlyList<AnimationStep> steps)
        {
            _clip = clip;
            _target = target;
            _rest = rest;
            _opens = opens;
            _steps = steps;
            Pace = pace;

            // A stride is authored so that its last frame repeats its first — Gabriel's
            // twenty-first frame is his first, agreeing to two thousandths of a unit in
            // sway and exactly in bob. Looping over all of them shows that pose twice and
            // the walk hitches once a stride.
            _period = Closes(clip) ? clip.FrameCount - 1 : clip.FrameCount;
        }

        /// <summary>
        /// Which animation this character walks with.
        /// </summary>
        /// <remarks>
        /// Whatever a script last handed them, and <c>CHARACTERS.TXT</c> otherwise. See
        /// <see cref="SetStride"/>.
        /// </remarks>
        private static string? Named(
            PlacedModel target, Actors.CharacterLibrary? characters, string? replaced) =>
            replaced is { Length: > 0 } ? replaced : characters?.Of(target.Name)?.WalkAnimation;

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
        /// <remarks>
        /// The recorded pose, held rather than mixed. Everything that measures the clip
        /// rather than plays it asks this: how far the stride travels, which sets the pace
        /// the feet have to match, and whether the last frame closes back onto the first.
        /// Mixing into the answer would bend the pace towards a wrap that has not happened.
        /// </remarks>
        private static Vector3 Mean(ActFile clip, int frame) => Average(Enumerable
            .Range(0, clip.MeshCount)
            .Select(m => clip.PoseOf(m, frame))
            .Where(p => p is not null)
            .Select(p => p!.Value.Translation));

        /// <summary>Where they sit at a moment between two frames.</summary>
        /// <remarks>
        /// What the stride uses while it plays, so the forward travel comes out at the same
        /// moment of the clip that poses the meshes. Taking it from a whole frame while the
        /// legs are mixed between two is the difference between a walk and a skate.
        /// </remarks>
        private static Vector3 MeanAt(ActFile clip, float frame) => Average(Enumerable
            .Range(0, clip.MeshCount)
            .Select(m => clip.PoseAt(m, frame, cycles: true))
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
            Content.ClipLibrary? clips,
            string? replaced = null)
        {
            if (Named(target, characters, replaced) is not { Length: > 0 } named ||
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
                    clip, target, rest, opens, (float)(travel / clip.Duration), animation.Steps);
            }

            return null;
        }

        /// <summary>Which feet went down on the frame just stepped, if any.</summary>
        public IReadOnlyList<AnimationStep> Landed { get; private set; } = Nothing;

        private static readonly List<AnimationStep> Nothing = [];

        /// <summary>
        /// The footstep nodes between the last frame shown and this one.
        /// </summary>
        /// <remarks>
        /// A range rather than an equality, because a stride is twenty frames and a frame
        /// of the game is a sixtieth of a second: at any pace above walking the loop skips
        /// frames, and a step tested for exactly would be missed. The wrap at the end of
        /// the loop is the other half of the same problem.
        /// </remarks>
        private List<AnimationStep> Feet(int frame)
        {
            if (_steps.Count == 0 || frame == _lastFrame)
            {
                return Nothing;
            }

            List<AnimationStep> fell = [];

            foreach (AnimationStep step in _steps)
            {
                bool inside = _lastFrame < frame
                    ? step.Frame > _lastFrame && step.Frame <= frame
                    : step.Frame > _lastFrame || step.Frame <= frame;

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
        /// <remarks>
        /// <para>
        /// <b>Between the recorded frames, not on them.</b> A stride is recorded at fifteen
        /// poses a second and drawn at sixty or more, so showing the recorded poses as they
        /// stand shows each of them four times and the legs arrive in four equal jumps.
        /// Every other clip in the game has gone through <see cref="ActFile.PoseAt"/> and
        /// been mixed since the day it was written; this one asked for whole frames, which
        /// is why walking was the one thing that still read as 1999 while the rest of a
        /// character did not.
        /// </para>
        /// <para>
        /// The forward travel is taken out at the same moment of the clip that poses the
        /// meshes, or the body's offset steps while its legs slide and the feet skate.
        /// </para>
        /// <para>
        /// The footsteps still go by whole frames. A footstep is an event on a numbered
        /// frame rather than a quantity to be mixed, and asking for it twice because a
        /// moment landed either side of one is a doubled sound.
        /// </para>
        /// </remarks>
        public void Step(ISceneSink geometry, float seconds)
        {
            _elapsed += Math.Max(0, seconds) * Math.Max(0.01f, Rate);

            // Looped, and seamlessly: with the forward travel removed, the last frame sits
            // exactly where the first does, so the join is invisible.
            float at = (float)(_elapsed * AnimationFile.FramesPerSecond % _period);
            int frame = (int)at;

            Landed = Feet(frame);
            _lastFrame = frame;

            Matrix4x4 correction =
                Matrix4x4.CreateTranslation(0, 0, _opens - ForwardAt(_clip, at)) * _rest;

            for (int mesh = 0; mesh < _clip.MeshCount; mesh++)
            {
                if (_clip.PoseAt(mesh, at, cycles: true) is { } pose)
                {
                    geometry.PoseMesh(_target.Placement, mesh, pose * correction);
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

            // The placement is a turn about the up axis and then a move, so the way the
            // actor faces can be read straight back out of it — as a heading rather than as
            // the rotation itself, because a glance is worked out from a heading.
            _built = placed.BuiltFacing;
            _facing = HeadingOf(placed.Transform);
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
        /// <summary>Which way this model is built to face, when its arrow says.</summary>
        private readonly float? _built;

        /// <summary>The heading a placement of this model amounts to.</summary>
        /// <remarks>
        /// The inverse of what turned it. A model built facing something other than −Z is
        /// turned by a different amount, so reading the heading back has to undo the same
        /// amount — otherwise a head is worked out against a body direction the body does
        /// not have.
        /// </remarks>
        public float HeadingOf(Matrix4x4 placement)
        {
            float turned = MathF.Atan2(placement.M31, placement.M33);

            return _built is { } forward
                ? Navigation.Walker.Wrapped(turned + forward)
                : Navigation.Walker.Rotation(turned);
        }

        /// <summary>Where the model is standing and which way it faces, this frame.</summary>
        /// <param name="standing">Its position.</param>
        /// <param name="facing">Its heading.</param>
        /// <remarks>
        /// <para>
        /// Read from the model every frame rather than remembered. It used to be told, by the
        /// walking loop, under whichever name the walk had been asked for — and a walk is
        /// asked for by noun as often as by model name, so <c>MovedTo("EMILIO")</c> never
        /// matched a head filed under <c>eml</c> and the update was silently dropped.
        /// </para>
        /// <para>
        /// Emilio is the case that shows it. His scene gives him no position at all, so his
        /// facing began as the heading of the identity transform — which is a half turn — and
        /// nothing ever replaced it. He walked to the bench with his head pointing at the
        /// door, by exactly 180 degrees, for as long as this cached anything.
        /// </para>
        /// <para>
        /// Nothing to keep in step is the point. A character is moved by walking, by an
        /// animation, by a script placing them and by an opening pose, and a cache has to be
        /// updated from all four; the model's own transform is already the answer to all four.
        /// </para>
        /// </remarks>
        public void Stands(Vector3 standing, float facing)
        {
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

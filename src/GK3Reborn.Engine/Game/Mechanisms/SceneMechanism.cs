using System.Numerics;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>A control a mechanism asks the interface to draw for it.</summary>
/// <param name="Verb">What it says, in the words the verb bar uses: LET GO, GRAB.</param>
/// <param name="Ready">
/// Whether pressing it now would do anything. False draws it dim and swallows the press,
/// which is what a control the player is waiting for a moment to use has to do.
/// </param>
public readonly record struct MechanismButton(string Verb, bool Ready);

/// <summary>
/// The code a room needs that its data cannot express.
/// </summary>
/// <remarks>
/// <para>
/// <b>GK3 is very nearly data-driven, and this is where it is not.</b> Eleven scenes
/// declare <c>custom=</c> in their initialisation file, and each names something the
/// shipped executable implemented in C: five laser heads turning on a circle, a giant
/// chessboard that opens trapdoors, a pendulum swinging across a chasm. Their scripts
/// reach it through one call — <c>CallSceneFunction("toggleLasers")</c> — which sends a
/// word to whatever the room declared.
/// </para>
/// <para>
/// <b>Every one of the corpus's 43 <c>CallSceneFunction</c> calls names one of these.</b>
/// Not one names a Sheep function, so a port that resolves them as Sheep — which is what
/// this one did — finds nothing, reports nothing and leaves the puzzle inert. The button
/// under Montreaux's desk clicks, plays its sound, and does not turn the lasers on.
/// </para>
/// <para>
/// <b>A mechanism owns objects the room already has.</b> None of them adds geometry: the
/// heads, the lasers, the chess tiles and the pendulum are all declared in the scene file
/// like any other prop, usually <c>hidden</c>, and the mechanism moves, shows and repaints
/// them. That is why this is given the room's <see cref="SceneUpdate"/> rather than the
/// renderer — it is the same thing a script gets.
/// </para>
/// <para>
/// <b>Waiting is answered before the work is done.</b> A script wrapping
/// <c>CallSceneFunction</c> in a wait block asks how long it will take before it is
/// invoked — see <c>SheepVirtualMachine</c> — so <see cref="Seconds"/> must be able to
/// price a call it has not performed yet. Every mechanism here can: what a turn costs is
/// the length of the animation it is about to play.
/// </para>
/// </remarks>
public abstract class SceneMechanism
{
    /// <summary>Creates the mechanism for one standing room.</summary>
    /// <param name="world">The room, for its models, its clips and its clock.</param>
    /// <param name="api">
    /// The script host — for the story's flags and counts, and for the handful of calls a
    /// mechanism has to make itself. Reached through the host rather than implemented
    /// again, so that a mechanism saying a line and a script saying one are the same code.
    /// </param>
    protected SceneMechanism(SceneUpdate world, Gk3SheepApi api)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(api);

        World = world;
        Api = api;
    }

    /// <summary>The room this belongs to.</summary>
    protected SceneUpdate World { get; }

    /// <summary>The script host.</summary>
    protected Gk3SheepApi Api { get; }

    /// <summary>The story it writes its state into.</summary>
    protected GameState Story => Api.State;

    /// <summary>
    /// Whether the player has asked not to be killed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Playing page's "Gabriel cannot be killed". <see cref="Assists.IsDeath"/> already
    /// covers the deaths that arrive as a script's <c>Die$</c> — it runs the retry the game
    /// itself offers, in its place. The three temple puzzles are the deaths that do
    /// <em>not</em>: the killing is decided here, in code, from where the blade is and
    /// which tile has gone out, and by the time a script is called the player is already
    /// dead. So each of them asks this before it kills, and puts the player safely back
    /// instead.
    /// </para>
    /// <para>
    /// It removes the dying, not the puzzle: whoever it saves is returned to the start of
    /// the attempt with nothing else given to them, which is exactly what choosing "retry"
    /// on the original's death screen does.
    /// </para>
    /// </remarks>
    protected bool Deathless => Story.PlotArmour;

    /// <summary>
    /// Says a line of dialogue, once whatever is happening has finished happening.
    /// </summary>
    /// <param name="plate">The licence plate the recording is filed under.</param>
    /// <param name="lines">How many lines of it to play.</param>
    /// <remarks>
    /// Deferred by a frame on purpose. A mechanism is called from inside an action that is
    /// still running, and starting a line inside another one cuts the first off — which is
    /// what the reference's <c>WaitForActionsToComplete</c> is avoiding in the two places
    /// it does this.
    /// </remarks>
    protected void Say(string plate, int lines = 1)
    {
        ArgumentNullException.ThrowIfNull(plate);

        World.Next(() => Api.Invoke(
            "StartDialogue",
            [SheepValue.FromString(plate), SheepValue.FromInt(lines)]));
    }

    /// <summary>
    /// Does something once a stretch of time has passed, or next frame if it is none.
    /// </summary>
    /// <param name="seconds">How long, usually the length of the clip just started.</param>
    /// <param name="work">What to do then.</param>
    /// <remarks>
    /// Every one of these mechanisms chains animations — turn, jump, land, turn back — and
    /// a clip the archives cannot supply is worth no seconds. Falling through to the next
    /// frame keeps the chain running rather than leaving the player mid-jump for ever.
    /// </remarks>
    protected void Then(double seconds, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!World.After(seconds, work))
        {
            World.Next(work);
        }
    }

    /// <summary>What the scene file called it.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Sets the room up, once, before anything runs in it.
    /// </summary>
    /// <remarks>
    /// The reference calls this <c>&lt;location&gt;-init</c> and runs it from
    /// <c>Scene::Load</c> rather than from a script, which is why no file anywhere calls
    /// it: it is the mechanism putting its own objects where they belong.
    /// </remarks>
    public virtual void Begin()
    {
    }

    /// <summary>
    /// How long a call will take, asked before it is made.
    /// </summary>
    /// <param name="asked">The word the script sent.</param>
    /// <returns>Seconds, or zero for a call that finishes in the frame it starts.</returns>
    public virtual double Seconds(string asked) => 0;

    /// <summary>
    /// Performs a call.
    /// </summary>
    /// <param name="asked">The word the script sent.</param>
    /// <returns>True when this mechanism knew the word.</returns>
    public abstract bool Perform(string asked);

    /// <summary>
    /// What it found to work with, for the log.
    /// </summary>
    /// <remarks>
    /// Every one of these owns props the scene file declares, and a mechanism that found
    /// none of them does nothing and says nothing — which is the failure this whole family
    /// exists to stop being silent. Asked after <see cref="Begin"/>.
    /// </remarks>
    public virtual string Report() => string.Empty;

    /// <summary>Moves the mechanism on.</summary>
    /// <param name="seconds">How much time has passed.</param>
    public virtual void Advance(double seconds)
    {
    }

    /// <summary>
    /// How much of the picture is being paid for, as the player has it set.
    /// </summary>
    /// <remarks>
    /// Handed over each frame by the launcher. One mechanism reads it: CS2's beams are
    /// drawn as light rather than as red plastic only where there is a lighting model good
    /// enough to make that look like anything, and a room lit by a 1999 bake is not one.
    /// </remarks>
    public RayTracingQuality Tracing { get; set; }

    /// <summary>
    /// Anything the mechanism wants drawn in the blended pass, nearest last.
    /// </summary>
    /// <param name="eye">Where the camera is, for facing and for sorting.</param>
    /// <returns>The sprites, or nothing — which is what all but one of these answer.</returns>
    /// <remarks>
    /// The renderer's material pass is a deferred G-buffer and cannot blend a thing; the
    /// particle pass is the one place in the engine where something may be see-through.
    /// See <c>Rendering.Shaders.ParticleShaders</c>.
    /// </remarks>
    public virtual IReadOnlyList<Particle> Particles(Vector3 eye) => [];

    /// <summary>
    /// Lights the mechanism adds to the room's rig, or nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A self-lit surface lights nothing.</b> It is drawn at full brightness and skips
    /// shading — that is the whole of what the flag means — so a laser beam marked emissive
    /// is a bright red line in a room that is exactly as dark as it was. Anything that is
    /// supposed to cast light on the floor under it has to be in the rig, the same way a
    /// fire the artists left dark gets a light synthesized for it. See
    /// <c>Game.FlameLighting</c>.
    /// </para>
    /// <para>
    /// Read only when <see cref="LightsMoved"/> says so, because laying a rig rebuilds the
    /// scene's light grid and that is a per-room cost rather than a per-frame one.
    /// </para>
    /// </remarks>
    public virtual IReadOnlyList<Formats.Scenes.AuthoredLight> Lights => [];

    /// <summary>
    /// Whether the lights have changed since the last time they were asked for.
    /// </summary>
    /// <remarks>
    /// Answering true clears it: the caller lays the rig and the mechanism goes quiet until
    /// something moves again. A room whose lights never move never answers true at all,
    /// which is every one of these but CS2.
    /// </remarks>
    public virtual bool LightsMoved => false;

    /// <summary>
    /// Told what the pointer is over, every frame.
    /// </summary>
    /// <param name="under">What the ray met, or null for empty air.</param>
    /// <param name="busy">Whether the story is in the middle of something.</param>
    /// <remarks>
    /// <b>Hovering is part of one puzzle's rules.</b> The chessboard decides whether the
    /// tile under the pointer is a legal knight's move <em>before</em> it is clicked, and
    /// writes the answer into <c>Te1MoveType</c> — which is what the action file's case
    /// asks about, and therefore what decides which of three scripts a click runs. Nothing
    /// else here needs it, and reading the pointer is free of consequence everywhere else.
    /// </remarks>
    public virtual void Pointing(Interaction.ScenePick? under, bool busy)
    {
    }

    /// <summary>
    /// Claims a click <em>before</em> the action files are consulted.
    /// </summary>
    /// <param name="under">What is under the pointer, or null for empty air.</param>
    /// <returns>
    /// Null to leave the click alone; otherwise the mechanism takes it, and the string is
    /// what to offer the player as the thing the click will do — empty for a click that
    /// is swallowed rather than advertised.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="TakesClick"/> is not enough for a thing that has a noun.</b> That hook
    /// is asked only once a click has failed to resolve an action, which is the right rule
    /// for the chessboard — its tiles carry no noun and nothing else wants them. It is the
    /// wrong rule wherever the object the mechanism needs is also an object the action
    /// files answer for, because the action always wins and the mechanism never hears about
    /// it. TE3's blade is exactly that: the scene gives it <c>noun=PENDULUM</c> and
    /// <c>TE3309P.NVC</c> gives PENDULUM a LOOK for ALL, so every click on the swinging
    /// blade played Gabriel's line about it and the grab could not be performed at all.
    /// </para>
    /// <para>
    /// The original has no such problem because it never asks the action files: TE3's whole
    /// click handler is one function keyed on the model name, which recognises
    /// <c>te3_pendulum_center_code</c>, grabs, and returns "handled". This is that, kept to
    /// the moments the mechanism actually wants — the room stays the player's the rest of
    /// the time.
    /// </para>
    /// </remarks>
    public virtual string? ClaimsClick(Interaction.ScenePick? under) => null;

    /// <summary>
    /// A button of its own the mechanism asks the interface to put on the screen.
    /// </summary>
    /// <returns>What to offer and whether it may be taken yet, or null for nothing.</returns>
    /// <remarks>
    /// <para>
    /// <b>For the one thing a player has no way of finding.</b> Everything else a mechanism
    /// offers is attached to something in the room — the blade says GRAB while it is within
    /// reach, a chess tile is a tile — and sweeping the pointer over the room is how a
    /// player finds it. That fails where the thing to click is neither near the pointer nor
    /// recognisable as a target: hanging off TE3's blade over the shaft, the only way on is
    /// a click on the altar, a small stone slab a long way below and behind him. Nothing
    /// says so, and the room's one exit was in practice unreachable.
    /// </para>
    /// <para>
    /// Drawn dim rather than taken away while it may not be taken yet. A button that comes
    /// and goes is a timing cue the player has to learn to read; one that lights up is the
    /// same cue where they are already looking.
    /// </para>
    /// </remarks>
    public virtual MechanismButton? Offers => null;

    /// <summary>Performs whatever <see cref="Offers"/> is offering.</summary>
    /// <remarks>
    /// Asked only when the player pressed the button. It may still decline — the button is
    /// drawn for the whole of a moment and live for part of it, and which part is the
    /// mechanism's business rather than the interface's.
    /// </remarks>
    public virtual void Press()
    {
    }

    /// <summary>
    /// Takes a click that would otherwise fall through to the room.
    /// </summary>
    /// <param name="under">What was clicked, or null for empty air.</param>
    /// <returns>True when the mechanism dealt with it and nothing else should.</returns>
    /// <remarks>
    /// Asked after the click has failed to find an action to perform and before it is
    /// treated as somewhere to walk. The chessboard is the caller: its tile floor carries
    /// no noun, so a click on it resolves nothing, and the room wants it to mean "jump back
    /// off the board". Where the thing clicked <em>does</em> carry a noun, this is never
    /// reached and <see cref="ClaimsClick"/> is the hook.
    /// </remarks>
    public virtual bool TakesClick(Interaction.ScenePick? under) => false;

    /// <summary>
    /// Takes a click on the floor, where the room does not let the player simply walk.
    /// </summary>
    /// <returns>True when the mechanism dealt with it and nobody should walk.</returns>
    /// <remarks>
    /// One room does: in TE6 Gabriel is circling a pentagram with a demon in it and cannot
    /// be sent wandering, so a click on the floor sets a flag the room's own script reads
    /// and plays one of its move animations. The reference calls the same hook a
    /// <em>walk override</em>.
    /// </remarks>
    public virtual bool TakesFloorClick() => false;

    /// <summary>Puts one of the room's props under an arbitrary transform.</summary>
    /// <param name="model">The prop.</param>
    /// <param name="transform">Where and how it goes.</param>
    /// <remarks>
    /// For the pendulum, which swings about an axis no heading can express: it turns about
    /// the room's Z and hangs from a pivot two thousand units above itself.
    /// </remarks>
    protected void Put(PlacedModel model, Matrix4x4 transform)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Placement.Exists)
        {
            World.Geometry.MoveModel(model.Placement, transform);
        }
    }

    /// <summary>Cuts the camera to one of the room's named angles.</summary>
    /// <param name="angle">What the scene file calls it.</param>
    /// <remarks>
    /// Forced, because these are the shots that hide a model being swapped or a player
    /// being teleported onto a platform, and a player who has turned cinematics off must
    /// still not see the join.
    /// </remarks>
    protected void Cut(string angle)
    {
        ArgumentNullException.ThrowIfNull(angle);

        Api.Invoke("ForceCutToCameraAngle", [SheepValue.FromString(angle)]);
    }

    /// <summary>Puts one of the room's props somewhere, facing a way.</summary>
    /// <param name="model">The prop.</param>
    /// <param name="position">Where to put it, in world space.</param>
    /// <param name="heading">Which way it faces, as the game's data measures a heading.</param>
    /// <param name="scale">How big, or null to leave it at its own size.</param>
    /// <remarks>
    /// <b>No half turn.</b> <see cref="Navigation.Walker.Rotation"/> adds one because GK3's
    /// <em>characters</em> are modelled facing −Z; a prop is not, and the reference's
    /// <c>GKObject::SetHeading</c> applies the heading outright. Turning a laser head
    /// through a further 180° puts its beam through the wall behind it.
    /// </remarks>
    protected void Stand(PlacedModel model, Vector3 position, float heading, Vector3? scale = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (World.Geometry is not { } geometry || !model.Placement.Exists)
        {
            return;
        }

        geometry.MoveModel(
            model.Placement,
            Matrix4x4.CreateScale(scale ?? Vector3.One) *
            Matrix4x4.CreateRotationY(heading) *
            Matrix4x4.CreateTranslation(position));
    }
}

/// <summary>
/// Which code each room needs.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on the scene file's own <c>custom=</c> where there is one, because that is the
/// game saying so. The reference keys the same table off the location name, which comes to
/// the same thing for all eleven and cannot express a room declaring nothing.
/// </para>
/// <para>
/// <b>Two rooms declare a mechanism nobody wrote.</b> <c>BET</c> and <c>CS8202P</c> say
/// <c>custom=Langolier</c> and no script anywhere sends them a word, so there is nothing
/// for the code to do and nothing is built. Named here so the next reader does not go
/// looking for it.
/// </para>
/// </remarks>
public static class SceneMechanisms
{
    /// <summary>Builds the mechanism a room declares, where one is written.</summary>
    /// <param name="declared">What the scene file's <c>custom=</c> says, or null.</param>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    /// <param name="archives">
    /// The game's files, for the one mechanism with a data file of its own. Optional: a
    /// tool with no archives still builds the rest of them.
    /// </param>
    /// <returns>The mechanism, or null when the room needs none or none is written.</returns>
    public static SceneMechanism? For(
        string? declared, SceneUpdate world, Gk3SheepApi api, Content.GameArchives? archives = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(api);

        return declared?.Trim().ToUpperInvariant() switch
        {
            "LASER" => new LaserHeads(world, api),
            "ANGELS" => new AngelTracing(world, api),
            "COORDINATEDEVICE" => new CoordinateDevice(world, api) { Archives = archives },
            "HOLY" => new DemonFight(world, api),
            "CHESS" => new Chessboard(world, api),
            "BRIDGE" => new Bridge(world, api),
            "CIRCLE" => new Pendulum(world, api) { Archives = archives },

            // Langolier is declared by BET and CS8202P and called by nothing.
            _ => Patched(api.State.Location, world, api),
        };
    }

    /// <summary>
    /// The rooms whose code is a patch rather than a mechanism.
    /// </summary>
    /// <remarks>
    /// Keyed by location, because these declare no <c>custom=</c>: the original ran
    /// <c>&lt;location&gt;-init</c> on <em>every</em> scene load, and four rooms use that
    /// to fix something their own data gets wrong. See <see cref="RoomPatches"/>.
    /// </remarks>
    private static RoomPatches? Patched(string location, SceneUpdate world, Gk3SheepApi api) =>
        location.ToUpperInvariant() is "LBY" or "MS3" or "CD1" or "CSE"
            ? new RoomPatches(location, world, api)
            : null;
}

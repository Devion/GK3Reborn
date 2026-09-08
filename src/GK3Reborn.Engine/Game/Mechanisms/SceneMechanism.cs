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
    protected bool Deathless => Story.PlotArmour;

    /// <summary>
    /// Says a line of dialogue, once whatever is happening has finished happening.
    /// </summary>
    /// <param name="plate">The licence plate the recording is filed under.</param>
    /// <param name="lines">How many lines of it to play.</param>
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
    public virtual string Report() => string.Empty;

    /// <summary>Moves the mechanism on.</summary>
    /// <param name="seconds">How much time has passed.</param>
    public virtual void Advance(double seconds)
    {
    }

    /// <summary>
    /// How much of the picture is being paid for, as the player has it set.
    /// </summary>
    public RayTracingQuality Tracing { get; set; }

    /// <summary>
    /// Anything the mechanism wants drawn in the blended pass, nearest last.
    /// </summary>
    /// <param name="eye">Where the camera is, for facing and for sorting.</param>
    /// <returns>The sprites, or nothing — which is what all but one of these answer.</returns>
    public virtual IReadOnlyList<Particle> Particles(Vector3 eye) => [];

    /// <summary>
    /// Lights the mechanism adds to the room's rig, or nothing.
    /// </summary>
    public virtual IReadOnlyList<Formats.Scenes.AuthoredLight> Lights => [];

    /// <summary>
    /// Whether the lights have changed since the last time they were asked for.
    /// </summary>
    public virtual bool LightsMoved => false;

    /// <summary>
    /// Told what the pointer is over, every frame.
    /// </summary>
    /// <param name="under">What the ray met, or null for empty air.</param>
    /// <param name="busy">Whether the story is in the middle of something.</param>
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
    public virtual string? ClaimsClick(Interaction.ScenePick? under) => null;

    /// <summary>
    /// A button of its own the mechanism asks the interface to put on the screen.
    /// </summary>
    /// <returns>What to offer and whether it may be taken yet, or null for nothing.</returns>
    public virtual MechanismButton? Offers => null;

    /// <summary>Performs whatever <see cref="Offers"/> is offering.</summary>
    public virtual void Press()
    {
    }

    /// <summary>
    /// Takes a click that would otherwise fall through to the room.
    /// </summary>
    /// <param name="under">What was clicked, or null for empty air.</param>
    /// <returns>True when the mechanism dealt with it and nothing else should.</returns>
    public virtual bool TakesClick(Interaction.ScenePick? under) => false;

    /// <summary>
    /// Takes a click on the floor, where the room does not let the player simply walk.
    /// </summary>
    /// <returns>True when the mechanism dealt with it and nobody should walk.</returns>
    public virtual bool TakesFloorClick() => false;

    /// <summary>Puts one of the room's props under an arbitrary transform.</summary>
    /// <param name="model">The prop.</param>
    /// <param name="transform">Where and how it goes.</param>
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
    private static RoomPatches? Patched(string location, SceneUpdate world, Gk3SheepApi api) =>
        location.ToUpperInvariant() is "LBY" or "MS3" or "CD1" or "CSE"
            ? new RoomPatches(location, world, api)
            : null;
}

using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>What a placed model is: a prop, an actor, or the player.</summary>
public enum PlacedModelKind
{
    /// <summary>Scenery loaded from a <c>.MOD</c> file — a lamp, a chair, a note.</summary>
    Prop,

    /// <summary>A character the scene puts in the room.</summary>
    Actor,
}

/// <summary>
/// A model the scene loaded from a file, and where it stands.
/// </summary>
/// <remarks>
/// Kept because the geometry the renderer holds is a one-way trip: it goes to the GPU in
/// whatever batches suit drawing, and nothing there can say which triangle belonged to
/// which prop. Anything that has to answer a question about an object after loading —
/// what did this click land on, what is the player standing next to — needs the model as
/// it was placed, so the loader hands it back.
/// </remarks>
/// <param name="Name">The model's own name, without an extension.</param>
/// <param name="Noun">The noun it answers to, if the scene gives it one.</param>
/// <param name="Verb">The verb a click does by default, if the scene names one.</param>
/// <param name="Model">The parsed mesh, in its own space.</param>
/// <param name="Transform">Where it stands, applied after each mesh's own transform.</param>
/// <param name="Kind">Whether it is scenery or a character.</param>
/// <param name="Placement">
/// Where it went in the geometry, so its parts can still be moved. A character has no
/// skeleton, so this is the only handle there is on a head.
/// </param>
public sealed record PlacedModel(
    string Name,
    string? Noun,
    string? Verb,
    ModFile Model,
    Matrix4x4 Transform,
    PlacedModelKind Kind,
    ModelPlacement Placement = default)
{
    /// <summary>The script that drives it on its own, or null.</summary>
    public string? Gas { get; init; }

    /// <summary>Where it is standing, so that where it stands now can be asked.</summary>
    /// <remarks>
    /// The sink owns the live transform: <c>MoveModel</c> writes it every time an actor
    /// takes a step, and nothing writes it back here. Optional, because a model can be
    /// built without a room to stand in — the tests do — and then where it was placed is
    /// all there is to say.
    /// </remarks>
    public ISceneSink? Stage { get; init; }

    /// <summary>
    /// Where it stands <em>now</em>, rather than where the scene first put it.
    /// </summary>
    /// <remarks>
    /// <see cref="Transform"/> is where it was placed and never changes. An actor crossing
    /// the room is moved through the sink, so anything asking where somebody is — what is
    /// under the pointer, what to turn a head towards — has to ask this instead, or it
    /// answers about the spot they were standing on when the room loaded.
    /// </remarks>
    public Matrix4x4 Standing =>
        Stage is { } stage && Placement.Exists ? stage.TransformOf(Placement) : Transform;

    /// <summary>That script, read.</summary>
    /// <remarks>
    /// Settable, because a script may hand a character a different idle while the scene is
    /// standing — <c>NEWIDLE MosIdle.gas</c>, and <c>SetIdleGAS</c> from Sheep.
    /// </remarks>
    public Formats.Animation.GasFile? Idle { get; set; }

    /// <summary>What they do while they are speaking.</summary>
    /// <remarks>
    /// A scene's actor line names one — <c>talk=madreltalk.gas</c> — and so does its
    /// <c>[LISTENERS]</c> section, per conversation. Without it a character says their
    /// lines standing perfectly still, which is the half of talking that lip sync does not
    /// cover.
    /// </remarks>
    public Formats.Animation.GasFile? Talk { get; set; }

    /// <summary>What they do while somebody else is speaking.</summary>
    public Formats.Animation.GasFile? Listen { get; set; }

    /// <summary>
    /// Whether it is being drawn.
    /// </summary>
    /// <remarks>
    /// A scene may declare a model <c>hidden</c> and a script bring it out later with
    /// <c>ShowModel</c>. It is loaded and placed either way — there is no showing something
    /// that was never read — so this is the difference between a model that is in the room
    /// and one that is in the picture. Settable, because that is exactly what the scripts
    /// change. Anything answering "what is under the pointer" has to respect it, or the
    /// player can click on things nobody can see.
    /// </remarks>
    public bool Visible { get; set; } = true;

    /// <summary>The head as the clips address it, when the head being drawn is refined.</summary>
    /// <remarks>
    /// Null for a prop, and null for a character whose head is drawn as authored. When it is
    /// here, <see cref="Model"/> already carries the subdivided head and this is the only
    /// remaining record of the vertices a <c>.ACT</c> is talking about — so the clip is
    /// played by fitting these against what it says and moving the mesh, rather than by
    /// writing its vertices into a buffer that is no longer the right size.
    /// </remarks>
    public Actors.HeadRig? Head { get; init; }
}

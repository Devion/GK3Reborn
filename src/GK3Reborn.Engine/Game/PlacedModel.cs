using System.Numerics;
using GK3Reborn.Formats.Models;

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
public sealed record PlacedModel(
    string Name,
    string? Noun,
    string? Verb,
    ModFile Model,
    Matrix4x4 Transform,
    PlacedModelKind Kind);

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

    /// <summary>Whether the scene gave this actor a spot of its own to stand on.</summary>
    public bool Spotted { get; init; }

    /// <summary>Which way this model is built to face, when its own arrow says.</summary>
    public float? BuiltFacing { get; init; }

    /// <summary>Where it is standing, so that where it stands now can be asked.</summary>
    public ISceneSink? Stage { get; init; }

    /// <summary>
    /// Where it stands <em>now</em>, rather than where the scene first put it.
    /// </summary>
    public Matrix4x4 Standing =>
        Stage is { } stage && Placement.Exists ? stage.TransformOf(Placement) : Transform;

    /// <summary>That script, read.</summary>
    public Formats.Animation.GasFile? Idle { get; set; }

    /// <summary>What they do while they are speaking.</summary>
    public Formats.Animation.GasFile? Talk { get; set; }

    /// <summary>What they do while somebody else is speaking.</summary>
    public Formats.Animation.GasFile? Listen { get; set; }

    /// <summary>
    /// Whether it is being drawn.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>Whether it is drawn as painted, with the room's lighting kept off it.</summary>
    public bool SelfLit { get; set; }

    /// <summary>
    /// The animation that states the pose it opens in, or null.
    /// </summary>
    public string? InitialAnimation { get; init; }

    /// <summary>
    /// Where each mesh group has been posed to, or null where nothing has moved it.
    /// </summary>
    public IReadOnlyList<Matrix4x4?> Posed => _posed ?? [];

    private Matrix4x4?[]? _posed;

    /// <summary>Notes where a clip has put one of the mesh groups.</summary>
    /// <param name="mesh">Which group.</param>
    /// <param name="meshToLocal">Its transform, replacing the one the model was built with.</param>
    public void Pose(int mesh, Matrix4x4 meshToLocal)
    {
        if (mesh < 0 || mesh >= Model.Meshes.Count)
        {
            return;
        }

        _posed ??= new Matrix4x4?[Model.Meshes.Count];
        _posed[mesh] = meshToLocal;
    }

    /// <summary>Where a mesh group is now, which is where it was built unless a clip moved it.</summary>
    /// <param name="mesh">Which group.</param>
    /// <returns>Its transform.</returns>
    public Matrix4x4 PoseOf(int mesh) =>
        _posed is { } posed && mesh >= 0 && mesh < posed.Length && posed[mesh] is { } moved
            ? moved
            : Model.Meshes[mesh].MeshToLocal;

    /// <summary>The head as the clips address it, when the head being drawn is refined.</summary>
    public Actors.HeadRig? Head { get; init; }
}

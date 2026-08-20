using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game;

/// <summary>
/// What a scene is, assembled from the one or two files that describe it.
/// </summary>
/// <remarks>
/// <para>
/// A scene has a general initialisation file named for the location — <c>R25.SIF</c> — and
/// may have a second named for the location and the timeblock together —
/// <c>R25202P.SIF</c>. The general one describes the room: its geometry, its furniture,
/// the cameras the player can stand at. The specific one describes what is happening in it
/// that afternoon: Grace on the couch, Mosely and his bottle, the book, the dialogue
/// cameras for the conversation they are about to have, and the action file that drives it.
/// </para>
/// <para>
/// Sixteen of R25's variants exist and none of their contents reaches the screen without
/// this. Reading only the general file gives an empty hotel room at every point in a story
/// that mostly happens in occupied ones.
/// </para>
/// <para>
/// The merge follows the original, which reads both files independently — each deciding
/// its own conditions — and then joins the results, general first. Lists concatenate; the
/// general block accumulates, with anything the specific file sets overriding what the
/// general one said. Where the two name the same model the specific file wins, which falls
/// out of the same last-declaration-wins rule that applies inside one file.
/// </para>
/// </remarks>
public sealed class SceneDefinition
{
    private readonly SceneInitFile? _general;
    private readonly SceneInitFile? _specific;

    /// <summary>Creates a definition.</summary>
    /// <param name="general">The location's file, if it has one.</param>
    /// <param name="specific">The location-and-timeblock file, if it has one.</param>
    public SceneDefinition(SceneInitFile? general, SceneInitFile? specific = null)
    {
        _general = general;
        _specific = specific;
    }

    /// <summary>The location's own file.</summary>
    public SceneInitFile? General => _general;

    /// <summary>The file for this location at this timeblock.</summary>
    public SceneInitFile? Specific => _specific;

    /// <summary>Whether there is anything to read at all.</summary>
    public bool IsEmpty => _general is null && _specific is null;

    /// <summary>Whether the conditions were decided against the story.</summary>
    public bool ConditionsResolved =>
        (_general?.ConditionsResolved ?? false) || (_specific?.ConditionsResolved ?? false);

    /// <summary>The scene asset to load, which in turn names the geometry and lights.</summary>
    /// <returns>The name, or null if neither file gives one.</returns>
    public string? SceneAsset() =>
        Later(_specific?.SceneAsset(ConditionsResolved), _general?.SceneAsset(ConditionsResolved));

    /// <summary>Where the scene's global light sits.</summary>
    /// <returns>The position, or null.</returns>
    public Vector3? GlobalLight() => _specific?.GlobalLight() ?? _general?.GlobalLight();

    /// <summary>Where actors may stand.</summary>
    /// <returns>The declaration, or null if neither file gives one.</returns>
    /// <remarks>
    /// A timeblock file overrides it where it has one to say — HAL swaps in
    /// <c>HalBoundsMAID2</c> for the blocks where the maid's cart is in the corridor.
    /// </remarks>
    public SceneBoundary? Boundary() => _specific?.Boundary() ?? _general?.Boundary();

    /// <summary>The object in the geometry that is the floor.</summary>
    /// <returns>Its name, or null if neither file says.</returns>
    public string? FloorObject() => _specific?.FloorObject() ?? _general?.FloorObject();

    /// <summary>The models the scene places.</summary>
    /// <returns>The models, general file first.</returns>
    public IReadOnlyList<SceneModel> Models() => MergeModels();

    /// <summary>The actors the scene places.</summary>
    public IReadOnlyList<SceneActor> Actors() => Join(_general?.Actors(), _specific?.Actors());

    /// <summary>The spots the scene defines.</summary>
    public IReadOnlyList<ScenePosition> Positions() =>
        Join(_general?.Positions(), _specific?.Positions());

    /// <summary>The cameras the player's view can occupy.</summary>
    public IReadOnlyList<SceneCamera> RoomCameras() =>
        Join(_general?.RoomCameras(), _specific?.RoomCameras());

    /// <summary>The cameras cinematics use.</summary>
    public IReadOnlyList<SceneCamera> CinematicCameras() =>
        Join(_general?.CinematicCameras(), _specific?.CinematicCameras());

    /// <summary>The soundtracks the scene plays, general file first.</summary>
    public IReadOnlyList<string> Soundtracks() =>
        Join(_general?.Soundtracks(), _specific?.Soundtracks());

    /// <summary>Where the player starts.</summary>
    public ScenePosition? StartPosition() =>
        PositionNamed("START") ?? (Positions() is [ScenePosition first, ..] ? first : null);

    /// <summary>A named spot, or null if neither file defines one under that name.</summary>
    /// <param name="name">The spot's name.</param>
    /// <returns>The position.</returns>
    /// <remarks>
    /// The timeblock file wins, since it is the one that both places an actor and says
    /// where — <c>pos=GRACE_INIT</c> and the <c>GRACE_INIT</c> spot are written together.
    /// </remarks>
    public ScenePosition? PositionNamed(string? name)
    {
        if (name is null)
        {
            return null;
        }

        return _specific?.PositionNamed(name) ?? _general?.PositionNamed(name);
    }

    /// <summary>The camera a scene opens on.</summary>
    /// <returns>The default camera, the first one, or null if neither file defines any.</returns>
    /// <remarks>
    /// From the general file. A timeblock file adds the cameras a cinematic cuts to and
    /// routinely declares an empty <c>[ROOM_CAMERAS]</c>, so letting it answer here would
    /// open half the scenes in the game on nothing.
    /// </remarks>
    public SceneCamera? DefaultCamera()
    {
        IReadOnlyList<SceneCamera> cameras = RoomCameras();

        return _general?.DefaultCamera()
            ?? cameras.FirstOrDefault(c => c.IsDefault)
            ?? (cameras.Count > 0 ? cameras[0] : null);
    }

    /// <summary>Finds a camera by name, falling back to the scene's default.</summary>
    /// <param name="name">Camera name, or null for the default.</param>
    /// <returns>The camera, or null if the scene defines none.</returns>
    /// <remarks>
    /// A named lookup searches the cinematic cameras too. They are not viewpoints the
    /// player can occupy, but they are the angles the artists framed deliberately — the one
    /// looking out of R25's window, the three-shot over the couch — which makes them the
    /// useful ones to name when comparing a render against the original.
    /// </remarks>
    public SceneCamera? CameraNamed(string? name)
    {
        if (name is null)
        {
            return DefaultCamera();
        }

        bool Match(SceneCamera c) => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase);

        return RoomCameras().FirstOrDefault(Match)
            ?? CinematicCameras().FirstOrDefault(Match)
            ?? DefaultCamera();
    }

    private static string? Later(string? specific, string? general) =>
        string.IsNullOrEmpty(specific) ? general : specific;

    private static IReadOnlyList<T> Join<T>(IReadOnlyList<T>? general, IReadOnlyList<T>? specific)
    {
        if (specific is not { Count: > 0 })
        {
            return general ?? [];
        }

        return general is { Count: > 0 } ? [.. general, .. specific] : specific;
    }

    /// <summary>
    /// The two files' model lists, joined by name.
    /// </summary>
    /// <remarks>
    /// The rule is the one that applies inside a single file, applied once more across the
    /// pair: with the conditions decided the last declaration wins outright, and without
    /// them a model is hidden only where every declaration agrees. See
    /// <see cref="SceneInitFile.Models"/> for why the two cases differ.
    /// </remarks>
    private IReadOnlyList<SceneModel> MergeModels()
    {
        IReadOnlyList<SceneModel> general = _general?.Models() ?? [];
        IReadOnlyList<SceneModel> specific = _specific?.Models() ?? [];

        if (specific.Count == 0)
        {
            return general;
        }

        if (general.Count == 0)
        {
            return specific;
        }

        Dictionary<string, SceneModel> merged = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];

        foreach (SceneModel model in general.Concat(specific))
        {
            if (!merged.TryGetValue(model.Name, out SceneModel? seen))
            {
                order.Add(model.Name);
                merged[model.Name] = model;
                continue;
            }

            merged[model.Name] = ConditionsResolved
                ? model
                : model with
                {
                    Hidden = model.Hidden && seen.Hidden,
                    VisibilityDisputed =
                        seen.VisibilityDisputed || model.Hidden != seen.Hidden,
                };
        }

        return [.. order.Select(n => merged[n])];
    }
}

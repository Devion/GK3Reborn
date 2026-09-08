using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game;

/// <summary>
/// What a scene is, assembled from the one or two files that describe it.
/// </summary>
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
    public SceneBoundary? Boundary() => _specific?.Boundary() ?? _general?.Boundary();

    /// <summary>The object in the geometry that is the floor.</summary>
    /// <returns>Its name, or null if neither file says.</returns>
    public string? FloorObject() => _specific?.FloorObject() ?? _general?.FloorObject();

    /// <summary>The models that fence the camera in.</summary>
    /// <returns>Their names, general file first.</returns>
    public IReadOnlyList<string> CameraBounds() =>
        Join(_general?.CameraBounds(), _specific?.CameraBounds());

    /// <summary>The mechanism the room needs code for, if it declares one.</summary>
    /// <returns>The name, timeblock file first; null where neither file declares one.</returns>
    public string? Mechanism() => Later(_specific?.Mechanism(), _general?.Mechanism());

    /// <summary>The models the scene places.</summary>
    /// <returns>The models, general file first.</returns>
    public IReadOnlyList<SceneModel> Models() => MergeModels();

    /// <summary>The actors the scene places.</summary>
    public IReadOnlyList<SceneActor> Actors() => MergeActors();

    /// <summary>
    /// Who the player is in this room, as the scene's own files say.
    /// </summary>
    /// <returns>The ego's noun — <c>GRACE</c>, <c>GABRIEL</c> — or null when neither file names one.</returns>
    public string? EgoNoun() =>
        Actors().LastOrDefault(a => a.IsEgo) is { Noun: { Length: > 0 } noun } ? noun : null;

    /// <summary>The spots the scene defines.</summary>
    public IReadOnlyList<ScenePosition> Positions() =>
        Join(_general?.Positions(), _specific?.Positions());

    /// <summary>The patches of floor that act on whoever walks onto them.</summary>
    /// <returns>The triggers, general file first.</returns>
    public IReadOnlyList<SceneTrigger> Triggers() =>
        Join(_general?.Triggers(), _specific?.Triggers());

    /// <summary>The cameras the player's view can occupy.</summary>
    public IReadOnlyList<SceneCamera> RoomCameras() =>
        Join(_general?.RoomCameras(), _specific?.RoomCameras());

    /// <summary>The cameras cinematics use.</summary>
    public IReadOnlyList<SceneCamera> CinematicCameras() =>
        Join(_general?.CinematicCameras(), _specific?.CinematicCameras());

    /// <summary>The soundtracks the scene plays, general file first.</summary>
    public IReadOnlyList<string> Soundtracks() =>
        Join(_general?.Soundtracks(), _specific?.Soundtracks());

    /// <summary>What actors do during each named conversation.</summary>
    /// <returns>The settings, general file first.</returns>
    public IReadOnlyList<SceneConversation> Conversations() =>
        Join(_general?.Conversations(), _specific?.Conversations());

    /// <summary>The cameras a conversation cuts between.</summary>
    /// <summary>Every camera the scene names, whatever kind it is.</summary>
    public IReadOnlyList<SceneCamera> Cameras() =>
        [.. RoomCameras(), .. CinematicCameras(), .. DialogueCameras()];

    public IReadOnlyList<SceneCamera> DialogueCameras() =>
        Join(_general?.DialogueCameras(), _specific?.DialogueCameras());

    /// <summary>The close-up views both files declare.</summary>
    public IReadOnlyList<InspectCamera> InspectCameras() =>
        [.. _general?.InspectCameras() ?? [], .. _specific?.InspectCameras() ?? []];

    /// <summary>
    /// The close-up view of a thing, if the scene declares one.
    /// </summary>
    /// <param name="key">The noun the player clicked, or a model name.</param>
    /// <param name="model">The model standing behind that noun, if it is known.</param>
    /// <returns>The camera, or null.</returns>
    public SceneCamera? InspectCameraFor(string key, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        IReadOnlyList<InspectCamera> cameras = InspectCameras();

        SceneCamera? Look(string name, bool byModel) => cameras
            .FirstOrDefault(c =>
                c.ByModel == byModel &&
                string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase))
            ?.Camera;

        return Look(key, byModel: false)
            ?? Look(key, byModel: true)
            ?? (model is { Length: > 0 } ? Look(model, byModel: true) : null);
    }

    /// <summary>Any camera the scene names, of whatever kind.</summary>
    /// <param name="name">The camera's name.</param>
    /// <returns>The camera, or null when the scene names none such.</returns>
    public SceneCamera? AnyCameraNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        bool Match(SceneCamera c) => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase);

        return RoomCameras().FirstOrDefault(Match)
            ?? CinematicCameras().FirstOrDefault(Match)
            ?? DialogueCameras().FirstOrDefault(Match);
    }

    /// <summary>Where the player starts.</summary>
    /// <summary>
    /// Where the player stands on walking into this room.
    /// </summary>
    /// <param name="from">The location they came from, or null.</param>
    /// <returns>The spot, or null when the scene names none and nothing should be guessed.</returns>
    public ScenePosition? StartPosition(string? from = null) =>
        (from is { Length: > 0 } ? PositionNamed("FR_" + from) : null)
        ?? PositionNamed("START");

    /// <summary>A named spot, or null if neither file defines one under that name.</summary>
    /// <param name="name">The spot's name.</param>
    /// <returns>The position.</returns>
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
    public SceneCamera? CameraNamed(string? name)
    {
        if (name is null)
        {
            return DefaultCamera();
        }

        return AnyCameraNamed(name) ?? DefaultCamera();
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
    /// <summary>Joins the two files' casts, with the timeblock's ego replacing the general one.</summary>
    private IReadOnlyList<SceneActor> MergeActors()
    {
        IReadOnlyList<SceneActor> general = _general?.Actors() ?? [];
        IReadOnlyList<SceneActor> specific = _specific?.Actors() ?? [];

        if (general.Count == 0 || specific.Count == 0)
        {
            return Dedupe(general.Count == 0 ? specific : general);
        }

        // Only when the timeblock names one. A file that names no ego is saying nothing
        // about who the player is, and the location's own answer stands.
        bool replaces = specific.Any(a => a.IsEgo);

        return Dedupe(
            [.. general.Where(a => !replaces || !a.IsEgo), .. specific]);
    }

    /// <summary>One entry per person, the later declaration winning.</summary>
    private static IReadOnlyList<SceneActor> Dedupe(IReadOnlyList<SceneActor> actors)
    {
        Dictionary<string, SceneActor> merged = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];

        foreach (SceneActor actor in actors)
        {
            if (!merged.ContainsKey(actor.Name))
            {
                order.Add(actor.Name);
            }

            merged[actor.Name] = actor;
        }

        return [.. order.Select(n => merged[n])];
    }

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

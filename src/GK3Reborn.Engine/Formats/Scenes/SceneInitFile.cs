using System.Numerics;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Formats.Scenes;

/// <summary>A camera the scene defines.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Position">Where it sits, in scene space.</param>
/// <param name="Yaw">Rotation about the up axis, in radians.</param>
/// <param name="Pitch">Rotation about the right axis, in radians.</param>
/// <param name="IsDefault">Whether the scene starts here.</param>
public sealed record SceneCamera(string Name, Vector3 Position, float Yaw, float Pitch, bool IsDefault)
{
    /// <summary>
    /// The direction the camera looks.
    /// </summary>
    /// <remarks>
    /// The original composes the rotation as yaw about Y then pitch about X and applies it
    /// to +Z; see <c>GameCamera::SetAngle</c> and <c>Transform::GetForward</c>. Reversing
    /// the order gives a view that looks plausible from shallow cameras and badly wrong
    /// from steep ones, which makes it an easy mistake to ship.
    /// </remarks>
    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        -MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Cos(Yaw));
}

/// <summary>A model the scene places.</summary>
/// <param name="Name">Model name, without extension.</param>
/// <param name="Noun">The noun it answers to, if any.</param>
/// <param name="Type">Its declared type: <c>scene</c>, <c>prop</c>, <c>hittest</c> and so on.</param>
/// <param name="Hidden">Whether every block that declares it hides it.</param>
public sealed record SceneModel(string Name, string? Noun, string? Type, bool Hidden)
{
    /// <summary>
    /// Whether one block hides it while another shows it, so its visibility depends on
    /// story state that has not been evaluated.
    /// </summary>
    public bool VisibilityDisputed { get; init; }
}

/// <summary>A spot in the scene the player or an actor can stand.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Position">Where it is, in scene space.</param>
/// <param name="Heading">Which way whoever stands there faces, in radians about the up axis.</param>
/// <param name="Camera">The room camera that goes with it, if any.</param>
public sealed record ScenePosition(string Name, Vector3 Position, float Heading, string? Camera);

/// <summary>An actor the scene places.</summary>
/// <param name="Name">Model name.</param>
/// <param name="Noun">The noun it answers to.</param>
/// <param name="IsEgo">Whether the player controls it.</param>
public sealed record SceneActor(string Name, string? Noun, bool IsEgo);

/// <summary>
/// Reader for scene initialisation files.
/// </summary>
/// <remarks>
/// <para>
/// A SIF says what a scene <em>is</em>: which scene asset to load for a given time of day,
/// which models stand in it, which actors are present, where the cameras are and where the
/// player may walk. Without it a BSP is a building with no way in and no indication of
/// which way is the front.
/// </para>
/// <para>
/// Almost everything in one is conditional. The same scene has different geometry,
/// different props and different lighting at each of the game's timeblocks, expressed as
/// repeated sections whose headers carry Sheep expressions. Reading a SIF without
/// evaluating those conditions gives the union of every state the scene can be in, which
/// is right for tooling and wrong for the game — so which to include is the caller's
/// choice.
/// </para>
/// </remarks>
public sealed class SceneInitFile
{
    private readonly IniDocument _document;

    private SceneInitFile(IniDocument document)
    {
        _document = document;
    }

    /// <summary>Name this file was read under.</summary>
    public string Name => _document.Name;

    /// <summary>The underlying document, for sections without a typed accessor.</summary>
    public IniDocument Document => _document;

    /// <summary>Parses a scene initialisation file.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed file.</returns>
    public static SceneInitFile Parse(string text, string name = "<memory>") =>
        new(IniDocument.Parse(text, name));

    /// <summary>The scene asset to load, which in turn names the geometry and lights.</summary>
    /// <param name="includeConditional">Whether to consider conditional sections.</param>
    /// <returns>The name, or null if the file does not give one.</returns>
    public string? SceneAsset(bool includeConditional = false) =>
        _document.LinesOf("GENERAL", includeConditional)
            .Select(l => l.Value("scene"))
            .LastOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>Where the scene's global light sits.</summary>
    /// <returns>The position, or null.</returns>
    public Vector3? GlobalLight() =>
        _document.LinesOf("GENERAL")
            .Where(l => string.Equals(l.Head.Key, "globalLight", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Vector("pos"))
            .LastOrDefault(v => v is not null);

    /// <summary>The cameras the player's view can occupy.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The cameras, in file order.</returns>
    public IReadOnlyList<SceneCamera> RoomCameras(bool includeConditional = true) =>
        CamerasIn("ROOM_CAMERAS", includeConditional);

    /// <summary>The cameras cinematics use.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The cameras, in file order.</returns>
    public IReadOnlyList<SceneCamera> CinematicCameras(bool includeConditional = true) =>
        CamerasIn("CINEMATIC_CAMERAS", includeConditional);

    /// <summary>The camera a scene opens on.</summary>
    /// <returns>The default camera, the first one, or null if the scene defines none.</returns>
    public SceneCamera? DefaultCamera()
    {
        List<SceneCamera> cameras = CamerasIn("ROOM_CAMERAS", includeConditional: true);
        return cameras.Find(c => c.IsDefault) ?? (cameras.Count > 0 ? cameras[0] : null);
    }

    /// <summary>The models the scene places, deduplicated by name.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The models.</returns>
    /// <remarks>
    /// <para>
    /// The same model appears in several conditional blocks, and the later ones refine the
    /// noun and type of the earlier, so those come from the last occurrence.
    /// </para>
    /// <para>
    /// Hiding does not work that way. A pair of blocks under complementary conditions —
    /// <c>{!IsCurrentTime("202p")}</c> and <c>{IsCurrentTime("202p")}</c> — describes two
    /// states of the scene, of which exactly one holds; the second is not a correction of
    /// the first. Until the conditions can be evaluated, taking the last occurrence hides
    /// whatever any block hides, which is how the hall door in R25 disappeared and left its
    /// knob behind. So a model is hidden only when every block that declares it agrees, and
    /// the rest are reported through <see cref="SceneModel.VisibilityDisputed"/>. Erring
    /// towards drawing matches this reader's treatment of conditionals everywhere else: an
    /// object that should not be there is a smaller loss than a missing wall or door.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SceneModel> Models(bool includeConditional = true)
    {
        Dictionary<string, SceneModel> models = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];

        foreach (IniLine line in _document.LinesOf("MODELS", includeConditional))
        {
            if (line.Value("model") is not { Length: > 0 } modelName)
            {
                continue;
            }

            bool hidden = line.HasFlag("hidden");

            if (!models.TryGetValue(modelName, out SceneModel? seen))
            {
                order.Add(modelName);
            }

            models[modelName] = new SceneModel(
                modelName,
                line.Value("noun"),
                line.Value("type"),
                hidden && (seen?.Hidden ?? true))
            {
                // Carried forward, or a third block agreeing with the second would erase
                // the disagreement the first one recorded.
                VisibilityDisputed = seen is not null && (seen.VisibilityDisputed || seen.Hidden != hidden),
            };
        }

        return order.Select(n => models[n]).ToList();
    }

    /// <summary>The actors the scene places.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The actors.</returns>
    public IReadOnlyList<SceneActor> Actors(bool includeConditional = true) =>
        _document.LinesOf("ACTORS", includeConditional)
            .Where(l => l.Value("model") is { Length: > 0 })
            .Select(l => new SceneActor(l.Value("model")!, l.Value("noun"), l.HasFlag("ego")))
            .ToList();

    /// <summary>The spots the scene defines.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The positions, in file order.</returns>
    public List<ScenePosition> Positions(bool includeConditional = true) =>
        _document.LinesOf("POSITIONS", includeConditional)
            .Where(l => l.Vector("pos") is not null)
            .Select(l => new ScenePosition(
                l.Head.Key,
                l.Vector("pos")!.Value,
                float.DegreesToRadians(l.Number("heading") ?? 0f),
                l.Value("camera")))
            .ToList();

    /// <summary>Where the player starts.</summary>
    /// <returns>The spot named START, the first one, or null.</returns>
    /// <remarks>
    /// Every scene in the corpus names its entry point START, but a few also arrive from
    /// elsewhere depending on the story, so this is a default rather than the only answer.
    /// </remarks>
    public ScenePosition? StartPosition()
    {
        List<ScenePosition> positions = Positions();

        return positions.Find(p =>
            string.Equals(p.Name, "START", StringComparison.OrdinalIgnoreCase))
            ?? (positions.Count > 0 ? positions[0] : null);
    }

    private List<SceneCamera> CamerasIn(string section, bool includeConditional)
    {
        List<SceneCamera> cameras = [];

        foreach (IniLine line in _document.LinesOf(section, includeConditional))
        {
            if (line.Vector("pos") is not { } position ||
                line.Find("angle")?.AsNumbers(2) is not { } angle)
            {
                continue;
            }

            cameras.Add(new SceneCamera(
                line.Head.Key,
                position,
                float.DegreesToRadians(angle[0]),
                float.DegreesToRadians(angle[1]),
                line.HasFlag("Default")));
        }

        return cameras;
    }
}

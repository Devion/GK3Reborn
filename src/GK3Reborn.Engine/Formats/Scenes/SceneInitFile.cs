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
/// <param name="Hidden">Whether it starts hidden.</param>
public sealed record SceneModel(string Name, string? Noun, string? Type, bool Hidden);

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
    /// The same model appears in several conditional blocks, often visible in one and
    /// hidden in another. Taking the last occurrence mirrors how the original applies
    /// blocks in order.
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

            if (!models.ContainsKey(modelName))
            {
                order.Add(modelName);
            }

            models[modelName] = new SceneModel(
                modelName,
                line.Value("noun"),
                line.Value("type"),
                line.HasFlag("hidden"));
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

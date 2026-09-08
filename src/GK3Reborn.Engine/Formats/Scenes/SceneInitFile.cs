using System.Globalization;
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
    /// <summary>Which conversation it belongs to, for a camera in the dialogue section.</summary>
    public string? Conversation { get; init; }

    /// <summary>Whether it is the shot a conversation opens on.</summary>
    public bool IsInitial { get; init; }

    /// <summary>Whether it is the shot a conversation ends on.</summary>
    public bool IsFinal { get; init; }

    /// <summary>The field of view it asks for, in radians, or null for the game's own.</summary>
    public float? FieldOfView { get; init; }

    /// <summary>
    /// The direction the camera looks.
    /// </summary>
    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        -MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Cos(Yaw));
}

/// <summary>A close-up view of one thing in the room.</summary>
/// <param name="Key">The noun or the model name it belongs to.</param>
/// <param name="ByModel">
/// Whether <paramref name="Key"/> is a model rather than a noun. The section keys its
/// lines both ways — 735 by noun and 470 by model across the corpus — and the two are
/// looked up in that order, because a noun is what the player clicked and a model is what
/// happens to be drawn there.
/// </param>
/// <param name="Camera">Where the view goes.</param>
public sealed record InspectCamera(string Key, bool ByModel, SceneCamera Camera);

/// <summary>Where an actor is allowed to stand, as the scene declares it.</summary>
/// <param name="Texture">Name of the boundary bitmap, without an extension.</param>
/// <param name="Size">How much of the world it covers, on X and Z, in scene units.</param>
/// <param name="Offset">Where the world origin sits within that area.</param>
public sealed record SceneBoundary(string Texture, Vector2 Size, Vector2 Offset);

/// <summary>A model the scene places.</summary>
/// <param name="Name">Model name, without extension.</param>
/// <param name="Noun">The noun it answers to, if any.</param>
/// <param name="Type">Its declared type: <c>scene</c>, <c>prop</c>, <c>hittest</c> and so on.</param>
/// <param name="Hidden">Whether every block that declares it hides it.</param>
public sealed record SceneModel(string Name, string? Noun, string? Type, bool Hidden)
{
    /// <summary>The verb a click on it does by default, if the line names one.</summary>
    public string? Verb { get; init; }

    /// <summary>The script that drives it when nobody is asking it to do anything.</summary>
    public string? Gas { get; init; }

    /// <summary>
    /// Whether one block hides it while another shows it, so its visibility depends on
    /// story state that has not been evaluated.
    /// </summary>
    public bool VisibilityDisputed { get; init; }

    /// <summary>Whether the line says the model is lit by nothing and drawn as painted.</summary>
    public bool FullLighting { get; init; }

    /// <summary>Where the model is to stand, if the line says.</summary>
    public Vector3? Position { get; init; }

    /// <summary>Which way it faces, in degrees about Y, if the line says.</summary>
    public float? Heading { get; init; }

    /// <summary>Whether a model is ground surfacing rather than a thing in the room.</summary>
    /// <param name="model">The line's model.</param>
    /// <returns>True when the line declares a decal.</returns>
    public static bool IsDecal(SceneModel? model) =>
        string.Equals(model?.Type, "decal", StringComparison.OrdinalIgnoreCase);

    /// <summary>An animation that puts it into its opening pose.</summary>
    public string? InitialAnimation { get; init; }
}

/// <summary>
/// A rectangle on the ground plan.
/// </summary>
/// <param name="MinX">The lower of its two X edges.</param>
/// <param name="MinZ">The lower of its two Z edges.</param>
/// <param name="MaxX">The higher of its two X edges.</param>
/// <param name="MaxZ">The higher of its two Z edges.</param>
public readonly record struct SceneRect(float MinX, float MinZ, float MaxX, float MaxZ)
{
    /// <summary>Puts two opposite corners in order.</summary>
    /// <param name="x1">One corner's X.</param>
    /// <param name="z1">One corner's Z.</param>
    /// <param name="x2">The other corner's X.</param>
    /// <param name="z2">The other corner's Z.</param>
    /// <returns>The rectangle they describe.</returns>
    public static SceneRect Between(float x1, float z1, float x2, float z2) => new(
        MathF.Min(x1, x2), MathF.Min(z1, z2), MathF.Max(x1, x2), MathF.Max(z1, z2));

    /// <summary>Whether a point on the ground plan is inside it.</summary>
    /// <param name="x">The point's X.</param>
    /// <param name="z">The point's Z.</param>
    /// <returns>True when it is, edges included.</returns>
    public bool Contains(float x, float z) =>
        x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
}

/// <summary>
/// A patch of floor that does something to whoever walks onto it.
/// </summary>
/// <param name="Noun">The noun its action is written about.</param>
/// <param name="Rect">The patch, on the ground plan.</param>
public sealed record SceneTrigger(string Noun, SceneRect Rect);

/// <summary>
/// What one actor does while a named conversation is going on.
/// </summary>
/// <param name="Conversation">The conversation's name, as <c>SetConversation</c> names it.</param>
/// <param name="Actor">Whose behaviour it changes, by noun.</param>
/// <param name="Talk">The script to run while they are speaking, or null to keep theirs.</param>
/// <param name="Listen">The script to run while somebody else is, or null to keep theirs.</param>
/// <param name="Enter">An animation to play on joining the conversation, or null.</param>
/// <param name="Exit">One to play on leaving it, or null.</param>
public sealed record SceneConversation(
    string Conversation,
    string Actor,
    string? Talk,
    string? Listen,
    string? Enter,
    string? Exit);

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
public sealed record SceneActor(string Name, string? Noun, bool IsEgo)
{
    /// <summary>Name of the spot the actor stands at, if the line gives one.</summary>
    public string? Position { get; init; }

    /// <summary>Whether the actor is in the scene but not to be drawn.</summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// The behaviour script they run when nobody is telling them to do anything.
    /// </summary>
    public string? Idle { get; init; }

    /// <summary>The one they run while they are speaking.</summary>
    public string? Talk { get; init; }

    /// <summary>The one they run while somebody else is.</summary>
    public string? Listen { get; init; }

    /// <summary>An animation that puts them into their opening pose.</summary>
    public string? InitialAnimation { get; init; }
}

/// <summary>
/// Reader for scene initialisation files.
/// </summary>
public sealed class SceneInitFile
{
    private readonly IniDocument _document;
    private readonly SectionFilter _applies;

    private SceneInitFile(IniDocument document, SectionFilter? applies)
    {
        _document = document;
        _applies = applies ?? IniDocument.EverySection;
        ConditionsResolved = applies is not null;
    }

    /// <summary>Name this file was read under.</summary>
    public string Name => _document.Name;

    /// <summary>The underlying document, for sections without a typed accessor.</summary>
    public IniDocument Document => _document;

    /// <summary>
    /// Whether the conditions were decided rather than taken all at once.
    /// </summary>
    public bool ConditionsResolved { get; }

    /// <summary>Parses a scene initialisation file.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed file, holding every state the scene can be in.</returns>
    public static SceneInitFile Parse(string text, string name = "<memory>") =>
        new(IniDocument.Parse(text, name), null);

    /// <summary>Parses a scene initialisation file for one state of the story.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="applies">Decides which of the conditional sections hold.</param>
    /// <returns>The parsed file, holding the scene as it stands right now.</returns>
    public static SceneInitFile Parse(string text, string name, SectionFilter applies)
    {
        ArgumentNullException.ThrowIfNull(applies);
        return new SceneInitFile(IniDocument.Parse(text, name), applies);
    }

    /// <summary>The sections to read, for a caller that asked for the conditional ones.</summary>
    private SectionFilter Applies(bool includeConditional) =>
        includeConditional ? _applies : IniDocument.UnconditionalSections;

    /// <summary>The scene asset to load, which in turn names the geometry and lights.</summary>
    /// <param name="includeConditional">Whether to consider conditional sections.</param>
    /// <returns>The name, or null if the file does not give one.</returns>
    public string? SceneAsset(bool includeConditional = false) =>
        _document.LinesOf("GENERAL", Applies(includeConditional))
            .Select(l => l.Value("scene"))
            .LastOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>Where actors may stand.</summary>
    /// <returns>The declaration, or null if the scene has no boundary.</returns>
    public SceneBoundary? Boundary()
    {
        foreach (IniLine line in _document.LinesOf("GENERAL", Applies(includeConditional: true)).Reverse())
        {
            if (line.Value("boundary") is not { Length: > 0 } texture)
            {
                continue;
            }

            float[]? size = line.Find("size")?.AsNumbers(2);
            float[]? offset = line.Find("offset")?.AsNumbers(2);

            if (size is null)
            {
                continue;
            }

            return new SceneBoundary(
                texture,
                new Vector2(size[0], size[1]),
                offset is null ? Vector2.Zero : new Vector2(offset[0], offset[1]));
        }

        return null;
    }

    /// <summary>The object in the geometry that is the floor.</summary>
    /// <returns>Its name, or null if the scene does not say.</returns>
    public string? FloorObject() =>
        _document.LinesOf("GENERAL", Applies(includeConditional: true))
            .Select(l => l.Value("floor"))
            .LastOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>
    /// The mechanism this room needs code for, if it needs any.
    /// </summary>
    /// <returns>The name the file gives it, or null where the room is only data.</returns>
    public string? Mechanism() =>
        _document.LinesOf("GENERAL", Applies(includeConditional: true))
            .Select(l => l.Value("custom"))
            .LastOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>The models that fence the camera in.</summary>
    /// <returns>Their names, in the order the file declares them; empty when it declares none.</returns>
    public IReadOnlyList<string> CameraBounds() =>
        [.. _document.LinesOf("GENERAL", Applies(includeConditional: true))
            .Select(l => l.Value("cameraBounds"))
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)];

    /// <summary>Where the scene's global light sits.</summary>
    /// <returns>The position, or null.</returns>
    public Vector3? GlobalLight() =>
        _document.LinesOf("GENERAL", Applies(includeConditional: true))
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

    /// <summary>The cameras a conversation cuts between.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The cameras.</returns>
    public IReadOnlyList<SceneCamera> DialogueCameras(bool includeConditional = true) =>
        CamerasIn("DIALOGUE_CAMERAS", includeConditional);

    /// <summary>
    /// The close-up views, one per thing worth looking at closely.
    /// </summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The views, in file order.</returns>
    public IReadOnlyList<InspectCamera> InspectCameras(bool includeConditional = true)
    {
        List<InspectCamera> cameras = [];

        foreach (IniLine line in _document.LinesOf("INSPECT_CAMERAS", Applies(includeConditional)))
        {
            bool byModel = string.Equals(line.Head.Key, "model", StringComparison.OrdinalIgnoreCase);

            if (!byModel &&
                !string.Equals(line.Head.Key, "noun", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Head.Value is not { Length: > 0 } key ||
                line.Vector("pos") is not { } position ||
                line.Find("angle")?.AsNumbers(2) is not { } angle)
            {
                continue;
            }

            cameras.Add(new InspectCamera(
                key,
                byModel,
                new SceneCamera(
                    key,
                    position,
                    float.DegreesToRadians(angle[0]),
                    float.DegreesToRadians(angle[1]),
                    IsDefault: false)));
        }

        return cameras;
    }

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
    public IReadOnlyList<SceneModel> Models(bool includeConditional = true)
    {
        Dictionary<string, SceneModel> models = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];

        foreach (IniLine line in _document.LinesOf("MODELS", Applies(includeConditional)))
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
                ConditionsResolved ? hidden : hidden && (seen?.Hidden ?? true))
            {
                Verb = line.Value("verb") ?? seen?.Verb,
                Gas = line.Value("gas") ?? seen?.Gas,
                InitialAnimation = line.Value("initanim") ?? seen?.InitialAnimation,
                Position = line.Vector("pos") ?? seen?.Position,
                Heading = line.Number("heading") ?? seen?.Heading,
                FullLighting = line.HasFlag("fulllighting") || (seen?.FullLighting ?? false),

                // Carried forward, or a third block agreeing with the second would erase
                // the disagreement the first one recorded. Nothing to carry once the
                // conditions are decided: only one of a pair of blocks applies.
                VisibilityDisputed = !ConditionsResolved &&
                                     seen is not null &&
                                     (seen.VisibilityDisputed || seen.Hidden != hidden),
            };
        }

        return order.Select(n => models[n]).ToList();
    }

    /// <summary>The action files the scene brings into scope.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>File names, in the order the file lists them.</returns>
    public IReadOnlyList<string> ActionFiles(bool includeConditional = true) =>
        [.. NamesIn("ACTIONS", includeConditional)];

    /// <summary>The soundtracks the scene plays in the background.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>File names, in the order the file lists them.</returns>
    public IReadOnlyList<string> Soundtracks(bool includeConditional = true) =>
        [.. NamesIn("AMBIENT", includeConditional)];

    /// <summary>What actors do during each named conversation.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The settings, in file order.</returns>
    public IReadOnlyList<SceneConversation> Conversations(bool includeConditional = true) =>
        _document.LinesOf("LISTENERS", Applies(includeConditional))
            .Where(l => l.Value("dialogue") is { Length: > 0 } && l.Value("actor") is { Length: > 0 })
            .Select(l => new SceneConversation(
                l.Value("dialogue")!,
                l.Value("actor")!,
                l.Value("talk"),
                l.Value("listen"),
                l.Value("enter"),
                l.Value("exit")))
            .ToList();

    /// <summary>Bare file names listed one per line in a section.</summary>
    private IEnumerable<string> NamesIn(string section, bool includeConditional) =>
        _document.LinesOf(section, Applies(includeConditional))
            .Select(l => l.Head.Key)
            .Where(name => name.Length > 0);

    /// <summary>The actors the scene places.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The actors.</returns>
    public IReadOnlyList<SceneActor> Actors(bool includeConditional = true) =>
        _document.LinesOf("ACTORS", Applies(includeConditional))
            .Where(l => l.Value("model") is { Length: > 0 })
            .Select(l => new SceneActor(l.Value("model")!, l.Value("noun"), l.HasFlag("ego"))
            {
                Position = l.Value("pos"),
                Hidden = l.HasFlag("hidden"),
                Idle = l.Value("idle"),
                Talk = l.Value("talk"),
                Listen = l.Value("listen"),
                InitialAnimation = l.Value("initanim"),
            })
            .ToList();

    /// <summary>The spots the scene defines.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The positions, in file order.</returns>
    public List<ScenePosition> Positions(bool includeConditional = true) =>
        _document.LinesOf("POSITIONS", Applies(includeConditional))
            .Where(l => l.Vector("pos") is not null)
            .Select(l => new ScenePosition(
                l.Head.Key,
                l.Vector("pos")!.Value,
                float.DegreesToRadians(l.Number("heading") ?? 0f),
                l.Value("camera")))
            .ToList();

    /// <summary>The patches of floor the scene watches for.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>The triggers, in file order.</returns>
    public List<SceneTrigger> Triggers(bool includeConditional = true) =>
        _document.LinesOf("TRIGGERS", Applies(includeConditional))
            .Select(l => (Noun: l.Value("noun"), Rect: Rectangle(l.Value("rect"))))
            .Where(t => t.Noun is { Length: > 0 } && t.Rect is not null)
            .Select(t => new SceneTrigger(t.Noun!, t.Rect!.Value))
            .ToList();

    /// <summary>
    /// Reads a <c>rect={x1,z1,x2,z2}</c> value.
    /// </summary>
    /// <param name="value">The value, braces and all.</param>
    /// <returns>The rectangle, or null when four numbers could not be read.</returns>
    private static SceneRect? Rectangle(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        string inside = value.Trim();

        if (inside.StartsWith('{') && inside.EndsWith('}'))
        {
            inside = inside[1..^1];
        }

        List<float> numbers = [];

        foreach (string part in inside.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Leading(part) is { } number)
            {
                numbers.Add(number);
            }
        }

        return numbers.Count >= 4
            ? SceneRect.Between(numbers[0], numbers[1], numbers[2], numbers[3])
            : null;
    }

    /// <summary>Reads the number a string starts with.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The number, or null when it does not start with one.</returns>
    private static float? Leading(string text)
    {
        string trimmed = text.Trim();
        int end = 0;
        bool point = false;

        while (end < trimmed.Length)
        {
            char c = trimmed[end];

            if (c == '.' && !point)
            {
                point = true;
            }
            else if (!char.IsAsciiDigit(c) && !(end == 0 && (c == '-' || c == '+')))
            {
                break;
            }

            end++;
        }

        return float.TryParse(
            trimmed[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;
    }

    /// <summary>A named spot, or null if the scene does not define one under that name.</summary>
    /// <param name="name">The spot's name.</param>
    /// <returns>The position.</returns>
    public ScenePosition? PositionNamed(string? name) =>
        name is null
            ? null
            : Positions().Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where the player starts.</summary>
    /// <returns>The spot named START, the first one, or null.</returns>
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

        foreach (IniLine line in _document.LinesOf(section, Applies(includeConditional)))
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
                line.HasFlag("Default"))
            {
                Conversation = line.Value("dialogue"),
                IsInitial = line.HasFlag("initial"),
                IsFinal = line.HasFlag("final"),
                FieldOfView = line.Find("fov")?.AsNumber() is { } wide && wide > 0
                    ? float.DegreesToRadians(wide)
                    : null,
            });
        }

        return cameras;
    }
}

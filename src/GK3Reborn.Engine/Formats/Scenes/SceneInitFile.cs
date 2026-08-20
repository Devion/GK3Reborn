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
    /// <remarks>
    /// Rare — the corpus uses it for exits, <c>verb=EXIT</c> and its left and right forms,
    /// where clicking the doorway should walk through it rather than open the action bar.
    /// </remarks>
    public string? Verb { get; init; }

    /// <summary>The script that drives it when nobody is asking it to do anything.</summary>
    /// <remarks>
    /// Named on a <c>gasprop</c> line as <c>gas=lbyfan.gas</c>. Ninety-one models across
    /// thirty-one scenes have one, and they are the things in a room that move on their
    /// own: the lobby's ceiling fans above all.
    /// </remarks>
    public string? Gas { get; init; }

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
public sealed record SceneActor(string Name, string? Noun, bool IsEgo)
{
    /// <summary>Name of the spot the actor stands at, if the line gives one.</summary>
    /// <remarks>
    /// Ego has none and starts at <c>START</c>. Everyone else is placed by name —
    /// <c>pos=GRACE_INIT</c> — which is why the timeblock file that puts Grace in the room
    /// also defines the spot she stands on.
    /// </remarks>
    public string? Position { get; init; }

    /// <summary>Whether the actor is in the scene but not to be drawn.</summary>
    public bool Hidden { get; init; }
}

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
    /// <remarks>
    /// It changes what a repeated declaration means. Read without deciding, two blocks
    /// naming the same model are two states of the scene and the reader has to reconcile
    /// them; read with the conditions decided, at most one of them applies and the later
    /// declaration simply refines the earlier, which is what the original does.
    /// </remarks>
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
    /// <remarks>
    /// One line carrying three pairs, and the corpus spells the last two both ways —
    /// <c>size=</c> in most scenes, <c>Size=</c> in RC1 — so the lookup is
    /// case-insensitive like every other key.
    /// </remarks>
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
    /// <remarks>
    /// Named so that a point can be dropped onto the ground without testing the whole
    /// room: the floor is one object among a hundred, and the scene says which.
    /// </remarks>
    public string? FloorObject() =>
        _document.LinesOf("GENERAL", Applies(includeConditional: true))
            .Select(l => l.Value("floor"))
            .LastOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>Where the scene's global light sits.</summary>
    /// <returns>The position, or null.</returns>
    /// <remarks>
    /// Conditional blocks count. R25 states its unconditional position once and then moves
    /// the light for each time of day, so reading only the unconditional blocks gives the
    /// scene its morning sun at midnight.
    /// </remarks>
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
    /// <remarks>
    /// Named like the others and carrying a <c>dialogue=</c> saying which conversation
    /// they belong to, which is why they are read the same way: a script may cut to one by
    /// name whether or not anybody is talking. <c>[INSPECT_CAMERAS]</c> is a different
    /// shape — keyed by <c>noun=</c> rather than named — and belongs to inspecting a thing
    /// rather than to pointing the camera somewhere.
    /// </remarks>
    public IReadOnlyList<SceneCamera> DialogueCameras(bool includeConditional = true) =>
        CamerasIn("DIALOGUE_CAMERAS", includeConditional);

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
    /// Hiding does not work that way when the conditions have not been decided. A pair of
    /// blocks under complementary conditions — <c>{!IsCurrentTime("202p")}</c> and
    /// <c>{IsCurrentTime("202p")}</c> — describes two states of the scene, of which exactly
    /// one holds; the second is not a correction of the first. Taking the last occurrence
    /// would hide whatever any block hides, which is how the hall door in R25 disappeared
    /// and left its knob behind. So without <see cref="ConditionsResolved"/> a model is
    /// hidden only when every block that declares it agrees, and the rest are reported
    /// through <see cref="SceneModel.VisibilityDisputed"/>. Erring towards drawing matches
    /// this reader's treatment of conditionals everywhere else: an object that should not
    /// be there is a smaller loss than a missing wall or door.
    /// </para>
    /// <para>
    /// With the conditions decided the question does not arise. Only one of the pair
    /// applies, so the last declaration wins outright — hiding included — and nothing is
    /// ever in dispute.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// Bare names on their own lines, no <c>key=</c> about them, and the name carries the
    /// meaning: <c>r25_all.nvc</c> applies to every timeblock, <c>r25_23all.nvc</c> to days
    /// two and three, <c>r25202p.nvc</c> to that afternoon alone. See
    /// <see cref="GK3Reborn.Game.TimeblockRange"/> for how that is read.
    /// </remarks>
    public IReadOnlyList<string> ActionFiles(bool includeConditional = true) =>
        [.. NamesIn("ACTIONS", includeConditional)];

    /// <summary>The soundtracks the scene plays in the background.</summary>
    /// <param name="includeConditional">Whether to include conditional sections.</param>
    /// <returns>File names, in the order the file lists them.</returns>
    /// <remarks>
    /// <c>.STK</c> files: a soundtrack is a small script of its own, saying which sounds to
    /// play and how often, not a piece of music to loop.
    /// </remarks>
    public IReadOnlyList<string> Soundtracks(bool includeConditional = true) =>
        [.. NamesIn("AMBIENT", includeConditional)];

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

    /// <summary>A named spot, or null if the scene does not define one under that name.</summary>
    /// <param name="name">The spot's name.</param>
    /// <returns>The position.</returns>
    public ScenePosition? PositionNamed(string? name) =>
        name is null
            ? null
            : Positions().Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

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
                line.HasFlag("Default")));
        }

        return cameras;
    }
}

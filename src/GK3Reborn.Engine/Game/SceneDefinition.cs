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

    /// <summary>The models that fence the camera in.</summary>
    /// <returns>Their names, general file first.</returns>
    /// <remarks>
    /// Joined rather than overridden, unlike the floor and the boundary beside it. A
    /// timeblock file that names a bounds model means "and this one as well" — the
    /// original merges these additively and the corpus is written for that.
    /// </remarks>
    public IReadOnlyList<string> CameraBounds() =>
        Join(_general?.CameraBounds(), _specific?.CameraBounds());

    /// <summary>The mechanism the room needs code for, if it declares one.</summary>
    /// <returns>The name, timeblock file first; null where neither file declares one.</returns>
    /// <remarks>
    /// The timeblock's answer wins, because it is the more specific file and because two of
    /// the eleven declarations are made there rather than in the location's own file —
    /// <c>BEC312P</c> and <c>LER307A</c> both name the coordinate device only on the
    /// afternoon Grace is carrying one.
    /// </remarks>
    public string? Mechanism() => Later(_specific?.Mechanism(), _general?.Mechanism());

    /// <summary>The models the scene places.</summary>
    /// <returns>The models, general file first.</returns>
    public IReadOnlyList<SceneModel> Models() => MergeModels();

    /// <summary>The actors the scene places.</summary>
    /// <remarks>
    /// <para>
    /// Joined, but the ego is <em>replaced</em> rather than added to. A location's general
    /// file names the person whose game it usually is, and a timeblock file names the person
    /// whose game it is now: POU declares Gabriel and <c>POU207A</c> declares Grace, because
    /// on the second morning the tour is hers and he is somewhere else entirely.
    /// </para>
    /// <para>
    /// Joining them outright put both in the room. Grace stood where the scene said and
    /// Gabriel stood at the origin with no spot of his own, in a scene he has no lines in
    /// and no business being in — and the room had two egos, which is one more than anything
    /// downstream expects. It is not a quirk of one scene: <b>157 scene and timeblock pairs
    /// across the corpus</b> name Gabriel generally and Grace specifically, which is every
    /// Grace timeblock in every location she visits.
    /// </para>
    /// <para>
    /// The same person is never placed twice either, for the reason the models are not: two
    /// copies of one character standing in one room is a worse answer than either of them.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SceneActor> Actors() => MergeActors();

    /// <summary>
    /// Who the player is in this room, as the scene's own files say.
    /// </summary>
    /// <returns>The ego's noun — <c>GRACE</c>, <c>GABRIEL</c> — or null when neither file names one.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is where ego comes from, not the timeblock.</b> A SIF's cast list marks one
    /// actor <c>ego</c>: <c>CS2.SIF</c> says <c>model=gra, noun=GRACE, ..., ego</c>, and
    /// on the second afternoon the chateau is hers. Nothing else in the data states it —
    /// there is no table of "day two at noon is Grace" — so a game that never read this
    /// flag was Gabriel everywhere, and every rule written <c>GRACE_ALL</c> resolved to
    /// its <c>GABE_ALL</c> twin instead. Reported: scanning anything in Grace's timeblock
    /// answered in Gabriel's voice, because <c>ANY_OBJECT, SCANNER, GABE_ALL_INV</c> is
    /// the line above <c>GRACE_ALL_INV</c> in <c>INV_23ALL.NVC</c> and both were on offer.
    /// </para>
    /// <para>
    /// The noun rather than the model. Scripts ask <c>IsCurrentEgo("Grace")</c> and the
    /// model is called <c>gra</c>; the noun is the name the rest of the game deals in.
    /// The merge has already decided which of the two files wins — see
    /// <see cref="Actors"/> — so whoever is marked here is the one who will be in the room.
    /// </para>
    /// </remarks>
    public string? EgoNoun() =>
        Actors().LastOrDefault(a => a.IsEgo) is { Noun: { Length: > 0 } noun } ? noun : null;

    /// <summary>The spots the scene defines.</summary>
    public IReadOnlyList<ScenePosition> Positions() =>
        Join(_general?.Positions(), _specific?.Positions());

    /// <summary>The patches of floor that act on whoever walks onto them.</summary>
    /// <returns>The triggers, general file first.</returns>
    /// <remarks>
    /// Joined rather than overridden, as the reference does in
    /// <c>SceneData::AddTriggerBlocks</c>: a timeblock file that declares one means "and
    /// this one as well". Only two locations declare any in their general file at all.
    /// </remarks>
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
    /// <remarks>
    /// Joined rather than overridden, the same as triggers: a timeblock file naming a
    /// conversation means "and this one as well". 237 lines across 75 rooms.
    /// </remarks>
    public IReadOnlyList<SceneConversation> Conversations() =>
        Join(_general?.Conversations(), _specific?.Conversations());

    /// <summary>The cameras a conversation cuts between.</summary>
    /// <summary>Every camera the scene names, whatever kind it is.</summary>
    /// <remarks>
    /// For anything choosing between them rather than looking one up: the artists framed
    /// all three lists and a shot that holds a conversation may be in any of them.
    /// </remarks>
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
    /// <remarks>
    /// By noun first and by model second, which is the original's order. A noun is what the
    /// action file and the player agree on; a model is what the artists framed, and several
    /// scenes give the camera for a thing only under the name of the mesh drawn there.
    /// </remarks>
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
    /// <remarks>
    /// What a script means by a camera angle: the original looks in every named list and
    /// complains if it finds nothing. Unlike <see cref="CameraNamed"/> this does not fall
    /// back to the default, because a script asking for a camera that is not there is a
    /// mistake worth hearing about rather than a reason to point the view somewhere else.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>Never an arbitrary one.</b> This used to fall back to the first entry of
    /// <c>[POSITIONS]</c>, which put Gabriel wherever the file happened to list first — in
    /// the phone room that is <c>EMILIO_HERE_1</c>, a spot authored for somebody else and
    /// directly in front of the camera, so walking in filled the screen with the back of his
    /// head until the room's enter script moved him. 22 of the game's scene files reach this,
    /// and exactly one of the 102 that place a player defines a <c>START</c> at all, so the
    /// guess was doing nearly all of the work and doing it wrongly.
    /// </para>
    /// <para>
    /// <b>The artists' own convention answers it instead.</b> A room names the spot you
    /// arrive at for each door into it, after where you came from: <c>FR_LBY</c> is where you
    /// stand having come from the lobby. 308 of them across 80 scenes. The room's enter
    /// script picks one by hand, and this is the same choice made a frame earlier, so the
    /// player never sees the wrong one.
    /// </para>
    /// <para>
    /// Failing all that, nothing. An unplaced player stands at the origin until a script
    /// moves them, which is what the reference does and is a great deal better than standing
    /// somewhere the artists meant for a different person.
    /// </para>
    /// </remarks>
    public ScenePosition? StartPosition(string? from = null) =>
        (from is { Length: > 0 } ? PositionNamed("FR_" + from) : null)
        ?? PositionNamed("START");

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
    /// <remarks>
    /// The rule is the one that applies inside a single file, applied once more across the
    /// pair: with the conditions decided the last declaration wins outright, and without
    /// them a model is hidden only where every declaration agrees. See
    /// <see cref="SceneInitFile.Models"/> for why the two cases differ.
    /// </remarks>
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

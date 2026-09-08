namespace GK3Reborn.Game;

/// <summary>
/// A scene to load, and the point in the story to load it at.
/// </summary>
public sealed class SceneRequest
{
    private SceneRequest(string scene, string? assetSuffix, GameState? state, Gk3SheepApi? api = null)
    {
        Scene = scene;
        AssetSuffix = assetSuffix;
        State = state;
        Api = state is null ? null : api ?? new Gk3SheepApi(state);
        Conditions = Api is null ? null : new SceneConditions(Api);
    }

    /// <summary>The scene's name, which is also its three-letter location code.</summary>
    public string Scene { get; }

    /// <summary>The <c>M</c>/<c>A</c>/<c>E</c>/<c>N</c> suffix to prefer, if the caller gave one.</summary>
    public string? AssetSuffix { get; }

    /// <summary>The story state, when the caller named a real timeblock.</summary>
    public GameState? State { get; }

    /// <summary>
    /// The timeblock's code, which is half the name of the scene's second file.
    /// </summary>
    public string? TimeblockCode => State?.Timeblock.ToString();

    /// <summary>The evaluator to read the scene file through, when there is a state.</summary>
    public SceneConditions? Conditions { get; }

    /// <summary>
    /// Whether building this room is the player arriving in it.
    /// </summary>
    public bool Counts { get; private init; } = true;

    /// <summary>
    /// The script host the conditions are decided through, when there is a state.
    /// </summary>
    public Gk3SheepApi? Api { get; }

    /// <summary>
    /// A request for the next room, in a story that is already under way.
    /// </summary>
    /// <param name="api">The host the story has been running against.</param>
    /// <param name="scene">Where the player is going.</param>
    /// <returns>The request.</returns>
    public static SceneRequest Continuing(Gk3SheepApi api, string scene)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(scene);

        string name = scene.ToUpperInvariant();
        GameState state = api.State;

        // Where the player is, so that a room built before the arrival is counted is still
        // built as the right room. Assigning it is also what remembers where they came
        // from — the question every scene asks to decide which door they walked in through
        // — and it is idempotent, so a script that already called SetLocation loses
        // nothing by it.
        state.Location = name;
        state.SetActorLocation(state.Ego, name);

        // A close-up belongs to the room it is a close-up of. Carrying one through a door
        // pointed the next room's camera at a thing that is not in it, and there was no way
        // back: inspecting the lobby's register and then walking into the phone room left
        // every room after it framed on a register.
        state.Inspecting = string.Empty;

        // And the camera is fenced in again. Turning the shell off lasts until the next
        // room in the original, which is what the scripts that turn it off and never back
        // on are relying on.
        state.CameraBoundaries = true;

        return new SceneRequest(name, null, state, api);
    }

    /// <summary>
    /// A request for somewhere the player is looking at through the binoculars.
    /// </summary>
    /// <param name="api">The host the story has been running against.</param>
    /// <param name="scene">The room being looked at.</param>
    /// <returns>The request.</returns>
    public static SceneRequest Peeking(Gk3SheepApi api, string scene)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(scene);

        return new SceneRequest(scene.ToUpperInvariant(), null, api.State, api) { Counts = false };
    }

    /// <summary>
    /// A request for the room a look through the binoculars is going back to.
    /// </summary>
    /// <param name="api">The host the story has been running against.</param>
    /// <param name="scene">The room, which is the one the player never left.</param>
    /// <returns>The request.</returns>
    public static SceneRequest Resuming(Gk3SheepApi api, string scene)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(scene);

        return new SceneRequest(scene.ToUpperInvariant(), null, api.State, api) { Counts = false };
    }

    /// <summary>Reads a timeblock argument.</summary>
    /// <param name="scene">Scene name, such as <c>R25</c>.</param>
    /// <param name="timeblock">
    /// A story timeblock such as <c>202P</c>, an asset suffix such as <c>N</c>, or null.
    /// </param>
    /// <returns>The request.</returns>
    public static SceneRequest For(string scene, string? timeblock)
    {
        ArgumentNullException.ThrowIfNull(scene);

        string name = Path.GetFileNameWithoutExtension(scene).ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(timeblock))
        {
            return new SceneRequest(name, null, null);
        }

        if (!Timeblock.TryParse(timeblock, out Timeblock parsed))
        {
            return new SceneRequest(name, timeblock, null);
        }

        var state = new GameState { Timeblock = parsed, Location = name };
        state.SetActorLocation(state.Ego, name);

        // Deliberately not EnterLocation: a scene file asks whether this is the first
        // visit by checking the count is zero, so during loading the count has to be the
        // number of previous visits. The arrival is recorded once the scene is standing.
        return new SceneRequest(name, null, state);
    }
}

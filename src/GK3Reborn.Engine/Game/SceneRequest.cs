namespace GK3Reborn.Game;

/// <summary>
/// A scene to load, and the point in the story to load it at.
/// </summary>
/// <remarks>
/// <para>
/// The timeblock a caller names can mean one of two things, and the difference matters.
/// <c>202P</c> is a point in the story: the scene file's conditions can be decided against
/// it, so the scene comes out in exactly one state, with the right bed made, the right
/// door in place and the right asset and bake chosen by the file itself. <c>A</c> is only
/// the suffix on an asset name — several timeblocks share one afternoon bake — so it picks
/// geometry and lighting and says nothing about the story.
/// </para>
/// <para>
/// Both are useful. The suffix form is how a tool asks to see a scene's afternoon
/// lighting without inventing a story state to justify it, and it is what the render
/// tooling has always taken. Naming no timeblock at all leaves the conditions undecided
/// and loads the union of every state the scene can be in, which is the right answer for
/// a corpus survey and the wrong one for a game.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// A scene may have a file named for the location and the timeblock together —
    /// <c>R25202P.SIF</c> — holding what is happening in the room rather than what the room
    /// is. Null when the caller gave only an asset suffix: <c>A</c> covers seven timeblocks
    /// and names none of them.
    /// </remarks>
    public string? TimeblockCode => State?.Timeblock.ToString();

    /// <summary>The evaluator to read the scene file through, when there is a state.</summary>
    public SceneConditions? Conditions { get; }

    /// <summary>
    /// The script host the conditions are decided through, when there is a state.
    /// </summary>
    /// <remarks>
    /// Shared with whatever else has to ask the story a question while the scene stands —
    /// the action files, above all, whose cases are Sheep expressions over the same state.
    /// Giving them a second host would give them a second answer.
    /// </remarks>
    public Gk3SheepApi? Api { get; }

    /// <summary>
    /// A request for the next room, in a story that is already under way.
    /// </summary>
    /// <param name="api">The host the story has been running against.</param>
    /// <param name="scene">Where the player is going.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="For"/> starts a story; this continues one. The difference is the whole
    /// point of walking through a door: a new state would forget everything the player has
    /// done, and a new host would forget every function the last room registered and every
    /// script it had loaded.
    /// </para>
    /// <para>
    /// The arrival is <em>not</em> recorded here. A scene file asks
    /// <c>GetEgoCurrentLocationCount() &lt; 1</c> to mean "the first time here", so while
    /// the file is being read the count still has to be the number of <em>previous</em>
    /// visits; the scripts that run once the room is standing ask for one instead. Counting
    /// the arrival first satisfies the scripts and not the file, and the two disagreeing is
    /// how RC1 came to play Gabriel's line about Wilkes's moped over a square with no moped
    /// in it — the scene had already decided not to place it. See
    /// <see cref="GameState.EnterLocation"/>, which the caller runs once the room is up.
    /// </para>
    /// </remarks>
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

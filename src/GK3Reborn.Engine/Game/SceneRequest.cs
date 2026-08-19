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
    private SceneRequest(string scene, string? assetSuffix, GameState? state)
    {
        Scene = scene;
        AssetSuffix = assetSuffix;
        State = state;
        Conditions = state is null ? null : new SceneConditions(new Gk3SheepApi(state));
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

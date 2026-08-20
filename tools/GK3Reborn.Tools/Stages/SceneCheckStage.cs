using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Game.Actors;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Rendering;
using GK3Reborn.UI.Interaction;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Loads every scene the game contains, at every point in the story it can be at.
/// </summary>
/// <remarks>
/// <para>
/// <c>Plan/04</c> makes "every scene loads headlessly" the exit criterion for P6, and this
/// is the thing that answers it. The loading is the engine's own — the same
/// <see cref="SceneLoader"/> the game uses, writing into a sink that counts instead of
/// drawing — so a pass here means the game can build these scenes, rather than meaning a
/// second implementation agrees with the first.
/// </para>
/// <para>
/// A scene is a location and a point in the story together, not a file, so the sweep is
/// over pairs: 79 locations against the 17 timeblocks the corpus names. Most pairs have no
/// timeblock file of their own and come out as the room with nobody in it, which is
/// correct and is most of the corpus; the ones that do are where the story lives.
/// </para>
/// <para>
/// What it reports is a baseline rather than a verdict. Some of what it finds is the
/// game's own — an actor placed at a spot the scene never defines, a noun with no verbs
/// because the two halves of an interaction were written in different files — and the
/// useful thing is that the numbers stay the same from one run to the next.
/// </para>
/// </remarks>
public sealed class SceneCheckStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SceneCheckStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Loads every scene at every timeblock.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="only">A location code to check on its own, or null for all of them.</param>
    /// <param name="deep">
    /// Whether to load the geometry, the bakes and every texture as well as the scene's
    /// composition. Off, the sweep answers what a scene is; on, it answers whether it
    /// loads, which costs about a second a pair.
    /// </param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True when nothing failed.</returns>
    public bool Run(string sourceDirectory, string? only, bool deep, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        IReadOnlyList<string> scenes = Locations(archives, only);
        IReadOnlyList<string> timeblocks = Timeblocks(archives);

        _log($"{scenes.Count} locations against {timeblocks.Count} timeblocks the corpus names: " +
             $"{string.Join(", ", timeblocks)}");

        var tally = new Tally();

        // What lets the sweep say how long the game's actions take rather than only what
        // they call. Reading an animation changes nothing, so this is as safe as the rest
        // of the sweep.
        tally.Api.Animations = new AnimationLibrary(archives);
        var loader = new SceneLoader(archives);

        foreach (string scene in scenes)
        {
            foreach (string timeblock in timeblocks)
            {
                Check(loader, scene, timeblock, deep, tally);
            }
        }

        Report(tally, deep);

        foreach (Diagnostic diagnostic in tally.Failures)
        {
            diagnostics.Add(diagnostic);
        }

        return tally.Failures.Count == 0;
    }

    /// <summary>Loads one location at one point in the story.</summary>
    private static void Check(
        SceneLoader loader, string scene, string timeblock, bool deep, Tally tally)
    {
        var diagnostics = new DiagnosticBag();
        var sink = new HeadlessSceneSink();
        SceneRequest request = SceneRequest.For(scene, timeblock);

        LoadedScene? loaded;

        try
        {
            loaded = deep
                ? loader.Load(sink, request, diagnostics)
                : loader.Compose(request, diagnostics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            tally.Failures.Add(new Diagnostic(
                "SCENE016",
                DiagnosticSeverity.Error,
                $"{scene} at {timeblock} threw while loading: {ex.GetType().Name}: {ex.Message}"));

            return;
        }

        tally.Pairs++;

        // A location's file can outlive the location. ARM's scene asset is named only
        // inside conditional blocks, so at a timeblock where none of them holds it names
        // none and there is nothing to load; MA2 names one the installation does not
        // contain at all. Either way the story cannot go there then, which is not the same
        // as the loader failing, so tell the two apart by asking whether an asset was found
        // rather than by whether loading returned something. Its complaints about the
        // missing geometry are still counted, they are just not held against it.
        LoadedScene? composed =
            loaded ?? loader.Compose(request, new DiagnosticBag());

        bool unavailable = loaded is null && composed?.Asset is null;

        if (unavailable)
        {
            tally.NoGeometry[scene] = tally.NoGeometry.GetValueOrDefault(scene) + 1;
        }

        // The diagnostics before the verdict, whatever happened: when a scene does not
        // load, the reason is in them, and reporting only that it failed throws it away.
        foreach (Diagnostic diagnostic in diagnostics.Items)
        {
            tally.Note(diagnostic, scene, timeblock, fatal: !unavailable);
        }

        if (loaded is null)
        {
            if (!unavailable)
            {
                tally.Failures.Add(new Diagnostic(
                    "SCENE017",
                    DiagnosticSeverity.Error,
                    $"{scene} at {timeblock} names a scene asset and still did not load."));
            }

            // Still worth counting what the scene says it is. A location with no geometry
            // at this point in the story still has a walk boundary, a cast and a set of
            // actions, and leaving those out would make the totals depend on whether the
            // sweep bothered to load any geometry.
            if (composed is not null)
            {
                Measure(composed, sink, deep: false, tally);
            }

            return;
        }

        Measure(loaded, sink, deep, tally);

        // The two things the scene file is read through, and the two that go quiet rather
        // than wrong when they fail: a function the host does not implement returns zero
        // and a condition that will not parse is treated as false, and either way the
        // scene loads in whichever state its unconditional block happens to describe.
        foreach (string unknown in request.Api?.UnknownFunctions ?? [])
        {
            tally.UnknownFunctions.Add(unknown);
        }

        foreach (Diagnostic diagnostic in request.Conditions?.Diagnostics.Items ?? [])
        {
            tally.Note(diagnostic, scene, timeblock);
        }

        if (loaded.Actions is { } resolver)
        {
            foreach (Diagnostic diagnostic in resolver.Diagnostics.Items)
            {
                tally.Note(diagnostic, scene, timeblock);
            }
        }
    }

    /// <summary>Adds one loaded scene to the running totals.</summary>
    private static void Measure(LoadedScene loaded, HeadlessSceneSink sink, bool deep, Tally tally)
    {
        SceneDefinition definition = loaded.Definition;

        tally.Models += definition.Models().Count;
        tally.Actors += definition.Actors().Count;
        tally.Cameras += definition.RoomCameras().Count;
        tally.Positions += definition.Positions().Count;

        if (definition.Specific is not null)
        {
            tally.Timeblocked++;
        }

        if (loaded.Walkable is { } boundary)
        {
            tally.Boundaries++;
            tally.WalkableTexels += boundary.WalkableTexels();

            if (definition.Boundary()?.Texture is { Length: > 0 } bitmap)
            {
                tally.Bitmaps.Add(bitmap);
            }
        }
        else if (definition.Boundary() is null)
        {
            tally.NoBoundary.Add(loaded.Name);
        }

        if (loaded.Ambient.Count > 0)
        {
            tally.Soundtracks++;
        }

        foreach (SoundtrackFile soundtrack in loaded.AmbienceRead)
        {
            tally.SoundtracksRead.Add(soundtrack.Name);
            tally.SoundtrackSteps += soundtrack.Nodes.Count;

            foreach (string sound in soundtrack.Sounds)
            {
                tally.Sounds.Add(sound);
            }
        }

        if (loaded.Actions is { } actions)
        {
            HashSet<string> known = new(actions.Nouns, StringComparer.OrdinalIgnoreCase);

            foreach (string noun in Nouns(definition))
            {
                tally.Nouns++;

                if (!known.Contains(noun))
                {
                    tally.NounsWithoutActions++;
                    tally.Unanswered.Add($"{loaded.Name} {noun}");
                    continue;
                }

                // Resolving is where the action files' own conditions are evaluated, so
                // this is the only part of the sweep that exercises them. A noun the files
                // know and that still offers nothing is not a mistake — most objects are
                // only usable at one point in the story — but the diagnostics it raises on
                // the way are worth having.
                IReadOnlyList<AvailableAction> available = actions.Resolve(noun);
                tally.Verbs += available.Count;

                // Whether the script behind each verb is one the runner could perform.
                // Reading it changes nothing, so this is safe to do for the whole corpus;
                // running them would be 24,000 stories at once.
                foreach (AvailableAction option in available)
                {
                    if (actions.Find(noun, option.LocalizedVerb) is not { } rule)
                    {
                        continue;
                    }

                    if (tally.Runner.Read(rule) is { } statements)
                    {
                        tally.Runnable++;
                        tally.Statements += statements.Count;

                        foreach (ActionStatement statement in statements)
                        {
                            tally.Called[statement.Call] = tally.Called.GetValueOrDefault(statement.Call) + 1;

                            if (!statement.Waited)
                            {
                                continue;
                            }

                            tally.Waited++;

                            if (statement.Seconds > 0)
                            {
                                tally.Timed++;
                                tally.Seconds += statement.Seconds;
                            }
                        }
                    }
                    else
                    {
                        tally.Unreadable.Add($"{loaded.Name} {noun}:{option.LocalizedVerb}");
                    }
                }
            }
        }

        if (!deep)
        {
            return;
        }

        tally.Loaded++;
        tally.Triangles += sink.TriangleCount;
        tally.Textures += sink.TextureCount;

        if (loaded.Geometry is { } bsp)
        {
            tally.Objects += bsp.ObjectNames.Count;
            tally.Targets += new ScenePicker(loaded).TargetCount;
        }
    }

    /// <summary>The nouns a scene hangs on its objects and its people.</summary>
    private static IEnumerable<string> Nouns(SceneDefinition definition) =>
        definition.Models().Select(m => m.Noun)
            .Concat(definition.Actors().Select(a => a.Noun))
            .Where(n => n is { Length: > 0 })
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every location the archives hold a general scene file for.</summary>
    /// <remarks>
    /// Three letters and nothing else: a longer name is a timeblock file, which describes
    /// what is happening in a room rather than what the room is, and is never loaded on its
    /// own.
    /// </remarks>
    private static IReadOnlyList<string> Locations(GameArchives archives, string? only)
    {
        if (only is { Length: > 0 })
        {
            return [only.ToUpperInvariant()];
        }

        return
        [
            .. archives.Names(".SIF")
                .Select(n => Path.GetFileNameWithoutExtension(n).ToUpperInvariant())
                .Where(n => n.Length == 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>Every point in the story the corpus names a scene file for.</summary>
    private static IReadOnlyList<string> Timeblocks(GameArchives archives) =>
    [
        .. archives.Names(".SIF")
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .Where(n => n.Length > 3)
            .Select(n => n[3..].ToUpperInvariant())
            .Where(code => Timeblock.TryParse(code, out Timeblock parsed) &&
                           string.Equals(parsed.ToString(), code, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Prints the totals.</summary>
    private void Report(Tally tally, bool deep)
    {
        _log(string.Empty);
        _log($"{tally.Pairs} location and timeblock pairs composed");
        _log($"  {tally.Timeblocked} have a timeblock file of their own");
        _log($"  {tally.Models} models, {tally.Actors} actors, {tally.Cameras} room cameras, " +
             $"{tally.Positions} positions");
        _log($"  {tally.Boundaries} load a walk boundary over {tally.Bitmaps.Count} bitmaps, " +
             $"{tally.WalkableTexels} open texels between them");

        if (tally.NoBoundary.Count > 0)
        {
            _log($"  declaring none: {string.Join(", ", tally.NoBoundary.Order(StringComparer.Ordinal))}");
        }

        _log($"  {tally.Soundtracks} name a soundtrack: {tally.SoundtracksRead.Count} distinct " +
             $"files read, {tally.SoundtrackSteps} steps, {tally.Sounds.Count} distinct sounds");

        if (tally.NoGeometry.Count > 0)
        {
            _log($"  {tally.NoGeometry.Values.Sum()} have no geometry at that point in the " +
                 "story, which is the story rather than a fault: " +
                 string.Join(
                     ", ",
                     tally.NoGeometry.OrderBy(p => p.Key, StringComparer.Ordinal)
                         .Select(p => $"{p.Key} x{p.Value}")));
        }
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"  {tally.Nouns} nouns on their objects, {tally.NounsWithoutActions} " +
            $"({tally.NounsWithoutActions * 100f / Math.Max(1, tally.Nouns):F1}%) unknown to the " +
            $"action files, {tally.Unanswered.Count} of them distinct"));
        _log($"  {tally.Verbs} verbs available across the nouns the action files do know");
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"  {tally.Runnable} of those have a script the runner can perform " +
            $"({tally.Runnable * 100f / Math.Max(1, tally.Verbs):F1}%), " +
            $"{tally.Statements} statements calling {tally.Called.Count} distinct functions"));

        _log(string.Create(CultureInfo.InvariantCulture,
            $"  {tally.Timed} of {tally.Waited} waited statements have a length " +
            $"({tally.Timed * 100f / Math.Max(1, tally.Waited):F1}%), " +
            $"{tally.Seconds / 60:F0} minutes of the player's time in all"));

        if (tally.Unreadable.Count > 0)
        {
            _log($"  cannot be performed: {string.Join(", ", tally.Unreadable.Take(6))}" +
                 (tally.Unreadable.Count > 6
                     ? $", and {tally.Unreadable.Count - 6} more"
                     : string.Empty));
        }

        // A call the host does not implement is recorded rather than performed, which is
        // right for the presentation surface and wrong for anything that moves the story.
        var api = new Gk3SheepApi(new GameState());

        // Attaching a host is what registers CallSheep and the inventory and location
        // functions, and attaching a scene is what registers the walker ones, so probing
        // without both would report a fifth of the corpus's calls as unimplemented when
        // the game does implement them. The scene is empty because only the registration
        // is being asked about, not what the functions would do.
        _ = new ScriptHost(api);
        SceneScripting.Attach(
            api,
            new LoadedScene("PROBE", new SceneDefinition(general: null), null, null, 0),
            new Glances());

        List<string> recorded =
            [.. tally.Called.Keys.Where(c => !api.Implements(c)).Order(StringComparer.OrdinalIgnoreCase)];

        _log($"  {tally.Called.Count - recorded.Count} of those {tally.Called.Count} functions " +
             $"are performed; the rest are recorded: {string.Join(", ", recorded)}");

        foreach ((string call, int count) in tally.Called.OrderByDescending(p => p.Value).Take(6))
        {
            _log($"    {call} x{count}");
        }
        _log(tally.UnknownFunctions.Count == 0
            ? "  every function the scene files call is implemented"
            : $"  functions no host implements: " +
              $"{string.Join(", ", tally.UnknownFunctions.Order(StringComparer.Ordinal))}");

        if (deep)
        {
            _log($"  {tally.Loaded} loaded their geometry: {tally.Triangles} triangles, " +
                 $"{tally.Objects} named objects, {tally.Textures} textures, " +
                 $"{tally.Targets} things a click can land on");
        }

        if (tally.Counts.Count == 0)
        {
            _log("  no diagnostics at all");
            return;
        }

        _log(string.Empty);
        _log("diagnostics, by code:");

        foreach ((string code, int count) in tally.Counts.OrderByDescending(p => p.Value))
        {
            _log($"  {code} x{count}: {tally.Examples[code]}");
        }
    }

    /// <summary>What the sweep has seen so far.</summary>
    private sealed class Tally
    {
        public int Pairs { get; set; }

        public int Timeblocked { get; set; }

        public int Models { get; set; }

        public int Actors { get; set; }

        public int Cameras { get; set; }

        public int Positions { get; set; }

        public int Boundaries { get; set; }

        public long WalkableTexels { get; set; }

        public int Soundtracks { get; set; }

        public int SoundtrackSteps { get; set; }

        public HashSet<string> SoundtracksRead { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Sounds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Nouns { get; set; }

        public int Verbs { get; set; }

        public int Runnable { get; set; }

        public int Statements { get; set; }

        /// <summary>Answers how long a call takes, and nothing else.</summary>
        public Gk3SheepApi Api { get; } = new(new GameState());

        /// <summary>Reads scripts without a story to run them against.</summary>
        public ActionRunner Runner => field ??= new ActionRunner(Api);

        public int Waited { get; set; }

        public int Timed { get; set; }

        public double Seconds { get; set; }

        public Dictionary<string, int> Called { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int NounsWithoutActions { get; set; }

        public int Loaded { get; set; }

        public long Triangles { get; set; }

        public int Objects { get; set; }

        public int Textures { get; set; }

        public int Targets { get; set; }

        public HashSet<string> Bitmaps { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> NoBoundary { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Unanswered { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> NoGeometry { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Examples { get; } = new(StringComparer.Ordinal);

        public HashSet<string> UnknownFunctions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<Diagnostic> Failures { get; } = [];

        /// <summary>Counts a diagnostic, keeping the first one of its code as the example.</summary>
        public void Note(Diagnostic diagnostic, string scene, string timeblock, bool fatal = true)
        {
            Counts[diagnostic.Code] = Counts.GetValueOrDefault(diagnostic.Code) + 1;

            if (!Examples.ContainsKey(diagnostic.Code))
            {
                Examples[diagnostic.Code] = $"{scene} {timeblock}: {diagnostic.Message}";
            }

            if (fatal && diagnostic.Severity == DiagnosticSeverity.Error)
            {
                Failures.Add(diagnostic);
            }
        }
    }
}

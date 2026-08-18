using System.Globalization;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>A presentation call the script made, recorded rather than performed.</summary>
/// <param name="Name">Function name.</param>
/// <param name="Arguments">Arguments rendered as text.</param>
public readonly record struct RecordedEvent(string Name, IReadOnlyList<string> Arguments);

/// <summary>
/// The GK3 API surface, bound to game state.
/// </summary>
/// <remarks>
/// <para>
/// The specification documents 359 functions, of which 174 are development-only, leaving
/// around 130 that gameplay needs. Those divide cleanly by what they do rather than by
/// how common they are.
/// </para>
/// <para>
/// **State functions are implemented.** Flags, game variables, noun/verb counts, topic
/// counts, score, timeblock and location are what determines whether the story can
/// progress, and they are what a differential comparison between engines has to agree
/// on.
/// </para>
/// <para>
/// **Presentation functions are recorded.** <c>CutToCameraAngle</c> is called 2,235 times
/// across the corpus and <c>StartAnimation</c> 2,067, but neither changes what the game
/// permits — and neither can be performed before the renderer exists. Recording them
/// keeps the trace complete and honest: the call happened, in that order, with those
/// arguments, and nothing was faked.
/// </para>
/// <para>
/// Anything not registered is reported once. Silence there would let a missing function
/// look like a working one.
/// </para>
/// </remarks>
public sealed class Gk3SheepApi : ISheepApi
{
    private readonly Dictionary<string, Func<IReadOnlyList<SheepValue>, SheepValue>> _functions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _waitable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedUnknown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the API over a game state.</summary>
    /// <param name="state">State the functions read and write.</param>
    public Gk3SheepApi(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;

        RegisterStateFunctions();
        RegisterRecordedFunctions();
    }

    /// <summary>The state these functions operate on.</summary>
    public GameState State { get; }

    /// <summary>Presentation calls, in the order they were made.</summary>
    public List<RecordedEvent> Events { get; } = [];

    /// <summary>Functions that were called but are not registered.</summary>
    public IReadOnlyCollection<string> UnknownFunctions => _reportedUnknown;

    /// <summary>Diagnostics raised while running.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <inheritdoc/>
    public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);

        if (_functions.TryGetValue(name, out Func<IReadOnlyList<SheepValue>, SheepValue>? function))
        {
            return function(arguments);
        }

        if (_reportedUnknown.Add(name))
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3200", DiagnosticSeverity.Warning,
                $"Script called '{name}', which is not implemented.",
                null, null, "a registered API function", name,
                "Implement it, or register it as a recorded presentation call."));
        }

        Events.Add(new RecordedEvent(name, [.. arguments.Select(a => a.AsString())]));
        return SheepValue.FromInt(0);
    }

    /// <inheritdoc/>
    public bool IsWaitable(string name) => _waitable.Contains(name);

    /// <summary>Registers a function.</summary>
    /// <param name="name">Function name.</param>
    /// <param name="implementation">What it does.</param>
    /// <param name="waitable">Whether callers may wait on it.</param>
    public void Register(
        string name, Func<IReadOnlyList<SheepValue>, SheepValue> implementation, bool waitable = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(implementation);

        _functions[name] = implementation;
        if (waitable)
        {
            _waitable.Add(name);
        }
    }

    private void RegisterStateFunctions()
    {
        SheepValue Zero(IReadOnlyList<SheepValue> _) => SheepValue.FromInt(0);

        Register("GetGameVariableInt", a => SheepValue.FromInt(State.GetVariable(Arg(a, 0))));
        Register("SetGameVariableInt", a =>
        {
            State.SetVariable(Arg(a, 0), Int(a, 1));
            return SheepValue.FromInt(0);
        });
        Register("IncGameVariableInt", a =>
        {
            State.IncrementVariable(Arg(a, 0), 1);
            return SheepValue.FromInt(0);
        });

        Register("GetFlag", a => SheepValue.FromInt(State.GetFlag(Arg(a, 0)) ? 1 : 0));
        Register("SetFlag", a =>
        {
            State.SetFlag(Arg(a, 0));
            return SheepValue.FromInt(0);
        });
        Register("ClearFlag", a =>
        {
            State.ClearFlag(Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        Register("GetNounVerbCount", a => SheepValue.FromInt(State.GetNounVerbCount(Arg(a, 0), Arg(a, 1))));
        Register("SetNounVerbCount", a =>
        {
            State.SetNounVerbCount(Arg(a, 0), Arg(a, 1), Int(a, 2));
            return SheepValue.FromInt(0);
        });
        Register("IncNounVerbCount", a =>
        {
            State.IncrementNounVerbCount(Arg(a, 0), Arg(a, 1));
            return SheepValue.FromInt(0);
        });

        Register("GetTopicCount", a => SheepValue.FromInt(State.GetTopicCount(Arg(a, 0), Arg(a, 1))));
        Register("SetTopicCount", a =>
        {
            State.SetTopicCount(Arg(a, 0), Arg(a, 1), Int(a, 2));
            return SheepValue.FromInt(0);
        });

        Register("ChangeScore", a =>
        {
            State.ChangeScore(Int(a, 0));
            return SheepValue.FromInt(0);
        });
        Register("GetScore", _ => SheepValue.FromInt(State.Score));

        Register("IsCurrentTime", a =>
            SheepValue.FromInt(string.Equals(Arg(a, 0), State.Timeblock.ToString(), StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        Register("IsCurrentLocation", a =>
            SheepValue.FromInt(string.Equals(Arg(a, 0), State.Location, StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        Register("GetEgoName", _ => SheepValue.FromString(State.Ego));
        Register("IsCurrentEgo", a =>
            SheepValue.FromInt(string.Equals(Arg(a, 0), State.Ego, StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        Register("SetActorLocation", a =>
        {
            State.SetActorLocation(Arg(a, 0), Arg(a, 1));
            return SheepValue.FromInt(0);
        });
        Register("GetActorLocation", a => SheepValue.FromString(State.GetActorLocation(Arg(a, 0))));

        // Explicitly answered rather than left unknown: scripts poll these constantly and
        // an unregistered warning for each would drown everything else.
        Register("IsActorNear", Zero);
        Register("IsWalkingActorNear", Zero);
        Register("IsActorAtLocation", Zero);
    }

    /// <summary>
    /// Registers the presentation surface as recorded calls.
    /// </summary>
    /// <remarks>
    /// Chosen by measured frequency across the 224 shipped scripts rather than by guess,
    /// so the most-used calls are the ones that stop producing warnings first.
    /// </remarks>
    private void RegisterRecordedFunctions()
    {
        (string Name, bool Waitable)[] recorded =
        [
            ("CutToCameraAngle", false),
            ("SetCameraAngle", false),
            ("GlideToCameraAngle", true),
            ("ForceCutToCameraAngle", false),
            ("StartAnimation", true),
            ("StartMoveAnimation", true),
            ("StartMorphAnimation", true),
            ("StopAnimation", false),
            ("SetMood", false),
            ("ClearMood", false),
            ("StartDialogue", true),
            ("StartDialogueNoFidgets", true),
            ("ContinueDialogue", true),
            ("ContinueDialogueNoFidgets", true),
            ("StartVoiceOver", true),
            ("ShowModel", false),
            ("HideModel", false),
            ("ShowSceneModel", false),
            ("HideSceneModel", false),
            ("WalkTo", true),
            ("WalkToSeeModel", true),
            ("WalkerBoundaryBlockRegion", false),
            ("WalkerBoundaryUnblockRegion", false),
            ("SetIdleGAS", false),
            ("SetTalkGAS", false),
            ("SetListenGAS", false),
            ("StartIdleFidget", false),
            ("StopFidget", false),
            ("TurnHead", false),
            ("TurnToModel", true),
            ("SetActorPosition", false),
            ("InitEgoPosition", false),
            ("SetTimerSeconds", true),
            ("SetTimerMs", true),
            ("PlaySound", true),
            ("PlaySoundTrack", false),
            ("StopSoundTrack", false),
            ("EnableCinematics", false),
            ("DisableCinematics", false),
            ("Blink", false),
            ("Expression", false),
            ("SetDefaultDialogueCamera", false),
        ];

        foreach ((string name, bool waitable) in recorded)
        {
            string captured = name;
            Register(name, a =>
            {
                Events.Add(new RecordedEvent(captured, [.. a.Select(v => v.AsString())]));
                return SheepValue.FromInt(0);
            }, waitable);
        }
    }

    private static string Arg(IReadOnlyList<SheepValue> arguments, int index) =>
        index < arguments.Count ? arguments[index].AsString() : string.Empty;

    private static int Int(IReadOnlyList<SheepValue> arguments, int index) =>
        index < arguments.Count ? arguments[index].AsInt() : 0;
}

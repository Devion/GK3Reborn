using System.Globalization;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;
using GK3Reborn.UI;

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

    /// <summary>Whether a function does something rather than being recorded.</summary>
    /// <param name="name">Function name.</param>
    /// <returns>True when it is registered.</returns>
    /// <remarks>
    /// Asked by tools that want to say what a script would really do before running it. An
    /// unregistered call is not an error — the presentation surface is deliberately
    /// recorded rather than performed — but a script whose every call is recorded has not
    /// moved the story, and the difference is worth being able to see.
    /// </remarks>
    public bool Implements(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _functions.ContainsKey(name);
    }

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

        // Both characters at once. It exists because the counts are per character, and a
        // door that has been opened is open for whoever walks in next.
        Register("SetNounVerbCountBoth", a =>
        {
            State.SetNounVerbCount("GABRIEL", Arg(a, 0), Arg(a, 1), Int(a, 2));
            State.SetNounVerbCount("GRACE", Arg(a, 0), Arg(a, 1), Int(a, 2));
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

        Register("IsActorAtLocation", a => SheepValue.FromInt(
            string.Equals(State.GetActorLocation(Arg(a, 0)), Arg(a, 1), StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0));

        Register("WasLastLocation", a => SheepValue.FromInt(
            string.Equals(State.LastLocation, Arg(a, 0), StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        // Counts are per timeblock and count previous visits; see GameState.EnterLocation
        // for why the current one is not among them.
        Register("GetEgoCurrentLocationCount", _ =>
            SheepValue.FromInt(State.GetLocationCount(State.Ego, State.Location)));

        Register("GetEgoLocationCount", a =>
            SheepValue.FromInt(State.GetLocationCount(State.Ego, Arg(a, 0))));

        Register("SetEgoLocationCount", a =>
        {
            State.SetLocationCount(State.Ego, Arg(a, 0), Int(a, 1));
            return SheepValue.FromInt(0);
        });

        Register("WasEgoEverInLocation", a =>
            SheepValue.FromInt(State.WasEverInLocation(State.Ego, Arg(a, 0)) ? 1 : 0));

        Register("GetChatCount", a => SheepValue.FromInt(State.GetChatCount(Arg(a, 0))));
        Register("SetChatCount", a =>
        {
            State.SetChatCount(Arg(a, 0), Int(a, 1));
            return SheepValue.FromInt(0);
        });
        Register("IncChatCount", a =>
        {
            State.IncrementChatCount(Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        // Inventory is asked about by name rather than by a parameter, so the two leads
        // get a function each, and ego gets a third that follows whoever is being played.
        Register("DoesGabeHaveInvItem", a =>
            SheepValue.FromInt(State.Inventory.Has("GABRIEL", Arg(a, 0)) ? 1 : 0));
        Register("DoesGraceHaveInvItem", a =>
            SheepValue.FromInt(State.Inventory.Has("GRACE", Arg(a, 0)) ? 1 : 0));
        Register("DoesEgoHaveInvItem", a =>
            SheepValue.FromInt(State.Inventory.Has(State.Ego, Arg(a, 0)) ? 1 : 0));

        // Which of the things in the bag is the one in hand. Using an item on something is
        // written in the action files as a verb named for the item, so this is how a rule
        // asks whether the player is about to use that one.
        Register("IsActiveInvItem", a => SheepValue.FromInt(
            string.Equals(
                State.Inventory.ActiveItemOf(State.Ego),
                Arg(a, 0),
                StringComparison.OrdinalIgnoreCase) ? 1 : 0));
        Register("SetEgoActiveInvItem", a =>
        {
            State.Inventory.SetActive(State.Ego, Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        Register("DoesSidneyFileExist", a =>
            SheepValue.FromInt(State.HasSidneyFile(Arg(a, 0)) ? 1 : 0));

        Register("GetRandomInt", a => SheepValue.FromInt(State.NextRandom(Int(a, 0), Int(a, 1))));

        // The Int forms take the noun and the verb as the identifiers a case's n$ and v$
        // carry. The original numbers those, because its script host can only pass integers
        // between a case and a function; this binds the names themselves, so the two forms
        // ask the same question and the Int suffix is only history.
        Register("GetNounVerbCountInt", a =>
            SheepValue.FromInt(State.GetNounVerbCount(Arg(a, 0), Arg(a, 1))));
        Register("GetTopicCountInt", a =>
            SheepValue.FromInt(State.GetTopicCount(Arg(a, 0), Arg(a, 1))));

        // The six statuses the original accepts, boiled down to who is holding the thing,
        // which is all any of them ever meant: NotPlaced, Placed and Used all say nobody.
        Register("SetInvItemStatus", a =>
        {
            string item = Arg(a, 0);

            switch (Arg(a, 1).ToUpperInvariant())
            {
                case "GRACEHAS":
                    Give("GRACE", item);
                    Take("GABRIEL", item);
                    break;
                case "GABEHAS":
                    Give("GABRIEL", item);
                    Take("GRACE", item);
                    break;
                case "BOTHHAVE":
                    Give("GABRIEL", item);
                    Give("GRACE", item);
                    break;
                case "NOTPLACED" or "PLACED" or "USED":
                    Take("GABRIEL", item);
                    Take("GRACE", item);
                    break;
                default:
                    Diagnostics.Add(new Diagnostic(
                        "GK3R3201", DiagnosticSeverity.Warning,
                        $"'{Arg(a, 1)}' is not an inventory status.",
                        null, null, "NotPlaced, Placed, Used, GabeHas, GraceHas or BothHave",
                        Arg(a, 1),
                        "The call is ignored, as it is in the original."));
                    break;
            }

            return SheepValue.FromInt(0);

            void Give(string who, string what) => State.Inventory.Add(who, what);
            void Take(string who, string what) => State.Inventory.Remove(who, what);
        });

        RegisterScreenFunctions();

        // Explicitly answered rather than left unknown: scripts poll these constantly and
        // an unregistered warning for each would drown everything else.
        Register("IsActorNear", Zero);
        Register("IsWalkingActorNear", Zero);
    }

    /// <summary>
    /// The screens a script can put in front of the room.
    /// </summary>
    /// <remarks>
    /// Every one of them goes on the same stack and comes off it the same way, which is
    /// what <c>Plan/03</c> section 3 asks for and what the original did not do. A script
    /// showing the binoculars and a player pressing Back are talking about the same object.
    /// </remarks>
    private void RegisterScreenFunctions()
    {
        Register("ShowInventory", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Inventory));
            return SheepValue.FromInt(0);
        });

        Register("HideInventory", _ =>
        {
            // The inspect panel belongs to the inventory that opened it, so it goes with
            // it. The original leaves it behind, which is a bug it shipped with: scanning
            // an item from the inspect screen left the panel over a room it had no
            // business being over.
            State.Screens.Hide(ScreenKind.InventoryInspect);
            State.Screens.Hide(ScreenKind.Inventory);
            return SheepValue.FromInt(0);
        });

        Register("InventoryInspect", a =>
        {
            State.Screens.Show(new Screen(ScreenKind.InventoryInspect, Arg(a, 0)));
            return SheepValue.FromInt(0);
        });

        Register("InventoryUninspect", _ =>
        {
            State.Screens.Hide(ScreenKind.InventoryInspect);
            return SheepValue.FromInt(0);
        });

        Register("InspectObject", a =>
        {
            State.Screens.Show(new Screen(ScreenKind.SceneInspect, Arg(a, 0)));
            return SheepValue.FromInt(0);
        });

        Register("InspectModelUsingAngle", a =>
        {
            State.Screens.Show(new Screen(ScreenKind.SceneInspect, Arg(a, 0)));
            return SheepValue.FromInt(0);
        });

        Register("UnInspect", _ =>
        {
            State.Screens.Hide(ScreenKind.SceneInspect);
            return SheepValue.FromInt(0);
        });

        Register("ShowBinocs", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Binoculars));
            return SheepValue.FromInt(0);
        });

        Register("ShowDrivingInterface", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Driving));
            return SheepValue.FromInt(0);
        });

        Register("FollowOnDrivingMap", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Driving, "follow"));
            return SheepValue.FromInt(0);
        });

        Register("ShowFingerprintInterface", a =>
        {
            State.Screens.Show(new Screen(ScreenKind.Fingerprint, Arg(a, 0)));
            return SheepValue.FromInt(0);
        });

        Register("SetVerbModal", a =>
        {
            State.MustChooseAnAction = Int(a, 0) != 0;
            return SheepValue.FromInt(0);
        });

        Register("IsTopLayerInventory", _ => SheepValue.FromInt(
            State.Screens.IsOnTop(ScreenKind.Inventory) ||
            State.Screens.IsOnTop(ScreenKind.InventoryInspect) ? 1 : 0));
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

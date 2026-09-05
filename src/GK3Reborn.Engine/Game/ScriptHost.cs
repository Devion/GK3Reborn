using System.Globalization;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>
/// Owns the loaded scripts and runs them, including when they call each other.
/// </summary>
/// <remarks>
/// <para>
/// <c>CallSheep</c> appears 640 times across the corpus and <c>Call</c> another 190, so a
/// virtual machine that cannot follow a call from one script into another cannot follow
/// the game's control flow at all. This is what closes that gap: a repository of scripts
/// by name, plus the API functions that jump between them.
/// </para>
/// <para>
/// Calls run to completion inline rather than being scheduled. The original waits on them
/// — <c>wait CallSheep(…)</c> is the common form — and running the callee immediately is
/// the same observable order for everything that does not depend on real elapsed time.
/// Recursion is bounded, because the data can and does call in circles.
/// </para>
/// </remarks>
public sealed class ScriptHost
{
    private readonly Dictionary<AssetId, SheepScriptFile> _scripts = [];
    private readonly SheepVirtualMachine _vm;
    private readonly Gk3SheepApi _api;
    private readonly int _maxDepth;
    private int _depth;

    /// <summary>Creates a host.</summary>
    /// <param name="api">The API surface, which this extends with the calling functions.</param>
    /// <param name="instructionLimit">Instruction budget for a single thread.</param>
    /// <param name="maxDepth">How deep script-to-script calls may nest.</param>
    public ScriptHost(Gk3SheepApi api, long instructionLimit = 200_000, int maxDepth = 32)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
        _vm = new SheepVirtualMachine(api, instructionLimit);
        _maxDepth = maxDepth;

        RegisterCallingFunctions();
        RegisterInventoryFunctions();

        // How anything that is not a Sheep thread gets to wait on a call into a script.
        // Set here rather than by the caller because it is the calling functions above
        // that make the threads, and they are registered here too.
        api.Collects = Within;

        // And how anything holding the API can ask whether a call would go anywhere. Set
        // here for the same reason: the scripts live here.
        api.Declares = Declares;
    }

    /// <summary>Scripts available to call, by name.</summary>
    public IReadOnlyCollection<AssetId> LoadedScripts => _scripts.Keys;

    /// <summary>Every function entered, in order, as <c>script:function</c>.</summary>
    public List<string> CallStackTrace { get; } = [];

    /// <summary>Diagnostics raised while running.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>The machine the host runs scripts on, for a scheduler to resume them.</summary>
    public SheepVirtualMachine Machine => _vm;

    /// <summary>
    /// Somewhere to leave a script that is waiting for something, if anywhere.
    /// </summary>
    /// <remarks>
    /// Without one, a blocked thread is told everything it waited on has finished and
    /// carried straight on, which is what this did from the beginning and what every tool
    /// still wants: a sweep of the corpus has no clock and no reason to want one. With
    /// one, the script waits, and the pacing it was written with is the pacing it gets.
    /// </remarks>
    public SheepScheduler? Scheduler
    {
        get => _scheduler;

        set
        {
            _scheduler = value;

            if (value is not null)
            {
                value.Calls = Within;
            }
        }
    }

    private SheepScheduler? _scheduler;

    /// <summary>Runs something, and says which scripts it called into.</summary>
    /// <remarks>
    /// <para>
    /// The list is held before it is pushed, so that popping it in a finally and returning
    /// it are the same list without a field in between.
    /// </para>
    /// <para>
    /// Public because two different things wait on a call into a script. A Sheep thread
    /// resumed by the scheduler is one; an action file's statement is the other, and it
    /// has no thread of its own to be parked, so it collects through here and waits on
    /// what it collected. See <see cref="Gk3SheepApi.Collects"/>.
    /// </para>
    /// </remarks>
    /// <param name="work">What to run.</param>
    /// <returns>The threads it started, in the order they were started.</returns>
    public List<SheepThread> Within(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        List<SheepThread> started = [];
        _nested.Push(started);

        try
        {
            work();
        }
        finally
        {
            _nested.Pop();
        }

        return started;
    }

    /// <summary>The script <c>CallGlobal</c> reaches into, once one has said it is.</summary>
    private string? _global;

    /// <summary>
    /// The scripts each running function has called, innermost last.
    /// </summary>
    /// <remarks>
    /// <c>wait CallSheep("rc1102p", "LookMop")</c> means "carry on when that function is
    /// over", and how long that takes is not knowable in advance: the function may itself
    /// wait on a timer, a line of dialogue or an animation. So the wait is on the
    /// <em>thread</em> rather than on a duration, and this is where a caller finds the
    /// threads it started. A fifth of every statement in the action corpus is one of these,
    /// and treating them as instant is what let RC1 show Wilkes's moped and hide it again
    /// in the same frame.
    /// </remarks>
    private readonly Stack<List<SheepThread>> _nested = new();

    /// <summary>Makes a script available to call.</summary>
    /// <param name="script">The script.</param>
    public void Add(SheepScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _scripts[AssetId.From(script.Name)] = script;
    }

    /// <summary>Whether a loaded script declares a function.</summary>
    /// <param name="scriptName">Script to look in, with or without extension.</param>
    /// <param name="functionName">Function to look for.</param>
    /// <returns>False when the script is not loaded or does not declare it.</returns>
    /// <remarks>
    /// Asking before calling, for a caller that has a choice about whether to call at all.
    /// <see cref="Run"/> is the wrong question for that: it warns and hands back an empty
    /// thread, which is the right answer for a script that <em>should</em> have been there
    /// and the wrong one for an interface deciding whether to offer a row.
    /// </remarks>
    public bool Declares(string scriptName, string functionName)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        ArgumentNullException.ThrowIfNull(functionName);

        return _scripts.TryGetValue(AssetId.From(scriptName), out SheepScriptFile? script) &&
               script.Functions.Any(
                   f => string.Equals(f.Name, functionName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs a function, following calls into other scripts.</summary>
    /// <param name="scriptName">Script to run, with or without extension.</param>
    /// <param name="functionName">Function to enter.</param>
    /// <returns>The thread, in whatever state it reached.</returns>
    public SheepThread Run(string scriptName, string functionName)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        ArgumentNullException.ThrowIfNull(functionName);

        if (!_scripts.TryGetValue(AssetId.From(scriptName), out SheepScriptFile? script))
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3400", DiagnosticSeverity.Warning,
                $"Script '{scriptName}' is not loaded.",
                null, null, "a loaded script", scriptName,
                "Load the script before calling into it, or check the name."));

            return new SheepThread(EmptyScript.Instance, functionName, 0);
        }

        // Plot armour. The five temple scripts each declare a Die that stops the music,
        // puts up the death screen and resets the puzzle behind it; with the assistance on,
        // the reset and the restart run and the death does not. Here rather than in the
        // API because a death is a function the story enters, not a call it makes, and this
        // is the one door every route into one goes through — the action file's
        // CallSheep("te6","Die"), TE4's AngelKills, TE5's fall, all of them.
        if (Assists.IsDeath(_api.State, script, functionName))
        {
            CallStackTrace.Add($"{AssetId.From(scriptName)}:{functionName}:survived");

            // Said out loud, because a player who has turned the assistance on and watched
            // Gabriel be struck down has no other way to tell a puzzle that reset him from
            // one that killed him.
            Log.Info($"Plot armour: {AssetId.From(scriptName)} would have killed Gabriel");

            SheepThread? instead = null;

            foreach (string step in Assists.Survive)
            {
                instead = Run(scriptName, step);
            }

            return instead ?? new SheepThread(script, functionName, 0);
        }

        CallStackTrace.Add($"{AssetId.From(scriptName)}:{functionName}");

        // Anything this function calls into is started while it runs, so a list is opened
        // for it first and taken back afterwards. That list is what a wait on a CallSheep
        // is actually waiting for; see below.
        _nested.Push([]);

        SheepThread thread;
        List<SheepThread> called;

        try
        {
            thread = _vm.Execute(script, functionName);
        }
        finally
        {
            called = _nested.Pop();
        }

        // And this thread is one of the enclosing function's calls, if there is one, so
        // that a wait two levels up covers everything underneath it.
        if (_nested.Count > 0)
        {
            _nested.Peek().Add(thread);
        }

        // With a scheduler, the thread waits its time out and somebody else carries it on.
        // Without one, the host assumes waited calls finish at once, which is what it did
        // before anything could take time and what every caller with no clock still needs.
        if (Scheduler?.Park(thread, called) != true)
        {
            int resumes = 0;

            while (thread.State is SheepThreadState.Blocked or SheepThreadState.Yielded &&
                   resumes++ < 1000)
            {
                SheepVirtualMachine.NotifyWaitsCompleted(thread);
                _vm.Resume(thread);
            }
        }

        foreach (Diagnostic diagnostic in thread.Diagnostics.Items)
        {
            Diagnostics.Add(diagnostic);
        }

        return thread;
    }

    private void RegisterCallingFunctions()
    {
        // Call(function) stays inside the script that is already running. The host is given
        // a name and no context, so the context is the machine's: the thread it is stepping
        // knows which script it belongs to. 190 of the game's calls are these — ARM202P
        // alone cuts between Gabe_CU$, Mose_CU$, TwoShot$ and Overview$ that way — and
        // leaving them recorded is a scene that runs with its camera never moving.
        _api.Register("Call", arguments =>
        {
            if (arguments.Count < 1 || _vm.Current is not { } running)
            {
                return SheepValue.FromInt(0);
            }

            return Nested(running.Script.Name, arguments[0].AsString());
        }, waitable: true);

        // The same thing, in whichever script was made the global one. Nothing in the game
        // calls it, and SetGlobalSheep is likewise unused, so it goes where it can and
        // records itself otherwise rather than inventing a policy for a case that does not
        // arise.
        _api.Register("CallGlobal", arguments =>
        {
            if (arguments.Count < 1)
            {
                return SheepValue.FromInt(0);
            }

            return _global is { Length: > 0 } global
                ? Nested(global, arguments[0].AsString())
                : SheepValue.FromInt(0);
        }, waitable: true);

        _api.Register("SetGlobalSheep", _ =>
        {
            _global = _vm.Current?.Script.Name;
            return SheepValue.FromInt(0);
        });

        // CallSheep(script, function) is the common form.
        _api.Register("CallSheep", arguments =>
        {
            if (arguments.Count < 2)
            {
                return SheepValue.FromInt(0);
            }

            return Nested(arguments[0].AsString(), arguments[1].AsString());
        }, waitable: true);

        _api.Register("CallGlobalSheep", arguments =>
            arguments.Count < 2
                ? SheepValue.FromInt(0)
                : Nested(arguments[0].AsString(), arguments[1].AsString()), waitable: true);

        _api.Register("CallSceneFunction", arguments =>
        {
            if (arguments.Count < 1)
            {
                return SheepValue.FromInt(0);
            }

            string function = arguments[0].AsString();

            // The room's own machinery first. Every one of the corpus's 43 calls names one
            // of these and none names a Sheep function, so this is the only branch the
            // shipped game ever takes — see Mechanisms.SceneMechanism for why the fallback
            // below silently did nothing for all of them.
            if (_api.Mechanism?.Perform(function) == true)
            {
                return SheepValue.FromInt(0);
            }

            // Otherwise, a function in the script named after the current location. Nothing
            // shipped uses it; it is kept because it is the reading the name suggests and
            // costs nothing when there is no such function.
            return _api.State.Location.Length == 0
                ? SheepValue.FromInt(0)
                : Nested(_api.State.Location, function);
        }, waitable: true);
    }

    private SheepValue Nested(string scriptName, string functionName)
    {
        if (_depth >= _maxDepth)
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3401", DiagnosticSeverity.Warning,
                "Script calls nested too deeply.",
                scriptName, null, $"fewer than {_maxDepth} nested calls", _depth.ToString(CultureInfo.InvariantCulture),
                "The scripts may call each other in a cycle. The innermost call is skipped."));

            return SheepValue.FromInt(0);
        }

        _depth++;
        try
        {
            Run(scriptName, functionName);
        }
        finally
        {
            _depth--;
        }

        return SheepValue.FromInt(0);
    }

    private void RegisterInventoryFunctions()
    {
        Inventory inventory = _api.State.Inventory;

        _api.Register("DoesEgoHaveInvItem", a =>
            SheepValue.FromInt(inventory.Has(_api.State.Ego, Arg(a, 0)) ? 1 : 0));

        _api.Register("DoesGabeHaveInvItem", a =>
            SheepValue.FromInt(inventory.Has("GABRIEL", Arg(a, 0)) ? 1 : 0));

        _api.Register("DoesGraceHaveInvItem", a =>
            SheepValue.FromInt(inventory.Has("GRACE", Arg(a, 0)) ? 1 : 0));

        _api.Register("EgoTakeInvItem", a =>
        {
            inventory.Add(_api.State.Ego, Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        _api.Register("GabeTakeInvItem", a =>
        {
            inventory.Add("GABRIEL", Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        _api.Register("GraceTakeInvItem", a =>
        {
            inventory.Add("GRACE", Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        _api.Register("EgoLoseInvItem", a =>
        {
            inventory.Remove(_api.State.Ego, Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        _api.Register("GraceLoseInvItem", a =>
        {
            inventory.Remove("GRACE", Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        _api.Register("CombineInvItems", a =>
        {
            // Combining consumes both and produces a third: the puzzle semantics matter,
            // so the two sources are removed rather than left in place.
            if (a.Count >= 3)
            {
                inventory.Remove(_api.State.Ego, Arg(a, 0));
                inventory.Remove(_api.State.Ego, Arg(a, 1));
                inventory.Add(_api.State.Ego, Arg(a, 2));
            }

            return SheepValue.FromInt(0);
        });

        _api.Register("SetLocation", a =>
        {
            _api.State.Location = Arg(a, 0);
            return SheepValue.FromInt(0);
        });

        _api.Register("SetEgo", a =>
        {
            _api.State.Ego = Arg(a, 0);
            return SheepValue.FromInt(0);
        });
    }

    private static string Arg(IReadOnlyList<SheepValue> arguments, int index) =>
        index < arguments.Count ? arguments[index].AsString() : string.Empty;

    /// <summary>A placeholder used when a call names a script that is not loaded.</summary>
    private static class EmptyScript
    {
        public static SheepScriptFile Instance { get; } = SheepScriptFile.Parse(Build(), "<missing>");

        private static byte[] Build()
        {
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            writer.Write("GK3Sheep"u8);
            writer.Write(0u);
            writer.Write(28);
            writer.Write(28);
            writer.Write(0);
            writer.Write(0);
            writer.Flush();
            return stream.ToArray();
        }
    }
}

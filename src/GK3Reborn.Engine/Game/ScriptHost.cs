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
    public SheepScheduler? Scheduler { get; set; }

    /// <summary>Makes a script available to call.</summary>
    /// <param name="script">The script.</param>
    public void Add(SheepScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _scripts[AssetId.From(script.Name)] = script;
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

        CallStackTrace.Add($"{AssetId.From(scriptName)}:{functionName}");

        SheepThread thread = _vm.Execute(script, functionName);

        // With a scheduler, the thread waits its time out and somebody else carries it on.
        // Without one, the host assumes waited calls finish at once, which is what it did
        // before anything could take time and what every caller with no clock still needs.
        if (Scheduler?.Park(thread) != true)
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
        // CallSheep(script, function) is the common form; Call(function) stays inside the
        // script that is already running, which the host cannot know here, so it is
        // recorded rather than guessed at.
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
            // Scene functions live in the script named after the current location.
            if (arguments.Count < 1 || _api.State.Location.Length == 0)
            {
                return SheepValue.FromInt(0);
            }

            return Nested(_api.State.Location, arguments[0].AsString());
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

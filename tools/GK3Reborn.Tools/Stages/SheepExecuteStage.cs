using System.Globalization;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Tools.Stages;

/// <summary>Totals from an execution sweep.</summary>
/// <param name="Scripts">Scripts executed.</param>
/// <param name="Functions">Functions entered.</param>
/// <param name="Completed">Functions that returned normally.</param>
/// <param name="Halted">Functions that reached <c>sitnspin</c>.</param>
/// <param name="Blocked">Functions that suspended on a wait block.</param>
/// <param name="Faulted">Functions that stopped on an error.</param>
/// <param name="Calls">API calls made in total.</param>
public readonly record struct SheepExecutionSummary(
    int Scripts, int Functions, int Completed, int Halted, int Blocked, int Faulted, long Calls);

/// <summary>
/// Runs every function of every script through the virtual machine.
/// </summary>
/// <remarks>
/// <para>
/// The API host does nothing but record what was asked of it and return a zero. That is
/// enough to exercise the whole instruction set against real bytecode: control flow,
/// arithmetic, string resolution, the calling convention and wait handling all run, and
/// anything the VM mishandles shows up as a fault or an unbalanced stack.
/// </para>
/// <para>
/// This is the sweep that turns "the VM works on the tests I wrote" into "the VM survives
/// 1,481 functions of code nobody wrote for it".
/// </para>
/// </remarks>
public sealed class SheepExecuteStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SheepExecuteStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Executes the corpus.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The totals.</returns>
    /// <param name="apiReturnValue">
    /// What every API call returns. Varying it steers conditionals down different paths,
    /// which is how a fault caused by the stub is told apart from one caused by the VM.
    /// </param>
    public SheepExecutionSummary Run(string sourceDirectory, DiagnosticBag diagnostics, int apiReturnValue = 0)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var api = new RecordingApi(apiReturnValue);
        var vm = new SheepVirtualMachine(api, instructionLimit: 200_000);

        int scripts = 0;
        int functions = 0;
        int completed = 0;
        int halted = 0;
        int blocked = 0;
        int faulted = 0;
        long calls = 0;
        Dictionary<string, int> faultCodes = new(StringComparer.Ordinal);

        foreach (FileInfo archiveFile in new DirectoryInfo(sourceDirectory)
                     .EnumerateFiles("*.brn")
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            using BarnArchive archive = BarnArchive.Open(archiveFile.FullName);

            foreach (BarnEntry entry in archive.Entries)
            {
                if (entry.IsPointer ||
                    !Path.GetExtension(entry.Name).Equals(".SHP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] data;
                try
                {
                    data = archive.Extract(entry);
                }
                catch (FormatParseException)
                {
                    continue;
                }

                if (!SheepScriptFile.IsSheep(data))
                {
                    continue;
                }

                SheepScriptFile script;
                try
                {
                    script = SheepScriptFile.Parse(data, entry.Name);
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                    continue;
                }

                scripts++;

                foreach ((string name, int _) in script.Functions)
                {
                    SheepThread thread = vm.Execute(script, name);
                    functions++;
                    calls += thread.Calls.Count;

                    switch (thread.State)
                    {
                        case SheepThreadState.Completed:
                            completed++;
                            break;
                        case SheepThreadState.Halted:
                            halted++;
                            break;
                        case SheepThreadState.Blocked:
                        case SheepThreadState.Yielded:
                            blocked++;
                            break;
                        default:
                            faulted++;
                            foreach (Diagnostic d in thread.Diagnostics.Items)
                            {
                                faultCodes[d.Code] = faultCodes.GetValueOrDefault(d.Code) + 1;
                            }

                            break;
                    }
                }
            }
        }

        foreach ((string code, int count) in faultCodes.OrderByDescending(kv => kv.Value))
        {
            _log($"    fault {code}: {count}");
        }

        _log(string.Create(CultureInfo.InvariantCulture,
            $"    {api.DistinctFunctions} distinct API functions invoked"));

        if (faulted > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2800", DiagnosticSeverity.Warning,
                $"{faulted} functions faulted during execution.",
                null, null, "every function to run without faulting",
                $"{faulted} faulted",
                "Fault codes are listed above; each names what the VM expected."));
        }

        return new SheepExecutionSummary(scripts, functions, completed, halted, blocked, faulted, calls);
    }

    /// <summary>
    /// An API host that records calls and returns nothing useful.
    /// </summary>
    /// <remarks>
    /// Every call returns the same constant, so conditionals take whichever branch that
    /// constant implies and this sweep does not explore both sides of a test. It exercises
    /// the machine, not the game.
    /// </remarks>
    private sealed class RecordingApi(int returnValue) : ISheepApi
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public int DistinctFunctions => _seen.Count;

        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
        {
            _seen.Add(name);
            return SheepValue.FromInt(returnValue);
        }

        // Without the specification's behaviour classification loaded, nothing is
        // treated as waitable, so wait blocks fall through rather than suspending.
        public bool IsWaitable(string name) => false;
    }
}

using System.Buffers.Binary;
using System.Globalization;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Sheep;

/// <summary>What a thread is doing.</summary>
public enum SheepThreadState
{
    /// <summary>Ready to run.</summary>
    Running,

    /// <summary>Returned normally.</summary>
    Completed,

    /// <summary>Waiting for calls in a wait block to finish.</summary>
    Blocked,

    /// <summary>Yielded to the scheduler and can resume immediately.</summary>
    Yielded,

    /// <summary>Executed <c>sitnspin</c>, which halts deliberately and forever.</summary>
    Halted,

    /// <summary>Stopped because something was wrong.</summary>
    Faulted,
}

/// <summary>A call the script made.</summary>
/// <param name="Name">API function name.</param>
/// <param name="Arguments">Arguments, in order.</param>
/// <param name="Waited">Whether the call was made inside a wait block.</param>
public readonly record struct SheepCall(string Name, IReadOnlyList<SheepValue> Arguments, bool Waited);

/// <summary>The host the virtual machine calls into.</summary>
public interface ISheepApi
{
    /// <summary>Invokes an API function.</summary>
    /// <param name="name">Function name, matched case-insensitively.</param>
    /// <param name="arguments">Arguments, in declaration order.</param>
    /// <returns>The return value; ignored for void functions.</returns>
    SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments);

    /// <summary>
    /// Reports whether a function can be waited on.
    /// </summary>
    /// <remarks>
    /// The original specification classifies every function as IMMEDIATE, WAIT or
    /// DEVELOPMENT, so this is metadata to be carried over rather than guessed at.
    /// </remarks>
    /// <param name="name">Function name.</param>
    /// <returns>True when the function is waitable.</returns>
    bool IsWaitable(string name);

    /// <summary>How long a waited call takes.</summary>
    /// <param name="name">Function name.</param>
    /// <param name="arguments">What it was called with, since the wait often is one.</param>
    /// <returns>Seconds, or zero when the host has no idea and the call is over at once.</returns>
    /// <remarks>
    /// Zero by default, which is what the engine did everywhere before anything could take
    /// time: a script waits and carries straight on. A host that knows better — a timer
    /// knows exactly, a camera glide knows its own duration — says so, and only then does a
    /// script's own pacing start to mean anything. Guessing for the calls whose length
    /// depends on assets that are not read yet would invent timing the game does not have.
    /// </remarks>
    double SecondsFor(string name, IReadOnlyList<SheepValue> arguments) => 0;
}

/// <summary>One running script.</summary>
public sealed class SheepThread
{
    internal SheepThread(SheepScriptFile script, string function, int address)
    {
        Script = script;
        FunctionName = function;
        Address = address;
    }

    /// <summary>The script being run.</summary>
    public SheepScriptFile Script { get; }

    /// <summary>The function this thread entered at.</summary>
    public string FunctionName { get; }

    /// <summary>Current bytecode position.</summary>
    public int Address { get; internal set; }

    /// <summary>What the thread is doing.</summary>
    public SheepThreadState State { get; internal set; } = SheepThreadState.Running;

    /// <summary>Instructions executed, which also bounds runaway scripts.</summary>
    public long InstructionsExecuted { get; internal set; }

    /// <summary>Every call the thread made, in order.</summary>
    public List<SheepCall> Calls { get; } = [];

    /// <summary>Diagnostics raised while running.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    internal List<SheepValue> Stack { get; } = [];

    internal Dictionary<int, SheepValue> Variables { get; } = [];

    internal bool InWaitBlock { get; set; }

    internal int PendingWaits { get; set; }

    /// <summary>
    /// How long the wait block this thread is in still has to run.
    /// </summary>
    /// <remarks>
    /// The longest of the calls inside it, because a wait block waits for all of them and
    /// is therefore over when the slowest is.
    /// </remarks>
    public double WaitSeconds { get; internal set; }
}

/// <summary>
/// Executes compiled Sheep.
/// </summary>
/// <remarks>
/// <para>
/// A stack machine, matching the original's conventions rather than improving on them.
/// Arguments are pushed in order followed by their count; a call pops the count, takes
/// that many arguments and pushes a result — even void functions push one, because the
/// original compiler emits a <c>Pop</c> after every void call and the stack would
/// otherwise drift.
/// </para>
/// <para>
/// Waiting is modelled as an explicit resumable state rather than by blocking a real
/// thread, which is what Plan/01-architecture.md section 6 requires: <c>wait</c> becomes
/// a suspension the game thread can resume, not an operation that stops the engine.
/// </para>
/// <para>
/// Execution is bounded. A script that loops forever stops with a fault rather than
/// hanging the caller, which matters because these scripts came from data the project
/// does not control.
/// </para>
/// </remarks>
public sealed class SheepVirtualMachine
{
    private readonly ISheepApi _api;
    private readonly long _instructionLimit;

    /// <summary>Creates a virtual machine.</summary>
    /// <param name="api">The host to call into.</param>
    /// <param name="instructionLimit">How many instructions a thread may execute before faulting.</param>
    public SheepVirtualMachine(ISheepApi api, long instructionLimit = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instructionLimit);

        _api = api;
        _instructionLimit = instructionLimit;
    }

    /// <summary>Starts a function and runs it as far as it will go.</summary>
    /// <param name="script">The script.</param>
    /// <param name="functionName">Function to enter, matched case-insensitively.</param>
    /// <returns>The thread, in whatever state it reached.</returns>
    public SheepThread Execute(SheepScriptFile script, string functionName)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(functionName);

        (string Name, int Offset)? entry = script.Functions
            .FirstOrDefault(f => Same(f.Name, functionName));

        if (entry is not { } found || found.Name is null)
        {
            var thread = new SheepThread(script, functionName, 0) { State = SheepThreadState.Faulted };
            thread.Diagnostics.Add(new Diagnostic(
                "GK3R3100", DiagnosticSeverity.Error,
                $"Script has no function '{functionName}'.",
                script.Name, null, "a declared function",
                string.Join(", ", script.Functions.Select(f => f.Name)),
                "Check the function name; Sheep matches names case-insensitively."));
            return thread;
        }

        var started = new SheepThread(script, found.Name, found.Offset);

        // Variables start at their declared initial values.
        for (int i = 0; i < script.Variables.Count; i++)
        {
            SheepVariable variable = script.Variables[i];
            started.Variables[i] = variable.Kind switch
            {
                SheepValueKind.Int => SheepValue.FromInt(variable.IntValue),
                SheepValueKind.Float => SheepValue.FromFloat(variable.FloatValue),
                _ => SheepValue.FromString(string.Empty),
            };
        }

        return Resume(started);
    }

    /// <summary>
    /// Whether two function names refer to the same function.
    /// </summary>
    /// <remarks>
    /// A compiled script names its functions with a <c>$</c> on the end — the disassembly
    /// of R25's reads <c>Window_Open$</c> — and the callers routinely leave it off:
    /// <c>CallSheep("R25_ALL","WINDOW_OPEN")</c> is how the action files spell it. The
    /// original appends the suffix when it is missing, with the comment "some GK3 data
    /// files do this, some don't". Matching exactly instead means the call finds nothing,
    /// the thread faults, and the action appears to run and do nothing at all — which is
    /// what opening R25's window did.
    /// </remarks>
    private static bool Same(string? declared, string wanted) =>
        declared is not null &&
        string.Equals(declared.TrimEnd('$'), wanted.TrimEnd('$'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tells a blocked thread that everything it waited on has finished.
    /// </summary>
    /// <remarks>
    /// The scheduler owns this: the VM suspends a thread and takes no view on when the
    /// animation, walk or timer it waited for actually completes. Calling this makes the
    /// thread runnable again; it does not run it.
    /// </remarks>
    /// <param name="thread">The blocked thread.</param>
    public static void NotifyWaitsCompleted(SheepThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        thread.PendingWaits = 0;
        thread.InWaitBlock = false;
        thread.WaitSeconds = 0;

        if (thread.State == SheepThreadState.Blocked)
        {
            thread.State = SheepThreadState.Yielded;
        }
    }

    /// <summary>
    /// The thread being stepped, while one is.
    /// </summary>
    /// <remarks>
    /// A script may ask the host to do something that depends on which script asked —
    /// <c>Call("TwoShot")</c> means "the function of that name <em>in me</em>", and there
    /// are 190 of those in the game. The host is given a name and no context, so this is
    /// the context. It nests, because a called script may call another.
    /// </remarks>
    public SheepThread? Current { get; private set; }

    /// <summary>Runs a thread until it stops.</summary>
    /// <param name="thread">The thread to run.</param>
    /// <returns>The same thread.</returns>
    public SheepThread Resume(SheepThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (thread.State is SheepThreadState.Completed or SheepThreadState.Faulted or SheepThreadState.Halted)
        {
            return thread;
        }

        SheepThread? outer = Current;
        Current = thread;

        try
        {
            return Step(thread);
        }
        finally
        {
            Current = outer;
        }
    }

    /// <summary>Steps one thread until it stops running.</summary>
    private SheepThread Step(SheepThread thread)
    {
        thread.State = SheepThreadState.Running;
        ReadOnlySpan<byte> code = thread.Script.Bytecode;

        while (thread.State == SheepThreadState.Running)
        {
            if (thread.InstructionsExecuted++ >= _instructionLimit)
            {
                Fault(thread, "GK3R3101", "Script exceeded its instruction limit.",
                    "a script that terminates",
                    thread.InstructionsExecuted.ToString(CultureInfo.InvariantCulture),
                    "The script may loop forever, or the limit may be too low for it.");
                break;
            }

            if ((uint)thread.Address >= (uint)code.Length)
            {
                // Running off the end is how a malformed jump presents.
                Fault(thread, "GK3R3102", "Execution ran past the end of the bytecode.",
                    $"an address below {code.Length}",
                    thread.Address.ToString(CultureInfo.InvariantCulture),
                    "A branch target is wrong, or the script is truncated.");
                break;
            }

            byte raw = code[thread.Address];
            if (!SheepOpcodes.IsDefined(raw))
            {
                Fault(thread, "GK3R3103", "Unknown instruction.",
                    "a defined opcode",
                    $"0x{raw:X2}",
                    "The bytecode is corrupt or uses an instruction this VM does not implement.");
                break;
            }

            var opcode = (SheepOpcode)raw;
            thread.Address++;

            int operand = 0;
            float operandFloat = 0;
            if (SheepOpcodes.OperandOf(opcode) != SheepOperand.None)
            {
                if (thread.Address + 4 > code.Length)
                {
                    Fault(thread, "GK3R3102", "Instruction operand runs past the end of the bytecode.",
                        "four more bytes", $"{code.Length - thread.Address} remaining",
                        "The script is truncated.");
                    break;
                }

                operand = BinaryPrimitives.ReadInt32LittleEndian(code[thread.Address..]);
                operandFloat = BinaryPrimitives.ReadSingleLittleEndian(code[thread.Address..]);
                thread.Address += 4;
            }

            Step(thread, opcode, operand, operandFloat);
        }

        return thread;
    }

    private void Step(SheepThread thread, SheepOpcode opcode, int operand, float operandFloat)
    {
        switch (opcode)
        {
            case SheepOpcode.SitnSpin:
                thread.State = SheepThreadState.Halted;
                break;

            case SheepOpcode.Yield:
                thread.State = SheepThreadState.Yielded;
                break;

            case SheepOpcode.ReturnV:
                thread.State = SheepThreadState.Completed;
                break;

            case SheepOpcode.CallSysFunctionV:
            case SheepOpcode.CallSysFunctionI:
            case SheepOpcode.CallSysFunctionF:
            case SheepOpcode.CallSysFunctionS:
                CallSystemFunction(thread, operand);
                break;

            case SheepOpcode.Branch:
            case SheepOpcode.BranchGoto:
                thread.Address = operand;
                break;

            case SheepOpcode.BranchIfZero:
                if (Pop(thread).AsInt() == 0)
                {
                    thread.Address = operand;
                }

                break;

            case SheepOpcode.BeginWait:
                thread.InWaitBlock = true;
                thread.PendingWaits = 0;
                thread.WaitSeconds = 0;
                break;

            case SheepOpcode.EndWait:
                if (thread.PendingWaits > 0)
                {
                    // Real suspension: the caller resumes this thread when the calls it is
                    // waiting on report completion.
                    thread.State = SheepThreadState.Blocked;
                }
                else
                {
                    thread.InWaitBlock = false;
                }

                break;

            case SheepOpcode.StoreI:
            case SheepOpcode.StoreF:
            case SheepOpcode.StoreS:
                thread.Variables[operand] = Pop(thread);
                break;

            case SheepOpcode.LoadI:
            case SheepOpcode.LoadF:
            case SheepOpcode.LoadS:
                thread.Stack.Add(thread.Variables.GetValueOrDefault(operand, SheepValue.FromInt(0)));
                break;

            case SheepOpcode.PushI:
                thread.Stack.Add(SheepValue.FromInt(operand));
                break;

            case SheepOpcode.PushF:
                thread.Stack.Add(SheepValue.FromFloat(operandFloat));
                break;

            case SheepOpcode.PushS:
                // The operand is an offset into the constant block, resolved by GetString.
                thread.Stack.Add(SheepValue.FromInt(operand));
                break;

            case SheepOpcode.GetString:
                {
                    int offset = Pop(thread).AsInt();
                    thread.Stack.Add(SheepValue.FromString(
                        thread.Script.StringConstants.GetValueOrDefault(offset, string.Empty)));
                    break;
                }

            case SheepOpcode.Pop:
                Pop(thread);
                break;

            case SheepOpcode.IToF:
            case SheepOpcode.FToI:
                {
                    // The operand says how far down the stack to convert, not that the top
                    // is the target.
                    int index = thread.Stack.Count - 1 - operand;
                    if (index >= 0 && index < thread.Stack.Count)
                    {
                        thread.Stack[index] = opcode == SheepOpcode.IToF
                            ? SheepValue.FromFloat(thread.Stack[index].AsInt())
                            : SheepValue.FromInt((int)thread.Stack[index].AsFloat());
                    }

                    break;
                }

            case SheepOpcode.Not:
                thread.Stack.Add(SheepValue.FromInt(Pop(thread).AsInt() == 0 ? 1 : 0));
                break;

            case SheepOpcode.NegateI:
                thread.Stack.Add(SheepValue.FromInt(-Pop(thread).AsInt()));
                break;

            case SheepOpcode.NegateF:
                thread.Stack.Add(SheepValue.FromFloat(-Pop(thread).AsFloat()));
                break;

            case SheepOpcode.DebugBreakpoint:
                break;

            default:
                Binary(thread, opcode);
                break;
        }
    }

    private static void Binary(SheepThread thread, SheepOpcode opcode)
    {
        SheepValue right = Pop(thread);
        SheepValue left = Pop(thread);

        SheepValue result = opcode switch
        {
            SheepOpcode.AddI => SheepValue.FromInt(left.AsInt() + right.AsInt()),
            SheepOpcode.AddF => SheepValue.FromFloat(left.AsFloat() + right.AsFloat()),
            SheepOpcode.SubtractI => SheepValue.FromInt(left.AsInt() - right.AsInt()),
            SheepOpcode.SubtractF => SheepValue.FromFloat(left.AsFloat() - right.AsFloat()),
            SheepOpcode.MultiplyI => SheepValue.FromInt(left.AsInt() * right.AsInt()),
            SheepOpcode.MultiplyF => SheepValue.FromFloat(left.AsFloat() * right.AsFloat()),

            // Division by zero yields zero rather than throwing: a data-driven script
            // must not be able to take the engine down.
            SheepOpcode.DivideI => SheepValue.FromInt(right.AsInt() == 0 ? 0 : left.AsInt() / right.AsInt()),
            SheepOpcode.DivideF => SheepValue.FromFloat(right.AsFloat() == 0 ? 0 : left.AsFloat() / right.AsFloat()),
            SheepOpcode.Modulo => SheepValue.FromInt(right.AsInt() == 0 ? 0 : left.AsInt() % right.AsInt()),

            SheepOpcode.IsEqualI => Bool(left.AsInt() == right.AsInt()),
            SheepOpcode.IsEqualF => Bool(left.AsFloat() == right.AsFloat()),
            SheepOpcode.IsNotEqualI => Bool(left.AsInt() != right.AsInt()),
            SheepOpcode.IsNotEqualF => Bool(left.AsFloat() != right.AsFloat()),
            SheepOpcode.IsGreaterI => Bool(left.AsInt() > right.AsInt()),
            SheepOpcode.IsGreaterF => Bool(left.AsFloat() > right.AsFloat()),
            SheepOpcode.IsLessI => Bool(left.AsInt() < right.AsInt()),
            SheepOpcode.IsLessF => Bool(left.AsFloat() < right.AsFloat()),
            SheepOpcode.IsGreaterEqualI => Bool(left.AsInt() >= right.AsInt()),
            SheepOpcode.IsGreaterEqualF => Bool(left.AsFloat() >= right.AsFloat()),
            SheepOpcode.IsLessEqualI => Bool(left.AsInt() <= right.AsInt()),
            SheepOpcode.IsLessEqualF => Bool(left.AsFloat() <= right.AsFloat()),
            SheepOpcode.And => Bool(left.AsInt() != 0 && right.AsInt() != 0),
            SheepOpcode.Or => Bool(left.AsInt() != 0 || right.AsInt() != 0),

            _ => SheepValue.FromInt(0),
        };

        thread.Stack.Add(result);
    }

    private void CallSystemFunction(SheepThread thread, int importIndex)
    {
        if ((uint)importIndex >= (uint)thread.Script.Imports.Count)
        {
            Fault(thread, "GK3R3104", "Call to an import that does not exist.",
                $"an index below {thread.Script.Imports.Count}",
                importIndex.ToString(CultureInfo.InvariantCulture),
                "The bytecode disagrees with the script's import table.");
            return;
        }

        SheepImport import = thread.Script.Imports[importIndex];

        // The count sits above the arguments, which is what lets a call know its own arity
        // without the VM tracking signatures.
        int argumentCount = Pop(thread).AsInt();
        if (argumentCount < 0 || argumentCount > thread.Stack.Count)
        {
            Fault(thread, "GK3R3105", $"Call to '{import.Name}' has an impossible argument count.",
                $"between 0 and {thread.Stack.Count}",
                argumentCount.ToString(CultureInfo.InvariantCulture),
                "The stack is unbalanced, which usually means an earlier instruction was mishandled.");
            return;
        }

        SheepValue[] arguments = new SheepValue[argumentCount];
        for (int i = argumentCount - 1; i >= 0; i--)
        {
            arguments[i] = Pop(thread);
        }

        bool waited = thread.InWaitBlock && _api.IsWaitable(import.Name);
        if (waited)
        {
            thread.PendingWaits++;

            // The longest call in the block decides when the block is over.
            thread.WaitSeconds = Math.Max(
                thread.WaitSeconds, _api.SecondsFor(import.Name, arguments));
        }

        thread.Calls.Add(new SheepCall(import.Name, arguments, waited));

        SheepValue result = _api.Invoke(import.Name, arguments);

        // Void calls push a result too; the compiler emits a matching Pop.
        thread.Stack.Add(result);
    }

    private static SheepValue Bool(bool value) => SheepValue.FromInt(value ? 1 : 0);

    private static SheepValue Pop(SheepThread thread)
    {
        if (thread.Stack.Count == 0)
        {
            return SheepValue.FromInt(0);
        }

        SheepValue value = thread.Stack[^1];
        thread.Stack.RemoveAt(thread.Stack.Count - 1);
        return value;
    }

    private static void Fault(
        SheepThread thread, string code, string message, string expected, string actual, string remediation)
    {
        thread.State = SheepThreadState.Faulted;
        thread.Diagnostics.Add(new Diagnostic(
            code, DiagnosticSeverity.Error, message,
            thread.Script.Name, thread.Address, expected, actual, remediation));
    }
}

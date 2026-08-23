using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>
/// Scripts that are waiting for something.
/// </summary>
/// <remarks>
/// <para>
/// A Sheep script says <c>wait StartAnimation("GraCs3WrdbOpen"); SetNounVerbCount(…)</c>
/// and means it: the second statement is not supposed to happen until the first has
/// finished. Without anywhere to put the waiting, the engine had to pretend everything
/// finished at once, which is the difference between a script that paces a scene and a
/// script that fires its whole contents into one frame.
/// </para>
/// <para>
/// The virtual machine has always parked a thread properly — a wait block with anything
/// outstanding leaves it <see cref="SheepThreadState.Blocked"/> — and nothing ever kept it
/// parked. This does. It holds the thread until the time the host said its calls would
/// take has passed, then resumes it, which may block it again on the next wait block.
/// </para>
/// <para>
/// A host that does not know how long a call takes says zero, and a wait on it is over
/// immediately, exactly as before. So this changes the pacing of the scripts whose waits
/// are measurable — a timer, a camera glide — and leaves every other script running as it
/// did.
/// </para>
/// </remarks>
public sealed class SheepScheduler
{
    private readonly SheepVirtualMachine _vm;
    private readonly List<Waiting> _waiting = [];

    /// <summary>Creates a scheduler over a machine.</summary>
    /// <param name="vm">The machine whose threads it resumes.</param>
    public SheepScheduler(SheepVirtualMachine vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
    }

    /// <summary>How many scripts are waiting for something.</summary>
    public int Count => _waiting.Count;

    /// <summary>What they are waiting on, in a stable order.</summary>
    public IReadOnlyList<string> Pending =>
        [.. _waiting.Select(w => $"{w.Thread.Script.Name}:{w.Thread.FunctionName}")];

    /// <summary>
    /// What to carry a resumed script out inside, so that anything it calls can be waited
    /// on in turn.
    /// </summary>
    /// <remarks>
    /// A function's second wait block is reached by being resumed, not by being started, so
    /// a <c>wait CallSheep(...)</c> there is issued from in here rather than from
    /// <see cref="ScriptHost.Run"/>. Given the work to do; answers which scripts it started.
    /// Null leaves a resumed script's calls unwaited, which is what a host with no notion
    /// of nested scripts wants.
    /// </remarks>
    public Func<Action, IReadOnlyList<SheepThread>>? Calls { get; set; }

    /// <summary>Takes charge of a script that has blocked.</summary>
    /// <param name="thread">The thread.</param>
    /// <param name="until">
    /// Scripts this one called and is waiting on, if any. A <c>wait CallSheep(...)</c> is
    /// over when the function it called is over, and that is not a length of time — it is
    /// another script, which may itself be waiting on an animation, a walk or a timer.
    /// </param>
    /// <returns>True when it was parked rather than being already finished.</returns>
    public bool Park(SheepThread thread, IReadOnlyList<SheepThread>? until = null)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (thread.State is not (SheepThreadState.Blocked or SheepThreadState.Yielded))
        {
            return false;
        }

        _waiting.Add(new Waiting(thread, thread.WaitSeconds) { Until = until });
        return true;
    }

    /// <summary>Lets time pass, and carries on whatever can carry on.</summary>
    /// <param name="seconds">How much time.</param>
    /// <returns>What was resumed, for whoever wants to say so.</returns>
    /// <remarks>
    /// A thread that blocks again goes straight back on the list with its new wait, so a
    /// script of five waited calls takes five waits to get through rather than one.
    /// </remarks>
    public IReadOnlyList<string> Advance(double seconds)
    {
        List<string> resumed = [];

        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            Waiting waiting = _waiting[i];
            waiting.Remaining -= seconds;

            if (waiting.Remaining > 0 || Outstanding(waiting.Until))
            {
                continue;
            }

            _waiting.RemoveAt(i);

            SheepThread carried = waiting.Thread;

            void Carry()
            {
                SheepVirtualMachine.NotifyWaitsCompleted(carried);
                _vm.Resume(carried);
            }

            IReadOnlyList<SheepThread>? called = null;

            if (Calls is { } within)
            {
                called = within(Carry);
            }
            else
            {
                Carry();
            }

            resumed.Add($"{carried.Script.Name}:{carried.FunctionName}");

            if (carried.State is SheepThreadState.Blocked or SheepThreadState.Yielded)
            {
                _waiting.Add(new Waiting(carried, carried.WaitSeconds) { Until = called });
            }
        }

        return resumed;
    }

    /// <summary>Whether any of the scripts a thread called is still going.</summary>
    /// <remarks>
    /// Being here is what "still going" means: a thread that finished was taken off this
    /// list, and one that blocked again went back on it. A caller that waited on a
    /// function which never blocked at all sees an empty answer and carries straight on,
    /// which is the ordinary case and costs nothing.
    /// </remarks>
    private bool Outstanding(IReadOnlyList<SheepThread>? until)
    {
        if (until is not { Count: > 0 })
        {
            return false;
        }

        foreach (SheepThread thread in until)
        {
            foreach (Waiting waiting in _waiting)
            {
                if (ReferenceEquals(waiting.Thread, thread))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Gives up on everything that is waiting.</summary>
    /// <remarks>
    /// For changing location, where a script waiting on something in the room it has left
    /// would resume into a scene that is no longer there.
    /// </remarks>
    public void Clear() => _waiting.Clear();

    private sealed class Waiting(SheepThread thread, double remaining)
    {
        public SheepThread Thread { get; } = thread;

        public double Remaining { get; set; } = remaining;

        /// <summary>Scripts this one called and cannot carry on without.</summary>
        public IReadOnlyList<SheepThread>? Until { get; init; }
    }
}

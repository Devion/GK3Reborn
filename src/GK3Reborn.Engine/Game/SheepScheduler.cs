using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>
/// Scripts that are waiting for something.
/// </summary>
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
    /// <param name="until">The threads to ask about.</param>
    /// <returns>True while any of them is still parked here.</returns>
    public bool Outstanding(IReadOnlyList<SheepThread>? until)
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
    public void Clear() => _waiting.Clear();

    private sealed class Waiting(SheepThread thread, double remaining)
    {
        public SheepThread Thread { get; } = thread;

        public double Remaining { get; set; } = remaining;

        /// <summary>Scripts this one called and cannot carry on without.</summary>
        public IReadOnlyList<SheepThread>? Until { get; init; }
    }
}

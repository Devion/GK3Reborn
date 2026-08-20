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

    /// <summary>Takes charge of a script that has blocked.</summary>
    /// <param name="thread">The thread.</param>
    /// <returns>True when it was parked rather than being already finished.</returns>
    public bool Park(SheepThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (thread.State is not (SheepThreadState.Blocked or SheepThreadState.Yielded))
        {
            return false;
        }

        _waiting.Add(new Waiting(thread, thread.WaitSeconds));
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

            if (waiting.Remaining > 0)
            {
                continue;
            }

            _waiting.RemoveAt(i);

            SheepVirtualMachine.NotifyWaitsCompleted(waiting.Thread);
            _vm.Resume(waiting.Thread);

            resumed.Add($"{waiting.Thread.Script.Name}:{waiting.Thread.FunctionName}");

            if (waiting.Thread.State is SheepThreadState.Blocked or SheepThreadState.Yielded)
            {
                _waiting.Add(new Waiting(waiting.Thread, waiting.Thread.WaitSeconds));
            }
        }

        return resumed;
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
    }
}

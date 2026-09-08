using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation;

namespace GK3Reborn.Game;

/// <summary>
/// Runs a <c>.STK</c> — the little program a room's sound is written as.
/// </summary>
public sealed class SoundtrackProgram
{
    private readonly SoundtrackFile _track;
    private readonly DeterministicRandom _chance;
    private readonly bool _loops;
    private readonly int[] _ran;

    private int _at = -1;
    private double _remaining;
    private bool _held;

    /// <summary>Starts a soundtrack.</summary>
    /// <param name="track">The file.</param>
    /// <param name="chance">
    /// Where the waits and the choices are drawn from. Deterministic, like everything else
    /// that draws: two runs of the same scene make the same noises at the same moments,
    /// which is what makes a recorded playthrough comparable — ADR 0004.
    /// </param>
    /// <param name="loops">
    /// Whether the list starts again at the end. A room's ambience does; a soundtrack
    /// played once by a script does not.
    /// </param>
    public SoundtrackProgram(SoundtrackFile track, DeterministicRandom chance, bool loops = true)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(chance);

        _track = track;
        _chance = chance;
        _loops = loops;
        _ran = new int[track.Nodes.Count];
    }

    /// <summary>The file being walked.</summary>
    public SoundtrackFile Track => _track;

    /// <summary>Which volume slider its sounds obey.</summary>
    public SoundtrackKind Kind => _track.Kind;

    /// <summary>The sound the current step started, or null when it started none.</summary>
    public SoundtrackSound? Sounding { get; private set; }

    /// <summary>Whether every node has run as often as it says it should.</summary>
    public bool Finished { get; private set; }

    /// <summary>Whether the walk has reached a sound that loops, and so cannot go on.</summary>
    public bool Holding => _held;

    /// <summary>How many steps have been taken, for tests and diagnostics.</summary>
    public int Steps { get; private set; }

    /// <summary>
    /// Runs the program forward.
    /// </summary>
    /// <param name="seconds">How much time has passed.</param>
    /// <param name="play">
    /// Starts a sound and answers how long it lasts, in seconds. Zero for a sound that
    /// could not be played, which costs the step its turn and no time — the same as the
    /// original, where a missing asset returns a length of nothing.
    /// </param>
    public void Advance(double seconds, Func<SoundtrackSound, double> play)
    {
        ArgumentNullException.ThrowIfNull(play);

        if (Finished || _held || _track.Nodes.Count == 0)
        {
            return;
        }

        _remaining -= seconds;

        // Bounded, because a file of nothing but waits that fail their chance would
        // otherwise walk its whole list every frame for ever. Sixty-four is more steps
        // than the longest soundtrack in the corpus has nodes.
        for (int guard = 0; guard < 64 && _remaining <= 0 && !Finished && !_held; guard++)
        {
            Step(play);
        }
    }

    /// <summary>Takes one step of the list.</summary>
    private void Step(Func<SoundtrackSound, double> play)
    {
        // The step that has just finished has run, whether or not it did anything: a node
        // that fails its chance still spends one of its repeats, which is what makes
        // "play this twice, then never again" mean what it says.
        if (_at >= 0 && _at < _ran.Length)
        {
            _ran[_at]++;
        }

        _at++;

        if (_at >= _track.Nodes.Count)
        {
            if (!_loops)
            {
                Finished = true;
                _remaining = double.MaxValue;
                return;
            }

            _at = 0;
        }

        if (Exhausted())
        {
            Finished = true;
            _remaining = double.MaxValue;
            return;
        }

        SoundtrackNode node = _track.Nodes[_at];
        Steps++;

        // Its repeat limit, reached: step over it without spending any time.
        if (node.Repeat > 0 && _ran[_at] >= node.Repeat)
        {
            _remaining = 0;
            return;
        }

        Sounding = null;

        // The chance is drawn per step rather than per file, and a step that fails it
        // takes no time — the list simply goes on to the next thing.
        if (node.Chance < 100 && _chance.NextInt32(1, 101) > node.Chance)
        {
            _remaining = 0;
            return;
        }

        _remaining = node.Step switch
        {
            SoundtrackStep.Wait => Waiting(node),
            SoundtrackStep.Sound or SoundtrackStep.PickRandom => Playing(node, play),
            _ => 0,
        };
    }

    /// <summary>How long a wait waits.</summary>
    private double Waiting(SoundtrackNode node)
    {
        int least = Math.Max(0, node.MinWaitMs);
        int most = node.MaxWaitMs;

        if (most <= least)
        {
            return least / 1000.0;
        }

        return _chance.NextInt32(least, most + 1) / 1000.0;
    }

    /// <summary>Starts a step's sound, and answers how long the step lasts.</summary>
    private double Playing(SoundtrackNode node, Func<SoundtrackSound, double> play)
    {
        if (node.Sounds.Count == 0)
        {
            return 0;
        }

        SoundtrackSound sound = node.Sounds.Count == 1
            ? node.Sounds[0]
            : node.Sounds[_chance.NextInt32(0, node.Sounds.Count)];

        double length = play(sound);

        Sounding = sound;

        // A looping sound is the end of the walk. There is no length that would take the
        // list past it, and the room goes on making that noise until it is left.
        if (sound.Loop)
        {
            _held = true;
            return double.MaxValue;
        }

        return length;
    }

    /// <summary>Whether there is nothing left for the program to do.</summary>
    private bool Exhausted()
    {
        for (int i = 0; i < _track.Nodes.Count; i++)
        {
            if (_loops)
            {
                if (_track.Nodes[i].Repeat == 0 || _ran[i] < _track.Nodes[i].Repeat)
                {
                    return false;
                }
            }
            else if (_ran[i] == 0)
            {
                return false;
            }
        }

        return true;
    }
}

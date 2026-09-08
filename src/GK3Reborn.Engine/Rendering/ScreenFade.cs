using System.Diagnostics;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn.Rendering;

/// <summary>
/// The fade between one room and the next.
/// </summary>
public sealed class ScreenFade
{
    /// <summary>How long the fade out takes when the load is slow enough to need all of it.</summary>
    public const double OutSeconds = 0.30;

    /// <summary>
    /// What "switch immediately" costs, in seconds.
    /// </summary>
    public const double SnapSeconds = 0.08;

    /// <summary>The shortest fade back in, in seconds.</summary>
    private const double LeastInSeconds = 0.10;

    /// <summary>
    /// What the display does to what the shader writes.
    /// </summary>
    private const double Gamma = 2.2;

    /// <summary>
    /// How often a frame is presented while the loader is working.
    /// </summary>
    private const double FrameSeconds = 1.0 / 30.0;

    private readonly Platform.SilkGameWindow _window;
    private readonly IRenderer _renderer;

    private Stopwatch? _out;
    private double _presented;

    private Stopwatch? _in;
    private double _length;
    private bool _first;

    /// <summary>Creates a fade over a window.</summary>
    /// <param name="window">The window, which still has to be pumped while it darkens.</param>
    /// <param name="renderer">What draws it.</param>
    public ScreenFade(Platform.SilkGameWindow window, IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(renderer);

        _window = window;
        _renderer = renderer;
    }

    /// <summary>Whether the picture is on its way out.</summary>
    public bool Leaving => _out is not null;

    /// <summary>Whether the picture is on its way back.</summary>
    public bool Arriving => _in is not null;

    /// <summary>
    /// Whether the way out has run its whole length and is now presenting black.
    /// </summary>
    public bool Faded =>
        _out is { IsRunning: true } clock && clock.Elapsed.TotalSeconds >= OutSeconds;

    /// <summary>
    /// Holds the last frame the player saw, and starts darkening it.
    /// </summary>
    public void Begin()
    {
        if (_renderer.Capture() is { } held)
        {
            _renderer.SetBackdrop(held);
        }

        _renderer.FadeColour = default;
        _renderer.Fade = 0f;

        // A frame ago, so the first offer is taken rather than turned away by the cadence
        // below. Nought here meant the first tick of every transition was skipped, and the
        // first tick is the one that arrives while the picture is still whole.
        _presented = -FrameSeconds;
        _in = null;

        // Made but not started. Between here and the first offer the caller frees the room
        // that has just been left, which is a tenth of a second of a large outdoor scene
        // coming off the device — and a fade whose clock ran through that would be a third
        // of the way down before it had drawn a single frame, so the first thing the player
        // saw of it would be a jump. The fade starts when there is somebody to draw it.
        _out = new Stopwatch();
    }

    /// <summary>
    /// Darkens the picture by however much time has passed, and shows it.
    /// </summary>
    public void Tick()
    {
        if (_out is not { } clock)
        {
            return;
        }

        if (!clock.IsRunning)
        {
            clock.Start();
        }

        double now = clock.Elapsed.TotalSeconds;

        // A whisker under the cadence, because an offer that arrives a microsecond early
        // is the same offer: turning one away costs the whole of the next gap, and the
        // gaps here are set by how long a piece of the load takes rather than by a clock.
        if (now - _presented < FrameSeconds * 0.9)
        {
            return;
        }

        _presented = now;
        _renderer.Fade = Curve(Math.Min(1.0, now / OutSeconds));
        Present();
    }

    /// <summary>
    /// Takes the picture the rest of the way to black.
    /// </summary>
    /// <returns>
    /// How long the way back should take, to hand to <see cref="ArriveOver"/>: as long as
    /// the way out actually took, so a transition the load cut short comes back as quickly
    /// as it went.
    /// </returns>
    public double Black()
    {
        if (_out is not { } clock)
        {
            return LeastInSeconds;
        }

        double reached = clock.Elapsed.TotalSeconds;

        // How much of the picture is still showing, in the units the eye reads it in
        // rather than in the alpha that produces them. See Curve.
        double showing = Math.Pow(1 - _renderer.Fade, 1 / Gamma);

        // What is left of the fade, at whichever speed is the quicker: its own, or the
        // couple of frames a load that beat it is allowed to cost.
        double remaining = Math.Min(SnapSeconds, showing * OutSeconds);

        if (showing > 0 && remaining > 0)
        {
            var snap = Stopwatch.StartNew();

            for (double through = 0; through < 1 && !_window.IsClosing;
                 through = snap.Elapsed.TotalSeconds / remaining)
            {
                _renderer.Fade = (float)(1 - Math.Pow(showing * (1 - through), Gamma));
                Present();
            }
        }

        _renderer.Fade = 1f;
        _out = null;
        _renderer.SetBackdrop(null);

        return Math.Clamp(reached + remaining, LeastInSeconds, OutSeconds);
    }

    /// <summary>Arms the fade back in, for the room's own loop to run.</summary>
    /// <param name="seconds">How long it should take, from <see cref="Black"/>.</param>
    public void ArriveOver(double seconds)
    {
        _length = Math.Max(LeastInSeconds, seconds);
        _first = true;
        _in = new Stopwatch();
    }

    /// <summary>
    /// Gives the screen back to something that will fill it itself.
    /// </summary>
    public void Clear() => _renderer.Fade = 0f;

    /// <summary>
    /// Lets the picture back in by one frame's worth.
    /// </summary>
    public void Advance()
    {
        if (_in is not { } clock)
        {
            return;
        }

        if (_first)
        {
            _first = false;
            _renderer.Fade = 1f;
            return;
        }

        if (!clock.IsRunning)
        {
            clock.Start();
        }

        double through = clock.Elapsed.TotalSeconds / _length;

        if (through >= 1)
        {
            _renderer.Fade = 0f;
            _in = null;
            return;
        }

        _renderer.Fade = Curve(1.0 - through);
    }

    /// <summary>Abandons the fade and puts the picture back the way it was.</summary>
    public void Cancel()
    {
        _out = null;
        _in = null;
        _renderer.Fade = 0f;
        _renderer.SetBackdrop(null);
    }

    /// <summary>
    /// Turns how far through the fade is into the alpha that will look like it.
    /// </summary>
    /// <param name="through">Nought at the start of the fade, one at the end.</param>
    /// <returns>What to draw the black at.</returns>
    public static float Curve(double through)
    {
        double t = Math.Clamp(through, 0, 1);
        double eased = t * t * (3 - (2 * t));

        return (float)(1 - Math.Pow(1 - eased, Gamma));
    }

    private void Present()
    {
        _window.PumpEvents();
        _window.EndFrame();
        _renderer.DrawFrame(0f, 0f, 0f);
    }
}

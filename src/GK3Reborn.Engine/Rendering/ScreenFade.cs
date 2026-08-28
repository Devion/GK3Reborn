using System.Diagnostics;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn.Rendering;

/// <summary>
/// The fade between one room and the next.
/// </summary>
/// <remarks>
/// <para>
/// A scene change is a stall. The room being left is torn off the device, the next one's
/// geometry, textures and acceleration structures are built, and between those two things
/// the window has nothing to show. Left alone that reads as the game hanging: the last
/// frame of the old room sits there for however long the load takes and is then replaced,
/// in a single frame, by somewhere else entirely.
/// </para>
/// <para>
/// <b>The load runs inside the fade rather than after it.</b> That is the whole point of
/// doing it this way. A warm walk through a door is a couple of hundred milliseconds, and a
/// cold arrival with ray tracing turned up and a packed content set to read is well over a
/// second; no single fade length suits both, and a fade long enough to cover the slow one
/// is a fade the fast one waits on for nothing. So the darkening is driven by the clock
/// while the loader reads, and a loader that finishes early stops the fade where it is.
/// </para>
/// <para>
/// <b>What darkens is a photograph.</b> The room's buffers have to be freed before the next
/// room's are allocated — two scenes' worth of enhanced textures resident at once is how a
/// transition becomes an out-of-memory — so the last frame the player saw is read back off
/// the swapchain and hung behind everything like a title card. Over a third of a second, a
/// still of the room and the room are the same picture.
/// </para>
/// <para>
/// The two halves are driven differently on purpose. Going out there is nothing to update,
/// so the frames are presented from here; coming back the room is standing and running, so
/// the fade is a number the room's own loop reads while it draws — which is the difference
/// between arriving into a room and arriving into a photograph of one.
/// </para>
/// </remarks>
public sealed class ScreenFade
{
    /// <summary>How long the fade out takes when the load is slow enough to need all of it.</summary>
    public const double OutSeconds = 0.30;

    /// <summary>
    /// What "switch immediately" costs, in seconds.
    /// </summary>
    /// <remarks>
    /// Not nought. A load that beats the fade leaves the picture part way down, and cutting
    /// from a half-dark room to a half-dark different room is the hard cut this exists to
    /// avoid — the fade would have made the change less visible and instead drawn attention
    /// to it. A couple of frames of darkening is under a tenth of a second, which nobody
    /// waits on and which the eye reads as a transition rather than as a jump.
    /// </remarks>
    public const double SnapSeconds = 0.08;

    /// <summary>The shortest fade back in, in seconds.</summary>
    private const double LeastInSeconds = 0.10;

    /// <summary>
    /// What the display does to what the shader writes.
    /// </summary>
    /// <remarks>
    /// The sRGB transfer function, near enough: the standard is a linear toe and a 2.4
    /// power above it, and 2.2 is the single exponent that fits the whole of it to within
    /// a step or two of eight-bit. What it is used for here is the shape of a ramp, and
    /// nothing that shape is wrong by is visible.
    /// </remarks>
    private const double Gamma = 2.2;

    /// <summary>
    /// How often a frame is presented while the loader is working.
    /// </summary>
    /// <remarks>
    /// Thirty a second, and it matters. The loader offers a tick after every texture it
    /// uploads, and presenting on each of them would put the fade in front of a swapchain
    /// that presents in FIFO — so a room with four hundred textures would wait on vsync
    /// four hundred times and take seven seconds to read what it reads in half of one. At
    /// this cadence, with two frames in flight, a submission never has to wait at all.
    /// </remarks>
    private const double FrameSeconds = 1.0 / 30.0;

    private readonly Platform.SilkGameWindow _window;
    private readonly VulkanRenderer _renderer;

    private Stopwatch? _out;
    private double _presented;

    private Stopwatch? _in;
    private double _length;
    private bool _first;

    /// <summary>Creates a fade over a window.</summary>
    /// <param name="window">The window, which still has to be pumped while it darkens.</param>
    /// <param name="renderer">What draws it.</param>
    public ScreenFade(Platform.SilkGameWindow window, VulkanRenderer renderer)
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
    /// Holds the last frame the player saw, and starts darkening it.
    /// </summary>
    /// <remarks>
    /// Called while the room being left is still on the device: the photograph comes off
    /// the swapchain, so there has to be something in it. Afterwards the caller is free to
    /// throw the room away — what is on screen no longer depends on it.
    /// </remarks>
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
    /// <remarks>
    /// Handed to the loader, which offers it between the pieces of work it does. Cheap and
    /// rate-limited, so a loader that ticks per texture costs one frame every thirtieth of
    /// a second and nothing at all on the ticks in between.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Called once the next room is standing and about to be drawn. The swap always happens
    /// at black, however little of the fade the load turned out to need — see
    /// <see cref="SnapSeconds"/>.
    /// </para>
    /// <para>
    /// The photograph is taken down here rather than by the caller, because it was put up
    /// here and a backdrop left standing would cover the room it was hiding the loss of.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// A timeblock that ends between two rooms puts a closing film and a title card in the
    /// gap, and both of those are the picture rather than something over it. Leaving the
    /// fade standing at black would draw black over them. Nothing is lost by dropping it:
    /// the room is already gone and the screen is already black, which is exactly what a
    /// film or a card wants to start from.
    /// </remarks>
    public void Clear() => _renderer.Fade = 0f;

    /// <summary>
    /// Lets the picture back in by one frame's worth.
    /// </summary>
    /// <remarks>
    /// Called from the room's own loop, before it draws, so that everything in the room is
    /// moving while the fade lifts. The first call is not timed: the first frame of a new
    /// room builds its acceleration structure and can take tens of milliseconds, and
    /// counting that against the fade would start it somewhere in its own middle.
    /// </remarks>
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
    /// <remarks>For a transition that could not finish — a room that would not load.</remarks>
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
    /// <remarks>
    /// <para>
    /// Two corrections, and the second is not optional. Smoothstep eases the ends, so the
    /// fade starts and stops without a visible corner.
    /// </para>
    /// <para>
    /// <b>And then the gamma.</b> The swapchain is sRGB, so the hardware decodes the
    /// picture to linear light before it blends and encodes the result afterwards — which
    /// means an alpha of a half leaves the screen at 73% of its brightness rather than at
    /// 50%, and an alpha of 0.995 still has the room faintly visible in it. Driven
    /// straight, the fade looks like nothing happening for a quarter of a second and then
    /// the picture falling off a cliff. Asking instead for the alpha that darkens the
    /// <em>encoded</em> value in a straight line — one minus what is left, raised to 2.2 —
    /// is what makes the ramp look like the ramp it is.
    /// </para>
    /// </remarks>
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

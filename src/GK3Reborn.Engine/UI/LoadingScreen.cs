using System.Diagnostics;
using System.Numerics;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>
/// What the window shows while something slow is being read.
/// </summary>
public sealed class LoadingScreen
{
    /// <summary>
    /// How long a load may take before it is worth saying anything about, in seconds.
    /// </summary>
    public const double SlowSeconds = 0.5;

    /// <summary>
    /// How long the whole screen takes to go, in seconds.
    /// </summary>
    private const double OutSeconds = 0.35;

    /// <summary>How often a frame is presented while the loader works.</summary>
    private const double FrameSeconds = 1.0 / 30.0;

    /// <summary>The menu's own palette, so this looks like the same interface.</summary>
    private static readonly Vector4 Veil = new(0f, 0f, 0f, 0.62f);
    private static readonly Vector4 Track = new(0.16f, 0.18f, 0.22f, 1f);
    private static readonly Vector4 Accent = new(0.85f, 0.68f, 0.36f, 1f);
    private static readonly Vector4 Ink = new(0.86f, 0.87f, 0.90f, 1f);

    private readonly Platform.SilkGameWindow _window;
    private readonly IRenderer _renderer;
    private readonly ScreenFade _fade;

    private readonly Stopwatch _clock = new();

    private OverlayAtlas? _blank;
    private OverlayAtlas? _cut;
    private Overlay? _sheet;

    private bool _armed;
    private bool _quiet;
    private double _waited;
    private double _through;
    private double _presented;
    private double? _owed;

    /// <summary>Creates a loading screen over a window.</summary>
    /// <param name="window">The window, which still has to be pumped while it waits.</param>
    /// <param name="renderer">What draws it.</param>
    /// <param name="fade">
    /// The transition between rooms, which covers the first part of a load on its own and
    /// which this takes over from. See <see cref="ScreenFade.Faded"/>.
    /// </param>
    public LoadingScreen(
        Platform.SilkGameWindow window, IRenderer renderer, ScreenFade fade)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(fade);

        _window = window;
        _renderer = renderer;
        _fade = fade;
    }

    /// <summary>
    /// The interface's own sheet of letters, once there is one.
    /// </summary>
    public OverlayAtlas? Atlas { get; set; }

    /// <summary>What the word is written in.</summary>
    public UiText Text { get; set; } = UiText.English;

    /// <summary>Whether the bar is on screen.</summary>
    public bool Showing { get; private set; }

    /// <summary>How much of the work is done, from nought to one.</summary>
    public double Through => _through;

    /// <summary>
    /// Says that something slow is starting, and puts a frame up straight away.
    /// </summary>
    /// <param name="waited">How long the wait had already been going on for.</param>
    /// <param name="bar">
    /// Whether this load may show itself. False for a load that happens with the game
    /// already running: a door leads out of one room and into another, and the fade between
    /// them is the whole of what the player should see. A bar in the middle of that says the
    /// game stopped to go and fetch something, which is exactly the impression the fade
    /// exists to avoid. The screen still runs — it is what gives the fade its frames while
    /// the room is being read — it just never appears.
    /// </param>
    public void Begin(TimeSpan waited = default, bool bar = true)
    {
        // Only if something went wrong: the fade is owed its length back by whoever took it
        // over, and Done is what pays it. Beginning again with one outstanding would lose
        // it, and a room that arrives with no fade at all is a hard cut.
        if (_owed is { } outstanding)
        {
            _owed = null;
            _fade.ArriveOver(outstanding);
        }

        _armed = true;
        _quiet = !bar;
        _through = 0;
        _presented = double.NegativeInfinity;
        _waited = Math.Max(0, waited.TotalSeconds);
        _clock.Restart();

        // Offered rather than assumed either way: a load the caller says was already slow
        // before this could exist comes up with its bar on the first frame, and one that is
        // only starting gets a black window and nothing else.
        Tick();

        if (!_quiet && !Showing && !_fade.Leaving)
        {
            Present();
        }
    }

    /// <summary>Records how far through the work is, and shows it if it is time.</summary>
    /// <param name="through">Nought at the start, one at the end.</param>
    public void At(double through)
    {
        _through = Math.Clamp(Math.Max(_through, through), 0, 1);
        Tick();
    }

    /// <summary>
    /// Offers a frame, at whatever the last <see cref="At"/> said.
    /// </summary>
    public void Tick()
    {
        if (!_armed)
        {
            return;
        }

        // The fade first, and while there is any of it left. It is still showing the room
        // being left, which is a better picture than anything here, and the two of them
        // presenting alternate frames would be a flicker between a photograph and a bar.
        if (_fade.Leaving && !_fade.Faded)
        {
            _fade.Tick();

            return;
        }

        double now = _waited + _clock.Elapsed.TotalSeconds;

        if (!Showing)
        {
            if (_quiet || now < SlowSeconds)
            {
                // Not slow enough to be worth saying anything about, or not a load that is
                // allowed to say anything at all. The fade, if there is one, still wants its
                // frames — pumping it is the whole of what this does on a quiet load.
                if (_fade.Leaving)
                {
                    _fade.Tick();
                }

                return;
            }

            Appear();
        }

        if (now - _presented < FrameSeconds * 0.9)
        {
            return;
        }

        _presented = now;

        Draw(1f);
        Present();
    }

    /// <summary>
    /// Takes the screen down, and gives the fade back whatever this took over from it.
    /// </summary>
    public void Done()
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        _clock.Stop();

        if (Showing)
        {
            Showing = false;

            var going = Stopwatch.StartNew();

            for (double through = 0; through < 1 && !_window.IsClosing;
                 through = going.Elapsed.TotalSeconds / OutSeconds)
            {
                Draw((float)(1 - through));
                Present();
            }

            _renderer.SetOverlay(null);
        }

        if (_owed is { } seconds)
        {
            _owed = null;
            _fade.ArriveOver(seconds);
        }
    }

    /// <summary>
    /// Takes the screen over from the fade, and puts the bar up.
    /// </summary>
    private void Appear()
    {
        Showing = true;

        if (_fade.Leaving)
        {
            _owed = _fade.Black();
        }

        _renderer.Fade = 0f;
    }

    /// <summary>Lays the screen out and hands it to the renderer.</summary>
    /// <param name="opacity">How much of it to draw, for the way out.</param>
    private void Draw(float opacity)
    {
        OverlayAtlas atlas = Atlas ?? (_blank ??= OverlayAtlas.Blank());

        if (_sheet is null || !ReferenceEquals(_cut, atlas))
        {
            _cut = atlas;
            _sheet = new Overlay(atlas);
        }

        // The first sheet has to be handed over rather than left to the display list.
        // SetOverlay uploads whatever atlas the list was cut from — but only once there is
        // an overlay pipeline to upload it to, and on Vulkan there is none until something
        // asks for one. This is the something: nothing else in the game does, until the
        // archives have been read and the interface's own sheet exists, which is precisely
        // what this screen is covering. Without it the bar was drawn on Direct3D and
        // silently missing on Vulkan.
        if (!_renderer.HasOverlay)
        {
            _renderer.SetOverlayAtlas(atlas);
        }

        int width = Math.Max(1, _window.FramebufferWidth);
        int height = Math.Max(1, _window.FramebufferHeight);

        _sheet.Begin(width, height);

        // Over the whole window, whatever is behind it. With nothing behind it this is black
        // over black and costs one rectangle; with the title screen behind it, it is what
        // makes a line of letters readable over a painting.
        _sheet.Rect(0, 0, width, height, Fainter(Veil, opacity));

        // A ninetieth of the window, which is where a bar stops reading as a hairline on a
        // 4K display and stops reading as a loading bar from a different decade on a small
        // one. Never less than three pixels: the track and the fill have to be told apart.
        float thickness = MathF.Max(3f, height / 90f);
        float barWidth = MathF.Round(width * 0.42f);
        float left = MathF.Round((width - barWidth) / 2f);
        float top = MathF.Round(height * 0.62f);

        _sheet.Rect(left, top, barWidth, thickness, Fainter(Track, opacity));
        _sheet.Rect(
            left,
            top,
            MathF.Round(barWidth * (float)_through),
            thickness,
            Fainter(Accent, opacity));

        // The word, over the bar and only where there is a font to write it with. Measured
        // rather than centred by character count, because the atlas may have been cut from
        // an outline at any size the window asked for.
        string word = Text.Say("loading", "Loading");
        float measured = _sheet.Measure(word);

        if (measured > 0)
        {
            _sheet.Text(
                word,
                MathF.Round((width - measured) / 2f),
                MathF.Round(top - (_sheet.LineHeight * 1.75f)),
                Fainter(Ink, opacity));
        }

        _renderer.SetOverlay(_sheet);
    }

    /// <summary>The same colour, drawn at a share of its own alpha.</summary>
    private static Vector4 Fainter(Vector4 colour, float opacity) =>
        colour with { W = colour.W * Math.Clamp(opacity, 0f, 1f) };

    private void Present()
    {
        _window.PumpEvents();
        _window.EndFrame();
        _renderer.DrawFrame(0f, 0f, 0f);
    }
}

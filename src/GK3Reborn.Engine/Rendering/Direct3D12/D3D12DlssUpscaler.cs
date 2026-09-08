using System.Globalization;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// DLSS on Direct3D 12.
/// </summary>
public sealed unsafe class D3D12DlssUpscaler : IDisposable
{
    private readonly Streamline _streamline;
    private readonly (uint Width, uint Height) _render;
    private readonly (uint Width, uint Height) _display;
    private readonly UpscalerQuality _quality;
    private readonly int _preset;
    private readonly bool _highDynamicRange;
    private readonly bool _rayReconstruction;
    private bool _disposed;

    private D3D12DlssUpscaler(
        Streamline streamline,
        (uint Width, uint Height) render,
        (uint Width, uint Height) display,
        UpscalerQuality quality,
        int preset,
        bool highDynamicRange,
        bool rayReconstruction)
    {
        _streamline = streamline;
        _render = render;
        _display = display;
        _quality = quality;
        _preset = preset;
        _highDynamicRange = highDynamicRange;
        _rayReconstruction = rayReconstruction;
    }

    /// <summary>Which upscaler this is.</summary>
    public static UpscalerKind Kind => UpscalerKind.Dlss;

    /// <summary>What it is doing, for the startup report.</summary>
    /// <returns>A line naming the version and the two sizes.</returns>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{_streamline.SuperResolutionVersion}, " +
        $"{(_rayReconstruction ? "ray reconstruction, " : string.Empty)}" +
        $"{_render.Width}x{_render.Height} to {_display.Width}x{_display.Height}");

    /// <summary>Turns the feature on for these sizes, or returns null.</summary>
    /// <param name="context">The device.</param>
    /// <param name="streamline">Streamline as the host started it, or null.</param>
    /// <param name="plan">What was asked for.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <param name="tracing">Whether the picture is being ray traced.</param>
    /// <returns>The upscaler, or null when DLSS is not available.</returns>
    public static D3D12DlssUpscaler? TryCreate(
        D3D12Context context,
        Streamline? streamline,
        UpscalePlan plan,
        (uint Width, uint Height) render,
        (uint Width, uint Height) display,
        bool tracing = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);

        if (streamline is not { Ready: true })
        {
            return null;
        }

        // Whether a traced picture is needed depends on which denoising plugin loaded. The
        // documented feature wants normals, roughness and albedo and has nothing to do
        // without them; neural rendering asks for colour, depth and motion, which this engine
        // draws whether or not it traced anything.
        // Ray reconstruction only. The neural uplift used to be run from here too, and that
        // was the wrong place for it: it reworks a finished picture and wants one that has
        // been tone-mapped, where this stage hands over linear light with lamps and windows
        // hundreds of times over one. It runs in the renderer now, after the tone map.
        bool reconstruction = !streamline.NeuralRenderingLoaded &&
                              plan.RayReconstruction &&
                              streamline.CanReconstruct(plan.Quality) &&
                              tracing;

        if (!streamline.SetDlssOptions(
                plan.Quality,
                plan.DlssPreset,
                display,
                plan.HighDynamicRange,
                reconstruction,
                plan.Neural))
        {
            Log.Warning("WARNING GK3R3436: DLSS refused the options it was given.");
            return null;
        }

        return new D3D12DlssUpscaler(
            streamline,
            render,
            display,
            plan.Quality,
            plan.DlssPreset,
            plan.HighDynamicRange,
            reconstruction);
    }

    /// <summary>Whether this upscaler already does what is being asked for.</summary>
    /// <param name="plan">What is wanted.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <returns>True when nothing needs rebuilding.</returns>
    public bool Serves(UpscalePlan plan, (uint Width, uint Height) render, (uint Width, uint Height) display)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Kind == UpscalerKind.Dlss &&
               plan.Quality == _quality &&
               plan.DlssPreset == _preset &&
               plan.HighDynamicRange == _highDynamicRange &&
               render == _render &&
               display == _display;
    }

    /// <summary>Runs the feature over one frame.</summary>
    /// <param name="list">The frame's command list.</param>
    /// <param name="colour">The room as drawn, in linear light at render resolution.</param>
    /// <param name="depth">Its depth buffer.</param>
    /// <param name="motion">Where each pixel was a frame ago, in pixels.</param>
    /// <param name="output">Where to put the result, at display resolution.</param>
    /// <param name="frame">The rest of what the runtime is told about this frame.</param>
    /// <returns>True when the runtime did the work.</returns>
    public bool Record(
        ID3D12GraphicsCommandList4* list,
        D3D12Texture colour,
        D3D12Texture depth,
        D3D12Texture motion,
        D3D12Texture output,
        in StreamlineFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(colour);
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);

        StreamlineFrame described = frame with
        {
            Colour = Surface(colour),
            Depth = Surface(depth),
            Motion = Surface(motion),
            Output = Surface(output),
        };

        return _streamline.Evaluate((nint)list, described, _rayReconstruction);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _streamline.ReleaseDlss();
    }

    /// <summary>Says what a texture is in the terms Streamline asks for.</summary>
    private static UpscaleSurface Surface(D3D12Texture texture) => new(
        (nint)texture.Handle,
        0,
        (uint)texture.State,
        (uint)texture.Width,
        (uint)texture.Height,
        (uint)texture.Format);
}

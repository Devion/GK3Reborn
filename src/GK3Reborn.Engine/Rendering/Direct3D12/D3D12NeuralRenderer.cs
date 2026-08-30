// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// The neural rendering network on Direct3D 12, driven straight through NGX.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the super-resolution pass rather than running after it. The network scales
/// and reworks in one step from the same three pictures — colour, depth and motion — so
/// putting it downstream of another temporal upscaler would be two histories filtering one
/// frame, which is how a picture ends up smeared.
/// </para>
/// <para>
/// <b>Streamline is not involved and need not even be present.</b> Everything here goes to
/// <c>nvngx_dlssnr.dll</c> through <see cref="Ngx"/>. Streamline carries on beside it for
/// frame generation and Reflex, which is why the two are started separately and neither is
/// asked about the other.
/// </para>
/// <para>
/// <b>The feature is built on the first frame that wants it, not when this object is made.</b>
/// NGX records its setting-up work onto a command list, so there has to be an open one — and
/// the only place this engine has one is where the frame is recorded. Everything up to that
/// point is opening the library and making a parameter block, which is why a failure to build
/// is reported once, from inside a frame, rather than at startup.
/// </para>
/// </remarks>
public sealed unsafe class D3D12NeuralRenderer : IDisposable
{
    /// <summary>The colour, at the size the room was drawn.</summary>
    private static ReadOnlySpan<byte> Colour => "DLSSNR.Color"u8;

    /// <summary>Where the network writes, at the size the picture is shown.</summary>
    private static ReadOnlySpan<byte> Output => "DLSSNR.Output"u8;

    private readonly Ngx _ngx;
    private readonly NgxParameters _parameters;
    private readonly (uint Width, uint Height) _render;
    private readonly (uint Width, uint Height) _display;
    private readonly bool _highDynamicRange;

    private NeuralUplift _uplift;
    private nint _feature;
    private bool _refused;
    private bool _fresh = true;
    private bool _disposed;

    private D3D12NeuralRenderer(
        Ngx ngx,
        NgxParameters parameters,
        (uint Width, uint Height) render,
        (uint Width, uint Height) display,
        bool highDynamicRange,
        NeuralUplift uplift)
    {
        _ngx = ngx;
        _parameters = parameters;
        _render = render;
        _display = display;
        _highDynamicRange = highDynamicRange;
        _uplift = uplift;
    }

    /// <summary>Whether the camera's sample point should be moved a little each frame.</summary>
    /// <remarks>
    /// <para>
    /// <b>False while the neural network is the thing running, and that is not a preference.</b>
    /// Jitter is how a super-resolution network is given more samples than the frame holds:
    /// the camera samples a different point inside each pixel every frame, the runtime is told
    /// where, and it reconstructs from the spread. The neural network is told nothing —
    /// <c>nvngx_dlssnr.dll</c> has no jitter parameter of any kind, and the plugin that drives
    /// it sends none — and super resolution is not running underneath it, because only the one
    /// feature is evaluated.
    /// </para>
    /// <para>
    /// So a jittered frame reaches a network that accumulates across frames, using motion
    /// vectors that say nothing moved, from a picture that shifted by a fraction of a pixel in
    /// a pattern it cannot know. What that looks like is the whole image seething — worst on
    /// small bright things and fine stonework, which is to say worst in a church.
    /// </para>
    /// </remarks>
    public static bool WantsJitter => false;

    /// <summary>Whether the network has given up, and the frame should go another way.</summary>
    /// <remarks>
    /// Set when the network would not build, or refused a frame. Both are permanent for this
    /// feature — a network that refuses one frame refuses every frame for the same reason —
    /// so the caller reads this and lets go, rather than asking again sixty times a second
    /// and drawing the small picture stretched while it does.
    /// </remarks>
    public bool Refused => _refused;

    /// <summary>What it is doing, for the startup report.</summary>
    /// <returns>A line naming the network and the two sizes.</returns>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"neural rendering{Named(_ngx.Version)}, {_uplift.Summarise()}, " +
        $"{_render.Width}x{_render.Height} to {_display.Width}x{_display.Height}");

    /// <summary>Opens the network for these sizes, or returns null.</summary>
    /// <param name="context">The device.</param>
    /// <param name="runtimes">Where the player put the vendors' files.</param>
    /// <param name="plan">What was asked for.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <returns>The renderer, or null when the network is not there or would not start.</returns>
    public static D3D12NeuralRenderer? TryCreate(
        D3D12Context context,
        UpscalerRuntimes? runtimes,
        UpscalePlan plan,
        (uint Width, uint Height) render,
        (uint Width, uint Height) display)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.Neural.Enabled)
        {
            return null;
        }

        if (runtimes?.Locate(UpscalerRuntimes.NgxRayReconstruction) is not { Length: > 0 } snippet)
        {
            return null;
        }

        Ngx? ngx = Ngx.TryStart(snippet, (nint)context.Device);

        if (ngx is null)
        {
            return null;
        }

        NgxParameters parameters = ngx.Allocate();

        if (!parameters.Exists)
        {
            ngx.Dispose();
            return null;
        }

        return new D3D12NeuralRenderer(
            ngx, parameters, render, display, plan.HighDynamicRange, plan.Neural.Sane());
    }

    /// <summary>Whether this already does what is being asked for.</summary>
    /// <param name="plan">What is wanted.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <returns>True when nothing needs rebuilding.</returns>
    /// <remarks>
    /// The strengths are deliberately not part of this. They are read afresh every frame and
    /// change nothing the network was built around, so a player dragging a slider should see
    /// the picture change under their hand rather than watch the feature be torn down and
    /// built again — which would drop the history and flash.
    /// </remarks>
    public bool Serves(
        UpscalePlan plan,
        (uint Width, uint Height) render,
        (uint Width, uint Height) display)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (_disposed || !plan.Neural.Enabled)
        {
            return false;
        }

        // The two that are built in, and so cannot be changed under a live feature: the sizes
        // decide the network's working memory, and the preset decides which weights it loaded.
        bool built = render == _render &&
                     display == _display &&
                     plan.HighDynamicRange == _highDynamicRange &&
                     plan.Neural.Preset == _uplift.Preset;

        if (!built)
        {
            return false;
        }

        _uplift = plan.Neural.Sane();
        return true;
    }

    /// <summary>Runs the network over one frame.</summary>
    /// <param name="list">The frame's command list.</param>
    /// <param name="colour">The room as drawn, in linear light at render resolution.</param>
    /// <param name="depth">Its depth buffer.</param>
    /// <param name="motion">Where each pixel was a frame ago, in render-resolution pixels.</param>
    /// <param name="output">Where to put the result, at display resolution.</param>
    /// <param name="frame">The rest of what the network is told about this frame.</param>
    /// <returns>True when the network did the work.</returns>
    /// <remarks>
    /// The four textures must already be in the states the caller put them in — the three
    /// inputs readable by a compute shader and the output writable — and no barrier is issued
    /// here. NGX believes what it is handed.
    /// </remarks>
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

        if (_refused)
        {
            return false;
        }

        if (_feature == 0 && !Build((nint)list))
        {
            return false;
        }

        Describe(_parameters);
        Textures(_parameters, colour, depth, motion, output);
        Strengths(_parameters, frame);

        uint result = _ngx.Evaluate((nint)list, _feature, _parameters);

        if (Ngx.Ok(result))
        {
            _fresh = false;
            return true;
        }

        // Once, and then never again for this feature. A network that refuses one frame
        // refuses every frame for the same reason, and a line per frame is not a log.
        _refused = true;

        Log.Warning(
            "WARNING GK3R3455: neural rendering: the network would not take the frame (" +
            Ngx.Reason(result) + "); falling back to the picture as drawn.");

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The caller waits for the queue before disposing the pipeline, which is what makes
        // this safe: NGX frees the network's working memory here, and freeing memory a queue
        // is still reading is a device loss rather than a leak.
        _ngx.Release(_feature);
        _feature = 0;

        _ngx.Destroy(_parameters);
        _ngx.Dispose();
    }

    /// <summary>
    /// Works out the ratio between the two sizes, when the network asks rather than is told.
    /// </summary>
    /// <param name="parameters">The block being built, which already carries both sizes.</param>
    /// <returns>Success, always: there is nothing here that can fail.</returns>
    /// <remarks>
    /// <para>
    /// The network keeps a hook for this so that a caller who has named a quality rung and no
    /// sizes can be given both. This engine is the other way round — it has already decided
    /// what to draw at, from the upscaler ladder the player set — so what is installed here
    /// answers from the sizes in the block rather than from a rung.
    /// </para>
    /// <para>
    /// <b>The ratio is the small size over the large one</b>, so it is one when nothing is
    /// being scaled and a half at twice. That direction is not a guess: the add-in this was
    /// checked against passes exactly one and exactly a half for those two cases.
    /// </para>
    /// <para>
    /// Nothing is captured. It reads the block it is handed and writes back into it, so there
    /// is no instance for it to belong to and no lifetime for it to outlive — which matters,
    /// because the network holds the pointer for as long as the feature lives.
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Ratio(void* parameters)
    {
        if (parameters is null)
        {
            return 0xBAD00005;
        }

        var block = new NgxParameters(parameters);

        uint drawn = 0;
        uint shown = 0;

        block.TryGet("DLSSNR.InputWidth"u8, ref drawn);
        block.TryGet("DLSSNR.OutputWidth"u8, ref shown);

        // Believed only when it could be a pair of window widths with the drawn one no
        // larger. Anything else — nought, a size no display has, the two the wrong way round
        // — means the block did not answer, and one is the ratio that leaves the picture the
        // size it already is rather than a ratio invented from rubbish.
        bool sane = drawn is > 0 and <= 16384 && shown is > 0 and <= 16384 && drawn <= shown;

        float ratio = sane ? (float)drawn / shown : 1f;

        block.Set("DLSSNR.ScalingRatio"u8, ratio);
        block.Set("DLSSNR.Scale"u8, ratio);

        return 1;
    }

    private static string Named(string version) =>
        version is { Length: > 0 } ? " " + version : string.Empty;

    /// <summary>Builds the feature, recording its setting-up onto the frame's list.</summary>
    private bool Build(nint list)
    {
        Describe(_parameters);

        // A rung the caller is not using. The sizes above are the whole of what this network
        // is being asked to scale between, and the ladder would only compute them again — so
        // it is set to the first value the plugin accepts and left alone.
        _parameters.Set("PerfQualityValue"u8, 0);

        // What the network has to know about its inputs before it is built. High dynamic
        // range where the frame carries it; motion at the size the room was drawn, which is
        // what this engine produces; and depth the ordinary way round, near at nought, which
        // is what the camera here is set up for.
        int flags = 0;

        if (_highDynamicRange)
        {
            flags |= 1;
        }

        flags |= 2;

        _parameters.Set("DLSS.Feature.Create.Flags"u8, flags);
        _parameters.Set("DLSSNR.Hint.Render.Preset"u8, (uint)_uplift.Preset);

        // One card. Both masks name the same node, which is what a single-adapter engine
        // means by them.
        _parameters.Set("CreationNodeMask"u8, 1u);
        _parameters.Set("VisibilityNodeMask"u8, 1u);

        uint result = _ngx.Create(list, Ngx.FeatureNeuralRendering, _parameters, out _feature);

        if (Ngx.Ok(result) && _feature != 0)
        {
            _fresh = true;

            Log.Info(
                "DLSS: neural rendering is running (" + Describe() + ").");

            return true;
        }

        _refused = true;
        _feature = 0;

        Log.Warning(
            "WARNING GK3R3454: neural rendering: the network would not build (" +
            Ngx.Reason(result) + "); the picture is drawn without it.");

        return false;
    }

    /// <summary>The sizes and the ratio, which the network wants at build and at every frame.</summary>
    /// <remarks>
    /// Written twice on purpose. The names are not aliases of one another — the network reads
    /// some of them when it is built and others when it is run, and which is which is not
    /// documented anywhere — so both callers set all of them, which is what the plugin NVIDIA
    /// ships does too.
    /// </remarks>
    private void Describe(NgxParameters block)
    {
        block.Set("Width"u8, _render.Width);
        block.Set("Height"u8, _render.Height);
        block.Set("OutWidth"u8, _display.Width);
        block.Set("OutHeight"u8, _display.Height);

        block.Set("DLSSNR.InputWidth"u8, _render.Width);
        block.Set("DLSSNR.InputHeight"u8, _render.Height);
        block.Set("DLSSNR.OutputWidth"u8, _display.Width);
        block.Set("DLSSNR.OutputHeight"u8, _display.Height);
        block.Set("DLSSNR.Output.Width"u8, _display.Width);
        block.Set("DLSSNR.Output.Height"u8, _display.Height);

        // The network's own width and height are the *output* size, not the input one. That
        // asymmetry is the plugin's, not a slip: the two names that read like the input are
        // the pair above.
        block.Set("DLSSNR.Width"u8, _display.Width);
        block.Set("DLSSNR.Height"u8, _display.Height);

        bool scaling = _render != _display;

        block.Set("DLSSNR.Upscaling"u8, scaling ? 1 : 0);
        block.Set(
            "DLSSNRComputeScalingRatioCallback"u8,
            (void*)(delegate* unmanaged[Cdecl]<void*, uint>)&Ratio);

        float ratio = _display.Width > 0 ? (float)_render.Width / _display.Width : 1f;

        block.Set("DLSSNR.ScalingRatio"u8, ratio);
        block.Set("DLSSNR.Scale"u8, ratio);
    }

    /// <summary>This frame's four textures, and the whole of each that is to be used.</summary>
    /// <remarks>
    /// Every sub-rectangle is the whole texture. They are set rather than left out because
    /// the network reads them unconditionally, and what is in a block it was handed last
    /// frame is not something to rely on.
    /// </remarks>
    private static void Textures(
        NgxParameters block,
        D3D12Texture colour,
        D3D12Texture depth,
        D3D12Texture motion,
        D3D12Texture output)
    {
        block.SetResource(Colour, (nint)colour.Handle);
        block.SetResource("DLSSNR.Depth"u8, (nint)depth.Handle);
        block.SetResource("DLSSNR.MVec"u8, (nint)motion.Handle);
        block.SetResource(Output, (nint)output.Handle);

        block.Set("DLSSNR.ColorSubrectBaseX"u8, 0u);
        block.Set("DLSSNR.ColorSubrectBaseY"u8, 0u);
        block.Set("DLSSNR.ColorSubrectWidth"u8, (uint)colour.Width);
        block.Set("DLSSNR.ColorSubrectHeight"u8, (uint)colour.Height);

        block.Set("DLSSNR.DepthSubrectBaseX"u8, 0u);
        block.Set("DLSSNR.DepthSubrectBaseY"u8, 0u);
        block.Set("DLSSNR.DepthSubrectWidth"u8, (uint)depth.Width);
        block.Set("DLSSNR.DepthSubrectHeight"u8, (uint)depth.Height);

        block.Set("DLSSNR.MVecSubrectBaseX"u8, 0u);
        block.Set("DLSSNR.MVecSubrectBaseY"u8, 0u);
        block.Set("DLSSNR.MVecSubrectWidth"u8, (uint)motion.Width);
        block.Set("DLSSNR.MVecSubrectHeight"u8, (uint)motion.Height);

        block.Set("DLSSNR.OutputSubrectBaseX"u8, 0u);
        block.Set("DLSSNR.OutputSubrectBaseY"u8, 0u);
        block.Set("DLSSNR.OutputSubrectWidth"u8, (uint)output.Width);
        block.Set("DLSSNR.OutputSubrectHeight"u8, (uint)output.Height);
    }

    /// <summary>What the network is asked to do to the frame, and what to make of it.</summary>
    private void Strengths(NgxParameters block, in StreamlineFrame frame)
    {
        // The vectors are already in render-resolution pixels, which is the unit the network
        // works in, so there is nothing to convert. This is the same fact the Streamline path
        // states the other way round, where the scale turns pixels into a normalised space.
        block.Set("DLSSNR.MVecScaleX"u8, 1f);
        block.Set("DLSSNR.MVecScaleY"u8, 1f);

        // Nought is near and one is far, the ordinary way round, matching the camera.
        block.Set("DLSSNR.DepthInverted"u8, 0);

        block.Set("DLSSNR.Enabled"u8, 1);

        // A frame the history is worthless for: a cut, a new room, a resize — or the first
        // frame after the feature was built, which has no history at all.
        block.Set("DLSSNR.Reset"u8, frame.Reset || _fresh ? 1 : 0);

        block.Set("DLSSNR.Intensity"u8, _uplift.Intensity);
        block.Set("DLSSNR.LocalToneStrength"u8, _uplift.LocalTone);
        block.Set("DLSSNR.GlobalToneStrength"u8, _uplift.GlobalTone);
        block.Set("DLSSNR.LocalStructureStrength"u8, _uplift.LocalStructure);
        block.Set("DLSSNR.SkinStructureStrength"u8, _uplift.SkinStrength);

        block.Set("DLSSNR.UseAutoMask"u8, _uplift.AutoSkinMask ? 1 : 0);
        block.Set("DLSSNR.Style"u8, (uint)_uplift.Style);

        // The interface is drawn after the upscale, over the network's own output, so there
        // is nothing here for it to correct for.
        block.Set("DLSSNR.UICorrection"u8, 0);
    }
}

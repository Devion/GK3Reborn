// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Runtime.InteropServices;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>The Direct3D backend's half of a context description.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxCreateBackendD3D12
{
    public FfxHeader Header;
    public nint Device;
}

/// <summary>
/// AMD FidelityFX Super Resolution on Direct3D 12.
/// </summary>
public sealed unsafe class D3D12FsrUpscaler : IDisposable
{
    /// <summary>Structure identifiers, from <c>ffx_upscale.h</c> and <c>ffx_api_dx12.h</c>.</summary>
    private const ulong CreateUpscale = 0x00010000u;
    private const ulong DispatchUpscale = 0x00010001u;

    /// <summary>
    /// The Direct3D 12 backend, which is two where Vulkan is three.
    /// </summary>
    private const ulong CreateBackendDirect3D12 = 0x00000002u;

    /// <summary>Context flags, from <c>FfxApiCreateContextUpscaleFlags</c>.</summary>
    private const uint HighDynamicRange = 1 << 0;
    private const uint AutoExposure = 1 << 5;

    /// <summary>Resource states, from <c>FfxApiResourceState</c>.</summary>
    private const uint StateComputeRead = 1 << 2;
    private const uint StateUnorderedAccess = 1 << 1;

    /// <summary>Resource usages, from <c>FfxApiResourceUsage</c>.</summary>
    private const uint UsageReadOnly = 0;
    private const uint UsageUnorderedAccess = 1 << 1;
    private const uint UsageDepthTarget = 1 << 2;

    /// <summary>Two-dimensional texture, from <c>FfxApiResourceType</c>.</summary>
    private const uint TextureTwoDimensional = 2;

    private readonly FfxApi _api;
    private readonly (uint Width, uint Height) _render;
    private readonly (uint Width, uint Height) _display;
    private readonly bool _highDynamicRange;

    private nint _context;
    private bool _disposed;

    private D3D12FsrUpscaler(
        FfxApi api,
        nint context,
        (uint Width, uint Height) render,
        (uint Width, uint Height) display,
        bool highDynamicRange)
    {
        _api = api;
        _context = context;
        _render = render;
        _display = display;
        _highDynamicRange = highDynamicRange;
    }

    /// <summary>Which upscaler this is.</summary>
    public static UpscalerKind Kind => UpscalerKind.Fsr;

    /// <summary>What it is doing, for the startup report.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{System.IO.Path.GetFileName(_api.Path)}, " +
        $"{_render.Width}x{_render.Height} to {_display.Width}x{_display.Height}");

    /// <summary>Opens the runtime and makes a context, or returns null.</summary>
    /// <param name="context">The device.</param>
    /// <param name="runtimes">Where the player's runtimes were found.</param>
    /// <param name="plan">What was asked for.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <returns>The upscaler, or null when the runtime is absent or refused.</returns>
    public static D3D12FsrUpscaler? TryCreate(
        D3D12Context context,
        UpscalerRuntimes? runtimes,
        UpscalePlan plan,
        (uint Width, uint Height) render,
        (uint Width, uint Height) display)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);

        if (runtimes?.Locate(UpscalerRuntimes.FidelityFxDirect3D12) is not { } path)
        {
            return null;
        }

        FfxApi? api = FfxApi.TryOpen(path);

        if (api is null)
        {
            return null;
        }

        var backend = new FfxCreateBackendD3D12
        {
            Header = new FfxHeader { Type = CreateBackendDirect3D12 },
            Device = (nint)context.Device,
        };

        // Automatic exposure always: there is no exposure texture to hand over, and an
        // upscaler that is not told how bright the frame is will otherwise assume one.
        uint flags = AutoExposure;

        if (plan.HighDynamicRange)
        {
            flags |= HighDynamicRange;
        }

        var description = new FfxCreateUpscale
        {
            Header = new FfxHeader { Type = CreateUpscale, Next = &backend },
            Flags = flags,
            MaxRenderWidth = render.Width,
            MaxRenderHeight = render.Height,
            MaxUpscaleWidth = display.Width,
            MaxUpscaleHeight = display.Height,
        };

        uint result = api.CreateContext(out nint handle, &description);

        if (result != 0 || handle == 0)
        {
            Log.Warning(
                "WARNING GK3R3434: FidelityFX refused to make an upscaling context "
                + $"(code {result}).");

            api.Dispose();
            return null;
        }

        return new D3D12FsrUpscaler(api, handle, render, display, plan.HighDynamicRange);
    }

    /// <summary>Whether this context is the one the plan and the sizes want.</summary>
    /// <param name="plan">What is being asked for now.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <returns>True when nothing has to be rebuilt.</returns>
    public bool Serves(
        UpscalePlan plan, (uint Width, uint Height) render, (uint Width, uint Height) display) =>
        plan.Kind == UpscalerKind.Fsr &&
        plan.HighDynamicRange == _highDynamicRange &&
        render == _render &&
        display == _display;

    /// <summary>Runs the upscaler over one frame.</summary>
    /// <param name="list">The frame's command list.</param>
    /// <param name="colour">The room as drawn, in linear light.</param>
    /// <param name="depth">Its depth.</param>
    /// <param name="motion">Its motion vectors.</param>
    /// <param name="output">Where to put the result.</param>
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

        if (_context == 0)
        {
            return false;
        }

        Camera? camera = frame.Camera;

        var dispatch = new FfxDispatchUpscale
        {
            Header = new FfxHeader { Type = DispatchUpscale },
            CommandList = list,

            Color = Describe(colour, StateComputeRead),
            Depth = Describe(depth, StateComputeRead),
            MotionVectors = Describe(motion, StateComputeRead),
            Output = Describe(output, StateUnorderedAccess),

            JitterX = frame.JitterPixels.X,
            JitterY = frame.JitterPixels.Y,

            // Already in render-resolution pixels and already pointing backwards, from where
            // a pixel is to where it was, which is the convention FidelityFX reads them in.
            MotionVectorScaleX = 1f,
            MotionVectorScaleY = 1f,

            RenderWidth = (uint)colour.Width,
            RenderHeight = (uint)colour.Height,
            UpscaleWidth = (uint)output.Width,
            UpscaleHeight = (uint)output.Height,

            EnableSharpening = (byte)(frame.Sharpen ? 1 : 0),
            Sharpness = Math.Clamp(frame.Sharpness, 0f, 1f),

            // Milliseconds, which is what the runtime documents and not what every other
            // clock in this renderer is in.
            FrameTimeDelta = frame.DeltaSeconds * 1000f,
            PreExposure = 1f,
            Reset = (byte)(frame.Reset ? 1 : 0),

            CameraNear = camera?.NearPlane ?? 0.1f,
            CameraFar = camera?.FarPlane ?? 10_000f,
            CameraFovAngleVertical = camera?.FieldOfView ?? (MathF.PI / 3f),

            // GK3's world is in its own units — a room is a few hundred across — and the one
            // place the runtime uses this is a heuristic about how fast things plausibly
            // move. Roughly forty units to the metre, from the walk boundaries: Gabriel is
            // about seventy-four units tall.
            ViewSpaceToMetersFactor = 1f / 40f,
        };

        uint result = _api.Dispatch(_context, &dispatch);

        if (result == 0)
        {
            return true;
        }

        Log.Warning($"WARNING GK3R3435: FidelityFX declined a frame (code {result}).");
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
        _api.DestroyContext(ref _context);
        _api.Dispose();
    }

    /// <summary>Describes one of the frame's textures to the runtime.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="state">The state it is in, in the runtime's own vocabulary.</param>
    /// <returns>The description.</returns>
    private static FfxResource Describe(D3D12Texture texture, uint state)
    {
        ResourceDesc description = texture.Handle->GetDesc();

        uint usage = UsageReadOnly;

        if ((description.Flags & ResourceFlags.AllowUnorderedAccess) != 0)
        {
            usage |= UsageUnorderedAccess;
        }

        if ((description.Flags & ResourceFlags.AllowDepthStencil) != 0)
        {
            usage |= UsageDepthTarget;
        }

        return new FfxResource
        {
            Resource = texture.Handle,
            State = state,
            Description = new FfxResourceDescription
            {
                Type = TextureTwoDimensional,
                Format = SurfaceFormat(texture.Sampling),
                Width = (uint)texture.Width,
                Height = (uint)texture.Height,
                Depth = 1,
                MipCount = 1,
                Flags = 0,
                Usage = usage,
            },
        };
    }

    /// <summary>A DXGI format, as FidelityFX numbers it.</summary>
    private static uint SurfaceFormat(Format format) => format switch
    {
        Format.FormatR16G16B16A16Float => 4,
        Format.FormatR16G16Float => 18,
        Format.FormatD32Float or Format.FormatR32Float => 28,
        Format.FormatR32G32B32A32Float => 3,
        Format.FormatR11G11B10Float => 16,
        Format.FormatR10G10B10A2Unorm => 17,
        _ => 0,
    };
}

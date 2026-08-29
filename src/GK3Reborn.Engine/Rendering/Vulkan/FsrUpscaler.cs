// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>The header every FidelityFX description structure begins with.</summary>
/// <remarks>
/// A number saying what the structure is and a pointer to the next one. It is how one call
/// carries several unrelated descriptions — the effect's own and the backend's — and how a
/// runtime newer than the header a caller compiled against can ignore what it does not
/// recognise instead of misreading it.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxHeader
{
    public ulong Type;
    public void* Next;
}

/// <summary>How a resource is described to the runtime.</summary>
/// <remarks>
/// Mirrors <c>FfxApiResourceDescription</c> field for field. The three unions in the C
/// declaration are all the same width, so they are named for the texture case here — which
/// is the only case anything in this game passes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct FfxResourceDescription
{
    public uint Type;
    public uint Format;
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint MipCount;
    public uint Flags;
    public uint Usage;
}

/// <summary>A resource, as the runtime takes one.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxResource
{
    public void* Resource;
    public FfxResourceDescription Description;
    public uint State;
}

/// <summary>The Vulkan backend's half of a context description.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxCreateBackendVk
{
    public FfxHeader Header;
    public nint Device;
    public nint PhysicalDevice;
    public nint GetDeviceProcAddr;
}

/// <summary>The upscaler's half of a context description.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxCreateUpscale
{
    public FfxHeader Header;
    public uint Flags;
    public uint MaxRenderWidth;
    public uint MaxRenderHeight;
    public uint MaxUpscaleWidth;
    public uint MaxUpscaleHeight;
    public nint Message;
}

/// <summary>One frame of work for the upscaler.</summary>
/// <remarks>
/// Laid out to match <c>ffxDispatchDescUpscale</c> exactly, padding included. The two
/// <c>bool</c>s in the C declaration are one byte each and are followed by floats, so the
/// three bytes of padding after each are written out rather than left to the compiler:
/// a structure that differs from the runtime's by a byte does not fail, it silently reads
/// the sharpness out of the frame time.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxDispatchUpscale
{
    public FfxHeader Header;
    public void* CommandList;

    public FfxResource Color;
    public FfxResource Depth;
    public FfxResource MotionVectors;
    public FfxResource Exposure;
    public FfxResource Reactive;
    public FfxResource TransparencyAndComposition;
    public FfxResource Output;

    public float JitterX;
    public float JitterY;
    public float MotionVectorScaleX;
    public float MotionVectorScaleY;

    public uint RenderWidth;
    public uint RenderHeight;
    public uint UpscaleWidth;
    public uint UpscaleHeight;

    public byte EnableSharpening;
    public byte SharpeningPad0;
    public byte SharpeningPad1;
    public byte SharpeningPad2;

    public float Sharpness;
    public float FrameTimeDelta;
    public float PreExposure;

    public byte Reset;
    public byte ResetPad0;
    public byte ResetPad1;
    public byte ResetPad2;

    public float CameraNear;
    public float CameraFar;
    public float CameraFovAngleVertical;
    public float ViewSpaceToMetersFactor;
    public uint Flags;
}

/// <summary>
/// AMD FidelityFX Super Resolution, driven through the runtime's own C interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which version.</b> Whatever <c>amd_fidelityfx_vk.dll</c> the player installed
/// provides. Nothing here names a version: the interface this talks to is the FidelityFX
/// API, which was introduced so that an application would not have to be rebuilt for a new
/// upscaler, and the newest Vulkan build AMD ships through it is FSR 3.1. Dropping in a
/// newer one is the whole point of the arrangement.
/// </para>
/// <para>
/// <b>What it is given.</b> The room in linear light, its depth, and motion vectors in
/// render-resolution pixels with this frame's jitter already removed — which is why
/// <c>MOTION_VECTORS_JITTER_CANCELLATION</c> is not set: the cancellation has already
/// happened, in the fragment shader, where the offset was known exactly.
/// </para>
/// <para>
/// <b>What it is not given.</b> No exposure texture, so automatic exposure is asked for;
/// no reactive mask and no transparency mask. Those two are how a game tells the upscaler
/// which pixels have no usable history — particles, alpha-blended smoke, an animated
/// texture. GK3 has very little of any of that, and an absent mask is a correct input
/// meaning "nothing here is reactive" rather than a missing one.
/// </para>
/// <para>
/// This project is GPL-3.0. Loading a separately-installed proprietary upscaler at runtime
/// is a deliberate exception, taken because the alternative is a worse picture for every
/// player who has the hardware for a better one; see <c>NOTICE</c>. Nothing of AMD's is
/// redistributed here and the game runs without it.
/// </para>
/// </remarks>
internal sealed unsafe class FsrUpscaler : IUpscaler
{
    /// <summary>Structure identifiers, from <c>ffx_upscale.h</c> and <c>ffx_api_vk.h</c>.</summary>
    private const ulong CreateUpscale = 0x00010000u;
    private const ulong DispatchUpscale = 0x00010001u;
    private const ulong CreateBackendVulkan = 0x00000003u;

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
    private readonly Extent2D _render;
    private readonly Extent2D _display;
    private readonly bool _highDynamicRange;

    private nint _context;

    private FsrUpscaler(
        FfxApi api, nint context, Extent2D render, Extent2D display, bool highDynamicRange)
    {
        _api = api;
        _context = context;
        _render = render;
        _display = display;
        _highDynamicRange = highDynamicRange;
    }

    /// <inheritdoc/>
    public UpscalerKind Kind => UpscalerKind.Fsr;

    /// <inheritdoc/>
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
    public static FsrUpscaler? TryCreate(
        VulkanContext context,
        UpscalerRuntimes? runtimes,
        UpscalePlan plan,
        Extent2D render,
        Extent2D display)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);

        if (runtimes?.Locate(UpscalerRuntimes.FidelityFx) is not { } path)
        {
            return null;
        }

        FfxApi? api = FfxApi.TryOpen(path);

        if (api is null)
        {
            return null;
        }

        nint procAddress = VulkanExport("vkGetDeviceProcAddr");

        if (procAddress == 0)
        {
            api.Dispose();
            return null;
        }

        var backend = new FfxCreateBackendVk
        {
            Header = new FfxHeader { Type = CreateBackendVulkan },
            Device = (nint)context.Device.Handle,
            PhysicalDevice = (nint)context.PhysicalDevice.Handle,
            GetDeviceProcAddr = procAddress,
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

        return new FsrUpscaler(api, handle, render, display, plan.HighDynamicRange);
    }

    /// <inheritdoc/>
    public bool Serves(UpscalePlan plan, Extent2D render, Extent2D display) =>
        plan.Kind == UpscalerKind.Fsr &&
        plan.HighDynamicRange == _highDynamicRange &&
        render.Width == _render.Width && render.Height == _render.Height &&
        display.Width == _display.Width && display.Height == _display.Height;

    /// <inheritdoc/>
    public bool Record(CommandBuffer command, in UpscaleFrame frame)
    {
        if (_context == 0)
        {
            return false;
        }

        Camera? camera = frame.Camera;

        var dispatch = new FfxDispatchUpscale
        {
            Header = new FfxHeader { Type = DispatchUpscale },
            CommandList = (void*)command.Handle,

            Color = Describe(frame.Colour, StateComputeRead),
            Depth = Describe(frame.Depth, StateComputeRead),
            MotionVectors = Describe(frame.Motion, StateComputeRead),
            Output = Describe(frame.Output, StateUnorderedAccess),

            JitterX = frame.JitterPixels.X,
            JitterY = frame.JitterPixels.Y,

            // The vectors are already in render-resolution pixels and already point
            // backwards, from where a pixel is to where it was, which is the convention
            // FidelityFX reads them in. There is nothing to convert.
            MotionVectorScaleX = 1f,
            MotionVectorScaleY = 1f,

            RenderWidth = frame.Colour.Extent.Width,
            RenderHeight = frame.Colour.Extent.Height,
            UpscaleWidth = frame.Output.Extent.Width,
            UpscaleHeight = frame.Output.Extent.Height,

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

            // GK3's world is in its own units — a room is a few hundred across — and the
            // one place the runtime uses this is a heuristic about how fast things plausibly
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
        _api.DestroyContext(ref _context);
        _api.Dispose();
    }

    /// <summary>Describes one of the frame's images to the runtime.</summary>
    /// <param name="image">The image.</param>
    /// <param name="state">The layout it is in, in the runtime's own vocabulary.</param>
    /// <remarks>
    /// Follows <c>ffxApiGetImageResourceDescriptionVK</c>: the usage flags are derived from
    /// how the image was created rather than from what it is being used for here, because
    /// that is what the runtime uses to decide whether it may write into it.
    /// </remarks>
    private static FfxResource Describe(UpscaleImage image, uint state)
    {
        if (!image.Exists)
        {
            return default;
        }

        uint usage = UsageReadOnly;

        if (image.Usage.HasFlag(ImageUsageFlags.StorageBit))
        {
            usage |= UsageUnorderedAccess;
        }

        if (image.Usage.HasFlag(ImageUsageFlags.DepthStencilAttachmentBit))
        {
            usage |= UsageDepthTarget;
        }

        return new FfxResource
        {
            Resource = (void*)image.Image.Handle,
            State = state,
            Description = new FfxResourceDescription
            {
                Type = TextureTwoDimensional,
                Format = SurfaceFormat(image.Format),
                Width = image.Extent.Width,
                Height = image.Extent.Height,
                Depth = 1,
                MipCount = 1,
                Flags = 0,
                Usage = usage,
            },
        };
    }

    /// <summary>Vulkan's format, as FidelityFX numbers it.</summary>
    /// <remarks>
    /// Only the four this renderer actually hands over. Anything else comes back as
    /// unknown, which the runtime treats as an error rather than guessing — the right
    /// outcome, since a format guessed wrong is a picture of noise.
    /// </remarks>
    private static uint SurfaceFormat(Format format) => format switch
    {
        Format.R16G16B16A16Sfloat => 4,
        Format.R16G16Sfloat => 18,
        Format.D32Sfloat or Format.R32Sfloat => 28,
        Format.R32G32B32A32Sfloat => 3,
        Format.B10G11R11UfloatPack32 => 16,
        Format.A2B10G10R10UnormPack32 => 17,
        _ => 0,
    };

    /// <summary>Finds an entry point in whichever Vulkan loader this process has.</summary>
    /// <param name="name">The function's name.</param>
    /// <returns>Its address, or nought.</returns>
    /// <remarks>
    /// Loading by name returns the module already in the process rather than a second copy,
    /// so this is the same loader the renderer's own calls go through. Asked for by name
    /// rather than taken from the binding library, because a pointer is what the runtime
    /// wants and the binding hands back a managed wrapper around one.
    /// </remarks>
    private static nint VulkanExport(string name)
    {
        foreach (string library in (string[])["vulkan-1", "libvulkan.so.1", "libvulkan.1.dylib"])
        {
            if (NativeLibrary.TryLoad(library, out nint handle) &&
                NativeLibrary.TryGetExport(handle, name, out nint address))
            {
                return address;
            }
        }

        return 0;
    }
}

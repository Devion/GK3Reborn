// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Upscaling;

// The structures FidelityFX takes that say nothing about which graphics API is underneath.
// Here rather than beside one backend because they are the same on both: the runtime's C
// interface is one set of calls, and all that differs between Vulkan and Direct3D is the
// backend description chained onto a context and what a resource handle points at. Each
// backend declares its own of those two beside itself.

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

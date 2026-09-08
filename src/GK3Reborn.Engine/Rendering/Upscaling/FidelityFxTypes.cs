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
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxHeader
{
    public ulong Type;
    public void* Next;
}

/// <summary>How a resource is described to the runtime.</summary>
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

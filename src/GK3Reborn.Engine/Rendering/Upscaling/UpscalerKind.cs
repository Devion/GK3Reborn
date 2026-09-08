// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>Which upscaler the player has asked for.</summary>
public enum UpscalerKind
{
    /// <summary>The room is drawn at the size of the window and nothing is upscaled.</summary>
    Off,

    /// <summary>
    /// The engine's own: one frame in, edge-directed, with a sharpening pass after it.
    /// </summary>
    Spatial,

    /// <summary>AMD FidelityFX Super Resolution, through <c>amd_fidelityfx_vk.dll</c>.</summary>
    Fsr,

    /// <summary>NVIDIA DLSS, through Streamline and <c>nvngx_dlss.dll</c>.</summary>
    Dlss,
}

/// <summary>How much of the picture is actually drawn.</summary>
public enum UpscalerQuality
{
    /// <summary>
    /// Everything, at the size of the window.
    /// </summary>
    Native,

    /// <summary>1.3x per dimension: a little under two thirds of the pixels.</summary>
    UltraQuality,

    /// <summary>1.5x per dimension: four ninths of the pixels.</summary>
    Quality,

    /// <summary>1.7x per dimension.</summary>
    Balanced,

    /// <summary>2.0x per dimension: a quarter of the pixels.</summary>
    Performance,

    /// <summary>3.0x per dimension: a ninth of the pixels.</summary>
    UltraPerformance,
}

/// <summary>Who made the graphics card.</summary>
public static class GpuVendors
{
    /// <summary>Which vendor a PCI identifier belongs to.</summary>
    /// <param name="id">The identifier the adapter reports.</param>
    /// <returns>The vendor.</returns>
    public static GpuVendor Of(uint id) => id switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        0x106B => GpuVendor.Apple,
        _ => GpuVendor.Unknown,
    };
}

/// <summary>Who made the adapter.</summary>
public enum GpuVendor
{
    /// <summary>Something not on the list.</summary>
    Unknown,

    /// <summary>NVIDIA. The only one DLSS runs on.</summary>
    Nvidia,

    /// <summary>AMD.</summary>
    Amd,

    /// <summary>Intel.</summary>
    Intel,

    /// <summary>Apple silicon.</summary>
    Apple,
}

/// <summary>Whether frames are interpolated between the ones the game draws.</summary>
public enum FrameGeneration
{
    /// <summary>Every frame shown is a frame the game drew.</summary>
    Off,

    /// <summary>One generated frame between each pair of drawn ones: twice the frames.</summary>
    Interpolated,

    /// <summary>Two generated frames between each pair of drawn ones: three times.</summary>
    Triple,

    /// <summary>Three generated frames between each pair of drawn ones: four times.</summary>
    Quadruple,
}

/// <summary>How many frames each setting asks for, and what to call it.</summary>
public static class FrameGenerations
{
    /// <summary>Every setting, in order, for a menu to step through.</summary>
    public static IReadOnlyList<FrameGeneration> All { get; } =
    [
        FrameGeneration.Off,
        FrameGeneration.Interpolated,
        FrameGeneration.Triple,
        FrameGeneration.Quadruple,
    ];

    /// <summary>How many frames to generate for each one drawn.</summary>
    /// <param name="generation">The setting.</param>
    /// <returns>Nought for off, then one, two or three.</returns>
    public static int Generated(this FrameGeneration generation) => generation switch
    {
        FrameGeneration.Interpolated => 1,
        FrameGeneration.Triple => 2,
        FrameGeneration.Quadruple => 3,
        _ => 0,
    };

    /// <summary>What to show for a setting.</summary>
    /// <param name="generation">The setting.</param>
    /// <returns>Its name.</returns>
    public static string Describe(this FrameGeneration generation) => generation switch
    {
        FrameGeneration.Interpolated => "2x",
        FrameGeneration.Triple => "3x",
        FrameGeneration.Quadruple => "4x",
        _ => "Off",
    };

    /// <summary>The most this hardware will do, as a setting.</summary>
    /// <param name="generated">How many frames the runtime says it will generate.</param>
    /// <returns>The highest setting that asks for no more than that.</returns>
    public static FrameGeneration Most(int generated) => generated switch
    {
        <= 0 => FrameGeneration.Off,
        1 => FrameGeneration.Interpolated,
        2 => FrameGeneration.Triple,
        _ => FrameGeneration.Quadruple,
    };
}

/// <summary>How hard to work at keeping the frame the display is waiting for close behind.</summary>
public enum LatencyMode
{
    /// <summary>Frames are queued as deep as the driver likes.</summary>
    Off,

    /// <summary>The queue is kept short.</summary>
    On,

    /// <summary>
    /// The queue is kept short and the card is held at a clock that keeps it short.
    /// </summary>
    Boost,
}

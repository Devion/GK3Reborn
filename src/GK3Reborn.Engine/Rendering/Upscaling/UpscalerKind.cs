// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>Which upscaler the player has asked for.</summary>
/// <remarks>
/// <para>
/// Three of the four need nothing installed. <see cref="Fsr"/> and <see cref="Dlss"/> are
/// the vendors' own, and neither ships here: their runtimes are redistributables with
/// their own licences, and the player puts them in <c>libs/</c> themselves. See
/// <see cref="UpscalerRuntimes"/> for what is looked for and where.
/// </para>
/// <para>
/// A kind whose runtime is absent is not an error and is not hidden. The settings page
/// draws the row, says what is missing, and the renderer falls back to
/// <see cref="Spatial"/> — which is always there — so a settings file copied from a machine
/// that had the DLLs onto one that does not still starts, and still upscales.
/// </para>
/// </remarks>
public enum UpscalerKind
{
    /// <summary>The room is drawn at the size of the window and nothing is upscaled.</summary>
    Off,

    /// <summary>
    /// The engine's own: one frame in, edge-directed, with a sharpening pass after it.
    /// </summary>
    /// <remarks>
    /// No history, so nothing it produces can ghost, smear or shimmer over time — and
    /// nothing it produces is better than the frame it was given. It is the honest floor:
    /// it works on every device the renderer runs on, it needs no download, and it is what
    /// the other two fall back to.
    /// </remarks>
    Spatial,

    /// <summary>AMD FidelityFX Super Resolution, through <c>amd_fidelityfx_vk.dll</c>.</summary>
    /// <remarks>
    /// Vendor-neutral in fact as well as in name: FSR is compute, runs on any Vulkan
    /// device, and is the upscaler an NVIDIA card gets when the player has not installed
    /// NVIDIA's.
    /// </remarks>
    Fsr,

    /// <summary>NVIDIA DLSS, through Streamline and <c>nvngx_dlss.dll</c>.</summary>
    Dlss,
}

/// <summary>How much of the picture is actually drawn.</summary>
/// <remarks>
/// <para>
/// The names are the industry's rather than this project's, and deliberately so: a player
/// who has set "Balanced" in another game knows what they are asking for here, and
/// inventing a private vocabulary for a ratio everybody already has a word for helps
/// nobody.
/// </para>
/// <para>
/// The ratio is per dimension, so Performance at 2.0 draws a quarter of the pixels.
/// </para>
/// </remarks>
public enum UpscalerQuality
{
    /// <summary>
    /// Everything, at the size of the window.
    /// </summary>
    /// <remarks>
    /// Not a no-op for a temporal upscaler: FSR and DLSS both accept a ratio of one and
    /// spend their whole budget on anti-aliasing instead, which is what DLAA is. For the
    /// spatial upscaler it genuinely is a no-op, and the sharpening pass is all that runs.
    /// </remarks>
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
/// <remarks>
/// Only ever asked one question — whether a vendor's own upscaler could conceivably run —
/// which is why it is four values and not a catalogue. From the PCI vendor identifier the
/// driver reports, because a device's *name* is a marketing string and matching on it is
/// how a card called "NVIDIA GeForce" in one driver and "NVidia Geforce" in the next stops
/// being recognised.
/// </remarks>
public static class GpuVendors
{
    /// <summary>Which vendor a PCI identifier belongs to.</summary>
    /// <param name="id">The identifier the adapter reports.</param>
    /// <returns>The vendor.</returns>
    /// <remarks>
    /// From the identifier rather than from the name. Which upscalers the settings page may
    /// offer hangs off this, and a card whose marketing string changes between driver
    /// releases must not change what the menu shows.
    /// </remarks>
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
/// <remarks>
/// Its own setting rather than a rung of <see cref="UpscalerQuality"/>, because it is a
/// different trade: upscaling buys frame rate at the cost of detail, and generation buys
/// smoothness at the cost of latency. Somebody may reasonably want either without the
/// other.
/// </remarks>
public enum FrameGeneration
{
    /// <summary>Every frame shown is a frame the game drew.</summary>
    Off,

    /// <summary>One generated frame between each pair of drawn ones: twice the frames.</summary>
    /// <remarks>
    /// Named for what it does rather than for its factor, and kept that way: settings are
    /// written out by name, so renaming this is every existing player's choice failing to
    /// read back. The three below say their factor because they never had another name.
    /// </remarks>
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
    /// <remarks>
    /// The runtime counts generated frames rather than multiples, so this is the translation
    /// and the only place it happens. Three generated frames is four times the frames shown,
    /// which is the number a player recognises and not the number the runtime wants.
    /// </remarks>
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
    /// <remarks>
    /// What the menu is trimmed to. A card that will generate one frame must not be offered
    /// four times: the runtime refuses the whole call rather than clamping it, so the player
    /// would step to a setting that silently turns generation off altogether.
    /// </remarks>
    public static FrameGeneration Most(int generated) => generated switch
    {
        <= 0 => FrameGeneration.Off,
        1 => FrameGeneration.Interpolated,
        2 => FrameGeneration.Triple,
        _ => FrameGeneration.Quadruple,
    };
}

/// <summary>How hard to work at keeping the frame the display is waiting for close behind.</summary>
/// <remarks>
/// <para>
/// Reflex, and its own setting because it is its own trade. It shortens the queue between
/// the frame this machine is building and the one the display is about to show, which costs
/// a little throughput and buys a game that answers the mouse sooner.
/// </para>
/// <para>
/// <b>Frame generation cannot run without it.</b> A generated frame is placed in time
/// between two drawn ones, and where in time that is comes from the same measurements Reflex
/// makes — so the two are not independent, however separate they look in a menu.
/// </para>
/// </remarks>
public enum LatencyMode
{
    /// <summary>Frames are queued as deep as the driver likes.</summary>
    Off,

    /// <summary>The queue is kept short.</summary>
    On,

    /// <summary>
    /// The queue is kept short and the card is held at a clock that keeps it short.
    /// </summary>
    /// <remarks>
    /// Worth less here than in a game that is limited by the processor: this one is not, most
    /// of the time, and what boost costs is power spent holding clocks up for a frame that
    /// was never waiting on them.
    /// </remarks>
    Boost,
}

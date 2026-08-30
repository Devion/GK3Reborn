// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// The points in a frame Reflex measures between.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are what Reflex is for.</b> The sleep is the only call that does anything to
/// latency, but it can only place itself where it knows how long each part of a frame takes,
/// and these are how it is told. A frame with no markers is a frame Reflex sleeps blindly
/// through, which is worse than not sleeping at all.
/// </para>
/// <para>
/// The numbers were read out of <c>sl.reflex.dll</c> on 2026-08-30 rather than copied from a
/// header. Its <c>slSetData</c> treats several of them specially, and the special cases are
/// what fix the whole list: nought records a timestamp for the frame, which is a simulation
/// starting; four sets the parameter the plugin calls <c>markerPresentFrame</c>, which is a
/// present starting; three does the same when the mode is the boosted one, which is a render
/// submission ending standing in for a present that has not happened yet; and seven and
/// eight are handed straight to the driver rather than being interpreted, which is what a
/// flash and a latency ping are.
/// </para>
/// <para>
/// They are sent in pairs and the pairs must not overlap. Reflex reads the distance between
/// a start and its end, so a start with no end is a measurement that never closes and an end
/// with no start is one that measures from the last frame.
/// </para>
/// </remarks>
public enum StreamlineMarker : uint
{
    /// <summary>The game has begun working out what this frame contains.</summary>
    SimulationStart = 0,

    /// <summary>It has finished, and the frame's contents are settled.</summary>
    SimulationEnd = 1,

    /// <summary>Recording of the frame's commands has begun.</summary>
    RenderSubmitStart = 2,

    /// <summary>The commands have all been given to the device.</summary>
    RenderSubmitEnd = 3,

    /// <summary>The present is about to be made.</summary>
    PresentStart = 4,

    /// <summary>It has returned.</summary>
    PresentEnd = 5,

    /// <summary>Input was read here. Optional, and the one marker that is not a pair.</summary>
    InputSample = 6,

    /// <summary>
    /// Flash the screen, for a camera measuring latency from outside the machine.
    /// </summary>
    /// <remarks>
    /// Handed to the driver rather than interpreted. Nothing in this engine sends it; it is
    /// here because the number belongs to it and a gap in an enum invites somebody to fill
    /// it with something else.
    /// </remarks>
    TriggerFlash = 7,

    /// <summary>The latency ping, which the overlay sends and this engine does not.</summary>
    LatencyPing = 8,
}

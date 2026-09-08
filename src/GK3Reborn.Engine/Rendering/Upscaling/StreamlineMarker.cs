// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// The points in a frame Reflex measures between.
/// </summary>
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
    TriggerFlash = 7,

    /// <summary>The latency ping, which the overlay sends and this engine does not.</summary>
    LatencyPing = 8,
}

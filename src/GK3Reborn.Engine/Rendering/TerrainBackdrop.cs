using System.Numerics;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>
/// A reconstructed horizon: real terrain standing where the painted skybox was.
/// </summary>
/// <remarks>
/// <para>
/// Built offline from the game's own sky paintings — each cube set stitched into a
/// panorama, its depth inferred, the ridges fitted as a heightfield, and the ground
/// classified into forest, rock, grass and dirt. What ships is geometry and blend
/// weights; the 1999 sky texels themselves are not part of it. The full pipeline and
/// the data contract are in <c>ContentWorkspace/enhanced/skyboxes/terrain-plan.md</c>.
/// </para>
/// <para>
/// Everything is in metres in the backdrop's own space: the box centre is the origin,
/// +Y is up, and the grid spans <see cref="ExtentMeters"/> out from the centre on X and
/// Z. The renderer never converts these to room units — the backdrop is drawn around
/// the camera in its own projection, the way the sky is, so the two spaces never meet.
/// </para>
/// </remarks>
public sealed record TerrainBackdrop
{
    /// <summary>Cells per side of the height grid.</summary>
    public required int Grid { get; init; }

    /// <summary>Half-width of the grid, in metres from the centre.</summary>
    public required float ExtentMeters { get; init; }

    /// <summary>Heights, row-major, <see cref="Grid"/> squared of them, metres, +Y up.</summary>
    /// <remarks>Row 0 is the -Z edge; the camera stands at height zero over the middle.</remarks>
    public required float[] Heights { get; init; }

    /// <summary>Blend weights per cell: forest, rock, grass, dirt in R, G, B, A.</summary>
    public required DecodedImage Splat { get; init; }

    /// <summary>Low-frequency colour per cell, from the vista the terrain replaces.</summary>
    /// <remarks>
    /// Applied hue-only — normalised by its own luminance — because carrying the old
    /// painting's darkness onto the modern tiles reads as dirt on the lens, not mood.
    /// </remarks>
    public required DecodedImage Tint { get; init; }

    /// <summary>The four tileable ground textures, in splat channel order.</summary>
    public required DecodedImage TileForest { get; init; }

    /// <summary>Rock, blended in wherever the surface is steep as well.</summary>
    public required DecodedImage TileRock { get; init; }

    /// <summary>Grass.</summary>
    public required DecodedImage TileGrass { get; init; }

    /// <summary>Dirt, also the fallback where nothing else claims a cell.</summary>
    public required DecodedImage TileDirt { get; init; }

    /// <summary>Direction the sun's light travels, or null for a sunless hour.</summary>
    public required Vector3? SunDirection { get; init; }

    /// <summary>How far the backdrop is turned, in radians — the sky's own azimuth.</summary>
    public required float Azimuth { get; init; }
}

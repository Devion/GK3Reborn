using System.Numerics;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>One corner of a modelled tree, in its own normalised frame.</summary>
/// <param name="Position">Where it is: base at the origin, one unit tall, +Y up.</param>
/// <param name="Normal">Which way the surface faces.</param>
/// <param name="TexCoord">Where it reads from its part's texture.</param>
public readonly record struct TerrainTreeVertex(
    Vector3 Position, Vector3 Normal, Vector2 TexCoord);

/// <summary>The triangles of one tree that share a texture.</summary>
/// <param name="Texture">Index into <see cref="TerrainBackdrop.TreeTextures"/>.</param>
/// <param name="FirstIndex">Where this part starts in its model's indices.</param>
/// <param name="IndexCount">How many indices it has.</param>
/// <param name="Leaves">
/// Whether it is foliage rather than bark, which decides whether it is drawn with the
/// alpha test and whether it takes the crown's light or the trunk's shade.
/// </param>
public readonly record struct TerrainTreePart(
    int Texture, uint FirstIndex, uint IndexCount, bool Leaves);

/// <summary>
/// One of the grown trees, at one level of detail, for the backdrop to draw.
/// </summary>
/// <remarks>
/// <para>
/// The same models the rooms plant — see <see cref="Content.TreeLibrary"/> — brought
/// across the seam into the backdrop's own space. A wood on a hillside four hundred
/// metres out is a field of impostors and always will be; a wood on the slope just
/// beyond the wall the player is leaning on is not, and drawing that one as cones is
/// what makes a reconstructed horizon read as scenery rather than as country.
/// </para>
/// <para>
/// Normalised: base at the origin and exactly one unit tall, so the placement's own
/// scale and the impostor's height for that kind are what size it. That is the same
/// frame the room's trees are fitted in, which is what lets one library serve both.
/// </para>
/// </remarks>
public sealed record TerrainTreeModel
{
    /// <summary>Which of the renderer's impostor shapes this stands in for.</summary>
    public required int Kind { get; init; }

    /// <summary>Nought for the full tree, one for the cheap one grown for a far hillside.</summary>
    public required int Detail { get; init; }

    /// <summary>What to call it in a report.</summary>
    public required string Name { get; init; }

    /// <summary>Its corners.</summary>
    public required TerrainTreeVertex[] Vertices { get; init; }

    /// <summary>Its triangles, three indices each, into <see cref="Vertices"/>.</summary>
    public required uint[] Indices { get; init; }

    /// <summary>Its parts, one per texture.</summary>
    public required IReadOnlyList<TerrainTreePart> Parts { get; init; }

    /// <summary>How many triangles it costs to draw one of these.</summary>
    public int Triangles => Indices.Length / 3;
}

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

    /// <summary>The room point the backdrop is centred on, in room units.</summary>
    /// <remarks>
    /// The scene's own centre. The camera's offset from it, scaled by the renderer's
    /// metres-per-unit, is what moves the camera through the backdrop — anchoring the
    /// horizon to the world instead of gluing it to the lens.
    /// </remarks>
    public required Vector3 AnchorUnits { get; init; }

    /// <summary>
    /// The forest: six floats per tree — x, y, z, scale, yaw, and which shape — in metres.
    /// </summary>
    /// <remarks>
    /// The shape is an index into the renderer's impostors: a spruce, a broadleaf, a
    /// cypress and scrub. It is the last of the six because it was the last to exist, and
    /// a set written before it says zero, which is the conifer every tree used to be.
    /// </remarks>
    public float[] Trees { get; init; } = [];

    /// <summary>
    /// The grown trees the nearest of that forest is drawn as, coarsest last.
    /// </summary>
    /// <remarks>
    /// Empty when the player has turned modelled trees off, or when the library that
    /// grows them is not installed — in which case the whole forest is impostors, which
    /// is what it was.
    /// </remarks>
    public IReadOnlyList<TerrainTreeModel> TreeModels { get; init; } = [];

    /// <summary>The bark and foliage those models are painted with.</summary>
    public IReadOnlyList<DecodedImage> TreeTextures { get; init; } = [];
}

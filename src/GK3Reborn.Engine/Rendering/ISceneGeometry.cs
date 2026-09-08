using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Materials;

namespace GK3Reborn.Rendering;

/// <summary>
/// A scene that has been put on a device, and what came of putting it there.
/// </summary>
public interface ISceneGeometry : ISceneSink, IDisposable
{
    /// <summary>Draw calls the scene resolves to.</summary>
    int BatchCount { get; }

    /// <summary>Textures a second request found already resident.</summary>
    int TexturesReused { get; }

    /// <summary>How much device memory those textures occupy.</summary>
    long TextureDeviceBytes { get; }

    /// <summary>Triangles in the acceleration structure.</summary>
    int TraceableTriangleCount { get; }

    /// <summary>Pieces the acceleration structure was built from.</summary>
    int TraceablePartCount { get; }

    /// <summary>How the floor is cut for relief, and whether it is cut at all.</summary>
    ReliefSettings Relief { get; set; }

    /// <summary>Triangles the relief pass added.</summary>
    int DisplacedTriangles { get; }

    /// <summary>The lattice step the relief pass cut on, in world units.</summary>
    float ReliefCell { get; }

    /// <summary>How far the relief pass moved a vertex at most.</summary>
    float ReliefDepth { get; }

    /// <summary>How far it moved one typically.</summary>
    float ReliefTypically { get; }

    /// <summary>Boundary vertices pinned in place, and boundary edges carried across.</summary>
    (int Pinned, int Continued) ReliefBoundary { get; }

    /// <summary>Surfaces the relief pass expected to find a height for.</summary>
    int ReliefExpected { get; }

    /// <summary>Surfaces it set aside because it could not.</summary>
    int ReliefSetApart { get; }

    /// <summary>How many times a rounded object is subdivided.</summary>
    int RoundLevels { get; set; }

    /// <summary>Objects the rounding pass smoothed.</summary>
    int RoundedObjects { get; }

    /// <summary>Triangles it added doing so.</summary>
    int RoundedTriangles { get; }

    /// <summary>The names of the objects it smoothed.</summary>
    IReadOnlyList<string> Rounded { get; }

    /// <summary>Whether a keyed card is given the thickness of the thing drawn on it.</summary>
    bool ThickenCutoutCards { get; set; }

    /// <summary>
    /// Whether the room's own surfaces are drawn only on the side their winding faces.
    /// </summary>
    bool CullBackFaces { get; set; }

    /// <summary>Whether a thickened card also casts a traced shadow.</summary>
    bool CardShadows { get; set; }

    /// <summary>Cards the thickening pass gave a shell.</summary>
    int CardsThickened { get; }

    /// <summary>Triangles those shells came to.</summary>
    int CardTriangles { get; }

    /// <summary>Triangles their shadows are traced against, which are not the same ones.</summary>
    int CardShadowTriangles { get; }

    /// <summary>The thinnest and thickest of them, in world units.</summary>
    (float Thinnest, float Thickest) CardThickness { get; }

    /// <summary>How a surface is shaded when nothing more specific applies.</summary>
    Materials.SurfaceFinishes Materials { get; set; }

    /// <summary>Where the time a cold load takes went, when anyone is measuring.</summary>
    LoadTimeline? Timeline { get; set; }

    /// <summary>Says that nothing more will be added, and builds what depends on that.</summary>
    void Finish();
}

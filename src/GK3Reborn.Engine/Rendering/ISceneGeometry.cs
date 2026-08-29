using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Materials;

namespace GK3Reborn.Rendering;

/// <summary>
/// A scene that has been put on a device, and what came of putting it there.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISceneSink"/> is the seam a scene is loaded *through*; this is the seam it is
/// held *behind*. The two are separate because they face opposite ways. Loading writes into
/// a sink and asks it nothing; the game, the launcher and the tools then ask the result a
/// great many questions — how many triangles, how much of the floor was displaced, which
/// objects were rounded and how far, what the texture cache did — and every one of those
/// answers is a fact about the scene rather than about the API holding it.
/// </para>
/// <para>
/// That is what makes this interface possible at all. A backend's geometry is buffers,
/// descriptor sets and an acceleration structure, none of which appears below; what appears
/// below is what <c>render-scene</c>, <c>check-scenes</c> and the load report print, and
/// those must read the same whichever backend built them or the corpus sweep stops being a
/// comparison. See <c>docs/scene-geometry.md</c>.
/// </para>
/// </remarks>
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

    /// <summary>How a surface is shaded when nothing more specific applies.</summary>
    Materials.SurfaceFinishes Materials { get; set; }

    /// <summary>Where the time a cold load takes went, when anyone is measuring.</summary>
    LoadTimeline? Timeline { get; set; }

    /// <summary>Says that nothing more will be added, and builds what depends on that.</summary>
    /// <remarks>
    /// The acceleration structure above all, which cannot be built while triangles are
    /// still arriving. Calling it twice is not an error; not calling it at all is a scene
    /// that draws and does not trace.
    /// </remarks>
    void Finish();
}

using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering.Geometry;

namespace GK3Reborn.Rendering;

/// <summary>
/// Draws a room into a picture, with no window anywhere.
/// </summary>
/// <remarks>
/// <para>
/// What <c>render-scene</c> and the reference renders talk to. It exists so that the same
/// room, the same camera and the same shaders can be put through either backend and the two
/// pictures put side by side — which is the only way to know that a second backend draws the
/// game rather than merely drawing.
/// </para>
/// <para>
/// Deliberately not <see cref="IRenderer"/>. That one presents to a window, keeps frames in
/// flight and owns a swapchain; this one draws once and hands back the pixels, and a tool
/// that wants a picture should not have to open a window to get one.
/// </para>
/// </remarks>
public interface IOffscreenRenderer : IDisposable
{
    /// <summary>Which API is behind this renderer.</summary>
    RenderBackend Backend { get; }

    /// <summary>Name of the device being used.</summary>
    string DeviceName { get; }

    /// <summary>Whether this device can trace at all.</summary>
    bool SupportsRayTracing { get; }

    /// <summary>How much tracing to do.</summary>
    RayTracingQuality Quality { get; set; }

    /// <summary>The wind's clock, which only the foliage reads.</summary>
    float Seconds { get; set; }

    /// <summary>How the room's lights are divided up, once it has been given some.</summary>
    SceneLightGrid? LightGrid { get; }

    /// <summary>Gives the room its smoke and embers.</summary>
    /// <param name="particles">The particles, furthest from the eye first.</param>
    /// <remarks>
    /// Empty unless a caller sets it, so a headless render draws a room whose fires are
    /// standing still — which is what two versions of this engine are compared with.
    /// </remarks>
    void SetParticles(IReadOnlyList<Particle> particles);

    /// <summary>Somewhere to put a scene, on this renderer's device.</summary>
    /// <returns>Empty geometry.</returns>
    SceneGeometry CreateGeometry();

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies.</param>
    void SetLights(IReadOnlyList<AuthoredLight> lights, SceneExtent scene = default);

    /// <summary>Draws a scene and returns the picture.</summary>
    /// <param name="geometry">What to draw, already finished.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <returns>The picture.</returns>
    DecodedImage Render(SceneGeometry geometry, int width, int height, Camera camera);
}

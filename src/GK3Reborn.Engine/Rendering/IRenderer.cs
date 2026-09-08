using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Upscaling;
using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// Somewhere frames are drawn and presented.
/// </summary>
public interface IRenderer : IDisposable
{
    // --- what the device is -------------------------------------------------------------

    /// <summary>Which API is behind this renderer.</summary>
    RenderBackend Backend { get; }

    /// <summary>The adapter's name, as the driver reports it.</summary>
    string DeviceName { get; }

    /// <summary>Who made the adapter.</summary>
    GpuVendor Vendor { get; }

    /// <summary>What the device in use can do.</summary>
    RenderCapabilityTier Tiers { get; }

    /// <summary>What this backend can see, for the startup report.</summary>
    /// <returns>What was found and what was chosen.</returns>
    DeviceReport Survey();

    // --- sizes --------------------------------------------------------------------------

    /// <summary>The size frames are presented at, which is the window's.</summary>
    (int Width, int Height) SwapchainSize { get; }

    /// <summary>How many buffers the swapchain has.</summary>
    int SwapchainImageCount { get; }

    /// <summary>
    /// The size the room is drawn at, which is <see cref="SwapchainSize"/> divided by
    /// whatever the upscaler was asked for.
    /// </summary>
    (int Width, int Height) RenderSize { get; }

    // --- what to draw -------------------------------------------------------------------

    /// <summary>Somewhere to put a scene, on this renderer's device.</summary>
    /// <returns>Empty geometry, ready to be loaded into.</returns>
    SceneGeometry CreateGeometry();

    /// <summary>Shows a scene, seen from a camera.</summary>
    /// <param name="scene">The scene, or null to show none.</param>
    /// <param name="camera">Where it is seen from, or null to leave the view alone.</param>
    void SetScene(SceneGeometry? scene, Camera? camera);

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies; default decides nothing.</param>
    void SetLights(IReadOnlyList<AuthoredLight> lights, SceneExtent scene = default);

    /// <summary>What the scene's lights came to on a grid, or null if there is no scene.</summary>
    SceneLightGrid? LightGrid { get; }

    /// <summary>Gives the room its smoke and embers.</summary>
    /// <param name="particles">The particles, furthest from the eye first.</param>
    void SetParticles(IReadOnlyList<Particle> particles);

    /// <summary>Gives the room its fog, or takes it away again.</summary>
    /// <param name="fog">The layer, or <see cref="FogVolume.None"/> for a room with none.</param>
    void SetFog(FogVolume fog);

    // --- how much of it -----------------------------------------------------------------

    /// <summary>Whether this renderer can trace rays at all.</summary>
    bool SupportsRayTracing { get; }

    /// <summary>How much tracing to do.</summary>
    RayTracingQuality Quality { get; set; }

    /// <summary>Where to look for the upscaler runtimes.</summary>
    UpscalerRuntimes? Runtimes { get; set; }

    /// <summary>Which upscalers this adapter can be asked for.</summary>
    IReadOnlyList<UpscalerKind> OfferedUpscalers { get; }

    /// <summary>What the upscaler was asked to do.</summary>
    UpscalePlan Upscaling { get; set; }

    /// <summary>What the display wants and how bright to drive it.</summary>
    OutputPlan Output { get; set; }

    /// <summary>How much of a reflection to show, and where the floors get theirs from.</summary>
    ReflectionPlan Reflections { get; set; }

    /// <summary>Whether to wait for the display before presenting.</summary>
    bool VerticalSync { get; set; }

    /// <summary>What the upscaler is actually doing, for the settings page.</summary>
    string UpscalerName { get; }

    /// <summary>Whether DLSS is available on this machine at all.</summary>
    bool DlssAvailable { get; }

    /// <summary>Whether the runtime offers ray reconstruction.</summary>
    bool DlssRayReconstruction { get; }

    /// <summary>Why ray reconstruction is or is not being used.</summary>
    string DlssRayReconstructionNote { get; }

    /// <summary>Whether the runtime offers frame generation.</summary>
    bool DlssFrameGeneration { get; }

    /// <summary>
    /// How many frames the runtime will generate for each drawn one, or nought for none.
    /// </summary>
    int FrameGenerationMaximum => 0;

    /// <summary>Whether latency can be controlled: Reflex, where there is one.</summary>
    bool LatencyControl => false;

    /// <summary>Whether the swapchain is really presenting high dynamic range.</summary>
    bool HighDynamicRangeActive { get; }

    // --- the frame ----------------------------------------------------------------------

    /// <summary>Draws and presents one frame, clearing to a colour.</summary>
    /// <param name="red">Clear red, 0 to 1.</param>
    /// <param name="green">Clear green, 0 to 1.</param>
    /// <param name="blue">Clear blue, 0 to 1.</param>
    /// <returns>False when the frame was skipped because the swapchain needed rebuilding.</returns>
    bool DrawFrame(float red, float green, float blue);

    /// <summary>
    /// Says that whatever a temporal pass remembers about the last frame is worthless.
    /// </summary>
    void ResetHistory();

    /// <summary>Says the swapchain is stale and must be rebuilt before the next frame.</summary>
    void Invalidate();

    /// <summary>Waits until the device has finished everything it was given.</summary>
    void Idle();

    /// <summary>Reads back the last presented frame.</summary>
    /// <returns>The picture, or null if nothing has been presented.</returns>
    DecodedImage? Capture();

    /// <summary>Reads back the motion vectors of the last frame.</summary>
    /// <returns>Two floats a pixel, row-major, or null if there are none.</returns>
    float[]? CaptureMotion();

    // --- what goes over it --------------------------------------------------------------

    /// <summary>Whether an interface is being drawn.</summary>
    bool HasOverlay { get; }

    /// <summary>How far the picture is faded out, from nought to one.</summary>
    float Fade { get; set; }

    /// <summary>What it is faded towards.</summary>
    Vector3 FadeColour { get; set; }

    /// <summary>Gives the interface its sheet of glyphs and pictures.</summary>
    /// <param name="atlas">The sheet.</param>
    void SetOverlayAtlas(OverlayAtlas atlas);

    /// <summary>Puts a screen's own picture on the device under a name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="image">The picture.</param>
    /// <returns>Its index in the overlay's picture list.</returns>
    int AddOverlayPicture(string name, DecodedImage image);

    /// <summary>Forgets a screen's picture.</summary>
    /// <param name="name">What it was called.</param>
    void DropOverlayPicture(string name);

    /// <summary>Finds a screen's picture by the name it was given.</summary>
    /// <param name="name">What it was called.</param>
    /// <returns>Its index, or a negative number if there is no such picture.</returns>
    int OverlayPicture(string name);

    /// <summary>Sets what the interface draws this frame.</summary>
    /// <param name="overlay">The display list, or null to draw none.</param>
    void SetOverlay(Overlay? overlay);

    /// <summary>Shows a frame of film over everything.</summary>
    /// <param name="frame">The frame, or null to stop showing one.</param>
    /// <param name="cover">Whether to fill the window rather than fit inside it.</param>
    void SetMovieFrame(DecodedImage? frame, bool cover = false);

    /// <summary>Shows a still picture behind everything.</summary>
    /// <param name="picture">The picture, or null to show none.</param>
    void SetBackdrop(DecodedImage? picture);

    /// <summary>Shows a still picture behind everything, without expanding its blocks.</summary>
    /// <param name="picture">The picture.</param>
    void SetBackdrop(CompressedImage picture);

    /// <summary>Where the film or backdrop is on screen, in the overlay's own pixels.</summary>
    /// <param name="width">Window width, as the overlay was begun with.</param>
    /// <param name="height">Window height.</param>
    /// <returns>Left, top, width and height, or all noughts when nothing is showing.</returns>
    Vector4 PictureRect(int width, int height);
}

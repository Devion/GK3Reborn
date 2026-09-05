using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Upscaling;
using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// Somewhere frames are drawn and presented.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the game and a graphics API. <see cref="ISceneSink"/> already separated
/// <em>loading</em> a scene from putting it on a device; this separates <em>running</em> one
/// from the device it runs on, which is what a second backend needs and the first one never
/// did. Until there was a second, <c>Application</c> named the Vulkan renderer outright and
/// there was nothing wrong with that.
/// </para>
/// <para>
/// Everything below is something the game asks for in its own terms — draw a frame, show
/// this film, fade to black, put this picture on the screen, tell me what the device is —
/// and nothing below mentions a buffer, a pipeline or a command list. That is the line: a
/// backend is free to hold a frame however it likes as long as the game never has to know,
/// and the layering tests keep <c>Game</c> from reaching past this to find out.
/// </para>
/// <para>
/// <b>Wider than it looks like it ought to be, and deliberately.</b> The settings page is
/// part of the game: it offers the upscalers this adapter actually has, says whether the
/// display is really running in high dynamic range, and reports what the runtime is doing.
/// A narrower interface would mean the settings page reaching around it to a concrete
/// renderer, which is the thing this exists to stop.
/// </para>
/// <para>
/// The two sizes are separate members rather than one and a multiplier, for the reason
/// spelled out where they are held: the room is drawn at <see cref="RenderSize"/> and
/// everything after the upscale — the encode onto the swapchain, the film, the interface,
/// the fade — is <see cref="SwapchainSize"/>. Drawing an interface at render resolution and
/// stretching it is the single most visible way to get this wrong.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// The renderer does not take ownership: the caller keeps the geometry alive for as long
    /// as it is set, and disposes it afterwards.
    /// </remarks>
    void SetScene(SceneGeometry? scene, Camera? camera);

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies; default decides nothing.</param>
    void SetLights(IReadOnlyList<AuthoredLight> lights, SceneExtent scene = default);

    /// <summary>What the scene's lights came to on a grid, or null if there is no scene.</summary>
    SceneLightGrid? LightGrid { get; }

    /// <summary>Gives the room its smoke and embers.</summary>
    /// <param name="particles">The particles, furthest from the eye first.</param>
    /// <remarks>
    /// Set every frame, because they move every frame. An empty list is the ordinary state
    /// of a room with no fire in it and records nothing at all. The order is the caller's:
    /// smoke is blended over what is behind it. See <see cref="Game.FlameParticles"/>.
    /// </remarks>
    void SetParticles(IReadOnlyList<Particle> particles);

    /// <summary>Gives the room its fog, or takes it away again.</summary>
    /// <param name="fog">The layer, or <see cref="FogVolume.None"/> for a room with none.</param>
    /// <remarks>
    /// Set when a room loads rather than every frame: a layer of fog is a fact about the
    /// room and not about the moment. What moves inside it — the drift of the density, a
    /// fire's light swinging through it — is the shader's own clock. See
    /// <see cref="Game.SceneFog"/> for which rooms have any.
    /// </remarks>
    void SetFog(FogVolume fog);

    // --- how much of it -----------------------------------------------------------------

    /// <summary>Whether this renderer can trace rays at all.</summary>
    bool SupportsRayTracing { get; }

    /// <summary>How much tracing to do.</summary>
    /// <remarks>
    /// Setting this on a renderer that cannot trace is not an error; it is a renderer that
    /// goes on drawing the raster picture, which is the whole game and looks right.
    /// </remarks>
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
    /// <remarks>
    /// Read at the top of a frame, like the other two plans, so that every row on the
    /// Picture page is something the player can watch happen rather than something that
    /// waits for the next door.
    /// </remarks>
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
    /// <remarks>
    /// Not the same question as <see cref="DlssFrameGeneration"/>, which asks whether the
    /// feature is there at all. This is what the card will actually do, and a menu that does
    /// not trim itself to it offers a factor the runtime refuses outright.
    /// </remarks>
    int FrameGenerationMaximum => 0;

    /// <summary>Whether latency can be controlled: Reflex, where there is one.</summary>
    bool LatencyControl => false;

    /// <summary>Whether the swapchain is really presenting high dynamic range.</summary>
    /// <remarks>
    /// What the surface gave back, not what was asked for. A settings page that reported the
    /// request would tell a player their display was in HDR when it was not.
    /// </remarks>
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
    /// <remarks>
    /// A cut, a room change or a teleport. Without it an upscaler spends a second smearing
    /// the old room over the new one, which reads as the game having stalled.
    /// </remarks>
    void ResetHistory();

    /// <summary>Says the swapchain is stale and must be rebuilt before the next frame.</summary>
    void Invalidate();

    /// <summary>Waits until the device has finished everything it was given.</summary>
    /// <remarks>
    /// Called before anything the device might still be reading is freed. Leaving a room
    /// with a frame in flight is the case that names itself.
    /// </remarks>
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
}

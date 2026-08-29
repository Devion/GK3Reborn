using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>
/// Somewhere frames are drawn and presented.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the game and a graphics API. <see cref="ISceneSink"/> already separated
/// *loading* a scene from putting it on a device; this separates *running* one from the
/// device it runs on, which is what a second backend needs and the first one never did.
/// Until there was a second, <c>Application</c> named the Vulkan renderer outright and
/// there was nothing wrong with that.
/// </para>
/// <para>
/// Deliberately a small interface over a large implementation. Everything below is
/// something the game asks for in its own terms — draw a frame, show this film, fade to
/// black, put this picture on the screen, tell me what the device is — and nothing below
/// mentions a buffer, a pipeline or a command list. That is the line: a backend is free to
/// hold a frame however it likes as long as the game never has to know, and the layering
/// tests keep <c>Game</c> from reaching past this to find out.
/// </para>
/// <para>
/// The two sizes are separate members rather than one and a multiplier, for the reason
/// spelled out where they are held: the room is drawn at
/// <see cref="RenderSize"/> and everything after the upscale — the encode onto the
/// swapchain, the film, the interface, the fade — is <see cref="OutputSize"/>. Drawing an
/// interface at render resolution and stretching it is the single most visible way to get
/// this wrong.
/// </para>
/// </remarks>
public interface IRenderer : IDisposable
{
    /// <summary>Which API is behind this renderer.</summary>
    RenderBackend Backend { get; }

    /// <summary>The size frames are presented at, which is the window's.</summary>
    (int Width, int Height) OutputSize { get; }

    /// <summary>
    /// The size the room is actually drawn at, which is <see cref="OutputSize"/> divided by
    /// whatever the upscaler was asked for.
    /// </summary>
    (int Width, int Height) RenderSize { get; }

    /// <summary>What the device in use can do.</summary>
    RenderCapabilityTier Capabilities { get; }

    /// <summary>Somewhere to put a scene, on this renderer's device.</summary>
    /// <returns>Empty geometry, ready to be loaded into.</returns>
    ISceneGeometry CreateGeometry();

    /// <summary>Shows a scene, seen from a camera.</summary>
    /// <param name="scene">The scene, or null to show none.</param>
    /// <param name="camera">Where it is seen from, or null to leave the view alone.</param>
    void SetScene(ISceneGeometry? scene, Camera? camera);

    /// <summary>Sets the lights a scene is lit by.</summary>
    /// <param name="rig">The lights.</param>
    /// <param name="ambient">The floor under them.</param>
    /// <param name="settings">How much tracing to do, and how.</param>
    void SetLights(
        Lighting.SceneLightRig? rig,
        System.Numerics.Vector3 ambient,
        RayTracingSettings settings);

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
    /// A cut, a room change or a teleport. Without it an upscaler spends a second of
    /// smearing the old room over the new one, which reads as the game having stalled.
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
    /// <returns>Two floats a pixel, or null if there are none.</returns>
    DecodedImage? CaptureMotionImage() => null;

    /// <summary>Reads back the motion vectors of the last frame.</summary>
    /// <returns>Two floats a pixel, row-major, or null if there are none.</returns>
    float[]? CaptureMotion();

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

    /// <summary>What the device is, for the startup report.</summary>
    /// <returns>What was found and what was chosen.</returns>
    DeviceReport Survey();
}

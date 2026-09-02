using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>Constants shared by every draw of a frame.</summary>
/// <param name="ViewProjection">World to clip space.</param>
/// <param name="PreviousViewProjection">
/// The same, as it was last frame. Half of what a motion vector is: where a point that is
/// here now would have been on the screen a frame ago.
/// </param>
/// <param name="LightDirection">Direction the fallback key light travels.</param>
/// <param name="CameraPosition">Where the eye is, in world space.</param>
/// <param name="Rays">
/// Shadowed light count, occlusion rays, rays per shadow, and how much the bake counts.
/// </param>
/// <param name="Tuning">
/// Occlusion radius, and three components nothing reads. The second used to be a frame
/// counter that seeded the sampling noise; it made the grain change every frame, which
/// with no temporal filter to average it is a pattern crawling across the picture.
/// </param>
/// <param name="GridOrigin">
/// The corner the light grid starts at, and how wide one of its cells is. See
/// <see cref="SceneLightGrid"/>.
/// </param>
/// <param name="GridCounts">
/// How many cells the grid has along each axis, and how many lights the rig holds in all.
/// </param>
/// <param name="Ambient">
/// The ambient floor in rgb, and in w how much the baked lightmaps shape it. It is tier
/// data rather than a constant because what it has to stand in for changes: where the baked
/// lightmaps still light the room it only keeps an unreached corner off black, and where
/// they are gone it is the whole of what the walls and floor bounce back.
/// </param>
/// <param name="Exposure">
/// This frame's jitter in pixels in xy, how much brighter a surface that carries its own
/// light is drawn in z, and nothing in w.
/// <para>
/// The jitter is here because the fragment stage has to take it back out of the motion
/// vectors. <c>gl_FragCoord</c> comes from the jittered projection and the previous clip
/// position comes from an unjittered one, so the difference between them is the movement
/// plus this frame's offset; adding the offset back leaves the movement.
/// </para>
/// <para>
/// The brightness is the HDR path's, and it is one in SDR. A bulb and a diffuse white wall
/// both come out of the shading at about one, which is the only answer an 8-bit target can
/// hold; on a display with somewhere above white to go, they should not be the same
/// brightness at all. See <see cref="OutputPlan"/>.
/// </para>
/// </param>
/// <param name="MirrorPlane">
/// The mirror this pass is reflecting about — <c>xyz</c> a unit normal out of the glass,
/// <c>w</c> the offset — and <b>zero in every pass that is not the reflection</b>.
/// <para>
/// It is the whole of what makes the reflection pass different from the ordinary one, and
/// it does two things. It clips: the reflected camera stands behind the mirror, so the wall
/// the mirror hangs on is between it and the room and would otherwise fill the reflection
/// with the inside of a wall. And it tells the mirror itself not to draw, because from
/// behind a mirror there is no mirror to see — which is also what stops the glass reading
/// an image of itself that does not exist yet.
/// </para>
/// <para>
/// Zero is not a plane. A normal of zero puts every point at distance <c>w</c> from it,
/// which is zero as well, so the test passes everywhere and the ordinary pass clips nothing
/// — no branch and no second shader.
/// </para>
/// </param>
/// <remarks>
/// One uniform buffer a frame, bound once and read by every pass. Neutral because both
/// backends want the same numbers in the same order: a Vulkan uniform buffer and a Direct3D
/// constant buffer differ in how they are bound and not at all in what they hold.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FrameUniforms(
    Matrix4x4 ViewProjection,
    Matrix4x4 PreviousViewProjection,
    Vector4 LightDirection,
    Vector4 CameraPosition,
    Vector4 Rays,
    Vector4 Tuning,
    Vector4 GridOrigin,
    Vector4 GridCounts,
    Vector4 Ambient,
    Vector4 Exposure,
    Vector4 MirrorPlane);

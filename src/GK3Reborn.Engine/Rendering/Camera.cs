using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// A view onto the scene.
/// </summary>
/// <remarks>
/// <para>
/// Left-handed, because GK3's world is. Its scenes were authored for Direct3D, where +X is
/// right, +Y is up and +Z is forward, and the reference implementation builds its view the
/// same way — see <c>RenderTransforms.h</c>, <c>VIEW_HAND VIEW_LH</c>, and the comment
/// there that negating the side axis is what would make the world appear right-handed.
/// Putting that world through a right-handed look-at renders every scene as its own mirror
/// image. It is nearly invisible — a mirrored room is still a plausible room — until
/// something in it carries writing, which is why it surfaced as the numbers on the hotel
/// doors reading backwards.
/// </para>
/// <para>
/// Uses a reversed-Y projection. Vulkan's clip space has Y pointing down, the opposite of
/// the convention <see cref="Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded"/> assumes,
/// so without the flip everything renders upside down — a mistake easy to misread as a
/// broken model rather than a broken matrix.
/// </para>
/// <para>
/// The framing helper exists because GK3's models are in their own units and sit wherever
/// the original artists left them: some are centred on the origin, others are placed at
/// their position within a scene. Deriving the view from the actual bounds means a model
/// is visible without anyone having to know which case it is.
/// </para>
/// </remarks>
public sealed class Camera
{
    /// <summary>Where the camera is.</summary>
    public Vector3 Position { get; init; } = new(0, 0, 5);

    /// <summary>What it looks at.</summary>
    public Vector3 Target { get; init; }

    /// <summary>Which way is up.</summary>
    public Vector3 Up { get; init; } = Vector3.UnitY;

    /// <summary>Vertical field of view, in radians.</summary>
    public float FieldOfView { get; init; } = MathF.PI / 3f;

    /// <summary>Near plane distance.</summary>
    public float NearPlane { get; init; } = 0.1f;

    /// <summary>Far plane distance.</summary>
    public float FarPlane { get; init; } = 10_000f;

    /// <summary>Direction the key light travels.</summary>
    public Vector3 LightDirection { get; init; } = Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.45f));

    /// <summary>Colour the target is cleared to.</summary>
    public Vector3 Background { get; init; } = new(0.08f, 0.09f, 0.12f);

    /// <summary>
    /// Where inside its pixel this frame samples, in clip space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero unless a temporal upscaler is running. It is what turns a sequence of frames
    /// into a denser sampling of one image: without it a still camera renders the same
    /// picture every frame and there is nothing for an accumulator to accumulate. See
    /// <see cref="Upscaling.JitterSequence"/>, which decides where.
    /// </para>
    /// <para>
    /// In clip units rather than pixels, because that is what goes into the matrix and
    /// because a camera does not know how big the target is. The conversion is
    /// <see cref="Upscaling.JitterSequence.ToClip"/>.
    /// </para>
    /// <para>
    /// The one settable property on an otherwise immutable camera, and set by the renderer
    /// rather than by whoever built it. Where the frame samples is a fact about this frame
    /// and this target, not about where the player is standing, and threading it through
    /// every place a camera is constructed — the free camera, the conversation camera, the
    /// scene's own angles — would put a presentation detail in all of them.
    /// </para>
    /// </remarks>
    public Vector2 Jitter { get; set; }

    /// <summary>The view matrix.</summary>
    public Matrix4x4 View => ViewOverride ?? Matrix4x4.CreateLookAtLeftHanded(Position, Target, Up);

    /// <summary>A view matrix given outright, rather than derived from the three points.</summary>
    /// <remarks>
    /// <para>
    /// Set by <see cref="Mirrored"/> and nothing else. <b>A mirrored camera cannot be
    /// described by an eye, a target and an up vector</b>, and believing that it can is a
    /// mistake that survives every plausibility check: reflecting the three and building an
    /// ordinary look-at from them puts the camera in the right place, pointing the right way,
    /// and produces the room seen in a mirror <em>and then flipped left to right again</em>.
    /// </para>
    /// <para>
    /// The reason is that a look-at always builds a basis of one handedness — it takes the
    /// cross product of two vectors it was given — while a reflection has a determinant of
    /// minus one and its view matrix must therefore have the opposite handedness from the
    /// camera it came from. The cross product quietly undoes exactly that, and the side axis
    /// comes out negated.
    /// </para>
    /// <para>
    /// What a reflection is, is the real view matrix with the reflection applied to the world
    /// before it: a point should land where its image lands for the camera that is really
    /// there. So that is what this holds.
    /// </para>
    /// </remarks>
    public Matrix4x4? ViewOverride { get; init; }

    /// <summary>This camera seen from the other side of a mirror.</summary>
    /// <param name="plane">
    /// The mirror's plane: <c>xyz</c> a unit normal out of the glass, <c>w</c> the offset.
    /// </param>
    /// <returns>The camera to render the room again from.</returns>
    /// <remarks>
    /// <para>
    /// Everything about the camera reflects: where it stands, what it looks at, and which
    /// way is up. The last of those is the one that is easy to leave out, and leaving it out
    /// is invisible on a mirror hanging vertically on a wall and turns the reflection upside
    /// down on any mirror that is not. Up is a direction, so it reflects without the
    /// plane's offset; the other two are points and reflect with it.
    /// </para>
    /// <para>
    /// <b>What comes out is wound the other way.</b> A reflection has a determinant of minus
    /// one, so every triangle rendered through this camera faces the opposite way from the
    /// same triangle rendered through the real one, and whatever draws it has to turn its
    /// culling around to match. Nothing here can do that — a camera is a matrix and knows
    /// nothing about a pipeline — so it is the caller's to remember, and a reflection
    /// showing nothing but the insides of the room is what forgetting looks like.
    /// </para>
    /// <para>
    /// The jitter is <em>not</em> copied. It belongs to a sequence of frames being
    /// accumulated into one picture of the scene as the player sees it, and the reflection
    /// is sampled by a shader rather than accumulated; carrying it over shakes the
    /// reflection by half a pixel against the mirror holding it.
    /// </para>
    /// </remarks>
    public Camera Mirrored(Vector4 plane)
    {
        Vector3 normal = new(plane.X, plane.Y, plane.Z);

        // The reflection itself, as a matrix, in the row-vector convention the rest of this
        // uses: a point times this is the point reflected through the plane.
        var reflection = new Matrix4x4(
            1f - (2f * normal.X * normal.X), -2f * normal.X * normal.Y, -2f * normal.X * normal.Z, 0f,
            -2f * normal.Y * normal.X, 1f - (2f * normal.Y * normal.Y), -2f * normal.Y * normal.Z, 0f,
            -2f * normal.Z * normal.X, -2f * normal.Z * normal.Y, 1f - (2f * normal.Z * normal.Z), 0f,
            -2f * plane.W * normal.X, -2f * plane.W * normal.Y, -2f * plane.W * normal.Z, 1f);

        return new Camera
        {
            Position = MirrorSurfaces.Reflect(plane, Position),
            Target = MirrorSurfaces.Reflect(plane, Target),
            Up = MirrorSurfaces.ReflectDirection(plane, Up),
            ViewOverride = reflection * View,
            FieldOfView = FieldOfView,
            NearPlane = NearPlane,
            FarPlane = FarPlane,
            LightDirection = LightDirection,
            Background = Background,
        };
    }

    /// <summary>Builds the projection matrix, including this frame's jitter.</summary>
    /// <param name="aspect">Width divided by height.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    /// Everything that rasterises or traces against this frame uses this one, jitter and
    /// all, so that a depth buffer, a normal and a fragment position all describe the same
    /// picture. The only thing that wants the unjittered form is the motion vector, which
    /// is a statement about where geometry went and not about where it was sampled — see
    /// <see cref="ProjectionWithoutJitter"/>.
    /// </remarks>
    public Matrix4x4 Projection(float aspect)
    {
        Matrix4x4 projection = ProjectionWithoutJitter(aspect);

        if (Jitter == Vector2.Zero)
        {
            return projection;
        }

        // Added to the z-to-x and z-to-y terms rather than to the translation row, because
        // the offset has to be proportional to w. This projection's w is the view-space
        // depth, so a constant added there would move a wall by a pixel and a distant
        // hillside by a hundred.
        projection.M31 += Jitter.X;
        projection.M32 += Jitter.Y;

        return projection;
    }

    /// <summary>The projection with the sample point back in the middle of the pixel.</summary>
    /// <param name="aspect">Width divided by height.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    /// What a motion vector is measured against. A vector taken between two jittered
    /// projections carries the difference between two jitters as well as the movement, and
    /// every temporal upscaler then filters against a signal that shakes by half a pixel
    /// whether or not anything moved. Keeping the previous frame's matrix unjittered and
    /// adding this frame's offset back in the fragment shader is how the two are separated.
    /// </remarks>
    public Matrix4x4 ProjectionWithoutJitter(float aspect)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            FieldOfView, aspect, NearPlane, FarPlane);

        // Flip Y for Vulkan's clip space.
        projection.M22 *= -1;
        return projection;
    }

    /// <summary>The ray a pixel of the rendered image looks along.</summary>
    /// <param name="x">Column, from the left edge of the image.</param>
    /// <param name="y">Row, from the top edge of the image.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>A ray from the camera through the middle of that pixel.</returns>
    /// <remarks>
    /// The basis is the left-handed one the view matrix uses — screen right is
    /// <c>cross(up, forward)</c>, not the other way about — so a ray built here lands on
    /// what the pixel actually shows rather than on its mirror image. The Y flip the
    /// projection carries for Vulkan's clip space does not appear here: this works in view
    /// space, where up is up, and the row is counted from the top because that is how an
    /// image is indexed and where a mouse position comes from.
    /// </remarks>
    public Ray RayThrough(int x, int y, int width, int height)
    {
        Vector3 forward = Vector3.Normalize(Target - Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Up, forward));
        Vector3 up = Vector3.Cross(forward, right);

        float extent = MathF.Tan(FieldOfView * 0.5f);
        float aspect = height > 0 ? (float)width / height : 1f;

        float across = width > 0 ? ((2f * (x + 0.5f)) / width) - 1f : 0f;
        float rise = height > 0 ? 1f - ((2f * (y + 0.5f)) / height) : 0f;

        return new Ray(
            Position,
            Vector3.Normalize(
                forward + (right * (across * extent * aspect)) + (up * (rise * extent))));
    }

    /// <summary>Places a camera so that a bounding box fills the view.</summary>
    /// <param name="minimum">Lower corner of the bounds.</param>
    /// <param name="maximum">Upper corner of the bounds.</param>
    /// <param name="up">Which axis is up.</param>
    /// <param name="azimuth">Rotation around the up axis, in radians.</param>
    /// <returns>The camera.</returns>
    public static Camera Framing(Vector3 minimum, Vector3 maximum, Vector3 up, float azimuth = 0.6f)
    {
        Vector3 center = (minimum + maximum) * 0.5f;
        Vector3 extent = maximum - minimum;

        // The largest single axis, not the diagonal: the diagonal of a tall thin subject
        // is far bigger than anything visible on screen, and framing to it leaves a
        // character as a speck in the middle of the image.
        float radius = MathF.Max(0.001f, MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) * 0.5f);

        const float FieldOfViewRadians = MathF.PI / 3f;

        float distance = radius / MathF.Tan(FieldOfViewRadians * 0.5f) * 1.3f;

        Vector3 forward = Vector3.Normalize(new Vector3(MathF.Sin(azimuth), 0, MathF.Cos(azimuth)));

        // The offset is built in a frame where the given axis is up, so this works for
        // both the Y-up models and anything Z-up without a separate code path.
        Vector3 upward = Vector3.Normalize(up);
        Vector3 right = Vector3.Cross(upward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-6f)
        {
            right = Vector3.UnitX;
        }

        Vector3 planar = Vector3.Normalize(Vector3.Cross(upward, Vector3.Normalize(right)));
        Vector3 direction = Vector3.Normalize(
            (planar * forward.Z) + (Vector3.Normalize(right) * forward.X) + (upward * 0.45f));

        return new Camera
        {
            Position = center + (direction * distance),
            Target = center,
            Up = upward,
            FieldOfView = FieldOfViewRadians,

            // Scaled to the subject: GK3's units put a character around 70 tall and a
            // full scene in the thousands, so fixed planes would clip one or the other.
            NearPlane = MathF.Max(0.01f, distance * 0.01f),
            FarPlane = distance * 10f,
        };
    }
}

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// A view onto the scene.
/// </summary>
/// <remarks>
/// <para>
/// Uses a reversed-Y projection. Vulkan's clip space has Y pointing down, the opposite of
/// the convention <see cref="Matrix4x4.CreatePerspectiveFieldOfView"/> assumes, so without
/// the flip everything renders upside down — a mistake easy to misread as a broken model
/// rather than a broken matrix.
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

    /// <summary>The view matrix.</summary>
    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Up);

    /// <summary>Builds the projection matrix.</summary>
    /// <param name="aspect">Width divided by height.</param>
    /// <returns>The projection.</returns>
    public Matrix4x4 Projection(float aspect)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            FieldOfView, aspect, NearPlane, FarPlane);

        // Flip Y for Vulkan's clip space.
        projection.M22 *= -1;
        return projection;
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

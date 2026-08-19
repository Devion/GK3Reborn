using System.Numerics;
using GK3Reborn.Platform;

namespace GK3Reborn.Rendering;

/// <summary>
/// A camera the player can fly around a scene with.
/// </summary>
/// <remarks>
/// <para>
/// Not how the game will present a scene — GK3 cuts between fixed viewpoints rather than
/// letting the player roam. It exists because being able to move through a scene is the
/// only practical way to check that geometry, textures and lighting are right everywhere
/// rather than only where a room camera happens to point.
/// </para>
/// <para>
/// Speed scales with the scene's size. GK3's units put a character at about 70 tall and a
/// street scene in the thousands, so a fixed speed is either unusably slow in one and
/// uncontrollable in the other.
/// </para>
/// </remarks>
public sealed class FreeCamera
{
    private const float PitchLimit = (MathF.PI / 2f) - 0.01f;

    private float _yaw;
    private float _pitch;

    /// <summary>Where the camera is.</summary>
    public Vector3 Position { get; set; }

    /// <summary>How fast it moves, in scene units per second.</summary>
    public float Speed { get; set; } = 200f;

    /// <summary>How far the near plane sits.</summary>
    public float NearPlane { get; set; } = 1f;

    /// <summary>How far the far plane sits.</summary>
    public float FarPlane { get; set; } = 10_000f;

    /// <summary>How much the view turns per pixel of pointer movement, in radians.</summary>
    public float LookSensitivity { get; set; } = 0.004f;

    /// <summary>Which way the camera looks.</summary>
    public Vector3 Forward => new(
        MathF.Cos(_pitch) * MathF.Sin(_yaw),
        MathF.Sin(_pitch),
        MathF.Cos(_pitch) * MathF.Cos(_yaw));

    /// <summary>Points the camera as another camera does.</summary>
    /// <param name="camera">The camera to copy.</param>
    public void CopyFrom(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Position = camera.Position;
        NearPlane = camera.NearPlane;
        FarPlane = camera.FarPlane;

        Vector3 direction = camera.Target - camera.Position;
        if (direction.LengthSquared() > 1e-9f)
        {
            direction = Vector3.Normalize(direction);
            _pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
            _yaw = MathF.Atan2(direction.X, direction.Z);
        }
    }

    /// <summary>Advances the camera by one frame of input.</summary>
    /// <param name="input">What the player is doing.</param>
    /// <param name="seconds">How long the frame lasted.</param>
    public void Update(IGameInput input, float seconds)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.IsDragging)
        {
            _yaw -= input.PointerDelta.X * LookSensitivity;
            _pitch = Math.Clamp(_pitch - (input.PointerDelta.Y * LookSensitivity), -PitchLimit, PitchLimit);
        }

        Vector3 forward = Forward;

        // cross(forward, up), not cross(up, forward). Matrix4x4.CreateLookAt is
        // right-handed, so the basis vector that maps to screen right is
        // cross(up, position - target) — which is cross(forward, up). The other
        // order is its negative, and strafes the wrong way.
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

        var movement = Vector3.Zero;

        if (input.IsHeld(CameraAction.Forward))
        {
            movement += forward;
        }

        if (input.IsHeld(CameraAction.Back))
        {
            movement -= forward;
        }

        if (input.IsHeld(CameraAction.Left))
        {
            movement -= right;
        }

        if (input.IsHeld(CameraAction.Right))
        {
            movement += right;
        }

        if (input.IsHeld(CameraAction.Up))
        {
            movement += Vector3.UnitY;
        }

        if (input.IsHeld(CameraAction.Down))
        {
            movement -= Vector3.UnitY;
        }

        if (movement.LengthSquared() > 1e-9f)
        {
            float speed = Speed * (input.IsHeld(CameraAction.Fast) ? 4f : 1f);
            Position += Vector3.Normalize(movement) * speed * seconds;
        }
    }

    /// <summary>Builds the camera to render with.</summary>
    /// <param name="template">Camera to take the lighting and background from.</param>
    /// <returns>The camera.</returns>
    public Camera ToCamera(Camera template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return new Camera
        {
            Position = Position,
            Target = Position + Forward,
            Up = Vector3.UnitY,
            FieldOfView = template.FieldOfView,
            NearPlane = NearPlane,
            FarPlane = FarPlane,
            LightDirection = template.LightDirection,
            Background = template.Background,
        };
    }
}

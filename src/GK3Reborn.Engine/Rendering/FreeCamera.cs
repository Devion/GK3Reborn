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

    /// <summary>
    /// Where the camera is looking, in degrees: heading across, pitch up and down.
    /// </summary>
    /// <remarks>
    /// Degrees rather than the radians it keeps inside, because the two things that read
    /// this are the game's own data — the binoculars' rectangles of sky and the scene
    /// files' camera angles — and both are written in degrees.
    /// </remarks>
    public Vector2 Aim
    {
        get => new(
            ((_yaw * 180f / MathF.PI) % 360f + 360f) % 360f,
            _pitch * 180f / MathF.PI);

        set
        {
            _yaw = value.X * MathF.PI / 180f;
            _pitch = Math.Clamp(value.Y * MathF.PI / 180f, -PitchLimit, PitchLimit);
        }
    }

    /// <summary>How far the near plane sits.</summary>
    public float NearPlane { get; set; } = 1f;

    /// <summary>How far the far plane sits.</summary>
    public float FarPlane { get; set; } = 10_000f;

    /// <summary>How much the view turns per pixel of pointer movement, in radians.</summary>
    public float LookSensitivity { get; set; } = 0.004f;

    /// <summary>
    /// What decides how far a step is allowed to get, or null to let it go anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Given where the camera is and the offset it wants to move by; answers where it ends
    /// up, which may be short of the offset and off to one side of it — a camera stopped
    /// dead by every wall it brushed would be unusable, so what stops it is expected to
    /// let it slide along instead.
    /// </para>
    /// <para>
    /// A hook rather than the thing itself. What the camera may not pass through is a
    /// question about the room, and the room belongs to the game rather than to the
    /// renderer; see <c>Game.Navigation.CameraBounds</c>, which is what fills this in.
    /// </para>
    /// </remarks>
    public Func<Vector3, Vector3, Vector3>? Confine { get; set; }

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
            // Yaw increases toward screen right. Forward is (sin yaw, ., cos yaw), whose
            // derivative in yaw is (cos yaw, ., -sin yaw) — which is cross(up, forward),
            // the left-handed right. Dragging the pointer right therefore has to add.
            // Under the old right-handed view the same two vectors were negatives of each
            // other and this subtracted, which is why the sign changes with the camera.
            _yaw += input.PointerDelta.X * LookSensitivity;

            // Pitch is unaffected by any of that: it is a rotation about the screen's own
            // horizontal axis, and the pointer's Y grows downward, so looking down
            // subtracts either way.
            _pitch = Math.Clamp(_pitch - (input.PointerDelta.Y * LookSensitivity), -PitchLimit, PitchLimit);
        }

        Vector3 forward = Forward;

        // cross(up, forward), the left-handed order, because the view matrix is
        // left-handed to match GK3's own world; see Camera. The other order is its
        // negative, and strafes the wrong way.
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));

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
            Vector3 step = Vector3.Normalize(movement) * speed * seconds;

            Position = Confine is { } fence ? fence(Position, step) : Position + step;
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

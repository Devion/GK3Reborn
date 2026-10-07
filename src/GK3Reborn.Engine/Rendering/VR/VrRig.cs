using System.Numerics;

namespace GK3Reborn.Rendering.VR;

/// <summary>A tracked pose in OpenXR's right-handed, metre-based reference space.</summary>
public readonly record struct VrPose(Vector3 Position, Quaternion Orientation);

public enum VrCameraMode { FirstPerson, Original }

public readonly record struct VrInput(
    VrPose Head, VrPose LeftAim, VrPose RightAim, bool HeadTracked,
    bool LeftTracked, bool RightTracked, float Teleport, float Select,
    Vector2 Move, Vector2 Turn, bool Menu, bool Alternate)
{
    public bool Inventory { get; init; }
    public bool Journal { get; init; }
    public bool Recenter { get; init; }
    public VrPose LeftGrip { get; init; }
    public VrPose RightGrip { get; init; }
    public bool LeftGripTracked { get; init; }
    public bool RightGripTracked { get; init; }
    public float LeftSqueeze { get; init; }
    public float RightSqueeze { get; init; }
    public bool LeftIndexTouch { get; init; }
    public bool RightIndexTouch { get; init; }
    public bool LeftThumbTouch { get; init; }
    public bool RightThumbTouch { get; init; }
}

/// <summary>The room's placement relative to the player's physical tracking space.</summary>
public sealed class VrRig
{
    // A character's 60-unit eye height corresponds to approximately 1.65 metres.
    public const float DefaultUnitsPerMetre = 60f / 1.65f;
    public float UnitsPerMetre { get; }
    public Vector3 Origin { get; private set; }
    public float Yaw { get; private set; }
    public VrCameraMode Mode { get; set; }
    public bool Placed { get; private set; }
    public float HeightOffsetMetres { get; set; }
    private bool _turnHeld;

    public VrRig(float unitsPerMetre = DefaultUnitsPerMetre)
    {
        if (!float.IsFinite(unitsPerMetre) || unitsPerMetre is < 1f or > 1000f)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsPerMetre));
        }
        UnitsPerMetre = unitsPerMetre;
    }

    private static Vector3 Reflect(Vector3 value) => new(value.X, value.Y, -value.Z);
    private Vector3 Rotate(Vector3 value) => Vector3.Transform(value, Matrix4x4.CreateRotationY(Yaw));
    public Vector3 Point(Vector3 tracking) => Origin + Rotate(Reflect(tracking) * UnitsPerMetre)
        + Vector3.UnitY * HeightOffsetMetres * UnitsPerMetre;
    public Vector3 Direction(VrPose pose, Vector3 local) =>
        Rotate(Reflect(Vector3.Transform(local, pose.Orientation)));
    public Ray Aim(VrPose pose) => new(Point(pose.Position), Direction(pose, -Vector3.UnitZ));

    public void Place(Vector3 basePosition, float yaw, VrPose head, VrCameraMode mode)
    {
        Mode = mode;
        Vector3 physicalForward = Reflect(Vector3.Transform(-Vector3.UnitZ, head.Orientation));
        Yaw = yaw - MathF.Atan2(physicalForward.X, physicalForward.Z);
        Vector3 offset = Rotate(Reflect(head.Position) * UnitsPerMetre);
        Origin = basePosition - (mode == VrCameraMode.FirstPerson
            ? new Vector3(offset.X, 0f, offset.Z) : offset);
        Placed = true;
        _turnHeld = false;
    }

    public void Teleport(Vector3 destination, VrPose head)
    {
        Vector3 offset = Rotate(Reflect(head.Position) * UnitsPerMetre);
        Origin = destination - new Vector3(offset.X, 0f, offset.Z);
    }

    public bool SnapTurn(float stick, VrPose head)
    {
        if (MathF.Abs(stick) < 0.3f)
        {
            _turnHeld = false;
        }
        if (_turnHeld || MathF.Abs(stick) < 0.7f)
        {
            return false;
        }
        Vector3 pivot = Point(head.Position);
        Yaw += MathF.CopySign(MathF.PI / 6f, stick);
        Origin += pivot - Point(head.Position);
        _turnHeld = true;
        return true;
    }

    public void Move(Vector3 displacement) => Origin += displacement;

    public void Turn(float radians, VrPose head)
    {
        Vector3 pivot = Point(head.Position);
        Yaw += radians;
        Origin += pivot - Point(head.Position);
    }

    public void SmoothTurn(float stick, VrPose head, float degreesPerSecond, float seconds)
    {
        float amount = MathF.Abs(stick) <= 0.2f ? 0 : MathF.CopySign((MathF.Abs(stick) - 0.2f) / 0.8f, stick);
        Turn(amount * degreesPerSecond * MathF.PI / 180 * Math.Clamp(seconds, 0, 0.1f), head);
    }

    public Camera Eye(VrPose pose, Vector4 tangents, Camera template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Vector3 eye = Point(pose.Position);
        float near = 0.05f * UnitsPerMetre;
        float far = MathF.Max(template.FarPlane, 500f * UnitsPerMetre);
        // X left, Y right, Z down, W up. Reflecting Z converts OpenXR to GK3;
        // its field-of-view tangents still describe screen left/right and down/up.
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveOffCenterLeftHanded(
            tangents.X * near, tangents.Y * near, tangents.Z * near, tangents.W * near, near, far);
        projection.M22 *= -1f;
        projection.M32 *= -1f;
        return new Camera
        {
            Position = eye, Target = eye + Direction(pose, -Vector3.UnitZ),
            Up = Direction(pose, Vector3.UnitY), NearPlane = near, FarPlane = far,
            ProjectionOverride = projection, Background = template.Background,
            LightDirection = template.LightDirection,
        };
    }
}

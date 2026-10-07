using System.Numerics;

namespace GK3Reborn.Rendering.VR;

/// <summary>A panel anchored in tracking space; rendering and picking share this pose.</summary>
public sealed class VrPanel
{
    public VrPose Pose { get; private set; }
    public bool Placed { get; private set; }
    public bool Interactive { get; set; } = true;
    public bool WorldMenu { get; set; }
    public bool Keyboard { get; set; }
    public Vector3? WorldTarget { get; set; }
    public bool PointerOnPanel { get; set; }
    public const float Width = 1.6f, Height = 0.9f;

    public void Place(VrPose head)
    {
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, head.Orientation);
        forward.Y = 0;
        if (forward.LengthSquared() < 0.001f) { forward = -Vector3.UnitZ; }
        forward = Vector3.Normalize(forward);
        float yaw = MathF.Atan2(-forward.X, -forward.Z);
        Pose = new VrPose(head.Position + forward * 1.5f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
        Placed = true;
    }

    public Vector2? Hit(VrPose aim)
    {
        if (!Placed) { return null; }
        Quaternion inverse = Quaternion.Inverse(Pose.Orientation);
        Vector3 origin = Vector3.Transform(aim.Position - Pose.Position, inverse);
        Vector3 direction = Vector3.Transform(Vector3.Transform(-Vector3.UnitZ, aim.Orientation), inverse);
        if (direction.Z >= -0.001f) { return null; }
        float distance = -origin.Z / direction.Z;
        Vector3 at = origin + direction * distance;
        if (distance <= 0 || MathF.Abs(at.X) > Width / 2 || MathF.Abs(at.Y) > Height / 2) { return null; }
        return new Vector2(0.5f + at.X / Width, 0.5f - at.Y / Height);
    }

    private const string Characters = "1234567890QWERTYUIOPASDFGHJKL-ZXCVBNM.,?";
    public string? KeyAt(Vector2 point)
    {
        if (!Interactive) { return null; }
        if (point.X is >= 0.8f and <= 0.99f && point.Y is >= 0.94f and <= 0.995f) { return "keyboard"; }
        if (!Keyboard || point.X is < 0.05f or >= 0.95f || point.Y is < 0.5f or >= 0.93f) { return null; }
        int row = Math.Min(4, (int)((point.Y - 0.5f) / 0.086f));
        int column = Math.Min(9, (int)((point.X - 0.05f) / 0.09f));
        return row == 4 ? column < 5 ? "space" : column < 8 ? "backspace" : "enter" : Characters[(row * 10) + column].ToString();
    }

    public void DrawControls(Overlay overlay)
    {
        if (!Interactive) { return; }
        void Key(string label, float x, float y, float width, float height)
        {
            float px = x * overlay.Width, py = y * overlay.Height;
            overlay.Rect(px, py, width * overlay.Width - 2, height * overlay.Height - 2, new Vector4(0.035f, 0.05f, 0.07f, 0.98f));
            overlay.Text(label, px + 6, py + 4, Vector4.One);
        }
        Key(Keyboard ? "Hide keys" : "Keyboard", 0.8f, 0.94f, 0.19f, 0.055f);
        if (!Keyboard) { return; }
        overlay.Rect(0.04f * overlay.Width, 0.49f * overlay.Height, 0.92f * overlay.Width, 0.45f * overlay.Height,
            new Vector4(0.15f, 0.2f, 0.25f, 1));
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 10; column++)
            { Key(Characters[row * 10 + column].ToString(), 0.05f + column * 0.09f, 0.5f + row * 0.086f, 0.09f, 0.086f); }
        }
        Key("Space", 0.05f, 0.844f, 0.45f, 0.086f);
        Key("Delete", 0.5f, 0.844f, 0.27f, 0.086f);
        Key("Enter", 0.77f, 0.844f, 0.18f, 0.086f);
    }
}

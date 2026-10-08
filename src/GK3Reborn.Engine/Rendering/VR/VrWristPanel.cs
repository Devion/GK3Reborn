using System.Numerics;
using GK3Reborn.Platform;
using GK3Reborn.UI;

namespace GK3Reborn.Rendering.VR;

/// <summary>A small status and shortcut panel above the left wrist, in tracking space.</summary>
public sealed class VrWristPanel
{
    public const float Width = 0.32f, Height = 0.18f;
    public VrPose Pose { get; private set; }
    public Overlay? Overlay { get; private set; }
    public bool Enabled { get; set; }
    public bool Visible { get; private set; }
    public Vector2? HoverPoint { get; set; }

    public void Track(VrInput input, bool focused, bool transitioning)
    {
        Visible = Enabled && input.LeftGripTracked && input.HeadTracked && focused && !transitioning;
        if (!Visible) { HoverPoint = null; return; }
        Quaternion orientation = Quaternion.Normalize(input.LeftGrip.Orientation *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2));
        Pose = new VrPose(input.LeftGrip.Position +
            Vector3.Transform(new Vector3(0, 0.09f, 0.08f), input.LeftGrip.Orientation), orientation);
    }

    public Vector2? Hit(VrPose aim)
    {
        if (!Visible) { return null; }
        Quaternion inverse = Quaternion.Inverse(Pose.Orientation);
        Vector3 origin = Vector3.Transform(aim.Position - Pose.Position, inverse);
        Vector3 direction = Vector3.Transform(Vector3.Transform(-Vector3.UnitZ, aim.Orientation), inverse);
        if (direction.Z >= -0.001f) { return null; }
        float distance = -origin.Z / direction.Z;
        Vector3 at = origin + direction * distance;
        if (distance <= 0 || MathF.Abs(at.X) > Width / 2 || MathF.Abs(at.Y) > Height / 2) { return null; }
        return new Vector2(0.5f + at.X / Width, 0.5f - at.Y / Height);
    }

    public static CameraAction? ActionAt(Vector2 point) => point.Y is >= 0.4f and <= 0.95f
        ? point.X is >= 0.05f and < 0.48f ? CameraAction.Journal
        : point.X is >= 0.52f and <= 0.95f ? CameraAction.Inventory : null : null;

    public void Build(OverlayAtlas atlas, string place, Func<string, ItemIcon>? pictures, UiText text)
    {
        if (Overlay?.Atlas != atlas) { Overlay = new Overlay(atlas); }
        Overlay.Begin(640, 360);
        Overlay.Rect(0, 0, 640, 360, new Vector4(0.035f, 0.045f, 0.055f, 0.97f));
        Overlay.Rect(8, 8, 624, 3, new Vector4(0.8f, 0.63f, 0.35f, 1));
        // Wrap the localized location/time line to keep long room names readable.
        float x = 20, y = 22;
        foreach (string word in place.Split(' '))
        {
            float size = Overlay.Measure(word + " ");
            if (x + size > 620) { x = 20; y += Overlay.LineHeight; }
            Overlay.Text(word + " ", x, y, Vector4.One); x += size;
        }
        Button(0.05f, "I_NOTEBOOKGAB_STD.BMP", text.Say("hud.journal", "Journal"));
        Button(0.52f, "RC_INVENTORY_OPEN_STD.BMP", text.Say("hud.pockets", "Pockets"));

        void Button(float left, string art, string label)
        {
            bool hovered = HoverPoint is { } at && at.X >= left && at.X <= left + 0.43f && at.Y is >= 0.4f and <= 0.95f;
            float px = left * 640;
            Overlay.Rect(px, 144, 275, 198, hovered ? new Vector4(0.24f, 0.22f, 0.16f, 1) : new Vector4(0.1f, 0.12f, 0.14f, 1));
            if (pictures?.Invoke(art) is { Drawn: true } icon)
            {
                Vector4 fit = icon.Fit(px + 77, 154, 120);
                Overlay.Picture(icon.Picture, fit.X, fit.Y, fit.Z, fit.W, Vector4.One);
            }
            Overlay.Text(label, px + (275 - Overlay.Measure(label)) / 2, 306, Vector4.One);
        }
        if (HoverPoint is { } pointer) { Overlay.Rect(pointer.X * 640 - 3, pointer.Y * 360 - 3, 6, 6, Vector4.One); }
    }
}

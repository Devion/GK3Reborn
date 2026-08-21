using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Platform;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the camera and for colour keying.
/// </summary>
public sealed class CameraAndKeyingTests
{
    [Fact]
    public void The_projection_flips_y_for_vulkan_clip_space()
    {
        var camera = new Camera { Position = new Vector3(0, 0, -5), Target = Vector3.Zero };

        Matrix4x4 projection = camera.Projection(4f / 3f);
        Matrix4x4 unflipped = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            camera.FieldOfView, 4f / 3f, camera.NearPlane, camera.FarPlane);

        Assert.Equal(-unflipped.M22, projection.M22, 5);
    }

    [Fact]
    public void A_point_above_the_target_lands_above_centre_on_screen()
    {
        var camera = new Camera { Position = new Vector3(0, 0, -5), Target = Vector3.Zero };
        Matrix4x4 viewProjection = camera.View * camera.Projection(1f);

        Vector4 clip = Vector4.Transform(new Vector4(0, 1, 0, 1), viewProjection);

        // Vulkan's Y grows downward, so a point above the centre must have negative Y.
        Assert.True(clip.Y / clip.W < 0);
    }

    [Fact]
    public void Framing_fits_the_subject_within_the_field_of_view()
    {
        var minimum = new Vector3(-10, 0, -10);
        var maximum = new Vector3(10, 120, 10);

        Camera camera = Camera.Framing(minimum, maximum, Vector3.UnitY);
        Vector3 center = (minimum + maximum) * 0.5f;

        Assert.Equal(center, camera.Target);

        float distance = (camera.Position - center).Length();
        float halfHeight = distance * MathF.Tan(camera.FieldOfView * 0.5f);

        // The tallest axis has to fit, with something left over.
        Assert.True(halfHeight > 60f, $"half height {halfHeight} does not cover the subject");
        Assert.True(halfHeight < 120f, $"half height {halfHeight} leaves the subject tiny");
    }

    [Fact]
    public void Framing_puts_the_camera_above_the_subject()
    {
        Camera camera = Camera.Framing(
            new Vector3(-1, -1, -1), new Vector3(1, 1, 1), Vector3.UnitY);

        Assert.True(camera.Position.Y > camera.Target.Y);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.1f)]
    [InlineData(-2.3f)]
    [InlineData(3.0f)]
    public void Strafing_right_moves_towards_the_right_of_the_screen(float yaw)
    {
        var camera = new FreeCamera { Position = Vector3.Zero, Speed = 100f };
        Turn(camera, yaw);

        // Taken before the move, so it is the basis the player was looking through
        // when they pressed the key.
        Matrix4x4 view = camera.ToCamera(new Camera()).View;

        camera.Update(new HeldInput(CameraAction.Right), 1f);

        // The view matrix's rotation maps a world direction onto the screen axes, so
        // this asks the projection which way is right rather than assuming a sign.
        Vector3 onScreen = Vector3.TransformNormal(camera.Position, view);

        Assert.True(onScreen.X > 0f, $"strafing right moved {onScreen.X} along screen X");
        Assert.True(MathF.Abs(onScreen.Y) < 1e-3f, "strafing should not change height");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.1f)]
    [InlineData(-2.3f)]
    [InlineData(3.0f)]
    public void Dragging_the_pointer_right_turns_the_view_right(float yaw)
    {
        var camera = new FreeCamera { Position = Vector3.Zero };
        Turn(camera, yaw);

        // The basis the player was looking through when they started the drag.
        Matrix4x4 view = camera.ToCamera(new Camera()).View;

        camera.Update(new DragInput(new Vector2(20f, 0f)), 1f);

        // Where the new forward lands on the old screen. Asking the view matrix rather
        // than asserting a sign on the yaw keeps this true whichever handedness the
        // camera uses, the way the strafe tests do.
        Vector3 onScreen = Vector3.TransformNormal(camera.Forward, view);

        Assert.True(onScreen.X > 0f, $"dragging right turned the view to screen X {onScreen.X}");
    }

    [Fact]
    public void Dragging_the_pointer_down_looks_down()
    {
        var camera = new FreeCamera { Position = Vector3.Zero };

        camera.Update(new DragInput(new Vector2(0f, 20f)), 1f);

        // The pointer's Y grows downward, so this is the player pulling the view down.
        Assert.True(camera.Forward.Y < 0f, $"the view tilted to {camera.Forward.Y}");
    }

    [Fact]
    public void Strafing_left_is_the_opposite_of_strafing_right()
    {
        var right = new FreeCamera { Position = Vector3.Zero, Speed = 100f };
        var left = new FreeCamera { Position = Vector3.Zero, Speed = 100f };
        Turn(right, 0.7f);
        Turn(left, 0.7f);

        right.Update(new HeldInput(CameraAction.Right), 1f);
        left.Update(new HeldInput(CameraAction.Left), 1f);

        Assert.Equal(-right.Position.X, left.Position.X, 3);
        Assert.Equal(-right.Position.Z, left.Position.Z, 3);
    }

    [Fact]
    public void Moving_forward_moves_into_the_screen()
    {
        var camera = new FreeCamera { Position = Vector3.Zero, Speed = 100f };
        Matrix4x4 view = camera.ToCamera(new Camera()).View;

        camera.Update(new HeldInput(CameraAction.Forward), 1f);

        // The view is left-handed, so it looks down its own positive Z and moving
        // forward has to increase it.
        Assert.True(Vector3.TransformNormal(camera.Position, view).Z > 0f);
    }

    [Fact]
    public void The_view_is_left_handed_like_the_world_it_shows()
    {
        // A point to the world's +X, seen from a camera looking along +Z, has to land on
        // the right of the screen. Through a right-handed view it lands on the left, and
        // every scene in the game comes out mirrored; see Camera.
        var camera = new Camera { Position = Vector3.Zero, Target = Vector3.UnitZ };

        Vector3 onScreen = Vector3.TransformNormal(Vector3.UnitX, camera.View);

        Assert.True(onScreen.X > 0f, $"world +X mapped to screen X {onScreen.X}");
    }

    [Fact]
    public void Magenta_becomes_transparent_and_stops_being_magenta()
    {
        // A two-by-two image: one opaque green texel and three key texels.
        byte[] pixels =
        [
            255, 0, 255, 255, 0, 200, 0, 255,
            255, 0, 255, 255, 255, 0, 255, 255,
        ];

        DecodedImage keyed = TextureKeying.Apply(
            new DecodedImage(2, 2, pixels, HasAlpha: false, "test"));

        Assert.True(keyed.HasAlpha);
        Assert.Equal(0, keyed.Pixels[3]);
        Assert.Equal(255, keyed.Pixels[7]);

        // The keyed texels take the opaque neighbour's colour, so filtering between them
        // cannot produce magenta.
        Assert.Equal(0, keyed.Pixels[0]);
        Assert.Equal(200, keyed.Pixels[1]);
        Assert.Equal(0, keyed.Pixels[2]);
    }

    [Fact]
    public void An_image_without_the_key_is_returned_untouched()
    {
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255];
        var image = new DecodedImage(2, 1, pixels, HasAlpha: false, "test");

        DecodedImage result = TextureKeying.Apply(image);

        Assert.Same(image.Pixels, result.Pixels);
        Assert.False(result.HasAlpha);
    }

    [Fact]
    public void A_fully_keyed_image_stays_transparent_rather_than_looping_forever()
    {
        byte[] pixels = [255, 0, 255, 255, 255, 0, 255, 255];

        DecodedImage result = TextureKeying.Apply(
            new DecodedImage(2, 1, pixels, HasAlpha: false, "test"));

        Assert.Equal(0, result.Pixels[3]);
        Assert.Equal(0, result.Pixels[7]);
    }

    /// <summary>Points a camera at a yaw, since the field behind it is private.</summary>
    private static void Turn(FreeCamera camera, float yaw)
    {
        Vector3 position = camera.Position;
        camera.CopyFrom(new Camera
        {
            Position = position,
            Target = position + new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)),
        });
    }

    /// <summary>Input with the pointer being dragged and no key held.</summary>
    private sealed class DragInput(Vector2 delta) : IGameInput
    {
        public Vector2 PointerDelta => delta;

        public bool IsDragging => true;

        public bool IsHeld(CameraAction action) => false;

        public bool WasPressed(CameraAction action) => false;

        public Vector2 PointerPosition => Vector2.Zero;

        public bool WasClicked(PointerButton button) => false;

        public bool WasDoubleClicked(PointerButton button) => false;

        public string Typed => string.Empty;

        public bool WasPressed(EditKey key) => false;

        public int ScrollDelta => 0;

        public void EndFrame()
        {
        }
    }

    /// <summary>Input with one action held down and nothing else happening.</summary>
    private sealed class HeldInput(CameraAction held) : IGameInput
    {
        public Vector2 PointerDelta => Vector2.Zero;

        public bool IsDragging => false;

        public bool IsHeld(CameraAction action) => action == held;

        public bool WasPressed(CameraAction action) => false;

        public Vector2 PointerPosition => Vector2.Zero;

        public bool WasClicked(PointerButton button) => false;

        public bool WasDoubleClicked(PointerButton button) => false;

        public string Typed => string.Empty;

        public bool WasPressed(EditKey key) => false;

        public int ScrollDelta => 0;

        public void EndFrame()
        {
        }
    }
}

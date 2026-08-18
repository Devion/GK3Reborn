using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
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
        Matrix4x4 unflipped = Matrix4x4.CreatePerspectiveFieldOfView(
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
}

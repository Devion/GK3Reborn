using System.Numerics;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for finding the plane a mirror reflects about, and for the camera on the far side
/// of it.
/// </summary>
/// <remarks>
/// Both halves of this were wrong once and neither said so. The material marks a whole slab
/// a mirror and the geometry has to pick the glass out of it; and a mirrored camera cannot
/// be described by an eye, a target and an up vector, which is a mistake that leaves the
/// camera in exactly the right place pointing exactly the right way.
/// </remarks>
public sealed class MirrorSurfaceTests
{
    /// <summary>A flat rectangle on the x/y plane at z, facing along the normal.</summary>
    private static MeshVertex[] Card(float z, Vector3 normal, float halfWidth = 10.5f, float halfHeight = 15f) =>
    [
        new(new Vector3(-halfWidth, -halfHeight, z), normal, Vector2.Zero, Vector2.Zero),
        new(new Vector3(halfWidth, -halfHeight, z), normal, Vector2.Zero, Vector2.Zero),
        new(new Vector3(halfWidth, halfHeight, z), normal, Vector2.Zero, Vector2.Zero),
        new(new Vector3(-halfWidth, halfHeight, z), normal, Vector2.Zero, Vector2.Zero),
    ];

    [Fact]
    public void A_flat_card_gives_the_plane_it_lies_on()
    {
        MirrorSurface glass = MirrorSurfaces.Fit(Card(3f, Vector3.UnitZ), Matrix4x4.Identity)!.Value;

        Assert.Equal(0f, glass.Plane.X, 4);
        Assert.Equal(0f, glass.Plane.Y, 4);
        Assert.Equal(1f, glass.Plane.Z, 4);

        // The offset is what puts the plane through the card rather than through the origin.
        Assert.Equal(-3f, glass.Plane.W, 4);
        Assert.Equal(new Vector3(0f, 0f, 3f), glass.Center);
    }

    [Fact]
    public void The_back_of_a_slab_is_a_plane_too_and_faces_the_other_way()
    {
        // What MIRRORL.MOD is: a box twenty-one by thirty by three whose front, back, sides,
        // top and bottom all carry the mirror's texture. Marking the texture marks every one
        // of them, and only one is the glass.
        MirrorSurface front = MirrorSurfaces.Fit(Card(3f, Vector3.UnitZ), Matrix4x4.Identity)!.Value;
        MirrorSurface back = MirrorSurfaces.Fit(Card(0f, -Vector3.UnitZ), Matrix4x4.Identity)!.Value;

        var eye = new Vector3(0f, 0f, 100f);

        // Both are flat, both are the same size, and the same fitted plane serves both. What
        // separates them is which way the vertices' own normals point — a fit gives a plane
        // and no side, and turning each fitted normal towards the camera would make the back
        // of the slab look exactly like the glass.
        Assert.Equal(front.Radius, back.Radius, 4);
        Assert.Null(MirrorSurfaces.Facing([back], eye));
        Assert.Equal(front, MirrorSurfaces.Facing([front, back], eye));
    }

    [Fact]
    public void A_slab_edge_is_not_the_glass()
    {
        // The sides of that box: flat, and a third the size. The rule is size over distance,
        // which is how much of the screen a thing covers — so the face wins from anywhere it
        // is visible at all rather than only from straight on.
        MirrorSurface face = MirrorSurfaces.Fit(Card(3f, Vector3.UnitZ), Matrix4x4.Identity)!.Value;

        MeshVertex[] edge =
        [
            new(new Vector3(10.5f, -15f, 0f), Vector3.UnitX, Vector2.Zero, Vector2.Zero),
            new(new Vector3(10.5f, -15f, 3f), Vector3.UnitX, Vector2.Zero, Vector2.Zero),
            new(new Vector3(10.5f, 15f, 3f), Vector3.UnitX, Vector2.Zero, Vector2.Zero),
            new(new Vector3(10.5f, 15f, 0f), Vector3.UnitX, Vector2.Zero, Vector2.Zero),
        ];

        MirrorSurface side = MirrorSurfaces.Fit(edge, Matrix4x4.Identity)!.Value;

        Assert.Equal(face, MirrorSurfaces.Facing([side, face], new Vector3(60f, 0f, 60f)));
    }

    [Fact]
    public void A_closed_box_is_no_plane_at_all()
    {
        // Every face of a box at once, which is what a submesh holding a whole slab looks
        // like. Its normals cancel, and a plane fitted to nothing is worse than no plane.
        MeshVertex[] box =
        [
            .. Card(3f, Vector3.UnitZ),
            .. Card(0f, -Vector3.UnitZ),
        ];

        Assert.Null(MirrorSurfaces.Fit(box, Matrix4x4.Identity));
    }

    [Fact]
    public void A_mirror_facing_away_is_not_this_frames_mirror()
    {
        MirrorSurface glass = MirrorSurfaces.Fit(Card(0f, Vector3.UnitZ), Matrix4x4.Identity)!.Value;

        // Behind it, which is the common case rather than an odd one: a room has mirrors on
        // more than one wall and most of them are facing away at any moment.
        Assert.Null(MirrorSurfaces.Facing([glass], new Vector3(0f, 0f, -100f)));

        // And edge-on, where what would come back is a sliver.
        Assert.Null(MirrorSurfaces.Facing([glass], new Vector3(1000f, 0f, 1f)));
    }

    [Fact]
    public void The_mirror_already_being_reflected_keeps_a_margin()
    {
        // TE4's two mirrors face each other across the room, so from the middle of it they
        // are very nearly tied — and a frame that changes its mind every frame is a
        // reflection flickering between two rooms.
        MirrorSurface here = MirrorSurfaces.Fit(
            Card(0f, Vector3.UnitZ), Matrix4x4.CreateTranslation(0f, 0f, -100f))!.Value;

        MirrorSurface there = MirrorSurfaces.Fit(
            Card(0f, -Vector3.UnitZ), Matrix4x4.CreateTranslation(0f, 0f, 101f))!.Value;

        // A hundred units away against a hundred and one: with nothing held, the nearer of
        // the two wins by a hair, which is exactly the tie that flickers.
        Assert.Equal(here, MirrorSurfaces.Facing([here, there], Vector3.Zero));

        // Holding the further one, a hair is not enough to take it away.
        Assert.Equal(there, MirrorSurfaces.Facing([here, there], Vector3.Zero, there));

        // Well past the margin it is: a mirror the camera has actually turned towards is
        // not held off by this.
        Assert.Equal(
            here, MirrorSurfaces.Facing([here, there], new Vector3(0f, 0f, -40f), there));
    }

    [Fact]
    public void A_point_on_the_glass_lands_on_the_same_pixel_in_both_renders()
    {
        // The whole reason the glass can read the reflection at its own screen position and
        // needs no matrix of its own: reflection fixes the mirror's plane pointwise, so a
        // point on it has the same clip position under both cameras. If this stops holding,
        // the reflection slides across the mirror as the camera moves and nothing else says
        // anything is wrong.
        var plane = new Vector4(0f, 0f, 1f, 0f);

        var camera = new Camera
        {
            Position = new Vector3(30f, 40f, 200f),
            Target = new Vector3(2f, -3f, 0f),
            Up = Vector3.UnitY,
        };

        Camera mirrored = camera.Mirrored(plane);

        Matrix4x4 real = camera.View * camera.Projection(16f / 9f);
        Matrix4x4 reflected = mirrored.View * mirrored.Projection(16f / 9f);

        foreach (Vector3 on in (ReadOnlySpan<Vector3>)[
            new(0f, 0f, 0f), new(10f, 7f, 0f), new(-12f, 15f, 0f)])
        {
            Vector4 here = Vector4.Transform(new Vector4(on, 1f), real);
            Vector4 there = Vector4.Transform(new Vector4(on, 1f), reflected);

            Assert.Equal(here.X / here.W, there.X / there.W, 3);
            Assert.Equal(here.Y / here.W, there.Y / there.W, 3);
        }
    }

    [Fact]
    public void The_mirrored_camera_is_not_a_look_at_from_the_reflected_points()
    {
        // The mistake this exists to catch, and it is invisible in every other way: the
        // camera lands in the right place, points the right way, and the reflection comes out
        // flipped left to right — a mirrored room being a perfectly plausible room. A
        // look-at always builds a basis of one handedness, and a reflection's view matrix
        // must have the opposite handedness from the camera it came from.
        var plane = new Vector4(0f, 0f, 1f, 0f);

        var camera = new Camera
        {
            Position = new Vector3(30f, 40f, 200f),
            Target = new Vector3(2f, -3f, 0f),
            Up = Vector3.UnitY,
        };

        Camera mirrored = camera.Mirrored(plane);

        Matrix4x4 lookAt = Matrix4x4.CreateLookAtLeftHanded(
            mirrored.Position, mirrored.Target, mirrored.Up);

        Assert.NotEqual(lookAt, mirrored.View);

        // And the reason: the determinant. A reflection reverses handedness, which is also
        // why whatever draws through this camera has to turn its culling around.
        Assert.True(mirrored.View.GetDeterminant() < 0f);
        Assert.True(camera.View.GetDeterminant() > 0f);
    }

    [Fact]
    public void Reflecting_twice_leaves_the_camera_where_it_started()
    {
        var plane = new Vector4(Vector3.Normalize(new Vector3(0.4f, 0.3f, -0.87f)), 103.3f);

        var camera = new Camera
        {
            Position = new Vector3(-99.8f, 65f, -17f),
            Target = new Vector3(-184.5f, 66.6f, 50.1f),
            Up = Vector3.UnitY,
        };

        Camera twice = camera.Mirrored(plane).Mirrored(plane);

        Assert.Equal(camera.Position.X, twice.Position.X, 2);
        Assert.Equal(camera.Position.Y, twice.Position.Y, 2);
        Assert.Equal(camera.Position.Z, twice.Position.Z, 2);
        Assert.True(twice.View.GetDeterminant() > 0f);
    }
}

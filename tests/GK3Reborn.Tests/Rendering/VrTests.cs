using System.Numerics;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.VR;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

public sealed class VrTests
{
    private static VrPose Head => new(new Vector3(0.4f, 1.65f, -0.3f), Quaternion.Identity);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 0.0001f, $"{expected} != {actual}");

    [Fact]
    public void Roomscale_uses_metres_and_preserves_physical_height()
    {
        var rig = new VrRig(40);
        rig.Place(new Vector3(10, 20, 30), 0, Head, VrCameraMode.FirstPerson);
        Near(new Vector3(10, 86, 30), rig.Point(Head.Position));
        Near(new Vector3(50, 86, 70), rig.Point(Head.Position + new Vector3(1, 0, -1)));
        Near(new Vector3(10, 66, 30), rig.Point(Head.Position - new Vector3(0, 0.5f, 0)));
    }
    [Fact]
    public void Original_camera_starts_at_the_authored_eye_and_retains_tracking()
    {
        var rig = new VrRig(40);
        Vector3 camera = new(100, 80, 200);
        rig.Place(camera, 0, Head, VrCameraMode.Original);
        Near(camera, rig.Point(Head.Position));
        Near(camera + new Vector3(4, 0, 0), rig.Point(Head.Position + new Vector3(0.1f, 0, 0)));
    }
    [Fact]
    public void Teleport_lands_under_the_head_after_roomscale_movement_and_turning()
    {
        var rig = new VrRig(40);
        rig.Place(Vector3.Zero, 1.2f, Head, VrCameraMode.FirstPerson);
        VrPose moved = Head with { Position = Head.Position + new Vector3(2, 0, 1) };
        rig.Teleport(new Vector3(100, 50, -100), moved);
        Near(new Vector3(100, 116, -100), rig.Point(moved.Position));
    }
    [Fact]
    public void Snap_turn_pivots_about_the_physical_head_and_requires_stick_release()
    {
        var rig = new VrRig(40);
        rig.Place(Vector3.Zero, 0, Head, VrCameraMode.FirstPerson);
        VrPose moved = Head with { Position = Head.Position + new Vector3(2, 0, 1) };
        Vector3 before = rig.Point(moved.Position);
        Assert.True(rig.SnapTurn(1, moved));
        Near(before, rig.Point(moved.Position));
        Assert.False(rig.SnapTurn(1, moved));
        Assert.False(rig.SnapTurn(0, moved));
        Assert.True(rig.SnapTurn(-1, moved));
        Near(before, rig.Point(moved.Position));
    }
    [Fact]
    public void Stereo_eyes_have_real_ipd_and_independent_asymmetric_projection()
    {
        var rig = new VrRig(40);
        rig.Place(Vector3.Zero, 0, Head, VrCameraMode.FirstPerson);
        var fov = new Vector4(-0.8f, 1.2f, -1.1f, 0.9f);
        Camera left = rig.Eye(Head with { Position = Head.Position - new Vector3(0.032f, 0, 0) }, fov, new Camera());
        Camera right = rig.Eye(Head with { Position = Head.Position + new Vector3(0.032f, 0, 0) }, fov, new Camera());
        Assert.Equal(2.56f, Vector3.Distance(left.Position, right.Position), 4);
        Matrix4x4 projection = left.ProjectionWithoutJitter(3);
        Vector4 edge = Vector4.Transform(new Vector4(fov.X * left.NearPlane, 0, left.NearPlane, 1), projection);
        Assert.Equal(-1f, edge.X / edge.W, 4);
        edge = Vector4.Transform(new Vector4(0, fov.W * left.NearPlane, left.NearPlane, 1), projection);
        Assert.Equal(-1f, edge.Y / edge.W, 4);
        Assert.Equal(projection, left.Mirrored(new Vector4(0, 0, 1, 0)).ProjectionWithoutJitter(1));
        Vector3 world = (left.Position + right.Position) / 2 + Vector3.UnitZ * 80;
        Vector4 l = Vector4.Transform(new Vector4(world, 1), left.View * projection);
        Vector4 r = Vector4.Transform(new Vector4(world, 1), right.View * projection);
        Assert.True(l.X / l.W > r.X / r.W);
    }
    [Fact]
    public void Preview_is_curved_and_does_not_teleport_until_release()
    {
        var arc = new TeleportArc();
        var ray = new Ray(new Vector3(0, 1.5f, 0), Vector3.UnitZ);
        TeleportHit? Ground(Vector3 a, Vector3 b) => b.Y <= 0
            ? new TeleportHit(Vector3.Lerp(a, b, a.Y / (a.Y - b.Y)), true) : null;
        Assert.Null(arc.Update(1, true, ray, 1, Ground));
        Assert.NotNull(arc.Destination);
        Assert.True(arc.Points.Count > 5);
        Assert.True(arc.Points[2].Y < arc.Points[1].Y);
        Vector3 expected = arc.Destination.Value;
        Assert.Equal(expected, arc.Update(0, true, ray, 1, Ground));
        Assert.Empty(arc.Points);
        Assert.Null(arc.Update(0, true, ray, 1, Ground));
    }
    [Fact]
    public void An_obstruction_stops_the_arc_and_refuses_a_landing()
    {
        var arc = new TeleportArc();
        var ray = new Ray(Vector3.UnitY, Vector3.UnitZ);
        TeleportHit? Wall(Vector3 a, Vector3 b) => new(a, false);
        arc.Update(1, true, ray, 1, Wall);
        Assert.Equal(2, arc.Points.Count);
        Assert.Null(arc.Destination);
        Assert.Null(arc.Update(0, true, ray, 1, Wall));
    }
    [Fact]
    public void Focus_or_tracking_loss_cancels_and_requires_a_fresh_trigger_press()
    {
        var arc = new TeleportArc();
        var ray = new Ray(Vector3.UnitY, Vector3.UnitZ);
        TeleportHit? Hit(Vector3 a, Vector3 b) => new(b, true);
        arc.Update(1, true, ray, 1, Hit);
        Assert.NotNull(arc.Destination);
        arc.Update(1, false, ray, 1, Hit);
        Assert.Empty(arc.Points);
        Assert.Null(arc.Update(0, true, ray, 1, Hit));
        Assert.Null(arc.Update(1, true, ray, 1, Hit));
        Assert.NotNull(arc.Update(0, true, ray, 1, Hit));
    }
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_world_scale_is_refused(float scale) => Assert.Throws<ArgumentOutOfRangeException>(() => new VrRig(scale));
}

using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Platform;
using GK3Reborn.Rendering.VR;
using GK3Reborn.UI;
using GK3Reborn.Rendering;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

public sealed class VrUsabilityTests
{
    private static VrPose Head => new(new Vector3(0, 1.2f, 0), Quaternion.Identity);

    [Fact]
    public void Panel_stays_in_place_when_head_turns_and_only_reanchors_on_request()
    {
        var panel = new VrPanel();
        panel.Place(Head);
        VrPose before = panel.Pose;
        Assert.Equal(new Vector2(0.5f), panel.Hit(Head));
        VrPose turned = Head with { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1) };
        Assert.Null(panel.Hit(turned));
        Assert.Equal(before, panel.Pose);
        panel.Place(turned);
        Assert.NotEqual(before, panel.Pose);
        Assert.True(Vector2.Distance(new Vector2(0.5f), panel.Hit(turned)!.Value) < 0.0001f);
    }

    [Fact]
    public void Seated_offset_moves_head_and_hands_together_without_moving_floor()
    {
        var rig = new VrRig(40);
        rig.Place(Vector3.Zero, 0, Head, VrCameraMode.FirstPerson);
        Vector3 hand = rig.Point(new Vector3(0.2f, 0.8f, 0));
        rig.HeightOffsetMetres = 0.5f;
        Assert.Equal(68, rig.Point(Head.Position).Y);
        Assert.Equal(hand + Vector3.UnitY * 20, rig.Point(new Vector3(0.2f, 0.8f, 0)));
        Assert.Equal(0, rig.Origin.Y);
    }

    [Fact]
    public void Smooth_turn_is_frame_rate_independent_and_pivots_around_head()
    {
        var a = new VrRig(); var b = new VrRig();
        a.Place(Vector3.Zero, 0, Head, VrCameraMode.FirstPerson);
        b.Place(Vector3.Zero, 0, Head, VrCameraMode.FirstPerson);
        Vector3 before = a.Point(Head.Position);
        for (int i = 0; i < 90; i++) { a.SmoothTurn(1, Head, 75, 1f / 90); }
        for (int i = 0; i < 72; i++) { b.SmoothTurn(1, Head, 75, 1f / 72); }
        Assert.Equal(a.Yaw, b.Yaw, 4);
        Assert.True(Vector3.Distance(before, a.Point(Head.Position)) < 0.001f);
        Assert.Equal(75 * MathF.PI / 180, a.Yaw, 4);
    }

    [Fact]
    public void Vr_tab_is_present_only_in_a_headset_session_and_edits_persistent_preferences()
    {
        var front = new FrontEnd(new Settings());
        Assert.DoesNotContain(front.Tabs, t => t.Id == "vr");
        front.Choose(new MenuAction("tab:vr"));
        Assert.Equal(FrontEndPage.Main, front.Page);
        front.VrEnabled = true;
        front.Choose(new MenuAction("tab:vr"));
        Assert.Equal(FrontEndPage.VR, front.Page);
        Assert.True(front.OnSettings);
        front.Choose(new MenuAction("vr-height", Fraction: 0.5f));
        Assert.Equal(0.5f, front.Settings.Vr.HeightOffsetMetres);
        front.Choose(new MenuAction("vr-move", Step: 1));
        Assert.Equal(VrLocomotionMode.Smooth, front.Settings.Vr.Locomotion);
        Assert.True(front.Dirty);
        Assert.True(front.StepSection(1));
        Assert.Equal(FrontEndPage.Gameplay, front.Page);
    }

    [Fact]
    public void Left_menu_works_without_a_right_controller_and_inventory_journal_have_buttons()
    {
        var session = new Session(); var sink = new Sink();
        var controls = new VrWindowControls(session, sink);
        controls.Poll();
        session.Input = session.Input with { Menu = true, RightTracked = false };
        controls.Poll();
        Assert.Contains(CameraAction.Quit, sink.Actions);
        Assert.Contains(EditKey.Escape, sink.Keys);
        sink.Actions.Clear(); controls.Poll();
        Assert.Empty(sink.Actions);
        session.Input = session.Input with { Menu = false, Inventory = true, Journal = true };
        controls.Poll();
        Assert.Contains(CameraAction.Inventory, sink.Actions);
        Assert.Contains(CameraAction.Journal, sink.Actions);
    }

    [Fact]
    public void Trigger_opens_actions_in_world_but_selects_and_drags_only_on_a_panel()
    {
        var session = new Session(); var sink = new Sink();
        session.Panel.Interactive = false;
        var controls = new VrWindowControls(session, sink);
        controls.Poll();
        session.Input = session.Input with { Select = 1 };
        controls.Poll(); Assert.Contains(PointerButton.Secondary, sink.Clicks);
        sink.Clicks.Clear(); session.Input = session.Input with { Select = 0 }; controls.Poll();
        session.Panel.Interactive = true;
        session.Input = session.Input with { Select = 1 }; controls.Poll();
        Assert.Contains(PointerButton.Primary, sink.Clicks); Assert.True(sink.ExternalPrimaryHeld);
        sink.Clicks.Clear();
        session.Input = session.Input with { Select = 0, RightAim = Head with { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2) } }; controls.Poll();
        session.Input = session.Input with { Select = 1 }; controls.Poll();
        Assert.Empty(sink.Clicks); Assert.False(sink.ExternalPrimaryHeld);
    }

    [Fact]
    public void Focus_loss_disarms_buttons_until_released()
    {
        var session = new Session(); var sink = new Sink(); var controls = new VrWindowControls(session, sink);
        controls.Poll(); session.Focused = false; session.Input = session.Input with { Select = 1 }; controls.Poll();
        session.Focused = true; controls.Poll(); Assert.Empty(sink.Clicks);
        session.Input = session.Input with { Select = 0 }; controls.Poll();
        session.Input = session.Input with { Select = 1 }; controls.Poll(); Assert.Single(sink.Clicks);
    }

    [Fact]
    public void Keyboard_covers_letters_numbers_and_editing_without_clicking_through()
    {
        var panel = new VrPanel { Keyboard = true };
        Assert.Equal("1", panel.KeyAt(new Vector2(0.08f, 0.53f)));
        Assert.Equal("Q", panel.KeyAt(new Vector2(0.08f, 0.6f)));
        Assert.Equal("space", panel.KeyAt(new Vector2(0.2f, 0.89f)));
        Assert.Equal("backspace", panel.KeyAt(new Vector2(0.6f, 0.89f)));
        Assert.Equal("enter", panel.KeyAt(new Vector2(0.9f, 0.89f)));
    }

    [Fact]
    public void Grip_and_touch_curl_distal_vertices_but_keep_the_wrist_fixed()
    {
        Vector3 wrist = new(0, 0, -2);
        Assert.Equal(wrist, VrHands.Curl(wrist, 1, 1, 1));
        Vector3 finger = new(0, 0, 3);
        Assert.Equal(finger, VrHands.Curl(finger, 0, 0, 0));
        Assert.True(VrHands.Curl(finger, 1, 0, 0).Y < -1);
        Vector3 index = new(1, 0, 3);
        Assert.Equal(index, VrHands.Curl(index, 1, 0, 0));
        Assert.True(VrHands.Curl(index, 0, 0.25f, 0).Y < 0);
    }

    [Fact]
    public void Wrist_moves_with_left_grip_and_hides_on_tracking_loss()
    {
        var wrist = new VrWristPanel { Enabled = true };
        VrInput input = new Session().Input with { LeftGrip = Head, LeftGripTracked = true };
        wrist.Track(input, true, false);
        Assert.True(wrist.Visible);
        VrPose before = wrist.Pose;
        wrist.Track(input with { LeftGrip = Head with { Position = Head.Position + Vector3.UnitX } }, true, false);
        Assert.Equal(before.Position + Vector3.UnitX, wrist.Pose.Position);
        wrist.Track(input with { LeftGripTracked = false }, true, false);
        Assert.False(wrist.Visible);
        Assert.Null(wrist.Hit(Head));
    }

    [Theory]
    [InlineData(0.25f, CameraAction.Journal)]
    [InlineData(0.75f, CameraAction.Inventory)]
    public void Right_trigger_selects_wrist_shortcut_without_clicking_the_room(float x, CameraAction action)
    {
        var session = new Session(); var sink = new Sink();
        session.Panel.Interactive = false;
        session.Panel.Wrist.Enabled = true;
        session.Input = session.Input with { LeftGrip = Head, LeftGripTracked = true };
        session.Panel.Wrist.Track(session.Input, true, false);
        VrPose pose = session.Panel.Wrist.Pose;
        Vector3 local = new((x - 0.5f) * VrWristPanel.Width, -0.2f * VrWristPanel.Height, 0.3f);
        session.Input = session.Input with { RightAim = new VrPose(pose.Position + Vector3.Transform(local, pose.Orientation), pose.Orientation) };
        var controls = new VrWindowControls(session, sink);
        controls.Poll();
        session.Input = session.Input with { Select = 1 };
        controls.Poll();
        Assert.Equal([action], sink.Actions);
        Assert.Empty(sink.Clicks);
        Assert.False(sink.ExternalPrimaryHeld);
        controls.Poll();
        Assert.Single(sink.Actions);
    }

    [Theory]
    [InlineData(VrCameraMode.FirstPerson)]
    [InlineData(VrCameraMode.Original)]
    public void Scripted_actor_and_camera_motion_never_translate_or_rotate_the_rig(VrCameraMode mode)
    {
        var (update, interaction) = Room();
        var session = new Session(); session.Rig.Mode = mode;
        var locomotion = new VrLocomotion();
        var walker = new FirstPerson { Ground = _ => 0 };
        var camera = new Camera();
        locomotion.Update(session, camera, walker, update, "gab", interaction, false, 1f / 90);
        Vector3 origin = session.Rig.Origin; float yaw = session.Rig.Yaw;
        for (int frame = 1; frame <= 90; frame++)
        {
            update.Step("gab", new Vector3(frame, frame / 2f, frame * 2), frame / 30f);
            camera = new Camera { Position = new Vector3(-frame, frame, frame) };
            locomotion.Update(session, camera, walker, update, "gab", interaction, false, 1f / 90);
            Assert.Equal(origin, session.Rig.Origin);
            Assert.Equal(yaw, session.Rig.Yaw);
        }
    }

    [Fact]
    public void Missing_floor_or_leaning_outside_walk_boundaries_does_not_black_out_the_view()
    {
        var (update, interaction) = Room();
        var session = new Session();
        var locomotion = new VrLocomotion();
        var walker = new FirstPerson { CanStand = _ => false };
        locomotion.Update(session, new Camera(), walker, update, "gab", interaction, true, 1f / 90);
        Assert.False(session.ComfortBlocked);
        session.Input = session.Input with { HeadTracked = false };
        locomotion.Update(session, new Camera(), walker, update, "gab", interaction, true, 1f / 90);
        Assert.True(session.ComfortBlocked);
        session.Input = session.Input with { HeadTracked = true };
        locomotion.Update(session, new Camera(), walker, update, "gab", interaction, true, 1f / 90);
        Assert.False(session.ComfortBlocked);
    }

    [Fact]
    public void Crossing_geometry_does_not_keep_retesting_a_stale_segment()
    {
        var (update, interaction) = Room(wall: true);
        var session = new Session(); var locomotion = new VrLocomotion();
        session.Rig.Place(Vector3.Zero, 0, Head, VrCameraMode.FirstPerson);
        var walker = new FirstPerson { Ground = _ => 0 };
        locomotion.Update(session, new Camera(), walker, update, "gab", interaction, false, 1f / 90);
        session.Input = session.Input with { Head = Head with { Position = Head.Position - Vector3.UnitZ } };
        locomotion.Update(session, new Camera(), walker, update, "gab", interaction, false, 1f / 90);
        Assert.True(session.ComfortBlocked);
        locomotion.Update(session, new Camera(), walker, update, "gab", interaction, false, 1f / 90);
        Assert.False(session.ComfortBlocked);
    }

    private static (SceneUpdate Update, SceneInteraction Interaction) Room(bool wall = false)
    {
        var sink = new HeadlessSceneSink();
        ModFile model = ModFile.FromMeshes("gab", []);
        var actor = new PlacedModel("gab", "GABRIEL", null, model, Matrix4x4.Identity, PlacedModelKind.Actor, sink.Add(model));
        var placed = new List<PlacedModel> { actor };
        if (wall)
        {
            ModFile barrier = ModFile.FromMeshes("wall", [new ModMesh
            {
                MeshToLocal = Matrix4x4.Identity, BoundsMin = new(-100, 0, 20), BoundsMax = new(100, 100, 20),
                Submeshes = [new ModSubmesh
                {
                    TextureName = "wall", Color = (255, 255, 255),
                    Positions = [new(-100, 0, 20), new(100, 0, 20), new(100, 100, 20), new(-100, 100, 20)],
                    Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                    TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero], Indices = [0, 1, 2, 0, 2, 3]
                }]
            }]);
            placed.Add(new PlacedModel("wall", null, null, barrier, Matrix4x4.Identity, PlacedModelKind.Prop, sink.Add(barrier)));
        }
        var scene = new LoadedScene("TEST", new SceneDefinition(SceneInitFile.Parse(
            "[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,60,0}, Default", "T.SIF")), null, null, placed.Count, Placed: placed);
        var api = new Gk3SheepApi(new GameState());
        return (new SceneUpdate(scene, api, new Glances(), sink), new SceneInteraction(scene, api));
    }

    private sealed class Session : IVrSession
    {
        public VrRig Rig { get; } = new();
        public VrInput Input { get; set; } = new(Head, Head, Head, true, true, true, 0, 0, default, default, false, false);
        public TeleportArc Teleport { get; } = new();
        public VrPreferences Preferences { get; set; } = new();
        public VrPanel Panel { get; } = new();
        public bool Focused { get; set; } = true;
        public bool RoomPending => false;
        public bool Transitioning { get; set; }
        public bool ComfortBlocked { get; set; }
        public void PlacedRoom() { }
        public bool BeginFrame() => true;
        public void EnterRoom() { }
    }
    private sealed class Sink : ISyntheticInput
    {
        public int FramebufferWidth => 1280;
        public int FramebufferHeight => 720;
        public float DpiScale => 1;
        public bool ExternalPrimaryHeld { get; set; }
        public List<CameraAction> Actions { get; } = [];
        public List<EditKey> Keys { get; } = [];
        public List<PointerButton> Clicks { get; } = [];
        public void RequestClose() { }
        public void MovePointer(Vector2 position) { }
        public void Press(PointerButton button) => Clicks.Add(button);
        public void Press(EditKey key) => Keys.Add(key);
        public void Press(CameraAction action) => Actions.Add(action);
        public void Scroll(int amount) { }
        public void Type(string text) { }
    }
}

using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using GK3Reborn.Game;
using GK3Reborn.Platform;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Platform;

public sealed class GamepadTests
{
    [Fact]
    public void Pointer_clicks_form_pairs_on_both_devices()
    {
        var window = (SilkGameWindow)RuntimeHelpers.GetUninitializedObject(typeof(SilkGameWindow));
        Set("_clicked", new HashSet<PointerButton>());
        var doubles = new HashSet<PointerButton>();
        Set("_doubleClicked", doubles);
        Set("_lastClick", new Dictionary<PointerButton, (double, Vector2)>());
        Click(1);
        Assert.False(window.WasDoubleClicked(PointerButton.Primary));
        Click(1.1);
        Assert.True(window.WasDoubleClicked(PointerButton.Primary));
        doubles.Clear();
        Click(1.2);
        Assert.False(window.WasDoubleClicked(PointerButton.Primary));

        void Click(double at) => typeof(SilkGameWindow)
            .GetMethod("Click", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [PointerButton.Primary, Vector2.Zero, at]);
        void Set(string field, object value) => typeof(SilkGameWindow)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    }

    [Fact]
    public void Pointer_buttons_stay_held_between_frames_and_release()
    {
        // Exercise the production input queries without creating a native window.
        var window = (SilkGameWindow)RuntimeHelpers.GetUninitializedObject(typeof(SilkGameWindow));
        var held = new HashSet<GamepadButton> { GamepadButton.RightTrigger };
        Set("_padHeld", held);
        Set("_clicked", new HashSet<PointerButton>());
        window.Bindings = InputBindings.Default.With(PointerButton.Primary, GamepadButton.RightTrigger);
        Assert.True(window.IsHeld(PointerButton.Primary));
        Assert.True(window.IsDragging);
        held.Clear();
        Assert.False(window.IsHeld(PointerButton.Primary));
        Assert.False(window.IsDragging);

        void Set(string field, object value) => typeof(SilkGameWindow)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    }

    [Fact]
    public void First_person_sticks_walk_and_look_and_free_cursor_stops_stick_walking()
    {
        var input = new PadInput { Sticks = new(new(0, -1), new(1, -1), 0, 0) };
        Assert.Equal(new Vector2(0, 1), Push(input));
        Vector2 look = Look(input, new Settings(), 0.1f);
        Assert.True(look.X > 0 && look.Y > 0);
        Assert.Equal(new Vector2(look.X, -look.Y), Look(input, new Settings { InvertLook = true }, 0.1f));
        Assert.Equal(look * 2, Look(input, new Settings(), 0.2f));
        input.FreeCursor = true;
        Assert.Equal(Vector2.Zero, Push(input));
    }

    [Fact]
    public void Camera_sticks_respect_pointer_mode_analog_speed_and_dead_zone()
    {
        var input = new PadInput { Sticks = new(new(0, -0.59f), Vector2.Zero, 0, 0) };
        var camera = new FreeCamera { Speed = 100 };
        camera.Update(input, 1);
        Assert.Equal(50, camera.Position.Z, 0.001f);
        input.PointerSpeed = 1200;
        camera.Update(input, 1);
        Assert.Equal(50, camera.Position.Z, 0.001f);
        input.Sticks = new(Vector2.Zero, new(0.1f, 0.1f), 0, 0);
        camera.Update(input, 1);
        Assert.Equal(Vector2.Zero, camera.Aim);
        input.Sticks = new(Vector2.Zero, new(1, -1), 0, 0);
        camera.Update(input, 0.1f);
        Assert.True(camera.Aim.X > 0 && camera.Aim.Y > 0);
    }

    private static Vector2 Push(IGameInput input) => (Vector2)typeof(Application)
        .GetMethod("Pushing", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [input])!;

    private static Vector2 Look(IGameInput input, Settings settings, float seconds) => (Vector2)typeof(Application)
        .GetMethod("Looking", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IGameInput), typeof(Settings), typeof(float)])!
        .Invoke(null, [input, settings, seconds])!;

    private sealed class PadInput : IGameInput
    {
        public GamepadSticks Sticks { get; set; }
        public float PointerSpeed { get; set; }
        public bool FreeCursor { get; set; }
        public bool IsHeld(CameraAction action) => FreeCursor && action == CameraAction.FreeCursor;
        public bool WasPressed(CameraAction action) => false;
        public Vector2 PointerDelta => Vector2.Zero;
        public Vector2 PointerPosition => Vector2.Zero;
        public int ScrollDelta => 0;
        public bool WasClicked(PointerButton button) => false;
        public bool WasDoubleClicked(PointerButton button) => false;
        public bool IsDragging => false;
        public bool IsHeld(PointerButton button) => false;
        public string Typed => string.Empty;
        public bool WasPressed(EditKey key) => false;
        public InputBindings Bindings { get; set; } = InputBindings.Default;
        public void EndFrame() { }
        public void Forget() { }
    }
}

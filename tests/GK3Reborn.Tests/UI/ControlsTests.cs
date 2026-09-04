using GK3Reborn.Game;
using GK3Reborn.Platform;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests that a key can be rebound, that the binding survives being written down, and that
/// a gamepad's buttons can be given jobs.
/// </summary>
/// <remarks>
/// The bindings used to be a static table inside the windowing backend, which is the right
/// place for a decision nobody can change and the wrong place for one everybody wants to.
/// What is checked here is the two things that make a rebinding feature worth having: that
/// the new key actually takes effect, and that the old one stops working.
/// </remarks>
public sealed class ControlsTests
{
    private static FrontEnd Controls()
    {
        var front = new FrontEnd(new Settings());
        front.Show(FrontEndPage.Controls);

        return front;
    }

    private static MenuItem Row(FrontEnd front, string id) =>
        front.Items.First(i => i.Id == id);

    [Fact]
    public void Every_action_starts_on_the_key_it_always_answered_to()
    {
        InputBindings bound = InputBindings.Default;

        Assert.Equal([InputKey.I], bound.Keys(CameraAction.Inventory));
        Assert.Equal([InputKey.F5], bound.Keys(CameraAction.QuickSave));
        Assert.Equal([InputKey.W, InputKey.Up], bound.Keys(CameraAction.Forward));

        // Two ways of saying the same thing, which the defaults have always offered and
        // which rebinding must not quietly take away from everybody who never rebinds.
        Assert.Equal("W, Up", bound.Describe(CameraAction.Forward));
        Assert.True(bound.Untouched);
    }

    [Fact]
    public void Choosing_a_key_row_waits_for_a_key_and_then_binds_it()
    {
        FrontEnd front = Controls();

        Assert.False(front.Listening);
        Assert.Equal("I", Row(front, "key:Inventory").Value);

        front.Choose(new MenuAction("key:Inventory"));

        Assert.True(front.Listening);
        Assert.Equal("Press a key…", Row(front, "key:Inventory").Value);

        Assert.True(front.Captured(InputKey.B, GamepadButton.None));

        Assert.False(front.Listening);
        Assert.Equal("B", Row(front, "key:Inventory").Value);
        Assert.Equal([InputKey.B], front.Bindings.Keys(CameraAction.Inventory));
    }

    [Fact]
    public void A_key_given_to_one_action_is_taken_from_whatever_had_it()
    {
        // Two actions on one key is not a state the player can see or get out of: both
        // would fire, and the page would show the key twice with nothing to say which won.
        FrontEnd front = Controls();

        front.Choose(new MenuAction("key:Journal"));
        front.Captured(InputKey.I, GamepadButton.None);

        Assert.Equal([InputKey.I], front.Bindings.Keys(CameraAction.Journal));
        Assert.Empty(front.Bindings.Keys(CameraAction.Inventory));
        Assert.Equal("—", Row(front, "key:Inventory").Value);
    }

    [Fact]
    public void Escape_leaves_a_binding_alone_and_backspace_clears_it()
    {
        FrontEnd front = Controls();

        front.Choose(new MenuAction("key:Inventory"));
        front.Captured(InputKey.Escape, GamepadButton.None);

        Assert.False(front.Listening);
        Assert.Equal([InputKey.I], front.Bindings.Keys(CameraAction.Inventory));

        front.Choose(new MenuAction("key:Inventory"));
        front.Captured(InputKey.None, GamepadButton.None, clear: true);

        Assert.False(front.Listening);
        Assert.Empty(front.Bindings.Keys(CameraAction.Inventory));
    }

    [Fact]
    public void A_key_row_answered_with_a_pad_button_binds_the_pad_button()
    {
        // The row is a suggestion about which device is likelier, not a rule. Refusing the
        // press would be the page telling the player they had pressed the wrong kind of
        // thing, which is never true.
        FrontEnd front = Controls();

        front.Choose(new MenuAction("key:Journal"));
        front.Captured(InputKey.None, GamepadButton.North);

        Assert.Equal(GamepadButton.North, front.Bindings.Button(CameraAction.Journal));

        // And it was taken from the inventory, which had it.
        Assert.Equal(GamepadButton.None, front.Bindings.Button(CameraAction.Inventory));
    }

    [Fact]
    public void The_pointer_is_on_the_face_buttons_and_can_be_moved()
    {
        InputBindings bound = InputBindings.Default;

        Assert.Equal(GamepadButton.South, bound.Button(PointerButton.Primary));
        Assert.Equal(GamepadButton.East, bound.Button(PointerButton.Secondary));
        Assert.Equal(GamepadButton.West, bound.Button(PointerButton.Middle));

        FrontEnd front = Controls();

        front.Choose(new MenuAction("ptr:Primary"));
        Assert.True(front.Listening);

        front.Captured(InputKey.None, GamepadButton.RightTrigger);

        Assert.Equal(
            GamepadButton.RightTrigger, front.Bindings.Button(PointerButton.Primary));
    }

    [Fact]
    public void Only_the_changes_are_written_down()
    {
        // A file that listed every binding would pin a player to whatever the defaults were
        // on the day they first opened this page.
        FrontEnd front = Controls();

        Assert.Null(front.Settings.Bindings);

        front.Choose(new MenuAction("key:Inventory"));
        front.Captured(InputKey.B, GamepadButton.None);

        StoredBindings? stored = front.Settings.Bindings;

        Assert.NotNull(stored);
        Assert.Equal(["Inventory"], stored.Keys.Keys);
        Assert.Equal("B", stored.Keys["Inventory"]);

        // And it comes back the same way round.
        InputBindings back = InputBindings.Restore(stored);

        Assert.Equal([InputKey.B], back.Keys(CameraAction.Inventory));
        Assert.Equal([InputKey.F5], back.Keys(CameraAction.QuickSave));
    }

    [Fact]
    public void A_binding_naming_a_key_this_version_has_never_heard_of_costs_that_binding()
    {
        // A settings file is a text file somebody may edit, and may have been written by a
        // later version of the game.
        var stored = new StoredBindings(
            new Dictionary<string, string>
            {
                ["Inventory"] = "Nonsense",
                ["NotAnAction"] = "B",
                ["Journal"] = "K",
            },
            new Dictionary<string, string> { ["Journal"] = "Sideways" },
            []);

        InputBindings bound = InputBindings.Restore(stored);

        Assert.Empty(bound.Keys(CameraAction.Inventory));
        Assert.Equal([InputKey.K], bound.Keys(CameraAction.Journal));
        Assert.Equal(GamepadButton.None, bound.Button(CameraAction.Journal));
    }

    [Fact]
    public void Putting_every_control_back_undoes_the_lot()
    {
        FrontEnd front = Controls();

        front.Choose(new MenuAction("key:Inventory"));
        front.Captured(InputKey.B, GamepadButton.None);

        Assert.NotNull(front.Settings.Bindings);

        front.Choose(new MenuAction("bindreset"));

        Assert.Null(front.Settings.Bindings);
        Assert.Equal([InputKey.I], front.Bindings.Keys(CameraAction.Inventory));
    }

    [Fact]
    public void The_settings_survive_being_written_and_read_back()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

        try
        {
            var front = new FrontEnd(new Settings()) { StoredAt = path };
            front.Show(FrontEndPage.Controls);

            front.Choose(new MenuAction("key:QuickSave"));
            front.Captured(InputKey.F7, GamepadButton.None);

            front.Choose(new MenuAction("padcursor"));
            front.Choose(new MenuAction("realistic"));
            front.Choose(new MenuAction("floorreflect"));

            Assert.True(front.Commit());

            Settings back = Settings.Load(path);

            Assert.Equal([InputKey.F7], InputBindings.Restore(back.Bindings).Keys(CameraAction.QuickSave));
            Assert.False(back.GamepadCursor);
            Assert.True(back.RealisticLighting);
            Assert.False(back.FloorReflections);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_gamepad_that_is_not_there_is_said_rather_than_left_to_be_guessed_at()
    {
        FrontEnd front = Controls();

        front.HasGamepad = false;

        Assert.Contains(
            front.Items,
            i => i.Kind == MenuItemKind.Label && i.Text.Contains("No gamepad"));

        front.HasGamepad = true;

        Assert.DoesNotContain(
            front.Items,
            i => i.Kind == MenuItemKind.Label && i.Text.Contains("No gamepad"));
    }

    [Fact]
    public void The_cursor_speed_is_clamped_to_something_a_hand_can_follow()
    {
        Settings mad = new Settings { GamepadCursorSpeed = float.NaN }.Sane();

        Assert.Equal(1200f, mad.GamepadCursorSpeed);

        Settings fast = new Settings { GamepadCursorSpeed = 100_000f }.Sane();

        Assert.Equal(Settings.FastestCursor, fast.GamepadCursorSpeed);

        Settings reflective = new Settings { Reflectivity = -3f }.Sane();

        Assert.Equal(0f, reflective.Reflectivity);
    }
}

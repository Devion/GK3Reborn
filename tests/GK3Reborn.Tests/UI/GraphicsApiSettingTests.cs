using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// The row that chooses which graphics API the game draws through.
/// </summary>
public sealed class GraphicsApiSettingTests
{
    private static MenuItem? Row(FrontEnd front, string id) =>
        front.Items.Cast<MenuItem?>().FirstOrDefault(i => i!.Value.Id == id);

    private static FrontEnd On(Settings? settings = null, RenderBackend running = default)
    {
        var front = new FrontEnd(settings ?? new Settings()) { RunningBackend = running };
        front.Show(FrontEndPage.Display);

        return front;
    }

    /// <summary>
    /// The row is there on Windows and nowhere else.
    /// </summary>
    [Fact]
    public void The_row_is_offered_only_where_there_is_a_choice()
    {
        MenuItem? row = Row(On(), "backend");

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(row);
            Assert.True(row!.Value.Selectable);
        }
        else
        {
            Assert.Null(row);
        }
    }

    [Fact]
    public void Stepping_it_reaches_the_setting()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "no second backend on this machine");

        FrontEnd front = On();
        RenderBackend before = front.Settings.Backend;

        front.Choose(new MenuAction("backend", 1));

        Assert.NotEqual(before, front.Settings.Backend);
    }

    /// <summary>Automatic says what it came to, because the word alone says nothing.</summary>
    [Fact]
    public void Automatic_says_which_one_it_resolved_to()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "no second backend on this machine");

        MenuItem? row = Row(
            On(new Settings { Backend = RenderBackend.Automatic },
               running: RenderBackend.Direct3D12),
            "backend");

        Assert.Equal("Automatic (Direct3D 12)", row!.Value.Value);
    }

    /// <summary>
    /// A choice that is not the one drawing says when it will be.
    /// </summary>
    [Fact]
    public void A_choice_that_is_not_running_yet_says_so()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "no second backend on this machine");

        MenuItem? waiting = Row(
            On(new Settings { Backend = RenderBackend.Vulkan },
               running: RenderBackend.Direct3D12),
            "backend");

        Assert.Equal("Vulkan, next start", waiting!.Value.Value);

        MenuItem? running = Row(
            On(new Settings { Backend = RenderBackend.Vulkan },
               running: RenderBackend.Vulkan),
            "backend");

        Assert.Equal("Vulkan", running!.Value.Value);
    }

    /// <summary>
    /// A front end nothing has told claims nothing about what is drawing.
    /// </summary>
    [Fact]
    public void Before_anything_says_what_is_running_the_row_claims_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "no second backend on this machine");

        MenuItem? row = Row(
            On(new Settings { Backend = RenderBackend.Vulkan }, running: RenderBackend.Automatic),
            "backend");

        Assert.Equal("Vulkan", row!.Value.Value);
    }

    /// <summary>
    /// A settings file that names Direct3D on a machine that has none is not a failure.
    /// </summary>
    [Fact]
    public void A_backend_this_machine_cannot_run_falls_back_to_automatic()
    {
        Settings sane = new Settings { Backend = RenderBackend.Direct3D12 }.Sane();

        Assert.Equal(
            OperatingSystem.IsWindows() ? RenderBackend.Direct3D12 : RenderBackend.Automatic,
            sane.Backend);
    }

    /// <summary>Windows draws through Direct3D unless somebody says otherwise.</summary>
    [Fact]
    public void Windows_chooses_direct3d_and_everything_else_chooses_vulkan()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? RenderBackend.Direct3D12 : RenderBackend.Vulkan,
            RenderBackends.Choose());

        Assert.Equal(RenderBackends.Choose(), RenderBackends.Resolve(RenderBackend.Automatic));
    }
}

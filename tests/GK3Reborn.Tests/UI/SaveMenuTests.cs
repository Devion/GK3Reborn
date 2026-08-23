using GK3Reborn.Game;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the pages that save and restore a game.
/// </summary>
/// <remarks>
/// The front end owns no store on purpose: it turns rows into a choice, and reading or
/// writing a game is the host's business. So what these check is the choosing — which slots
/// are offered, which are refusable, and that the one the player pointed at travels back.
/// </remarks>
public sealed class SaveMenuTests
{
    private static FrontEnd Paused() => new(new Settings(), inGame: true) { Saves = Written() };

    private static IReadOnlyList<SaveSlot> Written() =>
    [
        new(SaveStore.QuickSlot, "Quick save", "Hotel Lobby", DateTimeOffset.UnixEpoch, 2),
        new("03", "Before the cat", "Rennes-le-Chateau", DateTimeOffset.UnixEpoch, 2),
    ];

    private static MenuAction Chose(string id) => new(id);

    [Fact]
    public void The_pause_menu_offers_saving_and_restoring()
    {
        Assert.Contains(Paused().Items, i => i.Id == "save");
        Assert.Contains(Paused().Items, i => i.Id == "load");
    }

    /// <summary>Every numbered slot is offered when saving, free or not.</summary>
    /// <remarks>
    /// A save menu that shows only what has already been saved gives a new player nothing to
    /// aim at.
    /// </remarks>
    [Fact]
    public void Saving_offers_every_numbered_slot()
    {
        FrontEnd front = Paused();
        front.Choose(Chose("save"));

        Assert.Equal(FrontEndPage.Save, front.Page);
        Assert.Equal(
            SaveStore.NumberedSlots,
            front.Items.Count(i => i.Id.StartsWith("slot:", StringComparison.Ordinal)));

        Assert.All(
            front.Items.Where(i => i.Id.StartsWith("slot:", StringComparison.Ordinal)),
            i => Assert.True(i.Enabled));
    }

    /// <summary>The game's own two slots can be restored from and not written to by hand.</summary>
    /// <remarks>
    /// They belong to the game. A player who overwrites their own autosave has been given a
    /// way to lose something they did not know they had.
    /// </remarks>
    [Fact]
    public void The_games_own_slots_are_offered_for_restoring_only()
    {
        FrontEnd saving = Paused();
        saving.Choose(Chose("save"));

        Assert.DoesNotContain(saving.Items, i => i.Id == "slot:" + SaveStore.QuickSlot);

        FrontEnd loading = Paused();
        loading.Choose(Chose("load"));

        Assert.Contains(loading.Items, i => i.Id == "slot:" + SaveStore.QuickSlot);
    }

    /// <summary>A slot with nothing in it cannot be restored from.</summary>
    [Fact]
    public void An_empty_slot_cannot_be_restored()
    {
        FrontEnd front = Paused();
        front.Choose(Chose("load"));

        Assert.False(front.Items.Single(i => i.Id == "slot:07").Enabled);
        Assert.True(front.Items.Single(i => i.Id == "slot:03").Enabled);
    }

    /// <summary>A slot reads as what it was called and when it was written.</summary>
    [Fact]
    public void A_slot_says_what_it_holds()
    {
        FrontEnd front = Paused();
        front.Choose(Chose("load"));

        string filled = front.Items.Single(i => i.Id == "slot:03").Text;

        Assert.Contains("Slot 3", filled, StringComparison.Ordinal);
        Assert.Contains("Before the cat", filled, StringComparison.Ordinal);

        Assert.Contains(
            "empty",
            front.Items.Single(i => i.Id == "slot:07").Text,
            StringComparison.Ordinal);
    }

    /// <summary>Choosing a slot hands the host the slot and what to do with it.</summary>
    [Fact]
    public void Choosing_a_slot_says_which_one_and_which_way()
    {
        FrontEnd saving = Paused();
        saving.Choose(Chose("save"));

        Assert.Equal(FrontEndOutcome.Save, saving.Choose(Chose("slot:05")));
        Assert.Equal("05", saving.Slot);

        FrontEnd loading = Paused();
        loading.Choose(Chose("load"));

        Assert.Equal(FrontEndOutcome.Load, loading.Choose(Chose("slot:03")));
        Assert.Equal("03", loading.Slot);
    }

    /// <summary>Back from a slot list returns to the menu it was opened from.</summary>
    /// <remarks>
    /// It used to read "anything that is not Options is a child of Options", which was true
    /// while the only pages below the top were the three kinds of setting — and sent Back
    /// from the save slots to the settings screen the moment saving was added.
    /// </remarks>
    [Theory]
    [InlineData("save")]
    [InlineData("load")]
    public void Back_from_the_slots_returns_to_the_menu(string page)
    {
        FrontEnd front = Paused();
        front.Choose(Chose(page));

        Assert.True(front.Back());
        Assert.Equal(FrontEndPage.Main, front.Page);
    }

    /// <summary>And a settings page still goes back to the settings.</summary>
    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    [InlineData("gameplay")]
    public void Back_from_a_settings_page_returns_to_the_settings(string page)
    {
        FrontEnd front = Paused();
        front.Choose(Chose("options"));
        front.Choose(Chose(page));

        Assert.True(front.Back());
        Assert.Equal(FrontEndPage.Options, front.Page);

        Assert.True(front.Back());
        Assert.Equal(FrontEndPage.Main, front.Page);

        // And the top of the menu is where it stops.
        Assert.False(front.Back());
    }
}

using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actions;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Platform;
using GK3Reborn.UI.Interaction;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Which pointer is shown over what, which is what a click would do to it.
/// </summary>
public sealed class PointerChoiceTests
{
    /// <summary>The verbs, with the cursor column the shipped file gives nine of them.</summary>
    private static readonly VerbLibrary Verbs = VerbLibrary.Parse("""
        [VERBS]
        LOOK, up=i_look_std, type=Normal
        OPEN, up=i_open_std, type=Normal
        TALK, up=i_talk_std, type=Normal
        EXIT, cursor=c_exit_forward, up=i_arrowforward_std, type=Normal
        GO_UP, cursor=c_exit_up, up=i_arrowup_std, type=Normal
        GRAB, cursor=c_grab, up=i_grab_std, type=Normal
        T_ABBE, up=i_abbe_std, type=Topic
        Z_CHAT, up=i_chat_std, type=Chat
        FINGERPRINT_KIT, up=i_fingerkit_std, type=Inventory
        """);

    private static Hover Over(string noun, string? modelVerb, params string[] verbs) =>
        new(
            new ScenePick(noun, noun, modelVerb, 1f, Vector3.Zero, PickKind.Geometry),
            [.. verbs.Select(Offer)]);

    private static AvailableAction Offer(string verb) => new()
    {
        ActionId = "X:" + verb,
        NvcProvenance = "test",
        LocalizedVerb = verb,
        IconSemantic = "action",
        Category = verb == "LOOK"
            ? ActionCategory.Inspect
            : verb == "FINGERPRINT_KIT" ? ActionCategory.Item : ActionCategory.Primary,
        Enabled = true,
    };

    private static PointerShape Shape(Hover hover) => PointerChoice.For(hover, null, false, Verbs);

    [Fact]
    public void Nothing_under_the_pointer_is_the_arrow()
    {
        Assert.Equal(PointerShape.Default, Shape(Hover.Nothing));

        // A thing with a noun and nothing to do to it is the arrow too: the bar would say
        // "nothing to do with it here", and a hand would promise otherwise.
        Assert.Equal(PointerShape.Default, Shape(Over("WALL", null)));
    }

    [Fact]
    public void The_default_verb_decides_the_shape()
    {
        Assert.Equal(PointerShape.Look, Shape(Over("PAINTING", null, "LOOK")));
        Assert.Equal(PointerShape.Interact, Shape(Over("CABINET", null, "OPEN")));
        Assert.Equal(PointerShape.Interact, Shape(Over("BOOK", null, "GRAB")));
        Assert.Equal(PointerShape.Talk, Shape(Over("MOSELY", null, "TALK", "LOOK")));
        Assert.Equal(PointerShape.Exit, Shape(Over("DOOR", null, "EXIT", "LOOK")));
    }

    [Fact]
    public void A_look_offered_first_is_a_glass_even_when_more_can_be_done()
    {
        // LOOK sorts first and a plain click performs it, so that is what the pointer says.
        Assert.Equal(PointerShape.Look, Shape(Over("CABINET", null, "LOOK", "OPEN")));
    }

    [Fact]
    public void The_model_can_name_its_own_verb_and_that_wins()
    {
        Assert.Equal(PointerShape.Exit, Shape(Over("STAIRS", "GO_UP", "LOOK")));
        Assert.Equal(PointerShape.Talk, Shape(Over("ABBE", "TALK", "LOOK")));
    }

    [Fact]
    public void A_numbered_exit_is_the_way_out_whatever_its_verb_is_called()
    {
        Assert.Equal(PointerShape.Exit, Shape(Over("EXIT3", null, "LOOK")));
        Assert.Equal(PointerShape.Exit, Shape(Over("EXIT", null, "OPEN")));
    }

    [Fact]
    public void Topics_and_small_talk_are_talking()
    {
        Assert.Equal(PointerShape.Talk, Shape(Over("ABBE", null, "T_ABBE")));
        Assert.Equal(PointerShape.Talk, Shape(Over("ABBE", null, "Z_CHAT")));

        // Without the file to say which verbs are topics, their name says.
        Assert.Equal(PointerShape.Talk, PointerChoice.For(Over("ABBE", null, "T_ABBE"), null, false, null));
        Assert.Equal(PointerShape.Exit, PointerChoice.For(Over("HATCH", null, "GO_UP"), null, false, null));
    }

    [Fact]
    public void Something_out_of_the_bag_is_never_what_a_click_does()
    {
        // The kit is a verb on every noun from the moment it is picked up, and it is never
        // the default; a noun that answers to nothing else is the arrow.
        Assert.Equal(PointerShape.Default, Shape(Over("WALL", null, "FINGERPRINT_KIT")));
        Assert.Equal(PointerShape.Interact, Shape(Over("SAFE", null, "FINGERPRINT_KIT", "OPEN")));
    }

    [Fact]
    public void A_room_that_claims_the_click_gets_a_hand_and_an_open_bar_gets_the_arrow()
    {
        Hover blade = Over("PENDULUM", null, "LOOK");

        // The reference puts a grab cursor up for the two seconds the blade can be caught.
        Assert.Equal(PointerShape.Interact, PointerChoice.For(blade, "GRAB", false, Verbs));

        // A claim with no word to it is still a claim: the click is taken.
        Assert.Equal(PointerShape.Interact, PointerChoice.For(blade, string.Empty, false, Verbs));

        // The verb bar takes every click while it is up, wherever the pointer is.
        Assert.Equal(PointerShape.Default, PointerChoice.For(Over("DOOR", null, "EXIT"), null, true, Verbs));
    }

    [Fact]
    public void The_verb_file_says_which_verbs_leave_the_room()
    {
        Assert.Equal("c_exit_up", Verbs.CursorOf("GO_UP"));
        Assert.Equal("c_grab", Verbs.CursorOf("grab"));
        Assert.Null(Verbs.CursorOf("LOOK"));
        Assert.Null(Verbs.CursorOf(null));

        Assert.True(Verbs.LeadsOut("EXIT"));
        Assert.True(Verbs.LeadsOut("GO_UP"));
        Assert.False(Verbs.LeadsOut("GRAB"));
        Assert.False(Verbs.LeadsOut("OPEN"));
    }
}

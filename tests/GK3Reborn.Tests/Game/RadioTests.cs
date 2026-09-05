// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the headset Gabriel wears in the temple, and for the button that opens it.
/// </summary>
/// <remarks>
/// The action files below are the corpus's own, transcribed: the porch's four tile nouns
/// and the workroom's seven scales are the two places in the game where several nouns are
/// one radio conversation, and they are the whole reason this has a folding rule at all.
/// </remarks>
public sealed class RadioTests
{
    private static ActionResolver Resolver(GameState state, string file)
    {
        var resolver = new ActionResolver(new Gk3SheepApi(state));
        resolver.Add(NvcFile.Parse(file, "test.nvc", new DiagnosticBag()));

        return resolver;
    }

    // --- when he is wearing it ------------------------------------------------------------

    [Fact]
    public void The_headset_is_worn_for_one_timeblock_and_no_other()
    {
        // Day three, nine in the evening: the temple. It is the reference engine's own gate
        // on this button — Timeblock(3, 21) — and the reason it is a time rather than a
        // room is that the headset is on his head in all five temple rooms, including the
        // one with nothing to say.
        Assert.True(Radio.WornAt(new Timeblock(3, 9, IsAfternoon: true)));

        Assert.False(Radio.WornAt(new Timeblock(3, 9, IsAfternoon: false)));
        Assert.False(Radio.WornAt(new Timeblock(2, 9, IsAfternoon: true)));
        Assert.False(Radio.WornAt(new Timeblock(3, 12, IsAfternoon: true)));
        Assert.False(Radio.WornAt(default));
    }

    [Fact]
    public void The_timeblock_it_is_worn_in_is_the_one_the_temple_files_are_written_for()
    {
        // The action files that write RADIO rules are TE1309P, TE3309P and TE4309P, so the
        // gate and the data have to agree about what 309P is. They are derived from each
        // other nowhere, which is exactly why this is asserted.
        Assert.Equal("309P", Radio.Worn.ToString());
    }

    // --- what there is to ask -------------------------------------------------------------

    [Fact]
    public void Only_the_nouns_the_files_write_radio_rules_for_are_candidates()
    {
        ActionResolver resolver = Resolver(
            new GameState(),
            """
            BOWL_OF_ACID, LOOK, ALL, script={}
            WORDS_ABOVE_DOOR, RADIO, ALL, script={}
            ANGEL_FIGURE, LOOK, ALL, script={}
            CHOOSE_ONE_WORDS, RADIO, ALL, script={}
            """);

        Assert.Equal(["WORDS_ABOVE_DOOR", "CHOOSE_ONE_WORDS"], resolver.NounsFor("RADIO"));
        Assert.Empty(resolver.NounsFor("SMELL"));
    }

    [Fact]
    public void A_rule_whose_case_does_not_hold_is_not_a_topic()
    {
        // The list is what Grace will actually answer, not what she might. TE3's scales
        // answer once and only from the table, and a menu that offered them afterwards
        // would be a row that does nothing.
        var state = new GameState();

        ActionResolver resolver = Resolver(
            state,
            "SCALE_ON_TABLE, RADIO, 1ST_TIME, script={}");

        Assert.Equal(["SCALE_ON_TABLE"], Radio.Topics(resolver).Select(t => t.Noun));

        state.SetNounVerbCount("SCALE_ON_TABLE", "RADIO", 1);

        Assert.Empty(Radio.Topics(resolver));
    }

    [Fact]
    public void The_porchs_four_tile_nouns_are_one_topic_called_TILES()
    {
        // TE1309P, transcribed. Four nouns, one conversation, and every one of the four
        // rules ends by counting it against TILES — which is the authors saying that all
        // four of them are the tiles.
        ActionResolver resolver = Resolver(
            new GameState(),
            """
            CROSS_TILES, RADIO, ALL, script={wait StartVoiceOver("1QEGB62TI1",2);IncNounVerbCount("TILES","RADIO");}
            SKULL_TILES, RADIO, ALL, script={wait StartVoiceOver("1QEGB62TI1",2);IncNounVerbCount("TILES","RADIO");}
            SWORD_TILES, RADIO, ALL, script={wait StartVoiceOver("1QEGB62TI1",2);IncNounVerbCount("TILES","RADIO");}
            TILES, RADIO, ALL, script={wait StartVoiceOver("1QEGB62TI1",2);IncNounVerbCount("TILES","RADIO");}
            """);

        // One row, and it is the plain noun even though the file lists it last — file order
        // would have picked CROSS_TILES.
        Assert.Equal(["TILES"], Radio.Topics(resolver).Select(t => t.Noun));
    }

    [Fact]
    public void The_workrooms_seven_scale_nouns_are_one_topic_called_SCALE_ON_TABLE()
    {
        // TE3309P, and the other way round from the porch: here the canonical noun is
        // listed first, so an ordering rule that got the tiles right would have to get this
        // one wrong. Only the script naming itself is right for both.
        ActionResolver resolver = Resolver(
            new GameState(),
            """
            SCALE_ON_TABLE, RADIO, ALL, script={wait StartVoiceOver("1REGB62TV1",3); IncNounVerbCount("SCALE_ON_TABLE","RADIO");}
            SCALE_EGG, RADIO, ALL, script={wait StartVoiceOver("1REGB62TV1",3); IncNounVerbCount("SCALE_ON_TABLE","RADIO");}
            SCALE_FOR, RADIO, ALL, script={wait StartVoiceOver("1REGB62TV1",3); IncNounVerbCount("SCALE_ON_TABLE","RADIO");}
            SCALE_ZIG, RADIO, ALL, script={wait StartVoiceOver("1REGB62TV1",3); IncNounVerbCount("SCALE_ON_TABLE","RADIO");}
            """);

        Assert.Equal(["SCALE_ON_TABLE"], Radio.Topics(resolver).Select(t => t.Noun));
    }

    [Fact]
    public void Two_nouns_with_different_answers_stay_two_topics()
    {
        // The folding is by what a row would actually do. TE4's are six separate
        // conversations about six separate things and every one of them has to be offered.
        ActionResolver resolver = Resolver(
            new GameState(),
            """
            CHOOSE_MASTER_WORDS, RADIO, ALL, script={wait StartVoiceOver("1TEHD6D291",3);}
            CHOOSE_ONE_WORDS, RADIO, ALL, script={wait StartVoiceOver("1TEHC6D291",2);}
            IDENTIFY_BODY_WORDS, RADIO, ALL, script={wait StartVoiceOver("1TEHP6D291",3);}
            """);

        Assert.Equal(
            ["CHOOSE_MASTER_WORDS", "CHOOSE_ONE_WORDS", "IDENTIFY_BODY_WORDS"],
            Radio.Topics(resolver).Select(t => t.Noun));
    }

    [Fact]
    public void A_topic_is_named_by_whatever_the_room_calls_it()
    {
        // The third thing in this interface that puts a noun in front of the player, and it
        // goes through the same naming as the other two — see SceneInteraction.NameOf.
        ActionResolver resolver = Resolver(
            new GameState(),
            "BUTHANE, RADIO, ALL, script={}");

        Assert.Equal(
            ["Woman"],
            Radio.Topics(resolver, "GABRIEL", _ => "Woman").Select(t => t.Label));

        // And the noun itself where nobody offers anything better.
        Assert.Equal(["BUTHANE"], Radio.Topics(resolver).Select(t => t.Label));
    }

    [Fact]
    public void The_rooms_general_call_is_not_one_of_the_nouns()
    {
        // It is CallSheep(room, "RadioButton$") and belongs to no noun at all, which is
        // what the empty noun says. TE4's Solomon statue is only reachable through it: its
        // own rules are commented out in the file.
        var general = new RadioTopic(string.Empty, "Ask Grace");

        Assert.True(general.IsGeneral);
        Assert.False(new RadioTopic("TILES", "Tiles").IsGeneral);
    }

    // --- the button and its list ------------------------------------------------------------

    /// <summary>Where the headset ended up, found the way a click finds it.</summary>
    private static Vector2? Headset(GameHud hud)
    {
        for (int y = 0; y < 480; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                if (hud.ButtonAt(new Vector2(x, y)) == GameHud.RadioButton)
                {
                    return new Vector2(x, y);
                }
            }
        }

        return null;
    }

    [Fact]
    public void The_headset_is_drawn_only_where_it_is_worn()
    {
        var hud = new GameHud(new Overlay(Atlas()));

        hud.Build(Showing(worn: false), 640, 480);
        Assert.Null(Headset(hud));

        hud.Build(Showing(worn: true), 640, 480);
        Assert.NotNull(Headset(hud));
    }

    [Fact]
    public void The_headset_still_answers_a_click_with_nothing_to_say()
    {
        // Drawn dim rather than taken away, so that a button which comes and goes does not
        // tell the player which rooms have something in them. A dim button that swallows
        // the click instead of answering it is the failure this guards.
        var hud = new GameHud(new Overlay(Atlas()));

        hud.Build(Showing(worn: true, topics: []), 640, 480);

        Assert.NotNull(Headset(hud));
        Assert.Equal(0, hud.TopicCount);
    }

    [Fact]
    public void The_list_is_only_laid_out_while_it_is_open()
    {
        var hud = new GameHud(new Overlay(Atlas()));

        hud.Build(Showing(worn: true, open: false), 640, 480);
        Assert.Equal(0, hud.TopicCount);

        hud.Build(Showing(worn: true, open: true), 640, 480);
        Assert.Equal(2, hud.TopicCount);
    }

    [Fact]
    public void Every_row_of_the_list_can_be_clicked_and_says_which_noun_it_is()
    {
        // The same layout run twice: what was drawn and what answers a click come from one
        // pass, which is what keeps the row the player sees and the row they hit together.
        var hud = new GameHud(new Overlay(Atlas()));

        hud.Build(Showing(worn: true, open: true), 640, 480);

        List<int> hit = [];

        for (int y = 0; y < 480; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                int row = hud.TopicAt(new Vector2(x, y));

                if (row >= 0 && !hit.Contains(row))
                {
                    hit.Add(row);
                }
            }
        }

        Assert.Equal([0, 1], hit);
    }

    [Fact]
    public void The_list_swallows_a_click_so_it_does_not_reach_the_floor_behind_it()
    {
        // Everything else in this interface does; a list that did not would send Gabriel
        // walking across the room every time the player asked Grace a question.
        var hud = new GameHud(new Overlay(Atlas()));

        hud.Build(Showing(worn: true, open: true), 640, 480);

        int row = -1;

        for (int y = 0; y < 480 && row < 0; y++)
        {
            for (int x = 0; x < 200 && row < 0; x++)
            {
                row = hud.TopicAt(new Vector2(x, y));

                if (row >= 0)
                {
                    Assert.True(hud.OverInterface(new Vector2(x, y)));
                }
            }
        }

        Assert.True(row >= 0, "no row was laid out to test");
    }

    [Fact]
    public void The_headset_hangs_below_the_bar_rather_than_inside_it()
    {
        // It was in the bar first, at the height of a row beside the room's name, and was
        // reported as too easy to miss. Under the bar and half again its height: a control
        // the player has to discover cannot be the smallest thing on the screen.
        var hud = new GameHud(new Overlay(Atlas()));

        hud.Build(Showing(worn: true), 640, 480);

        float bar = hud.Overlay.LineHeight + (10f * hud.Scale);

        // Nothing along the bar's own row answers to the headset...
        for (int x = 0; x < 640; x++)
        {
            Assert.NotEqual(GameHud.RadioButton, hud.ButtonAt(new Vector2(x, bar / 2)));
        }

        // ...and it is bigger than the bar it hangs under.
        int tall = 0;

        for (int y = 0; y < 480; y++)
        {
            if (hud.ButtonAt(new Vector2(20, y)) == GameHud.RadioButton)
            {
                Assert.True(y > bar, $"the headset is drawn at {y}, inside the bar");
                tall++;
            }
        }

        Assert.True(tall > bar, $"the headset is {tall} tall against a bar of {bar}");
    }

    [Fact]
    public void The_label_under_the_pointer_is_not_drawn_over_the_list()
    {
        // It follows the pointer, the pointer is on the list, and what it would name is
        // whatever is behind the list. The verb menu suppresses it for the same reason.
        var hud = new GameHud(new Overlay(Atlas()));

        HudState pointing = Showing(worn: true, open: true) with
        {
            Noun = "Doorway In",
            Verb = "Look",
            At = new Vector2(20, 140),
        };

        hud.Build(pointing, 640, 480);
        int withList = hud.Overlay.Quads.Count;

        hud.Build(pointing with { RadioOpen = false }, 640, 480);

        Assert.True(
            hud.Overlay.Quads.Count > 0,
            "the label is not drawn with the list closed either");

        // The list is the larger of the two, so this is not a size comparison: it is that
        // closing the list brings the label back.
        Assert.NotEqual(withList, hud.Overlay.Quads.Count);
    }

    private static HudState Showing(
        bool worn, IReadOnlyList<RadioTopic>? topics = null, bool open = false) =>
        new(
            null,
            [],
            null,
            Vector2.Zero,
            MenuOpen: false,
            0,
            Vector2.Zero,
            null,
            null,
            [],
            null,
            InventoryOpen: false,
            "Temple Porch",
            RadioWorn: worn,
            Radio: topics ?? [new RadioTopic(string.Empty, "Ask Grace"), new RadioTopic("TILES", "Tiles")],
            RadioOpen: open);

    /// <summary>A sheet with enough letters on it to lay a menu out against.</summary>
    private static OverlayAtlas Atlas()
    {
        var image = new DecodedImage(
            64, 16, [.. Enumerable.Repeat<byte>(255, 64 * 16 * 4)], HasAlpha: false, "sheet");

        return OverlayAtlas.Build(
            FontFile.Parse("Font=ABCDEFGH\n", image, "TEST", new DiagnosticBag()));
    }
}

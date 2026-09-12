using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the fingerprint kit: the brush, the powder, the tape and the cloth.
/// </summary>
public sealed class FingerprintDustingTests
{
    private static readonly ScoreEvents Scores = ScoreEvents.Open();

    private static GameState Sneaking() => new()
    {
        Ego = "GABRIEL",
        Timeblock = new Timeblock(2, 10, IsAfternoon: false),
        Location = "R29",
    };

    /// <summary>Works the brush over a print until it is out, the way a player would.</summary>
    private static DustingStep Work(FingerprintDusting kit, int print, int frames = 200)
    {
        DustingStep last = DustingStep.None;

        for (int i = 0; i < frames && !kit.Prints[print].Ready; i++)
        {
            last = kit.Brushed(print, 40f, 1f / 60f);
        }

        return last;
    }

    [Fact]
    public void Nothing_comes_out_from_under_a_brush_with_no_powder_on_it()
    {
        var kit = new FingerprintDusting("GUN_IN_CASE", Sneaking());

        kit.TouchBrush();
        Assert.Equal(InHand.Brush, kit.Holding);

        for (int i = 0; i < 200; i++)
        {
            kit.Brushed(0, 40f, 1f / 60f);
        }

        Assert.Equal(0f, kit.Prints[0].Shown);

        kit.TouchDust();
        Assert.Equal(InHand.DustedBrush, kit.Holding);

        Work(kit, 0);
        Assert.True(kit.Prints[0].Ready);
    }

    [Fact]
    public void A_print_comes_out_only_where_the_brush_actually_is()
    {
        var kit = new FingerprintDusting("BLOODLINE_MANUSCRIPT", new GameState
        {
            Ego = "GRACE",
            Timeblock = new Timeblock(3, 12, IsAfternoon: true),
        });

        Assert.Equal(3, kit.Prints.Count);

        kit.TouchBrush();
        kit.TouchDust();
        Work(kit, 1);

        Assert.True(kit.Prints[1].Ready);
        Assert.Equal(0f, kit.Prints[0].Shown);
        Assert.Equal(0f, kit.Prints[2].Shown);
    }

    [Fact]
    public void Bringing_a_print_right_out_is_what_makes_Gabriel_remark_on_it()
    {
        var kit = new FingerprintDusting("GUN_IN_CASE", Sneaking());

        kit.TouchBrush();
        kit.TouchDust();

        // Half-way out is still nothing said.
        DustingStep half = kit.Brushed(0, 40f, 1f / 60f);

        Assert.False(half.Anything);
        Assert.True(kit.Prints[0].Shown > 0f);
        Assert.False(kit.Prints[0].Ready);

        Assert.Equal("0J8ES05291", Work(kit, 0).Say);
    }

    /// <summary>
    /// <b>The kit must not give the answer away.</b> A surface with nothing on it says so
    /// only once the brush has been over enough of it, which is the difference between
    /// dusting for prints and being told whether there are any.
    /// </summary>
    [Fact]
    public void A_bare_surface_says_nothing_until_the_brush_has_been_over_it()
    {
        var kit = new FingerprintDusting("HBHG_BOOK", new GameState
        {
            Ego = "GRACE",
            Timeblock = new Timeblock(2, 7, IsAfternoon: false),
        });

        Assert.Empty(kit.Prints);

        kit.TouchBrush();
        kit.TouchDust();

        float over = 0f;
        DustingStep step = DustingStep.None;

        while (!step.Anything && over < FingerprintDusting.SureItIsBare * 4)
        {
            step = kit.Brushed(-1, 20f, 1f / 60f);
            over += 20f;
        }

        Assert.Equal("10LCQ59291", step.Say);
        Assert.True(step.Close);
        Assert.True(over >= FingerprintDusting.SureItIsBare);

        // And it is said once, not at every stroke after it.
        Assert.False(kit.Brushed(-1, 20f, 1f / 60f).Anything);
    }

    [Fact]
    public void Tape_only_sticks_to_a_print_that_is_all_the_way_out()
    {
        var kit = new FingerprintDusting("GUN_IN_CASE", Sneaking());

        kit.TouchTape();
        kit.PressOn(0);

        Assert.Equal(InHand.Tape, kit.Holding);
        Assert.Equal(-1, kit.OnTape);

        // Nothing is kept by pressing the empty tape on the cloth either.
        Assert.False(kit.TouchCloth(Scores).Anything);

        kit.PutDown();
        kit.TouchBrush();
        kit.TouchDust();
        Work(kit, 0);
        kit.PutDown();

        kit.TouchTape();
        kit.PressOn(0);

        Assert.Equal(InHand.TapeWithPrint, kit.Holding);
        Assert.Equal(0, kit.OnTape);
    }

    [Fact]
    public void The_cloth_is_what_puts_a_print_into_the_story()
    {
        GameState story = Sneaking();
        var kit = new FingerprintDusting("GUN_IN_CASE", story);

        kit.TouchBrush();
        kit.TouchDust();
        Work(kit, 0);
        kit.PutDown();
        kit.TouchTape();
        kit.PressOn(0);

        Assert.False(story.GetFlag("GotGunButhanePrint"));

        DustingStep kept = kit.TouchCloth(Scores);

        Assert.True(story.GetFlag("GotGunButhanePrint"));
        Assert.Contains("BUTHANES_FINGERPRINT", story.Inventory.ItemsOf("GABRIEL"));
        Assert.Equal(
            Scores.Worth("e_210a_r29_fingerprint_kit_on_gun"), story.Score);

        // Its own line as it comes off, and the kit closes with nothing left on the gun.
        Assert.Equal("0A89N052H2", kept.Say);
        Assert.True(kept.Close);
        Assert.True(kit.Finished);
    }

    [Fact]
    public void Three_prints_are_said_in_the_order_they_are_taken()
    {
        GameState story = new()
        {
            Ego = "GRACE",
            Timeblock = new Timeblock(3, 12, IsAfternoon: true),
        };

        var kit = new FingerprintDusting("BLOODLINE_MANUSCRIPT", story);

        List<string?> said = [];

        // Taken last, middle, first: the lines still come in their own order.
        foreach (int which in new[] { 2, 1, 0 })
        {
            kit.TouchBrush();
            kit.TouchDust();
            Work(kit, which);
            kit.PutDown();
            kit.TouchTape();
            kit.PressOn(which);

            DustingStep step = kit.TouchCloth(Scores);
            said.Add(step.Say);

            Assert.Equal(which == 0, step.Close);
        }

        Assert.Equal(["1077H59292", "1077H59293", "1077H59294"], said);
        Assert.Contains("UNKNOWN_PRINT_1", story.Inventory.ItemsOf("GRACE"));
        Assert.Contains("UNKNOWN_PRINT_2", story.Inventory.ItemsOf("GRACE"));
        Assert.Contains("UNKNOWN_PRINT_3", story.Inventory.ItemsOf("GRACE"));
    }

    /// <summary>
    /// The water bottle is taken twice — by Gabriel on the third afternoon and by Grace
    /// that evening — and the two rooms read two different flags, so hers is not his.
    /// </summary>
    [Fact]
    public void Grace_sets_her_own_flag_where_she_has_one()
    {
        GameState his = new()
        {
            Ego = "GABRIEL",
            Timeblock = new Timeblock(3, 3, IsAfternoon: true),
        };

        Take(new FingerprintDusting("WATER_BOTTLE_ON_MOPED", his), his);

        Assert.True(his.GetFlag("GotWaterBottleEstellePrint"));
        Assert.False(his.GetFlag("GotWaterBottleEstellePrintGrace"));

        GameState hers = new()
        {
            Ego = "GRACE",
            Timeblock = new Timeblock(3, 6, IsAfternoon: true),
        };

        Take(new FingerprintDusting("WATER_BOTTLE_ON_MOPED", hers), hers);

        Assert.True(hers.GetFlag("GotWaterBottleEstellePrintGrace"));
        Assert.False(hers.GetFlag("GotWaterBottleEstellePrint"));
    }

    [Fact]
    public void A_print_already_in_the_bag_is_not_given_twice()
    {
        GameState story = Sneaking();
        story.SetFlag("GotGunButhanePrint");

        var kit = new FingerprintDusting("GUN_IN_CASE", story);

        // Already out, and marked as kept, so there is nothing to brush for.
        Assert.True(kit.Prints[0].Taken);
        Assert.True(kit.Prints[0].Ready);
        Assert.True(kit.Finished);

        kit.TouchTape();
        kit.PressOn(0);

        Assert.Equal("2FL8S27SG1", kit.TouchCloth(Scores).Say);
        Assert.DoesNotContain("BUTHANES_FINGERPRINT", story.Inventory.ItemsOf("GABRIEL"));
    }

    /// <summary>
    /// Whose print is on a lobby glass is not in the kit's table — see
    /// <see cref="DirtyGlasses"/> — so taking one hands the question back to the caller
    /// rather than putting anything into the story itself.
    /// </summary>
    [Fact]
    public void A_lobby_glass_hands_the_question_back()
    {
        GameState story = new()
        {
            Ego = "GABRIEL",
            Timeblock = new Timeblock(2, 2, IsAfternoon: true),
            Location = "LBY",
        };

        var kit = new FingerprintDusting(DirtyGlasses.WilkesGlass, story);

        kit.TouchBrush();
        kit.TouchDust();
        Work(kit, 0);
        kit.PutDown();
        kit.TouchTape();
        kit.PressOn(0);

        DustingStep step = kit.TouchCloth(Scores);

        Assert.True(step.Glass);
        Assert.True(step.Close);
        Assert.Null(step.Say);
        Assert.Empty(story.Inventory.ItemsOf("GABRIEL"));
    }

    [Fact]
    public void Putting_the_brush_down_is_what_every_other_part_of_the_box_does()
    {
        var kit = new FingerprintDusting("GUN_IN_CASE", Sneaking());

        kit.TouchBrush();
        kit.TouchDust();
        kit.TouchTape();

        Assert.Equal(InHand.Nothing, kit.Holding);

        kit.TouchBrush();
        kit.TouchDust();
        Assert.False(kit.TouchCloth(Scores).Anything);
        Assert.Equal(InHand.Nothing, kit.Holding);

        // And the tape comes off the same way, taken and put back.
        kit.TouchTape();
        Assert.Equal(InHand.Tape, kit.Holding);
        kit.TouchTape();
        Assert.Equal(InHand.Nothing, kit.Holding);
    }

    [Fact]
    public void Something_the_kit_has_never_heard_of_does_nothing_at_all()
    {
        var kit = new FingerprintDusting("A_PIECE_OF_STRING", Sneaking());

        Assert.Null(kit.Thing);
        Assert.Empty(kit.Prints);

        kit.TouchBrush();
        kit.TouchDust();

        for (int i = 0; i < 200; i++)
        {
            Assert.False(kit.Brushed(-1, 40f, 1f / 60f).Anything);
        }
    }

    private static void Take(FingerprintDusting kit, GameState story)
    {
        kit.TouchBrush();
        kit.TouchDust();
        Work(kit, 0);
        kit.PutDown();
        kit.TouchTape();
        kit.PressOn(0);
        kit.TouchCloth(Scores);

        Assert.True(kit.Finished);
        Assert.Contains("ESTELLES_FINGERPRINT", story.Inventory.ItemsOf(story.Ego));
    }
}

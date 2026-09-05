using GK3Reborn.Formats.Animation;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Sheep;
using GK3Reborn.Tests.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the two ways the game can be made easier.
/// </summary>
/// <remarks>
/// Both work by changing what the shipped scripts do rather than by editing them, which
/// means both can be wrong in the same quiet way: doing nothing, or doing it twice. The
/// moustache handed over a second time would put an item back in the bag after the player
/// had spent it, and plot armour that missed its function would leave somebody standing in
/// the temple with the puzzle reset and nothing running.
/// </remarks>
public sealed class AssistTests
{
    private static GameState At(int day, int hour, bool afternoon) =>
        new() { Timeblock = new Timeblock(day, hour, afternoon) };

    [Fact]
    public void The_moustache_is_not_handed_over_before_the_afternoon_it_belongs_to()
    {
        // Nothing in the game has anything to say about it until Day 1, 2pm: the cat, the
        // moped shop and Mosely's passport are all that afternoon's nouns.
        GameState morning = At(1, 10, afternoon: false);

        Assert.False(Assists.GiveMoustache(morning));
        Assert.False(morning.Inventory.Has(Assists.Owner, Assists.Moustache));
        Assert.False(morning.GetFlag(Assists.GaveMoustacheFlag));
    }

    [Fact]
    public void The_moustache_is_handed_over_once_and_not_again()
    {
        GameState afternoon = At(1, 2, afternoon: true);

        Assert.True(Assists.GiveMoustache(afternoon));
        Assert.True(afternoon.Inventory.Has(Assists.Owner, Assists.Moustache));

        // Called on the way into every room, so the second answer matters more than the
        // first: a player who has combined it into the cap must not find another one.
        afternoon.Inventory.Remove(Assists.Owner, Assists.Moustache);

        Assert.False(Assists.GiveMoustache(afternoon));
        Assert.False(afternoon.Inventory.Has(Assists.Owner, Assists.Moustache));
    }

    [Fact]
    public void It_is_still_given_in_the_afternoons_that_come_after_it()
    {
        // Somebody who walked past the moped shop and came back, or turned the setting on
        // in the evening. The item is dead weight by then, but it is the one the story's
        // own conditions ask about and withholding it would be the assistance not working.
        GameState later = At(2, 10, afternoon: false);

        Assert.True(Assists.GiveMoustache(later));
        Assert.True(later.Inventory.Has(Assists.Owner, Assists.Moustache));
    }

    [Theory]
    [InlineData("BLACK_MOUSTACHE")]
    [InlineData("CAP_N_MOUSTACHE")]
    [InlineData("COAT_N_MOUSTACHE")]
    [InlineData("MOSELY_DISGUISE")]
    [InlineData("MOPED_KEYS")]
    public void A_player_already_past_the_puzzle_is_not_handed_one_anyway(string carrying)
    {
        // Turning the assistance on halfway through a game played without it. Everything
        // the moustache can have become says the same thing: this is done.
        GameState afternoon = At(1, 2, afternoon: true);
        afternoon.Inventory.Add(Assists.Owner, carrying);

        Assert.False(Assists.GiveMoustache(afternoon));
        Assert.True(afternoon.GetFlag(Assists.GaveMoustacheFlag));

        // And nothing was added beside what they were carrying.
        Assert.Equal([carrying], afternoon.Inventory.ItemsOf(Assists.Owner));
    }

    [Fact]
    public void Having_been_given_it_travels_in_the_save()
    {
        // The flag rather than a field, so that saving after spending the moustache and
        // loading again does not hand over a second one.
        GameState played = At(1, 2, afternoon: true);
        Assists.GiveMoustache(played);
        played.Inventory.Remove(Assists.Owner, Assists.Moustache);

        var loaded = new GameState();
        loaded.Restore(played.Capture());

        Assert.True(loaded.GetFlag(Assists.GaveMoustacheFlag));
        Assert.False(Assists.GiveMoustache(loaded));
    }

    /// <summary>One of the temple scripts: a death, a reset and the restart after it.</summary>
    /// <param name="withRestart">Whether it declares the pair the death screen calls back into.</param>
    private static SheepScriptFile Temple(string name, bool withRestart = true) =>
        TestScripts.Build(name, builder =>
        {
            builder.Import("SetFlag", 0, 3);

            int died = builder.String("Died");
            int restarted = builder.String("Restarted");
            int resumed = builder.String("Resumed");

            void Sets(string function, int flag) =>
                builder.Function(function)
                    .Op(SheepOpcode.PushS, flag)
                    .Op(SheepOpcode.GetString)
                    .Op(SheepOpcode.PushI, 1)
                    .Op(SheepOpcode.CallSysFunctionV, 0)
                    .Op(SheepOpcode.Pop)
                    .Op(SheepOpcode.ReturnV);

            Sets("Die$", died);

            if (withRestart)
            {
                Sets("Restart$", restarted);
                Sets("PostDeath$", resumed);
            }
        });

    [Fact]
    public void Without_plot_armour_a_death_is_a_death()
    {
        var state = new GameState();
        var host = new ScriptHost(new Gk3SheepApi(state));
        host.Add(Temple("TE6.SHP"));

        host.Run("te6", "Die");

        Assert.True(state.GetFlag("Died"));
        Assert.False(state.GetFlag("Restarted"));
    }

    [Fact]
    public void With_plot_armour_the_puzzle_starts_again_instead()
    {
        // What the original's death screen does when the player chooses to try again,
        // without the screen and without the dying.
        var state = new GameState { PlotArmour = true };
        var host = new ScriptHost(new Gk3SheepApi(state));
        host.Add(Temple("TE6.SHP"));

        host.Run("te6", "Die");

        Assert.False(state.GetFlag("Died"));
        Assert.True(state.GetFlag("Restarted"));
        Assert.True(state.GetFlag("Resumed"));

        // Restart before PostDeath, or the reset undoes the thing it is meant to start.
        Assert.Equal(
            ["TE6:Die:survived", "TE6:Restart", "TE6:PostDeath"],
            host.CallStackTrace);
    }

    [Fact]
    public void Plot_armour_reaches_a_death_a_script_calls_as_well_as_one_an_action_does()
    {
        // TE4's AngelKills and TE5's fall reach it through CallSheep from inside the
        // script, and TE6309P.NVC reaches it from the action file. Both go through the
        // same door.
        var state = new GameState { PlotArmour = true };
        var host = new ScriptHost(new Gk3SheepApi(state));

        host.Add(Temple("TE5.SHP"));
        host.Add(TestScripts.Build("TE5CALLER.SHP", builder =>
        {
            builder.Import("CallSheep", 0, 3, 3);
            int script = builder.String("TE5");
            int function = builder.String("die");

            builder.Function("FallDie$")
                .Op(SheepOpcode.PushS, script)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushS, function)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushI, 2)
                .Op(SheepOpcode.CallSysFunctionV, 0)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.ReturnV);
        }));

        host.Run("TE5CALLER", "FallDie");

        Assert.False(state.GetFlag("Died"));
        Assert.True(state.GetFlag("Restarted"));
        Assert.True(state.GetFlag("Resumed"));
    }

    [Fact]
    public void A_script_with_nowhere_to_restart_is_left_alone()
    {
        // The five temple scripts all declare Restart and PostDeath. Anything else named
        // Die is not one of them, and half-running it would be worse than letting it run.
        var state = new GameState { PlotArmour = true };
        var host = new ScriptHost(new Gk3SheepApi(state));
        host.Add(Temple("SOMETHINGELSE.SHP", withRestart: false));

        host.Run("somethingelse", "Die");

        Assert.True(state.GetFlag("Died"));
    }

    /// <summary>A temple script that plays its room again after the reset.</summary>
    /// <remarks>
    /// Which is what all five do: <c>Die$</c> stops every soundtrack and <c>PostDeath$</c>
    /// starts the ones the room needs. TE6's is the demon's growl, and it is the one a
    /// player saved by plot armour heard for ever.
    /// </remarks>
    private static SheepScriptFile TempleWithMusic(string name) =>
        TestScripts.Build(name, builder =>
        {
            builder.Import("SetFlag", 0, 3);
            builder.Import("PlaySoundTrack", 0, 3);

            int resumed = builder.String("Resumed");
            int growl = builder.String("TE6Demon.STK");

            builder.Function("Die$").Op(SheepOpcode.ReturnV);
            builder.Function("Restart$").Op(SheepOpcode.ReturnV);

            builder.Function("PostDeath$")
                .Op(SheepOpcode.PushS, growl)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushI, 1)
                .Op(SheepOpcode.CallSysFunctionV, 1)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.PushS, resumed)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushI, 1)
                .Op(SheepOpcode.CallSysFunctionV, 0)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.ReturnV);
        });

    [Fact]
    public void Surviving_silences_the_room_the_way_the_death_would_have()
    {
        // Reported from TE6: saved from the demon, and its growl went on repeating with
        // nothing left able to stop it. Every Die begins with StopAllSoundTracks and every
        // PostDeath starts the room again afterwards, so skipping Die skipped the stop —
        // and a soundtrack already running is not started twice, which made PostDeath's
        // half a no-op too. The room simply never reset.
        var state = new GameState { PlotArmour = true };
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        host.Add(TempleWithMusic("TE6.SHP"));

        host.Run("te6", "Die");

        Assert.True(state.GetFlag("Resumed"));

        // The stop first, then the room started again from the top. The other way round
        // would silence the thing it had just begun.
        Assert.Equal(
            [Assists.Silence, "PlaySoundTrack"],
            api.Events.Select(e => e.Name));
    }

    [Fact]
    public void Plot_armour_is_the_players_answer_and_not_the_saves()
    {
        // Loading somebody else's game must not switch it on, and must not switch it off.
        var played = new GameState { PlotArmour = true };
        SaveGame save = played.Capture();

        var loaded = new GameState { PlotArmour = false };
        loaded.Restore(save);

        Assert.False(loaded.PlotArmour);
    }

    [Fact]
    public void Plot_armour_is_part_of_the_comparable_state()
    {
        // It changes what a script does, so two runs made with different answers to it have
        // diverged and the harness should be able to see why.
        var state = new GameState();
        string before = state.ComputeHash();

        state.PlotArmour = true;

        Assert.NotEqual(before, state.ComputeHash());
    }

    /// <summary>Both faces, as FACES.TXT lists them.</summary>
    /// <remarks>
    /// Trimmed to what matters: GA3's entry is Gabriel's with a mouth region two pixels
    /// taller, because the moustache is painted into it.
    /// </remarks>
    private const string Faces = """
        [DEFAULT]
        Blink Frequency         = 5000,12000

        [GAB]
        Forehead Offset         = 90,77
        Eyelids Offset          = 105,106
        Eyelids Alpha Channel   = gab_eyelids_alpha
        Blink Anims             = gabblink,90,gabblink2,10
        Mouth Offset            = 90,132
        Mouth Size              = 78x82

        [GA3]
        Forehead Offset         = 90,77
        Eyelids Offset          = 105,106
        Eyelids Alpha Channel   = ga3_eyelids_alpha
        Blink Anims             = ga3blink,90,ga3blink2,10
        Mouth Offset            = 90,130
        Mouth Size              = 78x84
        """;

    [Fact]
    public void The_moustached_gabriel_the_game_already_has_is_a_face_of_his_own()
    {
        // Which is the whole reason the assistance can show it: GA3 is a character in
        // FACES.TXT in his own right, so his moustache comes with a forehead, eyelids,
        // blinks and a mouth for every lip-sync shape rather than being one bitmap.
        FaceLibrary library = FaceLibrary.Parse(Faces);

        FaceConfig plain = Assert.IsType<FaceConfig>(library.Of(Assists.PlainFace));
        FaceConfig moustached = Assert.IsType<FaceConfig>(library.Of(Assists.MoustachedFace));

        Assert.Equal("GAB_FACE", plain.FaceTexture);
        Assert.Equal("GA3_FACE", moustached.FaceTexture);
        Assert.Equal("GA3_MOUTH03", moustached.MouthTexture("MOUTH03"));
        Assert.Equal("GA3_EYELIDS", moustached.RestingTexture(FacePart.Eyelids));
        Assert.NotEmpty(moustached.Blinks);
    }

    [Fact]
    public void The_two_faces_are_the_same_layout_so_one_can_be_painted_onto_the_other()
    {
        // The composition is made of GA3's bitmaps and painted onto GAB's texture, which
        // only works because the two are the same picture with a moustache added: the eyes,
        // the brow and the eyelids are at the same pixels in both.
        FaceLibrary library = FaceLibrary.Parse(Faces);

        FaceConfig plain = Assert.IsType<FaceConfig>(library.Of(Assists.PlainFace));
        FaceConfig moustached = Assert.IsType<FaceConfig>(library.Of(Assists.MoustachedFace));

        Assert.Equal(plain.ForeheadOffset, moustached.ForeheadOffset);
        Assert.Equal(plain.EyelidsOffset, moustached.EyelidsOffset);

        // The mouth is the one region that moves, and only far enough to make room for the
        // moustache above the lip.
        Assert.Equal(plain.MouthOffset.X, moustached.MouthOffset.X);
        Assert.InRange(plain.MouthOffset.Y - moustached.MouthOffset.Y, 0, 4);
    }

    [Fact]
    public void A_clothing_variant_of_gabriel_is_still_gabriel()
    {
        // Scenes place him as gabclothesday02 and the like, and the assistance is keyed on
        // the three-letter code rather than on a model name, so every day's Gabriel wears
        // it and nobody else does.
        FaceLibrary library = FaceLibrary.Parse(Faces);

        Assert.Equal(Assists.PlainFace, library.Of("gabclothesday02")?.Identifier);
        Assert.Null(library.Of("mos"));
    }
}

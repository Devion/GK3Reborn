using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for writing a game down and putting it back.
/// </summary>
/// <remarks>
/// The interesting property is not that a field round-trips but that <em>every</em> field
/// does, and a test that listed them would be the same list the save already is — it would
/// pass while missing whatever the save missed. So the check is the state hash, which
/// <c>GameState</c> computes over everything observable and which exists precisely because
/// a partial answer to "is this the same game" is worthless.
/// </remarks>
public sealed class SaveGameTests
{
    /// <summary>A game with something of everything in it.</summary>
    private static GameState Played()
    {
        var state = new GameState
        {
            Timeblock = new Timeblock(2, 2, IsAfternoon: true),
            Location = "R25",
            Ego = "GABRIEL",
            CameraAngle = "BY_DESK",
        };

        state.Location = "HAL";

        state.SetFlag("MetMosely");
        state.SetFlag("ReadTheParchment");
        state.SetVariable("SidScanner", 28);
        state.SetVariable("Attempts", 3);
        state.SetNounVerbCount("GABRIEL", "REGISTER", "LOOK", 2);
        state.SetTopicCount("MOSELY", "T_MURDER", 1);
        state.Said("MOSELY", "T_MURDER", "ALL");
        state.SetChatCount("MOSELY", 4);
        state.EnterLocation("GABRIEL", "LBY");
        state.SetActorLocation("MOSELY", "DIN");
        state.AddSidneyFile("fileParchment1");
        state.BlockedHitTests.Add("HAL_DOOR_HIT");
        state.ChangeScore(35);
        state.Timers.Set("CLOCK", "TICK", 12.5);
        state.Inventory.Add("GABRIEL", "TAPE_RECORDER");
        state.Inventory.Add("GABRIEL", "PARCHMENT_1");
        state.Inventory.SetActive("GABRIEL", "TAPE_RECORDER");
        state.Inventory.Add("GRACE", "NOTEBOOK");
        state.NextRandom(1, 100);
        state.NextRandom(1, 100);

        return state;
    }

    [Fact]
    public void A_saved_game_restores_to_the_same_game()
    {
        GameState played = Played();
        string before = played.ComputeHash();

        SaveGame save = played.Capture("halfway");

        var reloaded = new GameState();
        reloaded.Restore(save);

        Assert.Equal(before, reloaded.ComputeHash());
    }

    [Fact]
    public void Loading_throws_away_the_game_that_was_running()
    {
        // The classic save bug: a flag nobody set in this run survives the load and the
        // story takes a branch the player never earned, hours later and untraceably.
        SaveGame save = Played().Capture();

        var other = new GameState();
        other.SetFlag("NeverSetInTheSavedGame");
        other.SetVariable("Leftover", 99);
        other.Inventory.Add("GABRIEL", "SOMETHING_ELSE");
        other.AddSidneyFile("fileNeverScanned");
        other.Timers.Set("OLD", "TIMER", 3);

        other.Restore(save);

        Assert.False(other.GetFlag("NeverSetInTheSavedGame"));
        Assert.Equal(0, other.GetVariable("Leftover"));
        Assert.False(other.Inventory.Has("GABRIEL", "SOMETHING_ELSE"));
        Assert.False(other.HasSidneyFile("fileNeverScanned"));
        Assert.Equal(save.Timers.Count, other.Timers.Count);
    }

    [Fact]
    public void Reloading_does_not_re_roll_the_dice()
    {
        // Otherwise a save is a way to retry anything the story left to chance.
        GameState played = Played();
        SaveGame save = played.Capture();

        int next = played.NextRandom(1, 1_000_000);

        var reloaded = new GameState();
        reloaded.Restore(save);

        Assert.Equal(next, reloaded.NextRandom(1, 1_000_000));
    }

    [Fact]
    public void The_room_and_the_room_before_it_both_survive()
    {
        // Restoring through the Location setter would count a visit and rewrite the
        // history, which is a different game than the one that was saved.
        GameState played = Played();
        SaveGame save = played.Capture();

        var reloaded = new GameState();
        reloaded.Restore(save);

        // LBY and HAL rather than HAL and R25: EnterLocation moves the player as well as
        // counting the visit, so the last thing this game did was walk into the lobby.
        Assert.Equal("LBY", reloaded.Location);
        Assert.Equal("HAL", reloaded.LastLocation);
        Assert.Equal(played.GetLocationCount("GABRIEL", "LBY"), reloaded.GetLocationCount("GABRIEL", "LBY"));
    }

    [Fact]
    public void A_save_describes_itself_without_being_loaded()
    {
        SaveGame save = Played().Capture("before the tape");

        Assert.Equal("before the tape", save.Title);
        Assert.Equal(2, save.Day);
        Assert.Contains("LBY", save.Summary, StringComparison.Ordinal);
        Assert.Equal(SaveGame.CurrentSchema, save.SchemaVersion);
    }
}

/// <summary>
/// Tests for where saved games live.
/// </summary>
public sealed class SaveStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "gk3reborn-saves-" + Guid.NewGuid().ToString("n"));

    private SaveStore Store => new(_directory);

    private static SaveGame Save(string title = "") => new GameState
    {
        Location = "LBY",
        Ego = "GABRIEL",
        Timeblock = new Timeblock(1, 10, IsAfternoon: false),
    }.Capture(title);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_written_save_reads_back()
    {
        Assert.True(Store.Write("slot-01", Save("first")));

        SaveGame? read = Store.Read("slot-01", out SaveFault fault);

        Assert.Equal(SaveFault.None, fault);
        Assert.NotNull(read);
        Assert.Equal("first", read!.Title);
        Assert.Equal("LBY", read.Location);
    }

    [Fact]
    public void An_empty_slot_is_missing_rather_than_broken()
    {
        Assert.Null(Store.Read("slot-09", out SaveFault fault));
        Assert.Equal(SaveFault.Missing, fault);
    }

    [Fact]
    public void A_save_from_a_later_build_is_refused_by_name()
    {
        // Reading it would silently drop whatever fields this build does not know, and
        // dropping a field of a save is losing a game.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "slot-02.json"),
            """{"schemaVersion":99,"written":"2030-01-01T00:00:00+00:00","day":1,"hour":1,"afternoon":false,"location":"LBY","ego":"GABRIEL"}""");

        Assert.Null(Store.Read("slot-02", out SaveFault fault));
        Assert.Equal(SaveFault.FromTheFuture, fault);
    }

    [Fact]
    public void Rubbish_in_a_slot_is_reported_rather_than_thrown()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "slot-03.json"), "not a save at all");

        Assert.Null(Store.Read("slot-03", out SaveFault fault));
        Assert.Equal(SaveFault.Unreadable, fault);
    }

    [Fact]
    public void A_slot_name_cannot_become_a_path()
    {
        // Slot names arrive from a console command.
        Assert.False(SaveStore.IsSlotName("../settings"));
        Assert.False(SaveStore.IsSlotName(@"..\..\settings"));
        Assert.False(SaveStore.IsSlotName("has a space"));
        Assert.False(SaveStore.IsSlotName(""));
        Assert.False(SaveStore.IsSlotName(null));

        Assert.True(SaveStore.IsSlotName("autosave"));
        Assert.True(SaveStore.IsSlotName("slot-01"));
        Assert.True(SaveStore.IsSlotName("my_game"));

        Assert.False(Store.Write("../escape", Save()));
    }

    [Fact]
    public void The_list_is_newest_first_and_leaves_out_what_cannot_be_read()
    {
        Store.Write("slot-01", Save("one") with { Written = DateTimeOffset.UtcNow.AddHours(-2) });
        Store.Write("slot-02", Save("two") with { Written = DateTimeOffset.UtcNow });

        File.WriteAllText(Path.Combine(_directory, "slot-03.json"), "{");

        IReadOnlyList<SaveSlot> slots = Store.List();

        Assert.Equal(2, slots.Count);
        Assert.Equal("two", slots[0].Title);
        Assert.Equal("one", slots[1].Title);
    }

    [Fact]
    public void Writing_over_a_save_leaves_a_readable_one()
    {
        Store.Write("slot-01", Save("first"));
        Store.Write("slot-01", Save("second"));

        Assert.Equal("second", Store.Read("slot-01", out _)!.Title);
    }

    [Fact]
    public void A_deleted_slot_is_gone()
    {
        Store.Write("slot-01", Save());

        Assert.True(Store.Delete("slot-01"));
        Assert.Null(Store.Read("slot-01", out _));
    }
}

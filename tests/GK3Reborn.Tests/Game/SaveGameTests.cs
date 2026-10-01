using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for writing a game down and putting it back.
/// </summary>
public sealed class SaveGameTests
{
    [Fact]
    public void Loading_a_save_clears_a_pending_finished_screen()
    {
        var api = new Gk3SheepApi(new GameState());
        api.Invoke("FinishedScreen", []);
        Assert.True(api.FinishedRequested);

        api.RestoreGame(new GameState().Capture());
        Assert.False(api.FinishedRequested);
    }

    [Fact]
    public void Gabriels_disguise_uses_the_same_inventory_after_a_reload()
    {
        var state = new GameState { Ego = "GABRIEL_DISGUISED" };
        state.Inventory.Add("GABRIEL", "GILT_GLOVE");
        state.Inventory.SetActive(state.Ego, "GILT_GLOVE");

        Assert.True(state.Inventory.Has(state.Ego, "GILT_GLOVE"));

        var restored = new GameState();
        restored.Restore(state.Capture());

        Assert.True(restored.Inventory.Has("GABRIEL", "GILT_GLOVE"));
        Assert.True(restored.Inventory.Has("GABRIEL_DISGUISED", "GILT_GLOVE"));
        Assert.Equal("GILT_GLOVE", restored.Inventory.ActiveItemOf("GABRIEL"));
        Assert.Equal(["GABRIEL"], restored.Inventory.Owners);
    }

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
    public void Loading_somebody_elses_game_does_not_turn_the_easter_eggs_on()
    {
        // EGG is a preference kept as a story flag, because a flag is where the game itself
        // looks. Which means it travels in a save, and it must not arrive from one: what
        // the player asked for is not something a saved game gets to decide.
        var played = new GameState { EasterEggs = true };
        SaveGame save = played.Capture();

        var other = new GameState();
        other.Restore(save);

        Assert.False(other.EasterEggs);
        Assert.False(other.GetFlag("EGG"));

        // And the other way round: a save taken without them does not turn them off.
        var asked = new GameState { EasterEggs = true };

        asked.Restore(new GameState().Capture());

        Assert.True(asked.EasterEggs);
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
    public void Loading_during_a_cutscene_gives_the_camera_back()
    {
        // A script that is about to show something the player has to see takes the camera
        // with SetForcedCameraCuts and gives it back a moment later. Loading in between
        // throws that script away and there is nobody left to give it back: the restored
        // room came up with SceneUpdate.Directing true and stayed that way, so the mouse
        // did nothing in a room where no story was running.
        var cutscene = new GameState
        {
            Location = "R25",
            Ego = "GABRIEL",
            ForcedCameraCuts = true,
            CameraGliding = true,
        };

        cutscene.Restore(Played().Capture());

        Assert.False(cutscene.ForcedCameraCuts);
        Assert.False(cutscene.CameraGliding);
    }

    [Fact]
    public void Loading_during_an_action_does_not_carry_its_clock_in()
    {
        // The other half of the same fault, and the reason a load goes through the API
        // rather than the state: ActionSeconds belongs to the action that was playing, and
        // that action is gone with the room it ran in. Left standing it keeps the story
        // "in the middle of something" in the restored room, which is the camera taken
        // away again — for however long the abandoned action had left to run.
        var api = new Gk3SheepApi(new GameState())
        {
            ActionSeconds = 30,
            ActingOn = "REGISTER",
        };

        api.RestoreGame(Played().Capture());

        Assert.Equal(0, api.ActionSeconds);
        Assert.Equal(string.Empty, api.ActingOn);
        Assert.Equal("LBY", api.State.Location);
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
    public void A_save_with_a_figure_on_sidneys_map_reads_back()
    {
        var state = new GameState { Location = "R25", Ego = "GRACE", Timeblock = new Timeblock(3, 5, IsAfternoon: true) };
        state.SidneyMap = new SavedMap(["676,672"], [new SavedFigure("Circle", 676, 672, 484, 0, ["267,415"], Fixed: true)], 8, GridFixed: true);

        Assert.True(Store.Write("slot-04", state.Capture("lsr")));

        SaveGame? read = Store.Read("slot-04", out SaveFault fault);

        Assert.Equal(SaveFault.None, fault);
        Assert.True(read!.SidneyFigures[0].Fixed);
        Assert.Single(Store.List());
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
    public void The_list_is_newest_first_and_keeps_unreadable_saves_visible()
    {
        Store.Write("slot-01", Save("one") with { Written = DateTimeOffset.UtcNow.AddHours(-2) });
        Store.Write("slot-02", Save("two") with { Written = DateTimeOffset.UtcNow });

        File.WriteAllText(Path.Combine(_directory, "slot-03.json"), "{");

        IReadOnlyList<SaveSlot> slots = Store.List();

        Assert.Equal(3, slots.Count);
        Assert.Equal("two", slots[0].Title);
        Assert.Equal("one", slots[1].Title);
        Assert.Equal("slot-03", slots[2].Slot);
        Assert.Equal(SaveFault.Unreadable, slots[2].Fault);
    }

    [Fact]
    public void Quickload_uses_the_newest_valid_copy_without_changing_explicit_menu_selection()
    {
        string other = Path.Combine(_directory, "user-data");
        SaveGame older = Save("opened window") with { Written = DateTimeOffset.UtcNow.AddDays(-3) };
        Store.Write(SaveStore.QuickSlot, older);
        new SaveStore(other).Write(SaveStore.QuickSlot, Save("three days of progress"));
        var combined = new SaveStore(_directory, [other]);
        var api = new Gk3SheepApi(new GameState()) { Saves = combined };
        Assert.Equal("three days of progress", combined.ReadNewest(SaveStore.QuickSlot, out _)!.Title);
        Assert.Equal("opened window", combined.Read(SaveStore.QuickSlot, out _)!.Title);
        // A malformed primary must not stop the shortcut reaching the valid copy either.
        File.WriteAllText(Path.Combine(_directory, "quicksave.json"), "{");
        Assert.Equal("three days of progress", combined.ReadNewest(SaveStore.QuickSlot, out _)!.Title);
        api.Invoke("EngineLoadGame", []);
        Assert.Equal("LBY", api.State.Location);
    }

    [Fact]
    public void The_default_index_includes_both_platform_locations()
    {
        var store = new SaveStore();
        Assert.Contains(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "saves")), store.SearchDirectories);
        Assert.Contains(Path.GetFullPath(Path.Combine(GK3Reborn.Foundation.InstallPaths.UserData, "saves")), store.SearchDirectories);
    }

    [Fact]
    public void Both_locations_keep_colliding_saves_and_their_own_thumbnails()
    {
        string other = Path.Combine(_directory, "user-data");
        var user = new SaveStore(other);
        Store.Write("01", Save("beside executable"));
        user.Write("01", Save("user data"));
        File.WriteAllBytes(Path.Combine(_directory, "01.png"), [1]);
        File.WriteAllBytes(Path.Combine(other, "01.png"), [2]);
        var combined = new SaveStore(_directory, [other, _directory]);

        IReadOnlyList<SaveSlot> entries = combined.List();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("01", entry.Name));
        foreach (SaveSlot entry in entries)
        {
            Assert.Equal(entry.Title, combined.Read(entry.Slot, out _)!.Title);
            byte expected = entry.Title == "user data" ? (byte)2 : (byte)1;
            Assert.Equal(expected, File.ReadAllBytes(combined.PictureOf(entry.Slot))[0]);
        }
        Assert.True(combined.Write("01", Save("new save")));
        Assert.Equal("user data", user.Read("01", out _)!.Title);
        Assert.False(combined.Write(entries.Single(e => e.Title == "user data").Slot, Save()));
    }

    [Fact]
    public void A_missing_primary_folder_does_not_hide_user_saves_after_restarting()
    {
        string other = Path.Combine(_directory, "user-data");
        new SaveStore(other).Write("quicksave", Save("survives restart"));
        var restarted = new SaveStore(Path.Combine(_directory, "absent"), [other]);
        SaveSlot entry = Assert.Single(restarted.List());
        Assert.Equal("survives restart", restarted.Read(entry.Slot, out _)!.Title);
        Assert.Equal("survives restart", restarted.Read("quicksave", out _)!.Title);
    }

    [Fact]
    public void An_inaccessible_location_does_not_prevent_indexing_the_other_one()
    {
        Directory.CreateDirectory(_directory);
        string notDirectory = Path.Combine(_directory, "a-file");
        File.WriteAllText(notDirectory, "not a directory");
        Store.Write("01", Save());
        Assert.Single(new SaveStore(notDirectory, [_directory]).List());
    }

    [Fact]
    public void Invalid_and_future_saves_in_either_folder_are_listed()
    {
        string other = Path.Combine(_directory, "user-data");
        Store.Write("01", Save());
        new SaveStore(other).Write("01", Save() with { SchemaVersion = 99 });
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{");
        new SaveStore(other).Write("null-state", Save());
        string malformedPath = Path.Combine(other, "null-state.json");
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(malformedPath))!;
        malformed["topicCounts"] = null;
        File.WriteAllText(malformedPath, malformed.ToJsonString());
        var combined = new SaveStore(_directory, [other]);
        IReadOnlyList<SaveSlot> entries = combined.List();
        Assert.Equal(4, entries.Count);
        Assert.Single(entries, e => e.Fault == SaveFault.None);
        Assert.Single(entries, e => e.Fault == SaveFault.FromTheFuture);
        Assert.Equal(2, entries.Count(e => e.Fault == SaveFault.Unreadable));
    }

    [Fact]
    public void Renamed_valid_saves_are_indexed_and_qualified_keys_cannot_escape()
    {
        Store.Write("01", Save("renamed"));
        File.Move(Path.Combine(_directory, "01.json"), Path.Combine(_directory, "My save.JSON"));
        SaveSlot entry = Assert.Single(Store.List());
        Assert.Equal("renamed", Store.Read(entry.Slot, out _)!.Title);
        Assert.Null(Store.Read("@0:../outside.json", out SaveFault fault));
        Assert.Equal(SaveFault.Unreadable, fault);
        Assert.Null(Store.Read("@99:01.json", out _));
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

    /// <summary>An import an older build brought across is given its introductions.</summary>
    [Fact]
    public void A_save_with_no_history_past_the_first_block_is_given_its_introductions()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "gk3-save0001.json"),
            """{"schemaVersion":2,"written":"2026-01-01T00:00:00+00:00","day":2,"hour":10,"afternoon":false,"location":"LBY","ego":"GABRIEL"}""");

        SaveGame? save = Store.Read("gk3-save0001", out SaveFault fault);

        Assert.Equal(SaveFault.None, fault);
        Assert.NotNull(save);
        Assert.Equal(SaveGame.CurrentSchema, save.SchemaVersion);
        Assert.Contains("BUTHANE", save.Introduced);
    }

    [Fact]
    public void A_game_played_here_is_told_nothing_it_did_not_earn()
    {
        // It can answer the question itself, out of the topics it recorded, and a migration
        // that handed it the list anyway would name everybody the player had walked past.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "slot-04.json"),
            """{"schemaVersion":2,"written":"2026-01-01T00:00:00+00:00","day":2,"hour":10,"afternoon":false,"location":"LBY","ego":"GABRIEL","topicCounts":{"MOSELY|T_CASE":1}}""");

        SaveGame? save = Store.Read("slot-04", out _);

        Assert.NotNull(save);
        Assert.Equal(SaveGame.CurrentSchema, save.SchemaVersion);
        Assert.Empty(save.Introduced);
    }

    [Fact]
    public void A_save_still_standing_in_the_first_block_is_left_alone()
    {
        // Where an import and a game thirty seconds old look the same, and the list would
        // be most of the cast: the safe answer there is the one the labels already give.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "slot-05.json"),
            """{"schemaVersion":2,"written":"2026-01-01T00:00:00+00:00","day":1,"hour":10,"afternoon":false,"location":"LBY","ego":"GABRIEL"}""");

        Assert.Empty(Store.Read("slot-05", out _)!.Introduced);
    }
}

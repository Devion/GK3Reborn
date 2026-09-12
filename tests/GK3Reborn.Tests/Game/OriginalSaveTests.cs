using System.IO.Compression;
using System.Text;
using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for reading the 1999 game's own save files.
/// </summary>
public sealed class OriginalSaveTests
{
    /// <summary>
    /// A save file byte for byte as a retail one is laid out.
    /// </summary>
    /// <param name="title">What the player called it.</param>
    /// <param name="location">The room, as the original writes it.</param>
    /// <param name="timeblock">The point in the story.</param>
    /// <param name="score">The score.</param>
    /// <param name="mode">
    /// The build the game was made with, which is a variable-length string in the middle of
    /// a header the reader has to walk rather than seek across.
    /// </param>
    /// <param name="body">The state, uncompressed, or null for a save that carries none.</param>
    /// <returns>The file.</returns>
    private static byte[] Retail(
        string title,
        string location,
        string timeblock,
        int score,
        string mode = "Production",
        byte[]? body = null)
    {
        using var header = new MemoryStream();
        using var writer = new BinaryWriter(header);

        writer.Write("GK3!Save"u8);
        writer.Write(4);

        // Filled in once the rest is written: the field counts the header this size prefix
        // does not cover, plus the three fields of the persist header that follow it.
        long sizeAt = header.Position;
        writer.Write(0);

        long from = header.Position;

        writer.Write(65536);                    // product version
        writer.Write(143);                      // the build number of the exe
        Prefixed(writer, mode);
        writer.Write(1024);                     // the screen it was saved at, across
        writer.Write(768);                      // and down
        writer.Write(13);                       // how many saves this playthrough

        writer.Write(new byte[8]);              // eight bytes nobody has explained

        foreach (ushort part in new ushort[] { 2001, 6, 1, 18, 20, 44, 49, 835 })
        {
            writer.Write(part);
        }

        writer.Write(Padded("PC1200", 32));
        writer.Write(Padded("A Player", 32));
        writer.Write(Padded("Copyright 1999 Sierra Studios. All rights reserved.", 100));

        long persistAt = header.Position;

        writer.Write(1);                        // the persist header's own version
        writer.Write((byte)(body is null ? 0 : 1));

        long offsetAt = header.Position;
        writer.Write(0);                        // where the state begins, filled in below

        // The stated size runs to exactly here — three fields into the persist header,
        // which is how the retail files count it.
        long size = header.Position - from;

        Prefixed(writer, title);
        Prefixed(writer, location);
        Prefixed(writer, timeblock);
        writer.Write(score);
        writer.Write(965);
        writer.Write(1);
        writer.Write(0);                        // no thumbnail

        _ = persistAt;

        byte[] packed = body is null ? [] : Deflated(body);

        header.Position = sizeAt;
        writer.Write((int)size);
        header.Position = offsetAt;
        writer.Write(body is null ? 0 : (int)header.Length);
        header.Position = header.Length;

        writer.Write(packed);

        return header.ToArray();
    }

    private static byte[] Padded(string text, int width)
    {
        byte[] bytes = new byte[width];
        Encoding.ASCII.GetBytes(text).AsSpan(0, Math.Min(width - 1, text.Length)).CopyTo(bytes);
        return bytes;
    }

    private static byte[] Deflated(byte[] bytes)
    {
        using var into = new MemoryStream();

        using (var deflate = new ZLibStream(into, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        return into.ToArray();
    }

    private static void Prefixed(BinaryWriter writer, string text)
    {
        // As the retail files have it: a 32-bit length, the bytes, and a terminating nul
        // the length does not count.
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    /// <summary>The state a retail save carries, in the order the original writes it.</summary>
    /// <param name="counts">The noun, verb and topic counts.</param>
    /// <param name="flags">The flags that are set.</param>
    /// <param name="items">What each of them is carrying.</param>
    /// <param name="scored">The score events that have been earned.</param>
    /// <param name="gabriel">What Gabriel has in hand.</param>
    /// <param name="grace">What Grace has in hand.</param>
    /// <returns>The state, uncompressed.</returns>
    private static byte[] State(
        (string Noun, string Verb, bool Gabriel, int Count)[] counts,
        IReadOnlyList<string> flags,
        IReadOnlyDictionary<string, string> items,
        IReadOnlyList<string> scored,
        string gabriel = "NONE",
        string grace = "NONE")
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Whatever came before the two blocks this reads, which in a real save is most of
        // the file: every object the engine owned, written out ahead of the story.
        writer.Write(new byte[64]);

        // The score table, which the original writes as every event there is against
        // whether it has been earned.
        string[] every = [.. ScoreEvents.Open().Names];

        writer.Write(every.Length);

        foreach (string name in every)
        {
            Prefixed(writer, name);
            writer.Write((byte)(scored.Contains(name, StringComparer.OrdinalIgnoreCase) ? 1 : 0));
        }

        writer.Write(new byte[32]);

        // And the game state.
        Prefixed(writer, gabriel);
        Prefixed(writer, grace);

        writer.Write(counts.Length);

        foreach ((string noun, string verb, bool whose, int count) in counts)
        {
            Prefixed(writer, noun);
            Prefixed(writer, verb);
            writer.Write((byte)(whose ? 1 : 0));
            writer.Write((byte)count);
        }

        writer.Write((byte)1);                              // the sheep was compiled
        Prefixed(writer, "symbols{int n$;}code{}");

        Table(writer, Filled("CHAT", 120), _ => (byte)0);
        Table(writer, [.. Filled("FLAG", 60).Concat(flags)], f => (byte)(flags.Contains(f) ? 1 : 0));
        Strings(writer, Cast(), _ => "non");
        Table(writer, Cast(), _ => (byte)0);
        Table(writer, Rooms(), _ => (byte)1);
        Numbers(writer, Filled("VAR", 8), _ => 0);
        Strings(writer, [.. items.Keys], i => items[i]);

        string[] rooms = Rooms();

        writer.Write(rooms.Length);

        foreach (string room in rooms)
        {
            Prefixed(writer, room);
            writer.Write(2);
            Prefixed(writer, "102p");
            writer.Write(room == "rc1" ? 3 : 0);
            Prefixed(writer, "110a");
            writer.Write(room == "lby" ? 1 : 0);
        }

        Prefixed(writer, "102p");

        return stream.ToArray();
    }

    private static string[] Cast() =>
        ["ABBE", "BUCHELLI", "BUTHANE", "EMILIO", "ESTELLE", "GABRIEL", "GRACE", "JEAN",
         "LARRY", "MOSELY", "WILKES", "MONTREAUX"];

    private static string[] Rooms() =>
        [.. Enumerable.Range(0, 24).Select(i => i switch
        {
            0 => "rc1",
            1 => "lby",
            _ => $"r{i:00}",
        })];

    private static string[] Filled(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => $"{prefix}{i:000}")];

    private static void Table(BinaryWriter writer, string[] keys, Func<string, byte> value)
    {
        writer.Write(keys.Length);

        foreach (string key in keys)
        {
            Prefixed(writer, key);
            writer.Write(value(key));
        }
    }

    private static void Numbers(BinaryWriter writer, string[] keys, Func<string, int> value)
    {
        writer.Write(keys.Length);

        foreach (string key in keys)
        {
            Prefixed(writer, key);
            writer.Write(value(key));
        }
    }

    private static void Strings(BinaryWriter writer, string[] keys, Func<string, string> value)
    {
        writer.Write(keys.Length);

        foreach (string key in keys)
        {
            Prefixed(writer, key);
            Prefixed(writer, value(key));
        }
    }

    private static string Written(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".gk3");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_save_summary_is_read_as_the_layout_describes()
    {
        string path = Written(Retail("Before the cat", "rc1", "102p", 115));

        try
        {
            var summary = OriginalSaves.Summary(path);

            Assert.NotNull(summary);
            Assert.Equal("Before the cat", summary.Value.Title);
            Assert.Equal("RC1", summary.Value.Location);
            Assert.Equal(new Timeblock(1, 2, true), summary.Value.When);
            Assert.Equal(115, summary.Value.Score);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The header is walked field by field, so a variable-length field inside it moves
    /// everything after it and nothing breaks.
    /// </summary>
    [Fact]
    public void The_header_is_walked_rather_than_seeked_across()
    {
        // Release rather than Production: two characters shorter, and every offset after it
        // moves by two. A reader that seeked by a fixed size would read the summary askew.
        string path = Written(Retail("Odd header", "lby", "110a", 8, mode: "Release"));

        try
        {
            var summary = OriginalSaves.Summary(path);

            Assert.Equal("Odd header", summary?.Title);
            Assert.Equal("LBY", summary?.Location);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Anything that is not an original save is refused, never guessed at.</summary>
    [Fact]
    public void A_file_that_is_not_a_save_answers_null()
    {
        string path = Written(Encoding.UTF8.GetBytes("not a save at all, whatever the name"));

        try
        {
            Assert.Null(OriginalSaves.Summary(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A <c>.gk3</c> dropped into the game's own saves folder is brought across.</summary>
    [Fact]
    public void A_save_dropped_into_the_stores_own_folder_is_imported()
    {
        string saves = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(saves);

        try
        {
            // The file and the store are the same directory, which is the whole point.
            File.WriteAllBytes(
                Path.Combine(saves, "save0009.gk3"),
                Retail("Mosely Clothes", "rc1", "102p", 40));

            var store = new SaveStore(saves);

            Assert.Equal(1, OriginalSaves.Import(store.Directory, store, ScoreEvents.Open(), Introductions.Open()));

            SaveGame? save = store.Read("gk3-save0009", out SaveFault fault);

            Assert.Equal(SaveFault.None, fault);
            Assert.NotNull(save);
            Assert.Equal("RC1", save.Location);

            // And it is offered by the store, which is what the restore page reads.
            Assert.Contains(store.List(), slot => slot.Slot == "gk3-save0009");

            // The original is left where it was. Deleting an import is how somebody asks
            // for it again, so the file it came from has to still be there.
            Assert.True(File.Exists(Path.Combine(saves, "save0009.gk3")));

            // Idempotent against its own output: a second launch imports nothing and does
            // not trip over the .json it wrote beside the .gk3 last time.
            Assert.Equal(0, OriginalSaves.Import(store.Directory, store, ScoreEvents.Open(), Introductions.Open()));
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Fact]
    public void An_import_carries_the_story_position_and_what_it_implies()
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        File.WriteAllBytes(
            Path.Combine(directory, "save0002.gk3"),
            Retail("Day two", "r25", "210a", 200));

        string saves = Path.Combine(directory, "imported");

        try
        {
            var store = new SaveStore(saves);
            ScoreEvents scores = ScoreEvents.Open();

            Assert.Equal(1, OriginalSaves.Import(directory, store, scores, Introductions.Open()));

            // Idempotent: the same file is not brought across twice.
            Assert.Equal(0, OriginalSaves.Import(directory, store, scores, Introductions.Open()));

            SaveGame? save = store.Read("gk3-save0002", out _);

            Assert.NotNull(save);
            Assert.Equal("Day two", save.Title);
            Assert.Equal("R25", save.Location);
            Assert.Equal(200, save.Score);

            // Day one is behind a save standing in day two, so its events are earned —
            // which is what stops them scoring twice — and day two's own are not invented.
            Assert.Contains("e_110a_pho_phone_prince_james", save.Scored);
            Assert.DoesNotContain("e_210a_r25_pickup_fingerprint_kit", save.Scored);

            // And the pockets hold at least what a new game starts with: an import that
            // lost Prince James's card would strand the story it was meant to resume.
            Assert.Contains(
                save.Inventories,
                pockets =>
                    pockets.Owner == "GABRIEL" && pockets.Items.Contains("PRINCE_JAMES_CARD"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>An import knows the people the story has already introduced.</summary>
    [Theory]
    [InlineData("112p", "BUTHANE", "LARRY")]
    [InlineData("102p", "LARRY", null)]
    [InlineData("210a", "WILKES", null)]
    [InlineData("306p", "ABBE", null)]
    public void An_import_knows_who_the_story_has_already_introduced(
        string timeblock, string known, string? stranger)
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        File.WriteAllBytes(
            Path.Combine(directory, "save0003.gk3"),
            Retail("Mid-story", "lby", timeblock, 100));

        try
        {
            var store = new SaveStore(Path.Combine(directory, "imported"));

            Assert.Equal(
                1,
                OriginalSaves.Import(
                    directory, store, ScoreEvents.Open(), Introductions.Open()));

            SaveGame? save = store.Read("gk3-save0003", out _);

            Assert.NotNull(save);
            Assert.Contains(known, save.Introduced);

            if (stranger is not null)
            {
                Assert.DoesNotContain(stranger, save.Introduced);
            }

            // And a save standing in day two knows everybody, because every introduction in
            // the game is made on day one.
            if (save.Day > 1)
            {
                Assert.Equal(Introductions.Open().Count, save.Introduced.Count);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The state a full save carries comes back whole.</summary>
    [Fact]
    public void The_state_a_save_carries_is_read_back()
    {
        (string, string, bool, int)[] counts =
        [
            ("REGISTER", "LOOK", true, 2),
            ("MOSELY", "T_MURDER", true, 3),
            ("MOSELY", "T_MURDER", false, 1),
            ("BUTHANE", "T_INTRODUCE", true, 1),
            ("PARCHMENT_1", "SCANNER", false, 1),
        ];

        Dictionary<string, string> items = Pockets();

        items["TAPE_RECORDER"] = "GabeHas";
        items["SKETCHPAD"] = "GraceHas";
        items["PRINCE_JAMES_CARD"] = "BothHave";
        items["BASEBALL_CAP"] = "Used";

        byte[] file = Retail(
            "Mosely Clothes",
            "rc1",
            "102p",
            4,
            body: State(
                counts,
                ["MetMosely", "ReadTheParchment"],
                items,
                ["e_110a_r25_tape", "e_110a_r25_hanger"],
                gabriel: "TAPE_RECORDER"));

        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllBytes(Path.Combine(directory, "save0001.gk3"), file);

            var store = new SaveStore(Path.Combine(directory, "imported"));

            Assert.Equal(
                1, OriginalSaves.Import(directory, store, ScoreEvents.Open(), Introductions.Open()));

            SaveGame? save = store.Read("gk3-save0001", out _);
            Assert.NotNull(save);

            // The score is replayed out of the events rather than copied off the header,
            // and the two agree: two tape-and-hanger events at two points each.
            Assert.Equal(4, save.Score);
            Assert.Contains("e_110a_r25_tape", save.Scored);
            Assert.DoesNotContain("e_110a_din_enter", save.Scored);

            // Upper-cased, as this engine keys every name it is asked about.
            Assert.Contains("METMOSELY", save.Flags);
            Assert.DoesNotContain("FLAG001", save.Flags);

            // The pockets, both of them, and what is in hand.
            SavedInventory gabriel = save.Inventories.Single(i => i.Owner == "GABRIEL");
            SavedInventory grace = save.Inventories.Single(i => i.Owner == "GRACE");

            Assert.Contains("TAPE_RECORDER", gabriel.Items);
            Assert.Contains("PRINCE_JAMES_CARD", gabriel.Items);
            Assert.DoesNotContain("BASEBALL_CAP", gabriel.Items);
            Assert.Equal("TAPE_RECORDER", gabriel.Active);

            Assert.Contains("SKETCHPAD", grace.Items);
            Assert.Contains("PRINCE_JAMES_CARD", grace.Items);
            Assert.Null(grace.Active);

            // A restored game answers the questions an action file asks of it.
            var story = new GameState();
            story.Restore(save);

            Assert.Equal(2, story.GetNounVerbCount("GABRIEL", "REGISTER", "LOOK"));
            Assert.Equal(3, story.GetTopicCount("MOSELY", "T_MURDER"));
            Assert.Equal(3, story.GetNounVerbCount("GABRIEL", "MOSELY", "T_MURDER"));
            Assert.Equal(1, story.GetNounVerbCount("GRACE", "MOSELY", "T_MURDER"));

            // Somebody the save says was introduced is introduced, whatever the table for
            // this point in the story would have assumed on its own.
            Assert.Contains("BUTHANE", save.Introduced);

            // And what went through the scanner is what the counts say went through it.
            Assert.Contains("PARCHMENT_1", save.SidneyScans);
            Assert.DoesNotContain("TAPE_RECORDER", save.SidneyScans);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Sixty items, so the state is as long as a real one and reads as one.</summary>
    private static Dictionary<string, string> Pockets()
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string item in Filled("ITEM", 60))
        {
            items[item] = "NotPlaced";
        }

        return items;
    }

    /// <summary>
    /// An import an older build made is redone, so a player does not have to know that
    /// this build reads more of an original save than the last one did.
    /// </summary>
    [Fact]
    public void An_import_an_older_reader_made_is_done_again()
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllBytes(
                Path.Combine(directory, "save0007.gk3"),
                Retail(
                    "Older import",
                    "rc1",
                    "102p",
                    4,
                    body: State(
                        [("REGISTER", "LOOK", true, 2)],
                        ["MetMosely"],
                        Pockets(),
                        ["e_110a_r25_tape", "e_110a_r25_hanger"],
                        gabriel: "TAPE_RECORDER")));

            var store = new SaveStore(Path.Combine(directory, "imported"));
            ScoreEvents scores = ScoreEvents.Open();
            Introductions met = Introductions.Open();

            // As an older build left it: everything a point in the story implies, and none
            // of the state, with no mark to say which reader made it.
            Assert.True(store.Write(
                "gk3-save0007",
                new SaveGame
                {
                    SchemaVersion = SaveGame.CurrentSchema,
                    Written = DateTimeOffset.UtcNow,
                    Title = "Older import",
                    Day = 1,
                    Hour = 2,
                    Afternoon = true,
                    Location = "RC1",
                    Ego = "GABRIEL",
                }));

            Assert.Equal(1, OriginalSaves.Import(directory, store, scores, met));

            SaveGame? save = store.Read("gk3-save0007", out _);

            Assert.NotNull(save);
            Assert.Contains("METMOSELY", save.Flags);

            // And now it is left alone, because this reader is the one that made it.
            Assert.Equal(0, OriginalSaves.Import(directory, store, scores, met));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A save whose state will not decompress is still opened at the right hour.
    /// </summary>
    [Fact]
    public void A_save_whose_state_cannot_be_read_falls_back_to_its_header()
    {
        byte[] file = Retail("Torn", "lby", "110a", 12, body: [1, 2, 3, 4]);

        // Break the compressed block, leaving the header saying it is there.
        file[^1] ^= 0xFF;
        file[^2] ^= 0xFF;

        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllBytes(Path.Combine(directory, "save0004.gk3"), file);

            var store = new SaveStore(Path.Combine(directory, "imported"));

            Assert.Equal(
                1, OriginalSaves.Import(directory, store, ScoreEvents.Open(), Introductions.Open()));

            SaveGame? save = store.Read("gk3-save0004", out _);

            Assert.NotNull(save);
            Assert.Equal("LBY", save.Location);
            Assert.Equal(12, save.Score);

            // Which is the old behaviour, kept: at least what a new game starts with.
            Assert.Contains(
                save.Inventories,
                pockets => pockets.Items.Contains("PRINCE_JAMES_CARD"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Every retail save this machine can find reads, and its own score reconciles.
    /// </summary>
    [Fact]
    public void A_real_save_reconciles_its_own_score()
    {
        string[] found = [.. Installed()];

        Assert.SkipWhen(found.Length == 0, "no original saves on this machine");

        ScoreEvents scores = ScoreEvents.Open();

        foreach (string path in found)
        {
            var read = OriginalSaves.Read(path, scores);

            Assert.NotNull(read);

            (OriginalSaveHeader header, OriginalSaveState? state) = read.Value;

            Assert.NotNull(state);

            int total = state.Scored.Sum(e => scores.Worth(e) ?? 0);

            Assert.Equal(header.Score, total);

            // And the state is a story rather than a lucky parse: somebody is carrying
            // something, the rooms are the game's own, and the timeblock agrees.
            Assert.Contains(state.Items.Values, v => v is "GabeHas" or "GraceHas" or "BothHave");
            Assert.NotEmpty(state.Visits);
            Assert.Equal(header.When.ToString(), state.AdvancedTo.ToUpperInvariant());
        }
    }

    /// <summary>Where an original installation keeps its saves, when there is one.</summary>
    private static IEnumerable<string> Installed()
    {
        foreach (string root in Roots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*.gk3"))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> Roots()
    {
        if (Environment.GetEnvironmentVariable("GK3REBORN_ORIGINAL_SAVES") is
            { Length: > 0 } asked)
        {
            yield return asked;
        }

        // Beside a checkout, where this project's own reference saves live.
        string here = AppContext.BaseDirectory;

        for (DirectoryInfo? at = new(here); at is not null; at = at.Parent)
        {
            yield return Path.Combine(at.FullName, "savess");
            yield return Path.Combine(at.FullName, "GK3", "Save Games");
        }
    }
}

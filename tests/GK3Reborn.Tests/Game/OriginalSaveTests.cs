using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for reading the 1999 game's own save files.
/// </summary>
/// <remarks>
/// Built to match three retail saves byte for byte, which disagreed with the reference's
/// own writer twice: the magic is <c>GK3!Save</c> where G-Engine writes <c>SAVE</c>, and the
/// summary starts straight at the name with no version number in front of it. The layout
/// here is the measured one, and <c>OriginalSaves.Summary</c> answers null rather than
/// nonsense for anything that does not match it.
/// </remarks>
public sealed class OriginalSaveTests
{
    /// <summary>A save file byte-for-byte as the documented layout describes one.</summary>
    private static byte[] Retail(
        string title, string location, string timeblock, int score, int headerSize = 232)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("GK3!Save"u8);
        writer.Write(4);
        writer.Write(headerSize);
        writer.Write(new byte[headerSize]);

        Prefixed(writer, title);
        Prefixed(writer, location);
        Prefixed(writer, timeblock);
        writer.Write(score);
        writer.Write(965);
        writer.Write(1);
        writer.Write(0);

        return stream.ToArray();
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

    /// <summary>The header's own size field is honoured, not assumed.</summary>
    [Fact]
    public void The_header_is_skipped_by_its_own_stated_size()
    {
        string path = Written(Retail("Odd header", "lby", "110a", 8, headerSize: 300));

        try
        {
            Assert.Equal("Odd header", OriginalSaves.Summary(path)?.Title);
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

    /// <summary>An import restores the story position, the implied past, and the pockets.</summary>
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

            Assert.Equal(1, OriginalSaves.Import(directory, store, scores));

            // Idempotent: the same file is not brought across twice.
            Assert.Equal(0, OriginalSaves.Import(directory, store, scores));

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
}

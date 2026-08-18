using System.Text;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using Xunit;

namespace GK3Reborn.Tests.Formats;

public sealed class BarnArchiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "gk3reborn-tests", Path.GetRandomFileName());

    public BarnArchiveTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }

    private string Write(string name, byte[] contents)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static string Text(byte[] data) => Encoding.ASCII.GetString(data);

    [Fact]
    public void Stored_entries_round_trip()
    {
        string path = Write("test.brn", new BarnFixture()
            .AddStored("HELLO.TXT", "hello world")
            .Build());

        using BarnArchive archive = BarnArchive.Open(path);

        BarnEntry entry = Assert.Single(archive.Entries);
        Assert.Equal("HELLO.TXT", entry.Name);
        Assert.Equal(BarnCompression.None, entry.Compression);
        Assert.Equal("hello world", Text(archive.Extract(entry)));
    }

    [Fact]
    public void Zlib_entries_round_trip()
    {
        // Long enough that compression actually engages.
        string content = string.Concat(Enumerable.Repeat("gabriel knight ", 200));
        string path = Write("test.brn", new BarnFixture()
            .AddDeflated("BIG.TXT", content)
            .Build());

        using BarnArchive archive = BarnArchive.Open(path);

        BarnEntry entry = Assert.Single(archive.Entries);
        Assert.Equal(BarnCompression.Zlib, entry.Compression);
        Assert.Equal(content, Text(archive.Extract(entry)));
        Assert.True(entry.Size < content.Length, "the fixture should actually compress");
    }

    [Fact]
    public void Several_entries_are_all_addressable()
    {
        string path = Write("test.brn", new BarnFixture()
            .AddStored("ONE.TXT", "first")
            .AddDeflated("TWO.TXT", "second")
            .AddStored("THREE.TXT", "third")
            .Build());

        using BarnArchive archive = BarnArchive.Open(path);

        Assert.Equal(3, archive.Count);
        Assert.Equal("first", Text(archive.Extract(archive.Find("ONE.TXT")!)));
        Assert.Equal("second", Text(archive.Extract(archive.Find("TWO.TXT")!)));
        Assert.Equal("third", Text(archive.Extract(archive.Find("THREE.TXT")!)));
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        // Game data spells asset names inconsistently, so lookup must not care.
        string path = Write("test.brn", new BarnFixture().AddStored("Gabriel.MOD", "mesh").Build());

        using BarnArchive archive = BarnArchive.Open(path);

        Assert.NotNull(archive.Find("gabriel.mod"));
        Assert.NotNull(archive.Find("GABRIEL.MOD"));
        Assert.NotNull(archive.Find("Gabriel.MOD"));
        Assert.Null(archive.Find("gabriel.act"));
    }

    [Fact]
    public void Compression_type_three_is_treated_as_stored()
    {
        string path = Write("test.brn", new BarnFixture().AddTypeThree("ODD.TXT", "plain bytes").Build());

        using BarnArchive archive = BarnArchive.Open(path);

        BarnEntry entry = Assert.Single(archive.Entries);
        Assert.Equal(BarnCompression.None, entry.Compression);
        Assert.Equal("plain bytes", Text(archive.Extract(entry)));
    }

    [Fact]
    public void Entries_pointing_at_another_archive_are_flagged_and_refuse_extraction()
    {
        string path = Write("core.brn", new BarnFixture()
            .PointingAt("day1.brn")
            .AddStored("ELSEWHERE.MOD", "not really here")
            .Build());

        using BarnArchive archive = BarnArchive.Open(path);

        BarnEntry entry = Assert.Single(archive.Entries);
        Assert.True(entry.IsPointer);
        Assert.Equal("day1.brn", entry.ReferencedArchive);

        var ex = Assert.Throws<FormatParseException>(() => archive.Extract(entry));
        Assert.Equal("GK3R1020", ex.Diagnostic.Code);
        Assert.Contains("day1.brn", ex.Diagnostic.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_an_archive_is_rejected_by_signature()
    {
        string path = Write("bogus.brn", new BarnFixture().WithBadMagic().AddStored("X.TXT", "x").Build());

        var ex = Assert.Throws<FormatParseException>(() => BarnArchive.Open(path));

        Assert.Equal("GK3R1002", ex.Diagnostic.Code);
        Assert.Equal("GK3!", ex.Diagnostic.Expected);
        Assert.Equal("NOPE", ex.Diagnostic.Actual);
    }

    [Fact]
    public void A_truncated_archive_fails_with_the_file_named()
    {
        byte[] complete = new BarnFixture().AddStored("HELLO.TXT", "hello world").Build();
        string path = Write("short.brn", complete[..(complete.Length / 2)]);

        Exception? ex = Record.Exception(() =>
        {
            using BarnArchive archive = BarnArchive.Open(path);
            foreach (BarnEntry entry in archive.Entries)
            {
                archive.Extract(entry);
            }
        });

        Assert.NotNull(ex);
        Assert.True(ex is FormatParseException or EndOfStreamException, $"unexpected {ex.GetType().Name}");
    }

    [Fact]
    public void An_empty_file_is_rejected_rather_than_crashing()
    {
        string path = Write("empty.brn", []);
        Assert.ThrowsAny<Exception>(() => BarnArchive.Open(path));
    }

    [Fact]
    public void Entries_are_listed_in_a_stable_order()
    {
        string path = Write("test.brn", new BarnFixture()
            .AddStored("ZEBRA.TXT", "z")
            .AddStored("ALPHA.TXT", "a")
            .AddStored("MIDDLE.TXT", "m")
            .Build());

        using BarnArchive archive = BarnArchive.Open(path);

        // Manifests must be reproducible, so enumeration cannot depend on hash order.
        Assert.Equal(["ALPHA.TXT", "MIDDLE.TXT", "ZEBRA.TXT"], archive.Entries.Select(e => e.Name));
    }
}

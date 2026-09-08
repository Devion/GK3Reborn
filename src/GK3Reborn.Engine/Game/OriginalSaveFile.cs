// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace GK3Reborn.Game;

/// <summary>One entry of the original game's noun/verb map.</summary>
/// <param name="Noun">The thing.</param>
/// <param name="Verb">What was done to it, or the topic that was raised.</param>
/// <param name="Gabriel">Whose count it is: Gabriel's when true, Grace's when false.</param>
/// <param name="Count">How many times.</param>
public sealed record OriginalCount(string Noun, string Verb, bool Gabriel, int Count);

/// <summary>
/// What one original save says about itself, out of its fixed header.
/// </summary>
/// <param name="Title">What the player called it.</param>
/// <param name="Location">The three-letter room code, upper case.</param>
/// <param name="When">The point in the story.</param>
/// <param name="Score">The score at the time.</param>
/// <param name="MaxScore">What the game said was possible.</param>
/// <param name="Written">When it was saved, as the machine's own clock read.</param>
/// <param name="Picture">The thumbnail, as a PNG, or null when it has none.</param>
/// <param name="Body">Where the compressed state begins, or nought when there is none.</param>
public sealed record OriginalSaveHeader(
    string Title,
    string Location,
    Timeblock When,
    int Score,
    int MaxScore,
    DateTimeOffset Written,
    byte[]? Picture,
    int Body);

/// <summary>
/// Everything the original game's own state says, once it has been read back.
/// </summary>
public sealed record OriginalSaveState
{
    /// <summary>The item Gabriel has in hand, or empty for none.</summary>
    public string GabrielItem { get; init; } = string.Empty;

    /// <summary>The item Grace has in hand, or empty for none.</summary>
    public string GraceItem { get; init; } = string.Empty;

    /// <summary>Every noun, verb and topic either of them has counted against.</summary>
    public IReadOnlyList<OriginalCount> Counts { get; init; } = [];

    /// <summary>How often each noun has been chatted to.</summary>
    public IReadOnlyDictionary<string, int> ChatCounts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>The flags that are set.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Where each actor is, for those who are anywhere.</summary>
    public IReadOnlyDictionary<string, string> ActorLocations { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Which actors are out on the driving map.</summary>
    public IReadOnlyList<string> OnTheMap { get; init; } = [];

    /// <summary>Everywhere the player has been.</summary>
    public IReadOnlyList<string> Visited { get; init; } = [];

    /// <summary>The named integers the scripts keep.</summary>
    public IReadOnlyDictionary<string, int> Variables { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Every inventory item and the word the original files it under.</summary>
    public IReadOnlyDictionary<string, string> Items { get; init; } =
        new Dictionary<string, string>();

    /// <summary>How often the player has been in each room, in each point of the story.</summary>
    public IReadOnlyList<(string Location, string Timeblock, int Count)> Visits { get; init; } = [];

    /// <summary>The furthest point in the story the game has reached.</summary>
    public string AdvancedTo { get; init; } = string.Empty;

    /// <summary>The score events that have been earned.</summary>
    public IReadOnlyList<string> Scored { get; init; } = [];
}

/// <summary>
/// Reads a save the 1999 game wrote, header and state both.
/// </summary>
/// <remarks>
/// <para>
/// A retail <c>.gk3</c> is a fixed header, then a summary, then a thumbnail as a plain PNG,
/// and then — at the byte the header's own <c>Data Offset</c> names — a zlib stream holding
/// the whole of the engine's state. Inside that is a table of the 160 classes the game knows
/// and then every object it owns, written by each class's persist method in a fixed order
/// with no field names: binary mode drops the names the text format writes.
/// </para>
/// <para>
/// This reads the two blocks the story is actually kept in — the game state and the score
/// table — and finds each by parsing rather than by offset, because nothing in the file says
/// where an object begins. See <see cref="ReadState"/> for what makes a match certain.
/// </para>
/// </remarks>
public static class OriginalSaveFile
{
    /// <summary>The words the original files an inventory item under.</summary>
    /// <remarks>
    /// Read off the executable's own table at <c>0x675520</c>. Two of them mean somebody is
    /// carrying it; the rest mean it is in the world, spent, or not yet anywhere.
    /// </remarks>
    private static readonly string[] Statuses =
        ["BothHave", "GabeHas", "GraceHas", "Placed", "NotPlaced", "Used", "Normal"];

    /// <summary>Reads a save's header.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The header, or null when the file is not an original save.</returns>
    public static OriginalSaveHeader? ReadHeader(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var at = new Cursor(bytes);

        // The last four letters are compared without case: the reference writes SAVE and the
        // retail game wrote Save, and three real saves are how the difference was found.
        if (bytes.Length < 16 ||
            !"GK3!"u8.SequenceEqual(bytes.AsSpan(0, 4)) ||
            !Encoding.ASCII.GetString(bytes, 4, 4).Equals("Save", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            at.Skip(8);
            at.Int();                                   // save version
            int headerSize = at.Int();

            at.Int();                                   // product version, always 65536
            at.Int();                                   // the build number of the exe
            at.String();                                // Release or Production
            at.Int();                                   // the screen it was saved at, across
            at.Int();                                   // and down
            at.Int();                                   // how many saves this playthrough

            // Eight bytes nobody has explained, and then what reads as a Windows SYSTEMTIME.
            at.Skip(8);
            int year = at.Short();
            int month = at.Short();
            at.Short();                                 // day of the week, which the date says
            int day = at.Short();
            int hour = at.Short();
            int minute = at.Short();
            int second = at.Short();
            at.Short();                                 // milliseconds

            at.Skip(32);                                // the machine it was saved on
            at.Skip(32);                                // and who was logged in
            at.Skip(100);                               // the 1999 copyright line

            // The header's stated size runs to exactly here, which is three fields into what
            // follows: the original counted its own version, compression flag and data offset
            // as part of the header it was about to write. Checked rather than assumed,
            // because everything after it is read by walking rather than by seeking.
            if (at.At + 9 != 16 + headerSize)
            {
                return null;
            }

            at.Int();                                   // the persist header's own version
            bool compressed = at.Byte() != 0;
            int body = at.Int();

            string title = at.String();
            string location = at.String();
            string when = at.String();
            int score = at.Int();
            int maximum = at.Int();
            at.Int();                                   // which CD it wanted next

            int thumbnail = at.Int();

            byte[]? picture = thumbnail > 0 && at.At + thumbnail <= bytes.Length
                ? bytes.AsSpan(at.At, thumbnail).ToArray()
                : null;

            if (!Timeblock.TryParse(when, out Timeblock timeblock))
            {
                return null;
            }

            return new OriginalSaveHeader(
                title,
                location.ToUpperInvariant(),
                timeblock,
                score,
                maximum,
                Stamp(year, month, day, hour, minute, second),
                picture,
                compressed && body > 0 && body < bytes.Length ? body : 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The moment a save was written, as the machine that wrote it read the clock.</summary>
    private static DateTimeOffset Stamp(int year, int month, int day, int hour, int minute, int second)
    {
        // A local time with no zone beside it. Kept as one rather than guessed at, and left
        // alone when it is not a date at all — a save whose header this engine has misread
        // must not throw its way out of a directory scan.
        try
        {
            return new DateTimeOffset(
                new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Decompresses a save's state.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="header">Its header, which says where the state begins.</param>
    /// <returns>The state, or null when there is none or it will not decompress.</returns>
    public static byte[]? ReadBody(byte[] bytes, OriginalSaveHeader header)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(header);

        if (header.Body <= 0 || header.Body >= bytes.Length)
        {
            return null;
        }

        try
        {
            using var source = new MemoryStream(bytes, header.Body, bytes.Length - header.Body);
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            using var into = new MemoryStream();

            inflate.CopyTo(into);

            return into.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the story out of a save's state.
    /// </summary>
    /// <param name="body">The decompressed state.</param>
    /// <param name="events">Every score event the game knows, for finding its table.</param>
    /// <returns>The state, or null when neither block could be found.</returns>
    /// <remarks>
    /// Nothing in the stream says where an object starts, so both blocks are found by trying
    /// to parse one at every offset and keeping the first that comes out whole. That sounds
    /// weak and is not: the game state is thirteen members deep and ends in eighty-three rooms
    /// each carrying seventeen timeblocks, and every inventory line has to name one of six
    /// words the executable knows. Nothing else in a megabyte of state parses as that.
    /// </remarks>
    public static OriginalSaveState? ReadState(byte[] body, IReadOnlyCollection<string> events)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(events);

        OriginalSaveState? state = FindState(body);

        if (state is null)
        {
            return null;
        }

        return state with { Scored = FindScores(body, events) };
    }

    /// <summary>Finds and reads the game state.</summary>
    private static OriginalSaveState? FindState(byte[] body)
    {
        for (int at = 0; at + 16 < body.Length; at++)
        {
            // The cheap gate: two short strings — the items the two of them have in hand —
            // and then a count. Almost every offset in the file fails on the first of these.
            var probe = new Cursor(body, at);

            if (!probe.TryString(64, out _) ||
                !probe.TryString(64, out _) ||
                !probe.TryCount(20_000, out _))
            {
                continue;
            }

            if (TryState(body, at) is { } state)
            {
                return state;
            }
        }

        return null;
    }

    /// <summary>Reads the game state at an offset, if that is what is there.</summary>
    private static OriginalSaveState? TryState(byte[] body, int start)
    {
        try
        {
            var at = new Cursor(body, start);

            string gabriel = at.String();
            string grace = at.String();

            // mNVMap. One entry per noun, verb and ego the player has ever counted against,
            // topics included — a topic is a verb whose name begins T_.
            int entries = at.Int();

            if (entries is < 0 or > 20_000)
            {
                return null;
            }

            var counts = new List<OriginalCount>(entries);

            for (int i = 0; i < entries; i++)
            {
                string noun = at.String();
                string verb = at.String();
                int ego = at.Byte();
                int count = at.Byte();

                if (ego > 1)
                {
                    return null;
                }

                counts.Add(new OriginalCount(noun, verb, ego != 0, count));
            }

            // Whether the room's sheep was compiled, and then the sheep itself. Both are
            // skipped: this engine compiles its own, and a room's script is not story state.
            if (at.Byte() > 1)
            {
                return null;
            }

            at.String(1 << 24);

            Dictionary<string, int> chat = at.Bytes();
            Dictionary<string, int> flags = at.Bytes();
            Dictionary<string, string> actors = at.Strings();
            Dictionary<string, int> map = at.Bytes();
            Dictionary<string, int> visited = at.Bytes();
            Dictionary<string, int> variables = at.Ints();
            Dictionary<string, string> items = at.Strings();

            // Where the player has been, room by room and hour by hour.
            int rooms = at.Int();

            if (rooms is < 0 or > 4_000)
            {
                return null;
            }

            var visits = new List<(string, string, int)>();

            for (int i = 0; i < rooms; i++)
            {
                string room = at.String();
                int blocks = at.Int();

                if (blocks is < 0 or > 200)
                {
                    return null;
                }

                for (int j = 0; j < blocks; j++)
                {
                    string when = at.String();
                    int been = at.Int();

                    if (been > 0)
                    {
                        visits.Add((room, when, been));
                    }
                }
            }

            string advanced = at.String();

            // What makes this a match rather than a coincidence. The counts are the game's
            // own data and the same in every retail save; the statuses are the executable's
            // own six words. A block that satisfies all of this is the game state.
            if (items.Count < 50 || flags.Count < 50 || chat.Count < 100 ||
                actors.Count < 10 || rooms < 20 || visits.Count == 0 ||
                items.Values.Any(v => !Statuses.Contains(v, StringComparer.Ordinal)))
            {
                return null;
            }

            return new OriginalSaveState
            {
                GabrielItem = Item(gabriel),
                GraceItem = Item(grace),
                Counts = counts,
                ChatCounts = Set(chat),
                Flags = [.. flags.Where(f => f.Value != 0).Select(f => f.Key)],
                ActorLocations = actors
                    .Where(a => a.Value.Length > 0 && !a.Value.Equals("non", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase),
                OnTheMap = [.. map.Where(m => m.Value != 0).Select(m => m.Key)],
                Visited = [.. visited.Where(v => v.Value != 0).Select(v => v.Key)],
                Variables = Set(variables),
                Items = items,
                Visits = visits,
                AdvancedTo = advanced,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The item somebody has in hand, with the original's word for none taken out.</summary>
    private static string Item(string named) =>
        named.Length == 0 || named.Equals("NONE", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : named;

    /// <summary>The entries that are not nought, which is the only thing a count says.</summary>
    private static Dictionary<string, int> Set(Dictionary<string, int> counted) =>
        counted
            .Where(c => c.Value != 0)
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds and reads the score table.</summary>
    /// <param name="body">The decompressed state.</param>
    /// <param name="events">Every score event the game knows.</param>
    /// <returns>The events that have been earned, or nothing when the table was not found.</returns>
    private static List<string> FindScores(byte[] body, IReadOnlyCollection<string> events)
    {
        var known = new HashSet<string>(events, StringComparer.OrdinalIgnoreCase);

        if (known.Count == 0)
        {
            return [];
        }

        for (int at = 0; at + 8 < body.Length; at++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));

            // The corpus has 385 of them and every retail save writes all of them. The band
            // is wide enough to survive a version that knows a few more or fewer.
            if (count is < 100 or > 5_000)
            {
                continue;
            }

            if (TryScores(body, at, count, known) is { } earned)
            {
                return earned;
            }
        }

        return [];
    }

    /// <summary>Reads the score table at an offset, if that is what is there.</summary>
    private static List<string>? TryScores(
        byte[] body, int start, int count, HashSet<string> known)
    {
        try
        {
            var at = new Cursor(body, start + 4);
            var earned = new List<string>();
            int recognised = 0;

            for (int i = 0; i < count; i++)
            {
                string name = at.String(120);
                int got = at.Byte();

                if (got > 1)
                {
                    return null;
                }

                if (known.Contains(name))
                {
                    recognised++;
                }

                if (got != 0)
                {
                    earned.Add(name);
                }
            }

            // The table is the game's own list of every event there is, so almost all of it
            // has to be a name this engine also knows. A handful may not: the shipped table
            // is recovered rather than original — see Assets/Story/Scores.txt.
            return recognised >= count * 9 / 10 ? earned : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// A place in the save, and the shapes the original writes.
    /// </summary>
    /// <remarks>
    /// Every string is a 32-bit length, its bytes, and a terminating nul the length does not
    /// count. Everything else is a little-endian int, a short or a byte. There is no padding
    /// anywhere: a string of ten characters leaves the next int on an odd address.
    /// </remarks>
    private struct Cursor(byte[] bytes, int at = 0)
    {
        private readonly byte[] _bytes = bytes;

        /// <summary>Where the cursor is.</summary>
        public int At { get; private set; } = at;

        /// <summary>Passes over some bytes.</summary>
        /// <param name="count">How many.</param>
        public void Skip(int count)
        {
            Need(count);
            At += count;
        }

        /// <summary>Reads a byte.</summary>
        /// <returns>Its value.</returns>
        public byte Byte()
        {
            Need(1);
            return _bytes[At++];
        }

        /// <summary>Reads a sixteen-bit number.</summary>
        /// <returns>Its value.</returns>
        public int Short()
        {
            Need(2);
            int value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(At));
            At += 2;
            return value;
        }

        /// <summary>Reads a thirty-two-bit number.</summary>
        /// <returns>Its value.</returns>
        public int Int()
        {
            Need(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(At));
            At += 4;
            return value;
        }

        /// <summary>Reads a string.</summary>
        /// <param name="longest">The most characters one may have.</param>
        /// <returns>Its text.</returns>
        public string String(int longest = 512)
        {
            int length = Int();

            if (length < 0 || length > longest)
            {
                throw new InvalidDataException($"A string of {length} characters.");
            }

            Need(length + 1);

            if (_bytes[At + length] != 0)
            {
                throw new InvalidDataException("A string with no terminator.");
            }

            string value = Encoding.Latin1.GetString(_bytes, At, length);
            At += length + 1;

            return value;
        }

        /// <summary>Reads a string, when there is one to read.</summary>
        /// <param name="longest">The most characters one may have.</param>
        /// <param name="value">Its text.</param>
        /// <returns>True when one was read.</returns>
        public bool TryString(int longest, out string value)
        {
            value = string.Empty;

            try
            {
                value = String(longest);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        /// <summary>Reads a count, when there is a believable one to read.</summary>
        /// <param name="most">The largest it may be.</param>
        /// <param name="value">The count.</param>
        /// <returns>True when one was read.</returns>
        public bool TryCount(int most, out int value)
        {
            value = 0;

            if (At + 4 > _bytes.Length)
            {
                return false;
            }

            value = Int();

            return value >= 0 && value <= most;
        }

        /// <summary>Reads a counted table of names against bytes.</summary>
        /// <returns>The table.</returns>
        public Dictionary<string, int> Bytes() => Table(wide: false);

        /// <summary>Reads a counted table of names against numbers.</summary>
        /// <returns>The table.</returns>
        public Dictionary<string, int> Ints() => Table(wide: true);

        /// <summary>Reads a counted table of names against names.</summary>
        /// <returns>The table.</returns>
        public Dictionary<string, string> Strings()
        {
            int count = Count();
            var table = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                string key = String();
                table[key] = String();
            }

            return table;
        }

        private Dictionary<string, int> Table(bool wide)
        {
            int count = Count();
            var table = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                string key = String();
                table[key] = wide ? Int() : Byte();
            }

            return table;
        }

        private int Count()
        {
            int count = Int();

            return count is < 0 or > 40_000
                ? throw new InvalidDataException($"A table of {count} entries.")
                : count;
        }

        private readonly void Need(int count)
        {
            if (At < 0 || count < 0 || At + count > _bytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
        }
    }
}

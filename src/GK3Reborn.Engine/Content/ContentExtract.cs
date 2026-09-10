using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;

namespace GK3Reborn.Content;

/// <summary>
/// Writes what the game would read back out as files, so that it can be replaced.
/// </summary>
public static class ContentExtract
{
    /// <summary>What one run wrote.</summary>
    /// <param name="Written">How many files were written.</param>
    /// <param name="Bytes">How many bytes they came to.</param>
    /// <param name="Failed">How many entries could not be written.</param>
    public readonly record struct Result(int Written, long Bytes, int Failed)
    {
        /// <summary>Adds two runs together.</summary>
        /// <param name="a">One run.</param>
        /// <param name="b">The other.</param>
        /// <returns>Their total.</returns>
        public static Result operator +(Result a, Result b) =>
            new(a.Written + b.Written, a.Bytes + b.Bytes, a.Failed + b.Failed);

        /// <summary>Adds two runs together.</summary>
        /// <param name="left">One run.</param>
        /// <param name="right">The other.</param>
        /// <returns>Their total.</returns>
        public static Result Add(Result left, Result right) => left + right;
    }

    /// <summary>Unpacks the ReBarn volumes into a directory of overridable files.</summary>
    /// <param name="packs">The packs, already open.</param>
    /// <param name="output">Where to write. Created if it is not there.</param>
    /// <param name="kinds">Only these kinds, or null for all of them.</param>
    /// <param name="name">Only entries with this bare name, or null for all of them.</param>
    /// <param name="asPng">Whether to decode block-compressed textures to PNG.</param>
    /// <param name="say">Receives a line per kind and a line per failure.</param>
    /// <returns>What was written.</returns>
    public static Result FromPacks(
        RebarnContent packs,
        string output,
        IReadOnlyCollection<RebarnKind>? kinds,
        string? name,
        bool asPng,
        Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(say);

        string? wanted = name is { Length: > 0 } ? Path.GetFileName(name) : null;
        var total = new Result();

        foreach (IGrouping<RebarnKind, (RebarnArchive Pack, RebarnEntry Entry)> group in packs.Entries
                     .Where(e => kinds is null || kinds.Contains(e.Entry.Kind))
                     .Where(e => wanted is null || Matches(e.Entry, wanted))
                     .GroupBy(e => e.Entry.Kind)
                     .OrderBy(g => g.Key))
        {
            string directory = Path.Combine(output, RebarnFormat.DirectoryOf(group.Key));
            Directory.CreateDirectory(directory);

            var here = new Result();

            foreach ((RebarnArchive pack, RebarnEntry entry) in group
                         .OrderBy(e => e.Entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                here += One(pack, entry, directory, asPng, say);
            }

            say($"  {RebarnFormat.DirectoryOf(group.Key),-15} {here.Written,6} file(s), "
                + $"{here.Bytes / (1024.0 * 1024):F1} MB");

            total += here;
        }

        return total;
    }

    private static Result One(
        RebarnArchive pack, RebarnEntry entry, string directory, bool asPng, Action<string> say)
    {
        string bare = Path.GetFileNameWithoutExtension(entry.Name);

        try
        {
            if (asPng && entry.Payload == RebarnPayload.Dds)
            {
                byte[] png = PngWriter.Encode(
                    Readable(DdsFile.Read(pack.ReadMapped(entry), entry.Name)));

                string path = Path.Combine(directory, bare + ".png");
                File.WriteAllBytes(path, png);

                return new Result(1, png.Length, 0);
            }

            // Audio names such as LINE.QR1 have meaningful suffixes. Add a final WAV
            // wrapper so extracting a pack produces the exact enhanced/ layout that the
            // override layer and packer consume on the next round trip.
            string fileName = entry.Kind == RebarnKind.Audio
                ? entry.Name + ".wav"
                : entry.Name;
            string file = Path.Combine(directory, fileName);

            using (FileStream stream = File.Create(file))
            {
                pack.CopyTo(entry, stream);
            }

            return new Result(1, entry.Length, 0);
        }
        catch (Exception ex) when (ex is FormatParseException or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            // One entry, not the run. A pack holds thousands and the point of extracting is
            // to get at one of them; stopping on the first that will not decode would mean
            // nobody could reach the rest.
            say($"  {RebarnFormat.DirectoryOf(entry.Kind)}/{entry.Name}: {ex.Message}");

            return new Result(0, 0, 1);
        }
    }

    private static bool Matches(RebarnEntry entry, string wanted)
    {
        // A localised asset's extension is part of its identity, exactly as an audio
        // sequence number is: ESTRINGS.TXT and ESTRINGS.SIF would be one name without it,
        // and a French bitmap would answer a question about a French model. It has no
        // editable wrapper, so the match is simply exact.
        if (entry.Kind is RebarnKind.Localized or RebarnKind.Room)
        {
            return Path.GetFileName(entry.Name).Equals(
                Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase);
        }

        if (entry.Kind != RebarnKind.Audio)
        {
            return Path.GetFileNameWithoutExtension(entry.Name).Equals(
                Path.GetFileNameWithoutExtension(wanted), StringComparison.OrdinalIgnoreCase);
        }

        string query = Path.GetFileName(wanted);
        if (Path.GetFileName(entry.Name).Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Also accept the editable wrapper spelling, e.g. DOOR.WAV.wav. Exact identity
        // is tested first because DOOR.WAV by itself is an original name, not a wrapper.
        return query.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(entry.Name).Equals(query[..^4], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Puts a decoded block texture's channels back the way its source PNG had them.
    /// </summary>
    /// <param name="image">What the blocks decoded to.</param>
    /// <returns>An image that can be edited and packed again.</returns>
    private static DecodedImage Readable(CompressedImage image)
    {
        DecodedImage decoded = BlockDecoder.Decode(image);

        if (image.Format is not (BlockFormat.Bc5Unorm or BlockFormat.Bc4Unorm))
        {
            return decoded;
        }

        byte[] pixels = [.. decoded.Pixels];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (image.Format == BlockFormat.Bc4Unorm)
            {
                pixels[i + 1] = pixels[i];
                pixels[i + 2] = pixels[i];
                continue;
            }

            // z = sqrt(1 - x² - y²), in the 0..1 encoding the channels are stored in.
            double x = ((pixels[i] / 255.0) * 2) - 1;
            double y = ((pixels[i + 1] / 255.0) * 2) - 1;
            double z = Math.Sqrt(Math.Max(0, 1 - (x * x) - (y * y)));

            pixels[i + 2] = (byte)Math.Clamp(Math.Round(((z + 1) / 2) * 255), 0, 255);
        }

        return decoded with { Pixels = pixels };
    }

    /// <summary>Unpacks the game's own archives into a directory of overridable files.</summary>
    /// <param name="archives">The archives, already open.</param>
    /// <param name="output">Where to write. Created if it is not there.</param>
    /// <param name="extensions">Only these extensions, dot optional, or null for all.</param>
    /// <param name="name">Only assets with this bare name, or null for all of them.</param>
    /// <param name="say">Receives a line per failure.</param>
    /// <returns>What was written.</returns>
    public static Result FromGame(
        GameArchives archives,
        string output,
        IReadOnlyCollection<string>? extensions,
        string? name,
        Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(say);

        string[] suffixes = [.. (extensions ?? [])
            .Select(e => e.StartsWith('.') ? e : "." + e)];

        string? wanted = name is { Length: > 0 } ? Path.GetFileNameWithoutExtension(name) : null;

        Directory.CreateDirectory(output);

        var total = new Result();

        foreach (string asset in archives.Names())
        {
            if (suffixes.Length > 0 &&
                !suffixes.Any(s => asset.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (wanted is not null && !Path.GetFileNameWithoutExtension(asset)
                    .Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (archives.Read(asset) is not { } bytes)
                {
                    continue;
                }

                File.WriteAllBytes(Path.Combine(output, asset), bytes);
                total += new Result(1, bytes.Length, 0);
            }
            catch (Exception ex) when (ex is FormatParseException or IOException
                                          or UnauthorizedAccessException)
            {
                say($"  {asset}: {ex.Message}");
                total += new Result(0, 0, 1);
            }
        }

        return total;
    }
}

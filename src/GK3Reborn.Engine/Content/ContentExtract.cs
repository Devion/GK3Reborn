using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;

namespace GK3Reborn.Content;

/// <summary>
/// Writes what the game would read back out as files, so that it can be replaced.
/// </summary>
/// <remarks>
/// <para>
/// The other half of <see cref="ContentOverrides"/>. Overriding a texture means knowing
/// what is there to override — its name, its size, what it looks like — and a fifteen-
/// gigabyte pack and a set of 1999 archives are not things anybody can open in a paint
/// program. This unpacks either into the layout the override layer reads back, so the
/// round trip is: extract, edit in place, run.
/// </para>
/// <para>
/// <strong>The directory structure comes from the kind, not from the pack.</strong> A pack
/// stores no paths — an entry answers to its kind and its bare name, which is the whole
/// point of the format — but <see cref="RebarnFormat.DirectoryOf"/> is the same mapping
/// <c>enhanced/</c> uses and the same one <see cref="ContentOverrides"/> reads, so
/// <c>textures/R25WALLS.dds</c> comes out where <c>textures/R25WALLS.dds</c> goes back in.
/// Nothing has to be moved and no manifest has to be kept.
/// </para>
/// <para>
/// <strong>PNG is a real conversion, not a rename.</strong> Asked for it, a block-
/// compressed texture is decoded and its channels are put back the way the source had
/// them: a BC5 normal map has lost its blue, which is reconstructed from the other two,
/// and a BC4 height map is one channel that the source stored as grey across three. A
/// straight dump of what the blocks decode to would give a normal map that is black in
/// blue and a height map that is red, both of which load, and neither of which is the
/// picture that was compressed.
/// </para>
/// </remarks>
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

        string? wanted = name is { Length: > 0 } ? Path.GetFileNameWithoutExtension(name) : null;
        var total = new Result();

        foreach (IGrouping<RebarnKind, (RebarnArchive Pack, RebarnEntry Entry)> group in packs.Entries
                     .Where(e => kinds is null || kinds.Contains(e.Entry.Kind))
                     .Where(e => wanted is null || Path.GetFileNameWithoutExtension(e.Entry.Name)
                         .Equals(wanted, StringComparison.OrdinalIgnoreCase))
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

            string file = Path.Combine(directory, entry.Name);

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

    /// <summary>
    /// Puts a decoded block texture's channels back the way its source PNG had them.
    /// </summary>
    /// <param name="image">What the blocks decoded to.</param>
    /// <returns>An image that can be edited and packed again.</returns>
    /// <remarks>
    /// BC5 keeps two channels and BC4 one, so what comes out of the decoder is not the
    /// picture that went in: a normal map's blue is gone and a height map's grey is in red
    /// alone. Both are reversible — the blue of a unit normal is fixed by the other two,
    /// and the height maps were measured to be grey stored as RGB across the whole corpus,
    /// which is why they are BC4 in the first place. Anything else is left alone.
    /// </remarks>
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
    /// <remarks>
    /// Flat, and under a directory of its own. These are the 1999 assets, matched by their
    /// whole file name rather than by a kind, so no directory would tell the override layer
    /// anything it does not already know from the extension — and putting forty thousand
    /// files beside a dozen texture directories would bury them.
    /// </remarks>
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

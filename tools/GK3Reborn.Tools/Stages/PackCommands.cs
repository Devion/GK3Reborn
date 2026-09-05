using System.Globalization;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// The ReBarn subcommands: build a pack, look inside one, take one apart, check one.
/// </summary>
/// <remarks>
/// These parse their own arguments rather than going through the shared option record,
/// because the packer has a dozen flags of its own — a cap per kind, a plan, an encoder
/// path — and adding all of them to a record every other command shares would make the
/// help for those commands worse to no purpose.
/// </remarks>
public static class PackCommands
{
    /// <summary>The commands this handles.</summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["pack-content", "pack-plan", "pack-list", "pack-extract", "pack-verify"];

    /// <summary>Runs one of them.</summary>
    /// <param name="args">The whole command line, the command included.</param>
    /// <returns>A process exit code.</returns>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args[0] switch
        {
            "pack-content" => Pack(args),
            "pack-plan" => Plan(args),
            "pack-list" => List(args),
            "pack-extract" => Extract(args),
            "pack-verify" => Verify(args),
            _ => Usage($"Unknown pack command: {args[0]}"),
        };
    }

    private static int Pack(string[] args)
    {
        string? workspace = Flag(args, "--workspace");
        string? output = Flag(args, "--output");
        string? texconv = Flag(args, "--texconv");
        string? only = Flag(args, "--kinds");
        string? fromSource = Flag(args, "--only");
        bool force = Has(args, "--force");
        bool dryRun = Has(args, "--dry-run");
        bool encodeOnly = Has(args, "--encode-only");
        bool gpu = !Has(args, "--no-gpu");
        bool single = Has(args, "--single-volume");
        bool sizePlan = !Has(args, "--no-size-plan");

        if (workspace is null)
        {
            return Usage("pack-content requires --workspace.");
        }

        // Beside the game by default, which is where the engine looks for them.
        output ??= Path.Combine(workspace, "build", "pack");

        // The shared plan, then whichever languages have been extracted. Appended rather
        // than merged into the default, because which languages exist is a fact about the
        // workspace and not about this build — see ContentPackStage.LanguagePlan.
        List<PackKind> plan =
        [
            .. ContentPackStage.DefaultPlan,
            .. Has(args, "--no-languages") ? [] : ContentPackStage.LanguagePlan(workspace),
        ];

        if (only is not null)
        {
            HashSet<RebarnKind> wanted = [];

            foreach (string name in only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (RebarnFormat.KindOf(name) is not { } kind)
                {
                    return Usage($"--kinds: {name} names no kind of content.");
                }

                wanted.Add(kind);
            }

            plan = [.. plan.Where(k => wanted.Contains(k.Kind))];
        }

        // --only enhanced/trees packs one source directory and leaves the rest of the plan
        // alone. Which matters because the trees are three kinds out of one directory: any
        // filter by kind that reaches them also drags in every enhanced texture in the game,
        // and re-encoding six thousand of those to check that a tree packed is an hour.
        if (fromSource is { Length: > 0 })
        {
            string under = fromSource.Replace(Path.DirectorySeparatorChar, '/');
            plan = [.. plan.Where(
                k => k.Source.Contains(under, StringComparison.OrdinalIgnoreCase))];

            if (plan.Count == 0)
            {
                return Usage($"--only: no kind in the plan is packed from {fromSource}.");
            }
        }

        // --cap normals=512,height=256 overrides the defaults one kind at a time.
        if (Flag(args, "--cap") is { } caps)
        {
            foreach (string pair in caps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = pair.Split('=', 2);

                if (parts.Length != 2 ||
                    RebarnFormat.KindOf(parts[0]) is not { } kind ||
                    !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int cap))
                {
                    return Usage($"--cap: {pair} is not a kind and a size, such as normals=1024.");
                }

                for (int i = 0; i < plan.Count; i++)
                {
                    if (plan[i].Kind == kind)
                    {
                        plan[i] = plan[i] with { Cap = cap };
                    }
                }
            }
        }

        // One file for the shared content, for a machine that would rather have one. The
        // languages keep their own volumes whatever this says: the game opens exactly one
        // of them and reads the rest of the packs beside it, so folding French into Reborn
        // would put French in front of every installation whether or not anybody asked.
        if (single)
        {
            plan = [.. plan.Select(k => k.Volume.StartsWith("Reborn_", StringComparison.Ordinal)
                ? k
                : k with { Volume = "Reborn" })];
        }

        bool ok = new ContentPackStage(Console.WriteLine).Run(
            workspace, output, plan, texconv, force, dryRun, encodeOnly, gpu, sizePlan);

        if (ok && !dryRun && !encodeOnly)
        {
            Console.WriteLine();
            Console.WriteLine($"Copy the .rebarn files from {output} to the directory the game runs from.");
        }

        return ok ? 0 : 1;
    }

    private static int Plan(string[] args)
    {
        if (Flag(args, "--workspace") is not { } workspace)
        {
            return Usage("pack-plan requires --workspace.");
        }

        int multiplier = int.TryParse(
            Flag(args, "--density"), CultureInfo.InvariantCulture, out int m) && m > 0 ? m : 4;

        int floor = int.TryParse(
            Flag(args, "--floor"), CultureInfo.InvariantCulture, out int f) && f >= 4 ? f : 512;

        new TextureSizePlanStage(Console.WriteLine).Run(
            workspace, Flag(args, "--source"), multiplier, floor);

        return 0;
    }

    private static int List(string[] args)
    {
        if (Flag(args, "--input") is not { } input)
        {
            return Usage("pack-list requires --input, a .rebarn file or the directory holding them.");
        }

        string? filter = Flag(args, "--kinds");
        bool names = Has(args, "--names");

        using RebarnContentPacks packs = RebarnContentPacks.Open(input);

        if (packs.Archives.Count == 0)
        {
            Console.Error.WriteLine($"No ReBarn packs at {input}.");
            return 1;
        }

        foreach (RebarnArchive pack in packs.Archives)
        {
            var built = new DateTime(pack.Header.BuiltUtcTicks, DateTimeKind.Utc);

            Console.WriteLine($"{pack.Name}: {pack.Count} entries, "
                + $"{pack.Length / (1024.0 * 1024 * 1024):F2} GB, volume {pack.Header.Volume}, "
                + $"built {built:yyyy-MM-dd HH:mm} UTC");

            foreach (IGrouping<RebarnKind, RebarnEntry> group in pack.Entries
                         .GroupBy(e => e.Kind)
                         .OrderBy(g => g.Key))
            {
                if (filter is not null && RebarnFormat.KindOf(filter) != group.Key)
                {
                    continue;
                }

                long bytes = group.Sum(e => e.StoredLength);

                Console.WriteLine($"  {RebarnFormat.DirectoryOf(group.Key),-10} {group.Count(),6} "
                    + $"{bytes / (1024.0 * 1024):F0} MB");

                if (!names)
                {
                    continue;
                }

                foreach (RebarnEntry entry in group.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"      {entry.Name,-40} {entry.StoredLength,10} "
                        + $"{entry.Compression} {entry.Payload}");
                }
            }
        }

        return 0;
    }

    private static int Extract(string[] args)
    {
        if (Flag(args, "--input") is not { } input)
        {
            return Usage("pack-extract requires --input.");
        }

        if (Flag(args, "--output") is not { } output)
        {
            return Usage("pack-extract requires --output, a directory to write into.");
        }

        string? one = Flag(args, "--name");
        string? kindName = Flag(args, "--kinds");
        RebarnKind? kind = kindName is null ? null : RebarnFormat.KindOf(kindName);

        if (kindName is not null && kind is null)
        {
            return Usage($"--kinds: {kindName} names no kind of content.");
        }

        using RebarnContentPacks packs = RebarnContentPacks.Open(input);
        int written = 0;

        foreach (RebarnArchive pack in packs.Archives)
        {
            foreach (RebarnEntry entry in pack.Entries)
            {
                if (kind is not null && entry.Kind != kind)
                {
                    continue;
                }

                if (one is not null &&
                    !Path.GetFileNameWithoutExtension(entry.Name)
                        .Equals(Path.GetFileNameWithoutExtension(one), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string directory = Path.Combine(output, RebarnFormat.DirectoryOf(entry.Kind));
                Directory.CreateDirectory(directory);

                using FileStream file = File.Create(Path.Combine(directory, entry.Name));
                pack.CopyTo(entry, file);
                written++;
            }
        }

        Console.WriteLine($"Wrote {written} file(s) to {output}.");
        return written > 0 ? 0 : 1;
    }

    private static int Verify(string[] args)
    {
        if (Flag(args, "--input") is not { } input)
        {
            return Usage("pack-verify requires --input.");
        }

        using RebarnContentPacks packs = RebarnContentPacks.Open(input);

        if (packs.Archives.Count == 0)
        {
            Console.Error.WriteLine($"No ReBarn packs at {input}.");
            return 1;
        }

        int bad = 0;
        int checkedEntries = 0;
        int decoded = 0;

        foreach (RebarnArchive pack in packs.Archives)
        {
            // The index checksum was already verified on open; this reads every entry's
            // bytes, which is the only thing that can say the data section survived.
            foreach (RebarnEntry entry in pack.Entries)
            {
                checkedEntries++;

                if (!pack.Verify(entry))
                {
                    bad++;
                    Console.Error.WriteLine(
                        $"{pack.Name}: {RebarnFormat.DirectoryOf(entry.Kind)}/{entry.Name} "
                        + $"does not match its checksum.");

                    continue;
                }

                if (entry.Payload != RebarnPayload.Dds)
                {
                    continue;
                }

                // And that the game's own reader accepts it. A CRC only says the bytes are
                // the bytes that were written; a format the loader refuses is written
                // perfectly and falls back silently at runtime, which is worse.
                try
                {
                    CompressedImage image = DdsFile.Read(pack.ReadMapped(entry), entry.Name);

                    if (image.Width <= 0 || image.Mips <= 0)
                    {
                        throw new FormatParseException($"{entry.Name} has no levels.");
                    }

                    decoded++;
                }
                catch (FormatParseException ex)
                {
                    bad++;
                    Console.Error.WriteLine(
                        $"{pack.Name}: {RebarnFormat.DirectoryOf(entry.Kind)}/{entry.Name} "
                        + $"will not decode: {ex.Message}");
                }
            }

            Console.WriteLine($"{pack.Name}: {pack.Count} entries checked.");
        }

        Console.WriteLine(bad == 0
            ? $"All {checkedEntries} entries are intact; {decoded} decode as textures."
            : $"{bad} of {checkedEntries} entries are damaged or unreadable.");

        return bad == 0 ? 0 : 1;
    }

    private static string? Flag(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool Has(string[] args, string name) =>
        args.Skip(1).Any(a => a.Equals(name, StringComparison.Ordinal));

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(
            """

            pack-content  --workspace <dir> [--output <dir>] [--kinds a,b] [--cap normals=1024]
                          [--single-volume] [--no-languages] [--force] [--dry-run]
                          [--encode-only] [--no-gpu] [--no-size-plan] [--texconv <path>]
                Encode the enhanced content to DDS and pack it into ReBarn volumes.
                Uses manifests/pack-sizes.json when it is there, so each texture is
                packed at the size its world area justifies rather than all at 2048.
                Whatever extract-localized has left under enhanced/localized is packed
                into Reborn_<CODE>.rebarn, one volume per language; --no-languages
                leaves those alone.

            pack-plan     --workspace <dir> [--source <GK3 Data>] [--density N] [--floor N]
                Work out that size for every texture and write the manifest. --source
                lets it read the game's own inventory sprite list, whose close-ups must
                not be shrunk. Review the manifest before packing.

            pack-list     --input <file|dir> [--kinds <kind>] [--names]
            pack-extract  --input <file|dir> --output <dir> [--kinds <kind>] [--name NAME]
            pack-verify   --input <file|dir>

            kinds: textures normals orm height emissive models scene-geometry video
                   movie-audio localized manifests audio raw
            """);

        return 2;
    }
}

/// <summary>A set of packs opened for inspection, from a file or a directory.</summary>
/// <remarks>
/// Separate from <c>RebarnContent</c>, which merges the volumes into one namespace and is
/// what the game wants. A tool wants to see each volume as itself.
/// </remarks>
public sealed class RebarnContentPacks : IDisposable
{
    private RebarnContentPacks(IReadOnlyList<RebarnArchive> archives) => Archives = archives;

    /// <summary>The packs, in file-name order.</summary>
    public IReadOnlyList<RebarnArchive> Archives { get; }

    /// <summary>Opens a pack, or every pack in a directory.</summary>
    /// <param name="path">A <c>.rebarn</c> file or a directory holding some.</param>
    /// <returns>The opened packs.</returns>
    public static RebarnContentPacks Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IEnumerable<string> files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*" + RebarnFormat.Extension)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            : [path];

        var archives = new List<RebarnArchive>();

        try
        {
            foreach (string file in files)
            {
                archives.Add(RebarnArchive.Open(file));
            }

            return new RebarnContentPacks(archives);
        }
        catch
        {
            foreach (RebarnArchive archive in archives)
            {
                archive.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (RebarnArchive archive in Archives)
        {
            archive.Dispose();
        }
    }
}

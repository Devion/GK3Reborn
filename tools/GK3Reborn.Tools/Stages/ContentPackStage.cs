using System.Diagnostics;
using System.Globalization;
using System.Text;
using GK3Reborn.Formats.Rebarn;

namespace GK3Reborn.Tools.Stages;

/// <summary>How one kind of content is compressed and where it is packed.</summary>
/// <param name="Kind">What the entries are for.</param>
/// <param name="Source">Directory under the workspace holding the sources.</param>
/// <param name="Format">The DXGI format name texconv is asked for, or null to pack verbatim.</param>
/// <param name="Colour">Whether the source is sRGB-encoded, which decides <c>-srgbi</c>.</param>
/// <param name="Cap">Longest edge the output may have; the source's own size when zero.</param>
/// <param name="Volume">Which volume it is packed into.</param>
public sealed record PackKind(
    RebarnKind Kind,
    string Source,
    string? Format,
    bool Colour,
    int Cap,
    string Volume);

/// <summary>
/// Encodes the enhanced content to DDS and packs it into ReBarn volumes.
/// </summary>
/// <remarks>
/// <para>
/// One command from loose PNGs to the one or two files that ship beside the executable.
/// It exists because the two halves are not independent: the size a channel is encoded at
/// decides how large the pack is, and the pack is what the encoder's output is for.
/// </para>
/// <para>
/// Encoding is <c>texconv</c>, vendored with <c>PbrLab</c>. It has no notion of "cap the
/// longest edge", only an exact width and height, so sources are grouped by the size they
/// will come out at and one process is run for each group. There are about 160 distinct
/// sizes per kind, so that is a few hundred processes rather than eleven thousand.
/// </para>
/// <para>
/// Encoded files are kept in <c>build/</c> and reused. A DDS is only re-encoded when its
/// PNG is newer than it or when the encode parameters changed, which is what makes a second
/// run of this command take minutes instead of hours — and what lets the existing
/// <c>build/textures</c> from earlier compression runs be adopted rather than redone.
/// </para>
/// </remarks>
public sealed class ContentPackStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Where progress is written.</param>
    public ContentPackStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>The default plan: what is encoded how, and which volume it lands in.</summary>
    /// <remarks>
    /// <para>
    /// Colour keeps its full resolution because it is the channel a player looks at. The
    /// three material channels do not: a normal map, an occlusion/roughness map and a height
    /// map all modulate a surface the colour texture has already described, and detail in
    /// them below the colour's own resolution is not resolvable on screen. Capping them is
    /// worth about seventeen gigabytes.
    /// </para>
    /// <para>
    /// Formats follow what each channel actually holds, measured rather than assumed.
    /// Normals are BC5 because the third component is reconstructed in the shader. Height
    /// is BC4 because every height map in the set is grey stored as RGB — one channel of
    /// real information in three channels of file. ORM is BC7 because its three channels
    /// are genuinely different, though only eleven of 2,195 use the third at all.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PackKind> DefaultPlan { get; } =
    [
        new(RebarnKind.Texture, "enhanced/textures", "BC7_UNORM_SRGB", true, 0, "Reborn"),
        new(RebarnKind.Emissive, "enhanced/emissive", "BC7_UNORM_SRGB", true, 0, "Reborn"),
        new(RebarnKind.Model, "enhanced/models", null, false, 0, "Reborn"),
        new(RebarnKind.Video, "enhanced/video", null, false, 0, "Reborn"),
        new(RebarnKind.Normal, "enhanced/normals", "BC5_UNORM", false, 1024, "RebornMaterials"),
        new(RebarnKind.Orm, "enhanced/orm", "BC7_UNORM", false, 1024, "RebornMaterials"),
        new(RebarnKind.Height, "enhanced/height", "BC4_UNORM", false, 512, "RebornMaterials"),
    ];

    /// <summary>Encodes and packs.</summary>
    /// <param name="workspace">The content workspace root.</param>
    /// <param name="output">Where the volumes are written.</param>
    /// <param name="plan">What to pack; <see cref="DefaultPlan"/> when null.</param>
    /// <param name="texconv">Path to texconv.exe, or null to look beside PbrLab.</param>
    /// <param name="force">Re-encode even when a cached DDS is still valid.</param>
    /// <param name="dryRun">Report what would happen and write nothing.</param>
    /// <param name="encodeOnly">Encode to <c>build/</c> but do not write the volumes.</param>
    /// <param name="gpu">Let texconv use the GPU for BC7, which is several times faster.</param>
    /// <returns>True when every volume was written.</returns>
    public bool Run(
        string workspace,
        string output,
        IReadOnlyList<PackKind>? plan = null,
        string? texconv = null,
        bool force = false,
        bool dryRun = false,
        bool encodeOnly = false,
        bool gpu = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);

        IReadOnlyList<PackKind> kinds = plan ?? DefaultPlan;
        string encoder = texconv ?? FindTexconv(workspace);

        var byVolume = new Dictionary<string, List<Packable>>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (PackKind kind in kinds)
        {
            string source = Path.Combine(workspace, kind.Source.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(source))
            {
                _log($"{kind.Kind}: nothing at {kind.Source}");
                continue;
            }

            List<Packable> packable = kind.Format is null
                ? Verbatim(kind, source)
                : Encoded(kind, source, workspace, encoder, force, dryRun, gpu, problems);

            if (packable.Count == 0)
            {
                continue;
            }

            if (!byVolume.TryGetValue(kind.Volume, out List<Packable>? list))
            {
                byVolume[kind.Volume] = list = [];
            }

            list.AddRange(packable);
        }

        foreach (string problem in problems)
        {
            _log($"  ! {problem}");
        }

        if (dryRun)
        {
            foreach ((string volume, List<Packable> items) in byVolume.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                long bytes = items.Where(i => File.Exists(i.Path)).Sum(i => new FileInfo(i.Path).Length);
                _log($"{volume}{RebarnFormat.Extension}: {items.Count} entries, "
                    + $"{Gb(bytes)} of what already exists on disk");
            }

            return true;
        }

        if (encodeOnly)
        {
            _log("Encoded only; no volume written.");
            return true;
        }

        Directory.CreateDirectory(output);
        ushort volumeNumber = 0;
        bool ok = true;

        foreach ((string volume, List<Packable> items) in byVolume.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ok &= WriteVolume(output, volume, volumeNumber++, items, problems);
        }

        if (problems.Count > 0)
        {
            _log($"{problems.Count} file(s) could not be packed; see the lines above.");
        }

        return ok;
    }

    private bool WriteVolume(
        string output, string volume, ushort number, List<Packable> items, List<string> problems)
    {
        var builder = new RebarnBuilder { Volume = number };

        foreach (Packable item in items.OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            if (!File.Exists(item.Path))
            {
                problems.Add($"{item.Name}: {item.Path} is not there, so it is left out.");
                continue;
            }

            // A manifest or a model is worth deflating; a DDS or an MP4 is already
            // compressed and running deflate over one costs a decompression pass on the
            // critical path of a room load to save a few per cent of disk.
            RebarnCompression compression =
                RebarnFormat.PayloadOf(item.Path) is RebarnPayload.Json or RebarnPayload.Raw
                    ? RebarnCompression.Deflate
                    : RebarnCompression.Store;

            if (!builder.AddFile(item.Kind, item.Path, item.Name, compression))
            {
                problems.Add($"{item.Kind} {item.Name} appears twice; the later one is left out.");
            }
        }

        string path = Path.Combine(output, volume + RebarnFormat.Extension);
        var watch = Stopwatch.StartNew();
        long done = 0;
        int lastPercent = -1;

        RebarnVolumeReport report = builder.Write(path, (_, written) =>
        {
            done = written;
            int percent = builder.SourceBytes > 0 ? (int)(done * 100 / builder.SourceBytes) : 100;

            if (percent / 5 != lastPercent / 5)
            {
                lastPercent = percent;
                _log($"  {volume}: {percent}% ({Gb(done)})");
            }
        });

        _log($"{Path.GetFileName(report.Path)}: {report.Count} entries, {Gb(report.Bytes)} "
            + $"in {watch.Elapsed.TotalSeconds:F0} s");

        foreach (RebarnKindReport kind in report.Kinds)
        {
            _log($"    {RebarnFormat.DirectoryOf(kind.Kind),-10} {kind.Count,6}  {Gb(kind.Bytes)}");
        }

        return true;
    }

    private static List<Packable> Verbatim(PackKind kind, string source) =>
        [.. Directory.EnumerateFiles(source)
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .Select(f => new Packable(kind.Kind, Path.GetFileName(f), f))];

    private List<Packable> Encoded(
        PackKind kind,
        string source,
        string workspace,
        string texconv,
        bool force,
        bool dryRun,
        bool gpu,
        List<string> problems)
    {
        string cache = Path.Combine(
            workspace, "build", "rebarn", RebarnFormat.DirectoryOf(kind.Kind));

        // Earlier compression runs left DDS in build/textures, build/normals and
        // build/emissive. Adopting one costs a header read and saves an hour of BC7.
        string legacy = Path.Combine(workspace, "build", RebarnFormat.DirectoryOf(kind.Kind));

        var jobs = new Dictionary<(int Width, int Height, bool Alpha), List<string>>();
        var result = new List<Packable>();

        foreach (string png in Directory.EnumerateFiles(source, "*.PNG"))
        {
            string name = Path.GetFileNameWithoutExtension(png);

            if (PngSize(png) is not { } size)
            {
                problems.Add($"{kind.Kind} {name}: the PNG is truncated or not a PNG, so it is left out.");
                continue;
            }

            (int width, int height, bool alpha) = Target(size, kind.Cap);
            string wanted = name + ".DDS";

            if (Adopt(Path.Combine(legacy, wanted), width, height) is { } adopted && !force)
            {
                result.Add(new Packable(kind.Kind, wanted, adopted));
                continue;
            }

            string cached = Path.Combine(cache, wanted);

            if (!force && Fresh(cached, png, width, height))
            {
                result.Add(new Packable(kind.Kind, wanted, cached));
                continue;
            }

            result.Add(new Packable(kind.Kind, wanted, cached));

            if (!jobs.TryGetValue((width, height, alpha), out List<string>? list))
            {
                jobs[(width, height, alpha)] = list = [];
            }

            list.Add(png);
        }

        int pending = jobs.Sum(j => j.Value.Count);

        _log($"{kind.Kind}: {result.Count} file(s), {pending} to encode to {kind.Format}"
            + (kind.Cap > 0 ? $" capped at {kind.Cap}" : string.Empty));

        if (dryRun || pending == 0)
        {
            return result;
        }

        Directory.CreateDirectory(cache);
        int done = 0;

        foreach (((int width, int height, bool alpha), List<string> files) in
                 jobs.OrderByDescending(j => j.Value.Count))
        {
            Encode(texconv, files, cache, kind, width, height, alpha, gpu, problems);
            done += files.Count;
            _log($"  {kind.Kind}: {done}/{pending} encoded");
        }

        return result;
    }

    private static void Encode(
        string texconv,
        List<string> files,
        string cache,
        PackKind kind,
        int width,
        int height,
        bool alpha,
        bool gpu,
        List<string> problems)
    {
        string listing = Path.Combine(cache, "_inputs.txt");

        // No byte-order mark. texconv reads the list as plain text and takes the mark as
        // part of the first path, so exactly one file in each batch fails with a message
        // about the filename syntax being incorrect and the rest encode perfectly.
        File.WriteAllLines(listing, files, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var arguments = new List<string>
        {
            "-nologo",
            "-y",
            "-m", "0",       // the whole chain, down to one texel
            "-wrap",         // filter the chain as a tiling texture, because most of them are
            "-w", width.ToString(CultureInfo.InvariantCulture),
            "-h", height.ToString(CultureInfo.InvariantCulture),
            "-f", kind.Format!,
            "-ft", "dds",
            "-o", cache,
        };

        if (kind.Colour)
        {
            // The source is an sRGB-encoded PNG. Saying so is what stops texconv converting
            // it into one on the way to an _SRGB block format, which is a whole gamma step
            // of brightness in a file that is valid and loads. See PbrLab/compress.py.
            arguments.Add("-srgbi");
        }

        if (alpha)
        {
            // Keep an alpha-tested texture's coverage constant down the chain, or a chain
            // link or a leaf quietly dissolves with distance.
            arguments.Add("-sepalpha");
            arguments.Add("--keep-coverage");
            arguments.Add("0.5");
        }

        if (gpu && kind.Format!.StartsWith("BC7", StringComparison.Ordinal))
        {
            arguments.Add("-gpu");
            arguments.Add("0");
        }

        arguments.Add("-flist");
        arguments.Add(listing);

        var start = new ProcessStartInfo(texconv)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {texconv}.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        File.Delete(listing);

        if (process.ExitCode != 0)
        {
            problems.Add(
                $"texconv failed with {process.ExitCode} on {files.Count} file(s) at "
                + $"{width}x{height}: {Tail(stderr.Length > 0 ? stderr : stdout)}");
        }
    }

    private static string Tail(string text) =>
        text.Length <= 300 ? text.Trim() : text[^300..].Trim();

    /// <summary>Whether a cached DDS is still what the plan asks for.</summary>
    private static bool Fresh(string dds, string png, int width, int height)
    {
        if (!File.Exists(dds))
        {
            return false;
        }

        if (File.GetLastWriteTimeUtc(dds) < File.GetLastWriteTimeUtc(png))
        {
            return false;
        }

        return Adopt(dds, width, height) is not null;
    }

    /// <summary>Whether a DDS already on disk is the right size to be used as is.</summary>
    private static string? Adopt(string dds, int width, int height)
    {
        if (!File.Exists(dds))
        {
            return null;
        }

        try
        {
            Span<byte> head = stackalloc byte[20];

            using (FileStream stream = File.OpenRead(dds))
            {
                if (stream.Read(head) != head.Length)
                {
                    return null;
                }
            }

            // DDS: magic, then dwSize, dwFlags, dwHeight, dwWidth.
            int ddsHeight = BitConverter.ToInt32(head[12..]);
            int ddsWidth = BitConverter.ToInt32(head[16..]);

            return ddsWidth == width && ddsHeight == height ? dds : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>What size a source comes out at, and whether it carries alpha.</summary>
    /// <remarks>
    /// The cap is on the longest edge and the aspect ratio is kept, rounded to a multiple
    /// of four so that no block is padded. A source already inside the cap is left alone —
    /// this never enlarges anything.
    /// </remarks>
    internal static (int Width, int Height, bool Alpha) Target((int Width, int Height, bool Alpha) size, int cap)
    {
        if (cap <= 0 || (size.Width <= cap && size.Height <= cap))
        {
            return size;
        }

        double scale = (double)cap / Math.Max(size.Width, size.Height);

        return (Blocks(size.Width * scale), Blocks(size.Height * scale), size.Alpha);
    }

    private static int Blocks(double extent) =>
        Math.Max(4, (int)Math.Round(extent / 4, MidpointRounding.AwayFromZero) * 4);

    /// <summary>Reads a PNG's dimensions and colour type without decoding it.</summary>
    /// <remarks>
    /// The IHDR is the first chunk and is always thirteen bytes at offset sixteen. The last
    /// twelve bytes are checked too, because two files in the workspace are truncated: the
    /// header of a half-written PNG is perfectly good and says nothing about the rest.
    /// </remarks>
    internal static (int Width, int Height, bool Alpha)? PngSize(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            if (stream.Length < 45)
            {
                return null;
            }

            Span<byte> head = stackalloc byte[26];

            if (stream.Read(head) != head.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

            if (!head[..8].SequenceEqual(signature))
            {
                return null;
            }

            Span<byte> tail = stackalloc byte[12];
            stream.Position = stream.Length - 12;

            if (stream.Read(tail) != tail.Length || !tail[4..8].SequenceEqual("IEND"u8))
            {
                return null;
            }

            int width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            byte colourType = head[25];

            return width > 0 && height > 0
                ? (width, height, colourType is 4 or 6)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string FindTexconv(string workspace)
    {
        string[] candidates =
        [
            Path.Combine(workspace, "..", "PbrLab", "vendor", "directxtex", "texconv.exe"),
            Path.Combine(AppContext.BaseDirectory, "texconv.exe"),
            "texconv.exe",
        ];

        foreach (string candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);

            if (File.Exists(full))
            {
                return full;
            }
        }

        return "texconv.exe";
    }

    private static string Gb(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):F2} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F1} MB");

    private sealed record Packable(RebarnKind Kind, string Name, string Path);
}

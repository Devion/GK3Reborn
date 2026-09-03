using System.Diagnostics;
using GK3Reborn.Rendering.Geometry;
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
/// <param name="Files">
/// Which files in the source directory this kind claims, as a search pattern.
/// </param>
/// <param name="Recursive">Whether nested authoring lanes belong to the same kind.</param>
/// <remarks>
/// Every kind but one takes a directory of its own. <c>enhanced/trees</c> is the exception
/// and has to be: a grown tree is geometry, the foliage it is painted with, and a manifest
/// saying which is which, and the three are one thing that has to be produced, reviewed and
/// shipped together. Splitting them into three directories to suit the packer would put a
/// tree's parts three places apart for no reason a person would recognise.
/// </remarks>
public sealed record PackKind(
    RebarnKind Kind,
    string Source,
    string? Format,
    bool Colour,
    int Cap,
    string Volume,
    string Files = "*",
    bool Recursive = false);

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

        // Dialogue and general sound are kept in separate authoring directories, but
        // they are one runtime kind. Each WAV is named <original asset name>.wav; the
        // wrapper suffix is removed from the pack entry so scripts still address the
        // exact 1999 name, including dialogue sequence suffixes such as .QR1.
        new(RebarnKind.Audio, "enhanced/audio", null, false, 0, "Reborn", "*.wav", true),

        // The improved room geometry, and the manifest that says which rooms have any and
        // which build of each room it was cut from. A kind of its own rather than a model:
        // an entry here is addressed by a room's name, and a game missing every one of
        // them is complete. See docs/scene-geometry.md.
        new(RebarnKind.SceneGeometry, "enhanced/scene-geometry", null, false, 0, "Reborn", "*.glb"),
        new(RebarnKind.Manifest, "enhanced/scene-geometry", null, false, 0, "Reborn", "*.json"),

        // **The files no barn has**, from `enhanced/rooms`: the action file the crow's-nest
        // puzzle needs and the two clips the nest and the crow are animated with.
        // `AddedAssets` asks for these by their 1999 name and is consulted after every
        // archive, so an entry here can only ever answer for a name the game does not know.
        //
        // Left out until 2026-09-03, and invisible for the same reason the material library
        // was: the loader reads them loose from the workspace, and in development the
        // workspace is always there. A player has only the two volumes, and against those
        // `rc2_crowsnest.nvc` was a file the scene listed and no archive contained -- so the
        // restored puzzle could be looked at and not solved.
        //
        // **Three extensions and not the nine `AddedAssets` reads**, because a pack key
        // drops the extension (`RebarnFormat.Key`) and these are addressed by their own
        // names rather than by a room's. Two added files with the same stem are one entry,
        // and worse, a built room's `.SIF` answers a read of its `.BSP` -- which is why TE2
        // is not here. See docs/known-issues.md. One kind per extension because `Files` is
        // one glob.
        new(RebarnKind.Raw, "enhanced/rooms", null, false, 0, "Reborn", "*.nvc"),
        new(RebarnKind.Raw, "enhanced/rooms", null, false, 0, "Reborn", "*.anm"),
        new(RebarnKind.Raw, "enhanced/rooms", null, false, 0, "Reborn", "*.act"),

        // The modelled trees, all three parts of them, out of the one directory they are
        // grown into. The foliage cards go through the encoder like any other colour
        // texture, which is what lets the scene loader find them by name without knowing
        // they belong to a tree; the geometry and the manifest are packed as they stand.
        new(RebarnKind.Model, "enhanced/trees", null, false, 0, "Reborn", "*.glb"),
        new(RebarnKind.Manifest, "enhanced/trees", null, false, 0, "Reborn", "*.json"),
        new(RebarnKind.Texture, "enhanced/trees", "BC7_UNORM_SRGB", true, 0, "Reborn"),

        // The reconstructed horizon, flat on disk as <set>.<part>.<ext> because a pack
        // key carries no directory. Heights, forests and JSON deflate well and get it by
        // payload.
        //
        // The forest is the raw instance stream `publish_terrain.py` writes, not the
        // scatter's own JSON: parsed at load it was 95 ms of a 2.4 s outdoor scene and
        // 196 MB of the pack, against 4 ms and about 36 MB as floats. See
        // SceneLoader.ForestFor.
        new(RebarnKind.Raw, "enhanced/terrain", null, false, 0, "Reborn", "*.r32"),
        new(RebarnKind.Raw, "enhanced/terrain", null, false, 0, "Reborn", "*.f32"),
        new(RebarnKind.Raw, "enhanced/terrain", null, false, 0, "Reborn", "*.json"),

        // **The two maps go through the encoder, and the splat is not colour.** Both are
        // always 1024 square, so decoding them cost a fixed 160 ms of every outdoor scene
        // load — spent inside the screen fade, which offers no frame for the length of it.
        // As blocks they upload as they arrive, with the chain already built.
        //
        // The splat is four blend weights and must not be gamma-converted on the way in;
        // the tint is the vista's colour and must. Measured against the sources: the tint
        // round-trips at 0.58/255 RMSE and the splat at 1.55, both far under a visible step
        // on a smooth blend. Encoding the tint *without* the colour flag gives 56.
        new(RebarnKind.Raw, "enhanced/terrain", "BC7_UNORM", false, 0, "Reborn", "*.splat.png"),
        new(RebarnKind.Raw, "enhanced/terrain", "BC7_UNORM_SRGB", true, 0, "Reborn", "*.tint.png"),
        new(RebarnKind.Normal, "enhanced/normals", "BC5_UNORM", false, 1024, "RebornMaterials"),
        new(RebarnKind.Orm, "enhanced/orm", "BC7_UNORM", false, 1024, "RebornMaterials"),
        new(RebarnKind.Height, "enhanced/height", "BC4_UNORM", false, 512, "RebornMaterials"),

        // **The material library, and the corrections filed beside it.** The three maps
        // above say what a surface is *like*; this says what it *is*, and without it every
        // one of them is read at the shader's own defaults — matte, non-metallic, no
        // specular lobe anywhere in the game.
        //
        // It was left out until 2026-08-29, and the gap was invisible because nobody had
        // run against the packs alone: the loader reads the library as a loose file from
        // the workspace, and in development the workspace is always there. A player has
        // only the two volumes.
        //
        // Both files, and the edits *must* travel with it. They are where a person's
        // judgement lives — the roughness of every face, every pair of jeans and the cat's
        // coat is a correction, not a classification — and a library shipped without them
        // is the classifier's first guess.
        new(RebarnKind.Manifest, "manifests", null, false, 0, "RebornMaterials",
            "material-library*.json"),
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
    /// <param name="useSizePlan">
    /// Read <c>manifests/pack-sizes.json</c> and cap each texture at the size its world area
    /// justifies. False packs every texture at whatever the enhanced set holds.
    /// </param>
    /// <returns>True when every volume was written.</returns>
    public bool Run(
        string workspace,
        string output,
        IReadOnlyList<PackKind>? plan = null,
        string? texconv = null,
        bool force = false,
        bool dryRun = false,
        bool encodeOnly = false,
        bool gpu = true,
        bool useSizePlan = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);

        IReadOnlyList<PackKind> kinds = plan ?? DefaultPlan;
        string encoder = texconv ?? FindTexconv(workspace);

        // A size for each texture rather than one for each kind, worked out from the world
        // area it covers. It applies to every channel, because a normal map for a texture
        // that is 512 on screen has nothing to say above 512 either.
        Dictionary<string, PackedTexture> sizes = useSizePlan
            ? TextureSizePlanFile.Load(workspace)
            : [];

        if (sizes.Count > 0)
        {
            _log($"Size plan: {sizes.Count} textures sized individually "
                + $"({TextureSizePlanStage.ManifestPath})");
        }

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
                : Encoded(kind, source, workspace, encoder, force, dryRun, gpu, problems, sizes);

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

        // Every target is checked before the first one is written. The engine memory-maps
        // its packs and holds them for the life of the process, so a running game keeps
        // them open — and finding that out after the first volume has been replaced leaves
        // a mismatched set on disk, which is worse than not having written at all.
        List<string> locked = [.. byVolume.Keys
            .Select(v => Path.Combine(output, v + RebarnFormat.Extension))
            .Where(File.Exists)
            .Where(f => !Writable(f))];

        if (locked.Count > 0)
        {
            _log("These volumes are open in another process, so nothing was written:");

            foreach (string file in locked)
            {
                _log($"  {file}");
            }

            _log("Close the game (it holds its packs mapped for the whole session) and run again.");
            return false;
        }

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

        // Written beside the target and moved into place, so an interrupted run leaves the
        // volume that was there rather than half of a new one.
        string staging = path + ".writing";
        var watch = Stopwatch.StartNew();
        long done = 0;
        int lastPercent = -1;

        RebarnVolumeReport report = builder.Write(staging, (_, written) =>
        {
            done = written;
            int percent = builder.SourceBytes > 0 ? (int)(done * 100 / builder.SourceBytes) : 100;

            if (percent / 5 != lastPercent / 5)
            {
                lastPercent = percent;
                _log($"  {volume}: {percent}% ({Gb(done)})");
            }
        });

        File.Move(staging, path, overwrite: true);

        _log($"{Path.GetFileName(path)}: {report.Count} entries, {Gb(report.Bytes)} "
            + $"in {watch.Elapsed.TotalSeconds:F0} s");

        foreach (RebarnKindReport kind in report.Kinds)
        {
            _log($"    {RebarnFormat.DirectoryOf(kind.Kind),-10} {kind.Count,6}  {Gb(kind.Bytes)}");
        }

        return true;
    }

    /// <summary>Whether a file can be opened for writing right now.</summary>
    private static bool Writable(string path)
    {
        try
        {
            using var probe = new FileStream(
                path, FileMode.Open, FileAccess.Write, FileShare.None);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<Packable> Verbatim(PackKind kind, string source) =>
        [.. Directory.EnumerateFiles(
                source, kind.Files,
                kind.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .Select(f => new Packable(
                kind.Kind,
                kind.Kind == RebarnKind.Audio
                    ? Path.GetFileName(f)[..^Path.GetExtension(f).Length]
                    : Path.GetFileName(f),
                f))];

    private List<Packable> Encoded(
        PackKind kind,
        string source,
        string workspace,
        string texconv,
        bool force,
        bool dryRun,
        bool gpu,
        List<string> problems,
        Dictionary<string, PackedTexture> sizes)
    {
        string cache = Path.Combine(
            workspace, "build", "rebarn", RebarnFormat.DirectoryOf(kind.Kind));

        // Earlier compression runs left DDS in build/textures, build/normals and
        // build/emissive. Adopting one costs a header read and saves an hour of BC7.
        string legacy = Path.Combine(workspace, "build", RebarnFormat.DirectoryOf(kind.Kind));

        var jobs = new Dictionary<(int Width, int Height, bool Alpha), List<string>>();
        var result = new List<Packable>();
        int excluded = 0;
        int verbatim = 0;
        int keyed = 0;

        // The kind's own pattern, so that one directory can hold two things encoded
        // differently — the terrain's splat is data and its tint is colour, and the two
        // differ by exactly the sRGB flag that a whole gamma step of brightness hangs on.
        // Filtered to PNGs afterwards rather than by the pattern, because the default
        // pattern is "*" and the encoder has nothing to say about anything else.
        foreach (string png in Directory.EnumerateFiles(source, kind.Files)
                     .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileNameWithoutExtension(png);

            if (PngSize(png) is not { } size)
            {
                problems.Add($"{kind.Kind} {name}: the PNG is truncated or not a PNG, so it is left out.");
                continue;
            }

            // Absence from the plan means "nothing was said about it", which is not the
            // same as any of the things the plan can say. PackedTexture is a struct, so
            // reading `default` here would report every unplanned map as "not a surface"
            // and quietly drop it: three emissive maps whose colour texture is not in the
            // enhanced set went missing exactly that way.
            bool known = sizes.TryGetValue(name, out PackedTexture planned);

            // Not a surface, so it has no material channels. Nothing should have produced
            // one, but a stray file from an earlier run must not reach the pack either:
            // the rule is what says this is a picture rather than a material.
            if (known && !planned.Materials && kind.Kind is not RebarnKind.Texture)
            {
                excluded++;
                continue;
            }

            // Its 1999 original is colour-keyed and the replacement did not carry the key
            // across as alpha. Block data cannot be keyed, so packing it would put an
            // opaque magenta rectangle where the holes belong; left out, the loader goes on
            // reading the original, which is right. This is what lets the loader treat "the
            // pack holds it" as "its transparency is already correct".
            if (known && !planned.Pack)
            {
                keyed++;
                continue;
            }

            // Stored as the source PNG rather than block-compressed. A full-screen image
            // drawn one texel to one pixel gains nothing from BC7 and loses the gradients
            // in it to block artefacts, which is exactly where they are most visible.
            if (known && planned.Form == "png" && kind.Kind is RebarnKind.Texture)
            {
                result.Add(new Packable(kind.Kind, Path.GetFileName(png), png));
                verbatim++;
                continue;
            }

            // The tighter of the two caps wins: the kind's, and this texture's own.
            int cap = kind.Cap;

            if (known && planned.Size > 0)
            {
                cap = cap > 0 ? Math.Min(cap, planned.Size) : planned.Size;
            }

            (int width, int height, bool alpha) = Target(size, cap);
            string wanted = name + ".DDS";

            // Both candidates are held to the same rule, and the rule includes being no
            // older than the PNG. Matching dimensions is not freshness: a regenerated
            // texture keeps its size, so a stale DDS beside it looks adoptable and packs
            // the picture that was there this morning. `enhanced/*.png` beats `build/*.dds`
            // everywhere else in this project for exactly that reason.
            string legacyDds = Path.Combine(legacy, wanted);

            if (!force && Fresh(legacyDds, png, width, height))
            {
                result.Add(new Packable(kind.Kind, wanted, legacyDds));
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
            + (kind.Cap > 0 ? $" capped at {kind.Cap}" : string.Empty)
            + (sizes.Count > 0 ? ", size plan applied" : string.Empty)
            + (verbatim > 0 ? $", {verbatim} stored as PNG" : string.Empty)
            + (excluded > 0 ? $", {excluded} not a surface" : string.Empty)
            + (keyed > 0 ? $", {keyed} left to the original for its colour key" : string.Empty));

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

    /// <summary>Whether a DDS on disk is still what the plan asks for.</summary>
    /// <param name="dds">The candidate DDS.</param>
    /// <param name="png">The source it would have been made from.</param>
    /// <param name="width">Width the plan wants.</param>
    /// <param name="height">Height the plan wants.</param>
    /// <returns>True when it can be packed as it is.</returns>
    /// <remarks>
    /// Three things, all of which have to hold: it exists, it is no older than the PNG it
    /// was made from, and its extent is what the plan wants. The middle one is what makes a
    /// regenerated texture reach the pack.
    /// </remarks>
    public static bool Fresh(string dds, string png, int width, int height)
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

    /// <summary>Whether a DDS already on disk has the extent the plan asks for.</summary>
    /// <remarks>
    /// Size only. Freshness is <see cref="Fresh"/>'s business, and the two must not be
    /// confused: a regenerated texture keeps its dimensions, so size alone would adopt the
    /// compression of a picture that has since been replaced.
    /// </remarks>
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
    /// <param name="size">The source's extent and whether it has alpha.</param>
    /// <param name="cap">Longest edge allowed, or zero for no cap.</param>
    /// <returns>The extent to encode at.</returns>
    /// <remarks>
    /// The cap is on the longest edge and the aspect ratio is kept, rounded to a multiple
    /// of four so that no block is padded. A source already inside the cap is left alone —
    /// this never enlarges anything.
    /// </remarks>
    public static (int Width, int Height, bool Alpha) Target((int Width, int Height, bool Alpha) size, int cap)
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
    public static (int Width, int Height, bool Alpha)? PngSize(string path)
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

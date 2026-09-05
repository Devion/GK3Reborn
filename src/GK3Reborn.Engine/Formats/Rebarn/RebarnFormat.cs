using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Rebarn;

/// <summary>What an entry in a ReBarn pack is for.</summary>
/// <remarks>
/// The kind is part of an entry's identity rather than a property of it, because the
/// remake's sets are addressed by the <em>colour</em> texture's name: a surface called
/// <c>R25WALLS</c> has a colour texture, a normal map, an ORM and a height map, and all
/// four of them are called <c>R25WALLS</c>. Without a kind in the key they would collide.
/// </remarks>
public enum RebarnKind : byte
{
    /// <summary>Not stated. Never written; a pack from a later version may hold one.</summary>
    Unknown = 0,

    /// <summary>Base colour, sRGB.</summary>
    Texture = 1,

    /// <summary>Tangent-space normal map, linear, named for its colour texture.</summary>
    Normal = 2,

    /// <summary>Packed occlusion, roughness and metalness, linear.</summary>
    Orm = 3,

    /// <summary>Height, linear, one channel.</summary>
    Height = 4,

    /// <summary>What a surface emits, sRGB.</summary>
    Emissive = 5,

    /// <summary>Geometry, as glTF binary.</summary>
    Model = 6,

    /// <summary>A movie, in whatever container the video import produced.</summary>
    Video = 7,

    /// <summary>A manifest the toolchain wrote, as JSON.</summary>
    Manifest = 8,

    /// <summary>Sound.</summary>
    Audio = 9,

    /// <summary>
    /// Improved geometry for one of the game's rooms, as glTF binary.
    /// </summary>
    /// <remarks>
    /// A kind of its own rather than a <see cref="Model"/>, because it is not one. A model
    /// stands somewhere in a room; this <em>is</em> part of a room, addressed by the room's
    /// name, and every triangle in it names a surface of that room's original geometry. It
    /// is also the one kind a game may be missing entirely and be complete without.
    /// </remarks>
    SceneGeometry = 10,

    /// <summary>
    /// An asset of the 1999 game as one language spells it, addressed by its whole name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extension is part of the key, as it is for <see cref="Audio"/> and for nothing
    /// else. It has to be: a localised set holds <c>F014ED3S6J1.YAK</c> beside
    /// <c>A014ED3S.6J1</c> beside <c>27KASHAF.BMP</c>, and a key that dropped the extension
    /// would make the first two collide with the English recordings of the same lines and
    /// the third with its own enhanced <see cref="Texture"/>.
    /// </para>
    /// <para>
    /// It is read in front of the game's own archives rather than in front of the packs,
    /// because that is what it stands in for: an entry here is the French bitmap the French
    /// disc holds, not an improvement on anything. See <c>docs/localization.md</c>.
    /// </para>
    /// </remarks>
    Localized = 11,

    /// <summary>
    /// One language's soundtrack for a movie whose picture every language shares.
    /// </summary>
    /// <remarks>
    /// Addressed by the movie's name without an extension, the way
    /// <see cref="Content.VideoLibrary"/> addresses the picture. Separate from
    /// <see cref="Video"/> because the two are chosen independently: thirteen of GK3's
    /// sixteen spoken movies are the same footage in every language and differ only in what
    /// is said over them, and shipping a whole second copy of the picture to change the
    /// words would cost a hundred megabytes a language for nothing.
    /// </remarks>
    MovieAudio = 12,

    /// <summary>Anything else, addressed by name alone.</summary>
    Raw = 255,
}

/// <summary>What an entry's bytes are, once decompressed.</summary>
/// <remarks>
/// A hint rather than a contract. The reader does not act on it; a tool listing a pack
/// uses it to choose an extension, and a loader may use it to refuse early rather than
/// after a confusing parse failure.
/// </remarks>
public enum RebarnPayload : byte
{
    /// <summary>Unstated.</summary>
    Raw = 0,

    /// <summary>A DDS file, header included.</summary>
    Dds = 1,

    /// <summary>A PNG file.</summary>
    Png = 2,

    /// <summary>glTF binary.</summary>
    Glb = 3,

    /// <summary>An MP4 container.</summary>
    Mp4 = 4,

    /// <summary>UTF-8 JSON.</summary>
    Json = 5,

    /// <summary>A RIFF WAVE file.</summary>
    Wav = 6,
}

/// <summary>How an entry's bytes are stored in the pack.</summary>
public enum RebarnCompression : byte
{
    /// <summary>Verbatim. What every block-compressed texture uses.</summary>
    Store = 0,

    /// <summary>A raw DEFLATE stream, with no zlib or gzip wrapper.</summary>
    Deflate = 1,
}

/// <summary>One entry in a pack's index.</summary>
/// <param name="Kind">What the entry is for.</param>
/// <param name="Name">The name as the pack spells it, extension included.</param>
/// <param name="Offset">Absolute offset of the stored bytes within the volume.</param>
/// <param name="StoredLength">How many bytes are on disk.</param>
/// <param name="Length">How many bytes there are once decompressed.</param>
/// <param name="Payload">What the bytes are.</param>
/// <param name="Compression">How they are stored.</param>
/// <param name="Crc32">CRC-32 of the stored bytes, or zero when it was not computed.</param>
public sealed record RebarnEntry(
    RebarnKind Kind,
    string Name,
    long Offset,
    long StoredLength,
    long Length,
    RebarnPayload Payload,
    RebarnCompression Compression,
    uint Crc32)
{
    /// <summary>The key this entry answers to: its kind and its name without an extension.</summary>
    public string Key => RebarnFormat.Key(Kind, Name);
}

/// <summary>
/// The ReBarn container: constants, keys, and the header that opens every volume.
/// </summary>
/// <remarks>
/// <para>
/// ReBarn holds the remake's own content — enhanced textures and their material channels,
/// modernised models, imported video — in a file that sits beside the executable. It is
/// deliberately <em>not</em> GK3's Barn format: Barn is a 1999 archive with 32-bit offsets
/// and per-entry LZO, and what this has to hold is fifteen gigabytes of block data that
/// must reach the GPU without being decoded on the way.
/// </para>
/// <para>
/// Which is the whole design. Every offset is 64-bit; entries are aligned so that a mapped
/// view of one can be handed straight to a staging buffer; and block-compressed textures
/// are stored verbatim, because a DDS is already compressed and running DEFLATE over one
/// buys a few per cent for a decompression pass on the critical path of a room load. Time
/// to display is what matters — see <c>docs/formats/rebarn.md</c>.
/// </para>
/// <para>
/// Layout, in order: a 64-byte header, the data section, the name table, then the index.
/// The index is last so that a pack can be written in one streaming pass without knowing
/// in advance how large it will be, and the header is rewritten at the end with the three
/// offsets that pass discovered.
/// </para>
/// <code>
///   0   header       64 bytes
///   64  data         entries, each starting on a 256-byte boundary
///   ..  name table   UTF-8, no separators; entries carry an offset and a length
///   ..  index        entryCount records of 48 bytes, sorted by key hash
/// </code>
/// </remarks>
public static class RebarnFormat
{
    /// <summary>The four bytes a pack starts with, <c>RBRN</c>.</summary>
    public const uint Magic = 0x4E52_4252;

    /// <summary>The version this reader and writer speak.</summary>
    public const ushort Version = 1;

    /// <summary>How many bytes the header occupies.</summary>
    public const int HeaderBytes = 64;

    /// <summary>How many bytes one index record occupies.</summary>
    public const int EntryBytes = 48;

    /// <summary>What every entry's offset is a multiple of.</summary>
    /// <remarks>
    /// Chosen so that a mapped entry starts on a boundary a copy engine is happy with, and
    /// so that a DDS's block data — 128 bytes into the file — lands on a 128-byte boundary
    /// too. Ten thousand entries waste about a megabyte between them, which is nothing
    /// against fifteen gigabytes.
    /// </remarks>
    public const int Alignment = 256;

    /// <summary>The extension a pack file carries.</summary>
    public const string Extension = ".rebarn";

    /// <summary>Builds the key an entry answers to.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="name">The name, with or without an extension or a directory.</param>
    /// <returns>The canonical key.</returns>
    /// <remarks>
    /// Uppercase, no extension and no directory for ordinary ReBarn content. Two kinds are
    /// the exception. <see cref="RebarnKind.Audio"/> keeps its extension because GK3 stores
    /// a dialogue sequence there, so <c>A0NQIB44.QR1</c> and <c>A0NQIB44.QR2</c> are
    /// different recordings; <see cref="RebarnKind.Localized"/> keeps its because an entry
    /// there <em>is</em> a 1999 file name and the game asks for it by that whole name —
    /// <c>ESTRINGS.TXT</c>, <c>F014ED3S6J1.YAK</c>, <c>27KASHAF.BMP</c>.
    /// </remarks>
    public static string Key(RebarnKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        ReadOnlySpan<char> span = name.AsSpan().Trim();

        int slash = span.LastIndexOfAny('/', '\\');
        if (slash >= 0)
        {
            span = span[(slash + 1)..];
        }

        int dot = span.LastIndexOf('.');
        if (kind is not (RebarnKind.Audio or RebarnKind.Localized) && dot > 0)
        {
            span = span[..dot];
        }

        return string.Concat(
            ((byte)kind).ToString(CultureInfo.InvariantCulture),
            ":",
            span.ToString().ToUpperInvariant());
    }

    /// <summary>Hashes a key.</summary>
    /// <param name="key">A key from <see cref="Key"/>.</param>
    /// <returns>Its 64-bit FNV-1a hash.</returns>
    /// <remarks>
    /// The index is sorted by this so that a pack written twice from the same inputs is
    /// byte for byte the same file. A collision is resolved by comparing the name, which
    /// the index carries anyway.
    /// </remarks>
    public static ulong Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        ulong hash = 14695981039346656037UL;

        foreach (char c in key)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Rounds an offset up to the next entry boundary.</summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The offset, rounded up to a multiple of <see cref="Alignment"/>.</returns>
    public static long Align(long offset) =>
        (offset + (Alignment - 1)) & ~((long)Alignment - 1);

    /// <summary>Guesses what a file holds from its extension.</summary>
    /// <param name="path">A file name or path.</param>
    /// <returns>The payload kind, or <see cref="RebarnPayload.Raw"/> when it is none of them.</returns>
    public static RebarnPayload PayloadOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".DDS" => RebarnPayload.Dds,
            ".PNG" => RebarnPayload.Png,
            ".GLB" or ".GLTF" => RebarnPayload.Glb,
            ".MP4" or ".M4V" => RebarnPayload.Mp4,
            ".JSON" => RebarnPayload.Json,
            ".WAV" => RebarnPayload.Wav,
            _ => RebarnPayload.Raw,
        };
    }

    /// <summary>The extension a payload is conventionally written back out with.</summary>
    /// <param name="payload">What the bytes are.</param>
    /// <returns>The extension, dot included.</returns>
    public static string ExtensionOf(RebarnPayload payload) => payload switch
    {
        RebarnPayload.Dds => ".dds",
        RebarnPayload.Png => ".png",
        RebarnPayload.Glb => ".glb",
        RebarnPayload.Mp4 => ".mp4",
        RebarnPayload.Json => ".json",
        RebarnPayload.Wav => ".wav",
        _ => ".bin",
    };

    /// <summary>The directory name a kind is conventionally packed from and unpacked into.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <returns>A directory name.</returns>
    public static string DirectoryOf(RebarnKind kind) => kind switch
    {
        RebarnKind.Texture => "textures",
        RebarnKind.Normal => "normals",
        RebarnKind.Orm => "orm",
        RebarnKind.Height => "height",
        RebarnKind.Emissive => "emissive",
        RebarnKind.Model => "models",
        RebarnKind.SceneGeometry => "scene-geometry",
        RebarnKind.Video => "video",
        RebarnKind.MovieAudio => "movie-audio",
        RebarnKind.Localized => "localized",
        RebarnKind.Manifest => "manifests",
        RebarnKind.Audio => "audio",
        _ => "raw",
    };

    /// <summary>Parses a kind from the directory name it is conventionally packed from.</summary>
    /// <param name="name">A directory name such as <c>normals</c>.</param>
    /// <returns>The kind, or null when the name names none.</returns>
    public static RebarnKind? KindOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToUpperInvariant() switch
        {
            "TEXTURES" or "TEXTURE" => RebarnKind.Texture,
            "NORMALS" or "NORMAL" => RebarnKind.Normal,
            "ORM" => RebarnKind.Orm,
            "HEIGHT" or "HEIGHTS" => RebarnKind.Height,
            "EMISSIVE" => RebarnKind.Emissive,
            "MODELS" or "MODEL" => RebarnKind.Model,
            "SCENE-GEOMETRY" or "SCENES" => RebarnKind.SceneGeometry,
            "VIDEO" or "VIDEOS" => RebarnKind.Video,
            "MOVIE-AUDIO" or "MOVIEAUDIO" => RebarnKind.MovieAudio,
            "LOCALIZED" or "LOCALISED" => RebarnKind.Localized,
            "MANIFESTS" or "MANIFEST" => RebarnKind.Manifest,
            "AUDIO" => RebarnKind.Audio,
            "RAW" => RebarnKind.Raw,
            _ => null,
        };
    }

    /// <summary>Writes a header into a 64-byte span.</summary>
    /// <param name="into">Where to write; at least <see cref="HeaderBytes"/> long.</param>
    /// <param name="header">The header.</param>
    public static void WriteHeader(Span<byte> into, in RebarnHeader header)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, HeaderBytes);

        into[..HeaderBytes].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(into, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(into[4..], header.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(into[6..], header.Volume);
        BinaryPrimitives.WriteUInt32LittleEndian(into[8..], header.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(into[12..], header.EntryCount);
        BinaryPrimitives.WriteInt64LittleEndian(into[16..], header.IndexOffset);
        BinaryPrimitives.WriteInt64LittleEndian(into[24..], header.NameTableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(into[32..], header.NameTableLength);
        BinaryPrimitives.WriteInt64LittleEndian(into[40..], header.DataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(into[48..], header.IndexHash);
        BinaryPrimitives.WriteInt64LittleEndian(into[56..], header.BuiltUtcTicks);
    }

    /// <summary>Reads a header from a span.</summary>
    /// <param name="from">The bytes; at least <see cref="HeaderBytes"/> long.</param>
    /// <param name="file">Name used in diagnostics.</param>
    /// <returns>The header.</returns>
    /// <exception cref="FormatParseException">It is not a ReBarn pack this can read.</exception>
    public static RebarnHeader ReadHeader(ReadOnlySpan<byte> from, string file)
    {
        if (from.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(from) != Magic)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1170",
                DiagnosticSeverity.Error,
                $"{file} is not a ReBarn pack.",
                file,
                0,
                "RBRN",
                from.Length >= 4 ? Encoding.ASCII.GetString(from[..4]) : "<empty>",
                "Produce it again with `pack-content`."));
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(from[4..]);

        if (version != Version)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1171",
                DiagnosticSeverity.Error,
                $"{file} is a version {version} ReBarn pack and this build reads version {Version}.",
                file,
                4,
                Version.ToString(CultureInfo.InvariantCulture),
                version.ToString(CultureInfo.InvariantCulture),
                "Produce it again with `pack-content` from this build of the toolchain."));
        }

        return new RebarnHeader
        {
            Version = version,
            Volume = BinaryPrimitives.ReadUInt16LittleEndian(from[6..]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(from[8..]),
            EntryCount = BinaryPrimitives.ReadUInt32LittleEndian(from[12..]),
            IndexOffset = BinaryPrimitives.ReadInt64LittleEndian(from[16..]),
            NameTableOffset = BinaryPrimitives.ReadInt64LittleEndian(from[24..]),
            NameTableLength = BinaryPrimitives.ReadInt64LittleEndian(from[32..]),
            DataOffset = BinaryPrimitives.ReadInt64LittleEndian(from[40..]),
            IndexHash = BinaryPrimitives.ReadUInt64LittleEndian(from[48..]),
            BuiltUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(from[56..]),
        };
    }
}

/// <summary>A pack's header, as the first 64 bytes hold it.</summary>
public readonly record struct RebarnHeader
{
    /// <summary>Format version.</summary>
    public ushort Version { get; init; }

    /// <summary>Which volume of a multi-file set this is; zero when there is only one.</summary>
    public ushort Volume { get; init; }

    /// <summary>Reserved. Zero.</summary>
    public uint Flags { get; init; }

    /// <summary>How many entries the index holds.</summary>
    public uint EntryCount { get; init; }

    /// <summary>Where the index starts.</summary>
    public long IndexOffset { get; init; }

    /// <summary>Where the name table starts.</summary>
    public long NameTableOffset { get; init; }

    /// <summary>How long the name table is.</summary>
    public long NameTableLength { get; init; }

    /// <summary>Where the first entry's bytes start.</summary>
    public long DataOffset { get; init; }

    /// <summary>FNV-1a over the index and name table, so a truncated pack is caught on open.</summary>
    public ulong IndexHash { get; init; }

    /// <summary>When the pack was written, as UTC ticks.</summary>
    public long BuiltUtcTicks { get; init; }
}

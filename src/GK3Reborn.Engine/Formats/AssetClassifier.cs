using System.Text;

namespace GK3Reborn.Formats;

/// <summary>What an asset actually is, determined from its contents.</summary>
public enum AssetKind
{
    /// <summary>Nothing matched. These are the entries C2 exists to eliminate.</summary>
    Unknown,

    /// <summary>RIFF/WAVE audio.</summary>
    Audio,

    /// <summary>GK3's own bitmap container.</summary>
    BitmapGk3,

    /// <summary>Standard Windows bitmap.</summary>
    BitmapWindows,

    /// <summary>Model geometry.</summary>
    Model,

    /// <summary>Actor animation data.</summary>
    ActorAnimation,

    /// <summary>Lightmap.</summary>
    Lightmap,

    /// <summary>BSP scene geometry.</summary>
    SceneGeometry,

    /// <summary>Compiled Sheep bytecode.</summary>
    SheepBytecode,

    /// <summary>Sidney document markup.</summary>
    Html,

    /// <summary>Bitmap font.</summary>
    Font,

    /// <summary>A text asset: animation script, scene, action set, soundtrack and so on.</summary>
    Text,

    /// <summary>
    /// An OLE compound document. The archives carry the original team's design
    /// documents - the authoritative descriptions of Sheep, SIF, NVC and the rest.
    /// </summary>
    DesignDocument,

    /// <summary>A Windows executable or DLL left in the archives.</summary>
    Executable,

    /// <summary>A zip archive.</summary>
    ZipArchive,
}

/// <summary>The result of classifying one asset.</summary>
/// <param name="Kind">What the contents say it is.</param>
/// <param name="Basis">How the decision was reached, for the inventory report.</param>
/// <param name="Magic">Printable rendering of the leading bytes.</param>
public readonly record struct AssetClassification(AssetKind Kind, string Basis, string Magic);

/// <summary>
/// Identifies assets from their contents rather than their names.
/// </summary>
/// <remarks>
/// <para>
/// Extensions cannot be trusted in this corpus. Of 7,852 audio assets in the retail
/// archives, only 1,170 are named <c>.WAV</c>; the rest carry three-character codes
/// as their extension - <c>.N61</c>, <c>.6J1</c>, <c>.B61</c> and 2,744 others - which
/// is why the archives appear to hold 2,775 distinct file types when they hold on the
/// order of a dozen. Classifying by name would mishandle 85% of the game's audio.
/// </para>
/// <para>
/// Several binary formats write their tag in little-endian order, so a model reads as
/// <c>LDOM</c> rather than <c>MODL</c> and a lightmap as <c>TLUM</c>. The tags below
/// are written as they appear on disk.
/// </para>
/// </remarks>
public static class AssetClassifier
{
    private static readonly (byte[] Magic, AssetKind Kind, string Description)[] BinarySignatures =
    [
        ("RIFF"u8.ToArray(), AssetKind.Audio, "RIFF/WAVE audio"),
        ("61nM"u8.ToArray(), AssetKind.BitmapGk3, "GK3 bitmap"),
        ("BM"u8.ToArray(), AssetKind.BitmapWindows, "Windows bitmap"),
        ("LDOM"u8.ToArray(), AssetKind.Model, "model geometry (MODL)"),
        ("HTCA"u8.ToArray(), AssetKind.ActorAnimation, "actor animation (ACTH)"),
        ("TLUM"u8.ToArray(), AssetKind.Lightmap, "lightmap (MULT)"),
        ("NECS"u8.ToArray(), AssetKind.SceneGeometry, "BSP scene (SCEN)"),
        ("GK3S"u8.ToArray(), AssetKind.SheepBytecode, "compiled Sheep"),
        ("Font"u8.ToArray(), AssetKind.Font, "bitmap font"),
        ("Bitm"u8.ToArray(), AssetKind.Font, "bitmap font"),
        ([0xD0, 0xCF, 0x11, 0xE0], AssetKind.DesignDocument, "OLE compound document"),
        ("MZ"u8.ToArray(), AssetKind.Executable, "Windows executable"),
        // The full local-file-header signature rather than just "PK": a two-byte
        // prefix would claim any text asset beginning with those letters.
        ([(byte)'P', (byte)'K', 0x03, 0x04], AssetKind.ZipArchive, "zip archive"),
    ];

    /// <summary>How many leading bytes <see cref="Classify"/> needs.</summary>
    public const int RequiredBytes = 64;

    /// <summary>Classifies an asset from its leading bytes.</summary>
    /// <param name="head">The first bytes of the asset; <see cref="RequiredBytes"/> is plenty.</param>
    /// <returns>What the asset is, and why.</returns>
    public static AssetClassification Classify(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty)
        {
            return new AssetClassification(AssetKind.Unknown, "the asset is empty", string.Empty);
        }

        string magic = Printable(head[..Math.Min(4, head.Length)]);

        foreach ((byte[] signature, AssetKind kind, string description) in BinarySignatures)
        {
            if (head.StartsWith(signature))
            {
                return new AssetClassification(kind, description, magic);
            }
        }

        // Sidney's documents are markup, and the tag's case varies.
        if (StartsWithIgnoringCase(head, "<htm"u8))
        {
            return new AssetClassification(AssetKind.Html, "Sidney document markup", magic);
        }

        return LooksLikeText(head)
            ? new AssetClassification(AssetKind.Text, "printable text", magic)
            : new AssetClassification(AssetKind.Unknown, "no known signature", magic);
    }

    /// <summary>
    /// Decides whether a buffer is text.
    /// </summary>
    /// <remarks>
    /// GK3's text assets are Latin-1, and use CRLF - sometimes CR CR LF - line endings.
    /// Anything mostly printable is treated as text. A NUL in the middle of content is a
    /// reliable sign of a binary format, but a trailing NUL is just a terminator.
    /// </remarks>
    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        // Several text assets are NUL-terminated, so a NUL at the very end says nothing
        // about the format. Only a NUL in the middle of content indicates binary.
        while (!head.IsEmpty && head[^1] == 0)
        {
            head = head[..^1];
        }

        if (head.IsEmpty)
        {
            return false;
        }

        int printable = 0;

        foreach (byte b in head)
        {
            if (b == 0)
            {
                return false;
            }

            if (b is (>= 0x20 and < 0x7F) or (byte)'\r' or (byte)'\n' or (byte)'\t')
            {
                printable++;
            }
        }

        return printable >= head.Length * 0.95;
    }

    private static bool StartsWithIgnoringCase(ReadOnlySpan<byte> head, ReadOnlySpan<byte> prefix)
    {
        if (head.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (char.ToLowerInvariant((char)head[i]) != (char)prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return sb.ToString();
    }
}

using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Lightmaps;

/// <summary>
/// Reader for GK3's baked lightmaps.
/// </summary>
/// <remarks>
/// <para>
/// The tag reads <c>TLUM</c> on disk, being <c>MULT</c> stored little-endian. The layout
/// is a count followed by that many bitmaps packed back to back in the ordinary texture
/// format, with no offset table — so each one has to be measured to find the next.
/// </para>
/// <para>
/// The count matches the corresponding scene's surface count, and so does the order:
/// lightmap <c>i</c> lights surface <c>i</c>. That pairing is what makes stage C4b
/// possible, since a surface knows its own geometry and its lightmap UV offset and
/// scale, while the lightmap knows how much light landed on it (ADR 0002).
/// </para>
/// <para>
/// These are the whole of the original game's lighting. There is nothing dynamic to
/// recover, which is why the rigs C4b proposes have to be derived from this evidence and
/// then reviewed by a human rather than simply read out.
/// </para>
/// </remarks>
public sealed class MulFile
{
    private MulFile(string name, IReadOnlyList<DecodedImage> lightmaps)
    {
        Name = name;
        Lightmaps = lightmaps;
    }

    /// <summary>Name this lightmap set was read under.</summary>
    public string Name { get; }

    /// <summary>One lightmap per surface of the matching scene, in surface order.</summary>
    public IReadOnlyList<DecodedImage> Lightmaps { get; }

    /// <summary>Total lightmap pixels, as a measure of the baked lighting's resolution.</summary>
    public long TotalPixels => Lightmaps.Sum(l => (long)l.Width * l.Height);

    /// <summary>Builds a lightmap set from images already in memory.</summary>
    /// <param name="name">Name for the produced set.</param>
    /// <param name="lightmaps">One lightmap per surface, in surface order.</param>
    /// <returns>The set.</returns>
    /// <remarks>
    /// For tests and for tools that synthesise a room, the counterpart of
    /// <see cref="Formats.Scenes.BspFile.FromParts"/>. A bake is not a detail of such a
    /// room: whether a surface carries one decides how the composite spends every shadow
    /// it traces, so a test about shadows on lit ground has to be able to say that the
    /// ground was lit.
    /// </remarks>
    public static MulFile FromParts(string name, IReadOnlyList<DecodedImage> lightmaps) =>
        new(name, lightmaps);

    /// <summary>Parses a lightmap set.</summary>
    /// <param name="data">The asset's bytes.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed lightmaps.</returns>
    /// <exception cref="FormatParseException">The data is not a valid lightmap set.</exception>
    public static MulFile Parse(ReadOnlySpan<byte> data, string name = "<memory>")
    {
        var reader = new SpanReader(data, name);
        reader.ExpectMagic("TLUM"u8, "Lightmap header");

        uint count = reader.ReadUInt32();
        if (count > 65536)
        {
            throw Corrupt(name, reader.Position, "a plausible lightmap count",
                count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        List<DecodedImage> lightmaps = new((int)count);
        int offset = reader.Position;

        for (uint i = 0; i < count; i++)
        {
            if (offset >= data.Length)
            {
                throw Corrupt(name, offset, $"{count} lightmaps",
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            DecodedImage image = BitmapDecoder.Decode(data[offset..], out int consumed, $"{name}[{i}]");
            if (consumed <= 0)
            {
                throw Corrupt(name, offset, "a measurable bitmap", "zero length");
            }

            lightmaps.Add(image);
            offset += consumed;
        }

        return new MulFile(name, lightmaps);
    }

    private static FormatParseException Corrupt(string file, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1060",
            DiagnosticSeverity.Error,
            "Lightmap set is corrupt or truncated.",
            file,
            offset,
            expected,
            actual,
            "Re-extract the asset and report the lightmap name."));
}

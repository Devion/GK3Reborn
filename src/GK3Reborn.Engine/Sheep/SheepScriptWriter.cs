using System.Buffers.Binary;
using System.Text;

namespace GK3Reborn.Sheep;

/// <summary>
/// Writes a compiled script back out in the container the game ships.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of <see cref="SheepScriptFile.Parse"/>, and the reason it exists is
/// verification rather than authoring: a compiler that produces something only its own
/// reader understands has not been checked against anything. Writing the container and
/// reading it back is a round trip through the format's real rules — the length-prefixed
/// strings with their trailing byte, the section table, the offsets that are relative to
/// the header — and any of those misunderstood shows up immediately instead of in a
/// disassembly nobody compares.
/// </para>
/// <para>
/// The five sections are written in the order the reader expects to find them named, each
/// with its own twelve-byte name, its size, its count and its own offset table.
/// </para>
/// </remarks>
public static class SheepScriptWriter
{
    /// <summary>How much of the header comes before the section table.</summary>
    /// <remarks>
    /// Eight bytes of magic and five 32-bit fields. The header's declared size is this plus
    /// the table, and it is what every section offset is measured from — which is checkable
    /// against the content and checks out: all 224 shipped scripts declare exactly
    /// <c>28 + 4 × sections</c>.
    /// </remarks>
    private const int BeforeTable = 28;

    /// <summary>Writes a script.</summary>
    /// <param name="script">The script.</param>
    /// <returns>The bytes of a <c>.SHP</c> file.</returns>
    /// <remarks>
    /// A section with nothing in it is left out rather than written empty, which is what
    /// the game does: 206 of its scripts declare no variables and carry four sections,
    /// and the 17 that do declare some carry five.
    /// </remarks>
    public static byte[] Write(SheepScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);

        List<byte[]> sections = [];

        if (script.Imports.Count > 0)
        {
            sections.Add(Imports(script));
        }

        if (script.StringConstants.Count > 0)
        {
            sections.Add(Strings(script));
        }

        if (script.Variables.Count > 0)
        {
            sections.Add(Variables(script));
        }

        if (script.Functions.Count > 0)
        {
            sections.Add(Functions(script));
        }

        sections.Add(Code(script));

        int header = BeforeTable + (4 * sections.Count);
        List<byte> file = [.. "GK3Sheep"u8];

        file.AddRange(Int32(0));            // a version, always zero in the corpus
        file.AddRange(Int32(header));
        file.AddRange(Int32(header));
        file.AddRange(Int32(sections.Sum(s => s.Length)));
        file.AddRange(Int32(sections.Count));

        int at = 0;

        foreach (byte[] section in sections)
        {
            file.AddRange(Int32(at));
            at += section.Length;
        }

        foreach (byte[] section in sections)
        {
            file.AddRange(section);
        }

        return [.. file];
    }

    private static byte[] Imports(SheepScriptFile script)
    {
        List<byte> body = [];
        List<int> offsets = [];

        foreach (SheepImport import in script.Imports)
        {
            offsets.Add(body.Count);
            body.AddRange(LengthPrefixed(import.Name));
            body.Add((byte)import.ReturnType);
            body.Add((byte)import.ArgumentTypes.Count);

            foreach (sbyte argument in import.ArgumentTypes)
            {
                body.Add((byte)argument);
            }
        }

        return Section("SysImports", offsets, body);
    }

    /// <summary>
    /// Writes the string pool, keyed by the offsets the bytecode already carries.
    /// </summary>
    /// <remarks>
    /// These offsets are the only ones in the file that are read rather than skipped:
    /// <c>GetString</c> looks a constant up by where it starts in the pool. The compiler
    /// chose them when it interned the strings, so this lays the pool out to match rather
    /// than the other way round.
    /// </remarks>
    private static byte[] Strings(SheepScriptFile script)
    {
        List<int> offsets = [.. script.StringConstants.Keys.Order()];
        List<byte> body = [];

        foreach (int offset in offsets)
        {
            while (body.Count < offset)
            {
                body.Add(0);
            }

            body.AddRange(Encoding.Latin1.GetBytes(script.StringConstants[offset]));
            body.Add(0);
        }

        return Section("StringConsts", offsets, body);
    }

    private static byte[] Variables(SheepScriptFile script)
    {
        List<byte> body = [];
        List<int> offsets = [];

        foreach (SheepVariable variable in script.Variables)
        {
            offsets.Add(body.Count);
            body.AddRange(LengthPrefixed(variable.Name));

            switch (variable.Kind)
            {
                case SheepValueKind.Float:
                    body.AddRange(Int32(2));
                    body.AddRange(Single(variable.FloatValue));
                    break;

                case SheepValueKind.String:
                    body.AddRange(Int32(3));
                    body.AddRange(Int32(0));
                    break;

                default:
                    body.AddRange(Int32(1));
                    body.AddRange(Int32(variable.IntValue));
                    break;
            }
        }

        return Section("Variables", offsets, body);
    }

    private static byte[] Functions(SheepScriptFile script)
    {
        List<byte> body = [];
        List<int> offsets = [];

        foreach ((string name, int offset) in script.Functions)
        {
            offsets.Add(body.Count);
            body.AddRange(LengthPrefixed(name));
            body.AddRange(new byte[2]);
            body.AddRange(Int32(offset));
        }

        return Section("Functions", offsets, body);
    }

    /// <summary>
    /// Writes the code, which is one block and has always been one block.
    /// </summary>
    /// <remarks>
    /// The section's "count" is a count of blocks rather than of entries, and its one
    /// offset is where that block starts — zero, in all 224 of the game's scripts. The
    /// reader refuses anything else rather than reading the first block and ignoring the
    /// rest.
    /// </remarks>
    private static byte[] Code(SheepScriptFile script) =>
        Section("Code", [0], [.. script.Bytecode]);

    /// <summary>
    /// Wraps a body in the header every section carries.
    /// </summary>
    /// <param name="name">The section's name, in twelve bytes.</param>
    /// <param name="offsets">Where each entry starts within the body.</param>
    /// <param name="body">The entries.</param>
    /// <returns>The section.</returns>
    /// <remarks>
    /// Twelve bytes of name, then the header's own size written <b>twice</b>, then the size
    /// of the body, then the number of entries, then their offsets. The doubled size is the
    /// original's and it checks out across the corpus: every section in every shipped script
    /// declares <c>12 + 16 + 4 × entries</c> in both fields.
    /// </remarks>
    private static byte[] Section(string name, List<int> offsets, List<byte> body)
    {
        int header = 12 + 16 + (4 * offsets.Count);
        List<byte> section = [.. Encoding.ASCII.GetBytes(name.PadRight(12, '\0'))];

        section.AddRange(Int32(header));
        section.AddRange(Int32(header));
        section.AddRange(Int32(body.Count));
        section.AddRange(Int32(offsets.Count));

        foreach (int offset in offsets)
        {
            section.AddRange(Int32(offset));
        }

        section.AddRange(body);
        return [.. section];
    }

    private static byte[] LengthPrefixed(string value)
    {
        byte[] text = Encoding.Latin1.GetBytes(value);
        List<byte> field = [.. Int16((ushort)text.Length), .. text];

        // One byte more than the count says, which the reader documents and which is the
        // difference between reading this file and reading nonsense from here on.
        field.Add(0);
        return [.. field];
    }

    private static byte[] Int32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int16(ushort value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Single(float value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace GK3Reborn.Sheep;

/// <summary>
/// Writes a compiled script back out in the container the game ships.
/// </summary>
public static class SheepScriptWriter
{
    /// <summary>How much of the header comes before the section table.</summary>
    private const int BeforeTable = 28;

    /// <summary>Writes a script.</summary>
    /// <param name="script">The script.</param>
    /// <returns>The bytes of a <c>.SHP</c> file.</returns>
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
    private static byte[] Code(SheepScriptFile script) =>
        Section("Code", [0], [.. script.Bytecode]);

    /// <summary>
    /// Wraps a body in the header every section carries.
    /// </summary>
    /// <param name="name">The section's name, in twelve bytes.</param>
    /// <param name="offsets">Where each entry starts within the body.</param>
    /// <param name="body">The entries.</param>
    /// <returns>The section.</returns>
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

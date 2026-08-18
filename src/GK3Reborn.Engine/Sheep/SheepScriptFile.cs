using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Formats;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Sheep;

/// <summary>A system function a script calls, with its signature.</summary>
/// <param name="Name">Function name as written in the source.</param>
/// <param name="ReturnType">Return type code; 0 void, 1 int, 2 float, 3 string.</param>
/// <param name="ArgumentTypes">Argument type codes, in order.</param>
public readonly record struct SheepImport(string Name, sbyte ReturnType, IReadOnlyList<sbyte> ArgumentTypes);

/// <summary>A variable declared in a script's symbols block.</summary>
/// <param name="Name">Variable name.</param>
/// <param name="Kind">Its declared type.</param>
/// <param name="IntValue">Initial value, for ints.</param>
/// <param name="FloatValue">Initial value, for floats.</param>
public readonly record struct SheepVariable(string Name, SheepValueKind Kind, int IntValue, float FloatValue);

/// <summary>
/// Reader for compiled Sheep bytecode.
/// </summary>
/// <remarks>
/// <para>
/// 224 of these hold the game's logic. The file opens with <c>GK3Sheep</c> and a header
/// listing offsets to named sections — <c>SysImports</c>, <c>StringConsts</c>,
/// <c>Variables</c>, <c>Functions</c> and <c>Code</c> — each of which repeats its own
/// size and an offset table before its contents.
/// </para>
/// <para>
/// Documented from G-Engine's <c>SheepScript::ParseFromData</c>. The language and its
/// runtime API are specified by the original team in <c>SHEEP ENGINE.DOC</c>, which the
/// archives contain; the compiled layout is not covered there and comes from the reader.
/// </para>
/// </remarks>
public sealed class SheepScriptFile
{
    private SheepScriptFile(
        string name,
        IReadOnlyList<SheepImport> imports,
        IReadOnlyDictionary<int, string> stringConstants,
        IReadOnlyList<SheepVariable> variables,
        IReadOnlyList<(string Name, int Offset)> functions,
        byte[] bytecode)
    {
        Name = name;
        Imports = imports;
        StringConstants = stringConstants;
        Variables = variables;
        Functions = functions;
        Bytecode = bytecode;
    }

    /// <summary>Name this script was read under.</summary>
    public string Name { get; }

    /// <summary>System functions the script calls, in import order.</summary>
    public IReadOnlyList<SheepImport> Imports { get; }

    /// <summary>String constants, keyed by their offset within the constant block.</summary>
    public IReadOnlyDictionary<int, string> StringConstants { get; }

    /// <summary>Declared variables, in declaration order.</summary>
    public IReadOnlyList<SheepVariable> Variables { get; }

    /// <summary>Functions, paired with their bytecode offsets.</summary>
    public IReadOnlyList<(string Name, int Offset)> Functions { get; }

    /// <summary>The bytecode.</summary>
    public byte[] Bytecode { get; }

    /// <summary>Identifies whether a buffer is compiled Sheep.</summary>
    /// <param name="data">The asset's bytes.</param>
    /// <returns>True when it carries the signature.</returns>
    public static bool IsSheep(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual("GK3Sheep"u8);

    /// <summary>Parses a compiled script.</summary>
    /// <param name="data">The asset's bytes.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed script.</returns>
    /// <exception cref="FormatParseException">The data is not valid compiled Sheep.</exception>
    public static SheepScriptFile Parse(ReadOnlySpan<byte> data, string name = "<memory>")
    {
        var reader = new SpanReader(data, name);
        reader.ExpectMagic("GK3Sheep"u8, "Sheep header");

        reader.Skip(4);                       // possibly a format version
        int headerSize = reader.ReadInt32();
        reader.Skip(8);                       // header size again, then content size

        int sectionCount = reader.ReadInt32();
        if (sectionCount is < 0 or > 64)
        {
            throw Corrupt(name, reader.Position, "a plausible section count",
                sectionCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        int[] offsets = new int[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            offsets[i] = reader.ReadInt32();
        }

        List<SheepImport> imports = [];
        Dictionary<int, string> strings = [];
        List<SheepVariable> variables = [];
        List<(string, int)> functions = [];
        byte[] bytecode = [];

        foreach (int relative in offsets)
        {
            int start = relative + headerSize;
            if ((uint)start >= (uint)data.Length)
            {
                throw Corrupt(name, start, $"a section offset within {data.Length} bytes",
                    start.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var section = new SpanReader(data, name);
            section.Seek(start);
            string kind = section.ReadFixedString(12);

            switch (kind)
            {
                case "SysImports":
                    imports = ReadImports(ref section);
                    break;
                case "StringConsts":
                    strings = ReadStrings(ref section, data);
                    break;
                case "Variables":
                    variables = ReadVariables(ref section, name);
                    break;
                case "Functions":
                    functions = ReadFunctions(ref section);
                    break;
                case "Code":
                    bytecode = ReadCode(ref section, data);
                    break;
                default:
                    throw Corrupt(name, start, "a known section name", kind);
            }
        }

        return new SheepScriptFile(name, imports, strings, variables, functions, bytecode);
    }

    private static List<SheepImport> ReadImports(ref SpanReader reader)
    {
        reader.Skip(12);
        int count = reader.ReadInt32();
        reader.Skip(4 * count);

        List<SheepImport> imports = new(count);
        for (int i = 0; i < count; i++)
        {
            string name = ReadLengthPrefixedString(ref reader);
            sbyte returnType = reader.ReadInt8();

            sbyte argumentCount = reader.ReadInt8();
            sbyte[] arguments = new sbyte[Math.Max(0, (int)argumentCount)];
            for (int j = 0; j < arguments.Length; j++)
            {
                arguments[j] = reader.ReadInt8();
            }

            imports.Add(new SheepImport(name, returnType, arguments));
        }

        return imports;
    }

    private static Dictionary<int, string> ReadStrings(ref SpanReader reader, ReadOnlySpan<byte> data)
    {
        reader.Skip(8);
        int contentSize = reader.ReadInt32();
        int count = reader.ReadInt32();

        int[] offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = reader.ReadInt32();
        }

        int baseOffset = reader.Position;
        Dictionary<int, string> strings = new(count);

        for (int i = 0; i < count; i++)
        {
            int start = baseOffset + offsets[i];
            int end = i < count - 1 ? baseOffset + offsets[i + 1] : baseOffset + contentSize;
            if (start > end || end > data.Length)
            {
                continue;
            }

            // Entries are NUL-terminated inside their slot.
            ReadOnlySpan<byte> raw = data[start..end];
            int nul = raw.IndexOf((byte)0);
            strings[offsets[i]] = Encoding.Latin1.GetString(nul >= 0 ? raw[..nul] : raw);
        }

        return strings;
    }

    private static List<SheepVariable> ReadVariables(ref SpanReader reader, string file)
    {
        reader.Skip(12);
        int count = reader.ReadInt32();
        reader.Skip(4 * count);

        List<SheepVariable> variables = new(count);
        for (int i = 0; i < count; i++)
        {
            string variableName = ReadLengthPrefixedString(ref reader);
            int type = reader.ReadInt32();

            switch (type)
            {
                case 1:
                    variables.Add(new SheepVariable(variableName, SheepValueKind.Int, reader.ReadInt32(), 0));
                    break;
                case 2:
                    variables.Add(new SheepVariable(variableName, SheepValueKind.Float, 0, reader.ReadSingle()));
                    break;
                case 3:
                    reader.Skip(4);
                    variables.Add(new SheepVariable(variableName, SheepValueKind.String, 0, 0));
                    break;
                default:
                    // An unknown type would desynchronise everything after it.
                    throw Corrupt(file, reader.Position,
                        "a variable type of 1, 2 or 3",
                        type.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return variables;
    }

    private static List<(string, int)> ReadFunctions(ref SpanReader reader)
    {
        reader.Skip(12);
        int count = reader.ReadInt32();
        reader.Skip(4 * count);

        List<(string, int)> functions = new(count);
        for (int i = 0; i < count; i++)
        {
            string name = ReadLengthPrefixedString(ref reader);
            reader.Skip(2); // unknown
            functions.Add((name, reader.ReadInt32()));
        }

        return functions;
    }

    private static byte[] ReadCode(ref SpanReader reader, ReadOnlySpan<byte> data)
    {
        reader.Skip(8);
        int length = reader.ReadInt32();
        int blocks = reader.ReadInt32();

        if (blocks != 1)
        {
            throw Corrupt("<memory>", reader.Position, "exactly one code block",
                blocks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        reader.Skip(4); // offset to the single block, always zero

        int start = reader.Position;
        int end = Math.Min(start + length, data.Length);
        return data[start..end].ToArray();
    }

    /// <summary>Reads a 16-bit length-prefixed string.</summary>
    /// <remarks>
    /// The field occupies two bytes of length, then that many bytes, then one further
    /// byte. Consuming only the counted bytes leaves the reader one short, and because
    /// every subsequent field then reads from one byte early, the failure appears much
    /// later as an absurd length rather than at the string itself.
    /// </remarks>
    private static string ReadLengthPrefixedString(ref SpanReader reader)
    {
        int length = reader.ReadUInt16();
        string value = reader.ReadFixedString(Math.Max(0, length));
        reader.Skip(1);
        return value;
    }

    private static FormatParseException Corrupt(string file, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1070",
            DiagnosticSeverity.Error,
            "Compiled Sheep script is corrupt or is not a supported variant.",
            file,
            offset,
            expected,
            actual,
            "Re-extract the asset and report the script name."));
}

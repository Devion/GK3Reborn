using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Sheep;

namespace GK3Reborn.Tests.Sheep;

/// <summary>Builds a compiled script, so the VM is tested against real file layout.</summary>
internal sealed class ScriptBuilder
{
    private readonly List<(string Name, sbyte Return, sbyte[] Args)> _imports = [];
    private readonly List<string> _strings = [];
    private readonly List<(string Name, SheepValueKind Kind, int Int, float Float)> _variables = [];
    private readonly List<(string Name, int Offset)> _functions = [];
    private readonly List<byte> _code = [];

    public ScriptBuilder Import(string name, sbyte returnType = 0, params sbyte[] arguments)
    {
        _imports.Add((name, returnType, arguments));
        return this;
    }

    public int String(string value)
    {
        int offset = _strings.Sum(s => s.Length + 1);
        _strings.Add(value);
        return offset;
    }

    public ScriptBuilder Variable(string name, SheepValueKind kind, int intValue = 0, float floatValue = 0)
    {
        _variables.Add((name, kind, intValue, floatValue));
        return this;
    }

    public ScriptBuilder Function(string name)
    {
        _functions.Add((name, _code.Count));
        return this;
    }

    public int Here => _code.Count;

    public ScriptBuilder Op(SheepOpcode opcode)
    {
        _code.Add((byte)opcode);
        return this;
    }

    public ScriptBuilder Op(SheepOpcode opcode, int operand)
    {
        _code.Add((byte)opcode);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, operand);
        _code.AddRange(bytes.ToArray());
        return this;
    }

    public ScriptBuilder OpF(SheepOpcode opcode, float operand)
    {
        _code.Add((byte)opcode);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, operand);
        _code.AddRange(bytes.ToArray());
        return this;
    }

    /// <summary>Patches a previously emitted operand, for forward branches.</summary>
    public ScriptBuilder Patch(int instructionAddress, int operand)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, operand);
        for (int i = 0; i < 4; i++)
        {
            _code[instructionAddress + 1 + i] = bytes[i];
        }

        return this;
    }

    public SheepScriptFile Build(string name = "TEST.SHP")
    {
        var sections = new List<(string Name, byte[] Data)>
        {
            ("SysImports", BuildImports()),
            ("StringConsts", BuildStrings()),
            ("Variables", BuildVariables()),
            ("Functions", BuildFunctions()),
            ("Code", BuildCode()),
        };

        var header = new MemoryStream();
        var w = new BinaryWriter(header);
        w.Write("GK3Sheep"u8);
        w.Write(0u);

        int headerSize = 8 + 4 + 4 + 8 + 4 + (sections.Count * 4);
        w.Write(headerSize);
        w.Write(headerSize);
        w.Write(0);
        w.Write(sections.Count);

        int running = 0;
        foreach ((string _, byte[] data) in sections)
        {
            w.Write(running);
            running += data.Length;
        }

        foreach ((string _, byte[] data) in sections)
        {
            w.Write(data);
        }

        w.Flush();
        return SheepScriptFile.Parse(header.ToArray(), name);
    }

    private static void WriteName(BinaryWriter w, string name)
    {
        // Two bytes of length, the bytes, then one more.
        w.Write((ushort)name.Length);
        w.Write(Encoding.ASCII.GetBytes(name));
        w.Write((byte)0);
    }

    private static byte[] Section(string name, Action<BinaryWriter> body)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        byte[] tag = new byte[12];
        Encoding.ASCII.GetBytes(name).CopyTo(tag, 0);
        w.Write(tag);
        body(w);
        w.Flush();
        return stream.ToArray();
    }

    private byte[] BuildImports() => Section("SysImports", w =>
    {
        w.Write(new byte[12]);
        w.Write(_imports.Count);
        w.Write(new byte[4 * _imports.Count]);

        foreach ((string name, sbyte ret, sbyte[] args) in _imports)
        {
            WriteName(w, name);
            w.Write(ret);
            w.Write((sbyte)args.Length);
            foreach (sbyte a in args)
            {
                w.Write(a);
            }
        }
    });

    private byte[] BuildStrings() => Section("StringConsts", w =>
    {
        byte[] block = Encoding.ASCII.GetBytes(
            string.Concat(_strings.Select(s => s + "\0")));

        w.Write(new byte[8]);
        w.Write(block.Length);
        w.Write(_strings.Count);

        int offset = 0;
        foreach (string s in _strings)
        {
            w.Write(offset);
            offset += s.Length + 1;
        }

        w.Write(block);
    });

    private byte[] BuildVariables() => Section("Variables", w =>
    {
        w.Write(new byte[12]);
        w.Write(_variables.Count);
        w.Write(new byte[4 * _variables.Count]);

        foreach ((string name, SheepValueKind kind, int i, float f) in _variables)
        {
            WriteName(w, name);
            switch (kind)
            {
                case SheepValueKind.Int:
                    w.Write(1);
                    w.Write(i);
                    break;
                case SheepValueKind.Float:
                    w.Write(2);
                    w.Write(f);
                    break;
                default:
                    w.Write(3);
                    w.Write(0);
                    break;
            }
        }
    });

    private byte[] BuildFunctions() => Section("Functions", w =>
    {
        w.Write(new byte[12]);
        w.Write(_functions.Count);
        w.Write(new byte[4 * _functions.Count]);

        foreach ((string name, int offset) in _functions)
        {
            WriteName(w, name);
            w.Write((ushort)0);
            w.Write(offset);
        }
    });

    private byte[] BuildCode() => Section("Code", w =>
    {
        w.Write(new byte[8]);
        w.Write(_code.Count);
        w.Write(1);
        w.Write(0);
        w.Write(_code.ToArray());
    });
}


/// <summary>Convenience for building a named script in one call.</summary>
internal static class TestScripts
{
    /// <summary>Builds a script.</summary>
    /// <param name="name">Name the script is parsed under.</param>
    /// <param name="configure">Fills in imports, strings, variables and code.</param>
    /// <returns>The parsed script.</returns>
    public static SheepScriptFile Build(string name, Action<ScriptBuilder> configure)
    {
        var builder = new ScriptBuilder();
        configure(builder);
        return builder.Build(name);
    }
}

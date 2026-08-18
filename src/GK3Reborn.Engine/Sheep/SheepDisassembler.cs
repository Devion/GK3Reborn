using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace GK3Reborn.Sheep;

/// <summary>One decoded instruction.</summary>
/// <param name="Address">Byte offset of the opcode within the bytecode.</param>
/// <param name="Opcode">The instruction.</param>
/// <param name="Operand">Its operand, or null when it takes none.</param>
/// <param name="Comment">Resolved meaning of the operand, where one exists.</param>
public readonly record struct SheepInstruction(
    int Address,
    SheepOpcode Opcode,
    int? Operand,
    string? Comment);

/// <summary>
/// Decodes compiled Sheep back into a readable listing.
/// </summary>
/// <remarks>
/// <para>
/// Reading 224 scripts as hex is not practical, and the bytecode is the only description
/// of the game's logic that is guaranteed to match what shipped. A disassembler is
/// therefore the first thing P4 needs: it makes the scripts inspectable, and it is the
/// natural place to prove the instruction set is understood, since an unknown opcode or a
/// miscounted operand desynchronises the stream immediately and visibly.
/// </para>
/// <para>
/// Operands are resolved against the script's own tables, so a call shows the function it
/// invokes and a push shows the string it pushes rather than an index.
/// </para>
/// </remarks>
public static class SheepDisassembler
{
    /// <summary>Decodes a script's bytecode.</summary>
    /// <param name="script">The script.</param>
    /// <returns>Instructions in address order.</returns>
    /// <remarks>
    /// Decoding stops at the first byte that is not a known opcode, rather than guessing:
    /// past that point the stream is no longer aligned and everything after would be
    /// fiction. The caller can tell from the last address whether the whole script decoded.
    /// </remarks>
    public static IReadOnlyList<SheepInstruction> Decode(SheepScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);

        List<SheepInstruction> instructions = [];
        ReadOnlySpan<byte> code = script.Bytecode;
        int at = 0;

        while (at < code.Length)
        {
            byte raw = code[at];
            if (!SheepOpcodes.IsDefined(raw))
            {
                break;
            }

            var opcode = (SheepOpcode)raw;
            SheepOperand kind = SheepOpcodes.OperandOf(opcode);
            int address = at;
            at++;

            if (kind == SheepOperand.None)
            {
                instructions.Add(new SheepInstruction(address, opcode, null, null));
                continue;
            }

            if (at + 4 > code.Length)
            {
                break;
            }

            int operand = BinaryPrimitives.ReadInt32LittleEndian(code[at..]);
            float asFloat = BinaryPrimitives.ReadSingleLittleEndian(code[at..]);
            at += 4;

            instructions.Add(new SheepInstruction(
                address, opcode, operand, Describe(script, kind, operand, asFloat)));
        }

        return instructions;
    }

    /// <summary>Renders a script as a readable listing.</summary>
    /// <param name="script">The script.</param>
    /// <returns>The listing.</returns>
    public static string Render(SheepScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var output = new StringBuilder();
        output.Append(CultureInfo.InvariantCulture, $"// {script.Name}\n");

        if (script.Variables.Count > 0)
        {
            output.Append("\nsymbols\n{\n");
            foreach (SheepVariable variable in script.Variables)
            {
                string initial = variable.Kind switch
                {
                    SheepValueKind.Int => variable.IntValue.ToString(CultureInfo.InvariantCulture),
                    SheepValueKind.Float => variable.FloatValue.ToString("0.0###", CultureInfo.InvariantCulture),
                    _ => "\"\"",
                };

                output.Append(CultureInfo.InvariantCulture,
                    $"    {variable.Kind.ToString().ToLowerInvariant()} {variable.Name} = {initial};\n");
            }

            output.Append("}\n");
        }

        if (script.Imports.Count > 0)
        {
            output.Append("\n// system functions used\n");
            foreach (SheepImport import in script.Imports)
            {
                output.Append(CultureInfo.InvariantCulture,
                    $"//   {TypeName(import.ReturnType)} {import.Name}("
                    + $"{string.Join(", ", import.ArgumentTypes.Select(TypeName))})\n");
            }
        }

        IReadOnlyList<SheepInstruction> instructions = Decode(script);
        Dictionary<int, string> functionStarts = script.Functions
            .GroupBy(f => f.Offset)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(f => f.Name)), EqualityComparer<int>.Default);

        // Only addresses something actually branches to get a label, so the listing is
        // not cluttered with labels nothing uses.
        HashSet<int> branchTargets = [.. instructions
            .Where(i => SheepOpcodes.OperandOf(i.Opcode) == SheepOperand.Address && i.Operand.HasValue)
            .Select(i => i.Operand!.Value)];

        output.Append("\ncode\n{\n");

        foreach (SheepInstruction instruction in instructions)
        {
            if (functionStarts.TryGetValue(instruction.Address, out string? function))
            {
                output.Append(CultureInfo.InvariantCulture, $"\n  {function}\n");
            }

            if (branchTargets.Contains(instruction.Address))
            {
                output.Append(CultureInfo.InvariantCulture, $"  L{instruction.Address:D4}:\n");
            }

            output.Append(CultureInfo.InvariantCulture, $"    {instruction.Address,5}  {instruction.Opcode,-18}");

            if (instruction.Operand is { } operand)
            {
                output.Append(CultureInfo.InvariantCulture, $" {operand,-10}");
            }
            else
            {
                output.Append(new string(' ', 11));
            }

            if (instruction.Comment is { } comment)
            {
                output.Append(CultureInfo.InvariantCulture, $" // {comment}");
            }

            output.Append('\n');
        }

        output.Append("}\n");

        int decoded = instructions.Count == 0 ? 0 : instructions[^1].Address + 1;
        if (decoded < script.Bytecode.Length)
        {
            output.Append(CultureInfo.InvariantCulture,
                $"\n// decoding stopped at {decoded} of {script.Bytecode.Length} bytes\n");
        }

        return output.ToString();
    }

    private static string? Describe(SheepScriptFile script, SheepOperand kind, int operand, float asFloat) =>
        kind switch
        {
            SheepOperand.FunctionIndex => operand >= 0 && operand < script.Imports.Count
                ? script.Imports[operand].Name
                : $"unknown import {operand}",

            SheepOperand.VariableIndex => operand >= 0 && operand < script.Variables.Count
                ? script.Variables[operand].Name
                : $"unknown variable {operand}",

            SheepOperand.StringOffset => script.StringConstants.TryGetValue(operand, out string? text)
                ? $"\"{text}\""
                : $"string at {operand}",

            SheepOperand.Float => asFloat.ToString("0.0###", CultureInfo.InvariantCulture),
            SheepOperand.Address => $"-> L{operand:D4}",
            _ => null,
        };

    private static string TypeName(sbyte type) => type switch
    {
        0 => "void",
        1 => "int",
        2 => "float",
        3 => "string",
        _ => $"type{type}",
    };
}

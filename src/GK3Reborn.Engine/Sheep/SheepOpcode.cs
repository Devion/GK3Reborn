namespace GK3Reborn.Sheep;

/// <summary>The Sheep virtual machine's instruction set.</summary>
/// <remarks>
/// Taken from G-Engine's <c>SheepInstruction</c>. Opcode <c>0x0C</c> is absent: the
/// original documentation describes it as a deprecated export instruction, and no
/// retail script uses it.
/// </remarks>
public enum SheepOpcode : byte
{
    /// <summary>Spin in place forever. Used to halt a thread deliberately.</summary>
    SitnSpin = 0x00,

    /// <summary>Yield to the scheduler.</summary>
    Yield = 0x01,

    /// <summary>Call a system function returning nothing.</summary>
    CallSysFunctionV = 0x02,

    /// <summary>Call a system function returning an int.</summary>
    CallSysFunctionI = 0x03,

    /// <summary>Call a system function returning a float.</summary>
    CallSysFunctionF = 0x04,

    /// <summary>Call a system function returning a string.</summary>
    CallSysFunctionS = 0x05,

    /// <summary>Unconditional branch.</summary>
    Branch = 0x06,

    /// <summary>Branch produced by a <c>goto</c>.</summary>
    BranchGoto = 0x07,

    /// <summary>Branch when the top of the stack is zero.</summary>
    BranchIfZero = 0x08,

    /// <summary>Begin a wait block.</summary>
    BeginWait = 0x09,

    /// <summary>End a wait block, blocking until its calls complete.</summary>
    EndWait = 0x0A,

    /// <summary>Return from the current function.</summary>
    ReturnV = 0x0B,

    /// <summary>Store an int into a variable.</summary>
    StoreI = 0x0D,

    /// <summary>Store a float into a variable.</summary>
    StoreF = 0x0E,

    /// <summary>Store a string into a variable.</summary>
    StoreS = 0x0F,

    /// <summary>Load an int from a variable.</summary>
    LoadI = 0x10,

    /// <summary>Load a float from a variable.</summary>
    LoadF = 0x11,

    /// <summary>Load a string from a variable.</summary>
    LoadS = 0x12,

    /// <summary>Push an int constant.</summary>
    PushI = 0x13,

    /// <summary>Push a float constant.</summary>
    PushF = 0x14,

    /// <summary>Push a string-constant offset.</summary>
    PushS = 0x15,

    /// <summary>Discard the top of the stack.</summary>
    Pop = 0x16,

    /// <summary>Integer addition.</summary>
    AddI = 0x17,

    /// <summary>Float addition.</summary>
    AddF = 0x18,

    /// <summary>Integer subtraction.</summary>
    SubtractI = 0x19,

    /// <summary>Float subtraction.</summary>
    SubtractF = 0x1A,

    /// <summary>Integer multiplication.</summary>
    MultiplyI = 0x1B,

    /// <summary>Float multiplication.</summary>
    MultiplyF = 0x1C,

    /// <summary>Integer division.</summary>
    DivideI = 0x1D,

    /// <summary>Float division.</summary>
    DivideF = 0x1E,

    /// <summary>Integer negation.</summary>
    NegateI = 0x1F,

    /// <summary>Float negation.</summary>
    NegateF = 0x20,

    /// <summary>Integer equality.</summary>
    IsEqualI = 0x21,

    /// <summary>Float equality.</summary>
    IsEqualF = 0x22,

    /// <summary>Integer inequality.</summary>
    IsNotEqualI = 0x23,

    /// <summary>Float inequality.</summary>
    IsNotEqualF = 0x24,

    /// <summary>Integer greater-than.</summary>
    IsGreaterI = 0x25,

    /// <summary>Float greater-than.</summary>
    IsGreaterF = 0x26,

    /// <summary>Integer less-than.</summary>
    IsLessI = 0x27,

    /// <summary>Float less-than.</summary>
    IsLessF = 0x28,

    /// <summary>Integer greater-or-equal.</summary>
    IsGreaterEqualI = 0x29,

    /// <summary>Float greater-or-equal.</summary>
    IsGreaterEqualF = 0x2A,

    /// <summary>Integer less-or-equal.</summary>
    IsLessEqualI = 0x2B,

    /// <summary>Float less-or-equal.</summary>
    IsLessEqualF = 0x2C,

    /// <summary>Convert an int on the stack to a float.</summary>
    IToF = 0x2D,

    /// <summary>Convert a float on the stack to an int.</summary>
    FToI = 0x2E,

    /// <summary>Integer modulo.</summary>
    Modulo = 0x2F,

    /// <summary>Logical and.</summary>
    And = 0x30,

    /// <summary>Logical or.</summary>
    Or = 0x31,

    /// <summary>Logical not.</summary>
    Not = 0x32,

    /// <summary>Resolve a string-constant offset to its text.</summary>
    GetString = 0x33,

    /// <summary>Break into the debugger.</summary>
    DebugBreakpoint = 0x34,
}

/// <summary>What kind of operand an opcode carries, if any.</summary>
/// <remarks>
/// Members are named for Sheep's own types rather than .NET's, matching the language
/// specification, which is why CA1720 is suppressed.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Member names mirror the Sheep language's own type names.")]
public enum SheepOperand
{
    /// <summary>No operand.</summary>
    None,

    /// <summary>A 32-bit integer literal.</summary>
    Int,

    /// <summary>A 32-bit float literal.</summary>
    Float,

    /// <summary>An index into the system-import table.</summary>
    FunctionIndex,

    /// <summary>An index into the variable table.</summary>
    VariableIndex,

    /// <summary>An offset into the string-constant block.</summary>
    StringOffset,

    /// <summary>An absolute bytecode address.</summary>
    Address,
}

/// <summary>Describes the instruction set.</summary>
public static class SheepOpcodes
{
    /// <summary>Reports what operand an opcode takes.</summary>
    /// <param name="opcode">The opcode.</param>
    /// <returns>Its operand kind.</returns>
    public static SheepOperand OperandOf(SheepOpcode opcode) => opcode switch
    {
        SheepOpcode.CallSysFunctionV or SheepOpcode.CallSysFunctionI
            or SheepOpcode.CallSysFunctionF or SheepOpcode.CallSysFunctionS => SheepOperand.FunctionIndex,

        SheepOpcode.Branch or SheepOpcode.BranchGoto or SheepOpcode.BranchIfZero => SheepOperand.Address,

        SheepOpcode.StoreI or SheepOpcode.StoreF or SheepOpcode.StoreS
            or SheepOpcode.LoadI or SheepOpcode.LoadF or SheepOpcode.LoadS => SheepOperand.VariableIndex,

        SheepOpcode.PushI => SheepOperand.Int,
        SheepOpcode.PushF => SheepOperand.Float,
        SheepOpcode.PushS => SheepOperand.StringOffset,

        // Both conversion instructions carry a stack index saying how far down to reach,
        // which is easy to miss because the name suggests they act on the top.
        SheepOpcode.IToF or SheepOpcode.FToI => SheepOperand.Int,

        _ => SheepOperand.None,
    };

    /// <summary>True when the opcode is one the instruction set defines.</summary>
    /// <param name="value">Raw byte read from the bytecode.</param>
    /// <returns>Whether it maps to a known instruction.</returns>
    public static bool IsDefined(byte value) =>
        value != 0x0C && Enum.IsDefined((SheepOpcode)value);
}

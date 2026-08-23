using System.Buffers.Binary;
using System.Globalization;
using GK3Reborn.Formats;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Sheep;

/// <summary>
/// Turns a Sheep syntax tree into bytecode the virtual machine runs.
/// </summary>
/// <remarks>
/// <para>
/// The last piece of P4's front end. The output is a <see cref="SheepScriptFile"/> — the
/// same thing the reader produces from a shipped <c>.SHP</c> — so anything that can run the
/// game's own scripts runs these, and <see cref="SheepScriptWriter"/> can put one back on
/// disk in the original's container.
/// </para>
/// <para>
/// The instruction set is typed rather than polymorphic: there is an <c>AddI</c> and an
/// <c>AddF</c> and nothing that adds whichever it is given. So the compiler has to know the
/// type of every expression, which is the whole of the work here. Three types — int, float,
/// string — and one conversion, <c>IToF</c>, whose operand is <b>how far down the stack to
/// reach</b> rather than a value. Emitting it as though it converted the top is a mistake
/// that only shows up in expressions mixing the two.
/// </para>
/// <para>
/// Calls follow the original's convention exactly, because the machine reads it: arguments
/// left to right, then the count as an int, then the call. A void call still leaves a value
/// behind and the compiler emits the matching <c>Pop</c>; a string is pushed as its offset
/// in the constant pool and then fetched with <c>GetString</c>.
/// </para>
/// <para>
/// Every function ends with <c>ReturnV</c> and <b>four</b> <c>SitnSpin</c> bytes. That is
/// not a guess: the corpus contains 5,924 of the one and 1,481 of the other, which is four
/// to one exactly, and the next function always starts after them.
/// </para>
/// </remarks>
public sealed class SheepCompiler
{
    /// <summary>How many halt instructions pad the gap between two functions.</summary>
    public const int FunctionPadding = 4;

    private readonly SheepScriptNode _script;
    private readonly SheepSignatures _signatures;

    private readonly List<byte> _code = [];
    private readonly List<SheepImport> _imports = [];
    private readonly Dictionary<string, int> _importIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _strings = [];
    private readonly Dictionary<string, int> _stringOffset = new(StringComparer.Ordinal);
    private readonly List<SheepVariable> _variables = [];
    private readonly Dictionary<string, int> _variableIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, int Offset)> _functions = [];

    private readonly Dictionary<string, int> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Label, int Patch, int Line)> _pendingGotos = [];

    private int _stringBytes;

    private SheepCompiler(SheepScriptNode script, SheepSignatures signatures)
    {
        _script = script;
        _signatures = signatures;
    }

    /// <summary>Diagnostics raised while compiling.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Compiles source straight through.</summary>
    /// <param name="text">The source.</param>
    /// <param name="name">Name used in diagnostics and carried onto the result.</param>
    /// <param name="signatures">
    /// What the system functions take and return. Without it the compiler assumes every
    /// call returns an int and takes what it was given, which is right often enough to be
    /// useful and wrong in exactly the places a float argument appears.
    /// </param>
    /// <returns>The compiled script.</returns>
    /// <exception cref="FormatParseException">The source does not parse or does not compile.</exception>
    public static SheepScriptFile Compile(
        string text, string name = "<memory>", SheepSignatures? signatures = null) =>
        Compile(SheepParser.Parse(text, name), signatures);

    /// <summary>Compiles a syntax tree.</summary>
    /// <param name="script">The tree.</param>
    /// <param name="signatures">What the system functions take and return.</param>
    /// <returns>The compiled script.</returns>
    /// <exception cref="FormatParseException">The tree does not compile.</exception>
    public static SheepScriptFile Compile(
        SheepScriptNode script, SheepSignatures? signatures = null)
    {
        ArgumentNullException.ThrowIfNull(script);

        var compiler = new SheepCompiler(script, signatures ?? new SheepSignatures());
        return compiler.Run();
    }

    private SheepScriptFile Run()
    {
        // The pool opens with an empty string, so the first real one sits at offset one.
        // Every single one of the game's 224 scripts is laid out that way, and matching it
        // is what makes the bytecode this emits byte-identical to the original compiler's
        // rather than merely equivalent to it.
        Intern(string.Empty);

        foreach (SheepSymbolNode symbol in _script.Symbols)
        {
            Declare(symbol);
        }

        foreach (SheepFunctionNode function in _script.Functions)
        {
            _labels.Clear();
            _pendingGotos.Clear();
            _functions.Add((function.Name, _code.Count));

            foreach (SheepStatementNode statement in function.Body)
            {
                Statement(statement);
            }

            Emit(SheepOpcode.ReturnV);

            for (int i = 0; i < FunctionPadding; i++)
            {
                Emit(SheepOpcode.SitnSpin);
            }

            Resolve(function);
        }

        return SheepScriptFile.FromParts(
            _script.Name,
            _imports,
            _strings.Select((s, i) => (Offset: OffsetOf(i), Text: s))
                .ToDictionary(e => e.Offset, e => e.Text),
            _variables,
            _functions,
            [.. _code]);
    }

    /// <summary>Fills in the jumps whose targets were not known when they were written.</summary>
    private void Resolve(SheepFunctionNode function)
    {
        foreach ((string label, int patch, int line) in _pendingGotos)
        {
            if (!_labels.TryGetValue(label, out int target))
            {
                throw Malformed(
                    line, $"a label '{label}' in {function.Name}", "no such label",
                    "A goto names a label the function does not declare.");
            }

            BinaryPrimitives.WriteInt32LittleEndian(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_code)[patch..], target);
        }
    }

    private void Declare(SheepSymbolNode symbol)
    {
        if (_variableIndex.ContainsKey(symbol.Name))
        {
            throw Malformed(
                symbol.Line, "a name not already declared", symbol.Name,
                "Two symbols in one script share a name.");
        }

        // Only a constant may initialise a symbol: the block runs before any code does, so
        // there is nowhere to evaluate a call.
        SheepVariable variable = (symbol.Kind, symbol.Initial) switch
        {
            (SheepValueKind.Int, null) => new SheepVariable(symbol.Name, SheepValueKind.Int, 0, 0),
            (SheepValueKind.Int, SheepIntegerNode i) =>
                new SheepVariable(symbol.Name, SheepValueKind.Int, i.Value, 0),
            (SheepValueKind.Float, null) => new SheepVariable(symbol.Name, SheepValueKind.Float, 0, 0),
            (SheepValueKind.Float, SheepFloatNode f) =>
                new SheepVariable(symbol.Name, SheepValueKind.Float, 0, f.Value),
            (SheepValueKind.Float, SheepIntegerNode i) =>
                new SheepVariable(symbol.Name, SheepValueKind.Float, 0, i.Value),
            (SheepValueKind.String, null) => new SheepVariable(symbol.Name, SheepValueKind.String, 0, 0),
            (SheepValueKind.String, SheepStringNode) =>
                new SheepVariable(symbol.Name, SheepValueKind.String, 0, 0),
            _ => throw Malformed(
                symbol.Line, $"a constant {symbol.Kind} for {symbol.Name}", "something else",
                "A symbol's initial value must be a constant of its own type."),
        };

        _variableIndex[symbol.Name] = _variables.Count;
        _variables.Add(variable);
    }

    private void Statement(SheepStatementNode statement)
    {
        switch (statement)
        {
            case SheepBlockNode block:
                foreach (SheepStatementNode inner in block.Statements)
                {
                    Statement(inner);
                }

                break;

            case SheepExpressionStatementNode expression:
                Discard(expression.Expression, expression.Line);
                break;

            case SheepAssignmentNode assignment:
                Assign(assignment);
                break;

            case SheepIfNode conditional:
                If(conditional);
                break;

            case SheepReturnNode:
                Emit(SheepOpcode.ReturnV);
                break;

            case SheepLabelNode label:
                _labels[label.Name] = _code.Count;
                break;

            case SheepGotoNode jump:
                Emit(SheepOpcode.BranchGoto);
                _pendingGotos.Add((jump.Label, _code.Count, jump.Line));
                Operand(0);
                break;

            case SheepSitnSpinNode:
                Emit(SheepOpcode.SitnSpin);
                break;

            case SheepBreakpointNode:
                Emit(SheepOpcode.DebugBreakpoint);
                break;

            case SheepWaitNode wait:
                Emit(SheepOpcode.BeginWait);

                if (wait.Body is { } body)
                {
                    Statement(body);
                }

                Emit(SheepOpcode.EndWait);
                break;

            default:
                throw Malformed(
                    statement.Line, "a statement the compiler emits", statement.GetType().Name,
                    "The parser produced a node the compiler does not handle.");
        }
    }

    /// <summary>Evaluates something for its effect and throws the value away.</summary>
    /// <remarks>
    /// Every call leaves something behind, <b>including a void one</b> — the machine pushes
    /// a result either way. The matching Pop is the compiler's job, not the machine's,
    /// which is why the corpus has exactly as many of them as it has void calls: 18,447 of
    /// each.
    /// </remarks>
    private void Discard(SheepExpressionNode expression, int line)
    {
        Value(expression, line);
        Emit(SheepOpcode.Pop);
    }

    private void Assign(SheepAssignmentNode assignment)
    {
        if (!_variableIndex.TryGetValue(assignment.Name, out int index))
        {
            throw Malformed(
                assignment.Line, "a declared symbol", assignment.Name,
                "Assignment names a variable the symbols block does not declare.");
        }

        SheepValueKind wanted = _variables[index].Kind;
        SheepValueKind got = Typed(assignment.Value, assignment.Line);

        if (wanted == SheepValueKind.Float && got == SheepValueKind.Int)
        {
            Emit(SheepOpcode.IToF);
            Operand(0);
            got = SheepValueKind.Float;
        }

        if (wanted != got)
        {
            throw Malformed(
                assignment.Line, $"a {wanted} for {assignment.Name}", got.ToString(),
                "Sheep does not convert between these types.");
        }

        Emit(wanted switch
        {
            SheepValueKind.Float => SheepOpcode.StoreF,
            SheepValueKind.String => SheepOpcode.StoreS,
            _ => SheepOpcode.StoreI,
        });

        Operand(index);
    }

    private void If(SheepIfNode conditional)
    {
        Typed(conditional.Condition, conditional.Line);

        Emit(SheepOpcode.BranchIfZero);
        int overThen = _code.Count;
        Operand(0);

        Statement(conditional.Then);

        if (conditional.Else is null)
        {
            Patch(overThen, _code.Count);
            return;
        }

        Emit(SheepOpcode.Branch);
        int overElse = _code.Count;
        Operand(0);

        Patch(overThen, _code.Count);
        Statement(conditional.Else);
        Patch(overElse, _code.Count);
    }

    /// <summary>Emits an expression and says what type it left on the stack.</summary>
    /// <returns>The type, or null when the expression is a call that returns nothing.</returns>
    private SheepValueKind? Value(SheepExpressionNode expression, int line)
    {
        switch (expression)
        {
            case SheepIntegerNode number:
                Emit(SheepOpcode.PushI);
                Operand(number.Value);
                return SheepValueKind.Int;

            case SheepFloatNode number:
                Emit(SheepOpcode.PushF);
                OperandFloat(number.Value);
                return SheepValueKind.Float;

            case SheepStringNode text:
                Emit(SheepOpcode.PushS);
                Operand(Intern(text.Value));
                Emit(SheepOpcode.GetString);
                return SheepValueKind.String;

            case SheepVariableNode variable:
                return Load(variable, line);

            case SheepCallNode call:
                return Call(call, line);

            case SheepUnaryNode unary:
                return Unary(unary, line);

            case SheepBinaryNode binary:
                return Binary(binary, line);

            default:
                throw Malformed(
                    line, "an expression the compiler emits", expression.GetType().Name,
                    "The parser produced a node the compiler does not handle.");
        }
    }

    /// <summary>Emits an expression that has to leave a value behind.</summary>
    /// <remarks>
    /// Which is everywhere but a statement. A void call used as a value is a mistake the
    /// machine cannot notice — it pushes something either way — so it is caught here, where
    /// the line number is still to hand.
    /// </remarks>
    private SheepValueKind Typed(SheepExpressionNode expression, int line) =>
        Value(expression, line) ??
        throw Malformed(
            line, "an expression with a value", "a call that returns nothing",
            "That function returns nothing, so there is nothing to use it as.");

    private SheepValueKind Load(SheepVariableNode variable, int line)
    {
        if (!_variableIndex.TryGetValue(variable.Name, out int index))
        {
            throw Malformed(
                line, "a declared symbol", variable.Name,
                "The symbols block does not declare it. A system function needs brackets.");
        }

        SheepValueKind kind = _variables[index].Kind;

        Emit(kind switch
        {
            SheepValueKind.Float => SheepOpcode.LoadF,
            SheepValueKind.String => SheepOpcode.LoadS,
            _ => SheepOpcode.LoadI,
        });

        Operand(index);
        return kind;
    }

    private SheepValueKind? Call(SheepCallNode call, int line)
    {
        _signatures.TryGet(call.Name, out SheepImport known);

        for (int i = 0; i < call.Arguments.Count; i++)
        {
            SheepValueKind got = Typed(call.Arguments[i], line);

            // Converted the moment it is pushed, so the reach is always zero. The original
            // does the same: SetTimerSeconds(2) is a PushI and an IToF, not a PushF.
            if (known.Name is not null &&
                i < known.ArgumentTypes.Count &&
                known.ArgumentTypes[i] == SheepSignatures.Float &&
                got == SheepValueKind.Int)
            {
                Emit(SheepOpcode.IToF);
                Operand(0);
            }
        }

        Emit(SheepOpcode.PushI);
        Operand(call.Arguments.Count);

        sbyte returns = known.Name is not null ? known.ReturnType : SheepSignatures.Int;

        Emit(returns switch
        {
            SheepSignatures.Float => SheepOpcode.CallSysFunctionF,
            SheepSignatures.String => SheepOpcode.CallSysFunctionS,
            SheepSignatures.Void => SheepOpcode.CallSysFunctionV,
            _ => SheepOpcode.CallSysFunctionI,
        });

        Operand(Import(call, known, returns));

        return returns switch
        {
            SheepSignatures.Float => SheepValueKind.Float,
            SheepSignatures.String => SheepValueKind.String,
            SheepSignatures.Void => null,
            _ => SheepValueKind.Int,
        };
    }

    private SheepValueKind Unary(SheepUnaryNode unary, int line)
    {
        SheepValueKind kind = Typed(unary.Operand, line);

        if (unary.Operator == "!")
        {
            Emit(SheepOpcode.Not);
            return SheepValueKind.Int;
        }

        if (kind == SheepValueKind.String)
        {
            throw Malformed(
                line, "a number to negate", "a string", "Sheep has no string arithmetic.");
        }

        Emit(kind == SheepValueKind.Float ? SheepOpcode.NegateF : SheepOpcode.NegateI);
        return kind;
    }

    private SheepValueKind Binary(SheepBinaryNode binary, int line)
    {
        // Logical operators are integer operators and take whatever they are given as a
        // truth value, so neither side is converted.
        if (binary.Operator is "&&" or "||")
        {
            Typed(binary.Left, line);
            Typed(binary.Right, line);
            Emit(binary.Operator == "&&" ? SheepOpcode.And : SheepOpcode.Or);
            return SheepValueKind.Int;
        }

        SheepValueKind left = Typed(binary.Left, line);
        SheepValueKind right = Typed(binary.Right, line);

        if (left == SheepValueKind.String || right == SheepValueKind.String)
        {
            throw Malformed(
                line, $"numbers either side of '{binary.Operator}'", "a string",
                "The instruction set has no string arithmetic or comparison.");
        }

        bool floating = left == SheepValueKind.Float || right == SheepValueKind.Float;

        if (floating && left == SheepValueKind.Int)
        {
            // One below the top: the right operand is already sitting on it.
            Emit(SheepOpcode.IToF);
            Operand(1);
        }

        if (floating && right == SheepValueKind.Int)
        {
            Emit(SheepOpcode.IToF);
            Operand(0);
        }

        if (binary.Operator == "%")
        {
            if (floating)
            {
                throw Malformed(line, "whole numbers either side of '%'", "a float",
                    "The instruction set has one modulo and it is the integer one.");
            }

            Emit(SheepOpcode.Modulo);
            return SheepValueKind.Int;
        }

        Emit(Arithmetic(binary.Operator, floating, line));

        return binary.Operator switch
        {
            "+" or "-" or "*" or "/" => floating ? SheepValueKind.Float : SheepValueKind.Int,
            _ => SheepValueKind.Int,
        };
    }

    private SheepOpcode Arithmetic(string op, bool floating, int line) => op switch
    {
        "+" => floating ? SheepOpcode.AddF : SheepOpcode.AddI,
        "-" => floating ? SheepOpcode.SubtractF : SheepOpcode.SubtractI,
        "*" => floating ? SheepOpcode.MultiplyF : SheepOpcode.MultiplyI,
        "/" => floating ? SheepOpcode.DivideF : SheepOpcode.DivideI,
        "==" => floating ? SheepOpcode.IsEqualF : SheepOpcode.IsEqualI,
        "!=" or "<>" => floating ? SheepOpcode.IsNotEqualF : SheepOpcode.IsNotEqualI,
        "<" => floating ? SheepOpcode.IsLessF : SheepOpcode.IsLessI,
        ">" => floating ? SheepOpcode.IsGreaterF : SheepOpcode.IsGreaterI,
        "<=" => floating ? SheepOpcode.IsLessEqualF : SheepOpcode.IsLessEqualI,
        ">=" => floating ? SheepOpcode.IsGreaterEqualF : SheepOpcode.IsGreaterEqualI,
        _ => throw Malformed(line, "an operator the instruction set has", op, "Check the operator."),
    };

    /// <summary>Finds or adds a function's import entry.</summary>
    private int Import(SheepCallNode call, SheepImport known, sbyte returns)
    {
        if (_importIndex.TryGetValue(call.Name, out int existing))
        {
            return existing;
        }

        // Without a catalogue the arity is right and the types are a guess, which is what
        // an import table is for: the machine reads the name, and the types are there for
        // anything that wants to check the call rather than make it.
        IReadOnlyList<sbyte> arguments = known.Name is not null
            ? known.ArgumentTypes
            : [.. Enumerable.Repeat(SheepSignatures.Int, call.Arguments.Count)];

        _importIndex[call.Name] = _imports.Count;
        _imports.Add(new SheepImport(
            known.Name ?? call.Name, known.Name is not null ? known.ReturnType : returns, arguments));

        return _imports.Count - 1;
    }

    /// <summary>Finds or adds a string constant, and gives its offset in the pool.</summary>
    /// <remarks>
    /// The offset is what the bytecode carries, not an index, because that is what
    /// <c>GetString</c> looks up. Entries are NUL-terminated, so each one costs its own
    /// length and one more byte.
    /// </remarks>
    private int Intern(string text)
    {
        if (_stringOffset.TryGetValue(text, out int known))
        {
            return known;
        }

        int offset = _stringBytes;

        _stringOffset[text] = offset;
        _strings.Add(text);
        _stringBytes += System.Text.Encoding.Latin1.GetByteCount(text) + 1;

        return offset;
    }

    private int OffsetOf(int index)
    {
        int offset = 0;

        for (int i = 0; i < index; i++)
        {
            offset += System.Text.Encoding.Latin1.GetByteCount(_strings[i]) + 1;
        }

        return offset;
    }

    private void Emit(SheepOpcode opcode) => _code.Add((byte)opcode);

    private void Operand(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _code.AddRange(bytes);
    }

    private void OperandFloat(float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _code.AddRange(bytes);
    }

    private void Patch(int at, int target) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_code)[at..], target);

    private FormatParseException Malformed(
        int line, string expected, string actual, string remediation) =>
        new(new Diagnostic(
            "GK3R1082",
            DiagnosticSeverity.Error,
            $"Sheep source does not compile, at line {line.ToString(CultureInfo.InvariantCulture)}.",
            _script.Name,
            null,
            expected,
            actual,
            remediation));
}

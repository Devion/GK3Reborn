using System.Globalization;
using GK3Reborn.Formats;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Sheep;

/// <summary>
/// Reads Sheep source into a syntax tree.
/// </summary>
/// <remarks>
/// <para>
/// Recursive descent, following the BNF in the original team's <c>SHEEP ENGINE.DOC</c>.
/// <c>Plan/01-architecture.md</c> section 6 chose to hand-write this rather than port
/// G-Engine's flex/bison output, and the specification is what makes that cheap: the
/// grammar did not have to be recovered from generated code.
/// </para>
/// <para>
/// A script is an optional <c>symbols { }</c> block of typed declarations and an optional
/// <c>code { }</c> block of functions. Both are optional and either may come first in
/// principle; the content always writes symbols first.
/// </para>
/// <para>
/// Precedence follows C, which the language reference says it was modelled on:
/// <c>||</c> lowest, then <c>&amp;&amp;</c>, equality, relational, additive,
/// multiplicative, unary. <see cref="SheepExpression"/> parses the same production for
/// action-file conditions, which are expressions written as text rather than compiled;
/// the two agree deliberately, and this one builds a tree where that one evaluates as it
/// goes.
/// </para>
/// </remarks>
public sealed class SheepParser
{
    private readonly IReadOnlyList<SheepToken> _tokens;
    private readonly string _name;
    private int _at;

    private SheepParser(IReadOnlyList<SheepToken> tokens, string name)
    {
        _tokens = tokens;
        _name = name;
    }

    /// <summary>Parses a script.</summary>
    /// <param name="text">The source.</param>
    /// <param name="name">Name used in diagnostics and carried onto the result.</param>
    /// <returns>The syntax tree.</returns>
    /// <exception cref="FormatParseException">The source is not a valid script.</exception>
    public static SheepScriptNode Parse(string text, string name = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);

        return new SheepParser(SheepLexer.Scan(text, name), name).Script();
    }

    private SheepScriptNode Script()
    {
        List<SheepSymbolNode> symbols = [];
        List<SheepFunctionNode> functions = [];

        while (Current.Kind != SheepTokenKind.End)
        {
            if (Current.Word("symbols"))
            {
                Take();
                Expect("{");

                while (!Current.Is("}"))
                {
                    symbols.Add(Symbol());
                }

                Expect("}");
                continue;
            }

            if (Current.Word("code"))
            {
                Take();
                Expect("{");

                while (!Current.Is("}"))
                {
                    functions.Add(Function());
                }

                Expect("}");
                continue;
            }

            throw Malformed("symbols or code", Current.ToString());
        }

        return new SheepScriptNode(_name, symbols, functions);
    }

    /// <summary>Reads one declaration: a type, a name, and an optional initial value.</summary>
    private SheepSymbolNode Symbol()
    {
        int line = Current.Line;

        SheepValueKind kind =
            Current.Word("int") ? SheepValueKind.Int :
            Current.Word("float") ? SheepValueKind.Float :
            Current.Word("string") ? SheepValueKind.String :
            throw Malformed("int, float or string", Current.ToString());

        Take();

        SheepToken name = Current;

        if (name.Kind != SheepTokenKind.Identifier)
        {
            throw Malformed("a variable name", name.ToString());
        }

        Take();

        SheepExpressionNode? initial = null;

        if (Current.Is("="))
        {
            Take();
            initial = Expression();
        }

        Expect(";");
        return new SheepSymbolNode(name.Text, kind, initial, line);
    }

    /// <summary>Reads one function: a user name, empty parentheses, and a body.</summary>
    /// <remarks>
    /// Sheep functions take no arguments. The parentheses are written all the same, which
    /// is what makes a definition look like a definition rather than a label.
    /// </remarks>
    private SheepFunctionNode Function()
    {
        SheepToken name = Current;

        if (!name.IsUserName)
        {
            throw Malformed("a function name ending in $", name.ToString());
        }

        Take();
        Expect("(");
        Expect(")");
        Expect("{");

        List<SheepStatementNode> body = [];

        while (!Current.Is("}"))
        {
            if (Current.Kind == SheepTokenKind.End)
            {
                throw Malformed("a closing }", "the end of the script");
            }

            body.Add(Statement());
        }

        Expect("}");
        return new SheepFunctionNode(name.Text, body, name.Line);
    }

    private SheepStatementNode Statement()
    {
        int line = Current.Line;

        if (Current.Is("{"))
        {
            Take();
            List<SheepStatementNode> inner = [];

            while (!Current.Is("}"))
            {
                if (Current.Kind == SheepTokenKind.End)
                {
                    throw Malformed("a closing }", "the end of the script");
                }

                inner.Add(Statement());
            }

            Expect("}");
            return new SheepBlockNode(inner) { Line = line };
        }

        if (Current.Is(";"))
        {
            Take();
            return new SheepBlockNode([]) { Line = line };
        }

        if (Current.Word("if"))
        {
            return If(line);
        }

        if (Current.Word("return"))
        {
            Take();
            Optional(";");
            return new SheepReturnNode { Line = line };
        }

        if (Current.Word("goto"))
        {
            Take();
            SheepToken label = Current;

            if (label.Kind != SheepTokenKind.Identifier)
            {
                throw Malformed("a label name", label.ToString());
            }

            Take();
            Optional(";");
            return new SheepGotoNode(label.Text) { Line = line };
        }

        if (Current.Word("sitnspin"))
        {
            Take();
            Optional(";");
            return new SheepSitnSpinNode { Line = line };
        }

        if (Current.Word("breakpoint"))
        {
            Take();
            Optional(";");
            return new SheepBreakpointNode { Line = line };
        }

        if (Current.Word("wait"))
        {
            return Wait(line);
        }

        // A label is a user name followed by a colon, and a call is a name followed by an
        // open bracket. One token of lookahead tells them apart, which is why this is not
        // in the keyword list above.
        if (Current.Kind == SheepTokenKind.Identifier && Peek(1).Is(":"))
        {
            SheepToken label = Take();
            Expect(":");
            return new SheepLabelNode(label.Text) { Line = line };
        }

        if (Current.Kind == SheepTokenKind.Identifier && Peek(1).Is("="))
        {
            SheepToken target = Take();
            Expect("=");
            SheepExpressionNode value = Expression();
            Optional(";");
            return new SheepAssignmentNode(target.Text, value) { Line = line };
        }

        SheepExpressionNode expression = Expression();
        Optional(";");
        return new SheepExpressionStatementNode(expression) { Line = line };
    }

    private SheepIfNode If(int line)
    {
        Take();
        Expect("(");
        SheepExpressionNode condition = Expression();
        Expect(")");

        SheepStatementNode then = Statement();
        SheepStatementNode? otherwise = null;

        if (Current.Word("else"))
        {
            Take();
            otherwise = Statement();
        }

        return new SheepIfNode(condition, then, otherwise) { Line = line };
    }

    /// <summary>
    /// Reads a wait in each of its three forms.
    /// </summary>
    /// <remarks>
    /// <c>wait;</c> on its own, <c>wait Call();</c> applied to one call, and
    /// <c>wait { … }</c> applied to a group. The last is what makes several calls one
    /// wait: the block is over when the slowest of them is.
    /// </remarks>
    private SheepWaitNode Wait(int line)
    {
        Take();

        if (Current.Is(";"))
        {
            Take();
            return new SheepWaitNode(null) { Line = line };
        }

        return new SheepWaitNode(Statement()) { Line = line };
    }

    private SheepExpressionNode Expression() => Or();

    private SheepExpressionNode Or() => Binary(And, "||");

    private SheepExpressionNode And() => Binary(Equality, "&&");

    private SheepExpressionNode Equality() => Binary(Relational, "==", "!=", "<>");

    private SheepExpressionNode Relational() => Binary(Additive, "<=", ">=", "<", ">");

    private SheepExpressionNode Additive() => Binary(Multiplicative, "+", "-");

    private SheepExpressionNode Multiplicative() => Binary(Unary, "*", "/", "%");

    private SheepExpressionNode Binary(
        Func<SheepExpressionNode> next, params string[] operators)
    {
        SheepExpressionNode left = next();

        while (true)
        {
            string? matched = null;

            foreach (string op in operators)
            {
                if (Current.Is(op))
                {
                    matched = op;
                    break;
                }
            }

            if (matched is null)
            {
                return left;
            }

            Take();
            left = new SheepBinaryNode(matched, left, next());
        }
    }

    private SheepExpressionNode Unary()
    {
        if (Current.Is("-") || Current.Is("!"))
        {
            string op = Take().Text;
            return new SheepUnaryNode(op, Unary());
        }

        return Primary();
    }

    private SheepExpressionNode Primary()
    {
        SheepToken token = Current;

        switch (token.Kind)
        {
            case SheepTokenKind.Integer:
                Take();
                return new SheepIntegerNode(
                    int.Parse(token.Text, CultureInfo.InvariantCulture));

            case SheepTokenKind.Float:
                Take();
                return new SheepFloatNode(
                    float.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture));

            case SheepTokenKind.String:
                Take();
                return new SheepStringNode(token.Text);

            case SheepTokenKind.Identifier:
                Take();

                if (!Current.Is("("))
                {
                    return new SheepVariableNode(token.Text);
                }

                Take();
                List<SheepExpressionNode> arguments = [];

                while (!Current.Is(")"))
                {
                    arguments.Add(Expression());

                    if (!Current.Is(","))
                    {
                        break;
                    }

                    Take();
                }

                Expect(")");
                return new SheepCallNode(token.Text, arguments);

            default:
                if (token.Is("("))
                {
                    Take();
                    SheepExpressionNode inner = Expression();
                    Expect(")");
                    return inner;
                }

                throw Malformed("a value, a name or an open bracket", token.ToString());
        }
    }

    private SheepToken Current => _at < _tokens.Count
        ? _tokens[_at]
        : _tokens[^1];

    private SheepToken Peek(int ahead) => _at + ahead < _tokens.Count
        ? _tokens[_at + ahead]
        : _tokens[^1];

    private SheepToken Take()
    {
        SheepToken token = Current;

        if (_at < _tokens.Count - 1)
        {
            _at++;
        }

        return token;
    }

    private void Expect(string symbol)
    {
        if (!Current.Is(symbol))
        {
            throw Malformed($"'{symbol}'", Current.ToString());
        }

        Take();
    }

    private void Optional(string symbol)
    {
        if (Current.Is(symbol))
        {
            Take();
        }
    }

    private FormatParseException Malformed(string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1081",
            DiagnosticSeverity.Error,
            $"Sheep source is not valid at line {Current.Line}.",
            _name,
            Current.Offset,
            expected,
            actual,
            "Check the statement at that line against the language reference."));
}

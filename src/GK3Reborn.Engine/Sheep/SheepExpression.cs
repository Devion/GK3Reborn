using System.Globalization;
using GK3Reborn.Formats;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Sheep;

/// <summary>
/// Told about a call an expression made.
/// </summary>
/// <param name="name">The function called.</param>
/// <param name="arguments">What it resolved to.</param>
public delegate void SheepCallObserver(string name, IReadOnlyList<SheepValue> arguments);

/// <summary>
/// Evaluates Sheep expressions written as text.
/// </summary>
/// <remarks>
/// <para>
/// Action files define their conditions as expressions rather than compiled bytecode —
/// <c>RETURNED_COAT={ DoesEgoHaveInvItem("MOPED_KEYS") || GetGameVariableInt("…") }</c> —
/// so evaluating them needs a reader for the source language, not the VM.
/// </para>
/// <para>
/// This is a hand-written recursive-descent parser over the expression production of the
/// grammar in <c>SHEEP ENGINE.DOC</c>, which is the same approach
/// <c>Plan/01-architecture.md</c> section 6 chose for the full compiler. Building it here
/// first means the harder job starts from something already proven against real content.
/// </para>
/// <para>
/// Precedence follows C, which the language reference explicitly says it was modelled on:
/// <c>||</c> lowest, then <c>&amp;&amp;</c>, equality, relational, additive,
/// multiplicative, then unary.
/// </para>
/// </remarks>
public sealed class SheepExpression
{
    private readonly string _text;
    private readonly ISheepApi _api;
    private readonly IReadOnlyDictionary<string, SheepValue>? _variables;
    private int _position;

    private SheepExpression(
        string text,
        ISheepApi api,
        IReadOnlyDictionary<string, SheepValue>? variables,
        SheepCallObserver? observer)
    {
        _text = text;
        _api = api;
        _variables = variables;
        _observer = observer;
    }

    private readonly SheepCallObserver? _observer;

    /// <summary>Evaluates an expression.</summary>
    /// <param name="text">The expression source.</param>
    /// <param name="api">Host used to resolve function calls.</param>
    /// <param name="variables">
    /// Values bound to bare names. Action conditions use <c>n$</c> and <c>v$</c> for the
    /// noun and verb being evaluated, which is what lets one condition serve many rules.
    /// </param>
    /// <param name="observer">
    /// Told about each call as it is made, with the arguments it resolved to. For a caller
    /// that wants to know something about a call the return value does not say — how long
    /// it takes, most of all, since an expression evaluated on its own has no wait block to
    /// record that in.
    /// </param>
    /// <returns>The resulting value.</returns>
    /// <exception cref="FormatParseException">The expression is malformed.</exception>
    public static SheepValue Evaluate(
        string text,
        ISheepApi api,
        IReadOnlyDictionary<string, SheepValue>? variables = null,
        SheepCallObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(api);

        var parser = new SheepExpression(text, api, variables, observer);
        SheepValue result = parser.ParseOr();
        parser.SkipWhitespace();

        if (parser._position < text.Length)
        {
            throw parser.Malformed($"trailing text at {parser._position}");
        }

        return result;
    }

    /// <summary>Evaluates an expression as a condition.</summary>
    /// <param name="text">The expression source.</param>
    /// <param name="api">Host used to resolve function calls.</param>
    /// <returns>True when the expression is non-zero.</returns>
    /// <param name="variables">Values bound to bare names.</param>
    public static bool IsTrue(
        string text, ISheepApi api, IReadOnlyDictionary<string, SheepValue>? variables = null) =>
        Evaluate(text, api, variables).AsInt() != 0;

    private SheepValue ParseOr()
    {
        SheepValue left = ParseAnd();

        while (Match("||"))
        {
            // Both sides are evaluated. Sheep conditions call into game state, and
            // short-circuiting would make which calls happen depend on data, which the
            // differential harness would see as a divergence.
            SheepValue right = ParseAnd();
            left = SheepValue.FromInt(left.AsInt() != 0 || right.AsInt() != 0 ? 1 : 0);
        }

        return left;
    }

    private SheepValue ParseAnd()
    {
        SheepValue left = ParseEquality();

        while (Match("&&"))
        {
            SheepValue right = ParseEquality();
            left = SheepValue.FromInt(left.AsInt() != 0 && right.AsInt() != 0 ? 1 : 0);
        }

        return left;
    }

    private SheepValue ParseEquality()
    {
        SheepValue left = ParseRelational();

        while (true)
        {
            // "<>" is the language's second spelling of "not equal".
            if (Match("=="))
            {
                left = Compare(left, ParseRelational(), (a, b) => a == b);
            }
            else if (Match("!=") || Match("<>"))
            {
                left = Compare(left, ParseRelational(), (a, b) => a != b);
            }
            else
            {
                return left;
            }
        }
    }

    private SheepValue ParseRelational()
    {
        SheepValue left = ParseAdditive();

        while (true)
        {
            if (Match("<="))
            {
                left = Compare(left, ParseAdditive(), (a, b) => a <= b);
            }
            else if (Match(">="))
            {
                left = Compare(left, ParseAdditive(), (a, b) => a >= b);
            }
            else if (Match("<"))
            {
                left = Compare(left, ParseAdditive(), (a, b) => a < b);
            }
            else if (Match(">"))
            {
                left = Compare(left, ParseAdditive(), (a, b) => a > b);
            }
            else
            {
                return left;
            }
        }
    }

    private SheepValue ParseAdditive()
    {
        SheepValue left = ParseMultiplicative();

        while (true)
        {
            if (Match("+"))
            {
                left = Arithmetic(left, ParseMultiplicative(), (a, b) => a + b);
            }
            else if (Match("-"))
            {
                left = Arithmetic(left, ParseMultiplicative(), (a, b) => a - b);
            }
            else
            {
                return left;
            }
        }
    }

    private SheepValue ParseMultiplicative()
    {
        SheepValue left = ParseUnary();

        while (true)
        {
            if (Match("*"))
            {
                left = Arithmetic(left, ParseUnary(), (a, b) => a * b);
            }
            else if (Match("/"))
            {
                left = Arithmetic(left, ParseUnary(), (a, b) => b == 0 ? 0 : a / b);
            }
            else if (Match("%"))
            {
                SheepValue right = ParseUnary();
                left = SheepValue.FromInt(right.AsInt() == 0 ? 0 : left.AsInt() % right.AsInt());
            }
            else
            {
                return left;
            }
        }
    }

    private SheepValue ParseUnary()
    {
        if (Match("!"))
        {
            return SheepValue.FromInt(ParseUnary().AsInt() == 0 ? 1 : 0);
        }

        if (Match("-"))
        {
            SheepValue value = ParseUnary();
            return value.Kind == SheepValueKind.Float
                ? SheepValue.FromFloat(-value.AsFloat())
                : SheepValue.FromInt(-value.AsInt());
        }

        return ParsePrimary();
    }

    private SheepValue ParsePrimary()
    {
        SkipWhitespace();

        if (_position >= _text.Length)
        {
            throw Malformed("expression ended early");
        }

        if (Match("("))
        {
            SheepValue value = ParseOr();
            if (!Match(")"))
            {
                throw Malformed("missing closing parenthesis");
            }

            return value;
        }

        char c = _text[_position];

        if (c == '"')
        {
            return SheepValue.FromString(ParseString());
        }

        if (char.IsDigit(c) || (c == '.' && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1])))
        {
            return ParseNumber();
        }

        if (char.IsLetter(c) || c == '_')
        {
            return ParseIdentifier();
        }

        throw Malformed($"unexpected character '{c}' at {_position}");
    }

    private SheepValue ParseIdentifier()
    {
        int start = _position;
        while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or '$'))
        {
            _position++;
        }

        string name = _text[start.._position];
        SkipWhitespace();

        if (!Match("("))
        {
            // A bare name is a variable. Action conditions use n$ and v$ for the noun and
            // verb under evaluation, so one condition can serve many rules.
            if (_variables is not null && _variables.TryGetValue(name, out SheepValue bound))
            {
                return bound;
            }

            throw Malformed($"'{name}' is neither a call nor a bound variable");
        }

        List<SheepValue> arguments = [];
        SkipWhitespace();

        if (!Match(")"))
        {
            do
            {
                arguments.Add(ParseOr());
                SkipWhitespace();
            }
            while (Match(","));

            if (!Match(")"))
            {
                throw Malformed($"missing closing parenthesis in call to '{name}'");
            }
        }

        _observer?.Invoke(name, arguments);

        return _api.Invoke(name, arguments);
    }

    private SheepValue ParseNumber()
    {
        int start = _position;
        bool isFloat = false;

        while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
        {
            isFloat |= _text[_position] == '.';
            _position++;
        }

        string text = _text[start.._position];

        return isFloat
            ? SheepValue.FromFloat(float.Parse(text, CultureInfo.InvariantCulture))
            : SheepValue.FromInt(int.Parse(text, CultureInfo.InvariantCulture));
    }

    private string ParseString()
    {
        _position++; // opening quote
        int start = _position;

        while (_position < _text.Length && _text[_position] != '"')
        {
            _position++;
        }

        if (_position >= _text.Length)
        {
            throw Malformed("unterminated string");
        }

        string value = _text[start.._position];
        _position++; // closing quote
        return value;
    }

    private static SheepValue Compare(SheepValue left, SheepValue right, Func<float, float, bool> compare)
    {
        // Strings compare case-insensitively, matching the language's identifier rules.
        // Ordering them is expressed as comparing 0 against the comparison result, so
        // "==" and "!=" behave and an ordering operator on strings degenerates to
        // equality rather than to something arbitrary.
        if (left.Kind == SheepValueKind.String || right.Kind == SheepValueKind.String)
        {
            int difference = string.Compare(
                left.AsString(), right.AsString(), StringComparison.OrdinalIgnoreCase);
            return SheepValue.FromInt(compare(difference, 0) ? 1 : 0);
        }

        return SheepValue.FromInt(compare(left.AsFloat(), right.AsFloat()) ? 1 : 0);
    }

    private static SheepValue Arithmetic(SheepValue left, SheepValue right, Func<float, float, float> operation)
    {
        if (left.Kind == SheepValueKind.Float || right.Kind == SheepValueKind.Float)
        {
            return SheepValue.FromFloat(operation(left.AsFloat(), right.AsFloat()));
        }

        return SheepValue.FromInt((int)operation(left.AsInt(), right.AsInt()));
    }

    private bool Match(string token)
    {
        SkipWhitespace();

        if (_position + token.Length > _text.Length ||
            !_text.AsSpan(_position, token.Length).SequenceEqual(token))
        {
            return false;
        }

        // "<" must not swallow the "<" of "<=", and "!" must not swallow "!=".
        if (token is "<" or ">" or "!" or "=" &&
            _position + token.Length < _text.Length &&
            _text[_position + token.Length] is '=' or '>')
        {
            return false;
        }

        _position += token.Length;
        return true;
    }

    private void SkipWhitespace()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }

    private FormatParseException Malformed(string reason) =>
        new(new Diagnostic(
            "GK3R3300",
            DiagnosticSeverity.Error,
            $"Could not evaluate condition: {reason}.",
            null,
            _position,
            "a well-formed Sheep expression",
            _text.Length > 120 ? _text[..120] + "…" : _text,
            "Check the condition in the action file it came from."));
}

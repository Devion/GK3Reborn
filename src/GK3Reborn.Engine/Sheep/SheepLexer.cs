using System.Globalization;
using System.Text;
using GK3Reborn.Formats;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Sheep;

/// <summary>What a token is.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Member names mirror the Sheep language's own type names.")]
public enum SheepTokenKind
{
    /// <summary>A name: a keyword, a variable, or a function.</summary>
    Identifier,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number with a fractional part.</summary>
    Float,

    /// <summary>A quoted string.</summary>
    String,

    /// <summary>Punctuation or an operator.</summary>
    Symbol,

    /// <summary>The end of the source.</summary>
    End,
}

/// <summary>One token of Sheep source.</summary>
/// <param name="Kind">What sort of token it is.</param>
/// <param name="Text">
/// Its text. For an identifier this is as written, which matters for a diagnostic and not
/// for matching — Sheep is case-insensitive. For a string it is the contents without the
/// quotes.
/// </param>
/// <param name="Line">Which line it started on, counting from one.</param>
/// <param name="Offset">Where it started in the source, counting from zero.</param>
public readonly record struct SheepToken(
    SheepTokenKind Kind, string Text, int Line, int Offset)
{
    /// <summary>Whether this is a particular piece of punctuation.</summary>
    /// <param name="symbol">The punctuation.</param>
    /// <returns>True when it matches.</returns>
    public bool Is(string symbol) =>
        Kind == SheepTokenKind.Symbol && string.Equals(Text, symbol, StringComparison.Ordinal);

    /// <summary>Whether this is a particular keyword or name.</summary>
    /// <param name="word">The word, matched without regard to case.</param>
    /// <returns>True when it matches.</returns>
    public bool Word(string word) =>
        Kind == SheepTokenKind.Identifier &&
        string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is a name a script gave something of its own.</summary>
    public bool IsUserName =>
        Kind == SheepTokenKind.Identifier && Text.EndsWith('$');

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        SheepTokenKind.End => "the end of the script",
        SheepTokenKind.String => $"\"{Text}\"",
        _ => Text,
    };
}

/// <summary>
/// Turns Sheep source into tokens.
/// </summary>
public sealed class SheepLexer
{
    /// <summary>Punctuation, longest first, so that maximal munch falls out of the order.</summary>
    private static readonly string[] Symbols =
    [
        "||", "&&", "<=", ">=", "!=", "<>", "==",
        "+", "-", "*", "/", "%", "<", ">", "!", "=",
        "(", ")", "{", "}", ",", ";", ":",
    ];

    private readonly string _text;
    private readonly string _name;
    private int _position;
    private int _line = 1;

    private SheepLexer(string text, string name)
    {
        _text = text;
        _name = name;
    }

    /// <summary>Scans a whole script.</summary>
    /// <param name="text">The source.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>Its tokens, ending with one of kind <see cref="SheepTokenKind.End"/>.</returns>
    /// <exception cref="FormatParseException">The source contains something that is not a token.</exception>
    public static IReadOnlyList<SheepToken> Scan(string text, string name = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);

        var lexer = new SheepLexer(text, name);
        List<SheepToken> tokens = [];

        while (lexer.Next() is { } token)
        {
            tokens.Add(token);

            if (token.Kind == SheepTokenKind.End)
            {
                break;
            }
        }

        return tokens;
    }

    private SheepToken? Next()
    {
        SkipBlanksAndComments();

        if (_position >= _text.Length)
        {
            return new SheepToken(SheepTokenKind.End, string.Empty, _line, _position);
        }

        int start = _position;
        int line = _line;
        char c = _text[_position];

        if (IsLetter(c))
        {
            return Name(start, line);
        }

        if (char.IsAsciiDigit(c) || (c == '.' && _position + 1 < _text.Length &&
                                     char.IsAsciiDigit(_text[_position + 1])))
        {
            return Number(start, line);
        }

        if (c == '"')
        {
            return Quoted(start, line);
        }

        foreach (string symbol in Symbols)
        {
            if (_text.AsSpan(_position).StartsWith(symbol, StringComparison.Ordinal))
            {
                _position += symbol.Length;
                return new SheepToken(SheepTokenKind.Symbol, symbol, line, start);
            }
        }

        throw Malformed(start, "a token", $"'{c}'");
    }

    /// <summary>Reads a name, dollar and all.</summary>
    private SheepToken Name(int start, int line)
    {
        while (_position < _text.Length && (IsLetter(_text[_position]) ||
                                            char.IsAsciiDigit(_text[_position])))
        {
            _position++;
        }

        // The dollar ends a user identifier and belongs to it. There is no whitespace
        // allowed before it, which is why this is here rather than a separate token.
        if (_position < _text.Length && _text[_position] == '$')
        {
            _position++;
        }

        return new SheepToken(
            SheepTokenKind.Identifier, _text[start.._position], line, start);
    }

    /// <summary>
    /// Reads a number, and says whether it has a fractional part.
    /// </summary>
    private SheepToken Number(int start, int line)
    {
        bool fractional = false;

        while (_position < _text.Length &&
               (char.IsAsciiDigit(_text[_position]) || _text[_position] == '.'))
        {
            fractional |= _text[_position] == '.';
            _position++;
        }

        string text = _text[start.._position];

        if (fractional
            ? !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            : !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw Malformed(start, "a number", text);
        }

        return new SheepToken(
            fractional ? SheepTokenKind.Float : SheepTokenKind.Integer, text, line, start);
    }

    /// <summary>Reads a quoted string.</summary>
    private SheepToken Quoted(int start, int line)
    {
        _position++;
        var text = new StringBuilder();

        while (_position < _text.Length && _text[_position] != '"')
        {
            if (_text[_position] == '\n')
            {
                throw Malformed(start, "a closing quote", "the end of the line");
            }

            text.Append(_text[_position++]);
        }

        if (_position >= _text.Length)
        {
            throw Malformed(start, "a closing quote", "the end of the script");
        }

        _position++;
        return new SheepToken(SheepTokenKind.String, text.ToString(), line, start);
    }

    /// <summary>Skips whitespace and both comment forms, which do not nest.</summary>
    private void SkipBlanksAndComments()
    {
        while (_position < _text.Length)
        {
            char c = _text[_position];

            if (c == '\n')
            {
                _line++;
                _position++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                _position++;
                continue;
            }

            if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
            {
                while (_position < _text.Length && _text[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '*')
            {
                int start = _position;
                _position += 2;

                // Not nesting is the specification's own word for it, so the first close
                // ends the comment however many opens are inside it.
                while (_position + 1 < _text.Length &&
                       !(_text[_position] == '*' && _text[_position + 1] == '/'))
                {
                    if (_text[_position] == '\n')
                    {
                        _line++;
                    }

                    _position++;
                }

                if (_position + 1 >= _text.Length)
                {
                    throw Malformed(start, "a closing */", "the end of the script");
                }

                _position += 2;
                continue;
            }

            return;
        }
    }

    /// <summary>Whether a character may start or continue a name.</summary>
    private static bool IsLetter(char c) => char.IsAsciiLetter(c) || c == '_';

    private FormatParseException Malformed(int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1080",
            DiagnosticSeverity.Error,
            "Sheep source contains something that is not a token.",
            _name,
            offset,
            expected,
            actual,
            "Check the punctuation and the quoting around that point."));
}

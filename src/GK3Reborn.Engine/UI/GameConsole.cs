using System.Globalization;
using GK3Reborn.Sheep;

namespace GK3Reborn.UI;

/// <summary>
/// One line the console has printed.
/// </summary>
/// <param name="Text">What it says.</param>
/// <param name="Kind">Whether it is an echo, an answer or a complaint.</param>
public readonly record struct ConsoleLine(string Text, ConsoleLineKind Kind);

/// <summary>What a printed line is.</summary>
public enum ConsoleLineKind
{
    /// <summary>Something the console is telling the player.</summary>
    Notice,

    /// <summary>The command the player typed, echoed back.</summary>
    Echo,

    /// <summary>What the command answered.</summary>
    Result,

    /// <summary>Why it did not work.</summary>
    Complaint,
}

/// <summary>
/// A completion offered for what has been typed so far.
/// </summary>
/// <param name="Name">The function's name, in the casing the game uses.</param>
/// <param name="Signature">
/// Its prototype where the catalogue knows one, or just the name where it does not.
/// </param>
public readonly record struct Completion(string Name, string Signature);

/// <summary>
/// The developer console.
/// </summary>
/// <remarks>
/// <para>
/// The game's own scripting language is the command language, because it already is one:
/// everything the story can do is a Sheep call, the calls are named in the scripts the game
/// shipped with, and there are 139 signatures for them in the archives. Inventing a second
/// vocabulary on top would mean maintaining a translation between two sets of verbs that
/// mean the same things.
/// </para>
/// <para>
/// <b>Which is why the completion matters rather than being a nicety.</b> Nobody can be
/// expected to know that the way to see the easter-egg content is <c>SetFlag("EGG")</c>, or
/// to remember which of <c>SetLocation</c> and <c>SetEgoLocation</c> takes what. A list that
/// narrows as you type, showing each function's return type and arguments, is the
/// difference between a console that is usable without the source open beside it and one
/// that is not.
/// </para>
/// <para>
/// Calls are parsed here rather than compiled. A console line is one call with literal
/// arguments — there are no variables to resolve and no control flow to run — so a parser
/// of forty lines does the whole job, and the alternative would be standing up the
/// compiler, a script file and a thread to run one <c>SetFlag</c>.
/// </para>
/// <para>
/// Nothing here touches the clock, the renderer or the story directly. It is a buffer, a
/// history and a list of names; what a command does is whatever the host it is given does
/// with it.
/// </para>
/// </remarks>
public sealed class GameConsole
{
    /// <summary>How many printed lines are kept.</summary>
    private const int Scrollback = 200;

    /// <summary>How many completions are offered at once.</summary>
    /// <remarks>
    /// A list long enough to be worth reading and short enough to read. Typing one more
    /// character is a cheaper way to narrow it than scrolling is.
    /// </remarks>
    public const int Suggestions = 8;

    private readonly List<ConsoleLine> _lines = [];
    private readonly List<string> _history = [];
    private readonly List<Completion> _completions = [];
    private readonly List<string> _names = [];

    private string _typed = string.Empty;
    private int _recalled = -1;
    private int _chosen;

    /// <summary>Whether the console is showing.</summary>
    public bool Open { get; private set; }

    /// <summary>What has been typed so far.</summary>
    public string Typed => _typed;

    /// <summary>What the console has printed, oldest first.</summary>
    public IReadOnlyList<ConsoleLine> Lines => _lines;

    /// <summary>What the typed prefix could become, best first.</summary>
    public IReadOnlyList<Completion> Completions => _completions;

    /// <summary>Which completion is chosen, as an index into <see cref="Completions"/>.</summary>
    public int Chosen => _chosen;

    /// <summary>How many functions it knows the names of.</summary>
    public int Known => _names.Count;

    /// <summary>
    /// The signatures, when anything has read them out of the archives.
    /// </summary>
    /// <remarks>
    /// Optional. Without it the completion still offers names, which is most of the value;
    /// with it each one carries its return type and arguments, which is the rest.
    /// </remarks>
    public SheepSignatures? Catalogue { get; set; }

    /// <summary>
    /// What to do with a parsed call.
    /// </summary>
    /// <remarks>
    /// Given the function's name and its arguments; answers what it returned, or null when
    /// there is no such function. Set by whoever owns a game to run; left null by a test,
    /// which is what lets the buffer and the completion be exercised without a story.
    /// </remarks>
    public Func<string, IReadOnlyList<SheepValue>, SheepValue?>? Calls { get; set; }

    /// <summary>Tells the console which functions exist.</summary>
    /// <param name="names">Their names, in whatever order.</param>
    /// <remarks>
    /// From the host's own registry rather than from the archives, so the list is what this
    /// build can actually do rather than what the 1999 scripts called. The two differ, and
    /// offering a completion for something that would be recorded and not performed is
    /// worse than not offering it.
    /// </remarks>
    public void Knows(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        _names.Clear();
        _names.AddRange(names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        Complete();
    }

    /// <summary>Shows or hides the console.</summary>
    /// <param name="open">Whether it should be showing.</param>
    public void Show(bool open)
    {
        if (open == Open)
        {
            return;
        }

        Open = open;

        if (!open)
        {
            return;
        }

        if (_lines.Count == 0)
        {
            Print(
                $"{_names.Count} functions. Tab completes, up and down recall, Escape closes.",
                ConsoleLineKind.Notice);

            Print(
                // Written in ASCII on purpose. GK3's own bitmap fonts are what draws
                // this, and they have no glyph for an em dash: one comes out as a box.
                "Try SetFlag(\"EGG\") - the game's own easter-egg switch, which every " +
                "action file tests and nothing ever set.",
                ConsoleLineKind.Notice);
        }

        Complete();
    }

    /// <summary>Adds typed characters.</summary>
    /// <param name="text">What was typed since the last frame.</param>
    public void Type(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (char c in text)
        {
            // Printable only. The editing keys arrive through their own calls, and a
            // control character in the buffer is a glyph the font has no answer for.
            if (c >= ' ' && c != (char)127)
            {
                _typed += c;
            }
        }

        _recalled = -1;
        Complete();
    }

    /// <summary>Deletes the character before the caret.</summary>
    public void Backspace()
    {
        if (_typed.Length > 0)
        {
            _typed = _typed[..^1];
            _recalled = -1;
            Complete();
        }
    }

    /// <summary>Takes the chosen completion.</summary>
    /// <remarks>
    /// Completing writes the open bracket as well, because a function is being called
    /// rather than named, and the caret ends up where the arguments go.
    /// </remarks>
    public void TakeCompletion()
    {
        if (_completions.Count == 0)
        {
            return;
        }

        Completion taken = _completions[Math.Clamp(_chosen, 0, _completions.Count - 1)];

        _typed = taken.Name + "(";
        _recalled = -1;
        Complete();
    }

    /// <summary>Moves the choice through the completions, or through the history.</summary>
    /// <param name="delta">Negative for up, positive for down.</param>
    /// <remarks>
    /// One key doing two things, decided by whether there is a list to move through: with
    /// completions showing, up and down move the choice; with none — which is what an empty
    /// line or a finished call looks like — they recall what was typed before. That is what
    /// every shell does, and it is why neither needs a key of its own.
    /// </remarks>
    public void Move(int delta)
    {
        if (_completions.Count > 0)
        {
            int count = _completions.Count;
            _chosen = ((_chosen + delta) % count + count) % count;
            return;
        }

        if (_history.Count == 0)
        {
            return;
        }

        if (_recalled < 0)
        {
            _recalled = delta < 0 ? _history.Count - 1 : 0;
        }
        else
        {
            _recalled = Math.Clamp(_recalled + delta, 0, _history.Count - 1);
        }

        _typed = _history[_recalled];
    }

    /// <summary>Runs whatever has been typed.</summary>
    /// <returns>True when there was something to run.</returns>
    public bool Submit()
    {
        string line = _typed.Trim();
        _typed = string.Empty;
        _recalled = -1;
        _chosen = 0;

        if (line.Length == 0)
        {
            Complete();
            return false;
        }

        if (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal))
        {
            _history.Add(line);
        }

        Print(line, ConsoleLineKind.Echo);
        Complete();

        if (!TryRead(line, out string name, out List<SheepValue> arguments, out string? complaint))
        {
            Print(complaint!, ConsoleLineKind.Complaint);
            return true;
        }

        if (Calls is null)
        {
            Print("There is no game running to ask.", ConsoleLineKind.Complaint);
            return true;
        }

        SheepValue? answer;

        try
        {
            answer = Calls(name, arguments);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException
                                       or FormatException or IndexOutOfRangeException)
        {
            // A console that closes the game when a command is wrong is a console nobody
            // will use twice. What a call throws is the call's business; saying so is this.
            Print($"{name} failed: {e.Message}", ConsoleLineKind.Complaint);
            return true;
        }

        if (answer is not { } value)
        {
            Print($"There is no function called {name}.", ConsoleLineKind.Complaint);
            return true;
        }

        Print($"{name} -> {Describe(value)}", ConsoleLineKind.Result);
        return true;
    }

    /// <summary>Prints a line.</summary>
    /// <param name="text">What it says.</param>
    /// <param name="kind">What kind of line it is.</param>
    public void Print(string text, ConsoleLineKind kind = ConsoleLineKind.Notice)
    {
        ArgumentNullException.ThrowIfNull(text);

        _lines.Add(new ConsoleLine(text, kind));

        if (_lines.Count > Scrollback)
        {
            _lines.RemoveRange(0, _lines.Count - Scrollback);
        }
    }

    /// <summary>Reads one call.</summary>
    /// <param name="line">What was typed.</param>
    /// <param name="name">Receives the function's name.</param>
    /// <param name="arguments">Receives the arguments.</param>
    /// <param name="complaint">Receives why it could not be read.</param>
    /// <returns>True when it was read.</returns>
    /// <remarks>
    /// <c>Name</c> and <c>Name(...)</c> are both accepted, because a function of no
    /// arguments is a thing the player wants to type without the brackets and there is
    /// nothing else a bare name could mean here.
    /// </remarks>
    private static bool TryRead(
        string line,
        out string name,
        out List<SheepValue> arguments,
        out string? complaint)
    {
        name = string.Empty;
        arguments = [];
        complaint = null;

        int open = line.IndexOf('(', StringComparison.Ordinal);

        if (open < 0)
        {
            name = line.TrimEnd(';').Trim();

            if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                complaint = $"\"{line}\" is not a call.";
                return false;
            }

            return true;
        }

        name = line[..open].Trim();

        int close = line.LastIndexOf(')');

        if (close < open)
        {
            complaint = "That call is missing its closing bracket.";
            return false;
        }

        string inside = line[(open + 1)..close].Trim();

        if (inside.Length == 0)
        {
            return true;
        }

        foreach (string piece in Split(inside))
        {
            string argument = piece.Trim();

            if (argument.Length == 0)
            {
                complaint = "That call has an empty argument.";
                return false;
            }

            if (argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"')
            {
                arguments.Add(SheepValue.FromString(argument[1..^1]));
                continue;
            }

            if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole))
            {
                arguments.Add(SheepValue.FromInt(whole));
                continue;
            }

            if (float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out float real))
            {
                arguments.Add(SheepValue.FromFloat(real));
                continue;
            }

            // A bare word. Sheep's own calls quote their strings, but a player typing
            // SetLocation(RC1) has said something unambiguous and being pedantic about it
            // would only teach them to type the quotes.
            arguments.Add(SheepValue.FromString(argument));
        }

        return true;
    }

    /// <summary>Splits an argument list on commas that are not inside a string.</summary>
    private static IEnumerable<string> Split(string inside)
    {
        int from = 0;
        bool quoted = false;

        for (int i = 0; i < inside.Length; i++)
        {
            if (inside[i] == '"')
            {
                quoted = !quoted;
            }
            else if (inside[i] == ',' && !quoted)
            {
                yield return inside[from..i];
                from = i + 1;
            }
        }

        yield return inside[from..];
    }

    /// <summary>Works out what the typed prefix could become.</summary>
    /// <remarks>
    /// Names that <em>start</em> with what was typed first, then names that merely contain
    /// it. The first is what somebody typing a name they know wants; the second is what
    /// somebody looking for a name they half remember wants, and putting them in one list
    /// in that order serves both without a mode.
    /// </remarks>
    private void Complete()
    {
        _completions.Clear();
        _chosen = 0;

        // Only while a name is being typed. Once the bracket is open the player is writing
        // arguments, and a list of other functions covering the screen is in the way.
        if (!Open || _typed.Contains('(', StringComparison.Ordinal))
        {
            return;
        }

        string prefix = _typed.Trim();

        if (prefix.Length == 0)
        {
            return;
        }

        foreach (string name in _names)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _completions.Add(new Completion(name, Signature(name)));
            }
        }

        foreach (string name in _names)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _completions.Add(new Completion(name, Signature(name)));
            }
        }

        if (_completions.Count > Suggestions)
        {
            _completions.RemoveRange(Suggestions, _completions.Count - Suggestions);
        }
    }

    /// <summary>How a function should be written, as far as anything knows.</summary>
    private string Signature(string name) =>
        Catalogue is { } catalogue && catalogue.TryGet(name, out SheepImport import)
            ? SheepSignatures.Describe(import)
            : name + "(...)";

    /// <summary>What a returned value should be printed as.</summary>
    private static string Describe(SheepValue value) => value.Kind switch
    {
        SheepValueKind.Float => value.AsFloat().ToString("0.###", CultureInfo.InvariantCulture),
        SheepValueKind.String => $"\"{value.AsString()}\"",
        _ => value.AsInt().ToString(CultureInfo.InvariantCulture),
    };
}

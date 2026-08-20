using GK3Reborn.Formats;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>One statement of an action's script.</summary>
/// <param name="Call">The function it calls.</param>
/// <param name="Waited">Whether the script waited on it.</param>
/// <param name="Seconds">
/// How long the script waits on it. Zero when it was not waited on, and also zero when it
/// was but the host cannot say how long it takes — the two are told apart by
/// <paramref name="Waited"/>, not by this.
/// </param>
public readonly record struct ActionStatement(string Call, bool Waited, double Seconds = 0);

/// <summary>What running an action did.</summary>
/// <param name="Noun">What it was done to.</param>
/// <param name="Verb">What was done.</param>
/// <param name="Case">The case that made it available.</param>
/// <param name="Statements">The calls its script made, in order.</param>
/// <param name="Ran">
/// Whether the script ran. A script with a statement this cannot read is refused whole,
/// the way a compiler refuses a file: half an action is worse than none, because the half
/// that ran has already changed the story.
/// </param>
public sealed record ActionOutcome(
    string Noun,
    string Verb,
    string Case,
    IReadOnlyList<ActionStatement> Statements,
    bool Ran)
{
    /// <summary>How long the action takes, in seconds.</summary>
    /// <remarks>
    /// The sum of its waits, because the statements run one after another and a waited one
    /// holds up the rest. An action of nothing but unwaited calls is instantaneous, which
    /// is right: those are the ones the script did not want to wait for.
    /// </remarks>
    public double Seconds => Statements.Sum(s => s.Seconds);
}

/// <summary>
/// Runs the script an action names.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ActionResolver"/> chooses; this performs. They are deliberately separate:
/// choosing happens whenever the cursor moves and must not touch the story, while
/// performing is what a click is for. <c>Plan/03</c> section 2.3 requires that modernising
/// input must not change what an action does, so the script executed here is the
/// original's own text, unchanged, evaluated through the same Sheep host everything else
/// goes through.
/// </para>
/// <para>
/// An action's script is a much smaller language than Sheep. Across the corpus's 5,872
/// scripts there are 6,842 statements, every one of them a function call, 5,824 of those
/// prefixed with <c>wait</c>, drawn from 55 distinct functions of which
/// <c>StartVoiceOver</c> alone is 4,314. There are no branches, no loops and no locals: the
/// files put that in the case conditions and in the scripts the actions call into. So this
/// reads statements rather than compiling a language, and refuses anything else out loud
/// instead of guessing at it.
/// </para>
/// <para>
/// <c>wait</c> is recorded and not obeyed, because there is nothing yet for a script to
/// wait on: calls run inline to completion, which produces the same observable order for
/// anything that does not depend on real elapsed time. Keeping it in the record is what
/// lets that stop being true later without the traces becoming incomparable.
/// </para>
/// </remarks>
public sealed class ActionRunner
{
    private readonly Gk3SheepApi _api;

    /// <summary>Creates a runner.</summary>
    /// <param name="api">
    /// The host to run through. Give it the one a <see cref="ScriptHost"/> has been
    /// attached to, or <c>CallSheep</c> — a fifth of every statement in the corpus — will
    /// go nowhere.
    /// </param>
    public ActionRunner(Gk3SheepApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>Diagnostics raised while running.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Reads an action's script without performing it.</summary>
    /// <param name="action">The action.</param>
    /// <returns>Its statements, or null when one of them cannot be read.</returns>
    /// <remarks>
    /// Separate from running so that a sweep can ask whether the corpus's scripts are
    /// within reach without a story to run them against. Nothing here touches the state.
    /// </remarks>
    public IReadOnlyList<ActionStatement>? Read(NvcAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return TryRead(action, out List<ActionStatement> statements, out _) ? statements : null;
    }

    /// <summary>Runs an action's script.</summary>
    /// <param name="action">The action, as the resolver chose it.</param>
    /// <returns>What it did.</returns>
    public ActionOutcome Run(NvcAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!TryRead(action, out List<ActionStatement> statements, out List<string> sources))
        {
            return new ActionOutcome(action.Noun, action.Verb, action.Case, statements, Ran: false);
        }

        Approach(action);

        for (int i = 0; i < sources.Count; i++)
        {
            try
            {
                // How long a waited call takes is not something its return value says, and
                // an action script is evaluated one statement at a time with no wait block
                // to accumulate it in, so it is collected as the call is made. The longest
                // call in a statement decides the statement, the way a wait block is over
                // when its slowest member is.
                double seconds = 0;

                SheepExpression.Evaluate(
                    sources[i],
                    _api,
                    null,
                    statements[i].Waited
                        ? (name, arguments) =>
                            seconds = Math.Max(seconds, _api.SecondsFor(name, arguments))
                        : null);

                statements[i] = statements[i] with { Seconds = seconds };
            }
            catch (FormatParseException ex)
            {
                // Half an action has already happened, so this is reported rather than
                // unwound: the story cannot be put back and pretending otherwise would
                // hide the fact that it moved.
                Diagnostics.Add(ex.Diagnostic);
                Diagnostics.Add(new Diagnostic(
                    "GK3R3303",
                    DiagnosticSeverity.Error,
                    $"{action.Noun}:{action.Verb} stopped after {i} of {sources.Count} " +
                    "statements; what ran before it has already taken effect.",
                    action.Source, null, "a statement the host can perform", sources[i],
                    "Check the script field against the surrounding rules."));

                return new ActionOutcome(
                    action.Noun, action.Verb, action.Case, statements[..i], Ran: false);
            }
        }

        Finish(action);

        return new ActionOutcome(action.Noun, action.Verb, action.Case, statements, Ran: true);
    }

    /// <summary>
    /// What the engine does once an action is over, whatever its script said.
    /// </summary>
    /// <remarks>
    /// Topics and chats count themselves; ordinary verbs do not. That asymmetry is the
    /// original's — a topic is used up by being raised, so the engine records it, while an
    /// ordinary action increments its own count only if its script says
    /// <c>IncNounVerbCount</c>, which 260 of the corpus's scripts do. Counting them all
    /// here would make every <c>1ST_TIME</c> rule fire once and never again.
    /// </remarks>
    private void Finish(NvcAction action)
    {
        if (action.Verb.StartsWith("T_", StringComparison.OrdinalIgnoreCase))
        {
            _api.State.SetTopicCount(
                action.Noun, action.Verb, _api.State.GetTopicCount(action.Noun, action.Verb) + 1);

            // Which line of the topic, not just how many times. A topic is written as
            // several lines under different conditions and each is said once, so the count
            // alone cannot say which is left: two conditions may both hold, and asking
            // again should give the one not yet heard.
            _api.State.Said(action.Noun, action.Verb, action.Case);
        }
        else if (action.Verb.Equals("Z_CHAT", StringComparison.OrdinalIgnoreCase))
        {
            _api.State.IncrementChatCount(action.Noun);
        }
    }

    /// <summary>Reads a script into statements, or explains why it cannot.</summary>
    /// <summary>
    /// Takes the player to the thing before doing anything to it.
    /// </summary>
    /// <param name="action">The action, whose <c>approach</c> says how.</param>
    /// <remarks>
    /// <para>
    /// An action file says <c>approach=WalkTo, target=TO_B25</c> beside the script, and it
    /// is not part of the script: it is what has to be true before the script runs. Nothing
    /// performed it, so Gabriel opened doors from across the room.
    /// </para>
    /// <para>
    /// The walk is started and the script runs without waiting for it. That is what the
    /// original does — the line of dialogue plays over the walk — and waiting here would
    /// mean the runner had a clock, which it deliberately does not.
    /// </para>
    /// <para>
    /// <c>TurnToModel</c> and <c>TurnTo</c> are approaches too and they are turns rather
    /// than walks, so they go through the same hook saying so. 394 of the corpus's 3,617.
    /// </para>
    /// </remarks>
    private void Approach(NvcAction action)
    {
        if (_api.Walks is null ||
            action.Approach is not { Length: > 0 } approach ||
            action.Target is not { Length: > 0 } target)
        {
            return;
        }

        switch (approach.ToUpperInvariant())
        {
            case "WALKTO":
            case "WALKTOANIMATION":
                _api.Walks(_api.State.Ego, target, Approaching.Walk);
                break;

            case "WALKTOSEE":
            case "WALKTOSEEMODEL":
            case "NEARMODEL":
                _api.Walks(_api.State.Ego, target, Approaching.WalkToSee);
                break;

            // A turn is not a walk. Walking to the thing instead puts the player on top of
            // whatever they meant to look at.
            case "TURNTOMODEL":
            case "TURNTO":
                _api.Walks(_api.State.Ego, target, Approaching.Turn);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Works out how long a waited statement takes without performing it.
    /// </summary>
    /// <param name="text">The call, as the script wrote it.</param>
    /// <returns>Seconds, or zero when the host cannot say.</returns>
    /// <remarks>
    /// <para>
    /// How long a call takes depends on its arguments — which line of dialogue, which
    /// animation — so the arguments have to be worked out, and working them out means
    /// evaluating the expression. Reading a script is supposed to change nothing, so the
    /// evaluation is done against a host that does nothing, and the call is caught on its
    /// way in rather than being allowed to happen.
    /// </para>
    /// <para>
    /// An argument that is itself a call — <c>StartVoiceOver(GetVar("x"), 1)</c> — comes
    /// out empty, because the inert host has nothing to return. There are none of those in
    /// the corpus; if there were, they would read as instantaneous rather than wrong.
    /// </para>
    /// </remarks>
    private double Measure(string text)
    {
        double seconds = 0;

        try
        {
            SheepExpression.Evaluate(
                text.Contains('(', StringComparison.Ordinal) ? text : text + "()",
                Inert.Instance,
                null,
                (name, arguments) => seconds = Math.Max(seconds, _api.SecondsFor(name, arguments)));
        }
        catch (FormatParseException)
        {
            // Whatever is wrong with it will be reported by the read itself.
            return 0;
        }

        return seconds;
    }

    /// <summary>A host that does nothing, for finding out what a script would call.</summary>
    private sealed class Inert : ISheepApi
    {
        public static Inert Instance { get; } = new();

        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments) =>
            SheepValue.FromInt(0);

        public bool IsWaitable(string name) => false;
    }

    private bool TryRead(
        NvcAction action, out List<ActionStatement> statements, out List<string> sources)
    {
        statements = [];
        sources = [];

        foreach (string statement in Split(action.Script ?? string.Empty))
        {
            string text = statement;
            bool waited = false;

            if (text.StartsWith("wait", StringComparison.OrdinalIgnoreCase) &&
                text.Length > 4 &&
                char.IsWhiteSpace(text[4]))
            {
                waited = true;
                text = text[4..].TrimStart();
            }

            if (NameOf(text) is not { } call)
            {
                Diagnostics.Add(new Diagnostic(
                    "GK3R3302",
                    DiagnosticSeverity.Warning,
                    $"{action.Noun}:{action.Verb} has a statement that is not a call; the " +
                    "action is not performed.",
                    action.Source, null, "a function call, optionally waited on", text,
                    "Every action script in the corpus is calls and nothing else."));

                return false;
            }

            statements.Add(new ActionStatement(call, waited, waited ? Measure(text) : 0));

            // A bare name is a call with no arguments — the language has statements like
            // Yield that take none — and the expression reader wants the parentheses.
            sources.Add(text.Contains('(', StringComparison.Ordinal) ? text : text + "()");
        }

        return true;
    }

    /// <summary>The function a statement calls, if it is a call at all.</summary>
    private static string? NameOf(string statement)
    {
        int end = 0;

        while (end < statement.Length &&
               (char.IsAsciiLetterOrDigit(statement[end]) || statement[end] is '_' or '$'))
        {
            end++;
        }

        if (end == 0 || char.IsAsciiDigit(statement[0]))
        {
            return null;
        }

        string rest = statement[end..].TrimStart();

        return rest.Length == 0 || rest.StartsWith('(') ? statement[..end] : null;
    }

    /// <summary>Splits a script into statements on the semicolons that separate them.</summary>
    /// <remarks>
    /// Not every semicolon: one inside a string is part of the string, and one inside
    /// parentheses belongs to an argument list. Splitting on all of them works for nearly
    /// every script in the corpus and fails silently on the rest, which is the worst way
    /// for it to be wrong.
    /// </remarks>
    private static IEnumerable<string> Split(string script)
    {
        int start = 0;
        int depth = 0;
        bool quoted = false;

        for (int i = 0; i < script.Length; i++)
        {
            char c = script[i];

            if (quoted)
            {
                quoted = c != '"';
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ';' when depth <= 0:
                    if (script[start..i].Trim() is { Length: > 0 } statement)
                    {
                        yield return statement;
                    }

                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        if (script[start..].Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }
}

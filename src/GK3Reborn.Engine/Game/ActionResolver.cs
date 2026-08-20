using GK3Reborn.Formats;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;
using GK3Reborn.UI.Interaction;

namespace GK3Reborn.Game;

/// <summary>
/// Decides what the player can do to something right now.
/// </summary>
/// <remarks>
/// <para>
/// This is the hinge between the original data and the modern interaction model. The
/// game's action files list every noun, verb and the condition under which that pairing
/// applies; the original engine used the same information to decide which verbs to put on
/// its verb wheel. Asking it for one noun's currently valid verbs is the same query,
/// answered for a different interface.
/// </para>
/// <para>
/// <c>Plan/03-gameplay-ui-audio.md</c> section 2.3 requires that modernising input must
/// not change what an action does, so this resolver only ever *selects*: the script it
/// returns is the original script, unchanged, and execution still goes through Sheep.
/// </para>
/// <para>
/// It also never mutates state. A resolver that evaluated a condition by trying the
/// action would corrupt the save just by hovering the cursor.
/// </para>
/// </remarks>
public sealed class ActionResolver
{
    private readonly List<NvcFile> _files = [];
    private readonly ISheepApi _api;

    /// <summary>Creates a resolver.</summary>
    /// <param name="api">Host used to evaluate case conditions.</param>
    public ActionResolver(ISheepApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>Which verbs are topics, and which of those recur.</summary>
    /// <remarks>
    /// Null treats every verb as an ordinary one, which offers every line of every topic at
    /// once and never uses any of them up. The launcher reads <c>VERBS.TXT</c> once and
    /// sets it.
    /// </remarks>
    public Actions.VerbLibrary? Verbs { get; set; }

    /// <summary>Diagnostics raised while resolving.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Adds an action file to the set in scope.</summary>
    /// <param name="file">The file.</param>
    /// <remarks>
    /// Several files are usually in scope at once — one for the location, one for the
    /// timeblock, one shared across a day — and their rules combine.
    /// </remarks>
    public void Add(NvcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _files.Add(file);
    }

    /// <summary>Every noun any loaded file mentions.</summary>
    public IReadOnlyCollection<string> Nouns =>
        [.. _files.SelectMany(f => f.Actions).Select(a => a.Noun).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Finds the actions currently valid for a noun.
    /// </summary>
    /// <param name="noun">The thing being looked at.</param>
    /// <param name="ego">Who the player currently is, for the ego-specific built-in cases.</param>
    /// <returns>Valid actions, inspect first, then in file order.</returns>
    public IReadOnlyList<AvailableAction> Resolve(string noun, string ego = "GABRIEL")
    {
        ArgumentNullException.ThrowIfNull(noun);

        List<AvailableAction> result = [];
        HashSet<string> seenVerbs = new(StringComparer.OrdinalIgnoreCase);

        foreach (NvcFile file in _files)
        {
            foreach (NvcAction action in file.Actions)
            {
                if (!string.Equals(action.Noun, noun, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsCaseSatisfied(file, action.Case, ego, action.Noun, action.Verb))
                {
                    continue;
                }

                // The first matching rule for a verb wins, which is how the original data
                // is layered: a timeblock file overrides the shared one.
                if (!seenVerbs.Add(action.Verb))
                {
                    continue;
                }

                result.Add(new AvailableAction
                {
                    ActionId = $"{action.Noun}:{action.Verb}",
                    NvcProvenance = action.Source,
                    LocalizedVerb = action.Verb,
                    IconSemantic = IconFor(action.Verb),
                    Category = CategoryFor(action.Verb),
                    Enabled = true,
                });
            }
        }

        // Inspect first, so left click always has something predictable to do.
        return [.. result
            .OrderBy(a => a.Category == ActionCategory.Inspect ? 0 : 1)
            .ThenBy(a => result.IndexOf(a))];
    }

    /// <summary>Finds the rule a verb on a noun would run.</summary>
    /// <param name="noun">The thing being acted on.</param>
    /// <param name="verb">What is being done to it.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <returns>The rule, or null when nothing applies.</returns>
    /// <remarks>
    /// The same selection <see cref="Resolve"/> makes, for one verb rather than all of
    /// them, and returning the rule itself rather than something to put in a menu — because
    /// what a click needs is the script. Selecting still changes nothing; performing is
    /// <see cref="ActionRunner"/>'s job.
    /// </remarks>
    public NvcAction? Find(string noun, string verb, string ego = "GABRIEL")
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(verb);

        foreach (NvcFile file in _files)
        {
            foreach (NvcAction action in file.Actions)
            {
                if (!string.Equals(action.Noun, noun, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(action.Verb, verb, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsCaseSatisfied(file, action.Case, ego, action.Noun, action.Verb))
                {
                    return action;
                }
            }
        }

        return null;
    }

    /// <summary>Evaluates whether a named case currently holds.</summary>
    /// <param name="file">File the case belongs to.</param>
    /// <param name="caseName">Case name.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <returns>True when the case applies.</returns>
    /// <param name="noun">Noun under evaluation, bound to <c>n$</c>.</param>
    /// <param name="verb">Verb under evaluation, bound to <c>v$</c>.</param>
    public bool IsCaseSatisfied(
        NvcFile file, string caseName, string ego, string noun = "", string verb = "")
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(caseName);

        bool topic = Verbs?.IsTopic(verb) ?? false;

        // A topic is said once. Its case says when a line becomes available and never says
        // when it stops being — the original keeps the lines it has played and refuses them
        // however the case reads. Without that, a conversation offers the same line for
        // ever. The one verb in the game declared recurring is exempt.
        if (topic && !(Verbs?.IsRecurring(verb) ?? false) && HasSaid(noun, verb, caseName))
        {
            return false;
        }

        if (NvcFile.BuiltInCases.Contains(caseName))
        {
            return caseName.ToUpperInvariant() switch
            {
                // On a topic, ALL is not "always". It is the last thing there is to say
                // about it, available only once every other line has been used. Read as
                // "always", a topic's closing line is offered from the very start and can
                // be repeated for ever — which is how a conversation comes to show
                // something the player has not got to yet.
                "ALL" or "DEFAULT" when topic => IsLastWord(noun, verb),
                "GABE_ALL" when topic => IsGabriel(ego) && IsLastWord(noun, verb),
                "GRACE_ALL" when topic => !IsGabriel(ego) && IsLastWord(noun, verb),

                "ALL" or "DEFAULT" => true,
                "GABE_ALL" => IsGabriel(ego),
                "GRACE_ALL" => !IsGabriel(ego),
                "NOT_GABE_ALL" => !IsGabriel(ego),
                "NOT_GRACE_ALL" => IsGabriel(ego),

                // An action a timeblock's file writes over one the location's general file
                // gives. Always available; the OVERRIDE form outranks the plain one where
                // both could apply, which matters only once anything ranks them.
                "TIME_BLOCK" or "TIME_BLOCK_OVERRIDE" => true,

                // How often the player has already done this to this. The counts live in
                // the story's state and are reached through the same host the conditions
                // are, so a resolver never needs to know what kind of game it is in.
                "1ST_TIME" => Done(noun, verb) == 0,
                "2CD_TIME" or "2ND_TIME" => Done(noun, verb) == 1,
                "3RD_TIME" => Done(noun, verb) == 2,
                "OTR_TIME" => Done(noun, verb) > 0,

                "DIALOGUE_TOPICS_LEFT" => HasTopicsLeft(noun, ego),
                "NOT_DIALOGUE_TOPICS_LEFT" => !HasTopicsLeft(noun, ego),

                // Easter eggs are off. The original has the same placeholder.
                "EGG" => false,

                _ => true,
            };
        }

        if (!file.Cases.TryGetValue(caseName, out string? expression))
        {
            // A case defined in another file in scope is common, so look wider before
            // giving up.
            foreach (NvcFile other in _files)
            {
                if (other.Cases.TryGetValue(caseName, out expression))
                {
                    break;
                }
            }
        }

        if (expression is null)
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3301", DiagnosticSeverity.Warning,
                $"Case '{caseName}' is not defined in any loaded action file.",
                file.Name, null, "a case in a logic section or a built-in", caseName,
                "The action is treated as unavailable. Another file may define it."));
            return false;
        }

        try
        {
            Dictionary<string, SheepValue> variables = new(StringComparer.OrdinalIgnoreCase)
            {
                ["n$"] = SheepValue.FromString(noun),
                ["v$"] = SheepValue.FromString(verb),
            };

            return SheepExpression.IsTrue(expression, _api, variables);
        }
        catch (FormatParseException ex)
        {
            Diagnostics.Add(ex.Diagnostic);
            return false;
        }
    }

    private static bool IsGabriel(string ego) =>
        ego.StartsWith("GAB", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a topic has exactly one line left, which its closing case is for.</summary>
    /// <remarks>
    /// Counted over every file in scope, because a topic's lines are spread between the
    /// location's general action file and its timeblock ones.
    /// </remarks>
    private bool IsLastWord(string noun, string verb)
    {
        int lines = 0;

        foreach (NvcFile file in _files)
        {
            foreach (NvcAction action in file.Actions)
            {
                if (string.Equals(action.Noun, noun, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(action.Verb, verb, StringComparison.OrdinalIgnoreCase))
                {
                    lines++;
                }
            }
        }

        return lines > 0 && Raised(noun, verb) == lines - 1;
    }

    /// <summary>Whether one line of a topic has already been said.</summary>
    private bool HasSaid(string noun, string verb, string caseName) =>
        Ask("EngineHasSaidTopicLine",
            [SheepValue.FromString(noun),
             SheepValue.FromString(verb),
             SheepValue.FromString(caseName)]) != 0;

    /// <summary>How many times a topic has been raised.</summary>
    private int Raised(string noun, string verb) =>
        Ask("GetTopicCount", [SheepValue.FromString(noun), SheepValue.FromString(verb)]);

    /// <remarks>
    /// Through the host, like every other question this class asks about the story. A host
    /// that does not answer gives zero, and a topic then reads as never said — which is
    /// what an unplayed game should look like.
    /// </remarks>
    private int Ask(string function, IReadOnlyList<SheepValue> arguments)
    {
        try
        {
            return _api.Invoke(function, arguments).AsInt();
        }
        catch (FormatParseException ex)
        {
            Diagnostics.Add(ex.Diagnostic);
            return 0;
        }
    }

    /// <summary>How often the player has already done this to this.</summary>
    /// <remarks>
    /// Asked of the host rather than of a game state this class does not have, which is the
    /// same route the file's own conditions take when they write
    /// <c>GetNounVerbCount("BLOOD_POOL","LOOK")</c>. A host that does not implement it
    /// answers zero, and every count-based case then reads as the first time — which is
    /// what an unplayed game should look like anyway.
    /// </remarks>
    private int Done(string noun, string verb)
    {
        try
        {
            return _api.Invoke(
                "GetNounVerbCount",
                [SheepValue.FromString(noun), SheepValue.FromString(verb)]).AsInt();
        }
        catch (FormatParseException ex)
        {
            Diagnostics.Add(ex.Diagnostic);
            return 0;
        }
    }

    /// <summary>Whether anything is left to say to someone.</summary>
    /// <remarks>
    /// <para>
    /// A topic is a verb: dialogue is written as actions whose verbs are named
    /// <c>T_SOMETHING</c>, so "are there topics left" is "is there a <c>T_</c> action for
    /// this noun whose case holds and which has not been used up". The original tracks the
    /// topics played this conversation; this reads the count the story keeps, which says
    /// the same thing for everything except a topic said twice in one sitting.
    /// </para>
    /// <para>
    /// Topic cases are not consulted recursively. A topic whose own case is
    /// <c>DIALOGUE_TOPICS_LEFT</c> would ask this question to answer this question, and
    /// the original does not define what that means either.
    /// </para>
    /// </remarks>
    private bool HasTopicsLeft(string noun, string ego)
    {
        foreach (NvcFile file in _files)
        {
            foreach (NvcAction action in file.Actions)
            {
                if (!action.Verb.StartsWith("T_", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(action.Noun, noun, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (action.Case.StartsWith("DIALOGUE_TOPICS_LEFT", StringComparison.OrdinalIgnoreCase) ||
                    action.Case.StartsWith("NOT_DIALOGUE_TOPICS_LEFT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Done(noun, action.Verb) == 0 &&
                    IsCaseSatisfied(file, action.Case, ego, noun, action.Verb))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies a verb for presentation.
    /// </summary>
    /// <remarks>
    /// Only inspection is singled out, because the brief fixes left click to it. Marking
    /// anything else as the primary action is a design decision the resolver should not
    /// be making on its own; <c>Plan/03</c> section 2.1 requires that no puzzle action
    /// fires because the engine guessed.
    /// </remarks>
    private static ActionCategory CategoryFor(string verb) =>
        verb.Equals("LOOK", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("INSPECT", StringComparison.OrdinalIgnoreCase)
            ? ActionCategory.Inspect
            : ActionCategory.Primary;

    private static string IconFor(string verb) => verb.ToUpperInvariant() switch
    {
        "LOOK" or "INSPECT" => "eye",
        "TALK" => "speech",
        "PICKUP" or "TAKE" => "hand",
        "OPEN" => "open",
        "CLOSE" => "close",
        "PUSH" or "PRESS" => "press",
        "GO_UP" or "GO_DOWN" or "EXIT" or "ENTER" => "move",
        _ => "action",
    };
}

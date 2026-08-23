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
    /// <param name="carrying">
    /// What the player has in their bag, or null for all of it. An inventory verb is an
    /// item being used on the thing, so it is only on offer to somebody holding the item;
    /// without this, Buthane answers to <c>WALLET</c> before Gabriel has found one.
    /// </param>
    /// <returns>Valid actions, inspect first, then in file order.</returns>
    public IReadOnlyList<AvailableAction> Resolve(
        string noun, string ego = "GABRIEL", IReadOnlyCollection<string>? carrying = null)
    {
        ArgumentNullException.ThrowIfNull(noun);

        List<(NvcAction Rule, AvailableAction Offer)> found = [];

        // One action per verb, and which one is not "the first the files happen to list".
        // See Best: the case decides, and a rule guarded by a real condition outranks the
        // catch-all written above it.
        foreach (string verb in VerbsFor(noun))
        {
            if (Verbs?.KindOf(verb) == Actions.VerbKind.Inventory &&
                carrying is not null &&
                !carrying.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Find(noun, verb, ego) is not { } rule)
            {
                continue;
            }

            found.Add((rule, new AvailableAction
            {
                ActionId = $"{noun}:{verb}",
                NvcProvenance = rule.Source,
                LocalizedVerb = verb,
                IconSemantic = IconFor(verb),
                Category = CategoryFor(verb),
                Enabled = true,
            }));
        }

        bool topics = found.Exists(f => Verbs?.IsTopic(f.Rule.Verb) ?? false);

        // Inspect first, so left click always has something predictable to do. OrderBy is
        // stable, so everything else keeps the order the files gave it.
        return [.. found
            .Where(f => !topics || !OpensTheTopicList(f.Rule))
            .Select(f => f.Offer)
            .OrderBy(a => a.Category == ActionCategory.Inspect ? 0 : 1)];
    }

    /// <summary>
    /// Every verb any file offers on a noun, in the order the files list them.
    /// </summary>
    /// <remarks>
    /// <c>ANY_OBJECT</c> first, because it is a wildcard noun and the lowest priority
    /// there is: whatever it offers, a rule written about the thing itself replaces. It is
    /// how looking at something nobody wrote a line for still gets an answer —
    /// <c>ANY_OBJECT, LOOK, ALL</c> is Gabriel saying nothing about it is interesting.
    /// <c>ANY_INV_ITEM</c> is left out: it is a wildcard <em>verb</em> and only means
    /// anything once a particular item is named, which <see cref="Find"/> handles.
    /// </remarks>
    private List<string> VerbsFor(string noun)
    {
        List<string> verbs = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { "ANY_INV_ITEM" };

        void Gather(string under)
        {
            foreach (NvcFile file in _files)
            {
                foreach (NvcAction action in file.Actions)
                {
                    if (string.Equals(action.Noun, under, StringComparison.OrdinalIgnoreCase) &&
                        seen.Add(action.Verb))
                    {
                        verbs.Add(action.Verb);
                    }
                }
            }
        }

        Gather(Wildcard);

        foreach (string name in NamesOf(noun))
        {
            Gather(name);
        }

        return verbs;
    }

    /// <summary>The noun any rule may be written about, whatever the player clicked.</summary>
    private const string Wildcard = "ANY_OBJECT";

    /// <summary>The verb a rule may be written for, whichever item is in hand.</summary>
    private const string AnyItem = "ANY_INV_ITEM";

    /// <summary>
    /// The nouns a click on one noun also answers to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GK3 writes a handful of rules about two people at once and expects clicking either
    /// of them to find it. Nothing in the data declares the equivalence — the reference
    /// implementation hard-codes the same list and says so — so this is the shipped
    /// content's own shape rather than a rule anything can derive.
    /// </para>
    /// <para>
    /// Without them, Lady Howard and Estelle answer to nothing they share, and the
    /// Armchair's two bodies lose every line written about their clothes and their throats.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> NamesOf(string noun)
    {
        yield return noun;

        foreach ((string[] any, string shared) in Together)
        {
            if (any.Contains(noun, StringComparer.OrdinalIgnoreCase))
            {
                yield return shared;
            }
        }
    }

    /// <summary>Which nouns share a page with which.</summary>
    private static readonly (string[] Any, string Shared)[] Together =
    [
        (["LADY_HOWARD", "ESTELLE"], "LADY_H_ESTELLE"),
        (["GRACE", "MOSELY"], "GRACE_N_MOSE"),
        (["GABRIEL", "MOSELY"], "GABE_N_MOSE"),
        (["WILKES", "BUCHELLI"], "WILKES_N_BUCHELLI"),
        (["MALLORY", "MACDOUGALL"], "TWO_MEN"),
        (["MOSELY", "BUTHANE", "BUCHELLI"], "BUTHANE_MOSE_BUCHELLI"),
        (["DEAD_CLOTHES_HE1", "DEAD_CLOTHES_HE2"], "DEAD_CLOTHES"),
        (["DEAD_THROAT_HE1", "DEAD_THROAT_HE2"], "DEAD_THROATS"),
    ];

    /// <summary>
    /// Whether a rule is the Talk that exists only to reach the topics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TALK</c> is a real verb with 127 rules of its own, and most of them do something
    /// no topic does: Larry has a line, Prince James has a conversation, and nine rules are
    /// guarded by <c>NOT_DIALOGUE_TOPICS_LEFT</c> and are what a character says once there
    /// is nothing left to ask them. Those all stay.
    /// </para>
    /// <para>
    /// Thirty-two are guarded by <c>DIALOGUE_TOPICS_LEFT</c>, and that case means exactly
    /// "there is something to ask about". In the original, choosing Talk there opened the
    /// list of <c>T_</c> verbs; this port puts them on the menu itself, so offering Talk
    /// beside them is offering the player a door into the room they are standing in.
    /// </para>
    /// </remarks>
    private static bool OpensTheTopicList(NvcAction action) =>
        string.Equals(action.Verb, "TALK", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(action.Case, "DIALOGUE_TOPICS_LEFT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the rule a verb on a noun would run.</summary>
    /// <param name="noun">The thing being acted on.</param>
    /// <param name="verb">What is being done to it.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <returns>The rule, or null when nothing applies.</returns>
    /// <remarks>
    /// <para>
    /// Four sets of rules can answer, from the most general to the most particular, and the
    /// last one that does wins: the wildcard pair, the wildcard noun with this verb, this
    /// noun with the wildcard item verb, and finally the pair actually asked about. The two
    /// item forms are consulted only for an inventory verb, because <c>ANY_INV_ITEM</c>
    /// means "whatever is in hand" and there is nothing in hand otherwise.
    /// </para>
    /// <para>
    /// Selecting still changes nothing; performing is <see cref="ActionRunner"/>'s job.
    /// </para>
    /// </remarks>
    public NvcAction? Find(string noun, string verb, string ego = "GABRIEL")
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(verb);

        bool item = Verbs?.KindOf(verb) == Actions.VerbKind.Inventory;
        NvcAction? found = null;

        if (item)
        {
            found = Best(Wildcard, AnyItem, verb, ego) ?? found;
        }

        found = Best(Wildcard, verb, verb, ego) ?? found;

        foreach (string name in NamesOf(noun))
        {
            if (item)
            {
                found = Best(name, AnyItem, verb, ego) ?? found;
            }

            found = Best(name, verb, verb, ego) ?? found;
        }

        return found is null ? null : Approaching(found, ego);
    }

    /// <summary>
    /// Whether an action belongs to a different point in the story than this one.
    /// </summary>
    /// <param name="action">The rule.</param>
    /// <returns>True when it cannot sensibly run now.</returns>
    /// <remarks>
    /// <para>
    /// Reported as the church's four angels offering "Trace" on the first morning, two days
    /// before the puzzle that verb belongs to. The shipped data really does allow it: the
    /// case is <c>VALID_TO_TRACE</c>, which reads <c>!GetFlag("LockedSquare") &amp;&amp;
    /// GetNounVerbCount("Four_Angels","Trace") == 0</c>, and both halves are true from the
    /// moment the game begins. The original offers it early too.
    /// </para>
    /// <para>
    /// <b>The rule says when it belongs, in its own script.</b> Those actions end in
    /// <c>CallSheep("chu205p", "Done")</c> — they hand off to the compiled script of one
    /// point in the story, which is loaded at that point and at no other. An action that
    /// calls into a script the game has not got is an action that cannot finish, and
    /// offering it is offering a verb that does half of something.
    /// </para>
    /// <para>
    /// 107 distinct timeblock scripts are called this way across the corpus, so this is a
    /// general reading of the data rather than a patch for one statue. A rule that names no
    /// such script is not filtered by it at all.
    /// </para>
    /// </remarks>
    private bool Elsewhen(NvcAction action)
    {
        if (Now is not { } now || action.Script is not { Length: > 0 } script)
        {
            return false;
        }

        int at = 0;

        while ((at = script.IndexOf("CallSheep", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int open = script.IndexOf('"', at);
            int close = open < 0 ? -1 : script.IndexOf('"', open + 1);

            at += "CallSheep".Length;

            if (close <= open || close - open - 1 != 7)
            {
                // Not a name of the shape LLLNNNa, so it names no point in the story and
                // this has nothing to say about it.
                continue;
            }

            // Three letters of location and then the timeblock, which is the whole of the
            // convention: chu205p, hal310a, din303p.
            if (Timeblock.TryParse(script[(open + 4)..close], out Timeblock when) && when != now)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where the story has got to, when anything told the resolver.</summary>
    /// <remarks>
    /// Null leaves <see cref="Elsewhen"/> filtering nothing, which is what the tools want:
    /// a sweep asks what a room can do across the whole story rather than at one moment in
    /// it, and a resolver with no clock must not quietly answer a narrower question.
    /// </remarks>
    public Timeblock? Now { get; set; }

    /// <summary>
    /// Gives a topic the walk that the Talk it was hoisted out of would have made.
    /// </summary>
    /// <param name="rule">The rule that is going to run.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <returns>The same rule, or a copy of it carrying an approach.</returns>
    /// <remarks>
    /// <para>
    /// In the original, asking somebody about something took two steps. <c>TALK</c> carried
    /// the approach — <c>EMILIO, TALK, DIALOGUE_TOPICS_LEFT, approach=ANIM,
    /// target=GabEmlLbyShake</c> walks Gabriel over and shakes his hand — and the topics it
    /// then opened carried none, because by that point he was already standing there.
    /// </para>
    /// <para>
    /// <see cref="OpensTheTopicList"/> drops that Talk and puts the topics on the menu
    /// directly, which is the improvement <c>docs/screens.md</c> asks for; it also dropped
    /// the walk along with the step it replaced, so Gabriel could hold a conversation with
    /// Emilio from the phone-room curtains on the far side of the lobby.
    /// </para>
    /// <para>
    /// Only the approach is borrowed. The script a topic runs is its own and is untouched,
    /// which is what <c>Plan/03</c> section 2.3 requires of anything that modernises input.
    /// A topic that states its own approach keeps it, and a noun whose Talk states none
    /// gains nothing.
    /// </para>
    /// </remarks>
    private NvcAction Approaching(NvcAction rule, string ego)
    {
        if (rule.Approach is { Length: > 0 } ||
            Verbs?.IsTopic(rule.Verb) != true)
        {
            return rule;
        }

        // The Talk that would have opened this list, whether or not it is currently on
        // offer: it is being taken off the menu precisely when the topics are on it.
        foreach (NvcFile file in _files)
        {
            foreach (NvcAction action in file.Actions)
            {
                if (string.Equals(action.Noun, rule.Noun, StringComparison.OrdinalIgnoreCase) &&
                    OpensTheTopicList(action) &&
                    action is { Approach.Length: > 0, Target.Length: > 0 } &&
                    IsCaseSatisfied(file, action.Case, ego, action.Noun, action.Verb))
                {
                    return rule with { Approach = action.Approach, Target = action.Target };
                }
            }
        }

        return rule;
    }

    /// <summary>
    /// The one rule a noun and verb run, out of however many could.
    /// </summary>
    /// <param name="noun">The noun the rules are written about, which may be a wildcard.</param>
    /// <param name="written">The verb they are written for, which may be a wildcard.</param>
    /// <param name="asked">The verb actually being done, which is what decides its kind.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <returns>The rule to run, or null when none of them applies.</returns>
    /// <remarks>
    /// <para>
    /// <b>Not the first one the files list.</b> Several rules for one pair are ordinary —
    /// the lobby writes <c>REGISTER, LOOK, GABE_ALL</c> above <c>REGISTER, LOOK,
    /// NOT_SEEN_REGISTER</c> — and taking whichever came first gives Gabriel the line he
    /// says about a register he has already read, the first time he reads it.
    /// </para>
    /// <para>
    /// The case decides, on the original's own ladder: the catch-alls are worth least, a
    /// timeblock's override more, a condition somebody actually wrote more still, and
    /// "the first time you did this" most of all. Where two hand-written conditions tie,
    /// the more specific file wins, and where that ties too the original falls back on
    /// comparing the case names — see <see cref="Sooner"/>, which is as strange as it looks
    /// and is what the shipped data was authored against.
    /// </para>
    /// </remarks>
    private NvcAction? Best(string noun, string written, string asked, string ego)
    {
        NvcAction? best = null;
        int score = 0;
        int from = int.MaxValue;

        for (int index = 0; index < _files.Count; index++)
        {
            NvcFile file = _files[index];

            foreach (NvcAction action in file.Actions)
            {
                if (!string.Equals(action.Noun, noun, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(action.Verb, written, StringComparison.OrdinalIgnoreCase) ||
                    Elsewhen(action) ||
                    !IsCaseSatisfied(file, action.Case, ego, noun, asked))
                {
                    continue;
                }

                int worth = Worth(file, action.Case);

                if (worth > score)
                {
                    (best, score, from) = (action, worth, index);
                    continue;
                }

                if (worth < score || best is null)
                {
                    continue;
                }

                // A tie between two conditions somebody wrote. The files are in scope most
                // specific first, so a lower index is the more particular file and settles
                // it; only when they come from the same kind of file does the name decide.
                if (index < from || (index == from && Sooner(action.Case, best.Case)))
                {
                    (best, from) = (action, index);
                }
            }
        }

        return best;
    }

    /// <summary>How much a case label outranks another.</summary>
    /// <param name="file">The file the rule is in, which is asked first about the name.</param>
    /// <param name="caseName">The case.</param>
    /// <returns>Its rank, higher being stronger.</returns>
    /// <remarks>
    /// The original's ladder, lowest first: the catch-alls, then the ego-specific ones,
    /// then a timeblock's plain marker, then "not the first time", then the two questions
    /// about whether there is anything left to say, then a timeblock's override, then any
    /// condition written in a logic section, and above everything the counted ones.
    /// </remarks>
    private int Worth(NvcFile file, string caseName) => caseName.ToUpperInvariant() switch
    {
        "ALL" or "ALL_INV" or "DEFAULT" => 1,
        "GABE_ALL" or "GRACE_ALL" or "GABE_ALL_INV" or "GRACE_ALL_INV"
            or "NOT_GABE_ALL" or "NOT_GRACE_ALL" => 2,
        "TIME_BLOCK" => 3,
        "OTR_TIME" => 4,
        "DIALOGUE_TOPICS_LEFT" or "NOT_DIALOGUE_TOPICS_LEFT" => 5,
        "TIME_BLOCK_OVERRIDE" => 6,
        "1ST_TIME" or "2CD_TIME" or "2ND_TIME" or "3RD_TIME" => 8,
        _ => Defined(file, caseName) ? 7 : 1,
    };

    /// <summary>Whether any file in scope writes this case down.</summary>
    private bool Defined(NvcFile file, string caseName) =>
        file.Cases.ContainsKey(caseName) || _files.Exists(f => f.Cases.ContainsKey(caseName));

    /// <summary>
    /// Whether one case name sorts before another, the way the original sorts them.
    /// </summary>
    /// <param name="candidate">The case being considered.</param>
    /// <param name="standing">The case it would replace.</param>
    /// <returns>True when the candidate wins.</returns>
    /// <remarks>
    /// Not an ordinal comparison. A digit beats anything that is not one and a smaller
    /// digit beats a larger; an underscore beats any letter; otherwise the earlier letter
    /// wins. Where one name is a prefix of the other the shorter wins. It is written down
    /// here because it decides which of two hand-written conditions the player gets, and
    /// nothing about it is guessable from the data.
    /// </remarks>
    private static bool Sooner(string candidate, string standing)
    {
        string a = candidate.ToUpperInvariant();
        string b = standing.ToUpperInvariant();

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= b.Length)
            {
                return false;
            }

            if (i >= a.Length)
            {
                return true;
            }

            if (a[i] == b[i])
            {
                continue;
            }

            bool oneIsDigit = char.IsAsciiDigit(a[i]);
            bool otherIsDigit = char.IsAsciiDigit(b[i]);

            return (oneIsDigit && !otherIsDigit) ||
                   (oneIsDigit && otherIsDigit && a[i] < b[i]) ||
                   (a[i] == '_' && b[i] != '_' && !otherIsDigit) ||
                   (a[i] < b[i] && b[i] != '_' && !otherIsDigit);
        }

        return false;
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

                // The one built-in case the player can turn on. The original hard-codes it
                // false — its own source has the same placeholder — so the content behind
                // it never shipped in a playable form. Reading a flag instead costs nothing
                // when nobody sets it, which is every ordinary game, and gives the console
                // something to set.
                "EGG" => Flag("EGG"),

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

    /// <summary>Whether a story flag is set.</summary>
    /// <remarks>
    /// Through the host, like <see cref="Done"/> and for the same reason: a resolver has no
    /// game state of its own, and the route a file's own conditions take when they write
    /// <c>GetFlag("EGG")</c> is the route this should take too. A host that does not
    /// implement it answers zero, which reads as unset.
    /// </remarks>
    private bool Flag(string name)
    {
        try
        {
            return _api.Invoke("GetFlag", [SheepValue.FromString(name)]).AsInt() != 0;
        }
        catch (FormatParseException ex)
        {
            Diagnostics.Add(ex.Diagnostic);
            return false;
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

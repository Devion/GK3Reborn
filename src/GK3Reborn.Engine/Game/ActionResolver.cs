using GK3Reborn.Formats;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;
using GK3Reborn.UI.Interaction;

namespace GK3Reborn.Game;

/// <summary>
/// Decides what the player can do to something right now.
/// </summary>
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
    public Actions.VerbLibrary? Verbs { get; set; }

    /// <summary>Diagnostics raised while resolving.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Adds an action file to the set in scope.</summary>
    /// <param name="file">The file.</param>
    public void Add(NvcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _files.Add(file);
    }

    /// <summary>Every noun any loaded file mentions.</summary>
    public IReadOnlyCollection<string> Nouns =>
        [.. _files.SelectMany(f => f.Actions).Select(a => a.Noun).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Every noun any loaded file writes a rule about for one verb, in file order.
    /// </summary>
    /// <param name="verb">The verb to look for.</param>
    /// <returns>The nouns, whether or not any of their cases hold now.</returns>
    public IReadOnlyList<string> NounsFor(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);

        List<string> nouns = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (NvcFile file in _files)
        {
            foreach (NvcAction action in file.Actions)
            {
                if (string.Equals(action.Verb, verb, StringComparison.OrdinalIgnoreCase) &&
                    seen.Add(action.Noun))
                {
                    nouns.Add(action.Noun);
                }
            }
        }

        return nouns;
    }

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
                !carrying.Contains(verb, StringComparer.OrdinalIgnoreCase) &&
                !carrying.Contains(ItemFor(verb), StringComparer.OrdinalIgnoreCase))
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

        // Inspect first, so left click always has something predictable to do, and the
        // things out of the bag last, because they are the least likely of the three to be
        // what the player means. OrderBy is stable, so within each the file order stands.
        //
        // The bag going last is not only tidiness. <c>ANY_OBJECT, FINGERPRINT_KIT,
        // GABE_ALL</c> is the second line of GLB_ALL.NVC, which is in scope in every room
        // in the game, so from the moment Gabriel picks the kit up it is a verb on *every
        // noun there is* — and being a wildcard rule it was gathered before anything
        // written about the thing itself. Reported as the kit overriding most other nouns
        // on everything, with a right click needed to reach what the object actually does.
        return [.. found
            .Where(f => !topics || !OpensTheTopicList(f.Rule))
            .Select(f => f.Offer)
            .OrderBy(a => a.Category switch
            {
                ActionCategory.Inspect => 0,
                ActionCategory.Item => 2,
                _ => 1,
            })];
    }

    /// <summary>
    /// Every verb any file offers on a noun, in the order the files list them.
    /// </summary>
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
                        Offerable(action.Verb) &&
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

    /// <summary>
    /// Whether a verb is one the player could ever pick, rather than one only a script fires.
    /// </summary>
    /// <param name="verb">The verb a rule is written for.</param>
    /// <returns>True when it belongs on the menu.</returns>
    private bool Offerable(string verb) => Verbs is null || Verbs.Knows(verb);

    /// <summary>The noun any rule may be written about, whatever the player clicked.</summary>
    private const string Wildcard = "ANY_OBJECT";

    /// <summary>The verb a rule may be written for, whichever item is in hand.</summary>
    private const string AnyItem = "ANY_INV_ITEM";

    /// <summary>
    /// The nouns a click on one noun also answers to.
    /// </summary>
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

    /// <summary>
    /// What a thing is called in the bag, when that is not what it is called as a verb.
    /// </summary>
    /// <param name="verb">The inventory verb a rule is written for.</param>
    /// <returns>The item's own name, or the verb itself when the two agree.</returns>
    private static string ItemFor(string verb) => Renamed.GetValueOrDefault(verb, verb);

    /// <summary>
    /// The two inventory items <c>VERBS.TXT</c> and <c>INVENTORYSPRITES.TXT</c> disagree
    /// about. Everything else in the bag is used on the world under its own name, so an
    /// item verb is offered to whoever is carrying an item of that name — but the tube in
    /// Gabriel's pocket is <c>PREPARATION_H_TUBE</c> and the only rule in the game written
    /// for it, <c>OFFICE_WINDOW</c> in CEM210A, is written for the verb
    /// <c>PREPARATION_H</c>. Without this the verb is never offered, the Abbé's window
    /// never comes unstuck, and his office cannot be searched at all: the tier is
    /// unfinishable. The reference hard-codes the same two, and finds no table anywhere in
    /// the shipped data either — see ActionBar::Show.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PREPARATION_H"] = "PREPARATION_H_TUBE",
        ["FINGERPRINT_KIT"] = "FINGERPRINT_KIT_GRACES",
    };

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
    private static bool OpensTheTopicList(NvcAction action) =>
        string.Equals(action.Verb, "TALK", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(action.Case, "DIALOGUE_TOPICS_LEFT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the rule a verb on a noun would run.</summary>
    /// <param name="noun">The thing being acted on.</param>
    /// <param name="verb">What is being done to it.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <returns>The rule, or null when nothing applies.</returns>
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
    /// <param name="file">The file the rule is written in, which says when it belongs.</param>
    /// <param name="action">The rule.</param>
    /// <returns>True when it cannot sensibly run now.</returns>
    private bool Elsewhen(NvcFile file, NvcAction action)
    {
        if (Now is not { } now ||
            action.Script is not { Length: > 0 } script ||
            TimeblockRange.Specificity(file.Name) > 0)
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
    public Timeblock? Now { get; set; }

    /// <summary>
    /// Gives a topic the walk that the Talk it was hoisted out of would have made.
    /// </summary>
    /// <param name="rule">The rule that is going to run.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <returns>The same rule, or a copy of it carrying an approach.</returns>
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
    private NvcAction? Best(string noun, string written, string asked, string ego)
    {
        NvcAction? best = null;
        int score = 0;
        int from = int.MinValue;

        foreach (NvcFile file in _files)
        {
            int particular = Particularity(file);

            foreach (NvcAction action in file.Actions)
            {
                if (!string.Equals(action.Noun, noun, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(action.Verb, written, StringComparison.OrdinalIgnoreCase) ||
                    Elsewhen(file, action) ||
                    !IsCaseSatisfied(file, action.Case, ego, noun, asked))
                {
                    continue;
                }

                int worth = Worth(file, action.Case);

                if (worth > score)
                {
                    (best, score, from) = (action, worth, particular);
                    continue;
                }

                if (worth < score || best is null)
                {
                    continue;
                }

                // A tie between two conditions somebody wrote. The more particular file
                // settles it, and how particular a file is comes out of its own name rather
                // than out of where the scene file happens to list it: LBY.SIF names
                // lby_all.nvc above lby_1all.nvc, so reading order as priority gave day
                // one's rules to the file that covers every day. Only when the two are
                // equally particular does the case name decide.
                if (particular > from || (particular == from && Sooner(action.Case, best.Case)))
                {
                    (best, from) = (action, particular);
                }
            }
        }

        return best;
    }

    /// <summary>How particular a file in scope is, cached because it is asked per rule.</summary>
    private int Particularity(NvcFile file)
    {
        if (!_particular.TryGetValue(file.Name, out int rank))
        {
            _particular[file.Name] = rank = TimeblockRange.Specificity(file.Name);
        }

        return rank;
    }

    private readonly Dictionary<string, int> _particular = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How much a case label outranks another.</summary>
    /// <param name="file">The file the rule is in, which is asked first about the name.</param>
    /// <param name="caseName">The case.</param>
    /// <returns>Its rank, higher being stronger.</returns>
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
    private bool HasTopicsLeft(string noun, string ego)
    {
        if (_asking)
        {
            return false;
        }

        _asking = true;

        try
        {
            HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);

            foreach (NvcFile file in _files)
            {
                foreach (NvcAction action in file.Actions)
                {
                    // The prefix, and not the verb library, decides what counts as a topic
                    // here. It is what the original asks, and it keeps the answer the same
                    // for a tool reading the files without VERBS.TXT.
                    if (!action.Verb.StartsWith("T_", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(action.Noun, noun, StringComparison.OrdinalIgnoreCase) ||
                        !tried.Add(action.Verb))
                    {
                        continue;
                    }

                    if (Find(noun, action.Verb, ego) is not null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>Guards the one question in here that can be asked while it is being answered.</summary>
    private bool _asking;

    /// <summary>
    /// Classifies a verb for presentation.
    /// </summary>
    /// <summary>Which of the three kinds of row a verb makes.</summary>
    private ActionCategory CategoryFor(string verb) =>
        Verbs?.KindOf(verb) == Actions.VerbKind.Inventory
            ? ActionCategory.Item
            : verb.Equals("LOOK", StringComparison.OrdinalIgnoreCase) ||
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

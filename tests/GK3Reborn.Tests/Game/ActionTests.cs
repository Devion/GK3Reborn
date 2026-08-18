using GK3Reborn.Formats;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Sheep;
using GK3Reborn.UI.Interaction;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class SheepExpressionTests
{
    private static readonly Gk3SheepApi Api = new(new GameState());

    private static int Eval(string text) => SheepExpression.Evaluate(text, Api).AsInt();

    [Theory]
    [InlineData("1 + 2 * 3", 7)]          // multiplication binds tighter
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]         // subtraction is left-associative
    [InlineData("7 % 4", 3)]
    [InlineData("10 / 0", 0)]             // never throws
    [InlineData("-5 + 8", 3)]
    public void Arithmetic_follows_C_precedence(string expression, int expected) =>
        Assert.Equal(expected, Eval(expression));

    [Theory]
    [InlineData("1 && 0", 0)]
    [InlineData("1 || 0", 1)]
    [InlineData("!0", 1)]
    [InlineData("!1", 0)]
    [InlineData("2 > 1", 1)]
    [InlineData("2 >= 2", 1)]
    [InlineData("1 < 1", 0)]
    [InlineData("1 <= 1", 1)]
    [InlineData("1 == 1", 1)]
    [InlineData("1 != 1", 0)]
    [InlineData("1 <> 2", 1)]             // the language's second spelling of not-equal
    [InlineData("0 || 1 && 0", 0)]        // && binds tighter than ||
    public void Comparison_and_logic_work(string expression, int expected) =>
        Assert.Equal(expected, Eval(expression));

    [Fact]
    public void Relational_operators_are_not_confused_with_their_two_character_forms()
    {
        // A greedy "<" would eat the "<" of "<=" and compare wrongly.
        Assert.Equal(1, Eval("2 <= 2"));
        Assert.Equal(0, Eval("2 < 2"));
        Assert.Equal(1, Eval("3 <> 2"));
    }

    [Fact]
    public void Function_calls_reach_the_api()
    {
        var state = new GameState();
        state.SetFlag("metJean");
        var api = new Gk3SheepApi(state);

        Assert.True(SheepExpression.IsTrue("GetFlag(\"metJean\")", api));
        Assert.False(SheepExpression.IsTrue("GetFlag(\"neverMet\")", api));
        Assert.True(SheepExpression.IsTrue("!GetFlag(\"neverMet\") && GetFlag(\"metjean\")", api));
    }

    [Fact]
    public void Bare_names_resolve_against_bound_variables()
    {
        // Action conditions use n$ and v$ for the noun and verb under evaluation.
        var variables = new Dictionary<string, SheepValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["n$"] = SheepValue.FromString("DOOR"),
            ["v$"] = SheepValue.FromString("OPEN"),
        };

        Assert.Equal("DOOR", SheepExpression.Evaluate("n$", Api, variables).AsString());
        Assert.Equal(0, SheepExpression.Evaluate("GetNounVerbCount(n$, v$)", Api, variables).AsInt());
    }

    [Fact]
    public void An_unbound_bare_name_is_reported_rather_than_assumed_zero()
    {
        var ex = Assert.Throws<FormatParseException>(() => Eval("mysteryThing"));
        Assert.Equal("GK3R3300", ex.Diagnostic.Code);
    }

    [Fact]
    public void Malformed_expressions_are_reported_with_their_text()
    {
        var ex = Assert.Throws<FormatParseException>(() => Eval("(1 + 2"));
        Assert.Equal("GK3R3300", ex.Diagnostic.Code);
        Assert.NotNull(ex.Diagnostic.Actual);
    }

    [Fact]
    public void A_realistic_condition_from_the_game_evaluates()
    {
        var state = new GameState();
        state.SetVariable("MoselyOnCandyPath102p", 3);
        var api = new Gk3SheepApi(state);

        const string Condition =
            "(GetGameVariableInt(\"MoselyOnCandyPath102p\") == 1) || " +
            "(GetGameVariableInt(\"MoselyOnCandyPath102p\") == 3) || " +
            "(GetGameVariableInt(\"MoselyOnCandyPath102p\") == 5)";

        Assert.True(SheepExpression.IsTrue(Condition, api));
    }
}

public sealed class NvcFileTests
{
    private const string Sample = """
        STAIRS_LEFT,   GO_UP,   GABE_ALL,   approach=WalkTo, target=TO_HAL_L, script={wait CallSheep("lby","Up$");}
        MOSELY,        LOOK,    ALL,        script={wait StartVoiceOver("1E91244Q81",1);}
        MOSELY,        TALK,    HAS_TALKED, script={wait StartDialogue("GabeMos");}
        // a comment line
        SCENE,         ENTER,   ALL,        script={CallSheep("lby","SceneEnter");}

        [LOGIC]
        HAS_TALKED={GetTopicCount("MOSELY","T_INTRO") > 0}
        """;

    private static NvcFile Parse(out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        return NvcFile.Parse(Sample, "LBY.NVC", diagnostics);
    }

    [Fact]
    public void Rules_and_their_fields_are_read()
    {
        NvcFile file = Parse(out DiagnosticBag diagnostics);

        Assert.Equal(4, file.Actions.Count);
        Assert.Empty(diagnostics.Items);

        NvcAction stairs = file.Actions[0];
        Assert.Equal("STAIRS_LEFT", stairs.Noun);
        Assert.Equal("GO_UP", stairs.Verb);
        Assert.Equal("GABE_ALL", stairs.Case);
        Assert.Equal("WalkTo", stairs.Approach);
        Assert.Equal("TO_HAL_L", stairs.Target);
        Assert.Contains("CallSheep", stairs.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_containing_commas_does_not_split_the_line()
    {
        // The script field holds commas and braces, so it has to be lifted out before the
        // rest of the line is split.
        NvcFile file = Parse(out _);

        NvcAction mosely = file.Actions[1];
        Assert.Equal("MOSELY", mosely.Noun);
        Assert.Equal("LOOK", mosely.Verb);
        Assert.Contains("1E91244Q81", mosely.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_logic_section_defines_cases()
    {
        NvcFile file = Parse(out _);

        Assert.True(file.Cases.ContainsKey("HAS_TALKED"));
        Assert.Contains("GetTopicCount", file.Cases["HAS_TALKED"], StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_are_ignored()
    {
        NvcFile file = Parse(out _);
        Assert.DoesNotContain(file.Actions, a => a.Noun.StartsWith("//", StringComparison.Ordinal));
    }
}

public sealed class ActionResolverTests
{
    private const string Sample = """
        MOSELY,   LOOK,   ALL,        script={wait StartVoiceOver("x",1);}
        MOSELY,   TALK,   HAS_TALKED, script={wait StartDialogue("GabeMos");}
        MOSELY,   PICKUP, GABE_ALL,   script={wait StartVoiceOver("y",1);}
        DOOR,     OPEN,   UNLOCKED,   script={wait CallSheep("lby","Open$");}

        [LOGIC]
        HAS_TALKED={GetTopicCount("MOSELY","T_INTRO") > 0}
        UNLOCKED={GetFlag("DoorUnlocked")}
        """;

    private static ActionResolver Build(GameState state)
    {
        var resolver = new ActionResolver(new Gk3SheepApi(state));
        resolver.Add(NvcFile.Parse(Sample, "TEST.NVC", new DiagnosticBag()));
        return resolver;
    }

    [Fact]
    public void Only_actions_whose_case_holds_are_offered()
    {
        var state = new GameState();
        ActionResolver resolver = Build(state);

        Assert.Equal(["LOOK", "PICKUP"], resolver.Resolve("MOSELY").Select(a => a.LocalizedVerb));

        state.SetTopicCount("MOSELY", "T_INTRO", 1);
        Assert.Equal(["LOOK", "TALK", "PICKUP"], resolver.Resolve("MOSELY").Select(a => a.LocalizedVerb));
    }

    [Fact]
    public void Inspect_comes_first_so_left_click_is_predictable()
    {
        ActionResolver resolver = Build(new GameState());
        IReadOnlyList<AvailableAction> actions = resolver.Resolve("MOSELY");

        Assert.Equal(ActionCategory.Inspect, actions[0].Category);
        Assert.Equal("LOOK", actions[0].LocalizedVerb);
    }

    [Fact]
    public void Ego_specific_cases_follow_who_the_player_is()
    {
        ActionResolver resolver = Build(new GameState());

        Assert.Contains(resolver.Resolve("MOSELY", "GABRIEL"), a => a.LocalizedVerb == "PICKUP");
        Assert.DoesNotContain(resolver.Resolve("MOSELY", "GRACE"), a => a.LocalizedVerb == "PICKUP");
    }

    [Fact]
    public void Resolving_does_not_change_the_game()
    {
        // Hovering the cursor must not be able to corrupt a save.
        var state = new GameState();
        state.SetFlag("DoorUnlocked");
        ActionResolver resolver = Build(state);

        string before = state.ComputeHash();
        resolver.Resolve("MOSELY");
        resolver.Resolve("DOOR");

        Assert.Equal(before, state.ComputeHash());
    }

    [Fact]
    public void Actions_carry_provenance_back_to_the_original_file()
    {
        ActionResolver resolver = Build(new GameState());
        AvailableAction action = resolver.Resolve("MOSELY")[0];

        Assert.StartsWith("TEST.NVC:", action.NvcProvenance, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_noun_offers_nothing()
    {
        ActionResolver resolver = Build(new GameState());
        Assert.Empty(resolver.Resolve("NOT_A_THING"));
    }
}

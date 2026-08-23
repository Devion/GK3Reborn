using GK3Reborn.Game;
using GK3Reborn.Sheep;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the developer console.
/// </summary>
/// <remarks>
/// The console has no idea what a keyboard is: everything a key does is a method on it, so
/// these drive it the way the frame loop does and never mention a key.
/// </remarks>
public sealed class GameConsoleTests
{
    /// <summary>A console that knows a handful of functions and records what it was asked.</summary>
    private static (GameConsole Console, List<(string Name, IReadOnlyList<SheepValue> Args)> Asked)
        Ready(params string[] names)
    {
        var console = new GameConsole();
        List<(string, IReadOnlyList<SheepValue>)> asked = [];

        console.Knows(names.Length > 0
            ? names
            : ["SetFlag", "ClearFlag", "GetFlag", "SetLocation", "SetEgoLocation", "FlagIt"]);

        console.Calls = (name, arguments) =>
        {
            asked.Add((name, arguments));
            return SheepValue.FromInt(0);
        };

        console.Show(true);
        return (console, asked);
    }

    [Fact]
    public void Typing_a_prefix_offers_the_functions_that_start_with_it_first()
    {
        (GameConsole console, _) = Ready();

        console.Type("set");

        Assert.Equal(
            ["SetEgoLocation", "SetFlag", "SetLocation"],
            console.Completions.Select(c => c.Name));
    }

    [Fact]
    public void A_name_half_remembered_is_still_found()
    {
        // The second half of the list: somebody who knows there is a flag function but not
        // what it is called should not have to guess the first three letters of it.
        (GameConsole console, _) = Ready();

        console.Type("flag");

        // The one that starts with it leads; the three that merely contain it follow, in
        // their own order. Two passes over one list rather than a mode to switch between.
        Assert.Equal(
            ["FlagIt", "ClearFlag", "GetFlag", "SetFlag"],
            console.Completions.Select(c => c.Name));
    }

    [Fact]
    public void No_more_than_a_readable_number_are_offered()
    {
        (GameConsole console, _) = Ready(
            [.. Enumerable.Range(0, 40).Select(i => $"SetThing{i:00}")]);

        console.Type("set");

        Assert.Equal(GameConsole.Suggestions, console.Completions.Count);
    }

    [Fact]
    public void Completing_writes_the_name_and_opens_the_brackets()
    {
        // Because a function is being called rather than named, and the caret should end up
        // where the arguments go.
        (GameConsole console, _) = Ready();

        console.Type("setf");
        console.TakeCompletion();

        Assert.Equal("SetFlag(", console.Typed);
    }

    [Fact]
    public void The_list_goes_away_once_the_arguments_start()
    {
        // A list of other functions covering the screen while somebody types a string into
        // this one is in the way.
        (GameConsole console, _) = Ready();

        console.Type("setf");
        Assert.NotEmpty(console.Completions);

        console.TakeCompletion();
        Assert.Empty(console.Completions);
    }

    [Fact]
    public void Up_and_down_move_the_choice_while_there_is_a_list()
    {
        (GameConsole console, _) = Ready();

        console.Type("set");
        Assert.Equal(0, console.Chosen);

        console.Move(1);
        Assert.Equal(1, console.Chosen);

        // Wraps, so the last entry is one press from the first.
        console.Move(-1);
        console.Move(-1);
        Assert.Equal(console.Completions.Count - 1, console.Chosen);
    }

    [Fact]
    public void Up_and_down_recall_earlier_lines_when_there_is_no_list()
    {
        (GameConsole console, _) = Ready();

        console.Type("SetFlag(\"one\")");
        console.Submit();
        console.Type("SetFlag(\"two\")");
        console.Submit();

        console.Move(-1);
        Assert.Equal("SetFlag(\"two\")", console.Typed);

        console.Move(-1);
        Assert.Equal("SetFlag(\"one\")", console.Typed);
    }

    [Fact]
    public void A_call_reaches_the_host_with_its_arguments()
    {
        (GameConsole console, List<(string Name, IReadOnlyList<SheepValue> Args)> asked) = Ready();

        console.Type("SetFlag(\"EGG\")");
        console.Submit();

        (string name, IReadOnlyList<SheepValue> arguments) = Assert.Single(asked);

        Assert.Equal("SetFlag", name);
        Assert.Equal("EGG", Assert.Single(arguments).AsString());
    }

    [Fact]
    public void The_easter_egg_switch_is_a_flag_the_console_can_set()
    {
        // What the console is for, in one line. Every action file in the game tests EGG and
        // nothing in the shipped game ever sets it — the original's own resolver answers
        // false and says so — so this is the only way to see that content.
        var story = new GameState();
        var api = new Gk3SheepApi(story);
        var console = new GameConsole();

        console.Knows(api.FunctionNames);
        console.Calls = api.Perform;
        console.Show(true);

        Assert.False(story.GetFlag("EGG"));

        console.Type("SetFlag(\"EGG\")");
        console.Submit();

        Assert.True(story.GetFlag("EGG"));
    }

    [Fact]
    public void Arguments_are_read_as_the_kinds_they_look_like()
    {
        (GameConsole console, List<(string Name, IReadOnlyList<SheepValue> Args)> asked) =
            Ready("Thing");

        console.Type("Thing(\"a, b\", 12, 1.5, bare)");
        console.Submit();

        IReadOnlyList<SheepValue> arguments = asked[0].Args;

        // The comma inside the quotes is part of the string, not a separator.
        Assert.Equal(4, arguments.Count);
        Assert.Equal("a, b", arguments[0].AsString());
        Assert.Equal(SheepValueKind.Int, arguments[1].Kind);
        Assert.Equal(12, arguments[1].AsInt());
        Assert.Equal(SheepValueKind.Float, arguments[2].Kind);
        Assert.Equal(1.5f, arguments[2].AsFloat(), 3);
        Assert.Equal("bare", arguments[3].AsString());
    }

    [Fact]
    public void A_function_of_no_arguments_may_be_typed_without_brackets()
    {
        (GameConsole console, List<(string Name, IReadOnlyList<SheepValue> Args)> asked) =
            Ready("Rewind");

        console.Type("Rewind");
        console.Submit();

        Assert.Equal("Rewind", asked[0].Name);
        Assert.Empty(asked[0].Args);
    }

    [Fact]
    public void An_unknown_function_is_complained_about_rather_than_swallowed()
    {
        var console = new GameConsole();

        console.Knows(["SetFlag"]);
        console.Calls = (_, _) => null;
        console.Show(true);

        console.Type("Nonsense()");
        console.Submit();

        Assert.Contains(
            console.Lines,
            l => l.Kind == ConsoleLineKind.Complaint && l.Text.Contains("Nonsense", StringComparison.Ordinal));
    }

    [Fact]
    public void A_call_that_throws_is_reported_rather_than_ending_the_game()
    {
        // A console that closes the game when a command is wrong is a console nobody will
        // use twice.
        var console = new GameConsole();

        console.Knows(["Explode"]);
        console.Calls = (_, _) => throw new InvalidOperationException("no");
        console.Show(true);

        console.Type("Explode()");

        Assert.True(console.Submit());
        Assert.Contains(console.Lines, l => l.Kind == ConsoleLineKind.Complaint);
    }

    [Fact]
    public void Backspace_takes_a_character_and_narrows_the_list_again()
    {
        (GameConsole console, _) = Ready();

        console.Type("setl");
        Assert.Equal(["SetLocation"], console.Completions.Select(c => c.Name));

        console.Backspace();
        Assert.Equal("set", console.Typed);
        Assert.True(console.Completions.Count > 1);
    }

    [Fact]
    public void A_closed_console_offers_nothing()
    {
        (GameConsole console, _) = Ready();

        console.Type("set");
        Assert.NotEmpty(console.Completions);

        console.Show(false);
        console.Type("f");

        Assert.Empty(console.Completions);
    }
}

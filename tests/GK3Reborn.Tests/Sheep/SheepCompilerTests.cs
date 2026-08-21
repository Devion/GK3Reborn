using GK3Reborn.Formats;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Sheep;

/// <summary>
/// Tests for the Sheep front end: scanner, parser and compiler.
/// </summary>
/// <remarks>
/// Each of these compiles source and then <em>runs it</em>, because that is the only check
/// worth making. A compiler can be wrong in ways a syntax tree comparison cannot see —
/// the wrong conversion, a jump one byte out, an argument count that does not match what
/// was pushed — and every one of those shows up the moment the machine reads it.
/// </remarks>
public sealed class SheepCompilerTests
{
    /// <summary>An API that records what it was asked and answers what it was told to.</summary>
    private sealed class Recorder(params (string Name, SheepValue Result)[] answers) : ISheepApi
    {
        public List<SheepCall> Calls { get; } = [];

        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
        {
            Calls.Add(new SheepCall(name, arguments, false));

            foreach ((string named, SheepValue result) in answers)
            {
                if (string.Equals(named, name, StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }
            }

            return SheepValue.FromInt(0);
        }

        public bool IsWaitable(string name) => false;
    }

    /// <summary>The signatures a test needs, written the way the import table records them.</summary>
    private static SheepSignatures Signatures(params (string Name, sbyte Returns, sbyte[] Args)[] functions)
    {
        var catalogue = new SheepSignatures();

        foreach ((string name, sbyte returns, sbyte[] args) in functions)
        {
            catalogue.Add(new SheepImport(name, returns, args));
        }

        return catalogue;
    }

    private static (SheepThread Thread, Recorder Api) Run(
        string source, SheepSignatures? signatures = null, params (string, SheepValue)[] answers)
    {
        SheepScriptFile script = SheepCompiler.Compile(source, "TEST.SHP", signatures);
        var api = new Recorder(answers);

        return (new SheepVirtualMachine(api).Execute(script, "Main$"), api);
    }

    [Fact]
    public void A_script_of_one_call_compiles_and_runs()
    {
        (SheepThread thread, Recorder api) = Run(
            """
            code
            {
                Main$()
                {
                    SetFlag("SawPainting");
                }
            }
            """,
            Signatures(("SetFlag", SheepSignatures.Void, [SheepSignatures.String])));

        Assert.Equal(SheepThreadState.Completed, thread.State);

        SheepCall call = Assert.Single(api.Calls);
        Assert.Equal("SetFlag", call.Name);
        Assert.Equal("SawPainting", call.Arguments[0].AsString());
    }

    [Fact]
    public void Arguments_arrive_in_the_order_they_were_written()
    {
        (_, Recorder api) = Run(
            """
            code { Main$() { SetNounVerbCount("PAINTING", "LOOK", 3); } }
            """,
            Signatures(("SetNounVerbCount", SheepSignatures.Void,
                [SheepSignatures.String, SheepSignatures.String, SheepSignatures.Int])));

        SheepCall call = Assert.Single(api.Calls);

        Assert.Equal("PAINTING", call.Arguments[0].AsString());
        Assert.Equal("LOOK", call.Arguments[1].AsString());
        Assert.Equal(3, call.Arguments[2].AsInt());
    }

    [Fact]
    public void A_whole_number_written_where_a_float_belongs_is_converted()
    {
        // The original does the same rather than pushing a float constant:
        // SetTimerSeconds(2) compiles to a PushI and an IToF. Getting it wrong gives the
        // called function the bit pattern of the integer read as a float, which is not a
        // small error — 2 becomes about 3e-45.
        (_, Recorder api) = Run(
            """
            code { Main$() { SetTimerSeconds(2); } }
            """,
            Signatures(("SetTimerSeconds", SheepSignatures.Void, [SheepSignatures.Float])));

        Assert.Equal(2f, Assert.Single(api.Calls).Arguments[0].AsFloat(), 5);
    }

    [Fact]
    public void Arithmetic_on_whole_numbers_uses_the_integer_instructions()
    {
        (_, Recorder api) = Run(
            """
            code { Main$() { SetGameVariableInt("a", 2 + 3 * 4 - 1); } }
            """,
            Signatures(("SetGameVariableInt", SheepSignatures.Void,
                [SheepSignatures.String, SheepSignatures.Int])));

        Assert.Equal(13, Assert.Single(api.Calls).Arguments[1].AsInt());
    }

    [Fact]
    public void Mixing_a_whole_number_with_a_float_converts_the_right_one()
    {
        // The conversion instruction's operand is how far down the stack to reach, not a
        // value. An expression with the int on the left needs a reach of one, because the
        // float is already sitting on top of it — which is the whole reason this is worth
        // a test of its own.
        (_, Recorder api) = Run(
            """
            code { Main$() { Take(1 + 0.5); Take(0.5 + 1); } }
            """,
            Signatures(("Take", SheepSignatures.Void, [SheepSignatures.Float])));

        Assert.Equal(1.5f, api.Calls[0].Arguments[0].AsFloat(), 5);
        Assert.Equal(1.5f, api.Calls[1].Arguments[0].AsFloat(), 5);
    }

    [Fact]
    public void A_condition_chooses_between_the_two_branches()
    {
        SheepSignatures signatures = Signatures(
            ("GetFlag", SheepSignatures.Int, [SheepSignatures.String]),
            ("SetFlag", SheepSignatures.Void, [SheepSignatures.String]));

        const string Source =
            """
            code
            {
                Main$()
                {
                    if (GetFlag("open")) { SetFlag("wasOpen"); }
                    else { SetFlag("wasShut"); }
                }
            }
            """;

        (_, Recorder open) = Run(Source, signatures, ("GetFlag", SheepValue.FromInt(1)));
        (_, Recorder shut) = Run(Source, signatures, ("GetFlag", SheepValue.FromInt(0)));

        Assert.Equal("wasOpen", open.Calls[1].Arguments[0].AsString());
        Assert.Equal("wasShut", shut.Calls[1].Arguments[0].AsString());
    }

    [Fact]
    public void Both_spellings_of_not_equal_mean_the_same_thing()
    {
        // The grammar has != and <>, and content uses both.
        (_, Recorder api) = Run(
            """
            code { Main$() { Take(1 != 2); Take(1 <> 2); Take(2 != 2); } }
            """,
            Signatures(("Take", SheepSignatures.Void, [SheepSignatures.Int])));

        Assert.Equal(1, api.Calls[0].Arguments[0].AsInt());
        Assert.Equal(1, api.Calls[1].Arguments[0].AsInt());
        Assert.Equal(0, api.Calls[2].Arguments[0].AsInt());
    }

    [Fact]
    public void A_symbol_holds_a_value_across_statements()
    {
        (_, Recorder api) = Run(
            """
            symbols { int count = 2; }
            code
            {
                Main$()
                {
                    count = count + 5;
                    Take(count);
                }
            }
            """,
            Signatures(("Take", SheepSignatures.Void, [SheepSignatures.Int])));

        Assert.Equal(7, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void A_goto_jumps_backwards_to_its_label()
    {
        (SheepThread thread, Recorder api) = Run(
            """
            symbols { int n = 0; }
            code
            {
                Main$()
                {
                    top$:
                    n = n + 1;
                    Take(n);
                    if (n < 3) { goto top$; }
                }
            }
            """,
            Signatures(("Take", SheepSignatures.Void, [SheepSignatures.Int])));

        Assert.Equal(SheepThreadState.Completed, thread.State);
        Assert.Equal([1, 2, 3], api.Calls.Select(c => c.Arguments[0].AsInt()));
    }

    [Fact]
    public void A_wait_block_leaves_the_thread_waiting_on_what_is_inside_it()
    {
        // The whole point of the construct, and the thing the scheduler is built around.
        SheepScriptFile script = SheepCompiler.Compile(
            """
            code { Main$() { wait StartAnimation("GraCs3WrdbOpen"); SetFlag("done"); } }
            """,
            "TEST.SHP",
            Signatures(
                ("StartAnimation", SheepSignatures.Void, [SheepSignatures.String]),
                ("SetFlag", SheepSignatures.Void, [SheepSignatures.String])));

        var api = new WaitingApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Blocked, thread.State);
        Assert.Equal("StartAnimation", Assert.Single(api.Calls).Name);
    }

    /// <summary>An API on which one function can be waited.</summary>
    private sealed class WaitingApi : ISheepApi
    {
        public List<SheepCall> Calls { get; } = [];

        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
        {
            Calls.Add(new SheepCall(name, arguments, false));
            return SheepValue.FromInt(0);
        }

        public bool IsWaitable(string name) =>
            string.Equals(name, "StartAnimation", StringComparison.OrdinalIgnoreCase);

        public double SecondsFor(string name, IReadOnlyList<SheepValue> arguments) => 2.0;
    }

    [Fact]
    public void A_wait_over_a_group_covers_every_call_in_it()
    {
        SheepScriptFile script = SheepCompiler.Compile(
            """
            code
            {
                Main$()
                {
                    wait
                    {
                        StartAnimation("one");
                        StartAnimation("two");
                    }
                }
            }
            """,
            "TEST.SHP",
            Signatures(("StartAnimation", SheepSignatures.Void, [SheepSignatures.String])));

        var api = new WaitingApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Blocked, thread.State);
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public void Every_function_is_separately_callable()
    {
        SheepScriptFile script = SheepCompiler.Compile(
            """
            code
            {
                First$()  { Take(1); }
                Second$() { Take(2); }
            }
            """,
            "TEST.SHP",
            Signatures(("Take", SheepSignatures.Void, [SheepSignatures.Int])));

        Assert.Equal(["First$", "Second$"], script.Functions.Select(f => f.Name));

        var api = new Recorder();
        new SheepVirtualMachine(api).Execute(script, "Second$");

        Assert.Equal(2, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void A_function_ends_with_a_return_and_four_halts()
    {
        // Not a stylistic choice: the corpus has 1,481 returns and 5,924 halts, which is
        // four to one exactly, and the next function starts after them.
        SheepScriptFile script = SheepCompiler.Compile(
            "code { First$() { } Second$() { } }", "TEST.SHP");

        Assert.Equal(0, script.Functions[0].Offset);
        Assert.Equal(1 + SheepCompiler.FunctionPadding, script.Functions[1].Offset);
        Assert.Equal((byte)SheepOpcode.ReturnV, script.Bytecode[0]);

        for (int i = 1; i <= SheepCompiler.FunctionPadding; i++)
        {
            Assert.Equal((byte)SheepOpcode.SitnSpin, script.Bytecode[i]);
        }
    }

    [Fact]
    public void The_same_string_is_stored_once()
    {
        SheepScriptFile script = SheepCompiler.Compile(
            """
            code { Main$() { Take("gab"); Take("gab"); Take("gra"); } }
            """,
            "TEST.SHP",
            Signatures(("Take", SheepSignatures.Void, [SheepSignatures.String])));

        // Three, not two: the pool always opens with an empty string, which is what puts
        // the first real one at offset one in every script the game ships.
        Assert.Equal(3, script.StringConstants.Count);
        Assert.Equal(
            ["", "gab", "gra"],
            script.StringConstants.OrderBy(e => e.Key).Select(e => e.Value));

        Assert.Equal(1, script.StringConstants.Keys.Order().ElementAt(1));
    }

    [Fact]
    public void Comments_and_case_and_underscores_are_all_taken_as_written()
    {
        // Identifiers are case-insensitive and underscore counts as a letter; comments come
        // in two forms and do not nest.
        (_, Recorder api) = Run(
            """
            symbols { int _n = 4; }
            code
            {
                MAIN$()   // the entry point
                {
                    /* the name below is the same symbol as _n */
                    Take(_N);
                }
            }
            """,
            Signatures(("Take", SheepSignatures.Void, [SheepSignatures.Int])));

        Assert.Equal(4, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void A_goto_with_no_label_is_refused_and_says_which_one()
    {
        FormatParseException failed = Assert.Throws<FormatParseException>(() =>
            SheepCompiler.Compile("code { Main$() { goto nowhere$; } }", "TEST.SHP"));

        Assert.Contains("nowhere$", failed.Diagnostic.Expected ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undeclared_symbol_is_refused_rather_than_read_as_zero()
    {
        Assert.Throws<FormatParseException>(() =>
            SheepCompiler.Compile("code { Main$() { Take(missing); } }", "TEST.SHP"));
    }

    [Fact]
    public void Arithmetic_on_a_string_is_refused()
    {
        // There is no instruction for it. Compiling it as an integer add would compare two
        // hashes and answer confidently.
        Assert.Throws<FormatParseException>(() =>
            SheepCompiler.Compile(
                """code { Main$() { Take("a" + "b"); } }""", "TEST.SHP"));
    }

    [Fact]
    public void Source_that_is_not_a_script_says_where_it_stopped()
    {
        FormatParseException failed = Assert.Throws<FormatParseException>(() =>
            SheepCompiler.Compile("code { Main$() { if ( } }", "TEST.SHP"));

        Assert.Equal("GK3R1081", failed.Diagnostic.Code);
    }

    [Fact]
    public void A_compiled_script_survives_being_written_out_and_read_back()
    {
        // The round trip is the check on the container: the length-prefixed strings with
        // their trailing byte, the section table, the offsets relative to the header. Any
        // of those misread shows up here rather than in a disassembly nobody compares.
        SheepScriptFile compiled = SheepCompiler.Compile(
            """
            symbols { int count = 7; float pace = 1.5; string who = "gab"; }
            code
            {
                Main$()
                {
                    wait StartAnimation("GabWalk");
                    count = count + 1;
                    if (count > 3) { SetFlag("plenty"); }
                }
            }
            """,
            "TEST.SHP",
            Signatures(
                ("StartAnimation", SheepSignatures.Void, [SheepSignatures.String]),
                ("SetFlag", SheepSignatures.Void, [SheepSignatures.String])));

        SheepScriptFile again = SheepScriptFile.Parse(SheepScriptWriter.Write(compiled), "TEST.SHP");

        Assert.Equal(compiled.Bytecode, again.Bytecode);
        Assert.Equal(
            compiled.Imports.Select(SheepSignatures.Describe),
            again.Imports.Select(SheepSignatures.Describe));

        Assert.Equal(compiled.Functions, again.Functions);
        Assert.Equal(
            compiled.StringConstants.OrderBy(e => e.Key),
            again.StringConstants.OrderBy(e => e.Key));

        Assert.Equal(compiled.Variables, again.Variables);
    }
}

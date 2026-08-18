using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Sheep;

public sealed class SheepVirtualMachineTests
{
    /// <summary>Builds a compiled script, so the VM is tested against real file layout.</summary>
    private sealed class ScriptBuilder
    {
        private readonly List<(string Name, sbyte Return, sbyte[] Args)> _imports = [];
        private readonly List<string> _strings = [];
        private readonly List<(string Name, SheepValueKind Kind, int Int, float Float)> _variables = [];
        private readonly List<(string Name, int Offset)> _functions = [];
        private readonly List<byte> _code = [];

        public ScriptBuilder Import(string name, sbyte returnType = 0, params sbyte[] arguments)
        {
            _imports.Add((name, returnType, arguments));
            return this;
        }

        public int String(string value)
        {
            int offset = _strings.Sum(s => s.Length + 1);
            _strings.Add(value);
            return offset;
        }

        public ScriptBuilder Variable(string name, SheepValueKind kind, int intValue = 0, float floatValue = 0)
        {
            _variables.Add((name, kind, intValue, floatValue));
            return this;
        }

        public ScriptBuilder Function(string name)
        {
            _functions.Add((name, _code.Count));
            return this;
        }

        public int Here => _code.Count;

        public ScriptBuilder Op(SheepOpcode opcode)
        {
            _code.Add((byte)opcode);
            return this;
        }

        public ScriptBuilder Op(SheepOpcode opcode, int operand)
        {
            _code.Add((byte)opcode);
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, operand);
            _code.AddRange(bytes.ToArray());
            return this;
        }

        public ScriptBuilder OpF(SheepOpcode opcode, float operand)
        {
            _code.Add((byte)opcode);
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, operand);
            _code.AddRange(bytes.ToArray());
            return this;
        }

        /// <summary>Patches a previously emitted operand, for forward branches.</summary>
        public ScriptBuilder Patch(int instructionAddress, int operand)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, operand);
            for (int i = 0; i < 4; i++)
            {
                _code[instructionAddress + 1 + i] = bytes[i];
            }

            return this;
        }

        public SheepScriptFile Build()
        {
            var sections = new List<(string Name, byte[] Data)>
            {
                ("SysImports", BuildImports()),
                ("StringConsts", BuildStrings()),
                ("Variables", BuildVariables()),
                ("Functions", BuildFunctions()),
                ("Code", BuildCode()),
            };

            var header = new MemoryStream();
            var w = new BinaryWriter(header);
            w.Write("GK3Sheep"u8);
            w.Write(0u);

            int headerSize = 8 + 4 + 4 + 8 + 4 + (sections.Count * 4);
            w.Write(headerSize);
            w.Write(headerSize);
            w.Write(0);
            w.Write(sections.Count);

            int running = 0;
            foreach ((string _, byte[] data) in sections)
            {
                w.Write(running);
                running += data.Length;
            }

            foreach ((string _, byte[] data) in sections)
            {
                w.Write(data);
            }

            w.Flush();
            return SheepScriptFile.Parse(header.ToArray(), "TEST.SHP");
        }

        private static void WriteName(BinaryWriter w, string name)
        {
            // Two bytes of length, the bytes, then one more.
            w.Write((ushort)name.Length);
            w.Write(Encoding.ASCII.GetBytes(name));
            w.Write((byte)0);
        }

        private static byte[] Section(string name, Action<BinaryWriter> body)
        {
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            byte[] tag = new byte[12];
            Encoding.ASCII.GetBytes(name).CopyTo(tag, 0);
            w.Write(tag);
            body(w);
            w.Flush();
            return stream.ToArray();
        }

        private byte[] BuildImports() => Section("SysImports", w =>
        {
            w.Write(new byte[12]);
            w.Write(_imports.Count);
            w.Write(new byte[4 * _imports.Count]);

            foreach ((string name, sbyte ret, sbyte[] args) in _imports)
            {
                WriteName(w, name);
                w.Write(ret);
                w.Write((sbyte)args.Length);
                foreach (sbyte a in args)
                {
                    w.Write(a);
                }
            }
        });

        private byte[] BuildStrings() => Section("StringConsts", w =>
        {
            byte[] block = Encoding.ASCII.GetBytes(
                string.Concat(_strings.Select(s => s + "\0")));

            w.Write(new byte[8]);
            w.Write(block.Length);
            w.Write(_strings.Count);

            int offset = 0;
            foreach (string s in _strings)
            {
                w.Write(offset);
                offset += s.Length + 1;
            }

            w.Write(block);
        });

        private byte[] BuildVariables() => Section("Variables", w =>
        {
            w.Write(new byte[12]);
            w.Write(_variables.Count);
            w.Write(new byte[4 * _variables.Count]);

            foreach ((string name, SheepValueKind kind, int i, float f) in _variables)
            {
                WriteName(w, name);
                switch (kind)
                {
                    case SheepValueKind.Int:
                        w.Write(1);
                        w.Write(i);
                        break;
                    case SheepValueKind.Float:
                        w.Write(2);
                        w.Write(f);
                        break;
                    default:
                        w.Write(3);
                        w.Write(0);
                        break;
                }
            }
        });

        private byte[] BuildFunctions() => Section("Functions", w =>
        {
            w.Write(new byte[12]);
            w.Write(_functions.Count);
            w.Write(new byte[4 * _functions.Count]);

            foreach ((string name, int offset) in _functions)
            {
                WriteName(w, name);
                w.Write((ushort)0);
                w.Write(offset);
            }
        });

        private byte[] BuildCode() => Section("Code", w =>
        {
            w.Write(new byte[8]);
            w.Write(_code.Count);
            w.Write(1);
            w.Write(0);
            w.Write(_code.ToArray());
        });
    }

    /// <summary>An API that answers with whatever the test tells it to.</summary>
    private sealed class StubApi(int result = 0, params string[] waitable) : ISheepApi
    {
        public List<SheepCall> Calls { get; } = [];

        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
        {
            Calls.Add(new SheepCall(name, arguments, false));
            return SheepValue.FromInt(result);
        }

        public bool IsWaitable(string name) => waitable.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_call_receives_its_arguments_in_order()
    {
        var builder = new ScriptBuilder().Import("SetFlag", 0, 3, 1);
        int offset = builder.String("noon");

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushS, offset)
            .Op(SheepOpcode.GetString)
            .Op(SheepOpcode.PushI, 42)
            .Op(SheepOpcode.PushI, 2)          // argument count
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Completed, thread.State);
        SheepCall call = Assert.Single(api.Calls);
        Assert.Equal("SetFlag", call.Name);
        Assert.Equal("noon", call.Arguments[0].AsString());
        Assert.Equal(42, call.Arguments[1].AsInt());
    }

    [Fact]
    public void A_void_call_still_pushes_a_result()
    {
        // The original compiler emits a Pop after every void call, so a VM that pushed
        // nothing would drift the stack by one on each call.
        SheepScriptFile script = new ScriptBuilder()
            .Import("DoThing")
            .Function("Main$")
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Completed, thread.State);
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public void Arithmetic_and_comparison_work()
    {
        var builder = new ScriptBuilder().Import("Report", 0, 1);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushI, 7)
            .Op(SheepOpcode.PushI, 3)
            .Op(SheepOpcode.SubtractI)     // 4
            .Op(SheepOpcode.PushI, 5)
            .Op(SheepOpcode.MultiplyI)     // 20
            .Op(SheepOpcode.PushI, 1)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(20, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void Division_by_zero_yields_zero_rather_than_taking_the_engine_down()
    {
        // Scripts come from data the project does not control.
        var builder = new ScriptBuilder().Import("Report", 0, 1);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushI, 10)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.DivideI)
            .Op(SheepOpcode.PushI, 1)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Completed, thread.State);
        Assert.Equal(0, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void BranchIfZero_takes_the_branch_only_when_the_test_is_false()
    {
        var builder = new ScriptBuilder().Import("Taken").Import("Skipped");

        builder.Function("Main$").Op(SheepOpcode.PushI, 0);
        int branch = builder.Here;
        builder.Op(SheepOpcode.BranchIfZero, 0);
        builder.Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 1).Op(SheepOpcode.Pop);
        int target = builder.Here;
        builder.Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 0).Op(SheepOpcode.Pop);
        builder.Op(SheepOpcode.ReturnV);
        builder.Patch(branch, target);

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(builder.Build(), "Main$");

        Assert.Equal(["Taken"], api.Calls.Select(c => c.Name));
    }

    [Fact]
    public void Variables_start_at_their_declared_values_and_round_trip()
    {
        var builder = new ScriptBuilder()
            .Import("Report", 0, 1)
            .Variable("count$", SheepValueKind.Int, intValue: 5);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.LoadI, 0)
            .Op(SheepOpcode.PushI, 3)
            .Op(SheepOpcode.AddI)
            .Op(SheepOpcode.StoreI, 0)
            .Op(SheepOpcode.LoadI, 0)
            .Op(SheepOpcode.PushI, 1)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(8, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void A_wait_block_suspends_only_when_it_called_something_waitable()
    {
        var builder = new ScriptBuilder().Import("WalkTo").Import("SetFlag");

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.EndWait)
            .Op(SheepOpcode.ReturnV)
            .Build();

        SheepThread waiting = new SheepVirtualMachine(new StubApi(0, "WalkTo")).Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Blocked, waiting.State);
        Assert.True(Assert.Single(waiting.Calls).Waited);

        SheepThread notWaiting = new SheepVirtualMachine(new StubApi()).Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Completed, notWaiting.State);
    }

    [Fact]
    public void A_blocked_thread_resumes_where_it_stopped()
    {
        var builder = new ScriptBuilder().Import("WalkTo").Import("Arrived");

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.EndWait)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 1)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi(0, "WalkTo");
        var vm = new SheepVirtualMachine(api);

        SheepThread thread = vm.Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Blocked, thread.State);
        Assert.Equal(["WalkTo"], api.Calls.Select(c => c.Name));

        vm.Resume(thread);
        Assert.Equal(SheepThreadState.Completed, thread.State);
        Assert.Equal(["WalkTo", "Arrived"], api.Calls.Select(c => c.Name));
    }

    [Fact]
    public void SitnSpin_halts_deliberately()
    {
        SheepScriptFile script = new ScriptBuilder()
            .Function("Main$")
            .Op(SheepOpcode.SitnSpin)
            .Build();

        SheepThread thread = new SheepVirtualMachine(new StubApi()).Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Halted, thread.State);
    }

    [Fact]
    public void An_endless_loop_faults_instead_of_hanging()
    {
        var builder = new ScriptBuilder();
        builder.Function("Main$");
        int top = builder.Here;
        builder.Op(SheepOpcode.Branch, top);

        SheepThread thread = new SheepVirtualMachine(new StubApi(), instructionLimit: 500)
            .Execute(builder.Build(), "Main$");

        Assert.Equal(SheepThreadState.Faulted, thread.State);
        Assert.Equal("GK3R3101", thread.Diagnostics.Items[0].Code);
    }

    [Fact]
    public void A_branch_past_the_end_faults_rather_than_reading_wild_memory()
    {
        SheepScriptFile script = new ScriptBuilder()
            .Function("Main$")
            .Op(SheepOpcode.Branch, 9999)
            .Build();

        SheepThread thread = new SheepVirtualMachine(new StubApi()).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Faulted, thread.State);
        Assert.Equal("GK3R3102", thread.Diagnostics.Items[0].Code);
    }

    [Fact]
    public void Function_names_are_matched_case_insensitively()
    {
        // The language specification says upper and lower case are the same.
        SheepScriptFile script = new ScriptBuilder()
            .Function("JeanTalk$")
            .Op(SheepOpcode.ReturnV)
            .Build();

        var vm = new SheepVirtualMachine(new StubApi());
        Assert.Equal(SheepThreadState.Completed, vm.Execute(script, "jeantalk$").State);
        Assert.Equal(SheepThreadState.Completed, vm.Execute(script, "JEANTALK$").State);
    }

    [Fact]
    public void Calling_a_function_that_does_not_exist_is_reported()
    {
        SheepScriptFile script = new ScriptBuilder()
            .Function("Main$")
            .Op(SheepOpcode.ReturnV)
            .Build();

        SheepThread thread = new SheepVirtualMachine(new StubApi()).Execute(script, "Missing$");

        Assert.Equal(SheepThreadState.Faulted, thread.State);
        Assert.Equal("GK3R3100", thread.Diagnostics.Items[0].Code);
    }

    [Fact]
    public void IToF_converts_the_value_the_operand_points_at()
    {
        // The operand says how far down the stack to reach, not that the top is meant.
        var builder = new ScriptBuilder().Import("Report", 0, 2, 1);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushI, 7)
            .Op(SheepOpcode.PushI, 9)
            .Op(SheepOpcode.IToF, 1)      // convert the 7, one below the top
            .Op(SheepOpcode.PushI, 2)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(script, "Main$");

        SheepCall call = Assert.Single(api.Calls);
        Assert.Equal(SheepValueKind.Float, call.Arguments[0].Kind);
        Assert.Equal(SheepValueKind.Int, call.Arguments[1].Kind);
    }
}

using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Sheep;
using GK3Reborn.Content;
using GK3Reborn.Audio;
using GK3Reborn.Formats.Audio;
using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Story;
using System.Text.RegularExpressions;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>Validates the entire installed script corpus, not just selected scenes.</summary>
public sealed class ScriptCorpusTests
{
    // Reviewed unresolved original content. This is a visible backlog, not a claim
    // that these rules work. See docs/script-state-audit.txt for reasons and impact.
    private static readonly string[] UnresolvedCases =
    [
        "CHU104P.NVC:NOT_KNOW_ABBE_IN_OFFICE",
        "DIN106P.NVC:GET_CLOSE",
        "GLB_ALL.NVC:GABE_DECLINE",
        "GLB_ALL.NVC:G_ALREADY_HAVE",
        "INV102P.NVC:GOT_STONE_FROM_FIRE",
        "INV102P.NVC:NOT_GOT_STONE_FROM_FIRE",
        "INV_ALL.NVC:GOT_ANY_CLUE_NOTE",
        "INV_ALL.NVC:KNOW_EMILIOS_STORY",
        "INV_ALL.NVC:READ_HBHG_BOOK",
        "LBY_ALL.NVC:G_SIMONE_RAND_1",
        "PLO312P.NVC:SITE_MARKED_ON_MAP",
        "R27210A.NVC:MAID_CLEAN_BEDROOM_KNOCK",
        "R27210A.NVC:MAID_CLEAN_BEDROOM_PICK",
        "TE4309P.NVC:DOOR_CLOSED",
    ];

    [Fact]
    public void No_new_undefined_conditions_in_the_rooms_that_load_them()
    {
        string root = ContentRoot();
        var files = Directory.GetFiles(Path.Combine(root, "actions"), "*.NVC")
            .ToDictionary(path => Path.GetFileName(path), path => NvcFile.Parse(File.ReadAllText(path), Path.GetFileName(path), new()), StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string room in Directory.GetDirectories(Path.Combine(root, "scenes")))
        {
            string code = Path.GetFileName(room);
            string generalPath = Path.Combine(room, code + ".SIF");
            SceneInitFile? general = File.Exists(generalPath) ? SceneInitFile.Parse(File.ReadAllText(generalPath), Path.GetFileName(generalPath)) : null;
            foreach (string block in TimeblockRules.Known.Append("309P"))
            {
                Assert.True(Timeblock.TryParse(block, out Timeblock time));
                string specificPath = Path.Combine(room, code + block + ".SIF");
                SceneInitFile? specific = File.Exists(specificPath) ? SceneInitFile.Parse(File.ReadAllText(specificPath), Path.GetFileName(specificPath)) : null;
                if (general is null && specific is null)
                {
                    continue;
                }
                NvcFile[] scope = ActionSets.For(new SceneDefinition(general, specific), time)
                    .Where(files.ContainsKey).Select(name => files[name]).ToArray();
                HashSet<string> cases = new(NvcFile.BuiltInCases, StringComparer.OrdinalIgnoreCase);
                cases.UnionWith(scope.SelectMany(file => file.Cases.Keys));
                foreach (NvcFile file in scope)
                {
                    foreach (NvcAction action in file.Actions.Where(action => !cases.Contains(action.Case)))
                    {
                        missing.Add($"{file.Name}:{action.Case}");
                    }
                }
            }
        }
        Assert.Equal(UnresolvedCases.Order(StringComparer.Ordinal), missing);
    }

    [Fact]
    public void Every_compiled_script_import_has_a_handler()
    {
        string root = ContentRoot();
        var api = new Gk3SheepApi(new GameState());
        _ = new ScriptHost(api);
        var scene = new LoadedScene("TEST", new SceneDefinition(
            GK3Reborn.Formats.Scenes.SceneInitFile.Parse("[GENERAL]", "TEST.SIF")), null, null, 0);
        var glances = new GK3Reborn.Game.Actors.Glances();
        var world = new SceneUpdate(scene, api, glances, new GK3Reborn.Rendering.HeadlessSceneSink());
        using var device = new SilentDevice();
        var audio = new SceneAudio(new SoundLibrary(_ => null), new AnimationLibrary(_ => null), device);
        SceneScripting.Attach(api, scene, glances: glances, world: world, audio: audio);
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        string[] paths = Directory.GetFiles(Path.Combine(root, "scripts"), "*.SHP");
        Assert.True(paths.Length >= 224);
        foreach (string path in paths)
        {
            SheepScriptFile script = SheepScriptFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
            IReadOnlyList<SheepInstruction> instructions = SheepDisassembler.Decode(script);
            if (instructions.Count > 0)
            {
                SheepInstruction last = instructions[^1];
                Assert.Equal(script.Bytecode.Length, last.Address + (last.Operand.HasValue ? 5 : 1));
            }
            foreach (SheepImport import in script.Imports)
            {
                if (!api.KnowsFunction(import.Name))
                {
                    errors.Add($"{script.Name}: {import.Name}");
                }
            }
        }
        foreach (string path in Directory.GetFiles(Path.Combine(root, "actions"), "*.NVC"))
        {
            NvcFile file = NvcFile.Parse(File.ReadAllText(path), Path.GetFileName(path), new());
            foreach (string text in file.Actions.Select(action => action.Script ?? string.Empty).Concat(file.Cases.Values))
            {
                foreach (Match call in Regex.Matches(text, @"\b([A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant))
                {
                    if (!api.KnowsFunction(call.Groups[1].Value))
                    {
                        errors.Add($"{file.Name}: {call.Groups[1].Value}");
                    }
                }
            }
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Every_scene_condition_parses()
    {
        string[] paths = Directory.GetFiles(Path.Combine(ContentRoot(), "scenes"), "*.SIF", SearchOption.AllDirectories);
        Assert.True(paths.Length >= 572);
        foreach (string path in paths)
        {
            SceneInitFile.Parse(File.ReadAllText(path), Path.GetFileName(path), expression =>
            {
                if (!string.IsNullOrWhiteSpace(expression))
                {
                    SheepExpression.Evaluate(expression, new Inert());
                }
                return true;
            });
        }
    }

    internal static string ContentRoot()
    {
        string? root = Environment.GetEnvironmentVariable("GK3_NORMALIZED_CONTENT");
        for (DirectoryInfo? dir = new(Environment.CurrentDirectory); root is null && dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "ContentWorkspace", "normalized");
            if (Directory.Exists(candidate))
            {
                root = candidate;
            }
        }
        Assert.SkipUnless(root is not null && Directory.Exists(root), "needs GK3_NORMALIZED_CONTENT or ContentWorkspace/normalized");
        return root!;
    }

    [Fact]
    public void Every_action_and_case_parses_without_new_undefined_cases()
    {
        string root = ContentRoot();
        var diagnostics = new DiagnosticBag();
        NvcFile[] files = Directory.GetFiles(Path.Combine(root, "actions"), "*.NVC")
            .Select(path => NvcFile.Parse(File.ReadAllText(path), Path.GetFileName(path), diagnostics)).ToArray();
        Assert.True(files.Length >= 390);
        HashSet<string> cases = new(NvcFile.BuiltInCases, StringComparer.OrdinalIgnoreCase);
        cases.UnionWith(files.SelectMany(file => file.Cases.Keys));
        var errors = new List<string>();
        var runner = new ActionRunner(new Gk3SheepApi(new GameState()));
        var variables = new Dictionary<string, SheepValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["n$"] = SheepValue.FromString("NOUN"), ["v$"] = SheepValue.FromString("VERB"),
        };
        foreach (NvcFile file in files)
        {
            foreach (NvcAction action in file.Actions)
            {
                if (!cases.Contains(action.Case) && !UnresolvedCases.Contains(file.Name + ":" + action.Case, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{action.Source}: undefined case {action.Case}");
                }
                if (runner.Read(action) is null)
                {
                    errors.Add($"{action.Source}: unreadable action");
                }
            }
            foreach ((string name, string expression) in file.Cases)
            {
                try
                {
                    SheepExpression.Evaluate(expression, new Inert(), variables);
                }
                catch (GK3Reborn.Formats.FormatParseException error)
                {
                    errors.Add($"{file.Name}:{name}: {error.Message}");
                }
            }
        }
        Assert.Empty(diagnostics.Items);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private sealed class Inert : ISheepApi
    {
        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments) => SheepValue.FromInt(0);
        public bool IsWaitable(string name) => false;
    }

    private sealed class SilentDevice : IAudioBackend
    {
        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;
        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;
        public int Playing => 0;
        public AudioVoice Play(WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null) => AudioVoice.None;
        public bool IsPlaying(AudioVoice voice) => false;
        public void SetBusGain(AudioBus bus, float gain) { }
        public void SetVoiceGain(AudioVoice voice, float gain) { }
        public void Move(AudioVoice voice, Vector3 position) { }
        public void Listen(Vector3 position, Vector3 forward, Vector3 up) { }
        public void Silence(AudioVoice voice) { }
        public void StopBus(AudioBus bus) { }
        public void Update() { }
        public void Dispose() { }
    }
}

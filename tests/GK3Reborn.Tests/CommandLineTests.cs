using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace GK3Reborn.Tests;

/// <summary>
/// What the executable accepts after its name, and the usage text that says so.
/// </summary>
/// <remarks>
/// The switches are read where they are used, scattered through <see cref="Application"/>,
/// and the usage text is written in one place by hand. The two drift apart unless
/// something holds them together, so the last test here reads the source and checks
/// that every switch it reads is one <c>--help</c> mentions, and the other way round.
/// </remarks>
public sealed partial class CommandLineTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("--HELP")]
    public void Every_spelling_of_help_asks_for_it(string spelling) =>
        Assert.True(CommandLine.WantsHelp(["--scene", "R25", spelling]));

    [Fact]
    public void A_run_with_no_help_switch_is_a_run() =>
        Assert.False(CommandLine.WantsHelp(["--scene", "R25", "--rt", "high"]));

    [Fact]
    public void A_switch_takes_the_word_after_it() =>
        Assert.Equal("R25", CommandLine.Value(["--scene", "R25"], "--scene"));

    [Fact]
    public void The_next_switch_is_not_a_value() =>
        Assert.Null(CommandLine.Value(["--start", "--rt", "high"], "--start"));

    [Fact]
    public void A_switch_at_the_end_has_no_value() =>
        Assert.Null(CommandLine.Value(["--scene"], "--scene"));

    [Fact]
    public void No_backend_switch_asks_for_nothing() =>
        Assert.Null(CommandLine.BackendAsked(["--scene", "R25"]));

    [Fact]
    public void Backend_names_the_api() =>
        Assert.Equal("d3d12", CommandLine.BackendAsked(["--backend", "d3d12"]));

    [Theory]
    [InlineData("--vulkan")]
    [InlineData("-vulkan")]
    [InlineData("--vk")]
    [InlineData("--Vulkan")]
    public void The_vulkan_shorthands_ask_for_vulkan(string spelling) =>
        Assert.Equal("vulkan", CommandLine.BackendAsked([spelling]));

    [Theory]
    [InlineData("--d3d12")]
    [InlineData("-d3d12")]
    [InlineData("--dx12")]
    [InlineData("-dx12")]
    public void The_direct3d_shorthands_ask_for_direct3d(string spelling) =>
        Assert.Equal("d3d12", CommandLine.BackendAsked([spelling]));

    [Fact]
    public void Backend_outranks_a_shorthand() =>
        Assert.Equal("vulkan", CommandLine.BackendAsked(["--d3d12", "--backend", "vulkan"]));

    [Fact]
    public void A_misspelt_backend_is_handed_on_rather_than_resolved() =>
        Assert.Equal("dx11", CommandLine.BackendAsked(["--backend", "dx11"]));

    [Fact]
    public void The_usage_names_the_program_and_ends_in_a_newline()
    {
        string usage = CommandLine.Usage();

        Assert.StartsWith("GK3Reborn", usage, StringComparison.Ordinal);
        Assert.EndsWith("\n", usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every switch the engine reads is in the usage text, and every switch the usage text
    /// mentions is one the engine reads.
    /// </summary>
    [Fact]
    public void The_usage_and_the_source_agree_about_every_switch()
    {
        string engine = Path.Combine(RepositoryRoot(), "src", "GK3Reborn.Engine");
        string application = File.ReadAllText(Path.Combine(engine, "Application.cs"));
        string commandLine = File.ReadAllText(Path.Combine(engine, "CommandLine.cs"));

        HashSet<string> read = SwitchLiterals().Matches(application)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> accepted = SwitchLiterals().Matches(commandLine)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Concat(read)
            .ToHashSet(StringComparer.Ordinal);

        // Only the lines that introduce a switch, which start with two spaces and the
        // switch itself, and only the column the switches sit in: the description begins
        // at column 24, and a switch named inside one — "--name R25 takes the room's
        // files" — is an example rather than an entry.
        HashSet<string> documented = CommandLine.Usage()
            .Split('\n')
            .Where(line => line.StartsWith("  --", StringComparison.Ordinal))
            .Select(line => line[..Math.Min(line.Length, DescriptionColumn)])
            .SelectMany(entry => SwitchesIn().Matches(entry).Select(m => m.Value.ToLowerInvariant()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(read.Count > 40, $"expected the engine to read many switches, found {read.Count}");

        string[] undocumented = read.Except(documented).Order().ToArray();
        Assert.True(
            undocumented.Length == 0,
            "read by Application.cs and missing from --help: " + string.Join(", ", undocumented));

        string[] stale = documented.Except(accepted).Order().ToArray();
        Assert.True(
            stale.Length == 0,
            "in --help and read by nothing: " + string.Join(", ", stale));
    }

    private static string RepositoryRoot() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "RepositoryRoot")
            .Value!;

    /// <summary>Where a usage entry's description starts, counted from the line's start.</summary>
    private const int DescriptionColumn = 24;

    /// <summary>A quoted switch in C# source: <c>"--scene"</c>.</summary>
    [GeneratedRegex("\"(--[a-z][a-z0-9-]*)\"")]
    private static partial Regex SwitchLiterals();

    /// <summary>A switch anywhere in a line of the usage text.</summary>
    [GeneratedRegex("--[a-z][a-z0-9-]*")]
    private static partial Regex SwitchesIn();
}

using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace GK3Reborn.Tests.Architecture;

/// <summary>
/// Enforces the engine's internal layering.
/// </summary>
/// <remarks>
/// The engine is one assembly (ADR 0005), so the compiler no longer refuses a bad
/// reference the way a project graph did. These tests take that job over: they read the
/// engine's own source files and check which namespaces each area is allowed to reach.
/// Source inspection rather than IL inspection keeps this dependency-free and makes a
/// failure point straight at the offending <c>using</c>.
/// </remarks>
public sealed partial class LayeringTests
{
    // area -> namespaces under GK3Reborn that area may NOT reference.
    private static readonly (string Area, string[] Forbidden)[] Rules =
    [
        // Parsers must stay usable from tools and headless tests. Nothing about
        // rendering, audio, UI or game rules belongs in them.
        ("Formats", ["Rendering", "UI", "Game", "Audio", "Video", "Platform"]),

        // Foundation is the base of everything and depends on nothing above it.
        ("Foundation", ["Formats", "Content", "Rendering", "UI", "Game", "Audio", "Video", "Platform", "Sheep"]),

        // Content addresses assets; it must not know how they are drawn or played.
        ("Content", ["Rendering", "UI", "Game", "Audio", "Video", "Platform"]),

        // The scripting VM is a compatibility boundary, not a consumer of subsystems.
        ("Sheep", ["Rendering", "UI", "Audio", "Video", "Platform"]),

        // Game state reaches presentation only through interfaces in those namespaces,
        // never through the Vulkan backend directly.
        ("Game", ["Rendering.Vulkan"]),

        // Rendering is backend-neutral; only Rendering/Vulkan may use Silk.NET.Vulkan.
        ("Rendering", []),
    ];

    private static string EngineRoot
    {
        get
        {
            string repository = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "RepositoryRoot")
                .Value!;

            return Path.Combine(repository, "src", "GK3Reborn.Engine");
        }
    }

    [Fact]
    public void The_engine_source_tree_is_where_the_test_expects_it() =>
        Assert.True(Directory.Exists(EngineRoot), $"engine sources not found at {EngineRoot}");

    [Fact]
    public void Areas_do_not_reference_namespaces_above_them()
    {
        List<string> violations = [];

        foreach ((string area, string[] forbidden) in Rules)
        {
            string directory = Path.Combine(EngineRoot, area.Replace('.', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                foreach (string used in UsingsIn(file))
                {
                    foreach (string bad in forbidden)
                    {
                        if (used == $"GK3Reborn.{bad}" || used.StartsWith($"GK3Reborn.{bad}.", StringComparison.Ordinal))
                        {
                            violations.Add($"{Path.GetRelativePath(EngineRoot, file)} uses {used}");
                        }
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Only_the_vulkan_backend_uses_vulkan()
    {
        string vulkanBackend = Path.Combine("Rendering", "Vulkan");
        List<string> violations = [];

        foreach (string file in Directory.EnumerateFiles(EngineRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(EngineRoot, file);
            if (relative.StartsWith(vulkanBackend, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (UsingsIn(file).Any(u => u.StartsWith("Silk.NET.Vulkan", StringComparison.Ordinal)))
            {
                violations.Add(relative);
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Engine_code_does_not_use_ambient_randomness()
    {
        // ADR 0004: DeterministicRandom is the only permitted source. Ambient randomness
        // in anything that touches game state breaks replay, saves and story traversal.
        List<string> violations = [];

        foreach (string file in Directory.EnumerateFiles(EngineRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "DeterministicRandom.cs")
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (AmbientRandomness().IsMatch(text))
            {
                violations.Add(Path.GetRelativePath(EngineRoot, file));
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<string> UsingsIn(string file)
    {
        foreach (string line in File.ReadLines(file))
        {
            Match match = UsingDirective().Match(line);
            if (match.Success)
            {
                yield return match.Groups["ns"].Value;
            }
        }
    }

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?<ns>[A-Za-z_][\w.]*)\s*;")]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"\bRandom\.Shared\b|\bnew\s+Random\s*\(")]
    private static partial Regex AmbientRandomness();
}

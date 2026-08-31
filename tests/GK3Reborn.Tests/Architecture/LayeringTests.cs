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
        // never through a graphics backend directly.
        ("Game", ["Rendering.Vulkan", "Rendering.Direct3D12"]),

        // Rendering is backend-neutral; only Rendering/Vulkan may use Silk.NET.Vulkan and
        // only Rendering/Direct3D12 may use Silk.NET.Direct3D12.
        ("Rendering", []),

        // The shader front end is the one thing both backends share, so it must know
        // neither. It compiles source to SPIR-V and on to DXIL through SPIRV-Cross and
        // DXC; none of those three is a graphics API and none of them needs a device.
        ("Rendering.Shaders", ["Rendering.Vulkan", "Rendering.Direct3D12"]),
    ];

    /// <summary>The graphics APIs, and the one directory each is allowed to appear in.</summary>
    /// <remarks>
    /// Two backends make this a rule rather than an observation. A Vulkan type reached from
    /// the shared rendering code compiles perfectly well and quietly makes the Direct3D
    /// path unbuildable on the day it is written, which is not the day anyone finds out.
    /// </remarks>
    private static readonly (string Namespace, string Directory)[] Backends =
    [
        ("Silk.NET.Vulkan", Path.Combine("Rendering", "Vulkan")),
        ("Silk.NET.Direct3D12", Path.Combine("Rendering", "Direct3D12")),
        ("Silk.NET.DXGI", Path.Combine("Rendering", "Direct3D12")),
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
    public void Only_a_backend_uses_its_own_graphics_api()
    {
        List<string> violations = [];

        foreach (string file in Directory.EnumerateFiles(EngineRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(EngineRoot, file);

            foreach ((string api, string directory) in Backends)
            {
                if (relative.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (UsingsIn(file).Any(u => u.StartsWith(api, StringComparison.Ordinal)))
                {
                    violations.Add($"{relative} uses {api}");
                }
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

    /// <summary>The four files allowed to open a Silk.NET native library.</summary>
    /// <remarks>
    /// Every other file borrows a handle from one of them. See
    /// <c>Rendering/Shaders/ShaderToolchain.cs</c> for why there is a rule at all.
    /// </remarks>
    private static readonly string[] MayOpenANativeLibrary =
    [
        Path.Combine("Rendering", "Shaders", "ShaderToolchain.cs"),
        Path.Combine("Rendering", "Direct3D12", "D3D12Runtime.cs"),
        Path.Combine("Rendering", "Vulkan", "VulkanContext.cs"),
        Path.Combine("Audio", "OpenAlBackend.cs"),
    ];

    [Fact]
    public void Only_the_four_holders_open_a_native_library()
    {
        // Silk.NET's GetApi opens the shared library and its Dispose closes it, and when the
        // last handle closes the library is unloaded. Calling the pair freely is what made
        // the Linux build map and unmap glslang dozens of times in a run and then die with
        // SIGSEGV after its last test had passed; Windows and macOS hide the same mistake.
        // So opening is confined to four files, each of which documents that what it hands
        // out is never released.
        List<string> violations = [];

        foreach (string file in Directory.EnumerateFiles(EngineRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(EngineRoot, file);

            if (MayOpenANativeLibrary.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CodeIn(file).Any(line => OpensANativeLibrary().IsMatch(line)))
            {
                violations.Add($"{relative} calls GetApi");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Nothing_unloads_a_native_library()
    {
        // The other half of the rule above, and the half that actually crashed. A handle
        // borrowed from one of the four holders must not be disposed: doing so unloads the
        // library out from under everything else still using it, and leaves the process-exit
        // handlers its C++ statics registered pointing at an image that is no longer mapped.
        List<string> violations = [];

        foreach (string file in Directory.EnumerateFiles(EngineRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in CodeIn(file).SelectMany(line => UnloadsANativeLibrary().Matches(line)))
            {
                violations.Add(
                    $"{Path.GetRelativePath(EngineRoot, file)} disposes {match.Groups["handle"].Value}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>A file's lines with the comment-only ones dropped.</summary>
    /// <remarks>
    /// Both rules above are about what the engine calls, and this tree explains itself at
    /// length: the two files that document why a native library must not be reopened name
    /// the very calls they forbid. Prose is not a call site.
    /// </remarks>
    private static IEnumerable<string> CodeIn(string file) =>
        File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

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

    [GeneratedRegex(@"\b(?:AL|ALContext|Cross|DXC|DXGI|D3D12|Shaderc|Vk)\.GetApi\s*\(")]
    private static partial Regex OpensANativeLibrary();

    [GeneratedRegex(@"\b_?(?<handle>al|alc|cross|dxc|dxgi|d3d12|shaderc|vk)\.Dispose\s*\(\s*\)")]
    private static partial Regex UnloadsANativeLibrary();
}

using GK3Reborn.Rendering.Shaders;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The two stages of a graphics pipeline have to agree about their varyings.
/// </summary>
public sealed class ShaderInterfaceTests
{
    /// <summary>Every pair of stages that are linked into one pipeline.</summary>
    public static TheoryData<string> Pairs() => new()
    {
        "mesh",
        "mesh.rt",
        "composite",
        "output",
        "fog",
    };

    private static (string Vertex, string Fragment) SourcesOf(string name) => name switch
    {
        "mesh" => (MeshShaders.Compose(false, false), MeshShaders.Compose(true, false)),
        "mesh.rt" => (MeshShaders.Compose(false, true), MeshShaders.Compose(true, true)),
        "composite" => (CompositeShaders.Vertex, CompositeShaders.Fragment),
        "output" => (OutputShaders.Vertex, OutputShaders.Fragment),

        // The composite's vertex stage, which every full-screen pass shares: it declares no
        // varyings at all, and this is what says the fog's fragment stage asks for none.
        "fog" => (CompositeShaders.Vertex, FogShaders.Fragment),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such pair"),
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void What_a_vertex_stage_writes_is_what_its_fragment_stage_reads(string name)
    {
        using var spirv = new SpirvCompiler();
        using var transpiler = new HlslTranspiler();

        (string vertexSource, string fragmentSource) = SourcesOf(name);

        byte[] vertex = spirv.Compile(
            vertexSource, ShaderStage.Vertex, name + ".vert", "main", ShaderLanguage.Glsl);

        byte[] fragment = spirv.Compile(
            fragmentSource, ShaderStage.Fragment, name + ".frag", "main", ShaderLanguage.Glsl);

        IReadOnlySet<uint> written = transpiler.StageOutputLocations(vertex, name + ".vert");
        IReadOnlySet<uint> read = transpiler.StageInputLocations(fragment, name + ".frag");

        Assert.Equal(read.Order(), written.Order());
    }

    [Fact]
    public void The_compiler_refuses_a_pair_that_does_not_agree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXC is Windows-only");

        const string Vertex = """
            #version 460
            layout(location = 0) out vec4 kept;
            layout(location = 1) out vec4 ignored;
            void main()
            {
                kept = vec4(1.0);
                ignored = vec4(2.0);
                gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
            }
            """;

        const string Fragment = """
            #version 460
            layout(location = 0) in vec4 kept;
            layout(location = 0) out vec4 colour;
            void main() { colour = kept; }
            """;

        // Refused here, with both sets of locations in the message, rather than at pipeline
        // creation with a semantic and no location.
        using var compiler = new ShaderCompiler();

        var ex = Assert.Throws<ShaderCompilationException>(() =>
            compiler.CompileGraphics(ShaderTarget.Dxil, Vertex, Fragment, "mismatched"));

        Assert.Contains("disagree about their varyings", ex.Message, StringComparison.Ordinal);
    }
}

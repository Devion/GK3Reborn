using GK3Reborn.Rendering.Shaders;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The two stages of a graphics pipeline have to agree about their varyings.
/// </summary>
/// <remarks>
/// <para>
/// Vulkan links stages by location and does not mind a vertex shader writing something its
/// fragment shader ignores. Direct3D does mind, and not in a way anybody would guess: each
/// stage is compiled on its own and DXC packs its varyings into consecutive hardware
/// registers, so one unread output leaves every varying after it in a different register in
/// one stage than in the other. The pipeline is then refused with <c>Semantic 'TEXCOORD' is
/// defined for mismatched hardware registers</c>, which names a semantic that appears six
/// times and no location at all.
/// </para>
/// <para>
/// That is a bad afternoon, and it is entirely avoidable: a varying nobody reads is a defect
/// on both backends, because the vertex stage is computing and interpolating it anyway. The
/// mesh shader had one — a clip position written and never read — and the composite had
/// another: a texture coordinate the fragment stage ignores, because it reads its targets by
/// pixel instead. Both had been interpolated for nothing on Vulkan for as long as they had
/// existed, and this is what would have found either in a second.
/// </para>
/// </remarks>
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

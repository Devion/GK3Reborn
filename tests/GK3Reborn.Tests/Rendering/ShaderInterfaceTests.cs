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
/// mesh shader had exactly one — a clip position written and never read — and this is what
/// would have found it in a second.
/// </para>
/// </remarks>
public sealed class ShaderInterfaceTests
{
    /// <summary>Every pair of stages that are linked into one pipeline.</summary>
    public static TheoryData<string, bool> Pairs() => new()
    {
        { "mesh", false },
        { "mesh", true },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void What_a_vertex_stage_writes_is_what_its_fragment_stage_reads(string name, bool rayTracing)
    {
        using var spirv = new SpirvCompiler();
        using var transpiler = new HlslTranspiler();

        byte[] vertex = spirv.Compile(
            MeshShaders.Compose(fragment: false, rayTracing),
            ShaderStage.Vertex,
            name + ".vert",
            "main",
            ShaderLanguage.Glsl);

        byte[] fragment = spirv.Compile(
            MeshShaders.Compose(fragment: true, rayTracing),
            ShaderStage.Fragment,
            name + ".frag",
            "main",
            ShaderLanguage.Glsl);

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

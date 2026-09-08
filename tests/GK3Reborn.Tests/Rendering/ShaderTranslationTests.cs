using GK3Reborn.Rendering.Shaders;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The shaders are written once and compiled twice.
/// </summary>
public sealed class ShaderTranslationTests
{
    /// <summary>Every shader the test drives, with the stage and language it is written for.</summary>
    public static TheoryData<string, ShaderStage> Shaders() => new()
    {
        { "mesh.vert", ShaderStage.Vertex },
        { "mesh.frag", ShaderStage.Fragment },
        { "mesh.vert.rt", ShaderStage.Vertex },
        { "mesh.frag.rt", ShaderStage.Fragment },
        { "shadow.trace", ShaderStage.Compute },
        { "shadow.classify", ShaderStage.Compute },
        { "shadow.filter", ShaderStage.Compute },
        { "reflect.downsample", ShaderStage.Compute },
        { "reflect.march", ShaderStage.Compute },
        { "fog.frag", ShaderStage.Fragment },
    };

    private static string SourceOf(string name) => name switch
    {
        "mesh.vert" => MeshShaders.Compose(fragment: false, rayTracing: false),
        "mesh.frag" => MeshShaders.Compose(fragment: true, rayTracing: false),
        "mesh.vert.rt" => MeshShaders.Compose(fragment: false, rayTracing: true),
        "mesh.frag.rt" => MeshShaders.Compose(fragment: true, rayTracing: true),
        "shadow.trace" => DenoiserShaders.ComposeTrace(),
        "shadow.classify" => DenoiserShaders.ComposeClassify(),
        "shadow.filter" => DenoiserShaders.ComposeFilter(),
        "reflect.downsample" => ReflectionShaders.ComposeDownsample(),
        "reflect.march" => ReflectionShaders.ComposeMarch(),
        "fog.frag" => FogShaders.Fragment,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such shader"),
    };

    [Theory]
    [MemberData(nameof(Shaders))]
    public void Every_shader_compiles_to_spirv(string name, ShaderStage stage)
    {
        using var compiler = new ShaderCompiler();

        byte[] spirv = compiler.CompileTo(
            ShaderTarget.SpirV, SourceOf(name), stage, name, "main", ShaderLanguage.Glsl);

        // The magic number, little-endian. A module that got this far but is not one is a
        // compiler that reported success and wrote something else.
        Assert.True(spirv.Length > 4, $"{name} produced {spirv.Length} bytes");
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(spirv, 0));
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void Every_shader_translates_to_hlsl(string name, ShaderStage stage)
    {
        using var compiler = new ShaderCompiler();

        string hlsl = compiler.Translate(SourceOf(name), stage, name, "main", ShaderLanguage.Glsl);

        Assert.Contains("main(", hlsl, StringComparison.Ordinal);

        // Push constants have no register of their own in HLSL and SPIRV-Cross will pick
        // one if nobody says otherwise; the one it picks is b0, where the frame's uniform
        // buffer already is. Whether the placement took is invisible until two resources
        // collide in a root signature, so it is asserted here instead.
        Assert.DoesNotContain("register(b0, space0)", SecondCbufferOnwards(hlsl), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void Every_shader_compiles_to_signed_dxil(string name, ShaderStage stage)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXC is Windows-only");

        using var compiler = new ShaderCompiler();

        byte[] dxil = compiler.CompileTo(
            ShaderTarget.Dxil, SourceOf(name), stage, name, "main", ShaderLanguage.Glsl);

        Assert.True(dxil.Length > 20, $"{name} produced {dxil.Length} bytes");
        Assert.Equal("DXBC"u8.ToArray(), dxil[..4]);

        // DxilCompiler refuses an unsigned module, so reaching here means it was signed;
        // this says so out loud, because an unsigned module is refused at pipeline
        // creation rather than at compilation and the two are a long way apart.
        Assert.Contains(dxil[4..20], b => b != 0);
    }

    [Fact]
    public void The_ray_query_survives_the_translation()
    {
        using var compiler = new ShaderCompiler();

        string traced = compiler.Translate(
            DenoiserShaders.ComposeTrace(),
            ShaderStage.Compute, "shadow.trace", "main", ShaderLanguage.Glsl);

        // The whole reason ADR 0008 put these shaders in GLSL. If the translation quietly
        // dropped the ray query the shader would still compile, still link and still run —
        // leaving every surface in full light, with nothing in either log to say so.
        Assert.Contains("RayQuery", traced, StringComparison.Ordinal);
        Assert.Contains("TraceRayInline", traced, StringComparison.Ordinal);
        Assert.Contains("RaytracingAccelerationStructure", traced, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mesh_shader_traces_nothing_and_says_so()
    {
        using var compiler = new ShaderCompiler();

        string plain = compiler.Translate(
            MeshShaders.Compose(fragment: true, rayTracing: false),
            ShaderStage.Fragment, "mesh.frag", "main", ShaderLanguage.Glsl);

        string traced = compiler.Translate(
            MeshShaders.Compose(fragment: true, rayTracing: true),
            ShaderStage.Fragment, "mesh.frag.rt", "main", ShaderLanguage.Glsl);

        // Worth writing down, because the source reads as though it does. The mesh shader
        // declares an acceleration structure and an Occluded() beside it, and calls
        // neither: both occlusions are traced a pixel at a time in the compute pass above
        // and filtered over several frames, so nothing is available while this pass is
        // still running. glslang drops the unused function and the binding with it, on
        // both backends alike.
        //
        // The consequence for Direct3D is a root signature for this pass with no
        // acceleration structure in it. Finding that out by reading a generated root
        // signature would be a bad afternoon.
        Assert.DoesNotContain("RayQuery", traced, StringComparison.Ordinal);
        Assert.DoesNotContain("RaytracingAccelerationStructure", traced, StringComparison.Ordinal);

        // The define does reach the compiler all the same: the ray-traced variant writes a
        // direct-light target the other one has no attachment for.
        Assert.NotEqual(plain, traced);
    }

    [Fact]
    public void No_shader_needs_bindless_descriptor_indexing()
    {
        using var compiler = new ShaderCompiler();

        foreach ((string name, ShaderStage stage) in Shaders().Select(row => row.Data))
        {
            string hlsl = compiler.Translate(SourceOf(name), stage, name, "main", ShaderLanguage.Glsl);

            // Not a translation check — a statement about what the root signatures have to
            // support. Nothing in the tree indexes a descriptor array with a value that
            // varies across a wave, so no descriptor range needs the volatile flags and no
            // pass needs a bindless heap. If this ever fails, a shader has started doing
            // something the Direct3D descriptor tables were not built for.
            Assert.DoesNotContain("NonUniformResourceIndex", hlsl, StringComparison.Ordinal);
        }
    }

    /// <summary>The generated source with its first <c>cbuffer</c> declaration removed.</summary>
    private static string SecondCbufferOnwards(string hlsl)
    {
        int first = hlsl.IndexOf("cbuffer", StringComparison.Ordinal);
        if (first < 0)
        {
            return hlsl;
        }

        int second = hlsl.IndexOf("cbuffer", first + 1, StringComparison.Ordinal);
        return second < 0 ? string.Empty : hlsl[second..];
    }
}

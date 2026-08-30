using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The tracing and denoising pipelines, built on a real device from the real shaders.
/// </summary>
/// <remarks>
/// These are the three compute shaders the ray-traced path is made of, and they are where
/// the declared layout is most likely to drift from the shader: sixteen bindings in one of
/// them, with the tracing pass's masks deliberately out of order because the light rig took
/// the bindings between them. Direct3D refuses a pipeline whose root signature does not
/// satisfy its shader, so a pipeline that creates is the two agreeing.
/// </remarks>
[Collection(GpuTests.Name)]
public sealed class D3D12DenoiseTests
{
    private static bool CanTrace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            DeviceReport report = D3D12DeviceSelector.Survey();
            return report.Selected?.Tiers.HasFlag(RenderCapabilityTier.RayTracing) == true;
        }
        catch (D3D12Exception)
        {
            return false;
        }
    }

    [Fact]
    public void The_tracing_pass_agrees_with_its_shader()
    {
        Assert.SkipUnless(CanTrace(), "no Direct3D device with inline ray tracing");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        using D3D12Pipeline trace = D3D12Pipeline.CreateCompute(
            context, compiler, DenoiserShaders.ComposeTrace(), "shadow.trace", DenoiseLayout.Trace);

        Assert.True(trace.Signature.ParameterFor(0) >= 0);
        Assert.True(trace.Signature.PushConstantParameter >= 0);

        // Ten bindings and no sampler: the tracing pass reads its depth and normal with
        // texelFetch at a pixel it already has, so there is nothing to filter with.
        Assert.Equal(10u, trace.Signature.ViewDescriptorCount);
        Assert.Equal(0u, trace.Signature.SamplerDescriptorCount);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("classify")]
    [InlineData("filter")]
    public void The_denoising_passes_agree_with_their_shaders(string which)
    {
        Assert.SkipUnless(CanTrace(), "no Direct3D device with inline ray tracing");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        string source = which == "classify"
            ? DenoiserShaders.ComposeClassify()
            : DenoiserShaders.ComposeFilter();

        using D3D12Pipeline pipeline = D3D12Pipeline.CreateCompute(
            context, compiler, source, "shadow." + which, DenoiseLayout.Denoise);

        // Fifteen views and one sampler out of sixteen bindings: the sampler is bound on its
        // own rather than with a texture, because these read the same seven textures through
        // it and Direct3D keeps samplers in a heap of their own either way.
        Assert.Equal(15u, pipeline.Signature.ViewDescriptorCount);
        Assert.Equal(1u, pipeline.Signature.SamplerDescriptorCount);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }
}

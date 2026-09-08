using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.DXGI;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The full-screen passes, built on a real device from the real shaders.
/// </summary>
[Collection(GpuTests.Name)]
public sealed class D3D12ScreenPassTests
{
    private static bool HasDevice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            DeviceReport report = D3D12DeviceSelector.Survey();
            return report.Available && report.Selected is not null;
        }
        catch (D3D12Exception)
        {
            return false;
        }
    }

    [Fact]
    public void The_composite_reads_the_six_targets_of_a_frame()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        using D3D12ScreenPass pass = D3D12ScreenPass.Create(
            context,
            compiler,
            CompositeShaders.Vertex,
            CompositeShaders.Fragment,
            "composite",
            inputs: 6,
            constantBytes: 4,
            [Format.FormatR16G16B16A16Float]);

        // Six textures and six samplers, because HLSL has no combined image sampler and each
        // one is split in two.
        Assert.Equal(6u, pass.Signature.ViewDescriptorCount);
        Assert.Equal(6u, pass.Signature.SamplerDescriptorCount);
        Assert.True(pass.Signature.PushConstantParameter >= 0);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void The_output_encode_takes_the_picture_to_the_swapchain()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        // Both formats a swapchain can be, because the pipeline carries the target format and
        // one built for the wrong one is undefined rather than wrong. See D3D12Swapchain.
        foreach (Format format in (Format[])[Format.FormatR8G8B8A8Unorm, Format.FormatR10G10B10A2Unorm])
        {
            using D3D12ScreenPass pass = D3D12ScreenPass.Create(
                context,
                compiler,
                OutputShaders.Vertex,
                OutputShaders.Fragment,
                "output",
                inputs: 1,
                constantBytes: 32,
                [format]);

            Assert.Equal(1u, pass.Signature.ViewDescriptorCount);
            Assert.Equal(1u, pass.Signature.SamplerDescriptorCount);
        }

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }
}

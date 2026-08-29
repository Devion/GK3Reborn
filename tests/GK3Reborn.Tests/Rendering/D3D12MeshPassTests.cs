using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.DXGI;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The room's own pipeline, built on a real device from the real shaders.
/// </summary>
/// <remarks>
/// <para>
/// This is where the declared layout meets the compiled shader. MeshLayout says what the
/// pipeline binds and a root signature is built from it; the shaders are the engine's own,
/// translated from GLSL; and Direct3D refuses to create a pipeline whose root signature does
/// not satisfy what its shaders actually reference. So a pipeline that creates at all is a
/// layout that agrees with two thousand lines of shading nobody wrote twice.
/// </para>
/// <para>
/// Both variants, because they are different layouts rather than one layout with a branch: a
/// device that cannot trace must not be given a binding it cannot fill, so the ray-traced
/// variant has an acceleration structure at set 0 binding 4 and the other has nothing there
/// at all.
/// </para>
/// </remarks>
[Collection(GpuTests.Name)]
public sealed class D3D12MeshPassTests
{
    /// <summary>What the G-buffer holds, in the order the shader writes them.</summary>
    private static readonly Format[] Targets =
    [
        Format.FormatR16G16B16A16Float,
        Format.FormatR16G16B16A16Float,
        Format.FormatR16G16Float,
        Format.FormatR16G16B16A16Float,
    ];

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

    private static bool CanTrace() =>
        HasDevice() && D3D12DeviceSelector.Survey().Selected?.Tiers
            .HasFlag(RenderCapabilityTier.RayTracing) == true;

    [Fact]
    public void The_raster_pipeline_agrees_with_its_shaders()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        using D3D12MeshPass pass = D3D12MeshPass.Create(
            context, compiler, Targets[..3], Format.FormatD32Float, rayTracing: false);

        Assert.False(pass.RayTracing);

        // Set 0 and set 1 both reach the shader, and the draw constants have a home. A
        // signature missing any of the three creates and then binds nothing.
        Assert.True(pass.Signature.ParameterFor(MeshLayout.FrameSet) >= 0);
        Assert.True(pass.Signature.ParameterFor(MeshLayout.MaterialSet) >= 0);
        Assert.True(pass.Signature.PushConstantParameter >= 0);

        // Nine views: the frame’s uniform buffer and three light buffers, then the five
        // textures of a material. Five samplers, and only five, because the frame set has no
        // texture in it — HLSL has no combined image sampler, so SPIRV-Cross splits each of
        // the material’s five in two and the sampler half lands in a heap of its own.
        Assert.Equal(9u, pass.Signature.ViewDescriptorCount);
        Assert.Equal(5u, pass.Signature.SamplerDescriptorCount);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ray_traced_pipeline_agrees_with_its_shaders()
    {
        Assert.SkipUnless(CanTrace(), "no Direct3D device with inline ray tracing");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        using D3D12MeshPass pass = D3D12MeshPass.Create(
            context, compiler, Targets, Format.FormatD32Float, rayTracing: true);

        Assert.True(pass.RayTracing);
        Assert.True(pass.Signature.ParameterFor(MeshLayout.FrameSet) >= 0);

        // One more than the raster variant: the acceleration structure, which is a shader
        // resource view whose dimension says what it is.
        Assert.Equal(10u, pass.Signature.ViewDescriptorCount);
        Assert.Equal(5u, pass.Signature.SamplerDescriptorCount);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void The_two_variants_declare_different_frame_sets()
    {
        // No device needed: this is about the declaration rather than the pipeline. A binding
        // nothing can fill is not harmless on either backend, which is why there are two
        // shader variants rather than one that branches.
        int raster = MeshLayout.Raster.Bindings.Count(b => b.Set == MeshLayout.FrameSet);
        int traced = MeshLayout.Traced.Bindings.Count(b => b.Set == MeshLayout.FrameSet);

        Assert.Equal(raster + 1, traced);

        Assert.DoesNotContain(
            MeshLayout.Raster.Bindings,
            b => b.Kind == ShaderBindingKind.AccelerationStructure);

        Assert.Contains(
            MeshLayout.Traced.Bindings,
            b => b.Kind == ShaderBindingKind.AccelerationStructure);
    }
}

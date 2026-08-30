using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Materials;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using System.Numerics;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The reflection passes, built and run on a real device.
/// </summary>
/// <remarks>
/// The pyramid is the one place in the renderer where two subresources of one texture are in
/// different states at the same time — level <c>n</c> being written while level <c>n - 1</c>
/// is read — and getting that wrong is not a crash. It is a validation message and a picture
/// with the wrong reflections in it, both of which are easy to miss in a still. So this runs
/// the pass and reads what the debug layer had to say about it.
/// </remarks>
[Collection(GpuTests.Name)]
public sealed unsafe class D3D12ReflectionTests
{
    private static bool CanRender()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return D3D12DeviceSelector.Survey().Selected is not null;
        }
        catch (D3D12Exception)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("downsample")]
    [InlineData("march")]
    public void The_reflection_passes_agree_with_their_shaders(string which)
    {
        Assert.SkipUnless(CanRender(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        string source = which == "downsample"
            ? ReflectionShaders.ComposeDownsample()
            : ReflectionShaders.ComposeMarch();

        using D3D12Pipeline pipeline = D3D12Pipeline.CreateCompute(
            context, compiler, source, "reflect." + which, ReflectLayout.Bindings);

        // Nine views and one sampler out of ten bindings.
        Assert.Equal(9u, pipeline.Signature.ViewDescriptorCount);
        Assert.Equal(1u, pipeline.Signature.SamplerDescriptorCount);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void A_binding_after_the_sampler_sits_before_its_own_number()
    {
        Assert.SkipUnless(CanRender(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);

        using D3D12RootSignature signature =
            D3D12RootSignature.Create(context.Device, ReflectLayout.Bindings);

        // Bindings zero to five are textures and land where their numbers say.
        for (uint i = 0; i <= 5; i++)
        {
            Assert.Equal(i, signature.ViewOffset(0, i));
        }

        // Binding six is the sampler, which is in a heap of its own and takes no slot in the
        // view table — so everything above it is one slot earlier than its number. This is
        // the whole reason ViewOffset exists rather than the binding being used directly.
        Assert.Equal(0u, signature.SamplerOffset(0, 6));
        Assert.Equal(6u, signature.ViewOffset(0, 7));
        Assert.Equal(7u, signature.ViewOffset(0, 8));
        Assert.Equal(8u, signature.ViewOffset(0, 9));
    }

    [Fact]
    public void The_march_runs_over_the_pyramid_without_complaint()
    {
        Assert.SkipUnless(CanRender(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        const int Width = 320;
        const int Height = 200;

        using D3D12Reflections reflections =
            D3D12Reflections.Create(context, compiler, Width, Height);

        using D3D12Texture depth = D3D12Texture.CreateDepthTarget(
            context, GBufferFormats.Depth, Width, Height, sampled: true);

        using D3D12Texture normal = D3D12Texture.CreateRenderTarget(
            context, GBufferFormats.Normal, Width, Height);

        using D3D12Texture motion = D3D12Texture.CreateRenderTarget(
            context, GBufferFormats.Motion, Width, Height);

        using D3D12Texture lit = D3D12Texture.CreateRenderTarget(
            context, GBufferFormats.Light, Width, Height);

        reflections.Bind(depth, normal, motion, lit);

        var camera = new Camera { Position = new Vector3(0, 60, 0), Target = new Vector3(0, 60, 100) };

        // Twice, because the two targets take turns and the second frame is the first one
        // that reads a history — which is the frame where a state left wrong the first time
        // round is noticed.
        var parities = new int[2];

        for (int i = 0; i < 2; i++)
        {
            ID3D12GraphicsCommandList4* list = context.BeginOneShot();

            reflections.Record(
                list, camera, depth, normal, motion, lit, SurfaceFinish.Roughest);

            context.EndOneShot();
            parities[i] = reflections.Parity;
        }

        // The two frames landed in different targets. If they did not, the march would be
        // reading the target it was writing, and its history would be this frame rather than
        // the last — which averages a frame with itself and settles on nothing.
        Assert.NotEqual(parities[0], parities[1]);
        Assert.NotSame(reflections.Reflected, null);

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }
}

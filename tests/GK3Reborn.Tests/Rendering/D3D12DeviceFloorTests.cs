using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Core.Native;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The least a card has to be for the Direct3D renderer to run on it, and the shaders it
/// is then given.
/// </summary>
/// <remarks>
/// The device used to be asked for at feature level 12_0 while the survey that chose it
/// had asked for 11_0, so a first-generation Maxwell — a GeForce GTX 960M, which reports
/// 11_0 and can draw every raster pass — passed the survey and then failed to start with
/// <c>DXGI_ERROR_UNSUPPORTED</c>. The device is now made at the floor and the shaders are
/// compiled for what it reports. None of that can be exercised on a card that has
/// everything, so what is tested here is the arithmetic and that the real device on this
/// machine is described consistently.
/// </remarks>
[Collection(GpuTests.Name)]
public sealed class D3D12DeviceFloorTests
{
    [Theory]
    [InlineData(ShaderStage.Vertex, 0x65u, "vs_6_5")]
    [InlineData(ShaderStage.Fragment, 0x60u, "ps_6_0")]
    [InlineData(ShaderStage.Compute, 0x66u, "cs_6_6")]
    [InlineData(ShaderStage.Fragment, 0x61u, "ps_6_1")]
    public void A_profile_names_the_stage_and_the_model(ShaderStage stage, uint model, string profile) =>
        Assert.Equal(profile, DxilCompiler.ProfileFor(stage, model));

    [Theory]
    [InlineData(0x5Fu)]
    [InlineData(0x51u)]
    [InlineData(0x6Au)]
    [InlineData(0x70u)]
    public void A_model_dxil_does_not_have_is_refused(uint model) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DxilCompiler.ProfileFor(ShaderStage.Vertex, model));

    [Fact]
    public void The_default_model_is_the_ray_query_floor() =>
        Assert.Equal(D3D12DeviceSelector.RequiredShaderModel, DxilCompiler.DefaultShaderModel);

    [Fact]
    public void A_compiler_told_a_model_compiles_for_it_and_keys_its_cache_by_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXC is Windows-only");

        string cache = Path.Combine(Path.GetTempPath(), "gk3r-tests", Path.GetRandomFileName());

        const string source =
            """
            #version 460
            layout(local_size_x = 1) in;
            layout(set = 0, binding = 0) buffer Out { uint value; } result;
            void main() { result.value = 1u; }
            """;

        try
        {
            using var newer = new ShaderCompiler(cache) { DxilShaderModel = 0x65 };
            using var older = new ShaderCompiler(cache) { DxilShaderModel = 0x61 };

            byte[] atNewer = newer.CompileTo(
                ShaderTarget.Dxil, source, ShaderStage.Compute, "floor", "main", ShaderLanguage.Glsl);
            byte[] atOlder = older.CompileTo(
                ShaderTarget.Dxil, source, ShaderStage.Compute, "floor", "main", ShaderLanguage.Glsl);

            // Two compilers over one directory must not hand each other's modules back:
            // the same source at two models is two modules and two files. A module names
            // its model, so the bytes differ if the profile was honoured.
            Assert.NotEqual(atNewer, atOlder);
            Assert.Equal(2, Directory.EnumerateFiles(cache, "*.dxil").Count());

            // And the second time round each is read back rather than rebuilt.
            Assert.Equal(atOlder, older.CompileTo(
                ShaderTarget.Dxil, source, ShaderStage.Compute, "floor", "main", ShaderLanguage.Glsl));
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [Fact]
    public void The_device_is_made_at_the_floor_and_described_by_what_it_is()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: false);

        Assert.True(
            context.FeatureLevel >= D3DFeatureLevel.Level110,
            $"a device was made below the 11_0 floor: {context.FeatureLevel}");

        Assert.InRange(context.ShaderModel, D3D12Context.LowestShaderModel, 0x69u);
        Assert.InRange(context.DxilShaderModel, D3D12Context.LowestShaderModel, D3D12DeviceSelector.RequiredShaderModel);
        Assert.True(context.DxilShaderModel <= context.ShaderModel, "shaders compiled past what the device reports");

        // The survey's text and the device's own answer must say the same thing, or the
        // log names one model and the shaders are built for another.
        Assert.Contains(
            $"shader model {context.ShaderModel >> 4}.{context.ShaderModel & 0xF}",
            context.Adapter1.ApiVersion,
            StringComparison.Ordinal);

        // A device that stops short of 6.5 cannot have been given the tier: the two are
        // decided together, and the shaders it is handed contain no ray query.
        if (context.ShaderModel < D3D12DeviceSelector.RequiredShaderModel)
        {
            Assert.False(context.SupportsRayTracing);
        }
    }

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
}

using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for asking a device only for what it offered.
/// </summary>
/// <remarks>
/// <c>vkCreateDevice</c> fails outright when a requested feature is absent — it does not
/// grant what it can and report the rest. So a feature the renderer would like has to be
/// intersected with what the device advertises before it is asked for, and getting that
/// wrong is not a degraded picture but a game that will not start on that hardware.
/// </remarks>
public sealed class DeviceCapabilityTests
{
    [Fact]
    public void NothingIsAskedForThatTheDeviceDidNotOffer()
    {
        var none = new DeviceCapabilities(
            BlockCompression: false,
            AnisotropicFiltering: false,
            AstcCompression: true,
            Etc2Compression: true);

        Silk.NET.Vulkan.PhysicalDeviceFeatures asked = none.Requested();

        Assert.False(asked.TextureCompressionBC);
        Assert.False(asked.SamplerAnisotropy);
    }

    [Fact]
    public void EverythingOfferedIsAskedFor()
    {
        var all = new DeviceCapabilities(
            BlockCompression: true,
            AnisotropicFiltering: true,
            AstcCompression: false,
            Etc2Compression: false);

        Silk.NET.Vulkan.PhysicalDeviceFeatures asked = all.Requested();

        Assert.True(asked.TextureCompressionBC);
        Assert.True(asked.SamplerAnisotropy);
    }

    [Fact]
    public void ADeviceWithNoBlockCompressionSaysSoInItsReport()
    {
        var apple = new DeviceCapabilities(
            BlockCompression: false,
            AnisotropicFiltering: true,
            AstcCompression: true,
            Etc2Compression: true);

        // The line goes into the startup report, where somebody wondering why a Mac uses
        // four times the video memory of a PC for the same room will read it.
        Assert.Contains("no BC", apple.ToString(), StringComparison.Ordinal);
        Assert.Contains("host", apple.ToString(), StringComparison.Ordinal);
    }
}

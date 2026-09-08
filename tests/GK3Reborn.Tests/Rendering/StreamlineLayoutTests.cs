using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Where every field of a Streamline structure sits.
/// </summary>
public sealed class StreamlineLayoutTests
{
    /// <summary>
    /// The header every one of them begins with: a link, a GUID and a version.
    /// </summary>
    [Fact]
    public void The_header_is_a_link_a_guid_and_a_version()
    {
        Assert.Equal(32, Marshal.SizeOf<SlHeader>());

        Assert.Equal(0, (int)Marshal.OffsetOf<SlHeader>(nameof(SlHeader.Next)));
        Assert.Equal(8, (int)Marshal.OffsetOf<SlHeader>(nameof(SlHeader.Data1)));
        Assert.Equal(24, (int)Marshal.OffsetOf<SlHeader>(nameof(SlHeader.Version)));
    }

    /// <summary>
    /// Reflex reads the mode at thirty-two, the key at forty-two and the thread at
    /// forty-four, and copies forty-eight bytes.
    /// </summary>
    [Fact]
    public void Reflex_options_are_where_the_plugin_reads_them()
    {
        Assert.Equal(48, Marshal.SizeOf<SlReflexOptions>());

        Assert.Equal(32, (int)Marshal.OffsetOf<SlReflexOptions>(nameof(SlReflexOptions.Mode)));

        Assert.Equal(
            36, (int)Marshal.OffsetOf<SlReflexOptions>(nameof(SlReflexOptions.FrameLimitUs)));

        Assert.Equal(
            40,
            (int)Marshal.OffsetOf<SlReflexOptions>(nameof(SlReflexOptions.UseMarkersToOptimise)));

        Assert.Equal(
            42, (int)Marshal.OffsetOf<SlReflexOptions>(nameof(SlReflexOptions.VirtualKey)));

        Assert.Equal(
            44, (int)Marshal.OffsetOf<SlReflexOptions>(nameof(SlReflexOptions.IdThread)));
    }

    /// <summary>A marker is the header and a number.</summary>
    [Fact]
    public void A_marker_is_one_number_after_the_header()
    {
        Assert.Equal(
            32, (int)Marshal.OffsetOf<SlReflexMarker>(nameof(SlReflexMarker.Marker)));
    }

    /// <summary>
    /// Frame generation reads the count at thirty-six and the extents between fifty-two and
    /// eighty-eight, and copies a hundred and twenty bytes.
    /// </summary>
    [Fact]
    public void Frame_generation_options_are_where_the_plugin_reads_them()
    {
        Assert.Equal(120, Marshal.SizeOf<SlDlssgOptions>());

        Assert.Equal(32, (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.Mode)));

        Assert.Equal(
            36,
            (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.NumFramesToGenerate)));

        Assert.Equal(40, (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.Flags)));

        Assert.Equal(
            52, (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.NumBackBuffers)));

        Assert.Equal(
            56, (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.MvecDepthWidth)));

        Assert.Equal(
            64, (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.ColorWidth)));

        Assert.Equal(
            72, (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.ColorBufferFormat)));

        Assert.Equal(
            96, (int)Marshal.OffsetOf<SlDlssgOptions>(nameof(SlDlssgOptions.Callback)));
    }

    /// <summary>
    /// The state the plugin fills in, whose maximum is what a menu is trimmed to.
    /// </summary>
    [Fact]
    public void Frame_generation_state_is_where_the_plugin_writes_it()
    {
        Assert.Equal(
            32, (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.EstimatedVramBytes)));

        Assert.Equal(40, (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.Status)));

        Assert.Equal(
            44, (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.MinWidthOrHeight)));

        Assert.Equal(
            48,
            (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.NumFramesActuallyPresented)));

        Assert.Equal(
            52,
            (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.NumFramesToGenerateMax)));

        Assert.Equal(57, (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.Enabled)));
        Assert.Equal(64, (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.Fence)));
        Assert.Equal(72, (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.FenceValue)));
        Assert.Equal(80, (int)Marshal.OffsetOf<SlDlssgState>(nameof(SlDlssgState.Flag)));

        // Long enough for everything version four writes, which is the last of them.
        Assert.True(Marshal.SizeOf<SlDlssgState>() >= 81);
    }

    /// <summary>
    /// The markers are a run from nought, and the sleep is not one of them.
    /// </summary>
    [Fact]
    public void The_markers_are_numbered_as_the_plugin_numbers_them()
    {
        Assert.Equal(0u, (uint)StreamlineMarker.SimulationStart);
        Assert.Equal(1u, (uint)StreamlineMarker.SimulationEnd);
        Assert.Equal(2u, (uint)StreamlineMarker.RenderSubmitStart);
        Assert.Equal(3u, (uint)StreamlineMarker.RenderSubmitEnd);
        Assert.Equal(4u, (uint)StreamlineMarker.PresentStart);
        Assert.Equal(5u, (uint)StreamlineMarker.PresentEnd);
        Assert.Equal(6u, (uint)StreamlineMarker.InputSample);
        Assert.Equal(7u, (uint)StreamlineMarker.TriggerFlash);
        Assert.Equal(8u, (uint)StreamlineMarker.LatencyPing);

        // Not a marker at all: it is how slReflexSleep tells its own call apart from
        // anything an application could send, and it must stay clear of the run above.
        Assert.Equal(4096u, Streamline.MarkerSleep);
        Assert.True(Streamline.MarkerSleep > (uint)StreamlineMarker.LatencyPing);
    }

    /// <summary>Which API Streamline is told it is talking to.</summary>
    [Fact]
    public void The_two_backends_name_themselves_differently()
    {
        Assert.Equal(1u, Streamline.RenderApiDirect3D12);
        Assert.Equal(2u, Streamline.RenderApiVulkan);
    }
}

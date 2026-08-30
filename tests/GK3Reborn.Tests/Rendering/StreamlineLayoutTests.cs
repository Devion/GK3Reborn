using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Where every field of a Streamline structure sits.
/// </summary>
/// <remarks>
/// <para>
/// <b>These offsets are evidence, not preference.</b> None of the structures below appears
/// in any header in this tree; each was read out of the plugin that consumes it, by
/// decompiling the function that picks it out of the chained list and seeing which offsets
/// it loads from. The numbers asserted here are the ones those functions read.
/// </para>
/// <para>
/// They are worth a test because of how they fail. Streamline finds a structure by the GUID
/// in its header and then reads fields by offset; a field in the wrong place is not a type
/// error, a refused call, or a warning. It is the frame limit read out of the mode, or the
/// count of frames to generate read out of the padding after it — a call that succeeds and
/// does something else. A structure that grows a field, or that a compiler pads differently
/// on some future runtime, moves everything after it and says nothing.
/// </para>
/// <para>
/// If one of these fails after a Streamline bundle update, the answer is to decompile the
/// plugin again rather than to change the number until the test passes.
/// </para>
/// </remarks>
public sealed class StreamlineLayoutTests
{
    /// <summary>
    /// The header every one of them begins with: a link, a GUID and a version.
    /// </summary>
    /// <remarks>
    /// Thirty-two bytes, which is why every field list below starts at thirty-two. It is
    /// also the one layout here that is corroborated rather than inferred: the viewport
    /// GUID this engine has always sent is the one <c>sl.dlss_g</c> searches for, which it
    /// could not be if the bytes were arranged any other way.
    /// </remarks>
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
    /// <remarks>
    /// The half-word at forty-two is what fixes the whole list: it leaves a two-byte hole at
    /// forty, which is where the flag the published field order puts between the frame limit
    /// and the key belongs. Nothing else both lands on those three offsets and comes to
    /// forty-eight bytes.
    /// </remarks>
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
    /// <remarks>
    /// The count is the field the whole feature turns on, and the one this test exists for:
    /// the plugin refuses a nought there by name and refuses anything above what the card
    /// reports, so a count read out of the wrong four bytes is frame generation that is
    /// either off or rejected, and never a wrong picture anybody could see.
    /// </remarks>
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
    /// <remarks>
    /// Fifty-two is the number that decides whether this machine is offered two times or
    /// four, and it is only written when the header says version two or above — which is why
    /// the caller asks at four and this structure is long enough for what four writes.
    /// </remarks>
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
    /// <remarks>
    /// The special cases in the plugin are what fix the list, and they are spread across it:
    /// nought records a timestamp, four sets the parameter the plugin calls the present
    /// frame, and seven and eight go to the driver unread. A list shifted by one would put a
    /// simulation marker where a present marker belongs, and Reflex would measure the frame
    /// from the wrong end of itself.
    /// </remarks>
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
    /// <remarks>
    /// It decides what <c>slGetFeatureRequirements</c> answers with, and it is asked before
    /// any device exists — so a backend that says the wrong one collects the other API's
    /// extension names and asks for queues nothing will create. Direct3D 12 is one and
    /// Vulkan is two, from <c>sl::RenderAPI</c>.
    /// </remarks>
    [Fact]
    public void The_two_backends_name_themselves_differently()
    {
        Assert.Equal(1u, Streamline.RenderApiDirect3D12);
        Assert.Equal(2u, Streamline.RenderApiVulkan);
    }
}

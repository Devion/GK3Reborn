using System.Diagnostics;
using System.IO.Hashing;
using System.Reflection;
using GK3Reborn.Formats.Video;
using GK3Reborn.Formats.Video.H264;
using GK3Reborn.Formats.Video.Mp4;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the H.264 decoder.
/// </summary>
/// <remarks>
/// <para>
/// The bar is "matches FFmpeg sample for sample", because H.264 reconstruction is
/// specified exactly and a decoder that is one off anywhere drifts further from the
/// encoder with every predicted frame. The self-contained tests decode three tiny x264
/// streams — CABAC, CAVLC and 4:4:4, all with B-frames and 8x8 transforms — and compare a
/// CRC of every frame against the CRC of FFmpeg's decode, recorded when the streams were
/// made. The corpus comparison runs only where ffmpeg and the game's clips exist.
/// </para>
/// </remarks>
public sealed class H264DecoderTests
{
    /// <summary>testsrc 48x32, five frames, CABAC, two B-frames with a pyramid, 8x8 transform, weighted prediction.</summary>
    private const string CabacStream =
        "000000016764000aacd94d6c0440000003004000000503c48965800000000168eae132c8b0000000016588840047e8981379cf77fe0b2ec4fdb628be" +
        "58869f839e28ee81cfe44eeb854a3682661cc0364f6a71ba9a794e5e0a847e137f61aa62531f1712e6f5316a14299c1b4bb637f70fdf75cc70902354" +
        "4fdfaaf4d4ecf4f053534626e4a1b02b954fc1172fa4c7244f6dd3295739dd6e2404ec0b314aa13462125f438c3728ec6b9ed847e03fcb849ca1f158" +
        "af1134b5765bd730bf1d85d814c8d9fa6a5eb758bda0ccaaeb429ce4489800143c32b925037cb81ed59471a15e4e51df7346b33d95d2865a32a3e6b3" +
        "8e4790ed26475b1b663fdf024d38cb15cc07c140d378cd6b5026873496ff5a2506ae44860467dec308b4548218bc42945ce78b24480521609c1c1a93" +
        "de28a2ae98307d02c3e405439dd1ebd02d9ffe2314022dca31610b7bf9b006421b5b407c762841634b4eb073b2537dbe92f0d77742e12edb6b60d90c" +
        "0f1214e09c808784046b9aceab4b2a69cee6b8584bc4aea00efc36dc1e44bb5c3859e6ba6fb67a49791baf55d8179325f68eb9bd9a7d4fbe5a0a25d3" +
        "6268844b6d76975093cce9b93fda667cab2b675d13cc9a3b2167918031f03885afd7a64b93b78084a94de842100f75a7532421653417245ec796f6fe" +
        "8d9cd09f13a4efb13d7a0bcd2ddc792da330f8e2e56c0424798a89ee04aa0046e441d430d47594d62ed02d0067eb404c9f6e678c1df7149d2f9192cd" +
        "0238f96d7621733cc66be3182f7717da7c4b38d0f8eeec8100000001419a216c457f379fa33d2b3b91026de1668cac9e51884c349ef6f3c171fffa5e" +
        "4ea67cc08fa0780e0700000001419a425f843265308aff367595db202a0afa651ae1a5a752b261917abe147cfdf2267c96b27703d131e40c9d339280" +
        "afb2022b14767894c100000001419a635f843265308eff48be3ae202bf5e20b41ee6da7c4422e5b79b75fff3a674cc87d79c9492bb68fdcd020f8e32" +
        "879c00000001419a845f8432653084bf582f50819c0fec56173b68615345da4f53ae1f96f7a1fc7ba238e607f3819c5122904a472917bb";

    private static readonly uint[] CabacCrcs = [0x50CEA5BE, 0x7F1DBBB6, 0x02E19E48, 0x0F60B4C8, 0xA2B79D4C];

    /// <summary>The same picture with CAVLC.</summary>
    private const string CavlcStream =
        "000000016764000aacd94d6c0440000003004000000503c48965800000000168cae132c8b0000000016588840047b872f00e3628000811c0e4018441" +
        "00a5c6c84aedabc00f5e791621de51faea31aa001edff5fb6425fc6407041a86ac758bcfe0e1eaf766030040143a1100010022e00193fa3564227b1f" +
        "3839b821116e170360002699b8845193bc5f07c5bda8c508f3d460c900041455c10000c0000807b161fff868c42043a09e2800081440e810a8388000" +
        "20a800020331002000205f2133b81d23b81d33b81d23b840118ab81c08e55c0e0462ae070239572e5a7cc38e3e0016d4e40aeb832dbbf7c8200c0641" +
        "6004c53ad02a494a6ff07c98eb3e30ea4df6270609c0e10070040440065000105e01085287983c53b654592385c0d821196e0013dd9181140de734d0" +
        "3fad06de907df2f9e910000c0000807b04001041572ff8600aac7f80021a905eacd4a3bbd1be0d004001101300419000e86f108904bf066aa80304b6" +
        "8b2c1c83e04abd4a6446fff881089003d0100020002000400d08000e805184b1a96009622c60a702134d3610c09883756bcc01d295ca146255d323b5" +
        "201a2f53b9823048795fe0180158767e27c24e366880855da7b60c009d0140083a001c066283f818fc1857f8041ee904000204c5000100f404010023" +
        "41289a32cb9e62300801d2018e7800c0013453b7b4d486e2a5c17d1c07374262885f16345fe1c020c21c001233133294bc827253c18e3f0715efb368" +
        "01f4b7bd058aadfb0c7103eae0d320e5bc18bb5be04000640071420104ae65b2f08826f808282b08294444b80c270330417fe90dfe5b810fe0557e10" +
        "71b8f982e3a000000001419a216c0ae4e18c50e238e2bba9443cf9753db2ef87314069091c9114803e6d4ffc378bd5c041018fe2de00000001419a42" +
        "5f8432653015c95863100784ac838fd4c4b72dc560935f97455eb0c2a4f51460b2d307dae76a07642b78b638bc49df8612f5eede07cc06f5881df800" +
        "000001419a635f843265301dc9e184531ca0632df6d9ef289567e9ed977e0a39ba785455bdfe4c803fc30a93a5b959ca806fe27b8000000001419a84" +
        "5f84326530097278611716c96c38e7f7e896bf2e9ffc30cbf146d8ff9e70c8cb13d27dc4f7e18d5599b621f9567372ee4e39a4";

    private static readonly uint[] CavlcCrcs = [0xA3055311, 0x6E28299A, 0x7E3D24A1, 0x0239E599, 0x6DD14175];

    /// <summary>testsrc 40x24 in 4:4:4 (High 4:4:4 Predictive), three frames with one B-frame.</summary>
    private const string Chroma444Stream =
        "0000000167f4000a919cc6bc4c4e022000000300200000030281e244a3400000000168e9784c44844000000001658884019fd8f37f12e2bffc22b8aa" +
        "584d1ada028a567ffa9191f3618c1cf4a4e277226bbc3ebb8ec6dda8bbb8131a64fcd1653b09b412ff04d16ef6c51b42f796c43cf6ab49f9e3037e86" +
        "833012994703925b287ff3d6337f71cb3693a626a03d79951b3f3c4f2daf999c5095e96ef704b16580d17a7583ffe1ff291acbcd298bbfca735d35af" +
        "4704643c4c150409a995f87523006180d66bcbb1fa8713f6052781d1b32be17c151afa5ad67e16af5414daa8dadf4427a604a1ea33d00ba309dad396" +
        "fdac3479f268810bdeccecd22b83590bbf9ddc97aaac455bbe3d9473681dda69bf70bd4de9c8c2c66d1ef707ee19fd1481af0ff3cb4e1bf92d27f42e" +
        "01b5e53a3ae9f56a4389d5c94cbe09f8201be6b70d974ad6b7326521e4259aa50d616d46c049f292d88d743131f4899697c23d8830d359dcfdb5c9db" +
        "5cd9c4a05b2ca5efec6914fbcd0a31a0d3c423cca71dcaff4647d914e9895c28df60fae4e2731290b3357875cd40bf464e5fc860549c365a76aecc8c" +
        "fdce978bd9c1191f7ff0ec4b83d1d472932194a3b5c8b8f9e821e5b7cd6f462018927ff55d3e8a9984fce04646bc919dc6bd947b20b3729f97ff61ef" +
        "84acfe92f9b14e16cd56782981069790006f739d8a5f7200d66fa852328c2c3d680d8172f0452ab9a71f5d6ff72fd36c13739e6e8c62f9ff813e41e7" +
        "c6b07086886286240a65b46b70f2b6e6d05e23f980ae95c2e1a5781dc3d379c7a798be09ff328448d40762159098e2c100000001419a25b1087f7450" +
        "6db764edfc7b66091f3fc8a66cc83345f17dffbae1bee3002849fcc8da393bfc00000001419a497e10c994c214ff66f9c41cffe7c7e15dec96bb45df" +
        "fe181f7fa50002fdae31f49db96c650b";

    private static readonly uint[] Chroma444Crcs = [0xD3B04BEE, 0xC40B9AA1, 0x581A932B];

    [Fact]
    public void A_cabac_stream_with_b_frames_decodes_exactly()
    {
        AssertFramesMatch(CabacStream, 48, 32, CabacCrcs);
    }

    [Fact]
    public void A_cavlc_stream_with_b_frames_decodes_exactly()
    {
        AssertFramesMatch(CavlcStream, 48, 32, CavlcCrcs);
    }

    [Fact]
    public void A_444_stream_decodes_exactly()
    {
        AssertFramesMatch(Chroma444Stream, 40, 24, Chroma444Crcs);
    }

    [Fact]
    public void Frames_come_out_in_display_order_with_their_tags()
    {
        var decoder = new H264Decoder();
        decoder.DecodeAnnexB(Convert.FromHexString(CabacStream));
        decoder.Flush();

        var pocs = new List<int>();

        while (decoder.TryGetFrame(out DecodedFrame frame))
        {
            pocs.Add(frame.Poc);
            frame.Release();
        }

        Assert.Equal(5, pocs.Count);
        Assert.Equal(pocs.OrderBy(p => p), pocs);
    }

    [Fact]
    public void The_converter_turns_grey_into_grey()
    {
        var decoder = new H264Decoder();
        decoder.DecodeAnnexB(Convert.FromHexString(CabacStream));
        decoder.Flush();
        Assert.True(decoder.TryGetFrame(out DecodedFrame frame));

        var rgba = new byte[frame.Width * frame.Height * 4];
        YuvConverter.ToRgba(frame, rgba);
        frame.Release();

        // Every pixel is opaque, and a sample with neutral chroma comes out as a grey.
        for (int i = 3; i < rgba.Length; i += 4)
        {
            Assert.Equal(255, rgba[i]);
        }

        int first = 0;
        int y = frame.Y[frame.CropTop * frame.Stride + frame.CropLeft];
        int expected = Math.Clamp((int)Math.Round((y - 16) * 255.0 / 219.0), 0, 255);
        int cb = frame.Cb[0];
        int cr = frame.Cr[0];

        if (cb == 128 && cr == 128)
        {
            Assert.Equal(expected, rgba[first]);
            Assert.Equal(expected, rgba[first + 1]);
            Assert.Equal(expected, rgba[first + 2]);
        }
    }

    [Fact]
    public void Interlaced_video_is_refused_rather_than_decoded_wrongly()
    {
        // An SPS with frame_mbs_only_flag = 0: Baseline 4.0, 16x16, otherwise minimal.
        // profile 66, constraints 0, level 40, then ue(0) id, ue(0) log2_max_frame_num,
        // ue(0) poc type, ue(0) log2_max_poc_lsb, ue(1) refs, u(1) gaps, ue(0) width,
        // ue(0) height, u(1) frame_mbs_only = 0, u(1) mbaff, u(1) direct8x8, u(1) crop, u(1) vui, stop.
        byte[] sps = [0x67, 0x42, 0x00, 0x28, 0xE9, 0x00, 0x00];
        var decoder = new H264Decoder();

        Assert.ThrowsAny<Exception>(() => decoder.Configure([sps], []));
    }

    // ------------------------------------------------------------------ against ffmpeg

    [Fact]
    public void A_game_clip_matches_ffmpeg_sample_for_sample()
    {
        string? clip = FindClip("TENIERGEOD.mp4") ?? FindClip("PARCH2ZOOM.mp4");
        Assert.SkipUnless(clip is not null && HasFfmpeg(), "needs ffmpeg and the game's converted clips");

        using FileStream stream = File.OpenRead(clip!);
        using var mp4 = Mp4File.Open(stream, clip);
        Mp4Track video = mp4.Video!;
        var decoder = new H264Decoder();
        decoder.Configure(video.SequenceParameterSets, video.PictureParameterSets);

        var ours = new List<uint>();

        foreach (Mp4Sample sample in video.Samples)
        {
            decoder.Decode(mp4.Read(sample), video.NalLengthSize, sample.PresentationTime);
            Drain(decoder, ours);
        }

        decoder.Flush();
        Drain(decoder, ours);

        List<uint> theirs = FfmpegCrcs(clip!, decoder.Width, decoder.Height, mp4.Video!.Codec, false);
        Assert.Equal(theirs, ours);
    }

    private static void AssertFramesMatch(string hex, int width, int height, uint[] expected)
    {
        var decoder = new H264Decoder();
        decoder.DecodeAnnexB(Convert.FromHexString(hex));
        decoder.Flush();

        var crcs = new List<uint>();
        Drain(decoder, crcs);

        Assert.Equal(width, decoder.Width);
        Assert.Equal(height, decoder.Height);
        Assert.Equal(expected, crcs);
    }

    /// <summary>Takes every ready frame and records the CRC-32 of its cropped planes, as ffmpeg's rawvideo lays them out.</summary>
    private static void Drain(H264Decoder decoder, List<uint> crcs)
    {
        while (decoder.TryGetFrame(out DecodedFrame frame))
        {
            crcs.Add(Crc(frame));
            frame.Release();
        }
    }

    private static uint Crc(DecodedFrame frame)
    {
        var crc = new Crc32();
        int w = frame.Width;
        int h = frame.Height;

        for (int y = 0; y < h; y++)
        {
            crc.Append(frame.Y.AsSpan((frame.CropTop + y) * frame.Stride + frame.CropLeft, w));
        }

        if (frame.ChromaFormat != 0)
        {
            bool full = frame.ChromaFormat == 3;
            int cw = full ? w : (w + 1) / 2;
            int ch = full ? h : (h + 1) / 2;
            int cropLeft = full ? frame.CropLeft : frame.CropLeft / 2;
            int cropTop = full ? frame.CropTop : frame.CropTop / 2;

            foreach (byte[] plane in new[] { frame.Cb, frame.Cr })
            {
                for (int y = 0; y < ch; y++)
                {
                    crc.Append(plane.AsSpan((cropTop + y) * frame.ChromaStride + cropLeft, cw));
                }
            }
        }

        return crc.GetCurrentHashAsUInt32();
    }

    internal static string? FindClip(string name)
    {
        string? repository = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryRoot")?.Value;

        if (repository is null)
        {
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(repository, "..", "ContentWorkspace", "enhanced", "video", name));
        return File.Exists(path) ? path : null;
    }

    internal static bool HasFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static List<uint> FfmpegCrcs(string clip, int width, int height, string codec, bool is444)
    {
        using var probe = Process.Start(new ProcessStartInfo(
            "ffprobe", $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{clip}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        string pixFmt = probe.StandardOutput.ReadToEnd().Trim();
        probe.WaitForExit();
        is444 = pixFmt == "yuv444p";

        using var ffmpeg = Process.Start(new ProcessStartInfo(
            "ffmpeg", $"-v error -i \"{clip}\" -f rawvideo -pix_fmt {pixFmt} -")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        int cw = is444 ? width : (width + 1) / 2;
        int ch = is444 ? height : (height + 1) / 2;
        int size = width * height + 2 * cw * ch;
        var buffer = new byte[size];
        var crcs = new List<uint>();
        Stream output = ffmpeg.StandardOutput.BaseStream;

        while (true)
        {
            int got = 0;

            while (got < size)
            {
                int n = output.Read(buffer, got, size - got);

                if (n <= 0)
                {
                    break;
                }

                got += n;
            }

            if (got < size)
            {
                break;
            }

            crcs.Add(Crc32.HashToUInt32(buffer));
        }

        ffmpeg.WaitForExit();
        return crcs;
    }
}

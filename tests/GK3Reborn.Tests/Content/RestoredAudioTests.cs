using System.Buffers.Binary;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Tests.Formats;
using GK3Reborn.Tools.Stages;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>Restored sound follows override, ReBarn, original precedence.</summary>
public sealed class RestoredAudioTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gk3r-restored-audio").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_restored_pack_wins_over_the_original_barn()
    {
        string data = Original("LINE.QR1", 100);
        string pack = Pack(("LINE.QR1", (short)200));

        using GameArchives archives = GameArchives.Open(data);
        using RebarnContent restored = RebarnContent.OpenFiles([pack]);
        var sounds = new SoundLibrary(archives, restored);

        Assert.Equal(200, sounds.Read("LINE.QR1")!.Samples[0]);
    }

    [Fact]
    public void Dialogue_sequence_suffixes_remain_distinct_pack_keys()
    {
        string data = Original("LINE.QR1", 100);
        string pack = Pack(("LINE.QR1", (short)201), ("LINE.QR2", (short)202));

        using GameArchives archives = GameArchives.Open(data);
        using RebarnContent restored = RebarnContent.OpenFiles([pack]);
        var sounds = new SoundLibrary(archives, restored);

        Assert.Equal(201, sounds.Read("LINE.QR1")!.Samples[0]);
        Assert.Equal(202, sounds.Read("LINE.QR2")!.Samples[0]);
    }

    [Fact]
    public void A_wrapped_wav_override_wins_over_the_restored_pack()
    {
        string data = Original("LINE.QR1", 100);
        string pack = Pack(("LINE.QR1", (short)200));
        string overridesDirectory = Path.Combine(_root, "overrides", "audio");
        Directory.CreateDirectory(overridesDirectory);
        File.WriteAllBytes(Path.Combine(overridesDirectory, "LINE.QR1.wav"), Wave(300));

        using GameArchives archives = GameArchives.Open(data);
        using RebarnContent restored = RebarnContent.OpenFiles([pack]);
        ContentOverrides overrides = ContentOverrides.Open(Path.Combine(_root, "overrides"));
        archives.Overrides = overrides;
        restored.Overrides = overrides;
        var sounds = new SoundLibrary(archives, restored);

        Assert.Equal(300, sounds.Read("LINE.QR1")!.Samples[0]);
    }

    [Fact]
    public void Missing_restored_audio_falls_back_to_the_barn()
    {
        string data = Original("ONLY.WAV", 123);
        using GameArchives archives = GameArchives.Open(data);
        using RebarnContent restored = RebarnContent.OpenFiles([]);

        Assert.Equal(123, new SoundLibrary(archives, restored).Read("ONLY")!.Samples[0]);
    }

    [Fact]
    public void The_content_packer_recurses_both_audio_lanes_and_round_trips_their_names()
    {
        string workspace = Path.Combine(_root, "ContentWorkspace");
        string dialogue = Path.Combine(workspace, "enhanced", "audio", "dialogue");
        string sfx = Path.Combine(workspace, "enhanced", "audio", "sfx");
        string output = Path.Combine(_root, "packs");
        Directory.CreateDirectory(dialogue);
        Directory.CreateDirectory(sfx);
        File.WriteAllBytes(Path.Combine(dialogue, "LINE.QR1.wav"), Wave(401));
        File.WriteAllBytes(Path.Combine(sfx, "DOOR.WAV.wav"), Wave(402));

        PackKind[] plan =
        [
            new(RebarnKind.Audio, "enhanced/audio", null, false, 0,
                "Reborn", "*.wav", Recursive: true),
        ];

        Assert.True(new ContentPackStage(_ => { }).Run(
            workspace, output, plan, texconv: "unused", useSizePlan: false));

        using RebarnContent packed = RebarnContent.OpenFiles(
            [Path.Combine(output, "Reborn.rebarn")]);
        Assert.Contains("LINE.QR1", packed.Names(RebarnKind.Audio));
        Assert.Contains("DOOR.WAV", packed.Names(RebarnKind.Audio));
        Assert.Equal(401, WavFile.Read(
            packed.Read(RebarnKind.Audio, "LINE.QR1")!, "LINE.QR1", new DiagnosticBag())!.Samples[0]);

        string extracted = Path.Combine(_root, "unpacked");
        ContentExtract.Result result = ContentExtract.FromPacks(
            packed, extracted, [RebarnKind.Audio], null, asPng: false, _ => { });

        Assert.Equal(2, result.Written);
        Assert.True(File.Exists(Path.Combine(extracted, "audio", "LINE.QR1.wav")));
        Assert.True(File.Exists(Path.Combine(extracted, "audio", "DOOR.WAV.wav")));

        ContentExtract.Result filtered = ContentExtract.FromPacks(
            packed, Path.Combine(_root, "one"), [RebarnKind.Audio],
            "DOOR.WAV.wav", asPng: false, _ => { });
        Assert.Equal(1, filtered.Written);
    }

    private string Original(string name, short sample)
    {
        string directory = Path.Combine(_root, "Data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "core.brn"),
            new BarnFixture().AddStored(name, Wave(sample)).Build());
        return directory;
    }

    private string Pack(params (string Name, short Sample)[] sounds)
    {
        var builder = new RebarnBuilder();
        foreach ((string name, short sample) in sounds)
        {
            Assert.True(builder.AddBytes(
                RebarnKind.Audio, name, Wave(sample), RebarnPayload.Wav));
        }

        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".rebarn");
        builder.Write(path);
        return path;
    }

    private static byte[] Wave(short sample)
    {
        byte[] output = new byte[46];
        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), 38);
        "WAVEfmt "u8.CopyTo(output.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), 8000);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(34), 16);
        "data"u8.CopyTo(output.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), 2);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(44), sample);
        return output;
    }
}

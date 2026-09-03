using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Tests.Formats;
using GK3Reborn.Tools.Stages;
using Xunit;

namespace GK3Reborn.Tests.Tools;

/// <summary>The restoration workspace begins with a complete, correctly split corpus.</summary>
public sealed class AudioExtractTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gk3r-audio").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Yak_references_split_dialogue_from_general_audio_and_both_are_decoded()
    {
        string source = Path.Combine(_root, "Data");
        string workspace = Path.Combine(_root, "ContentWorkspace");
        Directory.CreateDirectory(source);

        byte[] barn = new BarnFixture()
            .AddStored("ELINE.YAK", "[HEADER]\n1\n[SOUNDS]\n1\n0,A0LINE.QR1,100\n")
            .AddStored("EHOOK.YAK", "[HEADER]\n1\n[SOUNDS]\n1\n0,PHOHOOKIN.WAV,100\n")
            .AddStored("A0LINE.QR1", Wave([100, -100]))
            .AddStored("PHOHOOKIN.WAV", Wave([50, -50]))
            .AddStored("DOOR.WAV", Wave([200, -200, 0]))
            .Build();
        File.WriteAllBytes(Path.Combine(source, "core.brn"), barn);

        var diagnostics = new DiagnosticBag();
        var manifest = new AudioExtractStage(_ => { }).Run(
            source, workspace, writeFiles: true, diagnostics);

        Assert.Equal(3, manifest.Summary["assets"]);
        Assert.Equal(1, manifest.Summary["dialogue"]);
        Assert.Equal(2, manifest.Summary["sfx"]);
        Assert.Equal(3, manifest.Summary["normalized"]);
        Assert.False(diagnostics.HasErrors);

        Assert.True(File.Exists(Path.Combine(
            workspace, "raw", "audio", "dialogue", "A0LINE.QR1")));
        Assert.True(File.Exists(Path.Combine(
            workspace, "normalized", "audio", "dialogue", "A0LINE.QR1.wav")));
        Assert.True(File.Exists(Path.Combine(
            workspace, "raw", "audio", "sfx", "DOOR.WAV")));
        Assert.True(File.Exists(Path.Combine(
            workspace, "normalized", "audio", "sfx", "DOOR.WAV.wav")));
        Assert.True(File.Exists(Path.Combine(
            workspace, "normalized", "audio", "sfx", "PHOHOOKIN.WAV.wav")));
        Assert.True(Directory.Exists(Path.Combine(
            workspace, "enhanced", "audio", "dialogue")));
        Assert.True(Directory.Exists(Path.Combine(
            workspace, "enhanced", "audio", "sfx")));

        Assert.Contains(manifest.Assets, a =>
            a.Name == "A0LINE.QR1" && a.Lane == "dialogue" && a.Yaks.Contains("ELINE.YAK"));
    }

    private static byte[] Wave(short[] samples)
    {
        byte[] output = new byte[44 + samples.Length * 2];
        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), output.Length - 8);
        "WAVEfmt "u8.CopyTo(output.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), 8000);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(34), 16);
        "data"u8.CopyTo(output.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(44 + i * 2), samples[i]);
        }

        return output;
    }
}

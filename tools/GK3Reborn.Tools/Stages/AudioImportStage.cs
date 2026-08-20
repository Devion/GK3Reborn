using System.Buffers.Binary;
using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Tools.Media;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Transcodes the game's sounds into something the runtime can play.
/// </summary>
/// <remarks>
/// <para>
/// GK3 ships 7,852 sounds and 7,656 of them are an MP3 stream inside a RIFF header —
/// format tag 85. Every line of dialogue and almost every soundtrack is one; the 196 that
/// are honestly PCM are footsteps and a fly. So "read the WAV files" does not get you a
/// game with sound in it.
/// </para>
/// <para>
/// <c>Plan/01</c> settles where that gets fixed: conversion is an import concern and the
/// runtime never shells out. So this decodes the corpus once, into the content workspace,
/// beside the textures that were converted out of BMP for the same reason. The engine then
/// reads plain PCM and needs no decoder, no codec licence and no external process while a
/// scene is running.
/// </para>
/// <para>
/// Names are kept exactly as the archives hold them, extension and all — a script asks for
/// <c>A0NQIB44.QR1</c> and that is what it must find. The <c>.wav</c> is appended rather
/// than substituted, because two archive entries can differ only in the extension that
/// carries their sequence number.
/// </para>
/// </remarks>
public sealed class AudioImportStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public AudioImportStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Transcodes every sound in the archives.</summary>
    /// <param name="source">The game's data directory.</param>
    /// <param name="workspace">The content workspace root.</param>
    /// <param name="ffmpegDirectory">Where to look for ffmpeg first, if anywhere.</param>
    /// <param name="force">Redo files that are already present.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True when every sound came out.</returns>
    public bool Run(
        string source,
        string workspace,
        string? ffmpegDirectory,
        bool force,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (FfmpegTools.Locate(ffmpegDirectory, diagnostics) is not { } ffmpeg)
        {
            return false;
        }

        _log($"ffmpeg: {ffmpeg.Version}");

        using GameArchives archives = GameArchives.Open(source);
        string output = Path.Combine(workspace, "normalized", "audio-pcm");
        Directory.CreateDirectory(output);

        IReadOnlyList<string> sounds = Sounds(archives);
        _log($"{sounds.Count} sounds in {archives.Count} archives");

        int copied = 0;
        int decoded = 0;
        int kept = 0;
        int refused = 0;
        string scratch = Path.Combine(Path.GetTempPath(), "gk3reborn-audio");
        Directory.CreateDirectory(scratch);

        foreach (string name in sounds)
        {
            string destination = Path.Combine(output, name + ".wav");

            if (!force && File.Exists(destination))
            {
                kept++;
                continue;
            }

            if (archives.Read(name) is not { } bytes)
            {
                refused++;
                continue;
            }

            if (Tag(bytes) == 1)
            {
                // Already PCM. Copying rather than transcoding keeps the samples bit-exact
                // and skips 196 pointless process launches.
                File.WriteAllBytes(destination, bytes);
                copied++;
                continue;
            }

            // ffmpeg wants a file. The name is kept so its error messages name the sound.
            string input = Path.Combine(scratch, name);
            File.WriteAllBytes(input, bytes);

            ProcessResult result = ffmpeg.RunFfmpeg(
            [
                "-hide_banner", "-v", "error", "-y",
                "-i", input,
                "-acodec", "pcm_s16le",
                destination,
            ]);

            File.Delete(input);

            if (result.Succeeded && File.Exists(destination))
            {
                decoded++;
            }
            else
            {
                refused++;
                diagnostics.Add(new Diagnostic(
                    "GK3R2010", DiagnosticSeverity.Warning,
                    "A sound could not be decoded, so nothing will play it.",
                    name, null, "a decodable audio stream",
                    result.StandardError.Trim() is { Length: > 0 } e ? e : "no output",
                    "Check the archive entry; it may not be audio at all."));
            }

            if ((decoded + copied) % 500 == 0 && decoded + copied > 0)
            {
                _log($"  {decoded + copied} of {sounds.Count}…");
            }
        }

        _log(string.Create(CultureInfo.InvariantCulture,
            $"{decoded} decoded, {copied} already PCM, {kept} left alone, {refused} refused"));
        _log($"into {output}");

        return refused == 0;
    }

    /// <summary>
    /// Every archive entry that is a sound.
    /// </summary>
    /// <remarks>
    /// Extension is no guide — dialogue is <c>.QR1</c>, <c>.N61</c> and hundreds of others,
    /// because the last characters carry a sequence number rather than a type. What every
    /// one of them does have is a RIFF header, so that is what is asked.
    /// </remarks>
    private static IReadOnlyList<string> Sounds(GameArchives archives) =>
        [.. archives.Names().Where(n => archives.Read(n) is { Length: >= 12 } b && Riff(b))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    private static bool Riff(ReadOnlySpan<byte> bytes) =>
        bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8);

    /// <summary>The format tag, or zero when there is no fmt chunk.</summary>
    private static int Tag(ReadOnlySpan<byte> bytes)
    {
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            long size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at + 4, 4));
            int body = at + 8;

            if (size < 0 || body + size > bytes.Length)
            {
                return 0;
            }

            if (bytes.Slice(at, 4).SequenceEqual("fmt "u8) && size >= 2)
            {
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
            }

            at = body + (int)size + ((int)size & 1);
        }

        return 0;
    }
}

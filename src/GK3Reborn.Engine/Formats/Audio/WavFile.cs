using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using GK3Reborn.Foundation.Diagnostics;
using NLayer;

namespace GK3Reborn.Formats.Audio;

/// <summary>
/// A RIFF/WAVE file, decoded to signed 16-bit samples.
/// </summary>
/// <remarks>
/// <para>
/// GK3's 7,852 sounds are RIFF files, and 7,656 of them — 97.5% — are not really WAV at
/// all: format tag 85 is an MP3 stream wrapped in a RIFF header. Only 196 are plain PCM.
/// So a reader that handles PCM alone can play the footsteps and the fly loop and nothing
/// anybody says.
/// </para>
/// <para>
/// Both are read here, in process. <c>Plan/01</c> rules out an external process at runtime,
/// which is a different thing from ruling out decoding — and the difference is worth 3.7 GB:
/// keeping a decoded copy of the corpus on disk cost that to save a few milliseconds a
/// sound, while the compressed originals are 347 MB and already inside the archives.
/// </para>
/// </remarks>
public sealed class WavFile
{
    /// <summary>Uncompressed pulse-code modulation.</summary>
    public const int FormatPcm = 1;

    /// <summary>An MP3 stream wearing a RIFF header.</summary>
    public const int FormatMpegLayer3 = 85;

    /// <summary>How much of an MP3 to decode per call, in bytes.</summary>
    /// <remarks>
    /// Verified against ffmpeg at 4,608, 16,384 and 65,536 bytes; 16,384 is four frames of
    /// stereo and a little over seven of mono.
    /// </remarks>
    private const int Block = 16384;

    private WavFile(string name, int channels, int sampleRate, short[] samples)
    {
        Name = name;
        Channels = channels;
        SampleRate = sampleRate;
        Samples = samples;
    }

    /// <summary>Name it was read under.</summary>
    public string Name { get; }

    /// <summary>One for mono, two for stereo.</summary>
    public int Channels { get; }

    /// <summary>Frames a second.</summary>
    public int SampleRate { get; }

    /// <summary>The samples, interleaved by channel.</summary>
    public short[] Samples { get; }

    /// <summary>How many frames long it is, counting a stereo pair as one.</summary>
    public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;

    /// <summary>How long it lasts, in seconds.</summary>
    public double Duration => SampleRate > 0 ? (double)FrameCount / SampleRate : 0;

    /// <summary>Reads a RIFF file.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives a reason when it cannot be read.</param>
    /// <returns>The sound, or null.</returns>
    public static WavFile? Read(ReadOnlySpan<byte> bytes, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (bytes.Length < 12 ||
            !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1120", DiagnosticSeverity.Warning,
                "A sound is not a RIFF/WAVE file, so it cannot be played.",
                name, null, "RIFF….WAVE",
                bytes.Length >= 4 ? Text(bytes[..4]) : "an empty file",
                "Check that the archive entry is audio and not something else."));

            return null;
        }

        int format = 0;
        int channels = 0;
        int rate = 0;
        int bits = 0;
        ReadOnlySpan<byte> data = default;

        // Chunks are id, length, payload, padded to even. Walking them rather than
        // assuming fmt-then-data matters: these files carry a `fact` chunk between the two.
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            ReadOnlySpan<byte> id = bytes.Slice(at, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at + 4, 4));
            int body = at + 8;

            if (size < 0 || body + size > bytes.Length)
            {
                size = bytes.Length - body;
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = bytes.Slice(body, (int)size);
            }

            at = body + (int)size + ((int)size & 1);
        }

        if (format == FormatMpegLayer3)
        {
            return Mpeg(data, name, diagnostics);
        }

        if (format != FormatPcm)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1121", DiagnosticSeverity.Warning,
                "A sound is in a compressed format nothing here decodes.",
                name, null, "format tag 1 or 85",
                format.ToString(CultureInfo.InvariantCulture),
                "Convert it to 16-bit PCM."));

            return null;
        }

        if (channels is < 1 or > 2 || rate <= 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1122", DiagnosticSeverity.Warning,
                "A sound has a channel count or sample rate nothing can play.",
                name, null, "one or two channels at a positive rate",
                string.Create(CultureInfo.InvariantCulture, $"{channels} channels at {rate} Hz"),
                "Check the fmt chunk."));

            return null;
        }

        return new WavFile(name, channels, rate, Decode(data, bits));
    }

    /// <summary>
    /// Decodes the MP3 stream inside a RIFF header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which is 7,656 of the game's 7,852 sounds — everything anybody says and almost every
    /// soundtrack. The <c>fmt</c> chunk describes the MP3 and the <c>data</c> chunk is the
    /// MP3 itself, so the frames are handed to the decoder as they stand and the header's
    /// channel count and rate are ignored in favour of what the stream actually says.
    /// </para>
    /// <para>
    /// In process, and not by shelling out. <c>Plan/01</c> rules out an external process at
    /// runtime, which is a different thing from ruling out decoding: keeping a decoded copy
    /// of the corpus on disk cost 3.7 GB to save what turns out to be a few milliseconds a
    /// sound.
    /// </para>
    /// </remarks>
    private static WavFile? Mpeg(ReadOnlySpan<byte> data, string name, DiagnosticBag diagnostics)
    {
        if (data.Length == 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1123", DiagnosticSeverity.Warning,
                "A sound's RIFF header promises an MP3 and contains nothing.",
                name, null, "a data chunk", "empty",
                "The archive entry may be truncated."));

            return null;
        }

        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var mpeg = new MpegFile(stream);

            int channels = Math.Clamp(mpeg.Channels, 1, 2);

            // ReadSamplesInt16, not ReadSamples: the byte overload of ReadSamples writes
            // *floats* into the buffer. Reading those as 16-bit gives exactly twice as many
            // samples as the sound has, each one the bit pattern of half a float — which
            // sounds like static and measures as a clip of the right name and the wrong
            // length.
            //
            // Always into a block, always at index 0. Decoding straight into the destination
            // is the obvious way to write this and it is wrong twice over: asked for a whole
            // clip in one call the decoder returns the full count and quietly leaves the back
            // of it silent, and asked to write at a non-zero index it returns the right count
            // of the wrong samples. Neither shows up as an error or a short clip — the length
            // is right to the sample either way, which is why this is worth the copy.
            int expected = (int)(mpeg.Duration.TotalSeconds * mpeg.SampleRate) * channels;
            short[] samples = new short[Math.Max(expected + mpeg.SampleRate, Block / 2)];
            byte[] block = new byte[Block];
            int count = 0;
            int read;

            while ((read = mpeg.ReadSamplesInt16(block, 0, Block)) > 0)
            {
                int got = read / 2;

                if (count + got > samples.Length)
                {
                    Array.Resize(ref samples, Math.Max(samples.Length * 2, count + got));
                }

                MemoryMarshal.Cast<byte, short>(block.AsSpan(0, got * 2))
                    .CopyTo(samples.AsSpan(count));

                count += got;
            }

            Array.Resize(ref samples, count);

            return new WavFile(name, channels, mpeg.SampleRate, samples);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or
                                       IndexOutOfRangeException or ArgumentException)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1124", DiagnosticSeverity.Warning,
                "A sound's MP3 stream could not be decoded.",
                name, null, "a readable MPEG stream", ex.Message,
                "The archive entry may be damaged; two of the game's own files are."));

            return null;
        }
    }

    /// <summary>
    /// Widens the sample data to signed 16-bit.
    /// </summary>
    /// <remarks>
    /// Eight-bit WAV is unsigned with 128 as silence, which is the one trap here: read it
    /// as signed and every sound is a loud square wave. GK3's PCM is all 16-bit, but the
    /// import writes what it is given.
    /// </remarks>
    private static short[] Decode(ReadOnlySpan<byte> data, int bits)
    {
        switch (bits)
        {
            case 16:
            {
                short[] samples = new short[data.Length / 2];

                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
                }

                return samples;
            }

            case 8:
            {
                short[] samples = new short[data.Length];

                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = (short)((data[i] - 128) << 8);
                }

                return samples;
            }

            default:
                return [];
        }
    }

    private static string Text(ReadOnlySpan<byte> bytes) =>
        System.Text.Encoding.ASCII.GetString(bytes);
}

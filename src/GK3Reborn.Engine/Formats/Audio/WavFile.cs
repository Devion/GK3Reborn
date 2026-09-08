using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using GK3Reborn.Foundation.Diagnostics;
using NLayer;

namespace GK3Reborn.Formats.Audio;

/// <summary>
/// A RIFF/WAVE file, decoded to signed 16-bit samples.
/// </summary>
public sealed class WavFile
{
    /// <summary>Uncompressed pulse-code modulation.</summary>
    public const int FormatPcm = 1;

    /// <summary>32-bit IEEE floating-point PCM.</summary>
    public const int FormatIeeeFloat = 3;

    /// <summary>An MP3 stream wearing a RIFF header.</summary>
    public const int FormatMpegLayer3 = 85;

    /// <summary>How much of an MP3 to decode per call, in bytes.</summary>
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

    /// <summary>Builds a sound from samples somebody else decoded.</summary>
    /// <param name="name">Name it will be known by.</param>
    /// <param name="samples">Interleaved sixteen-bit samples.</param>
    /// <param name="channels">One for mono, two for stereo.</param>
    /// <param name="sampleRate">Frames a second.</param>
    /// <returns>The sound.</returns>
    public static WavFile FromSamples(
        string name, short[] samples, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        return new WavFile(name, channels, sampleRate, samples);
    }

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

        if (format is not (FormatPcm or FormatIeeeFloat))
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1121", DiagnosticSeverity.Warning,
                "A sound is in a compressed format nothing here decodes.",
                name, null, "format tag 1, 3 or 85",
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

        short[]? samples = Decode(data, format, bits);

        if (samples is null)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1125", DiagnosticSeverity.Warning,
                "A sound uses a PCM word size nothing here decodes.",
                name, null, "8, 16, 24 or 32-bit PCM, or 32-bit float",
                $"format {format}, {bits} bits",
                "Export the restored master as 24-bit PCM or 32-bit float WAV."));

            return null;
        }

        return new WavFile(name, channels, rate, samples);
    }

    /// <summary>Writes the decoded sound as an ordinary 16-bit PCM RIFF/WAVE file.</summary>
    /// <returns>A lossless representation of the samples held by this instance.</returns>
    public byte[] ToPcmWave()
    {
        const int Header = 44;
        int dataBytes = checked(Samples.Length * sizeof(short));
        byte[] output = new byte[checked(Header + dataBytes)];

        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), output.Length - 8);
        "WAVE"u8.CopyTo(output.AsSpan(8));
        "fmt "u8.CopyTo(output.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(20), FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(22), checked((ushort)Channels));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(
            output.AsSpan(28), checked(SampleRate * Channels * sizeof(short)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(32), checked((ushort)(Channels * sizeof(short))));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(34), 16);
        "data"u8.CopyTo(output.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), dataBytes);

        for (int i = 0; i < Samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(Header + i * 2), Samples[i]);
        }

        return output;
    }

    /// <summary>
    /// Decodes the MP3 stream inside a RIFF header.
    /// </summary>
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

            // <b>Decoded as floats and clamped here, not by the decoder.</b> An MP3 is a
            // lossy reconstruction and its output routinely overshoots the waveform that
            // was encoded, so a clip mastered near full scale decodes to values above one.
            // NLayer's own 16-bit conversion lets those wrap: a sample riding at +32700
            // comes back as -32742, which is a full-scale spike one sample wide, and a run
            // of them is what a crackle is.
            //
            // Nobody could hear it in English. The English lines a player actually hears
            // are the restored masters in enhanced/audio, which are 24-bit PCM and peak
            // around -3 dB; it is the 1999 dubs that are hot. Measured on the French
            // A0144J44.N61: 31 samples at full scale and a one-sample swing of 65,503 out
            // of a possible 65,535, against a peak of 16,808 and no jump over 3,913 in the
            // English recording of the same line. German and Italian are dubs too.
            //
            // Always into a block, always at index 0. Decoding straight into the destination
            // is the obvious way to write this and it is wrong twice over: asked for a whole
            // clip in one call the decoder returns the full count and quietly leaves the back
            // of it silent, and asked to write at a non-zero index it returns the right count
            // of the wrong samples. Neither shows up as an error or a short clip — the length
            // is right to the sample either way, which is why this is worth the copy.
            int expected = (int)(mpeg.Duration.TotalSeconds * mpeg.SampleRate) * channels;
            short[] samples = new short[Math.Max(expected + mpeg.SampleRate, Block / 2)];
            float[] block = new float[Block / sizeof(float)];
            int count = 0;
            int got;

            while ((got = mpeg.ReadSamples(block, 0, block.Length)) > 0)
            {
                if (count + got > samples.Length)
                {
                    Array.Resize(ref samples, Math.Max(samples.Length * 2, count + got));
                }

                for (int i = 0; i < got; i++)
                {
                    samples[count + i] = Clamped(block[i]);
                }

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
    /// One decoded sample as signed 16-bit, held inside the range rather than wrapped.
    /// </summary>
    /// <param name="sample">The sample, nominally between -1 and 1.</param>
    /// <returns>The sample, clamped.</returns>
    internal static short Clamped(float sample) => float.IsFinite(sample)
        ? (short)Math.Clamp(
            MathF.Round(sample * 32767f), short.MinValue, short.MaxValue)
        : (short)0;

    /// <summary>
    /// Widens the sample data to signed 16-bit.
    /// </summary>
    private static short[]? Decode(ReadOnlySpan<byte> data, int format, int bits)
    {
        if (format == FormatIeeeFloat)
        {
            if (bits != 32)
            {
                return null;
            }

            short[] floating = new short[data.Length / sizeof(float)];

            for (int i = 0; i < floating.Length; i++)
            {
                float sample = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)));
                floating[i] = (short)Math.Round(
                    Math.Clamp(sample, -1f, 1f) * (sample < 0 ? 32768f : 32767f));
            }

            return floating;
        }

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

            case 24:
            {
                short[] samples = new short[data.Length / 3];

                for (int i = 0; i < samples.Length; i++)
                {
                    int at = i * 3;
                    int value = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);
                    if ((value & 0x0080_0000) != 0)
                    {
                        value |= unchecked((int)0xFF00_0000);
                    }

                    samples[i] = (short)(value >> 8);
                }

                return samples;
            }

            case 32:
            {
                short[] samples = new short[data.Length / 4];

                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = (short)(
                        BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)) >> 16);
                }

                return samples;
            }

            default:
                return null;
        }
    }

    private static string Text(ReadOnlySpan<byte> bytes) =>
        System.Text.Encoding.ASCII.GetString(bytes);
}

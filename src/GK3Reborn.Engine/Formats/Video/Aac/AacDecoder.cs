// Algorithms follow ISO/IEC 14496-3 subpart 4 (AAC-LC); the constant tables are in
// AacTables.cs and AacCodebookTables.cs, transcribed from the standard as carried by
// JAADec (public domain).

namespace GK3Reborn.Formats.Video.Aac;

/// <summary>
/// AAC-LC decoder for raw (non-ADTS) access units as stored in MP4 <c>mp4a</c> samples.
/// </summary>
/// <remarks>
/// <para>
/// The port plays the game's cut-scene movies from MP4, whose audio track is AAC-LC.
/// Decoding it in managed code keeps deployment a single self-contained build on every
/// platform: no FFmpeg binaries to ship per OS and architecture, no native loader, no
/// licence bundle. The whole decoder is a few thousand lines because AAC-LC without
/// SBR is a small format: Huffman-coded quantised spectra, a handful of stereo tools,
/// and an overlapped inverse MDCT.
/// </para>
/// <para>
/// Supported: single, pair and LFE channel elements, all window sequences and both
/// window shapes, all spectral codebooks, pulse data, TNS, M/S, intensity and
/// perceptual noise substitution, and the skip-over of data, fill (including SBR
/// payloads) and program-config elements. Not supported: 960-sample frames, main
/// profile prediction, LTP, SBR/PS reconstruction and more than two channels. The
/// decoder is not thread-safe; use one instance per stream.
/// </para>
/// </remarks>
public sealed class AacDecoder
{
    private const int LongStart = 1, EightShort = 2, LongStop = 3; // window_sequence; 0 is ONLY_LONG
    private const int ZeroBook = 0, NoiseBook = 13, IntensityBook2 = 14, IntensityBook = 15;
    private const int FrameLength = 1024;
    private const int ShortLength = 128;
    private const int ShortStart = (FrameLength - ShortLength) / 2; // 448
    private const int NoiseSeed = 0x1F2E3D4C;

    private readonly int _sampleRateIndex;
    private readonly ChannelState[] _states;
    private readonly Ics _left = new();
    private readonly Ics _right = new();
    private readonly bool[] _msUsed = new bool[8 * 64];
    private readonly Imdct _longImdct = new(2 * FrameLength);
    private readonly Imdct _shortImdct = new(2 * ShortLength);
    private readonly float[] _time = new float[2 * FrameLength];
    private readonly float[] _shortTime = new float[2 * ShortLength];
    private readonly float[][] _pcm;
    private readonly float[] _tnsLpc = new float[21];
    private readonly float[] _tnsTemp = new float[21];
    private uint _noiseState = NoiseSeed;

    /// <summary>Parses an AudioSpecificConfig and prepares a decoder for that stream.</summary>
    /// <param name="audioSpecificConfig">The DecoderSpecificInfo bytes from the MP4 <c>esds</c> box.</param>
    /// <exception cref="FormatParseException">The configuration is malformed.</exception>
    /// <exception cref="NotSupportedException">The stream needs a tool this decoder does not implement.</exception>
    public AacDecoder(ReadOnlySpan<byte> audioSpecificConfig)
    {
        ParseConfig(audioSpecificConfig, out int sampleRate, out int channels, out int objectType);
        SampleRate = sampleRate;
        Channels = channels;
        ObjectType = objectType;
        _sampleRateIndex = AacTables.SampleRateToIndex(sampleRate);

        _states = new ChannelState[channels];
        _pcm = new float[channels][];
        for (int i = 0; i < channels; i++)
        {
            _states[i] = new ChannelState();
            _pcm[i] = new float[FrameLength];
        }
    }

    /// <summary>Core sample rate, e.g. 48000 (SBR-signalled streams report the core rate, not the doubled one).</summary>
    public int SampleRate { get; }

    /// <summary>Output channels, 1 or 2.</summary>
    public int Channels { get; }

    /// <summary>The core audio object type (2 for LC).</summary>
    public int ObjectType { get; }

    /// <summary>Samples per channel produced by each access unit.</summary>
    public int FrameSamples { get; } = FrameLength;

    /// <summary>
    /// Reads the sample rate, channel count and core object type from an AudioSpecificConfig
    /// without committing to decode it. Returns false when the bytes are not a config this
    /// decoder could open (malformed, or a profile it does not support).
    /// </summary>
    public static bool TryParseConfig(ReadOnlySpan<byte> asc, out int sampleRate, out int channels, out int objectType)
    {
        try
        {
            ParseConfig(asc, out sampleRate, out channels, out objectType);
            return true;
        }
        catch (Exception e) when (e is FormatParseException or NotSupportedException)
        {
            sampleRate = 0;
            channels = 0;
            objectType = 0;
            return false;
        }
    }

    /// <summary>Forgets the overlap of the previous frame, for a seek or a stream restart.</summary>
    public void Reset()
    {
        foreach (ChannelState state in _states)
        {
            Array.Clear(state.Overlap);
            state.PreviousShape = 0;
        }

        _noiseState = NoiseSeed;
    }

    /// <summary>Decodes one access unit into interleaved 16-bit PCM.</summary>
    /// <returns>Samples per channel written (always 1024).</returns>
    /// <exception cref="FormatParseException">The access unit is corrupt.</exception>
    public int Decode(ReadOnlySpan<byte> accessUnit, Span<short> output)
    {
        int channels = Channels;
        if (output.Length < FrameLength * channels)
        {
            throw new ArgumentException("output must hold 1024 samples per channel", nameof(output));
        }

        DecodeFrame(accessUnit);
        for (int c = 0; c < channels; c++)
        {
            float[] pcm = _pcm[c];
            for (int i = 0; i < FrameLength; i++)
            {
                float v = MathF.Round(pcm[i]);
                output[i * channels + c] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
        }

        return FrameLength;
    }

    /// <summary>Decodes one access unit into interleaved float PCM in [-1, 1].</summary>
    /// <returns>Samples per channel written (always 1024).</returns>
    /// <exception cref="FormatParseException">The access unit is corrupt.</exception>
    public int Decode(ReadOnlySpan<byte> accessUnit, Span<float> output)
    {
        int channels = Channels;
        if (output.Length < FrameLength * channels)
        {
            throw new ArgumentException("output must hold 1024 samples per channel", nameof(output));
        }

        DecodeFrame(accessUnit);
        const float scale = 1.0f / 32768.0f;
        for (int c = 0; c < channels; c++)
        {
            float[] pcm = _pcm[c];
            for (int i = 0; i < FrameLength; i++)
            {
                output[i * channels + c] = Math.Clamp(pcm[i] * scale, -1.0f, 1.0f);
            }
        }

        return FrameLength;
    }

    // ---------------------------------------------------------------- configuration

    private static void ParseConfig(ReadOnlySpan<byte> asc, out int sampleRate, out int channels, out int objectType)
    {
        if (asc.Length < 2)
        {
            throw new FormatParseException("AAC: AudioSpecificConfig is shorter than two bytes");
        }

        AacBitReader reader = new(asc);
        int aot = ReadObjectType(ref reader);
        int sfi = reader.ReadInt(4);
        sampleRate = sfi == 15 ? (int)reader.ReadBitsLong(24) : SampleRateFor(sfi);
        int channelConfig = reader.ReadInt(4);

        if (aot == 5 || aot == 29)
        {
            // Explicit SBR/PS signalling: the extension rate is what SBR would produce; the
            // core decoder keeps the rate read above and ignores the SBR payloads.
            int extSfi = reader.ReadInt(4);
            if (extSfi == 15)
            {
                reader.ReadBitsLong(24);
            }

            aot = ReadObjectType(ref reader);
            if (aot == 22)
            {
                reader.ReadInt(4);
            }
        }

        objectType = aot;
        if (aot != 2)
        {
            throw new NotSupportedException($"AAC: audio object type {aot} is not supported (only AAC-LC)");
        }

        // GASpecificConfig
        bool frameLengthFlag = reader.ReadBool();
        if (frameLengthFlag)
        {
            throw new NotSupportedException("AAC: 960-sample frames are not supported");
        }

        if (reader.ReadBool())
        {
            reader.ReadInt(14); // coreCoderDelay
        }

        bool extensionFlag = reader.ReadBool();
        if (channelConfig == 0)
        {
            channels = SkipProgramConfig(ref reader, out _);
        }
        else
        {
            // Configurations 1..7 are mono, stereo, 3.0, 4.0, 5.0, 5.1 and 7.1.
            channels = channelConfig switch
            {
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                5 => 5,
                6 => 6,
                7 => 8,
                _ => throw new FormatParseException($"AAC: reserved channel configuration {channelConfig}"),
            };
        }

        if (extensionFlag)
        {
            reader.ReadBool(); // extensionFlag3
        }

        if (channels is not (1 or 2))
        {
            throw new NotSupportedException($"AAC: {channels}-channel streams are not supported (only mono and stereo)");
        }

        if (sampleRate <= 0)
        {
            throw new FormatParseException("AAC: invalid sampling rate");
        }
    }

    private static int ReadObjectType(ref AacBitReader reader)
    {
        int aot = reader.ReadInt(5);
        return aot == 31 ? 32 + reader.ReadInt(6) : aot;
    }

    private static int SampleRateFor(int index)
    {
        if (index >= AacTables.SampleRates.Length)
        {
            throw new FormatParseException($"AAC: reserved sampling frequency index {index}");
        }

        return AacTables.SampleRates[index];
    }

    /// <summary>Walks a program_config_element and returns the channel count it describes.</summary>
    private static int SkipProgramConfig(ref AacBitReader reader, out int sampleRateIndex)
    {
        reader.ReadInt(4); // element_instance_tag
        reader.ReadInt(2); // object_type
        sampleRateIndex = reader.ReadInt(4);
        int front = reader.ReadInt(4);
        int side = reader.ReadInt(4);
        int back = reader.ReadInt(4);
        int lfe = reader.ReadInt(2);
        int assoc = reader.ReadInt(3);
        int cc = reader.ReadInt(4);
        if (reader.ReadBool())
        {
            reader.ReadInt(4); // mono_mixdown_element_number
        }

        if (reader.ReadBool())
        {
            reader.ReadInt(4); // stereo_mixdown_element_number
        }

        if (reader.ReadBool())
        {
            reader.ReadInt(3); // matrix_mixdown_idx + pseudo_surround_enable
        }

        int channels = 0;
        for (int i = 0; i < front + side + back; i++)
        {
            channels += reader.ReadBool() ? 2 : 1;
            reader.ReadInt(4);
        }

        channels += lfe;
        reader.Skip(lfe * 4);
        reader.Skip(assoc * 4);
        reader.Skip(cc * 5);
        reader.ByteAlign();
        int comment = reader.ReadInt(8);
        reader.Skip(comment * 8);
        return channels;
    }

    // ---------------------------------------------------------------- raw_data_block

    private void DecodeFrame(ReadOnlySpan<byte> accessUnit)
    {
        AacBitReader reader = new(accessUnit);
        int nextChannel = 0;

        while (true)
        {
            if (reader.Remaining < 3)
            {
                throw new FormatParseException("AAC: access unit has no END element");
            }

            int id = reader.ReadInt(3);
            switch (id)
            {
                case 0: // SCE
                case 3: // LFE
                    reader.ReadInt(4);
                    DecodeSingle(ref reader, nextChannel < Channels ? nextChannel : -1);
                    nextChannel++;
                    break;

                case 1: // CPE
                    reader.ReadInt(4);
                    DecodePair(ref reader, nextChannel + 1 < Channels ? nextChannel : -1);
                    nextChannel += 2;
                    break;

                case 2: // CCE
                    throw new NotSupportedException("AAC: coupling channel elements are not supported");

                case 4: // DSE
                    SkipDataStream(ref reader);
                    break;

                case 5: // PCE
                    SkipProgramConfig(ref reader, out _);
                    break;

                case 6: // FIL
                    SkipFill(ref reader);
                    break;

                default: // END
                    // A frame with fewer elements than channels leaves the rest silent.
                    for (int c = nextChannel; c < Channels; c++)
                    {
                        Array.Clear(_pcm[c]);
                    }

                    return;
            }
        }
    }

    private static void SkipDataStream(ref AacBitReader reader)
    {
        reader.ReadInt(4);
        bool align = reader.ReadBool();
        int count = reader.ReadInt(8);
        if (count == 255)
        {
            count += reader.ReadInt(8);
        }

        if (align)
        {
            reader.ByteAlign();
        }

        reader.Skip(count * 8);
    }

    private static void SkipFill(ref AacBitReader reader)
    {
        // Fill elements carry padding or extension payloads (SBR, dynamic range); the core
        // decoder needs none of them.
        int count = reader.ReadInt(4);
        if (count == 15)
        {
            count += reader.ReadInt(8) - 1;
        }

        reader.Skip(count * 8);
    }

    // ---------------------------------------------------------------- channel elements

    private void DecodeSingle(ref AacBitReader reader, int channel)
    {
        Ics ics = _left;
        DecodeIcs(ref reader, ics, commonWindow: false);
        Dequantise(ics, null);
        ApplyTns(ics);
        if (channel >= 0)
        {
            Window(ics, _states[channel], _pcm[channel]);
        }
    }

    private void DecodePair(ref AacBitReader reader, int firstChannel)
    {
        Ics left = _left;
        Ics right = _right;
        bool commonWindow = reader.ReadBool();
        int msMaskPresent = 0;
        if (commonWindow)
        {
            DecodeIcsInfo(ref reader, left);
            right.CopyInfoFrom(left);
            msMaskPresent = reader.ReadInt(2);
            switch (msMaskPresent)
            {
                case 1:
                    Array.Fill(_msUsed, false);
                    for (int g = 0; g < left.NumGroups; g++)
                    {
                        for (int sfb = 0; sfb < left.MaxSfb; sfb++)
                        {
                            _msUsed[g * 64 + sfb] = reader.ReadBool();
                        }
                    }

                    break;
                case 2:
                    Array.Fill(_msUsed, true);
                    break;
                case 3:
                    throw new FormatParseException("AAC: reserved ms_mask_present value");
                default:
                    Array.Fill(_msUsed, false);
                    break;
            }
        }
        else
        {
            Array.Fill(_msUsed, false);
        }

        DecodeIcs(ref reader, left, commonWindow);
        DecodeIcs(ref reader, right, commonWindow);

        Dequantise(left, null);
        Dequantise(right, commonWindow ? left : null);

        if (commonWindow && msMaskPresent != 0)
        {
            ApplyMidSide(left, right);
        }

        ApplyIntensity(left, right, msMaskPresent != 0);
        ApplyTns(left);
        ApplyTns(right);

        if (firstChannel >= 0)
        {
            Window(left, _states[firstChannel], _pcm[firstChannel]);
            Window(right, _states[firstChannel + 1], _pcm[firstChannel + 1]);
        }
    }

    // ---------------------------------------------------------------- individual_channel_stream

    private void DecodeIcs(ref AacBitReader reader, Ics ics, bool commonWindow)
    {
        ics.GlobalGain = reader.ReadInt(8);
        if (!commonWindow)
        {
            DecodeIcsInfo(ref reader, ics);
        }

        DecodeSectionData(ref reader, ics);
        DecodeScaleFactors(ref reader, ics);

        ics.PulseCount = 0;
        if (reader.ReadBool())
        {
            if (ics.WindowSequence == EightShort)
            {
                throw new FormatParseException("AAC: pulse data in a short-window frame");
            }

            DecodePulseData(ref reader, ics);
        }

        ics.TnsPresent = reader.ReadBool();
        if (ics.TnsPresent)
        {
            DecodeTnsData(ref reader, ics);
        }

        if (reader.ReadBool())
        {
            throw new NotSupportedException("AAC: gain control (SSR) is not supported");
        }

        DecodeSpectralData(ref reader, ics);
    }

    private void DecodeIcsInfo(ref AacBitReader reader, Ics ics)
    {
        reader.ReadBool(); // ics_reserved_bit
        ics.WindowSequence = reader.ReadInt(2);
        ics.WindowShape = reader.ReadInt(1);

        if (ics.WindowSequence == EightShort)
        {
            ics.MaxSfb = reader.ReadInt(4);
            int grouping = reader.ReadInt(7);
            ics.NumWindows = 8;
            ics.NumGroups = 1;
            ics.GroupLength[0] = 1;
            for (int bit = 6; bit >= 0; bit--)
            {
                if ((grouping & (1 << bit)) != 0)
                {
                    ics.GroupLength[ics.NumGroups - 1]++;
                }
                else
                {
                    ics.GroupLength[ics.NumGroups++] = 1;
                }
            }

            ics.NumSwb = AacTables.NumSwbShort[_sampleRateIndex];
            ics.SwbOffset = AacTables.SwbOffsetShort[_sampleRateIndex];
        }
        else
        {
            ics.MaxSfb = reader.ReadInt(6);
            ics.NumWindows = 1;
            ics.NumGroups = 1;
            ics.GroupLength[0] = 1;
            ics.NumSwb = AacTables.NumSwbLong[_sampleRateIndex];
            ics.SwbOffset = AacTables.SwbOffsetLong[_sampleRateIndex];
            if (reader.ReadBool())
            {
                // predictor_data_present: main-profile prediction or LTP, neither of which LC carries.
                throw new NotSupportedException("AAC: prediction data is not supported");
            }
        }

        if (ics.MaxSfb > ics.NumSwb)
        {
            throw new FormatParseException($"AAC: max_sfb {ics.MaxSfb} exceeds the {ics.NumSwb} bands of this window");
        }
    }

    private static void DecodeSectionData(ref AacBitReader reader, Ics ics)
    {
        bool isShort = ics.WindowSequence == EightShort;
        int sectionBits = isShort ? 3 : 5;
        int escape = (1 << sectionBits) - 1;

        for (int g = 0; g < ics.NumGroups; g++)
        {
            int k = 0;
            while (k < ics.MaxSfb)
            {
                int book = reader.ReadInt(4);
                if (book == 12)
                {
                    throw new FormatParseException("AAC: reserved codebook 12 in section data");
                }

                int length = 0;
                int increment;
                while ((increment = reader.ReadInt(sectionBits)) == escape)
                {
                    length += escape;
                }

                length += increment;
                if (k + length > ics.MaxSfb)
                {
                    throw new FormatParseException("AAC: section runs past max_sfb");
                }

                for (int sfb = k; sfb < k + length; sfb++)
                {
                    ics.Codebook[g * 64 + sfb] = book;
                }

                k += length;
            }
        }
    }

    private static void DecodeScaleFactors(ref AacBitReader reader, Ics ics)
    {
        int scaleFactor = ics.GlobalGain;
        int noiseEnergy = ics.GlobalGain - 90;
        int intensityPosition = 0;
        bool firstNoise = true;
        HuffmanCodebook book = HuffmanCodebook.ScaleFactor;

        for (int g = 0; g < ics.NumGroups; g++)
        {
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                int index = g * 64 + sfb;
                switch (ics.Codebook[index])
                {
                    case ZeroBook:
                        ics.ScaleFactor[index] = 0;
                        break;

                    case IntensityBook:
                    case IntensityBook2:
                        intensityPosition += book.Value(book.Decode(ref reader), 0) - 60;
                        ics.ScaleFactor[index] = intensityPosition;
                        break;

                    case NoiseBook:
                        if (firstNoise)
                        {
                            // The first noise energy is sent as a 9-bit PCM delta, the rest are Huffman DPCM.
                            noiseEnergy += reader.ReadInt(9) - 256;
                            firstNoise = false;
                        }
                        else
                        {
                            noiseEnergy += book.Value(book.Decode(ref reader), 0) - 60;
                        }

                        ics.ScaleFactor[index] = noiseEnergy;
                        break;

                    default:
                        scaleFactor += book.Value(book.Decode(ref reader), 0) - 60;
                        if (scaleFactor is < 0 or > 255)
                        {
                            throw new FormatParseException($"AAC: scalefactor {scaleFactor} out of range");
                        }

                        ics.ScaleFactor[index] = scaleFactor;
                        break;
                }
            }
        }
    }

    private static void DecodePulseData(ref AacBitReader reader, Ics ics)
    {
        ics.PulseCount = reader.ReadInt(2) + 1;
        ics.PulseStartSfb = reader.ReadInt(6);
        if (ics.PulseStartSfb > ics.NumSwb)
        {
            throw new FormatParseException("AAC: pulse start band out of range");
        }

        for (int i = 0; i < ics.PulseCount; i++)
        {
            ics.PulseOffset[i] = reader.ReadInt(5);
            ics.PulseAmplitude[i] = reader.ReadInt(4);
        }
    }

    private static void DecodeTnsData(ref AacBitReader reader, Ics ics)
    {
        bool isShort = ics.WindowSequence == EightShort;
        int filterBits = isShort ? 1 : 2;
        int lengthBits = isShort ? 4 : 6;
        int orderBits = isShort ? 3 : 5;
        int maxOrder = isShort ? 7 : 12;

        for (int w = 0; w < ics.NumWindows; w++)
        {
            int filters = reader.ReadInt(filterBits);
            ics.TnsFilterCount[w] = filters;
            if (filters == 0)
            {
                continue;
            }

            int coefResolution = reader.ReadInt(1); // 0 -> 3 bits, 1 -> 4 bits
            for (int f = 0; f < filters; f++)
            {
                int slot = w * 4 + f;
                ics.TnsLength[slot] = reader.ReadInt(lengthBits);
                int order = reader.ReadInt(orderBits);
                if (order > maxOrder)
                {
                    throw new FormatParseException($"AAC: TNS order {order} exceeds the LC limit");
                }

                ics.TnsOrder[slot] = order;
                if (order == 0)
                {
                    continue;
                }

                ics.TnsDirection[slot] = reader.ReadBool();
                int compress = reader.ReadInt(1);
                int coefBits = coefResolution + 3 - compress;
                float[] map = AacTables.TnsCoefficients[compress * 2 + coefResolution];
                for (int i = 0; i < order; i++)
                {
                    ics.TnsCoefficient[slot * 20 + i] = map[reader.ReadInt(coefBits)];
                }
            }
        }
    }

    private static void DecodeSpectralData(ref AacBitReader reader, Ics ics)
    {
        float[] spec = ics.Spectrum;
        Array.Clear(spec);
        int[] swb = ics.SwbOffset;
        int windowBase = 0;

        for (int g = 0; g < ics.NumGroups; g++)
        {
            int groupLength = ics.GroupLength[g];
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                int bookIndex = ics.Codebook[g * 64 + sfb];
                if (bookIndex == ZeroBook || bookIndex >= NoiseBook)
                {
                    continue;
                }

                HuffmanCodebook book = HuffmanCodebook.Spectral[bookIndex];
                int width = swb[sfb + 1] - swb[sfb];
                for (int w = 0; w < groupLength; w++)
                {
                    int offset = (windowBase + w) * ShortLength + swb[sfb];
                    DecodeBand(ref reader, book, bookIndex, spec, offset, width);
                }
            }

            windowBase += groupLength;
        }
    }

    private static void DecodeBand(ref AacBitReader reader, HuffmanCodebook book, int bookIndex, float[] spec, int offset, int width)
    {
        int dimension = book.Dimension;
        int end = offset + width;
        if (book.Unsigned)
        {
            for (int k = offset; k < end; k += dimension)
            {
                int row = book.Decode(ref reader);
                for (int i = 0; i < dimension; i++)
                {
                    int v = book.Value(row, i);
                    if (v != 0 && reader.ReadBool())
                    {
                        v = -v;
                    }

                    spec[k + i] = v;
                }

                if (bookIndex == 11)
                {
                    // Escape book: magnitude 16 announces an escape sequence for that coefficient.
                    for (int i = 0; i < 2; i++)
                    {
                        float v = spec[k + i];
                        if (v == 16 || v == -16)
                        {
                            spec[k + i] = ReadEscape(ref reader, v < 0);
                        }
                    }
                }
            }
        }
        else
        {
            for (int k = offset; k < end; k += dimension)
            {
                int row = book.Decode(ref reader);
                for (int i = 0; i < dimension; i++)
                {
                    spec[k + i] = book.Value(row, i);
                }
            }
        }
    }

    private static float ReadEscape(ref AacBitReader reader, bool negative)
    {
        int prefix = 4;
        while (reader.ReadBool())
        {
            prefix++;
            if (prefix > 21)
            {
                throw new FormatParseException("AAC: escape prefix too long");
            }
        }

        int value = (1 << prefix) | reader.ReadInt(prefix);
        return negative ? -value : value;
    }

    // ---------------------------------------------------------------- spectral tools

    /// <summary>
    /// Applies pulses, inverse quantisation and scalefactor gains in place, and fills
    /// noise-substituted bands. <paramref name="msPartner"/> is the left channel of a
    /// common-window pair: where both channels use noise on a band flagged M/S, the
    /// standard wants the same noise vector in both, so the right channel copies it.
    /// </summary>
    private void Dequantise(Ics ics, Ics? msPartner)
    {
        float[] spec = ics.Spectrum;
        int[] swb = ics.SwbOffset;

        if (ics.PulseCount > 0)
        {
            int k = swb[ics.PulseStartSfb];
            for (int i = 0; i < ics.PulseCount; i++)
            {
                k += ics.PulseOffset[i];
                if (k >= FrameLength)
                {
                    throw new FormatParseException("AAC: pulse position out of range");
                }

                spec[k] += spec[k] < 0 ? -ics.PulseAmplitude[i] : ics.PulseAmplitude[i];
            }
        }

        int windowBase = 0;
        for (int g = 0; g < ics.NumGroups; g++)
        {
            int groupLength = ics.GroupLength[g];
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                int index = g * 64 + sfb;
                int book = ics.Codebook[index];
                if (book == ZeroBook || book >= IntensityBook2)
                {
                    continue;
                }

                int width = swb[sfb + 1] - swb[sfb];
                if (book == NoiseBook)
                {
                    bool correlated = msPartner is not null && _msUsed[index] && msPartner.Codebook[index] == NoiseBook;
                    for (int w = 0; w < groupLength; w++)
                    {
                        int offset = (windowBase + w) * ShortLength + swb[sfb];
                        if (correlated)
                        {
                            CopyNoise(msPartner!.Spectrum, msPartner.ScaleFactor[index], spec, ics.ScaleFactor[index], offset, width);
                        }
                        else
                        {
                            FillNoise(spec, offset, width, ics.ScaleFactor[index]);
                        }
                    }

                    continue;
                }

                float gain = AacTables.ScaleFactorGain[ics.ScaleFactor[index]];
                for (int w = 0; w < groupLength; w++)
                {
                    int offset = (windowBase + w) * ShortLength + swb[sfb];
                    for (int k = offset; k < offset + width; k++)
                    {
                        float q = spec[k];
                        if (q != 0)
                        {
                            int magnitude = (int)MathF.Abs(q);
                            float m = magnitude < 8192 ? AacTables.Pow43[magnitude] : (float)Math.Pow(magnitude, 4.0 / 3.0);
                            spec[k] = (q < 0 ? -m : m) * gain;
                        }
                    }
                }
            }

            windowBase += groupLength;
        }
    }

    private static float NoiseGain(int energy) => (float)Math.Pow(2.0, 0.25 * Math.Clamp(energy, -155, 100));

    private void FillNoise(float[] spec, int offset, int width, int energy)
    {
        // Linear congruential generator, seeded per stream so decoding is reproducible;
        // the vector is normalised to unit energy and scaled to the coded band energy.
        double sum = 0;
        for (int k = offset; k < offset + width; k++)
        {
            _noiseState = _noiseState * 1664525u + 1013904223u;
            float v = (int)_noiseState;
            spec[k] = v;
            sum += (double)v * v;
        }

        float scale = NoiseGain(energy) / (float)Math.Sqrt(sum);
        for (int k = offset; k < offset + width; k++)
        {
            spec[k] *= scale;
        }
    }

    private static void CopyNoise(float[] source, int sourceEnergy, float[] dest, int destEnergy, int offset, int width)
    {
        float scale = NoiseGain(destEnergy) / NoiseGain(sourceEnergy);
        for (int k = offset; k < offset + width; k++)
        {
            dest[k] = source[k] * scale;
        }
    }

    private void ApplyMidSide(Ics left, Ics right)
    {
        float[] l = left.Spectrum;
        float[] r = right.Spectrum;
        int[] swb = left.SwbOffset;
        int windowBase = 0;
        for (int g = 0; g < left.NumGroups; g++)
        {
            int groupLength = left.GroupLength[g];
            for (int sfb = 0; sfb < left.MaxSfb; sfb++)
            {
                int index = g * 64 + sfb;
                if (!_msUsed[index] || left.Codebook[index] >= NoiseBook || right.Codebook[index] >= NoiseBook)
                {
                    continue;
                }

                for (int w = 0; w < groupLength; w++)
                {
                    int offset = (windowBase + w) * ShortLength + swb[sfb];
                    int end = offset + swb[sfb + 1] - swb[sfb];
                    for (int k = offset; k < end; k++)
                    {
                        float mid = l[k];
                        float side = r[k];
                        l[k] = mid + side;
                        r[k] = mid - side;
                    }
                }
            }

            windowBase += groupLength;
        }
    }

    private void ApplyIntensity(Ics left, Ics right, bool msPresent)
    {
        float[] l = left.Spectrum;
        float[] r = right.Spectrum;
        int[] swb = right.SwbOffset;
        int windowBase = 0;
        for (int g = 0; g < right.NumGroups; g++)
        {
            int groupLength = right.GroupLength[g];
            for (int sfb = 0; sfb < right.MaxSfb; sfb++)
            {
                int index = g * 64 + sfb;
                int book = right.Codebook[index];
                if (book != IntensityBook && book != IntensityBook2)
                {
                    continue;
                }

                // 0.5^(is_position/4), negated for the "out of phase" book and again where
                // the M/S mask asks for it.
                float scale = (float)Math.Pow(0.5, 0.25 * right.ScaleFactor[index]);
                if (book == IntensityBook2)
                {
                    scale = -scale;
                }

                if (msPresent && _msUsed[index])
                {
                    scale = -scale;
                }

                for (int w = 0; w < groupLength; w++)
                {
                    int offset = (windowBase + w) * ShortLength + swb[sfb];
                    int end = offset + swb[sfb + 1] - swb[sfb];
                    for (int k = offset; k < end; k++)
                    {
                        r[k] = l[k] * scale;
                    }
                }
            }

            windowBase += groupLength;
        }
    }

    private void ApplyTns(Ics ics)
    {
        if (!ics.TnsPresent)
        {
            return;
        }

        bool isShort = ics.WindowSequence == EightShort;
        int maxBands = Math.Min(isShort ? AacTables.TnsMaxBandsShort[_sampleRateIndex] : AacTables.TnsMaxBandsLong[_sampleRateIndex], ics.MaxSfb);
        int[] swb = ics.SwbOffset;
        float[] spec = ics.Spectrum;
        float[] lpc = _tnsLpc;
        float[] temp = _tnsTemp;

        for (int w = 0; w < ics.NumWindows; w++)
        {
            int bottom = ics.NumSwb;
            for (int f = 0; f < ics.TnsFilterCount[w]; f++)
            {
                int slot = w * 4 + f;
                int top = bottom;
                bottom = Math.Max(top - ics.TnsLength[slot], 0);
                int order = ics.TnsOrder[slot];
                if (order == 0)
                {
                    continue;
                }

                // Reflection coefficients to the all-pole filter's direct-form coefficients.
                lpc[0] = 1;
                for (int m = 1; m <= order; m++)
                {
                    float k = ics.TnsCoefficient[slot * 20 + m - 1];
                    for (int i = 1; i < m; i++)
                    {
                        temp[i] = lpc[i] + k * lpc[m - i];
                    }

                    for (int i = 1; i < m; i++)
                    {
                        lpc[i] = temp[i];
                    }

                    lpc[m] = k;
                }

                int start = swb[Math.Min(bottom, maxBands)];
                int end = swb[Math.Min(top, maxBands)];
                int size = end - start;
                if (size <= 0)
                {
                    continue;
                }

                int step = 1;
                if (ics.TnsDirection[slot])
                {
                    step = -1;
                    start = end - 1;
                }

                int position = w * ShortLength + start;
                for (int m = 0; m < size; m++, position += step)
                {
                    float y = spec[position];
                    int taps = Math.Min(m, order);
                    for (int i = 1; i <= taps; i++)
                    {
                        y -= lpc[i] * spec[position - i * step];
                    }

                    spec[position] = y;
                }
            }
        }
    }

    // ---------------------------------------------------------------- filterbank

    /// <summary>Inverse transform, windowing and overlap-add for one channel.</summary>
    private void Window(Ics ics, ChannelState state, float[] output)
    {
        float[] overlap = state.Overlap;
        float[] time = _time;
        int shape = ics.WindowShape;
        int previousShape = state.PreviousShape;
        float[] longCurrent = shape == 1 ? AacTables.KbdLong : AacTables.SineLong;
        float[] longPrevious = previousShape == 1 ? AacTables.KbdLong : AacTables.SineLong;
        float[] shortCurrent = shape == 1 ? AacTables.KbdShort : AacTables.SineShort;
        float[] shortPrevious = previousShape == 1 ? AacTables.KbdShort : AacTables.SineShort;

        if (ics.WindowSequence == EightShort)
        {
            Array.Clear(time);
            float[] shortTime = _shortTime;
            for (int w = 0; w < 8; w++)
            {
                _shortImdct.Compute(ics.Spectrum.AsSpan(w * ShortLength, ShortLength), shortTime);
                float[] rising = w == 0 ? shortPrevious : shortCurrent;
                int at = ShortStart + w * ShortLength;
                for (int i = 0; i < ShortLength; i++)
                {
                    time[at + i] += shortTime[i] * rising[i];
                    time[at + ShortLength + i] += shortTime[ShortLength + i] * shortCurrent[ShortLength - 1 - i];
                }
            }

            for (int i = 0; i < FrameLength; i++)
            {
                output[i] = overlap[i] + time[i];
                overlap[i] = time[FrameLength + i];
            }
        }
        else
        {
            _longImdct.Compute(ics.Spectrum, time);

            // Rising half: a long window, or (LONG_STOP) silence, a short rise and a flat top.
            if (ics.WindowSequence == LongStop)
            {
                for (int i = 0; i < ShortStart; i++)
                {
                    output[i] = overlap[i];
                }

                for (int i = 0; i < ShortLength; i++)
                {
                    output[ShortStart + i] = overlap[ShortStart + i] + time[ShortStart + i] * shortPrevious[i];
                }

                for (int i = ShortStart + ShortLength; i < FrameLength; i++)
                {
                    output[i] = overlap[i] + time[i];
                }
            }
            else
            {
                for (int i = 0; i < FrameLength; i++)
                {
                    output[i] = overlap[i] + time[i] * longPrevious[i];
                }
            }

            // Falling half: a long window, or (LONG_START) a flat top, a short fall and silence.
            if (ics.WindowSequence == LongStart)
            {
                for (int i = 0; i < ShortStart; i++)
                {
                    overlap[i] = time[FrameLength + i];
                }

                for (int i = 0; i < ShortLength; i++)
                {
                    overlap[ShortStart + i] = time[FrameLength + ShortStart + i] * shortCurrent[ShortLength - 1 - i];
                }

                Array.Clear(overlap, ShortStart + ShortLength, FrameLength - ShortStart - ShortLength);
            }
            else
            {
                for (int i = 0; i < FrameLength; i++)
                {
                    overlap[i] = time[FrameLength + i] * longCurrent[FrameLength - 1 - i];
                }
            }
        }

        state.PreviousShape = shape;
    }

    // ---------------------------------------------------------------- state

    private sealed class ChannelState
    {
        public readonly float[] Overlap = new float[FrameLength];
        public int PreviousShape;
    }

    /// <summary>Everything decoded for one channel of one frame.</summary>
    private sealed class Ics
    {
        public int GlobalGain;
        public int WindowSequence;
        public int WindowShape;
        public int MaxSfb;
        public int NumWindows = 1;
        public int NumGroups = 1;
        public int NumSwb;
        public int[] SwbOffset = [];
        public readonly int[] GroupLength = new int[8];
        public readonly int[] Codebook = new int[8 * 64];     // [group * 64 + sfb]
        public readonly int[] ScaleFactor = new int[8 * 64];  // scalefactor, noise energy or intensity position
        public int PulseCount;
        public int PulseStartSfb;
        public readonly int[] PulseOffset = new int[4];
        public readonly int[] PulseAmplitude = new int[4];
        public bool TnsPresent;
        public readonly int[] TnsFilterCount = new int[8];
        public readonly int[] TnsLength = new int[8 * 4];
        public readonly int[] TnsOrder = new int[8 * 4];
        public readonly bool[] TnsDirection = new bool[8 * 4];
        public readonly float[] TnsCoefficient = new float[8 * 4 * 20];
        public readonly float[] Spectrum = new float[FrameLength]; // [window * 128 + bin] for short windows

        public void CopyInfoFrom(Ics other)
        {
            WindowSequence = other.WindowSequence;
            WindowShape = other.WindowShape;
            MaxSfb = other.MaxSfb;
            NumWindows = other.NumWindows;
            NumGroups = other.NumGroups;
            NumSwb = other.NumSwb;
            SwbOffset = other.SwbOffset;
            Array.Copy(other.GroupLength, GroupLength, GroupLength.Length);
        }
    }
}

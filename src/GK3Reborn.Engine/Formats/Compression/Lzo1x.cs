using System.Buffers.Binary;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Compression;

/// <summary>
/// Decompressor for the LZO1X streams used by GK3's Barn archives.
/// </summary>
/// <remarks>
/// <para>
/// This is a managed port of the classic <c>lzo1x_decompress</c>. That routine is
/// written as a web of <c>goto</c>s between labelled states, and C# will not let a
/// <c>goto</c> jump into a nested scope, so the labels become an explicit state
/// machine here. The states and their transitions are named after the originals so
/// the two can be read side by side — the format is defined by that code rather than
/// by a specification, and silently "tidying" the control flow is an excellent way to
/// introduce a corruption that shows up in one asset out of thousands.
/// </para>
/// <para>
/// Unlike the reference, every read and write is bounds-checked against the input and
/// output spans. A corrupt archive produces a <see cref="FormatParseException"/>
/// naming the offset, not an out-of-bounds write.
/// </para>
/// <para>
/// Decompressing in managed code rather than binding to native minilzo keeps the
/// toolchain free of a native dependency that would need building and shipping for
/// every target platform.
/// </para>
/// </remarks>
public static class Lzo1x
{
    private const int M2MaxOffset = 0x0800;

    private enum State
    {
        /// <summary>Top of the outer loop: read a token, expect a literal run.</summary>
        Loop,

        /// <summary>The short literal run that follows a copy.</summary>
        FirstLiteralRun,

        /// <summary>Decode a match token.</summary>
        Match,

        /// <summary>Emit the pending match.</summary>
        CopyMatch,

        /// <summary>After a match: read the trailing literal count from the token.</summary>
        MatchDone,

        /// <summary>Copy one to three trailing literals, then decode another match.</summary>
        MatchNext,
    }

    /// <summary>
    /// Decompresses <paramref name="input"/> into <paramref name="output"/>.
    /// </summary>
    /// <param name="input">Compressed bytes.</param>
    /// <param name="output">
    /// Destination, sized to the expected decompressed length. The archive records that
    /// length, so a short result is itself a signal that something is wrong.
    /// </param>
    /// <param name="file">Name used in diagnostics.</param>
    /// <returns>Number of bytes written to <paramref name="output"/>.</returns>
    /// <exception cref="FormatParseException">The stream is malformed.</exception>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output, string file = "<memory>")
    {
        if (input.IsEmpty)
        {
            throw Malformed(file, 0, "at least one byte of LZO data", "an empty stream");
        }

        int ip = 0;
        int op = 0;
        int t = 0;
        int matchPosition = 0;
        State state;

        // A stream whose first byte exceeds 17 opens with a literal run encoded in that
        // byte, skipping the usual token.
        if (input[0] > 17)
        {
            t = input[ip++] - 17;
            if (t < 4)
            {
                state = State.MatchNext;
            }
            else
            {
                CopyLiterals(input, ref ip, output, ref op, t, file);
                state = State.FirstLiteralRun;
            }
        }
        else
        {
            state = State.Loop;
        }

        while (true)
        {
            switch (state)
            {
                case State.Loop:
                    // A well-formed stream ends at its marker, never by exhausting input.
                    // Running dry therefore means the entry is truncated, and Next says so
                    // with an offset. Verified against the retail corpus: all 36,957
                    // entries terminate at the marker, so nothing real depends on being
                    // lenient here.
                    t = Next(input, ref ip, file);
                    if (t >= 16)
                    {
                        state = State.Match;
                        break;
                    }

                    if (t == 0)
                    {
                        t += ReadLengthExtension(input, ref ip, 15, file);
                    }

                    CopyLiterals(input, ref ip, output, ref op, t + 3, file);
                    state = State.FirstLiteralRun;
                    break;

                case State.FirstLiteralRun:
                    t = Next(input, ref ip, file);
                    if (t >= 16)
                    {
                        state = State.Match;
                        break;
                    }

                    matchPosition = op - (1 + M2MaxOffset) - (t >> 2) - (Next(input, ref ip, file) << 2);
                    CopyMatch(output, ref op, matchPosition, 3, file);
                    state = State.MatchDone;
                    break;

                case State.Match:
                    if (t >= 64)
                    {
                        matchPosition = op - 1 - ((t >> 2) & 7) - (Next(input, ref ip, file) << 3);
                        t = (t >> 5) - 1;
                        state = State.CopyMatch;
                    }
                    else if (t >= 32)
                    {
                        t &= 31;
                        if (t == 0)
                        {
                            t += ReadLengthExtension(input, ref ip, 31, file);
                        }

                        matchPosition = op - 1 - (ReadUInt16(input, ref ip, file) >> 2);
                        state = State.CopyMatch;
                    }
                    else if (t >= 16)
                    {
                        matchPosition = op - ((t & 8) << 11);
                        t &= 7;
                        if (t == 0)
                        {
                            t += ReadLengthExtension(input, ref ip, 7, file);
                        }

                        matchPosition -= ReadUInt16(input, ref ip, file) >> 2;

                        // The three-byte sequence 0x11 0x00 0x00 arrives here with the
                        // match position equal to the write cursor. That is how a stream
                        // says it has ended.
                        if (matchPosition == op)
                        {
                            return op;
                        }

                        matchPosition -= 0x4000;
                        state = State.CopyMatch;
                    }
                    else
                    {
                        matchPosition = op - 1 - (t >> 2) - (Next(input, ref ip, file) << 2);
                        CopyMatch(output, ref op, matchPosition, 2, file);
                        state = State.MatchDone;
                    }

                    break;

                case State.CopyMatch:
                    CopyMatch(output, ref op, matchPosition, t + 2, file);
                    state = State.MatchDone;
                    break;

                case State.MatchDone:
                    // The two low bits of the token just consumed carry the count of
                    // literals that follow the match.
                    t = input[ip - 2] & 3;
                    state = t == 0 ? State.Loop : State.MatchNext;
                    break;

                case State.MatchNext:
                    CopyLiterals(input, ref ip, output, ref op, t, file);
                    t = Next(input, ref ip, file);
                    state = State.Match;
                    break;

                default:
                    throw Malformed(file, ip, "a valid decoder state", state.ToString());
            }
        }
    }

    private static byte Next(ReadOnlySpan<byte> input, ref int ip, string file)
    {
        if ((uint)ip >= (uint)input.Length)
        {
            throw Malformed(file, ip, "another input byte", "end of compressed data");
        }

        return input[ip++];
    }

    private static int ReadUInt16(ReadOnlySpan<byte> input, ref int ip, string file)
    {
        if (ip + 2 > input.Length)
        {
            throw Malformed(file, ip, "a 16-bit offset", "end of compressed data");
        }

        int value = BinaryPrimitives.ReadUInt16LittleEndian(input[ip..]);
        ip += 2;
        return value;
    }

    /// <summary>
    /// Reads a run length encoded as zero or more 0x00 bytes followed by a non-zero byte.
    /// </summary>
    private static int ReadLengthExtension(ReadOnlySpan<byte> input, ref int ip, int bias, string file)
    {
        int extra = 0;
        while (true)
        {
            if ((uint)ip >= (uint)input.Length)
            {
                throw Malformed(file, ip, "a run-length byte", "end of compressed data");
            }

            if (input[ip] != 0)
            {
                break;
            }

            extra += 255;
            ip++;

            if (extra > 0x4000_0000)
            {
                throw Malformed(file, ip, "a terminated run length", "an unbounded run of zero bytes");
            }
        }

        return extra + bias + Next(input, ref ip, file);
    }

    private static void CopyLiterals(
        ReadOnlySpan<byte> input, ref int ip, Span<byte> output, ref int op, int count, string file)
    {
        if (count < 0 || ip + count > input.Length)
        {
            throw Malformed(file, ip, $"{count} literal byte(s)", $"{input.Length - ip} remaining");
        }

        if (op + count > output.Length)
        {
            throw Malformed(file, ip, $"room for {count} literal byte(s)", $"{output.Length - op} remaining");
        }

        input.Slice(ip, count).CopyTo(output[op..]);
        ip += count;
        op += count;
    }

    /// <summary>
    /// Copies a back-reference one byte at a time.
    /// </summary>
    /// <remarks>
    /// The copy must stay byte-wise: matches routinely overlap the write cursor, and a
    /// block copy would read bytes that have not been produced yet. That overlap is how
    /// LZO encodes runs.
    /// </remarks>
    private static void CopyMatch(Span<byte> output, ref int op, int matchPosition, int count, string file)
    {
        if (matchPosition < 0)
        {
            throw Malformed(file, op, "a back-reference within the output", $"offset {matchPosition}");
        }

        if (count < 0 || op + count > output.Length)
        {
            throw Malformed(file, op, $"room for a {count} byte match", $"{output.Length - op} remaining");
        }

        for (int i = 0; i < count; i++)
        {
            output[op++] = output[matchPosition++];
        }
    }

    private static FormatParseException Malformed(string file, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1010",
            DiagnosticSeverity.Error,
            "Malformed LZO stream.",
            file,
            offset,
            expected,
            actual,
            "The archive entry is corrupt or is not LZO1X data. Re-extract the archive and report the offset."));
}

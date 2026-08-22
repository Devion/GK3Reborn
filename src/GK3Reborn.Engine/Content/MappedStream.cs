namespace GK3Reborn.Content;

/// <summary>
/// A read-only, seekable stream over memory somebody else owns.
/// </summary>
/// <remarks>
/// <para>
/// A ReBarn pack is memory-mapped once and read through windows onto that mapping, which
/// is what keeps a hundred-megabyte movie out of the heap. A decoder wants a
/// <see cref="Stream"/>, and the framework has no read-only stream over
/// <see cref="ReadOnlyMemory{T}"/>: <c>MemoryStream</c> takes an array, so handing it one
/// means copying the whole file first, which is the one thing the mapping exists to avoid.
/// </para>
/// <para>
/// <b>The memory is not owned here.</b> It stays valid only as long as whatever owns the
/// mapping does, so a stream over a pack must not outlive the pack. The engine keeps its
/// packs open for the life of the process, which is what makes this safe there.
/// </para>
/// </remarks>
public sealed class MappedStream : Stream
{
    private readonly ReadOnlyMemory<byte> _memory;
    private int _at;

    /// <summary>Wraps a window of memory.</summary>
    /// <param name="memory">The bytes, owned by somebody else.</param>
    public MappedStream(ReadOnlyMemory<byte> memory) => _memory = memory;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => true;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _memory.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _at;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, _memory.Length);
            _at = (int)value;
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        int taken = Math.Min(buffer.Length, _memory.Length - _at);

        if (taken <= 0)
        {
            return 0;
        }

        _memory.Span.Slice(_at, taken).CopyTo(buffer);
        _at += taken;

        return taken;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long wanted = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _at + offset,
            SeekOrigin.End => _memory.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        Position = wanted;
        return _at;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

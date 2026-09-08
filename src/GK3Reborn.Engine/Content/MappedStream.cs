namespace GK3Reborn.Content;

/// <summary>
/// A read-only, seekable stream over memory somebody else owns.
/// </summary>
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

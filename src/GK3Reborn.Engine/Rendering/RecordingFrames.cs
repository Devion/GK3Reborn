using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>A bounded, lossless recording writer. Accepted frames are drained before completion.</summary>
public sealed class RecordingFrames : IDisposable
{
    private readonly BlockingCollection<(int Index, DecodedImage Image)> _pending = new(4);
    private readonly CancellationTokenSource _failed = new();
    private readonly Task[] _workers;
    private readonly string _directory;
    private bool _disposed;

    /// <summary>Starts two encoding workers, holding at most four additional frames in memory.</summary>
    public RecordingFrames(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _workers = [Task.Run(Consume), Task.Run(Consume)];
    }

    /// <summary>Transfers ownership of a captured image to the writer; blocks only when the queue is full.</summary>
    public void Write(int index, DecodedImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _pending.Add((index, image), _failed.Token);
        }
        catch (OperationCanceledException)
        {
            Task.WaitAll(_workers); // Surface the original I/O or encoding failure.
            throw;
        }
    }

    private void Consume()
    {
        try
        {
            foreach (var frame in _pending.GetConsumingEnumerable(_failed.Token))
            {
                string path = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"frame_{frame.Index:D5}.png"));
                File.WriteAllBytes(path, PngWriter.Encode(frame.Image, CompressionLevel.Fastest));
            }
        }
        catch
        {
            _failed.Cancel();
            throw;
        }
    }

    /// <summary>Waits for every accepted frame, propagating failures instead of reporting a complete recording.</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pending.CompleteAdding();
        Task.WaitAll(_workers);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try { Complete(); }
        finally
        {
            _disposed = true;
            _pending.Dispose();
            _failed.Dispose();
        }
    }
}

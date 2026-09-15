using System.Globalization;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

public sealed class RecordingFramesTests
{
    [Fact]
    public void Completion_drains_numbered_frames_without_changing_pixels()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gk3-record-" + Guid.NewGuid());
        try
        {
            using var writer = new RecordingFrames(folder);
            for (int i = 0; i < 24; i++)
            {
                writer.Write(i, new DecodedImage(1, 1, [(byte)i, 25, 90, 255], false, "frame"));
            }
            writer.Complete();
            Assert.Equal(24, Directory.GetFiles(folder, "*.png").Length);
            for (int i = 0; i < 24; i++)
            {
                string file = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"frame_{i:D5}.png"));
                DecodedImage image = PngReader.Decode(File.ReadAllBytes(file), file);
                Assert.Equal(new byte[] { (byte)i, 25, 90, 255 }, image.Pixels);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Worker_failure_is_reported_and_does_not_deadlock_the_producer()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gk3-record-" + Guid.NewGuid());
        var writer = new RecordingFrames(folder);
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "frame_00000.png"));
            writer.Write(0, new DecodedImage(1, 1, [1, 2, 3, 255], false, "frame"));
            Assert.Throws<AggregateException>(writer.Complete);
            Assert.Throws<AggregateException>(() => writer.Write(1, new DecodedImage(1, 1, [1, 2, 3, 255], false, "frame")));
            Assert.Throws<AggregateException>(writer.Dispose);
        }
        finally { writer.Dispose(); Directory.Delete(folder, recursive: true); }
    }
}

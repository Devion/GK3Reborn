using System.Buffers.Binary;
using GK3Reborn.Formats.Terrain;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// The backdrop forest's instance stream, which replaced the scatter's JSON because
/// parsing that was the most expensive thing in an outdoor scene load.
/// </summary>
public sealed class TerrainForestTests
{
    /// <summary>Writes trees the way <c>publish_terrain.py</c> does.</summary>
    private static byte[] Stream(params float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];

        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        return bytes;
    }

    [Fact]
    public void A_stream_reads_back_as_the_floats_that_were_written()
    {
        float[] written =
        [
            1551.2f, -19.8f, -1538.7f, 0.34f, 4.63f, 3f,
            1405.3f, 93.0f, -1395.6f, 0.43f, 5.59f, 1f,
        ];

        float[]? read = TerrainForest.Read(Stream(written));

        Assert.NotNull(read);
        Assert.Equal(written, read);
    }

    /// <summary>
    /// The order matters as much as the values: both tree pipelines index this stream.
    /// </summary>
    /// <remarks>
    /// x, y, z, scale, rotation, kind — the same six the impostor shader and the grown
    /// models read, in the same order, which is what lets a tree cross a detail tier and
    /// change only what it is built from.
    /// </remarks>
    [Fact]
    public void A_tree_is_six_floats_in_the_order_both_pipelines_read()
    {
        float[]? read = TerrainForest.Read(Stream(10f, 20f, 30f, 0.5f, 1.25f, 2f));

        Assert.NotNull(read);
        Assert.Equal(TerrainForest.FloatsPerTree, read.Length);
        Assert.Equal(10f, read[0]);
        Assert.Equal(20f, read[1]);
        Assert.Equal(30f, read[2]);
        Assert.Equal(0.5f, read[3]);
        Assert.Equal(1.25f, read[4]);
        Assert.Equal(2f, read[5]);
    }

    /// <summary>
    /// The bytes are little-endian whatever machine wrote or reads them.
    /// </summary>
    /// <remarks>
    /// Stated against literal bytes rather than against a round trip, because a round trip
    /// through the same code agrees with itself on a big-endian machine while disagreeing
    /// with every file the publisher ever wrote.
    /// </remarks>
    [Fact]
    public void The_stream_is_little_endian()
    {
        // 1.0f is 0x3F800000.
        byte[] one = [0x00, 0x00, 0x80, 0x3F, 0, 0, 0, 0, 0, 0, 0, 0,
                      0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        float[]? read = TerrainForest.Read(one);

        Assert.NotNull(read);
        Assert.Equal(1f, read[0]);
    }

    [Fact]
    public void A_set_with_no_trees_reads_as_an_empty_forest()
    {
        float[]? read = TerrainForest.Read([]);

        Assert.NotNull(read);
        Assert.Empty(read);
    }

    /// <summary>
    /// A length that is not a whole number of trees is refused rather than guessed at.
    /// </summary>
    /// <remarks>
    /// The format has no header, so this is the only check available — and it is the one
    /// that catches the cases that matter: a truncated write, and a file that is not a
    /// forest at all. The scene then keeps its horizon and draws no forest on it, rather
    /// than scattering trees from whatever the bytes happened to mean.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    [InlineData(TerrainForest.BytesPerTree + 4)]
    public void A_stream_that_is_not_whole_trees_is_refused(int length)
    {
        Assert.Null(TerrainForest.Read(new byte[length]));
    }

    [Fact]
    public void A_tree_is_twenty_four_bytes()
    {
        // The publisher writes this and the pack rule counts on it; if it ever changes,
        // both ends have to change together.
        Assert.Equal(24, TerrainForest.BytesPerTree);
    }
}

using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

public sealed class MemoryBudgetTests
{
    [Theory]
    [InlineData(0UL, int.MaxValue)]
    [InlineData(2147483648UL, 512)]
    [InlineData(4294967296UL, 1024)]
    [InlineData(8589934592UL, int.MaxValue)]
    public void Texture_limits_match_small_card_capacity(ulong bytes, int expected) => Assert.Equal(expected, TextureResolution.ForMemory(bytes));

    [Fact]
    public void Compressed_mip_selection_preserves_format_and_slices_the_original_storage()
    {
        byte[] blocks = new byte[128 + 32 + 16 + 16]; // 16x8, 8x4, 4x2, 2x1 BC7.
        blocks[128] = 77;
        var source = new CompressedImage(16, 8, 4, BlockFormat.Bc7Srgb, blocks, "wall");
        CompressedImage reduced = TextureResolution.Limit(source, 8);
        Assert.Equal((8, 4, 3), (reduced.Width, reduced.Height, reduced.Mips));
        Assert.Equal(source.Format, reduced.Format);
        Assert.Equal(77, reduced.Blocks.Span[0]);
        blocks[128] = 99;
        Assert.Equal(99, reduced.Blocks.Span[0]);
        Assert.Equal(128, source.Level(1).Offset);
    }
    [Fact]
    public void Decoded_images_reduce_all_channels_and_handle_odd_narrow_dimensions()
    {
        var source = new DecodedImage(1, 3, [10, 20, 30, 0, 20, 40, 60, 120, 30, 60, 90, 240], true, "odd");
        DecodedImage result = TextureResolution.Limit(source, 1);
        Assert.Equal((1, 1), (result.Width, result.Height));
        Assert.Equal(new byte[] { 20, 40, 60, 120 }, result.Pixels);
        Assert.Equal(12, source.Pixels.Length);
    }
    [Fact]
    public void Uploads_flush_before_the_next_allocation_exceeds_the_byte_or_copy_limit()
    {
        Assert.False(BufferUploads.NeedsFlush(0, 0, ulong.MaxValue));
        Assert.False(BufferUploads.NeedsFlush(1024, 1, 1024));
        Assert.True(BufferUploads.NeedsFlush(BufferUploads.MaximumBytes - 1, 1, 2));
        Assert.True(BufferUploads.NeedsFlush(1, BufferUploads.MaximumCopies, 1));
        Assert.True(BufferUploads.NeedsFlush(1, 1, ulong.MaxValue));
    }
    [Fact]
    public void Cache_eviction_waits_for_the_last_scene_and_preserves_fallbacks()
    {
        using var device = new MemoryDevice();
        using var cache = new TextureCache(device, Pixel) { MaximumRetainedBytes = 0 };
        var a = SceneGeometry.Create(device, cache);
        var b = SceneGeometry.Create(device, cache);
        cache.Add("wall", Pixel); cache.AddNormal("wall", Pixel);
        cache.AddOrm("wall", Pixel); cache.AddHeight("wall", Pixel, keepField: true);
        var texture = (MemoryTexture)cache.Get("wall");
        var fallback = (MemoryTexture)cache.Fallback;
        a.Dispose();
        Assert.False(texture.Disposed);
        Assert.True(cache.Has("wall"));
        b.Dispose();
        Assert.True(texture.Disposed);
        Assert.False(fallback.Disposed);
        Assert.False(cache.Has("wall"));
        Assert.Null(cache.FieldFor("wall"));
        Assert.Equal(0, cache.DeviceBytes);
        b.Dispose(); // no double lease release
        using var next = SceneGeometry.Create(device, cache);
        cache.Add("wall", Pixel);
        Assert.NotSame(texture, cache.Get("wall"));
    }
    [Fact]
    public void Low_memory_profile_limits_uploads_before_device_allocation()
    {
        using var device = new MemoryDevice();
        using var cache = new TextureCache(device, Pixel);
        using var scene = SceneGeometry.Create(device, cache);
        scene.ConfigureMemory(4UL * 1024 * 1024 * 1024);
        cache.Add("large", new DecodedImage(2048, 1, new byte[2048 * 4], false, "large"));
        Assert.Equal(1024, device.LastWidth);
        Assert.Equal(250_000, scene.Relief.TriangleBudget);
    }
    private static DecodedImage Pixel => new(1, 1, [100, 100, 100, 255], false, "pixel");
    private sealed class MemoryTexture(long bytes) : IGeometryTexture
    {
        public long Bytes => bytes;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public void Refresh(ReadOnlySpan<byte> pixels, int width, int height) => throw new NotSupportedException();
    }
    private sealed class MemoryDevice : IGeometryDevice
    {
        public bool SupportsRayTracing => false;
        public bool BlockCompression => true;
        public int LastWidth { get; private set; }
        public IGeometryTexture CreateTexture(DecodedImage image, GeometryTextureKind kind = GeometryTextureKind.Colour,
            bool mipmaps = true, IGeometryUploads? into = null)
        { LastWidth = image.Width; return new MemoryTexture(image.Pixels.Length); }
        public IGeometryTexture CreateTexture(CompressedImage image, IGeometryUploads? into = null) => new MemoryTexture(image.Blocks.Length);
        public IGeometryUploads BeginUploads() => throw new NotSupportedException();
        public IGeometryBuffer CreateBuffer<T>(ReadOnlySpan<T> data, GeometryBufferKind kind, IGeometryUploads? into = null) where T : unmanaged => throw new NotSupportedException();
        public IGeometryBuffer CreateDynamicVertices(ulong bytes) => throw new NotSupportedException();
        public IGeometryMaterial CreateMaterial(IGeometryTexture diffuse, IGeometryTexture lightmap, IGeometryTexture normal,
            IGeometryTexture orm, IGeometryTexture height) => throw new NotSupportedException();
        public void Reserve(int materials) { }
        public void ReleaseMaterials() { }
        public IGeometryAccelerationStructure? BuildAccelerationStructure(IReadOnlyList<TraceableMesh> meshes) => null;
        public void Wait() { }
        public void Dispose() { }
    }
}

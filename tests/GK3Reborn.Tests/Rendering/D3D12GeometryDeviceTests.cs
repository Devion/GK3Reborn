using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Geometry;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The Direct3D side of the seam a scene is put on a device through.
/// </summary>
/// <remarks>
/// Not a rendering test — nothing here draws. What it checks is the bookkeeping that a
/// scene depends on and that fails quietly: that a texture asked for twice is uploaded
/// once, that a material takes the five contiguous slots the shader expects, and that the
/// samplers stay a fixed run of five however many materials there are. That last one is a
/// hard limit rather than a preference — a shader-visible sampler heap holds two thousand
/// and forty-eight descriptors, and five a material would run out inside one room.
/// </remarks>
[Collection(GpuTests.Name)]
public sealed class D3D12GeometryDeviceTests
{
    private static bool HasDevice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            DeviceReport report = D3D12DeviceSelector.Survey();
            return report.Available && report.Selected is not null;
        }
        catch (D3D12Exception)
        {
            return false;
        }
    }

    private static DecodedImage Flat(byte value, int size = 8)
    {
        byte[] pixels = new byte[size * size * 4];
        Array.Fill(pixels, value);

        return new DecodedImage(size, size, pixels, HasAlpha: false, SourceFormat: "test");
    }

    [Fact]
    public void A_texture_asked_for_twice_is_uploaded_once()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);

        Assert.False(device.HasTexture("wall"));

        device.AddTexture("wall", Flat(200));
        Assert.True(device.HasTexture("wall"));
        Assert.Equal(1, device.TextureCount);
        Assert.Equal(0, device.TexturesReused);

        device.AddTexture("wall", Flat(200));
        Assert.Equal(1, device.TextureCount);
        Assert.Equal(1, device.TexturesReused);

        // Case-insensitively, because GK3's own files disagree with themselves about it and
        // a room that uploaded WALL.BMP beside wall.BMP would pay twice for one picture.
        device.AddTexture("WALL", Flat(200));
        Assert.Equal(1, device.TextureCount);
        Assert.Equal(2, device.TexturesReused);
    }

    [Fact]
    public void A_texture_nobody_added_reads_as_the_stand_in()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);

        // Not null and not an exception. A room that names a texture its barn does not
        // carry should draw in white rather than refuse to load, which is how the missing
        // one gets noticed and reported instead of stopping the game.
        Assert.NotNull(device.Texture("nothing at all"));
    }

    [Fact]
    public void Every_material_takes_five_slots_in_both_heaps()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);

        device.AddTexture("wall", Flat(200));

        for (int i = 0; i < 8; i++)
        {
            IGeometryMaterial material = device.CreateMaterial(
                device.Texture("wall"), device.White, device.Flat, device.Neutral, device.Level);

            Assert.NotNull(material);

            Assert.Equal((uint)(i + 1) * D3D12GeometryDevice.TexturesPerMaterial, device.ViewDescriptorsUsed);

            // The samplers do not grow with them. A shader-visible sampler heap holds two
            // thousand and forty-eight descriptors, so five a material would run out at
            // four hundred and nine batches — which a room reaches. They need not be per
            // material anyway: which sampler each of the five textures wants is a property
            // of what the texture is, and that is the same for every material in the game.
            Assert.Equal(D3D12GeometryDevice.TexturesPerMaterial, device.SamplerDescriptorsUsed);
        }
    }

    [Fact]
    public void Buffers_and_materials_say_nothing_to_the_debug_layer()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);

        using IGeometryUploads batch = device.BeginUploads();

        using IGeometryBuffer vertices = device.CreateBuffer<float>(
            [0f, 1f, 2f, 3f, 4f, 5f], GeometryBufferKind.Vertices, batch);

        using IGeometryBuffer indices = device.CreateBuffer<uint>(
            [0, 1, 2], GeometryBufferKind.Indices, batch);

        batch.Submit();

        Assert.Equal(24u, vertices.Bytes);
        Assert.Equal(12u, indices.Bytes);

        device.AddTexture("wall", Flat(200));
        device.CreateMaterial(
            device.Texture("wall"), device.White, device.Flat, device.Neutral, device.Level);

        device.Wait();

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void A_buffer_meant_to_be_rewritten_can_be()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);

        using IGeometryBuffer live = device.CreateDynamicVertices(256);
        live.Write<float>([1f, 2f, 3f]);
        live.Write<float>([4f, 5f, 6f]);

        // A buffer in device memory is not one of those, and saying so is better than
        // writing into a pointer that is not there.
        using IGeometryBuffer still = device.CreateBuffer<float>(
            [0f, 1f], GeometryBufferKind.Vertices);

        Assert.Throws<InvalidOperationException>(() => still.Write<float>([9f]));
    }
}

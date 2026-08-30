using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using GK3Reborn.Rendering.Geometry;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The Direct3D side of the seam a scene is put on a device through.
/// </summary>
/// <remarks>
/// Not a rendering test — nothing here draws. What it checks is the bookkeeping a scene
/// depends on and that fails quietly: that a texture asked for twice is uploaded once, that
/// a material takes the five contiguous slots the shader expects, and that the samplers stay
/// a fixed run of five however many materials there are. That last one is a hard limit
/// rather than a preference — a shader-visible sampler heap holds two thousand and
/// forty-eight descriptors, and five a material would run out inside one room.
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

    /// <summary>A unit quad, so that a room can be built without any of the game's data.</summary>
    private static ModFile Quad(string texture)
    {
        var submesh = new ModSubmesh
        {
            TextureName = texture,
            Color = (255, 255, 255),
            Positions = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)],
            Normals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
            TexCoords = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
            Indices = [0, 1, 2, 0, 2, 3],
        };

        return ModFile.FromMeshes(
            "quad",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = new Vector3(-1, -1, 0),
                    BoundsMax = new Vector3(1, 1, 0),
                    Submeshes = [submesh],
                },
            ]);
    }

    [Fact]
    public void A_texture_asked_for_twice_is_uploaded_once()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);
        using var cache = new TextureCache(device, Flat(255));

        Assert.False(cache.Has("wall"));

        cache.Add("wall", Flat(200));
        Assert.True(cache.Has("wall"));
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.Reused);

        cache.Add("wall", Flat(200));
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.Reused);

        // Case-insensitively, because GK3's own files disagree with themselves about it and
        // a room that uploaded WALL.BMP beside wall.BMP would pay twice for one picture.
        cache.Add("WALL", Flat(200));
        Assert.Equal(1, cache.Count);
        Assert.Equal(2, cache.Reused);
    }

    [Fact]
    public void A_texture_nobody_added_reads_as_the_fallback()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);
        using var cache = new TextureCache(device, Flat(255));

        // Not null and not an exception. A room that names a texture its barn does not carry
        // should draw in the fallback rather than refuse to load, which is how the missing
        // one gets noticed and reported instead of stopping the game.
        Assert.Same(cache.Fallback, cache.Get("nothing at all"));

        // And the maps a surface has none of come back as the stand-ins rather than null,
        // because a material binds five textures whether or not five exist.
        Assert.Same(cache.Flat, cache.GetNormal("nothing at all"));
        Assert.Same(cache.Neutral, cache.GetOrm("nothing at all"));
        Assert.Same(cache.Level, cache.GetHeight("nothing at all"));
    }

    [Fact]
    public void Every_material_takes_five_slots_and_the_samplers_do_not_grow()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);
        using var cache = new TextureCache(device, Flat(255));

        cache.Add("wall", Flat(200));

        for (int i = 0; i < 8; i++)
        {
            IGeometryMaterial material = device.CreateMaterial(
                cache.Get("wall"), cache.White, cache.Flat, cache.Neutral, cache.Level);

            Assert.NotNull(material);

            Assert.Equal(
                (uint)(i + 1) * D3D12GeometryDevice.TexturesPerMaterial,
                device.ViewDescriptorsUsed);

            // A shader-visible sampler heap holds two thousand and forty-eight descriptors,
            // so five a material would run out at four hundred and nine batches — which a
            // room reaches. They need not be per material anyway: which sampler each of the
            // five textures wants is a property of what the texture is, and that is the same
            // for every material in the game.
            Assert.Equal(D3D12GeometryDevice.TexturesPerMaterial, device.SamplerDescriptorsUsed);
        }
    }

    [Fact]
    public void Releasing_a_rooms_materials_hands_its_slots_back()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);
        using var cache = new TextureCache(device, Flat(255));

        cache.Add("wall", Flat(200));

        const uint Materials = 8;

        for (uint room = 0; room < 3; room++)
        {
            for (uint i = 0; i < Materials; i++)
            {
                device.CreateMaterial(
                    cache.Get("wall"), cache.White, cache.Flat, cache.Neutral, cache.Level);
            }

            Assert.Equal(
                Materials * D3D12GeometryDevice.TexturesPerMaterial,
                device.ViewDescriptorsUsed);

            device.ReleaseMaterials();

            // Back to nothing, and back to nothing three times: the heap is a bump
            // allocator, so a release that only worked once would leave the second room
            // starting where the first ended.
            Assert.Equal(0u, device.ViewDescriptorsUsed);
        }
    }

    [Fact]
    public void Walking_from_room_to_room_does_not_fill_the_view_heap()
    {
        // Reported as a crash on the way through a door, several rooms in: "the
        // DescriptorHeapTypeCbvSrvUav descriptor heap is full: 20480 of 20480 used, 5 more
        // asked for", thrown from SceneGeometry.Finish while the next room was being built.
        // Nothing released a room's materials, so every room a session visited kept its
        // descriptors for the life of the process and the heap ran out on about the
        // thirteenth. Twenty rooms here, and the count after each is the count after the
        // first.
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);
        using var cache = new TextureCache(device, Flat(255));

        uint afterTheFirst = 0;

        for (int room = 0; room < 20; room++)
        {
            using (SceneGeometry geometry = SceneGeometry.Create(device, cache))
            {
                geometry.AddTexture("wall", Flat(200));
                geometry.Add(Quad("wall"));
                geometry.Finish();

                if (room == 0)
                {
                    afterTheFirst = device.ViewDescriptorsUsed;
                    Assert.True(afterTheFirst > 0, "the room took no descriptors at all");
                }

                Assert.Equal(afterTheFirst, device.ViewDescriptorsUsed);
            }

            Assert.Equal(0u, device.ViewDescriptorsUsed);
        }
    }

    [Fact]
    public void A_lightmap_can_be_refreshed_without_being_replaced()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);

        using IGeometryTexture lightmap =
            device.CreateTexture(Flat(64, 32), GeometryTextureKind.Atlas);

        // What a time-of-day change is. The texture surviving is the point: every material
        // in the room already points at it, and replacing it would mean rebuilding several
        // hundred materials to change the light on geometry that has not moved.
        DecodedImage evening = Flat(180, 32);
        lightmap.Refresh(evening.Pixels, evening.Width, evening.Height);

        device.Wait();

        Assert.DoesNotContain(
            context.DrainMessages(),
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void Buffers_and_materials_say_nothing_to_the_debug_layer()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12Context context = D3D12Context.Create(enableValidation: true);
        using D3D12GeometryDevice device = D3D12GeometryDevice.Create(context);
        using var cache = new TextureCache(device, Flat(255));

        using (IGeometryUploads batch = device.BeginUploads())
        {
            using IGeometryBuffer vertices = device.CreateBuffer<float>(
                [0f, 1f, 2f, 3f, 4f, 5f], GeometryBufferKind.Vertices, batch);

            using IGeometryBuffer indices = device.CreateBuffer<uint>(
                [0, 1, 2], GeometryBufferKind.Indices, batch);

            batch.Submit();

            Assert.Equal(24u, vertices.Bytes);
            Assert.Equal(12u, indices.Bytes);
        }

        cache.Add("wall", Flat(200));
        device.CreateMaterial(
            cache.Get("wall"), cache.White, cache.Flat, cache.Neutral, cache.Level);

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

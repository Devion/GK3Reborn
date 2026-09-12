// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Text.RegularExpressions;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the sun's rays: which rooms get them, what the pass is told, and what it draws.
/// </summary>
[Collection(GpuTests.Name)]
public sealed class SunRayTests
{
    private static readonly Timeblock Morning = new(1, 10, IsAfternoon: false);
    private static readonly Timeblock Evening = new(1, 6, IsAfternoon: true);

    // --- which rooms ----------------------------------------------------------------------

    [Fact]
    public void A_daytime_sun_gives_rays_and_no_sun_gives_none()
    {
        AuthoredLight? sun = Sunlight.For(Morning, Vector3.Zero);

        Assert.NotNull(sun);

        SunRays rays = SunRays.For(sun);

        Assert.True(rays.Any);
        Assert.Equal(1f, rays.Strength);

        // Toward the sun is against the way its light travels, and it is above the horizon.
        Assert.True(Vector3.Dot(rays.Toward, -sun.Direction) > 0.999f);
        Assert.True(rays.Toward.Y > 0f);

        Assert.False(SunRays.For(null).Any);
        Assert.False(SunRays.For(Sunlight.For(Evening, Vector3.Zero)).Any);
    }

    [Fact]
    public void Turning_the_rays_off_keeps_the_sun_and_drops_the_strength()
    {
        SunRays rays = SunRays.For(Sunlight.For(Morning, Vector3.Zero));

        SunRays off = rays.Lit(false);

        Assert.False(off.Any);
        Assert.Equal(rays.Toward, off.Toward);
        Assert.True(off.Lit(true).Any == false, "nought stays nought: on is not a strength");
        Assert.True(rays.Lit(true).Any);
    }

    [Fact]
    public void A_sun_below_the_horizon_is_no_sun()
    {
        var setting = new AuthoredLight(
            "sun", AuthoredLightKind.Point, Vector3.Zero, new Vector3(0f, 0.5f, 1f), Vector3.One,
            0, 0, 0, 0, false, true, 1f, 1f);

        Assert.False(SunRays.For(setting).Any);
    }

    // --- what the pass is told ------------------------------------------------------------

    [Fact]
    public void The_block_carries_the_sun_the_camera_and_the_sky_it_was_drawn_with()
    {
        SunRays rays = new(Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.74f)), new Vector3(1f, 0.9f, 0.8f), 0.7f);
        var clouds = new Vector4(0.78f, 1f, 5f, 7f);

        SunRayConstants block = SunRayConstants.For(rays, Facing(), clouds, 320, 240);

        Assert.Equal(new Vector4(rays.Toward, 0.7f), block.Sun);
        Assert.Equal(new Vector4(rays.Colour, 1f), block.Colour);
        Assert.Equal(clouds, block.Clouds);
        Assert.Equal(new Vector4(320, 240, 0, 0), block.Screen);

        // The basis is the sky's: forward, right and up orthonormal, with the half-angles'
        // tangents beside the last two and the aspect on the horizontal one.
        Vector3 forward = new(block.Forward.X, block.Forward.Y, block.Forward.Z);
        Vector3 right = new(block.Right.X, block.Right.Y, block.Right.Z);
        Vector3 up = new(block.Up.X, block.Up.Y, block.Up.Z);

        Assert.True(MathF.Abs(Vector3.Dot(forward, right)) < 1e-5f);
        Assert.True(MathF.Abs(Vector3.Dot(forward, up)) < 1e-5f);
        Assert.True(MathF.Abs(Vector3.Dot(right, up)) < 1e-5f);
        Assert.Equal(block.Up.W * 320f / 240f, block.Right.W, 5);

        // A painted sky has no clouds to read.
        Assert.Equal(0f, SunRayConstants.For(rays, Facing(), null, 320, 240).Colour.W);
    }

    [Fact]
    public void The_block_is_within_what_both_backends_will_take()
    {
        SunRayLayout.Bindings.Validate();

        Assert.Equal(
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<SunRayConstants>(),
            SunRayLayout.Bindings.PushConstantBytes);

        Assert.True(SunRayLayout.Bindings.PushConstantBytes <= ShaderLayout.MaximumPushConstantBytes);
    }

    [Fact]
    public void The_shafts_are_packed_as_the_shader_reads_them()
    {
        var shaft = new LightShaft(
            new Vector3(1, 2, 3), Vector3.UnitZ, 40f, 60f, 250f, new Vector3(1f, 0.9f, 0.8f), 0.42f);

        byte[] bytes = SunRayLayout.PackShafts([shaft]);

        Assert.Equal(SunRayLayout.ShaftBufferBytes, bytes.Length);
        Assert.Equal(1, BitConverter.ToInt32(bytes, 0));

        // The first box starts on the sixteen-byte boundary after the count, origin first.
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 16));
        Assert.Equal(40f, BitConverter.ToSingle(bytes, 28));
        Assert.Equal(250f, BitConverter.ToSingle(bytes, 16 + 32 + 12));
        Assert.Equal(0.42f, BitConverter.ToSingle(bytes, 16 + 48));

        // The count is what the shader reads, so more than it can hold is cut, not overrun.
        LightShaft[] many = [.. Enumerable.Repeat(shaft, LightShaft.Capacity + 4)];

        Assert.Equal(LightShaft.Capacity, BitConverter.ToInt32(SunRayLayout.PackShafts(many), 0));
    }

    [Fact]
    public void A_sun_only_at_the_windows_gives_the_sky_walk_nothing_to_aim_at()
    {
        var shaft = new LightShaft(Vector3.Zero, -Vector3.UnitY, 40f, 60f, 250f, Vector3.One, 1f);
        SunRays indoors = SunRays.None.Through([shaft]) with { Strength = 1f };

        Assert.True(indoors.Any);
        Assert.False(indoors.Sky);

        SunRayConstants block = SunRayConstants.For(indoors, Facing(), null, 320, 240);

        Assert.Equal(Vector4.Zero with { W = 1f }, block.Sun);
        Assert.Equal(1f, block.Time.Y);
    }

    [Fact]
    public void The_rays_read_the_same_clouds_the_sky_draws()
    {
        // A ray that breaks through a gap the sky does not show is a ray from nowhere, so
        // the cloud function is the sky's own, character for character once the
        // indentation is taken off.
        static string Flat(string text) =>
            Regex.Replace(Regex.Replace(text, @"//[^\n]*", string.Empty), @"\s+", " ").Trim();

        Assert.Contains(Flat(SunRayShaders.Clouds), Flat(TerrainShaders.SkyFragment), StringComparison.Ordinal);
        Assert.Contains(Flat(SunRayShaders.Clouds), Flat(SunRayShaders.Fragment), StringComparison.Ordinal);
    }

    // --- what it draws --------------------------------------------------------------------

    [Fact]
    public void The_sun_brightens_the_sky_toward_it_and_a_wall_shades_the_air_under_it()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        // A small dark wall in the middle of the view, sky all round it.
        geometry.AddTexture("wall", Solid(20, 20, 20));
        geometry.Add(Wall("wall", 1.2f));

        DecodedImage before = renderer.Render(geometry, 128, 128, Facing());

        // The sun a little above the camera's line of sight, ahead of it.
        renderer.SetSunRays(new SunRays(Vector3.Normalize(new Vector3(0f, 0.35f, 1f)), Vector3.One, 1f));

        DecodedImage after = renderer.Render(geometry, 128, 128, Facing());

        // Sky near the sun, at the top of the picture, is brighter for the rays.
        (byte skyBefore, byte _, byte _) = Pixel(before, 64, 12);
        (byte skyAfter, byte _, byte _) = Pixel(after, 64, 12);

        Assert.True(skyAfter > skyBefore + 15, $"the rays added nothing to the sky: {skyBefore} became {skyAfter}");

        // The wall itself is lit by the air in front of it, but less than the open sky
        // beside it is: the walk from the wall's middle toward the sun crosses the wall
        // first and only then the sky.
        (byte wallAfter, byte _, byte _) = Pixel(after, 64, 64);
        (byte besideAfter, byte _, byte _) = Pixel(after, 118, 64);

        Assert.True(besideAfter >= wallAfter, $"the wall was lit more than the sky beside it: {wallAfter} against {besideAfter}");

        // And light is added, never taken away.
        for (int y = 0; y < 128; y += 8)
        {
            for (int x = 0; x < 128; x += 8)
            {
                (byte r0, byte _, byte _) = Pixel(before, x, y);
                (byte r1, byte _, byte _) = Pixel(after, x, y);

                Assert.True(r1 + 1 >= r0, $"the rays darkened ({x},{y}): {r0} became {r1}");
            }
        }
    }

    [Fact]
    public void A_shaft_of_daylight_lights_the_air_it_stands_in_and_a_wall_in_front_hides_it()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        // A small dark wall on the left of the view, three units in front of the origin.
        geometry.AddTexture("wall", Solid(20, 20, 20));
        geometry.Add(Wall("wall", 1.0f), Matrix4x4.CreateTranslation(-2f, 0f, -3f));

        DecodedImage before = renderer.Render(geometry, 128, 128, Facing());

        // A shaft coming straight down through the origin, a unit and a half wide, from
        // well above the view to well below it. Its middle is on the right of the frame,
        // where nothing is drawn, and it passes behind the wall on the left.
        var shaft = new LightShaft(
            new Vector3(0f, 6f, 0f), -Vector3.UnitY, 0.75f, 0.75f, 12f, Vector3.One, 1f);

        renderer.SetSunRays(SunRays.None.Through([shaft]) with { Strength = 1f });

        DecodedImage after = renderer.Render(geometry, 128, 128, Facing());

        (byte inBefore, byte _, byte _) = Pixel(before, 64, 64);
        (byte inAfter, byte _, byte _) = Pixel(after, 64, 64);
        (byte outBefore, byte _, byte _) = Pixel(before, 118, 64);
        (byte outAfter, byte _, byte _) = Pixel(after, 118, 64);
        (byte wallBefore, byte _, byte _) = Pixel(before, 30, 64);
        (byte wallAfter, byte _, byte _) = Pixel(after, 30, 64);

        Assert.True(inAfter > inBefore + 10, $"the shaft lit nothing: {inBefore} became {inAfter}");
        Assert.True(outAfter <= outBefore + 2, $"the air beside the shaft was lit: {outBefore} became {outAfter}");
        Assert.True(wallAfter <= wallBefore + 2, $"the shaft shone through the wall: {wallBefore} became {wallAfter}");
    }

    [Fact]
    public void A_sun_behind_the_camera_draws_nothing()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(20, 20, 20));
        geometry.Add(Wall("wall", 1.2f));

        byte[] before = renderer.Render(geometry, 128, 128, Facing()).Pixels;

        renderer.SetSunRays(new SunRays(Vector3.Normalize(new Vector3(0f, 0.35f, -1f)), Vector3.One, 1f));

        Assert.Equal(before, renderer.Render(geometry, 128, 128, Facing()).Pixels);
    }

    [Fact]
    public void The_same_room_is_lit_the_same_way_every_render()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(20, 20, 20));
        geometry.Add(Wall("wall", 1.2f));

        renderer.SetSunRays(new SunRays(Vector3.Normalize(new Vector3(0.2f, 0.35f, 1f)), Vector3.One, 1f));

        Assert.Equal(
            renderer.Render(geometry, 128, 128, Facing()).Pixels,
            renderer.Render(geometry, 128, 128, Facing()).Pixels);
    }

    [Fact]
    public void A_room_with_no_sun_is_drawn_exactly_as_it_was()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 200, 200));
        geometry.Add(Wall("wall", 4f));

        byte[] before = renderer.Render(geometry, 128, 128, Facing()).Pixels;

        renderer.SetSunRays(new SunRays(Vector3.UnitY, Vector3.One, 1f));
        renderer.SetSunRays(SunRays.None);

        Assert.Equal(before, renderer.Render(geometry, 128, 128, Facing()).Pixels);
    }

    [Fact]
    public void The_sun_brightens_the_sky_on_Direct3D_too()
    {
        Assert.SkipUnless(HasDirect3D(), "no Direct3D device");

        using var renderer = GK3Reborn.Rendering.Direct3D12.D3D12SceneRenderer.Create();
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(20, 20, 20));
        geometry.Add(Wall("wall", 1.2f));

        (byte skyBefore, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 12);

        renderer.SetSunRays(new SunRays(Vector3.Normalize(new Vector3(0f, 0.35f, 1f)), Vector3.One, 1f));

        (byte skyAfter, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 12);

        Assert.True(skyAfter > skyBefore + 15, $"the rays added nothing to the sky: {skyBefore} became {skyAfter}");
    }

    // --- helpers --------------------------------------------------------------------------

    private static bool HasDevice()
    {
        try
        {
            DeviceReport report = VulkanDeviceSelector.Survey();
            return report.Available && report.Adapters.Count > 0;
        }
        catch (VulkanException)
        {
            return false;
        }
    }

    private static bool HasDirect3D()
    {
        try
        {
            DeviceReport report =
                GK3Reborn.Rendering.Direct3D12.D3D12DeviceSelector.Survey();

            return report.Available && report.Selected is not null;
        }
        catch (GK3Reborn.Rendering.Direct3D12.D3D12Exception)
        {
            return false;
        }
    }

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    /// <summary>A quad at the origin, facing the camera, of a given half-width.</summary>
    private static ModFile Wall(string texture, float half)
    {
        Vector3[] positions =
        [
            new(-half, -half, 0), new(half, -half, 0), new(half, half, 0), new(-half, half, 0),
        ];

        return ModFile.FromMeshes(
            "wall",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = new Vector3(-half, -half, 0),
                    BoundsMax = new Vector3(half, half, 0),
                    Submeshes =
                    [
                        new ModSubmesh
                        {
                            TextureName = texture,
                            Color = (255, 255, 255),
                            Positions = positions,
                            Normals =
                            [
                                -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ,
                            ],
                            TexCoords =
                            [
                                new(0, 1), new(1, 1), new(1, 0), new(0, 0),
                            ],
                            Indices = [0, 1, 2, 0, 2, 3],
                        },
                    ],
                },
            ]);
    }

    private static Camera Facing() => new()
    {
        Position = new Vector3(0, 0, -6),
        Target = Vector3.Zero,
        Up = Vector3.UnitY,
        Background = Vector3.Zero,
        LightDirection = new Vector3(0, 0, 1),
    };

    private static DecodedImage Solid(byte r, byte g, byte b)
    {
        const int Size = 8;
        byte[] pixels = new byte[Size * Size * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return new DecodedImage(Size, Size, pixels, HasAlpha: false, "test");
    }
}

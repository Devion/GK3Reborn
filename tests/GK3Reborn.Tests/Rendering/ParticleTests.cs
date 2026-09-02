// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the smoke and embers a fire gives off, and for the one blended pass that
/// draws them.
/// </summary>
public sealed class ParticleTests
{
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

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    [Fact]
    public void A_particle_becomes_two_triangles_around_where_it_is()
    {
        var vertices = new ParticleVertex[ParticleVertex.Corners];

        int written = ParticleVertex.Build(
            [new Particle(new Vector3(3, 4, 5), 2f, Vector4.One, 0f, 1f)], vertices);

        Assert.Equal(ParticleVertex.Corners, written);

        foreach (ParticleVertex corner in vertices)
        {
            // Every corner carries the particle, not the corner's own world position: a
            // sprite is not in world space until the camera is known.
            Assert.Equal(new Vector4(3, 4, 5, 2), corner.PositionAndSize);

            Assert.Equal(1f, MathF.Abs(corner.CornerAndShape.X));
            Assert.Equal(1f, MathF.Abs(corner.CornerAndShape.Y));
        }

        // Four distinct corners across the six vertices, which is a square rather than a
        // pair of overlapping slivers.
        Assert.Equal(
            4,
            vertices
                .Select(v => (v.CornerAndShape.X, v.CornerAndShape.Y))
                .Distinct()
                .Count());
    }

    [Fact]
    public void More_particles_than_the_buffer_holds_are_dropped_rather_than_overrunning()
    {
        var vertices = new ParticleVertex[4 * ParticleVertex.Corners];

        int written = ParticleVertex.Build(
            [.. Enumerable.Repeat(new Particle(Vector3.Zero, 1f, Vector4.One, 0f, 0f), 40)],
            vertices);

        Assert.Equal(4 * ParticleVertex.Corners, written);
    }

    [Fact]
    public void A_fire_starts_throwing_embers_and_smoke()
    {
        var particles = new FlameParticles(
            [new Flame("te4firetransp", new Vector3(0, 40, 0), 12.6f, 6f, true)]);

        Assert.Equal(0, particles.Count);

        for (int i = 0; i < 60; i++)
        {
            particles.Advance(1f / 60f, Vector3.Zero);
        }

        Assert.True(particles.Count > 0, "a second of a bowl of fire threw nothing");

        IReadOnlyList<Particle> drawn = particles.Facing(new Vector3(0, 40, -100));

        Assert.Contains(drawn, p => p.Additive > 0.5f);
        Assert.Contains(drawn, p => p.Additive < 0.5f);

        // Everything is above the fire and none of it has gone through the floor.
        Assert.All(drawn, p => Assert.True(p.Position.Y > 39f, $"a mote fell to {p.Position.Y}"));
    }

    [Fact]
    public void A_bigger_fire_makes_more_of_it()
    {
        // A chafing dish's sterno against the temple's bowl of fire. It is the whole of
        // what tells a candle from a bonfire, and it comes out of one number.
        var sterno = new FlameParticles(
            [new Flame("din_sternoflame", new Vector3(0, 40, 0), 1.4f, 1f, true)]);

        var bowl = new FlameParticles(
            [new Flame("te4firetransp", new Vector3(0, 40, 0), 12.6f, 6f, true)]);

        for (int i = 0; i < 120; i++)
        {
            sterno.Advance(1f / 60f, Vector3.Zero);
            bowl.Advance(1f / 60f, Vector3.Zero);
        }

        Assert.True(
            bowl.Count > sterno.Count * 2,
            $"the bowl had {bowl.Count} against the sterno's {sterno.Count}");
    }

    [Fact]
    public void A_fire_the_room_is_not_drawing_makes_no_smoke()
    {
        var hidden = new FlameParticles(
            [new Flame("te6_candles", new Vector3(0, 40, 0), 8f, 2f, Visible: false)]);

        for (int i = 0; i < 120; i++)
        {
            hidden.Advance(1f / 60f, Vector3.Zero);
        }

        Assert.Equal(0, hidden.Count);

        // Until a script lights it.
        hidden.Show("te6_candles", alight: true);

        for (int i = 0; i < 120; i++)
        {
            hidden.Advance(1f / 60f, Vector3.Zero);
        }

        Assert.True(hidden.Count > 0, "a candle a script lit still made nothing");
    }

    [Fact]
    public void A_fire_across_the_map_costs_nothing()
    {
        var far = new FlameParticles(
            [new Flame("ma1_flameff", new Vector3(0, 40, 0), 6f, 3f, true)]);

        for (int i = 0; i < 120; i++)
        {
            far.Advance(1f / 60f, new Vector3(0, 40, 100_000f));
        }

        Assert.Equal(0, far.Count);
    }

    [Fact]
    public void The_same_fire_makes_the_same_smoke_every_run()
    {
        // Two renders of one room have to be comparable, which is the basis of every image
        // check in this project. A particle system seeded from a clock is not.
        static IReadOnlyList<Particle> Run()
        {
            var particles = new FlameParticles(
                [new Flame("cs5_flame01", new Vector3(-25, 62, -344), 3.4f, 3f, true)]);

            for (int i = 0; i < 90; i++)
            {
                particles.Advance(1f / 60f, Vector3.Zero);
            }

            return [.. particles.Facing(Vector3.Zero)];
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Smoke_is_handed_over_furthest_first()
    {
        var particles = new FlameParticles(
            [new Flame("te4firetransp", new Vector3(0, 40, 0), 12.6f, 6f, true)]);

        for (int i = 0; i < 180; i++)
        {
            particles.Advance(1f / 60f, Vector3.Zero);
        }

        var eye = new Vector3(0, 200, 0);
        IReadOnlyList<Particle> drawn = particles.Facing(eye);

        Assert.True(drawn.Count > 1, "nothing to sort");

        for (int i = 1; i < drawn.Count; i++)
        {
            Assert.True(
                Vector3.Distance(drawn[i - 1].Position, eye) >=
                    Vector3.Distance(drawn[i].Position, eye) - 1e-3f,
                "the particles came back nearest first");
        }
    }

    [Fact]
    public void An_ember_is_drawn_over_the_room()
    {
        // The renderer is deferred and its material pass cannot blend anything. This is the
        // one pass that can, and the only way to know it ran is to read the pixel.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(20, 20, 90));
        geometry.Add(Wall("wall"));

        Camera camera = Facing();

        (byte _, byte _, byte plainBlue) = Pixel(renderer.Render(geometry, 128, 128, camera), 64, 64);

        // One large ember in front of the wall, dead centre.
        renderer.SetParticles(
            [new Particle(new Vector3(0, 0, -1f), 1.2f, new Vector4(1f, 0.5f, 0.1f, 1f), 0f, 1f)]);

        DecodedImage lit = renderer.Render(geometry, 128, 128, camera);

        (byte r, byte g, byte b) = Pixel(lit, 64, 64);

        Assert.True(r > 120, $"the ember did not brighten the wall: red was {r}");
        Assert.True(r > b, $"the ember was not warm: {r},{g},{b}");
        Assert.True(b >= plainBlue, "an added ember took light away");

        // And it is a disc, not the whole screen.
        (byte cornerR, byte _, byte _) = Pixel(lit, 4, 4);

        Assert.True(cornerR < 80, $"the ember covered the corner too: red was {cornerR}");
    }

    [Fact]
    public void A_particle_behind_a_wall_is_hidden_by_it()
    {
        // The pass tests the depth the room left and writes none of its own. Without the
        // test every fire in the game would burn through the wall in front of it.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(20, 20, 90));
        geometry.Add(Wall("wall"));

        Camera camera = Facing();

        // Behind the wall, from the camera's point of view.
        renderer.SetParticles(
            [new Particle(new Vector3(0, 0, 4f), 1.2f, new Vector4(1f, 0.5f, 0.1f, 1f), 0f, 1f)]);

        (byte r, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, camera), 64, 64);

        Assert.True(r < 80, $"an ember behind the wall drew through it: red was {r}");
    }

    [Fact]
    public void An_ember_is_drawn_over_the_room_on_Direct3D_too()
    {
        // The same picture from the same two shaders through a different API. The premise
        // of having one set of shaders is that both backends draw the same thing, and a
        // blend state is exactly the sort of thing that is set on one and forgotten on the
        // other.
        Assert.SkipUnless(HasDirect3D(), "no Direct3D device");

        using var renderer = GK3Reborn.Rendering.Direct3D12.D3D12SceneRenderer.Create();
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(20, 20, 90));
        geometry.Add(Wall("wall"));

        Camera camera = Facing();

        renderer.SetParticles(
            [new Particle(new Vector3(0, 0, -1f), 1.2f, new Vector4(1f, 0.5f, 0.1f, 1f), 0f, 1f)]);

        DecodedImage lit = renderer.Render(geometry, 128, 128, camera);

        (byte r, byte g, byte b) = Pixel(lit, 64, 64);

        Assert.True(r > 120, $"the ember did not brighten the wall: red was {r}");
        Assert.True(r > b, $"the ember was not warm: {r},{g},{b}");

        (byte cornerR, byte _, byte _) = Pixel(lit, 4, 4);

        Assert.True(cornerR < 80, $"the ember covered the corner too: red was {cornerR}");
    }

    [Fact]
    public void A_particle_behind_a_wall_is_hidden_by_it_on_Direct3D_too()
    {
        Assert.SkipUnless(HasDirect3D(), "no Direct3D device");

        using var renderer = GK3Reborn.Rendering.Direct3D12.D3D12SceneRenderer.Create();
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(20, 20, 90));
        geometry.Add(Wall("wall"));

        renderer.SetParticles(
            [new Particle(new Vector3(0, 0, 4f), 1.2f, new Vector4(1f, 0.5f, 0.1f, 1f), 0f, 1f)]);

        (byte r, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 64);

        Assert.True(r < 80, $"an ember behind the wall drew through it: red was {r}");
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

    /// <summary>A quad at the origin, large enough to fill the view.</summary>
    private static ModFile Wall(string texture)
    {
        Vector3[] positions =
        [
            new(-4, -4, 0), new(4, -4, 0), new(4, 4, 0), new(-4, 4, 0),
        ];

        return ModFile.FromMeshes(
            "wall",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = new Vector3(-4, -4, 0),
                    BoundsMax = new Vector3(4, 4, 0),
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

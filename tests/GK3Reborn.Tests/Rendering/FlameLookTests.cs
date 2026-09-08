// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// A look at the three fires, written out as pictures.
/// </summary>
public sealed class FlameLookTests
{
    [Fact(Explicit = true)]
    public void Photograph_the_three_fires()
    {
        string? into = Environment.GetEnvironmentVariable("GK3REBORN_FLAME_OUT");

        Assert.SkipWhen(into is not { Length: > 0 }, "no GK3REBORN_FLAME_OUT");
        Assert.SkipUnless(Available(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(26, 22, 20));
        geometry.Add(Wall("wall"));

        // A candle, a hearth and the temple's bowl, each drawn at the same size on screen
        // so that what separates them is the shape and not how large it is.
        (string Name, float Height, float Width, int Kind)[] fires =
        [
            ("candle", 3.4f, 2.2f, 0),
            ("hearth", 24.0f, 14.8f, 1),
            ("cauldron", 12.6f, 13.2f, 2),
        ];

        foreach ((string name, float height, float width, int kind) in fires)
        {
            for (int frame = 0; frame < 3; frame++)
            {
                float scale = 2.6f / height;
                float radius = MathF.Max(width / 2f, 0.05f) * scale;
                float tall = height * scale;

                float lift = tall * 0.56f;
                float reach = radius * 1.5f;

                renderer.SetParticles(
                    [new Particle(
                        new Vector3(0, 0, -1f),
                        MathF.Sqrt((lift * lift) + (reach * reach)) * 1.04f,
                        Vector4.One,
                        0f,
                        Particle.Fire,
                        new Vector4(tall, radius, 32.5f + (frame * 0.9f), kind))]);

                DecodedImage shot = renderer.Render(geometry, 512, 512, Facing());

                File.WriteAllBytes(
                    Path.Combine(into!, $"flame-{name}-{frame}.png"), PngWriter.Encode(shot));
            }
        }
    }

    private static bool Available()
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

    private static ModFile Wall(string texture)
    {
        Vector3[] positions =
        [
            new(-8, -8, 0), new(8, -8, 0), new(8, 8, 0), new(-8, 8, 0),
        ];

        return ModFile.FromMeshes(
            "wall",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = new Vector3(-8, -8, 0),
                    BoundsMax = new Vector3(8, 8, 0),
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
                            TexCoords = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
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

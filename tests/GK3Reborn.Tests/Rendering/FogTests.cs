// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the fog lying in a room, and for the pass that marches it.
/// </summary>
public sealed class FogTests
{
    // --- which rooms have any -------------------------------------------------------------

    [Fact]
    public void Only_the_rooms_the_table_names_have_fog()
    {
        // Two hundred rooms have none, and that is the point of the table rather than an
        // accident of it: fog in a room that does not want it is worse than no fog at all.
        foreach (string room in SceneFog.Rooms)
        {
            Assert.True(SceneFog.For(room).Any, $"{room} is listed and has no fog");
        }

        foreach (string room in SceneFog.NightRooms)
        {
            Assert.True(
                SceneFog.For(room, SceneFog.SmallHours).Any,
                $"{room} is listed for the small hours and has no fog in them");
        }

        foreach (string room in (string[])["R25", "CS2", "TE4", "LBY", "CHU", "GRI"])
        {
            Assert.False(SceneFog.For(room).Any, $"{room} was given fog it did not ask for");

            Assert.False(
                SceneFog.For(room, SceneFog.SmallHours).Any,
                $"{room} was given fog at night it did not ask for");
        }

        Assert.False(SceneFog.For(null).Any);
        Assert.False(SceneFog.For(string.Empty).Any);
        Assert.False(SceneFog.For(null, SceneFog.SmallHours).Any);
    }

    [Fact]
    public void The_rooms_that_are_only_foggy_at_night_are_clear_at_every_other_hour()
    {
        // The whole reason the hour is asked for. These four are outdoors and the player is
        // in every one of them far more often in daylight than at two in the morning; a
        // layer that did not know the difference would put a bank of mist between the
        // gravestones under a two o'clock sun.
        foreach (string room in SceneFog.NightRooms)
        {
            foreach (Timeblock daylight in Daylight)
            {
                Assert.False(
                    SceneFog.For(room, daylight).Any,
                    $"{room} is foggy at {daylight}, which is broad daylight");
            }

            // And a caller with no story state at all is treated as daylight, because a room
            // drawn without fog is the room as it shipped.
            Assert.False(SceneFog.For(room).Any, $"{room} fogged with no hour to go on");
        }

        // The two that are underground do not care what time it is.
        foreach (string room in SceneFog.Rooms)
        {
            Assert.Equal(SceneFog.For(room), SceneFog.For(room, SceneFog.SmallHours));
            Assert.Equal(SceneFog.For(room), SceneFog.For(room, Daylight[0]));
        }
    }

    [Fact]
    public void A_room_is_named_whichever_way_the_scene_file_spells_it()
    {
        // SIFs spell a scene name in both cases — the file is CS5.SIF and the line inside it
        // is scene=cs5 — and which of the two reaches here depends on who asked.
        Assert.Equal(SceneFog.For("CS5"), SceneFog.For("cs5"));
        Assert.Equal(SceneFog.For("TE5"), SceneFog.For("te5"));

        Assert.Equal(
            SceneFog.For("CEM", SceneFog.SmallHours),
            SceneFog.For("cem", SceneFog.SmallHours));
    }

    [Fact]
    public void The_village_is_one_place_and_carries_one_layer()
    {
        // RC1 to RC4 are four sides of Rennes-le-Château rather than four places, and the
        // player crosses between them by walking. A street that fogs and the corner it turns
        // into that does not would be worse than neither.
        FogVolume street = SceneFog.For("RC1", SceneFog.SmallHours);

        Assert.True(street.Any);

        foreach (string room in (string[])["RC2", "RC3", "RC4"])
        {
            Assert.Equal(street, SceneFog.For(room, SceneFog.SmallHours));
        }
    }

    [Fact]
    public void A_night_layer_lies_on_the_ground_and_leaves_the_room_over_it_clear()
    {
        // The outdoor rooms are walked through rather than looked into, so what is above the
        // mist has to be clear or the room reads as smoke rather than as weather. A standing
        // character's eye is about seventy units up and the cameras stand at fifty-six.
        foreach (string room in SceneFog.NightRooms)
        {
            FogVolume night = SceneFog.For(room, SceneFog.SmallHours);

            Assert.True(night.Top is > 0f and < 12f, $"{room}'s layer tops out at {night.Top}");

            Assert.True(
                night.Ceiling < 110f,
                $"{room}'s mist reaches {night.Ceiling}, which is well over head height");

            // And thin. Outdoors a ray is in the layer for hundreds of units before it meets
            // anything, so these are a third of the cellars' density at most; the same
            // figure as CS5's would close the view at thirty metres.
            Assert.InRange(night.Density, 0.0004f, 0.0025f);
        }
    }

    [Fact]
    public void The_cellar_lies_on_its_floor_and_the_chasm_lies_under_one()
    {
        FogVolume cellars = SceneFog.For("CS5");
        FogVolume chasm = SceneFog.For("TE5");

        // CS5's flagstones are at y = 0.3 to 0.5 and TE5's walkway at y = 0.1, both measured
        // by picking the floor. So the sign of the top says which side of the floor the
        // layer is on, and it is the whole difference between damp and a drop.
        Assert.True(cellars.Top > 0.5f, "the cellar's mist is under its own floor");

        // TE5's bridge deck is at y = 0.6 and its parapet tops out at 8, and the murk has to
        // be under the whole of that — not merely under the walkway. A layer that laps at
        // the lip makes the bridge span a bank of cloud rather than a drop, which is the
        // first thing this was reported as. The ceiling rather than the top, because the
        // ceiling is the highest the march reaches at all.
        Assert.True(
            chasm.Ceiling < -8f,
            $"the chasm's murk reaches {chasm.Ceiling}, which is up at the bridge");

        // And far enough down the shaft to have wall visible above it. The floor of the
        // chasm is at y = -725, so this is well clear of the bottom too.
        Assert.InRange(chasm.Top, -350f, -150f);

        // The room above the cellar's layer has to be clear the same way. Six falloffs is
        // where the march gives up; a standing character's eye is about seventy units up.
        Assert.True(
            cellars.Ceiling < 90f,
            $"the cellar's mist reaches {cellars.Ceiling}, which is over head height");
    }

    [Fact]
    public void A_layer_with_no_density_is_no_layer()
    {
        Assert.False(FogVolume.None.Any);
        Assert.False(SceneFog.For("CS5") with { Density = 0f } is { Any: true });
        Assert.False(SceneFog.For("CS5") with { Steps = 0 } is { Any: true });
    }

    // --- what the shader is told ----------------------------------------------------------

    [Fact]
    public void The_block_carries_the_layer_and_the_grid_the_room_was_lit_with()
    {
        var grid = SceneLightGrid.Build([], new Vector3(-100, -20, -300), new Vector3(500, 180, 400));

        FogVolume fog = SceneFog.For("TE5");

        FogConstants block = FogConstants.For(
            fog, grid, new Vector3(0.1f, 0.2f, 0.3f), Facing(), seconds: 4f, 320, 240);

        Assert.Equal(new Vector4(fog.Colour, fog.Density), block.Tint);
        Assert.Equal(fog.Top, block.Layer.X);
        Assert.Equal(fog.Falloff, block.Layer.Y);
        Assert.Equal(fog.Anisotropy, block.Layer.Z);
        Assert.Equal(fog.Steps, block.Grain.W);

        // The grid is the room's own rather than a second one worked out here, because the
        // fog walks it with exactly the lookup the shading does.
        Assert.Equal(new Vector4(grid.Origin, grid.Cell), block.GridOrigin);
        Assert.Equal(grid.Counts.X, block.GridCounts.X);
        Assert.Equal(grid.Counts.Z, block.GridCounts.Z);

        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0f), block.Ambient);
        Assert.Equal(new Vector4(320, 240, 0, 0), block.Screen);
        Assert.Equal(4f, block.EyeAndTime.W);
    }

    [Fact]
    public void A_room_whose_lights_have_not_arrived_still_gets_a_grid_to_look_in()
    {
        // The shader indexes the cell list with whatever it is handed. A count of nought
        // along an axis is an index into nothing, so the fallback is one cell rather than
        // none — the same default the mesh shader's own block carries.
        FogConstants block = FogConstants.For(
            SceneFog.For("CS5"), null, Vector3.Zero, Facing(), 0f, 64, 64);

        Assert.Equal(new Vector4(0, 0, 0, 1f), block.GridOrigin);
        Assert.Equal(new Vector4(1, 1, 1, 0), block.GridCounts);
    }

    [Fact]
    public void The_block_is_within_what_both_backends_will_take()
    {
        // A Direct3D root signature holds sixty-four words in total and this is a table
        // short of that; Vulkan's own guarantee is lower still and is why this is checked
        // rather than assumed. See ShaderLayout.
        FogLayout.Bindings.Validate();

        Assert.Equal(
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<FogConstants>(),
            FogLayout.Bindings.PushConstantBytes);

        Assert.True(
            FogLayout.Bindings.PushConstantBytes <= GK3Reborn.Rendering.Shaders.ShaderLayout.MaximumPushConstantBytes);
    }

    // --- what it draws --------------------------------------------------------------------

    [Fact]
    public void Fog_takes_light_off_the_room_behind_it()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 200, 200));
        geometry.Add(Wall("wall"));

        (byte clearR, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 64);

        // A layer with nothing to scatter: it can only take light away.
        renderer.SetFog(Everywhere with { Colour = Vector3.Zero, Ambient = 0f });

        (byte foggedR, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 64);

        Assert.True(
            foggedR < clearR - 20,
            $"the fog took nothing off the wall: {clearR} became {foggedR}");
    }

    [Fact]
    public void A_layer_is_a_layer_and_leaves_the_air_above_it_alone()
    {
        // The one thing that makes this fog rather than a wash over the frame. The top of
        // the layer sits on the camera's own eye line, so the bottom half of the picture is
        // through it and the top half is not.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 200, 200));
        geometry.Add(Wall("wall"));

        renderer.SetFog(Everywhere with { Top = 0f, Falloff = 0.05f });

        DecodedImage image = renderer.Render(geometry, 128, 128, Facing());

        (byte aboveR, byte _, byte _) = Pixel(image, 64, 20);
        (byte belowR, byte _, byte _) = Pixel(image, 64, 108);

        Assert.True(
            belowR != aboveR,
            "the layer made no difference between the floor and the ceiling");

        Assert.True(
            Math.Abs(aboveR - 200) < 30,
            $"the air over the layer was fogged too: {aboveR} against a wall of 200");
    }

    [Fact]
    public void The_room_lights_the_fog_rather_than_the_fog_carrying_a_colour()
    {
        // The whole reason this pass reads the rig. A red lamp in the layer has to make the
        // fog red; a fog with a colour of its own is the flat wash the horizon work already
        // rejected.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(30, 30, 30));
        geometry.Add(Wall("wall"));

        renderer.SetLights(
            [Lamp(new Vector3(0, 0, -2f), new Vector3(1f, 0.1f, 0.1f))],
            new SceneExtent(new Vector3(-8, -8, -8), new Vector3(8, 8, 8)));

        renderer.SetFog(Everywhere with { Colour = Vector3.One, Ambient = 0f });

        (byte r, byte g, byte b) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 64);

        Assert.True(r > g + 20 && r > b + 20, $"the lamp did not colour the fog: {r},{g},{b}");
    }

    [Fact]
    public void The_same_room_fogs_the_same_way_every_render()
    {
        // Every image check in this project rests on two renders of one room being one
        // picture. A march whose dither or noise moved with the frame would end that.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 200, 200));
        geometry.Add(Wall("wall"));

        renderer.SetFog(Everywhere with { NoiseScale = 2f, NoiseStrength = 0.5f, NoiseDrift = 7f });

        Assert.Equal(
            renderer.Render(geometry, 128, 128, Facing()).Pixels,
            renderer.Render(geometry, 128, 128, Facing()).Pixels);
    }

    [Fact]
    public void A_room_with_no_fog_is_drawn_exactly_as_it_was()
    {
        // The pass is not recorded at all where there is no layer, and this is what says so
        // from the outside: not "nearly the same picture" but the same bytes.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 200, 200));
        geometry.Add(Wall("wall"));

        byte[] before = renderer.Render(geometry, 128, 128, Facing()).Pixels;

        renderer.SetFog(Everywhere);
        renderer.SetFog(FogVolume.None);

        Assert.Equal(before, renderer.Render(geometry, 128, 128, Facing()).Pixels);
    }

    [Fact]
    public void Fog_takes_light_off_the_room_on_Direct3D_too()
    {
        // The same picture from the same shader through a different API. The premise of one
        // set of shaders is that both backends draw the same thing, and a blend state is
        // exactly what is set on one and forgotten on the other.
        Assert.SkipUnless(HasDirect3D(), "no Direct3D device");

        using var renderer = GK3Reborn.Rendering.Direct3D12.D3D12SceneRenderer.Create();
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 200, 200));
        geometry.Add(Wall("wall"));

        (byte clearR, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 64);

        renderer.SetFog(Everywhere with { Colour = Vector3.Zero, Ambient = 0f });

        (byte foggedR, byte _, byte _) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 64);

        Assert.True(
            foggedR < clearR - 20,
            $"the fog took nothing off the wall: {clearR} became {foggedR}");
    }

    [Fact]
    public void The_room_lights_the_fog_on_Direct3D_too()
    {
        // And this is the one that needs the three light buffers to have been written into
        // the pass's own table in the right order — which is a thing the Vulkan path cannot
        // check for it, because there they are descriptors rather than a run of slots.
        Assert.SkipUnless(HasDirect3D(), "no Direct3D device");

        using var renderer = GK3Reborn.Rendering.Direct3D12.D3D12SceneRenderer.Create();
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(30, 30, 30));
        geometry.Add(Wall("wall"));

        renderer.SetLights(
            [Lamp(new Vector3(0, 0, -2f), new Vector3(1f, 0.1f, 0.1f))],
            new SceneExtent(new Vector3(-8, -8, -8), new Vector3(8, 8, 8)));

        renderer.SetFog(Everywhere with { Colour = Vector3.One, Ambient = 0f });

        (byte r, byte g, byte b) = Pixel(renderer.Render(geometry, 128, 128, Facing()), 64, 64);

        Assert.True(r > g + 20 && r > b + 20, $"the lamp did not colour the fog: {r},{g},{b}");
    }

    // --- the story's own hours ------------------------------------------------------------

    /// <summary>
    /// Blocks the night rooms are reached in with the sun up, as the corpus names them.
    /// </summary>
    /// <remarks>
    /// The first and last of the day on each of the three, plus the noon the château is
    /// toured in. Every one of these is a block that at least one of the rooms above ships a
    /// scene file for, so each is a picture somebody can actually be looking at.
    /// </remarks>
    private static Timeblock[] Daylight { get; } =
    [
        new(1, 10, IsAfternoon: false),
        new(1, 6, IsAfternoon: true),
        new(2, 7, IsAfternoon: false),
        new(2, 12, IsAfternoon: true),
        new(2, 2, IsAfternoon: true),
        new(3, 10, IsAfternoon: false),
        new(3, 12, IsAfternoon: true),
    ];

    // --- the test room --------------------------------------------------------------------

    /// <summary>
    /// A layer thick enough to see across six units of test room.
    /// </summary>
    /// <remarks>
    /// The corpus's own densities are thousandths of a unit because its rooms are hundreds
    /// of units across; this quad is eight, so the same layer over it would be invisible.
    /// The top is well above the camera, which puts the whole of the view inside the layer —
    /// the tests that want an edge move it down.
    /// </remarks>
    private static FogVolume Everywhere { get; } = new(
        Colour: new Vector3(0.8f, 0.8f, 0.8f),
        Density: 0.35f,
        Top: 40f,
        Falloff: 4f,
        Anisotropy: 0.3f,
        Ambient: 1f,
        NoiseScale: 0f,
        NoiseDrift: 0f,
        NoiseStrength: 0f,
        Steps: 24);

    private static AuthoredLight Lamp(Vector3 at, Vector3 colour) => new(
        "lamp",
        AuthoredLightKind.Point,
        at,
        -Vector3.UnitY,
        colour,
        HotSpot: 0,
        Falloff: 0,
        AttenuationStart: 0,
        AttenuationEnd: 40,
        UsesAttenuation: true,
        CastsShadows: false,
        Intensity: 3f,
        Radius: 1f);

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

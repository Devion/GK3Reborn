// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the insects and dust in a room's air, and for the daylight at its windows:
/// which rooms get them, where they gather, and what the blended pass is handed.
/// </summary>
public sealed class DriftTests
{
    private static readonly Timeblock Morning = new(1, 10, IsAfternoon: false);
    private static readonly Timeblock SmallHours = new(2, 2, IsAfternoon: false);

    // --- which rooms ----------------------------------------------------------------------

    [Fact]
    public void A_wooded_room_under_the_sky_gets_insects_and_dust_and_a_cellar_gets_neither()
    {
        Drift wood = SceneDrift.For(Morning, crowns: 12, sunlit: true);
        Drift square = SceneDrift.For(Morning, crowns: 0, sunlit: true);
        Drift cellar = SceneDrift.For(Morning, crowns: 0, sunlit: false);

        Assert.True(wood.Insects > 0 && wood.Motes > 0);
        Assert.True(square.Insects == 0 && square.Motes > 0);
        Assert.False(cellar.Any);
    }

    [Fact]
    public void Nothing_drifts_in_the_small_hours()
    {
        Assert.False(SceneDrift.For(SmallHours, crowns: 12, sunlit: true).Any);
    }

    [Fact]
    public void A_caller_with_no_hour_gets_the_daylight_answer()
    {
        Assert.True(SceneDrift.For(null, crowns: 1, sunlit: true).Any);
    }

    [Fact]
    public void The_seed_is_the_same_on_every_run_and_differs_between_rooms()
    {
        // Pinned to a literal: a hash that came out differently on another machine would
        // blow the wind another way there, which is the fault the written-out FNV avoids.
        Assert.Equal(SceneDrift.Seed("WOD"), SceneDrift.Seed("wod"));
        Assert.NotEqual(SceneDrift.Seed("WOD"), SceneDrift.Seed("CEM"));
        Assert.Equal(0x752B0719AADDF12BUL, SceneDrift.Seed("WOD"));
    }

    // --- where the trees are --------------------------------------------------------------

    [Fact]
    public void Foliage_is_known_by_its_sprite_and_a_pine_is_told_from_a_maple()
    {
        Assert.True(SceneDrift.IsFoliage("MAPLESIDE1.BMP", out bool conifer));
        Assert.False(conifer);

        Assert.True(SceneDrift.IsFoliage("pine2", out conifer));
        Assert.True(conifer);

        // A painted hillside of trees is not a tree.
        Assert.False(SceneDrift.IsFoliage("TREEGROUP01", out _));
        Assert.False(SceneDrift.IsFoliage("RC1WALL", out _));
    }

    [Fact]
    public void A_card_cut_into_pieces_by_the_splitter_is_one_crown()
    {
        // One 200-unit maple card as three polygons across one surface, and a wall.
        Vector3[] vertices =
        [
            new(0, 0, 0), new(200, 0, 0), new(200, 80, 0), new(0, 80, 0),
            new(0, 80, 0), new(200, 80, 0), new(200, 160, 0), new(0, 160, 0),
            new(0, 160, 0), new(200, 160, 0), new(200, 240, 0), new(0, 240, 0),
            new(300, 0, 0), new(400, 0, 0), new(400, 300, 0), new(300, 300, 0),
        ];

        ushort[] indices = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

        BspFile room = BspFile.FromParts(
            "room",
            ["tree", "wall"],
            [
                Surface(0, "MAPLESIDE1"),
                Surface(1, "RC1WALL"),
            ],
            [
                new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 },
                new BspPolygon { VertexIndexOffset = 4, VertexIndexCount = 4, SurfaceIndex = 0 },
                new BspPolygon { VertexIndexOffset = 8, VertexIndexCount = 4, SurfaceIndex = 0 },
                new BspPolygon { VertexIndexOffset = 12, VertexIndexCount = 4, SurfaceIndex = 1 },
            ],
            vertices,
            [Vector2.Zero],
            indices);

        IReadOnlyList<Crown> crowns = SceneDrift.Crowns(room, null);

        Crown crown = Assert.Single(crowns);

        Assert.Equal(new Vector3(0, 0, 0), crown.Least);
        Assert.Equal(new Vector3(200, 240, 0), crown.Most);
        Assert.False(crown.Conifer);
    }

    [Fact]
    public void A_card_too_small_to_be_a_tree_is_not_one()
    {
        Vector3[] vertices = [new(0, 0, 0), new(20, 0, 0), new(20, 20, 0), new(0, 20, 0)];

        BspFile room = BspFile.FromParts(
            "room",
            ["shrub"],
            [Surface(0, "TREE00")],
            [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 }],
            vertices,
            [Vector2.Zero],
            [0, 1, 2, 3]);

        Assert.Empty(SceneDrift.Crowns(room, null));
    }

    // --- the insects ----------------------------------------------------------------------

    [Fact]
    public void Midges_gather_under_the_nearest_tree_and_are_few_and_faint()
    {
        DriftField field = Wood(out Crown crown);
        Camera view = Looking(crown.Centre);

        for (int i = 0; i < 10 * 60; i++)
        {
            field.Advance(1f / 60f, view);
        }

        IReadOnlyList<Particle> about = field.Facing(view);

        // Some, and not many: a knot of them and a couple of flies.
        Assert.InRange(about.Count, 3, 12);

        foreach (Particle insect in about)
        {
            // Under the crown, not in it or above it.
            Assert.True(insect.Position.Y < crown.Most.Y, $"an insect was at {insect.Position.Y}");

            // Tiny and see-through.
            Assert.True(insect.Size < 0.8f, $"an insect was {insect.Size} across");
            Assert.True(insect.Tint.W < 0.5f, $"an insect was {insect.Tint.W} opaque");
        }
    }

    [Fact]
    public void A_knot_comes_in_from_nothing_rather_than_appearing()
    {
        DriftField field = Wood(out Crown crown);
        Camera view = Looking(crown.Centre);

        field.Advance(1f / 60f, view);

        // On the first frame the knot is at nought and the flies are all there is.
        foreach (Particle insect in field.Facing(view))
        {
            Assert.True(insect.Tint.W < 0.31f);
        }
    }

    // --- the dust -------------------------------------------------------------------------

    [Fact]
    public void The_dust_travels_with_the_camera_and_is_never_born_bright()
    {
        var field = new DriftField(new Drift(0, 60), [], SceneDrift.Seed("RC1"));

        Camera here = Looking(Vector3.Zero, from: new Vector3(0, 60, -300));

        for (int i = 0; i < 120; i++)
        {
            field.Advance(1f / 60f, here);
        }

        IReadOnlyList<Particle> around = field.Facing(here);

        Assert.True(around.Count > 30, $"only {around.Count} specks were drawn");

        // Cut to a camera on the far side of the room: the box moves with it, and every
        // speck that was out of the new box is reborn at nought and fades in.
        Camera there = Looking(Vector3.Zero, from: new Vector3(5000, 60, 5000));

        // A tenth of a second later: every speck is young, and the young are faint.
        for (int i = 0; i < 6; i++)
        {
            field.Advance(1f / 60f, there);
        }

        IReadOnlyList<Particle> reborn = field.Facing(there);

        Assert.True(reborn.Count > 0, "nothing was reborn round the new camera");

        foreach (Particle speck in reborn)
        {
            Assert.True(
                Vector3.Distance(speck.Position, there.Position) < 800f,
                "a speck was left behind at the old camera");
            Assert.True(speck.Tint.W < 0.1f, $"a speck was born at {speck.Tint.W}");
        }
    }

    [Fact]
    public void A_speck_in_a_shaft_of_daylight_is_brighter_and_takes_its_colour()
    {
        var field = new DriftField(new Drift(0, 200), [], SceneDrift.Seed("CHU"));
        Camera view = Looking(Vector3.Zero, from: new Vector3(0, 60, -300));

        for (int i = 0; i < 120; i++)
        {
            field.Advance(1f / 60f, view);
        }

        float brightest = 0f;

        foreach (Particle speck in field.Facing(view))
        {
            brightest = MathF.Max(brightest, speck.Tint.W);
        }

        // Now a shaft straight down through the middle of the box, wide enough to hold
        // a good share of it, and red.
        field.LitBy(
        [
            new LightShaft(
                new Vector3(0, 300, -140), -Vector3.UnitY, 120f, 120f, 400f,
                new Vector3(1f, 0.1f, 0.1f), 1f),
        ]);

        int lit = 0;

        foreach (Particle speck in field.Facing(view))
        {
            if (speck.Tint.W > brightest * 1.5f)
            {
                lit++;
                Assert.True(speck.Tint.X > speck.Tint.Y * 1.5f, "a lit speck did not take the light's colour");
            }
        }

        Assert.True(lit > 5, $"only {lit} specks were lit by the shaft");
    }

    [Fact]
    public void A_dark_room_with_nothing_in_it_hands_the_pass_nothing()
    {
        var field = new DriftField(Drift.None, [], 1UL);

        field.Advance(1f, Looking(Vector3.Zero));

        Assert.False(field.Any);
        Assert.Empty(field.Facing(Looking(Vector3.Zero)));
    }

    [Fact]
    public void The_same_room_drifts_the_same_way_every_run()
    {
        DriftField a = Wood(out Crown crown);
        DriftField b = Wood(out _);
        Camera view = Looking(crown.Centre);

        for (int i = 0; i < 600; i++)
        {
            a.Advance(1f / 60f, view);
            b.Advance(1f / 60f, view);
        }

        Assert.Equal(a.Facing(view), b.Facing(view));
    }

    // --- the daylight at the windows -----------------------------------------------------

    [Fact]
    public void A_window_the_sun_reaches_throws_the_suns_own_shaft_and_the_others_throw_skylight()
    {
        // A room from -400 to 400, with a pane in its east wall and one in its west wall.
        (Vector3, Vector3) room = (new Vector3(-400, 0, -400), new Vector3(400, 300, 400));

        var panes = new List<(string, Vector3, Vector3)>
        {
            ("east_window", new Vector3(398, 100, -40), new Vector3(402, 200, 40)),
            ("west_window", new Vector3(-402, 100, -40), new Vector3(-398, 200, 40)),
        };

        // The sun in the east, forty degrees up: its light travels west and down.
        var sun = new AuthoredLight(
            "sun", AuthoredLightKind.Point, new Vector3(60000, 50000, 0),
            Vector3.Normalize(new Vector3(-0.77f, -0.64f, 0f)), new Vector3(1f, 0.95f, 0.9f),
            0, 0, 0, 0, false, true, 1f, 1f);

        IReadOnlyList<LightShaft> shafts = SceneShafts.For(panes, sun, room, _ => 0f);

        Assert.Equal(2, shafts.Count);

        LightShaft east = shafts[0];
        LightShaft west = shafts[1];

        // The east window takes the sun's light along the sun's own direction; the west
        // window, which the sun is behind, throws softer light inward and down.
        Assert.Equal(1f, east.Strength);
        Assert.True(Vector3.Dot(east.Direction, sun.Direction) > 0.999f);

        Assert.True(west.Strength < east.Strength);
        Assert.True(west.Direction.X > 0.5f, "the west window's light did not come inward");
        Assert.True(west.Direction.Y < 0f, "the west window's light did not come down");

        // Both reach the floor and no further than the room.
        foreach (LightShaft shaft in shafts)
        {
            Vector3 end = shaft.Origin + (shaft.Direction * shaft.Length);

            Assert.InRange(end.Y, -5f, 5f);
            Assert.Equal(40f, shaft.HalfWidth);
        }
    }

    [Fact]
    public void No_sun_means_no_shafts_and_a_box_that_is_not_a_pane_throws_none()
    {
        (Vector3, Vector3) room = (new Vector3(-400, 0, -400), new Vector3(400, 300, 400));

        var panes = new List<(string, Vector3, Vector3)>
        {
            ("east_window", new Vector3(398, 100, -40), new Vector3(402, 200, 40)),
            ("window_seat", new Vector3(300, 0, -60), new Vector3(400, 80, 60)),
        };

        var sun = new AuthoredLight(
            "sun", AuthoredLightKind.Point, Vector3.Zero,
            Vector3.Normalize(new Vector3(-0.77f, -0.64f, 0f)), Vector3.One,
            0, 0, 0, 0, false, true, 1f, 1f);

        Assert.Empty(SceneShafts.For(panes, null, room, null));
        Assert.Single(SceneShafts.For(panes, sun, room, null));
    }

    [Fact]
    public void A_point_in_a_shaft_is_inside_it_and_one_beside_it_is_not()
    {
        var shaft = new LightShaft(
            new Vector3(0, 200, 0), -Vector3.UnitY, 40f, 60f, 200f, Vector3.One, 1f);

        Assert.True(shaft.Inside(new Vector3(10, 100, 10), out float along) < 1f);
        Assert.Equal(100f, along, 3);
        Assert.True(shaft.Inside(new Vector3(80, 100, 0), out _) > 1f);
    }

    [Fact]
    public void A_room_with_a_ceiling_is_roofed_and_a_field_is_not()
    {
        // Sixty-four texels of open floor over four hundred units, under a slab of ceiling.
        var walkable = new WalkBoundary(
            new IndexedImage(8, 8, new byte[64]), new Vector2(400, 400), new Vector2(200, 200));

        BspFile roofed = BspFile.FromParts(
            "room",
            ["ceiling"],
            [Surface(0, "PLASTER")],
            [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 }],
            [new(-300, 200, -300), new(300, 200, -300), new(300, 200, 300), new(-300, 200, 300)],
            [Vector2.Zero],
            [0, 1, 2, 3]);

        BspFile open = BspFile.FromParts(
            "field",
            ["ground"],
            [Surface(0, "GRASS")],
            [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 }],
            [new(-300, 0, -300), new(300, 0, -300), new(300, 0, 300), new(-300, 0, 300)],
            [Vector2.Zero],
            [0, 1, 2, 3]);

        Assert.True(SceneShafts.IsRoofed(roofed, walkable));
        Assert.False(SceneShafts.IsRoofed(open, walkable));
    }

    // --- helpers --------------------------------------------------------------------------

    private static BspSurface Surface(int owner, string texture) => new()
    {
        ObjectIndex = owner,
        TextureName = texture,
        LightmapUvOffset = Vector2.Zero,
        LightmapUvScale = Vector2.One,
        Flags = 0,
    };

    /// <summary>A field with one broadleaf crown.</summary>
    private static DriftField Wood(out Crown crown)
    {
        crown = new Crown(new Vector3(-100, 120, -100), new Vector3(100, 320, 100), Conifer: false);

        return new DriftField(
            SceneDrift.For(Morning, 1, sunlit: false),
            [crown],
            SceneDrift.Seed("WOD"));
    }

    private static Camera Looking(Vector3 at, Vector3? from = null) => new()
    {
        Position = from ?? (at + new Vector3(0, 40, -400)),
        Target = at,
        Up = Vector3.UnitY,
    };
}

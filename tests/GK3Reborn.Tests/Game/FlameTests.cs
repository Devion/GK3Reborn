// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for finding a room's open flames and making the light in it move.
/// </summary>
public sealed class FlameTests
{
    [Fact]
    public void A_flame_is_found_by_the_bitmap_it_is_painted_with()
    {
        // The lanterns of CS5, the chafing dishes of the dining room and the tomb's candles
        // are all the same quad with the same bitmap on it.
        IReadOnlyList<Flame> found = Flames.In(
            [Prop("cs5_flame01", "CS5FLAME", new Vector3(-25, 62, -344), height: 3.4f)], null);

        Flame flame = Assert.Single(found);

        Assert.Equal("cs5_flame01", flame.Model);
        Assert.Equal(-344f, flame.Position.Z, 3);
        Assert.Equal(3.4f, flame.Height, 3);
    }

    [Fact]
    public void A_prop_that_is_not_a_fire_is_not_one()
    {
        Assert.Empty(Flames.In(
            [Prop("lby_fan", "LBYFANBLADE", Vector3.Zero, height: 4f)], null));
    }

    [Fact]
    public void A_character_is_never_a_fire()
    {
        // Asking would read the whole cast's geometry in every room, and no character in
        // the game is painted with a flame.
        PlacedModel person = Prop("gab", "CS5FLAME", Vector3.Zero, height: 2f)
            with { Kind = PlacedModelKind.Actor };

        Assert.Empty(Flames.In([person], null));
    }

    [Fact]
    public void A_fire_that_only_becomes_one_when_its_script_paints_it_is_still_found()
    {
        // CS8_FIRE, RL2_FIRE and TE1FIRE ship painted with a floor and a column. They are
        // fires because their behaviour script's first [MTEXTURES] line makes them one, so
        // the authored texture alone finds none of the three.
        var library = new AnimationLibrary(name =>
            string.Equals(name, "cs8fireplacefire.ANM", StringComparison.OrdinalIgnoreCase)
                ? "[HEADER]\n13\n\n[MTEXTURES]\n1\n0,cs8_fire,0,0,te2firehi1t\n"
                : null);

        PlacedModel fire = Prop("cs8_fire", "RL2FLOOR", new Vector3(91, 36, 45), height: 9f)
            with { Idle = GasFile.Parse("ANIM cs8fireplacefire\nLOOP"u8) };

        Flame flame = Assert.Single(Flames.In([fire], library));

        Assert.Equal("cs8_fire", flame.Model);
    }

    [Fact]
    public void A_swap_aimed_at_another_room_s_fire_does_not_set_this_one_alight()
    {
        // TE2FIREHI is played by the bar's fire, the chapel's and the temple's brazier
        // alike, and every line in it names the model it was authored against. Matching the
        // texture and not the name would make a floor a fire in whichever room shares the
        // clip.
        var library = new AnimationLibrary(name =>
            string.Equals(name, "cs8fireplacefire.ANM", StringComparison.OrdinalIgnoreCase)
                ? "[HEADER]\n13\n\n[MTEXTURES]\n1\n0,cs8_fire,0,0,te2firehi1t\n"
                : null);

        PlacedModel floor = Prop("rl2_floor", "RL2FLOOR", Vector3.Zero, height: 1f)
            with { Idle = GasFile.Parse("ANIM cs8fireplacefire\nLOOP"u8) };

        Assert.Empty(Flames.In([floor], library));
    }

    [Fact]
    public void The_two_halves_of_a_back_to_back_card_are_one_fire()
    {
        // Nearly every flame in the game is modelled twice so that it draws from either
        // side. Counted as two, a room would get twice the light and twice the smoke.
        PlacedModel doubled = Prop(
            "cs5_flame01", "CS5FLAME", new Vector3(10, 60, 20), height: 3.4f, copies: 2);

        Assert.Single(Flames.In([doubled], null));
    }

    [Fact]
    public void Five_candles_in_one_model_are_five_fires()
    {
        // TE6_CANDLES is five candles around a tomb in a single file, a hundred units
        // apart. It is why the merge above cannot simply be "one model, one flame".
        var meshes = new List<ModMesh>();

        for (int i = 0; i < 5; i++)
        {
            meshes.Add(Card("CS5FLAME", new Vector3(i * 100f, 67f, 0f), 8f));
        }

        PlacedModel candles = new(
            "te6_candles", null, null, ModFile.FromMeshes("te6_candles", meshes),
            Matrix4x4.Identity, PlacedModelKind.Prop);

        Assert.Equal(5, Flames.In([candles], null).Count);
    }

    [Fact]
    public void A_stone_lying_in_a_fire_is_found_and_the_bowl_holding_it_is_not()
    {
        // TE4's bowl of fire, to scale: a flame 12.6 units tall in a bowl 21 across, with
        // a pebble 1.8 across at the bottom of it. The stone is what the room's puzzle is
        // about and the flame card is opaque where it is lit, so from anywhere but
        // overhead there is nothing in the bowl but fire.
        // Measured off TE4A: the bowl is 21.4 x 10.2 x 22.2 and the card 12.6 tall.
        var flame = new Flame(
            "te4firetransp", new Vector3(-113.3f, 35.5f, -216.3f), 12.6f, 21.4f, true);

        (string, Vector3, Vector3)[] objects =
        [
            ("te4stonefire_scene",
                new Vector3(-111.9f, 30.9f, -214.6f),
                new Vector3(-110.1f, 32.6f, -212.9f)),

            // The bowl itself has its middle under the flame too, and is not a thing
            // lying in it.
            ("te4firebasin",
                new Vector3(-124.0f, 30.4f, -227.4f),
                new Vector3(-102.6f, 40.6f, -205.2f)),

            // And the bowl of acid next to it is somewhere else.
            ("te4acidbasin",
                new Vector3(-142.0f, 30.4f, -217.1f),
                new Vector3(-120.5f, 40.6f, -194.7f)),
        ];

        (Flame lit, string held, _) = Assert.Single(Flames.Holding([flame], objects));

        Assert.Equal("te4stonefire_scene", held);
        Assert.Equal(flame.Model, lit.Model);
    }

    [Fact]
    public void A_bigger_fire_swings_further_and_more_slowly()
    {
        // A candle is nervous and a bonfire surges. Reading it the other way round is the
        // single thing that makes an artificial fire look artificial.
        var sterno = new Flame("din_sternoflame", Vector3.Zero, 1.4f, 1f, true);
        var bowl = new Flame("te4firetransp", Vector3.Zero, 12.6f, 6f, true);

        Assert.True(bowl.Swing > sterno.Swing, "the larger fire swings less far");
        Assert.True(bowl.Rate < sterno.Rate, "the larger fire flickers faster");
    }

    [Fact]
    public void The_light_the_artists_put_in_a_flame_wavers_about_its_own_brightness()
    {
        var flame = new Flame("cs5_flame01", new Vector3(0, 60, 0), 3.4f, 3f, true);

        AuthoredLight lantern = Light("cs5_lantern_light01", new Vector3(0.3f, 60, 0));

        AuthoredLight lit = Assert.Single(FlameLighting.Rig([lantern], [flame]));

        Assert.Equal(1f, Assert.NotNull(lit.Flicker).Bias);
        Assert.Equal(flame.Swing, lit.Flicker.Value.Swing, 4);

        // And its brightness is the artists' own, untouched.
        Assert.Equal(lantern.Intensity, lit.Intensity);
    }

    [Fact]
    public void A_light_across_the_room_is_left_alone()
    {
        // Every light an artist put in a flame is within 8.3 units of it; the nearest that
        // is plainly something else is 30.5. Sixteen separates them with room on both sides.
        var flame = new Flame("cs8_fire", Vector3.Zero, 9f, 5f, true);

        AuthoredLight elsewhere = Light("omni23", new Vector3(30.5f, 0, 0));

        AuthoredLight kept = FlameLighting.Rig([elsewhere], [flame])[0];

        Assert.Null(kept.Flicker);
    }

    [Fact]
    public void A_fire_the_artists_lit_gets_no_light_of_its_own_added()
    {
        var flame = new Flame("cs5_flame01", Vector3.Zero, 3.4f, 3f, true);

        Assert.Single(FlameLighting.Rig([Light("cs5_lantern_light01", new Vector3(0.3f, 0, 0))], [flame]));
    }

    [Fact]
    public void A_fire_the_artists_left_dark_is_given_a_light_that_averages_to_nothing()
    {
        // The temple's bowl of fire, the bar's fireplace and MA1's brazier are lit entirely
        // by the bake. A light added for them has to add movement without adding exposure,
        // or the room gets brighter than it has ever been.
        var bowl = new Flame("te4firetransp", new Vector3(-110, 39, -213), 12.6f, 6f, true);

        AuthoredLight lantern = Light("alantern_omni3", new Vector3(-42, 39, -213));

        IReadOnlyList<AuthoredLight> rig = FlameLighting.Rig([lantern], [bowl]);

        Assert.Equal(2, rig.Count);
        Assert.Null(rig[0].Flicker);

        FlameFlicker added = Assert.NotNull(rig[1].Flicker);

        Assert.Equal(0f, added.Bias);
        Assert.True(added.Swing > 0f, "a synthesized flame light with no swing lights nothing");
    }

    [Fact]
    public void A_fire_the_room_is_not_drawing_yet_is_not_lit()
    {
        // TE6 keeps its candles hidden until a script lights them, and a light standing in
        // an unlit candle is a glow with no source.
        var hidden = new Flame("te6_candles", Vector3.Zero, 8f, 2f, Visible: false);

        Assert.Empty(FlameLighting.Rig([], [hidden]));
    }

    [Fact]
    public void A_room_with_no_fire_in_it_gets_its_rig_back_untouched()
    {
        AuthoredLight[] rig = [Light("omni01", Vector3.Zero), Light("spot01", Vector3.One)];

        Assert.Same(rig, FlameLighting.Rig(rig, []));
    }

    [Fact]
    public void Two_candles_on_one_table_do_not_pulse_together()
    {
        // Fourteen candles around a tomb surging in unison reads as the room's lighting
        // being switched rather than as fourteen candles.
        var flame = new Flame("te6_flame01", Vector3.Zero, 8f, 2f, true);

        AuthoredLight first = Light("candleside_glow_special", new Vector3(1f, 0f, 0f));
        AuthoredLight second = Light("candleside_glow_special01", new Vector3(0f, 0f, 1f));

        IReadOnlyList<AuthoredLight> rig = FlameLighting.Rig([first, second], [flame]);

        Assert.NotEqual(
            Assert.NotNull(rig[0].Flicker).Seed,
            Assert.NotNull(rig[1].Flicker).Seed);
    }

    [Fact]
    public void A_light_that_stands_still_packs_as_the_identity()
    {
        // (0, 1, 0, 0), whose multiplier is bias + swing * wave = 1 at every instant. It is
        // what makes every room with no fire in it shade exactly as it always has.
        GpuLight steady = GpuLight.From(Light("omni01", Vector3.Zero));

        Assert.Equal(GpuLight.Steady, steady.Flicker);
    }

    [Fact]
    public void A_flame_light_carries_its_waver_to_the_shader()
    {
        AuthoredLight lantern = Light("cs5_lantern_light01", Vector3.Zero) with
        {
            Flicker = new FlameFlicker(0.15f, 1f, 1.8f, 0.42f),
        };

        GpuLight packed = GpuLight.From(lantern);

        Assert.Equal(new Vector4(0.15f, 1f, 1.8f, 0.42f), packed.Flicker);
    }

    [Fact]
    public void Each_of_the_three_bitmaps_is_its_own_kind_of_fire()
    {
        // The models are all the same flat card with the same script over it, so which set
        // was painted onto it is the only thing that tells the temple's bowl of fire from a
        // candle in a lantern — and they are drawn as three different fires.
        Assert.Equal(FlameKind.Candle, Flames.KindOf("CS5FLAME02"));
        Assert.Equal(FlameKind.Hearth, Flames.KindOf("TE2FIREHI1T"));
        Assert.Equal(FlameKind.Cauldron, Flames.KindOf("TE4FIRETRANSP7"));
        Assert.Null(Flames.KindOf("RL2FLOOR"));
    }

    [Fact]
    public void A_fire_burns_over_the_part_of_its_card_that_is_painted()
    {
        // A flame card is nearly always bigger than the flame on it: the bar's fire is a
        // quad twenty-four units tall with a low fire across the bottom third. Read the
        // card as the fire and the bar gets a bonfire in its fireplace.
        Flame flame = Assert.Single(Flames.In(
            [Prop("rl2_fire", "TE2FIREHI1T", new Vector3(0, 31, 0), height: 24f)],
            null,
            Painted(rows: (16, 47), columns: (16, 47))));

        // Rows 16 to 47 of 64 and the card textured the right way up.
        Assert.Equal(0.25f, flame.Paint.X, 2);
        Assert.Equal(0.75f, flame.Paint.Y, 2);

        Assert.Equal(12f, flame.Plume, 1);
        Assert.Equal(25f, flame.Foot.Y, 1);

        // And the card's own measurements are untouched, because the light and the smoke
        // are scaled off how big the fire is rather than off what is drawn.
        Assert.Equal(24f, flame.Height, 1);
    }

    [Fact]
    public void A_card_whose_texture_coordinates_are_negative_is_measured_all_the_same()
    {
        // GK3's are: a flame card's corners carry -0.02 and -0.98 rather than 0.98 and
        // 0.02. The sampler repeats and draws the same texels either way; taken at face
        // value the painted band falls outside the card and every fire in the game comes
        // out an inch tall.
        PlacedModel wrapped = Prop("te4firetransp", "TE4FIRETRANSP1", Vector3.Zero, height: 10f)
            with { Model = Wrapped("te4firetransp", "TE4FIRETRANSP1", 10f) };

        Flame flame = Assert.Single(Flames.In(
            [wrapped], null, Painted(rows: (16, 47), columns: (0, 63))));

        Assert.Equal(5f, flame.Plume, 1);
    }

    [Fact]
    public void A_fire_with_no_bitmap_to_read_burns_the_whole_of_its_card()
    {
        Flame flame = Assert.Single(Flames.In(
            [Prop("cs5_flame01", "CS5FLAME", Vector3.Zero, height: 3.4f)], null));

        Assert.Equal(3.4f, flame.Plume, 3);
        Assert.Equal(new Vector3(0f, 1f, 1f), flame.Paint);
    }

    [Fact]
    public void The_painted_cards_are_taken_out_of_the_picture()
    {
        // The card is the 1999 fire and it is opaque where it is lit, so a fire drawn as a
        // volume with its card still standing is a flame with a rectangle through it. Both
        // halves of a back-to-back card go, or one of them is that rectangle.
        var sink = new HeadlessSceneSink();

        PlacedModel doubled = Prop(
            "cs5_flame01", "CS5FLAME", new Vector3(10, 60, 20), height: 3.4f, copies: 2)
            with { Stage = sink, Placement = new ModelPlacement(0) };

        Flame flame = Assert.Single(Flames.In([doubled], null));

        Assert.Equal(2, flame.Cards.Count);
        Assert.Equal(2, Flames.Hide([flame], [doubled]));
        Assert.Equal(2, sink.HiddenPartCount);
    }

    /// <summary>A bitmap with flame painted on one band of it and nothing anywhere else.</summary>
    private static Func<string, DecodedImage?> Painted(
        (int First, int Last) rows, (int First, int Last) columns)
    {
        const int Size = 64;
        byte[] pixels = new byte[Size * Size * 4];

        for (int y = rows.First; y <= rows.Last; y++)
        {
            for (int x = columns.First; x <= columns.Last; x++)
            {
                int at = ((y * Size) + x) * 4;

                pixels[at] = 255;
                pixels[at + 1] = 128;
                pixels[at + 3] = 255;
            }
        }

        return _ => new DecodedImage(Size, Size, pixels, HasAlpha: true, "flame");
    }

    /// <summary>One upright quad textured the way GK3 textures them, with negative V.</summary>
    private static ModFile Wrapped(string name, string texture, float height) =>
        ModFile.FromMeshes(
            name,
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = new Vector3(-1, -height / 2, 0),
                    BoundsMax = new Vector3(1, height / 2, 0),
                    Submeshes =
                    [
                        new ModSubmesh
                        {
                            TextureName = texture,
                            Color = (255, 255, 255),
                            Positions =
                            [
                                new Vector3(-1, -height / 2, 0),
                                new Vector3(1, -height / 2, 0),
                                new Vector3(1, height / 2, 0),
                                new Vector3(-1, height / 2, 0),
                            ],
                            Normals =
                            [
                                Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ,
                            ],
                            TexCoords =
                            [
                                new Vector2(0f, -1f + 1e-3f),
                                new Vector2(1f, -1f + 1e-3f),
                                new Vector2(1f, -1e-3f),
                                new Vector2(0f, -1e-3f),
                            ],
                            Indices = [0, 1, 2, 0, 2, 3],
                        },
                    ],
                },
            ]);

    private static AuthoredLight Light(string name, Vector3 position) =>
        new(name, AuthoredLightKind.Point, position, -Vector3.UnitY, Vector3.One,
            0f, 0f, 10f, 80f, true, false, 1f, 1f);

    private static PlacedModel Prop(
        string name, string texture, Vector3 at, float height, int copies = 1)
    {
        var meshes = new List<ModMesh>();

        for (int i = 0; i < copies; i++)
        {
            meshes.Add(Card(texture, at, height));
        }

        return new PlacedModel(
            name, null, null, ModFile.FromMeshes(name, meshes),
            Matrix4x4.Identity, PlacedModelKind.Prop);
    }

    /// <summary>One upright quad, centred where it is asked for.</summary>
    private static ModMesh Card(string texture, Vector3 at, float height) => new()
    {
        MeshToLocal = Matrix4x4.CreateTranslation(at),
        BoundsMin = new Vector3(-1, -height / 2, 0),
        BoundsMax = new Vector3(1, height / 2, 0),
        Submeshes =
        [
            new ModSubmesh
            {
                TextureName = texture,
                Color = (255, 255, 255),
                Positions =
                [
                    new Vector3(-1, -height / 2, 0),
                    new Vector3(1, -height / 2, 0),
                    new Vector3(1, height / 2, 0),
                    new Vector3(-1, height / 2, 0),
                ],
                Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                TexCoords = [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY],
                Indices = [0, 1, 2, 0, 2, 3],
            },
        ],
    };
}

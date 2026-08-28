using System.Numerics;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for what a character is wearing when the room opens.
/// </summary>
/// <remarks>
/// A GK3 character owns one model and changes clothes by repainting it, out of a one-frame
/// animation named by <c>CHARACTERS.TXT</c> against the timeblock it starts applying at.
/// Both halves are pinned here: which animation a point in the story picks, and what
/// applying it does to the model.
/// </remarks>
public sealed class WardrobeTests
{
    /// <summary>Grace's own entry, verbatim, minus the parts nothing here reads.</summary>
    private const string Grace = """
        [GRA]
        WalkerHeight=72.0
        ShoeType=Female Leather
        ClothesDefault=GraClothes110a
        Clothes207a=GraClothes207a
        Clothes307a=GraClothes307a
        """;

    [Fact]
    public void A_character_wears_the_last_outfit_the_story_has_reached()
    {
        CharacterConfig gra = CharacterLibrary.Parse(Grace).Of("gra")!;

        // The second morning is exactly when the second outfit starts, and the third has
        // not happened yet.
        Assert.Equal("GraClothes207a", gra.ClothingFor(new Timeblock(2, 7, false)));

        // And it goes on being what she is wearing for the rest of that day.
        Assert.Equal("GraClothes207a", gra.ClothingFor(new Timeblock(2, 2, true)));
    }

    [Fact]
    public void The_default_outfit_is_worn_until_a_dated_one_applies()
    {
        CharacterConfig gra = CharacterLibrary.Parse(Grace).Of("gra")!;

        // Day one names no clothes of its own beyond the default, and the default is the
        // day-one outfit. A character with no story to place them wears it too.
        Assert.Equal("GraClothes110a", gra.ClothingFor(new Timeblock(1, 10, false)));
        Assert.Equal("GraClothes110a", gra.ClothingFor(null));
    }

    [Fact]
    public void A_dated_outfit_beats_the_default_however_the_file_orders_them()
    {
        // Wilkes is the case that makes this matter: his default is camouflage and his
        // 207a is not, so reading the default as "the first entry wins" would leave him in
        // fatigues for the rest of the game. The order is inverted here to prove the rule
        // is about the word "Default" and not about position.
        CharacterConfig wi2 = CharacterLibrary.Parse("""
            [WI2]
            Clothes207a=Wi2Clothes207a
            ClothesDefault=Wi2ClothesCamo
            """).Of("wi2")!;

        Assert.Equal("Wi2Clothes207a", wi2.ClothingFor(new Timeblock(2, 7, false)));
        Assert.Equal("Wi2ClothesCamo", wi2.ClothingFor(new Timeblock(1, 10, false)));
    }

    [Fact]
    public void A_character_the_file_gives_no_clothes_has_none_to_change_into()
    {
        CharacterConfig abe = CharacterLibrary.Parse("""
            [ABE]
            WalkerHeight=72.0
            ShoeType=Male Sneaker
            """).Of("abe")!;

        Assert.Empty(abe.Clothes);
        Assert.Null(abe.ClothingFor(new Timeblock(2, 7, false)));
    }

    [Fact]
    public void Dressing_repaints_the_groups_the_animation_names_and_nothing_else()
    {
        ModFile gra = Model("gra", "GRA_FOOT", "GRA_JEAN", "GRA_GRN", "GRA_FACE");

        // Grace's real second-day change, cut to the meshes this stand-in has.
        ModFile dressed = Wardrobe.Dress(gra, "gra", Clothes("""
            [HEADER]
            2

            [MTEXTURES]
            3
            0,gra,0,0,GRA_FOOTWHT
            0,gra,1,0,GRA_KHAKI
            0,gra,2,0,GRA_RED
            """));

        Assert.Equal(
            ["GRA_FOOTWHT", "GRA_KHAKI", "GRA_RED", "GRA_FACE"],
            dressed.Meshes.Select(m => m.Submeshes[0].TextureName));

        // The model it was read from is untouched, which is what lets the same file be
        // read once and worn differently by two rooms.
        Assert.Equal("GRA_JEAN", gra.Meshes[1].Submeshes[0].TextureName);
    }

    [Fact]
    public void Dressing_leaves_alone_a_model_the_animation_is_not_about()
    {
        // [WIL] is dressed by Wi2ClothesCamo, which paints wi2. The two models never stand
        // in one room, and painting one man's meshes by another man's indices would be
        // worse than leaving him in what he was modelled in.
        ModFile wil = Model("wil", "WIL_BODY", "WIL_LEGS");

        ModFile dressed = Wardrobe.Dress(wil, "wil", Clothes("""
            [HEADER]
            2

            [MTEXTURES]
            1
            0,wi2,0,0,WI2_CAMO
            """));

        Assert.Same(wil, dressed);
    }

    [Fact]
    public void A_line_past_the_end_of_the_model_is_reported_and_skipped()
    {
        ModFile gra = Model("gra", "GRA_FOOT");
        List<AnimationTexture> skipped = [];

        ModFile dressed = Wardrobe.Dress(gra, "gra", Clothes("""
            [HEADER]
            2

            [MTEXTURES]
            2
            0,gra,0,0,GRA_FOOTWHT
            0,gra,9,0,GRA_KHAKI
            """), skipped.Add);

        Assert.Equal("GRA_FOOTWHT", dressed.Meshes[0].Submeshes[0].TextureName);
        Assert.Equal(9, Assert.Single(skipped).Mesh);
    }

    [Fact]
    public void A_model_drawn_away_from_its_own_origin_is_stood_on_its_feet()
    {
        // Lady Howard's model is 83.6 units from its own origin and the scene stands her by
        // translating it, so she was drawn 84 units from her mark — at Poussin's tomb and at
        // Blanchefort both. The original moves the model by the difference between its
        // origin and its floor position; GKActor::SetModelPositionToActorPosition says so
        // outright: "the 3D model's visual position IS NOT always identical to the 3D
        // model's actor position".
        CharacterConfig lh2 = CharacterLibrary.Parse("""
            [LH2]
            HipAxesMeshIndex=0
            HipAxesGroupIndex=0
            HipAxesPointIndex=0
            LShoeAxesMeshIndex=1
            LShoeAxesGroupIndex=0
            LShoeAxesPointIndex=0
            RShoeAxesMeshIndex=2
            RShoeAxesGroupIndex=0
            RShoeAxesPointIndex=0
            ShoeThickness=0.5
            ShoeType=Female Heels
            """).Of("lh2")!;

        // Hips 84 units out and 30 up, shoes below them, as her model has them.
        ModFile away = Rigged(
            hips: new Vector3(0, 30, -84),
            left: new Vector3(-4, 1, -84),
            right: new Vector3(4, 1.5f, -84));

        Near(new Vector3(0, 0.5f, -84), Footing.Of(away, lh2)!.Value);

        ModFile stood = Footing.OnItsFeet(away, lh2, out Vector3 moved);

        Near(new Vector3(0, -0.5f, 84), moved);
        Near(Vector3.Zero, Footing.Of(stood, lh2)!.Value);
    }

    [Fact]
    public void A_model_already_on_its_own_origin_is_not_touched()
    {
        // Which is most of the cast: Gabriel, Grace, Estelle and Madeline are all within a
        // unit of theirs, which is why nobody noticed for so long.
        CharacterConfig gra = CharacterLibrary.Parse("""
            [GRA]
            HipAxesMeshIndex=0
            HipAxesGroupIndex=0
            HipAxesPointIndex=0
            LShoeAxesMeshIndex=1
            LShoeAxesGroupIndex=0
            LShoeAxesPointIndex=0
            RShoeAxesMeshIndex=2
            RShoeAxesGroupIndex=0
            RShoeAxesPointIndex=0
            ShoeThickness=0
            ShoeType=Female Leather
            """).Of("gra")!;

        ModFile home = Rigged(Vector3.Zero, Vector3.Zero, Vector3.Zero);

        Assert.Same(home, Footing.OnItsFeet(home, gra, out Vector3 moved));
        Assert.Equal(Vector3.Zero, moved);
    }

    [Fact]
    public void A_character_the_file_gives_no_triads_is_left_where_the_scene_puts_them()
    {
        // Two of the game's people have no hip triad. Guessing an offset for them would be
        // worse than the one the scene already states.
        CharacterConfig none = CharacterLibrary.Parse("""
            [ABE]
            ShoeType=Male Sneaker
            """).Of("abe")!;

        ModFile model = Rigged(new Vector3(0, 30, -84), Vector3.Zero, Vector3.Zero);

        Assert.Null(Footing.Of(model, none));
        Assert.Same(model, Footing.OnItsFeet(model, none, out _));
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(
            Vector3.Distance(expected, actual) < 1e-3f,
            $"expected {expected:F3} but was {actual:F3}");

    /// <summary>Three meshes, each one point, standing for the hip and shoe triads.</summary>
    private static ModFile Rigged(Vector3 hips, Vector3 left, Vector3 right) =>
        ModFile.FromMeshes("rig", [.. new[] { hips, left, right }.Select(at => new ModMesh
        {
            // The triad's point is read through its mesh's own transform, so the offset
            // lives there and the vertex is at the mesh's origin — which is how the game's
            // own models carry it.
            MeshToLocal = Matrix4x4.CreateTranslation(at),
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.One,
            Submeshes =
            [
                new ModSubmesh
                {
                    TextureName = "SKIN",
                    Color = (255, 255, 255),
                    Positions = [Vector3.Zero],
                    Normals = [Vector3.UnitY],
                    TexCoords = [Vector2.Zero],
                    Indices = [0, 0, 0],
                },
            ],
        })]);

    /// <summary>A model of one submesh a mesh, each with the texture named.</summary>
    private static ModFile Model(string name, params string[] textures) =>
        ModFile.FromMeshes(name, [.. textures.Select(texture => new ModMesh
        {
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.One,
            Submeshes =
            [
                new ModSubmesh
                {
                    TextureName = texture,
                    Color = (255, 255, 255),
                    Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                    TexCoords = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
                    Indices = [0, 1, 2],
                },
            ],
        })]);

    private static AnimationFile Clothes(string text) =>
        AnimationFile.Parse(text, "clothes.ANM", new DiagnosticBag());
}

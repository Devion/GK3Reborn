using GK3Reborn.Rendering.Materials;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the texture slots an enhanced material carries.
/// </summary>
/// <remarks>
/// A patch has to be able to say three different things about a map — leave it alone, use
/// this one, go back to having none — and null alone can only say two of them.
/// </remarks>
public sealed class NormalMapMaterialTests
{
    private static MaterialDefinition Material(string? normal = null, string? orm = null) =>
        new()
        {
            Id = "test",
            BaseColorTexture = "WALL",
            Roughness = 0.5f,
            Metallic = 0f,
            NormalTexture = normal,
            OrmTexture = orm,
            Provenance = GK3Reborn.Content.Authoring.AuthoringProvenance.Authored,
            Confidence = 1f,
        };

    [Fact]
    public void A_material_starts_with_no_maps()
    {
        // 324 of the game's 6,657 textures have a normal map so far. The rest look exactly
        // as they did, which is how a partial set stays a perfectly good set.
        MaterialDefinition material = Material();

        Assert.Null(material.NormalTexture);
        Assert.Null(material.OrmTexture);
    }

    [Fact]
    public void A_patch_that_says_nothing_leaves_the_maps_alone()
    {
        MaterialDefinition material = Material("WALL_N", "WALL_ORM").ApplyPatch(new MaterialPatch
        {
            Roughness = 0.2f,
        });

        Assert.Equal("WALL_N", material.NormalTexture);
        Assert.Equal("WALL_ORM", material.OrmTexture);
    }

    [Fact]
    public void A_patch_can_name_a_different_map()
    {
        MaterialDefinition material = Material("WALL_N").ApplyPatch(new MaterialPatch
        {
            NormalTexture = "WALL_N_V2",
        });

        Assert.Equal("WALL_N_V2", material.NormalTexture);
    }

    [Fact]
    public void An_empty_name_takes_the_map_away()
    {
        // Which a null cannot say: null means "this patch has no opinion". Without the
        // distinction there is no way to reject a generated map through the edit layer.
        MaterialDefinition material = Material("WALL_N", "WALL_ORM").ApplyPatch(new MaterialPatch
        {
            NormalTexture = string.Empty,
        });

        Assert.Null(material.NormalTexture);
        Assert.Equal("WALL_ORM", material.OrmTexture);
    }

    [Fact]
    public void Patching_a_map_leaves_everything_else_where_it_was()
    {
        MaterialDefinition material = Material("WALL_N").ApplyPatch(new MaterialPatch
        {
            OrmTexture = "WALL_ORM",
        });

        Assert.Equal("WALL_N", material.NormalTexture);
        Assert.Equal("WALL_ORM", material.OrmTexture);
        Assert.Equal("WALL", material.BaseColorTexture);
        Assert.Equal(0.5f, material.Roughness);
    }
}

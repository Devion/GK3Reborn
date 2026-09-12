using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for how far a room's outdoor ground may depart from the picture painted on it.
/// </summary>
public sealed class GroundVariationTests
{
    /// <summary>
    /// The floor map as the game writes it, cut down to the names these tests use.
    /// </summary>
    private static string? Ground(string? texture) => texture?.ToLowerInvariant() switch
    {
        "grass02" or "solidgrass" or "3qtr_grass" or "rc1motgras" => "Grass",
        "1qtr_grass" or "soliddirt" or "armdirt" or "rl1_path2" => "Dirt",
        "full_road" or "ploasph" or "gridrivewycncrt" or "concrete" => "Concrete",
        "cdbwhtrck" or "rock_blend" or "mcfstriatrockl" or "roqstone" => "Concrete",
        "becpblyrk_01" => "Concrete",
        "rc1coblston" or "kittile" => "Tile",
        "chuwdflr" => "Wood",
        "carpet1" => "Carpet",
        "checkertrans" => "Grass",
        _ => null,
    };

    [Theory]
    [InlineData("Grass02")]
    [InlineData("SolidGrass")]
    [InlineData("rc1motgras")]
    public void GrassVariesMost(string texture) =>
        Assert.Equal(1f, GroundVariation.Of(texture, Ground));

    [Theory]
    [InlineData("1QTR_Grass")]
    [InlineData("SolidDirt")]
    [InlineData("ArmDirt")]
    public void BareEarthVariesNearlyAsMuch(string texture)
    {
        float varies = GroundVariation.Of(texture, Ground);

        Assert.InRange(varies, 0.8f, 0.95f);
    }

    /// <summary>
    /// The one distinction the floor map cannot make for us: its <c>Concrete</c> bucket
    /// holds every cliff in the game beside the roads and the driveways.
    /// </summary>
    [Theory]
    [InlineData("cdbwhtrck")]
    [InlineData("Rock_Blend")]
    [InlineData("mcfstriatrockL")]
    [InlineData("roqstone")]
    [InlineData("BECPBLYRK_01")]
    public void RockFiledAsConcreteWeathers(string texture)
    {
        float rock = GroundVariation.Of(texture, Ground);
        float road = GroundVariation.Of("Full_Road", Ground);

        Assert.True(
            rock > road,
            $"{texture} is a rock face and should vary more than a road: {rock} vs {road}.");
    }

    [Theory]
    [InlineData("Full_Road")]
    [InlineData("Ploasph")]
    [InlineData("GRIdrivewycncrt")]
    public void MadeGroundVariesLeast(string texture)
    {
        float varies = GroundVariation.Of(texture, Ground);

        Assert.InRange(varies, 0.3f, 0.6f);
    }

    /// <summary>
    /// A room with a sky through its window is not a room out of doors, and its floor is
    /// still whatever the artists laid.
    /// </summary>
    [Theory]
    [InlineData("rc1Coblston")]
    [InlineData("kittile")]
    [InlineData("chuwdflr")]
    [InlineData("carpet1")]
    public void LaidFloorsAreLeftAlone(string texture) =>
        Assert.Equal(GroundVariation.None, GroundVariation.Of(texture, Ground));

    /// <summary>
    /// The same data quirk <c>Grass.Cover</c> has to step over: the floor map files a
    /// transparent keying checker under grass.
    /// </summary>
    [Fact]
    public void TheTransparentCheckerIsNotGround() =>
        Assert.Equal(GroundVariation.None, GroundVariation.Of("checkertrans", Ground));

    [Fact]
    public void GroundTheFloorMapDoesNotKnowGetsTheMiddle()
    {
        float varies = GroundVariation.Of("nobodysground", Ground);

        Assert.InRange(varies, 0.3f, 0.7f);
    }

    [Fact]
    public void AnExtensionIsNotPartOfTheName() =>
        Assert.Equal(
            GroundVariation.Of("Grass02", Ground),
            GroundVariation.Of("Grass02.BMP", Ground));

    [Fact]
    public void NothingIsNotGround() =>
        Assert.Equal(GroundVariation.None, GroundVariation.Of(string.Empty, Ground));
}

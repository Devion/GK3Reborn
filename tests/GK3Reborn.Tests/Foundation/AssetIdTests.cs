using GK3Reborn.Foundation;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

public sealed class AssetIdTests
{
    [Theory]
    [InlineData("day3-3.bik", "DAY3-3")]
    [InlineData("DAY3-3.BIK", "DAY3-3")]
    [InlineData("Day3-3", "DAY3-3")]
    [InlineData("  day3-3.bik  ", "DAY3-3")]
    [InlineData("Data/Movies/day3-3.bik", "DAY3-3")]
    [InlineData(@"Data\Movies\day3-3.bik", "DAY3-3")]
    public void From_normalizes_case_path_and_extension(string input, string expected) =>
        Assert.Equal(expected, AssetId.From(input).Value);

    [Fact]
    public void From_treats_differently_spelled_names_as_equal()
    {
        // GK3 data references the same asset inconsistently; that must not create
        // two distinct entries in the content store.
        Assert.Equal(AssetId.From("DAY3-3.BIK"), AssetId.From("day3-3"));
        Assert.Equal(AssetId.From("DAY3-3.BIK").GetHashCode(), AssetId.From("day3-3").GetHashCode());
    }

    [Fact]
    public void From_keeps_original_spelling_for_diagnostics() =>
        Assert.Equal("day3-3.bik", AssetId.From("day3-3.bik").Original);

    [Fact]
    public void FromExact_keeps_the_extension_as_part_of_identity()
    {
        Assert.Equal("GABRIEL.MOD", AssetId.FromExact("gabriel.mod").Value);
        Assert.NotEqual(AssetId.FromExact("gabriel.mod"), AssetId.FromExact("gabriel.act"));
    }

    [Fact]
    public void Leading_dot_is_not_treated_as_an_extension() =>
        Assert.Equal(".HIDDEN", AssetId.From(".hidden").Value);

    [Fact]
    public void Default_instance_is_empty()
    {
        AssetId id = default;
        Assert.True(id.IsEmpty);
        Assert.Equal(string.Empty, id.Value);
    }
}

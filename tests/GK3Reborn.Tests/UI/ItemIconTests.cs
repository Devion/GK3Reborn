using System.Numerics;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for fitting an item's picture into the square a list leaves for it.
/// </summary>
/// <remarks>
/// The pictures are not all one shape — a passport is wider than it is tall, a dagger the
/// other way about — so the square is what the layout reserves and the picture is what
/// goes in the middle of it. Filling the square instead is how a list ends up showing a
/// squashed picture of the thing the player is trying to recognise.
/// </remarks>
public sealed class ItemIconTests
{
    [Fact]
    public void A_square_picture_fills_the_square()
    {
        Vector4 at = new ItemIcon(1, 94, 94).Fit(100, 200, 40);

        Assert.Equal(new Vector4(100, 200, 40, 40), at);
    }

    [Fact]
    public void A_wide_picture_keeps_its_shape_and_sits_in_the_middle()
    {
        Vector4 at = new ItemIcon(1, 100, 50).Fit(0, 0, 40);

        Assert.Equal(40, at.Z);
        Assert.Equal(20, at.W);
        Assert.Equal(0, at.X);
        Assert.Equal(10, at.Y);
    }

    [Fact]
    public void A_tall_picture_keeps_its_shape_too()
    {
        Vector4 at = new ItemIcon(1, 50, 100).Fit(0, 0, 40);

        Assert.Equal(20, at.Z);
        Assert.Equal(40, at.W);
        Assert.Equal(10, at.X);
        Assert.Equal(0, at.Y);
    }

    [Fact]
    public void An_item_with_no_picture_has_nothing_to_draw()
    {
        Assert.False(default(ItemIcon).Drawn);
        Assert.False(new ItemIcon(0, 94, 94).Drawn);
        Assert.False(new ItemIcon(3, 0, 0).Drawn);
        Assert.Equal(default, new ItemIcon(0, 94, 94).Fit(0, 0, 40));
        Assert.Equal(default, new ItemIcon(3, 94, 94).Fit(0, 0, 0));
    }
}

using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for reading what a script builds into a room for itself.
/// </summary>
/// <remarks>
/// <c>AddModel("model=discoball_pole,type=prop")</c> is GK3's construction mode. Six
/// scripts in the game use it and every one of them is an easter egg — the disco ball over
/// the bar, the monkey in the fridge, the propeller on Mosely's hat — so a specification
/// read wrongly is a moment that quietly does not happen.
/// </remarks>
public sealed class ConstructionModeTests
{
    [Theory]
    [InlineData("model=discoball_pole,type=prop", "discoball_pole")]
    [InlineData("model=rl2_discospecks,type=prop", "rl2_discospecks")]
    [InlineData("model=empaphat, type=prop", "empaphat")]
    [InlineData("model=blades,\t\ttype=prop", "blades")]
    [InlineData("model=SpinProp,\ttype=prop", "SpinProp")]
    [InlineData("MODEL=rednose, TYPE=PROP", "rednose")]
    public void A_prop_specification_names_the_model_whatever_spacing_it_was_written_with(
        string specification, string expected)
    {
        // The corpus writes these six ways between them, and the tabs are in the files.
        Assert.Equal(expected, SceneLoader.ConstructedProp(specification));
    }

    [Theory]
    [InlineData("model=GOT,noun=GOAT,pos=EggParadeCorner1")]
    [InlineData("model=cat,noun=CAT,pos=EggParadeCorner1")]
    public void An_actor_specification_is_not_a_prop(string specification)
    {
        // RC3's parade of animals is AddActor, which wants a character and a place to put
        // them rather than a model. Staging one as a prop would put a goat in the square
        // with nothing to walk it anywhere.
        Assert.Null(SceneLoader.ConstructedProp(specification));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rl2_disco_a")]
    [InlineData("type=prop")]
    [InlineData("model=,type=prop")]
    [InlineData("checker_01")]
    public void Anything_that_is_not_a_specification_names_nothing(string text)
    {
        // Every string constant in the room's scripts goes past this — camera names,
        // textures, lines of dialogue — so saying no to the rest is most of its job.
        Assert.Null(SceneLoader.ConstructedProp(text));
    }
}

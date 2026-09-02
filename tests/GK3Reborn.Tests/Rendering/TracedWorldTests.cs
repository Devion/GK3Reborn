using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// How the traced world is divided, and what a ray is allowed to see of it.
/// </summary>
/// <remarks>
/// Two numbers and two rules, and every one of them is a decision that a wrong answer would
/// not crash: a mask nobody set reads as a character standing in their own shadow, and a
/// facing rule nobody set reads as half the fences in the game not being there. Both
/// backends read these, so they are asserted once here rather than twice in a device test
/// that most machines skip.
/// </remarks>
public sealed class TracedWorldTests
{
    [Fact]
    public void The_room_and_the_models_are_told_apart()
    {
        Assert.Equal(TracedWorld.WorldMask, TracedWorld.MaskFor(0));
        Assert.Equal(TracedWorld.ModelMask, TracedWorld.MaskFor(1));
        Assert.Equal(TracedWorld.ModelMask, TracedWorld.MaskFor(41));

        // A shadow ray leaving a character traces the room and nothing else, because GK3's
        // people are a dozen overlapping shells and a ray leaving the shirt starts inside
        // the torso. The two masks have to be different bits for that to be sayable at all.
        Assert.NotEqual(TracedWorld.WorldMask, TracedWorld.ModelMask);
    }

    [Fact]
    public void The_room_s_keyed_cards_are_a_third_thing()
    {
        // Room geometry that must not be traced as the room. The composite credits the
        // room's own occlusion against the 1999 bake and the two cancel exactly, which is
        // right for a wall — the artists' lightmap holds its shadow already — and wrong for
        // a railing, because a 1999 bake cast no alpha-tested rays either and a keyed card
        // is in the lightmap as its whole quad or as nothing. Given the room's mask a fence
        // is traced perfectly and darkens nothing.
        Assert.Equal(TracedWorld.UnbakedMask, TracedWorld.MaskFor(TracedWorld.CardPart));
        Assert.NotEqual(TracedWorld.WorldMask, TracedWorld.MaskFor(TracedWorld.CardPart));
        Assert.NotEqual(TracedWorld.ModelMask, TracedWorld.MaskFor(TracedWorld.CardPart));

        // And out of the way of the placement numbering, which Move and SetTraced are
        // called with: part zero is the room and one upwards are the models placed in it.
        Assert.True(TracedWorld.CardPart < 0);
    }

    [Fact]
    public void Only_a_model_keeps_its_winding_and_only_a_model_may_be_posed()
    {
        // A BSP carries no consistent winding — each triangle is given its own plane's
        // normal at load, which is the admission that the file does not say — and a card
        // occluder is a single-sided patch fitted to whichever way the artist happened to
        // wind the quad. The ray that most needs to hit one is a shadow ray leaving a
        // character, which asks for back faces to be culled; an instance that disables the
        // test overrides that flag, so the shells stay skipped and the fence still stops
        // the light.
        Assert.True(TracedWorld.FacesBothWays(0));
        Assert.True(TracedWorld.FacesBothWays(TracedWorld.CardPart));
        Assert.False(TracedWorld.FacesBothWays(1));

        // Posing rewrites vertices, and only a model is ever posed. The room is built once
        // and so are its railings: the one that swings is a door, and a door is a model.
        Assert.False(TracedWorld.Posable(0));
        Assert.False(TracedWorld.Posable(TracedWorld.CardPart));
        Assert.True(TracedWorld.Posable(1));
    }
}

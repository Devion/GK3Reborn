using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for finding the close-up a scene declares for a thing.
/// </summary>
public sealed class InspectCameraTests
{
    /// <summary>
    /// WDB's lines for Wilkes' body, verbatim: a noun on five models, one of which is a
    /// loaded character model and four of which are surfaces of the room.
    /// </summary>
    private const string Wdb = """
        [MODELS]
        model=Wi3, noun=WILKES, type=prop
        model=wdbwilkes_throat, noun=WILKES_THROAT_CU, type=scene
        model=wdb_wilkes_body, noun=WILKES, type=scene
        model=deadwilkes_head, noun=WILKES, type=scene

        [INSPECT_CAMERAS]
        model=wdb_wilkes_body, angle={-145.15,54.37}, pos={622.71,88.7,2037.24}
        noun=WILKES_THROAT_CU, angle={-91.71,58.87}, pos={644.86,69.1,2008.35}
        model=deadwilkes_head, angle={-112.52,39.74}, pos={648.07,61.59,2009.54}
        """;

    private static SceneDefinition Scene() =>
        new(SceneInitFile.Parse(Wdb, "WDB.SIF"));

    [Fact]
    public void A_close_up_is_found_through_any_model_the_noun_is_declared_on()
    {
        // The noun's only loaded model is Wi3, which carries no close-up and which the
        // scene never positions — it is a character model, so it stands in its bind pose
        // at the world origin. Asking only about Wi3 found nothing and the view fell back
        // to framing it, so inspecting Wilkes' body showed him upright in the void with
        // no ground under him. Reported as such.
        SceneCamera? found = Scene().InspectCameraFor("WILKES", "Wi3");

        Assert.NotNull(found);
        Assert.Equal(622.71f, found!.Position.X, 2);
        Assert.Equal(2037.24f, found.Position.Z, 2);
    }

    [Fact]
    public void A_close_up_keyed_on_the_noun_still_wins()
    {
        // The order is unchanged: the noun's own entry first, then the model's.
        SceneCamera? found = Scene().InspectCameraFor("WILKES_THROAT_CU", "wdbwilkes_throat");

        Assert.NotNull(found);
        Assert.Equal(644.86f, found!.Position.X, 2);
    }

    [Fact]
    public void A_noun_with_no_close_up_anywhere_still_answers_nothing()
    {
        Assert.Null(Scene().InspectCameraFor("BLOOD_POOLS", "wdb_blood01"));
    }
}

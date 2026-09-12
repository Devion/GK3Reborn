using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the body the player stands in: which of its parts are arms, what their own
/// eyes are kept from seeing, and how far a clip carries their head.
/// </summary>
public sealed class FirstPersonBodyTests
{
    /// <summary>Where each of the synthetic character's mesh groups is built.</summary>
    private static readonly (string Texture, Vector3 At)[] Body =
    [
        ("GAB_BOOT", new Vector3(-3.4f, 1.4f, -2.6f)),
        ("GAB_BOOT", new Vector3(3.3f, 1.2f, -2.7f)),
        ("GABRIGHTARM", new Vector3(-8.5f, 53.5f, 0.2f)),
        ("GABLEFTARM", new Vector3(8.3f, 53.6f, 0.8f)),
        ("GAB_SHIRT", new Vector3(0f, 55.5f, -1f)),
        ("GABEJEAN", new Vector3(0f, 33.8f, -0.9f)),
        ("GABEJEAN", new Vector3(-3.3f, 12.4f, -1.1f)),
        ("GABEJEAN", new Vector3(3.2f, 12f, 0.3f)),
        ("GABRIGHTARM", new Vector3(-8.8f, 45.7f, -1.3f)),
        ("GABLEFTARM", new Vector3(8.4f, 45.7f, 0.5f)),
        ("GAB_FACE", new Vector3(-0.4f, 69.3f, -1.4f)),
        ("GABPALM", new Vector3(-8.7f, 37.7f, -4.4f)),
        ("GABPALM", new Vector3(8.2f, 37.2f, -1.2f)),
    ];

    /// <summary>Which of those are arms, which is what the rule has to come back with.</summary>
    private static readonly int[] Arms = [2, 3, 8, 9, 11, 12];

    /// <summary>And which two of those are the shoulders, which their owner never sees.</summary>
    private static readonly int[] Shoulders = [2, 3];

    /// <summary>So this is what is left in their own view: the forearms and the hands.</summary>
    private static readonly int[] Seen = [8, 9, 11, 12];

    /// <summary>Which is the head.</summary>
    private const int Head = 10;

    /// <summary>How near two points have to be to count as the same point, in scene units.</summary>
    private const float Near = 0.01f;

    /// <summary>
    /// Gabriel's own layout, group for group: two boots, two upper arms, a shirt, hips and
    /// two thighs, two forearms, a head, and two hands. The textures say nothing useful —
    /// every one of Grace's arm groups is painted GRA_SKIN, and so is her neck — so what
    /// tells an arm apart is where it is built.
    /// </summary>
    [Fact]
    public void TheArmsAreTheGroupsOutToTheSideAndAboveTheHips()
    {
        Assert.Equal(Arms, CharacterArms.Find(Character()));
    }

    /// <summary>
    /// The top group down each arm is the shoulder, and the eye of the person wearing it is
    /// six units above and eight to the side of it — near enough to be inside it.
    /// </summary>
    [Fact]
    public void TheShouldersAreTheTopGroupDownEachArm()
    {
        Assert.Equal(Shoulders, CharacterArms.Shoulders(Character()).Order());
    }

    /// <summary>An arm of one group is all shoulder, and taking it leaves nothing to draw.</summary>
    [Fact]
    public void AnArmWithNothingBelowTheShoulderKeepsIt()
    {
        ModFile stumps = ModFile.FromMeshes(
            "stumps",
            [
                Group("GAB_FACE", Body[Head].At),
                Group("SKIN", new Vector3(-8.5f, 53.5f, 0f)),
                Group("SKIN", new Vector3(8.5f, 53.5f, 0f)),
            ]);

        Assert.Empty(CharacterArms.Shoulders(stumps));
    }

    /// <summary>A pair of arms is two of them; one is a rule that has misread the model.</summary>
    [Fact]
    public void ArmsDownOneSideOnlyAreNotArms()
    {
        ModFile lopsided = ModFile.FromMeshes(
            "one", [.. Body.Select(part => Group(part.Texture, part.At with { X = MathF.Abs(part.At.X) }))]);

        Assert.Empty(CharacterArms.Find(lopsided));
    }

    /// <summary>Something with nothing that reads as a head has nothing to measure from.</summary>
    [Fact]
    public void SomethingWithNoHeadHasNoArms()
    {
        ModFile crate = ModFile.FromMeshes("crate", [Group("WOOD", Vector3.Zero), Group("WOOD", new Vector3(40f, 40f, 0f))]);

        Assert.Empty(CharacterArms.Find(crate));
    }

    /// <summary>
    /// Standing in a body leaves the arms below the shoulder in view and takes the rest of
    /// it out of the player's own eyes only. Nothing is hidden outright, because the body still casts its
    /// shadow across the floor and still stands in the mirror.
    /// </summary>
    [Fact]
    public void StandingInABodyLeavesNothingOfItButTheArms()
    {
        (SceneUpdate update, HeadlessSceneSink sink, PlacedModel gabriel) = Room();

        update.Embody(gabriel, standingIn: true, keepArms: true);

        Assert.Equal(
            Enumerable.Range(0, Body.Length).Where(mesh => !Seen.Contains(mesh)), sink.UnseenBySelf[gabriel.Placement.Id]);

        // Not a thing they can click, and not a thing taken out of the room.
        Assert.False(gabriel.Visible);
        Assert.Equal(0, sink.HiddenCount);
        Assert.Equal(0, sink.HiddenPartCount);
    }

    /// <summary>With the row turned off, all of the body goes, and no further than their own view.</summary>
    [Fact]
    public void WithoutArmsTheWholeBodyGoesOutOfTheirOwnViewAndNoFurther()
    {
        (SceneUpdate update, HeadlessSceneSink sink, PlacedModel gabriel) = Room();

        update.Embody(gabriel, standingIn: true, keepArms: false);

        Assert.Equal(Enumerable.Range(0, Body.Length), sink.UnseenBySelf[gabriel.Placement.Id]);
        Assert.Equal(0, sink.HiddenCount);
    }

    /// <summary>And stepping out of it puts the whole of it back.</summary>
    [Fact]
    public void LeavingTheBodyPutsAllOfItBack()
    {
        (SceneUpdate update, HeadlessSceneSink sink, PlacedModel gabriel) = Room();

        update.Embody(gabriel, standingIn: true, keepArms: true);
        update.Embody(gabriel, standingIn: false, keepArms: true);

        Assert.Empty(sink.UnseenBySelf[gabriel.Placement.Id]);
        Assert.True(gabriel.Visible);
    }

    /// <summary>A body nothing has posed has its head exactly where it was built.</summary>
    [Fact]
    public void AStandingBodyHasNotMovedItsHead()
    {
        (SceneUpdate update, HeadlessSceneSink _, PlacedModel _) = Room();

        Assert.True(update.HeadShift("GABRIEL") is { } shift && shift.Length() < Near);
    }

    /// <summary>Kneeling is the head going down while the feet stay where they are.</summary>
    [Fact]
    public void AKneelIsTheShiftTheEyeFollows()
    {
        (SceneUpdate update, HeadlessSceneSink _, PlacedModel gabriel) = Room();

        gabriel.Pose(Head, Matrix4x4.CreateTranslation(Body[Head].At - new Vector3(0f, 30f, 0f)));

        Assert.True(update.HeadShift("GABRIEL") is { } shift && Vector3.Distance(shift, new Vector3(0f, -30f, 0f)) < Near);
    }

    /// <summary>
    /// And walking the length of the room carries their head along with it for nothing. The
    /// same cancellation covers a clip with its own root motion, where the feet the story
    /// reports move with the pose rather than with the placement: an eye that followed both
    /// would travel twice as far as the body it is in.
    /// </summary>
    [Fact]
    public void WalkingCarriesTheHeadAndShiftsItNotAtAll()
    {
        (SceneUpdate update, HeadlessSceneSink _, PlacedModel _) = Room();

        update.Step("GABRIEL", new Vector3(120f, 0f, -45f), 0f);

        Assert.True(update.HeadShift("GABRIEL") is { } shift && shift.Length() < Near);
    }

    /// <summary>One mesh group, built where it is given.</summary>
    private static ModMesh Group(string texture, Vector3 at) => new()
    {
        MeshToLocal = Matrix4x4.CreateTranslation(at),
        BoundsMin = new Vector3(-4f),
        BoundsMax = new Vector3(4f),
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
    };

    /// <summary>A character built the way Gabriel is.</summary>
    private static ModFile Character() => ModFile.FromMeshes("gab", [.. Body.Select(part => Group(part.Texture, part.At))]);

    /// <summary>A room with nobody in it but the player.</summary>
    private static (SceneUpdate Update, HeadlessSceneSink Sink, PlacedModel Gabriel) Room()
    {
        var sink = new HeadlessSceneSink();
        ModFile model = Character();
        ModelPlacement placement = sink.Add(model);

        var gabriel = new PlacedModel("gab", "GABRIEL", null, model, Matrix4x4.Identity, PlacedModelKind.Actor, placement);

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Placed: [gabriel]);

        return (new SceneUpdate(scene, new Gk3SheepApi(new GameState()), new Glances(), sink), sink, gabriel);
    }
}

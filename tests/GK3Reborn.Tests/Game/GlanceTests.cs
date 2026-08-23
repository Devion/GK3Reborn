using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game.Actors;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for an actor turning their head.
/// </summary>
/// <remarks>
/// GK3's people have no skeleton: a character is a dozen separate meshes with their own
/// transforms, so turning a head is placing one mesh differently, about its own origin.
/// Finding which mesh that is has to come from what it is painted with, because the format
/// has nowhere to write a name.
/// </remarks>
public sealed class GlanceTests
{
    private static ModMesh Mesh(float height, params string[] textures) =>
        new()
        {
            MeshToLocal = Matrix4x4.CreateTranslation(0, height, 0),
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.One,
            Submeshes =
            [
                .. textures.Select(t => new ModSubmesh
                {
                    TextureName = t,
                    Color = (255, 255, 255),
                    Positions = [Vector3.Zero],
                    Normals = [Vector3.UnitY],
                    TexCoords = [Vector2.Zero],
                    Indices = [0, 0, 0],
                }),
            ],
        };

    [Fact]
    public void The_head_is_the_mesh_wearing_a_face()
    {
        ModFile gabriel = ModFile.FromMeshes(
            "GAB",
            [
                Mesh(10, "GAB_SHOE"),
                Mesh(40, "GAB_PANT"),
                Mesh(60, "GAB_SHIRT"),
                Mesh(70, "GAB_FACE", "GAB_EYELIDS", "GABE_HAIR"),
            ]);

        Assert.Equal(3, CharacterHead.Find(gabriel));

        // Its own origin is where the neck is, because that is where the artist put it.
        Assert.Equal(70f, CharacterHead.PivotOf(gabriel, 3).Y);
    }

    [Fact]
    public void Hair_alone_is_weaker_evidence_than_a_mouth()
    {
        // A hat on a shelf wears hair; only a head wears a mouth.
        ModFile model = ModFile.FromMeshes(
            "SOMEBODY",
            [Mesh(80, "WIG_HAIR"), Mesh(60, "X_MOUTH00", "X_FOREHEAD")]);

        Assert.Equal(1, CharacterHead.Find(model));
    }

    [Fact]
    public void A_prop_has_no_head_and_is_left_alone()
    {
        // Turning some arbitrary part of a chair towards the player would be a stranger
        // bug than a chair that does not move.
        ModFile chair = ModFile.FromMeshes("CHAIR", [Mesh(0, "BASICWOOD"), Mesh(20, "BROWNPLASTIC")]);

        Assert.Null(CharacterHead.Find(chair));
    }

    [Fact]
    public void An_actor_already_facing_something_turns_their_head_not_at_all()
    {
        // Relative to the body, because the head is a child of it.
        (float yaw, float pitch) = Glances.Turn(
            Vector3.Zero, facing: 0f, eyes: 60f, target: new Vector3(0, 60, 100));

        Assert.Equal(0f, yaw, 3);
        Assert.Equal(0f, pitch, 3);
    }

    [Fact]
    public void Something_to_the_side_turns_the_head_towards_it()
    {
        // Yaw is measured the way the scene files measure a heading: zero along +Z,
        // increasing towards +X.
        (float yaw, _) = Glances.Turn(
            Vector3.Zero, facing: 0f, eyes: 60f, target: new Vector3(100, 60, 100));

        Assert.Equal(45f, float.RadiansToDegrees(yaw), 1);
    }

    [Fact]
    public void Something_overhead_tips_the_head_back()
    {
        // Fifty units above the eyes at a hundred away, which is inside what a neck
        // manages; a fan directly overhead would be clamped, and there is a test for that.
        (_, float pitch) = Glances.Turn(
            Vector3.Zero, facing: 0f, eyes: 60f, target: new Vector3(0, 110, 100));

        Assert.True(pitch > 0, "a ceiling fan is looked up at");
        Assert.Equal(26.6f, float.RadiansToDegrees(pitch), 1);
    }

    [Fact]
    public void A_neck_only_goes_so_far()
    {
        // Something behind the actor is looked at as far as possible rather than turning
        // the head all the way round.
        (float yaw, float pitch) = Glances.Turn(
            Vector3.Zero, facing: 0f, eyes: 60f, target: new Vector3(0, -900, -100));

        Assert.Equal(Glances.YawLimit, MathF.Abs(yaw), 3);
        Assert.Equal(-Glances.PitchLimit, pitch, 3);
    }

    [Fact]
    public void The_turn_is_measured_the_short_way_round()
    {
        // An actor facing very nearly north, looking at something a little to their west.
        // Without wrapping this reads as almost a full turn the other way and the clamp
        // then holds the head at its limit facing the wrong way.
        (float yaw, _) = Glances.Turn(
            Vector3.Zero,
            facing: float.DegreesToRadians(170),
            eyes: 60f,
            target: new Vector3(0, 60, -100));

        Assert.Equal(10f, float.RadiansToDegrees(yaw), 1);
    }

    [Fact]
    public void Somewhere_directly_overhead_gives_nothing_to_turn_towards()
    {
        (float yaw, float pitch) = Glances.Turn(
            Vector3.Zero, facing: 1f, eyes: 60f, target: new Vector3(0, 200, 0));

        Assert.Equal(0f, yaw);
        Assert.Equal(0f, pitch);
    }

    [Fact]
    public void An_actor_looks_at_one_thing_at_a_time()
    {
        var glances = new Glances();

        glances.Look(new Glance("gab", "MOSELY", new Vector3(1, 0, 0), Quick: false));
        glances.Look(new Glance("gab", "WINDOW", new Vector3(0, 0, 1), Quick: true));

        Glance only = Assert.Single(glances.All);

        Assert.Equal("WINDOW", only.Target);
        Assert.True(only.Quick);

        Assert.True(glances.Cancel("GAB"));
        Assert.Empty(glances.All);
        Assert.False(glances.Cancel("gab"));
    }
}

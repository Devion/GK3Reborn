using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for choosing which of a room's cameras watches a conversation.
/// </summary>
public sealed class ConversationCameraTests
{
    /// <summary>A camera at a place, looking at a point.</summary>
    private static SceneCamera Looking(string name, Vector3 from, Vector3 at)
    {
        Vector3 line = at - from;

        return new SceneCamera(
            name,
            from,
            MathF.Atan2(line.X, line.Z),
            -MathF.Atan2(line.Y, MathF.Sqrt((line.X * line.X) + (line.Z * line.Z))),
            IsDefault: false);
    }

    /// <summary>Two people a little apart, both facing each other along x.</summary>
    private static readonly Vector3 Gabriel = new(0, 0, 0);
    private static readonly Vector3 Emilio = new(60, 0, 0);

    /// <summary>Gabriel looks towards +x, Emilio towards −x.</summary>
    private static readonly Vector3[] Facings = [new(1, 0, 0), new(-1, 0, 0)];

    [Fact]
    public void A_shot_from_behind_a_speaker_is_refused()
    {
        // Behind Gabriel, looking along the line the two of them make. It frames both of
        // them perfectly and shows the back of one head, which is what a player reads as
        // the character having turned round.
        SceneCamera behind = Looking("BEHIND", new Vector3(-200, 60, 0), new Vector3(60, 60, 0));

        Assert.Null(
            ConversationCamera.Framing([behind], [Gabriel, Emilio], Facings));
    }

    [Fact]
    public void A_shot_from_the_side_sees_both_faces()
    {
        // Off to one side, so both of them are in three-quarter view. This is most of the
        // game's authored conversation shots.
        SceneCamera across = Looking("ACROSS", new Vector3(30, 60, -220), new Vector3(30, 60, 0));

        Assert.Equal(
            "ACROSS",
            ConversationCamera.Framing([across], [Gabriel, Emilio], Facings),
            ignoreCase: true);
    }

    [Fact]
    public void The_shot_that_sees_faces_beats_the_one_that_does_not()
    {
        SceneCamera behind = Looking("BEHIND", new Vector3(-200, 60, 0), new Vector3(60, 60, 0));
        SceneCamera across = Looking("ACROSS", new Vector3(30, 60, -220), new Vector3(30, 60, 0));

        Assert.Equal(
            "ACROSS",
            ConversationCamera.Framing([behind, across], [Gabriel, Emilio], Facings),
            ignoreCase: true);
    }

    /// <summary>Without facings it judges on framing alone, as it always did.</summary>
    [Fact]
    public void A_speaker_whose_facing_is_unknown_rules_nothing_out()
    {
        SceneCamera behind = Looking("BEHIND", new Vector3(-200, 60, 0), new Vector3(60, 60, 0));

        Assert.Equal(
            "BEHIND",
            ConversationCamera.Framing([behind], [Gabriel, Emilio]),
            ignoreCase: true);

        Assert.Equal(
            "BEHIND",
            ConversationCamera.Framing([behind], [Gabriel, Emilio], [Vector3.Zero, Vector3.Zero]),
            ignoreCase: true);
    }

    /// <summary>A camera that cannot see everybody is no camera at all.</summary>
    [Fact]
    public void A_camera_that_leaves_somebody_out_is_not_used()
    {
        // Right up against Gabriel, facing him, with Emilio far outside the frame.
        SceneCamera tight = Looking("TIGHT", new Vector3(-40, 60, 0), new Vector3(0, 60, 0));

        Assert.Null(ConversationCamera.Framing([tight], [Gabriel, Emilio], Facings));
    }

    /// <summary>A camera to take the lens and the planes from, as a room's own would.</summary>
    private static GK3Reborn.Rendering.Camera Template() =>
        new() { Position = new Vector3(0, 60, -300), Target = new Vector3(30, 60, 0) };

    [Fact]
    public void A_composed_shot_holds_both_speakers()
    {
        GK3Reborn.Rendering.Camera? shot = ConversationCamera.Composed(
            [Gabriel, Emilio], new Vector3(0, 60, -300), Template());

        Assert.NotNull(shot);

        // Square on to the line between them, so it stands off along z and looks back at
        // the middle of the pair.
        Assert.Equal(30f, shot!.Target.X, 1f);
        Assert.Equal(0f, shot.Target.Z, 1f);
        Assert.Equal(30f, shot.Position.X, 1f);

        // And on the side the view was already on, rather than across the line from it.
        Assert.True(shot.Position.Z < 0f, $"expected to stay on the near side, got {shot.Position.Z}");

        // Both of them in front of it, and neither of them behind the other.
        foreach (Vector3 who in (Vector3[])[Gabriel, Emilio])
        {
            Vector3 toward = (who with { Y = 60f }) - shot.Position;

            Assert.True(
                Vector3.Dot(Vector3.Normalize(shot.Target - shot.Position), Vector3.Normalize(toward)) > 0.7f,
                $"{who} is not in frame");
        }
    }

    [Fact]
    public void A_composed_shot_takes_the_lens_from_the_room()
    {
        GK3Reborn.Rendering.Camera template = Template();

        GK3Reborn.Rendering.Camera? shot = ConversationCamera.Composed(
            [Gabriel, Emilio], template.Position, template);

        Assert.NotNull(shot);
        Assert.Equal(template.FieldOfView, shot!.FieldOfView, 4);
        Assert.Equal(template.NearPlane, shot.NearPlane, 4);
        Assert.Equal(template.FarPlane, shot.FarPlane, 4);
    }

    [Fact]
    public void A_wall_on_one_side_puts_the_shot_on_the_other()
    {
        // Everything on the near side of the pair is outside the room, so the only place
        // left to stand is across the line from where the view is now.
        GK3Reborn.Rendering.Camera? shot = ConversationCamera.Composed(
            [Gabriel, Emilio],
            new Vector3(0, 60, -300),
            Template(),
            at => at.Z > 0f);

        Assert.NotNull(shot);
        Assert.True(shot!.Position.Z > 0f, $"expected the far side, got {shot.Position.Z}");
    }

    [Fact]
    public void A_room_with_nowhere_to_stand_composes_nothing()
    {
        Assert.Null(ConversationCamera.Composed(
            [Gabriel, Emilio], Vector3.Zero, Template(), _ => false));
    }

    [Fact]
    public void One_speaker_is_not_a_two_shot()
    {
        Assert.Null(ConversationCamera.Composed([Gabriel], Vector3.Zero, Template()));

        // And two people the story has left standing in the same place are one subject.
        Assert.Null(ConversationCamera.Composed(
            [Gabriel, Gabriel], Vector3.Zero, Template()));
    }

    [Fact]
    public void Whether_a_named_camera_holds_the_pair_can_be_asked_outright()
    {
        SceneCamera square = Looking("SQUARE", new Vector3(30, 60, -200), new Vector3(30, 60, 0));
        SceneCamera behind = Looking("BEHIND", new Vector3(-200, 60, 0), new Vector3(60, 60, 0));

        Assert.True(ConversationCamera.Frames(square, [Gabriel, Emilio], Facings));
        Assert.False(ConversationCamera.Frames(behind, [Gabriel, Emilio], Facings));
    }
}

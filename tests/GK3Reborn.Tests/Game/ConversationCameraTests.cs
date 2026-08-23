using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for choosing which of a room's cameras watches a conversation.
/// </summary>
/// <remarks>
/// The original has no answer here — <c>SetDefaultDialogueCamera</c> is a no-op in it, and
/// the lobby's introduction to Emilio names no conversation at all — so the port decides, out
/// of the artists' own cameras. What it must not do is decide badly: reported as Gabriel
/// turning round during dialogue, which was the view cutting to a shot from behind him.
/// </remarks>
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
    /// <remarks>
    /// Not every caller can say which way somebody is looking, and an unknown facing must
    /// cost a shot nothing rather than rule it out.
    /// </remarks>
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
    /// <remarks>
    /// A bad cut is worse than no cut: leaving the view where it was is always available and
    /// is never wrong, where a shot of one person during a conversation between two is.
    /// </remarks>
    [Fact]
    public void A_camera_that_leaves_somebody_out_is_not_used()
    {
        // Right up against Gabriel, facing him, with Emilio far outside the frame.
        SceneCamera tight = Looking("TIGHT", new Vector3(-40, 60, 0), new Vector3(0, 60, 0));

        Assert.Null(ConversationCamera.Framing([tight], [Gabriel, Emilio], Facings));
    }
}

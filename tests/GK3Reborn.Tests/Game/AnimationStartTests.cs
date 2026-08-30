using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Tests.Formats;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for where an <c>approach=anim</c> sends the actor.
/// </summary>
/// <remarks>
/// The approach names an animation rather than a place, so the place has to be read out of
/// the animation's opening frame: the hip axis triad is where the actor stands, and the
/// mesh's own basis says which way they face. Getting either wrong is not a crash — the
/// actor walks somewhere plausible and starts pouring coffee into the air — so it is worth
/// pinning the arithmetic down rather than judging it by eye.
/// </remarks>
public sealed class AnimationStartTests
{
    private const int RightShoe = 0;
    private const int LeftShoe = 1;
    private const int Hips = 2;

    /// <summary>Gabriel, near enough: three triads and the point that matters.</summary>
    private static CharacterConfig Character() => new(
        "GAB", 76f, null, null, null,
        Hips: new CharacterAxes(Hips, 0, 1),
        LeftShoe: new CharacterAxes(LeftShoe, 0, 0),
        RightShoe: new CharacterAxes(RightShoe, 0, 0));

    /// <summary>
    /// One frame posing the three triads, with a chosen hip basis.
    /// </summary>
    /// <remarks>
    /// The shoes are set a little apart across the actor's X and the hips above and between
    /// them, which is the arrangement the facing test reads: the triangle they make has a
    /// normal, and flattened onto the floor that normal is the way the body is pointing.
    /// </remarks>
    private static ClipBuilder Clip(Matrix4x4 hips, Vector3 point) =>
        new ClipBuilder(3, "gab")
            .Frame(
                (RightShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(2, 0, 0))),
                (LeftShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(-2, 0, 0))),
                (Hips, ClipBuilder.Transform(hips)),
                (Hips, ClipBuilder.Shape(0, new Vector3(9, 9, 9), point)));

    private static AnimationFile Animation(string clip) => AnimationFile.Parse(
        $"""
        [HEADER]
        30

        [ACTIONS]
        1
        0,{clip},0,0,0,0,0,0,0,0
        """,
        "TEST.ANM",
        new DiagnosticBag());

    private static ClipLibrary Library(string name, ClipBuilder clip)
    {
        byte[] built = clip.Build();

        return new ClipLibrary(asked =>
            string.Equals(asked, name + ".ACT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(asked, name, StringComparison.OrdinalIgnoreCase)
                ? built
                : null)
        {
            // The triad's point is a vertex, so the shapes have to survive being read.
            KeepVertices = true,
        };
    }

    private static (Vector3 Position, float Heading)? Start(Matrix4x4 hips, Vector3 point) =>
        AnimationStart.Of(
            Animation("gab_pour"), Library("gab_pour", Clip(hips, point)), "gab", Character());

    [Fact]
    public void The_actor_stands_where_the_hip_triad_opens()
    {
        // A quarter turn and a long way from the origin, so a missing rotation, a missing
        // translation and a swapped order all give different answers.
        Matrix4x4 hips = Matrix4x4.CreateRotationY(MathF.PI / 2f) *
                         Matrix4x4.CreateTranslation(100, 60, 200);

        var point = new Vector3(0, 3, 10);

        (Vector3 Position, float Heading)? start = Start(hips, point);

        Assert.NotNull(start);

        // The point is in the mesh's space, so it is carried by the whole basis rather
        // than added to its translation.
        Vector3 expected = Vector3.Transform(point, hips);

        Assert.True(
            Vector3.Distance(expected, start.Value.Position) < 0.01f,
            $"expected {expected}, got {start.Value.Position}");
    }

    [Fact]
    public void The_triad_is_read_rather_than_the_meshs_origin()
    {
        Matrix4x4 hips = Matrix4x4.CreateTranslation(100, 60, 200);

        (Vector3 Position, float Heading)? start = Start(hips, new Vector3(0, 0, 40));

        Assert.NotNull(start);

        // Forty units along the mesh's z, which is what tells this apart from an
        // implementation that settles for where the mesh itself sits.
        Assert.True(
            Vector3.Distance(new Vector3(100, 60, 240), start.Value.Position) < 0.01f,
            $"got {start.Value.Position}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(-135f)]
    public void They_arrive_facing_the_way_the_clip_faces(float degrees)
    {
        float turn = degrees * MathF.PI / 180f;

        Matrix4x4 hips = Matrix4x4.CreateRotationY(turn) *
                         Matrix4x4.CreateTranslation(10, 0, 20);

        (Vector3 Position, float Heading)? start = Start(hips, Vector3.Zero);

        Assert.NotNull(start);

        // A model faces −Z, so the heading of a basis is its rotation plus a half turn.
        // Walker.HeadingOf is the single place that half turn lives, and a heading that
        // does not agree with it is one that will disagree with every walk in the game.
        float expected = Walker.HeadingOf(hips);
        float apart = MathF.Abs(Wrap(expected - start.Value.Heading));

        Assert.True(apart < 0.01f, $"expected {expected}, got {start.Value.Heading}");
    }

    [Fact]
    public void A_pose_stands_on_its_soles_rather_than_on_its_hips()
    {
        // What the actor's logical position follows while a clip plays. The hips say where
        // in the room; taking the height from them as well put Gabriel thirty-four units
        // into the air after every walk in the game, and that height is what the next
        // walk's first floor query is asked about — see WalkFloor.Choose.
        Matrix4x4 hips = Matrix4x4.CreateTranslation(100, 60, 200);

        ClipBuilder clip = new ClipBuilder(3, "gab")
            .Frame(
                (RightShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(2, 5, 0))),
                (RightShoe, ClipBuilder.Shape(0, Vector3.Zero)),
                (LeftShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(-2, 1, 0))),
                (LeftShoe, ClipBuilder.Shape(0, Vector3.Zero)),
                (Hips, ClipBuilder.Transform(hips)),
                (Hips, ClipBuilder.Shape(0, new Vector3(9, 9, 9), Vector3.Zero)));

        Vector3? standing = AnimationStart.Standing(
            Library("gab_pose", clip).Read("gab_pose")!,
            0f,
            false,
            Character() with { ShoeThickness = 0.75f },
            Matrix4x4.Identity);

        Assert.NotNull(standing);

        // Where the hips are, horizontally.
        Assert.Equal(100f, standing.Value.X, 2);
        Assert.Equal(200f, standing.Value.Z, 2);

        // The lower shoe is the one taking the weight — mid-stride the other is in the air
        // — less the sole between the triad and the ground.
        Assert.Equal(0.25f, standing.Value.Y, 2);
    }

    [Fact]
    public void The_facing_is_the_stance_rather_than_the_hip_meshs_own_axis()
    {
        // The hips are turned a quarter circle away from the feet, which happens whenever
        // somebody is authored looking over their shoulder. The reference reads the facing
        // off the triangle the hips and shoes make — GKActor::GetModelFacingDirection — and
        // the mesh's own rotation is not that triangle. Reading the rotation instead came
        // out a half turn away for Gabriel, and everything measured against it was aimed at
        // his back: the head glances, the approach=anim arrivals, and the heading a
        // finished clip leaves the actor at.
        Matrix4x4 hips = Matrix4x4.CreateRotationY(MathF.PI / 2f) *
                         Matrix4x4.CreateTranslation(0, 60, 0);

        (Vector3 Position, float Heading)? start = Start(hips, Vector3.Zero);

        Assert.NotNull(start);

        // Right shoe at +x, left at −x, hips above: cross(right − left, hip − left)
        // flattened points along +z, which is a heading of zero — whatever the hip mesh
        // itself has been turned to.
        float apart = MathF.Abs(Wrap(start.Value.Heading));

        Assert.True(apart < 0.01f, $"expected 0, got {start.Value.Heading}");

        // And the mesh's own reading is a quarter turn from that, which is what makes this
        // fixture able to tell the two apart at all.
        Assert.True(
            MathF.Abs(Wrap(Walker.HeadingOf(hips))) > 1f,
            "the fixture no longer distinguishes the stance from the mesh");
    }

    [Fact]
    public void A_facing_part_way_through_a_clip_reads_the_feet_on_that_frame()
    {
        // Both feet swap sides between the two frames, which is the whole of a turn: the
        // stance faces +z on the first and −z on the second. Reading the feet on frame zero
        // under the hips of whatever frame was asked about is a triangle that never existed,
        // and it is worst on exactly the clips whose purpose is a turn — the museum's
        // Lh2MusEstTurn2Gab left Lady Howard and Estelle standing 165 and 99 degrees from
        // the man they had just turned to face.
        ClipBuilder clip = new ClipBuilder(3, "gab")
            .Frame(
                (RightShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(2, 0, 0))),
                (LeftShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(-2, 0, 0))),
                (Hips, ClipBuilder.Transform(Matrix4x4.CreateTranslation(0, 60, 0))))
            .Frame(
                (RightShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(-2, 0, 0))),
                (LeftShoe, ClipBuilder.Transform(Matrix4x4.CreateTranslation(2, 0, 0))),
                (Hips, ClipBuilder.Transform(Matrix4x4.CreateTranslation(0, 60, 0))));

        ActFile read = Library("gab_turn", clip).Read("gab_turn")!;

        float? opening = AnimationStart.FacingAt(
            read, 0f, false, Character(), Matrix4x4.Identity, null);

        float? closing = AnimationStart.FacingAt(
            read, 1f, false, Character(), Matrix4x4.Identity, null);

        Assert.NotNull(opening);
        Assert.NotNull(closing);

        Assert.True(MathF.Abs(Wrap(opening.Value)) < 0.01f, $"opened at {opening}");
        Assert.True(MathF.Abs(Wrap(MathF.PI - closing.Value)) < 0.01f, $"closed at {closing}");
    }

    [Fact]
    public void A_pose_that_records_no_shoes_falls_back_to_the_hips()
    {
        // A rigid clip records no vertices, so there is no sole to read. Answering with the
        // hips' height is wrong by a torso and it is still an answer; answering with
        // nothing loses the position altogether.
        Matrix4x4 hips = Matrix4x4.CreateTranslation(100, 60, 200);

        Vector3? standing = AnimationStart.Standing(
            Library("gab_pour", Clip(hips, Vector3.Zero)).Read("gab_pour")!,
            0f,
            false,
            Character(),
            Matrix4x4.Identity);

        Assert.NotNull(standing);
        Assert.Equal(60f, standing.Value.Y, 2);
    }

    [Fact]
    public void An_animation_that_poses_nobody_by_that_name_has_no_start()
    {
        Matrix4x4 hips = Matrix4x4.CreateTranslation(100, 60, 200);

        // The clip is filed under gab and the actor asking is Grace, which is the ordinary
        // case for a scenery animation: there is nobody to walk, and saying so is what
        // lets the action run anyway rather than being lost with the approach.
        Assert.Null(AnimationStart.Of(
            Animation("gab_pour"),
            Library("gab_pour", Clip(hips, Vector3.Zero)),
            "gra",
            Character()));
    }

    [Fact]
    public void A_character_with_no_triads_has_no_start()
    {
        Matrix4x4 hips = Matrix4x4.CreateTranslation(100, 60, 200);

        Assert.Null(AnimationStart.Of(
            Animation("gab_pour"),
            Library("gab_pour", Clip(hips, Vector3.Zero)),
            "gab",
            new CharacterConfig("GAB", 76f, null, null, null)));
    }

    private static float Wrap(float radians)
    {
        while (radians > MathF.PI)
        {
            radians -= 2f * MathF.PI;
        }

        while (radians < -MathF.PI)
        {
            radians += 2f * MathF.PI;
        }

        return radians;
    }
}

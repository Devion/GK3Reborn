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

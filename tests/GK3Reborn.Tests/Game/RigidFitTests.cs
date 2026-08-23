using System.Numerics;
using GK3Reborn.Game.Actors;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for recovering a head's motion from the vertices a clip moves.
/// </summary>
/// <remarks>
/// The whole of the head refinement rests on this: a clip says where every vertex of a head
/// is, and the refinement needs the one transform that says the same thing. What matters is
/// that an exact rigid motion comes back exactly, that noise is reported rather than
/// absorbed, and that input which does not determine a rotation is refused instead of
/// guessed at — a guess here shears or mirrors somebody's head.
/// </remarks>
public sealed class RigidFitTests
{
    /// <summary>A lumpy cloud, deterministic so a failure can be looked at twice.</summary>
    private static Vector3[] Cloud(int count = 64)
    {
        var random = new Random(20260822);
        var points = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            points[i] = new Vector3(
                (float)((random.NextDouble() * 20.0) - 10.0),
                (float)((random.NextDouble() * 20.0) - 10.0),
                (float)((random.NextDouble() * 20.0) - 10.0));
        }

        return points;
    }

    private static Vector3[] Moved(Vector3[] points, Matrix4x4 by) =>
        [.. points.Select(p => Vector3.Transform(p, by))];

    [Fact]
    public void RecoversARotationAndATranslation()
    {
        Vector3[] rest = Cloud();
        Matrix4x4 applied =
            Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.4f, 0.25f) *
            Matrix4x4.CreateTranslation(3f, -12f, 40f);

        Matrix4x4? fit = RigidFit.Solve(rest, Moved(rest, applied), out float residual);

        Assert.NotNull(fit);
        Assert.True(residual < 1e-3f, $"residual was {residual}");

        foreach (Vector3 point in rest)
        {
            Assert.True(
                Vector3.Distance(
                    Vector3.Transform(point, fit!.Value),
                    Vector3.Transform(point, applied)) < 1e-2f);
        }
    }

    [Fact]
    public void AHeadThatDidNotMoveComesBackAsNoMotion()
    {
        Vector3[] rest = Cloud();

        Matrix4x4? fit = RigidFit.Solve(rest, rest, out float residual);

        Assert.NotNull(fit);
        Assert.True(residual < 1e-4f);

        foreach (Vector3 point in rest)
        {
            Assert.True(Vector3.Distance(Vector3.Transform(point, fit!.Value), point) < 1e-3f);
        }
    }

    /// <summary>
    /// The residual is the number the corpus survey gates on, so it has to mean something.
    /// </summary>
    [Fact]
    public void ReportsWhatItCouldNotFit()
    {
        Vector3[] rest = Cloud();
        Vector3[] deformed = [.. rest];

        // One vertex pulled well off the rigid answer: a head that really did deform.
        deformed[7] += new Vector3(0f, 8f, 0f);

        Matrix4x4? fit = RigidFit.Solve(rest, deformed, out float residual);

        Assert.NotNull(fit);
        Assert.True(residual > 0.5f, $"residual was {residual}");
    }

    [Fact]
    public void RefusesFewerThanThreePoints()
    {
        Vector3[] two = [new(0f, 0f, 0f), new(1f, 0f, 0f)];

        Assert.Null(RigidFit.Solve(two, two, out _));
    }

    [Fact]
    public void RefusesMismatchedCounts()
    {
        Vector3[] rest = Cloud();

        Assert.Null(RigidFit.Solve(rest, rest.AsSpan(0, 10), out _));
    }

    /// <summary>Points in a line fix no rotation about that line.</summary>
    [Fact]
    public void RefusesPointsThatDoNotDetermineARotation()
    {
        Vector3[] line = [.. Enumerable.Range(0, 12).Select(i => new Vector3(i, 0f, 0f))];

        Assert.Null(RigidFit.Solve(line, line, out _));
    }

    /// <summary>
    /// A mirror is refused rather than fitted. GK3's world is left-handed and a fit that
    /// quietly returned a reflection would turn a character inside out on one frame.
    /// </summary>
    [Fact]
    public void RefusesAMirror()
    {
        Vector3[] rest = Cloud();
        Vector3[] mirrored = [.. rest.Select(p => new Vector3(-p.X, p.Y, p.Z))];

        Assert.Null(RigidFit.Solve(rest, mirrored, out _));
    }

    /// <summary>
    /// The fit has to survive a head that is nearly, but not quite, symmetrical about the
    /// axis it is turning on — which every head is.
    /// </summary>
    [Fact]
    public void SurvivesALargeTurn()
    {
        Vector3[] rest = Cloud();

        foreach (float angle in new[] { 0.1f, 1f, 2f, 3f, MathF.PI - 0.01f })
        {
            Matrix4x4 applied = Matrix4x4.CreateRotationY(angle);
            Matrix4x4? fit = RigidFit.Solve(rest, Moved(rest, applied), out float residual);

            Assert.NotNull(fit);
            Assert.True(residual < 1e-2f, $"{angle} rad gave residual {residual}");
        }
    }
}

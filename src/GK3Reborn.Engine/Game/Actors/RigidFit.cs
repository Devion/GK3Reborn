using System.Numerics;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// The rotation and translation that best carries one set of points onto another.
/// </summary>
/// <remarks>
/// <para>
/// GK3 animates by moving vertices, so a clip says where every point of a head is on every
/// frame and never says that the head turned. This puts that back: given the head as the
/// model authored it and the head as a clip shapes it, it recovers the single rigid motion
/// between them — which is all the clip was ever expressing, because a head does not
/// deform. Measured over the whole corpus — 3,069 clips, 122,034 recorded frames, all 56
/// models that have any — the leftover is 1.0% of head width for the median model and 4.1%
/// for the worst. See <c>docs/head-refinement.md</c>.
/// </para>
/// <para>
/// That is what lets a character's head be replaced with denser geometry without touching
/// the <c>.ACT</c> files: the clip keeps addressing the original 1,200-odd vertices, the
/// fit turns what it says into a transform, and the transform will carry any mesh at all.
/// </para>
/// <para>
/// The method is Kabsch's, with the orthogonal factor found by Higham's Newton iteration
/// rather than by a singular value decomposition, which <c>System.Numerics</c> does not
/// have and which would be a great deal of code for a 3×3.
/// </para>
/// </remarks>
public static class RigidFit
{
    /// <summary>How many Newton steps before giving up on convergence.</summary>
    /// <remarks>
    /// The iteration is quadratically convergent, so this is a backstop rather than a
    /// budget: well-conditioned input is done in five or six.
    /// </remarks>
    private const int Steps = 24;

    /// <summary>Close enough that another step would not move it.</summary>
    private const float Settled = 1e-6f;

    /// <summary>Finds the rigid motion carrying <paramref name="from"/> onto <paramref name="to"/>.</summary>
    /// <param name="from">Where the points are.</param>
    /// <param name="to">Where they should end up. Must be the same length.</param>
    /// <param name="residual">
    /// Root-mean-square distance left over per point, in the space the points are given in.
    /// Zero means the two sets really are one rigid motion apart.
    /// </param>
    /// <returns>The transform, or null when the points do not determine one.</returns>
    /// <remarks>
    /// Null rather than an identity, because the two answers mean opposite things: an
    /// identity says the head did not move, and a caller that cannot tell them apart draws
    /// a head that snaps back to rest on every frame the fit fails.
    /// </remarks>
    public static Matrix4x4? Solve(
        ReadOnlySpan<Vector3> from, ReadOnlySpan<Vector3> to, out float residual)
    {
        residual = 0f;

        // Three points are the fewest that fix a rotation, and even three only do it when
        // they are not in a line — which the covariance below reports as a singular matrix.
        if (from.Length != to.Length || from.Length < 3)
        {
            return null;
        }

        Vector3 middleFrom = Middle(from);
        Vector3 middleTo = Middle(to);

        // The covariance of the two centred clouds. Rows are the 'from' axes and columns
        // the 'to' axes, which is the order System.Numerics multiplies a row vector in.
        var covariance = new Matrix4x4();
        covariance.M44 = 1f;

        for (int i = 0; i < from.Length; i++)
        {
            Vector3 a = from[i] - middleFrom;
            Vector3 b = to[i] - middleTo;

            covariance.M11 += a.X * b.X;
            covariance.M12 += a.X * b.Y;
            covariance.M13 += a.X * b.Z;
            covariance.M21 += a.Y * b.X;
            covariance.M22 += a.Y * b.Y;
            covariance.M23 += a.Y * b.Z;
            covariance.M31 += a.Z * b.X;
            covariance.M32 += a.Z * b.Y;
            covariance.M33 += a.Z * b.Z;
        }

        if (Orthogonalise(covariance) is not { } rotation)
        {
            return null;
        }

        float total = 0f;

        for (int i = 0; i < from.Length; i++)
        {
            total += (Vector3.TransformNormal(from[i] - middleFrom, rotation) -
                      (to[i] - middleTo)).LengthSquared();
        }

        residual = MathF.Sqrt(total / from.Length);

        return Matrix4x4.CreateTranslation(-middleFrom) *
               rotation *
               Matrix4x4.CreateTranslation(middleTo);
    }

    /// <summary>The average of a set of points.</summary>
    private static Vector3 Middle(ReadOnlySpan<Vector3> points)
    {
        Vector3 total = Vector3.Zero;

        foreach (Vector3 point in points)
        {
            total += point;
        }

        return total / points.Length;
    }

    /// <summary>The nearest rotation to a matrix.</summary>
    /// <param name="matrix">The covariance, with the 3×3 part in the upper left.</param>
    /// <returns>The rotation, or null when there is not one to find.</returns>
    /// <remarks>
    /// <para>
    /// Higham's iteration, <c>R ← ½(R + R⁻ᵀ)</c>, which converges on the orthogonal factor
    /// of the polar decomposition — the same matrix an SVD would give as <c>UVᵀ</c>.
    /// </para>
    /// <para>
    /// <b>A negative determinant is refused rather than corrected.</b> The nearest
    /// orthogonal matrix to a covariance with a negative determinant is a reflection, and
    /// the usual correction — flip the smallest singular value — needs the decomposition
    /// this is avoiding. It also should not arise: a head and the same head a frame later
    /// are a rotation apart, not a mirror. If it does, the points are degenerate and no
    /// answer is better than a mirrored head. GK3's world is left-handed, which is a
    /// property of the space these points are expressed in and not of the motion between
    /// two poses within it; the determinant here is of the motion.
    /// </para>
    /// </remarks>
    private static Matrix4x4? Orthogonalise(Matrix4x4 matrix)
    {
        if (Determinant(matrix) <= 0f)
        {
            return null;
        }

        Matrix4x4 current = matrix;

        for (int step = 0; step < Steps; step++)
        {
            if (!Matrix4x4.Invert(current, out Matrix4x4 inverse))
            {
                return null;
            }

            Matrix4x4 next = Half(current, Matrix4x4.Transpose(inverse));
            float moved = Distance(current, next);
            current = next;

            if (moved < Settled)
            {
                break;
            }
        }

        // Convergence is assumed nowhere: a matrix that came out of the loop still skewed
        // would shear the head rather than turn it, which is worse than not refining it.
        return Orthonormal(current) ? current : null;
    }

    /// <summary>The determinant of the 3×3 part.</summary>
    private static float Determinant(Matrix4x4 m) =>
        (m.M11 * ((m.M22 * m.M33) - (m.M23 * m.M32))) -
        (m.M12 * ((m.M21 * m.M33) - (m.M23 * m.M31))) +
        (m.M13 * ((m.M21 * m.M32) - (m.M22 * m.M31)));

    /// <summary>Halfway between two matrices, in their 3×3 parts.</summary>
    private static Matrix4x4 Half(Matrix4x4 a, Matrix4x4 b)
    {
        var result = new Matrix4x4 { M44 = 1f };

        result.M11 = (a.M11 + b.M11) * 0.5f;
        result.M12 = (a.M12 + b.M12) * 0.5f;
        result.M13 = (a.M13 + b.M13) * 0.5f;
        result.M21 = (a.M21 + b.M21) * 0.5f;
        result.M22 = (a.M22 + b.M22) * 0.5f;
        result.M23 = (a.M23 + b.M23) * 0.5f;
        result.M31 = (a.M31 + b.M31) * 0.5f;
        result.M32 = (a.M32 + b.M32) * 0.5f;
        result.M33 = (a.M33 + b.M33) * 0.5f;

        return result;
    }

    /// <summary>The largest element-wise difference between two 3×3 parts.</summary>
    private static float Distance(Matrix4x4 a, Matrix4x4 b) =>
        MathF.Max(
            MathF.Max(
                MathF.Max(MathF.Abs(a.M11 - b.M11), MathF.Abs(a.M12 - b.M12)),
                MathF.Max(MathF.Abs(a.M13 - b.M13), MathF.Abs(a.M21 - b.M21))),
            MathF.Max(
                MathF.Max(MathF.Abs(a.M22 - b.M22), MathF.Abs(a.M23 - b.M23)),
                MathF.Max(
                    MathF.Max(MathF.Abs(a.M31 - b.M31), MathF.Abs(a.M32 - b.M32)),
                    MathF.Abs(a.M33 - b.M33))));

    /// <summary>Whether a matrix's 3×3 part is a rotation, to a loose tolerance.</summary>
    private static bool Orthonormal(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);

        const float slack = 1e-3f;

        return MathF.Abs(x.LengthSquared() - 1f) < slack &&
               MathF.Abs(y.LengthSquared() - 1f) < slack &&
               MathF.Abs(z.LengthSquared() - 1f) < slack &&
               MathF.Abs(Vector3.Dot(x, y)) < slack &&
               MathF.Abs(Vector3.Dot(x, z)) < slack &&
               MathF.Abs(Vector3.Dot(y, z)) < slack;
    }
}

using System.Numerics;

namespace GK3Reborn.Rendering.Lighting;

/// <summary>One lit surface, reduced to what light estimation needs.</summary>
/// <param name="Centroid">World-space centre of the surface.</param>
/// <param name="Normal">Area-weighted surface normal.</param>
/// <param name="Area">Surface area, used to weight its contribution.</param>
/// <param name="Brightness">Mean baked luminance, 0 to 1.</param>
/// <param name="Color">Mean baked colour.</param>
public readonly record struct LitSurface(
    Vector3 Centroid,
    Vector3 Normal,
    float Area,
    float Brightness,
    Vector3 Color);

/// <summary>An estimated directional light, with the confidence of the fit.</summary>
/// <param name="Direction">Direction the light travels, pointing away from the source.</param>
/// <param name="Intensity">Fitted intensity.</param>
/// <param name="Color">Mean colour of the light.</param>
/// <param name="Confidence">How well the fit explains the samples, 0 to 1.</param>
public readonly record struct DirectionalEstimate(
    Vector3 Direction,
    float Intensity,
    Vector3 Color,
    float Confidence);

/// <summary>An estimated point light.</summary>
/// <param name="Position">World-space position.</param>
/// <param name="Intensity">Fitted intensity.</param>
/// <param name="Color">Mean colour of the light.</param>
/// <param name="Radius">Approximate influence radius.</param>
/// <param name="Confidence">How well supported the estimate is, 0 to 1.</param>
public readonly record struct PointEstimate(
    Vector3 Position,
    float Intensity,
    Vector3 Color,
    float Radius,
    float Confidence);

/// <summary>
/// Recovers light sources from baked lighting.
/// </summary>
/// <remarks>
/// <para>
/// The useful observation is that a distant light is a *linear* problem. Lambertian
/// brightness from a directional source is <c>I = dot(n, d)</c>, so given many surfaces
/// with known normals and measured brightness, the direction and intensity fall out of a
/// three-by-three least-squares solve. No iteration, no initial guess, no local minima.
/// </para>
/// <para>
/// Point lights are not linear, because brightness depends on distance as well as angle.
/// They are estimated more coarsely, by clustering bright surfaces and placing a source
/// in front of each cluster — good enough to seed a rig a human then corrects, which is
/// all ADR 0002 asks of it.
/// </para>
/// <para>
/// Nothing here is trusted on its own. Every estimate carries a confidence derived from
/// how well it explains the samples, and low-confidence output is review queue rather
/// than content.
/// </para>
/// </remarks>
public static class LightEstimator
{
    /// <summary>
    /// Fits a single directional light to a set of lit surfaces.
    /// </summary>
    /// <param name="surfaces">Surfaces with their measured brightness.</param>
    /// <returns>The estimate, or null when the samples cannot constrain one.</returns>
    /// <remarks>
    /// Minimising the squared error between <c>dot(n, d)</c> and measured brightness gives
    /// the normal equations <c>(sum n nᵀ) d = sum I n</c>. Surfaces are weighted by area,
    /// so a large wall counts for more than a doorknob.
    /// </remarks>
    public static DirectionalEstimate? FitDirectional(IReadOnlyList<LitSurface> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);

        if (surfaces.Count < 4)
        {
            return null;
        }

        Span<float> a = stackalloc float[9];
        Vector3 b = Vector3.Zero;
        Vector3 color = Vector3.Zero;
        float totalWeight = 0;

        foreach (LitSurface s in surfaces)
        {
            float w = MathF.Max(s.Area, 1e-3f);
            Vector3 n = s.Normal;

            a[0] += w * n.X * n.X; a[1] += w * n.X * n.Y; a[2] += w * n.X * n.Z;
            a[3] += w * n.Y * n.X; a[4] += w * n.Y * n.Y; a[5] += w * n.Y * n.Z;
            a[6] += w * n.Z * n.X; a[7] += w * n.Z * n.Y; a[8] += w * n.Z * n.Z;

            b += w * s.Brightness * n;
            color += w * s.Brightness * s.Color;
            totalWeight += w;
        }

        if (!TrySolve3x3(a, b, out Vector3 d) || d.LengthSquared() < 1e-8f)
        {
            return null;
        }

        float intensity = d.Length();
        Vector3 direction = -Vector3.Normalize(d);

        // Confidence is one minus the normalised residual: how much of the measured
        // brightness the fitted direction actually explains.
        float residual = 0;
        float total = 0;
        foreach (LitSurface s in surfaces)
        {
            float predicted = MathF.Max(0, Vector3.Dot(s.Normal, -direction) * intensity);
            residual += (predicted - s.Brightness) * (predicted - s.Brightness);
            total += s.Brightness * s.Brightness;
        }

        float confidence = total > 1e-6f
            ? Math.Clamp(1 - MathF.Sqrt(residual / total), 0, 1)
            : 0;

        Vector3 meanColor = totalWeight > 0 ? color / totalWeight : Vector3.One;
        if (meanColor.Length() > 1e-6f)
        {
            // Normalise so colour is a hue rather than a second copy of intensity.
            meanColor /= MathF.Max(MathF.Max(meanColor.X, meanColor.Y), meanColor.Z);
        }

        return new DirectionalEstimate(direction, intensity, meanColor, confidence);
    }

    /// <summary>
    /// Proposes point lights by clustering the brightest surfaces.
    /// </summary>
    /// <param name="surfaces">Surfaces with their measured brightness.</param>
    /// <param name="clusterRadius">How close two surfaces must be to share a light.</param>
    /// <param name="maxLights">Cap on how many lights to propose.</param>
    /// <returns>The estimates, brightest first.</returns>
    /// <remarks>
    /// A cluster of bright surfaces implies a source somewhere in front of them, so the
    /// light is placed off the cluster's centroid along its average normal at a distance
    /// scaled by the cluster's own extent. This is a seed, not a solution: it gets the
    /// light into roughly the right place so a human can move it rather than create it.
    /// </remarks>
    public static IReadOnlyList<PointEstimate> FitPointLights(
        IReadOnlyList<LitSurface> surfaces, float clusterRadius, int maxLights = 8)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterRadius);

        List<LitSurface> bright = [.. surfaces
            .Where(s => s.Brightness > 0.12f)
            .OrderByDescending(s => s.Brightness)];

        List<PointEstimate> lights = [];
        bool[] taken = new bool[bright.Count];

        for (int i = 0; i < bright.Count && lights.Count < maxLights; i++)
        {
            if (taken[i])
            {
                continue;
            }

            List<LitSurface> cluster = [bright[i]];
            taken[i] = true;

            for (int j = i + 1; j < bright.Count; j++)
            {
                if (!taken[j] && Vector3.Distance(bright[i].Centroid, bright[j].Centroid) < clusterRadius)
                {
                    cluster.Add(bright[j]);
                    taken[j] = true;
                }
            }

            // A single surface is not evidence of a light; it could be anything.
            if (cluster.Count < 3)
            {
                continue;
            }

            Vector3 centroid = Vector3.Zero;
            Vector3 normal = Vector3.Zero;
            Vector3 color = Vector3.Zero;
            float brightness = 0;

            foreach (LitSurface s in cluster)
            {
                centroid += s.Centroid;
                normal += s.Normal * s.Brightness;
                color += s.Color;
                brightness += s.Brightness;
            }

            centroid /= cluster.Count;
            color /= cluster.Count;
            brightness /= cluster.Count;

            float extent = cluster.Max(s => Vector3.Distance(s.Centroid, centroid));
            float offset = MathF.Max(extent * 0.5f, clusterRadius * 0.25f);

            Vector3 direction = normal.LengthSquared() > 1e-6f
                ? Vector3.Normalize(normal)
                : Vector3.UnitY;

            lights.Add(new PointEstimate(
                centroid + (direction * offset),
                brightness,
                color,
                Radius: MathF.Max(extent * 2, clusterRadius),

                // More supporting surfaces means a better constrained position.
                Confidence: Math.Clamp(cluster.Count / 20f, 0.05f, 0.9f)));
        }

        return lights;
    }

    /// <summary>Solves a symmetric 3x3 system by Gaussian elimination with partial pivoting.</summary>
    private static bool TrySolve3x3(Span<float> a, Vector3 rhs, out Vector3 result)
    {
        Span<float> m = stackalloc float[12];
        for (int r = 0; r < 3; r++)
        {
            m[(r * 4) + 0] = a[(r * 3) + 0];
            m[(r * 4) + 1] = a[(r * 3) + 1];
            m[(r * 4) + 2] = a[(r * 3) + 2];
            m[(r * 4) + 3] = r == 0 ? rhs.X : r == 1 ? rhs.Y : rhs.Z;
        }

        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++)
            {
                if (MathF.Abs(m[(r * 4) + col]) > MathF.Abs(m[(pivot * 4) + col]))
                {
                    pivot = r;
                }
            }

            if (MathF.Abs(m[(pivot * 4) + col]) < 1e-9f)
            {
                // The surfaces do not span enough orientations to pin a direction down —
                // every normal facing the same way, for instance.
                result = default;
                return false;
            }

            if (pivot != col)
            {
                for (int k = 0; k < 4; k++)
                {
                    (m[(col * 4) + k], m[(pivot * 4) + k]) = (m[(pivot * 4) + k], m[(col * 4) + k]);
                }
            }

            for (int r = 0; r < 3; r++)
            {
                if (r == col)
                {
                    continue;
                }

                float factor = m[(r * 4) + col] / m[(col * 4) + col];
                for (int k = col; k < 4; k++)
                {
                    m[(r * 4) + k] -= factor * m[(col * 4) + k];
                }
            }
        }

        result = new Vector3(m[3] / m[0], m[7] / m[5], m[11] / m[10]);
        return true;
    }
}

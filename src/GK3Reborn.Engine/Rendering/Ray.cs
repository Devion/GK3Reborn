using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// A half-line through the world.
/// </summary>
/// <remarks>
/// What a click is, once it has left the screen. The direction is expected to be unit
/// length, so the distance a cast reports is in scene units and hits from different
/// sources can be compared against each other.
/// </remarks>
/// <param name="Origin">Where it starts.</param>
/// <param name="Direction">Which way it goes, normalised.</param>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    /// <summary>The point a given distance along the ray.</summary>
    /// <param name="distance">How far along, in scene units.</param>
    /// <returns>The point.</returns>
    public Vector3 At(float distance) => Origin + (Direction * distance);
}

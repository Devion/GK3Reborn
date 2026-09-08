using System.Numerics;

namespace GK3Reborn.Audio;

/// <summary>
/// Turns GK3's world into the one the sound device thinks in.
/// </summary>
public static class AudioSpace
{
    /// <summary>Puts a point or a direction into the device's coordinates.</summary>
    /// <param name="world">Where it is, or which way it points, in the game's world.</param>
    /// <returns>The same thing, in the right-handed world the device works in.</returns>
    public static Vector3 Device(Vector3 world) => new(world.X, world.Y, -world.Z);

    /// <summary>
    /// Which way the device works out is to the right of a listener.
    /// </summary>
    /// <param name="forward">Where the head faces, in device coordinates.</param>
    /// <param name="up">Which way is up for it, in device coordinates.</param>
    /// <returns>The direction the right ear points.</returns>
    public static Vector3 RightOfListener(Vector3 forward, Vector3 up) =>
        Vector3.Cross(forward, up);

    /// <summary>
    /// How far to the right of a listener something is heard, from -1 to 1.
    /// </summary>
    /// <param name="listener">Where the listener is, in the world.</param>
    /// <param name="forward">Where they face, in the world.</param>
    /// <param name="up">Which way is up for them, in the world.</param>
    /// <param name="source">Where the sound is, in the world.</param>
    /// <returns>
    /// Negative when it is heard on the left, positive on the right, and zero when it is
    /// straight ahead, behind, above or below.
    /// </returns>
    public static float Panning(Vector3 listener, Vector3 forward, Vector3 up, Vector3 source)
    {
        Vector3 right = RightOfListener(Device(forward), Device(up));
        Vector3 towards = Device(source) - Device(listener);

        return right.LengthSquared() <= 0 || towards.LengthSquared() <= 0
            ? 0
            : Vector3.Dot(Vector3.Normalize(towards), Vector3.Normalize(right));
    }
}

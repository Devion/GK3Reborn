using System.Numerics;

namespace GK3Reborn.Audio;

/// <summary>
/// Turns GK3's world into the one the sound device thinks in.
/// </summary>
/// <remarks>
/// <para>
/// <b>GK3's world is left-handed and OpenAL's is right-handed.</b> The renderer already
/// knows this — screen right is <c>cross(up, forward)</c> there, not the other way about —
/// and the ears had never been told. Handing world coordinates straight to the device
/// makes it work out its own right as <c>cross(forward, up)</c>, which points the opposite
/// way, so every sound in the game came from the wrong side: the fountain on your left was
/// heard on your right.
/// </para>
/// <para>
/// One axis reversed is exactly what changes handedness, and reversing it is its own
/// inverse. Z is the one turned over here — the choice is arbitrary, since any single axis
/// gives the same result, and Z is the one graphics conventionally flips.
/// </para>
/// <para>
/// <b>It must be applied to everything or to nothing.</b> The listener's position, the way
/// the head faces, which way is up for it, and every source's position are one geometry;
/// mirroring some of them and not the others does not swap left and right, it puts sounds
/// somewhere that does not exist. Distances are unaffected — a mirror is a rigid motion —
/// so anything that only measures how far away a sound is stays in the world's own
/// coordinates.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// OpenAL's own rule, written down so that a test can check the whole chain against it
    /// rather than against my idea of it. The game's rule is the other order, which is the
    /// whole of the difference this file exists to bridge.
    /// </remarks>
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
    /// <remarks>
    /// The whole chain in one line, so that "a sound on your left is heard on your left"
    /// is a thing a test can assert. Nothing in the game reads it; the device does this
    /// arithmetic itself, from what it is given.
    /// </remarks>
    public static float Panning(Vector3 listener, Vector3 forward, Vector3 up, Vector3 source)
    {
        Vector3 right = RightOfListener(Device(forward), Device(up));
        Vector3 towards = Device(source) - Device(listener);

        return right.LengthSquared() <= 0 || towards.LengthSquared() <= 0
            ? 0
            : Vector3.Dot(Vector3.Normalize(towards), Vector3.Normalize(right));
    }
}

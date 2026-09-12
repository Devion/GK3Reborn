using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Animation;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// The motion a clip is asking a refined head to make, read out of its vertices.
/// </summary>
public sealed class HeadMotion
{
    /// <summary>Scratch for the fit, kept so a frame does not allocate.</summary>
    private readonly List<Vector3> _from = [];

    /// <summary>The same, for the side being fitted onto.</summary>
    private readonly List<Vector3> _to = [];

    /// <summary>Whether this clip's head vertices and this model's head are one head.</summary>
    private bool? _fits;

    /// <summary>
    /// How far a fit may miss by, as a share of the head's own width.
    /// </summary>
    private const float Limit = 0.08f;

    /// <summary>Reads the head's motion on a frame.</summary>
    /// <param name="clip">The clip posing the character.</param>
    /// <param name="rig">The head's authored vertices, which is what the clip addresses.</param>
    /// <param name="at">Which frame, with a fraction of the way to the next.</param>
    /// <param name="repeat">Whether the clip loops, which decides how it reads past its end.</param>
    /// <returns>The transform, or null when this clip does not move this head.</returns>
    public Matrix4x4? Of(ActFile clip, HeadRig rig, float at, bool repeat)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(rig);

        if (_fits is false)
        {
            return null;
        }

        _from.Clear();
        _to.Clear();

        foreach (int submesh in clip.ShapedSubmeshes(rig.Mesh))
        {
            if (submesh < 0 || submesh >= rig.Rest.Length ||
                clip.ShapeAt(rig.Mesh, submesh, at, repeat) is not { } shape ||
                shape.Count != rig.Rest[submesh].Length)
            {
                continue;
            }

            // By sample rather than wholesale: the three axis markers every mesh group
            // carries sit sixty units out and do not move with the head, and a fit that
            // includes them is decided by them.
            foreach (int vertex in rig.Sample[submesh])
            {
                _from.Add(rig.Rest[submesh][vertex]);
                _to.Add(shape[vertex]);
            }
        }

        if (_from.Count < 3)
        {
            return null;
        }

        Matrix4x4? fit = RigidFit.Solve(
            CollectionsMarshal.AsSpan(_from),
            CollectionsMarshal.AsSpan(_to),
            out float residual);

        _fits ??= fit is not null && rig.Span > 0f && residual <= Limit * rig.Span;

        return _fits is true ? fit : null;
    }
}

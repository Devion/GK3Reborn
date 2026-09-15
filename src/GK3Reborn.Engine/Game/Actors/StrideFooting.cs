using System.Numerics;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>Aligns a complete walking cycle to the visible soles, without following the swinging foot.</summary>
public static class StrideFooting
{
    /// <summary>Returns a constant vertical correction for a stride's existing rest transform.</summary>
    /// <param name="model">The placed actor's model, including drawable triangle indices.</param>
    /// <param name="clip">The original walk cycle.</param>
    /// <param name="character">The shoe mesh identifiers.</param>
    /// <param name="rest">The correction already applied to the clip.</param>
    /// <returns>The offset that puts the cycle's lowest visible sole at zero, or zero without shoes.</returns>
    public static float Offset(ModFile model, ActFile clip, CharacterConfig? character, Matrix4x4 rest)
    {
        float lowest = float.PositiveInfinity;
        int[] shoes = [character?.LeftShoe?.Mesh ?? -1, character?.RightShoe?.Mesh ?? -1];
        foreach (int mesh in shoes.Distinct())
        {
            if (mesh < 0 || mesh >= model.Meshes.Count)
            {
                continue;
            }
            ModMesh shoe = model.Meshes[mesh];
            for (int frame = 0; frame < clip.FrameCount; frame++)
            {
                if (clip.PoseOf(mesh, frame) is not { } pose)
                {
                    continue;
                }
                Matrix4x4 transform = pose * rest;
                for (int group = 0; group < shoe.Submeshes.Count; group++)
                {
                    ModSubmesh part = shoe.Submeshes[group];
                    IReadOnlyList<Vector3> points = clip.ShapeAt(mesh, group, frame) ?? part.Positions;
                    // Character meshes include unused rig-marker vertices below the actual
                    // soles. Measuring every vertex makes the entire actor float or hop.
                    foreach (ushort index in part.Indices)
                    {
                        if (index < points.Count)
                        {
                            lowest = MathF.Min(lowest, Vector3.Transform(points[index], transform).Y);
                        }
                    }
                }
            }
        }
        return float.IsFinite(lowest) ? -lowest : 0f;
    }
}

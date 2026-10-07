using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering.Geometry;

namespace GK3Reborn.Rendering.VR;

/// <summary>Independent copies of the current character's hands, driven by grip poses.
/// The original models have no finger skeleton; finger regions bend procedurally.</summary>
public sealed class VrHands
{
    private readonly SceneGeometry _geometry;
    private readonly ModFile?[] _models = new ModFile?[2];
    private readonly ModelPlacement[] _placements = new ModelPlacement[2];
    private readonly float[] _grasp = new float[2], _index = new float[2], _thumb = new float[2];

    public VrHands(SceneGeometry geometry, PlacedModel actor)
    {
        _geometry = geometry;
        IReadOnlyList<int> arms = CharacterArms.Find(actor.Model);
        for (int side = 0; side < 2; side++)
        {
            // Gabriel faces -Z in his authored rest pose: positive X is his left.
            int[] candidates = [.. arms.Where(i => (actor.Model.Meshes[i].MeshToLocal.Translation.X > 0) == (side == 0))];
            if (candidates.Length == 0) { continue; }
            ModMesh hand = actor.Model.Meshes[candidates.MinBy(i => actor.Model.Meshes[i].MeshToLocal.Translation.Y)];
            Vector3 pivot = hand.MeshToLocal.Translation;
            Vector3 Canonical(Vector3 p)
            {
                Vector3 rest = Vector3.Transform(p, hand.MeshToLocal) - pivot;
                return new Vector3(rest.Z, side == 0 ? -rest.X : rest.X, -rest.Y);
            }
            var parts = new List<ModSubmesh>();
            foreach (ModSubmesh source in hand.Submeshes)
            {
                // MOD files carry unused marker vertices far beyond the actual hand.
                ushort[] used = [.. source.Indices.Distinct().Order()];
                var remap = used.Select((old, index) => (old, index)).ToDictionary(p => p.old, p => (ushort)p.index);
                Vector3[] positions = [.. used.Select(i => Canonical(source.Positions[i]))];
                Vector3[] normals = new Vector3[positions.Length];
                ushort[] indices = [.. source.Indices.Select(i => remap[i])];
                if (side == 1)
                {
                    for (int i = 0; i + 2 < indices.Length; i += 3) { (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]); }
                }
                RecalculateNormals(positions, indices, normals);
                parts.Add(source with { Positions = positions, Normals = normals,
                    TexCoords = [.. used.Select(i => source.TexCoords[i])], Indices = indices });
            }
            Vector3[] all = [.. parts.SelectMany(p => p.Positions)];
            if (all.Length == 0) { continue; }
            var mesh = hand with { MeshToLocal = Matrix4x4.Identity, Submeshes = parts,
                BoundsMin = all.Aggregate(Vector3.Min), BoundsMax = all.Aggregate(Vector3.Max) };
            _models[side] = ModFile.FromMeshes("vr-hand-" + side, [mesh]);
            _placements[side] = geometry.Add(_models[side]!);
            geometry.SetVisible(_placements[side], false);
        }
    }

    public void Update(IVrSession session, float seconds)
    {
        VrInput input = session.Input;
        for (int side = 0; side < 2; side++)
        {
            if (_models[side] is not { } model) { continue; }
            bool tracked = side == 0 ? input.LeftGripTracked : input.RightGripTracked;
            bool visible = tracked && session.Focused && session.Preferences.ShowHands && !session.Transitioning;
            _geometry.SetVisible(_placements[side], visible);
            if (!visible) { continue; }
            VrPose grip = side == 0 ? input.LeftGrip : input.RightGrip;
            float squeeze = side == 0 ? input.LeftSqueeze : input.RightSqueeze;
            float trigger = side == 0 ? input.Teleport : input.Select;
            bool indexTouch = side == 0 ? input.LeftIndexTouch : input.RightIndexTouch;
            bool thumbTouch = side == 0 ? input.LeftThumbTouch : input.RightThumbTouch;
            float ease = 1 - MathF.Exp(-18 * Math.Clamp(seconds, 0, 0.1f));
            _grasp[side] += (squeeze - _grasp[side]) * ease;
            _index[side] += (MathF.Max(trigger, indexTouch ? 0.25f : 0) - _index[side]) * ease;
            _thumb[side] += ((thumbTouch ? 0.8f : 0) - _thumb[side]) * ease;
            for (int sub = 0; sub < model.Meshes[0].Submeshes.Count; sub++)
            {
                ModSubmesh part = model.Meshes[0].Submeshes[sub];
                _geometry.ShapeMesh(_placements[side], 0, sub,
                    part.Positions.Select(p => Curl(p, _grasp[side], _index[side], _thumb[side])).ToArray());
            }
            VrRig rig = session.Rig;
            Vector3 x = rig.Direction(grip, Vector3.UnitX), y = rig.Direction(grip, Vector3.UnitY), z = rig.Direction(grip, -Vector3.UnitZ);
            Vector3 at = rig.Point(grip.Position);
            var transform = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, at.X, at.Y, at.Z, 1);
            _geometry.MoveModel(_placements[side], Matrix4x4.CreateScale(rig.UnitsPerMetre / VrRig.DefaultUnitsPerMetre) * transform);
        }
    }

    public static Vector3 Curl(Vector3 point, float grasp, float index, float thumb)
    {
        // Only the distal region bends; the wrist and palm remain anchored to grip.
        float length = MathF.Max(0, point.Z - 0.25f);
        float amount = point.X > 1.4f ? thumb : point.X > 0.4f ? index : grasp;
        float angle = Math.Clamp(amount, 0, 1) * MathF.Min(length / 2.5f, 1) * 1.7f;
        return new Vector3(point.X, point.Y - MathF.Sin(angle) * length, point.Z - length + MathF.Cos(angle) * length);
    }

    private static void RecalculateNormals(Vector3[] points, ushort[] indices, Vector3[] normals)
    {
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            Vector3 normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        for (int i = 0; i < normals.Length; i++) { normals[i] = normals[i].LengthSquared() > 1e-9f ? Vector3.Normalize(normals[i]) : Vector3.UnitY; }
    }
}

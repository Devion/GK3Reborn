using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game.Interaction;

/// <summary>What sort of thing a ray landed on.</summary>
public enum PickKind
{
    /// <summary>An object baked into the room's geometry — a wall, a door, a stair.</summary>
    Geometry,

    /// <summary>A volume in the geometry that is never drawn but can still be clicked.</summary>
    HitTest,

    /// <summary>A prop, loaded from its own model file.</summary>
    Prop,

    /// <summary>A character.</summary>
    Actor,
}

/// <summary>
/// What a ray into the scene found.
/// </summary>
/// <param name="Name">Name of the thing hit — a BSP object name or a model name.</param>
/// <param name="Noun">What the scene calls it, or null when the scene names it nothing.</param>
/// <param name="Verb">The verb a click does by default, if the scene names one.</param>
/// <param name="Distance">How far along the ray the hit is, in scene units.</param>
/// <param name="Point">Where the ray met it, in world space.</param>
/// <param name="Kind">What sort of thing it is.</param>
public readonly record struct ScenePick(
    string Name,
    string? Noun,
    string? Verb,
    float Distance,
    Vector3 Point,
    PickKind Kind)
{
    /// <summary>Whether the player can do anything to it.</summary>
    public bool IsInteractive => Noun is { Length: > 0 };
}

/// <summary>
/// Answers what is under a point on the screen.
/// </summary>
public sealed class ScenePicker
{
    private readonly List<Target> _targets = [];

    /// <summary>Builds a picker for a loaded scene.</summary>
    /// <param name="scene">The scene, with its geometry and its placed models.</param>
    public ScenePicker(LoadedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        Dictionary<string, SceneModel> declared = new(StringComparer.OrdinalIgnoreCase);

        foreach (SceneModel model in scene.Definition.Models())
        {
            declared[model.Name] = model;
        }

        if (scene.Geometry is { } bsp)
        {
            AddGeometry(bsp, declared, scene.ReplacedSurfaces, scene.HitTestMasks);
        }

        foreach (GrownStand stand in scene.Woods ?? [])
        {
            AddStand(stand, declared);
        }

        foreach (PlacedModel placed in scene.Models)
        {
            // Surfacing is drawn and never picked. A road laid over the floor is the
            // nearest thing the ray meets, and FloorTarget wants the floor by name -- so
            // without this a click on the road reaches a nameless prop, nothing happens,
            // and the player cannot walk on the ground he can see.
            if (SceneModel.IsDecal(declared.GetValueOrDefault(placed.Name ?? string.Empty)))
            {
                continue;
            }

            AddModel(placed);
        }
    }

    /// <summary>How many separately nameable things the ray can meet.</summary>
    public int TargetCount => _targets.Count;

    /// <summary>How many triangles those things are made of.</summary>
    public int TriangleCount =>
        _targets.Sum(t => t.Parts.Sum(p => p.Triangles.Length)) / 3;

    /// <summary>
    /// Things a script has switched off, by name.
    /// </summary>
    public ISet<string>? Blocked { get; init; }
    /// <summary>
    /// Everything in the room the player can act on, and where it is.
    /// </summary>
    /// <returns>
    /// Each noun once, with the object it was found on and the middle of what it occupies in
    /// world space. The object comes back because what a noun should be <em>called</em>
    /// sometimes depends on it — a hotel door is named by the number in its model's name —
    /// and there is no pick to ask when every hotspot is being listed at once.
    /// </returns>
    public IReadOnlyList<(string Noun, string Name, Vector3 Where)> Interactive()
    {
        var found = new List<(string, string, Vector3)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Target target in _targets)
        {
            // The same two refusals a click makes, and for the same reason: what is not
            // drawn is not there, and what a script has switched off is not there either.
            // Reported as an item still having a hotspot after it had been picked up —
            // taking something takes its model out of the room, so the ray stopped finding
            // it and this went on listing it. A label for something that is not there is
            // worse than no label, which is what the list is for.
            if (target.Noun is not { Length: > 0 } noun ||
                target.Of is { Visible: false } ||
                Blocked?.Contains(target.Name) == true ||
                !seen.Add(noun))
            {
                continue;
            }

            Vector3 minimum = new(float.MaxValue);
            Vector3 maximum = new(float.MinValue);
            bool any = false;

            foreach (Part part in target.Parts)
            {
                // Where the group is now, then where the model is now — the same pair the
                // ray is transformed by. A room's own geometry has no placement and its
                // triangles are already where they are.
                Matrix4x4 pose = target.Of is { } placed
                    ? part.Mesh >= 0
                        ? placed.PoseOf(part.Mesh) * placed.Standing
                        : placed.Standing
                    : Matrix4x4.Identity;

                minimum = Vector3.Min(minimum, Vector3.Transform(part.Minimum, pose));
                maximum = Vector3.Max(maximum, Vector3.Transform(part.Maximum, pose));
                any = true;
            }

            if (any)
            {
                found.Add((noun, target.Name, (minimum + maximum) * 0.5f));
            }
        }

        return found;
    }


    /// <summary>Casts a ray into the scene.</summary>
    /// <param name="ray">Where from and which way.</param>
    /// <param name="ignoring">
    /// Things the ray passes straight through, by name, or null to meet everything.
    /// </param>
    /// <returns>The nearest thing it met, or null if it met nothing.</returns>
    public ScenePick? Pick(Ray ray, IReadOnlySet<string>? ignoring = null)
    {
        ScenePick? nearest = null;
        float best = float.MaxValue;

        foreach (Target target in _targets)
        {
            if (ignoring is { Count: > 0 } skip && skip.Contains(target.Name))
            {
                continue;
            }

            // What is not drawn is not there to be clicked. A scene hides models it means
            // to show later — the moped waiting for its scripted ride past RC1 — and a ray
            // that meets one picks up a noun for something invisible, which reads as the
            // pointer catching on empty air.
            if (target.Of is { Visible: false })
            {
                continue;
            }

            // And what a script has switched off is not there either, which is how a scene
            // stops the player clicking through something it is in the middle of.
            if (Blocked is { Count: > 0 } off && off.Contains(target.Name))
            {
                continue;
            }

            // A model's triangles are kept in the space they were built in, so the ray
            // goes to where each part of it is now rather than the triangles being moved
            // to meet the ray. A part is a mesh group, because that is what an animation
            // moves: a clip replaces each group's own transform and the model's placement
            // is applied on top, so a character an animation has put somewhere is nowhere
            // near the placement the scene gave them.
            foreach (Part part in target.Parts)
            {
                if (Into(ray, target, part) is not { } local)
                {
                    continue;
                }

                if (!MeetsBox(local, part.Minimum, part.Maximum, best))
                {
                    continue;
                }

                if (Nearest(local, part, target.FrontFacingOnly, best) is not { } distance)
                {
                    continue;
                }

                best = distance;

                nearest = new ScenePick(
                    target.Name, target.Noun, target.Verb, distance, ray.At(distance), target.Kind);
            }
        }

        return nearest;
    }

    /// <summary>Casts a ray through a pixel of a rendered image.</summary>
    /// <param name="camera">The camera the image was rendered from.</param>
    /// <param name="x">Column, from the left edge.</param>
    /// <param name="y">Row, from the top edge.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>The nearest thing under that pixel, or null.</returns>
    public ScenePick? Pick(Camera camera, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return Pick(camera.RayThrough(x, y, width, height));
    }

    /// <summary>Gathers the room's own geometry, one target per named object.</summary>
    /// <param name="bsp">The room's parsed geometry.</param>
    /// <param name="declared">What the scene files say about each object, by name.</param>
    /// <param name="replaced">
    /// Surfaces a grown tree stands in for, which the renderer was told not to draw.
    /// </param>
    /// <param name="masks">
    /// What is painted on each hit-test texture, by texture name. See <see cref="Drawn"/>.
    /// </param>
    private void AddGeometry(
        BspFile bsp,
        Dictionary<string, SceneModel> declared,
        IReadOnlySet<int>? replaced,
        IReadOnlyDictionary<string, CutoutMask>? masks)
    {
        // Which objects carry a silhouette, decided before a triangle is gathered so that
        // the coordinates and the masks below stay in step with the triangles without being
        // padded. Only hit tests: what is painted on one is a statement about which part of
        // the quad is the thing, where on a wall it is a picture. Empty in 108 of the 110
        // rooms — see SceneLoader.ReadHitTestMasks for the count — which is what keeps a
        // coordinate per corner off every wall in the game.
        HashSet<int> masked = Masked(bsp, declared, masks);

        Dictionary<int, List<Vector3>> byObject = [];
        Dictionary<int, List<Vector2>> coordinates = [];
        Dictionary<int, List<CutoutMask?>> cutouts = [];

        foreach (BspPolygon polygon in bsp.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= bsp.Surfaces.Count ||
                replaced?.Contains(polygon.SurfaceIndex) == true)
            {
                continue;
            }

            BspSurface surface = bsp.Surfaces[polygon.SurfaceIndex];
            int objectIndex = surface.ObjectIndex;

            if (objectIndex < 0 || objectIndex >= bsp.ObjectNames.Count)
            {
                continue;
            }

            if (!byObject.TryGetValue(objectIndex, out List<Vector3>? triangles))
            {
                triangles = [];
                byObject[objectIndex] = triangles;
            }

            bool carries = masked.Contains(objectIndex);

            if (carries && !coordinates.ContainsKey(objectIndex))
            {
                coordinates[objectIndex] = [];
                cutouts[objectIndex] = [];
            }

            // Null where this particular surface of a masked object has no mask of its own,
            // which reads as "solid all over" — the same answer an object with no mask gets.
            CutoutMask? cutout =
                carries && masks?.TryGetValue(surface.TextureName, out CutoutMask? found) == true
                    ? found
                    : null;

            foreach ((ushort a, ushort b, ushort c) in bsp.Triangulate(polygon))
            {
                triangles.Add(bsp.Vertices[a]);
                triangles.Add(bsp.Vertices[b]);
                triangles.Add(bsp.Vertices[c]);

                if (!carries)
                {
                    continue;
                }

                coordinates[objectIndex].Add(bsp.TexCoordFor(a));
                coordinates[objectIndex].Add(bsp.TexCoordFor(b));
                coordinates[objectIndex].Add(bsp.TexCoordFor(c));
                cutouts[objectIndex].Add(cutout);
            }
        }

        foreach ((int objectIndex, List<Vector3> triangles) in byObject.OrderBy(p => p.Key))
        {
            string name = bsp.ObjectNames[objectIndex];
            declared.TryGetValue(name, out SceneModel? model);

            // A model the story has switched off is not there at all, so the ray goes
            // through it. Props are excluded here for a different reason: a prop line names
            // a file to load and stand in the room, not an object already inside the BSP,
            // and the model itself is picked separately.
            if (model is { Hidden: true } || (model is not null && IsProp(model)))
            {
                continue;
            }

            Part part = coordinates.TryGetValue(objectIndex, out List<Vector2>? uvs)
                ? new Part(-1, [.. triangles], [.. uvs], [.. cutouts[objectIndex]])
                : new Part(-1, [.. triangles]);

            _targets.Add(new Target(
                name,
                NounOf(model),
                model?.Verb,
                IsHitTest(model) ? PickKind.HitTest : PickKind.Geometry,
                [part],
                FrontFacingOnly: true));
        }
    }

    /// <summary>Which of the room's objects have a silhouette painted on them.</summary>
    /// <param name="bsp">The room's parsed geometry.</param>
    /// <param name="declared">What the scene files say about each object, by name.</param>
    /// <param name="masks">The silhouettes the loader read, by texture name.</param>
    /// <returns>Object indices, empty when the room has no masked hit test.</returns>
    private static HashSet<int> Masked(
        BspFile bsp,
        Dictionary<string, SceneModel> declared,
        IReadOnlyDictionary<string, CutoutMask>? masks)
    {
        HashSet<int> masked = [];

        if (masks is null || masks.Count == 0)
        {
            return masked;
        }

        foreach (BspSurface surface in bsp.Surfaces)
        {
            if (surface.ObjectIndex >= 0 &&
                surface.ObjectIndex < bsp.ObjectNames.Count &&
                masks.ContainsKey(surface.TextureName) &&
                declared.TryGetValue(bsp.ObjectNames[surface.ObjectIndex], out SceneModel? on) &&
                IsHitTest(on))
            {
                masked.Add(surface.ObjectIndex);
            }
        }

        return masked;
    }

    /// <summary>How many cells a stand's triangles are sorted into, along each axis.</summary>
    private const int StandCells = 4;

    /// <summary>The fewest triangles worth sorting into cells rather than leaving in one.</summary>
    private const int WorthSplitting = 256;

    /// <summary>Gathers one modelled tree grown over the room's own cards.</summary>
    /// <param name="stand">The tree and where it stands.</param>
    /// <param name="declared">What the scene files say about each object, by name.</param>
    private void AddStand(GrownStand stand, Dictionary<string, SceneModel> declared)
    {
        declared.TryGetValue(stand.Named, out SceneModel? model);

        List<Vector3> triangles = [];

        foreach (ModMesh mesh in stand.Tree.Meshes)
        {
            Matrix4x4 into = mesh.MeshToLocal * stand.Standing;

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
                {
                    triangles.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i]], into));
                    triangles.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i + 1]], into));
                    triangles.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i + 2]], into));
                }
            }
        }

        if (triangles.Count == 0)
        {
            return;
        }

        _targets.Add(new Target(
            stand.Named,
            NounOf(model),
            model?.Verb,
            PickKind.Geometry,
            Sorted(triangles),
            FrontFacingOnly: false));
    }

    /// <summary>Sorts a stand's triangles into cells, each of which gets its own box.</summary>
    /// <param name="triangles">Its triangles, three vertices at a time, in world space.</param>
    /// <returns>One part per cell that has anything in it.</returns>
    private static Part[] Sorted(List<Vector3> triangles)
    {
        if (triangles.Count < WorthSplitting * 3)
        {
            return [new Part(-1, [.. triangles])];
        }

        Vector3 least = new(float.MaxValue);
        Vector3 most = new(float.MinValue);

        foreach (Vector3 vertex in triangles)
        {
            least = Vector3.Min(least, vertex);
            most = Vector3.Max(most, vertex);
        }

        Vector3 span = Vector3.Max(most - least, new Vector3(1e-3f));
        Dictionary<int, List<Vector3>> cells = [];

        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            Vector3 middle = (triangles[i] + triangles[i + 1] + triangles[i + 2]) / 3f;
            Vector3 at = (middle - least) / span * StandCells;

            int cell =
                (Cell(at.X) * StandCells * StandCells) + (Cell(at.Y) * StandCells) + Cell(at.Z);

            if (!cells.TryGetValue(cell, out List<Vector3>? owned))
            {
                owned = [];
                cells[cell] = owned;
            }

            owned.Add(triangles[i]);
            owned.Add(triangles[i + 1]);
            owned.Add(triangles[i + 2]);
        }

        return [.. cells.OrderBy(c => c.Key).Select(c => new Part(-1, [.. c.Value]))];
    }

    /// <summary>Which cell a coordinate falls in, with the far edge kept inside.</summary>
    private static int Cell(float at) => Math.Clamp((int)at, 0, StandCells - 1);

    /// <summary>Gathers one placed prop or actor, in the model's own space.</summary>
    private void AddModel(PlacedModel placed)
    {
        List<Part> parts = [];

        for (int group = 0; group < placed.Model.Meshes.Count; group++)
        {
            List<Vector3> triangles = [];

            // Untransformed, because the group's own transform is what a clip replaces.
            // Baking it in here is what left an animated character's hotspot standing in
            // the pose the artist modelled them in.
            foreach (ModSubmesh submesh in placed.Model.Meshes[group].Submeshes)
            {
                for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
                {
                    triangles.Add(submesh.Positions[submesh.Indices[i]]);
                    triangles.Add(submesh.Positions[submesh.Indices[i + 1]]);
                    triangles.Add(submesh.Positions[submesh.Indices[i + 2]]);
                }
            }

            if (triangles.Count > 0)
            {
                parts.Add(new Part(group, [.. triangles]));
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        // Both faces, unlike the room. A room is a box seen from the inside and its far
        // wall's outer face is never what you clicked; a model is a closed shell whose
        // winding is the modeller's business, and rejecting its back faces loses picks on
        // anything authored inside out.
        _targets.Add(new Target(
            placed.Name,
            placed.Noun,
            placed.Verb,
            placed.Kind == PlacedModelKind.Actor ? PickKind.Actor : PickKind.Prop,
            [.. parts],
            FrontFacingOnly: false)
        {
            Of = placed,
        });
    }

    /// <summary>The noun an object answers to, if it answers to one.</summary>
    private static string? NounOf(SceneModel? model) =>
        model is null || IsNoClick(model) ? null : model.Noun;

    private static bool IsHitTest(SceneModel? model) =>
        string.Equals(model?.Type, "hittest", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoClick(SceneModel model) =>
        string.Equals(model.Type, "noclick", StringComparison.OrdinalIgnoreCase);

    private static bool IsProp(SceneModel model) =>
        string.Equals(model.Type, "prop", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(model.Type, "gasprop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ray as the target sees it, or null when the target is nowhere.
    /// </summary>
    private static Ray? Into(Ray ray, Target target, Part part)
    {
        if (target.Of is not { } placed)
        {
            return ray;
        }

        // Where the group is now, then where the model is now. The first is what a clip
        // changes and the second is what walking changes, and a character can be moved by
        // either — Emilio is put in the loveseat by one and crosses the square by the other.
        Matrix4x4 standing = part.Mesh >= 0
            ? placed.PoseOf(part.Mesh) * placed.Standing
            : placed.Standing;

        if (standing.IsIdentity)
        {
            return ray;
        }

        // A model scaled to nothing has no inverse and nothing to click on either.
        if (!Matrix4x4.Invert(standing, out Matrix4x4 back))
        {
            return null;
        }

        // The direction is left unnormalised on purpose: scaling it to unit length is what
        // would break the equality of distances that the caller relies on.
        return new Ray(
            Vector3.Transform(ray.Origin, back),
            Vector3.TransformNormal(ray.Direction, back));
    }

    /// <summary>The nearest hit on one target, if the ray reaches it at all.</summary>
    private static float? Nearest(Ray ray, Part part, bool frontFacingOnly, float limit)
    {
        Vector3[] triangles = part.Triangles;
        float? best = null;

        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            Vector3 a = triangles[i];
            Vector3 b = triangles[i + 1];
            Vector3 c = triangles[i + 2];

            if (frontFacingOnly &&
                Vector3.Dot(ray.Direction, Vector3.Cross(b - a, c - a)) >= 0f)
            {
                continue;
            }

            if (Meets(ray, a, b, c) is not { } hit || hit.Distance >= (best ?? limit))
            {
                continue;
            }

            if (!Drawn(part, i / 3, hit))
            {
                continue;
            }

            best = hit.Distance;
        }

        return best;
    }

    /// <summary>
    /// Whether the ray met the drawing on a triangle rather than a hole in it.
    /// </summary>
    /// <param name="part">The part the triangle belongs to.</param>
    /// <param name="triangle">Which triangle of it, counting from zero.</param>
    /// <param name="hit">Where the ray met it.</param>
    /// <returns>True when there is something there to be hit.</returns>
    private static bool Drawn(Part part, int triangle, Meeting hit)
    {
        if (part.Cutouts is not { } cutouts ||
            triangle >= cutouts.Length ||
            cutouts[triangle] is not { } mask ||
            part.Coordinates is not { } coordinates)
        {
            return true;
        }

        int corner = triangle * 3;

        if (corner + 2 >= coordinates.Length)
        {
            return true;
        }

        // Möller-Trumbore hands back the weights of the second and third corners; the first
        // takes what is left.
        Vector2 uv = ((1f - hit.U - hit.V) * coordinates[corner]) +
                     (hit.U * coordinates[corner + 1]) +
                     (hit.V * coordinates[corner + 2]);

        return mask.Covers(uv);
    }

    /// <summary>Where along a ray a triangle was met, and where on the triangle.</summary>
    /// <param name="Distance">How far along the ray, in the space the test was made in.</param>
    /// <param name="U">Weight of the triangle's second corner.</param>
    /// <param name="V">Weight of its third.</param>
    private readonly record struct Meeting(float Distance, float U, float V);

    /// <summary>Möller–Trumbore, without the culling: the caller decides about faces.</summary>
    private static Meeting? Meets(Ray ray, Vector3 a, Vector3 b, Vector3 c)
    {
        const float epsilon = 1e-7f;

        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 across = Vector3.Cross(ray.Direction, ac);
        float determinant = Vector3.Dot(ab, across);

        if (MathF.Abs(determinant) < epsilon)
        {
            return null;
        }

        float inverse = 1f / determinant;
        Vector3 toA = ray.Origin - a;
        float u = Vector3.Dot(toA, across) * inverse;

        if (u is < 0f or > 1f)
        {
            return null;
        }

        Vector3 along = Vector3.Cross(toA, ab);
        float v = Vector3.Dot(ray.Direction, along) * inverse;

        if (v < 0f || u + v > 1f)
        {
            return null;
        }

        float distance = Vector3.Dot(ac, along) * inverse;

        return distance > epsilon ? new Meeting(distance, u, v) : null;
    }

    /// <summary>Whether the ray enters a box before a distance it has already beaten.</summary>
    private static bool MeetsBox(Ray ray, Vector3 minimum, Vector3 maximum, float limit)
    {
        float near = 0f;
        float far = limit;

        for (int axis = 0; axis < 3; axis++)
        {
            float direction = Component(ray.Direction, axis);
            float origin = Component(ray.Origin, axis);
            float low = Component(minimum, axis);
            float high = Component(maximum, axis);

            if (MathF.Abs(direction) < 1e-9f)
            {
                if (origin < low || origin > high)
                {
                    return false;
                }

                continue;
            }

            float inverse = 1f / direction;
            float first = (low - origin) * inverse;
            float second = (high - origin) * inverse;

            if (first > second)
            {
                (first, second) = (second, first);
            }

            near = MathF.Max(near, first);
            far = MathF.Min(far, second);

            if (near > far)
            {
                return false;
            }
        }

        return true;
    }

    private static float Component(Vector3 vector, int axis) =>
        axis switch { 0 => vector.X, 1 => vector.Y, _ => vector.Z };

    /// <summary>
    /// One piece of a target that moves as a unit, and its triangles.
    /// </summary>
    private sealed record Part
    {
        public Part(int mesh, Vector3[] triangles)
            : this(mesh, triangles, null, null)
        {
        }

        public Part(
            int mesh, Vector3[] triangles, Vector2[]? coordinates, CutoutMask?[]? cutouts)
        {
            Mesh = mesh;
            Triangles = triangles;
            Coordinates = coordinates;
            Cutouts = cutouts;

            Vector3 minimum = new(float.MaxValue);
            Vector3 maximum = new(float.MinValue);

            foreach (Vector3 vertex in triangles)
            {
                minimum = Vector3.Min(minimum, vertex);
                maximum = Vector3.Max(maximum, vertex);
            }

            // A hair of slack, so a box around a wall with no thickness still has volume
            // for the slab test to work with.
            Minimum = minimum - new Vector3(0.01f);
            Maximum = maximum + new Vector3(0.01f);
        }

        public int Mesh { get; }

        public Vector3[] Triangles { get; }

        /// <summary>
        /// A texture coordinate per corner, in step with <see cref="Triangles"/>, or null.
        /// </summary>
        public Vector2[]? Coordinates { get; }

        /// <summary>
        /// The silhouette each triangle is drawn on, one per triangle, or null.
        /// </summary>
        public CutoutMask?[]? Cutouts { get; }

        public Vector3 Minimum { get; }

        public Vector3 Maximum { get; }
    }

    /// <summary>One nameable thing, in as many pieces as can move independently.</summary>
    private sealed record Target
    {
        public Target(
            string name,
            string? noun,
            string? verb,
            PickKind kind,
            Vector3[] triangles,
            bool FrontFacingOnly)
            : this(name, noun, verb, kind, [new Part(-1, triangles)], FrontFacingOnly)
        {
        }

        public Target(
            string name,
            string? noun,
            string? verb,
            PickKind kind,
            Part[] parts,
            bool FrontFacingOnly)
        {
            Name = name;
            Noun = noun;
            Verb = verb;
            Kind = kind;
            Parts = parts;
            this.FrontFacingOnly = FrontFacingOnly;
        }

        public string Name { get; }

        public string? Noun { get; }

        public string? Verb { get; }

        /// <summary>The model this stands for, when it is one that can move or be hidden.</summary>
        public PlacedModel? Of { get; init; }

        public PickKind Kind { get; }

        /// <summary>The pieces it is made of, each of which can be moved on its own.</summary>
        public Part[] Parts { get; }

        public bool FrontFacingOnly { get; }
    }
}

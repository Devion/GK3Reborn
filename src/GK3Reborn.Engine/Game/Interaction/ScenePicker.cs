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
/// <remarks>
/// A pick is always reported, even when the thing hit answers to nothing. Most of a room
/// is wallpaper with no noun, and that wallpaper still <em>blocks</em>: the difference
/// between clicking a wall and clicking a door hidden behind it is the whole point of
/// casting a ray rather than testing bounding boxes.
/// </remarks>
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
    /// <remarks>
    /// A noun is the whole test, as it is in the original: an object the player can name is
    /// an object the player can act on, and everything else is scenery.
    /// </remarks>
    public bool IsInteractive => Noun is { Length: > 0 };
}

/// <summary>
/// Answers what is under a point on the screen.
/// </summary>
/// <remarks>
/// <para>
/// GK3 puts nearly everything clickable inside the room's own geometry. A door, a drawer,
/// a notice board are objects in the BSP that the initialisation file names — <c>model=</c>
/// with a <c>noun=</c> — and the handful of things that are not, the props and the people,
/// are separate models standing in it. So resolving a click means casting one ray at the
/// geometry and at the placed models together and keeping whichever it reaches first.
/// </para>
/// <para>
/// Some clickable things are not drawn at all. A <c>hittest</c> is ordinary geometry with
/// an ordinary texture that the scene marks invisible: a slab across a doorway, a box over
/// the area a note occupies on a desk, giving the player something forgiving to aim at.
/// They are in the ray's world even though they are not in the picture, which is why a
/// picture is not enough to check this against — hence the noun map.
/// </para>
/// <para>
/// Hidden objects are the opposite: a <c>scene</c> or <c>hittest</c> model the story has
/// switched off is not merely undrawn, it is not there. The ray passes through it and hits
/// whatever stands behind, exactly as the original does by clearing the interactive flag on
/// those surfaces.
/// </para>
/// </remarks>
public sealed class ScenePicker
{
    private readonly List<Target> _targets = [];

    /// <summary>Builds a picker for a loaded scene.</summary>
    /// <param name="scene">The scene, with its geometry and its placed models.</param>
    /// <remarks>
    /// The triangles are gathered once, grouped by object with a box around each — the
    /// room's in world space, a model's in its own, since a model can still be moved. A
    /// room is fifteen thousand triangles and a click has to be answered between two
    /// frames; the box rejects nearly all of them before any arithmetic that matters
    /// happens.
    /// </remarks>
    public ScenePicker(LoadedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.Geometry is { } bsp)
        {
            AddGeometry(bsp, scene.Definition);
        }

        foreach (PlacedModel placed in scene.Models)
        {
            AddModel(placed);
        }
    }

    /// <summary>How many separately nameable things the ray can meet.</summary>
    public int TargetCount => _targets.Count;

    /// <summary>
    /// Things a script has switched off, by name.
    /// </summary>
    /// <remarks>
    /// Shared with the story rather than owned here, because a hit test switched off stays
    /// off across a camera cut and a reload of the same room. See
    /// <c>GameState.BlockedHitTests</c>.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// For showing them all at once while a key is held. A 1999 adventure game hides its
    /// hotspots and expects the player to sweep the pointer over the furniture until
    /// something lights up, which is the least interesting thing anybody does in one.
    /// </para>
    /// <para>
    /// Each noun once, not each object: the church carves its four angels as four models and
    /// the hallway's hit tests double up on doors, and a room labelled twice for the same
    /// thing reads as a fault rather than as thoroughness. The first one found wins, and the
    /// middle is of that one's own box.
    /// </para>
    /// </remarks>
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
    /// <returns>The nearest thing it met, or null if it met nothing.</returns>
    public ScenePick? Pick(Ray ray)
    {
        ScenePick? nearest = null;
        float best = float.MaxValue;

        foreach (Target target in _targets)
        {
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
    private void AddGeometry(BspFile bsp, SceneDefinition definition)
    {
        Dictionary<string, SceneModel> declared = new(StringComparer.OrdinalIgnoreCase);

        foreach (SceneModel model in definition.Models())
        {
            declared[model.Name] = model;
        }

        Dictionary<int, List<Vector3>> byObject = [];

        foreach (BspPolygon polygon in bsp.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= bsp.Surfaces.Count)
            {
                continue;
            }

            int objectIndex = bsp.Surfaces[polygon.SurfaceIndex].ObjectIndex;

            if (objectIndex < 0 || objectIndex >= bsp.ObjectNames.Count)
            {
                continue;
            }

            if (!byObject.TryGetValue(objectIndex, out List<Vector3>? triangles))
            {
                triangles = [];
                byObject[objectIndex] = triangles;
            }

            foreach ((ushort a, ushort b, ushort c) in bsp.Triangulate(polygon))
            {
                triangles.Add(bsp.Vertices[a]);
                triangles.Add(bsp.Vertices[b]);
                triangles.Add(bsp.Vertices[c]);
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

            _targets.Add(new Target(
                name,
                NounOf(model),
                model?.Verb,
                IsHitTest(model) ? PickKind.HitTest : PickKind.Geometry,
                [.. triangles],
                FrontFacingOnly: true));
        }
    }

    /// <summary>Gathers one placed prop or actor, in the model's own space.</summary>
    /// <remarks>
    /// Its own space rather than the room's, because it need not stay where it was put.
    /// An actor walks: the sink is handed a new transform every frame and the triangles
    /// gathered here never hear about it, so baking them into the room would leave
    /// Gabriel's noun standing on the spot he set off from — the pointer finding him
    /// where he used to be and finding nothing where he is.
    /// </remarks>
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
    /// <remarks>
    /// <c>noclick</c> is drawn and solid but never named, so a click on it lands on
    /// scenery. One object in the corpus is declared that way — TE3's floor — and it is
    /// the floor, which the player is meant to walk on rather than talk to.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The room's own geometry is already in world space and comes back untouched. A model
    /// is asked where it is standing now and the ray is put through the inverse of that.
    /// </para>
    /// <para>
    /// Distances survive the trip. An affine transform carries the point at <c>t</c> along
    /// the ray to the point at <c>t</c> along the transformed ray, so a hit found in a
    /// model's own space is at the same <c>t</c> in the room — which is what lets a
    /// scaled actor and a wall be compared for which one the ray reached first, and what
    /// lets the hit point be read off the original ray.
    /// </para>
    /// </remarks>
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

            if (Meets(ray, a, b, c) is { } distance && distance < (best ?? limit))
            {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>Möller–Trumbore, without the culling: the caller decides about faces.</summary>
    private static float? Meets(Ray ray, Vector3 a, Vector3 b, Vector3 c)
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

        return distance > epsilon ? distance : null;
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
    /// <remarks>
    /// The triangles are in the space the mesh group was built in rather than in the
    /// room's. A clip replaces a group's own transform and the model's placement is applied
    /// on top, so the only way a hotspot can follow an animated character is to leave the
    /// triangles where they are and move the ray instead.
    /// </remarks>
    private sealed record Part
    {
        public Part(int mesh, Vector3[] triangles)
        {
            Mesh = mesh;
            Triangles = triangles;

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

        public Vector3 Minimum { get; }

        public Vector3 Maximum { get; }
    }

    /// <summary>One nameable thing, in as many pieces as can move independently.</summary>
    /// <remarks>
    /// Which space each piece is in depends on what it is. The room's own geometry is in
    /// the room's, where it cannot go anywhere. A model's is the mesh group's own, and
    /// <see cref="Target.Of"/> is what says where that space currently sits in the room.
    /// </remarks>
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
        /// <remarks>
        /// Held rather than copied, because both where a model is and whether it is drawn
        /// change while the scene is standing, and the picker is built once. Null for the
        /// room's own geometry, which is always there and always where it was.
        /// </remarks>
        public PlacedModel? Of { get; init; }

        public PickKind Kind { get; }

        /// <summary>The pieces it is made of, each of which can be moved on its own.</summary>
        public Part[] Parts { get; }

        public bool FrontFacingOnly { get; }
    }
}

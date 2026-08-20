using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>
/// The script functions that need a scene rather than a story.
/// </summary>
/// <remarks>
/// <para>
/// Most of what a script asks about is the story — flags, counts, who is carrying what —
/// and lives on <see cref="GameState"/> for as long as the game does. A few questions are
/// about the room the player is standing in and mean nothing outside it: whether the van
/// parked across the road is in the way. Those are registered here, against one loaded
/// scene, and go when it does.
/// </para>
/// <para>
/// Three families so far. The walkers, because a boundary is painted once, before anybody
/// knows where the van will park, so the scripts move things onto and off the floor as the
/// story goes. And the cameras, because a camera angle is a <em>name</em> the scene gives —
/// <c>OPEN_WARDROBE</c>, <c>LONG_FROM_STAIRS</c> — and means nothing in the next room.
/// And the glances, because who somebody is looking at is a fact about a room with both of
/// them in it.
/// </para>
/// </remarks>
public static class SceneScripting
{
    /// <summary>Registers a scene's own functions on a host.</summary>
    /// <param name="api">The host.</param>
    /// <param name="scene">The scene they act on.</param>
    /// <param name="glances">Where to record who is looking at what, if anywhere.</param>
    /// <remarks>
    /// Call it again for the next scene: the functions close over this one, and the last
    /// registration wins, which is what changing rooms means.
    /// </remarks>
    public static void Attach(Gk3SheepApi api, LoadedScene scene, Glances? glances = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(scene);

        api.Register("WalkerBoundaryBlockModel", arguments =>
        {
            if (scene.Walkable is { } boundary &&
                arguments.Count > 0 &&
                Footprint(scene, arguments[0].AsString()) is var (minimum, maximum))
            {
                boundary.Block(arguments[0].AsString(), minimum, maximum);
            }

            return SheepValue.FromInt(0);
        });

        api.Register("WalkerBoundaryUnblockModel", arguments =>
        {
            if (scene.Walkable is { } boundary && arguments.Count > 0)
            {
                boundary.Unblock(arguments[0].AsString());
            }

            return SheepValue.FromInt(0);
        });

        // Two indices, because a scriptable region on these bitmaps is painted as an area
        // and the border around it, and opening one without the other leaves a wall a
        // texel thick where the doorway was.
        api.Register("WalkerBoundaryBlockRegion", arguments =>
            SetRegions(scene.Walkable, arguments, open: false));

        api.Register("WalkerBoundaryUnblockRegion", arguments =>
            SetRegions(scene.Walkable, arguments, open: true));

        AttachCameras(api, scene);
        AttachGlances(api, scene, glances);
    }

    /// <summary>
    /// Pointing the camera at one of the angles the scene names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three ways to ask, and the difference between them is the player's. A plain cut
    /// happens only if the player has left cinematics on, or a script has temporarily
    /// insisted; a forced cut ignores both, because some things the story has to show. A
    /// glide is a cut that should take a moment, and until there is a clock to take it in,
    /// it arrives at the same place — the angle it ends at is the observable part, and the
    /// travelling is not.
    /// </para>
    /// <para>
    /// A camera the scene does not name is reported rather than ignored. The original logs
    /// it too, and it is worth hearing: a script pointing the view at nothing leaves the
    /// player looking at whatever they were looking at before, which reads as the game
    /// having missed its cue.
    /// </para>
    /// </remarks>
    private static void AttachCameras(Gk3SheepApi api, LoadedScene scene)
    {
        api.Register("CutToCameraAngle", arguments =>
            CutTo(api, scene, arguments, forced: false));

        api.Register("ForceCutToCameraAngle", arguments =>
            CutTo(api, scene, arguments, forced: true));

        // Waitable in the original because the travelling takes time. Nothing waits on it
        // yet; the flag is kept so a script's recorded order does not change when it does.
        api.Register(
            "GlideToCameraAngle",
            arguments => CutTo(api, scene, arguments, forced: false),
            waitable: true);

        api.Register("SetForcedCameraCuts", arguments =>
        {
            api.State.ForcedCameraCuts = arguments.Count > 0 && arguments[0].AsInt() != 0;
            return SheepValue.FromInt(0);
        });

        api.Register("ClearForcedCameraCuts", _ =>
        {
            api.State.ForcedCameraCuts = false;
            return SheepValue.FromInt(0);
        });

        api.Register("EnableCinematics", _ =>
        {
            api.State.CinematicsEnabled = true;
            return SheepValue.FromInt(0);
        });

        api.Register("DisableCinematics", _ =>
        {
            api.State.CinematicsEnabled = false;
            return SheepValue.FromInt(0);
        });
    }

    private static SheepValue CutTo(
        Gk3SheepApi api, LoadedScene scene, IReadOnlyList<SheepValue> arguments, bool forced)
    {
        if (arguments.Count == 0)
        {
            return SheepValue.FromInt(0);
        }

        string name = arguments[0].AsString();

        if (scene.Definition.AnyCameraNamed(name) is null)
        {
            api.Diagnostics.Add(new Diagnostic(
                "GK3R3202", DiagnosticSeverity.Warning,
                $"'{name}' is not a camera this scene names.",
                scene.Name, null, "a room, cinematic or dialogue camera", name,
                "The view stays where it was, as it does in the original."));

            return SheepValue.FromInt(0);
        }

        if (forced || api.State.CinematicsEnabled || api.State.ForcedCameraCuts)
        {
            api.State.CameraAngle = name;
        }

        return SheepValue.FromInt(0);
    }


    /// <summary>
    /// Turning a head to look at something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference implementation registers all of these and every one of them is an
    /// empty body that returns zero, so no character in that build has ever glanced at
    /// anything. They are not switched off; they were never written.
    /// </para>
    /// <para>
    /// What they need is unusual and, as it turns out, easier than a skeleton would be.
    /// GK3's people are a dozen separate meshes with their own transforms, so turning a
    /// head is placing one mesh differently, about the mesh's own origin — which is where
    /// the neck is. <see cref="CharacterHead"/> finds which mesh that is from what it is
    /// painted with, since the format has no room for a name.
    /// </para>
    /// <para>
    /// The duration each of these carries is not obeyed yet: a glance arrives where it is
    /// going rather than easing there, because easing needs a clock and an update loop.
    /// The <c>Quick</c> forms are recorded as such so that when there is one, the
    /// difference between a snap and a turn is already in the data.
    /// </para>
    /// </remarks>
    private static void AttachGlances(Gk3SheepApi api, LoadedScene scene, Glances? glances)
    {
        if (glances is null)
        {
            return;
        }

        api.Register("LookitActor", a => Look(a, quick: false));
        api.Register("LookitActorQuick", a => Look(a, quick: true));
        api.Register("LookitModel", a => Look(a, quick: false));
        api.Register("LookitModelQuick", a => Look(a, quick: true));

        // The same, aimed at something in the geometry rather than at a prop standing in
        // it - CS2's script points Grace at a hit test, which is a slab nobody can see and
        // a perfectly good thing to look at.
        api.Register("LookitSceneModel", a => Look(a, quick: false));
        api.Register("LookitSceneModelQuick", a => Look(a, quick: true));

        api.Register("LookitCancel", a =>
        {
            if (a.Count > 0)
            {
                glances.Cancel(a[0].AsString());
            }

            return SheepValue.FromInt(0);
        });

        // TurnHead takes angles rather than a target: an actor looking at nothing in
        // particular, which is most of what a person does with their head.
        api.Register("TurnHead", a =>
        {
            if (a.Count >= 3 && Placed(scene, a[0].AsString()) is { } who)
            {
                float yaw = float.DegreesToRadians(a[1].AsInt());
                float pitch = float.DegreesToRadians(a[2].AsInt());

                Vector3 eye = who.Transform.Translation + new Vector3(0, 60, 0);
                Vector3 ahead = Vector3.Transform(
                    new Vector3(MathF.Sin(yaw), MathF.Tan(pitch), MathF.Cos(yaw)) * 100f,
                    Matrix4x4.CreateRotationY(Heading(scene, a[0].AsString())));

                glances.Look(new Glance(a[0].AsString(), null, eye + ahead, Quick: false));
            }

            return SheepValue.FromInt(0);
        }, waitable: true);

        SheepValue Look(IReadOnlyList<SheepValue> arguments, bool quick)
        {
            if (arguments.Count < 2)
            {
                return SheepValue.FromInt(0);
            }

            string actor = arguments[0].AsString();
            string target = arguments[1].AsString();

            if (Where(scene, target) is not { } point)
            {
                string standing = string.Join(
                    ", ",
                    scene.Models
                        .Select(m => m.Noun is { Length: > 0 } noun ? $"{m.Name} ({noun})" : m.Name)
                        .Take(12));

                api.Diagnostics.Add(new Diagnostic(
                    "GK3R3203", DiagnosticSeverity.Warning,
                    $"{actor} was told to look at '{target}', which is not in this scene.",
                    scene.Name, null,
                    $"one of: {standing}, or an object in the geometry",
                    target,
                    "Nobody turns; the original does nothing here either."));

                return SheepValue.FromInt(0);
            }

            glances.Look(new Glance(actor, target, point, quick));
            return SheepValue.FromInt(0);
        }
    }

    /// <summary>Where something in the scene is, for an actor to look at.</summary>
    /// <remarks>
    /// The middle of it rather than its feet: an actor looking at another looks at their
    /// head, and one looking at a wardrobe looks at the middle of the wardrobe.
    /// </remarks>
    private static Vector3? Where(LoadedScene scene, string name)
    {
        if (Placed(scene, name) is { } placed)
        {
            // An actor is looked at in the face, which is the one mesh worth finding.
            if (CharacterHead.Find(placed.Model) is { } head)
            {
                return Vector3.Transform(
                    CharacterHead.PivotOf(placed.Model, head), placed.Transform);
            }

            // Anything else, in the middle. A prop's transform is the identity - its
            // position is baked into its vertices, which is how the original ships them -
            // so where it stands has to be measured rather than read off.
            return Middle(placed);
        }

        return Bounds(scene, name) is var (low, high) ? (low + high) * 0.5f : null;
    }

    /// <summary>The middle of a placed model, in world space.</summary>
    private static Vector3 Middle(PlacedModel placed)
    {
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);

        foreach (ModMesh mesh in placed.Model.Meshes)
        {
            Matrix4x4 toWorld = mesh.MeshToLocal * placed.Transform;

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                foreach (Vector3 position in submesh.Positions)
                {
                    Vector3 world = Vector3.Transform(position, toWorld);
                    minimum = Vector3.Min(minimum, world);
                    maximum = Vector3.Max(maximum, world);
                }
            }
        }

        return minimum.X > maximum.X ? placed.Transform.Translation : (minimum + maximum) * 0.5f;
    }

    private static PlacedModel? Placed(LoadedScene scene, string name)
    {
        foreach (PlacedModel placed in scene.Models)
        {
            if (string.Equals(placed.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(placed.Noun, name, StringComparison.OrdinalIgnoreCase))
            {
                return placed;
            }
        }

        return null;
    }

    /// <summary>Which way an actor is facing, from the spot the scene stood them on.</summary>
    private static float Heading(LoadedScene scene, string actor)
    {
        foreach (SceneActor placed in scene.Definition.Actors())
        {
            if (string.Equals(placed.Name, actor, StringComparison.OrdinalIgnoreCase) &&
                scene.Definition.PositionNamed(placed.Position) is { } spot)
            {
                return spot.Heading;
            }
        }

        return 0f;
    }

    private static SheepValue SetRegions(
        WalkBoundary? boundary, IReadOnlyList<SheepValue> arguments, bool open)
    {
        foreach (SheepValue argument in arguments)
        {
            boundary?.SetRegionOpen(argument.AsInt(), open);
        }

        return SheepValue.FromInt(0);
    }

    /// <summary>
    /// The ground a named object stands on, as a rectangle.
    /// </summary>
    /// <remarks>
    /// A box around everything the object is made of, flattened onto the floor by throwing
    /// the height away — the original does the same, and it is coarse in the right
    /// direction: an actor walks around a chair rather than through the gap under its seat.
    /// The name may be a prop standing in the room or an object baked into the geometry,
    /// because the scene files name both the same way.
    /// </remarks>
    private static (Vector2 Minimum, Vector2 Maximum)? Footprint(LoadedScene scene, string name)
    {
        if (Bounds(scene, name) is not var (low, high))
        {
            return null;
        }

        return (new Vector2(low.X, low.Z), new Vector2(high.X, high.Z));
    }

    /// <summary>
    /// The box round everything named that, whether it is a prop or part of the room.
    /// </summary>
    /// <remarks>
    /// The scene files name both the same way, so both are looked for. Height matters
    /// where a footprint does not: an actor looking at a ceiling fan looks up at it, and
    /// one told to look at the floor of a wardrobe looks down.
    /// </remarks>
    private static (Vector3 Minimum, Vector3 Maximum)? Bounds(LoadedScene scene, string name)
    {
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);
        bool found = false;

        foreach (PlacedModel placed in scene.Models)
        {
            if (!string.Equals(placed.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ModMesh mesh in placed.Model.Meshes)
            {
                Matrix4x4 toWorld = mesh.MeshToLocal * placed.Transform;

                foreach (ModSubmesh submesh in mesh.Submeshes)
                {
                    foreach (Vector3 position in submesh.Positions)
                    {
                        Grow(Vector3.Transform(position, toWorld));
                    }
                }
            }
        }

        if (!found && scene.Geometry is { } bsp)
        {
            int index = IndexOf(bsp, name);

            foreach (BspPolygon polygon in bsp.Polygons)
            {
                if (polygon.SurfaceIndex < 0 ||
                    polygon.SurfaceIndex >= bsp.Surfaces.Count ||
                    bsp.Surfaces[polygon.SurfaceIndex].ObjectIndex != index ||
                    index < 0)
                {
                    continue;
                }

                foreach ((ushort a, ushort b, ushort c) in bsp.Triangulate(polygon))
                {
                    Grow(bsp.Vertices[a]);
                    Grow(bsp.Vertices[b]);
                    Grow(bsp.Vertices[c]);
                }
            }
        }

        return found ? (minimum, maximum) : null;

        void Grow(Vector3 point)
        {
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
            found = true;
        }
    }

    private static int IndexOf(BspFile bsp, string name)
    {
        for (int i = 0; i < bsp.ObjectNames.Count; i++)
        {
            if (string.Equals(bsp.ObjectNames[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

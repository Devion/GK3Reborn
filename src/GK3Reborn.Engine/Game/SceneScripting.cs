using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
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
/// Two families so far. The walkers, because a boundary is painted once, before anybody
/// knows where the van will park, so the scripts move things onto and off the floor as the
/// story goes. And the cameras, because a camera angle is a <em>name</em> the scene gives —
/// <c>OPEN_WARDROBE</c>, <c>LONG_FROM_STAIRS</c> — and means nothing in the next room.
/// </para>
/// </remarks>
public static class SceneScripting
{
    /// <summary>Registers a scene's own functions on a host.</summary>
    /// <param name="api">The host.</param>
    /// <param name="scene">The scene they act on.</param>
    /// <remarks>
    /// Call it again for the next scene: the functions close over this one, and the last
    /// registration wins, which is what changing rooms means.
    /// </remarks>
    public static void Attach(Gk3SheepApi api, LoadedScene scene)
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
        Vector2 minimum = new(float.MaxValue);
        Vector2 maximum = new(float.MinValue);
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
            minimum = Vector2.Min(minimum, new Vector2(point.X, point.Z));
            maximum = Vector2.Max(maximum, new Vector2(point.X, point.Z));
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

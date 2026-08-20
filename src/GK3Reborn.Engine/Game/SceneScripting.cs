using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
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
/// The walker functions are the whole of it so far. A boundary is painted once, before
/// anybody knows where the van will park, so the scripts move things onto and off the
/// floor as the story goes — which is why <see cref="WalkBoundary"/> keeps what is
/// standing on it beside the bitmap rather than in it.
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

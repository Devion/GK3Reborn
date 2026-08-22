using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Formats.Animation;
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
    /// <param name="audio">The room's audio, or null to leave the sound calls recorded.</param>
    /// <param name="world">What moves actors, or null to leave the walking calls recorded.</param>
    /// <param name="behaviours">
    /// Where a behaviour script named by another one is read from, or null to leave the
    /// fidget calls recorded. Only a caller with the archives can answer it.
    /// </param>
    /// <remarks>
    /// Call it again for the next scene: the functions close over this one, and the last
    /// registration wins, which is what changing rooms means.
    /// </remarks>
    public static void Attach(
        Gk3SheepApi api,
        LoadedScene scene,
        Glances? glances = null,
        SceneAudio? audio = null,
        SceneUpdate? world = null,
        Func<string, GasFile?>? behaviours = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(scene);

        Asking(api, scene);

        if (world is not null)
        {
            Walking(api, scene, world);
            Stand(api, scene, world);
            Animating(api, world);
            Showing(api, scene, world);

            if (behaviours is not null)
            {
                world.Behaviours = behaviours;
                Fidgeting(api, world, behaviours);
            }
        }

        if (audio is not null)
        {
            Speak(api, audio);
        }

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
            CutTo(api, scene, arguments, forced: false, gliding: false));

        api.Register("ForceCutToCameraAngle", arguments =>
            CutTo(api, scene, arguments, forced: true, gliding: false));

        // Waitable in the original because the travelling takes time. Nothing waits on it
        // yet; the flag is kept so a script's recorded order does not change when it does.
        api.Register(
            "GlideToCameraAngle",
            arguments => CutTo(api, scene, arguments, forced: false, gliding: true),
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

        // Whether a camera change travels or cuts. The story already reads this to decide
        // between the two; the scripts that ask about it were being told nothing.
        api.Register("IsCameraGlideEnabled", _ =>
            SheepValue.FromInt(api.State.CameraGliding ? 1 : 0));

        // A field of view in degrees, which the scene files also give per camera. Zero or
        // less means "put the scene's own back" rather than "look through a pinhole".
        api.Register("SetCameraFOV", a =>
        {
            float degrees = a.Count > 0 ? a[0].AsFloat() : 0;

            api.State.CameraFieldOfView =
                degrees is > 0 and < 180 ? degrees * MathF.PI / 180f : null;

            return SheepValue.FromInt(0);
        });

        api.Register("EnableCameraGlide", _ =>
        {
            api.State.CameraGliding = true;
            return SheepValue.FromInt(0);
        });

        api.Register("DisableCameraGlide", _ =>
        {
            api.State.CameraGliding = false;
            return SheepValue.FromInt(0);
        });
    }

    /// <summary>
    /// Questions a script asks about what is in the room.
    /// </summary>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room.</param>
    /// <remarks>
    /// All three were unanswered, and an unanswered question is worse than an unperformed
    /// instruction: a script branches on the answer, so a silent zero sends it down the
    /// wrong path and everything after that is wrong for a reason nothing records.
    /// </remarks>
    private static void Asking(Gk3SheepApi api, LoadedScene scene)
    {
        bool Placed(string name, PlacedModelKind? kind) =>
            scene.Models.Any(m =>
                (kind is null || m.Kind == kind) &&
                (m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                 (m.Noun is { Length: > 0 } noun &&
                  noun.Equals(name, StringComparison.OrdinalIgnoreCase))));

        api.Register("DoesModelExist", a =>
            SheepValue.FromInt(a.Count > 0 && Placed(a[0].AsString(), null) ? 1 : 0));

        api.Register("DoesActorExist", a =>
            SheepValue.FromInt(
                a.Count > 0 && Placed(a[0].AsString(), PlacedModelKind.Actor) ? 1 : 0));

        api.Register("DoesSceneModelExist", a =>
            SheepValue.FromInt(
                a.Count > 0 &&
                (Placed(a[0].AsString(), null) ||
                 (scene.Geometry is { } bsp &&
                  bsp.ObjectNames.Any(o =>
                      o.Equals(a[0].AsString(), StringComparison.OrdinalIgnoreCase))))
                    ? 1
                    : 0));
    }

    private static SheepValue CutTo(
        Gk3SheepApi api,
        LoadedScene scene,
        IReadOnlyList<SheepValue> arguments,
        bool forced,
        bool gliding)
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
            api.State.CameraGliding = gliding;
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
    /// <summary>
    /// Makes the calls that were recorded actually make a sound.
    /// </summary>
    /// <param name="api">The host.</param>
    /// <param name="audio">The room's audio.</param>
    /// <remarks>
    /// These were all registered as recorded — the presentation surface named but not
    /// performed — because there was no device and no decoder. There is both now, so they
    /// are registered over. A call whose sound cannot be found still returns cleanly: a
    /// missing footstep should not stop the script that stepped.
    /// </remarks>
    private static void Speak(Gk3SheepApi api, SceneAudio audio)
    {
        api.Register("PlaySound", arguments =>
        {
            if (arguments.Count > 0)
            {
                audio.Play(arguments[0].AsString());
            }

            return SheepValue.FromInt(0);
        });

        api.Register("StartVoiceOver", arguments =>
        {
            if (arguments.Count > 0)
            {
                audio.Speak(
                    arguments[0].AsString(),
                    arguments.Count > 1 ? arguments[1].AsInt() : 1);
            }

            return SheepValue.FromInt(0);
        });

        // What a conversation is actually said with. A topic's script does not call
        // StartVoiceOver — it calls into the location's compiled script, which calls these.
        // Left recorded, a topic runs, its camera cuts, the topic is used up, and nobody
        // says anything: the reported "the screen flashes but nothing happens".
        //
        // The fidget forms differ only in whether the speakers play their talking and
        // listening idles, which nothing here does yet, so the four are the same two calls.
        foreach (string start in new[] { "StartDialogue", "StartDialogueNoFidgets" })
        {
            api.Register(start, arguments =>
            {
                if (arguments.Count > 0)
                {
                    audio.Speak(
                        arguments[0].AsString(),
                        arguments.Count > 1 ? arguments[1].AsInt() : 1);
                }

                return SheepValue.FromInt(0);
            });
        }

        foreach (string more in new[] { "ContinueDialogue", "ContinueDialogueNoFidgets" })
        {
            api.Register(more, arguments =>
            {
                audio.Continue(arguments.Count > 0 ? arguments[0].AsInt() : 1);
                return SheepValue.FromInt(0);
            });
        }

        // A yak names one line outright where a voice-over names a run of them.
        api.Register("StartYak", arguments =>
        {
            if (arguments.Count > 0)
            {
                audio.Speak(arguments[0].AsString(), 1);
            }

            return SheepValue.FromInt(0);
        });

        api.Register("PlaySoundTrack", arguments =>
        {
            if (arguments.Count > 0)
            {
                audio.Loop(arguments[0].AsString());
            }

            return SheepValue.FromInt(0);
        });

        api.Register("StopSoundTrack", _ =>
        {
            audio.Loop(null);
            return SheepValue.FromInt(0);
        });

        // One of the two functions the corpus called that nothing answered.
        api.Register("StopAllSoundTracks", _ =>
        {
            audio.Loop(null);
            return SheepValue.FromInt(0);
        });

        // Stopping sounds. The effects bus is what a one-shot plays on, so silencing it is
        // what both of these mean; stopping one sound by name would need the device to
        // remember which voice was which, which it does not.
        api.Register("StopAllSounds", _ =>
        {
            audio.Quiet();
            return SheepValue.FromInt(0);
        });

        api.Register("StopSound", _ =>
        {
            audio.Quiet();
            return SheepValue.FromInt(0);
        });
    }

    /// <summary>
    /// Makes the walking calls move somebody.
    /// </summary>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room being crossed.</param>
    /// <param name="world">What moves them.</param>
    /// <remarks>
    /// <para>
    /// Both forms take a name and mean different things by it. <c>WalkTo</c> names a spot
    /// the scene declares — <c>TO_B25</c>, the patch of floor in front of the bathroom door
    /// — and <c>WalkToSee</c> names a <em>model</em>, meaning walk until you can see it.
    /// Seeing is not worked out yet, so it walks to the model instead, which is the same
    /// answer for anything in the open and too close for anything behind something else.
    /// </para>
    /// <para>
    /// These say how long they take through <see cref="Gk3SheepApi.SecondsFor"/>, so a
    /// waited walk holds up the line of dialogue that follows it rather than being spoken
    /// over on the way.
    /// </para>
    /// </remarks>
    private static void Walking(Gk3SheepApi api, LoadedScene scene, SceneUpdate world)
    {
        api.Walks = (actor, place, how, hurry) => Send(
            scene, world, actor, place, how != Approaching.Walk, how == Approaching.Turn,
            hurry);

        api.Register("WalkTo", a => SheepValue.FromInt(
            (int)Send(scene, world, Actor(api, a, 0), Name(a, 1), toModel: false)));

        api.Register("WalkToAnimation", a => SheepValue.FromInt(
            (int)Send(scene, world, Actor(api, a, 0), Name(a, 1), toModel: false)));

        api.Register("WalkToSeeModel", a => SheepValue.FromInt(
            (int)Send(scene, world, Actor(api, a, 0), Name(a, 1), toModel: true)));

        api.Register("TurnToModel", a => SheepValue.FromInt(
            (int)Send(scene, world, Actor(api, a, 0), Name(a, 1), toModel: true, turnOnly: true)));

        api.Register("TurnTo", a => SheepValue.FromInt(
            (int)Send(scene, world, Actor(api, a, 0), Name(a, 1), toModel: false, turnOnly: true)));

        api.WalksToAnimationStart = (actor, animation, hurry) =>
            ToAnimationStart(scene, world, actor, animation, hurry);

        api.Register("WalkerBoundaryBlockRegion", _ => SheepValue.FromInt(0));

        api.Register("StopWalking", _ =>
        {
            world.StopWalking();
            return SheepValue.FromInt(0);
        });
    }

    /// <summary>
    /// Starts a walk or a turn, and says how long it will take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arriving facing the right way is most of what a walk is for. A scene's named spots
    /// carry a heading — the way somebody standing there is meant to face — and walking to
    /// look at something means ending up looking at it. Without either, an actor arrives
    /// facing whichever way the last corner of the route pointed, which is usually a wall.
    /// </para>
    /// <para>
    /// A turn goes nowhere. 394 of the corpus's approaches are <c>TurnToModel</c>, and
    /// walking to the thing instead puts the actor on top of what they meant to look at.
    /// </para>
    /// </remarks>
    private static double Send(
        LoadedScene scene,
        SceneUpdate world,
        string actor,
        string place,
        bool toModel,
        bool turnOnly = false,
        bool hurry = false)
    {
        if (Aim(scene, place, toModel) is not { } aim)
        {
            return 0;
        }

        if (turnOnly)
        {
            return world.Turn(actor, aim.Look ?? aim.Destination);
        }

        // A named spot says which way to stand. A thing says to look at it — from wherever
        // the walk actually ends, which the boundary decides, not from where it was aimed.
        return world.Walk(actor, Approach(world, actor, aim), aim.Heading, aim.Look, hurry);
    }

    /// <summary>
    /// Walks an actor to the spot an animation expects them to start from.
    /// </summary>
    /// <remarks>
    /// Nothing happens if the animation moves nobody by that name, which is the right
    /// answer: an <c>approach=anim</c> naming a scenery animation has nobody to walk, and
    /// refusing to run the action because of it would lose the action as well as the walk.
    /// </remarks>
    private static double ToAnimationStart(
        LoadedScene scene,
        SceneUpdate world,
        string actor,
        string animation,
        bool hurry)
    {
        void Cannot(string wanted, string got) => world.Diagnostics.Add(new Diagnostic(
            "GK3R3320", DiagnosticSeverity.Info,
            "An approach names an animation nothing can be walked to the start of.",
            animation, null, wanted, got,
            "The action still runs; the actor simply plays it from where they stand."));

        if (world.Animations?.Read(animation) is not { } read)
        {
            Cannot("an .ANM of that name", "nothing");
            return 0;
        }

        if (world.Clips is not { } clips)
        {
            Cannot("a clip library", "none attached");
            return 0;
        }

        if (world.Characters?.Of(actor) is not { } character || character.Hips is null)
        {
            Cannot("hip axes in CHARACTERS.TXT for " + actor, "none");
            return 0;
        }

        // The model, not the actor. An actor answers to two names — gab and GABRIEL — and a
        // clip is filed under the model's, so the animation has to be searched for what the
        // scene actually placed rather than for what the action file called it.
        string model = scene.Models
            .FirstOrDefault(m =>
                string.Equals(m.Name, actor, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Noun, actor, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? actor;

        if (AnimationStart.Of(read, clips, model, character) is not { } start)
        {
            Cannot("a clip in it that poses " + model, "none of its " + read.Actions.Count);
            return 0;
        }

        return world.Walk(actor, start.Position, start.Heading, null, hurry);
    }

    /// <summary>Stops an actor short of the thing they were sent to.</summary>
    /// <remarks>
    /// The distance is the character's own <c>WalkerHeight</c> out of <c>CHARACTERS.TXT</c> —
    /// Gabriel is 76 units — which agrees with what the artists did where they placed an
    /// approach spot by hand: the few in the corpus that name both a thing and a position
    /// stand 68 to 184 units off it. See <see cref="Navigation.Walker.StandingOff"/> for why
    /// walking to the middle is not good enough.
    /// </remarks>
    private static Vector3 Approach(SceneUpdate world, string actor, Aiming aim)
    {
        if (aim.Look is not { } thing || world.Where(actor) is not { } from)
        {
            return aim.Destination;
        }

        float stand = world.Characters?.Of(actor)?.WalkerHeight is { } height && height > 0
            ? height
            : Navigation.Walker.StandOff;

        return Navigation.Walker.StandingOff(thing, from, stand);
    }

    /// <summary>Standing somebody at a named spot, without walking them there.</summary>
    /// <remarks>
    /// <para>
    /// <c>InitEgoPosition</c> is how a room decides where the player is standing when they
    /// arrive. A scene's <c>SCENE:ENTER</c> action asks <c>WasLastLocation</c> which door
    /// they came through and stands them at the matching spot — the hallway alone has one
    /// for the lobby stairs and one for each of the guest rooms. Left recorded, every
    /// arrival is wherever the scene's <c>[ACTORS]</c> section put them, which is the front
    /// door however you got in.
    /// </para>
    /// <para>
    /// It moves the camera as well, when the spot names one. That is the original's
    /// behaviour and it is the difference between arriving in a room and being teleported
    /// into it while the view stays where it was.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Makes the fidget calls do something.
    /// </summary>
    /// <param name="api">The host.</param>
    /// <param name="world">Where the characters are.</param>
    /// <param name="behaviours">Where a named script is read from.</param>
    /// <remarks>
    /// <para>
    /// A fidget is what a character does when nobody is telling them to do anything, and
    /// GK3 gives every one of them three: idling, talking and listening. All seven calls
    /// were recorded, which is a cast standing perfectly still through every conversation
    /// in the game.
    /// </para>
    /// <para>
    /// <c>SetIdleGAS</c> and its two relatives replace the script; <c>StartIdleFidget</c>
    /// and its relatives say which of the three to run whatever is happening; and
    /// <c>StopFidget</c> stands somebody still, which is what a script does before handing
    /// them something specific to do.
    /// </para>
    /// </remarks>
    private static void Fidgeting(
        Gk3SheepApi api, SceneUpdate world, Func<string, GasFile?> behaviours)
    {
        void Assign(IReadOnlyList<SheepValue> a, FidgetKind mode)
        {
            if (a.Count > 1)
            {
                world.SetBehaviour(a[0].AsString(), mode, behaviours(a[1].AsString()));
            }
        }

        api.Register("SetIdleGAS", a =>
        {
            Assign(a, FidgetKind.Idle);
            return SheepValue.FromInt(0);
        });

        api.Register("SetTalkGAS", a =>
        {
            Assign(a, FidgetKind.Talk);
            return SheepValue.FromInt(0);
        });

        api.Register("SetListenGAS", a =>
        {
            Assign(a, FidgetKind.Listen);
            return SheepValue.FromInt(0);
        });

        void Start(IReadOnlyList<SheepValue> a, FidgetKind mode)
        {
            if (a.Count > 0)
            {
                world.StartFidget(a[0].AsString(), mode);
            }
        }

        api.Register("StartIdleFidget", a =>
        {
            Start(a, FidgetKind.Idle);
            return SheepValue.FromInt(0);
        }, waitable: true);

        api.Register("StartTalkFidget", a =>
        {
            Start(a, FidgetKind.Talk);
            return SheepValue.FromInt(0);
        }, waitable: true);

        api.Register("StartListenFidget", a =>
        {
            Start(a, FidgetKind.Listen);
            return SheepValue.FromInt(0);
        }, waitable: true);

        api.Register("StopFidget", a =>
        {
            world.StopFidget(a.Count > 0 ? a[0].AsString() : null);
            return SheepValue.FromInt(0);
        });
    }

    /// <summary>
    /// Makes a scene's hidden staging appear and disappear.
    /// </summary>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room the models stand in.</param>
    /// <param name="world">Where they are drawn.</param>
    /// <remarks>
    /// <para>
    /// GK3 stages a moment by leaving the pieces of it in the room, declared <c>hidden</c>,
    /// and having the script show them when they are wanted. <c>ShowModel</c> and
    /// <c>HideModel</c> were recorded and not performed, which meant every such moment
    /// happened with its subject missing.
    /// </para>
    /// <para>
    /// RC1 at 102P is the case that was reported: on first leaving the hotel the scene
    /// shows <c>wmo</c>, plays the clip of it riding past, has Gabriel watch it and hides
    /// it again. With nothing shown, all the player got was Gabriel saying "A bike! Man, I
    /// need one of those" at an empty square — which reads as a line from some other part
    /// of the game playing by mistake.
    /// </para>
    /// </remarks>
    private static void Showing(Gk3SheepApi api, LoadedScene scene, SceneUpdate world)
    {
        void Set(IReadOnlyList<SheepValue> arguments, bool visible)
        {
            if (arguments.Count == 0)
            {
                return;
            }

            string named = arguments[0].AsString();

            if (world.ModelNamed(named) is not { } model)
            {
                api.Diagnostics.Add(new Diagnostic(
                    "GK3R3340", DiagnosticSeverity.Info,
                    "A script showed or hid a model this room does not place.",
                    scene.Name, null, "a model in the room", named,
                    "Common and usually harmless: scripts are shared between rooms."));

                return;
            }

            world.Show(model, visible);
        }

        api.Register("ShowModel", a =>
        {
            Set(a, visible: true);
            return SheepValue.FromInt(0);
        });

        api.Register("HideModel", a =>
        {
            Set(a, visible: false);
            return SheepValue.FromInt(0);
        });
    }

    private static void Stand(Gk3SheepApi api, LoadedScene scene, SceneUpdate world)
    {
        api.Register("InitEgoPosition", arguments =>
        {
            if (arguments.Count > 0)
            {
                At(api, scene, world, api.State.Ego, arguments[0].AsString(), moveCamera: true);
            }

            return SheepValue.FromInt(0);
        });

        api.Register("SetActorPosition", arguments =>
        {
            if (arguments.Count > 1)
            {
                At(api, scene, world,
                    arguments[0].AsString(), arguments[1].AsString(), moveCamera: false);
            }

            return SheepValue.FromInt(0);
        });
    }

    private static void At(
        Gk3SheepApi api,
        LoadedScene scene,
        SceneUpdate world,
        string actor,
        string spot,
        bool moveCamera)
    {
        if (scene.Definition.PositionNamed(spot) is not { } named)
        {
            api.Diagnostics.Add(new Diagnostic(
                "GK3R3320", DiagnosticSeverity.Warning,
                $"'{spot}' is not a position this scene names.",
                scene.Name, null, "a spot in the POSITIONS section", spot,
                "The actor stays where the scene put them."));

            return;
        }

        if (!world.Place(actor, named.Position, named.Heading))
        {
            api.Diagnostics.Add(new Diagnostic(
                "GK3R3321", DiagnosticSeverity.Info,
                "A script stood somebody somewhere who is not in the room.",
                scene.Name, null, "an actor the scene placed", actor,
                "Common when a room is entered as one character and scripted for another."));

            return;
        }

        if (moveCamera && named.Camera is { Length: > 0 } camera)
        {
            api.State.CameraGliding = false;
            api.State.CameraAngle = camera;
        }
    }

    /// <summary>Where a walking call points, and which way to face when it gets there.</summary>
    /// <param name="Destination">The spot on the floor to stand on.</param>
    /// <param name="Heading">The authored heading of a named spot, if it is one.</param>
    /// <param name="Look">What to look at, if the target is a thing rather than a spot.</param>
    private readonly record struct Aiming(Vector3 Destination, float? Heading, Vector3? Look);

    /// <summary>Where a walking call is pointing.</summary>
    /// <remarks>
    /// A named spot on the floor, or the middle of a thing. Tried in that order and
    /// regardless of which call asked, because the corpus is not perfectly consistent about
    /// which kind of name it hands to which function.
    /// </remarks>
    private static Aiming? Aim(LoadedScene scene, string place, bool toModel)
    {
        if (place.Length == 0)
        {
            return null;
        }

        if (!toModel && scene.Definition.PositionNamed(place) is { } spot)
        {
            return new Aiming(spot.Position, spot.Heading, null);
        }

        foreach (PlacedModel placed in scene.Models)
        {
            if (string.Equals(placed.Name, place, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(placed.Noun, place, StringComparison.OrdinalIgnoreCase))
            {
                Vector3 middle = Middle(placed);

                return new Aiming(middle, null, middle);
            }
        }

        if (scene.Definition.PositionNamed(place) is { } named)
        {
            return new Aiming(named.Position, named.Heading, null);
        }

        // Most of what a script points at is part of the room rather than something
        // standing in it — a door, a rack, a noticeboard.
        return scene.MiddleOf(place) is { } middleOf
            ? new Aiming(middleOf, null, middleOf)
            : null;
    }

    /// <summary>
    /// The actor a walking call is about.
    /// </summary>
    /// <remarks>
    /// The first argument when there is one, and whoever the player is otherwise. Scripts
    /// write both, and a walk with no name is always about ego.
    /// </remarks>
    private static string Actor(Gk3SheepApi api, IReadOnlyList<SheepValue> arguments, int index) =>
        index < arguments.Count && arguments[index].AsString() is { Length: > 0 } named
            ? named
            : api.State.Ego;

    private static string Name(IReadOnlyList<SheepValue> arguments, int index) =>
        index < arguments.Count ? arguments[index].AsString() : string.Empty;

    /// <summary>
    /// Makes the animation calls move something.
    /// </summary>
    /// <param name="api">The host.</param>
    /// <param name="world">What plays them.</param>
    /// <remarks>
    /// <para>
    /// The rigid part only. A clip's mesh transforms are applied and its vertex poses are
    /// not, which plays 2,188 of the corpus's 5,796 clips exactly right — a door, a drawer,
    /// a telephone — and plays a character as a set of mesh groups that move about without
    /// deforming. The second of those looks wrong and is still where the geometry goes.
    /// </para>
    /// <para>
    /// They say how long they take through <see cref="Gk3SheepApi.SecondsFor"/>, so a waited
    /// animation holds up what follows it rather than being talked over.
    /// </para>
    /// </remarks>
    private static void Animating(Gk3SheepApi api, SceneUpdate world)
    {
        api.Plays = (name, repeat) => world.Play(name, repeat);

        // What lets an action's approach finish before its script runs. The room is where
        // the clock is, so this is the room saying so; a tool leaves it null and every
        // action runs where it was asked for.
        api.Defers = world.After;

        api.Register("StartAnimation", a => SheepValue.FromInt(
            (int)world.Play(Name(a, 0))));

        // A move animation leaves the thing it moved where it ended; an ordinary one puts
        // it back. That distinction is GK3's and it is the whole difference between a
        // character who has walked somewhere and one who has mimed walking.
        api.Register("StartMoveAnimation", a => SheepValue.FromInt(
            (int)world.Play(Name(a, 0), repeat: false, moves: true)));

        api.Register("LoopAnimation", a => SheepValue.FromInt(
            (int)world.Play(Name(a, 0), repeat: true)));

        api.Register("StopAnimation", a =>
        {
            world.StopAnimating(Name(a, 0));
            return SheepValue.FromInt(0);
        });
    }

}

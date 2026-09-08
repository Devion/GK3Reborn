// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Tools.Stages;

/// <summary>What a scene file declared about the objects inside one room.</summary>
internal sealed class RoomRoles
{
    private readonly Dictionary<string, SortedSet<string>> _roles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An empty set, for a room no scene file names.</summary>
    public static RoomRoles None { get; } = new();

    /// <summary>Records a declaration.</summary>
    public void Add(string name, string role)
    {
        if (name.Length == 0)
        {
            return;
        }

        if (!_roles.TryGetValue(name, out SortedSet<string>? roles))
        {
            roles = new SortedSet<string>(StringComparer.Ordinal);
            _roles[name] = roles;
        }

        roles.Add(role);
    }

    /// <summary>Every role declared for an object.</summary>
    public IReadOnlyList<string> Of(string name) =>
        _roles.TryGetValue(name, out SortedSet<string>? roles) ? [.. roles] : [];

    /// <summary>Whether an object was declared with a role.</summary>
    public bool Has(string name, string role) =>
        _roles.TryGetValue(name, out SortedSet<string>? roles) && roles.Contains(role);
}

/// <summary>
/// Reads what the scene files say about the objects inside each room's geometry.
/// </summary>
internal sealed class SceneRoles
{
    private readonly Dictionary<string, RoomRoles> _rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads every scene file and attributes what it declares to a room.</summary>
    public static SceneRoles Read(GameArchives archives, Action<string> log)
    {
        var found = new SceneRoles();
        int declared = 0;

        foreach (string entry in archives.Names(".SIF"))
        {
            string name = Path.GetFileNameWithoutExtension(entry) ?? string.Empty;

            if (name.Length == 0 || archives.ReadText(entry) is not { } text)
            {
                continue;
            }

            SceneInitFile sif;

            try
            {
                sif = SceneInitFile.Parse(text, entry);
            }
            catch (FormatParseException)
            {
                continue;
            }

            string room = RoomOf(archives, sif, name);
            RoomRoles roles = found.Mutable(room);

            if (sif.FloorObject() is { Length: > 0 } floor)
            {
                roles.Add(floor, "floor");
                declared++;
            }

            foreach (string bounds in sif.CameraBounds())
            {
                roles.Add(bounds, "cameraBounds");
                declared++;
            }

            foreach (SceneModel model in sif.Models())
            {
                if (model.Type is { Length: > 0 } type)
                {
                    roles.Add(model.Name, type.ToLowerInvariant());
                    declared++;
                }

                if (model.Hidden)
                {
                    roles.Add(model.Name, "hidden");
                }
            }
        }

        log(string.Create(
            CultureInfo.InvariantCulture,
            $"scene files: {declared} declarations across {found._rooms.Count} rooms"));

        return found;
    }

    /// <summary>What a room's scene files declared.</summary>
    public RoomRoles For(string room) => _rooms.GetValueOrDefault(room) ?? RoomRoles.None;

    private RoomRoles Mutable(string room)
    {
        if (!_rooms.TryGetValue(room, out RoomRoles? roles))
        {
            roles = new RoomRoles();
            _rooms[room] = roles;
        }

        return roles;
    }

    /// <summary>Which geometry file a scene file's declarations are about.</summary>
    private static string RoomOf(GameArchives archives, SceneInitFile sif, string fallback)
    {
        if (sif.SceneAsset(includeConditional: true) is not { Length: > 0 } asset)
        {
            return fallback;
        }

        if (archives.ReadText(asset + ".SCN") is not { } text)
        {
            return asset;
        }

        try
        {
            return SceneAssetFile.Parse(text, asset + ".SCN").BspName ?? asset;
        }
        catch (FormatParseException)
        {
            return asset;
        }
    }
}

/// <summary>
/// Decides what should be done with one of a room's objects.
/// </summary>
internal static class Classifier
{
    /// <summary>How large an object may be, in world units, before it is architecture.</summary>
    public const float Room = 300f;

    /// <summary>Decides.</summary>
    public static (SceneObjectDisposition Disposition, string Reason) Decide(
        BspFile scene,
        int objectIndex,
        ObjectFacts facts,
        MaterialClasses classes,
        RoomRoles roles)
    {
        string name = scene.ObjectNames[objectIndex];
        string lowered = name.ToLowerInvariant();

        // 1. What the room itself says the surfaces are. A shadow decal is a translucent
        //    blob on the floor and a light fixture is a bulb: neither has a silhouette
        //    that beveling improves, and the fixture must keep its self-lit flag exactly.
        if (facts.AllShadow)
        {
            return (SceneObjectDisposition.Collision, "every surface is a shadow decal");
        }

        // 2. What a scene file declared. The only channel that is not inference.
        if (roles.Has(name, "hittest") || roles.Has(name, "noclick"))
        {
            return (SceneObjectDisposition.Collision, "declared as a hit test by a scene file");
        }

        if (roles.Has(name, "cameraBounds"))
        {
            return (SceneObjectDisposition.Collision, "declared as camera bounds by a scene file");
        }

        if (roles.Has(name, "floor"))
        {
            return (SceneObjectDisposition.Terrain,
                "declared as the room's floor; the engine cuts its relief at load");
        }

        // 3. What the geometry is, which overrules any name.
        if (facts.PlaneCount <= 1 || facts.TriangleCount <= 2)
        {
            return (SceneObjectDisposition.Flat,
                "a card: one plane, or too few triangles to enclose anything");
        }

        if (Matches(lowered, Foliage))
        {
            return (SceneObjectDisposition.Foliage, "foliage, which is replaced by grown trees");
        }

        if (Matches(lowered, Backdrops))
        {
            return (SceneObjectDisposition.Backdrop, "named as a painted view of somewhere else");
        }

        if (facts.Textures.Count > 0 && facts.Textures.All(t => classes.Of(t) == "foliage"))
        {
            return (SceneObjectDisposition.Foliage, "every texture is classed foliage");
        }

        if (facts.Textures.Count > 0 && facts.Textures.All(t => classes.Of(t) == "backdrop"))
        {
            return (SceneObjectDisposition.Backdrop, "every texture is classed backdrop");
        }

        if (facts.Size > Room)
        {
            return (SceneObjectDisposition.Architecture, string.Create(
                CultureInfo.InvariantCulture,
                $"{facts.Size:F0} units across is a piece of the building, not a thing in it"));
        }

        // 4. What the artist called it, now that size and shape have had their say. These
        //    are labels for parts of one room rather than filenames, which is why they are
        //    worth reading at all — see SceneObjectManifest.
        if (Matches(lowered, Vehicles))
        {
            return (SceneObjectDisposition.Vehicle, "named as a vehicle");
        }

        if (Matches(lowered, Rocks) ||
            (facts.Textures.Count > 0 && facts.Textures.All(t => classes.Of(t) is "stone" or "ground") &&
             facts.PlaneCount >= 8))
        {
            return (SceneObjectDisposition.Rock, string.Create(
                CultureInfo.InvariantCulture,
                $"rock: {facts.PlaneCount} plane orientations over {facts.TriangleCount} triangles"));
        }

        if (Matches(lowered, Ornaments))
        {
            return (SceneObjectDisposition.Ornament, "named as an ornament");
        }

        if (Matches(lowered, Furniture))
        {
            return (SceneObjectDisposition.Furniture, "named as furniture");
        }

        if (Matches(lowered, Architecture))
        {
            return (SceneObjectDisposition.Architecture, "named as part of the building");
        }

        // 5. Nothing named it, so the shape decides. An object turning through many
        //    orientations in few triangles is something lathed or carved; a handful of
        //    orientations is a box, and a box is furniture-shaped whatever it holds.
        if (facts.PlaneCount >= 12 && facts.Size <= Room / 2f)
        {
            return (SceneObjectDisposition.Ornament, string.Create(
                CultureInfo.InvariantCulture,
                $"unnamed but curved: {facts.PlaneCount} plane orientations at {facts.Size:F0} units"));
        }

        // A solid nobody named. Two thirds of what used to fall through to review is a
        // gravestone, a door frame, a tray or a table leg — small boxy things an
        // angle-limited bevel improves and cannot damage, because it touches only edges
        // that are already sharp and adds nothing across a flat panel. Treated as
        // furniture rather than as an ornament: the difference between the two is whether
        // the silhouette is subdivided, and that is not a guess worth making unnamed.
        if (facts.PlaneCount >= 3)
        {
            return (SceneObjectDisposition.Furniture, string.Create(
                CultureInfo.InvariantCulture,
                $"unnamed but solid: {facts.PlaneCount} plane orientations over " +
                $"{facts.TriangleCount} triangles at {facts.Size:F0} units"));
        }

        return (SceneObjectDisposition.Review, string.Create(
            CultureInfo.InvariantCulture,
            $"nothing decided it: {facts.PlaneCount} planes, {facts.TriangleCount} triangles, " +
            $"{facts.Size:F0} units"));
    }

    private static bool Matches(string name, string[] words) =>
        Array.Exists(words, w => name.Contains(w, StringComparison.Ordinal));

    /// <summary>Words that name a painted view of somewhere else.</summary>
    private static readonly string[] Backdrops =
        ["bkg", "backdrop", "background", "vista", "horizon", "skyline", "distant"];

    /// <summary>Words that name something already replaced by grown geometry.</summary>
    private static readonly string[] Foliage =
        ["tree", "bush", "leaves", "leaf", "pine", "hedge", "shrub", "foliage", "ivy"];

    /// <summary>Words that name something driven or ridden.</summary>
    private static readonly string[] Vehicles =
        ["moped", "vespa", "motorcycle", "wagon", "truck", "tractor"];

    /// <summary>Words that name something quarried.</summary>
    private static readonly string[] Rocks =
        ["rock", "boulder", "rubble", "cliff"];

    /// <summary>
    /// Words that name something made to be looked at.
    /// </summary>
    private static readonly string[] Ornaments =
    [
        "statue", "fountain", "vase", "urn", "lamp", "lantern", "chandil", "chandel",
        "candle", "bell", "sign", "plate", "bottle", "ornament", "painting", "sconce",
        "torc", "clock", "jug", "bowl", "dish", "pitcher", "goblet", "cross", "column",
        "pillar", "bust", "carv", "relief", "crest", "emblem", "finial", "knob",

        // Things turned on a lathe rather than made to be looked at, but wanting exactly
        // the same treatment and for the same reason: their whole shape is a circle drawn
        // with the six or eight flat faces 1999 could afford. Sorted as furniture at
        // first, which gave them one level of refinement where they need two, and left
        // the moped shop's barrels hexagonal.
        "barrel", "keg", "cask", "drum", "bucket", "pail", "tub", "vat", "churn",
        "cauldron", "kettle", "amphora", "planter", "wheel", "tyre", "tire",
    ];

    /// <summary>Words that name something stood on the floor and used.</summary>
    private static readonly string[] Furniture =
    [
        "chair", "table", "desk", "bench", "bed", "cab", "crate", "stool",
        "shelf", "dresser", "drawer", "wardrobe", "sofa", "couch", "counter", "rack",
        "cupboard", "chest", "trunk", "sterno", "podium", "lectern", "pew", "stall",
    ];

    /// <summary>Words that name a part of the building rather than a thing inside it.</summary>
    private static readonly string[] Architecture =
    [
        "wall", "floor", "ceiling", "cieling", "celing", "roof", "stair", "step", "door",
        "window", "arch", "beam", "balcon", "house", "bldg", "build", "tower", "fence",
        "street", "road", "curb", "platform", "rail", "gate", "portal", "niche", "hearth",
        "fireplace", "chimney", "sidewalk", "pavement", "bridge", "tunnel", "vault",
    ];
}

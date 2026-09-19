using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>What one horizon building is cut from.</summary>
/// <param name="Name">What the placement tables call it.</param>
/// <param name="Room">The room whose geometry it is, by its three-letter code.</param>
/// <param name="Objects">The room objects that make it up, by the names the BSP records.</param>
/// <param name="Front">
/// Which way the building faces where it stands in its own room, in degrees, measured the
/// way a scene file measures a heading. The model is turned so that its front ends up
/// toward negative Z, which is what a placement's yaw of nought then means.
/// </param>
public sealed record HorizonBuilding(string Name, string Room, string[] Objects, float Front);

/// <summary>
/// Cuts the buildings the horizon needs out of the rooms that already hold them.
///
/// <para>The reconstruction smooths a painted château into a hillside, and a splat stamp
/// cannot put one back: a stamp paints ground, and a building is not ground. Every
/// landmark the story asks the player to look at is, however, somewhere in the game as
/// real geometry — the Château de Serres is the room Gabriel breaks into, Tour Magdala is
/// the room Grace climbs — so the horizon is built out of the game's own low-poly
/// buildings rather than out of anything invented. They were modelled to be seen from a
/// street, which is a few hundred triangles each, which is exactly the budget a ridge a
/// kilometre away wants.</para>
///
/// <para>What comes out: a <c>.glb</c> per building, in metres, standing on the origin
/// with its front toward negative Z, ready for a placement to put it on the terrain.</para>
/// </summary>
public sealed class HorizonBuildingStage
{
    /// <summary>
    /// How many metres a room unit is.
    /// </summary>
    /// <remarks>
    /// Three independent measurements in the station yard put GK3's scale at 2.3–2.7 cm a
    /// unit, and the backdrop already works in the round number
    /// (<c>TerrainPlan.MetersPerUnit</c>). A building carried across at anything else
    /// would be the one thing on the hillside at the wrong size.
    /// </remarks>
    public const float MetersPerUnit = 0.025f;

    /// <summary>The kit, which is every building the horizon has a use for.</summary>
    public static IReadOnlyList<HorizonBuilding> Kit { get; } =
    [
        // The Château de Serres, seen across the valley from the north-east hexagram
        // point. CSE is its own exterior, so this is the building itself; the barn, the
        // garage and the van stay behind, being things nobody makes out from a kilometre.
        new("serres", "CSE",
            [
                "cse_mainhouse", "cse_tower", "cse_backwall", "cse_backwindows",
                "cse_frontwins", "cse_lowerwall", "cse_level3window", "cse_openwindow",
                "cse_roofclimbstart", "cse_frontdoorleft_scene", "cse_frontdoorright_scene",
            ],
            Front: 180f),

        // Rennes-le-Château's skyline as one piece, in the layout the artists gave it:
        // Tour Magdala on the end of the terrace, the Villa Bethania beside it and the
        // village behind. Cut together rather than one building at a time because what
        // the Site looks back at is the whole ridge, and their arrangement is the half of
        // it that reads at that distance.
        new("rennes-le-chateau", "MAG",
            [
                "mag_tourmagdela", "mag_tower", "mag_towerdoor", "mag_towerwindows",
                "mag_villabethania", "mag_houses", "mag_large_building", "mag_hotel",
                "mag_museum", "mag_wstuccobldg", "mag_smallbldgattached", "stonehouse1",
            ],
            Front: 0f),

        // And the tower on its own, for a vista that wants the landmark and not the town.
        new("tour-magdala", "MAG",
            ["mag_tourmagdela", "mag_tower", "mag_towerdoor", "mag_towerwindows"],
            Front: 0f),
    ];

    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public HorizonBuildingStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>
    /// Cuts the kit.
    /// </summary>
    /// <param name="sourceDirectory">The game's Data directory.</param>
    /// <param name="outputDirectory">Where the models go.</param>
    /// <param name="only">One building's name, or null for the whole kit.</param>
    /// <param name="diagnostics">Receives what went wrong.</param>
    /// <returns>True when every building asked for was cut.</returns>
    public bool Run(
        string sourceDirectory,
        string outputDirectory,
        string? only,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        Directory.CreateDirectory(outputDirectory);

        var rooms = new Dictionary<string, BspFile>(StringComparer.OrdinalIgnoreCase);
        int cut = 0;
        int wanted = 0;

        foreach (HorizonBuilding building in Kit)
        {
            if (only is { Length: > 0 } named &&
                !named.Equals(building.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            wanted++;

            if (Room(archives, rooms, building.Room, diagnostics) is not { } bsp ||
                Cut(bsp, building, diagnostics) is not { } model)
            {
                continue;
            }

            File.WriteAllBytes(
                Path.Combine(outputDirectory, building.Name + ".glb"),
                GlbWriter.Encode(model, "../textures/"));

            (Vector3 low, Vector3 high) = Bounds(model);

            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"{building.Name}: {model.TriangleCount} triangles out of {building.Room}, "
                + $"{high.X - low.X:F1} by {high.Z - low.Z:F1} m and {high.Y - low.Y:F1} m tall, "
                + $"{Textures(model).Count} texture(s)"));

            cut++;
        }

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"{cut} of {wanted} building(s) written to {outputDirectory}"));

        return wanted > 0 && cut == wanted;
    }

    /// <summary>Every texture a model is painted with, once each.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyCollection<string> Textures(ModFile model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var named = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModMesh mesh in model.Meshes)
        {
            foreach (ModSubmesh part in mesh.Submeshes)
            {
                if (part.TextureName is { Length: > 0 } texture)
                {
                    named.Add(texture);
                }
            }
        }

        return named;
    }

    /// <summary>Reads a room's geometry once, however many buildings come out of it.</summary>
    private static BspFile? Room(
        GameArchives archives,
        Dictionary<string, BspFile> rooms,
        string name,
        DiagnosticBag diagnostics)
    {
        if (rooms.TryGetValue(name, out BspFile? already))
        {
            return already;
        }

        if (archives.Read(name + ".BSP") is not { } bytes)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3470",
                DiagnosticSeverity.Error,
                "No archive holds this room's geometry, so nothing can be cut out of it.",
                name + ".BSP"));

            return null;
        }

        try
        {
            BspFile bsp = BspFile.Parse(bytes, name + ".BSP");

            rooms[name] = bsp;

            return bsp;
        }
        catch (FormatParseException broken)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3470", DiagnosticSeverity.Error, broken.Message, name + ".BSP"));

            return null;
        }
    }

    /// <summary>
    /// Takes a building's objects out of its room and stands them on the origin.
    /// </summary>
    /// <returns>The model, in metres, or null when the room names none of its objects.</returns>
    private static ModFile? Cut(BspFile bsp, HorizonBuilding building, DiagnosticBag diagnostics)
    {
        List<int> wanted = [];

        foreach (string name in building.Objects)
        {
            int index = Index(bsp, name);

            if (index < 0)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R3471",
                    DiagnosticSeverity.Warning,
                    "The room does not name this object, so the building is short of a piece.",
                    bsp.Name,
                    null,
                    "one of the room's objects",
                    name));

                continue;
            }

            wanted.Add(index);
        }

        if (wanted.Count == 0)
        {
            return null;
        }

        // Written and read straight back, because the encoder is the one piece of code
        // that turns a room's surfaces into a mesh with shared corners and creased
        // normals, and the reader is the one the game will use on the file this produces.
        ModFile room = GlbReader.Parse(
            SceneObjectGlb.EncodeRoom(bsp, wanted), building.Name);

        return Stand(room, building);
    }

    /// <summary>Where a room names an object, or -1.</summary>
    private static int Index(BspFile bsp, string name)
    {
        for (int i = 0; i < bsp.ObjectNames.Count; i++)
        {
            if (bsp.ObjectNames[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Puts a model on the origin, in metres, facing negative Z.
    /// </summary>
    /// <param name="room">The building, in the coordinates of the room it was cut from.</param>
    /// <param name="building">Which building, for the way it faces.</param>
    /// <returns>The model, baked rather than carried on a transform.</returns>
    /// <remarks>
    /// Baked into the corners rather than left on each mesh's <c>MeshToLocal</c>: the
    /// horizon flattens a model into one buffer and a placement is a position and a yaw,
    /// so a transform the file carries is one more thing to lose.
    /// </remarks>
    private static ModFile Stand(ModFile room, HorizonBuilding building)
    {
        (Vector3 low, Vector3 high) = Bounds(room);

        // The middle of its footprint, and the bottom of it: a placement names the ground
        // the building stands on, not the middle of its walls.
        var centre = new Vector3((low.X + high.X) / 2f, low.Y, (low.Z + high.Z) / 2f);

        Matrix4x4 stand =
            Matrix4x4.CreateTranslation(-centre)
            * Matrix4x4.CreateRotationY(float.DegreesToRadians(-building.Front))
            * Matrix4x4.CreateScale(MetersPerUnit);

        var meshes = new List<ModMesh>();

        foreach (ModMesh mesh in room.Meshes)
        {
            Matrix4x4 place = mesh.MeshToLocal * stand;
            var parts = new List<ModSubmesh>();

            foreach (ModSubmesh part in mesh.Submeshes)
            {
                var positions = new Vector3[part.Positions.Length];
                var normals = new Vector3[part.Normals.Length];

                for (int i = 0; i < positions.Length; i++)
                {
                    positions[i] = Vector3.Transform(part.Positions[i], place);
                }

                for (int i = 0; i < normals.Length; i++)
                {
                    normals[i] = Vector3.Normalize(Vector3.TransformNormal(part.Normals[i], place));
                }

                // The room encoder names a material TEXTURE#00104 so a triangle can be put
                // back on the surface it came off, and the reader takes a material name as
                // a texture name verbatim. Geometry leaving its room has no surfaces to go
                // back to, so the index comes off here: without it CSE's eleven bitmaps
                // read as a hundred and twenty-six, one a surface, and the horizon would
                // bind a sheet per triangle.
                parts.Add(part with
                {
                    TextureName = Painted(part.TextureName),
                    MaterialName = null,
                    Positions = positions,
                    Normals = normals,
                });
            }

            meshes.Add(mesh with
            {
                MeshToLocal = Matrix4x4.Identity,
                Submeshes = parts,
                BoundsMin = Vector3.Transform(mesh.BoundsMin, place),
                BoundsMax = Vector3.Transform(mesh.BoundsMax, place),
            });
        }

        return ModFile.FromMeshes(room.Name, meshes);
    }

    /// <summary>The bitmap behind a room material's name.</summary>
    /// <param name="material">The material name, which carries the surface it was on.</param>
    /// <returns>The texture name alone.</returns>
    private static string Painted(string material)
    {
        int at = material.LastIndexOf(SceneObjectGlb.SurfaceSeparator);

        return at > 0 ? material[..at] : material;
    }

    /// <summary>The box a model fills, in whatever coordinates it is in.</summary>
    private static (Vector3 Low, Vector3 High) Bounds(ModFile model)
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        foreach (ModMesh mesh in model.Meshes)
        {
            foreach (ModSubmesh part in mesh.Submeshes)
            {
                foreach (Vector3 corner in part.Positions)
                {
                    Vector3 at = Vector3.Transform(corner, mesh.MeshToLocal);

                    low = Vector3.Min(low, at);
                    high = Vector3.Max(high, at);
                }
            }
        }

        return low.X > high.X ? (Vector3.Zero, Vector3.Zero) : (low, high);
    }
}

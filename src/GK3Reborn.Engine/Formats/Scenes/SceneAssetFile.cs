using System.Numerics;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Formats.Scenes;

/// <summary>What kind of light a scene light is.</summary>
public enum AuthoredLightKind
{
    /// <summary>Radiates in every direction from a point.</summary>
    Point = 0,

    /// <summary>Radiates in a cone.</summary>
    Spot = 1,
}

/// <summary>
/// One light as the original artists placed it.
/// </summary>
/// <param name="Name">The light's name, from its section header.</param>
/// <param name="Kind">Point or spot.</param>
/// <param name="Position">Where it sits, in scene space.</param>
/// <param name="Direction">Which way it points; meaningful for spots.</param>
/// <param name="Color">Its colour, each channel in [0, 1].</param>
/// <param name="HotSpot">Half-angle of the cone's fully lit core, in radians.</param>
/// <param name="Falloff">Half-angle at which the cone reaches zero, in radians.</param>
/// <param name="AttenuationStart">Distance at which falloff with range begins.</param>
/// <param name="AttenuationEnd">Distance at which the light reaches zero.</param>
/// <param name="UsesAttenuation">Whether the attenuation range applies at all.</param>
/// <param name="CastsShadows">Whether the bake let this light cast shadows.</param>
/// <param name="Intensity">A multiplier on the colour.</param>
/// <param name="Radius">Radius of the emitter, which the bake used for soft shadows.</param>
public sealed record AuthoredLight(
    string Name,
    AuthoredLightKind Kind,
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float HotSpot,
    float Falloff,
    float AttenuationStart,
    float AttenuationEnd,
    bool UsesAttenuation,
    bool CastsShadows,
    float Intensity,
    float Radius);

/// <summary>
/// Reader for scene assets: the geometry, skybox, model list and lights of a scene at one
/// time of day.
/// </summary>
/// <remarks>
/// <para>
/// This file answers the question ADR 0002 left open. The plan assumed the original
/// lighting existed only as baked lightmaps and that light positions would have to be
/// inferred from them. They do not: every scene asset carries the full rig the artists
/// authored — position, direction, colour, cone angles, attenuation range, intensity,
/// emitter radius and whether the light cast shadows in the bake. R25 at night alone
/// declares 48 of them.
/// </para>
/// <para>
/// That is a far better starting point for a modern re-light than anything derived from
/// lightmap texels. The derived rigs remain useful as a cross-check — a light in the file
/// that leaves no trace in the bake was disabled or occluded — but the file is the source
/// of truth.
/// </para>
/// <para>
/// Units need care. Cone angles are stored in radians and attenuation distances in scene
/// units, but intensity and colour were tuned for a 1999 renderer with no exposure
/// control, so they are hints rather than physical quantities.
/// </para>
/// </remarks>
public sealed class SceneAssetFile
{
    private const string LightPrefix = "Light_";

    private SceneAssetFile(
        IniDocument document,
        string? bspName,
        IReadOnlyList<string> models,
        IReadOnlyList<AuthoredLight> lights,
        SkyboxDefinition? skybox)
    {
        Document = document;
        BspName = bspName;
        Models = models;
        Lights = lights;
        Skybox = skybox;
    }

    /// <summary>Name this file was read under.</summary>
    public string Name => Document.Name;

    /// <summary>The underlying document.</summary>
    public IniDocument Document { get; }

    /// <summary>Which BSP holds this scene's geometry.</summary>
    public string? BspName { get; }

    /// <summary>Models the scene expects to be present.</summary>
    public IReadOnlyList<string> Models { get; }

    /// <summary>The lights the artists placed.</summary>
    public IReadOnlyList<AuthoredLight> Lights { get; }

    /// <summary>The skybox, if the scene has one.</summary>
    public SkyboxDefinition? Skybox { get; }

    /// <summary>Parses a scene asset.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The parsed scene asset.</returns>
    public static SceneAssetFile Parse(string text, string name = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);

        // Scene assets keep one key/value pair per line and write vectors bare, so the
        // commas in them belong to the value.
        IniDocument document = IniDocument.Parse(text, name, multipleEntriesPerLine: false);

        string? bsp = document.Sections
            .SelectMany(s => s.Lines)
            .Select(l => l.Value("BSP"))
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        // The model list is written as name=1, so the model is the key.
        List<string> models = document.LinesOf("Models", includeConditional: true)
            .Select(l => l.Head.Key)
            .Where(n => n.Length > 0)
            .ToList();

        List<AuthoredLight> lights = [];
        foreach (IniSection section in document.SectionsStartingWith(LightPrefix))
        {
            if (ReadLight(section) is { } light)
            {
                lights.Add(light);
            }
        }

        return new SceneAssetFile(document, bsp, models, lights, ReadSkybox(document));
    }

    private static AuthoredLight? ReadLight(IniSection section)
    {
        Vector3? position = null;
        Vector3? direction = null;
        Vector3? color = null;
        int kind = 0;
        float hotSpot = 0, falloff = 0, attenuationStart = 0, attenuationEnd = 0;
        float intensity = 1, radius = 1;
        bool usesAttenuation = false, castsShadows = false;

        foreach (IniLine line in section.Lines)
        {
            IniEntry entry = line.Head;

            switch (entry.Key.ToUpperInvariant())
            {
                case "TYPE":
                    kind = entry.AsInteger() ?? 0;
                    break;
                case "POSITION":
                    position = Read3(entry);
                    break;
                case "DIRECTION":
                    direction = Read3(entry);
                    break;
                case "COLOR":
                    color = Read3(entry);
                    break;
                case "HOTSPOT":
                    hotSpot = entry.AsNumber() ?? 0;
                    break;
                case "FALLOFF":
                    falloff = entry.AsNumber() ?? 0;
                    break;
                case "ATTENSTART":
                    attenuationStart = entry.AsNumber() ?? 0;
                    break;
                case "ATTENEND":
                    attenuationEnd = entry.AsNumber() ?? 0;
                    break;
                case "USEATTEN":
                    usesAttenuation = (entry.AsInteger() ?? 0) != 0;
                    break;
                case "CASTSHADOWS":
                    castsShadows = (entry.AsInteger() ?? 0) != 0;
                    break;
                case "INTENSITY":
                    intensity = entry.AsNumber() ?? 1;
                    break;
                case "RADIUS":
                    radius = entry.AsNumber() ?? 1;
                    break;
                default:
                    break;
            }
        }

        if (position is not { } origin)
        {
            return null;
        }

        return new AuthoredLight(
            section.Name[LightPrefix.Length..],
            kind == 1 ? AuthoredLightKind.Spot : AuthoredLightKind.Point,
            origin,
            direction is { } d && d.LengthSquared() > 1e-9f ? Vector3.Normalize(d) : -Vector3.UnitY,
            color ?? Vector3.One,
            hotSpot,
            falloff,
            attenuationStart,
            attenuationEnd,
            usesAttenuation,
            castsShadows,
            intensity,
            radius);
    }

    private static Vector3? Read3(IniEntry entry) =>
        entry.AsNumbers(3) is { } v ? new Vector3(v[0], v[1], v[2]) : null;

    private static SkyboxDefinition? ReadSkybox(IniDocument document)
    {
        IniLine[] lines = document.LinesOf("Skybox", includeConditional: true).ToArray();
        if (lines.Length == 0)
        {
            return null;
        }

        string? Face(string side) => lines
            .Select(l => l.Value(side))
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        float? azimuth = lines.Select(l => l.Number("Azimuth")).FirstOrDefault(v => v is not null);

        var skybox = new SkyboxDefinition(
            Face("Left"), Face("Right"), Face("Front"), Face("Back"),
            Face("Up"), Face("Down"),
            azimuth is { } degrees ? float.DegreesToRadians(degrees) : 0f);

        return skybox.IsEmpty ? null : skybox;
    }
}

/// <summary>The six faces of a scene's sky, and how it is rotated.</summary>
/// <param name="Left">Texture for the left face.</param>
/// <param name="Right">Texture for the right face.</param>
/// <param name="Front">Texture for the front face.</param>
/// <param name="Back">Texture for the back face.</param>
/// <param name="Up">Texture for the top face.</param>
/// <param name="Down">Texture for the bottom face.</param>
/// <param name="Azimuth">Rotation about the up axis, in radians.</param>
/// <remarks>
/// Most scenes name only the faces the player can actually see from the fixed camera
/// positions, and comment the rest out, so missing faces are normal rather than an error.
/// </remarks>
public sealed record SkyboxDefinition(
    string? Left, string? Right, string? Front, string? Back, string? Up, string? Down, float Azimuth)
{
    /// <summary>Whether no face is defined.</summary>
    public bool IsEmpty =>
        Left is null && Right is null && Front is null && Back is null && Up is null && Down is null;
}

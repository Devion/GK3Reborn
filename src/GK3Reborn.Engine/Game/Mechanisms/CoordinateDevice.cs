using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// One reading off the handheld GPS: a place on its map, and a latitude and longitude.
/// </summary>
/// <param name="Map">The picture of the whole device, as the archives name it.</param>
/// <param name="Across">Where the player is on the map, in the picture's own pixels.</param>
/// <param name="Down">And how far down it.</param>
/// <param name="Latitude">The reading, already written out.</param>
/// <param name="Longitude">The other one.</param>
/// <param name="Reading">Where the two readings are lettered, in the same pixels.</param>
public readonly record struct GpsReading(
    string Map,
    float Across,
    float Down,
    string Latitude,
    string Longitude,
    (float X, float Latitude, float Longitude) Reading);

/// <summary>
/// The coordinate-fixing device Grace carries on the third day.
/// </summary>
public sealed class CoordinateDevice : SceneMechanism
{
    /// <summary>What the file says about one location.</summary>
    private sealed record Mapped(
        string Map,
        float NorthOffset,
        float WorldWidth,
        float OriginAcross,
        float OriginDown,
        Vector2 Known,
        (int Degrees, int Minutes, int Seconds) Latitude,
        (int Degrees, int Minutes, int Seconds) Longitude);

    /// <summary>
    /// Where the parts of the device sit on the picture of it.
    /// </summary>
    private sealed record Layout(
        string Suffix, float Corner, float CornerDown, float MapWidth, float Text,
        float Latitude, float Longitude);

    private readonly Dictionary<string, Mapped> _places =
        new(StringComparer.OrdinalIgnoreCase);

    private Layout _layout = new("_L", 11, 10, 205, 78, 255, 229);

    private Mapped? _here;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public CoordinateDevice(SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
    }

    /// <inheritdoc/>
    public override string Name => "CoordinateDevice";

    /// <summary>Whether the device is switched on.</summary>
    public bool On { get; private set; }

    /// <summary>Where the file is read from, when there is anything to read it out of.</summary>
    public GameArchives? Archives { get; init; }

    /// <inheritdoc/>
    public override void Begin()
    {
        if (Archives?.ReadText("GPS.TXT") is { } text)
        {
            Read(text);
        }

        _here = _places.GetValueOrDefault(Story.Location) ??
            _places.Values.FirstOrDefault();

        // Not carried between rooms. Walking out of the cave mouth with the device still
        // up would draw the wrong room's map over the next one, and every one of the six
        // calls that turns it off is a script doing so on the way out of somewhere.
        On = false;
    }

    /// <inheritdoc/>
    public override string Report() =>
        $"{_places.Count} mapped location(s), " +
        (_here is { } place ? $"showing {place.Map}" : "none for this room");

    /// <inheritdoc/>
    public override bool Perform(string asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (asked.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            On = true;

            return true;
        }

        if (asked.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            On = false;

            return true;
        }

        return false;
    }

    /// <summary>
    /// What the device is showing, or null when it is off or has nothing to show.
    /// </summary>
    public GpsReading? Reading()
    {
        if (!On || _here is not { } place ||
            World.Where(Story.Ego) is not { } standing)
        {
            return null;
        }

        Vector2 on = Pixels(place, standing);

        // Where the reading is taken from: the one point on the map whose latitude and
        // longitude the file writes down. Everything else is an offset from it.
        Vector2 from = Pixels(place, new Vector3(place.Known.X, 0, place.Known.Y));
        Vector2 away = on - from;

        // A hundred pixels of map is a known number of inches of world, and a degree of
        // latitude is a known number of metres. Wikipedia's figures, as the reference used.
        float metres = place.WorldWidth * 0.0254f;
        Vector2 offset = away * metres;

        return new GpsReading(
            place.Map + _layout.Suffix + ".BMP",
            _layout.Corner + (on.X * _layout.MapWidth),
            _layout.CornerDown + (on.Y * _layout.MapWidth),
            Written(place.Latitude, offset.Y / MetresPerLatitudeSecond),
            Written(place.Longitude, offset.X / MetresPerLongitudeSecond),
            (_layout.Text, _layout.Latitude, _layout.Longitude));
    }

    /// <summary>How far a second of latitude is on the ground, in metres.</summary>
    private const float MetresPerLatitudeSecond = 30.715f;

    /// <summary>And of longitude, at this latitude.</summary>
    private const float MetresPerLongitudeSecond = 30.92f;

    /// <summary>
    /// Where a point in the room falls on the map, as a fraction of the image.
    /// </summary>
    private static Vector2 Pixels(Mapped place, Vector3 world)
    {
        var flat = new Vector2(world.X, -world.Z) / MathF.Max(place.WorldWidth, 1f);

        float turn = -place.NorthOffset * MathF.PI / 180f;
        float cos = MathF.Cos(turn);
        float sin = MathF.Sin(turn);

        return new Vector2(
            place.OriginAcross + ((flat.X * cos) - (flat.Y * sin)),
            place.OriginDown + ((flat.X * sin) + (flat.Y * cos)));
    }

    /// <summary>A reading, offset from the known one by so many seconds.</summary>
    private static string Written(
        (int Degrees, int Minutes, int Seconds) known, float seconds)
    {
        int minutes = (int)(seconds / 60);
        float left = seconds - (minutes * 60f);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{known.Degrees:00}°{known.Minutes + minutes:00}'{known.Seconds + left:00.00}\"");
    }

    /// <summary>Reads the file's locations; its three layout sections are not wanted.</summary>
    private void Read(string text)
    {
        foreach (IniSection section in IniDocument.Parse(text, "GPS.TXT").Sections)
        {
            if (section.Name.Equals("large", StringComparison.OrdinalIgnoreCase))
            {
                _layout = Sized(section);

                continue;
            }

            if (section.Name is "medium" or "small" || section.Name.Length == 0)
            {
                continue;
            }

            string? map = null;
            float north = 0;
            float width = 1;
            float across = 0.5f;
            float down = 0.5f;
            float knownX = 0;
            float knownZ = 0;
            int[] latitude = [0, 0, 0];
            int[] longitude = [0, 0, 0];

            foreach (IniLine line in section.Lines)
            {
                if (line.Head is not { Key: { Length: > 0 } key, Value: { Length: > 0 } value })
                {
                    continue;
                }

                float number = Number(value);

                switch (key.ToUpperInvariant())
                {
                    case "NAME": map = value; break;
                    case "ANGXTON": north = number; break;
                    case "WORLDWIDTH": width = number; break;
                    case "MAPORIGINXPCT": across = number; break;
                    case "MAPORIGINYPCT": down = number; break;
                    case "SIGX": knownX = number; break;
                    case "SIGZ": knownZ = number; break;
                    case "SIGLATDEG": latitude[0] = (int)number; break;
                    case "SIGLATMIN": latitude[1] = (int)number; break;
                    case "SIGLATSEC": latitude[2] = (int)number; break;
                    case "SIGLNGDEG": longitude[0] = (int)number; break;
                    case "SIGLNGMIN": longitude[1] = (int)number; break;
                    case "SIGLNGSEC": longitude[2] = (int)number; break;
                    default: break;
                }
            }

            if (map is { Length: > 0 })
            {
                _places[section.Name] = new Mapped(
                    map,
                    north,
                    width,
                    across,
                    down,
                    new Vector2(knownX, knownZ),
                    (latitude[0], latitude[1], latitude[2]),
                    (longitude[0], longitude[1], longitude[2]));
            }
        }
    }

    /// <summary>Reads one of the file's three layouts.</summary>
    private static Layout Sized(IniSection section)
    {
        string suffix = "_L";
        float corner = 11;
        float cornerDown = 10;
        float map = 205;
        float text = 78;
        float latitude = 255;
        float longitude = 229;

        foreach (IniLine line in section.Lines)
        {
            if (line.Head is not { Key: { Length: > 0 } key, Value: { Length: > 0 } value })
            {
                continue;
            }

            switch (key.ToUpperInvariant())
            {
                case "NAMEEXT": suffix = value.Trim(); break;
                case "CORNERWIDTH": corner = Number(value); break;
                case "CORNERHEIGHT": cornerDown = Number(value); break;
                case "MAPWIDTH": map = Number(value); break;
                case "TEXTSTARTWIDTH": text = Number(value); break;
                case "LATHEIGHT": latitude = Number(value); break;
                case "LNGHEIGHT": longitude = Number(value); break;
                default: break;
            }
        }

        return new Layout(suffix, corner, cornerDown, map, text, latitude, longitude);
    }

    /// <summary>
    /// A number off a line, with the file's own trailing comment thrown away.
    /// </summary>
    private static float Number(string value)
    {
        int comment = value.IndexOf("//", StringComparison.Ordinal);
        ReadOnlySpan<char> figure = (comment >= 0 ? value[..comment] : value).AsSpan().Trim();

        return float.TryParse(
            figure, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
            ? number
            : 0;
    }
}

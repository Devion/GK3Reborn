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
/// <remarks>
/// <b>Everything is in the device picture's own pixels.</b> The picture is the whole
/// handheld — bezel, screen, and the words <c>LNG:</c> and <c>LAT:</c> printed on it — so
/// the cross and the two numbers have to land in the places the artists left for them.
/// Whatever draws it scales the lot by one number and they stay together at any size.
/// </remarks>
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
/// <remarks>
/// <para>
/// A handheld GPS. Switched on it puts a small map of wherever she is standing in the
/// corner of the screen, with a cross showing where on it she is and a latitude and
/// longitude that count up and down as she walks — which is the whole of how the player
/// finds the cave at Le Serpent Rouge's coordinates, and the reason it exists.
/// </para>
/// <para>
/// <b>It is not a Sheep call.</b> Three scenes reach it through
/// <c>CallSceneFunction("on")</c> and <c>("off")</c> and nothing else in the game does,
/// which is why it is here with the puzzles rather than beside the other screens.
/// </para>
/// <para>
/// <b>Everything about the mapping is in <c>GPS.TXT</c>.</b> One section per location —
/// <c>mcf</c>, <c>ler</c>, <c>bec</c> — giving where the room's origin falls on the map
/// image as a fraction of it, how wide a slice of the world the image covers (in inches),
/// how far the room's +X axis is rotated off north, and one <em>point of significance</em>
/// whose latitude and longitude are written down. Every reading is that point plus an
/// offset. Adapted from G-Engine's <c>GPSOverlay</c> under GPL-3, attributed in NOTICE;
/// its author notes the readings are close rather than exact, and that they land the
/// player in the right spot, which is what the puzzle needs of them.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <c>GPS.TXT</c> gives three of these, one per screen size the original supported, and
    /// the largest is the one worth having: this interface draws the picture at whatever
    /// fraction of the window suits and scales these with it, so the 640-pixel and
    /// 800-pixel variants have nothing to offer.
    /// </remarks>
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
    /// <remarks>
    /// Handed over by the launcher rather than opened here: a mechanism is built for rooms
    /// that have no archives at all — every tool builds one — and a device with no file
    /// draws nothing and breaks nothing.
    /// </remarks>
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
    /// <remarks>
    /// Read once a frame by whatever draws it. Everything in it is derived from where the
    /// player is standing at the moment it is asked, so there is no state to keep in step.
    /// </remarks>
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
    /// <remarks>
    /// <b>Fractions rather than pixels.</b> The reference works in the pixels of whichever
    /// of the three map sizes it picked for the window; this interface draws the map at
    /// whatever size the window affords, so the answer has to be independent of that. The
    /// arithmetic is the same otherwise: flatten to the ground plane, negate Z because the
    /// image counts down and the world counts up, scale by how much world the image covers,
    /// and turn it by however far the room's axes are off north.
    /// </remarks>
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
    /// <remarks>
    /// The layouts say which of three sizes of the same art to use for a 640, an 800 or a
    /// 1024-pixel screen, and pick a bitmap font to letter it with. Neither survives the
    /// port: the map is drawn at a fraction of the window and lettered in the interface's
    /// own face, so there is one size of everything and it fits any display.
    /// </remarks>
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
    /// <remarks>
    /// Nearly every line in this file carries one — <c>angXtoN = 0 // in degrees</c> — and
    /// the reader hands over what follows the equals sign whole.
    /// </remarks>
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

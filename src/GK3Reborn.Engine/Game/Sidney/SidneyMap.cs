// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Numerics;

namespace GK3Reborn.Game.Sidney;

/// <summary>A shape the analysis can lay over the map.</summary>
public enum MapShape
{
    /// <summary>None chosen.</summary>
    None,

    /// <summary>A circle.</summary>
    Circle,

    /// <summary>A square.</summary>
    Square,

    /// <summary>A six-pointed star.</summary>
    Hexagram,

    /// <summary>A triangle.</summary>
    Triangle,
}

/// <summary>What the points the player entered turned out to be.</summary>
public enum MapFinding
{
    /// <summary>Nothing can be made of them.</summary>
    Indeterminate,

    /// <summary>Too few to say anything.</summary>
    TooFew,

    /// <summary>They lie on a straight line.</summary>
    Line,

    /// <summary>Four of them make a rectangle.</summary>
    Rectangle,

    /// <summary>Four of them lie on a circle.</summary>
    Circle,

    /// <summary>Several arrangements fit and none stands out.</summary>
    Several,
}

/// <summary>What an analysis of the entered points came to.</summary>
/// <param name="Finding">What it made of them.</param>
/// <param name="Centre">The middle of the circle, where it found one.</param>
/// <param name="Radius">Its radius in map pixels.</param>
public sealed record MapAnalysis(MapFinding Finding, Vector2 Centre = default, float Radius = 0);

/// <summary>
/// Sidney's map: entering points on it and finding what they make.
/// </summary>
/// <remarks>
/// <para>
/// The map is <c>SIDNEYBIGMAP.BMP</c>, a labelled survey of the Rennes-le-Château country
/// with the Paris meridian drawn down it, and the puzzle is the one the books are about:
/// mark the churches and the ruins, and see that they fall on a line, a rectangle, or a
/// circle. <c>ESIDNEY.TXT</c> carries every answer the machine can give — that a line meets
/// the meridian at Arques, that four points are "linked perfectly by a circle" — and this
/// decides which of them applies.
/// </para>
/// <para>
/// <b>The geometry is measured, not scripted.</b> The original could have checked the
/// player's points against a hardcoded list and printed the matching note; doing it by
/// fitting means a player who marks four other points that genuinely lie on a circle is
/// told so, and one who marks the right places sloppily still is. The tolerances are in
/// map pixels and generous, because the player is clicking a village on a picture.
/// </para>
/// <para>
/// <b>Coordinates are approximate and say so.</b> The circle note quotes the coordinates of
/// its centre, and Sidney's map carries no georeference anywhere in the game's data — the
/// <c>GPS.TXT</c> entries belong to the handheld device in three outdoor scenes, not to
/// this. What is used here is a linear fit anchored on the meridian the map draws and on
/// the region's own extent, which puts a click within about a minute of arc. Good enough to
/// read out; not good enough to navigate by, and the doc says so.
/// </para>
/// </remarks>
public sealed class SidneyMap
{
    /// <summary>The map picture, in the archives.</summary>
    public const string Picture = "SIDNEYBIGMAP";

    /// <summary>How wide the map picture is, in its own pixels.</summary>
    public const int Extent = 1368;

    /// <summary>How close to a straight line points have to be, in map pixels.</summary>
    private const float LineTolerance = 14f;

    /// <summary>How close to a common circle four points have to be.</summary>
    private const float CircleTolerance = 18f;

    /// <summary>How close to a right angle a rectangle's corners have to be, in degrees.</summary>
    private const float SquareTolerance = 8f;

    /// <summary>How close to a laid shape a marked place has to be, in map pixels.</summary>
    /// <remarks>
    /// Wider than the circle's, because a shape is laid by eye over places the player
    /// clicked by eye, and the note it unlocks is a confirmation rather than a measurement.
    /// </remarks>
    private const float ShapeTolerance = 26f;

    /// <summary>
    /// Where the map's longitude and latitude are anchored.
    /// </summary>
    /// <remarks>
    /// The Paris meridian — 2 degrees 20 minutes 14 seconds east — is drawn down the map and
    /// labelled, which is one exact reference. The other is the region's own extent: the
    /// survey runs from about Couiza in the north-west to Bugarach in the south-east. Both
    /// are stated here rather than buried so that anybody who measures them properly can
    /// correct them in one place.
    /// </remarks>
    private const double MeridianLongitude = 2.0 + (20.0 / 60.0) + (14.0 / 3600.0);

    /// <summary>Where the meridian falls across the map, as a fraction of its width.</summary>
    private const double MeridianAcross = 0.655;

    /// <summary>How much ground the map covers east to west, in degrees of longitude.</summary>
    private const double SpanLongitude = 0.28;

    /// <summary>The latitude at the top edge.</summary>
    private const double TopLatitude = 42.985;

    /// <summary>How much ground it covers north to south.</summary>
    private const double SpanLatitude = 0.185;

    private readonly List<Vector2> _points = [];

    /// <summary>The points the player has entered, in map pixels.</summary>
    public IReadOnlyList<Vector2> Points => _points;

    /// <summary>The shape laid over the map, if any.</summary>
    public MapShape Shape { get; private set; }

    /// <summary>Where the shape sits, in map pixels.</summary>
    public Vector2 ShapeAt { get; private set; }

    /// <summary>How big it is: the radius of the circle it is drawn inside.</summary>
    public float ShapeSize { get; private set; }

    /// <summary>How far it has been turned, in degrees.</summary>
    public float ShapeTurn { get; private set; }

    /// <summary>Whether the marked places sit on the shape.</summary>
    public bool Locked { get; private set; }

    /// <summary>How many cells the grid is divided into each way, or zero for none.</summary>
    public int Grid { get; private set; }

    /// <summary>The last analysis, or null.</summary>
    public MapAnalysis? Found { get; private set; }

    /// <summary>Whether anything has been entered at all.</summary>
    public bool Any => _points.Count > 0 || Shape != MapShape.None || Grid > 0;

    /// <summary>The shapes as the game's own text names them.</summary>
    public static string NameOf(MapShape shape) => shape switch
    {
        MapShape.Circle => "Circle",
        MapShape.Square => "Square",
        MapShape.Hexagram => "Hexagram",
        MapShape.Triangle => "Triangle",
        _ => "None",
    };

    /// <summary>Marks a point on the map.</summary>
    /// <param name="at">Where, in map pixels.</param>
    /// <returns>True when it was taken.</returns>
    /// <remarks>
    /// A dozen at most. Past that the analysis is fitting noise, and the puzzle has never
    /// wanted more than four.
    /// </remarks>
    public bool Enter(Vector2 at)
    {
        if (_points.Count >= 12)
        {
            return false;
        }

        _points.Add(at);
        Found = null;

        return true;
    }

    /// <summary>Takes every point off the map.</summary>
    public void ClearPoints()
    {
        _points.Clear();
        Found = null;
    }

    /// <summary>Lays a grid over the map.</summary>
    /// <param name="cells">How many cells each way — 2, 4, 8, 12 or 16.</param>
    public void DrawGrid(int cells) => Grid = Math.Clamp(cells, 0, 64);

    /// <summary>Takes the grid off again.</summary>
    public void EraseGrid() => Grid = 0;

    /// <summary>
    /// Lays a shape over the map, fitted to whatever has been marked.
    /// </summary>
    /// <param name="shape">Which shape.</param>
    /// <remarks>
    /// Fitted rather than dropped in the middle at some arbitrary size. The player has
    /// marked the places they think matter; the question the screen exists to answer is
    /// whether a circle — or a square, or a hexagram — passes through them, and making them
    /// drag it into position first is asking them to do the analysis by hand.
    /// </remarks>
    public void UseShape(MapShape shape)
    {
        Shape = shape;
        ShapeTurn = 0;

        if (_points.Count == 0)
        {
            // Nothing to fit to: the middle of the map, big enough to see.
            ShapeAt = new Vector2(Extent / 2f, Extent / 2f);
            ShapeSize = Extent * 0.3f;
            Locked = false;

            return;
        }

        Vector2 middle = Vector2.Zero;

        foreach (Vector2 point in _points)
        {
            middle += point;
        }

        middle /= _points.Count;

        // A circle through three marked places is exact; anything else is centred on them
        // and sized to reach the furthest.
        if (shape == MapShape.Circle && _points.Count >= 3 &&
            Circumcircle(_points[0], _points[1], _points[2], out Vector2 centre, out float radius))
        {
            ShapeAt = centre;
            ShapeSize = radius;
        }
        else
        {
            float furthest = 0;

            foreach (Vector2 point in _points)
            {
                furthest = MathF.Max(furthest, Vector2.Distance(point, middle));
            }

            ShapeAt = middle;
            ShapeSize = MathF.Max(furthest, 40f);

            // A shape with corners is turned so one of them meets the first marked place,
            // which is what somebody laying a template on a map does before anything else.
            if (_points.Count > 0)
            {
                Vector2 toFirst = _points[0] - middle;

                if (toFirst.LengthSquared() > 1e-3f)
                {
                    ShapeTurn = MathF.Atan2(toFirst.Y, toFirst.X) * 180f / MathF.PI;
                }
            }
        }

        Locked = Fits();
    }

    /// <summary>Turns the shape.</summary>
    /// <param name="degrees">How far, clockwise.</param>
    /// <returns>Whether it now sits on the marked places.</returns>
    public bool Rotate(float degrees)
    {
        if (Shape == MapShape.None)
        {
            return false;
        }

        ShapeTurn = (ShapeTurn + degrees) % 360f;
        Locked = Fits();

        return Locked;
    }

    /// <summary>Takes the shape off again.</summary>
    public void EraseShape()
    {
        Shape = MapShape.None;
        Locked = false;
        ShapeSize = 0;
    }

    /// <summary>
    /// Whether every marked place sits on the shape as it is placed.
    /// </summary>
    /// <returns>True when the shape is locked down by the marks.</returns>
    /// <remarks>
    /// "Select points to lock down feature", as the game puts it. A shape that merely lies
    /// near the marks is not confirmation of anything; one that passes through all of them
    /// is the whole point of laying it there.
    /// </remarks>
    public bool Fits()
    {
        if (Shape == MapShape.None || _points.Count == 0 || ShapeSize <= 0)
        {
            return false;
        }

        foreach (Vector2 point in _points)
        {
            if (Away(point) > ShapeTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How far a place is from the shape's outline, in map pixels.</summary>
    private float Away(Vector2 point)
    {
        if (Shape == MapShape.Circle)
        {
            return MathF.Abs(Vector2.Distance(point, ShapeAt) - ShapeSize);
        }

        // Everything else is a ring of corners, and a place is measured against the nearest
        // of the sides between them.
        Vector2[] corners = Corners();
        float nearest = float.MaxValue;

        for (int i = 0; i < corners.Length; i++)
        {
            nearest = MathF.Min(nearest, ToSegment(point, corners[i], corners[(i + 1) % corners.Length]));
        }

        return nearest;
    }

    /// <summary>
    /// The shape's corners, in map pixels and in order round it.
    /// </summary>
    /// <returns>The corners; empty for a circle, which has none.</returns>
    /// <remarks>
    /// A hexagram is drawn as its two overlapping triangles rather than as a twelve-pointed
    /// outline, because that is what the analysis of Poussin's painting describes finding —
    /// one triangle, then a second forming the star — and the sides a place has to lie on
    /// are those triangles' sides.
    /// </remarks>
    public Vector2[] Corners()
    {
        int sides = Shape switch
        {
            MapShape.Square => 4,
            MapShape.Triangle => 3,
            MapShape.Hexagram => 6,
            _ => 0,
        };

        if (sides == 0)
        {
            return [];
        }

        var corners = new Vector2[sides];
        float turn = ShapeTurn * MathF.PI / 180f;

        for (int i = 0; i < sides; i++)
        {
            float angle = turn + (i * MathF.Tau / sides);

            corners[i] = ShapeAt + new Vector2(
                MathF.Cos(angle) * ShapeSize, MathF.Sin(angle) * ShapeSize);
        }

        return corners;
    }

    /// <summary>The two triangles a hexagram is drawn as, or nothing.</summary>
    /// <returns>Each triangle's three corners.</returns>
    public IReadOnlyList<Vector2[]> Triangles()
    {
        if (Shape != MapShape.Hexagram)
        {
            return [];
        }

        Vector2[] points = Corners();

        return [[points[0], points[2], points[4]], [points[1], points[3], points[5]]];
    }

    /// <summary>How far a point is from a line segment.</summary>
    private static float ToSegment(Vector2 point, Vector2 from, Vector2 to)
    {
        Vector2 along = to - from;
        float length = along.LengthSquared();

        if (length < 1e-6f)
        {
            return Vector2.Distance(point, from);
        }

        float t = Math.Clamp(Vector2.Dot(point - from, along) / length, 0f, 1f);

        return Vector2.Distance(point, from + (along * t));
    }

    /// <summary>
    /// Works out what the entered points make.
    /// </summary>
    /// <returns>The finding.</returns>
    public MapAnalysis Analyse()
    {
        Found = Measure(_points);

        return Found;
    }

    /// <summary>The finding for a set of points, without keeping it.</summary>
    /// <param name="points">The points, in map pixels.</param>
    /// <returns>What they make.</returns>
    public static MapAnalysis Measure(IReadOnlyList<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 2)
        {
            return new MapAnalysis(MapFinding.TooFew);
        }

        if (points.Count == 2)
        {
            // Two points are always a line, which is what the original says about them:
            // they can be joined, and nothing else suggests the join means anything.
            return new MapAnalysis(MapFinding.Line);
        }

        if (Collinear(points))
        {
            return new MapAnalysis(MapFinding.Line);
        }

        if (points.Count == 4)
        {
            // The rectangle is tested first, and it has to be: every rectangle's corners
            // lie on a circle — that is what a circumcircle is — so asking about the circle
            // first answers "circle" for all of them and the four-to-one rectangle the
            // story is also looking for could never be found.
            if (Rectangular(points))
            {
                return new MapAnalysis(MapFinding.Rectangle);
            }

            if (Circular(points, out Vector2 centre, out float radius))
            {
                return new MapAnalysis(MapFinding.Circle, centre, radius);
            }
        }

        // More than four points that are not on a line: several things fit and the machine
        // says so rather than picking one.
        return points.Count > 4
            ? new MapAnalysis(MapFinding.Several)
            : new MapAnalysis(MapFinding.Indeterminate);
    }

    /// <summary>Whether every point lies close enough to one straight line.</summary>
    private static bool Collinear(IReadOnlyList<Vector2> points)
    {
        Vector2 first = points[0];
        Vector2 last = points[^1];
        Vector2 along = last - first;
        float length = along.Length();

        if (length < 1e-3f)
        {
            return false;
        }

        for (int i = 1; i < points.Count - 1; i++)
        {
            Vector2 offset = points[i] - first;

            // The distance from the line, which is the cross product over its length.
            float away = MathF.Abs((along.X * offset.Y) - (along.Y * offset.X)) / length;

            if (away > LineTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether four points lie on one circle, and where its middle is.
    /// </summary>
    /// <remarks>
    /// The circle through the first three is found exactly — the intersection of two
    /// perpendicular bisectors — and the fourth is then measured against it. Fitting all
    /// four at once would answer "how nearly" rather than "whether", and the note this
    /// unlocks says <em>perfectly</em>.
    /// </remarks>
    private static bool Circular(IReadOnlyList<Vector2> points, out Vector2 centre, out float radius)
    {
        if (!Circumcircle(points[0], points[1], points[2], out centre, out radius))
        {
            return false;
        }

        return MathF.Abs(Vector2.Distance(centre, points[3]) - radius) <= CircleTolerance;
    }

    /// <summary>The circle through three points, or false where there is none.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second.</param>
    /// <param name="c">The third.</param>
    /// <param name="centre">Where the circle's middle is.</param>
    /// <param name="radius">How big it is.</param>
    /// <returns>False when the three are in a row, or on top of each other.</returns>
    private static bool Circumcircle(
        Vector2 a, Vector2 b, Vector2 c, out Vector2 centre, out float radius)
    {
        centre = default;
        radius = 0;

        float d = 2 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y)));

        if (MathF.Abs(d) < 1e-4f)
        {
            return false;
        }

        float aa = a.LengthSquared();
        float bb = b.LengthSquared();
        float cc = c.LengthSquared();

        centre = new Vector2(
            ((aa * (b.Y - c.Y)) + (bb * (c.Y - a.Y)) + (cc * (a.Y - b.Y))) / d,
            ((aa * (c.X - b.X)) + (bb * (a.X - c.X)) + (cc * (b.X - a.X))) / d);

        radius = Vector2.Distance(centre, a);

        return radius >= 1f;
    }

    /// <summary>Whether four points make a rectangle, in whatever order they were given.</summary>
    private static bool Rectangular(IReadOnlyList<Vector2> points)
    {
        // Around the hull rather than in the order they were clicked: the player marks
        // villages, not corners in sequence.
        Vector2 middle = (points[0] + points[1] + points[2] + points[3]) / 4;

        List<Vector2> ordered = [.. points];
        ordered.Sort((p, q) =>
            MathF.Atan2(p.Y - middle.Y, p.X - middle.X)
                .CompareTo(MathF.Atan2(q.Y - middle.Y, q.X - middle.X)));

        for (int i = 0; i < 4; i++)
        {
            Vector2 before = ordered[(i + 3) % 4] - ordered[i];
            Vector2 after = ordered[(i + 1) % 4] - ordered[i];

            if (before.Length() < 1f || after.Length() < 1f)
            {
                return false;
            }

            float angle = MathF.Acos(
                Math.Clamp(
                    Vector2.Dot(Vector2.Normalize(before), Vector2.Normalize(after)), -1f, 1f))
                * 180f / MathF.PI;

            if (MathF.Abs(angle - 90f) > SquareTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A point on the map, written the way the machine writes one.
    /// </summary>
    /// <param name="at">Where, in map pixels.</param>
    /// <returns>Degrees, minutes and seconds of longitude and latitude.</returns>
    /// <remarks>
    /// The format is the game's own <c>MapLatLongText</c>: longitude first, then latitude,
    /// each in degrees, minutes and seconds. See the note on the class about how approximate
    /// the anchoring is.
    /// </remarks>
    public static string Coordinates(Vector2 at)
    {
        double longitude = MeridianLongitude + (((at.X / Extent) - MeridianAcross) * SpanLongitude);
        double latitude = TopLatitude - ((at.Y / Extent) * SpanLatitude);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Sexagesimal(longitude)} long  {Sexagesimal(latitude)} lat");
    }

    /// <summary>Degrees, minutes and seconds, as the game writes them.</summary>
    private static string Sexagesimal(double degrees)
    {
        int whole = (int)degrees;
        double rest = (degrees - whole) * 60;
        int minutes = (int)rest;
        int seconds = (int)Math.Round((rest - minutes) * 60);

        if (seconds == 60)
        {
            seconds = 0;
            minutes++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{whole} deg {minutes}'{seconds}\"");
    }
}

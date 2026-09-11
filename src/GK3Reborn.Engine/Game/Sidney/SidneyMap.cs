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

    /// <summary>
    /// A straight line through the places marked for it.
    /// </summary>
    Line,
}

/// <summary>
/// One figure laid over the country, where it sits and whether it is confirmed.
/// </summary>
/// <param name="Shape">Which figure.</param>
/// <param name="At">Where its middle is, in map pixels.</param>
/// <param name="Size">The radius of the circle it is drawn inside.</param>
/// <param name="Turn">How far it has been turned, in degrees.</param>
/// <param name="Locked">Whether every marked place sits on its outline.</param>
/// <param name="Points">
/// The places it was fitted to, which are its own.
/// </param>
public sealed record LaidShape(
    MapShape Shape,
    Vector2 At,
    float Size,
    float Turn,
    bool Locked,
    IReadOnlyList<Vector2> Points)
{
    /// <summary>
    /// Whether the figure is settled for good: a step of Le Serpent Rouge the machine has
    /// confirmed, drawn where the answer is and beyond picking up, turning or erasing. The
    /// retail engine keeps these apart as "locked" figures; here they stay in the one list
    /// and simply stop answering to anything but the eye. See <see cref="SerpentRougeAnalysis"/>.
    /// </summary>
    public bool Fixed { get; init; }
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
    private const float ShapeTolerance = 26f;

    /// <summary>
    /// Where the map's longitude and latitude are anchored.
    /// </summary>
    private const double MeridianLongitude = 2.0 + (20.0 / 60.0) + (14.0 / 3600.0);

    /// <summary>Where the meridian falls across the map, as a fraction of its width.</summary>
    private const double MeridianAcross = 0.655;

    /// <summary>
    /// Where Arques sits, in map pixels.
    /// </summary>
    public static readonly Vector2 Arques = new(1262f, 165f);

    /// <summary>
    /// How many places a figure is made of.
    /// </summary>
    /// <param name="shape">Which figure.</param>
    /// <returns>The count, or nought where the figure takes no places.</returns>
    public static int Needs(MapShape shape) => shape switch
    {
        MapShape.Line => 2,
        MapShape.Triangle => 3,
        MapShape.Circle => 4,
        MapShape.Square => 4,
        MapShape.Hexagram => 6,
        _ => 0,
    };

    /// <summary>
    /// The places the survey itself marks with a red cross, in map pixels.
    /// </summary>
    public static readonly (string Name, Vector2 At)[] Sites =
    [
        ("Rennes-le-Château", new Vector2(266f, 416f)),
        ("St-Just-et-le-Bezu", new Vector2(301f, 983f)),
        ("Bugarach", new Vector2(990f, 1041f)),
        ("Coustaussa", new Vector2(404f, 273f)),
        ("Montazels", new Vector2(142f, 227f)),
    ];

    /// <summary>Rennes-le-Château, where the sunrise line starts.</summary>
    public static Vector2 Church => Sites[0].At;

    /// <summary>
    /// The ruin of the Château de Blanchefort, where the sunrise line is drawn to. The
    /// retail engine's own spot for it, measured from the foot of its map and turned over.
    /// </summary>
    public static readonly Vector2 Blanchefort = new(652f, 307f);

    /// <summary>
    /// How near a line has to pass to a named place to be said to go through it.
    /// </summary>
    private const float PlaceTolerance = 40f;

    /// <summary>How much ground the map covers east to west, in degrees of longitude.</summary>
    private const double SpanLongitude = 0.28;

    /// <summary>The latitude at the top edge.</summary>
    private const double TopLatitude = 42.985;

    /// <summary>How much ground it covers north to south.</summary>
    private const double SpanLatitude = 0.185;

    private readonly List<Vector2> _points = [];
    private readonly List<LaidShape> _laid = [];

    /// <summary>The points the player has entered, in map pixels.</summary>
    public IReadOnlyList<Vector2> Points => _points;

    /// <summary>
    /// Every figure laid over the country, in the order they were laid.
    /// </summary>
    public IReadOnlyList<LaidShape> Laid => _laid;

    /// <summary>
    /// The figure the player is working on: the one most recently laid that is not settled
    /// for good. Minus one when there is none.
    /// </summary>
    private int Current
    {
        get
        {
            for (int i = _laid.Count - 1; i >= 0; i--)
            {
                if (!_laid[i].Fixed)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>The figure being worked on, or null.</summary>
    public LaidShape? Working
    {
        get
        {
            int at = Current;

            return at >= 0 ? _laid[at] : null;
        }
    }

    /// <summary>The figures settled for good, in the order they were settled.</summary>
    public IEnumerable<LaidShape> Fixed => _laid.Where(laid => laid.Fixed);

    /// <summary>The figure most recently laid, if any.</summary>
    public MapShape Shape => Working?.Shape ?? MapShape.None;

    /// <summary>Where it sits, in map pixels.</summary>
    public Vector2 ShapeAt => Working?.At ?? Vector2.Zero;

    /// <summary>How big it is: the radius of the circle it is drawn inside.</summary>
    public float ShapeSize => Working?.Size ?? 0f;

    /// <summary>How far it has been turned, in degrees.</summary>
    public float ShapeTurn => Working?.Turn ?? 0f;

    /// <summary>Whether the marked places sit on it.</summary>
    public bool Locked => Working?.Locked ?? false;

    /// <summary>How many cells the grid is divided into each way, or zero for none.</summary>
    public int Grid { get; private set; }

    /// <summary>
    /// Whether the grid is the chessboard the Gemini and Cancer verses ask for, ruled inside
    /// the square and settled for good.
    /// </summary>
    public bool GridFixed { get; private set; }

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
        MapShape.Line => "Line",
        _ => "None",
    };

    /// <summary>Marks a point on the map.</summary>
    /// <param name="at">Where, in map pixels.</param>
    /// <returns>True when it was taken.</returns>
    public bool Enter(Vector2 at)
    {
        // A figure placed by construction rather than fitted to marks — the square round
        // the circle, the hexagram inside it — takes no marks of its own: a place marked
        // while it is in hand is a place on the map, as the meridian line's two are.
        bool constructed = Working is { Points.Count: 0, Locked: true };

        if (Selected != MapShape.None && !constructed)
        {
            if (Complete)
            {
                return false;
            }

            _points.Add(at);

            // The figure follows the places as they land, so it is never a step behind what
            // the player can see.
            UseShape(Selected);

            return true;
        }

        if (_points.Count >= 12)
        {
            return false;
        }

        _points.Add(at);
        Found = null;

        return true;
    }

    /// <summary>
    /// Takes back the place marked last.
    /// </summary>
    /// <returns>True when there was one to take back.</returns>
    public bool Undo()
    {
        if (_points.Count == 0)
        {
            return false;
        }

        _points.RemoveAt(_points.Count - 1);
        Found = null;

        return true;
    }

    /// <summary>
    /// Moves a place already marked.
    /// </summary>
    /// <param name="figure">Which figure it belongs to, or minus one for the working set.</param>
    /// <param name="which">Which of that figure's places, from nought.</param>
    /// <param name="to">Where it goes, in map pixels.</param>
    /// <returns>True when it moved.</returns>
    public bool MovePoint(int figure, int which, Vector2 to)
    {
        var at = new Vector2(Math.Clamp(to.X, 0, Extent), Math.Clamp(to.Y, 0, Extent));

        if (figure < 0)
        {
            if (which < 0 || which >= _points.Count)
            {
                return false;
            }

            _points[which] = at;
            Found = null;

            return true;
        }

        if (figure >= _laid.Count || which < 0 || which >= _laid[figure].Points.Count ||
            _laid[figure].Fixed)
        {
            return false;
        }

        // A figure's own place moved re-fits that figure and nothing else, which is the
        // whole reason a figure keeps its own.
        List<Vector2> own = [.. _laid[figure].Points];

        own[which] = at;
        _laid[figure] = Place(_laid[figure].Shape, own);

        return true;
    }

    /// <summary>Takes every point off the map.</summary>
    public void ClearPoints()
    {
        _points.Clear();
        Found = null;
    }

    /// <summary>
    /// Lays a grid over the map, or over the figure laid on it.
    /// </summary>
    /// <param name="cells">How many cells each way — 2, 4, 8, 12 or 16.</param>
    /// <param name="inShape">Whether to rule inside the figure rather than the whole map.</param>
    public void DrawGrid(int cells, bool inShape = false)
    {
        if (GridFixed)
        {
            return;
        }

        Grid = Math.Clamp(cells, 0, 64);
        GridInShape = inShape && Grid > 0;
    }

    /// <summary>Settles the grid for good, as the chessboard.</summary>
    public void FixGrid()
    {
        if (Grid > 0)
        {
            GridFixed = true;
        }
    }

    /// <summary>Whether the grid is ruled inside the figure rather than over the whole map.</summary>
    public bool GridInShape { get; private set; }

    /// <summary>Takes the grid off again.</summary>
    /// <returns>True when there was one to take off; the chessboard, once settled, stays.</returns>
    public bool EraseGrid()
    {
        if (GridFixed || Grid == 0)
        {
            return false;
        }

        Grid = 0;
        GridInShape = false;

        return true;
    }

    /// <summary>
    /// Lays a shape over the map, fitted to whatever has been marked.
    /// </summary>
    /// <param name="shape">Which shape.</param>
    public void UseShape(MapShape shape)
    {
        if (shape == MapShape.None)
        {
            return;
        }

        LaidShape placed = Place(shape, _points);

        // Laying a figure that is already there re-fits it rather than stacking a second
        // copy on the first, and brings it to the front so that the rotate turns it. A
        // figure settled for good is not "already there" in that sense: the sunrise line is
        // fixed and a second line may still be drawn.
        _laid.RemoveAll(already => already.Shape == shape && !already.Fixed);
        _laid.Add(placed);

        Found = null;
    }

    /// <summary>
    /// Chooses which figure the places being marked belong to.
    /// </summary>
    /// <param name="shape">The figure, or none to mark places belonging to nothing.</param>
    public void Select(MapShape shape)
    {
        Selected = shape;

        foreach (LaidShape laid in _laid)
        {
            if (laid.Shape == shape && !laid.Fixed)
            {
                // Already drawn: its places come back to be edited. A figure with none
                // leaves the marks on the map alone; they were never its.
                if (laid.Points.Count > 0)
                {
                    _points.Clear();
                    _points.AddRange(laid.Points);
                }

                Found = null;

                return;
            }
        }

        // Not drawn yet, so whatever is already marked becomes this figure's — but only
        // as many as it is made of. Adopting the lot gave a triangle four places and a
        // hexagram five, each fitted to whatever happened to be lying about, and the map
        // filled up with figures answering to nothing.
        // Choosing nothing leaves the marks as they are: they belong to nobody yet, which
        // is what they were before. Trimming to "nought places" here threw every mark away
        // whenever ANALYZE put the figure in hand down.
        int needs = Needs(shape);

        if (shape != MapShape.None && _points.Count > needs)
        {
            _points.RemoveRange(needs, _points.Count - needs);
        }

        if (_points.Count > 0)
        {
            UseShape(shape);
        }

        Found = null;
    }

    /// <summary>Which figure the places being marked belong to.</summary>
    public MapShape Selected { get; private set; }

    /// <summary>Whether the figure being marked has all the places it needs.</summary>
    public bool Complete =>
        Selected != MapShape.None && _points.Count >= Needs(Selected);

    /// <summary>Re-fits every figure, after the marks under them have changed.</summary>
    public void Refit()
    {
        for (int i = 0; i < _laid.Count; i++)
        {
            // Settled figures do not move, and a figure with no places of its own — the
            // square round the circle — has nothing to re-fit to and would only lose the
            // turn the player has given it.
            if (_laid[i].Fixed || _laid[i].Points.Count == 0)
            {
                continue;
            }

            _laid[i] = Place(_laid[i].Shape, _laid[i].Points);
        }
    }

    /// <summary>
    /// Settles a figure for good, where the answer is.
    /// </summary>
    /// <param name="figure">The figure, placed; it is kept as confirmed and fixed.</param>
    public void Fix(LaidShape figure)
    {
        ArgumentNullException.ThrowIfNull(figure);

        _laid.Add(figure with { Locked = true, Fixed = true });
        Found = null;
    }

    /// <summary>Replaces the figure being worked on.</summary>
    /// <param name="figure">What it becomes.</param>
    /// <returns>True when there was one.</returns>
    public bool Rework(LaidShape figure)
    {
        ArgumentNullException.ThrowIfNull(figure);

        int at = Current;

        if (at < 0)
        {
            return false;
        }

        _laid[at] = figure;

        return true;
    }

    /// <summary>
    /// A marked place near a spot, whether it is the working set's or an unsettled
    /// figure's, and takes it off the map.
    /// </summary>
    /// <remarks>
    /// The retail engine's <c>GetPlacedPointNearPoint</c>, twenty pixels, and the removal
    /// that follows every successful match: a place recognised becomes the answer's own.
    /// </remarks>
    /// <param name="spot">Where, in map pixels.</param>
    /// <param name="within">How near counts.</param>
    /// <returns>True when one was there.</returns>
    public bool TakeNear(Vector2 spot, float within = NearEnough)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            if (Vector2.Distance(_points[i], spot) < within)
            {
                _points.RemoveAt(i);
                Found = null;

                return true;
            }
        }

        for (int f = 0; f < _laid.Count; f++)
        {
            if (_laid[f].Fixed)
            {
                continue;
            }

            for (int i = 0; i < _laid[f].Points.Count; i++)
            {
                if (Vector2.Distance(_laid[f].Points[i], spot) < within)
                {
                    List<Vector2> own = [.. _laid[f].Points];

                    own.RemoveAt(i);
                    _laid[f] = own.Count == 0 && _laid[f].Shape == MapShape.Line
                        ? _laid[f] with { Points = own }
                        : Place(_laid[f].Shape, own);

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a marked place is near a spot, without taking it.</summary>
    /// <param name="spot">Where, in map pixels.</param>
    /// <param name="within">How near counts.</param>
    /// <returns>True when one is.</returns>
    public bool HasNear(Vector2 spot, float within = NearEnough) =>
        _points.Any(p => Vector2.Distance(p, spot) < within) ||
        _laid.Any(l => !l.Fixed && l.Points.Any(p => Vector2.Distance(p, spot) < within));

    /// <summary>Whether a settled figure has one of its places at a spot.</summary>
    /// <param name="spot">Where, in map pixels.</param>
    /// <returns>True when one has.</returns>
    public bool HasFixedNear(Vector2 spot) =>
        _laid.Any(l => l.Fixed && l.Points.Any(p => Vector2.Distance(p, spot) < NearEnough));

    /// <summary>How near a marked place has to be to a spot to be taken for it, in map pixels.</summary>
    public const float NearEnough = 20f;

    /// <summary>Where a figure sits once it is fitted to the marks.</summary>
    /// <summary>
    /// Where a figure sits once it is fitted to a set of places.
    /// </summary>
    /// <param name="shape">Which figure.</param>
    /// <param name="places">The places it answers to, which become its own.</param>
    /// <returns>The figure, placed.</returns>
    private LaidShape Place(MapShape shape, IReadOnlyList<Vector2> places)
    {
        List<Vector2> own = [.. places];

        if (own.Count == 0)
        {
            // A square with nothing of its own goes round the circle already laid, which is
            // the step the puzzle actually asks for: "fit exactly on the outer edge of the
            // previous circle". A hexagram with nothing of its own goes inside it, a point
            // to the north, which is where the retail engine puts a fresh one before the
            // player turns it. Failing either, the middle of the map, big enough to see.
            foreach (LaidShape already in _laid)
            {
                if (shape == MapShape.Square && already.Shape == MapShape.Circle)
                {
                    return new LaidShape(
                        shape,
                        already.At,
                        already.Size * MathF.Sqrt(2f),
                        45f,
                        Locked: true,
                        own);
                }

                if (shape == MapShape.Hexagram && already.Shape == MapShape.Circle && already.Fixed)
                {
                    return new LaidShape(shape, already.At, already.Size, 270f, Locked: true, own);
                }
            }

            return new LaidShape(
                shape, new Vector2(Extent / 2f, Extent / 2f), Extent * 0.3f, 0f, false, own);
        }

        Vector2 middle = Vector2.Zero;

        foreach (Vector2 point in own)
        {
            middle += point;
        }

        middle /= own.Count;

        Vector2 at;
        float size;
        float turn = 0f;

        // A line is the two ends of what was marked, and is drawn on past them; its middle
        // and half-length are only where the machine keeps it.
        if (shape == MapShape.Line)
        {
            Vector2 from = own[0];
            Vector2 to = own[^1];
            Vector2 along = to - from;

            at = middle;
            size = MathF.Max(along.Length() / 2, 1f);
            turn = along.LengthSquared() > 1e-3f
                ? MathF.Atan2(along.Y, along.X) * 180f / MathF.PI
                : 0f;
        }
        else if (shape == MapShape.Circle && own.Count >= 3 &&
            FitCircle(own, out Vector2 centre, out float radius))
        {
            // <b>Every place, not the first three of them.</b> Taking three left a player
            // who had marked five wondering why the circle sailed off the top of the map
            // ignoring the two at the bottom.
            at = centre;
            size = radius;
        }
        else
        {
            float furthest = 0;

            foreach (Vector2 point in own)
            {
                furthest = MathF.Max(furthest, Vector2.Distance(point, middle));
            }

            at = middle;
            size = MathF.Max(furthest, 40f);

            // A shape with corners is turned so one of them meets the first marked place,
            // which is what somebody laying a template on a map does before anything else.
            Vector2 toFirst = own[0] - middle;

            if (toFirst.LengthSquared() > 1e-3f)
            {
                turn = MathF.Atan2(toFirst.Y, toFirst.X) * 180f / MathF.PI;
            }
        }

        var fitted = new LaidShape(shape, at, size, turn, false, own);

        return fitted with { Locked = Fits(fitted) };
    }

    /// <summary>Turns the shape.</summary>
    /// <param name="degrees">How far, clockwise.</param>
    /// <returns>Whether it now sits on the marked places.</returns>
    public bool Rotate(float degrees)
    {
        int at = Current;

        if (at < 0)
        {
            return false;
        }

        LaidShape turned = _laid[at] with { Turn = ((_laid[at].Turn + degrees) % 360f + 360f) % 360f };

        // A figure with no places of its own keeps whatever confirmation it had: the square
        // round the circle is confirmed by the circle, not by marks, and turning it must not
        // take that away.
        _laid[at] = turned with { Locked = turned.Points.Count > 0 ? Fits(turned) : turned.Locked };

        return _laid[at].Locked;
    }

    /// <summary>Takes the figure being worked on off again.</summary>
    public void EraseShape()
    {
        int at = Current;

        if (at >= 0)
        {
            _laid.RemoveAt(at);
        }
    }

    /// <summary>
    /// Puts a saved map back: its marks, its figures and its grid.
    /// </summary>
    /// <param name="marks">The places, as "x,y" in map pixels.</param>
    /// <param name="figures">The figures, each with where it sits.</param>
    /// <param name="grid">How many cells the ruling is divided into.</param>
    public void Restore(
        IEnumerable<Vector2> marks, IEnumerable<LaidShape> figures, int grid)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(figures);

        _points.Clear();
        _laid.Clear();
        Found = null;
        Grid = Math.Clamp(Math.Abs(grid), 0, 64);
        GridInShape = grid < 0;

        _points.AddRange(marks);

        foreach (LaidShape figure in figures)
        {
            // A settled figure is taken on trust: it is the answer, and its places are
            // where the answer put them. Anything else is confirmed again from its marks,
            // save a figure with none, which keeps what the save said.
            _laid.Add(figure.Fixed
                ? figure with { Locked = true }
                : figure with { Locked = figure.Points.Count > 0 ? Fits(figure) : figure.Locked });
        }

        if (_points.Count > 0)
        {
            Analyse();
        }
    }

    /// <summary>Puts a saved grid back, settled or not.</summary>
    /// <param name="grid">How many cells, negative for one ruled inside the figure.</param>
    /// <param name="fixedGrid">Whether it is the chessboard, settled for good.</param>
    public void RestoreGrid(int grid, bool fixedGrid)
    {
        Grid = Math.Clamp(Math.Abs(grid), 0, 64);
        GridInShape = grid < 0;
        GridFixed = fixedGrid && Grid > 0;
    }

    /// <summary>Takes one named figure off, where it is not settled.</summary>
    /// <param name="shape">Which figure.</param>
    public void Remove(MapShape shape) => _laid.RemoveAll(laid => laid.Shape == shape && !laid.Fixed);

    /// <summary>Takes every unsettled figure off.</summary>
    public void EraseShapes() => _laid.RemoveAll(laid => !laid.Fixed);

    /// <summary>
    /// Whether every marked place sits on the shape as it is placed.
    /// </summary>
    /// <returns>True when the shape is locked down by the marks.</returns>
    public bool Fits() => Working is { } working && Fits(working);

    /// <summary>Whether every marked place sits on one figure as it is placed.</summary>
    /// <param name="laid">The figure.</param>
    /// <returns>True when it passes through all of them.</returns>
    public bool Fits(LaidShape laid)
    {
        ArgumentNullException.ThrowIfNull(laid);

        if (laid.Shape == MapShape.None || laid.Size <= 0)
        {
            return false;
        }

        // Against its own places, not against whatever happens to be on the map: a figure
        // is confirmed by what it was laid over.
        IReadOnlyList<Vector2> against = laid.Points.Count > 0 ? laid.Points : _points;

        if (against.Count == 0)
        {
            return false;
        }

        foreach (Vector2 point in against)
        {
            if (Away(point, laid) > ShapeTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How far a place is from a figure's outline, in map pixels.</summary>
    private static float Away(Vector2 point, LaidShape laid)
    {
        if (laid.Shape == MapShape.Circle)
        {
            return MathF.Abs(Vector2.Distance(point, laid.At) - laid.Size);
        }

        // A line has no inside: what matters is how far the place is from the line itself,
        // which runs on past both ends.
        if (laid.Shape == MapShape.Line)
        {
            float radians = laid.Turn * MathF.PI / 180f;
            var along = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
            Vector2 offset = point - laid.At;

            return MathF.Abs((along.X * offset.Y) - (along.Y * offset.X));
        }

        // Everything else is a ring of corners, and a place is measured against the nearest
        // of the sides between them.
        Vector2[] corners = Corners(laid);
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
    public Vector2[] Corners() => Working is { } working ? Corners(working) : [];

    /// <summary>One figure's corners, in map pixels and in order round it.</summary>
    /// <param name="laid">The figure.</param>
    /// <returns>The corners; empty for a circle, which has none.</returns>
    public static Vector2[] Corners(LaidShape laid)
    {
        ArgumentNullException.ThrowIfNull(laid);

        int sides = laid.Shape switch
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
        float turn = laid.Turn * MathF.PI / 180f;

        for (int i = 0; i < sides; i++)
        {
            float angle = turn + (i * MathF.Tau / sides);

            corners[i] = laid.At + new Vector2(
                MathF.Cos(angle) * laid.Size, MathF.Sin(angle) * laid.Size);
        }

        return corners;
    }

    /// <summary>The two triangles a hexagram is drawn as, or nothing.</summary>
    /// <returns>Each triangle's three corners.</returns>
    public IReadOnlyList<Vector2[]> Triangles() =>
        Working is { } working ? Triangles(working) : [];

    /// <summary>The two triangles a hexagram is drawn as, or nothing.</summary>
    /// <param name="laid">The figure.</param>
    /// <returns>Each triangle's three corners.</returns>
    public static IReadOnlyList<Vector2[]> Triangles(LaidShape laid)
    {
        ArgumentNullException.ThrowIfNull(laid);

        if (laid.Shape != MapShape.Hexagram)
        {
            return [];
        }

        Vector2[] points = Corners(laid);

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

    /// <summary>
    /// Whether the line through two places passes through a third.
    /// </summary>
    /// <param name="from">One place.</param>
    /// <param name="to">The other.</param>
    /// <param name="place">The third, in map pixels.</param>
    /// <returns>True when the line runs within a village's width of it.</returns>
    public static bool Through(Vector2 from, Vector2 to, Vector2 place)
    {
        Vector2 along = to - from;
        float length = along.Length();

        if (length < 1e-3f)
        {
            return false;
        }

        Vector2 offset = place - from;
        float away = MathF.Abs((along.X * offset.Y) - (along.Y * offset.X)) / length;

        return away <= PlaceTolerance;
    }

    /// <summary>
    /// Whether the line through two places crosses the Paris meridian on the map.
    /// </summary>
    /// <param name="from">One place.</param>
    /// <param name="to">The other.</param>
    /// <returns>True when it does, somewhere the map actually shows.</returns>
    public static bool CrossesMeridian(Vector2 from, Vector2 to)
    {
        float meridian = (float)(MeridianAcross * Extent);
        Vector2 along = to - from;

        if (MathF.Abs(along.X) < 1e-3f)
        {
            return MathF.Abs(from.X - meridian) <= PlaceTolerance;
        }

        float t = (meridian - from.X) / along.X;
        float y = from.Y + (along.Y * t);

        return y >= 0 && y <= Extent;
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
    private static bool Circular(IReadOnlyList<Vector2> points, out Vector2 centre, out float radius)
    {
        if (!Circumcircle(points[0], points[1], points[2], out centre, out radius))
        {
            return false;
        }

        return MathF.Abs(Vector2.Distance(centre, points[3]) - radius) <= CircleTolerance;
    }

    /// <summary>
    /// The circle that best passes through every marked place.
    /// </summary>
    /// <param name="points">The places.</param>
    /// <param name="centre">Where its middle is.</param>
    /// <param name="radius">How big it is.</param>
    /// <returns>True when one could be fitted that is worth drawing.</returns>
    private static bool FitCircle(
        List<Vector2> points, out Vector2 centre, out float radius)
    {
        centre = default;
        radius = 0;

        if (points.Count < 3)
        {
            return false;
        }

        Vector2 middle = Vector2.Zero;

        foreach (Vector2 point in points)
        {
            middle += point;
        }

        middle /= points.Count;

        double sxx = 0;
        double sxy = 0;
        double syy = 0;
        double sxz = 0;
        double syz = 0;

        foreach (Vector2 point in points)
        {
            double x = point.X - middle.X;
            double y = point.Y - middle.Y;
            double z = (x * x) + (y * y);

            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sxz += x * z;
            syz += y * z;
        }

        double determinant = (sxx * syy) - (sxy * sxy);

        if (Math.Abs(determinant) < 1e-6)
        {
            return false;
        }

        double cx = ((sxz * syy) - (syz * sxy)) / (2 * determinant);
        double cy = ((syz * sxx) - (sxz * sxy)) / (2 * determinant);
        double sum = 0;

        foreach (Vector2 point in points)
        {
            double dx = point.X - middle.X - cx;
            double dy = point.Y - middle.Y - cy;

            sum += Math.Sqrt((dx * dx) + (dy * dy));
        }

        centre = new Vector2((float)(middle.X + cx), (float)(middle.Y + cy));
        radius = (float)(sum / points.Count);

        return float.IsFinite(radius) && radius >= 1f && radius <= Extent * 2;
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

        // <b>An enormous circle through three places is a straight line.</b> The test above
        // only rejects points that are exactly collinear; three that are nearly so give a
        // circle whose centre is somewhere off in the next country and whose arc across the
        // map is indistinguishable from the line they actually make. Refusing it here sends
        // the caller to the ordinary fit, which is what those places deserve.
        return float.IsFinite(radius) && radius >= 1f && radius <= Extent * 4;
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

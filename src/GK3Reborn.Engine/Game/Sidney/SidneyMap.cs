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
    /// <remarks>
    /// The first figure the puzzle asks for and the last: the sunrise line from the church
    /// at Rennes-le-Château over the tower at Blanchefort, and the snake the railway makes
    /// north of the site. It was only ever a <em>finding</em> here, so it vanished the
    /// moment the next place was marked — and every step after the first needs it to stay.
    /// </remarks>
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
/// <remarks>
/// <b>A figure owns its marks.</b> One shared set meant that plotting the four corners of
/// the square re-fitted the circle to eight places and threw it off the four it was
/// confirmed by — and the puzzle is a stack of figures each answering to its own places.
/// What is marked but not yet given to a figure stays in the map's working set.
/// </remarks>
public sealed record LaidShape(
    MapShape Shape,
    Vector2 At,
    float Size,
    float Turn,
    bool Locked,
    IReadOnlyList<Vector2> Points);

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

    /// <summary>
    /// Where Arques sits, in map pixels.
    /// </summary>
    /// <remarks>
    /// <b>Measured off the map rather than guessed at.</b> The enhanced
    /// <c>SIDNEYBIGMAP</c> is 2,736 pixels square — exactly twice the coordinates the marks
    /// are kept in — and the village's own block of buildings, not its label, sits at
    /// (2523, 330) on it. The label is up and to the left of the place, which is why the
    /// buildings are what was measured.
    /// </remarks>
    public static readonly Vector2 Arques = new(1262f, 165f);

    /// <summary>
    /// How many places a figure is made of.
    /// </summary>
    /// <param name="shape">Which figure.</param>
    /// <returns>The count, or nought where the figure takes no places.</returns>
    /// <remarks>
    /// <b>The answer has a size, so the question should too.</b> A circle through four
    /// villages is four places; a line is two. Letting the player put eleven on the map and
    /// then wonder why nothing confirms is a puzzle made of arithmetic they cannot see. The
    /// screen stops taking places once a figure has as many as it needs.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>Found in the picture rather than written down from a walkthrough.</b> The survey
    /// is green and white and carries exactly five red marks; scanning the enhanced
    /// 2,736-pixel copy for red and clustering what it finds gives five, and cropping each
    /// one reads its label off the map. Four of them are the four the circle wants.
    /// </para>
    /// <para>
    /// They are here so the screen can offer to place one. The crosses are three pixels
    /// across on a survey shown in about four hundred and fifty, which is a fine motor task
    /// rather than a puzzle, and <c>docs/screens.md</c> asks for an interface easier than
    /// the original's.
    /// </para>
    /// </remarks>
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
    /// The ruin of the Château de Blanchefort, where the sunrise line is drawn to.
    /// </summary>
    /// <remarks>
    /// <b>Not one of the crosses.</b> The survey marks villages with a red cross and this
    /// with a single dark point below its label, which is the only marker anywhere near it.
    /// Measured the same way as the rest: (1382, 714) on the enhanced 2,736-pixel copy.
    /// </remarks>
    public static readonly Vector2 Blanchefort = new(691f, 357f);

    /// <summary>
    /// How near a line has to pass to a named place to be said to go through it.
    /// </summary>
    /// <remarks>
    /// Wider than a village, because the two places the line is drawn from were clicked by
    /// eye on a picture and the note it unlocks is a confirmation rather than a
    /// measurement — the same reasoning as the tolerance a laid figure is locked by.
    /// </remarks>
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
    /// <remarks>
    /// <b>More than one at a time, because what they make together is the puzzle.</b> The
    /// books this game is built on lay a pentagram over a circle over a square and read the
    /// country off where the lines cross; a screen that holds one figure at a time makes the
    /// player remember the last one. The most recently laid is the one the rotate turns and
    /// the one the single-figure properties below report, which is what an editor does with
    /// a selection.
    /// </remarks>
    public IReadOnlyList<LaidShape> Laid => _laid;

    /// <summary>The figure most recently laid, if any.</summary>
    public MapShape Shape => _laid.Count > 0 ? _laid[^1].Shape : MapShape.None;

    /// <summary>Where it sits, in map pixels.</summary>
    public Vector2 ShapeAt => _laid.Count > 0 ? _laid[^1].At : Vector2.Zero;

    /// <summary>How big it is: the radius of the circle it is drawn inside.</summary>
    public float ShapeSize => _laid.Count > 0 ? _laid[^1].Size : 0f;

    /// <summary>How far it has been turned, in degrees.</summary>
    public float ShapeTurn => _laid.Count > 0 ? _laid[^1].Turn : 0f;

    /// <summary>Whether the marked places sit on it.</summary>
    public bool Locked => _laid.Count > 0 && _laid[^1].Locked;

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
        MapShape.Line => "Line",
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
        if (Selected != MapShape.None)
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
    /// <remarks>
    /// <b>The original has no such thing</b>: its map offers ENTER POINTS and CLEAR POINTS,
    /// so one misplaced click costs every place marked so far. The puzzle is played by
    /// clicking villages on a picture and a misplaced click is the ordinary case, which is
    /// exactly what <c>docs/screens.md</c> means by an interface easier than that one's.
    /// </remarks>
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
    /// <remarks>
    /// Kept on the map: a place dragged off the edge is a place the analysis would measure
    /// somewhere the picture does not show.
    /// </remarks>
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

        if (figure >= _laid.Count || which < 0 || which >= _laid[figure].Points.Count)
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
    /// <remarks>
    /// <b>Both of those are the game's own.</b> <c>ESIDNEY.TXT</c> offers Grid2 through
    /// Grid16 and then asks "Fill entire screen" or "Fill shape", and the chessboard the
    /// Gemini and Cancer passages are about is eight by eight ruled inside the tilted
    /// square — which a grid that can only cover the whole map cannot draw.
    /// </remarks>
    public void DrawGrid(int cells, bool inShape = false)
    {
        Grid = Math.Clamp(cells, 0, 64);
        GridInShape = inShape && Grid > 0;
    }

    /// <summary>Whether the grid is ruled inside the figure rather than over the whole map.</summary>
    public bool GridInShape { get; private set; }

    /// <summary>Takes the grid off again.</summary>
    public void EraseGrid()
    {
        Grid = 0;
        GridInShape = false;
    }

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
        if (shape == MapShape.None)
        {
            return;
        }

        LaidShape placed = Place(shape, _points);

        // Laying a figure that is already there re-fits it rather than stacking a second
        // copy on the first, and brings it to the front so that the rotate turns it.
        _laid.RemoveAll(already => already.Shape == shape);
        _laid.Add(placed);

        Found = null;
    }

    /// <summary>
    /// Chooses which figure the places being marked belong to.
    /// </summary>
    /// <param name="shape">The figure, or none to mark places belonging to nothing.</param>
    /// <remarks>
    /// <b>Choosing a figure is how a place knows what it is for.</b> One shared set meant
    /// the square's corners re-fitted the circle; choosing first means every place goes to
    /// the figure it belongs to, the figure re-fits as each one lands, and choosing a figure
    /// already drawn picks its places back up to be edited rather than throwing it away.
    /// </remarks>
    public void Select(MapShape shape)
    {
        Selected = shape;

        foreach (LaidShape laid in _laid)
        {
            if (laid.Shape == shape)
            {
                // Already drawn: its places come back to be edited.
                _points.Clear();
                _points.AddRange(laid.Points);
                Found = null;

                return;
            }
        }

        // Not drawn yet, so whatever is already marked becomes this figure's — but only
        // as many as it is made of. Adopting the lot gave a triangle four places and a
        // hexagram five, each fitted to whatever happened to be lying about, and the map
        // filled up with figures answering to nothing.
        int needs = Needs(shape);

        if (_points.Count > needs)
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
            _laid[i] = Place(_laid[i].Shape, _laid[i].Points);
        }
    }

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
            // previous circle". Failing that, the middle of the map, big enough to see.
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
        if (_laid.Count == 0)
        {
            return false;
        }

        LaidShape turned = _laid[^1] with { Turn = (_laid[^1].Turn + degrees) % 360f };

        _laid[^1] = turned with { Locked = Fits(turned) };

        return _laid[^1].Locked;
    }

    /// <summary>Takes the most recently laid figure off again.</summary>
    public void EraseShape()
    {
        if (_laid.Count > 0)
        {
            _laid.RemoveAt(_laid.Count - 1);
        }
    }

    /// <summary>
    /// Puts a saved map back: its marks, its figures and its grid.
    /// </summary>
    /// <param name="marks">The places, as "x,y" in map pixels.</param>
    /// <param name="figures">The figures, each with where it sits.</param>
    /// <param name="grid">How many cells the ruling is divided into.</param>
    /// <remarks>
    /// Whether each figure is confirmed is worked out again rather than restored, because
    /// it is a fact about the figure and the marks together and both are here. A saved
    /// "locked" that disagreed with them would be a confirmation the player could no longer
    /// earn or lose.
    /// </remarks>
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
            _laid.Add(figure with { Locked = Fits(figure) });
        }

        if (_points.Count > 0)
        {
            Analyse();
        }
    }

    /// <summary>Takes one named figure off.</summary>
    /// <param name="shape">Which figure.</param>
    public void Remove(MapShape shape) => _laid.RemoveAll(laid => laid.Shape == shape);

    /// <summary>Takes every figure off.</summary>
    public void EraseShapes() => _laid.Clear();

    /// <summary>
    /// Whether every marked place sits on the shape as it is placed.
    /// </summary>
    /// <returns>True when the shape is locked down by the marks.</returns>
    /// <remarks>
    /// "Select points to lock down feature", as the game puts it. A shape that merely lies
    /// near the marks is not confirmation of anything; one that passes through all of them
    /// is the whole point of laying it there.
    /// </remarks>
    public bool Fits() => _laid.Count > 0 && Fits(_laid[^1]);

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
    /// <remarks>
    /// A hexagram is drawn as its two overlapping triangles rather than as a twelve-pointed
    /// outline, because that is what the analysis of Poussin's painting describes finding —
    /// one triangle, then a second forming the star — and the sides a place has to lie on
    /// are those triangles' sides.
    /// </remarks>
    public Vector2[] Corners() => _laid.Count > 0 ? Corners(_laid[^1]) : [];

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
        _laid.Count > 0 ? Triangles(_laid[^1]) : [];

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
    /// <remarks>
    /// The whole line, not the piece between the two: what the sunrise line is *for* is
    /// where it goes on past Blanchefort, which is Arques.
    /// </remarks>
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

    /// <summary>
    /// The circle that best passes through every marked place.
    /// </summary>
    /// <param name="points">The places.</param>
    /// <param name="centre">Where its middle is.</param>
    /// <param name="radius">How big it is.</param>
    /// <returns>True when one could be fitted that is worth drawing.</returns>
    /// <remarks>
    /// <para>
    /// The ordinary algebraic fit: every place satisfies x squared plus y squared plus Dx
    /// plus Ey plus F equals nought for one circle, which is linear in D, E and F, so the
    /// normal equations give the circle whose squared error is least. Three places give the
    /// exact circle through them, which is what the circumcircle gave; more give the circle
    /// they actually suggest instead of the one the first three happen to make.
    /// </para>
    /// <para>
    /// Worked around the middle of the places rather than the map's corner, because the
    /// normal equations of a fit far from the origin lose their precision to the size of the
    /// numbers. Places in a line have no circle worth drawing and are refused here, which is
    /// what keeps the figure on the map.
    /// </para>
    /// </remarks>
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

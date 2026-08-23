using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the binoculars.
/// </summary>
/// <remarks>
/// Everything about them is data — twenty-one vantage points in <c>BINOCS.TXT</c>, each
/// naming what can be seen from it as a rectangle of sky in degrees. What can be wrong is
/// the reading: the file writes its headings and its bodies in inconsistent case, and a
/// case-sensitive lookup silently loses four of the forty-seven sights.
/// </remarks>
public sealed class BinocularsTests
{
    private const string Data = """
        // From CD1
        [CD1102P]
        LOC=MA3_a,LHM_a_a,PL3
        ANIM=GabPutBinoc

        [CD1102PMA3_a]
        ZOOMRECT=174,0,189,10
        CAMANGLE=-287.38,4.5
        CAMPOS=2423.19,530.67,-4351.27
        FLOOR=ma3_floor
        ENTERSHEEP=CD1102PMA3Ent
        EXITSHEEP=CD1102PMA3Ext

        [CD1102PLHM_a_a]
        ZOOMRECT=118,1,126,6
        CAMANGLE=258.19,0.00
        CAMPOS=562.72,61,86.287
        FLOOR=lhm_floor
        VORECT=75,-1,80,6
        LIC#=1ELVW446R1

        [CD1102pPL3]
        ZOOMRECT=69,1,81,7
        CAMANGLE=127.84,-3.00
        CAMPOS=-53.6,29.85,358.8
        """;

    [Fact]
    public void A_vantage_point_lists_what_can_be_seen_from_it()
    {
        Panorama view = Binoculars.From(Data).For("CD1", "102P");

        Assert.Equal(3, view.Sights.Count);
        Assert.Equal("GabPutBinoc", view.PutAway);
        Assert.True(view.Any);
    }

    [Fact]
    public void The_files_inconsistent_case_does_not_lose_a_sight()
    {
        // The heading writes CD1102P and the body under it CD1102pPL3. A case-sensitive
        // lookup drops that one and the player can never look at Blanchefort.
        Panorama view = Binoculars.From(Data).For("CD1", "102P");

        Assert.Contains(view.Sights, s => s.Location == "PL3");
    }

    [Fact]
    public void A_sight_carries_the_camera_that_leans_in_on_it()
    {
        Sight sight = Binoculars.From(Data).For("CD1", "102P").Sights[0];

        Assert.Equal("MA3", sight.Scene);
        Assert.Equal(new Vector2(-287.38f, 4.5f), sight.Angle);
        Assert.Equal(new Vector3(2423.19f, 530.67f, -4351.27f), sight.Position);
        Assert.Equal("ma3_floor", sight.Floor);
        Assert.Equal("CD1102PMA3Ent", sight.Entering);
    }

    [Fact]
    public void What_is_centred_is_what_the_rectangles_say()
    {
        Panorama view = Binoculars.From(Data).For("CD1", "102P");

        Assert.Equal("MA3_a", view.At(180f, 5f)?.Location);
        Assert.Equal("LHM_a_a", view.At(120f, 3f)?.Location);
        Assert.Equal("PL3", view.At(75f, 4f)?.Location);

        // Between two of them is hillside.
        Assert.Null(view.At(150f, 5f));

        // The right heading at the wrong pitch is the sky above it.
        Assert.Null(view.At(180f, -5f));
    }

    [Fact]
    public void A_voice_over_spot_is_read_beside_the_sight_it_shares_a_section_with()
    {
        Panorama view = Binoculars.From(Data).For("CD1", "102P");

        Assert.Single(view.Remarks);
        Assert.Equal("1ELVW446R1", view.Heard(77f, 2f)?.Licence);
        Assert.Null(view.Heard(180f, 5f));
    }

    [Fact]
    public void Somewhere_with_no_binoculars_has_nothing_to_see()
    {
        Binoculars binoculars = Binoculars.From(Data);

        Assert.False(binoculars.Usable("LBY", "110A"));
        Assert.False(binoculars.For("LBY", "110A").Any);
        Assert.False(binoculars.Usable(null, null));
    }

    [Fact]
    public void A_run_with_no_game_data_has_no_binoculars_rather_than_a_crash()
    {
        Assert.Equal(0, Binoculars.Empty.Count);
        Assert.False(Binoculars.Empty.For("CD1", "102P").Any);
    }
}

/// <summary>
/// Tests for Sidney's map.
/// </summary>
/// <remarks>
/// The puzzle the books are about: mark the churches and the ruins and see what they fall
/// on. The geometry is measured rather than checked against a list of right answers, so
/// these tests are about the measuring — a circle has to be recognised from four points
/// given in any order, and four points that merely look roughly round must not be.
/// </remarks>
public sealed class SidneyMapTests
{
    /// <summary>Four points exactly on a circle, at the given angles.</summary>
    private static Vector2[] OnACircle(Vector2 centre, float radius, params float[] degrees) =>
        [.. degrees.Select(d => centre + new Vector2(
            radius * MathF.Cos(d * MathF.PI / 180f),
            radius * MathF.Sin(d * MathF.PI / 180f)))];

    [Fact]
    public void Four_points_on_a_circle_are_recognised_as_one()
    {
        MapAnalysis found = SidneyMap.Measure(
            OnACircle(new Vector2(600, 700), 300f, 0, 85, 200, 300));

        Assert.Equal(MapFinding.Circle, found.Finding);
        Assert.Equal(600f, found.Centre.X, 0);
        Assert.Equal(700f, found.Centre.Y, 0);
        Assert.Equal(300f, found.Radius, 0);
    }

    [Fact]
    public void The_order_the_places_were_marked_in_does_not_matter()
    {
        // Not ninety degrees apart: four points evenly spaced on a circle are a rectangle,
        // and the rectangle is the more specific answer.
        Vector2[] points = OnACircle(new Vector2(500, 500), 250f, 10, 95, 200, 300);

        Assert.Equal(MapFinding.Circle, SidneyMap.Measure(points).Finding);
        Assert.Equal(MapFinding.Circle, SidneyMap.Measure([points[2], points[0], points[3], points[1]]).Finding);
    }

    [Fact]
    public void Four_points_evenly_spaced_on_a_circle_are_reported_as_the_rectangle_they_are()
    {
        // Both are true of them; the rectangle is what the story is also looking for, and
        // answering "circle" for every rectangle would mean it could never be found.
        Assert.Equal(
            MapFinding.Rectangle,
            SidneyMap.Measure(OnACircle(new Vector2(500, 500), 250f, 0, 90, 180, 270)).Finding);
    }

    [Fact]
    public void Four_points_that_are_merely_roughly_round_are_not_a_circle()
    {
        var centre = new Vector2(600, 700);
        Vector2[] points = OnACircle(centre, 300f, 0, 85, 200, 300);

        // The fourth pulled sixty pixels straight out from the middle, which is well past
        // the tolerance. The note this unlocks says "perfectly".
        points[3] = centre + ((points[3] - centre) * (360f / 300f));

        Assert.NotEqual(MapFinding.Circle, SidneyMap.Measure(points).Finding);
    }

    [Fact]
    public void Points_in_a_row_are_a_line()
    {
        Assert.Equal(
            MapFinding.Line,
            SidneyMap.Measure([new(100, 100), new(300, 200), new(500, 300), new(700, 400)]).Finding);
    }

    [Fact]
    public void Two_points_are_always_a_line_and_one_is_not_enough()
    {
        Assert.Equal(MapFinding.Line, SidneyMap.Measure([new(10, 10), new(900, 400)]).Finding);
        Assert.Equal(MapFinding.TooFew, SidneyMap.Measure([new(10, 10)]).Finding);
        Assert.Equal(MapFinding.TooFew, SidneyMap.Measure([]).Finding);
    }

    [Fact]
    public void Four_corners_are_a_rectangle_however_they_were_clicked()
    {
        Vector2[] corners = [new(200, 200), new(800, 200), new(800, 500), new(200, 500)];

        Assert.Equal(MapFinding.Rectangle, SidneyMap.Measure(corners).Finding);
        Assert.Equal(
            MapFinding.Rectangle,
            SidneyMap.Measure([corners[0], corners[2], corners[1], corners[3]]).Finding);
    }

    [Fact]
    public void More_than_four_scattered_points_are_several_possibilities()
    {
        Assert.Equal(
            MapFinding.Several,
            SidneyMap.Measure(
                [new(100, 700), new(400, 120), new(900, 300), new(650, 900), new(220, 400)]).Finding);
    }

    [Fact]
    public void Marks_and_the_ruling_go_on_and_come_off()
    {
        var map = new SidneyMap();

        Assert.False(map.Any);

        map.Enter(new Vector2(100, 100));
        map.DrawGrid(8);

        Assert.True(map.Any);
        Assert.Single(map.Points);
        Assert.Equal(8, map.Grid);

        map.ClearPoints();
        map.EraseGrid();

        Assert.False(map.Any);
    }

    [Fact]
    public void The_map_stops_taking_marks_long_before_it_is_fitting_noise()
    {
        var map = new SidneyMap();

        for (int i = 0; i < 20; i++)
        {
            map.Enter(new Vector2(i * 30, i * 20));
        }

        Assert.Equal(12, map.Points.Count);
    }

    [Fact]
    public void A_place_reads_out_as_degrees_minutes_and_seconds()
    {
        // The format is the game's own MapLatLongText. How exact the anchoring is has its
        // own note on the class; what this checks is that it is written the right way.
        string said = SidneyMap.Coordinates(new Vector2(700, 500));

        Assert.Contains("deg", said, StringComparison.Ordinal);
        Assert.Contains("long", said, StringComparison.Ordinal);
        Assert.Contains("lat", said, StringComparison.Ordinal);
        Assert.Contains("'", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Longitude_grows_eastwards_and_latitude_falls_southwards()
    {
        // The one thing a georeference can be wrong about in a way that is not merely
        // imprecise: which way round it goes.
        static (double Longitude, double Latitude) Read(Vector2 at)
        {
            string[] parts = SidneyMap.Coordinates(at).Split([" long", " lat"], StringSplitOptions.None);

            static double Degrees(string text)
            {
                string[] bits = text.Replace("deg", " ", StringComparison.Ordinal)
                    .Replace("'", " ", StringComparison.Ordinal)
                    .Replace("\"", " ", StringComparison.Ordinal)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                return double.Parse(bits[0], System.Globalization.CultureInfo.InvariantCulture) +
                       (double.Parse(bits[1], System.Globalization.CultureInfo.InvariantCulture) / 60) +
                       (double.Parse(bits[2], System.Globalization.CultureInfo.InvariantCulture) / 3600);
            }

            return (Degrees(parts[0]), Degrees(parts[1]));
        }

        Assert.True(Read(new Vector2(1000, 500)).Longitude > Read(new Vector2(300, 500)).Longitude);
        Assert.True(Read(new Vector2(500, 1000)).Latitude < Read(new Vector2(500, 300)).Latitude);
    }
}

/// <summary>
/// Tests for the shape laid over Sidney's map.
/// </summary>
/// <remarks>
/// "Select points to lock down feature", the game says, and the note it is working towards
/// is "Shape locked and confirmed." So the thing that must be true is that a shape laid over
/// places that are genuinely on it locks, and one laid over places that are not does not —
/// otherwise the confirmation confirms nothing.
/// </remarks>
public sealed class SidneyShapeTests
{
    /// <summary>Points at the given angles on a circle.</summary>
    private static Vector2[] Around(Vector2 centre, float radius, params float[] degrees) =>
        [.. degrees.Select(d => centre + new Vector2(
            radius * MathF.Cos(d * MathF.PI / 180f),
            radius * MathF.Sin(d * MathF.PI / 180f)))];

    private static SidneyMap Marked(params Vector2[] points)
    {
        var map = new SidneyMap();

        foreach (Vector2 point in points)
        {
            map.Enter(point);
        }

        return map;
    }

    [Fact]
    public void A_circle_laid_over_places_that_are_on_one_locks()
    {
        SidneyMap map = Marked(Around(new Vector2(600, 700), 300f, 0, 85, 200, 300));

        map.UseShape(MapShape.Circle);

        Assert.True(map.Locked);
        Assert.Equal(600f, map.ShapeAt.X, 0);
        Assert.Equal(300f, map.ShapeSize, 0);
    }

    [Fact]
    public void A_circle_laid_over_places_that_are_not_does_not_lock()
    {
        // Three on a circle and a fourth well inside it: nothing to confirm.
        SidneyMap map = Marked([
            .. Around(new Vector2(600, 700), 300f, 0, 120, 240),
            new Vector2(600, 700)]);

        map.UseShape(MapShape.Circle);

        Assert.False(map.Locked);
    }

    [Fact]
    public void A_triangle_locks_on_three_places_that_make_one()
    {
        SidneyMap map = Marked(Around(new Vector2(500, 500), 240f, 0, 120, 240));

        map.UseShape(MapShape.Triangle);

        Assert.Equal(3, map.Corners().Length);
        Assert.True(map.Locked);
    }

    [Fact]
    public void A_square_locks_on_four_corners_and_the_turn_is_what_makes_it_fit()
    {
        // Corners of a square, marked starting from one that is not where the template
        // lands by default, so the fit has to come from turning it.
        SidneyMap map = Marked(Around(new Vector2(500, 500), 260f, 45, 135, 225, 315));

        map.UseShape(MapShape.Square);

        Assert.True(map.Locked);

        // Turned a quarter of the way round it is the same square, so it still fits.
        Assert.True(map.Rotate(90f));

        // Turned an eighth it is not, and the confirmation goes away.
        Assert.False(map.Rotate(45f));
    }

    [Fact]
    public void A_hexagram_is_drawn_as_the_two_triangles_the_analysis_describes()
    {
        SidneyMap map = Marked(Around(new Vector2(700, 700), 300f, 0, 60, 120, 180, 240, 300));

        map.UseShape(MapShape.Hexagram);

        Assert.True(map.Locked);

        IReadOnlyList<Vector2[]> triangles = map.Triangles();

        Assert.Equal(2, triangles.Count);
        Assert.All(triangles, t => Assert.Equal(3, t.Length));

        // The second is the first turned sixty degrees, which is what makes it a star and
        // not a triangle drawn twice.
        Assert.NotEqual(triangles[0][0].X, triangles[1][0].X, 0);
    }

    [Fact]
    public void Erasing_takes_the_shape_and_its_confirmation_away()
    {
        SidneyMap map = Marked(Around(new Vector2(600, 700), 300f, 0, 85, 200, 300));

        map.UseShape(MapShape.Circle);
        map.EraseShape();

        Assert.Equal(MapShape.None, map.Shape);
        Assert.False(map.Locked);
        Assert.False(map.Fits());
    }

    [Fact]
    public void A_shape_with_nothing_marked_is_laid_but_never_confirmed()
    {
        var map = new SidneyMap();

        map.UseShape(MapShape.Square);

        Assert.Equal(MapShape.Square, map.Shape);
        Assert.False(map.Locked);
        Assert.True(map.ShapeSize > 0);
    }

    [Fact]
    public void Marking_another_place_re_fits_the_shape_rather_than_leaving_it_behind()
    {
        var centre = new Vector2(600, 700);
        SidneyMap map = Marked(Around(centre, 300f, 0, 120, 240));

        map.UseShape(MapShape.Circle);

        Assert.True(map.Locked);

        // A fourth place nowhere near the circle. Re-laying it must not still confirm.
        map.Enter(centre + new Vector2(20, 10));
        map.UseShape(MapShape.Circle);

        Assert.False(map.Locked);
    }
}

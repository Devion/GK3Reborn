using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GK3Reborn.Content;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for the scene dressing: the town of Couiza, Rennes-les-Bains extended, and the
/// gateway over Rennes-le-Chateau's cemetery entrance.
/// </summary>
public sealed class SceneDressingTests
{
    /// <summary>TR1's own file, cut down to the sections the dressing touches.</summary>
    private const string Tr1 =
        "[GENERAL]\r\nfloor=tr1_floor\r\ncameraBounds=tr1_cambnds\r\n\r\n"
        + "[ACTORS]\r\nmodel=gab,noun=GABRIEL,pos=FR_MAP,ego\r\n\r\n"
        + "[MODELS]\r\n"
        + "model=tr1_fftree1,  type=prop\r\n"
        + "model=tudorbasic01, noun=OTR_BUILDINGS,   type=scene\r\n"
        + "model=tr1_station,  noun=STATION,        type=scene\r\n"
        + "\r\n[INSPECT_CAMERAS]\r\n"
        + "model=tr1_station, angle={-95.38, 2.00}, pos={960.23, 96.44, 90.25}\r\n";

    /// <summary>RL1's own file, cut down to the sections the dressing touches.</summary>
    private const string Rl1 =
        "[GENERAL]\r\nfloor=rl1_floor\r\ncameraBounds=Rl1CameraBounds\r\n\r\n"
        + "[ACTORS]\r\nmodel=gab,noun=GABRIEL,idle=gabIdle.gas,ego\r\n\r\n"
        + "[MODELS]\r\n"
        + "model=rl1_bar,     noun=BAR,       type=scene\r\n"
        + "model=howse_door,  noun=OTR_DOORS, type=scene\r\n"
        + "model=rl1_trees,   type=scene\r\n";

    private static CutContent Dressing() => CutContent.Open(
        CutContentTier.None, DressedTowns.Couiza | DressedTowns.RennesLesBains);

    private static string Apply(CutContent table) =>
        Encoding.Latin1.GetString(
            table.Apply("TR1.SIF", Encoding.Latin1.GetBytes(Tr1)));

    /// <summary>The model lines the dressing adds, as (name, whole line).</summary>
    private static List<(string Model, string Line)> Added()
    {
        var added = new List<(string, string)>();

        foreach (string line in Apply(Dressing()).Split('\n'))
        {
            string body = line.Trim();

            if (!body.StartsWith("model=RBN_CZ_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = body["model=".Length..].Split(',')[0].Trim();
            added.Add((name, body));
        }

        return added;
    }

    [Fact]
    public void NothingIsDressedWhenTheGeometryIsNotInstalled()
    {
        // The gate is the geometry itself, and this is the case that matters: a player
        // with no content packs must get the room exactly as it shipped, with no
        // diagnostics about forty models that are not there.
        CutContent table = CutContent.Open(CutContentTier.None, DressedTowns.None);

        Assert.True(table.IsEmpty);
        Assert.Equal(Tr1, Apply(table));
        Assert.Equal(Rl1, ApplyRl1(table));
    }

    [Fact]
    public void TheDressingIsNotPartOfTheRestorations()
    {
        // Two tables and two switches. Everything in CutContent.txt is the developers' own
        // data switched back on; none of this is, and the day the two are merged is the day
        // "content the game shipped with" starts meaning "content we thought it should
        // have had".
        CutContent restorations = CutContent.Open(CutContentTier.Reconstructed);

        Assert.DoesNotContain(
            "RBN_CZ_",
            Encoding.Latin1.GetString(restorations.Apply("TR1.SIF", Encoding.Latin1.GetBytes(Tr1))),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryLineOfTheTableCanBeRead()
    {
        // A line the parser cannot read is a building that never appears and says nothing
        // about it, which is the same silence the room already suffers from.
        Assert.Equal(0, Dressing().Unreadable);
    }

    [Fact]
    public void EveryEditAppliesToTheRoomItNames()
    {
        // Both rooms, because the table is one table: CutContent.Open hands out a single
        // cached instance per switch and its counters are that instance's, so a test that
        // applies TR1 alone is asking whether 65 edits equals 258 and only passes when
        // some other test happened to run first and apply RL1 to the same table.
        CutContent table = Dressing();

        Apply(table);
        ApplyRl1(table);

        Assert.Equal(0, table.Failed);
        Assert.Equal(table.EditCount, table.Applied);
    }

    [Fact]
    public void TheDressingOnlyTouchesTheTwoRoomsItIsFor()
    {
        // Two rooms. If that changes the change should be deliberate, because a dressing
        // table that quietly reaches a third room is a room somebody has to discover has
        // been changed.
        Assert.Equal(["RL1.SIF", "TR1.SIF"], Dressing().Names);
    }

    [Fact]
    public void EitherTownCanBeInstalledWithoutTheOther()
    {
        // They are packed and rebuilt separately, so a workspace part way through a
        // rebuild of one still has the other. Taking neither because one is missing would
        // empty a room that has everything it needs.
        CutContent couiza = CutContent.Open(CutContentTier.None, DressedTowns.Couiza);
        CutContent bains = CutContent.Open(CutContentTier.None, DressedTowns.RennesLesBains);

        Assert.Equal(["TR1.SIF"], couiza.Names);
        Assert.Equal(["RL1.SIF"], bains.Names);
    }

    [Fact]
    public void NoTwoPlacementsNameOneModel()
    {
        // SceneDefinition.MergeModels keys a room's models by name, and TR1 has both a room
        // file and timeblock files, so the merge runs. Two lines naming one model are one
        // building, and the other one is simply not in the room.
        List<string> names = [.. Added().Select(a => a.Model)];

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryBuildingIsAPropAndAnswersToTheRoomsOwnNoun()
    {
        // type=scene means "an object already inside the BSP" and loads no file at all, so
        // a facade declared that way is counted and never drawn. OTR_BUILDINGS is TR1's
        // own noun with its own recorded LOOK line, which is why the town needs no
        // dialogue.
        foreach ((string model, string line) in Added().Where(a => IsBuilding(a.Model)))
        {
            Assert.Contains("type=prop", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("noun=OTR_BUILDINGS", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pos={", line, StringComparison.Ordinal);
        }
    }

    /// <summary>Whether a model is a building rather than a tree card or a lane.</summary>
    private static bool IsBuilding(string model) =>
        !model.Contains("TREE", StringComparison.OrdinalIgnoreCase) &&
        !IsSurface(model);

    /// <summary>Whether a model is surfacing rather than something built.</summary>
    private static bool IsSurface(string model) =>
        model.Contains("ROAD", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("APRON", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("GROUND", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EveryLaneIsLaidWhereItWasAuthored()
    {
        // A lane follows the ground, so its vertices are the room's own coordinates and a
        // pos would move it off the floor it was draped over.
        //
        // And it is a decal, which is what keeps it out of the picker. A road declared
        // `prop` is the nearest thing the ray meets, `FloorTarget` wants the floor by
        // name, and the player cannot walk on ground he can plainly see.
        List<(string Model, string Line)> lanes = [.. Added().Where(a => IsSurface(a.Model))];

        Assert.NotEmpty(lanes);

        foreach ((string model, string line) in lanes)
        {
            Assert.DoesNotContain("pos=", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("type=decal", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryTreeIsACardStandingWhereItWasAuthored()
    {
        // A grown tree carries its own transform and pos is never consulted once the tree
        // pass has claimed a model, so a tree card with a pos would be a tree standing
        // somewhere the layout did not put it — and there would be nothing on screen to
        // say which of the two places was meant.
        List<(string Model, string Line)> trees =
            [.. Added().Where(a => a.Model.Contains("TREE", StringComparison.OrdinalIgnoreCase))];

        Assert.NotEmpty(trees);

        foreach ((string model, string line) in trees)
        {
            Assert.DoesNotContain("pos=", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("type=prop", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NothingThatShippedIsMovedOrRenamed()
    {
        // The whole claim of this feature. Every line TR1 already had has to survive the
        // edit unchanged; the dressing may only add.
        string[] before = Tr1.Split('\n');
        string[] after = Apply(Dressing()).Split('\n');

        foreach (string line in before)
        {
            Assert.Contains(line, after);
        }

        Assert.True(after.Length > before.Length);
    }

    [Fact]
    public void ApplyingTwiceChangesNothingTheSecondTime()
    {
        // `append` is idempotent, and it has to be: the table is applied to the bytes on
        // their way out of the archive, and the archive is read again every time the
        // player walks into the room.
        CutContent table = Dressing();

        string once = Apply(table);
        string twice = Encoding.Latin1.GetString(
            table.Apply("TR1.SIF", Encoding.Latin1.GetBytes(once)));

        Assert.Equal(once, twice);
    }

    // ---------------------------------------------------------------------------------
    // Rennes-les-Bains
    // ---------------------------------------------------------------------------------

    private static string ApplyRl1(CutContent table) =>
        Encoding.Latin1.GetString(
            table.Apply("RL1.SIF", Encoding.Latin1.GetBytes(Rl1)));

    /// <summary>The model lines RL1's dressing adds, as (name, whole line).</summary>
    private static List<(string Model, string Line)> AddedToRl1()
    {
        var added = new List<(string, string)>();

        foreach (string line in ApplyRl1(Dressing()).Split('\n'))
        {
            string body = line.Trim();

            if (!body.StartsWith("model=RBN_RB_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            added.Add((body["model=".Length..].Split(',')[0].Trim(), body));
        }

        return added;
    }

    [Fact]
    public void RennesLesBainsGetsATown()
    {
        // The point of the whole set. Eight buildings on one street is what shipped; the
        // number here is not asserted exactly, because the layout is packed rather than
        // typed and a donor that stops passing the wall gate would legitimately change it.
        Assert.True(AddedToRl1().Count > 60);
    }

    [Fact]
    public void NoTwoRennesLesBainsPlacementsNameOneModel()
    {
        // RL1 has a room file and eight timeblock files, so MergeModels runs and the
        // second of two identical names silently replaces the first.
        List<string> names = [.. AddedToRl1().Select(a => a.Model)];

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryRennesLesBainsBuildingIsAPropAndAnswersToTheRoomsOwnNoun()
    {
        // BUILDINGS is RL1's own noun and RL1_ALL.NVC gives it a LOOK rule with a recorded
        // line for both characters, so the new streets need no dialogue of their own.
        foreach ((string model, string line) in AddedToRl1().Where(a => IsBuilding(a.Model)))
        {
            Assert.Contains("type=prop", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("noun=BUILDINGS", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pos={", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheNewGroundAndRoadsAreDecals()
    {
        // Both have to be. An actor takes his height from the floor object, so a second
        // thing he could stand on is a second answer to a question that has one; and
        // SceneInteraction.FloorTarget only walks him when the pick is the floor by name,
        // so a prop laid over the ground swallows every click that would have moved him.
        List<(string Model, string Line)> surfacing =
            [.. AddedToRl1().Where(a => IsSurface(a.Model))];

        Assert.NotEmpty(surfacing);

        foreach ((string model, string line) in surfacing)
        {
            Assert.Contains("type=decal", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pos=", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NothingIsBuiltWhereThePlayerWalksOrTheCameraGoes()
    {
        // The two rectangles the whole layout is arranged around. The walk bitmap is
        // RL1.SIF's own `size={618.472290,1349.645874} offset={79.474701,26.514526}`,
        // which WalkBoundary maps as `world = u * size - offset`; the camera shell is
        // RL1CAMERABOUNDS. A building inside either is one the player can walk into or the
        // camera can end up inside, and the packer refuses both -- this is the same check
        // on the side that ships.
        foreach ((string model, string line) in AddedToRl1().Where(a => IsBuilding(a.Model)))
        {
            int at = line.IndexOf("pos={", StringComparison.Ordinal) + "pos={".Length;
            string[] parts = line[at..line.IndexOf('}', at)].Split(',');

            float x = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            float z = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);

            // The centre alone, because the footprint is the packer's business and this is
            // about the placement that shipped: a centre inside either rectangle is a
            // building squarely in the room the player uses.
            Assert.False(x is > -79.5f and < 539.0f && z is > -26.5f and < 1323.2f,
                $"{model} stands at {x},{z}, inside the walk bitmap");

            Assert.False(x is > -451.9f and < 751.1f && z is > -791.2f and < 1736.0f,
                $"{model} stands at {x},{z}, inside the camera shell");
        }
    }

    [Fact]
    public void ApplyingRennesLesBainsTwiceChangesNothingTheSecondTime()
    {
        CutContent table = Dressing();

        string once = ApplyRl1(table);
        string twice = Encoding.Latin1.GetString(
            table.Apply("RL1.SIF", Encoding.Latin1.GetBytes(once)));

        Assert.Equal(once, twice);
    }

    // ---------------------------------------------------------------------------------
    // Rennes-le-Chateau's cemetery gateway
    // ---------------------------------------------------------------------------------

    /// <summary>RC3's own file, cut down to the sections the dressing touches.</summary>
    private const string Rc3 =
        "[GENERAL]\r\nfloor=rc3_floor\r\ncameraBounds=rc3_cambnds\r\n\r\n"
        + "[ACTORS]\r\nmodel=gab,noun=GABRIEL,idle=gabIdle.gas,talk=gabTalk.gas,ego\r\n\r\n"
        + "[MODELS]\r\n"
        + "model=rc3_cemwalls, type=scene\r\n"
        + "model=rc3_church, noun=CHURCH, type=scene\r\n"
        + "model=rc3_exittocem, noun=EXIT2, type=hittest, verb=EXIT_LEFT\r\n"
        + "\r\n[POSITIONS]\r\n"
        + "TO_CEM, pos={1100.46, -36.25, -1932.93}, heading=32.26, camera=FR_CHU\r\n";

    private static CutContent Gateway() =>
        CutContent.Open(CutContentTier.None, DressedTowns.RennesLeChateau);

    private static string ApplyRc3(CutContent table) =>
        Encoding.Latin1.GetString(
            table.Apply("RC3.SIF", Encoding.Latin1.GetBytes(Rc3)));

    /// <summary>The one model line the gateway adds.</summary>
    private static string GatewayLine() =>
        ApplyRc3(Gateway())
            .Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith("model=RBN_RC_", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheGatewayIsInstalledOnItsOwn()
    {
        // Its own sentinel and its own table file, because it is packed and rebuilt apart
        // from either town: a workspace that has the gateway and not Couiza should get the
        // gateway. The file is its own for a duller reason -- both town builders rewrite
        // Dressing.txt, and a section added to it by hand survives one of them and not the
        // other.
        Assert.Equal(["RC3.SIF"], Gateway().Names);
    }

    [Fact]
    public void NoGatewayWithoutItsGeometry()
    {
        // The same gate as the towns'. A player with no content packs gets RC3 exactly as
        // it shipped -- a gap in a wall -- and no diagnostic about a model that is not
        // there.
        CutContent table = CutContent.Open(CutContentTier.None, DressedTowns.None);

        Assert.Equal(Rc3, ApplyRc3(table));
    }

    [Fact]
    public void TheGatewayIsAPropThatAnswersToTheCemeteryExit()
    {
        // type=prop, because type=scene means "already inside the BSP" and loads no file.
        //
        // EXIT2 with EXIT_LEFT is rc3_exittocem's own binding, which RC3_ALL.NVC already
        // has a rule for. That matters twice: a click on the gateway walks Gabriel through
        // it, and the pediment does not swallow the clicks that would otherwise have met
        // the doorway behind it -- ScenePicker takes the nearest target whether it answers
        // to anything or not.
        string line = GatewayLine();

        Assert.Contains("type=prop", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("noun=EXIT2", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verb=EXIT_LEFT", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pos={", line, StringComparison.Ordinal);
        Assert.Contains("heading=", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheGatewayStandsInTheOpeningItWasMeasuredFrom()
    {
        // The model is built at the origin and put here by pos, so this coordinate is the
        // whole of what says the gateway is over the doorway rather than in a field. The
        // box is generous on purpose: it asks whether the placement is the room's, and
        // does not restate the number build_rc3_gate.py measured.
        string line = GatewayLine();
        int at = line.IndexOf("pos={", StringComparison.Ordinal) + "pos={".Length;
        string[] parts = line[at..line.IndexOf('}', at)].Split(',');

        float x = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        float y = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        float z = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);

        // TO_CEM -- the spot the player walks to before the room changes -- is at
        // (1100.5, -36.3, -1932.9), and the gateway stands over it.
        Assert.InRange(x, 1060f, 1120f);
        Assert.InRange(z, -1970f, -1910f);

        // Above head height, because pos.y is the underside of the lintel, which is the
        // model's floor. The room's ground there is about -40.
        Assert.InRange(y, 30f, 60f);
    }

    [Fact]
    public void NothingOfRc3IsMovedOrRenamed()
    {
        // The whole claim, as for the two towns: every line RC3 already had survives the
        // edit unchanged, and the dressing may only add.
        string[] before = Rc3.Split('\n');
        string[] after = ApplyRc3(Gateway()).Split('\n');

        foreach (string line in before)
        {
            Assert.Contains(line, after);
        }

        Assert.True(after.Length > before.Length);
    }

    [Fact]
    public void ApplyingTheGatewayTwiceChangesNothingTheSecondTime()
    {
        // The table is applied to the bytes on their way out of the archive, and the
        // archive is read again every time the player walks into the room.
        CutContent table = Gateway();

        string once = ApplyRc3(table);
        string twice = Encoding.Latin1.GetString(
            table.Apply("RC3.SIF", Encoding.Latin1.GetBytes(once)));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void EveryLineOfTheGatewaysTableCanBeRead()
    {
        // One line, and a line the parser cannot read is a gateway that never appears and
        // says nothing about it.
        Assert.Equal(0, Gateway().Unreadable);
    }
}

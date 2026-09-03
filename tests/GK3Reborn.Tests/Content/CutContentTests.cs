using System.Text;
using GK3Reborn.Content;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for the cut-content restoration table.
/// </summary>
/// <remarks>
/// Two properties matter here and the rest follows from them. An edit has to <em>find</em>
/// what it is about to change and say so when it cannot, because a restoration that
/// silently does nothing is exactly the failure that lost this content in the first place.
/// And the game has to be untouched without the flag, because this is content the
/// developers switched off.
/// </remarks>
public sealed class CutContentTests
{
    private const string Sif =
        "[GENERAL]\r\nscene=wod_n\r\n\r\n[MODELS]\r\n"
        + "//model=wod_stones,noun=ROCKS,type=scene\r\n"
        + "model=wod_dectree01,noun=TREES,type=scene\r\n"
        + "model=cem_graves, type=scene\r\n"
        + "\r\n[MODELS={IsCurrentTime(\"310a\")}]\r\n"
        + "model=wod_holetop, type=scene,hidden\r\n"
        + "\r\n[INSPECT_CAMERAS]\r\n"
        + "model=wod_dectree01, angle={1,2}\r\n";

    private const string Nvc =
        "SCENE, ENTER, ALL, script={}\r\n"
        + "//ROCKS,   LOOK,  GABE_ALL,  script={wait StartVoiceOver(\"0NLO044Q81\",1);}\r\n"
        + "TREES,     LOOK,  GABE_ALL,  script={wait StartVoiceOver(\"0NLO044QS1\",1);}\r\n";

    [Fact]
    public void NothingIsRestoredWithoutTheFlag()
    {
        CutContent table = CutContent.Open(CutContentTier.None);

        Assert.True(table.IsEmpty);
        Assert.Equal(0, table.EditCount);
    }

    [Fact]
    public void EveryLineOfTheShippedTableCanBeRead()
    {
        // A mistyped operation is a restoration that never happens, and the whole point of
        // this table is that such things stop being silent. It went unnoticed once: an
        // append written with one argument fewer than the parser wanted was dropped without
        // a word, and the count said everything had applied.
        Assert.Equal(0, CutContent.Open(CutContentTier.Reconstructed).Unreadable);
    }

    [Fact]
    public void AnAppendAddsToASectionOnceAndOnlyOnce()
    {
        const string Listing =
            "[GENERAL]\r\nscene=rc2\r\n\r\n[ACTIONS]\r\nrc2_all.nvc\r\n";

        const string Table =
            "[OBSERVATION]\nappend X.SIF ACTIONS rc2_crowsnest.nvc\n";

        CutContent table = CutContent.Parse(Table, CutContentTier.Observation);

        string after = Encoding.Latin1.GetString(
            table.Apply("X.SIF", Encoding.Latin1.GetBytes(Listing)));

        Assert.Contains("rc2_all.nvc", after, StringComparison.Ordinal);
        Assert.Contains("rc2_crowsnest.nvc", after, StringComparison.Ordinal);

        // Applying it to a file that already lists it changes nothing and is not a failure.
        CutContent again = CutContent.Parse(Table, CutContentTier.Observation);

        string twice = Encoding.Latin1.GetString(
            again.Apply("X.SIF", Encoding.Latin1.GetBytes(after)));

        Assert.Equal(after, twice);
        Assert.Equal(0, again.Failed);
    }

    [Fact]
    public void An_append_reaches_a_flat_file_and_keeps_its_spaces()
    {
        // The inventory files are not sections and not commas: "v_hose = Garden hose" is a
        // line with a space in it, in a file that has no [section] until well past the part
        // that matters. Both of those broke the first version of this.
        const string Flat =
            "NONE\t= undefined\r\nSPRAY_GUN\t= SprayBottle\r\n\r\n[ToolTips]\r\nx=y\r\n";

        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nappend X.TXT - HOSE_AND_SPRAY_GUN = SprayBottle\n",
            CutContentTier.Observation);

        string after = Encoding.Latin1.GetString(
            table.Apply("X.TXT", Encoding.Latin1.GetBytes(Flat)));

        Assert.Contains("HOSE_AND_SPRAY_GUN = SprayBottle", after, StringComparison.Ordinal);

        // Before the first section, not swept into ToolTips at the end of the file.
        Assert.True(
            after.IndexOf("HOSE_AND_SPRAY_GUN", StringComparison.Ordinal) <
            after.IndexOf("[ToolTips]", StringComparison.Ordinal));
    }

    [Fact]
    public void TheShippedTableParsesAndNamesOnlyTextAssets()
    {
        CutContent observation = CutContent.Open(CutContentTier.Observation);
        CutContent all = CutContent.Open(CutContentTier.All);

        Assert.False(observation.IsEmpty);

        // The puzzle tier is strictly more than the observation tier, never different.
        Assert.True(all.EditCount > observation.EditCount);

        foreach (string name in all.Names)
        {
            Assert.True(
                name.EndsWith(".SIF", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".NVC", StringComparison.OrdinalIgnoreCase),
                $"{name} is neither a scene nor an action file.");
        }
    }

    [Fact]
    public void ACommentedRuleIsUncommented()
    {
        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nrule X.NVC ROCKS LOOK GABE_ALL\n", CutContentTier.Observation);

        string after = Apply(table, "X.NVC", Nvc);

        Assert.DoesNotContain("//ROCKS", after, StringComparison.Ordinal);
        Assert.Contains("ROCKS,   LOOK,  GABE_ALL", after, StringComparison.Ordinal);

        // The rest of the file is untouched, line endings included.
        Assert.Contains("TREES,     LOOK,  GABE_ALL", after, StringComparison.Ordinal);
        Assert.Equal(Nvc.Length - 2, after.Length);
    }

    [Fact]
    public void ACommentedBindingIsUncommentedInPlace()
    {
        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nbind X.SIF wod_stones ROCKS scene\n", CutContentTier.Observation);

        string after = Apply(table, "X.SIF", Sif);

        Assert.Contains("model=wod_stones,noun=ROCKS,type=scene", after, StringComparison.Ordinal);
        Assert.DoesNotContain("//model=wod_stones", after, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewBindingInheritsItsAnchorsSection()
    {
        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nbind X.SIF wod_hole HOLE scene after=wod_holetop\n",
            CutContentTier.Observation);

        string after = Apply(table, "X.SIF", Sif);
        string[] lines = after.Split("\r\n");

        int anchor = Array.FindIndex(lines, l => l.Contains("wod_holetop", StringComparison.Ordinal));
        int added = Array.FindIndex(lines, l => l.Contains("noun=HOLE", StringComparison.Ordinal));

        Assert.True(anchor >= 0 && added == anchor + 1);

        // Which is the conditional block, not the unconditional one above it: a binding in
        // the wrong [MODELS] is live at the wrong time.
        int conditional = Array.FindIndex(lines, l => l.StartsWith("[MODELS={", StringComparison.Ordinal));
        Assert.True(added > conditional);
    }

    [Fact]
    public void ANounIsAddedToABindingThatHasNone()
    {
        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nnoun X.SIF cem_graves ILLEGIBLE_GRAVES\n", CutContentTier.Observation);

        string after = Apply(table, "X.SIF", Sif);

        Assert.Contains(
            "model=cem_graves, noun=ILLEGIBLE_GRAVES, type=scene", after, StringComparison.Ordinal);
    }

    [Fact]
    public void ANounIsReplacedWithoutDisturbingTheRestOfTheLine()
    {
        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nnoun X.SIF wod_dectree01 WOODS\n", CutContentTier.Observation);

        string after = Apply(table, "X.SIF", Sif);

        Assert.Contains("model=wod_dectree01,noun=WOODS,type=scene", after, StringComparison.Ordinal);

        // The camera entry names the same model and is not a binding. Rewriting it would
        // move the close-up shot, which is the sort of change nobody would connect to this.
        Assert.Contains("model=wod_dectree01, angle={1,2}", after, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditThatCannotFindItsLineSaysSoAndChangesNothing()
    {
        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nrule X.NVC LANTERN LOOK GABE_ALL\n", CutContentTier.Observation);

        var diagnostics = new DiagnosticBag();
        byte[] original = Encoding.Latin1.GetBytes(Nvc);
        byte[] after = table.Apply("X.NVC", original, diagnostics);

        Assert.Same(original, after);
        Assert.Equal(1, table.Failed);
        Assert.Equal(0, table.Applied);
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1190");
    }

    [Fact]
    public void AnAssetTheTableSaysNothingAboutIsReturnedUntouched()
    {
        CutContent table = CutContent.Open(CutContentTier.All);

        byte[] original = Encoding.Latin1.GetBytes("anything at all");

        Assert.False(table.Handles("SOMETHING.BMP"));
        Assert.Same(original, table.Apply("SOMETHING.BMP", original));
    }

    [Fact]
    public void TheSameAssetIsEditedOnceHoweverOftenItIsRead()
    {
        CutContent table = CutContent.Parse(
            "[OBSERVATION]\nrule X.NVC ROCKS LOOK GABE_ALL\n", CutContentTier.Observation);

        byte[] original = Encoding.Latin1.GetBytes(Nvc);

        byte[] first = table.Apply("X.NVC", original);
        byte[] second = table.Apply("X.NVC", original);

        Assert.Same(first, second);
        Assert.Equal(1, table.Applied);
    }

    [Fact]
    public void TheTierDecidesHowMuchOfTheTableIsTaken()
    {
        const string Table =
            "[OBSERVATION]\nrule X.NVC ROCKS LOOK GABE_ALL\n"
            + "[PUZZLE]\nrule X.NVC ROCKS PICKUP GABE_ALL\n";

        Assert.Equal(1, CutContent.Parse(Table, CutContentTier.Observation).EditCount);
        Assert.Equal(2, CutContent.Parse(Table, CutContentTier.All).EditCount);
    }

    private static string Apply(CutContent table, string name, string text) =>
        Encoding.Latin1.GetString(table.Apply(name, Encoding.Latin1.GetBytes(text)));
}

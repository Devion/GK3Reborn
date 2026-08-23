using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for reading animations, and for finding the one a script meant.
/// </summary>
/// <remarks>
/// The format itself is easy — an INI file with a frame count. What is not easy is the
/// naming: a script says <c>StartVoiceOver("0NQIB44QR1", 2)</c> and means two files, called
/// something else, in a directory of 7,400. Getting that wrong made every one of the
/// game's 4,642 voice-overs instantaneous, so it is what these mostly check.
/// </remarks>
public sealed class AnimationFileTests
{
    private static AnimationFile Parse(string text, DiagnosticBag? bag = null) =>
        AnimationFile.Parse(text, "TEST", bag ?? new DiagnosticBag());

    [Fact]
    public void An_animation_is_as_long_as_its_frame_count_at_fifteen_frames_a_second()
    {
        AnimationFile animation = Parse("[HEADER]\n150\n");

        Assert.Equal(150, animation.FrameCount);
        Assert.Equal(10.0, animation.Duration, 3);
    }

    [Fact]
    public void An_animation_with_no_frame_count_is_reported_rather_than_guessed_at()
    {
        var bag = new DiagnosticBag();

        Assert.Equal(0, Parse("[SOUNDS]\n0\n", bag).FrameCount);
        Assert.Contains(bag.Items, d => d.Code == "GK3R1110");
    }

    [Fact]
    public void The_sounds_and_the_frames_they_play_on_are_read()
    {
        AnimationFile animation = Parse(
            "[HEADER]\n150\n\n[SOUNDS]\n2\n0,ClownTile1,100\n40,ClownTile2,60\n");

        Assert.Equal(
            [new AnimationSound(0, "ClownTile1", 100), new AnimationSound(40, "ClownTile2", 60)],
            animation.Sounds);
    }

    [Fact]
    public void The_vertex_animations_it_starts_are_read()
    {
        AnimationFile animation = Parse(
            "[HEADER]\n60\n\n[ACTIONS]\n1\n0,GRA_CS3_WRDB_OPEN.ACT, 0, 0, 0, 0\n");

        Assert.Equal(0, animation.Actions[0].Frame);
        Assert.Equal("GRA_CS3_WRDB_OPEN.ACT", animation.Actions[0].Name);
    }

    [Fact]
    public void An_action_line_with_no_numbers_places_nothing()
    {
        // 4,984 of the corpus's 9,417 action lines — every walk cycle and every talking
        // fidget. They mean "play this wherever the model is standing".
        AnimationFile animation = Parse("[HEADER]\n60\n\n[ACTIONS]\n1\n0,GRA_WALK.ACT\n");

        Assert.Null(Assert.Single(animation.Actions).Placement);
    }

    [Fact]
    public void An_action_line_of_zeroes_is_a_placement_of_nothing()
    {
        // Carrying the numbers is what makes a clip absolute, not what the numbers say.
        // Eight zeros is an absolute clip whose offset happens to be nothing: it plays at
        // the coordinates it was authored at, which is not the same thing as playing
        // wherever its model is standing.
        //
        // 3,931 lines are written this way — two fifths of the corpus — and reading them
        // as "no placement" put every scripted set piece a character performs wherever
        // that character happened to be. Mosely read his newspaper out beyond the dining
        // room while the paper, being a prop, stayed on the table.
        AnimationFile animation = Parse(
            "[HEADER]\n60\n\n[ACTIONS]\n1\n0,MOS_MOSDINPAPERSHAKE.ACT,0,0,0,0,0,0,0,0\n");

        AnimationPlacement placement = Assert.Single(animation.Actions).Placement!.Value;

        Assert.Equal(Vector3.Zero, placement.Position);
        Assert.Equal(0f, placement.Heading);
    }

    [Fact]
    public void An_absolute_action_line_negates_the_first_offset_and_swaps_y_with_z()
    {
        // Both quirks are the original's: the first offset goes actor-to-model and is
        // wanted the other way round, and the assets came out of Maya with y and z
        // swapped. Reproducing them is the difference between a character standing on the
        // floor and one standing in a wall.
        AnimationFile animation = Parse(
            "[HEADER]\n60\n\n[ACTIONS]\n1\n0,GRA_WALK.ACT,1,2,3,0,10,20,30,0\n");

        AnimationPlacement placement = Assert.Single(animation.Actions).Placement!.Value;

        // World-to-model (10, 30, 20), plus the negated and swapped model-to-actor
        // (-1, -3, -2).
        Assert.Equal(9f, placement.Position.X, 4);
        Assert.Equal(27f, placement.Position.Y, 4);
        Assert.Equal(18f, placement.Position.Z, 4);
        Assert.Equal(0f, placement.Heading, 4);
    }

    [Fact]
    public void The_heading_is_the_difference_of_the_two_headings_in_radians()
    {
        AnimationFile animation = Parse(
            "[HEADER]\n60\n\n[ACTIONS]\n1\n0,GRA_WALK.ACT,0,0,0,30,0,0,0,120\n");

        AnimationPlacement placement = Assert.Single(animation.Actions).Placement!.Value;

        Assert.Equal(90f * MathF.PI / 180f, placement.Heading, 4);
    }

    [Fact]
    public void A_caption_keeps_the_commas_that_are_part_of_the_sentence()
    {
        // The line is <frame>,SpeakerCaption,<end>,<noun>,<caption> and the caption is a
        // sentence, so anything that treats every comma as a field separator loses most of
        // what was said.
        AnimationFile animation = Parse(
            "[HEADER]\n200\n\n[GK3]\n1\n40,SpeakerCaption, 200, GRACE,One, two, three\n");

        AnimationCaption caption = Assert.Single(animation.Captions);

        Assert.Equal(40, caption.Frame);
        Assert.Equal(200, caption.EndFrame);
        Assert.Equal("GRACE", caption.Speaker);
        Assert.Equal(3, caption.Text.Split(',').Length);
    }

    [Fact]
    public void An_ordinary_line_names_its_speaker_and_its_words_separately()
    {
        // 7,380 of the game's lines are written this way against 211 of the other, so a
        // reader that handles only SpeakerCaption understands three percent of the dialogue.
        AnimationFile animation = Parse(
            "[HEADER]\n49\n\n[GK3]\n3\n0,SPEAKER,GABRIEL\n" +
            "0,CAPTION,Knight.  Gabriel Knight.\n46,DIALOGUECUE\n");

        AnimationCaption caption = Assert.Single(animation.Captions);

        Assert.Equal("GABRIEL", caption.Speaker);
        Assert.Equal("Knight.  Gabriel Knight.", caption.Text);
    }

    [Fact]
    public void A_caption_is_not_said_twice()
    {
        // The INI reader repeats a bare keyword as its own value, because the files that
        // need it rely on the value never being empty. Putting a sentence back together from
        // that naively gives "Way to go=Way to go".
        AnimationFile animation = Parse(
            "[HEADER]\n30\n\n[GK3]\n2\n0,SPEAKER,GRACE\n0,CAPTION,Way to go\n");

        Assert.Equal("Way to go", Assert.Single(animation.Captions).Text);
    }

    [Fact]
    public void The_lip_synch_nodes_are_passed_over()
    {
        // 98,153 of them across the corpus, a mouth shape per frame. Reading one needs a
        // face with shapes to put it into.
        AnimationFile animation = Parse(
            "[HEADER]\n30\n\n[GK3]\n3\n0,LIPSYNCH,MOUTH,A\n" +
            "0,SPEAKER,GRACE\n0,CAPTION,Yes\n");

        Assert.Equal("Yes", Assert.Single(animation.Captions).Text);
    }

    [Fact]
    public void A_line_of_dialogue_is_found_under_the_language_prefix()
    {
        // Scripts never write the prefix. The engine adds it, which is why a licence plate
        // taken straight out of an action file matches nothing on disk.
        var library = new AnimationLibrary(
            name => name == "E0NQIB44QR1.YAK" ? "[HEADER]\n45\n" : null);

        Assert.Equal(3.0, library.SecondsOf("0NQIB44QR1"), 3);
    }

    [Fact]
    public void An_ordinary_animation_is_found_without_one()
    {
        var library = new AnimationLibrary(
            name => name.Equals("GRACS3WRDBOPEN.ANM", StringComparison.OrdinalIgnoreCase)
                ? "[HEADER]\n30\n"
                : null);

        Assert.Equal(2.0, library.SecondsOf("GraCs3WrdbOpen"), 3);
    }

    [Fact]
    public void A_voice_over_of_several_lines_is_several_animations_in_a_row()
    {
        // The last character of the plate is a sequence number, and each line is the plate
        // with the next one in its place. Three lines of thirty frames is six seconds, not
        // two.
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["E0NQIB44QR1.YAK"] = "[HEADER]\n30\n",
            ["E0NQIB44QR2.YAK"] = "[HEADER]\n30\n",
            ["E0NQIB44QR3.YAK"] = "[HEADER]\n30\n",
        };

        var library = new AnimationLibrary(
            name => files.TryGetValue(name, out string? text) ? text : null);

        Assert.Equal(2.0, library.SecondsOfVoiceOver("0NQIB44QR1", 1), 3);
        Assert.Equal(6.0, library.SecondsOfVoiceOver("0NQIB44QR1", 3), 3);
    }

    [Fact]
    public void The_sequence_carries_on_past_nine_into_the_letters()
    {
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EPLATE9.YAK"] = "[HEADER]\n15\n",
            ["EPLATEA.YAK"] = "[HEADER]\n15\n",
            ["EPLATEB.YAK"] = "[HEADER]\n15\n",
        };

        var library = new AnimationLibrary(
            name => files.TryGetValue(name, out string? text) ? text : null);

        Assert.Equal(3.0, library.SecondsOfVoiceOver("PLATE9", 3), 3);
    }

    [Fact]
    public void A_line_that_is_missing_costs_nothing_rather_than_ending_the_conversation()
    {
        // 45 of the corpus's 4,961 lines have no file. A conversation containing one should
        // still take roughly as long as it takes.
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EPLATE1.YAK"] = "[HEADER]\n15\n",
            ["EPLATE3.YAK"] = "[HEADER]\n15\n",
        };

        var library = new AnimationLibrary(
            name => files.TryGetValue(name, out string? text) ? text : null);

        Assert.Equal(2.0, library.SecondsOfVoiceOver("PLATE1", 3), 3);
    }

    [Fact]
    public void An_animation_that_is_not_there_is_only_looked_for_once()
    {
        int looks = 0;

        var library = new AnimationLibrary(_ =>
        {
            looks++;
            return null;
        });

        Assert.Equal(0, library.SecondsOf("NOPE"));
        Assert.Equal(0, library.SecondsOf("NOPE"));

        // Four names tried, once. The thing most likely to ask twice is a script in a loop.
        Assert.Equal(4, looks);
        Assert.Equal(1, library.Count);
    }
}

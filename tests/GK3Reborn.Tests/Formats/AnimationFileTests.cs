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

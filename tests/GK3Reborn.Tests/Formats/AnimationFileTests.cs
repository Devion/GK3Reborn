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
    public void The_room_surfaces_it_repaints_are_read()
    {
        // The bar's dance floor. Three checker patterns cycled on a loop is the whole of
        // what makes a floor flash, and the section was walked past in silence.
        AnimationFile animation = Parse(
            "[HEADER]\n33\n\n[STEXTURES]\n2\n" +
            "0,rl2_disco_a,rl2floor,checker_02\n\n4,rl2_disco_a,rl2floor,checker_03\n");

        Assert.Equal(
            [
                new AnimationSceneTexture(0, "rl2_disco_a", "rl2floor", "checker_02"),
                new AnimationSceneTexture(4, "rl2_disco_a", "rl2floor", "checker_03"),
            ],
            animation.SceneTextures);
    }

    [Fact]
    public void The_room_objects_it_shows_and_hides_are_read()
    {
        AnimationFile animation = Parse(
            "[HEADER]\n20\n\n[SVISIBILITY]\n2\n" +
            "0,lhe_a,lhecurtain,off\n10,lhe_a,lhecurtain,on\n");

        Assert.Equal(
            [
                new AnimationSceneVisibility(0, "lhe_a", "lhecurtain", false),
                new AnimationSceneVisibility(10, "lhe_a", "lhecurtain", true),
            ],
            animation.SceneVisibility);
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
    public void A_moment_speaks_the_lines_it_names_itself()
    {
        // ECOFFEEPOT.MOM, the dining room's spit take. Neither of the two lines it names is
        // a call in DIN110A, so a reader that walks past DIALOGUE loses both of them —
        // "Mosely?  Is that YOU?" is the second — and the ContinueDialogue the script makes
        // afterwards, which continues the run these started.
        AnimationFile animation = Parse(
            "[HEADER]\n141\n\n[GK3]\n3\n15,DIALOGUE,E174AY0W5Z5\n" +
            "62,DIALOGUE,E174AY0W5Z6\n59,CAMERA,VIEW_OF_SPIT\n");

        Assert.Equal(
            [
                new AnimationDialogue(15, "E174AY0W5Z5"),
                new AnimationDialogue(62, "E174AY0W5Z6"),
            ],
            animation.Dialogue);

        // The plate is kept exactly as written, language letter and all: the animation
        // library resolves a name with or without one, and stripping it would change the
        // stem a later ContinueDialogue carries on from.
        Assert.Equal(new AnimationShot(59, "VIEW_OF_SPIT", false), Assert.Single(animation.Shots));
    }

    [Fact]
    public void A_camera_node_says_whether_the_view_travels_or_cuts()
    {
        AnimationFile animation = Parse(
            "[HEADER]\n60\n\n[GK3]\n2\n38,CAMERA,TOMB_CIN, glide\n15,CAMERA,BABY_CU,glide\n");

        Assert.All(animation.Shots, shot => Assert.True(shot.Glide));
        Assert.Equal("TOMB_CIN", animation.Shots[0].Camera);
    }

    [Fact]
    public void A_mood_is_worn_and_an_expression_is_over_when_it_has_played()
    {
        // EPAINTINGS.MOM carries one of each, five frames apart. They are the same line
        // with one difference and it is the whole difference: a mood stays on until
        // something takes it off, which is why the two cannot be read as one.
        AnimationFile animation = Parse(
            "[HEADER]\n90\n\n[GK3]\n2\n65,EXPRESSION, GRACE, SURPRISED\n" +
            "75,MOOD, GRACE, HALFANGRY\n");

        Assert.Equal(
            [
                new AnimationMood(65, "GRACE", "SURPRISED", false),
                new AnimationMood(75, "GRACE", "HALFANGRY", true),
            ],
            animation.Moods);
    }

    [Fact]
    public void A_line_of_dialogue_carries_the_music_that_changes_under_it()
    {
        // E01KED3S4U6 — "Yes, they dropped Grace at the hotel and took off.  But I'm afraid
        // I have bad news." The lobby's soundtrack stops at frame 40 and the fight's comes
        // up at 50, in the middle of the sentence, which is why a line is the clock these
        // are cut against rather than the script that started it.
        AnimationFile animation = Parse(
            "[HEADER]\n125\n\n[GK3]\n3\n40,StopAllSoundTracks\n" +
            "50,PlaySoundTrack,FightDrone.STK\n128,StopSoundtrack,R33StoryIn.STK\n");

        Assert.Equal(
            [
                new AnimationMusic(40, null, Stop: true),
                new AnimationMusic(50, "FightDrone.STK", Stop: false),
                new AnimationMusic(128, "R33StoryIn.STK", Stop: true),
            ],
            animation.Music);

        // And it is a performance, which is what the flag has always meant.
        Assert.True(animation.StartsSoundtrack);
    }

    [Fact]
    public void Stopping_every_soundtrack_names_none_and_stopping_one_names_it()
    {
        AnimationFile animation = Parse("[HEADER]\n30\n\n[GK3]\n1\n1,StopAllSoundTracks\n");

        Assert.Null(Assert.Single(animation.Music).Track);
        Assert.True(Assert.Single(animation.Music).Stop);

        // Nothing is started, so it is not a performance — a file that only silences
        // things is not a scene happening.
        Assert.False(animation.StartsSoundtrack);
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
    public void A_moment_is_found_under_its_own_extension()
    {
        // StartMom("coffeepot") asks for "Ecoffeepot" and the file is ECOFFEEPOT.MOM. Until
        // the extension was tried, every one of the game's 39 moments resolved to nothing
        // and played nothing, waited on by a script that was told it took no time: the
        // dining room lost Gabriel's spit take, two lines, five sounds, a camera cut, and
        // Mosely folding his newspaper onto the table before the conversation.
        var library = new AnimationLibrary(
            name => name.Equals("ECOFFEEPOT.MOM", StringComparison.OrdinalIgnoreCase)
                ? "[HEADER]\n141\n"
                : null);

        Assert.Equal(141 / 15.0, library.SecondsOf("Ecoffeepot"), 3);
    }

    [Fact]
    public void An_animation_beats_a_moment_of_the_same_name()
    {
        // DEFAULT is the one name in the corpus that exists as both, and the reference
        // registers .ANM ahead of .MOM. Reversing that would hand every plain lookup of
        // that name a moment instead of the animation it asked for.
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DEFAULT.ANM"] = "[HEADER]\n30\n",
            ["DEFAULT.MOM"] = "[HEADER]\n141\n",
        };

        var library = new AnimationLibrary(
            name => files.TryGetValue(name, out string? text) ? text : null);

        Assert.Equal(2.0, library.SecondsOf("DEFAULT"), 3);
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

        // Six names tried, once. The thing most likely to ask twice is a script in a loop.
        Assert.Equal(6, looks);
        Assert.Equal(1, library.Count);
    }
}

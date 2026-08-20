using System.Numerics;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for reading a soundtrack.
/// </summary>
/// <remarks>
/// A <c>.STK</c> is a little script, not a piece of music: wait a second, play the room's
/// theme, wait five to ten, play a mood. The part that is easy to get wrong is
/// <c>[PRS]</c>, where a run of sections is one step rather than several.
/// </remarks>
public sealed class SoundtrackTests
{
    private static SoundtrackFile Parse(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        return SoundtrackFile.Parse(text, "TEST.STK", diagnostics);
    }

    [Fact]
    public void The_steps_come_back_in_the_order_they_run()
    {
        // R25's, near enough: a wait, the room's theme, a longer wait, a mood.
        SoundtrackFile track = Parse(
            """
            [WAIT]
            MinWaitMS=1000
            Repeat=1

            [SOUND]
            Name=R25Theme1
            Volume=80.0
            Repeat=1
            StopMethod=1
            FadeOutMS=3000

            [WAIT]
            MinWaitMS=5000
            MaxWaitMS=10000
            """,
            out DiagnosticBag diagnostics);

        Assert.Equal(
            [SoundtrackStep.Wait, SoundtrackStep.Sound, SoundtrackStep.Wait],
            track.Nodes.Select(n => n.Step));

        Assert.Equal(1000, track.Nodes[0].MinWaitMs);
        Assert.Equal(1, track.Nodes[0].Repeat);

        SoundtrackSound theme = Assert.Single(track.Nodes[1].Sounds);
        Assert.Equal("R25Theme1", theme.Name);

        // Written 80.0 as often as 80, and the original reads both as a whole number.
        Assert.Equal(80, theme.Volume);
        Assert.Equal(SoundtrackStop.FadeOut, theme.Stop);
        Assert.Equal(3000, theme.FadeOutMs);

        Assert.Equal(10000, track.Nodes[2].MaxWaitMs);
        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void A_run_of_PRS_sections_is_one_step_and_not_several()
    {
        // Reading each as its own step would play all three of the vampire's hisses at
        // once instead of one of them.
        SoundtrackFile track = Parse(
            """
            [PRS]
            Name=VMHiss1.wav
            3D=1

            [PRS]
            Name=VMHiss2.wav
            3D=1

            [PRS]
            Name=VMHiss3.wav
            3D=1

            [WAIT]
            MinWaitMS=500
            """,
            out _);

        Assert.Equal([SoundtrackStep.PickRandom, SoundtrackStep.Wait], track.Nodes.Select(n => n.Step));
        Assert.Equal(
            ["VMHiss1.wav", "VMHiss2.wav", "VMHiss3.wav"],
            track.Nodes[0].Sounds.Select(s => s.Name));
    }

    [Fact]
    public void A_run_of_PRS_sections_at_the_end_of_a_file_still_becomes_a_step()
    {
        SoundtrackFile track = Parse(
            """
            [WAIT]
            MinWaitMS=500

            [PRS]
            Name=One.wav

            [PRS]
            Name=Two.wav
            """,
            out _);

        Assert.Equal(2, track.Nodes.Count);
        Assert.Equal(SoundtrackStep.PickRandom, track.Nodes[^1].Step);
        Assert.Equal(2, track.Nodes[^1].Sounds.Count);
    }

    [Fact]
    public void A_positioned_sound_carries_where_it_comes_from()
    {
        SoundtrackFile track = Parse(
            """
            [SOUND]
            Name=Fountain
            3D=1
            MinDist=150
            MaxDist=600
            X=10.5
            Y=2
            Z=-30
            Follow=vm1
            Loop=1
            FadeInMs=250
            """,
            out _);

        SoundtrackSound sound = Assert.Single(track.Nodes).Sounds[0];

        Assert.True(sound.Is3D);
        Assert.Equal(150f, sound.MinDistance);
        Assert.Equal(600f, sound.MaxDistance);
        Assert.Equal(new Vector3(10.5f, 2f, -30f), sound.Position);
        Assert.Equal("vm1", sound.Follow);
        Assert.True(sound.Loop);
        Assert.Equal(250, sound.FadeInMs);
    }

    [Theory]
    [InlineData("Music", SoundtrackKind.Music)]
    [InlineData("SFX", SoundtrackKind.Effect)]
    [InlineData("Ambient", SoundtrackKind.Ambient)]
    public void What_a_soundtrack_is_for_decides_which_slider_it_obeys(
        string written, SoundtrackKind expected)
    {
        SoundtrackFile track = Parse($"[SOUNDTRACK]\nSoundType={written}\n", out _);

        Assert.Equal(expected, track.Kind);
    }

    [Fact]
    public void A_key_that_means_nothing_is_reported_and_ignored()
    {
        // Real: TITLETHEME.STK writes MisWaitMS where it meant MinWaitMS, and three of the
        // vampire soundtracks write MinDistWaitMS. The original ignores them, so those
        // waits are zero, and matching that matters more than fixing the typo.
        SoundtrackFile track = Parse(
            """
            [WAIT]
            MisWaitMS=5000
            """,
            out DiagnosticBag diagnostics);

        Assert.Equal(0, Assert.Single(track.Nodes).MinWaitMs);
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1101");
    }

    [Fact]
    public void A_section_that_means_nothing_is_reported_and_skipped()
    {
        SoundtrackFile track = Parse("[CHORUS]\nName=Nope\n", out DiagnosticBag diagnostics);

        Assert.Empty(track.Nodes);
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1100");
    }

    [Fact]
    public void Every_sound_it_can_play_is_listed_once()
    {
        SoundtrackFile track = Parse(
            """
            [SOUND]
            Name=Mood2

            [SOUND]
            Name=Mood1

            [SOUND]
            Name=mood2
            """,
            out _);

        Assert.Equal(["Mood1", "Mood2"], track.Sounds);
    }

    [Fact]
    public void An_empty_soundtrack_is_a_soundtrack_with_nothing_in_it()
    {
        SoundtrackFile track = Parse(string.Empty, out DiagnosticBag diagnostics);

        Assert.Empty(track.Nodes);
        Assert.Empty(track.Sounds);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(SoundtrackKind.Ambient, track.Kind);
    }
}

using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for walking a <c>.STK</c> — the little program a room's sound is written as.
/// </summary>
/// <remarks>
/// R25's afternoon is the shape of nearly all of them: wait a second, play the room's
/// theme once, then moods with one to ten seconds between them, round and round. Playing
/// only the first sound of that — which is what happened before — gives a hotel room a
/// permanent hum it never had, and none of the fourteen moods it did.
/// </remarks>
public sealed class SoundtrackProgramTests
{
    private static readonly string[] Hisses = ["HISS1", "HISS2", "HISS3"];

    private static SoundtrackFile Track(string text) =>
        SoundtrackFile.Parse(text, "TEST.STK", new DiagnosticBag());

    private static DeterministicRandom Chance() => new(0x1234_5678_9ABC_DEF0);

    /// <summary>Runs a program for a while, recording what it started and when.</summary>
    private static List<(double At, string Sound)> Run(
        SoundtrackProgram program, double seconds, double step = 0.25, double length = 1.0)
    {
        var started = new List<(double, string)>();
        double now = 0;

        program.Advance(0, sound =>
        {
            started.Add((now, sound.Name));
            return sound.Loop ? 0 : length;
        });

        while (now < seconds)
        {
            now += step;

            program.Advance(step, sound =>
            {
                started.Add((now, sound.Name));
                return sound.Loop ? 0 : length;
            });
        }

        return started;
    }

    [Fact]
    public void AWaitHoldsTheListUpForAsLongAsItSays()
    {
        SoundtrackProgram program = new(
            Track("""
                [WAIT]
                MinWaitMS=2000

                [SOUND]
                Name=THEME
                """),
            Chance());

        // Three seconds: long enough for the wait and the sound, and not long enough for
        // the list to come round to the wait a second time.
        List<(double At, string Sound)> started = Run(program, 3);

        Assert.Single(started, s => s.Sound == "THEME");
        Assert.InRange(started[0].At, 1.9, 2.3);
    }

    [Fact]
    public void AWaitWithARangeTakesSomethingInsideIt()
    {
        SoundtrackProgram program = new(
            Track("""
                [WAIT]
                MinWaitMS=5000
                MaxWaitMS=10000

                [SOUND]
                Name=MOOD
                """),
            Chance());

        List<(double At, string Sound)> started = Run(program, 12, step: 0.1);

        Assert.NotEmpty(started);
        Assert.InRange(started[0].At, 5.0, 10.2);
    }

    [Fact]
    public void TheListGoesRoundAgainWhenItReachesTheEnd()
    {
        // What keeps a hotel room from sounding like a loop: the same fourteen moods, in
        // the same order, with a different gap each time.
        SoundtrackProgram program = new(
            Track("""
                [SOUND]
                Name=MOOD1

                [WAIT]
                MinWaitMS=1000
                """),
            Chance());

        List<(double At, string Sound)> started = Run(program, 12, length: 1.0);

        Assert.True(started.Count >= 4, $"went round {started.Count} time(s)");
        Assert.All(started, s => Assert.Equal("MOOD1", s.Sound));
    }

    [Fact]
    public void ASoundThatLoopsIsTheEndOfTheWalk()
    {
        // A soundtrack meant to be continuous is an introduction and then something that
        // loops. Nothing after it is ever reached, which is the original's own behaviour
        // and is why 83 of the corpus's files have a bed and the rest do not.
        SoundtrackProgram program = new(
            Track("""
                [SOUND]
                Name=INTRO
                Loop=1

                [SOUND]
                Name=NEVER
                """),
            Chance());

        List<(double At, string Sound)> started = Run(program, 20);

        Assert.Single(started);
        Assert.Equal("INTRO", started[0].Sound);
        Assert.True(program.Holding);
    }

    [Fact]
    public void ARunOfAlternativesIsOneStepRatherThanSeveral()
    {
        // Reading each [PRS] as its own step plays all three of the vampire's hisses at
        // once instead of one of them.
        SoundtrackProgram program = new(
            Track("""
                [PRS]
                Name=HISS1

                [PRS]
                Name=HISS2

                [PRS]
                Name=HISS3

                [WAIT]
                MinWaitMS=60000
                """),
            Chance());

        List<(double At, string Sound)> started = Run(program, 2);

        Assert.Single(started);
        Assert.Contains(started[0].Sound, Hisses);
    }

    [Fact]
    public void ANodeStopsHappeningOnceItHasRunAsOftenAsItSays()
    {
        // R25 plays its theme once and its moods for ever; Repeat=1 on the theme is the
        // whole of what says so.
        SoundtrackProgram program = new(
            Track("""
                [SOUND]
                Name=THEME
                Repeat=1

                [SOUND]
                Name=MOOD
                """),
            Chance());

        List<(double At, string Sound)> started = Run(program, 12, length: 1.0);

        Assert.Single(started, s => s.Sound == "THEME");
        Assert.True(started.Count(s => s.Sound == "MOOD") > 1);
    }

    [Fact]
    public void ANodeThatNeverHappensStillSpendsItsTurn()
    {
        // A chance of nothing at all, which the corpus writes: the node is stepped over
        // and its repeat is spent, so a soundtrack made only of these finishes.
        SoundtrackProgram program = new(
            Track("""
                [SOUND]
                Name=NEVER
                Random=0
                Repeat=1

                [SOUND]
                Name=ALSONEVER
                Random=0
                Repeat=1
                """),
            Chance());

        List<(double At, string Sound)> started = Run(program, 5);

        Assert.Empty(started);
        Assert.True(program.Finished);
    }

    [Fact]
    public void ASoundtrackPlayedOnceStopsAtTheEndOfItsList()
    {
        SoundtrackProgram program = new(
            Track("""
                [SOUND]
                Name=ONCE
                """),
            Chance(),
            loops: false);

        List<(double At, string Sound)> started = Run(program, 10, length: 1.0);

        Assert.Single(started);
        Assert.True(program.Finished);
    }

    [Fact]
    public void TwoRunsOfTheSameSoundtrackMakeTheSameNoisesAtTheSameMoments()
    {
        // ADR 0004. A room that sounds different every time cannot be compared against
        // itself, and a recorded playthrough is only evidence if it repeats.
        string text = """
            [WAIT]
            MinWaitMS=1000
            MaxWaitMS=9000

            [PRS]
            Name=A

            [PRS]
            Name=B

            [PRS]
            Name=C
            """;

        List<(double At, string Sound)> first = Run(new SoundtrackProgram(Track(text), Chance()), 60);
        List<(double At, string Sound)> second = Run(new SoundtrackProgram(Track(text), Chance()), 60);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnEmptySoundtrackDoesNothingRatherThanSpinning()
    {
        SoundtrackProgram program = new(Track(string.Empty), Chance());

        Assert.Empty(Run(program, 5));
    }
}

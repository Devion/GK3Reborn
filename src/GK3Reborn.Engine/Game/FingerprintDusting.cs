// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game;

/// <summary>
/// The kit's own sound effects.
/// </summary>
public static class DustingSounds
{
    /// <summary>Lifting the brush out of the kit.</summary>
    public const string TakeBrush = "GABGETTAPE.WAV";

    /// <summary>One sweep of the brush, alternating so it does not machine-gun.</summary>
    public static readonly string[] Sweep = ["COATRUB1.WAV", "COATRUB2.WAV"];

    /// <summary>Tearing a strip off the roll.</summary>
    public const string TakeTape = "GABWRAPTAPE.WAV";

    /// <summary>Pressing it onto a print.</summary>
    public const string Press = "GABPUTTAPE.WAV";

    /// <summary>And onto the cloth, which is where it is kept.</summary>
    public const string Keep = "GABPOCKETTAPE.WAV";
}

/// <summary>What the player has picked up out of the kit.</summary>
public enum InHand
{
    /// <summary>Nothing.</summary>
    Nothing,

    /// <summary>The brush, clean, which finds nothing until it has been dipped.</summary>
    Brush,

    /// <summary>The brush with powder on it.</summary>
    DustedBrush,

    /// <summary>A strip of tape, for taking a print that has been brought out.</summary>
    Tape,

    /// <summary>The tape with a print on it, which goes on the cloth to be kept.</summary>
    TapeWithPrint,
}

/// <summary>One print on the thing being dusted, as far as the player has got with it.</summary>
public sealed class RevealedPrint
{
    /// <summary>What it is, and what taking it is worth.</summary>
    public required Fingerprint Print { get; init; }

    /// <summary>How far the powder has brought it out, nought to one.</summary>
    public float Shown { get; set; }

    /// <summary>Whether it is on the cloth.</summary>
    public bool Taken { get; set; }

    /// <summary>Whether the tape can be pressed on it.</summary>
    public bool Ready => Shown >= 1f;
}

/// <summary>
/// What the kit does next: a line, a question, the kit put away. Any of them may be
/// nothing, and a step with nothing in it is the usual answer.
/// </summary>
/// <param name="Say">A dialogue licence plate to say, or null.</param>
/// <param name="Glass">
/// Whether this is one of the lobby's two glasses, whose print is not simply whose the
/// table says: the caller runs <see cref="DirtyGlasses"/> for it.
/// </param>
/// <param name="Close">Whether the kit is put away once the line is over.</param>
public readonly record struct DustingStep(
    string? Say = null,
    bool Glass = false,
    bool Close = false)
{
    /// <summary>Nothing happened.</summary>
    public static DustingStep None => default;

    /// <summary>Whether anything at all is to be done.</summary>
    public bool Anything => Say is not null || Glass || Close;
}

/// <summary>
/// One session with the fingerprint kit: what is in hand, how far the powder has brought
/// each print out, and which of them are on the cloth.
/// </summary>
public sealed class FingerprintDusting
{
    /// <summary>How far the brush goes over a bare surface before Gabriel is sure.</summary>
    public const float SureItIsBare = 800f;

    /// <summary>
    /// How much of a print one pixel of brushing brings out, per second. The whole formula
    /// is <c>moved * seconds * 0.2</c>, which is a couple of seconds of working the brush
    /// back and forth over one print.
    /// </summary>
    public const float BrushedOut = 0.2f;

    private readonly GameState _story;
    private readonly List<RevealedPrint> _prints = [];
    private float _overNothing;
    private int _taken;
    private float _sinceSound;
    private int _sweeps;

    /// <summary>Opens the kit over something.</summary>
    /// <param name="noun">What is being dusted, as the script names it.</param>
    /// <param name="story">The game.</param>
    public FingerprintDusting(string noun, GameState story)
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(story);

        _story = story;
        Noun = noun;
        Thing = FingerprintKit.Thing(noun, story.Timeblock);

        foreach (Fingerprint print in Thing?.Prints ?? [])
        {
            // A print already in the bag stays out of the powder's way: it is drawn, and
            // pressing tape on it says so rather than giving it twice. Which is also what
            // shifts the line said as the next one comes off.
            bool had = Held(print, story);

            _prints.Add(new RevealedPrint
            {
                Print = print,
                Shown = had ? 1f : 0f,
                Taken = had,
            });

            if (had)
            {
                _taken++;
            }
        }
    }

    /// <summary>What is being dusted.</summary>
    public string Noun { get; }

    /// <summary>Its picture and its prints, or null when the kit has no entry for it.</summary>
    public DustedThing? Thing { get; }

    /// <summary>What the player has picked up.</summary>
    public InHand Holding { get; private set; }

    /// <summary>The prints, in the order the table lists them.</summary>
    public IReadOnlyList<RevealedPrint> Prints => _prints;

    /// <summary>Which print is on the tape, or minus one.</summary>
    public int OnTape { get; private set; } = -1;

    /// <summary>Whether every print has been taken.</summary>
    public bool Finished => _prints.Count > 0 && _prints.All(p => p.Taken);

    /// <summary>
    /// How far through a stroke the brush is, nought to one and round again.
    /// </summary>
    public float Stroke { get; private set; }

    /// <summary>Whether a sweep of the brush finished this frame, for the sound.</summary>
    public bool Sweeping { get; private set; }

    /// <summary>The sound that sweep makes, which alternates so it does not machine-gun.</summary>
    public string SweepSound => DustingSounds.Sweep[_sweeps % DustingSounds.Sweep.Length];

    /// <summary>
    /// How many times a second the brush goes back and forth while it is being worked.
    /// </summary>
    private const float SweepsASecond = 2.5f;

    /// <summary>
    /// How long between one sweep of the sound and the next, in seconds. About the length
    /// of the sample, so one rub runs into the next instead of stuttering over it.
    /// </summary>
    private const float SoundGap = 0.45f;

    /// <summary>Puts whatever is in hand back in the kit.</summary>
    public void PutDown()
    {
        Holding = InHand.Nothing;
        OnTape = -1;
        Stroke = 0f;
        _sinceSound = 0f;
        Sweeping = false;
    }

    /// <summary>
    /// The brush: picked up with an empty hand, put back with the brush already in it.
    /// </summary>
    public void TouchBrush()
    {
        if (Holding == InHand.Nothing)
        {
            Holding = InHand.Brush;
        }
        else if (Holding is InHand.Brush or InHand.DustedBrush)
        {
            PutDown();
        }
    }

    /// <summary>The powder. Only a clean brush takes any.</summary>
    public void TouchDust()
    {
        if (Holding == InHand.Brush)
        {
            Holding = InHand.DustedBrush;
        }
    }

    /// <summary>
    /// The tape: a strip is taken with an empty hand and put back with one already held.
    /// A brush is put down on it, as it is anywhere else in the kit.
    /// </summary>
    public void TouchTape()
    {
        switch (Holding)
        {
            case InHand.Nothing:
                Holding = InHand.Tape;
                break;

            case InHand.Tape:
                PutDown();
                break;

            case InHand.Brush:
            case InHand.DustedBrush:
                PutDown();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The cloth, which is where a print on the tape is kept.
    /// </summary>
    /// <param name="scores">What each score event is worth.</param>
    /// <returns>What follows.</returns>
    public DustingStep TouchCloth(ScoreEvents scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        if (Holding is InHand.Brush or InHand.DustedBrush)
        {
            PutDown();

            return DustingStep.None;
        }

        if (Holding != InHand.TapeWithPrint || OnTape < 0 || OnTape >= _prints.Count)
        {
            return DustingStep.None;
        }

        RevealedPrint print = _prints[OnTape];

        if (print.Taken)
        {
            PutDown();

            return new DustingStep(FingerprintKit.AlreadyTaken);
        }

        print.Taken = true;
        _taken++;
        PutDown();

        // The lobby's two glasses are the one surface whose print is not simply whose the
        // table says, and all of that is decided elsewhere.
        if (DirtyGlasses.IsOne(Noun))
        {
            return new DustingStep(Glass: true, Close: true);
        }

        FingerprintKit.Collect(print.Print, _story, scores);

        // The lines are said in the order the prints come off, not the order they are
        // listed: three off the manuscript is three different lines whichever is taken
        // first.
        string? said = Thing?.Lifted is { } lines && _taken - 1 < lines.Count
            ? lines[_taken - 1]
            : null;

        return new DustingStep(said, Close: Finished);
    }

    /// <summary>
    /// Presses the tape on a print, which only works once the powder has it fully out.
    /// </summary>
    /// <param name="print">Which print.</param>
    public void PressOn(int print)
    {
        if (Holding == InHand.Brush)
        {
            PutDown();

            return;
        }

        if (Holding != InHand.Tape || print < 0 || print >= _prints.Count ||
            !_prints[print].Ready)
        {
            return;
        }

        Holding = InHand.TapeWithPrint;
        OnTape = print;
    }

    /// <summary>
    /// Works the brush over the thing.
    /// </summary>
    /// <param name="over">
    /// Which print the brush is on, or minus one for the thing itself.
    /// </param>
    /// <param name="moved">How far the pointer travelled this frame, in screen pixels.</param>
    /// <param name="seconds">How long the frame took.</param>
    /// <returns>What follows: a print out, or a surface that has nothing on it.</returns>
    public DustingStep Brushed(int over, float moved, float seconds)
    {
        Sweeping = false;

        if (Holding != InHand.DustedBrush || moved <= 0 || Thing is null)
        {
            return DustingStep.None;
        }

        // The brush is being worked, whatever it is over and whether or not anything is
        // coming out of it.
        Stroke = (Stroke + (seconds * SweepsASecond)) % 1f;
        _sinceSound += seconds;

        if (_sinceSound >= SoundGap)
        {
            _sinceSound = 0f;
            Sweeping = true;
            _sweeps++;
        }

        // Nothing on it: the powder says so only once the brush has been over enough of it,
        // and then Gabriel says so and puts the kit away.
        if (_prints.Count == 0)
        {
            if (_overNothing >= SureItIsBare)
            {
                return DustingStep.None;
            }

            _overNothing += moved;

            return _overNothing < SureItIsBare
                ? DustingStep.None
                : new DustingStep(
                    Thing.Nothing is { Length: > 0 } none ? none : null, Close: true);
        }

        if (over < 0 || over >= _prints.Count)
        {
            return DustingStep.None;
        }

        RevealedPrint print = _prints[over];

        if (print.Shown >= 1f)
        {
            return DustingStep.None;
        }

        print.Shown = Math.Clamp(print.Shown + (moved * seconds * BrushedOut), 0f, 1f);

        return print.Shown >= 1f && print.Print.Uncovers is { Length: > 0 } says
            ? new DustingStep(says)
            : DustingStep.None;
    }

    /// <summary>Whether a print is already in the bag, by whichever flag names it.</summary>
    private static bool Held(Fingerprint print, GameState story) =>
        (print.Flag is { Length: > 0 } his && story.GetFlag(his)) ||
        (print.GraceFlag is { Length: > 0 } hers && story.GetFlag(hers));
}

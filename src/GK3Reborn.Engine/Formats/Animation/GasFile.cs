// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GK3Reborn.Formats.Animation;

/// <summary>A <c>.GAS</c> script: what a thing does when nobody is asking it to.</summary>
/// <remarks>
/// <para>
/// GK3 gives some models and every character a script of their own — named on a scene's
/// model line as <c>type=gasprop, gas=lbyfan.gas</c>, or on an actor line as
/// <c>idle=madrc1mapidle.gas, talk=madreltalk.gas, listen=madreltalk.gas</c> — and runs it
/// for as long as the scene is loaded. The lobby's ceiling fans turn because of one, and so
/// does everything a person does while they are not being told to do anything: breathing,
/// shifting their weight, gesturing while they speak.
/// </para>
/// <para>
/// The format is a line an instruction, a keyword and its arguments, with <c>//</c>
/// comments. Arguments are separated by commas or by spaces and the content uses both,
/// sometimes in the same file — <c>ANIM AbeHe1FightFidget, FALSE 50</c> — so both are
/// separators here and neither is significant.
/// </para>
/// <para>
/// The language has 25 keywords across 502 scripts. The half that matters most is the
/// smallest: <c>ONEOF</c> is 1,559 of the corpus's 4,000-odd instructions, and a run of
/// them is <em>one</em> choice, not several — which is what makes an idle read as a person
/// rather than as a loop.
/// </para>
/// </remarks>
public sealed record GasFile
{
    private GasFile(IReadOnlyList<GasStep> steps, IReadOnlyList<string> unsupported)
    {
        Steps = steps;
        Unsupported = unsupported;
    }

    /// <summary>What it does, in order.</summary>
    public IReadOnlyList<GasStep> Steps { get; }

    /// <summary>Keywords found but not understood, each once.</summary>
    public IReadOnlyList<string> Unsupported { get; }

    /// <summary>Where a label sits, or null if the script has no such label.</summary>
    /// <param name="label">The label's name.</param>
    /// <returns>The index of the step after it.</returns>
    public int? LabelAt(string label)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Action == GasAction.Label &&
                string.Equals(Steps[i].Name, label, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// What to play instead when an animation is cut short.
    /// </summary>
    /// <param name="animation">The animation that was interrupted.</param>
    /// <returns>The one that puts the character back, or null.</returns>
    /// <remarks>
    /// <c>USE CLEANUP abebinocbreath, abebinocdown</c> — if the Abbé is interrupted while
    /// breathing through his binoculars, he lowers them rather than snapping to standing
    /// with them still raised. Declared at the top of a script rather than executed, which
    /// is why these are looked up rather than stepped through.
    /// </remarks>
    public string? CleanupFor(string animation)
    {
        ArgumentNullException.ThrowIfNull(animation);

        foreach (GasStep step in Steps)
        {
            if (step.Action == GasAction.Cleanup &&
                string.Equals(step.Name, animation, StringComparison.OrdinalIgnoreCase))
            {
                return step.Other;
            }
        }

        return null;
    }

    /// <summary>Whether every line of it is understood.</summary>
    public bool Complete => Unsupported.Count == 0;

    /// <summary>
    /// Whether the script is one animation that simply runs for ever.
    /// </summary>
    /// <remarks>
    /// <c>ANIM lbyfan_spin</c> and <c>loop</c>, which is the shape of nearly every piece of
    /// scenery in the game: the ceiling fans, the fountains, the fires, the flashing clock.
    /// Worth telling apart because it can be played as a <em>looping clip</em> rather than
    /// as a script that starts the clip again every time round. The difference is a
    /// fifteenth of a second of held pose at each seam, which on a fan going ninety degrees
    /// a second is a visible hitch every four seconds.
    /// </remarks>
    public bool Continuous =>
        Steps.Count(s => s.Action == GasAction.Animate) == 1 &&
        Steps.All(s => s.Action is GasAction.Animate or GasAction.Label
                                or GasAction.Goto or GasAction.Loop);

    /// <summary>Reads a script.</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The script, however much of it is understood.</returns>
    public static GasFile Parse(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.Latin1.GetString(bytes);

        var steps = new List<GasStep>();
        var unsupported = new List<string>();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int comment = line.IndexOf("//", StringComparison.Ordinal);

            if (comment >= 0)
            {
                line = line[..comment].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            // Commas and spaces both separate, and brackets are decoration: CHOOSEWALK
            // writes its list as "( a ,b ,c )" and nothing else uses them.
            string[] parts = line.Split(
                [' ', '\t', ',', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            if (Read(parts) is { } step)
            {
                steps.Add(step);
                continue;
            }

            string keyword = parts[0].ToUpperInvariant();

            if (!unsupported.Contains(keyword, StringComparer.Ordinal))
            {
                unsupported.Add(keyword);
            }
        }

        return new GasFile(steps, unsupported);
    }

    /// <summary>Reads one instruction, or null when the keyword is not one.</summary>
    private static GasStep? Read(string[] parts)
    {
        string keyword = parts[0].ToUpperInvariant();
        string? First = parts.Length > 1 ? parts[1] : null;

        switch (keyword)
        {
            // ANIM <name> [TRUE|FALSE] [percent]. The flag says whether the animation is
            // played relative to where the model already is; the number is a percentage
            // chance of playing it at all, which is what keeps a repeated fidget from
            // reading as a loop.
            case "ANIM" when First is not null:
            case "LOOPANIM" when First is not null:
                return new GasStep(GasAction.Animate, First, 0)
                {
                    Chance = Percent(parts, 2),
                    Relative = !parts.Skip(2).Any(
                        p => p.Equals("FALSE", StringComparison.OrdinalIgnoreCase)),
                    Repeats = keyword == "LOOPANIM",
                };

            // ONEOF <name> [weight]. A run of these is one choice.
            case "ONEOF" when First is not null:
                return new GasStep(GasAction.OneOf, First, 0)
                {
                    Weight = Number(parts, 2) is { } weight and > 0 ? (int)weight : 100,
                };

            // WAIT <seconds>, or WAIT <from> <to> <percent> in the one file that uses it.
            case "WAIT" when Number(parts, 1) is { } seconds:
                return new GasStep(GasAction.Wait, null, seconds)
                {
                    To = Number(parts, 2) ?? seconds,
                    Chance = Percent(parts, 3),
                };

            case "LABEL" when First is not null:
                return new GasStep(GasAction.Label, First, 0);

            case "GOTO" when First is not null:
                return new GasStep(GasAction.Goto, First, 0);

            case "LOOP":
                return new GasStep(GasAction.Loop, null, 0);

            // SET <name>, <value> and INC <name>: one integer register per name, which is
            // the whole of the language's state.
            case "SET" when First is not null:
                return new GasStep(GasAction.Set, First, 0)
                {
                    Value = (int)(Number(parts, 2) ?? 0),
                };

            case "INC" when First is not null:
                return new GasStep(GasAction.Increment, First, 0);

            // IF <name> <op> <value> <label>, with the commas optional everywhere. Both
            // spellings are in the content and splitting on either makes them one shape.
            case "IF" when parts.Length >= 5:
                return new GasStep(GasAction.If, parts[1], 0)
                {
                    Comparison = parts[2],
                    Value = (int)(Number(parts, 3) ?? 0),
                    Other = parts[4],
                };

            // USE says something about the script rather than doing something in it, and
            // what it says depends on the word after it. CLEANUP is 328 of the 341 and
            // names an animation and what to play if it is cut short; the other four
            // subjects — IPOS, NEWIDLE, CLEARFLAG — take one argument and are kept as
            // declarations rather than run.
            case "USE" or "USES" or "USETALK" when parts.Length >= 3:
                return parts[1].Equals("CLEANUP", StringComparison.OrdinalIgnoreCase) &&
                       parts.Length >= 4
                    ? new GasStep(GasAction.Cleanup, parts[2], 0) { Other = parts[3] }
                    : new GasStep(GasAction.Declare, parts[1], 0) { Other = parts[2] };

            case "NEWIDLE" when First is not null:
                return new GasStep(GasAction.NewIdle, First, 0);

            case "WALKTO" when First is not null:
                return new GasStep(GasAction.WalkTo, First, 0);

            case "CHOOSEWALK" when parts.Length > 1:
                return new GasStep(GasAction.ChooseWalk, First, 0)
                {
                    Names = [.. parts.Skip(1)],
                };

            // LOOKAT <who> <what to move> <seconds>.
            case "LOOKAT" when First is not null:
                return new GasStep(GasAction.LookAt, First, Number(parts, 3) ?? 0);

            case "DLG" when First is not null:
                return new GasStep(GasAction.Speak, First, 0);

            case "SETMOOD" when First is not null:
                return new GasStep(GasAction.SetMood, First, 0);

            case "LOCATION" when First is not null:
                return new GasStep(GasAction.AtLocation, First, 0);

            case "RESETIPOS":
                return new GasStep(GasAction.ResetPosition, null, 0);

            // WHENNEAR <who>, <distance>, <label> — a standing condition rather than an
            // instruction: from here on, jump to that label whenever it becomes true.
            case "WHENNEAR" when parts.Length >= 4:
                return Watching(GasAction.WhenNear, parts);

            case "WHENNOLONGERNEAR" when parts.Length >= 4:
                return Watching(GasAction.WhenNoLongerNear, parts);

            // WHENINVIEW <who>, <angle>, <label> [, percent] — the angle is how wide this
            // actor's own sight is, in degrees, not a distance. Mosely notices Gabriel
            // within 90 degrees of the way he is facing and insults him; Gabriel's test
            // idle yawns when Emilio comes within 70 of his. Both of the corpus's two
            // uses carry the angle in the third field, and reading the label from there
            // is a jump to a label named "90".
            case "WHENINVIEW" when parts.Length >= 4:
                return new GasStep(GasAction.WhenInView, parts[1], 0)
                {
                    Value = (int)(Number(parts, 2) ?? 0),
                    Other = parts[3],
                    Chance = Percent(parts, 4),
                };

            default:
                return null;
        }
    }

    private static GasStep Watching(GasAction action, string[] parts) =>
        new(action, parts[1], 0)
        {
            Value = (int)(Number(parts, 2) ?? 0),
            Other = parts[3],

            // The optional fourth: who to measure from, when it is not this script's own
            // actor. The museum is the reason it exists — Estelle's whisper idle watches
            // the distance from Gabriel to LADY_HOWARD, so the two women notice him
            // together rather than one at a time.
            Between = parts.Length > 4 ? parts[4] : null,
        };

    /// <summary>Reads a number from a position, or null when there is not one there.</summary>
    private static double? Number(string[] parts, int index) =>
        index < parts.Length &&
        double.TryParse(
            parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    /// <summary>
    /// Reads a percentage from a position onwards, defaulting to certainty.
    /// </summary>
    /// <remarks>
    /// The chance may follow an optional <c>TRUE</c>/<c>FALSE</c>, so it is looked for
    /// rather than counted to.
    /// </remarks>
    private static int Percent(string[] parts, int from)
    {
        for (int i = from; i < parts.Length; i++)
        {
            if (Number(parts, i) is { } value and > 0 and <= 100)
            {
                return (int)value;
            }
        }

        return 100;
    }
}

/// <summary>One instruction of a <see cref="GasFile"/>.</summary>
/// <param name="Action">What it does.</param>
/// <param name="Name">The animation, label, variable or actor it names.</param>
/// <param name="Seconds">How long it waits or looks, where it does either.</param>
public readonly record struct GasStep(GasAction Action, string? Name, double Seconds)
{
    /// <summary>A second name: a cleanup animation, or the label a condition jumps to.</summary>
    public string? Other { get; init; }

    /// <summary>Every name, where the instruction takes a list of them.</summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>The comparison an <see cref="GasAction.If"/> makes.</summary>
    public string? Comparison { get; init; }

    /// <summary>Whose distance a condition measures, when not this script's own actor.</summary>
    public string? Between { get; init; }

    /// <summary>A number: what to set, what to compare against, how near is near.</summary>
    public int Value { get; init; }

    /// <summary>The chance of doing it at all, as a percentage.</summary>
    /// <remarks>
    /// A hundred when the instruction gives none, which is nearly all of them. It is what
    /// keeps a fidget repeated nine times in a row from reading as a loop.
    /// </remarks>
    public int Chance { get; init; }

    /// <summary>This choice's share of the draw, against the others beside it.</summary>
    public int Weight { get; init; }

    /// <summary>The longest a wait may be, where it is a range.</summary>
    public double To { get; init; }

    /// <summary>Whether the animation plays where the model is rather than where authored.</summary>
    public bool Relative { get; init; }

    /// <summary>Whether the animation repeats rather than being started again.</summary>
    public bool Repeats { get; init; }
}

/// <summary>The instructions a behaviour script is made of.</summary>
public enum GasAction
{
    /// <summary>Play an animation.</summary>
    Animate,

    /// <summary>One of the choices in the run it belongs to.</summary>
    OneOf,

    /// <summary>Do nothing for a while.</summary>
    Wait,

    /// <summary>A place to jump back to.</summary>
    Label,

    /// <summary>Jump to a label.</summary>
    Goto,

    /// <summary>Start again from the top.</summary>
    Loop,

    /// <summary>Put a number in one of the script's registers.</summary>
    Set,

    /// <summary>Add one to a register.</summary>
    Increment,

    /// <summary>Jump to a label when a register compares as stated.</summary>
    If,

    /// <summary>Declare what to play when an animation is cut short.</summary>
    Cleanup,

    /// <summary>Declare something else about the script: its starting spot, a flag to clear.</summary>
    Declare,

    /// <summary>Replace this character's idle with another script.</summary>
    NewIdle,

    /// <summary>Walk to a named spot.</summary>
    WalkTo,

    /// <summary>Walk to one of several named spots.</summary>
    ChooseWalk,

    /// <summary>Turn and look at somebody.</summary>
    LookAt,

    /// <summary>Say a line.</summary>
    Speak,

    /// <summary>Set the character's mood.</summary>
    SetMood,

    /// <summary>Only run this script at a named location.</summary>
    AtLocation,

    /// <summary>Go back to where the character started.</summary>
    ResetPosition,

    /// <summary>Jump when somebody comes within a distance.</summary>
    WhenNear,

    /// <summary>Jump when they stop being within it.</summary>
    WhenNoLongerNear,

    /// <summary>Jump when something comes into view.</summary>
    WhenInView,
}

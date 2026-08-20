// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GK3Reborn.Formats.Animation;

/// <summary>A <c>.GAS</c> script: what a thing does when nobody is asking it to.</summary>
/// <remarks>
/// <para>
/// GK3 gives some models a script of their own, named on the scene's model line as
/// <c>type=gasprop, gas=lbyfan.gas</c>, and runs it for as long as the scene is loaded.
/// The lobby's ceiling fans are the clearest case: two lines, an animation and a loop, and
/// without them the fans hang still over a hotel that is otherwise alive.
/// </para>
/// <para>
/// The format is a line an instruction, a keyword and its arguments, with <c>//</c>
/// comments. Only the part that drives scenery is read here — an animation, a wait, a
/// label and a jump — which covers seventy of the seventy-seven scripts the game's scenes
/// actually name. The other seven, and the several hundred that belong to *characters*,
/// use the branching half of the language: <c>ONEOF</c> to pick an idle at random,
/// <c>WALKTO</c> to send someone across a room, <c>IF</c> and <c>SET</c> over the story's
/// own variables. Those want the Sheep virtual machine behind them and are a separate
/// piece of work; a script using them is reported and left alone rather than half-run.
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

    /// <summary>Whether every line of it is understood.</summary>
    public bool Complete => Unsupported.Count == 0;

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

            string[] parts = line.Split(
                [' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string keyword = parts[0].ToUpperInvariant();

            switch (keyword)
            {
                case "ANIM" when parts.Length > 1:
                    steps.Add(new GasStep(GasAction.Animate, parts[1], 0));
                    break;

                // The same thing, but the animation is asked to repeat rather than the
                // script coming round again. Scenery treats them alike.
                case "LOOPANIM" when parts.Length > 1:
                    steps.Add(new GasStep(GasAction.Animate, parts[1], 0));
                    break;

                case "WAIT" when parts.Length > 1 &&
                                 double.TryParse(
                                     parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out double seconds):
                    steps.Add(new GasStep(GasAction.Wait, null, seconds));
                    break;

                case "LABEL" when parts.Length > 1:
                    steps.Add(new GasStep(GasAction.Label, parts[1], 0));
                    break;

                case "GOTO" when parts.Length > 1:
                    steps.Add(new GasStep(GasAction.Goto, parts[1], 0));
                    break;

                case "LOOP":
                    steps.Add(new GasStep(GasAction.Loop, null, 0));
                    break;

                default:
                    if (!unsupported.Contains(keyword, StringComparer.Ordinal))
                    {
                        unsupported.Add(keyword);
                    }

                    break;
            }
        }

        return new GasFile(steps, unsupported);
    }
}

/// <summary>One instruction of a <see cref="GasFile"/>.</summary>
/// <param name="Action">What it does.</param>
/// <param name="Name">The animation or label it names, where it names one.</param>
/// <param name="Seconds">How long it waits, where it waits.</param>
public readonly record struct GasStep(GasAction Action, string? Name, double Seconds);

/// <summary>The instructions a scenery script is made of.</summary>
public enum GasAction
{
    /// <summary>Play an animation.</summary>
    Animate,

    /// <summary>Do nothing for a while.</summary>
    Wait,

    /// <summary>A place to jump back to.</summary>
    Label,

    /// <summary>Jump to a label.</summary>
    Goto,

    /// <summary>Start again from the top.</summary>
    Loop,
}

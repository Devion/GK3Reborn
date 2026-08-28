// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GK3Reborn.Foundation.Diagnostics;

/// <summary>
/// Where the time went in one scene load.
/// </summary>
/// <remarks>
/// <para>
/// A wall clock and a list of stamps, because that is what answers the question actually
/// asked of a slow load: not "how long did it take" — the loader already reports that —
/// but "which part of it". A profiler answers it too and answers it about a build nobody
/// is running; this ships, so the number a player reports is the number the loader
/// measured on their machine.
/// </para>
/// <para>
/// <b>Steps are wall-clock spans between stamps, not the cost of a named call.</b> A stamp
/// closes whatever has run since the last one, so a step's name has to describe the work
/// before it. That makes the list total exactly the load, with nothing unattributed —
/// which is the property that matters, because the time a breakdown loses is the time the
/// slow thing is hiding in.
/// </para>
/// </remarks>
public sealed class LoadTimeline
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(string Step, double Milliseconds)> _steps = [];
    private TimeSpan _last = TimeSpan.Zero;

    /// <summary>What has been stamped, in order.</summary>
    public IReadOnlyList<(string Step, double Milliseconds)> Steps => _steps;

    /// <summary>How long the whole timeline has been running.</summary>
    public double TotalMilliseconds => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Closes the work since the last stamp and names it.</summary>
    /// <param name="step">What ran, in the words somebody reading a log would want.</param>
    /// <remarks>
    /// Repeating a name adds to it rather than appending a second row, so a step inside a
    /// loop — a texture, a model — comes out as one line with its total on it.
    /// </remarks>
    public void Stamp(string step)
    {
        ArgumentNullException.ThrowIfNull(step);

        TimeSpan now = _clock.Elapsed;
        double taken = (now - _last).TotalMilliseconds;
        _last = now;

        for (int i = 0; i < _steps.Count; i++)
        {
            if (string.Equals(_steps[i].Step, step, StringComparison.Ordinal))
            {
                _steps[i] = (step, _steps[i].Milliseconds + taken);
                return;
            }
        }

        _steps.Add((step, taken));
    }

    /// <summary>The breakdown, as lines to log.</summary>
    /// <param name="floorMilliseconds">
    /// Steps under this are summed into one "the rest" line instead of listed. A load is
    /// twenty-odd steps and most of them are a millisecond; listing those buries the three
    /// that are not.
    /// </param>
    /// <returns>The report, newline-separated, without a trailing newline.</returns>
    public string Report(double floorMilliseconds = 5)
    {
        double total = _steps.Sum(s => s.Milliseconds);
        var listed = _steps.Where(s => s.Milliseconds >= floorMilliseconds).ToList();
        double rest = total - listed.Sum(s => s.Milliseconds);

        int width = listed.Count > 0 ? listed.Max(s => s.Step.Length) : 0;
        var report = new StringBuilder();

        // Slowest first. A breakdown is read to find the worst thing in it, and the order
        // work happened in is already in the names.
        foreach ((string step, double milliseconds) in
                 listed.OrderByDescending(s => s.Milliseconds))
        {
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {step.PadRight(width)}  {milliseconds,7:F1} ms  {milliseconds / total,5:P0}"));
        }

        if (rest >= 0.05)
        {
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {"everything else".PadRight(width)}  {rest,7:F1} ms  {rest / total,5:P0}"));
        }

        return report.ToString().TrimEnd();
    }
}

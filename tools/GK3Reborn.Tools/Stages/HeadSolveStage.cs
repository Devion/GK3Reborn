using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Measures how rigidly every character's head moves, across the whole clip corpus.
/// </summary>
/// <remarks>
/// <para>
/// The head refinement rests on a claim about the data — that a head's vertex track carries
/// a rigid motion and nothing else — and this is where that claim is checked rather than
/// asserted. For every character, every clip and every recorded frame, it fits the authored
/// head onto what the clip says and reports what is left over as a fraction of the head's
/// own width.
/// </para>
/// <para>
/// The number to read is the median, not the maximum. A clip that really does deform a head
/// exists — <c>GAB_GABTE3HDOFF</c>, which is Gabriel's head coming off, and the worst frame
/// in the game at 17% — and one such frame says nothing about the nine hundred clips either
/// side of it, which is why the worst offender is named rather than folded into a figure.
/// </para>
/// <para>
/// As measured: all 56 models with head clips pass, over 3,069 clips and 122,034 recorded
/// frames, at 1.0% of head width for the median model and 4.1% for the worst.
/// </para>
/// </remarks>
public sealed class HeadSolveStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public HeadSolveStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>What the survey found for one character.</summary>
    /// <param name="Model">The model the clips target.</param>
    /// <param name="HeadMesh">Which mesh the head was found to be.</param>
    /// <param name="HeadSpan">How wide the head is, in scene units, without its axis triad.</param>
    /// <param name="Clips">How many clips moved the head.</param>
    /// <param name="Frames">How many recorded frames were fitted.</param>
    /// <param name="Median">Leftover at the median, as a percentage of head width.</param>
    /// <param name="Ninetieth">Leftover at the ninetieth percentile.</param>
    /// <param name="NinetyNinth">Leftover at the ninety-ninth percentile.</param>
    /// <param name="Worst">The largest leftover of any frame.</param>
    /// <param name="WorstClip">Which clip that frame belongs to.</param>
    /// <param name="Refused">Frames the fit would not take, usually a mismatched clip.</param>
    public sealed record Head(
        string Model,
        int HeadMesh,
        float HeadSpan,
        int Clips,
        int Frames,
        float Median,
        float Ninetieth,
        float NinetyNinth,
        float Worst,
        string? WorstClip,
        int Refused);

    /// <summary>Surveys the corpus.</summary>
    /// <param name="source">The game's data directory.</param>
    /// <param name="only">Survey only this model, or null for every character.</param>
    /// <param name="output">Where to write the report, or null to only log it.</param>
    /// <param name="diagnostics">Receives what could not be read.</param>
    /// <returns>True when every character surveyed came back rigid enough to refine.</returns>
    public bool Run(
        string source, string? only, string? output, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(source);

        // Clips are paired to models by the header rather than the filename: 12.9% of the
        // corpus is filed under something other than what it animates.
        Dictionary<string, List<(string Name, ActFile Clip)>> byModel =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in archives.Names(".ACT"))
        {
            if (archives.Read(name) is not { } bytes ||
                ActFile.Read(bytes, name, diagnostics, vertices: true) is not { } clip)
            {
                continue;
            }

            if (only is { Length: > 0 } wanted &&
                !clip.ModelName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (byModel.TryGetValue(clip.ModelName, out List<(string, ActFile)>? clips)
                ? clips
                : byModel[clip.ModelName] = []).Add((name, clip));
        }

        _log($"{byModel.Count} models have clips");

        var heads = new List<Head>();

        foreach ((string model, List<(string Name, ActFile Clip)> clips) in
                 byModel.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (Survey(archives, model, clips) is { } head)
            {
                heads.Add(head);
            }
        }

        heads.Sort((a, b) => b.Median.CompareTo(a.Median));

        _log(string.Empty);
        _log("model  mesh  span   clips  frames    median     p90     p99    worst  worst clip");

        foreach (Head head in heads)
        {
            _log(string.Create(CultureInfo.InvariantCulture,
                $"{head.Model,-6} {head.HeadMesh,4} {head.HeadSpan,6:F1} {head.Clips,6} " +
                $"{head.Frames,7} {head.Median,8:F2}% {head.Ninetieth,6:F2}% " +
                $"{head.NinetyNinth,6:F2}% {head.Worst,7:F2}%  {head.WorstClip}"));
        }

        // The gate. A head whose typical frame is more than this far from rigid is one the
        // refinement would be lying about, and the honest answer for that character is to
        // draw the head the game authored.
        const float limit = 5f;

        List<Head> failing = [.. heads.Where(h => h.Median > limit)];

        _log(string.Empty);
        _log(string.Create(CultureInfo.InvariantCulture,
            $"{heads.Count - failing.Count} of {heads.Count} heads are rigid at the median " +
            $"to better than {limit:F0}% of head width"));

        foreach (Head head in failing)
        {
            diagnostics.Add(new Diagnostic(
                "HEAD001",
                DiagnosticSeverity.Warning,
                string.Create(CultureInfo.InvariantCulture,
                    $"{head.Model} deforms its head by {head.Median:F2}% of its width at the " +
                    $"median; refining it would not reproduce the original animation.")));
        }

        if (output is { Length: > 0 })
        {
            Write(output, heads, limit);
            _log($"written to {output}");
        }

        return failing.Count == 0;
    }

    /// <summary>Fits one character's head across every clip that moves it.</summary>
    private static Head? Survey(
        GameArchives archives, string model, List<(string Name, ActFile Clip)> clips)
    {
        if (archives.Read(model + ".MOD") is not { } bytes)
        {
            return null;
        }

        ModFile parsed;

        try
        {
            parsed = ModFile.Parse(bytes, model + ".MOD");
        }
        catch (InvalidDataException)
        {
            return null;
        }

        // The rig is what the refinement would use, so the survey asks for it the same way
        // rather than finding the head by some second route that could disagree.
        if (HeadRefinement.Apply(parsed, 1).Rig is not { } rig || rig.Span <= 0f)
        {
            return null;
        }

        var residuals = new List<float>();
        string? worstClip = null;
        float worst = -1f;
        int refused = 0;
        int moved = 0;

        foreach ((string name, ActFile clip) in clips)
        {
            bool any = false;

            foreach (int frame in Frames(clip, rig.Mesh))
            {
                if (Fit(clip, rig, frame) is not { } residual)
                {
                    refused++;
                    continue;
                }

                any = true;
                float share = residual * 100f / rig.Span;
                residuals.Add(share);

                if (share > worst)
                {
                    worst = share;
                    worstClip = Path.GetFileNameWithoutExtension(name);
                }
            }

            if (any)
            {
                moved++;
            }
        }

        if (residuals.Count == 0)
        {
            return null;
        }

        residuals.Sort();

        return new Head(
            model,
            rig.Mesh,
            rig.Span,
            moved,
            residuals.Count,
            At(residuals, 0.50f),
            At(residuals, 0.90f),
            At(residuals, 0.99f),
            worst,
            worstClip,
            refused);
    }

    /// <summary>Which frames of a clip record a shape for the head.</summary>
    /// <remarks>
    /// The recorded frames, not every frame the clip runs for. A mesh that does not move is
    /// not written again, so surveying all of them would count one pose many times over and
    /// report the still frames as evidence.
    /// </remarks>
    private static IEnumerable<int> Frames(ActFile clip, int mesh) =>
        clip.Vertices
            .Where(v => v.Mesh == mesh)
            .Select(v => v.Frame)
            .Distinct()
            .OrderBy(f => f);

    /// <summary>What one frame's head leaves over after the best rigid fit.</summary>
    /// <remarks>
    /// <b>The held pose, not only what this frame records.</b> A mesh that has not moved is
    /// not written again, so on most frames a head has one or two of its submeshes recorded
    /// and the rest are still standing at whatever they were last set to. Fitting only the
    /// submeshes written on the exact frame measures a rotation from eleven vertices of a
    /// collar rather than from three hundred of a head, and then reports the resulting
    /// nonsense as evidence that the head deforms — which it did, until this read a clip the
    /// same way playback reads one.
    /// </remarks>
    private static float? Fit(ActFile clip, HeadRig rig, int frame)
    {
        var from = new List<Vector3>();
        var to = new List<Vector3>();

        foreach (int submesh in clip.ShapedSubmeshes(rig.Mesh))
        {
            if (submesh < 0 || submesh >= rig.Rest.Length ||
                clip.ShapeAt(rig.Mesh, submesh, frame) is not { } shape ||
                shape.Count != rig.Rest[submesh].Length)
            {
                continue;
            }

            foreach (int vertex in rig.Sample[submesh])
            {
                from.Add(rig.Rest[submesh][vertex]);
                to.Add(shape[vertex]);
            }
        }

        if (from.Count < 3)
        {
            return null;
        }

        Matrix4x4? fit = RigidFit.Solve(from.ToArray(), to.ToArray(), out float residual);

        return fit is null ? null : residual;
    }

    /// <summary>A percentile of a sorted list.</summary>
    private static float At(List<float> sorted, float part) =>
        sorted[Math.Clamp((int)(part * (sorted.Count - 1)), 0, sorted.Count - 1)];

    /// <summary>Writes the report where the content build can read it.</summary>
    private static void Write(string output, List<Head> heads, float limit)
    {
        var report = new
        {
            schemaVersion = 1,
            stage = "C6.head-solve",
            note =
                "Leftover after the best rigid fit of each character's authored head onto " +
                "every recorded frame of every clip that moves it, as a percentage of head " +
                "width. Small numbers mean the head does not deform and can be refined.",
            medianLimit = limit,
            models = heads,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");

        File.WriteAllText(output, JsonSerializer.Serialize(report, Json) + Environment.NewLine);
    }

    /// <summary>How the report is written. Cached, because the analyser is right about that.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

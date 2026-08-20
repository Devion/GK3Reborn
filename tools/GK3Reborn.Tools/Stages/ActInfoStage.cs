using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Reads every vertex animation in the game and says what is in them.
/// </summary>
/// <remarks>
/// <para>
/// D2 in <c>Plan/06-c6-rig-solve.md</c>. The reader's five invariants are checked as each
/// file is read, so a sweep that comes back clean is the evidence that the format has been
/// understood — and the numbers it reports are what a later change gets compared against.
/// </para>
/// <para>
/// Vertex data is read but not kept unless asked for. The corpus is 399 MB of it and the
/// deltas have to be decoded either way, because a compressed frame is a delta against the
/// previous one; what <c>--verbose</c> costs is holding the results.
/// </para>
/// </remarks>
public sealed class ActInfoStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public ActInfoStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Reads the corpus.</summary>
    /// <param name="source">The game's data directory.</param>
    /// <param name="only">Report only clips targeting this model, or null for all.</param>
    /// <param name="keepVertices">Whether to keep vertex poses, which is most of the data.</param>
    /// <param name="diagnostics">Receives what could not be read.</param>
    /// <returns>True when every clip parsed.</returns>
    public bool Run(string source, string? only, bool keepVertices, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(source);
        IReadOnlyList<string> names = archives.Names(".ACT");

        _log($"{names.Count} vertex animations in {archives.Count} archives");

        int read = 0;
        int refused = 0;
        int rigid = 0;
        long frames = 0;
        long poses = 0;
        long shapes = 0;
        int misnamed = 0;

        Dictionary<string, (int Clips, long Frames)> models =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            if (archives.Read(name) is not { } bytes)
            {
                refused++;
                continue;
            }

            if (ActFile.Read(bytes, name, diagnostics, keepVertices) is not { } clip)
            {
                refused++;
                continue;
            }

            if (only is { Length: > 0 } wanted &&
                !clip.ModelName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            read++;
            frames += clip.FrameCount;
            poses += clip.Transforms.Count;
            shapes += clip.Vertices.Count;

            if (!clip.Deforms)
            {
                rigid++;
            }

            // The header names the model and the filename does not always agree. Clips are
            // named <something>_<action>, and that something is usually but not reliably the
            // model — which is why the header is what pairs a clip to what it animates.
            string leading = Path.GetFileNameWithoutExtension(name).Split('_')[0];

            if (!leading.Equals(clip.ModelName, StringComparison.OrdinalIgnoreCase))
            {
                misnamed++;
            }

            (int clips, long total) = models.GetValueOrDefault(clip.ModelName);
            models[clip.ModelName] = (clips + 1, total + clip.FrameCount);
        }

        _log(string.Create(CultureInfo.InvariantCulture,
            $"{read} read, {refused} refused, {frames} keyframes across {models.Count} models"));

        if (refused > 0)
        {
            // Two of the game's own files have no header at all. They are damaged in the
            // shipped data rather than misread here — the reference implementation refuses
            // them too — so this is reported and not treated as the reader being wrong.
            _log("  the refused ones are listed below; two of the corpus are known to be damaged");
        }

        string share = string.Create(
            CultureInfo.InvariantCulture, $"{rigid * 100f / Math.Max(1, read):F1}");

        _log($"{rigid} are rigid ({share}%) — transforms only, no skinning needed to play them");

        string counted = string.Create(CultureInfo.InvariantCulture, $"{poses} mesh poses");

        _log(counted + (keepVertices
            ? string.Create(CultureInfo.InvariantCulture, $", {shapes} submesh shapes")
            : string.Empty));

        string odd = string.Create(
            CultureInfo.InvariantCulture, $"{misnamed * 100f / Math.Max(1, read):F1}");

        _log($"{misnamed} clips are named for something other than the model they target ({odd}%)");

        foreach ((string model, (int clips, long total)) in
                 models.OrderByDescending(m => m.Value.Clips).Take(8))
        {
            _log(string.Create(CultureInfo.InvariantCulture,
                $"  {model}: {clips} clips, {total} keyframes"));
        }

        return refused == 0;
    }
}

using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering.Materials;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Says which textures the game actually walks on, and how each one is finished.
/// </summary>
/// <remarks>
/// <para>
/// A floor is the surface that shows a shading mistake first. It is large, flat and
/// horizontal, so a specular lobe from anything overhead spreads across the whole of it —
/// the same roughness on the front of a cabinet is invisible and on a floor reads as
/// standing water.
/// </para>
/// <para>
/// Which textures are floors is not a guess and not a matter of what they are called.
/// Every scene's general <c>.SIF</c> names one <c>floor=</c> object, the BSP knows which
/// surfaces belong to that object, and each surface names its texture. That is the
/// definitive list, and it is what this reports: <c>TE3FLOORCRS</c> is a floor and so is
/// <c>TILES</c>, while <c>27FLOOR</c> is not on any floor object in the game.
/// </para>
/// </remarks>
public sealed class FloorMaterialStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public FloorMaterialStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Reports every texture on a floor object.</summary>
    /// <param name="sourceDirectory">The game's Data directory.</param>
    /// <param name="workspace">The content workspace, for the material library.</param>
    /// <param name="diagnostics">Receives what went wrong.</param>
    /// <returns>True when at least one floor was found.</returns>
    public bool Run(string sourceDirectory, string? workspace, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        SurfaceFinishes finishes = workspace is { Length: > 0 }
            ? SurfaceFinishes.Load(
                Path.Combine(workspace, "manifests", "material-library.json"))
            : SurfaceFinishes.Empty;

        // Texture -> the rooms whose floor it is.
        var floors = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        int scenes = 0;

        foreach (string name in Scenes(archives))
        {
            if (archives.ReadText(name + ".SIF") is not { } text)
            {
                continue;
            }

            SceneInitFile sif = SceneInitFile.Parse(text, name + ".SIF");

            if (sif.FloorObject() is not { Length: > 0 } floor)
            {
                continue;
            }

            if (archives.Read(name + ".BSP") is not { } bytes)
            {
                continue;
            }

            BspFile bsp;

            try
            {
                bsp = BspFile.Parse(bytes, name + ".BSP");
            }
            catch (FormatParseException)
            {
                continue;
            }

            scenes++;

            foreach (BspSurface surface in bsp.Surfaces)
            {
                if (surface.ObjectIndex < 0 || surface.ObjectIndex >= bsp.ObjectNames.Count)
                {
                    continue;
                }

                if (!string.Equals(
                        bsp.ObjectNames[surface.ObjectIndex], floor, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!floors.TryGetValue(surface.TextureName, out SortedSet<string>? rooms))
                {
                    rooms = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    floors[surface.TextureName] = rooms;
                }

                rooms.Add(name);
            }
        }

        _log($"{scenes} scene(s) name a floor object; {floors.Count} distinct textures are on one");
        _log(string.Empty);
        _log($"{"texture",-24} {"rough",-6} {"spec",-6} {"rooms",-5} where");

        int glossy = 0;

        foreach ((string texture, SortedSet<string> rooms) in floors)
        {
            SurfaceFinish finish = finishes.Of(texture);

            if (finish.Roughness < 0.5f)
            {
                glossy++;
            }

            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"{texture,-24} {finish.Roughness,-6:F2} {finish.Specular,-6:F2} " +
                $"{rooms.Count,-5} {string.Join(" ", rooms.Take(6))}"));
        }

        _log(string.Empty);
        _log($"{glossy} of {floors.Count} are smoother than 0.5, which on a floor is a polish");

        return floors.Count > 0;
    }

    /// <summary>Every scene the archives hold, by name.</summary>
    private static IEnumerable<string> Scenes(GameArchives archives) =>
        archives.Names(".BSP")
            .Select(n => Path.GetFileNameWithoutExtension(n) ?? string.Empty)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
}

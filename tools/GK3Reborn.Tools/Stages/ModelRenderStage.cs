using System.Globalization;
using GK3Reborn.Rendering.Geometry;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Renders one model straight out of the game's archives to a PNG.
/// </summary>
/// <remarks>
/// <para>
/// The shortest path from shipped data to pixels: open the barns, parse the model, decode
/// the textures it names, upload both, draw. Nothing is pre-converted, so what this
/// produces is evidence about the parsers and the renderer together rather than about an
/// intermediate file.
/// </para>
/// <para>
/// It renders offscreen deliberately. A headless render needs no window, runs on a build
/// agent, and its output can be compared between runs — none of which is true of a
/// screenshot.
/// </para>
/// </remarks>
public sealed class ModelRenderStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public ModelRenderStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Renders one model.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="modelName">Model name, with or without the <c>.MOD</c> extension.</param>
    /// <param name="outputPath">Where to write the PNG.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="heads">How far to subdivide a character's head; zero draws it as authored.</param>
    /// <param name="portrait">
    /// Frame the character's head rather than the whole of them, turned three-eighths of a
    /// turn so the face is seen from one side.
    /// </param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True if something was rendered.</returns>
    public bool Run(
        string sourceDirectory,
        string modelName,
        string outputPath,
        int width,
        int height,
        int heads,
        bool portrait,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // A path to a .glb renders that file instead of asking the archives for a .MOD.
        // Generated geometry has to be looked at on its own before it is scattered over a
        // hillside, and the alternative — grow a tree, load a scene, hunt for it in the
        // frame — is a slow way to find out that a needle spray is too small.
        bool generated = Path.GetExtension(modelName)
            .Equals(".glb", StringComparison.OrdinalIgnoreCase);

        string wanted = generated || Path.GetExtension(modelName)
                .Equals(".MOD", StringComparison.OrdinalIgnoreCase)
            ? modelName
            : modelName + ".MOD";

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        byte[]? modelBytes = generated
            ? (File.Exists(wanted) ? File.ReadAllBytes(wanted) : null)
            : archives.Read(wanted);

        if (modelBytes is null)
        {
            diagnostics.Add(new Diagnostic(
                "RENDER001",
                DiagnosticSeverity.Error,
                generated
                    ? $"There is no file at {wanted}."
                    : $"No archive contains {wanted}."));

            return false;
        }

        ModFile parsed = generated
            ? GlbReader.Parse(modelBytes, wanted)
            : ModFile.Parse(modelBytes, wanted);
        _log($"{wanted}: {parsed.Meshes.Count} meshes, {parsed.TriangleCount} triangles");

        // The same call the game makes, so what is rendered here is what a player sees
        // rather than a second implementation that could drift from it.
        (ModFile model, HeadRig? rig) = HeadRefinement.Apply(parsed, heads);

        if (rig is not null)
        {
            _log(string.Create(CultureInfo.InvariantCulture,
                $"head: mesh {rig.Mesh}, {rig.Span:F1} units across, refined {heads} " +
                $"level(s) to {model.TriangleCount} triangles"));
        }

        using VulkanContext context = VulkanContext.CreateHeadless();
        _log($"device: {context.DeviceName}");

        using var renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        foreach (string texture in model.Meshes
                     .SelectMany(m => m.Submeshes)
                     .Select(s => s.TextureName)
                     .Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Beside the model first, for the same reason the scene loader looks there:
            // a grown tree is painted with foliage drawn for it, which no archive holds.
            string local = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(wanted)) ?? ".", texture + ".PNG");

            if (generated && File.Exists(local))
            {
                geometry.AddTexture(texture, PngReader.Decode(File.ReadAllBytes(local), local));
                continue;
            }

            byte[]? bytes = archives.Read(texture) ?? archives.Read(texture + ".BMP");
            if (bytes is null || !BitmapDecoder.CanDecode(bytes))
            {
                diagnostics.Add(new Diagnostic(
                    "RENDER002",
                    DiagnosticSeverity.Warning,
                    $"{wanted} references a texture no archive contains: {texture}."));

                continue;
            }

            geometry.AddTexture(texture, BitmapDecoder.Decode(bytes, texture));
        }

        geometry.Add(model);

        if (geometry.TriangleCount == 0)
        {
            diagnostics.Add(new Diagnostic(
                "RENDER003", DiagnosticSeverity.Error, $"{wanted} has no drawable geometry."));

            return false;
        }

        Vector3 minimum = geometry.Minimum;
        Vector3 maximum = geometry.Maximum;

        // <b>A portrait is the head, framed on its own.</b> Framing the whole character and
        // cropping would give a face forty pixels across; the head is about a tenth of a
        // standing figure's height, so what is wanted is the camera brought to it. Which
        // mesh is the head is a question already answered, by what it is painted with —
        // an eyelid or a mouth is only ever on a head.
        float azimuth = 0.6f;

        if (portrait)
        {
            if (Bust(model) is not { } bust)
            {
                diagnostics.Add(new Diagnostic(
                    "RENDER004",
                    DiagnosticSeverity.Warning,
                    $"{wanted} has no head to make a portrait of; framing all of it."));
            }
            else
            {
                (minimum, maximum) = bust;

                // Three-eighths of a turn: not the flat passport face the model was built
                // for, and not so far round that one eye is lost. Enough that the nose has
                // a side to it, which is what makes a low-polygon head read as a person.
                // <b>A three-quarter view, on whichever side the model is authored facing.</b>
                // Nearly every character is built looking down one axis and a fixed turn
                // from there gives the same view of all of them — but not quite every one:
                // Emilio is built facing the other way, so a turn that shows the rest their
                // face shows him the back of his head. Measured by rendering each of them
                // through a full turn and looking.
                azimuth = Facing(wanted) - (MathF.PI / 4f);

                _log(string.Create(
                    CultureInfo.InvariantCulture,
                    $"head: ({minimum.X:F1}, {minimum.Y:F1}, {minimum.Z:F1}) .. " +
                    $"({maximum.X:F1}, {maximum.Y:F1}, {maximum.Z:F1})"));
            }
        }

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"bounds: ({minimum.X:F1}, {minimum.Y:F1}, {minimum.Z:F1}) .. " +
            $"({maximum.X:F1}, {maximum.Y:F1}, {maximum.Z:F1})"));

        _log($"textures: {geometry.TextureCount}, triangles: {geometry.TriangleCount}");

        // GK3 is Y-up: model bounds are consistently tallest on Y, and every sun direction
        // recovered from the lightmaps points down that axis.
        DecodedImage image = renderer.Render(
            geometry, width, height, Camera.Framing(minimum, maximum, Vector3.UnitY, azimuth));

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(outputPath, PngWriter.Encode(image));
        _log($"wrote {outputPath}");

        return true;
    }

    /// <summary>
    /// Where a character's head is, in the model's own space.
    /// </summary>
    /// <param name="model">The character.</param>
    /// <returns>The head's corners, or null when the model has no head.</returns>
    /// <remarks>
    /// A little of the neck is kept below it and a little air above, because a head cropped
    /// exactly to its own bounds reads as a floating object rather than as somebody looking
    /// at you.
    /// </remarks>
    private static (Vector3 Minimum, Vector3 Maximum)? Bust(ModFile model)
    {
        if (CharacterHead.Find(model) is not { } which)
        {
            return null;
        }

        ModMesh mesh = model.Meshes[which];
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);

        foreach (ModSubmesh submesh in mesh.Submeshes)
        {
            foreach (Vector3 position in submesh.Positions)
            {
                // <b>Three of a head's points are not on the head.</b> Every one carries an
                // axis triad — a point sixty units out along each axis — which the rig is
                // read from and which is nowhere near the face. Measuring with them in gives
                // a head eighty-five units tall instead of twenty-three, and frames the
                // whole character. HeadRefinement drops them for the same reason.
                if (IsAxisTriad(position))
                {
                    continue;
                }

                Vector3 at = Vector3.Transform(position, mesh.MeshToLocal);

                minimum = Vector3.Min(minimum, at);
                maximum = Vector3.Max(maximum, at);
            }
        }

        if (minimum.X > maximum.X)
        {
            return null;
        }

        // <b>Whatever sits on the head comes with it.</b> A hat is its own mesh, and Lady
        // Howard's is wider than her head and taller than it — framed to the head alone she
        // is a chin under a purple wall. Anything whose middle is within a head's width of
        // the head's middle is part of the portrait: a hat, a wig, a pair of glasses.
        Vector3 middle = (minimum + maximum) * 0.5f;
        float across = MathF.Max(maximum.X - minimum.X, maximum.Z - minimum.Z);

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            if (i == which || Span(model.Meshes[i]) is not { } other)
            {
                continue;
            }

            Vector3 centre = (other.Minimum + other.Maximum) * 0.5f;

            // Above the head's middle and no further out sideways than the head is wide.
            // A body fails the first test and an outstretched arm the second, which is what
            // measuring by plain distance let in — it framed the whole character again.
            if (centre.Y < middle.Y ||
                MathF.Abs(centre.X - middle.X) > across ||
                MathF.Abs(centre.Z - middle.Z) > across)
            {
                continue;
            }

            minimum = Vector3.Min(minimum, other.Minimum);
            maximum = Vector3.Max(maximum, other.Maximum);
        }

        // Shoulders below and hat-room above. A head cropped exactly to its own bounds
        // reads as a floating object rather than as somebody looking at you, and Lady
        // Howard's hat is not part of her head mesh at all.
        float tall = maximum.Y - minimum.Y;

        return (
            minimum - new Vector3(0, tall * 0.30f, 0),
            maximum + new Vector3(0, tall * 0.28f, 0));
    }

    /// <summary>
    /// Which way a character is built facing, in radians about the up axis.
    /// </summary>
    /// <param name="model">The model's name.</param>
    /// <returns>The angle its face looks along.</returns>
    /// <remarks>
    /// Found by rendering the character through a full turn and looking at which frame is
    /// the face. Everything not named here is built looking along zero, which is nearly all
    /// of them.
    /// </remarks>
    private static float Facing(string model) =>
        model.StartsWith("EML", StringComparison.OrdinalIgnoreCase) ? MathF.PI : 0f;

    /// <summary>One mesh's corners, ignoring its rig markers.</summary>
    private static (Vector3 Minimum, Vector3 Maximum)? Span(ModMesh mesh)
    {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);

        foreach (ModSubmesh submesh in mesh.Submeshes)
        {
            foreach (Vector3 position in submesh.Positions)
            {
                if (IsAxisTriad(position))
                {
                    continue;
                }

                Vector3 at = Vector3.Transform(position, mesh.MeshToLocal);

                minimum = Vector3.Min(minimum, at);
                maximum = Vector3.Max(maximum, at);
            }
        }

        return minimum.X > maximum.X ? null : (minimum, maximum);
    }

    /// <summary>Whether a point is one of the head's rig markers rather than the head.</summary>
    private static bool IsAxisTriad(Vector3 point)
    {
        const float Marker = 60f;
        const float Slack = 1e-2f;

        return (Near(point.X, Marker) && Near(point.Y, 0f) && Near(point.Z, 0f)) ||
               (Near(point.X, 0f) && Near(point.Y, Marker) && Near(point.Z, 0f)) ||
               (Near(point.X, 0f) && Near(point.Y, 0f) && Near(point.Z, Marker));

        static bool Near(float value, float to) => MathF.Abs(value - to) < Slack;
    }
}

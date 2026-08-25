using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Materials;
using GK3Reborn.Rendering.Vulkan;
using GK3Reborn.Sheep;
using GK3Reborn.UI.Interaction;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Renders a scene the way the game assembles it, to a PNG.
/// </summary>
/// <remarks>
/// The loading is the engine's own, so what this produces is what the game would show
/// from the same viewpoint rather than a second implementation that can drift from it.
/// Rendering offscreen keeps it usable on a build agent and makes the output comparable
/// between runs.
/// </remarks>
public sealed class SceneRenderStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SceneRenderStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Renders a scene.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="sceneName">Scene name, such as <c>R25</c>.</param>
    /// <param name="timeblock">
    /// A story timeblock such as <c>202P</c>, which decides the scene file's conditions, or
    /// an asset suffix — <c>M</c>, <c>A</c>, <c>E</c>, <c>N</c> — which only picks the bake.
    /// </param>
    /// <param name="cameraName">Which room camera to use; null takes the scene's default.</param>
    /// <param name="rayTracing">Quality level: none, low, med or high.</param>
    /// <param name="outputPath">Where to write the PNG.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="walkOverlay">Whether to draw the walk boundary over the floor.</param>
    /// <param name="walkPath">
    /// Two points to find a way between, as <c>from:to</c>. Either may be the name of one
    /// of the scene's positions or a pair of world coordinates, <c>x,z</c>.
    /// </param>
    /// <param name="pick">
    /// A pixel to report what is under, as <c>x,y</c> from the top-left of the image.
    /// </param>
    /// <param name="nounMap">Where to write a map of what is clickable, if anywhere.</param>
    /// <param name="perform">An action to carry out, as <c>noun:verb</c>.</param>
    /// <param name="advanceSeconds">How much time to let pass afterwards.</param>
    /// <param name="glance">An actor to point at something, as <c>actor:target</c>.</param>
    /// <param name="enhanced">Higher-resolution textures to prefer, or null for none.</param>
    /// <param name="heads">How far to subdivide a character's head; zero draws it as authored.</param>
    /// <param name="relief">Whether the floor's height map is cut into the geometry.</param>
    /// <param name="trees">Whether foliage cards are grown into modelled trees.</param>
    /// <param name="packs">Where the ReBarn volumes are, or null to use loose content only.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True if something was rendered.</returns>
    public bool Run(
        string sourceDirectory,
        string sceneName,
        string? timeblock,
        string? cameraName,
        string? rayTracing,
        string outputPath,
        int width,
        int height,
        bool walkOverlay,
        string? walkPath,
        string? pick,
        string? nounMap,
        string? perform,
        double advanceSeconds,
        string? glance,
        string? enhanced,
        int heads,
        bool relief,
        bool trees,
        string? packs,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(sceneName);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        using VulkanContext context = VulkanContext.CreateHeadless();
        _log($"device: {context.DeviceName}");

        using var renderer = SceneRenderer.Create(context);

        if (RayTracingSettings.Parse(rayTracing) is { } quality)
        {
            if (!renderer.SupportsRayTracing && quality != RayTracingQuality.None)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE009",
                    DiagnosticSeverity.Warning,
                    $"{context.DeviceName} offers no ray tracing; rendering without it."));
            }

            renderer.Quality = quality;
        }
        else if (rayTracing is not null)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE010",
                DiagnosticSeverity.Error,
                $"Unknown ray tracing quality '{rayTracing}'; expected none, low, med or high."));

            return false;
        }

        using SceneGeometry geometry = renderer.CreateGeometry();

        // Off is what the room looked like before a floor could be displaced, which is the
        // only way to compare the two: everything else about the frame is identical.
        geometry.Relief = relief ? ReliefSettings.Default : ReliefSettings.Off;

        SceneRequest request = SceneRequest.For(sceneName, timeblock);

        if (request.State is not null)
        {
            _log($"story: {request.State.Timeblock} in {request.State.Location}, first visit");
        }

        var loader = new SceneLoader(archives, _log) { SmoothHeads = heads };

        // Opened for the whole render, because a packed texture's blocks point into the
        // memory-mapped volume and stay valid only while it is open.
        using RebarnContent? volumes = packs is { Length: > 0 }
            ? RebarnContent.Open(packs, diagnostics)
            : null;

        if (volumes?.Describe() is { } summary)
        {
            _log($"packs: {summary}");
        }

        if (enhanced is { Length: > 0 })
        {
            EnhancedTextures set = EnhancedTextures.Open(enhanced);
            loader.Enhanced = set;
            _log($"enhanced: {set.Count} textures available at {enhanced}");

            // The generated maps sit beside the colour textures, and the surface finishes
            // in the workspace's manifests above them. Without these the tool renders
            // every surface at the shader's own defaults, so it cannot show a material
            // bug at all — a floor the library calls polished comes out matte, and a
            // correction made in the edit layer changes nothing on screen.
            loader.Normals = EnhancedTextures.Open(Beside(enhanced, "normals"));
            loader.Orms = EnhancedTextures.Open(Beside(enhanced, "orm"));
            loader.Heights = EnhancedTextures.Open(Beside(enhanced, "height"));

            SurfaceFinishes finishes = Finishes(enhanced);
            geometry.Materials = finishes;

            _log($"materials: {finishes.Count} measured, {finishes.Reflective} reflective, " +
                 $"{finishes.Metallic} metal" +
                 (finishes.Corrected > 0 ? $", {finishes.Corrected} corrected by hand" : string.Empty));
        }

        if (volumes is not null)
        {
            // The foliage a grown tree is painted with is packed as an ordinary colour
            // texture, so this is what finds it: no special case, just the compressed set
            // answering for a name the archives have never heard of.
            loader.Compressed = CompressedTextures.Open(string.Empty, volumes);
        }

        // Outside the --enhanced block, because the packs are a supply of their own: a
        // shipped game has volumes beside the executable and no content workspace anywhere,
        // and gating the trees on a loose directory would mean nobody who installed the
        // game ever saw one. Reported whether or not there are any, since a render with no
        // trees in it and a render whose trees never loaded look identical.
        TreeLibrary grown = trees
            ? TreeLibrary.Open(
                enhanced is { Length: > 0 } ? Beside(enhanced, "trees") : string.Empty,
                volumes,
                diagnostics)
            : TreeLibrary.Open(string.Empty);

        loader.Trees = grown;
        _log(grown.IsEmpty
            ? "trees: none grown; every foliage card stays flat"
            : $"trees: {grown.Count} grown across {grown.SpeciesCount} species, " +
              (grown.Packed ? "read from the packs" : "read loose"));

        if (glance is { Length: > 0 })
        {
            PointSomebody(loader, archives, request, glance, diagnostics);
        }

        LoadedScene? scene = loader.Load(geometry, request, diagnostics);

        if (scene is null || geometry.TriangleCount == 0)
        {
            return false;
        }

        // The pose the scene states everything opens in. An init anim takes no time — it
        // says where a door rests and how a person is sitting — so unlike anything else
        // this tool leaves out, it belongs in a single frame. Without it Emilio's chair is
        // empty and RC1's copy of the hotel door hangs in its bind pose.
        PoseOpening(archives, scene, geometry, diagnostics);

        // Before anything that draws the world, so that what an action changed - a van
        // moved into the road, a region shut off - is in the picture rather than behind it.
        if (perform is { Length: > 0 })
        {
            Perform(archives, scene, request, perform, diagnostics);
        }

        if (advanceSeconds > 0)
        {
            Advance(archives, scene, request, advanceSeconds, diagnostics);
        }

        if (walkOverlay)
        {
            DrawWalkOverlay(geometry, scene);
        }

        if (walkPath is { Length: > 0 })
        {
            DrawWalkPath(geometry, scene, walkPath, diagnostics);
        }

        if (scene.Ambient.Count > 0)
        {
            _log($"ambient: {string.Join(", ", scene.Ambient)}");
        }

        ReportNounCoverage(scene);

        // With the room's bounds, not without them. They decide two things: which lights
        // are distant keys whose stored range cannot reach the scene, and how the light
        // grid is divided — and a grid over no bounds is one cell holding the whole rig,
        // which is the behaviour it exists to replace.
        renderer.SetLights(
            scene.Lights, new GK3Reborn.Rendering.Vulkan.SceneExtent(geometry.Minimum, geometry.Maximum));

        if (renderer.LightGrid is { } grid)
        {
            _log(
                $"light grid: {grid.Counts.X}x{grid.Counts.Y}x{grid.Counts.Z} cells of " +
                $"{grid.Cell:0} units, {grid.Average:0.0} lights a cell on average and " +
                $"{grid.Busiest} at most" +
                (grid.Overfull > 0 ? $", {grid.Overfull} cells over the limit" : string.Empty));
        }

        _log($"lights: {scene.Lights.Count} authored " +
             $"({scene.Lights.Count(l => l.CastsShadows)} casting shadows in the bake)");

        // An action can point the camera somewhere - CS3's wardrobe cuts to OPEN_WARDROBE
        // as it swings open - so unless the caller asked for a particular angle, the render
        // shows where the story left the view rather than where the scene starts.
        string? angle = cameraName is { Length: > 0 }
            ? cameraName
            : request.State?.CameraAngle is { Length: > 0 } cut ? cut : null;

        Camera camera = SceneLoader.CameraFor(scene, geometry, angle);

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"camera: {scene.CameraNamed(angle)?.Name ?? "framed"} at " +
            $"({camera.Position.X:F1}, {camera.Position.Y:F1}, {camera.Position.Z:F1})"));

        _log($"drawing {geometry.TriangleCount} triangles in {geometry.BatchCount} batches" +
             (geometry.DisplacedTriangles > 0
                 ? $", the floor cut into {geometry.DisplacedTriangles} of them at " +
                   $"{geometry.ReliefCell:0.#} units a cell"
                 : string.Empty));

        if (renderer.Quality != RayTracingQuality.None)
        {
            RayTracingSettings settings = RayTracingSettings.For(renderer.Quality);

            _log($"ray tracing {renderer.Quality}: {geometry.TraceableTriangleCount} opaque " +
                 $"triangles traced, {settings.ShadowLights} shadowed lights at " +
                 $"{settings.ShadowSamples} ray(s), {settings.AmbientOcclusionRays} occlusion rays");
        }

        if (pick is { Length: > 0 })
        {
            ReportPick(scene, camera, pick, width, height, diagnostics);
        }

        if (nounMap is { Length: > 0 })
        {
            WriteNounMap(scene, camera, width, height, nounMap);
        }

        DecodedImage image = renderer.Render(geometry, width, height, camera);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(outputPath, PngWriter.Encode(image));
        _log($"wrote {outputPath}");

        return true;
    }

    /// <summary>Lays the walk boundary over the floor it describes.</summary>
    /// <remarks>
    /// Every part of a boundary — its row order, the sign of its offset, the size it is
    /// stretched to — produces a plausible-looking mask when it is wrong. Seeing it on the
    /// floor is the check, which is why `Plan/04` makes overlay validation an exit
    /// criterion for this phase rather than a nicety.
    /// </remarks>
    /// <summary>A sibling of the enhanced textures directory.</summary>
    /// <param name="enhanced">Where the enhanced colour textures are.</param>
    /// <param name="what">The sibling's name.</param>
    /// <returns>Its path.</returns>
    private static string Beside(string enhanced, string what) =>
        Path.Combine(
            Path.GetDirectoryName(enhanced.TrimEnd(Path.DirectorySeparatorChar, '/')) ?? ".",
            what);

    /// <summary>
    /// The material library, from the workspace the enhanced textures live in.
    /// </summary>
    /// <param name="enhanced">Where the enhanced colour textures are.</param>
    /// <returns>The finishes, empty when there is no library.</returns>
    /// <remarks>
    /// <c>SurfaceFinishes.Load</c> reads the hand-written edit layer beside the library as
    /// well, which is the point: a correction is only worth making if the thing that draws
    /// the picture can be made to show it.
    /// </remarks>
    private static SurfaceFinishes Finishes(string enhanced)
    {
        // enhanced/textures -> enhanced -> the workspace, which is where manifests live.
        string textures = enhanced.TrimEnd(Path.DirectorySeparatorChar, '/');
        string root = Path.GetDirectoryName(textures) ?? ".";
        string workspace = Path.GetDirectoryName(root) ?? ".";

        return SurfaceFinishes.Load(
            Path.Combine(workspace, "manifests", "material-library.json"));
    }

    private void DrawWalkOverlay(SceneGeometry geometry, LoadedScene scene)
    {
        if (scene.Walkable is not { } boundary || scene.Geometry is not { } bsp)
        {
            _log("walk overlay: the scene declares no boundary");
            return;
        }

        IReadOnlyList<WalkOverlayPatch> patches =
            WalkOverlay.Build(bsp, scene.Definition.FloorObject(), boundary);

        foreach (WalkOverlayPatch patch in patches)
        {
            geometry.AddOverlay(
                $"walk-region-{patch.Region}", patch.Positions, patch.Indices, patch.Colour);
        }

        _log($"walk overlay: {patches.Sum(p => p.Indices.Length / 6)} texels over the floor, " +
             $"regions {string.Join(", ", patches.Select(p => p.Region))}");
    }

    /// <summary>Finds a way across the scene and draws it.</summary>
    /// <remarks>
    /// The same check the region overlay is for, one step further on. A boundary can be
    /// laid down correctly and still be unusable — a doorway one texel wide that no route
    /// ever goes through, a gradient that pushes actors into a wall — and the only way to
    /// see that is to ask for a walk across the room and look at what comes back.
    /// </remarks>
    private void DrawWalkPath(
        SceneGeometry geometry, LoadedScene scene, string request, DiagnosticBag diagnostics)
    {
        if (scene.Walkable is not { } boundary || scene.Geometry is not { } bsp)
        {
            _log("walk path: the scene declares no boundary");
            return;
        }

        string[] ends = request.Split(':');

        if (ends.Length != 2 ||
            Endpoint(scene, ends[0]) is not { } from ||
            Endpoint(scene, ends[1]) is not { } to)
        {
            IEnumerable<string> names = scene.Definition.Positions().Select(p => p.Name);

            diagnostics.Add(new Diagnostic(
                "SCENE012",
                DiagnosticSeverity.Error,
                $"Cannot read '{request}' as a walk. Give it as from:to, where each end is " +
                "a pair of world coordinates, x,z, or one of this scene's positions: " +
                $"{string.Join(", ", names)}."));

            return;
        }

        WalkRoute route = WalkPath.Find(boundary, from, to);

        if (route.IsEmpty)
        {
            _log("walk path: nowhere to stand at either end");
            return;
        }

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"walk path: {(route.ReachedGoal ? "arrives" : "gets as close as it can")} in " +
            $"{route.Points.Count} leg(s), {route.Length():F1} units"));

        foreach (Vector3 point in route.Points)
        {
            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"  ({point.X:F1}, {point.Z:F1}) region " +
                $"{boundary.RegionAt(point)}"));
        }

        // Blue when it arrives, red when it could only get near, so a route that stops at a
        // shut door reads as one at a glance rather than looking like a short walk.
        Vector3 colour = route.ReachedGoal
            ? new Vector3(0.2f, 0.6f, 1f)
            : new Vector3(0.95f, 0.2f, 0.2f);

        if (WalkOverlay.Route(
                bsp, scene.Definition.FloorObject(), boundary, route.Points, colour)
            is not { } patch)
        {
            _log("walk path: no floor under the route to draw it on");
            return;
        }

        geometry.AddOverlay("walk-route", patch.Positions, patch.Indices, patch.Colour);
    }

    /// <summary>Reads one end of a requested walk.</summary>
    /// <returns>
    /// The point, or null when the text is neither one of the scene's positions nor a pair
    /// of coordinates.
    /// </returns>
    private static Vector3? Endpoint(LoadedScene scene, string text)
    {
        text = text.Trim();

        foreach (ScenePosition position in scene.Definition.Positions())
        {
            if (string.Equals(position.Name, text, StringComparison.OrdinalIgnoreCase))
            {
                return position.Position;
            }
        }

        string[] parts = text.Split(',');

        return parts.Length == 2 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
            ? new Vector3(x, 0f, z)
            : null;
    }

    /// <summary>Says what a pixel of the render is looking at.</summary>
    private void ReportPick(
        LoadedScene scene,
        Camera camera,
        string request,
        int width,
        int height,
        DiagnosticBag diagnostics)
    {
        string[] parts = request.Split(',');

        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            diagnostics.Add(new Diagnostic(
                "SCENE013",
                DiagnosticSeverity.Error,
                $"Cannot read '{request}' as a pixel. Give it as x,y from the top-left of " +
                "the image."));

            return;
        }

        var picker = new ScenePicker(scene);

        if (picker.Pick(camera, x, y, width, height) is not { } hit)
        {
            _log($"pick ({x}, {y}): nothing, the ray leaves the room");
            return;
        }

        string noun = hit.Noun is { Length: > 0 } named ? named : "no noun, scenery";
        string verb = hit.Verb is { Length: > 0 } does ? $", verb {does}" : string.Empty;

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"pick ({x}, {y}): {hit.Name} [{hit.Kind}] {noun}{verb} at {hit.Distance:F1} " +
            $"units, ({hit.Point.X:F1}, {hit.Point.Y:F1}, {hit.Point.Z:F1})"));

        if (hit.Noun is { Length: > 0 } clicked)
        {
            ReportActions(scene, clicked);
        }
    }

    /// <summary>Says what the player may do to a noun, here and now.</summary>
    /// <remarks>
    /// The other half of a click. Picking says <em>what</em> was clicked and the action
    /// files say what that means, and the two only agree if the noun the geometry carries
    /// is one the action files have heard of — a noun with no verbs is either a scene file
    /// naming something the action files do not, or an action set that should have been
    /// loaded and was not.
    /// </remarks>
    private void ReportActions(LoadedScene scene, string noun)
    {
        if (scene.Actions is not { } actions)
        {
            _log("  verbs: unknown, because no point in the story was named");
            return;
        }

        IReadOnlyList<AvailableAction> available = actions.Resolve(noun);

        _log(available.Count == 0
            ? $"  {noun}: no verbs apply right now"
            : $"  {noun}: {string.Join(", ", available.Select(a => a.LocalizedVerb))}");

        foreach (Diagnostic diagnostic in actions.Diagnostics.Items)
        {
            _log($"  {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    /// <summary>Draws what the player could click, one colour per noun.</summary>
    /// <remarks>
    /// The overlay validation the phase asks for, and the only kind that works here. Much
    /// of what a click can land on is never drawn — a hit test is a slab across a doorway
    /// with its visibility switched off — so comparing the render against the original
    /// says nothing about whether the doorway can be clicked. This casts the same ray the
    /// game would through every pixel and colours it by what answered, which puts the
    /// invisible geometry on screen beside the visible.
    /// </remarks>
    private void WriteNounMap(
        LoadedScene scene, Camera camera, int width, int height, string path)
    {
        var picker = new ScenePicker(scene);

        // A quarter of each axis. A ray per pixel of a full render is sixteen times the
        // work for a picture whose smallest feature is a doorway, and the result is scaled
        // back up so it can be laid beside the render it belongs to.
        const int Coarseness = 4;

        int columns = Math.Max(1, width / Coarseness);
        int rows = Math.Max(1, height / Coarseness);

        string?[] nouns = new string?[columns * rows];
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<PickKind, int> kinds = [];
        int clickable = 0;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                if (picker.Pick(camera, column, row, columns, rows) is not { } hit)
                {
                    continue;
                }

                if (hit.Noun is not { Length: > 0 } noun)
                {
                    nouns[(row * columns) + column] = string.Empty;
                    continue;
                }

                nouns[(row * columns) + column] = noun;
                counts[noun] = counts.GetValueOrDefault(noun) + 1;
                kinds[hit.Kind] = kinds.GetValueOrDefault(hit.Kind) + 1;
                clickable++;
            }
        }

        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int row = Math.Min(rows - 1, y * rows / height);

            for (int x = 0; x < width; x++)
            {
                int column = Math.Min(columns - 1, x * columns / width);
                (byte r, byte g, byte b) = ColourFor(nouns[(row * columns) + column]);

                int at = ((y * width) + x) * 4;
                pixels[at] = r;
                pixels[at + 1] = g;
                pixels[at + 2] = b;
                pixels[at + 3] = 255;
            }
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(
            path, PngWriter.Encode(new DecodedImage(width, height, pixels, false, "noun-map")));

        _log($"noun map: {picker.TargetCount} things the ray can meet, {counts.Count} nouns " +
             $"over {clickable * 100 / Math.Max(1, columns * rows)}% of the view");

        // Which kinds answered matters as much as which nouns did: a hit test that never
        // comes back is one the player cannot click, and it is invisible in the render.
        _log("  from " + string.Join(
            ", ",
            kinds.OrderByDescending(p => p.Value)
                .Select(p => $"{p.Value * 100 / Math.Max(1, clickable)}% {p.Key}")));

        foreach ((string noun, int count) in counts.OrderByDescending(p => p.Value).Take(12))
        {
            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"  {noun}: {count * 100f / (columns * rows):F1}%"));
        }

        _log($"wrote {path}");
    }

    /// <summary>A stable colour for a noun.</summary>
    /// <remarks>
    /// Black is nothing, dark grey is scenery with no noun, and everything else gets a
    /// saturated colour derived from its own letters — so the same door is the same colour
    /// in every render, and two objects side by side are seldom the same colour by chance.
    /// </remarks>
    private static (byte R, byte G, byte B) ColourFor(string? noun)
    {
        if (noun is null)
        {
            return (0, 0, 0);
        }

        if (noun.Length == 0)
        {
            return (48, 48, 52);
        }

        uint hash = 2166136261;

        foreach (char letter in noun.ToUpperInvariant())
        {
            hash = (hash ^ letter) * 16777619;
        }

        // Around the hue circle, kept bright so the map reads as a diagram rather than as
        // a picture. Saturation carries a second slice of the hash: hue alone puts two of
        // R25's nouns within a few degrees of each other, and a map whose whole job is to
        // tell objects apart cannot afford that.
        float hue = (hash % 360) / 60f;
        float fraction = hue - MathF.Floor(hue);
        const byte High = 245;
        byte low = (byte)(40 + (((hash >> 16) % 3) * 70));
        byte rising = (byte)(low + ((High - low) * fraction));
        byte falling = (byte)(High - ((High - low) * fraction));

        return (int)hue switch
        {
            0 => (High, rising, low),
            1 => (falling, High, low),
            2 => (low, High, rising),
            3 => (low, falling, High),
            4 => (rising, low, High),
            _ => (High, low, falling),
        };
    }

    /// <summary>Checks the scene's nouns against the ones the action files know.</summary>
    /// <remarks>
    /// The two halves of an interaction are written in different files by different people:
    /// the scene file hangs a noun on a piece of geometry, and the action files say what
    /// that noun can have done to it. A noun in one and not the other is a click that
    /// resolves to something and then offers nothing, and neither file is wrong on its own,
    /// so the only way to see it is to put them side by side.
    /// </remarks>
    private void ReportNounCoverage(LoadedScene scene)
    {
        if (scene.Actions is not { } actions)
        {
            return;
        }

        HashSet<string> known = new(actions.Nouns, StringComparer.OrdinalIgnoreCase);

        List<string> declared =
        [
            .. scene.Definition.Models().Select(m => m.Noun)
                .Concat(scene.Definition.Actors().Select(a => a.Noun))
                .Where(n => n is { Length: > 0 })
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];

        List<string> unknown = [.. declared.Where(n => !known.Contains(n))];

        _log($"nouns: {declared.Count} on the scene's objects, " +
             $"{declared.Count - unknown.Count} of them known to the action files");

        if (unknown.Count > 0)
        {
            _log($"  nothing can be done to: {string.Join(", ", unknown.Take(12))}" +
                 (unknown.Count > 12 ? $", and {unknown.Count - 12} more" : string.Empty));
        }
    }

    /// <summary>
    /// Puts everything the scene gives an opening pose into it.
    /// </summary>
    /// <remarks>
    /// The same call the launcher makes, with the same two libraries behind it. A
    /// <see cref="SceneUpdate"/> is built and thrown away: nothing here advances it, so
    /// none of what it can do that takes time happens, and the one thing that takes no
    /// time does.
    /// </remarks>
    private void PoseOpening(
        GameArchives archives, LoadedScene scene, SceneGeometry geometry, DiagnosticBag diagnostics)
    {
        var api = new Gk3SheepApi(new GameState());
        var update = new SceneUpdate(scene, api, new Game.Actors.Glances(), geometry)
        {
            Clips = new ClipLibrary(archives),
            Animations = new AnimationLibrary(archives),
        };

        int posed = update.Open();

        if (posed > 0)
        {
            _log($"opening pose: {posed} clip(s) sampled");
        }

        foreach (Diagnostic problem in update.Diagnostics.Items)
        {
            diagnostics.Add(problem);
        }
    }

    /// <summary>Carries out an action, and says what it did.</summary>
    /// <remarks>
    /// The end of the sentence the rest of this command spells out: a click resolves to a
    /// noun, the action files say which verbs that noun answers to, and this performs one.
    /// The scripts are loaded into a <see cref="ScriptHost"/> first, because a fifth of
    /// every statement in the corpus is <c>CallSheep</c> and without them it would go
    /// nowhere and look as though the action had done less than it did.
    /// </remarks>
    private void Perform(
        GameArchives archives,
        LoadedScene scene,
        SceneRequest request,
        string wanted,
        DiagnosticBag diagnostics)
    {
        string[] parts = wanted.Split(':');

        if (parts.Length != 2 || scene.Actions is not { } actions || request.Api is not { } api)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE019",
                DiagnosticSeverity.Error,
                $"Cannot read '{wanted}' as an action. Give it as noun:verb, and name a " +
                "point in the story with --timeblock so there is one to act in."));

            return;
        }

        string noun = parts[0].Trim();
        string verb = parts[1].Trim();

        if (actions.Find(noun, verb) is not { } rule)
        {
            _log($"do {noun}:{verb}: nothing applies here and now");

            foreach (AvailableAction option in actions.Resolve(noun))
            {
                _log($"  {noun} does answer to {option.LocalizedVerb}");
            }

            return;
        }

        var host = new ScriptHost(api);
        SceneScripting.Attach(api, scene);
        int loaded = LoadScripts(archives, host);
        var runner = new ActionRunner(api);

        // With somewhere to put a waiting script and something that knows how long its
        // calls take, an action takes the time it was written to take instead of happening
        // all at once. Both are needed: a scheduler with no durations parks nothing for
        // long, and durations with no scheduler are a number nobody spends.
        api.Animations = new AnimationLibrary(archives);
        host.Scheduler = new SheepScheduler(host.Machine);

        string before = api.State.ComputeHash();
        int events = api.Events.Count;

        ActionOutcome outcome = runner.Run(rule);

        _log($"do {noun}:{verb} [{rule.Case}] from {rule.Source}, {loaded} scripts loaded");
        _log($"  {(outcome.Ran ? "ran" : "refused")} " +
             $"{outcome.Statements.Count} statement(s): " +
             string.Join(
                 "; ",
                 outcome.Statements.Select(t => t.Waited ? $"wait {t.Call}" : t.Call)));

        foreach (RecordedEvent recorded in api.Events.Skip(events))
        {
            _log($"    {recorded.Name}({string.Join(", ", recorded.Arguments)})");
        }

        _log(outcome.Seconds > 0
            ? $"  takes {outcome.Seconds:0.0}s of the player's time"
            : "  is over as soon as it starts");

        if (host.CallStackTrace.Count > 0)
        {
            _log($"  entered {string.Join(", ", host.CallStackTrace.Take(8))}" +
                 (host.CallStackTrace.Count > 8
                     ? $", and {host.CallStackTrace.Count - 8} more"
                     : string.Empty));
        }

        _log(api.State.ComputeHash() == before
            ? "  the story is where it was"
            : "  the story moved");

        if (api.State.Screens.Open.Count > 0)
        {
            _log($"  in front of the room now: {string.Join(" > ", api.State.Screens.Open)}" +
                 $"{(api.State.Screens.InventoryReachable ? string.Empty : ", inventory out of reach")}");
        }

        if (scene.Walkable is { Blocked.Count: > 0 } boundary)
        {
            string standing = string.Join(
                ", ",
                boundary.Blocked.Select(b => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{b.Name} over ({b.Minimum.X:F0}, {b.Minimum.Y:F0}) to ({b.Maximum.X:F0}, {b.Maximum.Y:F0})")));

            _log($"  in the way now, {boundary.WalkableTexels()} texels still open: {standing}");
        }

        foreach (Diagnostic diagnostic in runner.Diagnostics.Items.Concat(host.Diagnostics.Items))
        {
            diagnostics.Add(diagnostic);
        }
    }

    /// <summary>Loads every compiled script the archives hold.</summary>
    /// <remarks>
    /// All of them, rather than the ones this location might want, because the names an
    /// action calls are not knowable without reading its script and the whole set is a few
    /// hundred small files.
    /// </remarks>
    private int LoadScripts(GameArchives archives, ScriptHost host)
    {
        int loaded = 0;

        foreach (string name in archives.Names(".SHP"))
        {
            if (archives.Read(name) is not { } data)
            {
                continue;
            }

            try
            {
                host.Add(SheepScriptFile.Parse(data, name));
                loaded++;
            }
            catch (FormatParseException ex)
            {
                _log($"  {name} did not parse: {ex.Diagnostic.Message}");
            }
        }

        return loaded;
    }

    /// <summary>Lets time pass, and performs whatever the story had asked for by then.</summary>
    /// <remarks>
    /// The other way an action starts. A script can set a timer — a phone that rings a
    /// minute after the player walks in — and what fires is a noun and a verb resolved
    /// then, not a piece of work saved earlier, so the rule that applies is the one that
    /// applies when it goes off.
    /// </remarks>
    private void Advance(
        GameArchives archives,
        LoadedScene scene,
        SceneRequest request,
        double seconds,
        DiagnosticBag diagnostics)
    {
        if (scene.Actions is not { } actions || request.Api is not { } api)
        {
            _log("advance: no point in the story to let pass; name one with --timeblock");
            return;
        }

        IReadOnlyList<GameTimer> due = api.State.Timers.Advance(seconds);

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"advance {seconds:F1}s: {due.Count} action(s) come due, " +
            $"{api.State.Timers.Count} still waiting"));

        if (due.Count == 0)
        {
            return;
        }

        var host = new ScriptHost(api);
        SceneScripting.Attach(api, scene);
        LoadScripts(archives, host);
        var runner = new ActionRunner(api);

        foreach (GameTimer timer in due)
        {
            if (actions.Find(timer.Noun, timer.Verb) is not { } rule)
            {
                _log($"  {timer.Noun}:{timer.Verb} came due and nothing applies to it now");
                continue;
            }

            ActionOutcome outcome = runner.Run(rule);

            _log($"  {timer.Noun}:{timer.Verb} [{rule.Case}] " +
                 $"{(outcome.Ran ? "ran" : "was refused")}: " +
                 string.Join("; ", outcome.Statements.Select(t => t.Call)));
        }

        foreach (Diagnostic diagnostic in runner.Diagnostics.Items.Concat(host.Diagnostics.Items))
        {
            diagnostics.Add(diagnostic);
        }
    }

    /// <summary>Points an actor at something before the scene is built.</summary>
    /// <remarks>
    /// Two loads, because of an ordering that cannot be got round here: turning a head
    /// means placing one of an actor's meshes differently, so the glance has to be decided
    /// before the actor is placed — and where the thing being looked at <em>is</em> is only
    /// known once everything has been placed. The first load is thrown away and exists to
    /// answer that. A script does not have this problem: by the time one runs the room is
    /// already standing, which is what an update loop will let this do too.
    /// </remarks>
    private void PointSomebody(
        SceneLoader loader,
        GameArchives archives,
        SceneRequest request,
        string wanted,
        DiagnosticBag diagnostics)
    {
        string[] ends = wanted.Split(':');

        if (ends.Length != 2)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE021",
                DiagnosticSeverity.Error,
                $"Cannot read '{wanted}' as a glance. Give it as actor:target."));

            return;
        }

        var probe = new HeadlessSceneSink();
        var quiet = new DiagnosticBag();

        if (new SceneLoader(archives).Load(probe, request, quiet) is not { } standing)
        {
            _log($"glance: {request.Scene} would not load, so nobody is looking anywhere");
            return;
        }

        var api = new Gk3SheepApi(request.State ?? new GameState());
        SceneScripting.Attach(api, standing, loader.Glances);

        SheepExpression.Evaluate(
            $"LookitActor(\"{ends[0].Trim()}\", \"{ends[1].Trim()}\", \"\", 0)", api);

        foreach (Diagnostic diagnostic in api.Diagnostics.Items)
        {
            diagnostics.Add(diagnostic);
        }
    }
}

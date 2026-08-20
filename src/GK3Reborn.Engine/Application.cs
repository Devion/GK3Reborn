using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn;

/// <summary>The composition root and main loop.</summary>
/// <remarks>
/// Startup order is fixed by Plan/01-architecture.md section 3: paths and logging,
/// then content manifest and locale validation, then window, then renderer device and
/// feature tier, then audio endpoint, then game state. Each step must fail with an
/// actionable message rather than proceeding in a broken state.
/// </remarks>
public static class Application
{
    /// <summary>Runs the game.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="nativeLibraryRoot">
    /// Directory the host resolved native libraries from, for the startup report. Passed
    /// in because the dependency runs Bootstrap -> App and never the reverse.
    /// </param>
    /// <returns>Process exit code.</returns>
    public static int Run(string[] args, string? nativeLibraryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        Console.WriteLine("GK3Reborn 0.1.0");
        Console.WriteLine("Scaffold stage: subsystems are contracts only.");
        Console.WriteLine($"Native library root: {nativeLibraryRoot ?? "(not installed)"}");

        // The deterministic clock and RNG are live from the first commit so that no
        // subsystem is ever written against wall-clock time or ambient randomness.
        var clock = new GameClock();
        clock.AdvanceFixed(60);
        var random = new DeterministicRandom(seed: 0x6B33);

        Console.WriteLine($"Clock: tick {clock.Tick}, sim {clock.SimulationTimeSeconds:F3}s");
        Console.WriteLine($"RNG seed 0x{random.Seed:X}: first draw {random.NextUInt64():X16}");

        Console.WriteLine();

        if (Option(args, "--scene") is { } scene)
        {
            return RenderScene(
                Option(args, "--data") ?? DefaultDataDirectory(),
                scene,
                Option(args, "--timeblock"),
                Option(args, "--camera"),
                int.TryParse(Option(args, "--frames"), out int frames) ? frames : 0,
                Option(args, "--screenshot"),
                args.Contains("--verbose", StringComparer.OrdinalIgnoreCase),
                RayTracingSettings.Parse(Option(args, "--rt")) ?? RayTracingQuality.None,
                EnhancedTextureDirectory(args),
                args);
        }

        ReportGraphics();

        if (args.Contains("--offscreen", StringComparer.OrdinalIgnoreCase))
        {
            return RenderOffscreen();
        }

        if (args.Contains("--render", StringComparer.OrdinalIgnoreCase))
        {
            return RenderFrames(args.Contains("--headless-frames", StringComparer.OrdinalIgnoreCase) ? 60 : 0);
        }

        return 0;
    }

    /// <summary>
    /// Opens a window and shows a scene from the game's own archives.
    /// </summary>
    /// <param name="dataDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="sceneName">Which scene to load.</param>
    /// <param name="timeblock">Which time of day, or null for whichever exists.</param>
    /// <param name="cameraName">Which of the scene's cameras to start at.</param>
    /// <param name="frameLimit">Stop after this many frames, or zero to run until closed.</param>
    /// <param name="screenshotPath">Where to write the last frame, if anywhere.</param>
    /// <param name="verbose">Whether to list everything that could not be loaded.</param>
    /// <param name="quality">How much ray tracing to start with.</param>
    /// <param name="enhancedDirectory">Higher-resolution textures to prefer, if any.</param>
    /// <param name="args">The command line, for the options only the running scene reads.</param>
    /// <returns>Process exit code.</returns>
    /// <remarks>
    /// The camera starts at one of the scene's own viewpoints, which is what the player
    /// would see, and can then be flown around to check the parts a fixed camera never
    /// shows.
    /// </remarks>
    private static int RenderScene(
        string dataDirectory,
        string sceneName,
        string? timeblock,
        string? cameraName,
        int frameLimit,
        string? screenshotPath,
        bool verbose,
        RayTracingQuality quality,
        string? enhancedDirectory,
        string[] args)
    {
        if (!Directory.Exists(dataDirectory))
        {
            Console.Error.WriteLine($"No content directory at {dataDirectory}.");
            Console.Error.WriteLine("Pass --data <dir> pointing at the game's Data directory.");
            return 2;
        }

        using GameArchives archives = GameArchives.Open(dataDirectory);
        Console.WriteLine($"Content: {archives.Count} archives in {dataDirectory}");

        using var window = Platform.SilkGameWindow.Open($"GK3Reborn - {sceneName}");
        using var renderer = VulkanRenderer.Create(window, window);

        ReportGraphics(renderer.Survey());
        Console.WriteLine($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        var diagnostics = new DiagnosticBag();
        SceneRequest request = Playable(archives, sceneName, timeblock);
        Gk3SheepApi api = request.Api ?? new Gk3SheepApi(new GameState());

        // What makes a waited call take time. Without it every line of dialogue in the
        // game is over in the frame it starts.
        api.Animations = new AnimationLibrary(archives);

        if (request.State is not null)
        {
            Console.WriteLine($"Story: {request.State.Timeblock} in {request.State.Location}");
        }

        // Sound. The device may not open — a machine without one, or one already held —
        // and the game runs quietly rather than not at all.
        Audio.OpenAlBackend? audio = Audio.OpenAlBackend.Open(
            Audio.SpeakerLayout.Stereo, diagnostics);

        var sounds = new SoundLibrary(archives);

        SceneAudio? room = audio is null
            ? null
            : new SceneAudio(sounds, api.Animations, audio);

        Console.WriteLine(audio is null
            ? "Audio: none, the game runs silent"
            : $"Audio: {audio.DeviceName}");

        // The host outlives the room. Its scripts and its registrations belong to the
        // story rather than to the room, and reloading them at every door would lose
        // whatever a script was in the middle of.
        // Who the cast are and how each of them walks. Read once: it describes the game's
        // people rather than any one room, and every room asks the same questions of it.
        Game.Actors.CharacterLibrary characters = Game.Actors.CharacterLibrary.Open(archives);

        // Which verbs are things to say rather than things to do. Without it a topic is
        // indistinguishable from a verb, every line of it is offered at once, and none of
        // them is ever used up.
        Game.Actions.VerbLibrary verbs = Game.Actions.VerbLibrary.Open(archives);

                var host = new ScriptHost(api);

        // Scripts wait for real here, unlike in the tools, because here there is a clock
        // for them to wait against.
        host.Scheduler = new SheepScheduler(host.Machine);

        Console.WriteLine($"Scripts: {LoadScripts(archives, host)} loaded");

        // The interface. GK3's own bitmap fonts rather than anything imported: they are in
        // the archives, they are the right size for the game's own screens, and reading one
        // is a smaller job than shaping a scalable typeface would be.
        // The shapes as well as the transforms: GK3's characters have no skeleton, so
        // without them a walk is mesh groups sliding about rather than anybody walking.
        var clips = new ClipLibrary(archives) { KeepVertices = true };
        var fonts = new FontLibrary(archives);
        GameHud? hud = null;

        if (fonts.Any("F_ARIAL_T12", "F_ARIAL_T10", "F_ARIAL_T8") is { } font)
        {
            OverlayAtlas atlas = OverlayAtlas.Build(font);

            renderer.SetOverlayAtlas(atlas);
            hud = new GameHud(new Overlay(atlas));

            Console.WriteLine(
                $"Interface: {font.Name}, {font.Count} glyphs at {font.Height}px, " +
                $"sheet {atlas.Image.Width}x{atlas.Image.Height}, " +
                $"{(renderer.HasOverlay ? "drawing" : "NOT drawing")}");
        }
        else
        {
            Console.WriteLine("Interface: no font found, nothing is drawn over the room");
        }

        int result = 0;
        bool first = true;

        // One pass a room. A door is a script that says SetLocation and nothing more, so
        // going through one is this loop coming round again rather than anything the room
        // itself knows how to do.
        while (true)
        {
            using SceneGeometry geometry = renderer.CreateGeometry();

            // A fresh loader each time: it carries the last room's glances and its count of
            // enhanced textures, and neither belongs to the next one.
            var loader = new SceneLoader(archives, Console.WriteLine);

            if (enhancedDirectory is { Length: > 0 })
            {
                EnhancedTextures enhanced = EnhancedTextures.Open(enhancedDirectory);
                loader.Enhanced = enhanced;

                // Normal maps sit beside the colour textures rather than among them: a
                // surface may have a better colour and no normal map, or the other way
                // round, and they are judged separately.
                EnhancedTextures normals = EnhancedTextures.Open(
                    Path.Combine(
                        Path.GetDirectoryName(
                            enhancedDirectory.TrimEnd(Path.DirectorySeparatorChar, '/')) ??
                            enhancedDirectory,
                        "normals"));

                // --flat leaves the colour textures enhanced and the surfaces smooth,
                // which is the only way to see what the normal pass alone is doing.
                loader.Normals = args.Contains("--flat", StringComparer.OrdinalIgnoreCase)
                    ? null
                    : normals;

                // The block-compressed build of the same set, which is preferred over both
                // wherever it has an answer: nothing to decode, a mip chain already built,
                // and a quarter of the video memory.
                CompressedTextures compressed = CompressedTextures.Open(
                    CompressedTextureDirectory(args, enhancedDirectory));

                loader.Compressed =
                    args.Contains("--uncompressed", StringComparer.OrdinalIgnoreCase)
                        ? null
                        : compressed;

                if (first && normals.Count > 0)
                {
                    Console.WriteLine($"Normal maps: {normals.Count} available");
                }

                if (first && loader.Compressed is { Count: > 0 })
                {
                    Console.WriteLine(
                        $"Compressed textures: {compressed.Count} available with " +
                        $"{compressed.NormalCount} normal map(s) in {compressed.Directory}");
                }

                if (first)
                {
                    Console.WriteLine(enhanced.Count > 0
                        ? $"Enhanced textures: {enhanced.Count} available in {enhancedDirectory}"
                        : $"Enhanced textures: none found in {enhancedDirectory}");
                }
            }

            var loading = Stopwatch.StartNew();

            if (loader.Load(geometry, request, diagnostics) is not { } scene)
            {
                foreach (Diagnostic diagnostic in diagnostics.Items)
                {
                    Console.Error.WriteLine(diagnostic);
                }

                audio?.Dispose();
                return 3;
            }

            renderer.SetLights(scene.Lights);
            renderer.Quality = renderer.SupportsRayTracing ? quality : RayTracingQuality.None;

            if (first)
            {
                Console.WriteLine(renderer.SupportsRayTracing
                    ? $"Ray tracing: {renderer.Quality} ({geometry.TraceableTriangleCount} opaque "
                      + "triangles traced)"
                    : "Ray tracing: unavailable on this device");
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Loaded {scene.Name} in {loading.Elapsed.TotalMilliseconds:F0} ms, " +
                $"{geometry.TextureCount} textures resident, {geometry.TexturesReused} reused, " +
                $"{geometry.TextureDeviceBytes / (1024.0 * 1024):F0} MB of them on the device"));

            Console.WriteLine($"Scene {scene.Name}: {geometry.TriangleCount} triangles in "
                + $"{geometry.BatchCount} batches, {geometry.TextureCount} textures"
                + (loader.EnhancedTexturesUsed > 0
                    ? $" ({loader.EnhancedTexturesUsed} enhanced"
                      + (loader.CompressedUsed > 0 ? $", {loader.CompressedUsed} compressed)" : ")")
                    : string.Empty)
                + (loader.NormalMapsUsed > 0
                    ? $", {loader.NormalMapsUsed} normal mapped"
                    : string.Empty)
                + $", {scene.Lights.Count} authored lights");

            Report(diagnostics, verbose);

            // Whatever was waiting was waiting on the room that has gone.
            host.Scheduler.Clear();

            var update = new SceneUpdate(
                scene,
                api,
                loader.Glances,
                geometry,
                scene.Actions,
                new ActionRunner(api),
                host.Scheduler);

            // Registered again for the new room, and after the update exists because the
            // walking functions need something to walk in. The scene functions close over
            // the room they were given and the last registration wins.
            SceneScripting.Attach(api, scene, loader.Glances, room, update);

            // What lets an animation actually move something. Vertex poses are left unread:
            // gab alone is 50.2 million samples and nothing deforms yet.
            update.Animations = api.Animations;
            update.Clips = clips;
            update.Characters = characters;

            if (scene.Actions is { } actions)
            {
                actions.Verbs = verbs;
            }

            Console.WriteLine(
                $"Update: {update.Movable} actor(s) can turn their head, " +
                $"{characters.Count} character(s) know how to walk, " +
                $"{verbs.TopicCount} topic(s) can be raised");

            // What the room does when somebody walks into it, which is mostly deciding
            // where they are standing. A scene places its actors wherever its [ACTORS]
            // section says — usually START — and this is what moves the player to the spot
            // matching the door they came through. Without it every arrival is the front
            // door, however the player got in.
            if (scene.Actions?.Find("SCENE", "ENTER") is { } entering)
            {
                new ActionRunner(api).Run(entering);
                Console.WriteLine($"entered: SCENE:ENTER [{entering.Case}]");
            }

            room?.Silence();

            // What the room sounds like when nothing is happening in it.
            if (room?.StartAmbience(scene.AmbienceRead) is { } bed)
            {
                Console.WriteLine($"Ambience: {bed}");
            }

            if (first)
            {
                Opening(args, api, scene);
            }
            else if (Option(args, "--then")?.Split(':') is [string n, string v] &&
                     scene.Actions?.Find(n.Trim(), v.Trim()) is { } follow)
            {
                // The same as --do, in the second room. It exists to measure a second
                // transition — and a return trip is the one that shows what the texture
                // cache is worth — without needing a mouse.
                new ActionRunner(api).Run(follow);

                Console.WriteLine($"Then {n.Trim()}:{v.Trim()} [{follow.Case}]");
            }

            RoomExit exit = FlyScene(
                window, renderer, geometry, scene, cameraName, frameLimit, update,
                new SceneInteraction(scene, api), room, hud, api.State, args);

            result = exit.Code;

            if (exit.Destination is not { Length: > 0 } next)
            {
                break;
            }

            // The geometry is about to go. Frames are still in flight reading its buffers,
            // and freeing those underneath the device is a crash somewhere else entirely.
            renderer.SetScene(null, null);
            renderer.Idle();

            request = SceneRequest.Continuing(api, next);

            // The next room has its own idea of where to stand; the camera the player named
            // belonged to the one they have left.
            cameraName = null;
            first = false;
        }

        audio?.Dispose();

        if (screenshotPath is not null && renderer.Capture() is { } capture)
        {
            File.WriteAllBytes(screenshotPath, Formats.Bitmaps.PngWriter.Encode(capture));
            Console.WriteLine($"Wrote {screenshotPath}");
        }

        return result;
    }

    /// <summary>Loads every compiled script in the game.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="host">Where they go.</param>
    /// <returns>How many were loaded.</returns>
    /// <remarks>
    /// Once, before the first room. A fifth of the corpus's action statements are
    /// <c>CallSheep</c>, and a script that is not loaded is a call that does nothing — most
    /// visibly the ones that take the player from one room to the next.
    /// </remarks>
    private static int LoadScripts(GameArchives archives, ScriptHost host)
    {
        int loaded = 0;

        foreach (string name in archives.Names(".SHP"))
        {
            if (archives.Read(name) is not { } bytes)
            {
                continue;
            }

            try
            {
                host.Add(Sheep.SheepScriptFile.Parse(bytes, name));
                loaded++;
            }
            catch (Formats.FormatParseException)
            {
                // A script that will not parse is one call that does nothing, not a game
                // that will not start.
            }
        }

        return loaded;
    }

    /// <summary>Says what could not be loaded.</summary>
    /// <param name="diagnostics">What was raised while loading.</param>
    /// <param name="verbose">Whether to list them rather than count them.</param>
    private static void Report(DiagnosticBag diagnostics, bool verbose)
    {
        Diagnostic[] problems =
            [.. diagnostics.Items.Where(d => d.Severity >= DiagnosticSeverity.Warning)];

        if (problems.Length == 0)
        {
            return;
        }

        Console.WriteLine(verbose
            ? $"{problems.Length} assets could not be loaded:"
            : $"({problems.Length} assets could not be loaded; --verbose lists them)");

        if (verbose)
        {
            foreach (Diagnostic problem in problems)
            {
                Console.WriteLine($"  {problem}");
            }
        }
    }

    /// <summary>
    /// The switches that set something going before the player takes over.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="api">The host.</param>
    /// <param name="scene">The room they act on.</param>
    /// <remarks>
    /// Only in the first room. They exist for looking at one thing on purpose, and firing
    /// them again at every door would mean the player could never get away from them.
    /// </remarks>
    private static void Opening(string[] args, Gk3SheepApi api, LoadedScene scene)
    {
        if (Option(args, "--do")?.Split(':') is [string noun, string verb] &&
            scene.Actions?.Find(noun.Trim(), verb.Trim()) is { } rule)
        {
            ActionOutcome outcome = new ActionRunner(api).Run(rule);

            Console.WriteLine(
                $"Doing {noun.Trim()}:{verb.Trim()} [{rule.Case}]: " +
                $"{(outcome.Ran ? "ran" : "refused")} {outcome.Statements.Count} statement(s)");
        }

        if (Option(args, "--play") is { Length: > 0 } clip)
        {
            Sheep.SheepExpression.Evaluate($"StartAnimation(\"{clip}\")", api);
            Console.WriteLine($"Playing {clip}");
        }

        if (Option(args, "--glide") is { Length: > 0 } destination)
        {
            Sheep.SheepExpression.Evaluate(
                $"GlideToCameraAngle(\"{destination}\")", api);
            Console.WriteLine($"Gliding to {destination}");
        }

        if (Option(args, "--glance")?.Split(':') is [string who, string at])
        {
            Sheep.SheepExpression.Evaluate(
                $"LookitActor(\"{who.Trim()}\", \"{at.Trim()}\", \"\", 0)",
                api);

            foreach (Diagnostic diagnostic in api.Diagnostics.Items)
            {
                Console.WriteLine($"  {diagnostic}");
            }
        }
    }

    /// <summary>Runs the present loop with a camera the player can move.</summary>
    /// <param name="window">The window and its input.</param>
    /// <param name="renderer">The renderer.</param>
    /// <param name="geometry">The scene's geometry.</param>
    /// <param name="scene">The scene.</param>
    /// <param name="cameraName">Which camera to open on, if any.</param>
    /// <param name="frameLimit">Stop after this many frames, or zero for no limit.</param>
    /// <param name="update">The world going on by itself.</param>
    /// <param name="interaction">Turns pointing at the room into doing something to it.</param>
    /// <param name="room">What the room sounds like, if there is a device.</param>
    /// <param name="hud">The interface, if there is a font to draw it with.</param>
    /// <param name="story">Where the story stands, for the inventory strip.</param>
    /// <param name="options">The command line, for the debugging switches.</param>
    /// <returns>Why the room was left, and where for.</returns>
    /// <remarks>
    /// The loop drives the world as well as the view: <see cref="SceneUpdate.Advance"/> is
    /// given the frame's elapsed time, so a head that was told to look at something turns
    /// while the player watches rather than having always been turned.
    /// </remarks>
    private static RoomExit FlyScene(
        Platform.SilkGameWindow window,
        VulkanRenderer renderer,
        SceneGeometry geometry,
        LoadedScene scene,
        string? cameraName,
        int frameLimit,
        SceneUpdate update,
        SceneInteraction interaction,
        SceneAudio? room,
        GameHud? hud,
        GameState story,
        string[] options)
    {
        string here = scene.Name;

        int cameraIndex = Math.Max(
            0,
            scene.Cameras.ToList().FindIndex(c => string.Equals(
                c.Name, cameraName ?? scene.CameraNamed(null)?.Name, StringComparison.OrdinalIgnoreCase)));

        Camera template = SceneLoader.CameraFor(scene, geometry, cameraName);

        var camera = new FreeCamera
        {
            Speed = MathF.Max(50f, (geometry.Maximum - geometry.Minimum).Length() * 0.15f),
        };

        camera.CopyFrom(template);

        Console.WriteLine();
        Console.WriteLine("WASD to move, E and Q for up and down, drag to look,");
        Console.WriteLine("Tab for the next camera, R to return to it, F2 for ray tracing,");
        Console.WriteLine("click to act on what is under the pointer, right-click to see");
        Console.WriteLine("everything it answers to, Escape to leave.");

        // Where the scene opened, so a glide has somewhere to leave from rather than
        // arriving the moment it is asked for.
        update.StartAt(template);

        Camera? directing = update.View;

        var stopwatch = Stopwatch.StartNew();
        double previous = 0;
        int presented = 0;
        string? hovering = null;
        string? spoken = null;
        int said = 0;
        Hover? menu = null;
        Vector2 menuAt = Vector2.Zero;
        int menuIndex = 0;
        Vector2? pinned = Pinned(options);
        bool forceMenu = options.Contains("--menu", StringComparer.OrdinalIgnoreCase);

        if (pinned is { } spot)
        {
            Console.WriteLine($"Pointer pinned at {spot.X}, {spot.Y}");
        }

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();

            double now = stopwatch.Elapsed.TotalSeconds;
            float delta = (float)Math.Min(0.1, now - previous);
            previous = now;

            if (window.WasPressed(Platform.CameraAction.Quit))
            {
                break;
            }

            if (window.WasPressed(Platform.CameraAction.NextCamera) && scene.Cameras.Count > 0)
            {
                cameraIndex = (cameraIndex + 1) % scene.Cameras.Count;
                template = SceneLoader.CameraFor(scene, geometry, scene.Cameras[cameraIndex].Name);
                camera.CopyFrom(template);

                Console.WriteLine($"camera: {scene.Cameras[cameraIndex].Name}");
            }

            if (window.WasPressed(Platform.CameraAction.Reset))
            {
                camera.CopyFrom(template);
            }

            if (window.WasPressed(Platform.CameraAction.CycleRayTracing) && renderer.SupportsRayTracing)
            {
                RayTracingQuality[] levels = Enum.GetValues<RayTracingQuality>();

                renderer.Quality = levels[(Array.IndexOf(levels, renderer.Quality) + 1) % levels.Length];
                Console.WriteLine($"ray tracing: {renderer.Quality}");
            }

            // What the world could not do, said once. Animation naming is the sort of thing
            // that fails by nothing happening, which is indistinguishable from nothing
            // having been asked for.
            for (; said < update.Diagnostics.Items.Count; said++)
            {
                Console.WriteLine($"  {update.Diagnostics.Items[said]}");
            }

            foreach (string happened in update.Advance(delta))
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{stopwatch.Elapsed.TotalSeconds:F2}s] {happened}"));
            }

            // The story moving the camera takes it back off the player, for as long as it
            // is moving. Letting them keep flying through a scripted glide would fight
            // them for the mouse, and letting the glide win afterwards would take the view
            // away from somebody who had gone to look at something.
            if (!ReferenceEquals(update.View, directing) && update.View is { } directed)
            {
                directing = update.View;
                template = directed;
                camera.CopyFrom(directed);
            }

            camera.Update(window, delta);

            Camera view = camera.ToCamera(template);

            // What the pointer is over. Asked every frame and free of consequences by
            // design — the resolver evaluates conditions to answer, so anything that wrote
            // to the story here would advance the game by moving the mouse across it.
            // The pointer is in window pixels and the viewport is in framebuffer pixels,
            // which are not the same on a scaled display. Picking in the wrong one puts the
            // ray somewhere the player is not looking, and only on some machines.
            Vector2 aimed = pinned ?? new Vector2(
                window.PointerPosition.X * window.DpiScale,
                window.PointerPosition.Y * window.DpiScale);

            Hover hover = interaction.At(
                view,
                (int)aimed.X,
                (int)aimed.Y,
                window.FramebufferWidth,
                window.FramebufferHeight);

            if (hover.Noun != hovering)
            {
                hovering = hover.Noun;

                if (hovering is { Length: > 0 })
                {
                    Console.WriteLine(hover.Actionable
                        ? $"> {hovering} — click to {hover.Default}"
                        : $"> {hovering} — nothing to do with it here");
                }
            }

            // --pointer puts it somewhere fixed, which is the only way to photograph the
            // interface: the label follows the mouse, and a headless run has never moved it.
            Vector2 pointer = aimed;

            // --menu opens it without a right-click, for the same reason --pointer exists.
            if (forceMenu && menu is null && hover.Actionable)
            {
                menu = hover;
                menuAt = pointer;
                menuIndex = 0;
            }

            if (window.WasClicked(Platform.PointerButton.Secondary))
            {
                // The menu belongs to the thing it was opened over, not to wherever the
                // pointer wanders next, so what was under it is kept — and so is where it
                // was, because a menu that follows the pointer cannot be clicked.
                menu = menu is null && hover.Actionable ? hover : null;
                menuAt = pointer;
                menuIndex = 0;

                // Asking and getting nothing has to look different from asking and being
                // ignored, or a room where nothing answers is indistinguishable from a
                // right-click that did not register.
                if (menu is null)
                {
                    Console.WriteLine(hover.Noun is { Length: > 0 } asked
                        ? $"{asked} answers to nothing here and now"
                        : "nothing under the pointer");
                }
            }

            if (menu is { } listed)
            {
                // One selection, three ways to move it. The wheel steps through the list
                // and wraps, because two or three verbs are not worth a dead end at either
                // end; putting the pointer on a row moves it there instead.
                if (window.ScrollDelta != 0 && listed.Actions.Count > 0)
                {
                    int count = listed.Actions.Count;

                    menuIndex = (((menuIndex - window.ScrollDelta) % count) + count) % count;
                }
                else if (hud?.RowAt(pointer) is int row and >= 0)
                {
                    menuIndex = row;
                }
            }

            if (window.WasClicked(Platform.PointerButton.Primary))
            {
                // A click inside the open menu takes whatever is selected; a click anywhere
                // else dismisses it without doing anything, which is what every menu does.
                bool inside = menu is not null && hud?.RowAt(pointer) >= 0;

                ActionOutcome? did = menu is { } open
                    ? inside && menuIndex < open.Actions.Count
                        ? interaction.Do(open, open.Actions[menuIndex].LocalizedVerb)
                        : null
                    : interaction.Do(hover);

                if (menu is not null)
                {
                    menu = null;
                }

                if (did is { } outcome)
                {
                    Console.WriteLine(
                        $"{outcome.Noun}:{outcome.Verb} [{outcome.Case}] - " +
                        $"{(outcome.Ran ? "ran" : "refused")} {outcome.Statements.Count} statement(s)" +
                        (outcome.Seconds > 0 ? $", {outcome.Seconds:F1}s" : string.Empty));
                }
            }

            // The device is the clock for dialogue: the next line of a voice-over starts
            // when the last one's source stops, so they never overlap and never drift.
            room?.Update();

            if (room?.Caption is { Length: > 0 } caption && caption != spoken)
            {
                spoken = caption;
                Console.WriteLine($"  {room.Speaker}: {caption}");
            }

            if (hud is not null)
            {
                Hover showing = menu ?? hover;

                hud.Build(
                    new HudState(
                        showing.Noun,
                        [.. showing.Actions.Select(a => a.LocalizedVerb)],
                        hover.Default,
                        pointer,
                        menu is not null,
                        menuIndex,
                        menuAt,
                        room?.Speaker,
                        room?.Caption,
                        story.Inventory.ItemsOf("GABRIEL"),
                        story.Inventory.ActiveItemOf("GABRIEL"),
                        InventoryOpen: true,
                        $"{scene.Name} - {story.Timeblock}"),
                    window.FramebufferWidth,
                    window.FramebufferHeight);

                renderer.SetOverlay(hud.Overlay);
            }

            // A door is a script that says SetLocation and nothing more. Noticing it here
            // rather than inside the action means it works however the story asked —
            // clicked, on a timer, or from a script three calls deep.
            if (!string.Equals(story.Location, here, StringComparison.OrdinalIgnoreCase) &&
                story.Location is { Length: > 0 } elsewhere)
            {
                Console.WriteLine($"Leaving {here} for {elsewhere}");
                return new RoomExit(0, elsewhere);
            }

            window.EndFrame();

            renderer.SetScene(geometry, view);

            if (renderer.DrawFrame(0f, 0f, 0f))
            {
                presented++;
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Presented {presented} frames in {stopwatch.Elapsed.TotalSeconds:F1}s "
            + $"({presented / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0} fps)"));

        return new RoomExit(0, null);
    }

    /// <summary>Why a room was left.</summary>
    /// <param name="Code">Process exit code, if this is the end of it.</param>
    /// <param name="Destination">Where the story went, or null when the player quit.</param>
    private readonly record struct RoomExit(int Code, string? Destination);

    /// <summary>
    /// Makes sure the scene is loaded at a point in the story, not merely at a time of day.
    /// </summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    /// <param name="timeblock">What the player asked for.</param>
    /// <returns>A request with a story behind it, where the room has one.</returns>
    /// <remarks>
    /// <para>
    /// <c>202P</c> is a point in the story and <c>N</c> is an asset suffix meaning night.
    /// Both are legitimate — see <see cref="SceneRequest"/> — and the render tooling wants
    /// the second, because looking at a room's night lighting should not require inventing
    /// a story to justify it.
    /// </para>
    /// <para>
    /// A game is different. With no story state the scene's conditions go undecided, no
    /// action files come into scope and no soundtrack is chosen: every object in the room
    /// answers to nothing and nobody says a word. That is a room, not a game, and it looks
    /// exactly like a broken one. So the launcher takes a real timeblock instead and says
    /// which, rather than running a version of the game where nothing can be done.
    /// </para>
    /// </remarks>
    private static SceneRequest Playable(GameArchives archives, string scene, string? timeblock)
    {
        SceneRequest asked = SceneRequest.For(scene, timeblock);

        if (asked.State is not null)
        {
            return asked;
        }

        IReadOnlyList<string> known = Timeblocks(archives, scene);

        if (known.Count == 0)
        {
            Console.WriteLine(
                $"Story: {scene} has no timeblock of its own, so its conditions stay " +
                "undecided and its objects answer to nothing.");

            return asked;
        }

        string chosen = known[0];

        Console.WriteLine(timeblock is { Length: > 0 } asOfDay
            ? $"Story: '{asOfDay}' is a time of day, not a point in the story, so nothing " +
              $"in the room would answer to anything. Using {chosen} instead."
            : $"Story: no timeblock given, so nothing in the room would answer to " +
              $"anything. Using {chosen}.");

        Console.WriteLine($"  {scene} knows: {string.Join(" ", known)}");

        return SceneRequest.For(scene, chosen);
    }

    /// <summary>The story timeblocks a scene has a file for.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    /// <returns>The codes, in order.</returns>
    /// <remarks>
    /// A scene's second file is named for the location and the timeblock together —
    /// <c>R25202P.SIF</c> — so the timeblocks a room has are what is left after its own
    /// name. The room's own <c>R25.SIF</c> leaves nothing and is skipped.
    /// </remarks>
    private static IReadOnlyList<string> Timeblocks(GameArchives archives, string scene)
    {
        string prefix = scene.ToUpperInvariant();

        return
        [
            .. archives.Names(".SIF")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null &&
                            n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            n.Length > prefix.Length)
                .Select(n => n![prefix.Length..])
                .Where(c => Timeblock.TryParse(c, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Where <c>--pointer X,Y</c> says the pointer is.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>The point, or null to follow the mouse.</returns>
    /// <remarks>
    /// For screenshots and for saying what is under a place without having to be there
    /// with a mouse. The rest of the loop cannot tell the difference, which is the point:
    /// what it photographs is what a player at that spot would see.
    /// </remarks>
    private static Vector2? Pinned(string[] args) =>
        Option(args, "--pointer")?.Split(',') is [string x, string y] &&
        float.TryParse(x, CultureInfo.InvariantCulture, out float px) &&
        float.TryParse(y, CultureInfo.InvariantCulture, out float py)
            ? new Vector2(px, py)
            : null;

    /// <summary>Reads an option's value from the command line.</summary>
    private static string? Option(string[] args, string name)
    {
        int at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>Where the game is usually installed relative to the repository.</summary>
    /// <remarks>
    /// A convenience for development only. Anything shipped reads its content path from
    /// configuration rather than guessing.
    /// </remarks>
    /// <summary>
    /// Where the enhanced textures are, if the player wants them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--enhanced &lt;dir&gt;</c> names them outright. <c>--workspace &lt;dir&gt;</c> is
    /// enough on its own, since they always live in the same place inside one, and a bare
    /// <c>--enhanced</c> with nothing after it takes the workspace beside the repository —
    /// a flag that reads as "yes please" and quietly does nothing is worse than no flag.
    /// </para>
    /// <para>
    /// None of them means the game looks exactly as it shipped, which has to stay the
    /// default: this content is a draft until somebody has reviewed it, and nobody should
    /// be shown generated art without having asked for it.
    /// </para>
    /// </remarks>
    private static string? EnhancedTextureDirectory(string[] args)
    {
        bool asked = args.Contains("--enhanced", StringComparer.OrdinalIgnoreCase);

        if (Option(args, "--enhanced") is { Length: > 0 } named && !named.StartsWith('-'))
        {
            return Path.IsPathRooted(named) || Option(args, "--workspace") is not { } under
                ? named
                : Path.Combine(under, named);
        }

        if (Option(args, "--workspace") is { Length: > 0 } workspace)
        {
            return Path.Combine(workspace, "enhanced", "textures");
        }

        return asked ? Path.Combine(DefaultWorkspaceDirectory(), "enhanced", "textures") : null;
    }

    /// <summary>Where the block-compressed build of the enhanced textures sits.</summary>
    /// <remarks>
    /// Beside the enhanced set rather than under it, because it is a build output and not a
    /// source: <c>build/textures</c> and <c>build/normals</c> in the same workspace. There
    /// is no separate flag to ask for it — anybody who has asked for enhanced textures wants
    /// the cheap form of them — but <c>--uncompressed</c> turns it off, which is what makes
    /// it possible to put the two side by side and see what the compression cost.
    /// </remarks>
    private static string CompressedTextureDirectory(string[] args, string enhancedDirectory)
    {
        if (Option(args, "--workspace") is { Length: > 0 } workspace)
        {
            return Path.Combine(workspace, "build");
        }

        // Up out of enhanced/textures, which is where --enhanced points by default.
        string? enhancedRoot = Path.GetDirectoryName(
            enhancedDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'));

        string? root = enhancedRoot is null ? null : Path.GetDirectoryName(enhancedRoot);

        return Path.Combine(root ?? DefaultWorkspaceDirectory(), "build");
    }

    /// <summary>Where the content workspace usually sits relative to the repository.</summary>
    /// <remarks>A convenience for development, like <see cref="DefaultDataDirectory"/>.</remarks>
    private static string DefaultWorkspaceDirectory() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "ContentWorkspace"));

    private static string DefaultDataDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "GK3", "Data"));

    /// <summary>
    /// Renders one frame with no window and writes it to a file.
    /// </summary>
    /// <returns>Process exit code.</returns>
    /// <remarks>
    /// A windowed run proves the code does not crash. Only reading the pixels back proves
    /// something was drawn, and the two failure modes look identical from outside.
    /// </remarks>
    private static int RenderOffscreen()
    {
        using Rendering.Vulkan.OffscreenRenderer renderer = Rendering.Vulkan.OffscreenRenderer.Create();

        Formats.Bitmaps.DecodedImage image = renderer.RenderTriangle(640, 360, (0.05f, 0.06f, 0.09f));
        string path = Path.Combine(AppContext.BaseDirectory, "offscreen.png");
        File.WriteAllBytes(path, Formats.Bitmaps.PngWriter.Encode(image));

        Console.WriteLine($"Rendered {image.Width}x{image.Height} on {renderer.DeviceName}");
        Console.WriteLine($"Wrote {path}");

        return 0;
    }

    /// <summary>
    /// Opens a window and presents frames.
    /// </summary>
    /// <param name="frameLimit">Stop after this many frames, or zero to run until closed.</param>
    /// <returns>Process exit code.</returns>
    /// <remarks>
    /// A frame limit makes this usable as a smoke test: it proves a device, swapchain and
    /// present loop work on a machine without needing anyone to close a window.
    /// </remarks>
    private static int RenderFrames(int frameLimit)
    {
        using var window = Platform.SilkGameWindow.Open("GK3Reborn");
        using var renderer = Rendering.Vulkan.VulkanRenderer.Create(window, window);

        Console.WriteLine($"Renderer: {renderer}");

        window.Resized += (_, _) => renderer.Invalidate();

        int presented = 0;
        int attempts = 0;

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();

            // The clear colour walks so the window visibly animates rather than looking
            // like a still image that might be a frozen first frame.
            float t = presented / 120f;
            if (renderer.DrawFrame(0.05f + (0.05f * MathF.Sin(t)), 0.06f, 0.09f))
            {
                presented++;
            }

            if (++attempts > 100_000)
            {
                break;
            }
        }

        Console.WriteLine($"Presented {presented} frames at {renderer.SwapchainSize.Width}x"
            + $"{renderer.SwapchainSize.Height} across {renderer.SwapchainImageCount} swapchain images");

        return 0;
    }

    /// <summary>
    /// Prints what the machine's graphics hardware can do.
    /// </summary>
    /// <remarks>
    /// Runs before any window exists, so it doubles as a diagnostic on a machine that
    /// cannot run the game at all. A device that cannot present is reported rather than
    /// treated as an error, because saying why is more useful than failing.
    /// </remarks>
    /// <summary>Prints what a device survey found.</summary>
    /// <param name="report">
    /// The survey, or null to make one. A caller that already has a renderer should pass its
    /// own: building an instance purely to look through it is 145 ms of the time to a first
    /// frame, and doing it on another thread to hide that lost a device about one run in six.
    /// </param>
    private static void ReportGraphics(Rendering.Vulkan.VulkanDeviceReport? report = null) =>
        Console.Write(GraphicsReport(report ?? Rendering.Vulkan.VulkanDeviceSelector.Survey()));

    private static string GraphicsReport(Rendering.Vulkan.VulkanDeviceReport report)
    {
        var text = new System.Text.StringBuilder();

        if (!report.VulkanAvailable)
        {
            return text.AppendLine(CultureInfo.InvariantCulture, $"Vulkan unavailable: {report.Unavailable}").ToString();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Vulkan: {report.Devices.Count} device(s), "
            + $"validation layers {(report.ValidationAvailable ? "available" : "not installed")}");

        foreach (Rendering.Vulkan.VulkanDeviceInfo device in report.Devices)
        {
            bool selected = ReferenceEquals(device, report.Selected);
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(selected ? "*" : " ")} {device}");

            foreach (string note in device.TierNotes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"      {note}");
            }
        }

        if (report.Selected is null)
        {
            text.AppendLine("  no device can present; the game cannot render here");
        }

        return text.ToString();
    }
}

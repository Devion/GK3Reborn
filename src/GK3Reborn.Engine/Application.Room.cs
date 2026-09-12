using System.Diagnostics;
using GK3Reborn.Rendering.Geometry;
using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using GK3Reborn.Rendering.Materials;
using GK3Reborn.Rendering.Vulkan;
using GK3Reborn.Sheep;

namespace GK3Reborn;
/// <summary>The frame loop for a room the player is in.</summary>
public static partial class Application
{
    /// <summary>Runs the present loop with a camera the player can move.</summary>
    /// <returns>Why the room was left, and where for.</returns>
    /// <param name="window">The window and its input.</param>
    /// <param name="renderer">The renderer.</param>
    /// <param name="geometry">The scene's geometry.</param>
    /// <param name="scene">The scene.</param>
    /// <param name="cameraName">Which camera to open on, if any.</param>
    /// <param name="frameLimit">Stop after this many frames, or zero for no limit.</param>
    /// <param name="update">The world going on by itself.</param>
    /// <param name="interaction">Turns pointing at the room into doing something to it.</param>
    /// <param name="room">What the room sounds like, if there is a device.</param>
    /// <param name="movies">What plays a cutscene when a script asks for one.</param>
    /// <param name="hud">The interface, if there is a font to draw it with.</param>
    /// <param name="cut">Cuts a sheet of letters for the window's current size, so one that changes size is drawn at the new one rather.</param>
    /// <param name="api">The script API, for the save store and for the room a load asks the game to move to.</param>
    /// <param name="screens">What draws the screens in front of the room, if anything can.</param>
    /// <param name="icons">The picture belonging to an inventory item, where it has one.</param>
    /// <param name="closeUps">The bigger picture the artists painted of an item, for the screen that shows one thing rather than a list.</param>
    /// <param name="verbIcons">The picture belonging to a verb, resting or picked out, where it has one.</param>
    /// <param name="artwork">The game's own art by file name, for the GPS.</param>
    /// <param name="rig">The room's lights as it was laid with them, so that a mechanism which adds lights of its own can have the whole.</param>
    /// <param name="sidney">Grace's computer, which one of those screens is.</param>
    /// <param name="map">The driving map's art and roads.</param>
    /// <param name="binoculars">What can be seen from here, if anything.</param>
    /// <param name="story">Where the story stands, for the inventory strip.</param>
    /// <param name="console">The developer console, which outlives the room.</param>
    /// <param name="front">The menu, which Escape opens.</param>
    /// <param name="pages">What draws it, or null when there is no font to draw with.</param>
    /// <param name="apply">What to do with a setting the moment it changes.</param>
    /// <param name="relanguaged">Asks whether the language was changed since it was last asked, and forgets that it was.</param>
    /// <param name="options">The command line, for the debugging switches.</param>
    /// <param name="strings">What the game calls places and times, for the corner of the screen.</param>
    /// <param name="journal">The quest log, which the journal screen draws and the hint button asks.</param>
    /// <param name="fade">The transition into this room, which the loop lifts one frame at a time so that the room is live underneath it.</param>
    private static RoomExit FlyScene( Rendering.ScreenFade fade, Platform.SilkGameWindow window, Rendering.IRenderer renderer, SceneGeometry geometry,
        LoadedScene scene, string? cameraName, int frameLimit, SceneUpdate update, SceneInteraction interaction, SceneAudio? room,
        Game.MoviePlayer movies, GameHud? hud, Func<bool, OverlayAtlas?> cut, Gk3SheepApi api, ScreenPainter? screens, Func<string, ItemIcon> icons,
        Func<string, ItemIcon> closeUps, Func<string, bool, ItemIcon> verbIcons, Func<string, ItemIcon> artwork,
        IReadOnlyList<Formats.Scenes.AuthoredLight> rig, Game.Sidney.SidneyMachine? sidney, DrivingMap map, Binoculars binoculars, GameState story,
        GameConsole console, FrontEnd front, MenuPage? pages, Action<Settings> apply, Func<bool> relanguaged, string[] options, GameStrings strings,
        Game.Story.Journal journal)
    {
        ArgumentNullException.ThrowIfNull(fade);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(cut);

        // The hose, while one is being aimed.
        Game.WaterAiming? aiming = null;

        // The fingerprint kit, while one is open.
        Game.FingerprintDusting? dusting = null;

        // Where the pointer was last frame, so the kit knows how far the brush travelled.
        Vector2 brushWas = default;

        // Who is out on the roads while the map is open, and how far along their roads they have got.
        Game.DrivingTraffic? traffic = null;

        // Which screen the traffic was built for, so that opening the map, closing it and opening it again starts everybody where they set out.
        string? trafficFor = null;

        // Clicks the room has swallowed in a row for being busy, with nobody speaking.
        int refused = 0;

        // Whether the headset's list of topics is up, and which of them is picked out.
        bool radioOpen = false;
        int radioIndex = 0;

        // How many topics were said to be on offer last time it changed.
        int radioSaid = -1;
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(closeUps);
        ArgumentNullException.ThrowIfNull(verbIcons);
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(rig);

        string here = scene.Name;

        // Putting the binoculars down from a zoomed view.
        RoomExit Lower(Game.BinocularView looking)
        {
            if (looking.Sight.Leaving is { Length: > 0 } after && api.Perform( "CallSheep",
                    [
                        Sheep.SheepValue.FromString("binocs"), Sheep.SheepValue.FromString(after), ]) is not null)
            {
                Log.Info($"Binoculars: {after} put {scene.Name} back");
            }

            api.Leaning = null;
            api.Resuming = looking;
            api.WantedCamera = (looking.Eye, looking.Look);

            // Still through the eyepieces: leaning back out is not putting them down.
            story.Screens.Replace(new Screen(ScreenKind.Binoculars));
            update.Cancel();

            Log.Info($"Binoculars raised again at {looking.From}");

            return new RoomExit(0, looking.From);
        }

        // What rises off the room's fires.
        IReadOnlyList<Game.Flame> burning = Game.Flames.In(scene.Models, api.Animations, scene.Bitmaps);

        var smoke = new Game.FlameParticles( burning, volumes: !options.Contains("--no-shader-fire", StringComparer.OrdinalIgnoreCase));

        smoke.Follow(scene.Models);

        // And what those fires are burning over.
        smoke.Holds(Game.Flames.Holding(burning, geometry.SceneObjectBoxes()));

        if (smoke.Glints > 0)
        {
            Log.Info($"Fire: {smoke.Glints} thing(s) lying in a fire, glinting");
        }

        // And what is in the sky over it.
        bool noBirds = options.Contains("--no-birds", StringComparer.OrdinalIgnoreCase);

        Game.Flock overhead = Game.SceneBirds.For(here, story.Timeblock);

        var birds = new Game.BirdFlock( overhead, Game.SceneBirds.Over(overhead, scene.Geometry, scene.Walkable, scene.Cameras));

        if (birds.Count > 0)
        {
            Log.Info(string.Create( CultureInfo.InvariantCulture, $"Birds: {birds.Count} over {here}, wheeling at " +
                $"({birds.Wheel.Centre.X:F0}, {birds.Wheel.Centre.Y:F0}, " + $"{birds.Wheel.Centre.Z:F0}) within {birds.Wheel.Radius:F0} of it, " +
                $"{overhead.Wingspan:F0} across at " + $"{Game.BirdFlock.FlapsPerSecond(overhead.Wingspan):F1} beats a second"));
        }

        // And what its fountains throw.
        bool noSpray = options.Contains("--no-spray", StringComparer.OrdinalIgnoreCase);

        var spray = new Game.FountainSpray(Game.Fountains.In(scene.Models));

        if (spray.Any)
        {
            Log.Info($"Fountain: {spray.Rings} ring(s) of falling water throwing spray");
        }

        // And what is alight in somebody's hand.
        var smoking = new Game.CigaretteSmoke(Game.Cigarettes.In(scene.Models));

        if (smoking.Any)
        {
            Log.Info($"Cigarette: {smoking.Count} alight, giving off smoke");
        }

        // And what drifts through it: insects under its trees and dust in its air.
        bool noInsects = options.Contains("--no-insects", StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<Game.Crown> crowns = Game.SceneDrift.Crowns(scene.Geometry, scene.Models);

        var drifting = new Game.DriftField( Game.SceneDrift.For(story.Timeblock, crowns.Count, sunlit: scene.Sun is not null), crowns,
            Game.SceneDrift.Seed(here));

        if (drifting.Any)
        {
            Log.Info(string.Create( CultureInfo.InvariantCulture, $"Drift: {crowns.Count} crown(s) over {here}, up to {drifting.InsectCount} " +
                $"insect(s), {drifting.MoteCount} specks of dust, wind bearing " +
                $"{MathF.Atan2(drifting.Wind.X, drifting.Wind.Z) * 180f / MathF.PI:F0}"));
        }

        // The dust hangs in the daylight at the windows, where there is any, and is lit by it.
        bool noSunRays = options.Contains("--no-sun-rays", StringComparer.OrdinalIgnoreCase);

        // Whether the binoculars have been raised to the player's eyes this time round.
        bool throughEyes = false;

        // The last caption logged, so each line is said once.
        string? captioned = null;

        // Whether anything was handed to the blended pass last frame.
        bool blending = false;

        // The room's smoke, embers, beams and birds, moved on and handed over sorted for the eye they are about to be seen by.
        void BlendAir(Camera view, float delta)
        {
            // And whatever the room's own machinery wants blended: CS2's laser beams are drawn as light scattering in the air, which is the one.
            if (api.Mechanism is { } machine)
            {
                machine.Tracing = renderer.Quality;

                // And its own lights, where it has any that move.
                if (machine.LightsMoved)
                {
                    renderer.SetLights(
                        [.. rig, .. machine.Lights],
                        new SceneExtent(geometry.Minimum, geometry.Maximum));
                }
            }

            IReadOnlyList<Rendering.Particle> blended = api.Mechanism?.Particles(view.Position) ?? [];

            if (smoke.Emitters > 0)
            {
                smoke.Advance(delta, view.Position);

                IReadOnlyList<Rendering.Particle> puffs = smoke.Facing(view.Position);

                // Both, where a room has both. Neither list is long and the pass takes one.
                blended = blended.Count == 0 ? puffs : [.. puffs, .. blended];
            }

            // And the birds, ahead of all of it.
            if (birds.Count > 0)
            {
                birds.Advance(delta);

                if (front.Settings.Birds && !noBirds)
                {
                    IReadOnlyList<Rendering.Particle> flying = birds.Facing(view);

                    blended = blended.Count == 0 ? flying : [.. flying, .. blended];
                }
            }

            // And the spray off the fountains, which is in the room rather than over it.
            if (spray.Any)
            {
                spray.Advance(delta, view.Position);

                if (!noSpray)
                {
                    IReadOnlyList<Rendering.Particle> thrown = spray.Facing(view.Position);

                    if (thrown.Count > 0)
                    {
                        blended = blended.Count == 0 ? thrown : [.. blended, .. thrown];
                    }
                }
            }

            // And what a lit cigarette gives off.
            if (smoking.Any)
            {
                smoking.Advance(delta, view.Position);

                IReadOnlyList<Rendering.Particle> puffing = smoking.Facing(view.Position);

                if (puffing.Count > 0)
                {
                    blended = blended.Count == 0 ? puffing : [.. blended, .. puffing];
                }
            }

            // And the insects and the dust, after the rest.
            if (drifting.Any)
            {
                drifting.LitBy(front.Settings.SunRays && !noSunRays ? scene.Shafts : []);
                drifting.Advance(delta, view);

                if (front.Settings.InsectsAndDust && !noInsects)
                {
                    IReadOnlyList<Rendering.Particle> adrift = drifting.Facing(view);

                    if (adrift.Count > 0)
                    {
                        blended = blended.Count == 0 ? adrift : [.. blended, .. adrift];
                    }
                }
            }

            if (blended.Count > 0 || blending)
            {
                renderer.SetParticles(blended);
                blending = blended.Count > 0;
            }
        }

        int cameraIndex = Math.Max( 0, scene.Cameras.ToList().FindIndex(c => string.Equals(
                c.Name, cameraName ?? scene.CameraNamed(null)?.Name, StringComparison.OrdinalIgnoreCase)));

        Camera template = SceneLoader.CameraFor(scene, geometry, cameraName);

        var camera = new FreeCamera
        {
            Speed = MathF.Max(50f, (geometry.Maximum - geometry.Minimum).Length() * 0.15f),
        };

        // Whoever asked for the shell to be turned off is looking at the room rather than playing it, and the story is not allowed to take the.
        bool onTheCommandLine = options.Contains("--free-camera", StringComparer.OrdinalIgnoreCase);

        bool Flying() => onTheCommandLine || front.Settings.FreeCamera;

        // The player as a body in the room rather than a camera over it.
        var walker = new Game.Navigation.FirstPerson
        {
            CanStand = scene.Walkable is { } floor ? floor.IsWalkable : scene.CameraShell is { IsEmpty: false } fence
                    ? at => fence.Contains(at + (Vector3.UnitY * Game.Navigation.Walker.StandOff)) : null,

            Ground = scene.Ground is { } underfoot ? underfoot.Height : null,
        };

        bool onFootFromTheCommandLine = options.Contains("--first-person", StringComparer.OrdinalIgnoreCase);

        // --push X,Y holds the movement controls for the whole run, which is the only way a run with no keyboard can walk anywhere: the way out of a.
        Vector2 pushed = Pushed(options);

        // Asked every frame for the reason the free camera is: it is a row on the Playing page, and a setting a player can only see work by leaving.
        bool OnFoot() => (onFootFromTheCommandLine || front.Settings.FirstPerson) && !Flying();

        // The shell the scene's artists drew around the space the camera may occupy.
        if (scene.CameraShell is not { IsEmpty: false } shell)
        {
            Log.Info("Camera bounds: none, so the camera may go anywhere");
        }
        else
        {
            // A script may turn the shell off for a shot that has to be outside it, and the original only turns it off until the next room — so this.
            camera.Confine = (from, movement) => story.CameraBoundaries && !Flying() ? shell.Resolve(from, movement) : from + movement;

            if (Flying())
            {
                Log.Info("Camera bounds: off, so the camera may leave the room");
            }

            // A viewpoint outside its own shell is not fatal — the way back in is always open — but it is worth saying, because from out there the.
            else if (!shell.Contains(template.Position))
            {
                Log.Info($"Camera bounds: {scene.Name}'s view starts outside them");
            }
        }

        camera.CopyFrom(template);

        // And the body looks where the room's own camera looks, so that arriving on foot faces whatever the scene was composed to show.
        void Aim(FreeCamera at)
        {
            walker.Yaw = at.Aim.X * MathF.PI / 180f;
            walker.Pitch = at.Aim.Y * MathF.PI / 180f;
        }

        Aim(camera);

        // Except that in first person the view is the ego's own head: the room is entered looking the way they are facing, not the way its opening.
        if (update.Turned(story.Ego) is { } entered)
        {
            walker.Yaw = entered;
        }

        // A room reached by leaning in through the binoculars starts at the camera the binoculars named rather than at the room's own, and a room.
        if (api.WantedCamera is { } leaned)
        {
            api.WantedCamera = null;

            camera.Position = leaned.Position;
            camera.Aim = leaned.Angle;

            Log.Info(string.Create( CultureInfo.InvariantCulture,
                $"Arrived at a view of the room's own choosing: {leaned.Position:F0} looking {leaned.Angle.X:F0}"));
        }

        // --eye and --aim put the camera where no authored camera stands.
        Vector3? standing = Standing(options);
        Vector2? looking = Aimed(options);

        void Place()
        {
            if (standing is { } eye)
            {
                camera.Position = eye;
                walker.Position = eye - (Vector3.UnitY * Game.Navigation.FirstPerson.Eyes);
            }

            if (looking is { } look)
            {
                camera.Aim = look;
                Aim(camera);
            }

            // On foot the camera is worked out from the body every frame, so putting it somewhere means standing the player there and pointing them.
            if (OnFoot() && (standing is not null || looking is not null))
            {
                update.Step(story.Ego, walker.Position, walker.Yaw);
            }
        }

        if (standing is not null || looking is not null)
        {
            Place();

            Log.Info(string.Create( CultureInfo.InvariantCulture,
                $"Camera placed at {camera.Position:F0} looking {camera.Aim.X:F1}, {camera.Aim.Y:F1}"));
        }

        Log.Info();
        update.StartAt(template);

        Camera? directing = update.View;

        var stopwatch = Stopwatch.StartNew();
        double previous = 0;

        // Whether a movie was on screen last frame, so the renderer is told to stop drawing one exactly once rather than every frame for the rest of.
        bool showingMovie = false;
        int saidAboutMovies = 0;
        int presented = 0;
        string? hovering = null;
        string? spoken = null;
        int said = 0;
        Hover? menu = null;
        Vector2 menuAt = Vector2.Zero;
        int menuIndex = 0;

        // Whether the ego's model is being stood in rather than looked at, so it is taken out of the picture and put back exactly once each way.
        bool embodied = false;

        // How near their own eyes the view has to be for that to be true, in scene units.
        const float InsideTheHead = 45f;

        // And whether the view was in the player's own eyes last frame, so that coming back to them is a move rather than an arrival — and whether.
        bool afoot = false;
        bool everAfoot = false;

        // How long the player has been pushing into something and getting nowhere, so that a doorframe is told from an ego nothing ever placed.
        float stuck = 0f;
        const float StrandedFor = 1f;

        // Which way the story last had the ego facing, so that a turn it makes is told apart from the player turning their own head.
        float turned = float.NaN;

        // And which way it is still turning them, which the view follows round rather than being pinned to.
        float? turningTo = null;

        // How fast the view follows the body round, in radians a second. Under the actors' own Walker.TurnRate, because a head that kept up with.
        const float TurningHead = 4.5f;

        // The last way out walked into, and when, so that one which answers with a line rather than a door is not run again on every frame the.
        string? tried = null;
        double triedAt = double.NegativeInfinity;
        const double ExitAgainAfter = 3.0;

        // How long a room is safe to arrive in before a way out will take.
        const double ExitNotAtOnce = 1.0;

        // The lobby's glass whose print is waiting on an answer; see Game.DirtyGlasses.
        GlassQuestion? glassAsked = null;

        // Whether Sidney was up last frame, so that putting it away can run what the original runs then.
        bool sidneyWasUp = false;
        Vector2? pinned = Pinned(options);
        bool forceMenu = options.Contains("--menu", StringComparer.OrdinalIgnoreCase);

        // --console opens it and types into it, which is the only way to photograph it: a headless run has no keyboard, and an interface nobody can.
        if (Option(options, "--console") is { } typed)
        {
            console.Show(true);
            console.Type(typed);
        }

        // --run types a command and presses Enter, which is how a headless run drives the game: a walk, a flag, a script function, anything the.
        var deferred = new List<(int Frame, string Command)>();

        // --click 90;150@1232,35 presses the primary button on those frames.
        var clicks = new List<(int Frame, Vector2? At)>();

        foreach (string press in Option(options, "--click")?.Split( ';', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            string[] parts = press.Trim().Split('@');

            if (int.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out int when))
            {
                clicks.Add(( when, parts.Length > 1 && parts[1].Split(',') is [string cx, string cy] &&
                    float.TryParse(cx, CultureInfo.InvariantCulture, out float px) && float.TryParse(cy, CultureInfo.InvariantCulture, out float py)
                        ? new Vector2(px, py) : null));
            }
        }

        if (Option(options, "--run") is { } command)
        {
            // Several calls, separated by semicolons, run in order in the same frame — a teleport and then the question that depends on it.
            foreach (string one in command.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string call = one.Trim();

                if (call.StartsWith('@') && call.IndexOf(' ') is > 1 and var split && int.TryParse(call[1..split], out int at))
                {
                    deferred.Add((at, call[(split + 1)..].Trim()));
                }
                else
                {
                    deferred.Add((0, call));
                }
            }

            deferred.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            Run(0);
        }

        void Run(int frame)
        {
            // Before the commands, because a command is typed into the console and a click is not: a frame that does both should read as the player.
            if (clicks.FindIndex(c => c.Frame == frame) is int press and >= 0)
            {
                if (clicks[press].At is { } moved)
                {
                    pinned = moved;
                }

                clicks.RemoveAt(press);
                window.Press(Platform.PointerButton.Primary);

                Log.Info(string.Create( CultureInfo.InvariantCulture, $"click at frame {frame}, at {pinned?.X ?? -1:F0},{pinned?.Y ?? -1:F0}"));
            }

            while (deferred.Count > 0 && deferred[0].Frame <= frame)
            {
                console.Show(true);
                console.Type(deferred[0].Command);
                deferred.RemoveAt(0);

                int before = console.Lines.Count;
                console.Submit();

                // Mirrored to the terminal, because a headless run has no way to read the console's own scrollback and an answer nobody can read is.
                foreach (ConsoleLine line in console.Lines.Skip(before))
                {
                    Log.Info($"run: {line.Text}");
                }

                console.Show(false);
            }
        }

        if (pinned is { } spot)
        {
            Log.Info($"Pointer pinned at {spot.X}, {spot.Y}");
        }

        // What the interface was laid out for.
        int laidOutFor = window.FramebufferHeight;

        // Whether the pointer was down last frame, so that picking a marked place up can happen on the edge of the press.
        bool heldLastFrame = false;
        var panFrom = Vector2.Zero;
        float laidOutAt = front.Settings.TextScale;

        bool flicker = options.Contains("--flicker", StringComparer.OrdinalIgnoreCase);
        byte[]? previousFrame = null;
        double flickerTotal = 0;
        int flickerFrames = 0;

        // Nothing has been clicked on in this room yet.
        window.Forget();

        while (!window.IsClosing && (frameLimit == 0 || presented < frameLimit))
        {
            window.PumpEvents();
            Run(presented);

            double now = stopwatch.Elapsed.TotalSeconds;
            float delta = (float)Math.Min(0.1, now - previous);
            previous = now;

            // Told before anything the story does this frame, because which camera a conversation picks and whether it is moved to both depend on.
            story.FirstPerson = OnFoot();
            story.ViewIsTheirs = story.FirstPerson && afoot;

            // On foot the player is the ego, whoever has the camera, and where they stand is their answer rather than their pose's.
            update.Driven = OnFoot() && !update.Performing(story.Ego) ? story.Ego : null;

            // A window that goes fullscreen doubles in height.
            if (hud is not null && (window.FramebufferHeight != laidOutFor || front.Settings.TextScale != laidOutAt))
            {
                laidOutFor = window.FramebufferHeight;
                laidOutAt = front.Settings.TextScale;

                if (cut(false) is { } grown)
                {
                    int magnify = grown.Scalable || grown.Font is null ? 1 : Magnification(grown.Font, UI.TextSizing.Sheet(laidOutFor, laidOutAt));

                    if (!grown.Scalable && grown.Name.Equals(hud.Overlay.Atlas.Name, StringComparison.Ordinal))
                    {
                        // The sheet is right and only the magnification wrong, which costs a field rather than a rebuild.
                        hud.Overlay.Magnify = magnify;

                        if (screens is not null)
                        {
                            screens.Overlay.Magnify = magnify;
                        }
                    }
                    else
                    {
                        hud.Retarget(grown);
                        hud.Overlay.Magnify = magnify;

                        // The screens in front of the room are cut from the same sheet as the captions and were being left on the old one: the.
                        if (screens is not null)
                        {
                            screens.Retarget(grown);
                            screens.Overlay.Magnify = magnify;
                        }

                        Log.Info( $"Interface: {grown.Name} at {grown.Height}px" + (magnify > 1 ? $" x{magnify}" : string.Empty) +
                            $" for {laidOutFor} lines");
                    }
                }

                if (pages is not null && cut(true) is { } wider)
                {
                    pages.Retarget(wider);
                }
            }

            // The console first, and while it is open it has the keyboard: every key below means something else to it.
            bool typing = console.Open || Spelling(story, sidney);

            if (window.WasPressed(Platform.EditKey.Console))
            {
                console.Show(!console.Open);
            }
            else if (typing)
            {
                Typing(window, console);
            }

            if (movies.Playing)
            {
                // A movie has the screen and the keyboard.
                if (window.WasPressed(Platform.CameraAction.Quit))
                {
                    movies.Stop();
                }
                else
                {
                    movies.Advance(delta);
                }

                renderer.SetMovieFrame(movies.Frame);
                showingMovie = true;

                for (; saidAboutMovies < movies.Diagnostics.Items.Count; saidAboutMovies++)
                {
                    Log.Report(movies.Diagnostics.Items[saidAboutMovies]);
                }
            }
            else if (showingMovie)
            {
                // Once, on the frame after it ended, rather than every frame afterwards.
                renderer.SetMovieFrame(null);
                showingMovie = false;
            }

            // A screen first.
            if (!typing && !movies.Playing && story.Screens.Top is not null && window.WasPressed(Platform.CameraAction.Quit))
            {
                // Leaning into another room through the binoculars is the one screen a pop cannot close: the room under it is the looked-at one.
                if (api.Leaning is { } backing)
                {
                    return Lower(backing);
                }

                story.Screens.Back();
            }
            else if (!typing && !movies.Playing && window.WasPressed(Platform.CameraAction.Quit))
            {
                if (pages is null)
                {
                    // No font, so there is no menu to open and Escape means what it used to.
                    break;
                }

                // The same press is still on the frame's books as an editing key, and the menu reads that one to close itself.
                window.EndFrame();

                // The menu is a screen and screens are pointed at.
                window.PointerLocked = false;

                front.InGame = true;

                // The room as the player last saw it, taken before the menu is drawn over it.
                Formats.Bitmaps.DecodedImage? seen = renderer.Capture();

                // From the top, every time.
                front.Show(FrontEndPage.Main);

                // What the slots hold, before the page can draw them.
                front.Saves = api.Saves?.List() ?? [];
                front.Illustrations = slot => Illustration(renderer, api.Saves, slot);

                FrontEndOutcome chose = ShowMenu( window, renderer, pages, front, apply, MenuBehind.Room, () => cut(true));

                if (chose is FrontEndOutcome.Save && front.Slot is { Length: > 0 } into)
                {
                    // Named for where the player is, when they have not named it themselves.
                    string called = front.Naming is { Length: > 0 } given ? given : strings.Where(scene.Name, story.Timeblock.ToString());

                    bool wrote = api.Saves?.Write(into, story.Capture(called)) ?? false;

                    // And a picture of the room, from the last frame drawn before the menu went up.
                    if (wrote && seen is { } photograph)
                    {
                        api.Saves?.Illustrate(into, Thumbnail(photograph));

                        // The renderer is holding the old picture for this slot under the same name.
                        renderer.DropOverlayPicture("save:" + into);
                    }

                    Log.Info(wrote ? $"Saved to {into}: {called}" : $"Could not save to {into}.");

                    front.Naming = string.Empty;
                    renderer.SetOverlay(null);
                    continue;
                }

                if (chose is FrontEndOutcome.Load && front.Slot is { Length: > 0 } from &&
                    api.Saves?.Read(from, out Game.SaveFault fault) is { } recovered)
                {
                    api.RestoreGame(recovered);

                    Log.Info($"Restored {from}: {recovered.Title}");

                    // The room the save was written in, which is very likely not this one.
                    api.Wanted = null;
                    renderer.SetOverlay(null);

                    // A save that names no room leaves the player where they are with the story restored around them, which is odd but survivable.
                    if (story.Location is not { Length: > 0 } saved)
                    {
                        continue;
                    }

                    update.Cancel();

                    return new RoomExit(0, saved);
                }

                // The room has been standing still behind the menu and the clock has not.
                previous = stopwatch.Elapsed.TotalSeconds;

                // Whatever the story was holding, it is not holding it any more.
                if (chose is FrontEndOutcome.Unstick)
                {
                    IReadOnlyList<string> let = update.Unstick();

                    if (let.Count == 0)
                    {
                        Log.Info("Unstuck: nothing was holding the room.");
                    }
                    else
                    {
                        Log.Info("Unstuck: let go of " + string.Join(", ", let) + ".");
                    }
                }

                if (chose is FrontEndOutcome.Quit)
                {
                    break;
                }

                // The language moved while the menu was up.
                if (relanguaged())
                {
                    if (story.Location is { Length: > 0 } again)
                    {
                        Log.Info($"Language: loading {again} again for it.");

                        update.Cancel();
                        renderer.SetOverlay(null);

                        return new RoomExit(0, again);
                    }
                }

                // Whatever the interface last drew belonged to the menu.
                renderer.SetOverlay(null);
                continue;
            }

            if (!typing && window.WasPressed(Platform.CameraAction.NextCamera) && scene.Cameras.Count > 0)
            {
                cameraIndex = (cameraIndex + 1) % scene.Cameras.Count;
                template = SceneLoader.CameraFor(scene, geometry, scene.Cameras[cameraIndex].Name);
                camera.CopyFrom(template);
                Aim(camera);

                Log.Info($"camera: {scene.Cameras[cameraIndex].Name}");
            }

            if (!typing && window.WasPressed(Platform.CameraAction.Reset))
            {
                camera.CopyFrom(template);
                Aim(camera);
            }

            // Pockets, from a key rather than from a small target at the edge of the screen.
            if (!typing && window.WasPressed(Platform.CameraAction.Inventory) && story.Screens.InventoryReachable)
            {
                if (story.Screens.IsOnTop(ScreenKind.Inventory))
                {
                    story.Screens.Back();
                }
                else
                {
                    story.Screens.Show(new Screen(ScreenKind.Inventory));
                }
            }

            // The quest log.
            if (!typing && window.WasPressed(Platform.CameraAction.Journal) && story.Screens.InventoryReachable)
            {
                if (story.Screens.IsOnTop(ScreenKind.Journal))
                {
                    story.Screens.Back();
                }
                else
                {
                    story.Screens.Show(new Screen(ScreenKind.Journal));
                }
            }

            if (!typing && window.WasPressed(Platform.CameraAction.QuickSave))
            {
                bool wrote = api.Saves?.Write( Game.SaveStore.QuickSlot, story.Capture("Quick save")) ?? false;

                Log.Info(wrote ? "Saved." : "Could not save.");
                console.Print(wrote ? "Saved." : "Could not save.");
            }

            if (!typing && window.WasPressed(Platform.CameraAction.QuickLoad))
            {
                Game.SaveGame? loaded = api.Saves?.Read(Game.SaveStore.QuickSlot, out Game.SaveFault fault) is { } read &&
                    fault == Game.SaveFault.None ? read : null;

                if (loaded is null)
                {
                    Log.Info("No quick save to load.");
                    console.Print("No quick save to load.");
                }
                else
                {
                    api.RestoreGame(loaded);
                    api.Wanted = loaded.Location;

                    Log.Info($"Loaded: {loaded.Summary}");
                }
            }

            // A load names the room the save was taken in, and it may be this one — in which case the ordinary "the story moved us" test below would.
            if (api.Wanted is { Length: > 0 } restored)
            {
                api.Wanted = null;
                update.Cancel();

                return new RoomExit(0, restored);
            }

            if (!typing && window.WasPressed(Platform.CameraAction.CycleRayTracing) && renderer.SupportsRayTracing)
            {
                RayTracingQuality[] levels = Enum.GetValues<RayTracingQuality>();

                renderer.Quality = levels[(Array.IndexOf(levels, renderer.Quality) + 1) % levels.Length];
                Log.Info($"ray tracing: {renderer.Quality}");
            }

            // What the world could not do, said once.
            for (; said < update.Diagnostics.Items.Count; said++)
            {
                Log.Info($"  {update.Diagnostics.Items[said]}");
            }

            foreach (string happened in update.Advance(delta))
            {
                Log.Info(string.Create( CultureInfo.InvariantCulture, $"  [{stopwatch.Elapsed.TotalSeconds:F2}s] {happened}"));
            }

            // Every line as it starts, so a headless run can show that somebody spoke.
            if (room?.Caption is { Length: > 0 } line && !ReferenceEquals(line, captioned))
            {
                captioned = line;
                Log.Info(string.Create( CultureInfo.InvariantCulture, $"  [{stopwatch.Elapsed.TotalSeconds:F2}s] {room.Speaker ?? "?"}: {line}"));
            }

            // The story moving the camera takes it back off the player, for as long as it is moving.
            if (!ReferenceEquals(update.View, directing) && update.View is { } directed)
            {
                directing = update.View;
                template = directed;
                camera.CopyFrom(directed);

                Place();
            }

            // And while it is telling one, the camera is the story's rather than the player's: see SceneUpdate.Directing, which is the whole rule.
            bool theirs = !typing && story.Screens.InTheRoom && !(update.Directing && !Flying());

            // The mouse is taken for looking about only while the player is in the room with nothing in front of it.
            window.PointerLocked = OnFoot() && !typing && story.Screens.InTheRoom && menu is null && !movies.Playing &&
                !window.IsHeld(Platform.CameraAction.FreeCursor);

            // Standing in the room rather than floating over it.
            bool onFoot = OnFoot() && !typing && story.Screens.InTheRoom && !Flying() && !update.Framed;
            bool walking = false;
            bool shouldered = false;

            Vector3 travelling = walker.Ahead;

            // Coming back from a shot the story was holding, the view travels back into the player's own eyes instead of arriving in them.
            if (onFoot && !afoot && everAfoot)
            {
                Vector2 was = camera.Aim;

                walker.ReturnFrom( camera.Position, was.X * MathF.PI / 180f, was.Y * MathF.PI / 180f);
            }

            afoot = onFoot;
            everAfoot |= onFoot;

            if (onFoot)
            {
                // Whatever shot was built for a conversation is the story's, and the story has let go of the camera.
                update.Stage(null);

                // Where the story has left the player.
                walker.Position = update.Where(story.Ego) ?? update.ModelNamed(story.Ego)?.Standing.Translation ?? walker.Position;


                // The controls are the player's while nothing is being done to them — not while nothing at all is happening, which is a room where.
                bool free = menu is null && !movies.Playing;
                bool driving = free && !update.Busy(story.Ego);

                // Which way the story has them facing.
                float facing = update.Turned(story.Ego) ?? walker.Yaw;

                if (driving)
                {
                    turningTo = null;
                }
                else if (MathF.Abs(Game.Navigation.Walker.Wrapped(facing - turned)) > 0.001f)
                {
                    turningTo = facing;
                }

                walker.Speed = front.Settings.FirstPersonSpeed;

                var asked = new Game.Navigation.FirstPersonInput( Pushing(window) + pushed, Looking(window, front.Settings, delta),
                    window.IsHeld(Platform.CameraAction.Fast));

                // Reaching for the mouse takes the view back, so a turn the story started is never fought over.
                if (asked.Look != Vector2.Zero)
                {
                    turningTo = null;
                }

                // The story's turns arrive in steps and land whole — a walk's first corner, an arrival facing, a clip settling — and pinning the
                // view to the body wrote every one of those into the picture as a jerk. Turned towards instead, at a rate a neck could manage.
                if (turningTo is { } toward)
                {
                    float left = Game.Navigation.Walker.Wrapped(toward - walker.Yaw);
                    float most = TurningHead * delta;

                    if (MathF.Abs(left) <= most)
                    {
                        walker.Yaw = toward;
                        turningTo = null;
                    }
                    else
                    {
                        walker.Yaw = Game.Navigation.Walker.Wrapped(walker.Yaw + (MathF.Sign(left) * most));
                    }
                }

                if (driving)
                {
                    Game.Navigation.FirstPersonStep went = walker.Advance(asked, delta);

                    walking = went.Travelled > 0f || went.Blocked;
                    shouldered = went.Blocked;

                    // What they were asking for, not what they got: pressed into the edge of the ground, the step slides along it and the way out is.
                    Vector3 meant = walker.Direction(asked.Move);

                    if (meant.LengthSquared() > 1e-6f)
                    {
                        travelling = Vector3.Normalize(meant);
                    }

                    // Only once they have actually moved or turned.
                    if (went.Travelled > 0f || asked.Look != Vector2.Zero)
                    {
                        update.Step(story.Ego, walker.Position, walker.Yaw);
                    }

                    // Pressing into something that will not give, and getting nowhere at all: an ego nothing ever placed is at the world origin.
                    stuck = went.Blocked && went.Travelled <= 0f ? stuck + delta : 0f;

                    if (stuck > StrandedFor && scene.Walkable is { } fenced && !fenced.IsWalkable(walker.Position) &&
                        fenced.NearestWalkable(walker.Position) is { } patch)
                    {
                        stuck = 0f;
                        walker.Position = patch with
                        {
                            Y = scene.Ground?.Height(patch) ?? walker.Position.Y,
                        };

                        update.Step(story.Ego, walker.Position, walker.Yaw);

                        Log.Info(string.Create( CultureInfo.InvariantCulture, $"{story.Ego}: nothing had put them anywhere they could move " +
                            $"from, so they are standing at " + $"{walker.Position.X:F0}, {walker.Position.Z:F0}"));
                    }

                    // Nothing is animating these legs, so the feet are counted rather than heard from a walk cycle's own landings.
                    if (walker.Footfall())
                    {
                        update.Footstep(story.Ego);
                    }
                }
                else if (free)
                {
                    walker.Turn(asked.Look);
                }

                turned = update.Turned(story.Ego) ?? walker.Yaw;

                (Vector3 eye, float yaw, float pitch) = walker.Shot(Game.Navigation.FirstPerson.Eyes, delta);

                camera.Position = eye;
                camera.Aim = new Vector2(yaw * 180f / MathF.PI, pitch * 180f / MathF.PI);
            }
            else if (theirs && !OnFoot())
            {
                camera.Update(window, delta);
            }

            Camera view = camera.ToCamera(template);

            // Nobody sees the inside of their own head.
            bool behindTheEyes = OnFoot() && (onFoot || walker.Returning || (update.Gliding && update.EyesOf(story.Ego) is { } head &&
                  Vector3.DistanceSquared(view.Position, head) < InsideTheHead * InsideTheHead));

            if (behindTheEyes != embodied)
            {
                embodied = behindTheEyes;

                if (update.ModelNamed(story.Ego) is { } body)
                {
                    update.Show(body, !behindTheEyes);
                }
            }

            // Where the view actually is, while the player is the one holding it.
            update.Elsewhere = onFoot ? view : null;

            // What GK3's billboard flag has always meant, done here because here is where the frame's camera is finally known — the free camera and.
            geometry.TurnBillboards(view.Position);

            // Where the player's ears are.
            room?.Listen( view.Position, Vector3.Normalize(view.Target - view.Position), view.Up);

            // Walking into the way out takes it, which is what a way out means to somebody who is walking rather than clicking.
            if (onFoot && walking && !update.Busy(story.Ego) && stopwatch.Elapsed.TotalSeconds > ExitNotAtOnce &&
                string.Equals(story.Location, here, StringComparison.OrdinalIgnoreCase) && interaction.WayOut(
                    new Rendering.Ray(view.Position, travelling), shouldered) is { } leaving && leaving.Noun is { Length: > 0 } way &&
                (!string.Equals(way, tried, StringComparison.OrdinalIgnoreCase) || stopwatch.Elapsed.TotalSeconds - triedAt > ExitAgainAfter))
            {
                tried = way;
                triedAt = stopwatch.Elapsed.TotalSeconds;

                // Walked into rather than clicked from across the room, so the approach walk in front of it is skipped: the player is standing in
                // the doorway already, and the few units it had left to cover were covered at the actors' pace after they crossed the room at.
                if (interaction.Do(leaving, approach: false) is { } took)
                {
                    Log.Info($"{story.Ego}: walked into {took.Noun}:{took.Verb}");
                }
            }

            // What the pointer is over.
            bool crosshair = onFoot && window.PointerLocked;

            Vector2 aimed = pinned ?? (crosshair ? new Vector2(window.FramebufferWidth / 2f, window.FramebufferHeight / 2f) : new Vector2(
                    window.PointerPosition.X * window.DpiScale, window.PointerPosition.Y * window.DpiScale));

            Hover hover = interaction.At( view, (int)aimed.X, (int)aimed.Y, window.FramebufferWidth, window.FramebufferHeight);

            // And the room's own machinery is told, where the room has any.
            api.Mechanism?.Pointing(hover.Pick, update.Occupied || menu is not null);

            // And whether it wants the click outright, which is a different question from TakesClick below: that one is asked once a click has.
            string? claimed = api.Mechanism?.ClaimsClick(hover.Pick);

            // What the player sees, not the noun behind it: the numbered exits are drawn as the place they lead to, and a log that says EXIT3 cannot.
            if (hover.Label != hovering)
            {
                hovering = hover.Label;

                if (hovering is { Length: > 0 })
                {
                    Log.Info(hover.Actionable ? $"> {hovering} — click to {hover.Default}" : $"> {hovering} — nothing to do with it here");
                }
            }

            // --pointer puts it somewhere fixed, which is the only way to photograph the interface: the label follows the mouse, and a headless run.
            Vector2 pointer = aimed;

            // Whether the verb bar was up when this frame began, and whether anything was taken off it.
            bool barWasShowing = menu is not null;
            bool barTookAVerb = false;

            // --menu opens it without a right-click, for the same reason --pointer exists.
            if (forceMenu && menu is null && hover.Actionable)
            {
                menu = hover;
                menuAt = pointer;
                menuIndex = 0;
            }

            // The poem keeps its page only while it is open; closed, it opens next time at the verse in hand, as the retail engine has it.
            if (story.Screens.Top is not { Kind: ScreenKind.InventoryInspect } reading || !Game.SerpentRouge.IsReader(reading.Subject))
            {
                Game.SerpentRouge.Close(story);
            }

            // What Sidney has to do outside its own screen: a line Grace says over it, a room the story leaves for, Sidney put away — one at a time.
            if (sidney is { HasCues: true } && !update.Acting && sidney.TakeCue() is { } cue)
            {
                switch (cue.Kind)
                {
                    case Game.Sidney.SerpentRougeCue.Say:
                        new ActionRunner(api).Run(new Formats.Actions.NvcAction
                        {
                            Noun = "SIDNEY", Verb = "SAYS", Case = "ALL", Script = string.Create( CultureInfo.InvariantCulture,
                                $"wait StartDialogue(\"{cue.Plate}\", {cue.Lines})"), Source = "Sidney",
                        });

                        Log.Info($"Sidney: {cue.Plate}");
                        break;

                    case Game.Sidney.SerpentRougeCue.Leave:
                        // The end of the second afternoon: Gemini done, and Grace goes out to the hallway, where the rules end the timeblock.
                        story.Screens.Hide(ScreenKind.Sidney);
                        story.Location = cue.Plate;

                        Log.Info($"Sidney: leaving for {cue.Plate}");
                        break;

                    case Game.Sidney.SerpentRougeCue.Close:
                        story.Screens.Hide(ScreenKind.Sidney);

                        Log.Info("Sidney: put away");
                        break;

                    default:
                        break;
                }
            }

            // Putting Sidney away runs the room's own ExitSidney, as the original does whenever Sidney is closed: it stands Grace up from the desk.
            bool sidneyUp = story.Screens.IsOpen(ScreenKind.Sidney);

            if (sidneyWasUp && !sidneyUp && !update.Acting && string.Equals(here, "R25", StringComparison.OrdinalIgnoreCase))
            {
                new ActionRunner(api).Run(new Formats.Actions.NvcAction
                {
                    Noun = "SIDNEY", Verb = "EXIT", Case = "ALL", Script = "wait CallSheep(\"R25_ALL\", \"ExitSidney\")", Source = "Sidney",
                });

                Log.Info("Sidney: ExitSidney");
            }

            sidneyWasUp = sidneyUp;

            // Whose glass was that?
            if (glassAsked is { } pending)
            {
                if (!pending.Opened)
                {
                    if (menu is null && !update.Acting)
                    {
                        menu = interaction.Ask(pending.Question, interaction.NameOf(pending.Glass));
                        menuAt = pointer;
                        menuIndex = 0;
                        radioOpen = false;

                        glassAsked = pending with
                        {
                            Opened = true, Wilkes = story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidWilkes),
                            Buchelli = story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidBuchelli),
                        };
                    }
                }
                else if (menu is null)
                {
                    string? answer = story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidWilkes) > pending.Wilkes
                            ? Game.DirtyGlasses.SaidWilkes : story.GetTopicCount(pending.Question, Game.DirtyGlasses.SaidBuchelli) > pending.Buchelli
                            ? Game.DirtyGlasses.SaidBuchelli : null;

                    if (answer is not null)
                    {
                        Dusted(Game.DirtyGlasses.Answer(pending.Glass, answer, story), pending.Glass, story, api);
                    }
                    else
                    {
                        Log.Info($"fingerprints: {pending.Glass} put away unanswered");
                    }

                    glassAsked = null;
                }
            }

            if (!console.Open && window.WasClicked(Platform.PointerButton.Secondary))
            {
                // Not while something is already happening.
                bool busy = update.Acting || room?.Talking == true;

                // The menu belongs to the thing it was opened over, not to wherever the pointer wanders next, so what was under it is kept — and so.
                menu = menu is null && hover.Actionable && !busy ? hover : null;
                menuAt = pointer;
                menuIndex = 0;

                // One list at a time.
                radioOpen = false;

                // Asking and getting nothing has to look different from asking and being ignored, or a room where nothing answers is.
                if (menu is null)
                {
                    Log.Info(busy ? "not now — something is already being said or done" : hover.Noun is { Length: > 0 } asked
                            ? $"{asked} answers to nothing here and now" : "nothing under the pointer");
                }
            }

            // What Gabriel can raise with Grace, here and now.
            List<Game.RadioTopic> topics = [];

            // Not while an action is playing and not while a line is playing, which is the reference's rule for this button —.
            if (Game.Radio.WornAt(story.Timeblock) && !update.Acting && room?.Talking != true && scene.Actions is { } radioActions)
            {
                // The room's own general call first, where the room has one.
                if (api.Declares?.Invoke(here, Game.Radio.Call) == true)
                {
                    topics.Add(new Game.RadioTopic( string.Empty, (hud?.Text ?? UiText.English).Say("radio.ask", "Ask Grace")));
                }

                topics.AddRange( Game.Radio.Topics(radioActions, story.Ego, interaction.NameOf));
            }

            if (radioIndex >= topics.Count)
            {
                radioIndex = 0;
            }

            if (Game.Radio.WornAt(story.Timeblock) && topics.Count > 0 && topics.Count != radioSaid)
            {
                radioSaid = topics.Count;

                Log.Info(topics.Count > 0 ? $"Radio: {topics.Count} thing(s) to ask Grace — " + string.Join(", ", topics.Select(t => t.Label))
                    : "Radio: nothing to ask Grace about");
            }

            if (radioOpen)
            {
                if (topics.Count == 0)
                {
                    // A topic can stop being available while its list is open — a script running under it is enough — and a list of nothing cannot.
                    radioOpen = false;
                }
                else if (hud?.TopicAt(pointer) is int over and >= 0)
                {
                    radioIndex = over;
                }
                else if (window.ScrollDelta != 0 && hud?.TopicCount > 0)
                {
                    int count = hud.TopicCount;

                    radioIndex = (((radioIndex - window.ScrollDelta) % count) + count) % count;
                }
            }

            if (menu is not null)
            {
                // One selection, three ways to move it.
                if (window.ScrollDelta != 0 && hud?.RowCount > 0)
                {
                    int count = hud.RowCount;

                    menuIndex = (((menuIndex - window.ScrollDelta) % count) + count) % count;
                }
                else if (hud?.RowAt(pointer) is int row and >= 0)
                {
                    menuIndex = row;
                }
            }

            // Whether the click below was swallowed by somebody talking rather than by the room being busy.
            bool cutALine = false;

            // A screen in front of the room takes the frame: it draws instead of the room's interface and it takes the click.
            if (story.Screens.Top?.Kind != ScreenKind.Binoculars)
            {
                throughEyes = false;
            }

            if (screens is not null && story.Screens.Top is { } panel)
            {
                Panorama seen = binoculars.For(scene.Name, story.Timeblock.ToString());

                // The hose.
                if (panel.Kind == ScreenKind.Water)
                {
                    aiming ??= new Game.WaterAiming();

                    float span = MathF.Min(window.FramebufferWidth, window.FramebufferHeight) * 0.72f;

                    aiming.PointAt(new Vector2( (pointer.X - ((window.FramebufferWidth - span) / 2f)) / span,
                        (pointer.Y - ((window.FramebufferHeight - span) / 2f)) / span));

                    if (aiming.Advance((float)delta))
                    {
                        // What the original's own rule does with it: the case that reads ten seconds on the nest is now true, and its script takes.
                        Log.Info("water: the nest comes down");

                        if (scene.Actions?.Find("WATER_INTERFACE", "AIM", story.Ego) is { } soaked)
                        {
                            new ActionRunner(api).Run(soaked);
                        }

                        story.Screens.Back();
                    }
                }
                else
                {
                    aiming = null;
                }

                // The kit.
                if (panel.Kind == ScreenKind.Fingerprint)
                {
                    if (dusting is null || !string.Equals( dusting.Noun, panel.Subject, StringComparison.OrdinalIgnoreCase))
                    {
                        dusting = new Game.FingerprintDusting(panel.Subject ?? string.Empty, story);
                        brushWas = pointer;

                        Log.Info( $"fingerprints: dusting {dusting.Noun}, " + $"{dusting.Prints.Count} print(s) on it");
                    }

                    float moved = Vector2.Distance(pointer, brushWas);
                    brushWas = pointer;

                    if (!update.Acting && glassAsked is null && window.IsHeld(Platform.PointerButton.Primary) &&
                        screens.HitAt(pointer) is { Length: > 0 } under &&
                        (under == "fp:panel" || under.StartsWith("fp:print:", StringComparison.Ordinal)))
                    {
                        int on = under.StartsWith("fp:print:", StringComparison.Ordinal) &&
                            int.TryParse(under[9..], CultureInfo.InvariantCulture, out int which) ? which : -1;

                        glassAsked = Kitted(dusting.Brushed(on, moved, (float)delta), dusting, story, api) ?? glassAsked;

                        if (dusting.Sweeping)
                        {
                            room?.Play(dusting.SweepSound);
                        }
                    }
                }
                else
                {
                    dusting = null;
                }

                // The map's traffic, built once per opening and moved on every frame.
                if (panel.Kind == ScreenKind.Driving)
                {
                    string opened = panel.ToString();

                    // The map is a place, and opening it asks whether the point in the story is over the way walking through a door does.
                    if (panel.Subject is null && (traffic is null || trafficFor != opened) && !story.ChangingTimeblock &&
                        Game.Story.TimeblockRules.Check(story) is not null)
                    {
                        Log.Info($"Timeblock: {story.Timeblock} is over on the map");
                        update.Cancel();

                        return new RoomExit(0, here);
                    }

                    if (traffic is null || trafficFor != opened)
                    {
                        traffic = Game.DrivingTraffic.For( story, map, panel.Subject?.Split(':') is ["follow", string chased] &&
                            int.TryParse(chased, NumberStyles.Integer, CultureInfo.InvariantCulture, out int which) ? which : 0,
                            panel.Subject?.Split(':') is ["ride", string going] ? going : null);

                        trafficFor = opened;

                        if (traffic.Destination is { } riding)
                        {
                            Log.Info($"Riding from {traffic.From} to {riding}");
                        }

                        if (traffic.Chase is { } quarry)
                        {
                            Log.Info( $"Following {quarry.Noun} out of {traffic.From} " + $"to {quarry.Arrives ?? "where they started"}");
                        }
                    }

                    traffic.Advance(delta);

                    // And the chase ends where the quarry stops.
                    if (traffic is { Following: true, Arrived: true } chase)
                    {
                        if (!chase.Said)
                        {
                            chase.Said = true;

                            if (chase.Chase?.Says is { Length: > 0 } verdict)
                            {
                                new ActionRunner(api).Run(new Formats.Actions.NvcAction
                                {
                                    Noun = chase.Chase.Noun, Verb = DrivingMap.Follow, Case = "ARRIVED", Script = string.Create(
                                        CultureInfo.InvariantCulture, $"wait StartDialogue(\"{verdict}\", 1)"), Source = "the chase",
                                });

                                Log.Info($"Followed {chase.Chase.Noun}: {verdict}");
                            }
                        }

                        if (!update.Acting)
                        {
                            traffic = null;
                            trafficFor = null;

                            if (Arrive(chase, story) is { } rideOn)
                            {
                                story.Screens.CloseAll();
                                story.RideTo(rideOn);
                            }
                            else
                            {
                                // The map stays up for the player to choose from, as a plain map with whoever is still circling on it.
                                var plain = new Screen(ScreenKind.Driving);

                                story.Screens.Replace(plain);

                                traffic = Game.DrivingTraffic.For(story, map);
                                trafficFor = plain.ToString();
                            }
                        }
                    }
                    else if (traffic is { Riding: true, Arrived: true, Destination: { } there })
                    {
                        traffic = null;
                        trafficFor = null;

                        story.Screens.CloseAll();
                        story.RideTo(there);
                    }
                }
                else if (traffic is not null)
                {
                    // A chase the player walked out of, which they are allowed to do: the map has the same way out every screen has.
                    if (traffic is { Following: true, Arrived: false, Chase: { } gaveUp })
                    {
                        story.SetNounVerbCount(gaveUp.Counted, DrivingMap.Follow, 0);

                        Log.Info($"Gave up following {gaveUp.Noun}");
                    }
                    else if (traffic is { Following: true, Arrived: true } caught)
                    {
                        // Closed while Gabriel was still saying what he made of it.
                        if (Arrive(caught, story) is { } rideOn)
                        {
                            story.RideTo(rideOn);
                        }
                    }

                    traffic = null;
                    trafficFor = null;
                }

                if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && screens.HitAt(pointer) is { Length: > 0 } chose)
                {
                    // Leaning in is a camera and, often, another room, so it is handled here where both are in reach rather than in OnScreen.
                    if (dusting is not null && panel.Kind == ScreenKind.Fingerprint && chose.StartsWith("fp:", StringComparison.Ordinal))
                    {
                        switch (chose)
                        {
                            case "fp:exit":
                                story.Screens.Back();
                                break;

                            case "fp:brush":
                                dusting.TouchBrush();
                                room?.Play(Game.DustingSounds.TakeBrush);
                                break;

                            case "fp:dust":
                                dusting.TouchDust();
                                break;

                            case "fp:tape":
                                if (dusting.Holding == Game.InHand.Nothing)
                                {
                                    room?.Play(Game.DustingSounds.TakeTape);
                                }

                                dusting.TouchTape();
                                break;

                            case "fp:cloth":
                                if (dusting.Holding == Game.InHand.TapeWithPrint)
                                {
                                    room?.Play(Game.DustingSounds.Keep);
                                }

                                glassAsked = Kitted(dusting.TouchCloth(api.Scores), dusting, story, api) ?? glassAsked;
                                break;

                            case "fp:base":
                                // Anywhere in the box that is not a piece of it puts a held brush back, which is what the original does with a click.
                                if (dusting.Holding is Game.InHand.Brush or Game.InHand.DustedBrush)
                                {
                                    dusting.PutDown();
                                }

                                break;

                            case "fp:panel":
                                // Except on the thing itself, where a dusted brush is being worked rather than put down — the press that starts a.
                                if (dusting.Holding == Game.InHand.Brush)
                                {
                                    dusting.PutDown();
                                }

                                break;

                            default:
                                if (chose.StartsWith("fp:print:", StringComparison.Ordinal) && int.TryParse(
                                        chose[9..], CultureInfo.InvariantCulture, out int pressed))
                                {
                                    Game.InHand was = dusting.Holding;
                                    dusting.PressOn(pressed);

                                    if (was != dusting.Holding && dusting.Holding == Game.InHand.TapeWithPrint)
                                    {
                                        room?.Play(Game.DustingSounds.Press);
                                    }
                                }

                                break;
                        }
                    }
                    else
                    // Asking for help.
                    if (chose.StartsWith("hint:", StringComparison.Ordinal))
                    {
                        string wanted = chose[5..];

                        if (journal.Find(wanted) is { } asking)
                        {
                            string? given = journal.Reveal(asking);

                            Log.Info(given is { Length: > 0 }
                                ? $"journal: {asking.Title} — {given}" : $"journal: no more hints for {asking.Title}");
                        }
                    }
                    else if (chose.StartsWith("sidney:shape:", StringComparison.Ordinal) && sidney is not null &&
                        Enum.TryParse(chose[13..], ignoreCase: true, out Game.Sidney.MapShape picked))
                    {
                        console.Print(sidney.LayShape(picked).Text);
                    }
                    else if (chose == "sidney:mark" && sidney is not null && screens.MapBounds is { Z: > 0 } drawn)
                    {
                        // Back into the map's own 1,368 pixels, so a mark means the same place whatever size the window is and however far it is.
                        console.Print(sidney.Mark(screens.MapAt(pointer)).Text);
                    }
                    // Giving chase from the map itself, which the original could not do: there, the only way to follow anybody was to catch them.
                    else if (chose.StartsWith("drive:", StringComparison.Ordinal) && panel.Kind == ScreenKind.Driving &&
                             DrivingMap.Refused(story, chose[6..]) is { } excuse)
                    {
                        new ActionRunner(api).Run(new Formats.Actions.NvcAction
                        {
                            Noun = chose[6..], Verb = "DRIVE", Case = "REFUSED", Script = string.Create( CultureInfo.InvariantCulture,
                                $"wait StartDialogue(\"{excuse}\", 1)"), Source = "the map",
                        });

                        Log.Info($"The map will not go to {chose[6..]}: {excuse}");
                    }
                    else if (chose.StartsWith("follow:", StringComparison.Ordinal) && panel.Kind == ScreenKind.Driving)
                    {
                        if (chose == "follow:skip")
                        {
                            traffic?.Skip();
                        }
                        else
                        {
                            story.Screens.Replace(new Screen(ScreenKind.Driving, chose));
                        }
                    }
                    else if (chose.StartsWith("zoom:", StringComparison.Ordinal) &&
                        seen.Sights.FirstOrDefault(s => s.Location == chose[5..]) is { } sight)
                    {
                        Log.Info($"Binoculars: {sight.Location}");

                        if (!string.Equals(sight.Scene, scene.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // Another room, looked at rather than walked to.
                            api.Leaning = new Game.BinocularView( scene.Name, sight, update.Where(story.Ego) ?? Vector3.Zero,
                                update.Facing(story.Ego) ?? 0, camera.Position, camera.Aim);

                            api.Wanted = sight.Scene;
                            api.WantedCamera = (sight.Position, sight.Angle);

                            story.Screens.Replace(new Screen( ScreenKind.Binoculars, $"{Screen.Zoomed}:{sight.Scene}"));
                        }
                        else
                        {
                            story.Screens.Back();

                            camera.Position = sight.Position;
                            camera.Aim = sight.Angle;
                        }
                    }

                    // And lowering them again, which is the room the player never left.
                    else if (chose == "binocs:back")
                    {
                        if (api.Leaning is { } lowering)
                        {
                            return Lower(lowering);
                        }

                        story.Screens.Back();
                    }
                    else if (chose.StartsWith("verb:", StringComparison.Ordinal) && panel.Subject is { Length: > 0 } about &&
                             scene.Actions?.Find(about, chose[5..], story.Ego) is { } onItem)
                    {
                        // The item's own action, run where it was written to run: with the inventory still on top, because that is what its case.
                        ActionOutcome ran = new ActionRunner(api).Run(onItem);

                        Log.Info( $"{about}:{chose[5..]} [{onItem.Case}] - " +
                            $"{(ran.Ran ? "ran" : "refused")} {ran.Statements.Count} statement(s)");
                    }
                    else if (chose.StartsWith("item:", StringComparison.Ordinal) && chose[5..] is { Length: > 0 } inHand &&
                             panel.Kind == ScreenKind.Inventory && !inHand.StartsWith("SIDNEY", StringComparison.OrdinalIgnoreCase))
                    {
                        story.Inventory.SetActive(story.Ego, inHand);

                        IReadOnlyList<string> offered = ItemVerbs( new Screen(ScreenKind.Inventory, inHand), scene, story) ?? [];

                        // One thing to do, so it is done.
                        Formats.Actions.NvcAction? single = offered is [string only] ? scene.Actions?.Find(inHand, only, story.Ego) : null;

                        // The words hanging beside an item belong to the item that was clicked, so every click moves them: to the thing just clicked.
                        story.Screens.Replace(new Screen( ScreenKind.Inventory, single is null && offered.Count > 0 ? inHand : null));

                        if (single is { } act)
                        {
                            ActionOutcome ran = new ActionRunner(api).Run(act);

                            Log.Info( $"{inHand}:{act.Verb} [{act.Case}] - " +
                                $"{(ran.Ran ? "ran" : "refused")} {ran.Statements.Count} statement(s)");
                        }
                    }
                    else
                    {
                        OnScreen( chose, story, sidney, update, console, sidney is null ? null
                                : item => ScanIntoSidney(item, api, scene, sidney, console));
                    }
                }

                // Dragging is a press, not a click.
                bool holding = !console.Open && window.IsHeld(Platform.PointerButton.Primary);

                if (holding && !heldLastFrame && sidney is { Dragging: < 0 } && panel.Kind == ScreenKind.Sidney &&
                    screens.HitAt(pointer) is { } under2 && under2.StartsWith("sidney:point:", StringComparison.Ordinal) &&
                    under2[13..].Split(':') is [string owner, string index] && int.TryParse(owner, out int belongs) &&
                    int.TryParse(index, out int lifted))
                {
                    sidney.StartDrag(belongs, lifted);
                }

                // A drag on the map itself slides it.
                if (holding && sidney is { Dragging: < 0 } sliding && panel.Kind == ScreenKind.Sidney && sliding.Zoom > 1f &&
                    screens.MapBounds is { Z: > 0 } over && pointer.X >= over.X && pointer.X <= over.X + over.Z &&
                    pointer.Y >= over.Y && pointer.Y <= over.Y + over.W)
                {
                    if (heldLastFrame)
                    {
                        float across = Game.Sidney.SidneyMap.Extent / sliding.Zoom / over.Z;

                        sliding.PanBy(new System.Numerics.Vector2( (panFrom.X - pointer.X) * across, (panFrom.Y - pointer.Y) * across));
                    }

                    panFrom = pointer;
                }

                heldLastFrame = holding;

                // And it follows the pointer until the button comes back up, which is the whole of dragging one: the map is drawn from the marks.
                if (sidney is { Dragging: >= 0 } && panel.Kind == ScreenKind.Sidney)
                {
                    if (holding && screens.MapBounds is { Z: > 0 })
                    {
                        sidney.DragTo(screens.MapAt(pointer));
                    }
                    else if (sidney.EndDrag() is { } settled)
                    {
                        console.Print(settled.Text);
                    }
                }

                // The wheel, before anything else looks at the pointer: a list inside Sidney is what it means while one is under it.
                if (panel.Kind == ScreenKind.Sidney && window.ScrollDelta != 0)
                {
                    screens.SidneyWheel(pointer, window.ScrollDelta);
                }

                // Sidney's two text boxes are the only places in the game the player types into that are not the console, so the keys go there while.
                if (!console.Open && sidney is { } typing2 && panel.Kind == ScreenKind.Sidney &&
                    (typing2.Screen == Game.Sidney.SidneyScreen.Search || typing2.Appending))
                {
                    if (window.Typed is { Length: > 0 } letters)
                    {
                        typing2.Typed += letters;
                    }

                    if (window.WasPressed(Platform.EditKey.Backspace) && typing2.Typed.Length > 0)
                    {
                        typing2.Typed = typing2.Typed[..^1];
                    }

                    if (window.WasPressed(Platform.EditKey.Enter))
                    {
                        if (typing2.Appending)
                        {
                            console.Print(typing2.Append().Text);
                        }
                        else
                        {
                            typing2.Look();
                        }
                    }
                }

                if (!console.Open && window.WasPressed(Platform.CameraAction.Quit))
                {
                    if (api.Leaning is { } backing)
                    {
                        return Lower(backing);
                    }

                    story.Screens.Back();
                }

                // The binoculars are the one screen the player still looks *through*, so the camera keeps taking their input while it is raised.
                if (panel.Kind == ScreenKind.Binoculars && !(panel.Subject?.StartsWith(Screen.Zoomed, StringComparison.Ordinal) ?? false))
                {
                    // Raised to the player's own eyes, once: just in front of the face, at eye height, looking the way they stand.
                    if (!throughEyes)
                    {
                        throughEyes = true;

                        if (update.EyesOf(story.Ego) is { } eyes)
                        {
                            // --aim outranks the way they stand, so a run can be pointed at a sight without dragging a pointer it has not got.
                            float facing = looking is { } asked ? asked.X * MathF.PI / 180f
                                : update.SettledFacing(story.Ego) ?? (camera.Aim.X * MathF.PI / 180f);
                            Vector3 ahead = new(MathF.Sin(facing), 0f, MathF.Cos(facing));
                            Vector3 at = eyes + (ahead * 10f);

                            if (Vector3.Distance(camera.Position, at) > 4f)
                            {
                                camera.Position = at;
                                camera.Aim = new Vector2(facing * 180f / MathF.PI, looking?.Y ?? 0f);
                            }
                        }
                    }

                    if (!console.Open && !typing)
                    {
                        camera.Update(window, (float)delta);
                    }
                }

                screens.Build( new ScreenView( panel, story.Inventory.ItemsOf(story.Ego), story.Inventory.ActiveItemOf(story.Ego), sidney,
                        Reachable(panel, scene, story), panel.Subject, map, DrivingMap.Open(story, scene.Name), renderer.OverlayPicture, seen,
                        camera.Aim, ItemVerbs(panel, scene, story), panel.Kind == ScreenKind.Journal ? journal.Read() : null, dusting, icons,
                        closeUps, verbIcons, artwork, aiming, traffic, front.Settings.Captions ? room?.Caption : null,
                        front.Settings.Captions ? room?.Speaker : null,
                        panel.Kind == ScreenKind.InventoryInspect && Game.SerpentRouge.IsReader(panel.Subject)
                            ? Game.SerpentRouge.Show(story, Game.SerpentRouge.Page(story)) : null), window.FramebufferWidth, window.FramebufferHeight,
                    pointer);

                renderer.SetOverlay(screens.Overlay);

                // A screen in front of the room has no nouns, so the pointer is the arrow.
                window.PointerShape = Platform.PointerShape.Default;

                window.EndFrame();

                // The binoculars are looked through, so the room behind them goes on: the birds keep flying and the fires keep burning.
                if (panel.Kind == ScreenKind.Binoculars)
                {
                    BlendAir(view, delta);
                }

                renderer.SetScene(geometry, view);

                // Here as well as below: a player who opens the inventory on the frame they arrive takes this branch instead, and a fade nobody.
                fade.Advance();

                // Counted like any other frame, so a run with a frame limit still ends and its screenshot is of the screen rather than of the room.
                if (renderer.DrawFrame(0f, 0f, 0f))
                {
                    presented++;
                }

                continue;
            }

            // The headset, which is a list rather than a screen and so is answered before the two that open one.
            if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && hud?.ButtonAt(pointer) == GameHud.RadioButton)
            {
                radioOpen = !radioOpen && topics.Count > 0;
                radioIndex = 0;
                menu = null;

                if (!radioOpen && topics.Count == 0)
                {
                    // The button is drawn dim in this case, so this is a player checking rather than a player being ignored.
                    Log.Info("Radio: nothing to ask Grace about here");
                }
            }

            // A topic, which performs the room's own RADIO rule for that noun — the same rule, with the same conditions, that picking RADIO off the.
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && radioOpen &&
                     hud?.TopicAt(pointer) is int picked and >= 0 && picked < topics.Count)
            {
                Game.RadioTopic topic = topics[picked];
                radioOpen = false;

                if (topic.IsGeneral)
                {
                    Log.Info($"Radio: calling Grace from {here}");
                    Sheep.SheepExpression.Evaluate( $"CallSheep(\"{here}\", \"{Game.Radio.Call}\")", api);
                }
                else if (interaction.Do(topic.Noun, Game.Radio.Verb) is { } asked)
                {
                    Log.Info($"Radio: {asked.Noun}:{asked.Verb} [{asked.Case}]");
                }
            }

            // A click anywhere else while the list is up puts it away without doing anything, which is what every menu does — and it must not also.
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && radioOpen)
            {
                radioOpen = false;
            }

            // The room's own button, which is a move the mechanism has asked for outright because pointing at the room will not find it.
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && hud?.ButtonAt(pointer) == GameHud.PromptButton)
            {
                api.Mechanism?.Press();
                menu = null;
            }

            // The top bar's two buttons, which are the only way in that a player who has not read a key list will find.
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && hud?.ButtonAt(pointer) is { Length: > 0 } opening &&
                story.Screens.InventoryReachable)
            {
                ScreenKind wanted = opening == "open:journal" ? ScreenKind.Journal : ScreenKind.Inventory;

                if (story.Screens.IsOnTop(wanted))
                {
                    story.Screens.Back();
                }
                else
                {
                    story.Screens.Show(new Screen(wanted));
                }

                menu = null;
            }

            // The strip along the foot of the screen is the inventory, so a click on it is a click on what the player is carrying rather than on the.
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && hud?.ItemAt(pointer) is { Length: > 0 } clicked)
            {
                if (string.Equals( story.Inventory.ActiveItemOf(story.Ego), clicked, StringComparison.OrdinalIgnoreCase))
                {
                    story.Screens.Show(new Screen(ScreenKind.InventoryInspect, clicked));
                    Log.Info($"inventory: looking at {clicked}");
                }
                else
                {
                    story.Inventory.SetActive(story.Ego, clicked);
                    Log.Info($"inventory: holding {clicked}");
                }

                menu = null;
            }
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary) && menu is null && hud?.OverInterface(pointer) != true &&
                     // Assigned in the condition because Skip() silences the line it reports, so it must be called here and exactly once — and which.
                     ((cutALine = room?.Skip() == true) || update.Occupied))
            {
                // Somebody is speaking, so the click reads the line rather than the room: it cuts the recording short and the next one starts.

                // A click that went nowhere because the room said it was busy, with nobody speaking.
                if (cutALine)
                {
                    refused = 0;
                }
                else if (++refused >= 3)
                {
                    refused = 0;

                    IReadOnlyList<string> let = update.Unstick();

                    Log.Info(let.Count == 0 ? "Unstuck by three clicks: nothing was holding the room."
                        : "Unstuck by three clicks: let go of " + string.Join(", ", let) + ".");
                }
            }
            // Leaning in, on a button of its own.
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Middle) && menu is null && !update.Acting &&
                     hud?.OverInterface(pointer) != true)
            {
                if (interaction.Do(hover, hover.Closer) is { } looked)
                {
                    Log.Info($"Did: {looked.Noun}:{looked.Verb}");
                }
            }
            else if (!console.Open && window.WasClicked(Platform.PointerButton.Primary))
            {
                // The room took this one, so it was never stuck.
                refused = 0;

                // A click inside the open menu takes whatever is selected; a click anywhere else dismisses it without doing anything, which is what.
                bool inside = menu is not null && hud?.RowAt(pointer) >= 0;

                // A double-click means the same thing as a click, more urgently: whatever walking the action puts in front of itself is run rather.
                bool hurry = window.WasDoubleClicked(Platform.PointerButton.Primary);

                // What the selected row means, which is a verb, an item to use, or the row that only opens the bag.
                string? chosenRow = inside ? hud?.RowNamed(menuIndex) : null;
                bool openingBag = chosenRow == GameHud.UseRow;

                // Shift arrives at once.
                update.WarpNextWalk = window.IsHeld(Platform.CameraAction.Fast);

                barTookAVerb = menu is not null && chosenRow is { Length: > 0 } && !openingBag;

                // The room's own machinery gets first refusal, ahead of the action files.
                bool roomTook = menu is null && claimed is not null && hud?.OverInterface(pointer) != true &&
                    api.Mechanism?.TakesClick(hover.Pick) == true;

                ActionOutcome? did = roomTook ? null : menu is { } open ? chosenRow is { Length: > 0 } && !openingBag
                            ? interaction.Do(open, chosenRow, hurry) : null : interaction.Do(hover, hurry: hurry);

                if (roomTook)
                {
                    Log.Info($"{story.Ego}: the room claimed the click" + (claimed is { Length: > 0 } what ? $" — {what}" : string.Empty));
                }

                // Nothing to do to the thing clicked, so ask whether it was the ground and go there.
                if (roomTook)
                {
                    // Already dealt with above, before the action files were asked.
                }
                else if (did is null && menu is null && window.WasClicked(Platform.PointerButton.Primary) && hud?.OverInterface(pointer) != true &&
                    api.Mechanism?.TakesClick(hover.Pick) == true)
                {
                    Log.Info($"{story.Ego}: the room took the click");
                }
                else if (did is null && menu is null && !update.Performing(story.Ego) && hud?.OverInterface(pointer) != true &&
                    interaction.FloorTarget(hover) is { } ground)
                {
                    // Except where the room takes its own floor clicks.
                    if (api.Mechanism?.TakesFloorClick() == true)
                    {
                        Log.Info($"{story.Ego}: the room took the click on the floor");
                    }
                    else
                    {
                        // A click across the room runs, a click at the player's feet does not.
                        double crossing = update.Walk( story.Ego, ground, hurry: hurry, mayRun: true);

                        Log.Info(crossing > 0 ? string.Create( CultureInfo.InvariantCulture,
                                $"{story.Ego}: walking to {ground.X:F0}, {ground.Z:F0}, {crossing:F1}s") : $"{story.Ego}: nowhere to walk from here");
                    }
                }


                // A menu the story has made modal stays up until something on it is chosen.
                if (menu is not null && !openingBag && !(story.MustChooseAnAction && did is null))
                {
                    menu = null;
                }

                if (did is { } outcome)
                {
                    Log.Info( $"{outcome.Noun}:{outcome.Verb} [{outcome.Case}] - " + (outcome.Deferred ? string.Create( CultureInfo.InvariantCulture,
                                $"walking {outcome.Approaching:F1}s first, then " + $"{outcome.Statements.Count} statement(s)")
                            : $"{(outcome.Ran ? "ran" : "refused")} " + $"{outcome.Statements.Count} statement(s)") +
                        (outcome.Seconds > 0 ? $", {outcome.Seconds:F1}s" : string.Empty));
                }
            }

            // A conversation ends when the bar it was being held through goes away.
            if (barWasShowing && menu is null && !barTookAVerb && api.State.Conversation is not null)
            {
                Log.Info($"conversation: {api.State.Conversation} ends with the verb bar");
                Sheep.SheepExpression.Evaluate( "CallSheep(\"GLB_ALL\", \"CodeCallEndConv\")", api);
            }

            // The device is the clock for dialogue: the next line of a voice-over starts when the last one's source stops, so they never overlap and.
            room?.Update(delta);

            if (room?.Caption is { Length: > 0 } caption && caption != spoken)
            {
                spoken = caption;
                Log.Info($"  {room.Speaker}: {caption}");
            }

            // Nothing of the room is drawn over a movie: not the caption of whatever was being said when it started, not the noun under the pointer.
            if (movies.Playing)
            {
                window.PointerShape = Platform.PointerShape.Default;

                if (pages is not null && front.Settings.MovieSubtitles && movies.Caption is { Length: > 0 })
                {
                    pages.Film( movies.Caption, movies.Speaker, null, 0f, window.FramebufferWidth, window.FramebufferHeight);

                    renderer.SetOverlay(pages.Overlay);
                }
                else
                {
                    renderer.SetOverlay(null);
                }
            }
            else if (hud is not null)
            {
                Hover showing = menu ?? hover;

                // What the room claims outranks what the action files offer, and it is the only thing on the bar while it does.
                bool advertised = menu is null && claimed is { Length: > 0 };

                // The pointer says what a click would do, before the bar has to be read: the arrow over nothing, a glass over what can be looked at.
                window.PointerShape = PointerChoice.For(hover, claimed, menu is not null, scene.Actions?.Verbs);

                hud.Build( new HudState( showing.Label, advertised ? [claimed!] : [.. showing.Actions
                                .Where(a => !IsAnItem(a.LocalizedVerb, scene.Actions?.Verbs)) .Select(a => a.LocalizedVerb)],
                        advertised ? claimed : hover.Default, pointer, menu is not null, menuIndex, menuAt,
                        front.Settings.Captions ? room?.Speaker : null, front.Settings.Captions ? room?.Caption : null,
                        story.Inventory.ItemsOf(story.Ego), story.Inventory.ActiveItemOf(story.Ego), InventoryOpen: true,
                        strings.Where(scene.Name, story.Timeblock.ToString()), console, strings.Score(story.Score, api.Scores.Maximum),
                        [.. showing.Actions
                            .Where(a => IsAnItem(a.LocalizedVerb, scene.Actions?.Verbs)) .Select(a => a.LocalizedVerb)],
                        window.IsHeld(Platform.CameraAction.ShowHotspots) ? OnScreen( interaction.Nouns(), view, window.FramebufferWidth,
                                window.FramebufferHeight) : null, icons, verbIcons, (api.Mechanism as Game.Mechanisms.CoordinateDevice)?.Reading(),
                        artwork, Game.Radio.WornAt(story.Timeblock), topics, radioOpen, radioIndex, api.Mechanism?.Offers, crosshair),
                    window.FramebufferWidth, window.FramebufferHeight);

                renderer.SetOverlay(hud.Overlay);
            }

            // A door is a script that says SetLocation and nothing more.
            if (api.Leaning is null && !string.Equals(story.Location, here, StringComparison.OrdinalIgnoreCase) &&
                story.Location is { Length: > 0 } elsewhere)
            {
                Log.Info($"Leaving {here} for {elsewhere}");

                // Where they stood and what they saw, in case the room they are going to is one the game never had.
                if (api.Returning is null && !Looking(api) && update.Where(story.Ego) is { } stood)
                {
                    api.Returning = new Game.ReturnSpot( here, elsewhere, stood, update.SettledFacing(story.Ego) ?? 0f, camera.Position, camera.Aim);
                }

                // Nothing this room was still holding back gets to happen in the next one.
                update.Cancel();

                return new RoomExit(0, elsewhere);
            }

            window.EndFrame();

            BlendAir(view, delta);

            renderer.SetScene(geometry, view);

            // The other half of the transition, one frame at a time.
            fade.Advance();

            if (renderer.DrawFrame(0f, 0f, 0f))
            {
                presented++;
            }

            // What Direct3D thought of that frame.
            if (presented == 1 && renderer is Rendering.Direct3D12.D3D12Renderer direct3d)
            {
                foreach (string message in direct3d.Messages)
                {
                    Log.Warning("d3d: " + message);
                }
            }

            // How much the picture changes from one frame to the next, over frames where the room itself is doing nothing.
            if (flicker && presented > 4)
            {
                if (renderer.Capture() is { } captured)
                {
                    byte[] frame = captured.Pixels;

                    if (previousFrame is { Length: > 0 } && previousFrame.Length == frame.Length)
                    {
                        long total = 0;

                        for (int i = 0; i < frame.Length; i++)
                        {
                            total += Math.Abs(frame[i] - previousFrame[i]);
                        }

                        flickerTotal += (double)total / frame.Length;
                        flickerFrames++;

                        // The last pair, as a picture.
                        var picture = new byte[frame.Length / 4];

                        for (int i = 0; i < picture.Length; i++)
                        {
                            int at = i * 4;
                            int most = Math.Max( Math.Abs(frame[at] - previousFrame[at]), Math.Max( Math.Abs(frame[at + 1] - previousFrame[at + 1]),
                                    Math.Abs(frame[at + 2] - previousFrame[at + 2])));

                            picture[i] = (byte)Math.Min(255, most * 12);
                        }

                        File.WriteAllBytes("flicker.raw", picture);
                    }

                    previousFrame = frame;
                }
            }
        }

        if (flickerFrames > 0)
        {
            Log.Info(string.Create( CultureInfo.InvariantCulture, $"Flicker: {flickerTotal / flickerFrames:F3} of an eight-bit step between " +
                $"frames, over {flickerFrames} frames"));
        }

        Log.Info(string.Create( CultureInfo.InvariantCulture, $"Presented {presented} frames in {stopwatch.Elapsed.TotalSeconds:F1}s "
            + $"({presented / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0} fps)"));

        return new RoomExit(0, null);
    }
}

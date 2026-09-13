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
/// <summary>The screens in front of the room: Sidney, the map, the kit.</summary>
public static partial class Application
{
    /// <summary>An interface picture out of the packs, expanded to something the overlay can draw.</summary>
    /// <returns>The picture, or null when no pack has it.</returns>
    /// <param name="packs">The ReBarn volumes, which may hold none.</param>
    /// <param name="localized">The chosen language's pack, or null when there is none.</param>
    /// <param name="name">The texture's name, as the enhanced set keys it.</param>
    private static Formats.Bitmaps.DecodedImage? Packed( Content.RebarnContent packs, Content.LocalizedContent? localized, string name)
    {
        ArgumentNullException.ThrowIfNull(packs);

        Formats.Bitmaps.CompressedImage? blocks = localized?.ReadTexture(Formats.Rebarn.RebarnKind.Texture, name)
            ?? packs.ReadTexture(Formats.Rebarn.RebarnKind.Texture, name);

        return blocks is { } found ? Formats.Bitmaps.BlockDecoder.Decode(found) : null;
    }

    /// <summary>Hands the driving map's own pictures to the interface.</summary>
    /// <param name="archives">The game's data, which is where the art is.</param>
    /// <param name="renderer">What holds the pictures.</param>
    /// <param name="screens">What draws them, and needs to know how big each one is.</param>
    /// <param name="enhanced">Asks for the upscaled form of a picture, from the loose set or the packs, and answers null where there is.</param>
    private static void LoadMapArt( GameArchives archives, Rendering.IRenderer renderer, ScreenPainter screens,
        Func<string, Formats.Bitmaps.DecodedImage?> enhanced)
    {
        int loaded = 0;
        int upscaled = 0;

        foreach (string key in new[] { DrivingMap.Background }
                     .Concat(DrivingMap.All.Select(s => s.Sprite.ToUpperInvariant())))
        {
            if (archives.Read(key + ".BMP") is not { } bytes)
            {
                continue;
            }

            try
            {
                Formats.Bitmaps.DecodedImage original = Formats.Bitmaps.BitmapDecoder.Decode(bytes, key);

                Formats.Bitmaps.DecodedImage? better = enhanced(key);

                if (better is not null)
                {
                    upscaled++;
                }

                if (renderer.AddOverlayPicture(key, better ?? original) > 0)
                {
                    screens.Sizes[key] = (original.Width, original.Height);
                    loaded++;
                }
            }
            catch (Formats.FormatParseException)
            {
                // A picture the archives do not have or cannot decode is a place that will not be on the map.
            }
        }

        if (loaded > 0)
        {
            Log.Info( $"Driving map: {loaded} of {DrivingMap.All.Count + 1} pictures" + (upscaled > 0 ? $", {upscaled} enhanced" : string.Empty));
        }
    }

    /// <summary>What a click on one of the screens in front of the room means.</summary>
    /// <param name="chose">The painter's identifier for what was clicked.</param>
    /// <param name="story">The game.</param>
    /// <param name="sidney">Grace's computer.</param>
    /// <param name="update">The room, for anything that has to happen in it.</param>
    /// <param name="console">Where a screen says what it did.</param>
    /// <param name="scan">How to put an item into Sidney, which needs the room's rules.</param>
    private static void OnScreen( string chose, GameState story, Game.Sidney.SidneyMachine? sidney, SceneUpdate update, GameConsole console,
        Action<string>? scan)
    {
        ArgumentNullException.ThrowIfNull(update);

        string[] parts = chose.Split(':');

        switch (parts[0])
        {
            case "close":
                story.Screens.Back();
                break;

            // A verse of Le Serpent Rouge: the close-up becomes the verse's, so that the verbs along the foot are the verse's own — READ, THINK, the.
            case "verse" when parts.Length > 1 && Game.SerpentRouge.VerseOf(parts[1]) is { } verse:
                story.Screens.Replace(new Screen(ScreenKind.InventoryInspect, verse.Noun));
                break;

            // Putting the hose down.
            case "water:away":
                story.Screens.Back();
                break;

            // Click to hold, click again to look at it closely — which is the whole of the inventory's interaction and the reason it does not need a.
            case "item" when parts.Length > 1 && parts[1].StartsWith("SIDNEY", StringComparison.OrdinalIgnoreCase):
                story.Screens.Show(new Screen(ScreenKind.Sidney));
                break;

            case "item" when parts.Length > 1:
                if (string.Equals( story.Inventory.ActiveItemOf(story.Ego), parts[1], StringComparison.OrdinalIgnoreCase))
                {
                    story.Screens.Show(new Screen(ScreenKind.InventoryInspect, parts[1]));
                }
                else
                {
                    story.Inventory.SetActive(story.Ego, parts[1]);
                }

                break;

            // Riding the moped, which is arriving from the map rather than from the room the player left: scene files and scene scripts both ask.
            case "drive" when parts.Length > 1:
                story.Screens.Replace(new Screen(ScreenKind.Driving, "ride:" + parts[1]));
                break;

            // Split into three at most, because what a command is *about* may itself carry a colon and only the first two fields are the command.
            case "sidney" when sidney is not null && parts.Length > 1:
                OnSidney( parts[1], chose.Split(':', 3) is [_, _, string about] ? about : string.Empty, story, sidney, console, scan);

                break;

            default:
                break;
        }
    }

    /// <summary>What can be done to the item a close-up is showing.</summary>
    /// <returns>The verbs, or null when the screen is not about an item.</returns>
    /// <param name="panel">The screen on top.</param>
    /// <param name="scene">The room, which is where the action files are.</param>
    /// <param name="story">The game, for who the player is and what they carry.</param>
    private static IReadOnlyList<string>? ItemVerbs( Screen panel, LoadedScene scene, GameState story)
    {
        if (panel.Kind is not (ScreenKind.InventoryInspect or ScreenKind.Inventory) || panel.Subject is not { Length: > 0 } item ||
            scene.Actions is not { } actions)
        {
            return null;
        }

        return [.. actions .Resolve(item, story.Ego, story.Inventory.ItemsOf(story.Ego)) .Select(a => a.LocalizedVerb)
            .Where(v => !IsAboutTheRoom(v))];
    }

    /// <summary>Whether a verb only means anything for a thing still in the room.</summary>
    /// <returns>True when it has no meaning for something already in a pocket.</returns>
    /// <param name="verb">The verb an action file wrote.</param>
    private static bool IsAboutTheRoom(string verb) => verb.Equals("PICKUP", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("TAKE", StringComparison.OrdinalIgnoreCase) || verb.Equals("OPEN", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("CLOSE", StringComparison.OrdinalIgnoreCase) || verb.Equals("ENTER", StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts each of the room's nouns where it appears on the screen.</summary>
    /// <returns>The ones in front of the camera, nearest first.</returns>
    /// <param name="nouns">Each noun and the middle of what it occupies, in world space.</param>
    /// <param name="camera">Where the view is.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    private static IReadOnlyList<(string Noun, Vector2 At)> OnScreen( IReadOnlyList<(string Noun, Vector3 Where)> nouns, Camera camera, int width,
        int height)
    {
        // Without the jitter.
        Matrix4x4 viewProjection = camera.View * camera.ProjectionWithoutJitter((float)width / Math.Max(1, height));

        List<(string Noun, Vector2 At, float Depth)> found = [];

        foreach ((string noun, Vector3 where) in nouns)
        {
            Vector4 clip = Vector4.Transform(new Vector4(where, 1f), viewProjection);

            if (clip.W <= 0.001f)
            {
                continue;
            }

            var screen = new Vector2( (clip.X / clip.W * 0.5f + 0.5f) * width, (clip.Y / clip.W * 0.5f + 0.5f) * height);

            if (screen.X < 0 || screen.X > width || screen.Y < 0 || screen.Y > height)
            {
                continue;
            }

            found.Add((noun, screen, clip.W));
        }

        return [.. found.OrderBy(f => f.Depth).Select(f => (f.Noun, f.At))];
    }

    /// <summary>An angle in degrees, for a line somebody has to read.</summary>
    private static double Degrees(float radians) => radians * 180.0 / Math.PI;

    /// <summary>One character an opening pose moved, as a line of the log.</summary>
    private static string Described((string Who, Vector3 Where, float Placed, float? Wanted) m)
    {
        string at = FormattableString.Invariant( $"{m.Who} at {m.Where.X:F0}, {m.Where.Z:F0} facing {Degrees(m.Placed):F0}");

        return m.Wanted is { } want ? at + FormattableString.Invariant(
                $" (the clip wants {Degrees(want):F0}, hips {Game.Actors.AnimationStart.Reading:F0}° off)") : at;
    }

    /// <summary>The interface's number for a slot's picture, loading it the first time it is asked for.</summary>
    /// <returns>The number, or nought when the slot has no picture.</returns>
    /// <param name="renderer">What holds the interface's pictures.</param>
    /// <param name="saves">Where the saves are.</param>
    /// <param name="slot">Which slot.</param>
    private static int Illustration( Rendering.IRenderer renderer, Game.SaveStore? saves, string slot)
    {
        if (saves is null)
        {
            return 0;
        }

        string name = "save:" + slot;

        if (renderer.OverlayPicture(name) is > 0 and { } already)
        {
            return already;
        }

        return saves.Picture(slot) is { } picture ? renderer.AddOverlayPicture(name, picture) : 0;
    }

    /// <summary>A frame reduced to something a menu row can hold.</summary>
    private static Formats.Bitmaps.DecodedImage Thumbnail(Formats.Bitmaps.DecodedImage frame)
    {
        const int Step = 4;

        int width = Math.Max(1, frame.Width / Step);
        int height = Math.Max(1, frame.Height / Step);
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int from = (((y * Step) * frame.Width) + (x * Step)) * 4;
                int to = ((y * width) + x) * 4;

                pixels[to] = frame.Pixels[from];
                pixels[to + 1] = frame.Pixels[from + 1];
                pixels[to + 2] = frame.Pixels[from + 2];
                pixels[to + 3] = 255;
            }
        }

        return new Formats.Bitmaps.DecodedImage(width, height, pixels, false, "save");
    }

    /// <summary>Whether a verb is a thing in the bag rather than something to do.</summary>
    /// <returns>True for an inventory item.</returns>
    /// <param name="verb">The verb an action file wrote.</param>
    /// <param name="verbs">What the game says each verb is.</param>
    private static bool IsAnItem(string verb, Game.Actions.VerbLibrary? verbs) => verbs?.KindOf(verb) == Game.Actions.VerbKind.Inventory;

    /// <summary>What a click inside Sidney means.</summary>
    /// <param name="item">The item's noun.</param>
    /// <param name="api">The sheep machine, for running the item's own rule.</param>
    /// <param name="scene">The room, which holds the action rules.</param>
    /// <param name="sidney">Grace's computer.</param>
    /// <param name="console">Where to say what happened, if anywhere.</param>
    private static void ScanIntoSidney( string item, Gk3SheepApi api, LoadedScene scene, Game.Sidney.SidneyMachine sidney, GameConsole? console)
    {
        if (sidney.Scan(item) is not { } scanned)
        {
            return;
        }

        console?.Print(scanned.Text);
        Log.Info($"Scanned: {scanned.Text}");

        GameState story = api.State;
        bool wasScanning = story.GetFlag("UsingScanner");
        bool hadInventory = story.Screens.IsOpen(ScreenKind.Inventory);

        story.SetFlag("UsingScanner");
        story.Screens.Show(new Screen(ScreenKind.Inventory, item));

        try
        {
            if (scene.Actions?.Find(item, "SCANNER", story.Ego) is not { } rule)
            {
                return;
            }

            // Counted before the rule runs, which is the order the original works in: Estelles_Print then sets the count outright, for both egos.
            story.IncrementNounVerbCount(item, "SCANNER");

            ActionOutcome ran = new ActionRunner(api).Run(rule);

            Log.Info( $"{item}:SCANNER [{rule.Case}] - " + $"{(ran.Ran ? "ran" : "refused")} {ran.Statements.Count} statement(s)");
        }
        finally
        {
            // A script may well have closed the inventory itself; every one of these ends in HideInventory.
            if (!hadInventory)
            {
                story.Screens.Hide(ScreenKind.Inventory);
            }

            if (!wasScanning)
            {
                story.ClearFlag("UsingScanner");
            }
        }
    }

    private static void OnSidney( string what, string which, GameState story, Game.Sidney.SidneyMachine sidney, GameConsole console,
        Action<string>? scan)
    {
        switch (what)
        {
            case "screen" when Enum.TryParse(which, out Game.Sidney.SidneyScreen screen):
                sidney.Show(screen);
                break;

            case "home":
                sidney.Home();
                break;

            // Opening a message marks it read, which is what turns the corner's notification off.
            case "mail":
                sidney.ReadMail(sidney.Mail().FirstOrDefault(m => m.Id == which));
                break;

            // The translate screen keeps its own open file: analysing a parchment and translating a tape are two things a player may have going at.
            case "open":
                sidney.OpenForTranslation(sidney.Files.FirstOrDefault(f => f.Id == which));
                break;

            // The analyze screen's four menus: one open at a time, and clicking the open one shuts it.
            case "menu" when int.TryParse(which, out int menu):
                sidney.Menu = sidney.Menu == menu ? 0 : menu;
                break;

            // The ruling the map is divided into, and whether it fills the figure or the whole picture.
            case "assist":
                console.Print(sidney.Assist().Text);
                break;

            // Yes and no arrive as their keys rather than as their words: the button says OUI in French and JA in German, and reading the first.
            case "solve":
                console.Print(sidney.Finish(Agreed(which)).Text);
                break;

            case "grid" when int.TryParse(which, out int cells):
                console.Print(sidney.Rule(cells).Text);
                break;

            case "fill":
                sidney.RuleInShape = !sidney.RuleInShape;
                break;

            case "from":
                sidney.From = which;
                break;

            case "translate":
                console.Print(sidney.Translate().Text);
                break;

            case "complete":
                sidney.Complete(Agreed(which));
                break;

            case "append":
                console.Print(sidney.Append().Text);
                break;

            // Scanning runs the game's own rule as well as making the file.
            case "scan":
                scan?.Invoke(which);
                break;

            case "file":
                sidney.OpenFile(sidney.Files.FirstOrDefault(f => f.Id == which));
                break;

            case "look":
                sidney.Look();
                break;

            case "page":
                sidney.Follow(which);
                break;

            case "suspect" when int.TryParse(which, out int index):
                sidney.OpenSuspect( sidney.Suspects().FirstOrDefault(s => s.Index == index));

                break;

            case "link" when sidney.Files.FirstOrDefault(f => f.Id == which) is { } linking:
                console.Print(sidney.LinkToSuspect(linking).Text);
                break;

            case "unlink" when sidney.Files.FirstOrDefault(f => f.Id == which) is { } unlinking:
                console.Print(sidney.UnlinkFromSuspect(unlinking).Text);
                break;

            case "match":
                console.Print(sidney.MatchPrint().Text);
                break;

            // Composing a card and printing it are two things: the machine has to have a
            // job and a face on it before Gabriel will say what he thinks of it.
            case "id" when sidney.Library.Identities() .FirstOrDefault(i => i.Key == which) is { } identity:
                sidney.ChooseIdentity(identity);
                break;

            case "face":
                sidney.GracesFace = which.StartsWith("GRA", StringComparison.OrdinalIgnoreCase);
                break;

            case "print":
                console.Print(sidney.PrintIdentity().Text);
                break;

            case "do" when Enum.TryParse(which, out Game.Sidney.SidneyAction action):
                sidney.Perform(action);
                break;

            // The anagram parser: a word moved into the phrase, the last one taken back
            // out, and the screen put away again.
            case "word" when int.TryParse(which, out int word):
                console.Print(sidney.ChooseAnagramWord(word).Text);
                break;

            case "erase":
                console.Print(sidney.EraseAnagramWord().Text);
                break;

            case "anagram":
                sidney.CloseAnagram();
                break;

            case "answer":
                sidney.Answer(which);
                break;

            default:
                break;
        }
    }

    /// <summary>Whether one of Sidney's yes-or-no answers was the yes.</summary>
    /// <returns>True for yes.</returns>
    /// <param name="answer">The choice's key, which is Yes or No.</param>
    private static bool Agreed(string answer) => answer.StartsWith("Y", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a screen may take the player from here.</summary>
    /// <returns>The places, which is empty where the screen is not about going anywhere.</returns>
    /// <param name="screen">Which screen is asking.</param>
    /// <param name="scene">The room.</param>
    /// <param name="story">The game.</param>
    private static List<string> Reachable(Screen screen, LoadedScene scene, GameState story)
    {
        if (screen.Kind is not (ScreenKind.Driving or ScreenKind.Binoculars))
        {
            return [];
        }

        List<string> places = [];

        foreach (string location in story.VisitedLocations(story.Ego))
        {
            if (!string.Equals(location, scene.Name, StringComparison.OrdinalIgnoreCase))
            {
                places.Add(location.ToUpperInvariant());
            }
        }

        places.Sort(StringComparer.Ordinal);

        return places;
    }

    /// <summary>The end of a chase: what it put on the map, and where it leaves the player.</summary>
    /// <returns>Where the player rides on to, or null when the map is left up for them to choose — which is every chase but Lady.</returns>
    /// <param name="traffic">The chase.</param>
    /// <param name="story">The game.</param>
    private static string? Arrive(Game.DrivingTraffic traffic, GameState story)
    {
        if (traffic.Chase is not { } quarry)
        {
            return null;
        }

        // Two only where the chase led somewhere new.
        story.SetNounVerbCount( quarry.Counted, DrivingMap.Follow, quarry.Reveals.Count > 0 ? 2 : 1);

        foreach (string place in quarry.Reveals)
        {
            if (DrivingMap.Reveal(story, place))
            {
                Log.Info($"The map now knows {place}");
            }
        }

        // And where they are now.
        if (quarry.LeavesThemAt is { } at)
        {
            story.SetActorLocation(quarry.Noun, at);
        }

        string arrived = quarry.Arrives ?? traffic.From ?? story.Location;

        Log.Info(quarry.LeavesMapOpen ? $"Followed {quarry.Noun} to {arrived}; the map stays open" : $"Followed {quarry.Noun} to {arrived}");

        return quarry.LeavesMapOpen ? null : arrived;
    }

    /// <summary>Why a room was left.</summary>
    /// <param name="Code">Process exit code, if this is the end of it.</param>
    /// <param name="Destination">Where the story went, or null when the player quit.</param>
    private readonly record struct RoomExit(int Code, string? Destination);

    /// <summary>A dusted glass waiting on the player to say whose it was.</summary>
    /// <param name="Glass">Which glass.</param>
    /// <param name="Question">The noun whose topics ask.</param>
    private readonly record struct GlassQuestion(string Glass, string Question)
    {
        /// <summary>Whether the bar has been opened yet, or the line is still being said.</summary>
        public bool Opened { get; init; }

        /// <summary>How often Wilkes had been named before the bar opened.</summary>
        public int Wilkes { get; init; }

        /// <summary>How often Buchelli had been named before the bar opened.</summary>
        public int Buchelli { get; init; }
    }

    /// <summary>Puts a picture's transparency back, where the game keeps it in a second bitmap.</summary>
    /// <returns>The picture, with the mask applied where there is one.</returns>
    /// <param name="archives">The game's data.</param>
    /// <param name="file">The picture's file name.</param>
    /// <param name="picture">The picture, decoded.</param>
    private static Formats.Bitmaps.DecodedImage Masked( GameArchives archives, string file, Formats.Bitmaps.DecodedImage picture)
    {
        // Only the game's own art reaches this: a replacement carries its own transparency and never wants the 1999 mask over it.
        string beside = Path.GetFileNameWithoutExtension(file) + "A" + Path.GetExtension(file);

        if (archives.Read(beside) is not { } bytes)
        {
            return picture;
        }

        Formats.Bitmaps.DecodedImage mask;

        try
        {
            mask = Formats.Bitmaps.BitmapDecoder.Decode(bytes, beside);
        }
        catch (Formats.FormatParseException)
        {
            return picture;
        }

        if (mask.Width != picture.Width || mask.Height != picture.Height)
        {
            return picture;
        }

        byte[] pixels = new byte[picture.Pixels.Length];
        picture.Pixels.CopyTo(pixels, 0);

        // The mask is painted white where the picture shows and black where it does not, so any one of its channels is the alpha.
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = mask.Pixels[i - 3];
        }

        Bleed(pixels, picture.Width, picture.Height);

        return picture with { Pixels = pixels, HasAlpha = true };
    }

    /// <summary>Gives every transparent pixel the colour of its nearest visible neighbour.</summary>
    /// <param name="pixels">The picture, RGBA, changed in place.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height.</param>
    private static void Bleed(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
        {
            return;
        }

        byte[] was = (byte[])pixels.Clone();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;

                if (was[at + 3] != 0)
                {
                    continue;
                }

                int red = 0, green = 0, blue = 0, seen = 0;

                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;

                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        int near = ((ny * width) + nx) * 4;

                        if (was[near + 3] == 0)
                        {
                            continue;
                        }

                        red += was[near];
                        green += was[near + 1];
                        blue += was[near + 2];
                        seen++;
                    }
                }

                if (seen == 0)
                {
                    continue;
                }

                pixels[at] = (byte)(red / seen);
                pixels[at + 1] = (byte)(green / seen);
                pixels[at + 2] = (byte)(blue / seen);
            }
        }
    }

    /// <summary>Carries out whatever the fingerprint kit just did.</summary>
    /// <returns>The glass question this raised, or null when it raised none.</returns>
    /// <param name="step">What the kit says follows.</param>
    /// <param name="kit">The dusting it came from.</param>
    /// <param name="story">The game.</param>
    /// <param name="api">The room, for the line and the score sheet.</param>
    private static GlassQuestion? Kitted( Game.DustingStep step, Game.FingerprintDusting kit, GameState story, Gk3SheepApi api)
    {
        if (!step.Anything)
        {
            return null;
        }

        if (step.Say is { Length: > 0 } line)
        {
            new ActionRunner(api).Run(new Formats.Actions.NvcAction
            {
                Noun = kit.Noun, Verb = "FINGERPRINT_KIT", Case = "DUSTED", Script = string.Create(
                    CultureInfo.InvariantCulture, $"wait StartDialogue(\"{line}\", 1)"), Source = "the fingerprint kit",
            });
        }

        // The lobby's two glasses: whose print it is is not in the table, and asking is a conversation in the room rather than anything the kit can.
        GlassQuestion? asked = null;

        if (step.Glass && Game.DirtyGlasses.Dust(kit.Noun, story) is { } glass)
        {
            Dusted(glass, kit.Noun, story, api);

            if (glass.Asks is { } question)
            {
                asked = new GlassQuestion(kit.Noun, question);
            }
        }

        if (step.Close)
        {
            story.Screens.Back();
        }

        return asked;
    }

    /// <summary>What dusting one of the lobby's glasses came to, done to the story.</summary>
    /// <param name="glass">What Gabriel made of it.</param>
    /// <param name="noun">Which glass.</param>
    /// <param name="story">The game.</param>
    /// <param name="api">The room, for the line and the score sheet.</param>
    private static void Dusted(Game.GlassDusting glass, string noun, GameState story, Gk3SheepApi api)
    {
        if (glass.Says is { Length: > 0 } line)
        {
            new ActionRunner(api).Run(new Formats.Actions.NvcAction
            {
                Noun = noun, Verb = "FINGERPRINT_KIT", Case = "DUSTED", Script = string.Create(
                    CultureInfo.InvariantCulture, $"wait StartDialogue(\"{line}\", 1)"), Source = "the fingerprint kit",
            });
        }

        if (glass.Lifts)
        {
            IReadOnlyList<string> gained = Game.FingerprintKit.Lift(noun, story, api.Scores);

            Log.Info($"fingerprints: {noun} gave {string.Join(", ", gained)}");
        }

        if (glass.Mislabels is { Length: > 0 } wrong)
        {
            story.Inventory.Add(story.Ego, wrong);

            Log.Info($"fingerprints: {noun} gave {wrong}, which is the wrong name");
        }

        Log.Info($"fingerprints: {Game.DirtyGlasses.Variable} = {story.GetVariable(Game.DirtyGlasses.Variable)}");
    }
}

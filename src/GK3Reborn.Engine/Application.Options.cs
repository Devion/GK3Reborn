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
/// <summary>Reading the command line.</summary>
public static partial class Application
{
    /// <summary>Makes sure the scene is loaded at a point in the story, not merely at a time of day.</summary>
    /// <returns>A request with a story behind it, where the room has one.</returns>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    /// <param name="timeblock">What the player asked for.</param>
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
            Log.Info( $"Story: {scene} has no timeblock of its own, so its conditions stay " + "undecided and its objects answer to nothing.");

            return asked;
        }

        string chosen = known[0];

        Log.Info(timeblock is { Length: > 0 } asOfDay ? $"Story: '{asOfDay}' is a time of day, not a point in the story, so nothing " +
              $"in the room would answer to anything. Using {chosen} instead."
            : $"Story: no timeblock given, so nothing in the room would answer to " + $"anything. Using {chosen}.");

        Log.Info($"  {scene} knows: {string.Join(" ", known)}");

        return SceneRequest.For(scene, chosen);
    }

    /// <summary>The story timeblocks a scene has a file for.</summary>
    /// <returns>The codes, in order.</returns>
    /// <param name="archives">The game's archives.</param>
    /// <param name="scene">The scene's name.</param>
    private static IReadOnlyList<string> Timeblocks(GameArchives archives, string scene)
    {
        string prefix = scene.ToUpperInvariant();

        return
        [
            .. archives.Names(".SIF") .Select(Path.GetFileNameWithoutExtension) .Where(n => n is not null &&
                            n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Length > prefix.Length) .Select(n => n![prefix.Length..])
                .Where(c => Timeblock.TryParse(c, out _)) .Distinct(StringComparer.OrdinalIgnoreCase) .Order(StringComparer.OrdinalIgnoreCase), ];
    }

    /// <summary>Where --pointer X,Y says the pointer is.</summary>
    /// <returns>The point, or null to follow the mouse.</returns>
    /// <param name="args">The command line.</param>
    private static Vector2? Pinned(string[] args) => Option(args, "--pointer")?.Split(',') is [string x, string y] &&
        float.TryParse(x, CultureInfo.InvariantCulture, out float px) && float.TryParse(y, CultureInfo.InvariantCulture, out float py)
            ? new Vector2(px, py) : null;

    /// <summary>Where --eye x,y,z asks the camera to stand.</summary>
    /// <returns>The viewpoint, or null when the switch is absent or unreadable.</returns>
    /// <param name="args">The command line.</param>
    private static Vector3? Standing(string[] args) => Option(args, "--eye")?.Split(',') is [string x, string y, string z] &&
        float.TryParse(x, CultureInfo.InvariantCulture, out float ex) && float.TryParse(y, CultureInfo.InvariantCulture, out float ey) &&
        float.TryParse(z, CultureInfo.InvariantCulture, out float ez) ? new Vector3(ex, ey, ez) : null;

    /// <summary>Which way --aim heading,pitch asks it to look, in degrees.</summary>
    /// <returns>The aim, or null when the switch is absent or unreadable.</returns>
    /// <param name="args">The command line.</param>
    private static Vector2? Aimed(string[] args) => Option(args, "--aim")?.Split(',') is [string h, string p] &&
        float.TryParse(h, CultureInfo.InvariantCulture, out float heading) && float.TryParse(p, CultureInfo.InvariantCulture, out float pitch)
            ? new Vector2(heading, pitch) : null;

    /// <summary>Which way --push X,Y holds the movement controls.</summary>
    /// <returns>X to the player's right, Y ahead of them, or nothing.</returns>
    /// <param name="args">The command line.</param>
    private static Vector2 Pushed(string[] args) => Option(args, "--push")?.Split(',') is [string x, string y] &&
        float.TryParse(x, CultureInfo.InvariantCulture, out float across) && float.TryParse(y, CultureInfo.InvariantCulture, out float ahead)
            ? new Vector2(across, ahead) : Vector2.Zero;

    /// <summary>Which way the player is asking to walk, in their own frame.</summary>
    /// <returns>X to their right, Y ahead of them.</returns>
    /// <param name="input">What they are doing.</param>
    private static Vector2 Pushing(Platform.IGameInput input)
    {
        var move = Vector2.Zero;

        if (input.IsHeld(Platform.CameraAction.Forward))
        {
            move.Y += 1f;
        }

        if (input.IsHeld(Platform.CameraAction.Back))
        {
            move.Y -= 1f;
        }

        if (input.IsHeld(Platform.CameraAction.Right))
        {
            move.X += 1f;
        }

        if (input.IsHeld(Platform.CameraAction.Left))
        {
            move.X -= 1f;
        }

        // The stick's Y grows downwards, so pushing it away from you is walking forward.
        Vector2 stick = Game.Navigation.FirstPerson.Pushed(input.Sticks.Left);

        return move + new Vector2(stick.X, -stick.Y);
    }

    /// <summary>How far the player is asking to turn this frame, in radians.</summary>
    /// <returns>X across, Y up.</returns>
    /// <param name="input">What they are doing.</param>
    /// <param name="settings">How fast they asked looking to be, and which way up.</param>
    /// <param name="seconds">How long the frame lasted, for the stick.</param>
    private static Vector2 Looking(Platform.IGameInput input, Settings settings, float seconds)
    {
        // The mouse turns the view with nothing held while it is pinned for looking; with the pointer back it is a drag, which is how the camera has.
        Vector2 look = input.PointerLocked || input.IsDragging ? new Vector2(input.PointerDelta.X, -input.PointerDelta.Y) *
              (Game.Navigation.FirstPerson.Sensitivity * settings.LookSensitivity) : Vector2.Zero;

        Vector2 stick = Game.Navigation.FirstPerson.Pushed(input.Sticks.Right);

        look += new Vector2(stick.X, -stick.Y) * (Game.Navigation.FirstPerson.StickRate * settings.LookSensitivity * seconds);

        return settings.InvertLook ? new Vector2(look.X, -look.Y) : look;
    }

    /// <summary>How far to subdivide a character's head.</summary>
    /// <returns>The number of levels, within range.</returns>
    /// <param name="args">The command line.</param>
    /// <param name="settings">What the player chose.</param>
    private static int HeadLevels(string[] args, Settings settings)
    {
        if (args.Contains("--flat-heads", StringComparer.OrdinalIgnoreCase))
        {
            return 0;
        }

        return Option(args, "--heads") is { } value && int.TryParse(value, CultureInfo.InvariantCulture, out int levels)
            ? Math.Clamp(levels, 0, Game.Actors.HeadRefinement.MaximumLevels) : settings.SmoothHeads;
    }

    /// <summary>Reads an option's value from the command line.</summary>
    private static string? Option(string[] args, string name) => CommandLine.Value(args, name);

    /// <summary>Whether the room being built is one the binoculars are showing, or the one they are being lowered in.</summary>
    private static bool Looking(Gk3SheepApi api) => api.Leaning is not null || api.Resuming is not null;

    /// <summary>How much of the cut-content table the command line asks for.</summary>
    /// <returns>Which tier to apply.</returns>
    /// <param name="args">The command line.</param>
    /// <param name="settings">The player's saved settings, which the menu writes.</param>
    private static CutContentTier RestorationTier(string[] args, Settings settings)
    {
        if (!args.Contains("--restore-cut-content", StringComparer.OrdinalIgnoreCase))
        {
            return settings.RestoredContent;
        }

        string? how = Option(args, "--restore-cut-content");

        return how?.ToUpperInvariant() switch
        {
            "ALL" => CutContentTier.All, "REBUILT" => CutContentTier.Reconstructed, _ => CutContentTier.Observation,
        };
    }

    /// <summary>Where the game is usually installed relative to the repository.</summary>
    private static string? EnhancedTextureDirectory(string[] args)
    {
        bool asked = args.Contains("--enhanced", StringComparer.OrdinalIgnoreCase);

        if (Option(args, "--enhanced") is { Length: > 0 } named && !named.StartsWith('-'))
        {
            return Path.IsPathRooted(named) || Option(args, "--workspace") is not { } under ? named : Path.Combine(under, named);
        }

        if (Option(args, "--workspace") is { Length: > 0 } workspace)
        {
            return Path.Combine(workspace, "enhanced", "textures");
        }

        return asked ? Path.Combine(DefaultWorkspaceDirectory(), "enhanced", "textures") : null;
    }
}

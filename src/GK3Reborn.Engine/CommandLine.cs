namespace GK3Reborn;

/// <summary>
/// What can be typed after the executable's name, and what each thing means.
/// </summary>
public static class CommandLine
{
    /// <summary>The spellings that ask for the usage text.</summary>
    private static readonly string[] HelpSwitches = ["--help", "-h", "-?", "/?"];

    /// <summary>Whether the command line asks for the usage text and nothing else.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>True if it does.</returns>
    public static bool WantsHelp(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Any(a => HelpSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Reads a switch's value from the command line.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="name">The switch, with its dashes.</param>
    /// <returns>The word after it, or null if the switch is absent or has no word.</returns>
    public static string? Value(string[] args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(name);

        int at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        if (at < 0 || at + 1 >= args.Length)
        {
            return null;
        }

        string next = args[at + 1];
        return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
    }

    /// <summary>Which graphics API the command line asks for, by name.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>
    /// What <c>--backend</c> was given, or what a shorthand stands for, or null when neither
    /// was typed. Not parsed: <see cref="Rendering.RenderBackends.TryParse"/> does that, so
    /// that a typo is reported rather than resolved.
    /// </returns>
    public static string? BackendAsked(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (Value(args, "--backend") is { } named)
        {
            return named;
        }

        if (Has(args, "--vulkan", "-vulkan", "--vk", "-vk"))
        {
            return "vulkan";
        }

        if (Has(args, "--d3d12", "-d3d12", "--dx12", "-dx12", "--direct3d", "-direct3d"))
        {
            return "d3d12";
        }

        return null;
    }

    /// <summary>The usage text, in full.</summary>
    /// <returns>What <c>--help</c> prints.</returns>
    public static string Usage() =>
        """
        GK3Reborn — Gabriel Knight 3, rebuilt

        usage:
          GK3Reborn [options]

        With no options: intro, menu, then day one at 10am. Every run writes log.txt.

        help:
          --help, -h, -?        Print this and exit.

        where to start:
          --data DIR            The game's Data directory.
          --start SCENE         Room the story starts in. Default R25.
          --timeblock TB        Time of day, as the game names it: 110A is day 1, 10am.
          --scene SCENE         Open a room directly. No intro, no menu.
          --front               Show the menu first anyway.
          --camera NAME         Start at one of the room's cameras.
          --skip-intro          Play no opening films.
          --no-movies           Play no films at all.
          --front-page PAGE     Open the menu on a page: Options, Video, Display.
          --dance               Spell the word on the title screen before the first frame.
          --dance-guests A,B    Invite only these guests to the party, by model name.
          --settings FILE       Use another settings file.
          --movie NAME          Play one film and nothing else.
          --language CODE       en, fr, de, it, es, pt, ru, pl. Needs Reborn_<CODE>.rebarn.

        graphics:
          --backend NAME        vulkan or d3d12.
          --vulkan, --d3d12     The same, shorter.
          --rt LEVEL            Ray tracing: off, low, medium or high.
          --width N, --height N Window size.
          --libs-dir DIR        Where the DLSS and FSR runtimes are.
          --expand-blocks       Decompress textures instead of using BC formats.
          --heads N             Head refinement, 0 to 3.
          --flat-heads          The same as --heads 0.
          --round N             How far round objects are rounded.
          --relief N            Displacement budget. 0 displaces nothing.
          --no-thick-cards      Railings and fences stay flat cards.
          --no-card-shadows     Thick cards cast no shadow.
          --no-cull             Draw both sides of every room surface.
          --no-birds            No birds render in the sky.
          --no-insects          No insects and no dust in the air.
          --no-grass            No grass grown over the lawns.
          --no-sun-rays         No rays of sunlight, outdoors or in at a window.
          --no-shimmer          No heat haze over the far ground.
          --no-towns            Couiza and Rennes-les-Bains as the game shipped them.
          --towns               Build both towns out, whatever the setting says.
          --no-shader-fire      Draw the game's painted flame cards instead of fire.
          --no-sun              No synthesized sun outdoors.
          --real-light          Light rooms from real sources only. Some get darker.
          --no-real-light       Keep the artists' fill lights on.
          --no-floor-reflections No rendered reflections in polished floors.
          --no-emissive         Glowing objects light nothing.
          --emissive-list       List what glows in this room.
          --daylight-list       List which objects are windows.
          --flat                Colour textures only. No normal maps, no relief.
          --font NAME           Use a game font sheet, such as F_CAPTION_D_20.
          --font-file PATH      Use a TrueType file.
          --bitmap-font         Use the game's 640x480 letters.

        content:
          --enhanced [DIR]      Prefer loose enhanced textures.
          --workspace DIR       Where the content workspace is.
          --uncompressed        Read loose sets instead of the packs.
          --rebarn              Packs only. Refuse to start without them.
          --packs DIR           Where the packs are.
          --overrides DIR       Where the player's overriding files are.
          --no-overrides        Ignore the overrides directory.
          --restore-cut-content Restore content the game cannot reach. Add "all" for
                                working rules, "rebuilt" for unmodelled objects.

        photographing a run (headless, no keyboard):
          --frames N            Stop after N frames.
          --screenshot PATH     Write the last frame there.
          --offscreen           Draw one frame with no window and write it out.
          --render              Present frames until the window is closed.
          --headless-frames     Stop --render after sixty frames.
          --pointer X,Y         Pin the pointer there.
          --click F[@X,Y][;..]  Click on frame F, optionally moving the pointer first.
          --menu                Open the verb wheel under it.
          --eye X,Y,Z           Put the camera there.
          --aim H,P             Aim it. Heading and pitch, in degrees.
          --free-camera         Let the camera be flown.
          --console TEXT        Open the console and type that.
          --run CMD[;CMD]       Run console commands first. @N runs one on frame N.
          --do NOUN:VERB[;..]   Perform actions on arrival.
          --then NOUN:VERB      The same in the second room.
          --did TB              Mark a timeblock complete.
          --play CLIP           Play an animation on arrival.
          --carry ITEM[,..]     Start with things in the bag.
          --screen KIND[:ABOUT] Open a screen on arrival.
          --scan ITEM[,..]      Scan things into Sidney and open the first.
          --sidney PAGE[:WHAT]  Open Sidney at a page, and something on it.
          --analyse I:OP[;I:OP] Run Sidney's analyze operations, in order.
          --link ITEM[;ITEM]   Link scanned files to the open suspect.
          --mark X,Y[;X,Y]      Mark places on Sidney's map.
          --shape N[;N]         Lay saved figures over the map. Again takes one off.
          --grid N              Rule the map into N cells. Negative rules the figure.
          --map STEP[;STEP]     Drive the map: mark X,Y / shape NAME / grid N / do OP.
          --zoom N              Sidney's map zoom, 1 to 6.
          --glide CAMERA        Glide to a named camera.
          --glance WHO:AT       Have somebody look at something.
          --verbose             List everything that failed to load.
          --timings             Report load times at every door.
          --lights              List the room's authored lights.
          --trace-actors        Report where everybody stands as clips move them.
          --motion              Draw the motion vectors instead of the picture.
          --flicker             Measure frame-to-frame change.

        getting content out:
          --extract             Write content out as files and exit.
          --name TEXT           Only entries whose names match.
          --kinds LIST          Only these kinds: textures, normals, orm, height, emissive,
                                models, scene-geometry, video, menu, manifests, raw. With
                                --from game, file extensions instead.
          --from SOURCE         packs, game or all. Default packs.
          --as FORM             png or dds.
          --extract-to DIR      Write somewhere other than overrides/.

        The offline tools are a separate program: GK3Reborn.Tools --help lists them.

        """;

    private static bool Has(string[] args, params string[] spellings) =>
        args.Any(a => spellings.Contains(a, StringComparer.OrdinalIgnoreCase));
}

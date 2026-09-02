namespace GK3Reborn;

/// <summary>
/// What can be typed after the executable's name, and what each thing means.
/// </summary>
/// <remarks>
/// <para>
/// The switches themselves are read where they are used — <see cref="Application"/> asks
/// for each at the moment it matters, and that is the right place for the reading — so
/// this is the one place they are all written down. A switch that is not in
/// <see cref="Usage"/> is a switch nobody can find, and a test holds the two together.
/// </para>
/// <para>
/// No arguments has to be how a player starts the game, so nothing here is required and
/// nothing is substituted; the defaults belong where they can be read, in the settings.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// The next switch is not this one's value. <c>--start --rt high</c> means "start
    /// where the game starts, and trace at high", not "open the room called --rt" — and
    /// taking it as a room name is a failure a long way from the mistake, after a window
    /// has opened and a menu has been sat through.
    /// </remarks>
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
    /// <remarks>
    /// <c>--vulkan</c> and <c>--d3d12</c> are the shorthands, and their single-dash
    /// spellings too, because <c>-vulkan</c> is what somebody types when a Direct3D machine
    /// will not start and they have been told to try the other renderer. Before this they
    /// were ignored without a word, and the game went on failing in Direct3D. <c>--backend</c>
    /// outranks a shorthand when both are given, being the one that names what it means.
    /// </remarks>
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

        No arguments is how a player starts it: the intro, the menu, and then day one at
        ten in the morning in the lobby of the Hôtel de Rennes-le-Château. Everything else
        is for looking at a particular thing, and every run writes log.txt beside the
        executable (or in the user's own directory when that cannot be written to).

        help:
          --help, -h, -?        Print this and exit.

        where to start:
          --data DIR            The game's Data directory. Found beside the executable or in
                                the usual development place otherwise.
          --start SCENE         The room the story begins in after the menu. Default R25.
          --timeblock TB        The time of day, as the game names it: 110A is day 1, 10am.
          --scene SCENE         Open a room directly, with no intro and no menu.
          --front               With --scene, show the menu first anyway.
          --camera NAME         Start at one of the room's own cameras.
          --skip-intro          Go straight to the menu; play none of the opening films.
          --front-page PAGE     Open the menu on one of its pages: Options, Video, Display.
          --settings FILE       Read and write another settings file, leaving yours alone.
          --movie NAME          Play one of the films straight away.

        graphics:
          --backend NAME        vulkan or d3d12. Windows gets Direct3D 12 unless told
                                otherwise; everywhere else is Vulkan. If Direct3D cannot
                                start on this machine the game says so and uses Vulkan.
          --vulkan, --d3d12     The same, shorter. -vulkan and -dx12 are accepted too.
          --rt LEVEL            Ray tracing: off, low, medium or high. Outranks the setting
                                for this run only; a device without it draws none.
          --width N, --height N The window size, for photographing an interface at a size
                                this display has not got.
          --libs-dir DIR        Where the DLSS and FSR runtimes are. Default libs/ beside
                                the executable, and libs/streamline/ under it.
          --expand-blocks       Behave like a device with no block-compressed texture
                                formats, which is how the Mac's path is exercised elsewhere.
          --heads N             How many times to refine the characters' heads, 0 to 3.
          --flat-heads          The same as --heads 0.
          --round N             How far the round things are rounded.
          --relief N            The displacement budget; 0 displaces nothing.
          --no-thick-cards      Draw every railing, fence and chain as the flat card it
                                shipped as, rather than giving it a thickness.
          --flat                Colour textures only: no normal maps, no relief.
          --font NAME           Draw the interface with one of the game's own font sheets,
                                such as F_CAPTION_D_20.
          --font-file PATH      Draw it from a TrueType file.
          --bitmap-font         Draw it in the game's own 640x480 letters.

        content:
          --enhanced [DIR]      Prefer the loose enhanced textures; bare means the content
                                workspace beside the repository.
          --workspace DIR       The content workspace, for the loose enhanced sets.
          --uncompressed        Read the loose sets rather than the block-compressed packs.
          --rebarn              The .rebarn packs and nothing else; refuse to start without.
          --packs DIR           Where the packs are. Beside the executable otherwise.
          --overrides DIR       Where the player's own overriding files are. Default
                                overrides/ beside the executable.
          --no-overrides        Ignore the overrides directory.

        photographing a run (headless, no keyboard):
          --frames N            Stop after N frames.
          --screenshot PATH     Write the last frame there.
          --offscreen           Draw one frame with no window at all and write it out.
          --render              Open a window and present frames until it is closed. A
                                smoke test of the device, the swapchain and the present.
          --headless-frames     With --render, sixty frames and then stop.
          --pointer X,Y         Pin the pointer at a spot, as if a mouse were there.
          --menu                Open the verb wheel under it.
          --eye X,Y,Z           Stand the camera somewhere in the room.
          --aim H,P             Aim it, heading and pitch in degrees.
          --free-camera         Let the camera be flown; the Playing page has the same row.
          --console TEXT        Open the console and type that into it.
          --run CMD[;CMD]       Run console commands before the first frame; @N in front
                                of one runs it on frame N instead.
          --do NOUN:VERB[;..]   Perform actions on arrival, as a click would.
          --then NOUN:VERB      The same in the second room, for measuring a return trip.
          --did TB              Mark a timeblock's completion rules as met.
          --play CLIP           Play an animation on arrival.
          --carry ITEM[,..]     Put things in the bag before the room is looked at.
          --screen KIND[:ABOUT] Open a screen on the way in; the colon names its subject.
          --scan ITEM[,..]      Scan things into Sidney, and open the first file.
          --sidney PAGE         Open Sidney at one of its pages.
          --glide CAMERA        Glide the camera to a named angle.
          --glance WHO:AT       Have somebody look at something.
          --verbose             List everything that could not be loaded.
          --timings             Say where the load time went at every door.
          --lights              List the room's authored lights.
          --trace-actors        Say where everybody stands whenever a clip moves them.
          --motion              Report the motion vectors rather than drawing them.
          --flicker             Measure how much the picture changes frame to frame.

        getting content out:
          --extract             Write the game's content out as files, laid out for
                                overrides/, and exit. Nothing else below applies otherwise.
          --name TEXT           Only entries whose names match: --name R25 takes the room's
                                files.
          --kinds LIST          Only these kinds of content, comma-separated: textures,
                                normals, orm, height, emissive, models, scene-geometry,
                                video, manifests, raw. With --from game, file extensions
                                instead: --kinds SIF,NVC.
          --from SOURCE         packs, game or all. Default packs.
          --as FORM             png or dds. Textures come out as they are stored otherwise.
          --extract-to DIR      Somewhere other than overrides/. A whole-pack extract with
                                no filter refuses to go into overrides/, because everything
                                in there overrides itself.

        The offline tools are a separate program: GK3Reborn.Tools --help lists them.

        """;

    private static bool Has(string[] args, params string[] spellings) =>
        args.Any(a => spellings.Contains(a, StringComparer.OrdinalIgnoreCase));
}

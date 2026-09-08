using System.Numerics;
using GK3Reborn.Formats.Ini;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Animation;

/// <summary>A sound an animation plays as it runs.</summary>
/// <param name="Frame">Which frame it starts on.</param>
/// <param name="Name">The audio asset.</param>
/// <param name="Volume">How loud, from 0 to 100.</param>
/// <param name="Model">
/// Which model it comes from, or empty for a sound with no place in the room. The game's
/// own files name one for anything a character does — Gabriel's yawn is <c>gab</c> — which
/// is what lets it be heard from where they are standing.
/// </param>
public readonly record struct AnimationSound(
    int Frame, string Name, int Volume, string Model = "")
{
    /// <summary>How loud it is, as a gain rather than the file's percentage.</summary>
    public float Gain => Math.Clamp(Volume, 0, 100) / 100f;
}

/// <summary>A line of dialogue an animation shows and speaks.</summary>
/// <param name="Frame">Which frame it appears on.</param>
/// <param name="EndFrame">Which frame it goes away on.</param>
/// <param name="Speaker">The noun of whoever is talking.</param>
/// <param name="Text">What they say.</param>
public readonly record struct AnimationCaption(int Frame, int EndFrame, string Speaker, string Text);

/// <summary>A vertex animation an animation starts on a frame.</summary>
/// <param name="Frame">Which frame it starts on.</param>
/// <param name="Name">The <c>.ACT</c> asset.</param>
/// <param name="Placement">
/// Where in the room to play it, or null to play it wherever the model already is.
/// </param>
public readonly record struct AnimationAction(
    int Frame, string Name, AnimationPlacement? Placement = null);

/// <summary>Which part of a face a texture belongs to.</summary>
public enum FacePart
{
    /// <summary>The mouth, which is what lip sync moves.</summary>
    Mouth,

    /// <summary>The eyelids, which is what a blink moves.</summary>
    Eyelids,

    /// <summary>The forehead, which is where the brows are.</summary>
    Forehead,
}

/// <summary>A mouth shape an animation puts on somebody's face.</summary>
/// <param name="Frame">Which frame it appears on.</param>
/// <param name="Actor">The noun of whoever's face it is.</param>
/// <param name="Mouth">
/// The shape, as the files name it: <c>MOUTH00</c> to <c>MOUTH07</c>, and a handful of
/// <c>MOUTH04_BLOOD</c>. It is a suffix, not a texture — the character's own three-letter
/// code goes in front of it.
/// </param>
public readonly record struct AnimationMouth(int Frame, string Actor, string Mouth);

/// <summary>A patch an animation lays over part of somebody's face, or takes off again.</summary>
/// <param name="Frame">Which frame.</param>
/// <param name="Actor">The noun of whoever's face it is.</param>
/// <param name="Part">Which region of the face.</param>
/// <param name="Texture">The bitmap, or null to put the face back as it was.</param>
public readonly record struct AnimationFace(
    int Frame, string Actor, FacePart Part, string? Texture);

/// <summary>A foot an animation puts down.</summary>
/// <param name="Frame">Which frame it lands on.</param>
/// <param name="Actor">The noun of whoever is walking.</param>
/// <param name="Scuff">Whether the foot is dragged rather than planted.</param>
public readonly record struct AnimationStep(int Frame, string Actor, bool Scuff);

/// <summary>A line of recorded speech an animation starts part-way through itself.</summary>
/// <param name="Frame">Which frame it begins on.</param>
/// <param name="Plate">
/// The licence plate of the line, as the file writes it — usually with the language letter
/// already on the front, which is what tells this apart from the plate a script gives.
/// </param>
public readonly record struct AnimationDialogue(int Frame, string Plate);

/// <summary>A camera an animation puts the view on part-way through itself.</summary>
/// <param name="Frame">Which frame it cuts on.</param>
/// <param name="Camera">The camera's name, as the scene names it.</param>
/// <param name="Glide">Whether the view travels there rather than cutting.</param>
public readonly record struct AnimationShot(int Frame, string Camera, bool Glide);

/// <summary>An expression an animation puts on somebody's face part-way through itself.</summary>
/// <param name="Frame">Which frame it appears on.</param>
/// <param name="Actor">The noun of whoever's face it is.</param>
/// <param name="Name">The mood or expression — <c>SURPRISED</c>, <c>HALFANGRY</c>.</param>
/// <param name="Worn">
/// Whether it is worn until something takes it off (<c>MOOD</c>) or happens once and is
/// over (<c>EXPRESSION</c>). The distinction is the file's and it matters: a mood left on
/// is a character who stays surprised for the rest of the scene.
/// </param>
public readonly record struct AnimationMood(int Frame, string Actor, string Name, bool Worn);

/// <summary>A soundtrack an animation starts or stops part-way through itself.</summary>
/// <param name="Frame">Which frame it happens on.</param>
/// <param name="Track">
/// The <c>.STK</c>, written with or without its extension depending on who typed the line,
/// or null for <c>STOPALLSOUNDTRACKS</c>.
/// </param>
/// <param name="Stop">Whether it stops one rather than starting it.</param>
/// <param name="Looping">
/// Whether a started soundtrack walks its list forever or once. <c>PLAYSOUNDTRACKTBS</c> is
/// the once-through form; nothing in the corpus uses it, and it is read because the
/// distinction is real and the machinery already has it.
/// </param>
public readonly record struct AnimationMusic(
    int Frame, string? Track, bool Stop, bool Looping = true);

/// <summary>A texture an animation swaps part-way through.</summary>
/// <param name="Frame">Which frame it changes on.</param>
/// <param name="Model">The model whose surface it is.</param>
/// <param name="Mesh">Which mesh group.</param>
/// <param name="Submesh">Which submesh within it.</param>
/// <param name="Texture">What to paint it with.</param>
public readonly record struct AnimationTexture(
    int Frame, string Model, int Mesh, int Submesh, string Texture);

/// <summary>A texture an animation lays over part of the <em>room</em>, rather than a model.</summary>
/// <param name="Frame">Which frame it changes on.</param>
/// <param name="Scene">
/// The scene asset the line was authored against — <c>rl2_disco_a</c>. Recorded and not
/// matched against: an animation is only ever played by the room that owns it, and the
/// name is the variant the artist happened to be looking at when they wrote the line.
/// </param>
/// <param name="ObjectName">The room object whose surfaces to repaint — <c>rl2floor</c>.</param>
/// <param name="Texture">What to paint them with.</param>
public readonly record struct AnimationSceneTexture(
    int Frame, string Scene, string ObjectName, string Texture);

/// <summary>A part of the room an animation shows or hides part-way through.</summary>
/// <param name="Frame">Which frame it changes on.</param>
/// <param name="Scene">The scene asset the line was authored against.</param>
/// <param name="ObjectName">The room object to show or hide.</param>
/// <param name="Visible">Whether it is drawn from this frame on.</param>
public readonly record struct AnimationSceneVisibility(
    int Frame, string Scene, string ObjectName, bool Visible);

/// <summary>A model an animation shows or hides part-way through.</summary>
/// <param name="Frame">Which frame it changes on.</param>
/// <param name="Model">The model's own name, as the scene placed it.</param>
/// <param name="Visible">Whether it is drawn from this frame on.</param>
/// <param name="Mesh">Which mesh group, or -1 for the whole model.</param>
/// <param name="Submesh">Which submesh within it, or -1 for all of them.</param>
public readonly record struct AnimationVisibility(
    int Frame, string Model, bool Visible, int Mesh = -1, int Submesh = -1);

/// <summary>Where an absolute animation puts the thing it moves.</summary>
/// <param name="Position">The spot, in world space.</param>
/// <param name="Heading">Which way it faces there, in radians about the vertical.</param>
public readonly record struct AnimationPlacement(Vector3 Position, float Heading);

/// <summary>
/// Reader for GK3's animations.
/// </summary>
public sealed class AnimationFile
{
    /// <summary>How many frames a second an animation runs at, unless it says otherwise.</summary>
    public const int FramesPerSecond = 15;

    private AnimationFile(
        string name,
        int frames,
        IReadOnlyList<AnimationAction> actions,
        IReadOnlyList<AnimationSound> sounds,
        IReadOnlyList<AnimationCaption> captions,
        IReadOnlyList<AnimationMouth> mouths,
        IReadOnlyList<AnimationFace> faces,
        IReadOnlyList<AnimationVisibility> visibility,
        IReadOnlyList<AnimationStep> steps,
        IReadOnlyList<AnimationTexture> textures,
        IReadOnlyList<AnimationSceneTexture> sceneTextures,
        IReadOnlyList<AnimationSceneVisibility> sceneVisibility,
        int rate)
    {
        Steps = steps;
        Textures = textures;
        SceneTextures = sceneTextures;
        SceneVisibility = sceneVisibility;
        Name = name;
        FrameCount = frames;
        Actions = actions;
        Sounds = sounds;
        Captions = captions;
        Mouths = mouths;
        Faces = faces;
        Visibility = visibility;
        Rate = rate;
    }

    /// <summary>
    /// How many frames a second <em>this</em> animation runs at.
    /// </summary>
    public int Rate { get; }

    /// <summary>What it shows and hides as it runs, in file order.</summary>
    public IReadOnlyList<AnimationVisibility> Visibility { get; }

    /// <summary>The feet it puts down, in file order.</summary>
    public IReadOnlyList<AnimationStep> Steps { get; }

    /// <summary>The textures it swaps, in file order.</summary>
    public IReadOnlyList<AnimationTexture> Textures { get; }

    /// <summary>The room surfaces it repaints, in file order.</summary>
    public IReadOnlyList<AnimationSceneTexture> SceneTextures { get; }

    /// <summary>The room objects it shows and hides, in file order.</summary>
    public IReadOnlyList<AnimationSceneVisibility> SceneVisibility { get; }

    /// <summary>Name this animation was read under.</summary>
    public string Name { get; }

    /// <summary>How many frames long it is.</summary>
    public int FrameCount { get; }

    /// <summary>How long it lasts, in seconds.</summary>
    public double Duration => (double)FrameCount / Math.Max(1, Rate);

    /// <summary>The vertex animations it starts, in file order.</summary>
    public IReadOnlyList<AnimationAction> Actions { get; }

    /// <summary>The sounds it plays, in file order.</summary>
    public IReadOnlyList<AnimationSound> Sounds { get; }

    /// <summary>Whether the animation puts a soundtrack under itself.</summary>
    public bool StartsSoundtrack { get; init; }

    /// <summary>
    /// Whether this animation is something that happens rather than a pose.
    /// </summary>
    public bool IsPerformance => StartsSoundtrack;

    /// <summary>The lines it speaks, in file order.</summary>
    public IReadOnlyList<AnimationCaption> Captions { get; }

    /// <summary>The recorded lines it starts as it runs, in file order.</summary>
    public IReadOnlyList<AnimationDialogue> Dialogue { get; init; } = [];

    /// <summary>The cameras it puts the view on as it runs, in file order.</summary>
    public IReadOnlyList<AnimationShot> Shots { get; init; } = [];

    /// <summary>The moods and expressions it sets as it runs, in file order.</summary>
    public IReadOnlyList<AnimationMood> Moods { get; init; } = [];

    /// <summary>The soundtracks it starts and stops as it runs, in file order.</summary>
    public IReadOnlyList<AnimationMusic> Music { get; init; } = [];


    /// <summary>
    /// The mouth shapes it puts on people, in frame order.
    /// </summary>
    public IReadOnlyList<AnimationMouth> Mouths { get; }

    /// <summary>The patches it lays over faces and takes off again, in frame order.</summary>
    public IReadOnlyList<AnimationFace> Faces { get; }

    /// <summary>Parses an animation.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives warnings about lines that could not be read.</param>
    /// <returns>The animation.</returns>
    public static AnimationFile Parse(string text, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IniDocument document = IniDocument.Parse(text, name);

        int frames = 0;
        List<AnimationAction> actions = [];
        List<AnimationSound> sounds = [];
        List<AnimationCaption> captions = [];
        List<AnimationMouth> mouths = [];
        List<AnimationFace> faces = [];
        List<AnimationVisibility> visibility = [];
        bool soundtrack = false;
        List<AnimationStep> steps = [];
        List<AnimationTexture> textures = [];
        List<AnimationSceneTexture> sceneTextures = [];
        List<AnimationSceneVisibility> sceneVisibility = [];
        List<AnimationDialogue> spoken = [];
        List<AnimationShot> shots = [];
        List<AnimationMood> moods = [];
        List<AnimationMusic> music = [];
        int rate = FramesPerSecond;

        foreach (IniSection section in document.Sections)
        {
            switch (section.Name.ToUpperInvariant())
            {
                case "HEADER":
                    frames = section.Lines.Count > 0
                        ? (int)(section.Lines[0].Head.AsNumber() ?? 0)
                        : 0;
                    break;

                case "ACTIONS":
                    Read(section, line => actions.Add(new AnimationAction(
                        (int)(line.Entries[0].AsNumber() ?? 0),
                        line.Entries[1].Key,
                        Placement(line))));
                    break;

                case "SOUNDS":
                    // The fourth field is the model the sound comes from — "gab" for
                    // Gabriel's yawn — which is what puts it in the room rather than in the
                    // player's head. It was being dropped.
                    Read(section, line => sounds.Add(new AnimationSound(
                        (int)(line.Entries[0].AsNumber() ?? 0),
                        line.Entries[1].Key,
                        line.Entries.Count > 2 ? (int)(line.Entries[2].AsNumber() ?? 100) : 100,
                        line.Entries.Count > 3 ? line.Entries[3].Key : string.Empty)));
                    break;

                case "MTEXTURES":
                    // <frame>,<model>,<mesh>,<submesh>,<texture>
                    Read(section, line =>
                    {
                        if (line.Entries.Count > 4)
                        {
                            textures.Add(new AnimationTexture(
                                (int)(line.Entries[0].AsNumber() ?? 0),
                                line.Entries[1].Key,
                                (int)(line.Entries[2].AsNumber() ?? 0),
                                (int)(line.Entries[3].AsNumber() ?? 0),
                                line.Entries[4].Key));
                        }
                    });

                    break;

                case "STEXTURES":
                    // <frame>,<scene>,<object>,<texture>. The room rather than a model:
                    // the bar's dance floor cycling, the lobby window gaining a van.
                    Read(section, line =>
                    {
                        if (line.Entries.Count > 3)
                        {
                            sceneTextures.Add(new AnimationSceneTexture(
                                (int)(line.Entries[0].AsNumber() ?? 0),
                                line.Entries[1].Key,
                                line.Entries[2].Key,
                                line.Entries[3].Key));
                        }
                    });

                    break;

                case "SVISIBILITY":
                    // <frame>,<scene>,<object>,<on/off>.
                    Read(section, line =>
                    {
                        if (line.Entries.Count > 3)
                        {
                            sceneVisibility.Add(new AnimationSceneVisibility(
                                (int)(line.Entries[0].AsNumber() ?? 0),
                                line.Entries[1].Key,
                                line.Entries[2].Key,
                                Switched(line.Entries[3].Key)));
                        }
                    });

                    break;

                case "MVISIBILITY":
                    // Two shapes of line, told apart by how many fields there are:
                    // <frame>,<model>,<on/off> for the whole model, and
                    // <frame>,<model>,<mesh>,<submesh>,<on/off> for one part of it.
                    Read(section, line => visibility.Add(line.Entries.Count > 3
                        ? new AnimationVisibility(
                            (int)(line.Entries[0].AsNumber() ?? 0),
                            line.Entries[1].Key,
                            Switched(line.Entries[4].Key),
                            (int)(line.Entries[2].AsNumber() ?? -1),
                            (int)(line.Entries[3].AsNumber() ?? -1))
                        : new AnimationVisibility(
                            (int)(line.Entries[0].AsNumber() ?? 0),
                            line.Entries[1].Key,
                            Switched(line.Entries[2].Key))));
                    break;

                case "OPTIONS":
                    Read(section, line =>
                    {
                        if (line.Entries.Count > 2 &&
                            line.Entries[1].Key.Equals("FRAMERATE", StringComparison.OrdinalIgnoreCase) &&
                            line.Entries[2].AsNumber() is > 0 and { } named)
                        {
                            rate = (int)named;
                        }
                    });
                    break;

                case "GK3":
                    Spoken(
                        section, captions, mouths, faces, steps,
                        spoken, shots, moods, music);

                    // Whether it puts music under itself, which is the sharpest thing in an
                    // animation file that says "this is a scene that happens" rather than
                    // "this is where a thing rests". See <see cref="IsPerformance"/>.
                    soundtrack = soundtrack || music.Any(m => !m.Stop);

                    break;

                default:
                    break;
            }
        }

        if (frames <= 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1110", DiagnosticSeverity.Warning,
                "An animation gives no frame count, so it has no length.",
                name, null, "a [HEADER] with a number in it",
                frames.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Anything waiting on it will not wait."));
        }

        return new AnimationFile(
            name, Math.Max(0, frames), actions, sounds, captions, mouths, faces,
            visibility, steps, textures, sceneTextures, sceneVisibility, rate)
        {
            StartsSoundtrack = soundtrack,
            Dialogue = spoken,
            Shots = shots,
            Moods = moods,
            Music = music,
        };
    }

    /// <summary>Reads an on/off field.</summary>
    private static bool Switched(string value) =>
        value.Trim() is { Length: > 0 } text &&
        (text.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("1", StringComparison.Ordinal) ||
         text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("YES", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads an action line's absolute placement, if it has one.
    /// </summary>
    private static AnimationPlacement? Placement(IniLine line)
    {
        if (line.Entries.Count < 10)
        {
            return null;
        }

        float At(int index) => line.Entries[index].AsNumber() ?? 0;

        // Actor to model, wanted as model to actor, with y and z as Maya left them.
        var modelToActor = new Vector3(-At(2), -At(4), -At(3));
        float modelToActorHeading = At(5);

        var worldToModel = new Vector3(At(6), At(8), At(7));
        float worldToModelHeading = At(9);

        Vector3 position = worldToModel + Vector3.Transform(
            modelToActor,
            Matrix4x4.CreateRotationY(worldToModelHeading * MathF.PI / 180f));

        return new AnimationPlacement(
            position, (worldToModelHeading - modelToActorHeading) * MathF.PI / 180f);
    }

    /// <summary>
    /// Walks the lines of a node section, skipping the count they open with.
    /// </summary>
    private static void Read(IniSection section, Action<IniLine> line)
    {
        for (int i = 1; i < section.Lines.Count; i++)
        {
            if (section.Lines[i].Entries.Count >= 2)
            {
                line(section.Lines[i]);
            }
        }
    }

    /// <summary>
    /// Reads the spoken lines.
    /// </summary>
    private static void Spoken(
        IniSection section,
        List<AnimationCaption> captions,
        List<AnimationMouth> mouths,
        List<AnimationFace> faces,
        List<AnimationStep> steps,
        List<AnimationDialogue> spoken,
        List<AnimationShot> shots,
        List<AnimationMood> moods,
        List<AnimationMusic> music)
    {
        string speaker = string.Empty;

        for (int i = 1; i < section.Lines.Count; i++)
        {
            IniLine line = section.Lines[i];

            if (line.Entries.Count < 2)
            {
                continue;
            }

            int frame = (int)(line.Entries[0].AsNumber() ?? 0);

            switch (line.Entries[1].Key.ToUpperInvariant())
            {
                case "SPEAKER":
                    speaker = line.Entries.Count > 2 ? line.Entries[2].Key : string.Empty;
                    break;

                case "CAPTION":
                    if (line.Entries.Count > 2)
                    {
                        captions.Add(new AnimationCaption(frame, 0, speaker, Rest(line, 2)));
                    }

                    break;

                case "SPEAKERCAPTION":
                    if (line.Entries.Count > 4)
                    {
                        captions.Add(new AnimationCaption(
                            frame,
                            (int)(line.Entries[2].AsNumber() ?? 0),
                            line.Entries[3].Key,
                            Rest(line, 4)));
                    }

                    break;

                // <frame>,LIPSYNCH,<noun>,MOUTH03. The shape is a suffix rather than a
                // texture: the character's own three-letter code goes in front of it, and
                // which code that is depends on which model is standing in the room.
                // A foot landing. The node says only when and whose; what it sounds like
                // is decided from the floor underfoot and the character's shoes, neither of
                // which the animation knows. 3,704 of these across the corpus, all of them
                // read past until there was something that could make a noise with one.
                case "FOOTSTEP":
                case "FOOTSCUFF":
                    if (line.Entries.Count > 2)
                    {
                        steps.Add(new AnimationStep(
                            frame,
                            line.Entries[2].Key,
                            line.Entries[1].Key.Equals("FOOTSCUFF", StringComparison.OrdinalIgnoreCase)));
                    }

                    break;

                // <frame>,DIALOGUE,<plate>. One animation asking for another one's
                // recorded line, which is how a moment speaks: the spit take in the dining
                // room carries two of these and the script around it carries neither.
                case "DIALOGUE":
                    if (line.Entries.Count > 2 &&
                        line.Entries[2].Key.Trim() is { Length: > 0 } plate)
                    {
                        spoken.Add(new AnimationDialogue(frame, plate));
                    }

                    break;

                // <frame>,CAMERA,<name>[,GLIDE]. A moment frames itself.
                case "CAMERA":
                    if (line.Entries.Count > 2 &&
                        line.Entries[2].Key.Trim() is { Length: > 0 } shot)
                    {
                        shots.Add(new AnimationShot(
                            frame,
                            shot,
                            line.Entries.Skip(3).Any(e => e.Key.Trim().Equals(
                                "GLIDE", StringComparison.OrdinalIgnoreCase))));
                    }

                    break;

                // <frame>,MOOD,<noun>,<mood> and <frame>,EXPRESSION,<noun>,<expression>.
                // The same line with one difference: a mood is worn until something takes
                // it off, an expression happens and is over.
                case "MOOD":
                case "EXPRESSION":
                    if (line.Entries.Count > 3 &&
                        line.Entries[2].Key.Trim() is { Length: > 0 } wearer &&
                        line.Entries[3].Key.Trim() is { Length: > 0 } worn)
                    {
                        moods.Add(new AnimationMood(
                            frame,
                            wearer,
                            worn,
                            line.Entries[1].Key.Trim().Equals(
                                "MOOD", StringComparison.OrdinalIgnoreCase)));
                    }

                    break;

                // <frame>,PLAYSOUNDTRACK,<stk> and its once-through twin. The name is
                // written with or without the extension depending on who typed it, which is
                // the reader's problem rather than the caller's.
                case "PLAYSOUNDTRACK":
                case "PLAYSOUNDTRACKTBS":
                    if (line.Entries.Count > 2 &&
                        line.Entries[2].Key.Trim() is { Length: > 0 } started)
                    {
                        music.Add(new AnimationMusic(
                            frame,
                            started,
                            Stop: false,
                            Looping: !line.Entries[1].Key.Trim().EndsWith(
                                "TBS", StringComparison.OrdinalIgnoreCase)));
                    }

                    break;

                // <frame>,STOPSOUNDTRACK,<stk>, against <frame>,STOPALLSOUNDTRACKS, which
                // names nothing and means every one of them — the room's own included.
                case "STOPSOUNDTRACK":
                    if (line.Entries.Count > 2 &&
                        line.Entries[2].Key.Trim() is { Length: > 0 } silenced)
                    {
                        music.Add(new AnimationMusic(frame, silenced, Stop: true));
                    }

                    break;

                case "STOPALLSOUNDTRACKS":
                    music.Add(new AnimationMusic(frame, null, Stop: true));
                    break;

                case "LIPSYNCH":
                    if (line.Entries.Count > 3)
                    {
                        mouths.Add(new AnimationMouth(
                            frame, line.Entries[2].Key, line.Entries[3].Key));
                    }

                    break;

                // <frame>,FACETEX,<noun>,<bitmap>,<part> and <frame>,UNFACETEX,<noun>,<part>.
                // A part nothing here paints — L and R, the two eyes, twenty nodes in the
                // whole corpus — is left alone rather than painted over the wrong region.
                case "FACETEX":
                    if (line.Entries.Count > 3 && PartOf(line, 4) is { } painted)
                    {
                        faces.Add(new AnimationFace(
                            frame, line.Entries[2].Key, painted, line.Entries[3].Key));
                    }

                    break;

                case "UNFACETEX":
                    if (line.Entries.Count > 2 && PartOf(line, 3) is { } cleared)
                    {
                        faces.Add(new AnimationFace(frame, line.Entries[2].Key, cleared, null));
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Which region of the face a node names, or null when it is one nothing paints.
    /// </summary>
    private static FacePart? PartOf(IniLine line, int index) =>
        line.Entries.Count <= index
            ? FacePart.Mouth
            : line.Entries[index].Key.Trim().ToUpperInvariant() switch
            {
                "M" => FacePart.Mouth,
                "E" => FacePart.Eyelids,
                "H" => FacePart.Forehead,
                _ => null,
            };

    /// <summary>
    /// Puts the fields from an index onwards back together as one string.
    /// </summary>
    private static string Rest(IniLine line, int from) =>
        string.Join(
            ", ",
            line.Entries
                .Skip(from)
                .Select(e => string.Equals(e.Key, e.Value, StringComparison.Ordinal)
                    ? e.Key
                    : $"{e.Key}={e.Value}"));
}

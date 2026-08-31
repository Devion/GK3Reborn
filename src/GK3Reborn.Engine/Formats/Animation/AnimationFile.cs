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
/// <remarks>
/// GK3 paints a character's face from one bitmap and patches three regions of it at
/// runtime. <c>FACES.TXT</c> gives the pixel offset of each region per character, which is
/// the only place the geometry of a face is written down: the model has a single
/// <c>&lt;code&gt;_FACE</c> texture and no separate mouth, brow or eyelid to move.
/// </remarks>
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
/// <remarks>
/// A walk clip carries three or four of these to a stride, and what one sounds like is not
/// in the animation: the floor underfoot and the character's shoes decide, through
/// <c>FLOORMAP.TXT</c> and <c>FOOTSTEPS.TXT</c>. See <c>Game.Actors.Footsteps</c>.
/// </remarks>
public readonly record struct AnimationStep(int Frame, string Actor, bool Scuff);

/// <summary>A line of recorded speech an animation starts part-way through itself.</summary>
/// <param name="Frame">Which frame it begins on.</param>
/// <param name="Plate">
/// The licence plate of the line, as the file writes it — usually with the language letter
/// already on the front, which is what tells this apart from the plate a script gives.
/// </param>
/// <remarks>
/// <para>
/// The <em>moments</em> are what carry these: a scripted beat that belongs to nobody in
/// particular and speaks for itself. <c>StartMom("coffeepot")</c> is the dining room's
/// spit take, and both of the lines around it — Gabriel's "Mosely? Is that YOU?" and
/// Mosely's reply — are nodes in the moment rather than calls in the script, because the
/// timing is the animation's and not the story's.
/// </para>
/// <para>
/// Fifty of them, in 36 of the game's 39 moments. Read past, the beat plays in mime.
/// </para>
/// </remarks>
public readonly record struct AnimationDialogue(int Frame, string Plate);

/// <summary>A camera an animation puts the view on part-way through itself.</summary>
/// <param name="Frame">Which frame it cuts on.</param>
/// <param name="Camera">The camera's name, as the scene names it.</param>
/// <param name="Glide">Whether the view travels there rather than cutting.</param>
/// <remarks>
/// A moment frames itself. The spit take cuts to <c>VIEW_OF_SPIT</c> three frames before
/// the coffee leaves Gabriel's mouth, and the handshake in front of the Lady Howard's
/// door cuts between four cameras across its 600 frames. Eighteen of these in the corpus,
/// all in moments.
/// </remarks>
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
/// <remarks>
/// <para>
/// These are what the <em>music</em> hangs on. Eighty-one of them across the corpus and 79
/// are inside a line of dialogue's own <c>.YAK</c>: a line is the clock the score is cut
/// against, so the fight music comes up under the sentence that starts the fight rather
/// than a beat before or after it. <c>E01KED3S4U6</c> — "Yes, they dropped Grace at the
/// hotel and took off. But I'm afraid I have bad news." — stops the lobby's soundtrack at
/// frame 40 and starts <c>FightDrone.STK</c> at 50, in the middle of the word.
/// </para>
/// <para>
/// The other two are in moments, where <see cref="Game.SceneUpdate"/> schedules them
/// against the animation's own clock instead.
/// </para>
/// </remarks>
public readonly record struct AnimationMusic(
    int Frame, string? Track, bool Stop, bool Looping = true);

/// <summary>A texture an animation swaps part-way through.</summary>
/// <param name="Frame">Which frame it changes on.</param>
/// <param name="Model">The model whose surface it is.</param>
/// <param name="Mesh">Which mesh group.</param>
/// <param name="Submesh">Which submesh within it.</param>
/// <param name="Texture">What to paint it with.</param>
/// <remarks>
/// 168 of the corpus's animations carry an <c>[MTEXTURES]</c> section, and they are the
/// things in the game that change what they show rather than where they are: Larry's alarm
/// clock counting, a face on a monitor, a sign that lights. The node names a mesh group and
/// a submesh rather than a texture to replace, because that is what a modeller knows.
/// </remarks>
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
/// <remarks>
/// 198 lines across 78 of the corpus's animations, and they are the room changing rather
/// than a thing in it: the bar's dance floor cycling through three checker patterns, the
/// view through the lobby window gaining a parked van, the light coming on in Grace's
/// office. Distinct from <see cref="AnimationTexture"/>, which addresses a mesh group of a
/// model the scene loaded from a file of its own.
/// </remarks>
public readonly record struct AnimationSceneTexture(
    int Frame, string Scene, string ObjectName, string Texture);

/// <summary>A part of the room an animation shows or hides part-way through.</summary>
/// <param name="Frame">Which frame it changes on.</param>
/// <param name="Scene">The scene asset the line was authored against.</param>
/// <param name="ObjectName">The room object to show or hide.</param>
/// <param name="Visible">Whether it is drawn from this frame on.</param>
/// <remarks>
/// The room's counterpart of <see cref="AnimationVisibility"/>, and rare: five lines in one
/// animation. Read all the same, because the alternative is a section the parser walks past
/// in silence.
/// </remarks>
public readonly record struct AnimationSceneVisibility(
    int Frame, string Scene, string ObjectName, bool Visible);

/// <summary>A model an animation shows or hides part-way through.</summary>
/// <param name="Frame">Which frame it changes on.</param>
/// <param name="Model">The model's own name, as the scene placed it.</param>
/// <param name="Visible">Whether it is drawn from this frame on.</param>
/// <param name="Mesh">Which mesh group, or -1 for the whole model.</param>
/// <param name="Submesh">Which submesh within it, or -1 for all of them.</param>
/// <remarks>
/// <para>
/// 208 of the corpus's animations carry an <c>[MVISIBILITY]</c> section, and it is how a
/// character who is not in the room walks into it. <c>EmlRc1ExitLobby</c> is the plain
/// case: Emilio is hidden until the moment he opens the hotel door, and frame 0 of the
/// animation that swings the door is what turns him on.
/// </para>
/// <para>
/// Without it the door still swings and the door still makes its noise, because those are
/// an <c>[ACTIONS]</c> clip and a <c>[SOUNDS]</c> cue — so the failure looks like a door
/// opening by itself rather than like a missing person.
/// </para>
/// </remarks>
public readonly record struct AnimationVisibility(
    int Frame, string Model, bool Visible, int Mesh = -1, int Submesh = -1);

/// <summary>Where an absolute animation puts the thing it moves.</summary>
/// <param name="Position">The spot, in world space.</param>
/// <param name="Heading">Which way it faces there, in radians about the vertical.</param>
/// <remarks>
/// <para>
/// An <c>[ACTIONS]</c> line may carry eight numbers after the clip's name: an offset and
/// heading from the actor to the model, then a second pair from the world to the model. The
/// spot is the second offset plus the first <em>rotated</em> by the second heading, and the
/// facing is the difference of the two headings. That is as strange as it sounds and it is
/// what the original computes.
/// </para>
/// <para>
/// 502 of the corpus's 6,040 action lines carry them — 8.3%. The other 92% place nothing
/// and mean "play this where the model is standing".
/// </para>
/// </remarks>
public readonly record struct AnimationPlacement(Vector3 Position, float Heading);

/// <summary>
/// Reader for GK3's animations.
/// </summary>
/// <remarks>
/// <para>
/// 6,831 <c>.ANM</c> and 7,403 <c>.YAK</c> files, the largest asset family in the game and
/// the same format: an INI file whose <c>[HEADER]</c> is a frame count, and whose sections
/// list what happens on which frame — vertex animations to start, sounds to play, lines to
/// speak.
/// </para>
/// <para>
/// A YAK is a line of dialogue. <c>StartVoiceOver("1LLJ644QR1", 1)</c> names one: the last
/// character is a sequence number and the rest is the plate, so a voice-over of several
/// lines is several YAKs in a row. That makes this the thing that decides how long a
/// conversation takes — 20,709 of the corpus's 26,552 action-script statements are a
/// waited <c>StartVoiceOver</c>, and until something could say how long one lasts, every
/// one of them was over the instant it began.
/// </para>
/// <para>
/// Reading is not playing. The frames here are a schedule; running one needs the vertex
/// animation format and an audio device, neither of which exists. What it does give is
/// duration, which is what a script waiting on one needs to know.
/// </para>
/// </remarks>
public sealed class AnimationFile
{
    /// <summary>How many frames a second an animation runs at, unless it says otherwise.</summary>
    /// <remarks>
    /// Fifteen, from G-Engine's <c>Animation::mFramesPerSecond</c>. Nothing in the files
    /// says so, which is worth knowing: a reader that assumed thirty would make every
    /// line of dialogue in the game half as long as it is.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <see cref="FramesPerSecond"/> unless an <c>[OPTIONS]</c> line says otherwise, which
    /// thirty of the corpus's animations do — anywhere from 5 to 580. They are the ones
    /// whose timing is nothing like the rest: a fan, a flicker, a clock.
    /// </para>
    /// <para>
    /// The option carries a frame number, so in principle the rate may change part-way
    /// through. One animation in the game does that and nothing appears to play it, so the
    /// last rate named wins for the whole clip, as the reference implementation also does.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The sharpest signal in an animation file that it is a <em>scene</em> — something
    /// with music and engine noise that happens over time — rather than a statement about
    /// where something rests. Nine actor declarations in the corpus open with one, and all
    /// nine are somebody arriving in a vehicle.
    /// </remarks>
    public bool StartsSoundtrack { get; init; }

    /// <summary>
    /// Whether this animation is something that happens rather than a pose.
    /// </summary>
    /// <remarks>
    /// A pose says where a thing rests and is sampled at its first frame; a performance is
    /// played. Told apart by the soundtrack, because that is the one thing in the file that
    /// only a performance has.
    /// </remarks>
    public bool IsPerformance => StartsSoundtrack;

    /// <summary>The lines it speaks, in file order.</summary>
    public IReadOnlyList<AnimationCaption> Captions { get; }

    /// <summary>The recorded lines it starts as it runs, in file order.</summary>
    /// <remarks>
    /// Distinct from <see cref="Captions"/>, which is what a line of dialogue says about
    /// <em>itself</em>. This is one animation asking for another one's line, and it is how
    /// a moment speaks — see <see cref="AnimationDialogue"/>.
    /// </remarks>
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
    /// <remarks>
    /// 98,410 of these in the corpus and they are the whole of GK3's lip sync: one shape
    /// out of eight, on the frames the mouth changes. A line of dialogue is a <c>.YAK</c>
    /// whose sound is the recording and whose <c>LIPSYNCH</c> nodes are what the face does
    /// while it plays, which is why they are read from the same file rather than derived
    /// from the audio.
    /// </remarks>
    public IReadOnlyList<AnimationMouth> Mouths { get; }

    /// <summary>The patches it lays over faces and takes off again, in frame order.</summary>
    /// <remarks>
    /// Brows, blinks and set mouths. A blink is one of these and nothing else:
    /// <c>gabblink</c> is four frames of eyelid textures, which is why blinking needs no
    /// geometry and no separate system.
    /// </remarks>
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
    /// <remarks>
    /// The corpus writes it eight ways between them — <c>on</c>, <c>ON</c>, <c>ON</c> with a
    /// leading space — so this is a case-insensitive comparison on the trimmed text rather
    /// than anything cleverer. Anything that is not recognisably "on" is off, which is the
    /// safe way round: a model wrongly hidden is a missing person, and a model wrongly shown
    /// is a person standing in a wall.
    /// </remarks>
    private static bool Switched(string value) =>
        value.Trim() is { Length: > 0 } text &&
        (text.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("1", StringComparison.Ordinal) ||
         text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("YES", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads an action line's absolute placement, if it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>&lt;frame&gt;,&lt;clip&gt;,x1,y1,z1,angle1,x2,y2,z2,angle2</c>. The first offset
    /// goes actor-to-model and is wanted the other way round, so it is <b>negated</b>; and
    /// <b>y and z are swapped</b> in both, because the assets came out of Maya. Both quirks
    /// are the original's and reproducing them is the difference between a character
    /// standing on the floor and one standing in a wall.
    /// </para>
    /// <para>
    /// <b>Carrying the numbers is what makes a clip absolute, not what the numbers say.</b>
    /// A line written with eight zeros is an absolute clip whose offset happens to be
    /// nothing — it plays at the coordinates it was authored at — and a line written with no
    /// numbers at all is a relative one that plays wherever its model is standing. The
    /// corpus splits three ways: 4,984 lines carry no numbers, <b>3,931 carry eight
    /// zeros</b>, and 502 carry a real offset.
    /// </para>
    /// <para>
    /// Treating the eight zeros as "no placement" collapses two fifths of the corpus into
    /// the wrong half. It is invisible on props, which are placed by the identity either
    /// way, and it moves every scripted set piece a character performs to wherever that
    /// character happens to be standing. Mosely reads his newspaper in the hotel dining
    /// room through one of these: the paper is a prop and stayed on the table, and he was
    /// corrected onto his model's resting place out beyond the room.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// Every section states how many entries follow and then lists them. The count is
    /// ignored in favour of the lines actually present, which is what the original does and
    /// what survives a file whose count is wrong.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Two forms, and the rarer one is the one that reads like documentation.
    /// <c>SpeakerCaption</c> carries everything on one line — end frame, noun, text — and
    /// occurs 211 times, in the long cutscenes. The ordinary form is a <c>SPEAKER</c> node
    /// naming who is talking followed by a <c>CAPTION</c> node with what they say, and that
    /// is 7,380 of the game's lines. A reader that handles only the first understands three
    /// percent of the dialogue.
    /// </para>
    /// <para>
    /// A caption is a sentence and contains commas, so everything past the fixed fields is
    /// put back together rather than taken as more fields.
    /// </para>
    /// <para>
    /// <c>LIPSYNCH</c> is 98,410 of the corpus's nodes — a mouth shape on each frame the
    /// mouth changes — and <c>FACETEX</c>/<c>UNFACETEX</c> another 1,268, which are the
    /// brows and the blinks. All three name a region of a character's face and a bitmap to
    /// put there; see <see cref="FacePart"/>.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// One letter: <c>M</c> mouth, <c>E</c> eyelids, <c>H</c> forehead. Two of the corpus's
    /// nodes leave it off entirely and both name a mouth bitmap, so a missing letter means
    /// the mouth.
    /// </remarks>
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
    /// <remarks>
    /// The reader repeats a bare keyword as its own value, because the files that need it
    /// rely on the value never being empty. A caption is a sentence rather than a keyword,
    /// so putting it back means writing the key alone unless the two actually differ —
    /// otherwise every line of dialogue in the game is said twice.
    /// </remarks>
    private static string Rest(IniLine line, int from) =>
        string.Join(
            ",",
            line.Entries
                .Skip(from)
                .Select(e => string.Equals(e.Key, e.Value, StringComparison.Ordinal)
                    ? e.Key
                    : $"{e.Key}={e.Value}"));
}

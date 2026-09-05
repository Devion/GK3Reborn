using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// The faces in a room, and what each of them is doing.
/// </summary>
/// <remarks>
/// <para>
/// GK3's characters have no facial geometry. A head is one mesh wearing one bitmap, and
/// every expression the game has — a word, a blink, a raised brow — is a small picture
/// pasted into a copy of that bitmap while the game runs. <c>FACES.TXT</c> says where each
/// region goes; the animations say which picture and when.
/// </para>
/// <para>
/// So this is a compositor with a clock. Three regions are tracked per character — mouth,
/// eyelids, forehead — and whenever any of them changes, the four bitmaps are pasted
/// together into a face, given to the renderer under a name of its own, and the character's
/// head is repainted with it. Compositions are cached by what they are made of, because a
/// sentence comes back to the same eight mouth shapes over and over and a blink is the same
/// two pictures every time.
/// </para>
/// <para>
/// Two things drive it. <b>Lip sync</b> comes from the line being spoken: a <c>.YAK</c>
/// carries the recording in its <c>[SOUNDS]</c> and the mouth shapes in its <c>[GK3]</c>,
/// against the same frame numbers, so the mouth follows the words by construction rather
/// than by analysis. <b>Blinking</b> runs on its own: every character has two blink
/// animations and a frequency in <c>FACES.TXT</c>, and one is drawn at random every five to
/// twelve seconds. Without that, a room full of people stares.
/// </para>
/// </remarks>
public sealed class Faces
{
    private readonly FaceLibrary _library;

    /// <summary>The three letters a character's own bitmaps and animations are named after.</summary>
    /// <param name="model">Their model name, which may carry a clothing variant with it.</param>
    /// <returns>The code, or null when nothing in FACES.TXT is about them.</returns>
    /// <remarks>
    /// Not always the model name. The lobby places Simone as <c>sim_</c> and her face is
    /// listed under <c>SIM</c>, so an animation built from the model name — <c>sim_sleepon</c>
    /// — names nothing at all. It is the same code the mouth and eyelid bitmaps use.
    /// </remarks>
    public string? CodeFor(string model) =>
        _library.Of(model)?.Identifier;

    /// <summary>
    /// Whose artwork a character's face is composed from, where it is not their own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sides are the three-letter code <c>FACES.TXT</c> lists a character under, and
    /// the substitute has to be one the file describes and whose bitmaps are in the
    /// archives — a name with nothing behind it leaves the face as its own rather than
    /// blank. The face is still <em>painted onto</em> the character's own texture, so the
    /// model itself is untouched: it is the picture that changes and not the person.
    /// </para>
    /// <para>
    /// There is one of these, and it is the moustache. <c>GA3</c> is the game's own
    /// moustached Gabriel — the disguised actor standing offstage in the moped shop — and
    /// its face bitmap is Gabriel's own with a moustache painted into it, on the same
    /// layout, with a matching mouth for all eight lip-sync shapes and its own blinks. See
    /// <see cref="Assists.MoustachedFace"/>.
    /// </para>
    /// <para>
    /// Set before anybody is added, because a face is composed the moment it is taken on.
    /// Empty is the ordinary case and the game as it shipped.
    /// </para>
    /// </remarks>
    public IDictionary<string, string> ComposedFrom { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly GameArchives _archives;
    private readonly AnimationLibrary _animations;
    private readonly ISceneSink _geometry;
    private readonly DeterministicRandom _random;

    private readonly Dictionary<string, Face> _faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Face> _order = [];
    private readonly Dictionary<string, DecodedImage?> _bitmaps =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _composed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the faces for one room.</summary>
    /// <param name="library">How each character's face is put together.</param>
    /// <param name="archives">Where the bitmaps come from.</param>
    /// <param name="animations">Where the blink animations come from.</param>
    /// <param name="geometry">Where the faces are drawn.</param>
    /// <param name="seed">
    /// What the blink timings are drawn from. Fixed rather than taken from the clock, so
    /// that two runs of the same scene blink at the same moments — <c>ADR 0004</c> again.
    /// </param>
    public Faces(
        FaceLibrary library,
        GameArchives archives,
        AnimationLibrary animations,
        ISceneSink geometry,
        ulong seed = 0x9E3779B97F4A7C15)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(geometry);

        _library = library;
        _archives = archives;
        _animations = animations;
        _geometry = geometry;
        _random = new DeterministicRandom(seed);
    }

    /// <summary>How many people in the room have a face this can move.</summary>
    public int Count => _order.Count;

    /// <summary>How many distinct faces have been composed and uploaded.</summary>
    /// <remarks>
    /// A measure of what the effect costs: eight mouth shapes and a couple of blink
    /// states make a few dozen over a conversation, each a copy of a 256-square bitmap.
    /// </remarks>
    public int Composed => _composed.Count;

    /// <summary>Whether anybody is talking.</summary>
    public bool Talking => _order.Exists(f => f.Line is not null);

    /// <summary>
    /// Whose line is running, by model name, or null when nobody is speaking.
    /// </summary>
    /// <remarks>
    /// The line names its own actor, so this is known without anybody being told. It is
    /// what decides whether a character runs their talking script or their listening one:
    /// lip sync is half of speaking and the gestures are the other half.
    /// </remarks>
    public string? Speaking =>
        _order.Find(f => f.Line is not null)?.Model.Name;

    /// <summary>Takes charge of a character's face, if they have one.</summary>
    /// <param name="model">The character, as the scene placed them.</param>
    /// <returns>True when the face was taken on.</returns>
    /// <remarks>
    /// Three things have to line up: <c>FACES.TXT</c> has to describe the character, the
    /// archives have to hold their face bitmap, and the model actually has to be painted
    /// with it. Anything else is a model that only looks like a person — a portrait, a
    /// waxwork, the demon — and is left alone.
    /// </remarks>
    public bool Add(PlacedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.Placement.Exists ||
            _library.Of(model.Name) is not { } config ||
            Bitmap(config.FaceTexture) is null ||
            !Wears(model, config.FaceTexture))
        {
            return false;
        }

        // Their own artwork unless the player has asked for somebody else's, which is
        // decided once here rather than looked up on every frame the face changes.
        FaceConfig artwork = Artwork(config);

        var face = new Face(model, config, artwork)
        {
            Mouth = artwork.RestingTexture(FacePart.Mouth),
            Eyelids = artwork.RestingTexture(FacePart.Eyelids),
            Forehead = artwork.RestingTexture(FacePart.Forehead),
        };

        _order.Add(face);
        _faces[model.Name] = face;

        if (model.Noun is { Length: > 0 } noun)
        {
            _faces[noun] = face;
        }

        // The resting composite straight away. A face left as its own bitmap has no
        // eyelids and no brow on it at all, because those are pasted on and never baked.
        face.Blink = Wait(artwork);
        Paint(face);

        return true;
    }

    /// <summary>Starts a line of dialogue, so the mouth follows it.</summary>
    /// <param name="line">
    /// The animation carrying the line, or null when nothing is being said. Each of its
    /// <c>LIPSYNCH</c> nodes names the actor it belongs to, so nothing here has to be told
    /// who is speaking.
    /// </param>
    /// <remarks>
    /// Anything a previous line left on a mouth is put back first. A line cut short —
    /// which is what starting a conversation over does — otherwise leaves the speaker
    /// holding whatever shape they were on, and a face frozen mid-word is worse than one
    /// that never moved.
    /// </remarks>
    public void Say(AnimationFile? line)
    {
        foreach (Face face in _order)
        {
            if (face.Line is not null)
            {
                face.Line = null;
                face.Said = 0;
                face.Mouth = face.Config.RestingTexture(FacePart.Mouth);
                Paint(face);
            }
        }

        if (line is null)
        {
            return;
        }

        foreach (AnimationMouth cue in line.Mouths)
        {
            if (_faces.TryGetValue(cue.Actor, out Face? face))
            {
                face.Line = line;
                face.Said = 0;
            }
        }
    }

    /// <summary>Paints a region of somebody's face, or puts it back as it was.</summary>
    /// <param name="actor">Their noun or their model name.</param>
    /// <param name="part">Which region.</param>
    /// <param name="texture">The bitmap, or null to restore.</param>
    /// <returns>True when there was a face to paint.</returns>
    public bool Paint(string actor, FacePart part, string? texture)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!_faces.TryGetValue(actor, out Face? face))
        {
            return false;
        }

        Set(face, part, texture);
        Paint(face);
        return true;
    }

    /// <summary>What a region of somebody's face is painted with at the moment.</summary>
    /// <param name="actor">Their noun or their model name.</param>
    /// <param name="part">Which region.</param>
    /// <returns>The bitmap's name, or null when there is no such face here.</returns>
    /// <remarks>
    /// The read side of <see cref="Paint(string, FacePart, string?)"/>. A face's regions are
    /// worn rather than momentary — a mood holds one until its "off" animation takes it back
    /// — so what is on one is state, and state nothing can read is state nothing can check.
    /// </remarks>
    public string? Wearing(string actor, FacePart part)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return _faces.TryGetValue(actor, out Face? face) ? Worn(face, part) : null;
    }

    /// <summary>Lets time pass: mouths follow what is being said, and eyes blink.</summary>
    /// <param name="seconds">How much time.</param>
    public void Advance(double seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        foreach (Face face in _order)
        {
            Speak(face, seconds);
            Blink(face, seconds);
        }
    }

    /// <summary>Moves a mouth to wherever the line being spoken has got to.</summary>
    /// <remarks>
    /// The cues are absolute frame numbers from the start of the line, so the last one at
    /// or before now is the shape — not the next one along. A frame that took longer than a
    /// fifteenth of a second therefore skips shapes rather than falling behind the sound,
    /// which is the whole reason the mouth is driven by the clock and not by a cursor.
    /// </remarks>
    private void Speak(Face face, double seconds)
    {
        if (face.Line is not { } line)
        {
            return;
        }

        face.Said += seconds;

        double frame = face.Said * AnimationFile.FramesPerSecond;
        string? shape = null;

        foreach (AnimationMouth cue in line.Mouths)
        {
            if (cue.Frame > frame)
            {
                break;
            }

            if (_faces.TryGetValue(cue.Actor, out Face? whose) && ReferenceEquals(whose, face))
            {
                shape = cue.Mouth;
            }
        }

        // Past the end of the line, the mouth closes and stays closed. The audio decides
        // when a line is over, not this, so running out of cues is not the same as being
        // finished — a pause between words has no cues in it either.
        if (frame > line.FrameCount)
        {
            face.Line = null;
            shape = null;
        }

        string wanted = shape is { Length: > 0 } said
            ? face.Config.MouthTexture(said)
            : face.Config.RestingTexture(FacePart.Mouth);

        if (!face.Mouth.Equals(wanted, StringComparison.OrdinalIgnoreCase))
        {
            face.Mouth = wanted;
            Paint(face);
        }
    }

    /// <summary>
    /// Starts an expression, if it is one and if its subject is in the room.
    /// </summary>
    /// <param name="animation">The animation a script asked for.</param>
    /// <returns>True when somebody's face took it on.</returns>
    /// <remarks>
    /// Most of GK3's expressions are animations with no clip in them at all —
    /// <c>ABEANGRY</c> is two frames of brow bitmaps and nothing else — so an animation
    /// that moves no geometry is not necessarily an animation that does nothing. It goes
    /// through the same path a blink does, because a blink <em>is</em> one of these.
    /// </remarks>
    public bool Perform(AnimationFile animation)
    {
        ArgumentNullException.ThrowIfNull(animation);

        bool taken = false;

        void Start(string actor)
        {
            if (_faces.TryGetValue(actor, out Face? face))
            {
                face.Playing = animation;
                face.Played = 0;
                taken = true;
            }
        }

        foreach (AnimationFace cue in animation.Faces)
        {
            Start(cue.Actor);
        }

        // Lip sync outside dialogue. 1,362 of the game's .ANM files carry LIPSYNCH nodes
        // of their own — Gabriel eating a sweet in the lobby is five of them — so a mouth
        // is not only moved by lines that have a recording behind them.
        foreach (AnimationMouth cue in animation.Mouths)
        {
            Start(cue.Actor);
        }

        return taken;
    }

    /// <summary>
    /// Runs whatever expression a face is wearing, and counts down to the next blink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A blink animation is nothing but <c>FACETEX</c> nodes on the eyelids — three or six
    /// frames of it — so playing one is walking its nodes by frame, exactly as lip sync
    /// walks its own, and exactly as a raised eyebrow does. One loop covers all three.
    /// </para>
    /// <para>
    /// <b>An expression that has run is not an expression taken off.</b> Every blink in the
    /// corpus ends on an <c>UNFACETEX</c> — <c>xxx_BLINK_01</c>, <c>_02</c>, <c>_01</c>,
    /// clear — which is the animation putting the eyelids back itself, and the same is true
    /// of the "off" half of every mood. So an animation reaching its last frame stops here
    /// and changes nothing: whatever its final node said is what the face keeps.
    /// </para>
    /// <para>
    /// That is not a nicety, because the moods are two animations and only the first of
    /// them runs when the mood goes on. <c>SIMSLEEPON.ANM</c> is one <c>FACETEX</c> holding
    /// Simone's eyelids at <c>SIM_BLINK_02</c> — shut — and no <c>UNFACETEX</c> at all,
    /// against <c>SIMSLEEPOFF.ANM</c>, which is the <c>UNFACETEX</c> on its own. Clearing
    /// the face when the "on" ended opened her eyes two frames after the lobby put her to
    /// sleep, and she then blinked through the small hours face down on the reception desk.
    /// </para>
    /// <para>
    /// The mouth is the exception, and only for lip sync. A <c>LIPSYNCH</c> shape has no
    /// node that puts it back — there is no <c>UNLIPSYNCH</c> — so a mouth left on the last
    /// shape of a word is a face frozen mid-word, and that one is restored.
    /// </para>
    /// </remarks>
    private void Blink(Face face, double seconds)
    {
        if (face.Playing is { } playing)
        {
            face.Played += seconds;

            double frame = face.Played * AnimationFile.FramesPerSecond;
            bool over = frame >= playing.FrameCount;

            if (over)
            {
                face.Playing = null;
                face.Blink = Wait(face.Config);
            }

            foreach (FacePart part in Parts)
            {
                // Only what this animation has actually said about this region, and only up
                // to now. A region it never mentions is somebody else's — a brow going up
                // must not put a held expression back, and a blink must not either.
                if (!Says(playing, part, over ? playing.FrameCount : frame,
                        out string? texture))
                {
                    // An animation may move a mouth two ways: a FACETEX naming a bitmap
                    // outright, or a LIPSYNCH naming one of the eight shapes. The shape
                    // needs the character's code in front of it; the bitmap does not.
                    if (part != FacePart.Mouth || LatestShape(playing, face, frame) is not
                        { } shape)
                    {
                        continue;
                    }

                    // And nothing puts a shape back, so the end of the word does.
                    texture = over ? null : face.Config.MouthTexture(shape);
                }

                // Otherwise the mouth is the one part an expression does not own outright:
                // a line of dialogue may be being said with it, and a brow going up should
                // not stop the talking.
                if (part == FacePart.Mouth && face.Line is not null && texture is null)
                {
                    continue;
                }

                Set(face, part, texture);
            }

            Paint(face);
            return;
        }

        face.Blink -= seconds;

        if (face.Blink > 0 || face.Config.Blinks.Count == 0)
        {
            return;
        }

        // Somebody wearing their eyelids does not blink. A blink would run its own three
        // pictures over whatever is held there and then clear it, so a sleeping character
        // blinking is also a sleeping character whose eyes are open afterwards — one fault
        // and not two. The timer keeps running, so the moment the mood comes off and the
        // eyelids are their own again, blinking picks up where it was.
        if (!Rested(face, FacePart.Eyelids))
        {
            face.Blink = Wait(face.Config);
            return;
        }

        face.Playing = _animations.Read(Choose(face.Config.Blinks));
        face.Played = 0;

        // An animation that is not there must not leave the character waiting for ever on
        // a blink that cannot happen.
        if (face.Playing is null)
        {
            face.Blink = Wait(face.Config);
        }
    }

    /// <summary>The three regions, in the order they are pasted on.</summary>
    private static readonly FacePart[] Parts =
        [FacePart.Forehead, FacePart.Eyelids, FacePart.Mouth];

    /// <summary>Whether a region is the character's own rather than something put on it.</summary>
    /// <remarks>
    /// The resting bitmaps are named by convention off the character's three letters, so
    /// "nothing is painted here" is a comparison and does not need remembering. Which is
    /// what makes the sleeping test above cost nothing to keep true: the "off" animation
    /// puts the eyelids back by name and this reads as rested again straight away.
    /// </remarks>
    private static bool Rested(Face face, FacePart part) =>
        Worn(face, part).Equals(
            face.Config.RestingTexture(part), StringComparison.OrdinalIgnoreCase);

    /// <summary>What a region is painted with at the moment.</summary>
    private static string Worn(Face face, FacePart part) => part switch
    {
        FacePart.Eyelids => face.Eyelids,
        FacePart.Forehead => face.Forehead,
        _ => face.Mouth,
    };

    /// <summary>The last thing an animation put on a region at or before a moment.</summary>
    /// <param name="animation">The animation being walked.</param>
    /// <param name="part">Which region.</param>
    /// <param name="frame">How far in, in frames.</param>
    /// <param name="texture">Its bitmap, or null where the node was an <c>UNFACETEX</c>.</param>
    /// <returns>Whether the animation says anything about this region by now at all.</returns>
    /// <remarks>
    /// The return and the out are two different questions and both matter. A region an
    /// animation never touches is left as it was; a region it clears with an
    /// <c>UNFACETEX</c> goes back to the character's own. Answering both with a null
    /// texture is what made a blink put a mood back.
    /// </remarks>
    private static bool Says(
        AnimationFile animation, FacePart part, double frame, out string? texture)
    {
        bool found = false;
        texture = null;

        foreach (AnimationFace cue in animation.Faces)
        {
            if (cue.Frame > frame)
            {
                break;
            }

            if (cue.Part == part)
            {
                found = true;
                texture = cue.Texture;
            }
        }

        return found;
    }

    /// <summary>The last mouth shape an animation asked for at or before a moment.</summary>
    /// <remarks>
    /// Only this character's. An animation may carry the lip sync for a whole scene — a
    /// cutscene <c>.YAK</c> is everybody's lines at once — and reading somebody else's
    /// cues would put their words in this mouth.
    /// </remarks>
    private string? LatestShape(AnimationFile animation, Face face, double frame)
    {
        string? found = null;

        foreach (AnimationMouth cue in animation.Mouths)
        {
            if (cue.Frame > frame)
            {
                break;
            }

            if (_faces.TryGetValue(cue.Actor, out Face? whose) && ReferenceEquals(whose, face))
            {
                found = cue.Mouth;
            }
        }

        return found;
    }

    /// <summary>Records what a region is wearing, resting when nothing is named.</summary>
    private static void Set(Face face, FacePart part, string? texture)
    {
        string wanted = texture is { Length: > 0 } named
            ? named.ToUpperInvariant()
            : face.Config.RestingTexture(part);

        switch (part)
        {
            case FacePart.Eyelids:
                face.Eyelids = wanted;
                break;

            case FacePart.Forehead:
                face.Forehead = wanted;
                break;

            default:
                face.Mouth = wanted;
                break;
        }
    }

    /// <summary>Picks a blink animation by the weights the file gives them.</summary>
    private string Choose(IReadOnlyList<BlinkChoice> choices)
    {
        int total = 0;

        foreach (BlinkChoice choice in choices)
        {
            total += choice.Weight;
        }

        int draw = _random.NextInt32(0, Math.Max(1, total));

        foreach (BlinkChoice choice in choices)
        {
            draw -= choice.Weight;

            if (draw < 0)
            {
                return choice.Animation;
            }
        }

        return choices[0].Animation;
    }

    /// <summary>How long until the next blink, somewhere in the character's own range.</summary>
    private double Wait(FaceConfig config) =>
        config.BlinkFrom + (_random.NextDouble() * Math.Max(0, config.BlinkTo - config.BlinkFrom));

    /// <summary>Composes a face from its parts and puts it on the character's head.</summary>
    /// <remarks>
    /// Order matters and is the order the regions overlap in: the forehead sits above the
    /// eyes and its bitmap reaches down over them, so the eyelids go on after it. A
    /// composition that has been made before is only named again — the pictures are already
    /// on the device.
    /// </remarks>
    private void Paint(Face face)
    {
        string name = $"__FACE:{face.Config.Identifier}:{face.Forehead}:{face.Eyelids}:{face.Mouth}";

        if (_composed.Add(name))
        {
            if (Bitmap(face.Config.FaceTexture) is not { } start)
            {
                return;
            }

            byte[] pixels = [.. start.Pixels];
            var composed = new DecodedImage(
                start.Width, start.Height, pixels, start.HasAlpha, "face");

            Over(composed, Painted(face, face.Forehead), face.Config.ForeheadOffset, null);
            // The eyelids' alpha channel belongs to the resting eyelids and to nothing
            // else. It is a hole cut where the eye opening is, so that the eyeball
            // underneath shows through a lid that is open; laying it over a lid an
            // animation has painted punches that same hole through a shut eye, and the
            // open eyes baked into the face bitmap come back up through it. So a blink
            // half closed, and Simone asleep on the reception desk with her eyes open.
            Over(
                composed,
                Painted(face, face.Eyelids),
                face.Config.EyelidsOffset,
                Rested(face, FacePart.Eyelids) ? face.Config.EyelidsAlpha : null);
            Over(composed, Painted(face, face.Mouth), face.Config.MouthOffset, null);

            _geometry.AddTexture(name, composed);
        }

        // Onto the texture the model is actually painted with, which is the character's
        // own even when the picture was made out of somebody else's bitmaps.
        _geometry.Repaint(face.Model.Placement, face.Own.FaceTexture, name);
    }

    /// <summary>
    /// Composes every face again, after a change to what they are composed from.
    /// </summary>
    /// <returns>How many faces changed.</returns>
    /// <remarks>
    /// <para>
    /// A face is composed when it is taken on, which is once a room. Without this, changing
    /// <see cref="ComposedFrom"/> from the pause menu would wait for the next door — and a
    /// switch the player cannot see working is a switch they will assume is broken.
    /// </para>
    /// <para>
    /// The three regions go back to resting, because what is on them is named for whoever's
    /// artwork was in use a moment ago. A mouth mid-word is put back on the next cue, which
    /// is a fifteenth of a second away.
    /// </para>
    /// </remarks>
    public int Recompose()
    {
        int changed = 0;

        foreach (Face face in _order)
        {
            FaceConfig artwork = Artwork(face.Own);

            if (ReferenceEquals(artwork, face.Config))
            {
                continue;
            }

            face.Config = artwork;
            face.Mouth = artwork.RestingTexture(FacePart.Mouth);
            face.Eyelids = artwork.RestingTexture(FacePart.Eyelids);
            face.Forehead = artwork.RestingTexture(FacePart.Forehead);

            Paint(face);
            changed++;
        }

        return changed;
    }

    /// <summary>Whose bitmaps a character's face is composed from.</summary>
    /// <remarks>
    /// Their own unless somebody has asked otherwise and the substitute is real: a code
    /// <c>FACES.TXT</c> does not describe, or one whose face bitmap is not in the archives,
    /// leaves the character looking like themselves rather than like nothing.
    /// </remarks>
    private FaceConfig Artwork(FaceConfig own) =>
        ComposedFrom.TryGetValue(own.Identifier, out string? other) &&
        _library.Of(other) is { } instead &&
        Bitmap(instead.FaceTexture) is not null
            ? instead
            : own;

    /// <summary>
    /// A patch's name under the artwork actually being used.
    /// </summary>
    /// <remarks>
    /// The three regions are usually named by the character's own code and resolved through
    /// their config, so they follow a substitution on their own. An animation is the
    /// exception: <c>GABSMILE.ANM</c> names <c>GAB_SMILE_01</c> outright, and pasting that
    /// onto a moustached Gabriel would shave him for the length of the smile. So a bitmap
    /// named for the face it was painted for is looked for under the artwork in use first,
    /// and kept as it is when there is no such picture.
    /// </remarks>
    private string Painted(Face face, string texture)
    {
        string own = face.Own.Identifier;

        if (ReferenceEquals(face.Own, face.Config) ||
            !texture.StartsWith(own + "_", StringComparison.OrdinalIgnoreCase))
        {
            return texture;
        }

        string instead = face.Config.Identifier + texture[own.Length..];

        return Bitmap(instead) is not null ? instead : texture;
    }

    /// <summary>Pastes one bitmap over another at a spot, honouring transparency.</summary>
    /// <param name="face">The face being built, which is written into.</param>
    /// <param name="texture">The patch.</param>
    /// <param name="at">Where its top left corner goes.</param>
    /// <param name="alpha">
    /// A bitmap saying how much of the patch to show, or null for all of it. The resting
    /// eyelids have one: they are a soft edge against the skin rather than a cut-out, and
    /// pasted without it they read as a strip of paint across the eyes.
    /// </param>
    /// <remarks>
    /// The patches are keyed rather than authored with an alpha channel — a forehead's
    /// corners are magenta — and the decoder has already turned that into transparency, so
    /// this is an ordinary blend. Anything hanging off the edge of the face is dropped: the
    /// offsets are the artists' and a bad one should paste a smaller picture, not wrap
    /// round to the far side of somebody's head.
    /// </remarks>
    private void Over(DecodedImage face, string texture, FaceSpot at, string? alpha)
    {
        if (Bitmap(texture) is not { } patch)
        {
            return;
        }

        Over(face, patch, at, alpha is { Length: > 0 } named ? Bitmap(named) : null);
    }

    /// <summary>The same, for a patch that is not in the archives under a name.</summary>
    private static void Over(DecodedImage face, DecodedImage patch, FaceSpot at, DecodedImage? mask)
    {
        for (int y = 0; y < patch.Height; y++)
        {
            int row = at.Y + y;

            if (row < 0 || row >= face.Height)
            {
                continue;
            }

            for (int x = 0; x < patch.Width; x++)
            {
                int column = at.X + x;

                if (column < 0 || column >= face.Width)
                {
                    continue;
                }

                int from = ((y * patch.Width) + x) * 4;
                int to = ((row * face.Width) + column) * 4;

                int over = patch.Pixels[from + 3];

                if (mask is { } soft && x < soft.Width && y < soft.Height)
                {
                    // Greyscale: the channels agree, so any one of them is the amount.
                    over = over * soft.Pixels[(((y * soft.Width) + x) * 4)] / 255;
                }

                if (over <= 0)
                {
                    continue;
                }

                for (int channel = 0; channel < 3; channel++)
                {
                    face.Pixels[to + channel] = (byte)(
                        ((patch.Pixels[from + channel] * over) +
                         (face.Pixels[to + channel] * (255 - over))) / 255);
                }
            }
        }
    }

    /// <summary>Reads and decodes a bitmap, once.</summary>
    /// <remarks>
    /// Misses are remembered as well as hits. A character whose brow bitmap is missing
    /// would otherwise be looked up in every archive on every frame their expression
    /// changed.
    /// </remarks>
    private DecodedImage? Bitmap(string name)
    {
        if (_bitmaps.TryGetValue(name, out DecodedImage? known))
        {
            return known;
        }

        DecodedImage? decoded = null;

        if (_archives.Read(name + ".BMP") is { } bytes && BitmapDecoder.CanDecode(bytes))
        {
            decoded = BitmapDecoder.Decode(bytes, name);
        }

        _bitmaps[name] = decoded;
        return decoded;
    }

    /// <summary>Whether a model is actually painted with a texture.</summary>
    private static bool Wears(PlacedModel model, string texture)
    {
        foreach (Formats.Models.ModMesh mesh in model.Model.Meshes)
        {
            foreach (Formats.Models.ModSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.TextureName.Equals(texture, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>One character's face, and what is currently on it.</summary>
    private sealed class Face(PlacedModel model, FaceConfig own, FaceConfig artwork)
    {
        public PlacedModel Model { get; } = model;

        /// <summary>The character as <c>FACES.TXT</c> lists them.</summary>
        /// <remarks>
        /// What the model is painted with, and so what the composition replaces. The same
        /// object as <see cref="Config"/> unless somebody has asked for another face's
        /// artwork.
        /// </remarks>
        public FaceConfig Own { get; } = own;

        /// <summary>Whose bitmaps and offsets the composition is made of.</summary>
        /// <remarks>
        /// Settable, because the player may change it in the pause menu without leaving the
        /// room. See <see cref="Faces.Recompose"/>.
        /// </remarks>
        public FaceConfig Config { get; set; } = artwork;

        public required string Mouth { get; set; }

        public required string Eyelids { get; set; }

        public required string Forehead { get; set; }

        /// <summary>The line they are saying, if they are saying one.</summary>
        public AnimationFile? Line { get; set; }

        /// <summary>How far into it they are, in seconds.</summary>
        public double Said { get; set; }

        /// <summary>Seconds until their next blink.</summary>
        public double Blink { get; set; }

        /// <summary>The expression they are in the middle of — a blink or a brow — if any.</summary>
        public AnimationFile? Playing { get; set; }

        /// <summary>How far into it they are, in seconds.</summary>
        public double Played { get; set; }
    }
}

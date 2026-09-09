// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;

namespace GK3Reborn.UI;

/// <summary>What the party is built out of.</summary>
/// <param name="Model">Reads a model by name, or null when the archives have none.</param>
/// <param name="Clips">The vertex animations.</param>
/// <param name="Animations">The animation files that name them.</param>
/// <param name="Texture">
/// Brings one texture into a scene by name, the way a room's are brought in, so the
/// dancers wear whatever the enhanced set has for them.
/// </param>
/// <param name="Sounds">The sound effects, or null for a silent party.</param>
/// <param name="Audio">What plays them, or null for a silent one.</param>
/// <param name="Wall">The title screen's wall, to hang behind the floor, or null for black.</param>
public sealed record DiscoPartyContent(
    Func<string, ModFile?> Model,
    ClipLibrary Clips,
    AnimationLibrary Animations,
    Action<ISceneSink, string> Texture,
    SoundLibrary? Sounds,
    IAudioBackend? Audio,
    DecodedImage? Wall)
{
    /// <summary>
    /// Which guests to invite, by model name, or null for everybody. For photographing
    /// one of them at a time.
    /// </summary>
    public IReadOnlySet<string>? Only { get; init; }
}

/// <summary>
/// The party behind the title screen once the player has spelled the word.
/// </summary>
/// <remarks>
/// <para>
/// The bar at Rennes-le-Château has a secret: with the eggs on, the bartender lowers a
/// mirror ball, the floor turns to checkers and he dances to <c>BarDisco</c>. This is that
/// secret let out onto the title screen, with everybody else in the game who was ever
/// given something silly to do invited along — Grace's splits, Jean's flips, Emilio's
/// clown routine, Gabriel's propeller — and the animals from the cemetery parade.
/// </para>
/// <para>
/// It is a real scene: the models out of the archives, their own clips driving them, in a
/// <see cref="SceneGeometry"/> the renderer draws under the menu's display list. Nothing is
/// pre-rendered. The room around them is the one thing that is not the game's: a checkered
/// floor and the title screen's own wall, lit by a ring of coloured lights that turn.
/// </para>
/// <para>
/// Nothing here reads the clock. The menu's loop hands it how long the last frame took.
/// </para>
/// </remarks>
public sealed class DiscoParty : IDisposable
{
    /// <summary>The music, once the ball is down.</summary>
    public const string Music = "BarDisco";

    /// <summary>How long a beat is, in seconds. The floor flashes on it.</summary>
    public const float BeatSeconds = 0.5f;

    /// <summary>Where the mirror ball hangs.</summary>
    private static readonly Vector3 BallSpot = new(0f, 175f, 0f);

    /// <summary>How far the floor reaches either side of the ball.</summary>
    private const float FloorHalfWidth = 700f;

    /// <summary>How far it reaches in front of and behind the ball.</summary>
    private const float FloorHalfDepth = 420f;

    /// <summary>How tall the wall behind it is.</summary>
    private const float WallHeight = 1400f;

    /// <summary>One checkered tile, in scene units.</summary>
    private const float Tile = 210f;

    /// <summary>The three floors the bar's own flash cycles.</summary>
    private static readonly string[] Checkers = ["checker_01", "checker_02", "checker_03"];

    /// <summary>What the floor is painted with when the party is built.</summary>
    private const string FloorTexture = "checker_01";

    /// <summary>What the wall is called on the device.</summary>
    private const string WallTexture = "menu_wall";

    /// <summary>How many lights turn over the floor.</summary>
    private const int Ring = 6;

    /// <summary>How often the light rig is re-laid, in seconds.</summary>
    /// <remarks>
    /// Laying a rig rebuilds the scene's light grid. Ten times a second is plenty for
    /// lights that take fifteen seconds to go round, and costs a tenth of what once a
    /// frame would.
    /// </remarks>
    private const float LightStep = 0.1f;

    private readonly DiscoPartyContent _content;
    private readonly SceneGeometry _geometry;
    private Action<string>? _log;
    private readonly List<Performer> _performers = [];
    private readonly List<(float At, Action What)> _programme = [];
    private readonly List<AuthoredLight> _lights = [];

    private ModelPlacement _floor = ModelPlacement.None;
    private AudioVoice _music = AudioVoice.None;
    private float _elapsed;
    private float _sinceLights = float.MaxValue;
    private int _checker;
    private int _beats;
    private bool _flashing;
    private bool _hushed;
    private bool _disposed;

    private DiscoParty(DiscoPartyContent content, SceneGeometry geometry)
    {
        _content = content;
        _geometry = geometry;
    }

    /// <summary>The scene, for the renderer.</summary>
    public SceneGeometry Geometry => _geometry;

    /// <summary>Where the scene reaches, for the light grid.</summary>
    public SceneExtent Extent => new(_geometry.Minimum, _geometry.Maximum);

    /// <summary>The lights as they stand now.</summary>
    public IReadOnlyList<AuthoredLight> Lights => _lights;

    /// <summary>
    /// Whether the lights have moved since this was last read. Reading it clears it.
    /// </summary>
    public bool LightsMoved
    {
        get
        {
            bool moved = _lightsMoved;
            _lightsMoved = false;
            return moved;
        }
    }

    private bool _lightsMoved;

    /// <summary>How long the party has been going, in seconds.</summary>
    public float Elapsed => _elapsed;

    /// <summary>Whether the ball is down and the music is on.</summary>
    public bool Started => _flashing;

    /// <summary>How many beats have gone by since the music started.</summary>
    public int Beats => _beats;

    /// <summary>Where in the beat the party is, from nought to one.</summary>
    public float Beat => _flashing ? (_elapsed % BeatSeconds) / BeatSeconds : 0f;

    /// <summary>What the bartender says, and when, or null when he has not yet.</summary>
    public string? Caption { get; private set; }

    /// <summary>How long the caption has been up.</summary>
    public float CaptionAge { get; private set; }

    /// <summary>Who came to the party, by model name.</summary>
    public IReadOnlyList<string> Guests => [.. _performers.Select(p => p.Name)];

    /// <summary>Where the party is looked at from.</summary>
    /// <remarks>
    /// Low and a little back, so the ball hangs in the top of the view and the floor runs
    /// off the bottom under the menu's rows. It sways: not a camera move, just enough that
    /// the picture is never quite still.
    /// </remarks>
    public Camera Camera => new()
    {
        // Straight at the ball, which puts it in the middle of the window and clear of
        // the statue: the statue stands over the left third, and the guests keep to the
        // right of the ball for the same reason.
        Position = new Vector3(
            70f * MathF.Sin(_elapsed / 9f),
            130f + (10f * MathF.Sin(_elapsed / 5f)),
            -430f),
        Target = new Vector3(0f, 85f, 40f),
        Up = Vector3.UnitY,
        FieldOfView = MathF.PI / 3.4f,
        NearPlane = 5f,
        FarPlane = 4000f,
        LightDirection = Vector3.Normalize(new Vector3(0.2f, -0.8f, 0.5f)),
        Background = Vector3.Zero,
    };

    /// <summary>Builds the party.</summary>
    /// <param name="content">What to build it out of.</param>
    /// <param name="renderer">The device the scene goes on.</param>
    /// <param name="log">Where to say what came and what did not.</param>
    /// <returns>The party, or null when the archives have not got the ball.</returns>
    public static DiscoParty? Build(
        DiscoPartyContent content, IRenderer renderer, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(renderer);

        SceneGeometry geometry = renderer.CreateGeometry();
        var party = new DiscoParty(content, geometry);

        party._log = log;

        try
        {
            if (!party.Furnish(log))
            {
                party.Dispose();
                return null;
            }
        }
        catch
        {
            party.Dispose();
            throw;
        }

        return party;
    }

    /// <summary>Puts everything in the room and writes the programme.</summary>
    private bool Furnish(Action<string>? log)
    {
        Floor();
        Wall();

        // The ball first, because without it there is no party: it is what comes down and
        // what everything else waits for.
        if (Rig(log) is not { } rig)
        {
            log?.Invoke("Disco: the archives have no mirror ball, so there is no party");
            return false;
        }

        // The ball comes down over the length of its clip, and the moment it is down the
        // music starts, the floor lights up and the guests begin to arrive.
        float down = (float)rig.Routine[0].Animation.Duration;

        _programme.Add((down, () =>
        {
            foreach (Puppet glow in rig.Puppets.Where(p => p.Glows))
            {
                _geometry.SetVisible(glow.Placement, true);
            }

            foreach (Performer turning in _performers.Where(
                         p => p.Name.Equals("rl2_discospecks", StringComparison.OrdinalIgnoreCase)))
            {
                turning.Visible = true;
            }

            _flashing = true;
            _sinceLights = float.MaxValue;
            StartMusic();
        }));

        Invite(down + 0.8f, log, "bar", new Vector3(60f, 0f, 40f), 0f,
            Numbers("BarDISCOBABY", "BarSHAKEITBABY", "BarSHAKEITBABY", "BarGETDOWNBABY"));

        _programme.Add((down + 1.6f, () => Caption = "Can't -- stop -- must -- disco"));

        // Grace does the splits, then holds them and turns on the spot: the pose is the
        // joke and a slow turn shows it off from every side.
        Invite(down + 3.2f, log, "gra", new Vector3(-20f, 0f, -130f), 0.35f,
            Numbers("GraFlexy"), after: After.Hold, spin: 1.1f);

        Invite(down + 5.4f, log, "jea", new Vector3(250f, 0f, -30f), -0.35f,
            Numbers("JeaFlippy"));

        Invite(down + 7.6f, log, "eml", new Vector3(-40f, 0f, 250f), 0.15f,
            Numbers("EmlLbyClowny", "EmlLbyClowny2", "EmlLbyClowny3"));

        // Gabriel flies laps over the whole floor. The propeller is on his head; it is a
        // long story, and it is told at the foot of Mount Cardou.
        Invite(down + 10f, log, "gab", Vector3.Zero, 0f, Numbers("GabPropIdle"),
            path: t =>
            {
                float angle = (t * 0.55f) + 1f;
                float radius = 320f;

                return Matrix4x4.CreateRotationY(angle + (MathF.PI / 2f)) *
                    Matrix4x4.CreateTranslation(
                        100f + (radius * MathF.Cos(angle)),
                        150f + (18f * MathF.Sin(t * 1.7f)),
                        (radius * 0.5f * MathF.Sin(angle)) + 140f);
            });

        Invite(down + 12f, log, "cat", new Vector3(200f, 0f, -60f), -0.6f,
            Numbers("catsitfidg1", "catsitfidg2", "catsitfidg3"));

        Invite(down + 13f, log, "chk", new Vector3(-30f, 0f, 170f), 0.7f,
            Numbers("Chkpeck", "Chkpeck", "ChkLook", "ChkScratch"));

        // The goat and the dog walk laps around the edge of the floor, one each way. A walk
        // clip carries its owner forward inside the clip; that is taken back out and the
        // path moves them instead, which is how the game's own walks work.
        Invite(down + 15f, log, "got", Vector3.Zero, 0f, Numbers("GoatWalk"),
            strides: true, path: t => Lap(t * 0.32f, 340f, 250f, 0f));

        Invite(down + 16f, log, "dog", Vector3.Zero, 0f, Numbers("DogWalk"),
            strides: true, path: t => Lap(-t * 0.42f, 310f, 220f, MathF.PI));

        _programme.Sort((a, b) => a.At.CompareTo(b.At));

        log?.Invoke($"Disco: {_performers.Count} guests, {_geometry.TriangleCount} triangles");

        return true;
    }

    /// <summary>A place on a lap of the floor's edge.</summary>
    /// <param name="angle">How far round, in radians.</param>
    /// <param name="width">Half the lap's width.</param>
    /// <param name="depth">Half its depth.</param>
    /// <param name="built">Which way round the walker is built to face its own stride.</param>
    private static Matrix4x4 Lap(float angle, float width, float depth, float built)
    {
        var at = new Vector3(100f + (width * MathF.Cos(angle)), 0f, 60f + (depth * MathF.Sin(angle)));

        // Facing along the lap: the derivative of the ellipse, which is where the walker is
        // about to be.
        var ahead = new Vector3(-width * MathF.Sin(angle), 0f, depth * MathF.Cos(angle));
        float heading = MathF.Atan2(ahead.X, ahead.Z);

        return Matrix4x4.CreateRotationY(heading + built) * Matrix4x4.CreateTranslation(at);
    }

    /// <summary>Moves the party on.</summary>
    /// <param name="seconds">How long the last frame took.</param>
    public void Advance(float seconds)
    {
        if (_disposed)
        {
            return;
        }

        float step = Math.Clamp(seconds, 0f, 0.1f);
        float was = _elapsed;
        _elapsed += step;

        foreach ((float at, Action what) in _programme)
        {
            if (at > was && at <= _elapsed)
            {
                what();
            }
        }

        if (Caption is not null)
        {
            CaptionAge += step;
        }

        if (_flashing)
        {
            int beats = (int)(_elapsed / BeatSeconds);

            if (beats != _beats)
            {
                _beats = beats;
                _checker = (_checker + 1) % Checkers.Length;

                // The bar's own flash cycles three checker patterns. Repainted rather than
                // swapped: the floor is one model and the three pictures are already in.
                _geometry.Repaint(_floor, FloorTexture, Checkers[_checker]);
            }
        }

        foreach (Performer performer in _performers)
        {
            performer.Step(_geometry, step, this);
        }

        _sinceLights += step;

        if (_sinceLights >= LightStep)
        {
            _sinceLights = 0f;
            LayLights();
        }
    }

    /// <summary>Takes the music off for as long as something else owns the sound.</summary>
    public void Hush()
    {
        _hushed = true;

        if (_music.Exists)
        {
            _content.Audio?.Silence(_music);
            _music = AudioVoice.None;
        }
    }

    /// <summary>Puts it back.</summary>
    public void Resume()
    {
        _hushed = false;

        if (_flashing)
        {
            StartMusic();
        }
    }

    /// <summary>The music, looped, if there is any and nobody has asked for quiet.</summary>
    private void StartMusic()
    {
        if (_hushed || _music.Exists || _content.Audio is null || _content.Sounds is null)
        {
            return;
        }

        if (_content.Sounds.Read(Music) is { } track)
        {
            _music = _content.Audio.Play(track, AudioBus.Music, repeat: true);
        }
    }

    /// <summary>Plays one of a clip's sound cues.</summary>
    private void Cue(AnimationSound cue)
    {
        if (_content.Audio is null || _content.Sounds is null)
        {
            return;
        }

        if (_content.Sounds.Read(cue.Name) is { } sound)
        {
            AudioVoice voice = _content.Audio.Play(sound, AudioBus.Effects);

            if (voice.Exists && cue.Gain < 1f)
            {
                _content.Audio.SetVoiceGain(voice, cue.Gain);
            }
        }
    }

    /// <summary>Lays the lights where they have got to.</summary>
    /// <remarks>
    /// A ring of coloured lights turning over the floor, each a different colour and each
    /// drifting through the hues at its own rate, and a white key over the ball so that the
    /// mirror ball is lit whatever colour the ring is showing. Before the ball is down
    /// there is one dim warm light, which is the bar with its house lights on.
    /// </remarks>
    private void LayLights()
    {
        _lights.Clear();

        if (!_flashing)
        {
            _lights.Add(Light("house", new Vector3(0f, 320f, -120f),
                new Vector3(1f, 0.85f, 0.7f), 0.9f, 900f));
            _lightsMoved = true;
            return;
        }

        float turn = _elapsed * 0.42f;

        for (int i = 0; i < Ring; i++)
        {
            float angle = turn + (i * 2f * MathF.PI / Ring);
            float hue = ((_elapsed * 0.08f) + (i / (float)Ring)) % 1f;

            // Pulsed on the beat, and not all together: each light is a sixth of a beat
            // behind the last, so the ring chases rather than blinks.
            float pulse = 0.7f + (0.3f * MathF.Max(0f, MathF.Cos(
                ((Beat - (i / (float)Ring)) * 2f * MathF.PI))));

            _lights.Add(Light(
                $"disco_{i}",
                new Vector3(420f * MathF.Cos(angle), 260f, 260f * MathF.Sin(angle)),
                Hue(hue),
                1.5f * pulse,
                800f));
        }

        _lights.Add(Light("ball_key", BallSpot + new Vector3(0f, 70f, -90f),
            new Vector3(1f, 1f, 1f), 1.4f, 500f));

        _lightsMoved = true;
    }

    private static AuthoredLight Light(
        string name, Vector3 at, Vector3 colour, float intensity, float reach) =>
        new(
            name,
            AuthoredLightKind.Point,
            at,
            -Vector3.UnitY,
            colour,
            0f,
            0f,
            reach * 0.15f,
            reach,
            UsesAttenuation: true,
            CastsShadows: false,
            intensity,
            Radius: 8f);

    /// <summary>A saturated colour, from where it is round the wheel.</summary>
    private static Vector3 Hue(float hue)
    {
        float h = (hue % 1f) * 6f;
        float x = 1f - MathF.Abs((h % 2f) - 1f);

        return (int)h switch
        {
            0 => new Vector3(1f, x, 0f),
            1 => new Vector3(x, 1f, 0f),
            2 => new Vector3(0f, 1f, x),
            3 => new Vector3(0f, x, 1f),
            4 => new Vector3(x, 0f, 1f),
            _ => new Vector3(1f, 0f, x),
        };
    }

    /// <summary>The checkered floor.</summary>
    private void Floor()
    {
        foreach (string checker in Checkers)
        {
            _content.Texture(_geometry, checker);
        }

        ModFile floor = Quad(
            "disco_floor",
            FloorTexture,
            new Vector3(-FloorHalfWidth, 0f, -FloorHalfDepth),
            new Vector3(FloorHalfWidth, 0f, -FloorHalfDepth),
            new Vector3(FloorHalfWidth, 0f, FloorHalfDepth),
            new Vector3(-FloorHalfWidth, 0f, FloorHalfDepth),
            Vector3.UnitY,
            new Vector2(2f * FloorHalfWidth / Tile, 2f * FloorHalfDepth / Tile));

        _floor = _geometry.Add(floor);
    }

    /// <summary>The wall behind it: the title screen's own, hung as a backdrop.</summary>
    private void Wall()
    {
        if (_content.Wall is not { } picture)
        {
            return;
        }

        _geometry.AddTexture(WallTexture, picture);

        // Wide enough that the camera's sway never finds its edge, and tiled across at
        // about the height it is painted, since it is painted to meet itself.
        float half = FloorHalfWidth * 3f;
        float across = picture.Height > 0
            ? (2f * half) / (WallHeight * picture.Width / picture.Height)
            : 2f;

        ModFile wall = Quad(
            "disco_wall",
            WallTexture,
            new Vector3(-half, WallHeight, FloorHalfDepth),
            new Vector3(half, WallHeight, FloorHalfDepth),
            new Vector3(half, -400f, FloorHalfDepth),
            new Vector3(-half, -400f, FloorHalfDepth),
            -Vector3.UnitZ,
            new Vector2(across, 1f));

        _geometry.Add(wall);
    }

    /// <summary>
    /// The ball, its pole and the specks it throws: the bar's own three models, kept in
    /// the arrangement the bar authored them in.
    /// </summary>
    /// <remarks>
    /// The pole's clips and the two glowing models are all authored in the bar's own
    /// space, so one translation moves all three together and the pole still comes down
    /// exactly onto the ball. The translation is chosen so that the ball hangs at
    /// <see cref="BallSpot"/>.
    /// </remarks>
    private Performer? Rig(Action<string>? log)
    {
        if (_content.Model("discolights_ball") is not { } ball ||
            _content.Model("discoball_pole") is not { } pole)
        {
            return null;
        }

        (Vector3 low, Vector3 high) = Bounds(ball);
        Vector3 anchor = (low + high) / 2f;

        var performer = new Performer("discoball_pole", anchor, BallSpot, 0f);

        performer.Puppets.Add(Enroll(pole));
        performer.Puppets.Add(Enroll(ball) with { Glows = true });

        if (_content.Model("rl2_discospecks") is { } specks)
        {
            performer.Puppets.Add(Enroll(specks) with { Glows = true });
        }

        // Down, then round for ever.
        if (Learn(performer, "disco_godown", After.Next) is not { } down ||
            Learn(performer, "disco_goround", After.Loop) is not { } round)
        {
            log?.Invoke("Disco: the ball has no clips to come down on");
            return null;
        }

        performer.Routine.Add(down);
        performer.Routine.Add(round);

        // The specks turn with a clip of their own, played alongside the pole's rather
        // than after it: a second performer over the same puppet, sharing the translation.
        Performer? turning = null;

        if (Learn(performer, "rl2_speckrotate", After.Loop) is { } specksTurn)
        {
            turning = new Performer("rl2_discospecks", anchor, BallSpot, 0f) { Visible = false };
            turning.Puppets.AddRange(specksTurn.Parts.Select(p => p.Puppet).Distinct());
            turning.Routine.Add(specksTurn);
        }

        foreach (Puppet puppet in performer.Puppets)
        {
            Place(performer, puppet);
        }

        performer.Visible = true;
        performer.Show(_geometry, true);

        // The ball and the specks stay out of sight until the pole has come down to them.
        foreach (Puppet puppet in performer.Puppets.Where(p => p.Glows))
        {
            _geometry.SetVisible(puppet.Placement, false);
        }

        performer.Step(_geometry, 0f, this);
        _performers.Add(performer);

        if (turning is not null)
        {
            turning.Step(_geometry, 0f, this);
            _performers.Add(turning);
        }

        return performer;
    }

    /// <summary>Asks one guest along.</summary>
    /// <param name="at">When they arrive, in seconds from the start.</param>
    /// <param name="log">Where to say whether they came.</param>
    /// <param name="name">Their model.</param>
    /// <param name="spot">Where they stand.</param>
    /// <param name="yaw">Which way they are turned, in radians about the vertical.</param>
    /// <param name="numbers">What they do, in order.</param>
    /// <param name="after">What happens when the last number ends.</param>
    /// <param name="spin">How fast they turn on the spot once they are holding, in radians a second.</param>
    /// <param name="strides">Whether their clip walks them forward, which is taken back out.</param>
    /// <param name="path">Where they are over time, instead of standing on the spot.</param>
    private void Invite(
        float at,
        Action<string>? log,
        string name,
        Vector3 spot,
        float yaw,
        string[] numbers,
        After after = After.Loop,
        float spin = 0f,
        bool strides = false,
        Func<float, Matrix4x4>? path = null)
    {
        if (_content.Only is { } only && !only.Contains(name))
        {
            return;
        }

        if (_content.Model(name) is not { } model)
        {
            log?.Invoke($"Disco: {name} did not come; the archives have no such model");
            return;
        }

        var performer = new Performer(name, Vector3.Zero, spot, yaw)
        {
            Spin = spin,
            Strides = strides,
            Path = path,
            Visible = false,
        };

        performer.Puppets.Add(Enroll(model));

        for (int i = 0; i < numbers.Length; i++)
        {
            // The last number in a routine ends the way the guest was asked; every other
            // one leads to the next.
            After ending = i == numbers.Length - 1 ? after : After.Next;

            if (Learn(performer, numbers[i], ending) is { } number)
            {
                performer.Routine.Add(number);
            }
            else
            {
                log?.Invoke($"Disco: {name} has no {numbers[i]}");
            }
        }

        if (performer.Routine.Count == 0)
        {
            log?.Invoke($"Disco: {name} did not come; none of their numbers could be read");
            return;
        }

        // Where the clip puts them on its first frame, so that the spot is where their
        // feet are rather than where the bar or the lobby had them.
        performer.Anchor = Feet(performer.Routine[0], name);

        log?.Invoke(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Disco: {name} anchored at ({performer.Anchor.X:F0}, {performer.Anchor.Y:F0}, "
            + $"{performer.Anchor.Z:F0}), {performer.Puppets.Count} model(s), "
            + $"{performer.Routine.Sum(r => r.Parts.Count)} clip(s)"));

        foreach (Puppet puppet in performer.Puppets)
        {
            Place(performer, puppet);
        }

        performer.Show(_geometry, false);
        performer.Step(_geometry, 0f, this);
        _performers.Add(performer);

        _programme.Add((at, () =>
        {
            performer.Visible = true;
            performer.Show(_geometry, true);
            log?.Invoke(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Disco: {name} arrives at {_elapsed:F1}s"));
        }));
    }

    private static string[] Numbers(params string[] numbers) => numbers;

    /// <summary>Reads one animation and the clips it names onto a performer's puppets.</summary>
    /// <remarks>
    /// An animation that names a model the performer does not yet have gets that model
    /// added: Emilio's clown routine is Emilio, a red nose, a party hat and a newspaper,
    /// and each is a model of its own with a clip of its own.
    /// </remarks>
    private Number? Learn(Performer performer, string name, After after)
    {
        if (_content.Animations.Read(name) is not { } animation || animation.Actions.Count == 0)
        {
            return null;
        }

        var number = new Number(animation, after);

        foreach (AnimationAction action in animation.Actions)
        {
            if (_content.Clips.Read(action.Name) is not { } clip)
            {
                continue;
            }

            Puppet? puppet = performer.Puppets.FirstOrDefault(
                p => p.Name.Equals(clip.ModelName, StringComparison.OrdinalIgnoreCase));

            if (puppet is null)
            {
                if (_content.Model(clip.ModelName) is not { } model)
                {
                    continue;
                }

                puppet = Enroll(model);
                performer.Puppets.Add(puppet);
            }

            if (clip.MeshCount != puppet.Model.Meshes.Count)
            {
                continue;
            }

            // A puppet a clip poses is placed by the clip, mesh by mesh, and must not be
            // moved as a whole as well: the two would multiply.
            puppet.Posed = true;
            number.Parts.Add((clip, puppet));
        }

        // What the clip repaints as it runs has to be on the device before it runs.
        foreach (AnimationTexture swap in animation.Textures)
        {
            _content.Texture(_geometry, swap.Texture);
        }

        if (number.Parts.Count == 0)
        {
            return null;
        }

        // A held number stops on its widest stance: Grace's splits are the middle of her
        // clip, and the end of it is her standing up again. Widest, with a little weight
        // on tallest, so that of the dozen frames she is down for the one held is the one
        // where she has straightened up to be looked at.
        if (after == After.Hold)
        {
            (ActFile clip, _) = number.Parts.FirstOrDefault(
                p => p.Puppet.Name.Equals(performer.Name, StringComparison.OrdinalIgnoreCase));

            clip ??= number.Parts[0].Clip;

            float best = float.MinValue;

            for (int frame = 0; frame < clip.FrameCount; frame++)
            {
                var origins = Enumerable.Range(0, clip.MeshCount)
                    .Select(m => clip.PoseOf(m, frame))
                    .Where(p => p is not null)
                    .Select(p => p!.Value.Translation)
                    .ToList();

                if (origins.Count == 0)
                {
                    continue;
                }

                float spread = 0f;

                foreach (Vector3 one in origins)
                {
                    foreach (Vector3 other in origins)
                    {
                        spread = MathF.Max(
                            spread,
                            Vector2.Distance(new(one.X, one.Z), new(other.X, other.Z)));
                    }
                }

                float score = spread + (origins.Max(o => o.Y) / 2f);

                if (score > best)
                {
                    best = score;
                    number.HoldFrame = frame;
                }
            }
        }

        return number;
    }

    /// <summary>Loads a model's textures and puts it in the scene, unplaced.</summary>
    private Puppet Enroll(ModFile model)
    {
        foreach (string texture in model.Meshes
                     .SelectMany(m => m.Submeshes)
                     .Select(s => s.TextureName)
                     .Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _content.Texture(_geometry, texture);
        }

        // By the name a clip would call it: a clip names its model bare, and a model read
        // under a file name would never match.
        return new Puppet(
            Path.GetFileNameWithoutExtension(model.Name), model, _geometry.Add(model));
    }

    /// <summary>Puts a puppet where its performer stands, before any clip has moved it.</summary>
    private void Place(Performer performer, Puppet puppet)
    {
        if (puppet.Glows)
        {
            _geometry.SetSelfLit(puppet.Placement, true);
        }

        if (!puppet.Posed)
        {
            _geometry.MoveModel(puppet.Placement, performer.World(0f));
        }
    }

    /// <summary>
    /// Where a clip stands its owner on its first frame: the middle of the body across,
    /// and the lowest point of it down.
    /// </summary>
    /// <remarks>
    /// The posed vertices rather than the mesh origins. A character's lowest origin is a
    /// shoe and near enough the floor; a cat's is its body, and a cat stood by its body's
    /// origin is a cat up to its shoulders in the dance floor. The rig markers — the
    /// three-vertex triads the artists left in — are left out, since they can sit
    /// anywhere.
    /// </remarks>
    private static Vector3 Feet(Number number, string owner)
    {
        (ActFile clip, Puppet puppet) = number.Parts.FirstOrDefault(
            p => p.Puppet.Name.Equals(owner, StringComparison.OrdinalIgnoreCase));

        if (clip is null)
        {
            (clip, puppet) = number.Parts[0];
        }

        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        for (int m = 0; m < clip.MeshCount && m < puppet.Model.Meshes.Count; m++)
        {
            ModMesh mesh = puppet.Model.Meshes[m];
            Matrix4x4 pose = clip.PoseOf(m, 0) ?? mesh.MeshToLocal;

            for (int sub = 0; sub < mesh.Submeshes.Count; sub++)
            {
                IReadOnlyList<Vector3> positions =
                    clip.ShapeOf(m, sub, 0) ?? mesh.Submeshes[sub].Positions;

                if (positions.Count < 12)
                {
                    continue;
                }

                foreach (Vector3 position in positions)
                {
                    Vector3 placed = Vector3.Transform(position, pose);
                    low = Vector3.Min(low, placed);
                    high = Vector3.Max(high, placed);
                }
            }
        }

        if (low.X > high.X)
        {
            return Vector3.Zero;
        }

        return new Vector3((low.X + high.X) / 2f, low.Y, (low.Z + high.Z) / 2f);
    }

    /// <summary>A model's corners, as authored.</summary>
    private static (Vector3 Low, Vector3 High) Bounds(ModFile model)
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        foreach (ModMesh mesh in model.Meshes)
        {
            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                foreach (Vector3 position in submesh.Positions)
                {
                    Vector3 placed = Vector3.Transform(position, mesh.MeshToLocal);
                    low = Vector3.Min(low, placed);
                    high = Vector3.Max(high, placed);
                }
            }
        }

        return low.X <= high.X ? (low, high) : (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>A textured rectangle, drawn from both sides.</summary>
    private static ModFile Quad(
        string name,
        string texture,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector3 normal,
        Vector2 repeats)
    {
        Vector3[] positions = [a, b, c, d, a, b, c, d];
        Vector3[] normals = [normal, normal, normal, normal, -normal, -normal, -normal, -normal];

        Vector2[] uv =
        [
            new(0f, 0f), new(repeats.X, 0f), new(repeats.X, repeats.Y), new(0f, repeats.Y),
            new(0f, 0f), new(repeats.X, 0f), new(repeats.X, repeats.Y), new(0f, repeats.Y),
        ];

        // Both windings, so that whichever side the renderer keeps is the one that shows.
        ushort[] indices = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6];

        var submesh = new ModSubmesh
        {
            TextureName = texture,
            Color = (255, 255, 255),
            Positions = positions,
            Normals = normals,
            TexCoords = uv,
            Indices = indices,
        };

        var mesh = new ModMesh
        {
            Name = name,
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = Vector3.Min(Vector3.Min(a, b), Vector3.Min(c, d)),
            BoundsMax = Vector3.Max(Vector3.Max(a, b), Vector3.Max(c, d)),
            Submeshes = [submesh],
        };

        return ModFile.FromMeshes(name, [mesh]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _log?.Invoke(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Disco: over after {_elapsed:F1}s"));

        if (_music.Exists)
        {
            _content.Audio?.Silence(_music);
            _music = AudioVoice.None;
        }

        _geometry.Dispose();
    }

    /// <summary>What a performer does when a number ends.</summary>
    private enum After
    {
        /// <summary>Starts it again.</summary>
        Loop,

        /// <summary>Goes on to the next number, or round to the first.</summary>
        Next,

        /// <summary>Stays on its lowest frame, which for the splits is the splits.</summary>
        Hold,
    }

    /// <summary>One model in the scene.</summary>
    private sealed record Puppet(string Name, ModFile Model, ModelPlacement Placement)
    {
        /// <summary>Whether it is drawn lit by itself, like the mirror ball.</summary>
        public bool Glows { get; init; }

        /// <summary>Whether a clip places it, mesh by mesh, rather than the group moving it whole.</summary>
        public bool Posed { get; set; }
    }

    /// <summary>One animation file, and the clips it drives on a performer's puppets.</summary>
    private sealed class Number(AnimationFile animation, After after)
    {
        public AnimationFile Animation { get; } = animation;

        public After After { get; } = after;

        public List<(ActFile Clip, Puppet Puppet)> Parts { get; } = [];

        public float Rate => Math.Max(1, Animation.Rate);

        public int Frames =>
            Math.Max(Math.Max(1, Animation.FrameCount), Parts.Max(p => p.Clip.FrameCount));

        /// <summary>The frame a held number stops on.</summary>
        public int HoldFrame { get; set; } = int.MaxValue;
    }

    /// <summary>
    /// Somebody at the party: a group of models sharing one place, running one routine.
    /// </summary>
    private sealed class Performer(string name, Vector3 anchor, Vector3 spot, float yaw)
    {
        private float _elapsed;
        private float _turned;
        private int _index;
        private int _lastFrame = -1;

        public string Name { get; } = name;

        /// <summary>Where the clips stand the performer, in their own authored space.</summary>
        public Vector3 Anchor { get; set; } = anchor;

        public Vector3 Spot { get; } = spot;

        public float Yaw { get; } = yaw;

        public float Spin { get; init; }

        public bool Strides { get; init; }

        public Func<float, Matrix4x4>? Path { get; init; }

        public bool Visible { get; set; }

        public List<Puppet> Puppets { get; } = [];

        public List<Number> Routine { get; } = [];

        /// <summary>Whether the routine has ended on a held frame.</summary>
        public bool Holding { get; private set; }

        /// <summary>From the clips' space to the party's, at a moment.</summary>
        public Matrix4x4 World(float seconds) =>
            Matrix4x4.CreateTranslation(-Anchor) *
            Matrix4x4.CreateRotationY(Yaw + _turned) *
            (Path?.Invoke(seconds) ?? Matrix4x4.CreateTranslation(Spot));

        public void Show(SceneGeometry geometry, bool visible)
        {
            foreach (Puppet puppet in Puppets)
            {
                geometry.SetVisible(puppet.Placement, visible);
            }
        }

        /// <summary>Goes on to the next number.</summary>
        public void Next()
        {
            _index = (_index + 1) % Math.Max(1, Routine.Count);
            _elapsed = 0f;
            _lastFrame = -1;
            Holding = false;
        }

        /// <summary>Poses every puppet for the moment.</summary>
        public void Step(SceneGeometry geometry, float seconds, DiscoParty party)
        {
            if (Routine.Count == 0)
            {
                return;
            }

            Number number = Routine[_index];
            float frames = number.Frames;

            if (!Holding)
            {
                _elapsed += seconds;
            }

            if (Holding && Spin != 0f)
            {
                _turned += Spin * seconds;
            }

            float at = _elapsed * number.Rate;
            bool cycles = number.After == After.Loop;

            // A looping clip runs its last frame back into its first, so its period is the
            // whole count; one that ends stops on its last frame.
            if (cycles && at >= frames)
            {
                at %= frames;
                _elapsed = at / number.Rate;
            }
            else if (!cycles && at >= MathF.Min(frames - 1, number.HoldFrame))
            {
                switch (number.After)
                {
                    case After.Next:
                        // Cues on the last frame of the number that is ending, then the
                        // next one from its start.
                        Cues(number, (int)frames - 1, party);
                        Next();
                        Step(geometry, 0f, party);
                        return;

                    default:
                        at = MathF.Min(frames - 1, number.HoldFrame);
                        Holding = true;
                        break;
                }
            }

            int frame = (int)at;

            if (Visible)
            {
                Cues(number, frame, party);
            }

            _lastFrame = frame;

            Matrix4x4 world = World(party.Elapsed);

            foreach ((ActFile clip, Puppet puppet) in number.Parts)
            {
                Matrix4x4 correction = world;

                if (Strides)
                {
                    // The forward travel the clip carries, taken back out so that the path
                    // is what moves the walker. The clips walk down their own Z.
                    float opens = Mean(clip, 0f).Z;
                    float now = Mean(clip, at, cycles).Z;
                    correction = Matrix4x4.CreateTranslation(0f, 0f, opens - now) * world;
                }

                for (int mesh = 0; mesh < clip.MeshCount; mesh++)
                {
                    // A clip that never places a mesh group leaves it where the model
                    // was built — the cat's clips move its vertices and nothing else — so
                    // the model's own transform stands in, and the group still goes
                    // where the performer is.
                    Matrix4x4 pose = clip.PoseAt(mesh, at, cycles)
                        ?? (mesh < puppet.Model.Meshes.Count
                            ? puppet.Model.Meshes[mesh].MeshToLocal
                            : Matrix4x4.Identity);

                    geometry.PoseMesh(puppet.Placement, mesh, pose * correction);

                    foreach (int submesh in clip.ShapedSubmeshes(mesh))
                    {
                        if (clip.ShapeAt(mesh, submesh, at, cycles) is { } shape)
                        {
                            geometry.ShapeMesh(puppet.Placement, mesh, submesh, shape);
                        }
                    }
                }
            }

            // A puppet no clip ever moves still has to move with the group: the mirror
            // ball has no clip and hangs where the pole comes down to.
            foreach (Puppet still in Puppets)
            {
                if (!still.Posed)
                {
                    geometry.MoveModel(still.Placement, world);
                }
            }
        }

        /// <summary>Plays the sounds and applies the swaps between the last frame and this one.</summary>
        private void Cues(Number number, int frame, DiscoParty party)
        {
            if (frame == _lastFrame)
            {
                return;
            }

            bool Crossed(int cue) => _lastFrame < frame
                ? cue > _lastFrame && cue <= frame
                : cue > _lastFrame || cue <= frame;

            foreach (AnimationSound sound in number.Animation.Sounds)
            {
                if (Crossed(sound.Frame))
                {
                    party.Cue(sound);
                }
            }

            foreach (AnimationVisibility change in number.Animation.Visibility)
            {
                if (!Crossed(change.Frame) ||
                    Puppets.FirstOrDefault(p => p.Name.Equals(change.Model, StringComparison.OrdinalIgnoreCase))
                        is not { } puppet)
                {
                    continue;
                }

                if (change.Mesh < 0)
                {
                    party._geometry.SetVisible(puppet.Placement, change.Visible && Visible);
                }
                else
                {
                    party._geometry.SetPartVisible(
                        puppet.Placement, change.Mesh, Math.Max(0, change.Submesh), change.Visible);
                }
            }

            foreach (AnimationTexture swap in number.Animation.Textures)
            {
                if (!Crossed(swap.Frame) ||
                    Puppets.FirstOrDefault(p => p.Name.Equals(swap.Model, StringComparison.OrdinalIgnoreCase))
                        is not { } puppet ||
                    swap.Mesh < 0 || swap.Mesh >= puppet.Model.Meshes.Count ||
                    swap.Submesh < 0 || swap.Submesh >= puppet.Model.Meshes[swap.Mesh].Submeshes.Count)
                {
                    continue;
                }

                party._geometry.Repaint(
                    puppet.Placement,
                    puppet.Model.Meshes[swap.Mesh].Submeshes[swap.Submesh].TextureName,
                    swap.Texture);
            }
        }

        private static Vector3 Mean(ActFile clip, float frame, bool cycles = false)
        {
            var sum = Vector3.Zero;
            int count = 0;

            for (int mesh = 0; mesh < clip.MeshCount; mesh++)
            {
                if (clip.PoseAt(mesh, frame, cycles) is { } pose)
                {
                    sum += pose.Translation;
                    count++;
                }
            }

            return count > 0 ? sum / count : Vector3.Zero;
        }
    }
}

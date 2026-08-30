using System.Text.Json;
using GK3Reborn.Rendering.Geometry;
using System.Text.Json.Serialization;
using GK3Reborn.Audio;
using GK3Reborn.Foundation;
using GK3Reborn.Platform;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Upscaling;

namespace GK3Reborn.Game;

/// <summary>How much of the picture the player wants paid for.</summary>
/// <remarks>
/// Separate from <see cref="RayTracingQuality"/> so the menu can offer one word — the
/// quality ladder is a rendering decision and this is a preference about it.
/// </remarks>
public enum PictureQuality
{
    /// <summary>The 1999 picture: baked light, no rays.</summary>
    Original,

    /// <summary>Shadows, cheaply.</summary>
    Improved,

    /// <summary>Shadows and occlusion.</summary>
    High,

    /// <summary>Everything, at the highest ray budget.</summary>
    Highest,
}

/// <summary>
/// What the player has chosen, and where it is kept.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these has somewhere real to go. The audio levels are the mixer's buses,
/// which nothing had ever set; the picture quality is the ray-tracing ladder; the walking
/// pace is what a double-click multiplies. A setting with no destination is a promise the
/// interface cannot keep, so there are none here.
/// </para>
/// <para>
/// Kept in the user's own profile rather than beside the executable. A game directory may
/// be read-only, shared between accounts, or replaced wholesale by an update, and none of
/// those should cost somebody their volume levels.
/// </para>
/// <para>
/// <b>Everything is clamped on the way in.</b> A settings file is a text file somebody may
/// edit, and a hand-typed volume of forty is not a reason to fail to start.
/// </para>
/// </remarks>
public sealed record Settings
{
    /// <summary>How loud everything is, over the top of the rest.</summary>
    public float MasterVolume { get; init; } = 1f;

    /// <summary>Music and the cutscenes' own soundtrack.</summary>
    public float MusicVolume { get; init; } = 1f;

    /// <summary>What a room sounds like when nothing is happening in it.</summary>
    public float AmbienceVolume { get; init; } = 1f;

    /// <summary>Doors, footsteps, everything that happens once.</summary>
    public float EffectsVolume { get; init; } = 1f;

    /// <summary>Speech.</summary>
    public float DialogueVolume { get; init; } = 1f;

    /// <summary>What the sound is being played through.</summary>
    public SpeakerLayout Speakers { get; init; } = SpeakerLayout.Stereo;

    /// <summary>How much of the picture to pay for.</summary>
    public PictureQuality Picture { get; init; } = PictureQuality.High;

    /// <summary>Whether to use the higher-resolution textures where they exist.</summary>
    public bool EnhancedTextures { get; init; } = true;

    /// <summary>Whether the window has a border, covers a monitor, or takes one over.</summary>
    /// <remarks>
    /// Windowed by default, which is the only one of the three that is right on a machine
    /// nobody has told the game anything about. A player who wants the screen says so once.
    /// </remarks>
    public WindowMode Display { get; init; } = WindowMode.Windowed;

    /// <summary>How wide the window is, in pixels, or nought for the monitor's own size.</summary>
    /// <remarks>
    /// Only read for <see cref="WindowMode.Windowed"/> and
    /// <see cref="WindowMode.ExclusiveFullscreen"/>. A borderless window is the size of the
    /// monitor by definition — that is what makes it borderless fullscreen rather than a
    /// large window — so a size stored here is remembered and not applied.
    /// </remarks>
    public int DisplayWidth { get; init; }

    /// <summary>How tall it is.</summary>
    public int DisplayHeight { get; init; }

    /// <summary>
    /// How much larger or smaller the interface's letters are than the size the window
    /// would pick on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The automatic size is a share of the window's height, which is the right rule for a
    /// display nobody has told the game anything about and is not the right rule for
    /// everybody: the same share is crowded on a large monitor sat close to and too small
    /// on a television across a room. This multiplies whatever that rule arrived at, so it
    /// is a correction to the automatic size rather than a replacement for it — a player
    /// who then goes fullscreen still gets letters cut for the new window, only their own
    /// share of it.
    /// </para>
    /// <para>
    /// Applied after the automatic size has been clamped, not before. The share is already
    /// capped above about 1440 lines — a twenty-sixth of a 4K screen is nobody's idea of a
    /// settings page — and a multiplier that went in ahead of that cap would do nothing at
    /// all on exactly the large displays this row exists for.
    /// </para>
    /// <para>
    /// Both atlases move together. The menu is drawn larger than the room's captions and
    /// stays so; this is one preference about reading, not two.
    /// </para>
    /// </remarks>
    public float TextScale { get; init; } = 1f;

    /// <summary>The smallest the interface's letters may be asked to go.</summary>
    /// <remarks>
    /// Ends rather than a free number, because the two things past either of them are
    /// unreadable text and a settings page with four rows on it. Sixty per cent is where a
    /// caption stops being comfortable at 1080 lines and is still a third off the menu on a
    /// 4K display, which is the complaint this row exists to answer.
    /// </remarks>
    public const float SmallestText = 0.6f;

    /// <summary>The largest.</summary>
    public const float LargestText = 1.6f;

    /// <summary>Whether frames wait for the display.</summary>
    /// <remarks>
    /// On, because the alternative is tearing and because FIFO is the only present mode
    /// Vulkan guarantees exists. Off asks for the fastest mode the surface offers and
    /// quietly stays on where there is none.
    /// </remarks>
    public bool VerticalSync { get; init; } = true;

    /// <summary>Which upscaler to use, if any.</summary>
    public UpscalerKind Upscaler { get; init; } = UpscalerKind.Off;

    /// <summary>How much of the picture to actually draw.</summary>
    public UpscalerQuality UpscalerQuality { get; init; } = UpscalerQuality.Quality;

    /// <summary>Whether the upscaled picture is sharpened.</summary>
    public bool Sharpening { get; init; } = true;

    /// <summary>How hard, from nothing to as much as the filter will do.</summary>
    public float Sharpness { get; init; } = 0.5f;

    /// <summary>Whether frames are generated between the ones the game draws.</summary>
    public FrameGeneration FrameGeneration { get; init; } = FrameGeneration.Off;

    /// <summary>How hard to work at keeping latency down. See <see cref="LatencyMode"/>.</summary>
    public LatencyMode Latency { get; init; } = LatencyMode.On;

    /// <summary>Which graphics API to draw through.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="RenderBackend.Automatic"/> by default, which is Direct3D 12 on Windows and
    /// Vulkan everywhere else — see <see cref="RenderBackends.Choose"/> for why that way
    /// round. Naming one is for the case the automatic answer is wrong on a particular
    /// machine, which is the first thing to try when a Windows machine misbehaves.
    /// </para>
    /// <para>
    /// <b>Read once, at startup.</b> The device, the swapchain, every pipeline and every
    /// texture belong to a backend, so changing it means building all of them again — which
    /// is what starting the game does. The settings page says so rather than pretending
    /// otherwise.
    /// </para>
    /// <para>
    /// <c>--backend</c> on the command line outranks it. Somebody who typed a backend for one
    /// run meant that run, and should not have to put the setting back afterwards.
    /// </para>
    /// </remarks>
    public RenderBackend Backend { get; init; } = RenderBackend.Automatic;

    /// <summary>
    /// Whether DLSS is allowed to denoise the traced light as well as upscale it.
    /// </summary>
    /// <remarks>
    /// On by default and only meaningful with DLSS and ray tracing both on. The two
    /// denoisers are the same job done twice, and doing it twice is what smears a picture;
    /// off keeps the engine's own filter, which is what somebody comparing the two wants.
    /// </remarks>
    public bool RayReconstruction { get; init; } = true;

    /// <summary>Which of DLSS's trained models to ask for, or nought for its own choice.</summary>
    /// <remarks>The letter's ordinal. See <see cref="DlssPresets"/>.</remarks>
    public int DlssPreset { get; init; }

    /// <summary>
    /// Whether NVIDIA's neural rendering network reworks the picture as it upscales it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needs only <c>nvngx_dlssnr.dll</c> in the libs folder — not Streamline, and not the
    /// plugin that would ordinarily drive it, because the driver installed on most machines
    /// refuses to load that plugin whatever else is present. See
    /// <see cref="Rendering.Upscaling.Ngx"/> for why, and for what is done instead.
    /// </para>
    /// <para>
    /// Off by default. It changes how the game looks rather than fixing how it looks, and a
    /// port whose whole business is the 1999 picture should not restyle it unasked.
    /// </para>
    /// </remarks>
    public bool NeuralUplift { get; init; }

    /// <summary>How much of the neural effect to apply, from nothing to all of it.</summary>
    public float NeuralIntensity { get; init; } = 1f;

    /// <summary>How hard local contrast is lifted.</summary>
    public float NeuralLocalTone { get; init; } = 1f;

    /// <summary>How hard the picture's overall tone is reworked.</summary>
    public float NeuralGlobalTone { get; init; } = 1f;

    /// <summary>How much fine structure and micro-detail is rebuilt.</summary>
    public float NeuralLocalStructure { get; init; } = 1f;

    /// <summary>Whether skin takes the general structure strength rather than its own.</summary>
    public bool NeuralSkinFollowsStructure { get; init; } = true;

    /// <summary>How much detail skin takes, when it is not following.</summary>
    public float NeuralSkinStructure { get; init; } = 0.5f;

    /// <summary>Whether the network works out for itself which pixels are skin.</summary>
    public bool NeuralAutoSkinMask { get; init; } = true;

    /// <summary>Which of the network's trained weights, or nought for its own choice.</summary>
    public int NeuralPreset { get; init; }

    /// <summary>Which of the network's looks, or nought for its own choice.</summary>
    public int NeuralStyle { get; init; }

    /// <summary>Whether to ask the display for a high dynamic range colour space.</summary>
    public bool HighDynamicRange { get; init; }

    /// <summary>Which encoding to ask for, where the display offers a choice.</summary>
    public HdrTransfer HdrTransfer { get; init; } = HdrTransfer.Automatic;

    /// <summary>What the standard-range picture is put through.</summary>
    public ToneMapping ToneMapping { get; init; } = ToneMapping.Clip;

    /// <summary>Where a sheet of white paper sits, in candelas per square metre.</summary>
    public float PaperWhiteNits { get; init; } = 200f;

    /// <summary>The brightest the display can go.</summary>
    public float PeakNits { get; init; } = 1000f;

    /// <summary>Where a sunlit surface is allowed to reach.</summary>
    public float SunNits { get; init; } = 800f;

    /// <summary>Where a lamp, a bulb or a lit window is allowed to reach.</summary>
    public float LightNits { get; init; } = 1000f;

    /// <summary>
    /// Whether a foliage card is replaced by a modelled tree where one has been grown.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EnhancedTextures"/> because it is a separate judgement and
    /// a separate cost. A better bitmap on a wall is free; a wood of modelled trees is
    /// eighty thousand triangles where there were nine thousand, and it changes an outdoor
    /// scene's silhouette rather than its surface. Somebody who wants the 1999 outline
    /// should be able to have it with the rest of the enhancement left on.
    /// </remarks>
    public bool ModelledTrees { get; init; } = true;

    /// <summary>
    /// Whether a scene's painted horizon is replaced by reconstructed terrain where a
    /// set has been built for it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ModelledTrees"/> for the same reason that is separate
    /// from <see cref="EnhancedTextures"/>: it is its own judgement and its own cost —
    /// half a million triangles of hillside behind the room — and it changes what the
    /// horizon <em>is</em> rather than how it is drawn. Off, or with no terrain data
    /// installed, every scene keeps the 1999 sky painting exactly as shipped.
    /// </remarks>
    public bool TerrainBackdrop { get; init; } = true;

    /// <summary>
    /// Whether a room's own objects are drawn from improved geometry where any has been
    /// built for them.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ModelledTrees"/> and <see cref="TerrainBackdrop"/> for the
    /// third time and the same reason: its own judgement and its own cost. What it changes
    /// is the edges of the things in a room — a table whose top has a width, a lantern
    /// whose sides are a curve — and somebody who wants 1999's infinitely sharp edges
    /// should be able to keep them with the rest of the enhancement on. Off, or with
    /// nothing built, every room is drawn exactly as it shipped.
    /// </remarks>
    public bool ImprovedSceneGeometry { get; init; } = true;

    /// <summary>
    /// How many times a character's head is subdivided, or zero to draw it as authored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the head. A character's body deforms and is addressed by vertex index, so it
    /// cannot be refined without invalidating that character's clips; the head is rigid and
    /// can. See <see cref="Actors.HeadRefinement"/>.
    /// </para>
    /// <para>
    /// Two by default, which is where a twenty-triangle hairdo stops reading as a polygon.
    /// Zero is a real answer for somebody who wants the 1999 outline.
    /// </para>
    /// </remarks>
    public int SmoothHeads { get; init; } = 2;

    /// <summary>Whether the camera travels between angles or cuts.</summary>
    public bool CameraGlide { get; init; } = true;

    /// <summary>Whether the story is allowed to move the camera for effect.</summary>
    public bool Cinematics { get; init; } = true;

    /// <summary>
    /// Whether the camera may fly out of the room and keep flying through a cutscene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things at once, and they are the same thing: the artists' camera shell stops
    /// fencing the view in, and <c>SceneUpdate.Directing</c> stops taking the controls away
    /// while the story is telling something. It is the escape hatch the original makes for
    /// <c>Tools::Active</c>, and what <c>--free-camera</c> has always asked for.
    /// </para>
    /// <para>
    /// Off by default and deliberately not an assist: it changes nothing the story asks of
    /// the player, and it is here for somebody photographing the game rather than playing
    /// it. Turning it on is how the geometry gets looked at from outside, which is a
    /// picture no part of the game was built to survive — so the row says what it does.
    /// </para>
    /// </remarks>
    public bool FreeCamera { get; init; }

    /// <summary>Whether what is said is also written.</summary>
    public bool Captions { get; init; } = true;

    /// <summary>
    /// Whether every voice comes from the middle rather than from where its speaker stands.
    /// </summary>
    /// <remarks>
    /// An accessibility option — Plan/03 section 8 — and not a mixing preference: a line
    /// placed across a room is harder to make out, and somebody who needs the words has to
    /// be able to ask for them plainly. Gabriel is centred either way, because the player
    /// is him.
    /// </remarks>
    public bool CenterAllDialogue { get; init; }

    /// <summary>
    /// How much faster a double-click sends Gabriel.
    /// </summary>
    /// <remarks>
    /// The stride is played faster by the same amount, or the feet slide. One means a
    /// double-click does nothing, which is a legitimate answer for somebody who wants the
    /// pace the game was authored at.
    /// </remarks>
    public float HurryFactor { get; init; } = 2f;

    /// <summary>Whether the intro plays on starting.</summary>
    public bool PlayIntro { get; init; } = true;

    /// <summary>
    /// Whether the game's easter-egg content is switched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EGG</c> is one of the built-in cases an action file may be written against, and
    /// the original hard-codes it false with a note saying it should return true when
    /// easter eggs are enabled — the switch itself never shipped, so nothing behind the
    /// case has ever been reachable in a playing game. This is that switch. It sets the
    /// story's <c>EGG</c> flag, which is what the case reads and what Sidney's sixth email
    /// is written against.
    /// </para>
    /// <para>
    /// Off by default, because the game as it shipped is the game as it shipped, and
    /// somebody playing GK3 for the first time should meet it that way.
    /// </para>
    /// </remarks>
    public bool EasterEggs { get; init; }

    /// <summary>
    /// Whether Gabriel starts the moped afternoon with the moustache already made, and
    /// wears it from then on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cat-hair moustache is the end of GK3's most notorious chain: spray the cat,
    /// tape the hole it squeezes through, peel the fur off the tape, and stick that to a
    /// packet of maple syrup. This hands over the finished <c>BLACK_MOUSTACHE</c> at the
    /// start of Day 1, 2pm and skips the whole of it. What is left — the cap, the coat and
    /// the marker on Mosely's passport — is assembly rather than puzzle, and taking that
    /// away too would leave the moped shop with nothing in front of it at all.
    /// </para>
    /// <para>
    /// And he wears it. The game has no art for a moustached Gabriel — the original never
    /// shows the disguise on him — so it is the item's own picture, cut out and pasted onto
    /// his face where his lip is. It is a joke rather than a fidelity claim, which is why
    /// it is one switch with the assistance rather than a setting of its own.
    /// </para>
    /// </remarks>
    public bool AlwaysWearsMoustache { get; init; }

    /// <summary>Whether nothing the story does is allowed to kill Gabriel.</summary>
    /// <remarks>
    /// <para>
    /// Five scripts can kill him, all of them in the temple under the château on the last
    /// night, and every one of them goes through the same door: a <c>Die</c> function that
    /// stops the music, puts up the death screen and resets the puzzle behind it. With this
    /// on, that door is answered differently — the puzzle is reset and started again, and
    /// the death screen never appears. See <see cref="Assists"/>.
    /// </para>
    /// <para>
    /// The staging still plays. He falls, or the pendulum swings, or the demon strikes:
    /// those are the scene showing the player what went wrong, and they are already over by
    /// the time the game says he is dead. What is taken away is the death itself and the
    /// attempt it costs.
    /// </para>
    /// </remarks>
    public bool PlotArmour { get; init; }

    /// <summary>Where the settings live for this user.</summary>
    /// <remarks>
    /// <c>%AppData%\GK3Reborn</c> on Windows, <c>~/.config/GK3Reborn</c> on Linux and
    /// <c>~/Library/Application Support/GK3Reborn</c> on macOS. See
    /// <see cref="InstallPaths.UserData"/> for why the last of those is named rather than
    /// left to the BCL.
    /// </remarks>
    public static string DefaultPath => Path.Combine(InstallPaths.UserData, "settings.json");

    /// <summary>The ray-tracing level this picture quality asks for.</summary>
    /// <remarks>
    /// Derived, and kept out of the file. It and the two plans below are views of settings
    /// that are already stored; writing them as well would put the same decision in the
    /// file twice, in a form nothing reads back — and a hand-edited copy of one of them
    /// would then silently do nothing.
    /// </remarks>
    [JsonIgnore]
    public RayTracingQuality Quality => Picture switch
    {
        PictureQuality.Original => RayTracingQuality.None,
        PictureQuality.Improved => RayTracingQuality.Low,
        PictureQuality.High => RayTracingQuality.Medium,
        _ => RayTracingQuality.High,
    };

    /// <summary>What the renderer should do about upscaling.</summary>
    /// <remarks>
    /// Built rather than stored, so that there is one place these settings mean something
    /// and the renderer never sees a half-applied change. Whether the colour is high
    /// dynamic range is deliberately not set here: it is a fact about the output chain
    /// the renderer owns, and asserting it from a settings file would let the two disagree.
    /// </remarks>
    [JsonIgnore]
    public UpscalePlan Upscaling => new UpscalePlan
    {
        Kind = Upscaler,
        Quality = UpscalerQuality,
        Sharpen = Sharpening,
        Sharpness = Sharpness,
        FrameGeneration = FrameGeneration,
        Latency = Latency,
        RayReconstruction = RayReconstruction,
        DlssPreset = DlssPreset,

        Neural = new Rendering.Upscaling.NeuralUplift
        {
            Enabled = NeuralUplift,
            Intensity = NeuralIntensity,
            LocalTone = NeuralLocalTone,
            GlobalTone = NeuralGlobalTone,
            LocalStructure = NeuralLocalStructure,
            SkinFollowsStructure = NeuralSkinFollowsStructure,
            SkinStructure = NeuralSkinStructure,
            AutoSkinMask = NeuralAutoSkinMask,
            Preset = NeuralPreset,
            Style = NeuralStyle,
        },
    }.Sane();

    /// <summary>What the renderer should do about the display.</summary>
    [JsonIgnore]
    public OutputPlan Output => new OutputPlan
    {
        HighDynamicRange = HighDynamicRange,
        Transfer = HdrTransfer,
        ToneMap = ToneMapping,
        PaperWhiteNits = PaperWhiteNits,
        PeakNits = PeakNits,
        SunNits = SunNits,
        LightNits = LightNits,
    }.Sane();

    /// <summary>Reads the settings, or returns the defaults.</summary>
    /// <param name="path">Where to read from, or null for this user's own.</param>
    /// <returns>The settings; never null and never out of range.</returns>
    /// <remarks>
    /// A missing file is the ordinary case — it is what a first run looks like — and an
    /// unreadable one is treated the same way. Refusing to start because a preferences file
    /// has a stray comma in it would be the worst possible trade.
    /// </remarks>
    public static Settings Load(string? path = null)
    {
        string file = path ?? DefaultPath;

        try
        {
            if (!File.Exists(file))
            {
                return new Settings();
            }

            return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json)
                    ?? new Settings())
                .Sane();
        }
        catch (Exception error) when (error is IOException
                                          or JsonException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return new Settings();
        }
    }

    /// <summary>Writes the settings.</summary>
    /// <param name="path">Where to write, or null for this user's own.</param>
    /// <returns>True when they were written.</returns>
    /// <remarks>
    /// Failure is reported rather than thrown. Somebody with a read-only profile should
    /// still be able to turn the music down for this session.
    /// </remarks>
    public bool Save(string? path = null)
    {
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
            File.WriteAllText(file, JsonSerializer.Serialize(Sane(), Json));
            return true;
        }
        catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The same settings with every value inside its range.</summary>
    public Settings Sane() => this with
    {
        MasterVolume = Level(MasterVolume),
        MusicVolume = Level(MusicVolume),
        AmbienceVolume = Level(AmbienceVolume),
        EffectsVolume = Level(EffectsVolume),
        DialogueVolume = Level(DialogueVolume),
        Speakers = Enum.IsDefined(Speakers) ? Speakers : SpeakerLayout.Stereo,
        Picture = Enum.IsDefined(Picture) ? Picture : PictureQuality.High,
        HurryFactor = float.IsFinite(HurryFactor) ? Math.Clamp(HurryFactor, 1f, 4f) : 2f,
        SmoothHeads = Math.Clamp(SmoothHeads, 0, Actors.HeadRefinement.MaximumLevels),

        Display = Enum.IsDefined(Display) ? Display : WindowMode.Windowed,

        TextScale = float.IsFinite(TextScale)
            ? Math.Clamp(TextScale, SmallestText, LargestText)
            : 1f,

        // Nought means "the monitor's own", which is the answer for a display nobody has
        // chosen a size for. Anything else is clamped to something a swapchain can be made
        // at; the driver clamps again to what the surface allows.
        DisplayWidth = DisplayWidth <= 0 ? 0 : Math.Clamp(DisplayWidth, 320, 16_384),
        DisplayHeight = DisplayHeight <= 0 ? 0 : Math.Clamp(DisplayHeight, 240, 16_384),

        Upscaler = Enum.IsDefined(Upscaler) ? Upscaler : UpscalerKind.Off,
        UpscalerQuality = Enum.IsDefined(UpscalerQuality)
            ? UpscalerQuality
            : UpscalerQuality.Quality,
        Sharpness = float.IsFinite(Sharpness) ? Math.Clamp(Sharpness, 0f, 1f) : 0.5f,
        FrameGeneration = Enum.IsDefined(FrameGeneration) ? FrameGeneration : FrameGeneration.Off,
        Latency = Enum.IsDefined(Latency) ? Latency : LatencyMode.On,

        // A machine that is not Windows cannot have Direct3D whatever the file says, and a
        // settings file copied from one that was is not a reason to fail to start.
        Backend = Enum.IsDefined(Backend) && RenderBackends.IsPossible(Backend)
            ? Backend
            : RenderBackend.Automatic,
        DlssPreset = Math.Clamp(DlssPreset, 0, DlssPresets.Highest),

        HdrTransfer = Enum.IsDefined(HdrTransfer) ? HdrTransfer : HdrTransfer.Automatic,
        ToneMapping = Enum.IsDefined(ToneMapping) ? ToneMapping : ToneMapping.Clip,

        // The luminances are clamped in one place, by the plan that consumes them, because
        // their bounds depend on each other — a peak below paper white is not a peak — and
        // two implementations of that rule would be one too many.
        PaperWhiteNits = Sensible(PaperWhiteNits, 200f),
        PeakNits = Sensible(PeakNits, 1000f),
        SunNits = Sensible(SunNits, 800f),
        LightNits = Sensible(LightNits, 1000f),
    };

    /// <summary>A luminance that is at least a number, before the plan bounds it.</summary>
    private static float Sensible(float value, float fallback) =>
        float.IsFinite(value) && value > 0f ? value : fallback;

    /// <summary>Hands the audio levels to the mixer.</summary>
    /// <param name="audio">The device, or null when there is none.</param>
    /// <remarks>
    /// <para>
    /// Called whenever a level changes as well as at startup, so a slider is heard while it
    /// is being dragged rather than after the menu is closed.
    /// </para>
    /// <para>
    /// Every bus is set, not only the ones something plays on today. There are nine buses
    /// and five sliders; a bus left out is a sound the player cannot turn down, and which
    /// one that is would depend on which of two near-identical names the code that plays it
    /// happened to pick. Speech is a case in point: it is played on
    /// <see cref="AudioBus.DialogueCentered"/> and not on <see cref="AudioBus.DialogueInWorld"/>.
    /// </para>
    /// </remarks>
    public void ApplyTo(IAudioBackend? audio)
    {
        if (audio is null)
        {
            return;
        }

        audio.SetBusGain(AudioBus.Master, MasterVolume);

        audio.SetBusGain(AudioBus.Music, MusicVolume);
        audio.SetBusGain(AudioBus.Cinematics, MusicVolume);

        audio.SetBusGain(AudioBus.Ambience, AmbienceVolume);

        audio.SetBusGain(AudioBus.Effects, EffectsVolume);
        audio.SetBusGain(AudioBus.Foley, EffectsVolume);
        audio.SetBusGain(AudioBus.UserInterface, EffectsVolume);

        audio.SetBusGain(AudioBus.DialogueInWorld, DialogueVolume);
        audio.SetBusGain(AudioBus.DialogueCentered, DialogueVolume);
    }

    private static float Level(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;

    private static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

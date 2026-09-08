using System.Text.Json;
using GK3Reborn.Rendering.Geometry;
using System.Text.Json.Serialization;
using GK3Reborn.Audio;
using GK3Reborn.Foundation;
using GK3Reborn.Platform;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Upscaling;
using GK3Reborn.Content;

namespace GK3Reborn.Game;

/// <summary>How much of the picture the player wants paid for.</summary>
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

    /// <summary>
    /// Whether only real sources of light are allowed to light the room.
    /// </summary>
    public bool RealisticLighting { get; init; }

    /// <summary>
    /// Whether a polished floor shows the room standing on it.
    /// </summary>
    public bool FloorReflections { get; init; } = true;

    /// <summary>How strong a reflection off a polished surface is, from zero to two.</summary>
    public float Reflectivity { get; init; } = 1f;

    /// <summary>Whether the window has a border, covers a monitor, or takes one over.</summary>
    public WindowMode Display { get; init; } = WindowMode.Windowed;

    /// <summary>How wide the window is, in pixels, or nought for the monitor's own size.</summary>
    public int DisplayWidth { get; init; }

    /// <summary>How tall it is.</summary>
    public int DisplayHeight { get; init; }

    /// <summary>
    /// How much larger or smaller the interface's letters are than the size the window
    /// would pick on its own.
    /// </summary>
    public float TextScale { get; init; } = 1f;

    /// <summary>The smallest the interface's letters may be asked to go.</summary>
    public const float SmallestText = 0.6f;

    /// <summary>The largest.</summary>
    public const float LargestText = 1.6f;

    /// <summary>Whether frames wait for the display.</summary>
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
    public RenderBackend Backend { get; init; } = RenderBackend.Automatic;

    /// <summary>
    /// Whether DLSS is allowed to denoise the traced light as well as upscale it.
    /// </summary>
    public bool RayReconstruction { get; init; } = true;

    /// <summary>Which of DLSS's trained models to ask for, or nought for its own choice.</summary>
    public int DlssPreset { get; init; }

    /// <summary>
    /// Whether NVIDIA's neural rendering network reworks the picture as it upscales it.
    /// </summary>
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
    public bool ModelledTrees { get; init; } = true;

    /// <summary>
    /// Whether a scene's painted horizon is replaced by reconstructed terrain where a
    /// set has been built for it.
    /// </summary>
    public bool TerrainBackdrop { get; init; } = true;

    /// <summary>
    /// Whether birds fly in the sky over the outdoor rooms that have them.
    /// </summary>
    public bool Birds { get; init; } = true;

    /// <summary>
    /// Whether the two towns the game modelled a corner of are drawn whole.
    /// </summary>
    public bool RebuiltTowns { get; init; } = true;

    /// <summary>
    /// Whether a room's own objects are drawn from improved geometry where any has been
    /// built for them.
    /// </summary>
    public bool ImprovedSceneGeometry { get; init; } = true;

    /// <summary>
    /// Whether a railing, a fence or a chain is given the thickness of the thing drawn on
    /// it.
    /// </summary>
    public bool ThickCutoutCards { get; init; } = true;

    /// <summary>
    /// Whether the room's own surfaces are drawn only on the side they face.
    /// </summary>
    public bool CullBackFaces { get; init; } = true;

    /// <summary>
    /// How many times a character's head is subdivided, or zero to draw it as authored.
    /// </summary>
    public int SmoothHeads { get; init; } = 2;

    /// <summary>Whether the camera travels between angles or cuts.</summary>
    public bool CameraGlide { get; init; } = true;

    /// <summary>Whether the story is allowed to move the camera for effect.</summary>
    public bool Cinematics { get; init; } = true;

    /// <summary>
    /// Whether the camera may fly out of the room and keep flying through a cutscene.
    /// </summary>
    public bool FreeCamera { get; init; }

    /// <summary>
    /// Which language the game is read, spoken and written in.
    /// </summary>
    public string Language { get; init; } = Content.GameLanguage.Default.Code;

    /// <summary>Whether what is said is also written.</summary>
    public bool Captions { get; init; } = true;

    /// <summary>
    /// Whether what is said in a film is also written.
    /// </summary>
    public bool MovieSubtitles { get; init; } = true;

    /// <summary>
    /// Whether every voice comes from the middle rather than from where its speaker stands.
    /// </summary>
    public bool CenterAllDialogue { get; init; }

    /// <summary>
    /// How much faster a double-click sends Gabriel.
    /// </summary>
    public float HurryFactor { get; init; } = 2f;

    /// <summary>Whether the intro plays on starting.</summary>
    public bool PlayIntro { get; init; } = true;

    /// <summary>
    /// Whether the game's easter-egg content is switched on.
    /// </summary>
    public bool EasterEggs { get; init; }

    /// <summary>
    /// Whether Gabriel starts the moped afternoon with the moustache already made, and
    /// wears it from then on.
    /// </summary>
    public bool AlwaysWearsMoustache { get; init; }

    /// <summary>Whether nothing the story does is allowed to kill Gabriel.</summary>
    public bool PlotArmour { get; init; }

    /// <summary>Whether Gabriel catches the pendulum himself.</summary>
    public bool CatchesPendulum { get; init; }

    /// <summary>
    /// How much of the content the game shipped with and cannot reach is put back.
    /// </summary>
    public CutContentTier RestoredContent { get; init; } = CutContentTier.None;

    /// <summary>
    /// Which key and which pad button do which job, or null where nobody has said.
    /// </summary>
    public StoredBindings? Bindings { get; init; }

    /// <summary>Whether the gamepad's left stick moves the pointer.</summary>
    public bool GamepadCursor { get; init; } = true;

    /// <summary>How fast it moves it, in logical pixels a second at full deflection.</summary>
    public float GamepadCursorSpeed { get; init; } = 1200f;

    /// <summary>Where the settings live for this user.</summary>
    public static string DefaultPath => Path.Combine(InstallPaths.UserData, "settings.json");

    /// <summary>The ray-tracing level this picture quality asks for.</summary>
    [JsonIgnore]
    public RayTracingQuality Quality => Picture switch
    {
        PictureQuality.Original => RayTracingQuality.None,
        PictureQuality.Improved => RayTracingQuality.Low,
        PictureQuality.High => RayTracingQuality.Medium,
        _ => RayTracingQuality.High,
    };

    /// <summary>What the renderer should do about upscaling.</summary>
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

        // Normalised rather than merely checked: "FR" and " fr " are somebody editing the
        // file by hand and mean French, and storing them as typed would make two settings
        // files that say the same thing compare unequal.
        Language = Content.GameLanguage.Of(Language).Code,

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

        Reflectivity = float.IsFinite(Reflectivity)
            ? Math.Clamp(Reflectivity, 0f, MostReflective)
            : 1f,

        GamepadCursorSpeed = float.IsFinite(GamepadCursorSpeed)
            ? Math.Clamp(GamepadCursorSpeed, SlowestCursor, FastestCursor)
            : 1200f,
    };

    /// <summary>The strongest a reflection may be made.</summary>
    public const float MostReflective = 2f;

    /// <summary>The slowest the stick may drive the pointer, in pixels a second.</summary>
    public const float SlowestCursor = 300f;

    /// <summary>And the fastest.</summary>
    public const float FastestCursor = 3000f;

    /// <summary>A luminance that is at least a number, before the plan bounds it.</summary>
    private static float Sensible(float value, float fallback) =>
        float.IsFinite(value) && value > 0f ? value : fallback;

    /// <summary>Hands the audio levels to the mixer.</summary>
    /// <param name="audio">The device, or null when there is none.</param>
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

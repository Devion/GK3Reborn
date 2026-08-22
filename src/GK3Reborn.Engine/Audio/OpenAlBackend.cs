using System.Globalization;
using System.Numerics;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace GK3Reborn.Audio;

/// <summary>
/// The audio device, over OpenAL Soft.
/// </summary>
/// <remarks>
/// <para>
/// One buffer per sound, uploaded the first time it is asked for and kept, and a pool of
/// sources handed out as things play. GK3 never has many sounds going at once — a line of
/// dialogue, a room tone, a door — so the pool is small and a sound that cannot get a
/// source is dropped rather than queued. Dropping is the right failure: a footstep that
/// arrives late is worse than one that never arrives.
/// </para>
/// <para>
/// Buses are gain multipliers applied when a source starts and when a gain changes.
/// OpenAL has no bus concept, so this is the mixer: every voice remembers which bus it is
/// on, so turning dialogue down turns down the line being spoken and not merely the next
/// one.
/// </para>
/// <para>
/// Opening the device is allowed to fail. A machine with no sound card, or a headless run,
/// gets no backend and a diagnostic saying so, because refusing to start the game over it
/// would be worse than running it quietly.
/// </para>
/// </remarks>
public sealed unsafe class OpenAlBackend : IAudioBackend
{
    private const int Sources = 24;

    private readonly AL _al;
    private readonly ALContext _alc;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly Dictionary<string, uint> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AudioBus, float> _gains = [];
    private readonly List<Voice> _voices = [];

    private int _next = 1;
    private bool _disposed;
    private Vector3 _ear;

    /// <summary>The EFX extension, or null on a device without it.</summary>
    /// <remarks>
    /// Optional by design. Everything here works without it except the muffling, and a
    /// device that cannot filter should still place its sounds.
    /// </remarks>
    private readonly EffectExtension? _effects;

    private OpenAlBackend(
        AL al, ALContext alc, Device* device, Context* context, SpeakerLayout layout)
    {
        _al = al;
        _alc = alc;
        _device = device;
        _context = context;
        RequestedLayout = layout;
        ActualLayout = layout;

        // Inverse rolloff clamped at both ends, which is the model the original's own audio
        // uses and what its .STK min and max distances describe: full volume within the
        // minimum, falling as the reciprocal of distance after it, level again past the
        // maximum. The default model is inverse *unclamped*, which keeps getting quieter
        // for ever and never quite reaches silence.
        _al.DistanceModel(DistanceModel.InverseDistanceClamped);

        _effects = al.TryGetExtension(out EffectExtension effects) ? effects : null;

        foreach (AudioBus bus in Enum.GetValues<AudioBus>())
        {
            _gains[bus] = 1f;
        }
    }

    /// <inheritdoc/>
    public SpeakerLayout RequestedLayout { get; }

    /// <inheritdoc/>
    public SpeakerLayout ActualLayout { get; }

    /// <inheritdoc/>
    public int Playing => _voices.Count;

    /// <summary>What the device called itself.</summary>
    public string DeviceName { get; private init; } = "unknown";

    /// <summary>Opens the default output device.</summary>
    /// <param name="layout">The layout the player asked for.</param>
    /// <param name="diagnostics">Receives a reason when there is no device.</param>
    /// <returns>The backend, or null when nothing could be opened.</returns>
    public static OpenAlBackend? Open(SpeakerLayout layout, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        AL al;
        ALContext alc;

        try
        {
            alc = ALContext.GetApi();
            al = AL.GetApi();
        }
        catch (Exception ex)
            when (ex is DllNotFoundException or EntryPointNotFoundException or FileNotFoundException)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1130", DiagnosticSeverity.Warning,
                "No OpenAL library, so the game runs without sound.",
                "audio", null, "an OpenAL Soft runtime", ex.Message,
                "Install OpenAL Soft, or accept a silent game."));

            return null;
        }

        Device* device = alc.OpenDevice(string.Empty);

        if (device is null)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1131", DiagnosticSeverity.Warning,
                "No audio output device, so the game runs without sound.",
                "audio", null, "a default output device", "none",
                "Check that the machine has a sound device that is not held exclusively."));

            return null;
        }

        Context* context = alc.CreateContext(device, null);

        if (context is null || !alc.MakeContextCurrent(context))
        {
            alc.CloseDevice(device);

            diagnostics.Add(new Diagnostic(
                "GK3R1132", DiagnosticSeverity.Warning,
                "The audio device opened but would not give a context.",
                "audio", null, "a current OpenAL context", "none",
                "Another process may hold the device exclusively."));

            return null;
        }

        string name = alc.GetContextProperty(device, GetContextString.DeviceSpecifier);

        return new OpenAlBackend(al, alc, device, context, layout)
        {
            DeviceName = string.IsNullOrWhiteSpace(name) ? "unknown" : name,
        };
    }

    /// <inheritdoc/>
    public void SetBusGain(AudioBus bus, float gain)
    {
        _gains[bus] = Math.Clamp(gain, 0f, 4f);

        foreach (Voice voice in _voices)
        {
            if (voice.Bus == bus || bus == AudioBus.Master)
            {
                _al.SetSourceProperty(voice.Source, SourceFloat.Gain, Gain(voice));
            }
        }
    }

    /// <inheritdoc/>
    public void SetVoiceGain(AudioVoice voice, float gain)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (Voice playing in _voices)
        {
            if (playing.Id != voice.Id)
            {
                continue;
            }

            playing.Level = Math.Clamp(gain, 0f, 1f);
            _al.SetSourceProperty(playing.Source, SourceFloat.Gain, Gain(playing));
            return;
        }
    }

    /// <inheritdoc/>
    public AudioVoice Play(
        WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (sound.Samples.Length == 0 || _voices.Count >= Sources)
        {
            return AudioVoice.None;
        }

        uint buffer = Buffer(sound);
        uint source = _al.GenSource();

        _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(source, SourceBoolean.Looping, repeat);
        _al.SetSourceProperty(source, SourceFloat.Gain, Gain(bus));

        if (at is { } placed)
        {
            // In the room. A stereo buffer cannot be placed — OpenAL plays it flat at the
            // head whatever else is asked for — but every ambience in the game is mono, and
            // saying so is better than a fountain that follows the player about.
            _al.SetSourceProperty(source, SourceBoolean.SourceRelative, false);
            _al.SetSourceProperty(
                source, SourceVector3.Position, placed.Position.X, placed.Position.Y, placed.Position.Z);

            _al.SetSourceProperty(
                source, SourceFloat.ReferenceDistance, MathF.Max(1f, placed.Minimum));

            _al.SetSourceProperty(
                source, SourceFloat.MaxDistance,
                MathF.Max(placed.Minimum + 1f, placed.Maximum));

            _al.SetSourceProperty(source, SourceFloat.RolloffFactor, 1f);
        }
        else
        {
            // At the head: a voice-over, a menu click, anything the player is meant to hear
            // the same wherever they stand.
            _al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
            _al.SetSourceProperty(source, SourceVector3.Position, 0f, 0f, 0f);
        }

        _al.SourcePlay(source);

        var voice = new Voice(_next++, source, bus) { At = at };
        _voices.Add(voice);

        Muffle(voice);

        return new AudioVoice(voice.Id);
    }

    /// <inheritdoc/>
    public void Move(AudioVoice voice, Vector3 position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < _voices.Count; i++)
        {
            if (_voices[i].Id != voice.Id || _voices[i].At is not { } at)
            {
                continue;
            }

            _voices[i].At = at with { Position = position };
            _al.SetSourceProperty(
                _voices[i].Source, SourceVector3.Position, position.X, position.Y, position.Z);

            Muffle(_voices[i]);
        }
    }

    /// <inheritdoc/>
    public void Listen(Vector3 position, Vector3 forward, Vector3 up)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _ear = position;

        _al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);

        // Six floats: where the head faces, then which way is up for it. OpenAL takes them
        // as one array and will silently ignore a call that passes them any other way.
        float* orientation = stackalloc float[6]
        {
            forward.X, forward.Y, forward.Z,
            up.X, up.Y, up.Z,
        };

        _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);

        foreach (Voice voice in _voices)
        {
            Muffle(voice);
        }
    }

    /// <summary>Takes the top off a sound that is far away.</summary>
    /// <remarks>
    /// <para>
    /// Distance does two things to a sound and OpenAL only does one of them by itself. It
    /// makes it quieter, which the rolloff handles, and it takes the high frequencies out
    /// of it, which is most of what tells a listener that something is far off rather than
    /// merely quiet. A fountain across a square is a hiss; the same fountain turned down is
    /// still a fountain at your feet.
    /// </para>
    /// <para>
    /// A low-pass through EFX, opened once and skipped where the device has no EFX at all.
    /// The curve is a straight line from no filtering at the sound's own minimum distance to
    /// a quarter of the high frequencies at its maximum — a stand-in for air absorption
    /// rather than a model of it, and nothing to do with what is in the way.
    /// </para>
    /// </remarks>
    private void Muffle(Voice voice)
    {
        if (_effects is null || voice.At is not { } at)
        {
            return;
        }

        float distance = Vector3.Distance(_ear, at.Position);
        float span = MathF.Max(1f, at.Maximum - at.Minimum);
        float far = Math.Clamp((distance - at.Minimum) / span, 0f, 1f);

        if (voice.Filter == 0)
        {
            voice.Filter = _effects.GenFilter();
            _effects.SetFilterProperty(voice.Filter, FilterInteger.FilterType, (int)FilterType.Lowpass);
        }

        _effects.SetFilterProperty(voice.Filter, FilterFloat.LowpassGain, 1f);
        _effects.SetFilterProperty(voice.Filter, FilterFloat.LowpassGainHF, 1f - (0.75f * far));

        _effects.SetSourceProperty(
            voice.Source, EFXSourceInteger.DirectFilter, (int)voice.Filter);
    }

    /// <inheritdoc/>
    public void Silence(AudioVoice voice)
    {
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            if (_voices[i].Id == voice.Id)
            {
                Release(_voices[i]);
                _voices.RemoveAt(i);
            }
        }
    }

    /// <inheritdoc/>
    public void StopBus(AudioBus bus)
    {
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            if (_voices[i].Bus == bus)
            {
                Release(_voices[i]);
                _voices.RemoveAt(i);
            }
        }
    }

    /// <inheritdoc/>
    public bool IsPlaying(AudioVoice voice)
    {
        foreach (Voice candidate in _voices)
        {
            if (candidate.Id == voice.Id)
            {
                _al.GetSourceProperty(candidate.Source, GetSourceInteger.SourceState, out int state);
                return state == (int)SourceState.Playing;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Update()
    {
        if (_disposed)
        {
            return;
        }

        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            _al.GetSourceProperty(_voices[i].Source, GetSourceInteger.SourceState, out int state);

            if (state is not ((int)SourceState.Playing or (int)SourceState.Paused))
            {
                Release(_voices[i]);
                _voices.RemoveAt(i);
            }
        }
    }

    /// <summary>What the device thinks a voice is set to, read back from it.</summary>
    /// <param name="voice">The handle.</param>
    /// <returns>The properties that decide where it is and how far it carries.</returns>
    /// <remarks>
    /// Read back rather than remembered, because the thing worth knowing is what the device
    /// took, not what it was told. A stereo buffer, for one, is played flat at the head
    /// whatever position it is given.
    /// </remarks>
    public string Describe(AudioVoice voice)
    {
        foreach (Voice candidate in _voices)
        {
            if (candidate.Id != voice.Id)
            {
                continue;
            }

            _al.GetSourceProperty(candidate.Source, SourceBoolean.SourceRelative, out bool relative);
            _al.GetSourceProperty(candidate.Source, SourceFloat.ReferenceDistance, out float reference);
            _al.GetSourceProperty(candidate.Source, SourceFloat.MaxDistance, out float maximum);
            _al.GetSourceProperty(candidate.Source, SourceFloat.RolloffFactor, out float rolloff);
            _al.GetSourceProperty(candidate.Source, SourceVector3.Position, out System.Numerics.Vector3 where);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"head-relative {relative}, at {where:F0}, full within {reference:F0}, " +
                $"clamped past {maximum:F0}, rolloff {rolloff:F1}, " +
                $"filter {(candidate.Filter != 0 ? "on" : "none")}");
        }

        return "not playing";
    }

    /// <summary>Describes the device, for the launcher's banner.</summary>
    /// <returns>The device name and how much of it is in use.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{DeviceName}, {_buffers.Count} sound(s) resident, {_voices.Count}/{Sources} voices");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Voice voice in _voices)
        {
            Release(voice);
        }

        _voices.Clear();

        foreach (uint buffer in _buffers.Values)
        {
            _al.DeleteBuffer(buffer);
        }

        _buffers.Clear();

        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);

        _al.Dispose();
        _alc.Dispose();
    }

    private float Gain(AudioBus bus) =>
        _gains.GetValueOrDefault(bus, 1f) * _gains.GetValueOrDefault(AudioBus.Master, 1f);

    /// <summary>How loud a voice should be: its bus, its master, and its own level.</summary>
    private float Gain(Voice voice) => Gain(voice.Bus) * voice.Level;

    /// <summary>Uploads a sound, or returns the buffer it already has.</summary>
    private uint Buffer(WavFile sound)
    {
        if (_buffers.TryGetValue(sound.Name, out uint existing))
        {
            return existing;
        }

        uint buffer = _al.GenBuffer();

        _al.BufferData(
            buffer,
            sound.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16,
            sound.Samples,
            sound.SampleRate);

        _buffers[sound.Name] = buffer;
        return buffer;
    }

    private void Release(Voice voice)
    {
        _al.SourceStop(voice.Source);

        if (voice.Filter != 0 && _effects is not null)
        {
            _effects.SetSourceProperty(voice.Source, EFXSourceInteger.DirectFilter, 0);
            _effects.DeleteFilter(voice.Filter);
            voice.Filter = 0;
        }

        // Detached before deletion: a source still bound to a buffer keeps it alive, and
        // the buffers here outlive the voices.
        _al.SetSourceProperty(voice.Source, SourceInteger.Buffer, 0);
        _al.DeleteSource(voice.Source);
    }

    /// <summary>One sound in flight.</summary>
    /// <remarks>
    /// A class rather than a struct because a placed sound is edited while it plays: a
    /// following emitter moves and its filter changes with every step the listener takes.
    /// </remarks>
    private sealed class Voice(int id, uint source, AudioBus bus)
    {
        public int Id { get; } = id;

        public uint Source { get; } = source;

        public AudioBus Bus { get; } = bus;

        /// <summary>Where it is in the room, or null when it plays at the head.</summary>
        public AudioPlacement? At { get; set; }

        /// <summary>Its low-pass, or zero when the device has no EFX.</summary>
        public uint Filter { get; set; }

        /// <summary>Its own level, under the bus, for fading one voice against another.</summary>
        public float Level { get; set; } = 1f;
    }
}

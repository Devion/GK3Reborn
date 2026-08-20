using System.Globalization;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;
using Silk.NET.OpenAL;

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

    private OpenAlBackend(
        AL al, ALContext alc, Device* device, Context* context, SpeakerLayout layout)
    {
        _al = al;
        _alc = alc;
        _device = device;
        _context = context;
        RequestedLayout = layout;
        ActualLayout = layout;

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
                _al.SetSourceProperty(voice.Source, SourceFloat.Gain, Gain(voice.Bus));
            }
        }
    }

    /// <inheritdoc/>
    public AudioVoice Play(WavFile sound, AudioBus bus, bool repeat = false)
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

        // Head-relative for now. Placing a sound in the room needs the emitter's position,
        // which is a scene concern rather than this one's.
        _al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
        _al.SetSourceProperty(source, SourceVector3.Position, 0f, 0f, 0f);

        _al.SourcePlay(source);

        var voice = new Voice(_next++, source, bus);
        _voices.Add(voice);

        return new AudioVoice(voice.Id);
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

        // Detached before deletion: a source still bound to a buffer keeps it alive, and
        // the buffers here outlive the voices.
        _al.SetSourceProperty(voice.Source, SourceInteger.Buffer, 0);
        _al.DeleteSource(voice.Source);
    }

    private readonly record struct Voice(int Id, uint Source, AudioBus Bus);
}

// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Upscaling;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// NVIDIA Streamline: the loader every NGX feature is reached through.
/// </summary>
public sealed unsafe class Streamline : IDisposable
{
    /// <summary>Which graphics API a device is, from <c>sl::RenderAPI</c>.</summary>
    internal const uint RenderApiDirect3D12 = 1;
    internal const uint RenderApiVulkan = 2;

    /// <summary>Streamline's own feature numbers.</summary>
    private const uint FeatureSuperResolution = 0;
    private const uint FeatureFrameGeneration = 1000;

    /// <summary>Latency reporting, which frame generation cannot run without.</summary>
    private const uint FeatureReflex = 3;
    private const uint FeaturePresentCounter = 4;

    /// <summary>Ray reconstruction, as the public headers number it.</summary>
    private const uint FeatureRayReconstruction = 1001;

    /// <summary>
    /// Neural rendering: ray reconstruction as the newer <c>sl.dlss_nr.dll</c> numbers
    /// itself.
    /// </summary>
    private const uint FeatureNeuralRendering = 1004;

    /// <summary>The version of the interface this was written against.</summary>
    private const ulong SdkVersion = (2UL << 48) | (12UL << 32) | (0UL << 16) | 0xfedcUL;

    /// <summary>Preference flags, from <c>sl::PreferenceFlags</c>.</summary>
    private const ulong DisableCommandListStateTracking = 1UL << 0;
    private const ulong UseManualHooking = 1UL << 2;
    private const ulong UseFrameBasedResourceTagging = 1UL << 7;

    /// <summary>Buffer names, from <c>sl_core_types.h</c>.</summary>
    private const uint TagDepth = 0;
    private const uint TagMotionVectors = 1;
    private const uint TagScalingInputColor = 3;
    private const uint TagScalingOutputColor = 4;
    private const uint TagAlbedo = 7;
    private const uint TagSpecularAlbedo = 8;
    private const uint TagNormalRoughness = 14;

    /// <summary>The three buffers <c>sl.dlss_nr.dll</c> reads.</summary>
    /// <summary>The picture as it stood before the interface was drawn over it.</summary>
    private const uint TagHudLessColour = 2;

    private const uint TagNeuralInputColor = 70;
    private const uint TagNeuralOutputColor = 71;
    private const uint TagNeuralControlMask = 72;

    /// <summary>A tagged resource does not change until the frame is presented.</summary>
    private const uint ValidUntilPresent = 1;

    /// <summary>
    /// The marker <c>slReflexSleep</c> sends, which is not a marker.
    /// </summary>
    internal const uint MarkerSleep = 4096;

    /// <summary>
    /// How hard the neural-rendering network is asked to work, from nothing to one.
    /// </summary>
    private const float NeuralStrength = 1f;

    /// <summary>Whether the network picks its own control mask. One for yes.</summary>
    private const byte NeuralAutoMask = 1;

    private readonly nint _library;
    private readonly List<string> _instanceExtensions = [];
    private readonly List<string> _deviceExtensions = [];

    private readonly delegate* unmanaged[Cdecl]<void*, ulong, uint> _init;
    private readonly delegate* unmanaged[Cdecl]<uint> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<void*, uint> _setVulkanInfo;
    private readonly delegate* unmanaged[Cdecl]<void*, uint> _setD3DDevice;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint> _isFeatureSupported;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint> _getFeatureRequirements;
    private readonly delegate* unmanaged[Cdecl]<uint, byte*, void**, uint> _getFeatureFunction;
    private readonly delegate* unmanaged[Cdecl]<void**, uint*, uint> _getNewFrameToken;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, void*, uint> _setConstants;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, void*, uint, void*, uint> _setTagForFrame;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, void**, uint, void*, uint> _evaluateFeature;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint> _freeResources;
    private readonly delegate* unmanaged[Cdecl]<void**, uint> _upgradeInterface;
    private readonly delegate* unmanaged[Cdecl]<uint, byte*, uint> _isFeatureLoaded;

    private delegate* unmanaged[Cdecl]<void*, void*, uint> _setDlssOptions;
    private delegate* unmanaged[Cdecl]<void*, void*, uint> _setDlssdOptions;
    private delegate* unmanaged[Cdecl]<void*, void*, uint> _setDlssnrOptions;
    private delegate* unmanaged[Cdecl]<void*, void*, uint> _setFrameGenerationOptions;

    /// <summary>Reflex's own three, which are shaped differently from each other.</summary>
    private delegate* unmanaged[Cdecl]<void*, uint> _setReflexOptions;
    private delegate* unmanaged[Cdecl]<void*, uint> _sleep;
    private delegate* unmanaged[Cdecl]<uint, void*, uint> _setMarker;
    private delegate* unmanaged[Cdecl]<void*, void*, void*, uint> _getFrameGenerationState;

    /// <summary>Which denoising feature loaded, or nought when neither did.</summary>
    private uint _denoiser;

    private nint _instance;
    private nint _physicalDevice;
    private nint _device;
    private bool _attached;
    private Matrix4x4? _previousViewProjection;

    /// <summary>The frame the common constants were last set for.</summary>
    private void* _constantsFor;
    private uint _frameNumber;

    /// <summary>Which API the device is, in Streamline's numbering.</summary>
    private uint _renderApi = RenderApiVulkan;

    /// <summary>This frame's token, from <see cref="BeginFrame"/>.</summary>
    private void* _token;

    private uint _latencyMode;
    private int _framesToGenerate;

    private Streamline(nint library)
    {
        _library = library;

        _init = (delegate* unmanaged[Cdecl]<void*, ulong, uint>)Entry("slInit");
        _shutdown = (delegate* unmanaged[Cdecl]<uint>)Entry("slShutdown");
        _setVulkanInfo = (delegate* unmanaged[Cdecl]<void*, uint>)Entry("slSetVulkanInfo");
        _setD3DDevice = (delegate* unmanaged[Cdecl]<void*, uint>)Entry("slSetD3DDevice");
        _isFeatureSupported =
            (delegate* unmanaged[Cdecl]<uint, void*, uint>)Entry("slIsFeatureSupported");
        _getFeatureRequirements =
            (delegate* unmanaged[Cdecl]<uint, void*, uint>)Entry("slGetFeatureRequirements");
        _getFeatureFunction =
            (delegate* unmanaged[Cdecl]<uint, byte*, void**, uint>)Entry("slGetFeatureFunction");
        _getNewFrameToken =
            (delegate* unmanaged[Cdecl]<void**, uint*, uint>)Entry("slGetNewFrameToken");
        _setConstants =
            (delegate* unmanaged[Cdecl]<void*, void*, void*, uint>)Entry("slSetConstants");
        _setTagForFrame =
            (delegate* unmanaged[Cdecl]<void*, void*, void*, uint, void*, uint>)Entry("slSetTagForFrame");
        _evaluateFeature =
            (delegate* unmanaged[Cdecl]<uint, void*, void**, uint, void*, uint>)Entry("slEvaluateFeature");
        _freeResources = (delegate* unmanaged[Cdecl]<uint, void*, uint>)Entry("slFreeResources");
        _upgradeInterface = (delegate* unmanaged[Cdecl]<void**, uint>)Entry("slUpgradeInterface");
        _isFeatureLoaded = (delegate* unmanaged[Cdecl]<uint, byte*, uint>)Entry("slIsFeatureLoaded");
    }

    /// <summary>Whether the whole chain is up and DLSS can be evaluated.</summary>
    public bool Ready => _attached && Supported;

    /// <summary>Whether the device reported that it can run super resolution.</summary>
    public bool Supported { get; private set; }

    /// <summary>Whether ray reconstruction is loaded, supported and driveable.</summary>
    public bool HasRayReconstruction => _denoiser != 0;

    /// <summary>Which of the two denoising plugins is the one that loaded.</summary>
    public string RayReconstructionVariant => _denoiser switch
    {
        FeatureNeuralRendering => "DLSS neural rendering",
        FeatureRayReconstruction => "DLSS ray reconstruction",
        _ => string.Empty,
    };

    /// <summary>
    /// Whether the loaded denoiser needs the inputs only a traced picture has.
    /// </summary>
    public bool RayReconstructionNeedsTracedInputs => _denoiser == FeatureRayReconstruction;

    /// <summary>Whether the denoiser that loaded is the neural rendering one.</summary>
    public bool NeuralRenderingLoaded => _denoiser == FeatureNeuralRendering;

    /// <summary>Whether the loaded denoiser has the rung the plan is asking for.</summary>
    /// <param name="quality">The rung the plan asks for.</param>
    /// <returns>True when the denoising feature can be used at that rung.</returns>
    public bool CanReconstruct(UpscalerQuality quality) =>
        HasRayReconstruction &&
        (_denoiser != FeatureNeuralRendering || quality != UpscalerQuality.UltraQuality);

    /// <summary>
    /// Why ray reconstruction is not available, when it looked as though it should be.
    /// </summary>
    public string RayReconstructionNote { get; private set; } = string.Empty;

    /// <summary>Whether the frame-generation plugin was loaded and is supported.</summary>
    public bool HasFrameGeneration { get; private set; }

    /// <summary>What the super-resolution network calls itself.</summary>
    public string SuperResolutionVersion { get; private set; } = "DLSS";

    /// <summary>Instance extensions the loaded features need.</summary>
    public IReadOnlyList<string> InstanceExtensions => _instanceExtensions;

    /// <summary>Device extensions the loaded features need.</summary>
    public IReadOnlyList<string> DeviceExtensions => _deviceExtensions;

    /// <summary>How many extra compute queues to create for Streamline's own work.</summary>
    public uint ComputeQueuesWanted { get; private set; }

    /// <summary>How many extra graphics queues to create for it.</summary>
    public uint GraphicsQueuesWanted { get; private set; }

    /// <summary>
    /// Starts Streamline, or returns null when it is not installed or will not start.
    /// </summary>
    /// <param name="runtimes">Where the player's runtimes were found.</param>
    /// <param name="renderApi">Which graphics API the device about to be attached is.</param>
    /// <param name="wantFrameGeneration">Whether to load the frame-generation plugin.</param>
    /// <param name="wantRayReconstruction">Whether to load the ray-reconstruction plugin.</param>
    /// <returns>A started Streamline, or null.</returns>
    public static Streamline? TryStart(
        UpscalerRuntimes? runtimes,
        uint renderApi = RenderApiVulkan,
        bool wantFrameGeneration = true,
        bool wantRayReconstruction = true)
    {
        if (runtimes?.Locate(UpscalerRuntimes.StreamlineInterposer) is not { } interposer)
        {
            return null;
        }

        nint library;

        try
        {
            library = NativeLibrary.Load(interposer);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException
                                          or ArgumentException)
        {
            return null;
        }

        Streamline started;

        try
        {
            started = new Streamline(library) { _renderApi = renderApi };
        }
        catch (EntryPointNotFoundException)
        {
            NativeLibrary.Free(library);
            return null;
        }

        string directory = System.IO.Path.GetDirectoryName(interposer) ?? AppContext.BaseDirectory;

        bool frameGeneration = wantFrameGeneration &&
                               runtimes.DlssFrameGeneration.Present;

        bool rayReconstruction = wantRayReconstruction &&
                                 runtimes.DlssRayReconstruction.Present;

        if (!started.Start(directory, frameGeneration, rayReconstruction))
        {
            started.Dispose();
            return null;
        }

        started.HasFrameGeneration = frameGeneration;
        started.SuperResolutionVersion =
            runtimes.Dlss.Version is { Length: > 0 } version ? "DLSS " + version : "DLSS";

        started.Gather(FeatureSuperResolution);

        if (rayReconstruction)
        {
            // Whichever of the two plugins is beside the interposer. Neural rendering is
            // asked after first because it is the one current bundles ship: the network file
            // this build looks for is nvngx_dlssnr.dll, which is its network and not the
            // documented feature's. A feature whose plugin is absent simply states no
            // requirements, so asking after both costs a call.
            if (started.Gather(FeatureNeuralRendering) &&
                started.Loaded(FeatureNeuralRendering))
            {
                started._denoiser = FeatureNeuralRendering;
            }
            else if (started.Gather(FeatureRayReconstruction) &&
                     started.Loaded(FeatureRayReconstruction))
            {
                started._denoiser = FeatureRayReconstruction;
            }
            else
            {
                started.Note();
            }
        }

        if (frameGeneration)
        {
            // Reflex first: frame generation depends on it, and what it wants of the device
            // has to be in the extension list either way.
            //
            // And Reflex is worth having on its own. It is what shortens the queue between
            // the frame this thread is building and the one the display is waiting for, and
            // it costs a plugin that is being loaded anyway — so whether it loaded is kept,
            // rather than being treated as a detail of frame generation.
            started.HasLatencyControl =
                started.Gather(FeatureReflex) && started.Loaded(FeatureReflex);

            started.Gather(FeaturePresentCounter);

            started.HasFrameGeneration =
                started.Gather(FeatureFrameGeneration) &&
                started.Loaded(FeatureFrameGeneration);
        }

        return started;
    }

    /// <summary>
    /// Tells Streamline about the Vulkan objects the renderer made, and asks whether the
    /// device can actually run DLSS.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="physicalDevice">The device chosen.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="graphicsFamily">Family the graphics queue came from.</param>
    /// <param name="graphicsIndex">Index within it of the queue set aside for Streamline.</param>
    /// <param name="computeFamily">Family the compute queue came from.</param>
    /// <param name="computeIndex">Index within it of the queue set aside for Streamline.</param>
    /// <returns>True when DLSS is usable on this device.</returns>
    public bool Attach(
        nint instance,
        nint physicalDevice,
        nint device,
        uint graphicsFamily,
        uint graphicsIndex,
        uint computeFamily,
        uint computeIndex)
    {
        _instance = instance;
        _physicalDevice = physicalDevice;
        _device = device;

        var info = new SlVulkanInfo
        {
            Header = SlHeader.Of(
                0x0eed6fd5, 0x82cd, 0x43a9, 0xbd, 0xb5, 0x47, 0xa5, 0xba, 0x2f, 0x45, 0xd6, 3),
            Device = device,
            Instance = instance,
            PhysicalDevice = physicalDevice,
            GraphicsQueueFamily = graphicsFamily,
            GraphicsQueueIndex = graphicsIndex,
            ComputeQueueFamily = computeFamily,
            ComputeQueueIndex = computeIndex,
            OpticalFlowQueueFamily = computeFamily,
            OpticalFlowQueueIndex = computeIndex,
        };

        uint result = _setVulkanInfo(&info);

        if (result != 0)
        {
            Log.Warning($"WARNING GK3R3437: Streamline would not take the device (code {result}).");
            return false;
        }

        _attached = true;

        var adapter = new SlAdapterInfo
        {
            Header = SlHeader.Of(
                0x0677315f, 0xa746, 0x4492, 0x9f, 0x42, 0xcb, 0x61, 0x42, 0xc9, 0xc3, 0xd4, 1),
            VkPhysicalDevice = physicalDevice,
        };

        return Examine(&adapter);
    }

    /// <summary>
    /// Tells Streamline about the Direct3D device the renderer made, and asks whether it can
    /// run DLSS.
    /// </summary>
    /// <param name="device">The <c>ID3D12Device</c>.</param>
    /// <param name="luid">The adapter's locally unique identifier, eight bytes.</param>
    /// <returns>True when DLSS is usable on this device.</returns>
    public bool AttachDirect3D(nint device, ReadOnlySpan<byte> luid)
    {
        if (_setD3DDevice is null)
        {
            Log.Warning("WARNING GK3R3438: this Streamline has no slSetD3DDevice.");
            return false;
        }

        _device = device;

        uint result = _setD3DDevice((void*)device);

        if (result != 0)
        {
            Log.Warning($"WARNING GK3R3437: Streamline would not take the device (code {result}).");
            return false;
        }

        _attached = true;

        byte* bytes = stackalloc byte[8];

        for (int i = 0; i < luid.Length && i < 8; i++)
        {
            bytes[i] = luid[i];
        }

        var adapter = new SlAdapterInfo
        {
            Header = SlHeader.Of(
                0x0677315f, 0xa746, 0x4492, 0x9f, 0x42, 0xcb, 0x61, 0x42, 0xc9, 0xc3, 0xd4, 1),
            DeviceLuid = bytes,
            DeviceLuidSizeInBytes = 8,
        };

        return Examine(&adapter);
    }

    /// <summary>Asks the adapter what of DLSS it can actually do.</summary>
    /// <param name="adapter">Which adapter, named the way the attached API names one.</param>
    /// <returns>True when super resolution is usable.</returns>
    private bool Examine(SlAdapterInfo* adapter)
    {
        uint answer = _isFeatureSupported(FeatureSuperResolution, adapter);

        Supported = answer == 0;

        if (!Supported)
        {
            // Not a failure worth a warning: it is what an AMD or Intel card says, and
            // what a GeForce older than Turing says. The settings page reports it. The
            // code is worth printing though — "no" and "no, because the driver is too old"
            // are different problems and only one of them is the player's to fix.
            Log.Info($"DLSS: this device does not support it ({Reason(answer)}).");
            return false;
        }

        Log.Info(
            $"DLSS: available, {SuperResolutionVersion}" +
            (HasRayReconstruction ? ", " + RayReconstructionVariant : string.Empty) +
            (HasFrameGeneration ? ", frame generation" : string.Empty));

        if (_denoiser != 0)
        {
            uint denoiser = _isFeatureSupported(_denoiser, adapter);

            if (denoiser != 0)
            {
                RayReconstructionNote =
                    "this device does not support it (" + Reason(denoiser) + ")";
                _denoiser = 0;
            }
        }

        if (HasFrameGeneration &&
            _isFeatureSupported(FeatureFrameGeneration, adapter) != 0)
        {
            HasFrameGeneration = false;
        }

        // Reflex answers for hardware that is not NVIDIA's as well: there it collects
        // latency statistics and sleeps for nobody, which is a working answer rather than a
        // missing feature. So this asks and believes the answer rather than assuming a
        // GeForce.
        if (HasLatencyControl && _isFeatureSupported(FeatureReflex, adapter) != 0)
        {
            HasLatencyControl = false;
        }

        return true;
    }


    /// <summary>Sets what the feature should do, for the sizes now in use.</summary>
    /// <param name="quality">Which rung of the ladder.</param>
    /// <param name="preset">Which trained model, or nought for the runtime's choice.</param>
    /// <param name="display">The size the picture is shown at.</param>
    /// <param name="highDynamicRange">Whether the colour runs past one.</param>
    /// <param name="rayReconstruction">Whether to configure the denoising variant instead.</param>
    /// <param name="uplift">
    /// What the neural network is asked to do, where that is the denoiser. Ignored by the
    /// documented ray-reconstruction feature, which has none of these controls.
    /// </param>
    /// <returns>True when the runtime accepted it.</returns>
    public bool SetDlssOptions(
        UpscalerQuality quality,
        int preset,
        (uint Width, uint Height) display,
        bool highDynamicRange,
        bool rayReconstruction,
        NeuralUplift? uplift = null)
    {
        if (!Ready)
        {
            return false;
        }

        uint mode = Mode(quality);
        uint chosen = preset > 0 ? (uint)preset : 0;

        if (rayReconstruction && _denoiser == FeatureNeuralRendering)
        {
            if (!Resolve(
                    FeatureNeuralRendering, "slDLSSNRSetOptions", ref _setDlssnrOptions))
            {
                return false;
            }

            NeuralUplift asked = uplift?.Sane() ?? NeuralUplift.None;

            var neural = new SlDlssnrOptions
            {
                Header = SlHeader.Of(
                    0x29dfdfe0, 0x273a, 0x4e72, 0xb4, 0x92, 0x2d, 0xc8, 0x23, 0xd5, 0xb1, 0xad, 3),

                // One is the only value that runs it; there is no ladder here.
                Mode = 1,

                // The network's own appearance controls. Full strength, not nought: the
                // plugin supplies no default for these four — they are original fields and
                // it reads them from the caller unconditionally — but it does default the
                // fifth, added later, to one. One is therefore the scale's full end and the
                // value a caller who set nothing would have been given had these been
                // defaulted with it. Nought asks the network to do none of what it does,
                // and it does not answer that by passing the picture through.
                Intensity = asked.Intensity,
                LocalToneStrength = asked.LocalTone,
                LocalStructureStrength = asked.LocalStructure,
                GlobalToneStrength = asked.GlobalTone,
                SkinStructureStrength = asked.SkinStrength,

                // Nothing is tagged as a control mask, so the network is left to find its
                // own. What it decides from is unknown, and it decides which pixels it is
                // allowed to rework — so this is the second switch to try when the picture
                // is wrong in a way that follows the camera rather than the geometry.
                UseAutoMask = (byte)(asked.AutoSkinMask ? 1 : 0),

                // Not the super-resolution preset. That number is a rung on a ladder of
                // trained upscaling models named by letter; this one indexes a table of
                // network weights inside nvngx_dlssnr.dll, and the two have nothing to do
                // with each other. The network resolves nought to whichever weights it
                // ships as default, and reports anything it does not have as unavailable
                // and falls back — so a wrong number here is quiet rather than fatal, which
                // is exactly why it should not be a number from the other ladder.
                Preset = (uint)asked.Preset,
                Style = (uint)asked.Style,

                // The one rung this feature does not have. It takes max performance,
                // balanced, max quality, ultra performance and DLAA and refuses ultra
                // quality by number, so asking for that rung here is asking the plugin to
                // decline every frame. Max quality is the neighbour to fall back to.
                PerformanceMode = mode == 5 ? 3 : mode,
            };

            SlViewport target = Viewport();

            return _setDlssnrOptions(&target, &neural) == 0;
        }

        if (rayReconstruction)
        {
            if (!Resolve(
                    FeatureRayReconstruction, "slDLSSDSetOptions", ref _setDlssdOptions))
            {
                return false;
            }

            var options = new SlDlssdOptions
            {
                Header = SlHeader.Of(
                    0x0ad87504, 0x774e, 0x4bf3, 0x96, 0x33, 0xa4, 0x4d, 0x1f, 0x7f, 0x9c, 0xb8, 3),
                Mode = mode,
                OutputWidth = display.Width,
                OutputHeight = display.Height,
                PreExposure = 1f,
                ExposureScale = 1f,
                ColorBuffersHdr = (byte)(highDynamicRange ? 1 : 0),

                // The roughness rides in the normal target's spare channel, which is the
                // mode that costs no extra render target. See MeshShaders.
                NormalRoughnessMode = 1,
                DlaaPreset = chosen,
                QualityPreset = chosen,
                BalancedPreset = chosen,
                PerformancePreset = chosen,
                UltraPerformancePreset = chosen,
                UltraQualityPreset = chosen,
            };

            SlViewport viewport = Viewport();

            return _setDlssdOptions(&viewport, &options) == 0;
        }

        if (!Resolve(FeatureSuperResolution, "slDLSSSetOptions", ref _setDlssOptions))
        {
            return false;
        }

        var superResolution = new SlDlssOptions
        {
            Header = SlHeader.Of(
                0x6ac826e4, 0x4c61, 0x4101, 0xa9, 0x2d, 0x63, 0x8d, 0x42, 0x10, 0x57, 0xb8, 3),
            Mode = mode,
            OutputWidth = display.Width,
            OutputHeight = display.Height,
            PreExposure = 1f,
            ExposureScale = 1f,
            ColorBuffersHdr = (byte)(highDynamicRange ? 1 : 0),

            // No exposure texture is tagged, so the runtime works it out from the picture.
            UseAutoExposure = 1,
            DlaaPreset = chosen,
            QualityPreset = chosen,
            BalancedPreset = chosen,
            PerformancePreset = chosen,
            UltraPerformancePreset = chosen,
            UltraQualityPreset = chosen,
        };

        SlViewport handle = Viewport();

        return _setDlssOptions(&handle, &superResolution) == 0;
    }

    /// <summary>Runs the feature over one frame.</summary>
    /// <param name="commandList">The frame&#8217;s command buffer or command list.</param>
    /// <param name="frame">What to upscale.</param>
    /// <param name="rayReconstruction">Whether the denoising variant is the one running.</param>
    /// <returns>True when the runtime did the work.</returns>
    public bool Evaluate(nint commandList, in StreamlineFrame frame, bool rayReconstruction)
    {
        if (!Ready)
        {
            return false;
        }

        // The frame's own token where a caller opened one, and a token of this call's own
        // where nobody did. The second is what the headless renderers do — they draw one
        // picture and never present, so there is no frame for a marker to be part of.
        void* token = _token;

        if (token is null)
        {
            uint number = _frameNumber++;

            if (_getNewFrameToken(&token, &number) != 0 || token is null)
            {
                return false;
            }
        }

        SlViewport viewport = Viewport();

        SlResource colour = Describe(frame.Colour);
        SlResource depth = Describe(frame.Depth);
        SlResource motion = Describe(frame.Motion);
        SlResource output = Describe(frame.Output);

        bool neural = rayReconstruction && _denoiser == FeatureNeuralRendering;

        // Neural rendering reads its colour and writes its result under the two buffer
        // names the headers leave reserved, and takes depth and motion under the ordinary
        // ones. Tagging the scaling pair instead leaves it with no input at all, which it
        // reports as a missing input parameter and refuses the frame over.
        uint inputTag = neural ? TagNeuralInputColor : TagScalingInputColor;
        uint outputTag = neural ? TagNeuralOutputColor : TagScalingOutputColor;

        SlResourceTag* tags = stackalloc SlResourceTag[4];

        tags[0] = Tag(&colour, inputTag, frame.Colour);
        tags[1] = Tag(&output, outputTag, frame.Output);
        tags[2] = Tag(&depth, TagDepth, frame.Depth);
        tags[3] = Tag(&motion, TagMotionVectors, frame.Motion);

        if (_setTagForFrame(token, &viewport, tags, 4, (void*)commandList) != 0)
        {
            return false;
        }

        if (!Constants(token, &viewport, in frame))
        {
            return false;
        }

        void** inputs = stackalloc void*[1];
        inputs[0] = &viewport;

        uint feature = rayReconstruction ? _denoiser : FeatureSuperResolution;
        uint result = _evaluateFeature(feature, token, inputs, 1, (void*)commandList);

        if (result == 0)
        {
            return true;
        }

        Log.Warning($"WARNING GK3R3438: DLSS declined a frame (code {result}).");
        return false;
    }

    /// <summary>Opens a frame, and hands back whether there is one to work with.</summary>
    /// <returns>True when a token was issued.</returns>
    public bool BeginFrame()
    {
        if (!_attached)
        {
            return false;
        }

        void* token = null;
        uint number = _frameNumber++;

        if (_getNewFrameToken(&token, &number) != 0 || token is null)
        {
            _token = null;
            return false;
        }

        _token = token;
        return true;
    }

    /// <summary>Closes the frame, so nothing later reaches for a stale token.</summary>
    public void EndFrame() => _token = null;

    /// <summary>Whether Reflex loaded and can be driven.</summary>
    public bool HasLatencyControl { get; private set; }

    /// <summary>What the latency mode is set to: nought off, one on, two on with boost.</summary>
    public uint LatencyMode => _latencyMode;

    /// <summary>Says how hard to work at keeping the queue short.</summary>
    /// <param name="mode">Nought off, one low latency, two low latency with boost.</param>
    /// <param name="frameLimitUs">A frame cap in microseconds, or nought for none.</param>
    /// <returns>True when the runtime took it.</returns>
    public bool SetLatencyMode(uint mode, uint frameLimitUs = 0)
    {
        if (!_attached || !HasLatencyControl)
        {
            return false;
        }

        if (!Resolve(FeatureReflex, "slReflexSetOptions", ref _setReflexOptions))
        {
            return false;
        }

        var options = new SlReflexOptions
        {
            Header = SlHeader.Of(
                0xf03af81a, 0x6d0b, 0x4902, 0xa6, 0x51, 0xc4, 0x96, 0x5e, 0x21, 0x54, 0x34, 1),
            Mode = mode,
            FrameLimitUs = frameLimitUs,

            // Nought, and all three deliberately. The engine imposes no cap of its own —
            // vertical sync and the display do that — the marker hint applies only to the
            // boosted mode, where what it optimises is a submission pattern this renderer
            // does not have, and the plugin refuses any virtual key but VK_F13 through
            // VK_F15 and returns an error saying so.
            UseMarkersToOptimise = 0,
            VirtualKey = 0,
            IdThread = 0,
        };

        uint result = _setReflexOptions(&options);

        if (result != 0)
        {
            Log.Warning($"WARNING GK3R3441: Reflex would not take its options (code {result}).");
            return false;
        }

        _latencyMode = mode;
        return true;
    }

    /// <summary>Waits, if Reflex thinks this frame should start later than it wants to.</summary>
    /// <returns>True when it was asked.</returns>
    public bool Sleep()
    {
        if (!_attached || !HasLatencyControl || _token is null || _latencyMode == 0)
        {
            return false;
        }

        if (!Resolve(FeatureReflex, "slReflexSleep", ref _sleep))
        {
            return false;
        }

        return _sleep(_token) == 0;
    }

    /// <summary>Says where in the frame the caller has reached.</summary>
    /// <param name="marker">Which point.</param>
    /// <returns>True when the runtime took it.</returns>
    public bool Mark(StreamlineMarker marker)
    {
        if (!_attached || !HasLatencyControl || _token is null)
        {
            return false;
        }

        if (!Resolve(FeatureReflex, "slReflexSetMarker", ref _setMarker))
        {
            return false;
        }

        return _setMarker((uint)marker, _token) == 0;
    }

    /// <summary>The largest number of frames this card will generate for each drawn one.</summary>
    public int FrameGenerationMaximum { get; private set; }

    /// <summary>Why frame generation is not running, or nought when it is.</summary>
    public uint FrameGenerationStatus { get; private set; }

    /// <summary>How many frames the last present actually put on the display.</summary>
    public uint FramesPresented { get; private set; }

    /// <summary>Asks the runtime what it can do and what it is doing.</summary>
    /// <returns>True when it answered.</returns>
    public bool RefreshFrameGeneration()
    {
        if (!_attached || !HasFrameGeneration)
        {
            return false;
        }

        if (!Resolve(FeatureFrameGeneration, "slDLSSGGetState", ref _getFrameGenerationState))
        {
            return false;
        }

        var state = new SlDlssgState
        {
            Header = SlHeader.Of(
                0xcc8ac8e1, 0xa179, 0x44f5, 0x97, 0xfa, 0xe7, 0x41, 0x12, 0xf9, 0xbc, 0x61, 4),
        };

        SlViewport viewport = Viewport();

        if (_getFrameGenerationState(&viewport, &state, null) != 0)
        {
            return false;
        }

        // Clamped, because the number is used to size a menu and a bad read should cost a
        // short list rather than a very long one.
        FrameGenerationMaximum = (int)Math.Min(state.NumFramesToGenerateMax, 16u);
        FrameGenerationStatus = state.Status;
        FramesPresented = state.NumFramesActuallyPresented;
        return true;
    }

    /// <summary>Turns frame generation on at a factor, or off.</summary>
    /// <param name="generated">
    /// How many frames to make for each one drawn: nought off, one for two-times, three for
    /// four-times.
    /// </param>
    /// <param name="render">The size the room is drawn at, which is what motion and depth are.</param>
    /// <param name="display">The size the picture is shown at, which is what colour is.</param>
    /// <returns>True when the runtime took it.</returns>
    public bool SetFrameGeneration(
        int generated, (uint Width, uint Height) render, (uint Width, uint Height) display)
    {
        if (!_attached || !HasFrameGeneration)
        {
            return false;
        }

        if (!Resolve(
                FeatureFrameGeneration, "slDLSSGSetOptions", ref _setFrameGenerationOptions))
        {
            return false;
        }

        var options = new SlDlssgOptions
        {
            Header = SlHeader.Of(
                0xfac5f1cb, 0x2dfd, 0x4f36, 0xa1, 0xe6, 0x3a, 0x9e, 0x86, 0x52, 0x56, 0xc5, 4),
            Mode = generated > 0 ? 1u : 0u,
            NumFramesToGenerate = (uint)Math.Max(1, generated),
            NumBackBuffers = 3,
            MvecDepthWidth = render.Width,
            MvecDepthHeight = render.Height,
            ColorWidth = display.Width,
            ColorHeight = display.Height,
        };

        SlViewport viewport = Viewport();
        uint result = _setFrameGenerationOptions(&viewport, &options);

        if (result != 0)
        {
            Log.Warning(
                $"WARNING GK3R3442: frame generation would not take {generated} " +
                $"generated frame(s) (code {Reason(result)}).");

            return false;
        }

        _framesToGenerate = generated;
        return true;
    }

    /// <summary>How many frames it is currently set to generate for each drawn one.</summary>
    public int FramesGenerated => _framesToGenerate;

    /// <summary>Puts a Streamline proxy in front of a Direct3D or DXGI interface.</summary>
    /// <param name="wrapped">
    /// The interface to replace. On success it points at the proxy instead.
    /// </param>
    /// <returns>True when it was replaced.</returns>
    public bool UpgradeInterface(void** wrapped)
    {
        if (!_attached || wrapped is null || *wrapped is null)
        {
            return false;
        }

        uint result = _upgradeInterface(wrapped);

        if (result == 0)
        {
            return true;
        }

        Log.Warning(
            $"WARNING GK3R3443: Streamline would not proxy that interface ({Reason(result)}). " +
            "Frame generation needs the swapchain to be one of its own, so it will not run.");

        return false;
    }

    /// <summary>Hands over the picture as it was before the interface went on it.</summary>
    /// <param name="commandList">The frame's command list, for the barriers the runtime adds.</param>
    /// <param name="hudLess">The copy, at display size, in whatever state it was left in.</param>
    /// <returns>True when the runtime took it.</returns>
    public bool TagHudLess(nint commandList, UpscaleSurface hudLess)
    {
        if (!_attached || _token is null || !hudLess.Exists)
        {
            return false;
        }

        SlViewport viewport = Viewport();
        SlResource resource = Describe(hudLess);

        SlResourceTag tag = Tag(&resource, TagHudLessColour, hudLess);

        return _setTagForFrame(_token, &viewport, &tag, 1, (void*)commandList) == 0;
    }

    /// <summary>Lets go of whatever the feature allocated for this viewport.</summary>
    public void ReleaseDlss()
    {
        if (!_attached)
        {
            return;
        }

        SlViewport viewport = Viewport();

        _freeResources(FeatureSuperResolution, &viewport);

        if (_denoiser != 0)
        {
            _freeResources(_denoiser, &viewport);
        }

        _previousViewProjection = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_attached)
        {
            _shutdown();
            _attached = false;
        }

        if (_library != 0)
        {
            NativeLibrary.Free(_library);
        }
    }

    /// <summary>Hands the runtime this frame's camera.</summary>
    private bool Constants(void* token, SlViewport* viewport, in StreamlineFrame frame)
    {
        // Once a frame, however many features are run in it. The runtime refuses a second
        // set — "Setting different 'common' constants multiple times within the same frame is
        // NOT allowed" — and it is right to: the camera and the jitter are facts about the
        // frame, not about the feature, and the second caller would only be restating them.
        // Two features in one frame is the ordinary case now that the neural uplift runs
        // after the tone map, over a picture super resolution may already have made.
        //
        // Also, the second set would not be a restatement. This walks the camera's history
        // forward — the previous view-projection becomes this one — so calling it twice would
        // leave the second feature believing the camera had not moved since itself.
        if (token == _constantsFor)
        {
            return true;
        }

        Camera camera = frame.Camera ?? new Camera();
        float aspect = frame.Aspect > 0 ? frame.Aspect : 1f;

        Matrix4x4 projection = camera.ProjectionWithoutJitter(aspect);
        Matrix4x4 viewProjection = camera.View * projection;
        Matrix4x4 previous = _previousViewProjection ?? viewProjection;

        _previousViewProjection = viewProjection;

        Matrix4x4.Invert(projection, out Matrix4x4 clipToView);
        Matrix4x4.Invert(viewProjection, out Matrix4x4 clipToWorld);

        Matrix4x4 clipToPrevious = clipToWorld * previous;
        Matrix4x4.Invert(clipToPrevious, out Matrix4x4 previousToClip);

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 up = Vector3.Cross(forward, right);

        var constants = new SlConstants
        {
            Header = SlHeader.Of(
                0xdcd35ad7, 0x4e4a, 0x4bad, 0xa9, 0x0c, 0xe0, 0xc4, 0x9e, 0xb2, 0x3a, 0xfe, 2),

            JitterX = frame.JitterPixels.X,
            JitterY = frame.JitterPixels.Y,

            // The vectors are in render-resolution pixels; this is what turns them into
            // the normalised space Streamline reasons in.
            MotionVectorScaleX = frame.Motion.Width > 0
                ? 1f / frame.Motion.Width
                : 1f,
            MotionVectorScaleY = frame.Motion.Height > 0
                ? 1f / frame.Motion.Height
                : 1f,

            CameraPosX = camera.Position.X,
            CameraPosY = camera.Position.Y,
            CameraPosZ = camera.Position.Z,
            CameraUpX = up.X,
            CameraUpY = up.Y,
            CameraUpZ = up.Z,
            CameraRightX = right.X,
            CameraRightY = right.Y,
            CameraRightZ = right.Z,
            CameraForwardX = forward.X,
            CameraForwardY = forward.Y,
            CameraForwardZ = forward.Z,

            CameraNear = camera.NearPlane,
            CameraFar = camera.FarPlane,
            CameraFieldOfView = camera.FieldOfView,
            CameraAspectRatio = aspect,
            MotionVectorsInvalidValue = 0f,

            // Nought is near and one is far, the ordinary way round.
            DepthInverted = 0,

            // The vectors are the whole movement, the camera's included, which is what a
            // vertex shader that projects a previous world position necessarily produces.
            CameraMotionIncluded = 1,
            MotionVectors3D = 0,
            Reset = (byte)(frame.Reset ? 1 : 0),
            OrthographicProjection = 0,
            MotionVectorsDilated = 0,

            // Taken out in the fragment shader, where the offset was known exactly.
            MotionVectorsJittered = 0,

            MinimumRelativeLinearDepthObjectSeparation = 40f,
        };

        Copy(projection, constants.CameraViewToClip);
        Copy(clipToView, constants.ClipToCameraView);
        Copy(Matrix4x4.Identity, constants.ClipToLensClip);
        Copy(clipToPrevious, constants.ClipToPrevClip);
        Copy(previousToClip, constants.PrevClipToClip);

        if (_setConstants(&constants, token, viewport) != 0)
        {
            return false;
        }

        _constantsFor = token;
        return true;
    }

    private static void Copy(Matrix4x4 matrix, float* destination)
    {
        destination[0] = matrix.M11;
        destination[1] = matrix.M12;
        destination[2] = matrix.M13;
        destination[3] = matrix.M14;
        destination[4] = matrix.M21;
        destination[5] = matrix.M22;
        destination[6] = matrix.M23;
        destination[7] = matrix.M24;
        destination[8] = matrix.M31;
        destination[9] = matrix.M32;
        destination[10] = matrix.M33;
        destination[11] = matrix.M34;
        destination[12] = matrix.M41;
        destination[13] = matrix.M42;
        destination[14] = matrix.M43;
        destination[15] = matrix.M44;
    }

    private static SlViewport Viewport() => new()
    {
        Header = SlHeader.Of(
            0x171b6435, 0x9b3c, 0x4fc8, 0x99, 0x94, 0xfb, 0xe5, 0x25, 0x69, 0xaa, 0xa4, 1),
        Value = 0,
    };

    private static SlResource Describe(UpscaleSurface surface) => new()
    {
        Header = SlHeader.Of(
            0x3a9d70cf, 0x2418, 0x4b72, 0x83, 0x91, 0x13, 0xf8, 0x72, 0x1c, 0x72, 0x61, 1),

        // Two-dimensional texture.
        Type = 0,
        Native = surface.Native,
        View = surface.View,

        // A Vulkan image layout or a Direct3D resource state. The runtime keeps the number
        // and never interprets it, because it knows which API it was given a device for.
        State = surface.State,
        Width = surface.Width,
        Height = surface.Height,
        NativeFormat = surface.NativeFormat,
        MipLevels = 1,
        ArrayLayers = 1,
        Usage = surface.Usage,
    };

    private static SlResourceTag Tag(SlResource* resource, uint type, UpscaleSurface extent) => new()
    {
        Header = SlHeader.Of(
            0x4c6a5aad, 0xb445, 0x496c, 0x87, 0xff, 0x1a, 0xf3, 0x84, 0x5b, 0xe6, 0x53, 1),
        Resource = resource,
        Type = type,
        Lifecycle = ValidUntilPresent,
        ExtentWidth = extent.Width,
        ExtentHeight = extent.Height,
    };

    /// <summary>What one of Streamline's result codes means, in words.</summary>
    private static string Reason(uint code) => code switch
    {
        0 => "no error",
        2 => "the graphics driver is too old",
        3 => "the operating system is too old",
        4 => "hardware-accelerated GPU scheduling is switched off in Windows",
        5 => "no device had been created",
        6 => "no supported adapter was found",
        7 => "this adapter is not supported",
        8 => "no plugins were loaded",
        9 => "a Vulkan call failed",
        15 => "NGX would not start",
        23 => "Streamline was not initialised",
        31 => "the feature is missing",
        32 => "the feature is not supported here",
        33 => "the feature needs hooks this engine does not install",
        34 => "the feature would not load",
        36 => "a feature it depends on is missing",
        _ => "code " + code.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The DLSS mode one rung of the quality ladder means.</summary>
    private static uint Mode(UpscalerQuality quality) => quality switch
    {
        // Not "off": a ratio of one is what NVIDIA calls DLAA, and it is the whole budget
        // spent on anti-aliasing rather than on resolution.
        UpscalerQuality.Native => 6,
        UpscalerQuality.UltraQuality => 5,
        UpscalerQuality.Quality => 3,
        UpscalerQuality.Balanced => 2,
        UpscalerQuality.Performance => 1,
        _ => 4,
    };

    private bool Start(string directory, bool frameGeneration, bool rayReconstruction)
    {
        List<uint> features = [FeatureSuperResolution];

        if (rayReconstruction)
        {
            // Both, because which one the player installed is not known until Streamline has
            // been asked, and what to load cannot be changed afterwards. Naming a feature
            // whose plugin is not there is not an error: it fails to load and says so when
            // its requirements are asked for.
            features.Add(FeatureNeuralRendering);
            features.Add(FeatureRayReconstruction);
        }

        if (frameGeneration)
        {
            features.Add(FeatureFrameGeneration);
            features.Add(FeatureReflex);
            features.Add(FeaturePresentCounter);
        }

        uint[] wanted = [.. features];

        nint path = Marshal.StringToHGlobalUni(directory);
        nint paths = Marshal.AllocHGlobal(sizeof(nint));
        nint project = Marshal.StringToHGlobalAnsi("6e58f9cd-2b41-4f6b-9a3f-1c8d7c9b5e21");

        // Required, and not optional in the way the header's wording suggests. Streamline
        // wants either an application id NVIDIA issued or an engine type *and* a version;
        // with neither, NGX declines to start and every feature comes back as unsupported
        // on hardware that plainly supports it.
        nint version = Marshal.StringToHGlobalAnsi(
            typeof(Streamline).Assembly.GetName().Version?.ToString() ?? "1.0.0");

        try
        {
            Marshal.WriteIntPtr(paths, path);

            fixed (uint* featurePointer = wanted)
            {
                var preferences = new SlPreferences
                {
                    Header = SlHeader.Of(
                        0x1ca10965, 0xbf8e, 0x432b, 0x8d, 0xa1, 0x67, 0x16, 0xd8, 0x79, 0xfb, 0x14, 1),

                    // Default logging, no console, and nothing written to disk: a log file
                    // appearing beside somebody's game because they turned an upscaler on
                    // is not a thing this project should do without being asked.
                    LogLevel = 1,

                    // The warnings and errors do come through, into this engine's own log,
                    // because they are the only account of what the plugins made of what
                    // they were handed. "Failed to create DLSS-NR NGX feature" and
                    // "performance mode 5 is not supported" are sentences that answer a
                    // question nothing on this side of the boundary can otherwise answer.
                    LogMessageCallback = (void*)(delegate* unmanaged[Cdecl]<uint, byte*, void>)&Said,

                    PathsToPlugins = (void*)paths,
                    NumPathsToPlugins = 1,

                    // The engine creates its own instance, device and swapchain and tells
                    // Streamline about them afterwards.
                    Flags = DisableCommandListStateTracking | UseManualHooking |
                            UseFrameBasedResourceTagging,

                    FeaturesToLoad = featurePointer,
                    NumFeaturesToLoad = (uint)wanted.Length,
                    ProjectId = (void*)project,

                    // Custom engine, and whichever API the caller is about to attach —
                    // which the header says to state, because it decides what the
                    // requirements queries come back with. It used to say Vulkan
                    // unconditionally, which on the Direct3D backend meant every feature
                    // was asked what Vulkan extensions it wanted and none was asked what it
                    // wanted of a Direct3D device.
                    Engine = 0,
                    EngineVersion = (void*)version,
                    RenderApi = _renderApi,
                };

                uint result = _init(&preferences, SdkVersion);

                if (result == 0)
                {
                    return true;
                }

                Log.Warning($"WARNING GK3R3439: Streamline would not start (code {result}).");
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(path);
            Marshal.FreeHGlobal(project);
            Marshal.FreeHGlobal(version);
        }
    }

    /// <summary>What Streamline and its plugins have to say, in this engine's log.</summary>
    /// <param name="type">Nought for information, one for a warning, two for an error.</param>
    /// <param name="message">A null-terminated string owned by the caller.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Said(uint type, byte* message)
    {
        if (type == 0 || message is null)
        {
            return;
        }

        string? text = Marshal.PtrToStringAnsi((nint)message)?.TrimEnd('\r', '\n');

        if (text is not { Length: > 0 })
        {
            return;
        }

        if (type >= 2)
        {
            Log.Warning("WARNING GK3R3440: Streamline: " + text);
        }
        else
        {
            Log.Info("Streamline: " + text);
        }
    }

    /// <summary>
    /// Says that neither denoising plugin loaded, and what to do about it.
    /// </summary>
    private void Note()
    {
        RayReconstructionNote =
            "neither denoising plugin would load; check that sl.dlss_nr.dll and " +
            "nvngx_dlssnr.dll are both beside the other Streamline files, and that the " +
            "graphics driver is 570 or newer";

        Log.Info("DLSS: ray reconstruction is not available (" + RayReconstructionNote + ").");
    }

    /// <summary>Whether a plugin actually loaded, rather than merely being on disk.</summary>
    /// <param name="feature">Which feature.</param>
    /// <returns>True when the runtime has it.</returns>
    private bool Loaded(uint feature)
    {
        byte loaded = 0;

        if (_isFeatureLoaded(feature, &loaded) != 0 || loaded == 0)
        {
            Log.Info($"Streamline: feature {feature} stated its requirements but did not load.");
            return false;
        }

        return true;
    }

    /// <summary>Collects what a feature needs from the instance and the device.</summary>
    /// <param name="feature">Which feature to ask.</param>
    /// <returns>
    /// True when it answered. A feature that cannot state its requirements did not load,
    /// whatever its files on disk say — which is also how the caller tells the two denoising
    /// plugins apart, since only the one that is actually there answers.
    /// </returns>
    private bool Gather(uint feature)
    {
        var requirements = new SlFeatureRequirements
        {
            Header = SlHeader.Of(
                0x66714097, 0xac6d, 0x4bc6, 0x89, 0x15, 0x1e, 0x0f, 0x55, 0xa6, 0xb6, 0x1f, 2),
        };

        uint result = _getFeatureRequirements(feature, &requirements);

        if (result != 0)
        {
            Log.Info($"Streamline: feature {feature} states no requirements ({Reason(result)}).");

            return false;
        }

        Collect(_instanceExtensions, requirements.InstanceExtensions, requirements.NumInstanceExtensions);
        Collect(_deviceExtensions, requirements.DeviceExtensions, requirements.NumDeviceExtensions);

        ComputeQueuesWanted = Math.Max(ComputeQueuesWanted, requirements.ComputeQueuesRequired);
        GraphicsQueuesWanted = Math.Max(GraphicsQueuesWanted, requirements.GraphicsQueuesRequired);

        // Printed because it is the one place a mistake in the structure layouts above
        // shows up as something readable rather than as "DLSS is not supported". A count of
        // four with plausible extension names is a layout that matched; a count of nine
        // million is not.
        Log.Info(
            $"Streamline: feature {feature} wants " +
            $"{requirements.NumInstanceExtensions} instance and " +
            $"{requirements.NumDeviceExtensions} device extension(s), " +
            $"{requirements.GraphicsQueuesRequired} graphics and " +
            $"{requirements.ComputeQueuesRequired} compute queue(s); driver " +
            $"{requirements.DriverVersionDetected[0]}.{requirements.DriverVersionDetected[1]} " +
            $"against {requirements.DriverVersionRequired[0]}.{requirements.DriverVersionRequired[1]}");

        return true;
    }

    private static void Collect(List<string> into, byte** names, uint count)
    {
        if (names is null)
        {
            return;
        }

        for (uint i = 0; i < count; i++)
        {
            string? name = Marshal.PtrToStringAnsi((nint)names[i]);

            if (name is { Length: > 0 } && !into.Contains(name, StringComparer.Ordinal))
            {
                into.Add(name);
            }
        }
    }

    /// <summary>Finds one of a feature's own functions, once.</summary>
    private bool Resolve(
        uint feature, string name, ref delegate* unmanaged[Cdecl]<void*, void*, uint> into)
    {
        if (into is not null)
        {
            return true;
        }

        void* function = Function(feature, name);

        if (function is null)
        {
            return false;
        }

        into = (delegate* unmanaged[Cdecl]<void*, void*, uint>)function;
        return true;
    }

    /// <summary>The same, for a function taking one structure.</summary>
    private bool Resolve(
        uint feature, string name, ref delegate* unmanaged[Cdecl]<void*, uint> into)
    {
        if (into is not null)
        {
            return true;
        }

        void* function = Function(feature, name);

        if (function is null)
        {
            return false;
        }

        into = (delegate* unmanaged[Cdecl]<void*, uint>)function;
        return true;
    }

    /// <summary>The same, for a function taking a number and a structure.</summary>
    private bool Resolve(
        uint feature, string name, ref delegate* unmanaged[Cdecl]<uint, void*, uint> into)
    {
        if (into is not null)
        {
            return true;
        }

        void* function = Function(feature, name);

        if (function is null)
        {
            return false;
        }

        into = (delegate* unmanaged[Cdecl]<uint, void*, uint>)function;
        return true;
    }

    /// <summary>The same, for a function taking three structures.</summary>
    private bool Resolve(
        uint feature, string name, ref delegate* unmanaged[Cdecl]<void*, void*, void*, uint> into)
    {
        if (into is not null)
        {
            return true;
        }

        void* function = Function(feature, name);

        if (function is null)
        {
            return false;
        }

        into = (delegate* unmanaged[Cdecl]<void*, void*, void*, uint>)function;
        return true;
    }

    /// <summary>Looks one of a feature's functions up by name.</summary>
    private void* Function(uint feature, string name)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");

        fixed (byte* pointer = bytes)
        {
            void* function = null;

            if (_getFeatureFunction(feature, pointer, &function) != 0)
            {
                return null;
            }

            return function;
        }
    }

    /// <summary>One exported function, or a throw naming the one that was missing.</summary>
    private void* Entry(string name)
    {
        if (!NativeLibrary.TryGetExport(_library, name, out nint address))
        {
            throw new EntryPointNotFoundException(name);
        }

        return (void*)address;
    }
}

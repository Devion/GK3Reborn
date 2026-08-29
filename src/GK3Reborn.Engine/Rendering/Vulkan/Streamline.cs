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
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// NVIDIA Streamline: the loader every NGX feature is reached through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists before the renderer does.</b> Streamline's features ask for Vulkan
/// device extensions and for queues of their own, and both have to be in the
/// <c>vkCreateDevice</c> call. So it is started first, asked what it needs, and the
/// renderer folds that into the instance and device it was going to create anyway. Getting
/// this order wrong is not a subtle failure: DLSS simply reports that the device does not
/// support it.
/// </para>
/// <para>
/// <b>Manual hooking.</b> This engine creates its own instance, device and swapchain and
/// tells Streamline about them afterwards through <c>slSetVulkanInfo</c>. That is the mode
/// NVIDIA calls manual hooking, and it buys super resolution, which needs nothing but a
/// command buffer.
/// </para>
/// <para>
/// <b>What it does not buy, and why.</b> Streamline's own housekeeping runs inside the
/// calls it intercepts — <c>vkQueuePresentKHR</c> above all — so an engine that presents
/// through the loader is one Streamline never sees a frame end in. It says so
/// (<c>presentCommon() was not observed</c>) and warns that what it hands out is never
/// collected again. Frame generation is worse than incomplete: its hooks <i>are</i> the
/// swapchain calls, so it cannot run at all.
/// </para>
/// <para>
/// The documented cure is to load <c>sl.interposer.dll</c> in place of <c>vulkan-1.dll</c>
/// and take <c>vkGetInstanceProcAddr</c> from it. <b>Redirecting the loader alone is not
/// enough and must not be done on its own.</b> Once the interposer proxies device creation
/// Streamline configures itself from what it saw, and the <c>slSetVulkanInfo</c> call below
/// then arrives too late — it fails, and Streamline reports its plugins as already
/// initialised against a device it may not have wanted. The window is a second, separate
/// problem: frame generation learns the HWND from <c>vkCreateWin32SurfaceKHR</c>, and this
/// engine's surface is made by GLFW through a loader of its own, so that hook is never
/// seen and the swapchain is refused outright. Both have to be dealt with in the same
/// change, and that change wants a machine to test on.
/// </para>
/// <para>
/// <b>Nothing is linked.</b> Every entry point is resolved by name from a file the player
/// supplied. A missing file, a wrong architecture, a runtime that declines to start: all of
/// them come back as "DLSS is not available" and the game draws the frame anyway.
/// </para>
/// </remarks>
public sealed unsafe class Streamline : IDisposable
{
    /// <summary>Streamline's own feature numbers.</summary>
    private const uint FeatureSuperResolution = 0;
    private const uint FeatureFrameGeneration = 1000;

    /// <summary>Latency reporting, which frame generation cannot run without.</summary>
    /// <remarks>
    /// <c>sl.dlss_g</c>'s manifest names <c>sl.reflex</c> among its required plugins, and a
    /// required plugin is not loaded on the feature's behalf: a plugin is loaded because the
    /// application asked for its feature, so frame generation without these two in the same
    /// list is frame generation refused for a missing dependency. Reflex in turn reports
    /// through the presented-frame counter, so both go in together.
    /// </remarks>
    private const uint FeatureReflex = 3;
    private const uint FeaturePresentCounter = 4;

    /// <summary>Ray reconstruction, as the public headers number it.</summary>
    /// <remarks>The plugin is <c>sl.dlss_d.dll</c> and its entry point is
    /// <c>slDLSSDSetOptions</c>.</remarks>
    private const uint FeatureRayReconstruction = 1001;

    /// <summary>
    /// Neural rendering: ray reconstruction as the newer <c>sl.dlss_nr.dll</c> numbers
    /// itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plugin's own manifest declares <c>"id": 1004</c>, <c>"rhi": ["d3d12", "vk"]</c>
    /// and an entry point called <c>slDLSSNRSetOptions</c>, none of which appears in any
    /// published Streamline header. Its options structure was read out of the plugin
    /// instead; see <see cref="SlDlssnrOptions"/>, which records what the reading rests on.
    /// </para>
    /// <para>
    /// It is a different feature from <see cref="FeatureRayReconstruction"/> rather than a
    /// renaming of it. It wants far less: colour, depth and motion, tagged as buffers
    /// seventy and seventy-one and the ordinary two, and none of the normals, roughness or
    /// albedo that the documented feature needs. That is why it can run over a picture this
    /// engine already draws.
    /// </para>
    /// </remarks>
    private const uint FeatureNeuralRendering = 1004;

    /// <summary>The version of the interface this was written against.</summary>
    /// <remarks>
    /// 2.12.0, plus the magic the headers append. Streamline accepts a caller older than
    /// itself and refuses one newer, which is why this is the version of the headers the
    /// structures here were copied from rather than the version of the DLL that was found.
    /// </remarks>
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

    /// <summary>The three buffers <c>sl.dlss_nr.dll</c> reads, which the headers reserve.</summary>
    /// <remarks>
    /// <c>sl_core_types.h</c> names seventy, seventy-one and seventy-two only as
    /// <c>kBufferTypeReserved70</c> through <c>72</c>. The plugin asks for exactly those
    /// three: the first two are required and it refuses the frame without them, and the
    /// third is fetched as optional and left null when nothing tagged it. It takes depth and
    /// motion under their ordinary names.
    /// </remarks>
    private const uint TagNeuralInputColor = 70;
    private const uint TagNeuralOutputColor = 71;
    private const uint TagNeuralControlMask = 72;

    /// <summary>A tagged resource does not change until the frame is presented.</summary>
    private const uint ValidUntilPresent = 1;

    /// <summary>
    /// How hard the neural-rendering network is asked to work, from nothing to one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One knob standing in for the five the network exposes, because nothing yet knows
    /// what a GK3 room wants from them and five sliders over an unknown scale is worse than
    /// one number in one place. Turn it down here to see what the network is contributing;
    /// the difference between this and the picture DLSS alone produces is all of it.
    /// </para>
    /// <para>
    /// The scale's top is the only end that is known. The plugin defaults the one control
    /// it added later — skin structure — to one and leaves the four original ones to the
    /// caller, so one is where a caller who set nothing would have landed.
    /// </para>
    /// </remarks>
    private const float NeuralStrength = 1f;

    /// <summary>Whether the network picks its own control mask. One for yes.</summary>
    /// <remarks>
    /// The mask says which pixels it may rework. Nought with no mask tagged is the other
    /// half of the experiment <see cref="NeuralStrength"/> starts.
    /// </remarks>
    private const byte NeuralAutoMask = 1;

    private readonly nint _library;
    private readonly List<string> _instanceExtensions = [];
    private readonly List<string> _deviceExtensions = [];

    private readonly delegate* unmanaged[Cdecl]<void*, ulong, uint> _init;
    private readonly delegate* unmanaged[Cdecl]<uint> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<void*, uint> _setVulkanInfo;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint> _isFeatureSupported;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint> _getFeatureRequirements;
    private readonly delegate* unmanaged[Cdecl]<uint, byte*, void**, uint> _getFeatureFunction;
    private readonly delegate* unmanaged[Cdecl]<void**, uint*, uint> _getNewFrameToken;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, void*, uint> _setConstants;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, void*, uint, void*, uint> _setTagForFrame;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, void**, uint, void*, uint> _evaluateFeature;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint> _freeResources;

    private delegate* unmanaged[Cdecl]<void*, void*, uint> _setDlssOptions;
    private delegate* unmanaged[Cdecl]<void*, void*, uint> _setDlssdOptions;
    private delegate* unmanaged[Cdecl]<void*, void*, uint> _setDlssnrOptions;

    /// <summary>Which denoising feature loaded, or nought when neither did.</summary>
    private uint _denoiser;

    private nint _instance;
    private nint _physicalDevice;
    private nint _device;
    private bool _attached;
    private Matrix4x4? _previousViewProjection;
    private uint _frameNumber;

    private Streamline(nint library)
    {
        _library = library;

        _init = (delegate* unmanaged[Cdecl]<void*, ulong, uint>)Entry("slInit");
        _shutdown = (delegate* unmanaged[Cdecl]<uint>)Entry("slShutdown");
        _setVulkanInfo = (delegate* unmanaged[Cdecl]<void*, uint>)Entry("slSetVulkanInfo");
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
    }

    /// <summary>Whether the whole chain is up and DLSS can be evaluated.</summary>
    public bool Ready => _attached && Supported;

    /// <summary>Whether the device reported that it can run super resolution.</summary>
    public bool Supported { get; private set; }

    /// <summary>Whether ray reconstruction is loaded, supported and driveable.</summary>
    public bool HasRayReconstruction => _denoiser != 0;

    /// <summary>Which of the two denoising plugins is the one that loaded.</summary>
    /// <remarks>
    /// For the settings page and the log. The two are not interchangeable — they want
    /// different inputs — so a player looking at a picture wants to know which one drew it.
    /// </remarks>
    public string RayReconstructionVariant => _denoiser switch
    {
        FeatureNeuralRendering => "DLSS neural rendering",
        FeatureRayReconstruction => "DLSS ray reconstruction",
        _ => string.Empty,
    };

    /// <summary>
    /// Whether the loaded denoiser needs the inputs only a traced picture has.
    /// </summary>
    /// <remarks>
    /// True for the documented feature, which wants normals, roughness and albedo. False
    /// for neural rendering, which asks for colour, depth and motion and nothing else, and
    /// so has something to work with whether or not anything was traced.
    /// </remarks>
    public bool RayReconstructionNeedsTracedInputs => _denoiser == FeatureRayReconstruction;

    /// <summary>Whether the loaded denoiser has the rung the plan is asking for.</summary>
    /// <remarks>
    /// <para>
    /// Neural rendering has no ultra-quality rung: it refuses that mode by number. Falling
    /// back to the neighbouring rung inside the options would be worse than not running,
    /// because the rung is not only a mode — it is the ratio the room was drawn at. The
    /// plugin asks NGX to work the scaling ratio out from the mode it was given, so a mode
    /// that disagrees with <see cref="UpscalePlan.Ratio"/> hands the network an input
    /// smaller or larger than the one it computed for, and every pixel it reads is off.
    /// </para>
    /// <para>
    /// So that rung gets plain super resolution instead, which does have it.
    /// </para>
    /// </remarks>
    /// <param name="quality">The rung the plan asks for.</param>
    /// <returns>True when the denoising feature can be used at that rung.</returns>
    public bool CanReconstruct(UpscalerQuality quality) =>
        HasRayReconstruction &&
        (_denoiser != FeatureNeuralRendering || quality != UpscalerQuality.UltraQuality);

    /// <summary>
    /// Why ray reconstruction is not available, when it looked as though it should be.
    /// </summary>
    /// <remarks>
    /// Empty when there is nothing to say. It exists because "you have the files and it
    /// still is not on" is the one state a player cannot diagnose for themselves.
    /// </remarks>
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
    /// <param name="wantFrameGeneration">Whether to load the frame-generation plugin.</param>
    /// <param name="wantRayReconstruction">Whether to load the ray-reconstruction plugin.</param>
    /// <returns>A started Streamline, or null.</returns>
    /// <remarks>
    /// Which plugins to load is decided here and cannot change afterwards, so both are
    /// loaded whenever their files are present rather than only when the setting is on:
    /// loading a plugin costs a DLL and some address space, and not having loaded it costs
    /// the player a restart when they change their mind.
    /// </remarks>
    public static Streamline? TryStart(
        UpscalerRuntimes? runtimes,
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
            started = new Streamline(library);
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
            if (started.Gather(FeatureNeuralRendering))
            {
                started._denoiser = FeatureNeuralRendering;
            }
            else if (started.Gather(FeatureRayReconstruction))
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
            started.Gather(FeatureReflex);
            started.Gather(FeaturePresentCounter);
            started.Gather(FeatureFrameGeneration);
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

        uint answer = _isFeatureSupported(FeatureSuperResolution, &adapter);

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
            uint denoiser = _isFeatureSupported(_denoiser, &adapter);

            if (denoiser != 0)
            {
                RayReconstructionNote =
                    "this device does not support it (" + Reason(denoiser) + ")";
                _denoiser = 0;
            }
        }

        if (HasFrameGeneration &&
            _isFeatureSupported(FeatureFrameGeneration, &adapter) != 0)
        {
            HasFrameGeneration = false;
        }

        return true;
    }

    /// <summary>Sets what the feature should do, for the sizes now in use.</summary>
    /// <param name="quality">Which rung of the ladder.</param>
    /// <param name="preset">Which trained model, or nought for the runtime's choice.</param>
    /// <param name="display">The size the picture is shown at.</param>
    /// <param name="highDynamicRange">Whether the colour runs past one.</param>
    /// <param name="rayReconstruction">Whether to configure the denoising variant instead.</param>
    /// <returns>True when the runtime accepted it.</returns>
    public bool SetDlssOptions(
        UpscalerQuality quality,
        int preset,
        Extent2D display,
        bool highDynamicRange,
        bool rayReconstruction)
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
                Intensity = NeuralStrength,
                LocalToneStrength = NeuralStrength,
                LocalStructureStrength = NeuralStrength,
                GlobalToneStrength = NeuralStrength,
                SkinStructureStrength = NeuralStrength,

                // Nothing is tagged as a control mask, so the network is left to find its
                // own. What it decides from is unknown, and it decides which pixels it is
                // allowed to rework — so this is the second switch to try when the picture
                // is wrong in a way that follows the camera rather than the geometry.
                UseAutoMask = NeuralAutoMask,

                // Not the super-resolution preset. That number is a rung on a ladder of
                // trained upscaling models named by letter; this one indexes a table of
                // network weights inside nvngx_dlssnr.dll, and the two have nothing to do
                // with each other. The network resolves nought to whichever weights it
                // ships as default, and reports anything it does not have as unavailable
                // and falls back — so a wrong number here is quiet rather than fatal, which
                // is exactly why it should not be a number from the other ladder.
                Preset = 0,

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
    /// <param name="command">The frame's command buffer.</param>
    /// <param name="frame">What to upscale.</param>
    /// <param name="rayReconstruction">Whether the denoising variant is the one running.</param>
    /// <returns>True when the runtime did the work.</returns>
    public bool Evaluate(CommandBuffer command, in UpscaleFrame frame, bool rayReconstruction)
    {
        if (!Ready)
        {
            return false;
        }

        void* token = null;
        uint number = _frameNumber++;

        if (_getNewFrameToken(&token, &number) != 0 || token is null)
        {
            return false;
        }

        SlViewport viewport = Viewport();

        SlResource colour = Describe(frame.Colour, ImageLayout.ShaderReadOnlyOptimal);
        SlResource depth = Describe(frame.Depth, ImageLayout.ShaderReadOnlyOptimal);
        SlResource motion = Describe(frame.Motion, ImageLayout.ShaderReadOnlyOptimal);
        SlResource output = Describe(frame.Output, ImageLayout.General);

        bool neural = rayReconstruction && _denoiser == FeatureNeuralRendering;

        // Neural rendering reads its colour and writes its result under the two buffer
        // names the headers leave reserved, and takes depth and motion under the ordinary
        // ones. Tagging the scaling pair instead leaves it with no input at all, which it
        // reports as a missing input parameter and refuses the frame over.
        uint inputTag = neural ? TagNeuralInputColor : TagScalingInputColor;
        uint outputTag = neural ? TagNeuralOutputColor : TagScalingOutputColor;

        SlResourceTag* tags = stackalloc SlResourceTag[4];

        tags[0] = Tag(&colour, inputTag, frame.Colour.Extent);
        tags[1] = Tag(&output, outputTag, frame.Output.Extent);
        tags[2] = Tag(&depth, TagDepth, frame.Depth.Extent);
        tags[3] = Tag(&motion, TagMotionVectors, frame.Motion.Extent);

        if (_setTagForFrame(token, &viewport, tags, 4, (void*)command.Handle) != 0)
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
        uint result = _evaluateFeature(feature, token, inputs, 1, (void*)command.Handle);

        if (result == 0)
        {
            return true;
        }

        Log.Warning($"WARNING GK3R3438: DLSS declined a frame (code {result}).");
        return false;
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
    /// <remarks>
    /// The matrices are row-major and carry no jitter, which Streamline's own header says
    /// twice. The offset is given separately, and the previous frame's matrix is kept here
    /// rather than asked of the renderer so that the pair is always consistent.
    /// </remarks>
    private bool Constants(void* token, SlViewport* viewport, in UpscaleFrame frame)
    {
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
            MotionVectorScaleX = frame.Motion.Extent.Width > 0
                ? 1f / frame.Motion.Extent.Width
                : 1f,
            MotionVectorScaleY = frame.Motion.Extent.Height > 0
                ? 1f / frame.Motion.Extent.Height
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

        return _setConstants(&constants, token, viewport) == 0;
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

    private static SlResource Describe(UpscaleImage image, ImageLayout layout) => new()
    {
        Header = SlHeader.Of(
            0x3a9d70cf, 0x2418, 0x4b72, 0x83, 0x91, 0x13, 0xf8, 0x72, 0x1c, 0x72, 0x61, 1),

        // Two-dimensional texture.
        Type = 0,
        Native = (nint)image.Image.Handle,
        View = (nint)image.View.Handle,
        State = (uint)layout,
        Width = image.Extent.Width,
        Height = image.Extent.Height,
        NativeFormat = (uint)image.Format,
        MipLevels = 1,
        ArrayLayers = 1,
        Usage = (uint)image.Usage,
    };

    private static SlResourceTag Tag(SlResource* resource, uint type, Extent2D extent) => new()
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
    /// <remarks>
    /// From <c>sl_result.h</c>, in its order. Only the ones that can plausibly come back
    /// from the calls made here are named; anything else is printed as its number, which is
    /// still enough to look up. The point is that "DLSS is unavailable" and "your driver is
    /// too old" are different sentences and only one of them tells the player what to do.
    /// </remarks>
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
                    // Streamline about them afterwards. See the class remarks.
                    Flags = DisableCommandListStateTracking | UseManualHooking |
                            UseFrameBasedResourceTagging,

                    FeaturesToLoad = featurePointer,
                    NumFeaturesToLoad = (uint)wanted.Length,
                    ProjectId = (void*)project,

                    // Custom engine, and Vulkan — which the header says to state, because
                    // it decides what the requirements queries come back with.
                    Engine = 0,
                    EngineVersion = (void*)version,
                    RenderApi = 2,
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
    /// <remarks>
    /// Called from Streamline's own threads, so it does nothing but copy a string and hand
    /// it on. Information is dropped: at the log level set above it is the per-frame
    /// commentary, and a line a frame is not a log, it is a leak.
    /// </remarks>
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
    /// <remarks>
    /// Reached when the files were found on disk and neither feature would state its
    /// requirements, which is the one state a player cannot diagnose for themselves: the
    /// files are plainly there and the setting is plainly off. The usual cause is a network
    /// file that does not match its plugin, or a driver older than the plugin wants — the
    /// neural-rendering plugin asks for 570.
    /// </remarks>
    private void Note()
    {
        RayReconstructionNote =
            "neither denoising plugin would load; check that sl.dlss_nr.dll and " +
            "nvngx_dlssnr.dll are both beside the other Streamline files, and that the " +
            "graphics driver is 570 or newer";

        Log.Info("DLSS: ray reconstruction is not available (" + RayReconstructionNote + ").");
    }

    /// <summary>Collects what a feature needs from the instance and the device.</summary>
    /// <remarks>
    /// Called once per loaded feature, before anything Vulkan exists. The strings come back
    /// pointing into the runtime's own memory and are copied here, because nothing promises
    /// they outlive the call.
    /// </remarks>
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

            // Believing the files instead is how ray reconstruction came to be reported as
            // available and then refused every time it was asked for.
            if (feature == FeatureFrameGeneration)
            {
                HasFrameGeneration = false;
            }

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

        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");

        fixed (byte* pointer = bytes)
        {
            void* function = null;

            if (_getFeatureFunction(feature, pointer, &function) != 0 || function is null)
            {
                return false;
            }

            into = (delegate* unmanaged[Cdecl]<void*, void*, uint>)function;
            return true;
        }
    }

    /// <summary>One exported function, or a throw naming the one that was missing.</summary>
    /// <remarks>
    /// Thrown rather than returned, and caught by the one caller, because a Streamline that
    /// is missing any of these is not a Streamline: the file is truncated, or is a build for
    /// another architecture, and there is nothing to be gained by finding out again on the
    /// next line. The name in the exception is what makes a bad download diagnosable.
    /// </remarks>
    private void* Entry(string name)
    {
        if (!NativeLibrary.TryGetExport(_library, name, out nint address))
        {
            throw new EntryPointNotFoundException(name);
        }

        return (void*)address;
    }
}

// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// The header every Streamline structure begins with: a link, a GUID and a version.
/// </summary>
/// <remarks>
/// <para>
/// Streamline's C++ headers express this as a base class with a deleted default
/// constructor. It has no virtual functions, so its layout is simply its three fields —
/// eight bytes of pointer, sixteen of GUID, eight of version — and this mirrors that
/// exactly.
/// </para>
/// <para>
/// <b>The version is part of the contract.</b> Streamline reads it to decide how many
/// fields it may look at, so a structure declared here at a version whose fields are not
/// all present is a runtime reading past the end of it. Every version below is the one
/// stated in the header the field list was copied from.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlHeader
{
    public void* Next;
    public uint Data1;
    public ushort Data2;
    public ushort Data3;
    public byte Data4;
    public byte Data5;
    public byte Data6;
    public byte Data7;
    public byte Data8;
    public byte Data9;
    public byte Data10;
    public byte Data11;
    public ulong Version;

    /// <summary>Builds one from the GUID as the headers write it.</summary>
    public static SlHeader Of(
        uint a, ushort b, ushort c,
        byte d0, byte d1, byte d2, byte d3, byte d4, byte d5, byte d6, byte d7,
        ulong version) => new()
        {
            Data1 = a,
            Data2 = b,
            Data3 = c,
            Data4 = d0,
            Data5 = d1,
            Data6 = d2,
            Data7 = d3,
            Data8 = d4,
            Data9 = d5,
            Data10 = d6,
            Data11 = d7,
            Version = version,
        };
}

/// <summary>What the application tells Streamline about itself, at startup.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlPreferences
{
    public SlHeader Header;

    public byte ShowConsole;
    private readonly byte _pad0;
    private readonly byte _pad1;
    private readonly byte _pad2;

    public uint LogLevel;
    public void* PathsToPlugins;
    public uint NumPathsToPlugins;
    private readonly uint _pad3;
    public void* PathToLogsAndData;
    public void* AllocateCallback;
    public void* ReleaseCallback;
    public void* LogMessageCallback;
    public ulong Flags;
    public void* FeaturesToLoad;
    public uint NumFeaturesToLoad;
    public uint ApplicationId;
    public uint Engine;
    private readonly uint _pad4;
    public void* EngineVersion;
    public void* ProjectId;
    public uint RenderApi;
    private readonly uint _pad5;
}

/// <summary>The Vulkan objects the application made for itself.</summary>
/// <remarks>
/// Handed over immediately after <c>vkCreateDevice</c>. It is the manual-hooking half of
/// the bargain: Streamline does not proxy the device creation, so it has to be told what
/// was created and which queues were set aside for it.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlVulkanInfo
{
    public SlHeader Header;

    public nint Device;
    public nint Instance;
    public nint PhysicalDevice;

    public uint ComputeQueueIndex;
    public uint ComputeQueueFamily;
    public uint GraphicsQueueIndex;
    public uint GraphicsQueueFamily;
    public uint OpticalFlowQueueIndex;
    public uint OpticalFlowQueueFamily;

    public byte UseNativeOpticalFlowMode;
    private readonly byte _pad0;
    private readonly byte _pad1;
    private readonly byte _pad2;

    public uint ComputeQueueCreateFlags;
    public uint GraphicsQueueCreateFlags;
    public uint OpticalFlowQueueCreateFlags;
}

/// <summary>Which of several pictures on screen a call is about. There is one here.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SlViewport
{
    public SlHeader Header;
    public uint Value;
    private readonly uint _pad;
}

/// <summary>A resource, as Streamline takes one.</summary>
/// <remarks>
/// For Vulkan every field matters: the runtime records its own barriers against the
/// layout given in <see cref="State"/>, and one that does not match what the command
/// buffer actually left the image in is a validation error at best and a read of
/// undefined contents at worst.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlResource
{
    public SlHeader Header;

    public byte Type;
    private readonly byte _pad0;
    private readonly byte _pad1;
    private readonly byte _pad2;
    private readonly uint _pad3;

    public nint Native;
    public nint Memory;
    public nint View;

    public uint State;
    public uint Width;
    public uint Height;
    public uint NativeFormat;
    public uint MipLevels;
    public uint ArrayLayers;
    public ulong GpuVirtualAddress;
    public uint Flags;
    public uint Usage;
    public uint Reserved;
    private readonly uint _pad4;
}

/// <summary>A resource with a name saying what the runtime should read it as.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlResourceTag
{
    public SlHeader Header;

    public SlResource* Resource;
    public uint Type;
    public uint Lifecycle;

    public uint ExtentTop;
    public uint ExtentLeft;
    public uint ExtentWidth;
    public uint ExtentHeight;
}

/// <summary>Where the camera is and where it was, in the form Streamline reads.</summary>
/// <remarks>
/// <b>Row major, and without the jitter.</b> Streamline says so twice in its own header,
/// and it means it: the matrices here describe where geometry is, and the sub-pixel offset
/// is given separately in <see cref="JitterX"/> so that the runtime can account for it
/// where it needs to and ignore it where it does not.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlConstants
{
    public SlHeader Header;

    public fixed float CameraViewToClip[16];
    public fixed float ClipToCameraView[16];
    public fixed float ClipToLensClip[16];
    public fixed float ClipToPrevClip[16];
    public fixed float PrevClipToClip[16];

    public float JitterX;
    public float JitterY;
    public float MotionVectorScaleX;
    public float MotionVectorScaleY;
    public float PinholeOffsetX;
    public float PinholeOffsetY;

    public float CameraPosX;
    public float CameraPosY;
    public float CameraPosZ;
    public float CameraUpX;
    public float CameraUpY;
    public float CameraUpZ;
    public float CameraRightX;
    public float CameraRightY;
    public float CameraRightZ;
    public float CameraForwardX;
    public float CameraForwardY;
    public float CameraForwardZ;

    public float CameraNear;
    public float CameraFar;
    public float CameraFieldOfView;
    public float CameraAspectRatio;
    public float MotionVectorsInvalidValue;

    public byte DepthInverted;
    public byte CameraMotionIncluded;
    public byte MotionVectors3D;
    public byte Reset;
    public byte OrthographicProjection;
    public byte MotionVectorsDilated;
    public byte MotionVectorsJittered;
    private readonly byte _pad0;

    public float MinimumRelativeLinearDepthObjectSeparation;
}

/// <summary>What the super-resolution feature is asked to do.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SlDlssOptions
{
    public SlHeader Header;

    public uint Mode;
    public uint OutputWidth;
    public uint OutputHeight;
    public float Sharpness;
    public float PreExposure;
    public float ExposureScale;

    public byte ColorBuffersHdr;
    public byte IndicatorInvertAxisX;
    public byte IndicatorInvertAxisY;
    private readonly byte _pad0;

    public uint DlaaPreset;
    public uint QualityPreset;
    public uint BalancedPreset;
    public uint PerformancePreset;
    public uint UltraPerformancePreset;
    public uint UltraQualityPreset;

    public byte UseAutoExposure;
    public byte AlphaUpscalingEnabled;
    private readonly byte _pad1;
    private readonly byte _pad2;
}

/// <summary>What the ray-reconstruction feature is asked to do.</summary>
/// <remarks>
/// The same shape as <see cref="SlDlssOptions"/> with the two view matrices and a
/// statement of how the normals and the roughness are packed. This engine packs the
/// roughness into the normal target's spare channel, which is the mode the runtime calls
/// packed and which costs no extra target.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlDlssdOptions
{
    public SlHeader Header;

    public uint Mode;
    public uint OutputWidth;
    public uint OutputHeight;
    public float Sharpness;
    public float PreExposure;
    public float ExposureScale;

    public byte ColorBuffersHdr;
    public byte IndicatorInvertAxisX;
    public byte IndicatorInvertAxisY;
    private readonly byte _pad0;

    public uint NormalRoughnessMode;

    public fixed float WorldToCameraView[16];
    public fixed float CameraViewToWorld[16];

    public byte AlphaUpscalingEnabled;
    private readonly byte _pad1;
    private readonly byte _pad2;
    private readonly byte _pad3;

    public uint DlaaPreset;
    public uint QualityPreset;
    public uint BalancedPreset;
    public uint PerformancePreset;
    public uint UltraPerformancePreset;
    public uint UltraQualityPreset;
}

/// <summary>What the neural-rendering feature is asked to do.</summary>
/// <remarks>
/// <para>
/// <c>sl::DLSSNROptions</c>, as <c>sl.dlss_nr.dll</c> reads it. NVIDIA publishes no header
/// for this one — the Streamline SDK carries <c>sl_dlss.h</c>, <c>sl_dlss_d.h</c> and
/// <c>sl_dlss_g.h</c> and no <c>sl_dlss_nr.h</c> — so every field below was read out of the
/// plugin rather than copied from a declaration. Where each came from is written down
/// because a structure that cannot be checked against a header is worth only as much as the
/// note saying where it came from.
/// </para>
/// <para>
/// <b>How the layout was fixed.</b> The plugin's <c>slDLSSNRSetOptions</c> copies the
/// caller's viewport, hangs these options off its <c>next</c> pointer and calls its own
/// <c>slSetData</c>. That walks the chain for this GUID and normalises what it finds into a
/// seventy-two byte, version-three copy. The offsets it copies from, and the defaults it
/// substitutes for a caller declaring an older version — nought at <c>0x34</c> and
/// <c>0x38</c>, nought at <c>0x3C</c>, <c>1.0f</c> at <c>0x40</c> and three at <c>0x44</c> —
/// are what pin every field down. Each then reaches the network as the NGX parameter named
/// in its summary.
/// </para>
/// <para>
/// <b>Declare version three.</b> Anything lower and the plugin substitutes those defaults
/// instead of reading the later fields, which is a quiet way to lose the quality setting:
/// <see cref="PerformanceMode"/> lives in the version-three tail.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlDlssnrOptions
{
    public SlHeader Header;

    /// <summary>Nought leaves it off and one runs it; nothing else is read.</summary>
    /// <remarks>
    /// The plugin's evaluation reads this before anything else and returns having done
    /// nothing unless it is exactly one, so it is a switch rather than a ladder. The ladder
    /// is <see cref="PerformanceMode"/>.
    /// </remarks>
    public uint Mode;

    /// <summary>NGX <c>DLSSNR.Intensity</c>.</summary>
    public float Intensity;

    /// <summary>NGX <c>DLSSNR.LocalToneStrength</c>.</summary>
    public float LocalToneStrength;

    /// <summary>NGX <c>DLSSNR.LocalStructureStrength</c>.</summary>
    public float LocalStructureStrength;

    /// <summary>NGX <c>DLSSNR.GlobalToneStrength</c>.</summary>
    public float GlobalToneStrength;

    /// <summary>NGX <c>DLSSNR.Style</c>. Nought is the network's own.</summary>
    public uint Style;

    /// <summary>NGX <c>DLSSNR.Hint.Render.Preset</c>. Nought is the runtime's choice.</summary>
    public uint Preset;

    /// <summary>NGX <c>DLSSNR.UseAutoMask</c>: let the network find its own control mask.</summary>
    /// <remarks>
    /// Worth leaving on here. The alternative is tagging a control mask as buffer seventy-two,
    /// and this engine has nothing to put in one.
    /// </remarks>
    public byte UseAutoMask;
    private readonly byte _pad0;
    private readonly byte _pad1;
    private readonly byte _pad2;

    /// <summary>NGX <c>DLSSNR.SkinStructureStrength</c>. The plugin's default is one.</summary>
    public float SkinStructureStrength;

    /// <summary>Which rung of the ladder, numbered as <c>sl::DLSSMode</c> numbers it.</summary>
    /// <remarks>
    /// Reaches NGX as <c>PerfQualityValue</c>, one less than this. The plugin accepts one,
    /// two, three, four and six — max performance, balanced, max quality, ultra performance
    /// and DLAA — and refuses five, ultra quality, with "performance mode is not supported".
    /// </remarks>
    public uint PerformanceMode;
}

/// <summary>What a feature needs before a device is made.</summary>
/// <remarks>
/// Asked before <c>vkCreateDevice</c>, because the answer is a list of device extensions
/// and a count of queues, and both have to be in that call. Nothing in it can be applied
/// afterwards.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlFeatureRequirements
{
    public SlHeader Header;

    public uint Flags;
    public uint MaxCpuThreads;
    public uint MaxViewports;
    public uint NumRequiredTags;
    public void* RequiredTags;

    public fixed uint OsVersionDetected[3];
    public fixed uint OsVersionRequired[3];
    public fixed uint DriverVersionDetected[3];
    public fixed uint DriverVersionRequired[3];

    public uint ComputeQueuesRequired;
    public uint GraphicsQueuesRequired;

    public uint NumDeviceExtensions;
    private readonly uint _pad0;
    public byte** DeviceExtensions;

    public uint NumInstanceExtensions;
    private readonly uint _pad1;
    public byte** InstanceExtensions;

    public uint NumFeatures12;
    private readonly uint _pad2;
    public byte** Features12;

    public uint NumFeatures13;
    private readonly uint _pad3;
    public byte** Features13;

    public uint OpticalFlowQueuesRequired;
    private readonly uint _pad4;
}

/// <summary>Which device a question about support is about.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlAdapterInfo
{
    public SlHeader Header;

    public byte* DeviceLuid;
    public uint DeviceLuidSizeInBytes;
    private readonly uint _pad0;
    public nint VkPhysicalDevice;
}

/// <summary>
/// What Reflex is asked to do about latency.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read out of <c>sl.reflex.dll</c> rather than copied from a header.</b> NVIDIA
/// publishes <c>sl_reflex.h</c>, but no copy of it is in this tree and a structure that is
/// nearly right is worse than one that is absent: Streamline finds a structure in the
/// chained list by GUID and reads its fields by offset, so a wrong GUID is a call that
/// silently does nothing and a wrong offset is a field read from the middle of another one.
/// </para>
/// <para>
/// The plugin's <c>slSetData</c> was decompiled on 2026-08-30. It searches the chain for
/// <c>F03AF81A-6D0B-4902-A651-C4965E215434</c>, reads the mode as a word at thirty-two, the
/// virtual key as a half-word at forty-two, and the thread at forty-four, and then copies
/// forty-eight bytes of the whole thing into its own context. Thirty-two is where a field
/// lands after <see cref="SlHeader"/>, so the field list below is the only one that both
/// fits those three offsets and comes to forty-eight bytes.
/// </para>
/// <para>
/// The two fields nothing here reads are named from the published field order, and both are
/// left at nought: a frame limit this engine does not impose, and a hint that only applies
/// to the boosted mode.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SlReflexOptions
{
    public SlHeader Header;

    /// <summary>Nought off, one low latency, two low latency with boost.</summary>
    public uint Mode;

    /// <summary>A frame cap in microseconds, or nought for none.</summary>
    public uint FrameLimitUs;

    /// <summary>Whether the markers may be used to place the sleep. Boosted mode only.</summary>
    public byte UseMarkersToOptimise;

    private readonly byte _pad0;

    /// <summary>
    /// A hot key that stands in for the latency-ping message, or nought.
    /// </summary>
    /// <remarks>
    /// The plugin refuses anything but <c>VK_F13</c>, <c>VK_F14</c> and <c>VK_F15</c> — it
    /// says so and returns an error — so nought is the only other value worth sending.
    /// </remarks>
    public ushort VirtualKey;

    /// <summary>Which thread the latency statistics messages come from, or nought.</summary>
    public uint IdThread;
}

/// <summary>One marker, saying where in the frame the caller has reached.</summary>
/// <remarks>
/// <para>
/// Not a structure any header names: <c>slReflexSetMarker</c> takes a marker and a frame
/// token, and builds this to pass them down. Recovered by decompiling that function, which
/// writes a header carrying <c>E268B3DC-F963-4C37-9776-AF048E132621</c> at version one, puts
/// the marker at thirty-two, and chains the frame token behind it.
/// </para>
/// <para>
/// <c>slReflexSleep</c> builds the same structure with the marker set to four thousand and
/// ninety-six, which is not a marker at all — it is how the plugin tells its own sleep apart
/// from the markers an application sends. See <see cref="Streamline.MarkerSleep"/>.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SlReflexMarker
{
    public SlHeader Header;

    public uint Marker;
    private readonly uint _pad0;
}

/// <summary>What the frame-generation feature is asked to do.</summary>
/// <remarks>
/// <para>
/// Read out of <c>sl.dlss_g.dll</c> on 2026-08-30, for the reason
/// <see cref="SlReflexOptions"/> gives. Its <c>slSetData</c> searches the chain for
/// <c>FAC5F1CB-2DFD-4F36-A1E6-3A9E865256C5</c>, refuses a count of nought at thirty-six
/// ("numFramesToGenerate must be greater than 0"), refuses one above what the hardware
/// reports, and copies a hundred and twenty bytes into its own context. Its <c>slGetData</c>
/// reads the flags at forty and the six extents and five formats between fifty-two and
/// eighty-eight to estimate memory, which is what fixes every offset below.
/// </para>
/// <para>
/// <b>The count is the whole of multi-frame generation.</b> One generated frame for every
/// drawn one is two times; three is four times. What the card will allow is not a guess —
/// <see cref="SlDlssgState.NumFramesToGenerateMax"/> is the number, and asking for more is
/// an error naming both.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SlDlssgOptions
{
    public SlHeader Header;

    /// <summary>Nought off, one on, two automatic.</summary>
    public uint Mode;

    /// <summary>How many frames to make for each one drawn. One is two-times.</summary>
    public uint NumFramesToGenerate;

    public uint Flags;
    public uint DynamicResWidth;
    public uint DynamicResHeight;
    public uint NumBackBuffers;

    /// <summary>The size the motion and depth buffers are, which here is the render size.</summary>
    public uint MvecDepthWidth;
    public uint MvecDepthHeight;

    /// <summary>The size the colour buffer is, which here is the display size.</summary>
    public uint ColorWidth;
    public uint ColorHeight;

    public uint ColorBufferFormat;
    public uint MvecBufferFormat;
    public uint DepthBufferFormat;
    public uint HudLessBufferFormat;
    public uint UiBufferFormat;

    private readonly uint _pad0;

    /// <summary>
    /// A callback the plugin takes atomically at ninety-six, and which nothing here sets.
    /// </summary>
    /// <remarks>
    /// Named only by its offset, because what it is was not established: the plugin stores
    /// it with an interlocked exchange into a slot nothing else read in the part that was
    /// decompiled. Null is the value a caller who set nothing would have sent.
    /// </remarks>
    public nint Callback;

    private readonly ulong _pad1;
    private readonly ulong _pad2;
}

/// <summary>What the frame-generation feature says about itself.</summary>
/// <remarks>
/// <para>
/// <b>The header's version decides how much of this is filled in.</b> The plugin checks it
/// three times: at two it writes <see cref="NumFramesToGenerateMax"/>, at three the fence
/// pair, at four the last flag. Asking at version one and then reading the maximum is
/// reading whatever was in the buffer, which is how a card that can generate three frames
/// comes to be offered none.
/// </para>
/// <para>
/// Recovered from <c>slGetData</c> in <c>sl.dlss_g.dll</c>, which fills a structure found by
/// <c>CC8AC8E1-A179-44F5-97FA-E74112F9BC61</c>.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SlDlssgState
{
    public SlHeader Header;

    /// <summary>How much device memory it expects to want.</summary>
    public ulong EstimatedVramBytes;

    /// <summary>Nought when it is running; anything else is a reason it is not.</summary>
    public uint Status;

    /// <summary>The smallest edge it will work on, which the plugin states as a hundred.</summary>
    public uint MinWidthOrHeight;

    /// <summary>How many frames the last present actually put on the display.</summary>
    public uint NumFramesActuallyPresented;

    /// <summary>The largest count this card and driver will accept. Version two.</summary>
    public uint NumFramesToGenerateMax;

    private readonly byte _pad0;

    /// <summary>Whether it is in a state where it would run. Version two.</summary>
    public byte Enabled;

    private readonly ushort _pad1;
    private readonly uint _pad2;

    public ulong Fence;
    public ulong FenceValue;

    public byte Flag;
    private readonly byte _pad3;
    private readonly ushort _pad4;
    private readonly uint _pad5;
    private readonly ulong _pad6;
    private readonly ulong _pad7;
    private readonly ulong _pad8;
    private readonly ulong _pad9;
}

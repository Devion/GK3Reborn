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
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SlDlssnrOptions
{
    public SlHeader Header;

    /// <summary>Nought leaves it off and one runs it; nothing else is read.</summary>
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
    public byte UseAutoMask;
    private readonly byte _pad0;
    private readonly byte _pad1;
    private readonly byte _pad2;

    /// <summary>NGX <c>DLSSNR.SkinStructureStrength</c>. The plugin's default is one.</summary>
    public float SkinStructureStrength;

    /// <summary>Which rung of the ladder, numbered as <c>sl::DLSSMode</c> numbers it.</summary>
    public uint PerformanceMode;
}

/// <summary>What a feature needs before a device is made.</summary>
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
    public ushort VirtualKey;

    /// <summary>Which thread the latency statistics messages come from, or nought.</summary>
    public uint IdThread;
}

/// <summary>One marker, saying where in the frame the caller has reached.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SlReflexMarker
{
    public SlHeader Header;

    public uint Marker;
    private readonly uint _pad0;
}

/// <summary>What the frame-generation feature is asked to do.</summary>
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
    public nint Callback;

    private readonly ulong _pad1;
    private readonly ulong _pad2;
}

/// <summary>What the frame-generation feature says about itself.</summary>
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

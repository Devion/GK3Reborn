// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Vulkan;

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

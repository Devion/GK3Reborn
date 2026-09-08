// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// NGX, taken straight from <c>nvngx_dlssnr.dll</c> rather than through Streamline or the
/// driver.
/// </summary>
internal sealed unsafe partial class Ngx : IDisposable
{
    /// <summary>The modules an NGX core has been known to be, in the order to try them.</summary>
    private static readonly string[] Cores =
        ["_nvngx.dll", "nvngx.dll", "nvngx_dlss.dll", "nvngx_dlssd.dll"];

    /// <summary>The feature number neural rendering answers to.</summary>
    public const uint FeatureNeuralRendering = 18;

    /// <summary>What NGX returns when it did the work.</summary>
    private const uint Success = 1;

    /// <summary>The interface version the snippet is told the caller was built against.</summary>
    private const uint ApiVersion = 0x15;

    /// <summary>The application identifier handed to NGX.</summary>
    private const ulong ApplicationId = 141959980;

    private readonly nint _library;
    private readonly void* _device;

    private readonly delegate* unmanaged[Cdecl]<void*, uint> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<void**, uint> _allocateParameters;
    private readonly delegate* unmanaged[Cdecl]<void*, uint> _destroyParameters;
    private readonly delegate* unmanaged[Cdecl]<void*, uint, void*, void**, uint> _createFeature;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, void*, void*, uint> _evaluateFeature;
    private readonly delegate* unmanaged[Cdecl]<void*, uint> _releaseFeature;

    private bool _disposed;

    private Ngx(
        nint library,
        void* device,
        delegate* unmanaged[Cdecl]<void*, uint> shutdown,
        delegate* unmanaged[Cdecl]<void**, uint> allocateParameters,
        delegate* unmanaged[Cdecl]<void*, uint> destroyParameters,
        delegate* unmanaged[Cdecl]<void*, uint, void*, void**, uint> createFeature,
        delegate* unmanaged[Cdecl]<void*, void*, void*, void*, uint> evaluateFeature,
        delegate* unmanaged[Cdecl]<void*, uint> releaseFeature)
    {
        _library = library;
        _device = device;
        _shutdown = shutdown;
        _allocateParameters = allocateParameters;
        _destroyParameters = destroyParameters;
        _createFeature = createFeature;
        _evaluateFeature = evaluateFeature;
        _releaseFeature = releaseFeature;
    }

    /// <summary>What the snippet says its version is, for the startup line.</summary>
    public string Version { get; private init; } = string.Empty;

    /// <summary>Whether a result code means the call did what was asked.</summary>
    /// <param name="result">What NGX returned.</param>
    /// <returns>True on success.</returns>
    public static bool Ok(uint result) => result == Success;

    /// <summary>What a result code means, as far as it can honestly be said.</summary>
    /// <param name="result">What NGX returned.</param>
    /// <returns>A short phrase, or the number.</returns>
    public static string Reason(uint result) => result switch
    {
        0xBAD00001 => "the feature is not supported on this device",
        0xBAD00002 => "a platform error",
        0xBAD00004 => "the feature was not found",
        0xBAD00005 => "a parameter was rejected",
        0xBAD00007 => "NGX was not initialised",
        0xBAD00008 => "an input format the network will not take",
        0xBAD00009 => "a texture the network writes to was not made writable",
        0xBAD0000A => "a required input was missing",
        0xBAD0000B => "the network would not start",
        0xBAD0000C => "the runtime is older than the network",
        0xBAD0000D => "the card is out of memory",
        0xBAD0000E => "a texture format the network will not take",
        0xBAD00012 => "the driver does not implement this feature",
        _ => "code 0x" + result.ToString("X8", CultureInfo.InvariantCulture),
    };

    /// <summary>Opens the snippet and starts NGX on a device.</summary>
    /// <param name="snippet">The full path of <c>nvngx_dlssnr.dll</c>.</param>
    /// <param name="device">The Direct3D 12 device, as an <c>ID3D12Device*</c>.</param>
    /// <returns>The runtime, or null with a line in the log saying why not.</returns>
    public static Ngx? TryStart(string snippet, nint device)
    {
        ArgumentException.ThrowIfNullOrEmpty(snippet);

        if (device == 0)
        {
            return null;
        }

        // The parameter block first. It is the half that can be missing for a reason no file
        // in the libs folder would fix, and opening the snippet to discover that would be
        // work thrown away.
        if (!Core(out void* allocate, out void* destroy))
        {
            Log.Warning(
                "WARNING GK3R3456: neural rendering: no NGX core is loaded in this process, " +
                "so there is nothing to make the network a parameter block. It needs DLSS to " +
                "have started, which is what loads one.");

            return null;
        }

        nint library;

        try
        {
            library = NativeLibrary.Load(snippet);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
        {
            Log.Warning(
                "WARNING GK3R3450: neural rendering: " + Path.GetFileName(snippet) +
                " would not load (" + error.Message + ").");

            return null;
        }

        void* initialise = Export(library, "NVSDK_NGX_D3D12_Init_Ext");
        void* shutdown = Export(library, "NVSDK_NGX_D3D12_Shutdown1");
        void* create = Export(library, "NVSDK_NGX_D3D12_CreateFeature");
        void* evaluate = Export(library, "NVSDK_NGX_D3D12_EvaluateFeature");
        void* release = Export(library, "NVSDK_NGX_D3D12_ReleaseFeature");

        if (initialise is null || shutdown is null || create is null || evaluate is null ||
            release is null)
        {
            Log.Warning(
                "WARNING GK3R3451: neural rendering: " + Path.GetFileName(snippet) +
                " does not export the Direct3D 12 entry points a network snippet should.");

            NativeLibrary.Free(library);
            return null;
        }

        nint path = Marshal.StringToHGlobalUni(InstallPaths.WritableDirectory("ngx"));

        try
        {
            var start =
                (delegate* unmanaged[Cdecl]<ulong, void*, void*, uint, void*, uint>)initialise;

            uint result = start(ApplicationId, (void*)path, (void*)device, ApiVersion, null);

            if (!Ok(result))
            {
                Log.Warning(
                    "WARNING GK3R3452: neural rendering: NGX would not start (" +
                    Reason(result) + ").");

                NativeLibrary.Free(library);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }

        return new Ngx(
            library,
            (void*)device,
            (delegate* unmanaged[Cdecl]<void*, uint>)shutdown,
            (delegate* unmanaged[Cdecl]<void**, uint>)allocate,
            (delegate* unmanaged[Cdecl]<void*, uint>)destroy,
            (delegate* unmanaged[Cdecl]<void*, uint, void*, void**, uint>)create,
            (delegate* unmanaged[Cdecl]<void*, void*, void*, void*, uint>)evaluate,
            (delegate* unmanaged[Cdecl]<void*, uint>)release)
        {
            Version = VersionOf(snippet),
        };
    }

    /// <summary>Makes a parameter block for the network to read.</summary>
    /// <returns>The block, or an empty one when NGX refused.</returns>
    public NgxParameters Allocate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        void* parameters = null;
        uint result = _allocateParameters(&parameters);

        if (Ok(result) && parameters is not null)
        {
            return new NgxParameters(parameters);
        }

        Log.Warning(
            "WARNING GK3R3453: neural rendering: NGX would not make a parameter block (" +
            Reason(result) + ").");

        return default;
    }

    /// <summary>Gives a parameter block back.</summary>
    /// <param name="parameters">The block.</param>
    public void Destroy(NgxParameters parameters)
    {
        if (!_disposed && parameters.Exists)
        {
            _destroyParameters(parameters.Handle);
        }
    }

    /// <summary>Builds a feature, recording whatever setting up it needs onto a list.</summary>
    /// <param name="commandList">An open <c>ID3D12GraphicsCommandList</c>.</param>
    /// <param name="feature">Which feature.</param>
    /// <param name="parameters">What it is to be built for.</param>
    /// <param name="handle">The feature, on success.</param>
    /// <returns>The result code.</returns>
    public uint Create(nint commandList, uint feature, NgxParameters parameters, out nint handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        handle = 0;

        if (commandList == 0 || !parameters.Exists)
        {
            return 0xBAD00005;
        }

        void* built = null;
        uint result = _createFeature((void*)commandList, feature, parameters.Handle, &built);

        if (Ok(result) && built is not null)
        {
            handle = (nint)built;
        }

        return result;
    }

    /// <summary>Runs a feature over one frame.</summary>
    /// <param name="commandList">An open <c>ID3D12GraphicsCommandList</c>.</param>
    /// <param name="feature">What <see cref="Create"/> gave back.</param>
    /// <param name="parameters">This frame's textures and settings.</param>
    /// <returns>The result code.</returns>
    public uint Evaluate(nint commandList, nint feature, NgxParameters parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return commandList == 0 || feature == 0 || !parameters.Exists
            ? 0xBAD00005
            : _evaluateFeature((void*)commandList, (void*)feature, parameters.Handle, null);
    }

    /// <summary>Gives a feature back.</summary>
    /// <param name="feature">What <see cref="Create"/> gave back.</param>
    public void Release(nint feature)
    {
        if (!_disposed && feature != 0)
        {
            _releaseFeature((void*)feature);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown(_device);
        NativeLibrary.Free(_library);
    }

    /// <summary>Finds the parameter block calls in whichever NGX core is already loaded.</summary>
    /// <param name="allocate">The allocator, on success.</param>
    /// <param name="destroy">Its counterpart.</param>
    /// <returns>True when a core was found that has both.</returns>
    private static bool Core(out void* allocate, out void* destroy)
    {
        allocate = null;
        destroy = null;

        foreach (string name in Cores)
        {
            nint module = GetModuleHandleW(name);

            if (module == 0)
            {
                continue;
            }

            void* found = Export(module, "NVSDK_NGX_D3D12_AllocateParameters");
            void* paired = Export(module, "NVSDK_NGX_D3D12_DestroyParameters");

            if (found is null || paired is null)
            {
                continue;
            }

            Log.Info("DLSS: neural rendering takes its parameter block from " + name + ".");

            allocate = found;
            destroy = paired;

            return true;
        }

        return false;
    }

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string name);

    private static void* Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out nint address) ? (void*)address : null;

    private static string VersionOf(string path)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);

            return info.FileVersion is { Length: > 0 } version
                ? version.Replace(',', '.').Replace(" ", string.Empty)
                : string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          NotSupportedException or ArgumentException)
        {
            return string.Empty;
        }
    }
}

/// <summary>One NGX parameter block: the bag of named numbers and textures a network reads.</summary>
/// <param name="parameters">The block NGX handed back.</param>
internal readonly unsafe struct NgxParameters(void* parameters)
{
    private readonly void* _parameters = parameters;

    /// <summary>Whether there is a block here at all.</summary>
    public bool Exists => _parameters is not null;

    /// <summary>The block itself, for the calls that take it whole.</summary>
    public void* Handle => _parameters;

    /// <summary>Sets a plain pointer: a callback, or a texture described elsewhere.</summary>
    /// <param name="name">Which value.</param>
    /// <param name="value">The pointer.</param>
    public void Set(ReadOnlySpan<byte> name, void* value) => Call(0, name, (nint)value);

    /// <summary>Sets a Direct3D 12 texture.</summary>
    /// <param name="name">Which value.</param>
    /// <param name="resource">The resource, as an <c>ID3D12Resource*</c>.</param>
    public void SetResource(ReadOnlySpan<byte> name, nint resource) => Call(1, name, resource);

    /// <summary>Sets a whole number, which is also how a switch is set.</summary>
    /// <param name="name">Which value.</param>
    /// <param name="value">The number.</param>
    public void Set(ReadOnlySpan<byte> name, int value) => Call(3, name, value);

    /// <summary>Sets a size or a count.</summary>
    /// <param name="name">Which value.</param>
    /// <param name="value">The number.</param>
    public void Set(ReadOnlySpan<byte> name, uint value) => Call(4, name, value);

    /// <summary>Sets a strength or a ratio.</summary>
    /// <param name="name">Which value.</param>
    /// <param name="value">The number.</param>
    public void Set(ReadOnlySpan<byte> name, float value) => Call(6, name, value);

    /// <summary>Reads a size back, or leaves what was there alone.</summary>
    /// <param name="name">Which value.</param>
    /// <param name="value">Where to put it.</param>
    /// <returns>True when the block had one.</returns>
    public bool TryGet(ReadOnlySpan<byte> name, ref uint value)
    {
        if (_parameters is null)
        {
            return false;
        }

        fixed (byte* text = name)
        {
            ulong room = value;
            var get = (delegate* unmanaged[Cdecl]<void*, byte*, ulong*, uint>)Slot(12);

            if (!Ngx.Ok(get(_parameters, text, &room)))
            {
                return false;
            }

            value = (uint)room;
            return true;
        }
    }

    private void Call<T>(int slot, ReadOnlySpan<byte> name, T value)
        where T : unmanaged
    {
        if (_parameters is null)
        {
            return;
        }

        fixed (byte* text = name)
        {
            var set = (delegate* unmanaged[Cdecl]<void*, byte*, T, void>)Slot(slot);
            set(_parameters, text, value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void* Slot(int index) => (*(void***)_parameters)[index];
}

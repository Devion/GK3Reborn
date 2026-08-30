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
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The ordinary route to a network is Streamline, which asks
/// the driver's <c>_nvngx.dll</c> for the feature, which loads the matching
/// <c>nvngx_*.dll</c> and dispatches into it. That route is closed for neural rendering: the
/// driver builds a snippet's file name from a table indexed by feature number, and the entry
/// for feature eighteen is an empty string — so no library is ever loaded for it, the
/// per-feature entry point stays null, and every request comes back as
/// <c>NVSDK_NGX_Result_FAIL_NotImplemented</c>. <c>sl.dlss_nr.dll</c> then declares itself
/// unsupported and Streamline drops the plugin. Nothing on this side of the boundary can
/// change that, and a newer driver may simply fill the entry in.
/// </para>
/// <para>
/// <b>What this does instead.</b> A snippet is a complete NGX runtime — it exports
/// <c>NVSDK_NGX_D3D12_Init_Ext</c>, <c>CreateFeature</c>, <c>EvaluateFeature</c> and the rest
/// itself — so opening the file directly and calling its own exports skips the driver's
/// dispatch table altogether. The network is the same one Streamline would have run; only
/// the road to it is different.
/// </para>
/// <para>
/// <b>None of this comes from a header.</b> NVIDIA publishes no <c>sl_dlss_nr.h</c> at all
/// and no NGX headers in this tree. Every signature and every vtable slot below was read out
/// of the binaries on 2026-08-30 and agreed against two independent callers: NVIDIA's own
/// <c>sl.dlss_nr.dll</c>, and a third-party ReShade add-in known to drive this snippet
/// successfully on the driver installed here. See <see cref="NgxParameters"/> for the one
/// place they appeared to differ and how it was settled.
/// </para>
/// <para>
/// <b>The snippet is not quite self-sufficient, and the one thing it lacks decides how this
/// is put together.</b> It exports the whole feature lifecycle — start, create, evaluate,
/// release, shut down — but no way to make the parameter block those calls take. NGX keeps
/// that in the driver's core; a snippet exports only <c>PopulateParameters_Impl</c>, for the
/// core to call back into. Its export table was read to be sure of it. So the block comes
/// from whichever NGX core is already in the process and the feature is driven through the
/// snippet, which is the same split the ReShade add-in makes and the reason it works where
/// the ordinary route does not.
/// </para>
/// <para>
/// The consequence worth knowing: <b>a core has to have been loaded by something else</b>,
/// which in this engine means Streamline having started. Not a burden in practice — this
/// stands in for super resolution, so anybody turning it on has DLSS installed — but it is
/// why an absent core is reported as its own sentence rather than as a missing file.
/// </para>
/// <para>
/// This runs beside Streamline rather than instead of it: super resolution, frame generation
/// and Reflex carry on through Streamline while the denoising network is driven from here.
/// </para>
/// </remarks>
internal sealed unsafe partial class Ngx : IDisposable
{
    /// <summary>The modules an NGX core has been known to be, in the order to try them.</summary>
    private static readonly string[] Cores =
        ["_nvngx.dll", "nvngx.dll", "nvngx_dlss.dll", "nvngx_dlssd.dll"];

    /// <summary>The feature number neural rendering answers to.</summary>
    /// <remarks>
    /// Not a guess: <c>sl.dlss_nr.dll</c> passes exactly this to the requirements query and
    /// to create. It sits one past ray reconstruction, which is thirteen.
    /// </remarks>
    public const uint FeatureNeuralRendering = 18;

    /// <summary>What NGX returns when it did the work.</summary>
    /// <remarks>
    /// One, not nought — the opposite of Streamline's convention and of most of this
    /// codebase's. Everything that went wrong is <c>0xBAD00000</c> with a number in the low
    /// bits, which is why failure is tested by value and not by sign.
    /// </remarks>
    private const uint Success = 1;

    /// <summary>The interface version the snippet is told the caller was built against.</summary>
    private const uint ApiVersion = 0x15;

    /// <summary>The application identifier handed to NGX.</summary>
    /// <remarks>
    /// NGX has two ways in — an identifier NVIDIA issued to a title, or a project identifier
    /// — and this snippet exports only the first. This project has neither, so what is passed
    /// is the value the ReShade add-in passes and this snippet is known to accept. It selects
    /// nothing about the network: it is what NGX files its telemetry and its over-the-air
    /// configuration under. A project that is issued one of its own should put it here.
    /// </remarks>
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
    /// <remarks>
    /// Only the codes this path can plausibly produce are named, and the rest are printed as
    /// their number — which is still enough to look up. Printing a confident wrong sentence
    /// for a code is worse than printing none.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Everything is resolved by name from a file that may not be there, the way the rest of
    /// this engine treats a vendor runtime: an absent or unusable <c>nvngx_dlssnr.dll</c> is
    /// an ordinary answer and never a process that will not start.
    /// </para>
    /// <para>
    /// The directory NGX is given to work in is the game's own writable one rather than the
    /// one the snippet was loaded from. A player's <c>libs</c> folder may sit somewhere they
    /// cannot write, and a runtime that cannot write its working files fails in a way that
    /// reads as the feature being unsupported.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <b>This records into the list rather than merely allocating.</b> The list must be open
    /// and must be submitted afterwards, and the feature is not usable until that submission
    /// has run — which is why creation happens on the first frame that wants the network
    /// rather than where the rest of the frame's plumbing is built.
    /// </remarks>
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
    /// <remarks>
    /// The caller is responsible for the card having finished with it. NGX frees the
    /// network's working memory here, and freeing memory a queue is still reading is a device
    /// loss rather than a leak.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>Adopted, never loaded.</b> Only modules already in the process are looked at, which
    /// is the whole point: the core belongs to whatever put it there — Streamline, by way of
    /// the driver — and it has been initialised on a device this code did not create. Loading
    /// a second copy would be a second, uninitialised NGX handing back blocks nothing had
    /// filled in.
    /// </para>
    /// <para>
    /// Four names because the core has gone under several, tried in the order the ReShade
    /// add-in tries them. The driver's own is the first; the others are what it has been
    /// called and what has carried it.
    /// </para>
    /// </remarks>
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
/// <remarks>
/// <para>
/// A C++ object with nothing but virtual functions, so calling it is a matter of knowing
/// which slot is which. <b>The order is not the one the public NGX header is usually quoted
/// as having.</b> What is below was read off two callers that both work.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>sl.dlss_nr.dll</c> sets textures through slot nought — passing a Direct3D resource
/// straight through on that backend and the address of a description structure on Vulkan,
/// which only a <c>void*</c> slot could do — sets <c>PerfQualityValue</c> through slot three,
/// the sub-rectangles through slot four, every strength through slot six with the value
/// arriving in <c>xmm2</c>, and reads a function pointer back through slot eight.
/// </description>
/// </item>
/// <item>
/// <description>
/// The ReShade add-in sets textures through slot one, its own callback through slot nought,
/// its switches through three, its sizes through four, its strengths through six, and reads
/// an integer back through slot eleven.
/// </description>
/// </item>
/// </list>
/// <para>
/// The two agree on three, four and six and on where the setters stop, which fixes the whole
/// layout: the setters run <c>void*</c>, Direct3D 12 resource, Direct3D 11 resource,
/// <c>int</c>, <c>unsigned</c>, <c>double</c>, <c>float</c>, <c>unsigned long long</c>, and
/// the getters follow in the same order. That is the header's usual list reversed — which is
/// exactly the sort of thing worth writing down once rather than rediscovering from a frame
/// of noise.
/// </para>
/// <para>
/// <b>Names are UTF-8 literals on purpose.</b> Such a literal lives in the assembly's own
/// data with a terminating nought that its length does not count, so taking its address costs
/// nothing and hands NGX precisely the null-terminated string it wants. Marshalling a managed
/// string here would allocate forty-odd times a frame.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>The one slot here that is inferred rather than watched.</b> Every setter above was
    /// seen being called; this getter was not. What fixes it is that the getters follow the
    /// setters in the same order eight slots later, which two observed pairs agree on — a
    /// pointer set at nought and read at eight, and a whole number set at three and read at
    /// eleven. A size is set at four, so it is read at twelve.
    /// </para>
    /// <para>
    /// The answer is read into eight bytes rather than four all the same. If that inference
    /// is ever wrong the neighbouring slot takes a <c>double</c>, and eight bytes written
    /// into room for four is a smashed stack — a crash a long way from its cause. Into eight
    /// bytes it is merely a number that makes no sense, which the one caller checks for.
    /// </para>
    /// </remarks>
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

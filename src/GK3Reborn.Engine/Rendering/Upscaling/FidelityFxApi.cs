// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// The five functions AMD's FidelityFX runtime exports, resolved by name from a file the
/// player supplied.
/// </summary>
/// <remarks>
/// <para>
/// <c>amd_fidelityfx_vk.dll</c> and <c>amd_fidelityfx_dx12.dll</c> present one small C
/// interface for every effect they contain: create a context, configure it, query it, dispatch it, destroy it. Everything
/// specific to super resolution is a structure passed through those, identified by a
/// number in its header — which is why this file is short and the backends' own upscalers is
/// not.
/// </para>
/// <para>
/// <b>Resolved by hand rather than declared with <c>DllImport</c>.</b> A <c>DllImport</c>
/// binds a file name at the point of the call and throws when it is missing, which for a
/// runtime the player may not have installed means the difference between a settings row
/// that says "not installed" and a process that dies the first time somebody selects it.
/// Loading the library by absolute path also means the file found is the one the game
/// looked at and reported the version of, rather than whatever else on the search path
/// happens to be called the same thing.
/// </para>
/// </remarks>
internal sealed unsafe class FfxApi : IDisposable
{
    private readonly nint _library;

    private readonly delegate* unmanaged[Cdecl]<void**, void*, void*, uint> _createContext;
    private readonly delegate* unmanaged[Cdecl]<void**, void*, uint> _destroyContext;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, uint> _configure;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, uint> _query;
    private readonly delegate* unmanaged[Cdecl]<void*, void*, uint> _dispatch;

    private FfxApi(
        nint library,
        delegate* unmanaged[Cdecl]<void**, void*, void*, uint> createContext,
        delegate* unmanaged[Cdecl]<void**, void*, uint> destroyContext,
        delegate* unmanaged[Cdecl]<void*, void*, uint> configure,
        delegate* unmanaged[Cdecl]<void*, void*, uint> query,
        delegate* unmanaged[Cdecl]<void*, void*, uint> dispatch)
    {
        _library = library;
        _createContext = createContext;
        _destroyContext = destroyContext;
        _configure = configure;
        _query = query;
        _dispatch = dispatch;
    }

    /// <summary>Where the library was loaded from.</summary>
    public string Path { get; private init; } = string.Empty;

    /// <summary>Opens the runtime, or returns null when it is not there.</summary>
    /// <param name="path">Full path to <c>amd_fidelityfx_vk.dll</c>, or null.</param>
    /// <returns>The entry points, or null.</returns>
    /// <remarks>
    /// Every failure is null rather than an exception, because every one of them means the
    /// same thing to the caller — this upscaler is not available — and none of them is a
    /// reason not to draw the frame. A file that exists and does not export what it should
    /// is the interesting case: it is what a truncated download or the wrong architecture
    /// looks like, and it comes back null here rather than as an access violation later.
    /// </remarks>
    public static FfxApi? TryOpen(string? path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path))
        {
            return null;
        }

        nint library;

        try
        {
            library = NativeLibrary.Load(path);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException
                                          or ArgumentException)
        {
            return null;
        }

        if (!Find(library, "ffxCreateContext", out nint create) ||
            !Find(library, "ffxDestroyContext", out nint destroy) ||
            !Find(library, "ffxConfigure", out nint configure) ||
            !Find(library, "ffxQuery", out nint query) ||
            !Find(library, "ffxDispatch", out nint dispatch))
        {
            NativeLibrary.Free(library);
            return null;
        }

        return new FfxApi(
            library,
            (delegate* unmanaged[Cdecl]<void**, void*, void*, uint>)create,
            (delegate* unmanaged[Cdecl]<void**, void*, uint>)destroy,
            (delegate* unmanaged[Cdecl]<void*, void*, uint>)configure,
            (delegate* unmanaged[Cdecl]<void*, void*, uint>)query,
            (delegate* unmanaged[Cdecl]<void*, void*, uint>)dispatch)
        {
            Path = path,
        };
    }

    /// <summary>Creates an effect context.</summary>
    /// <param name="context">Receives the handle.</param>
    /// <param name="description">Head of the chained description structures.</param>
    /// <returns>Nought for success.</returns>
    public uint CreateContext(out nint context, void* description)
    {
        void* created = null;
        uint result = _createContext(&created, description, null);
        context = (nint)created;

        return result;
    }

    /// <summary>Destroys one.</summary>
    public uint DestroyContext(ref nint context)
    {
        if (context == 0)
        {
            return 0;
        }

        void* handle = (void*)context;
        uint result = _destroyContext(&handle, null);
        context = 0;

        return result;
    }

    /// <summary>Changes something about a context, or about the runtime when null.</summary>
    public uint Configure(nint context, void* description) =>
        _configure((void*)context, description);

    /// <summary>Asks a context, or the runtime when null, a question.</summary>
    public uint Query(nint context, void* description) =>
        _query((void*)context, description);

    /// <summary>Records work into a command buffer.</summary>
    public uint Dispatch(nint context, void* description) =>
        _dispatch((void*)context, description);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
        }
    }

    private static bool Find(nint library, string name, out nint address) =>
        NativeLibrary.TryGetExport(library, name, out address);
}

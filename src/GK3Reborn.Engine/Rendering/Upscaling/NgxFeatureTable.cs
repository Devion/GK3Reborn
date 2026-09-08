// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Runtime.InteropServices;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// Fills in the one blank entry that stops the driver loading the neural rendering network.
/// </summary>
internal static unsafe partial class NgxFeatureTable
{
    /// <summary>The name the blank entry should have had.</summary>
    private const string Missing = "dlssnr";

    /// <summary>Where the NGX core says it lives.</summary>
    private const string CoreKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";

    /// <summary>
    /// The names the table holds, in order, from feature nought to feature seventeen.
    /// </summary>
    private static readonly string[] Names =
    [
        "dldenoiser", "dlss", "dlinpainting", "dlisr", "dlslowmo", "dlvsr", "dlcolorize",
        "dlstyletransfer", "dlvdenoiser", "dlisp", "dlresolve", "dlssg", "deepdvc", "dlssd",
        "truehdr", "latewarp", "vsr", "fgx",
    ];

    private static bool _tried;

    /// <summary>Whether the entry is filled in, so the driver can load the network.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>What happened, in a sentence, for the log and the settings page.</summary>
    public static string Note { get; private set; } = "not attempted";

    /// <summary>Fills the entry in, once per run.</summary>
    /// <returns>True when the driver can now load the network.</returns>
    public static bool TryEnable()
    {
        if (_tried)
        {
            return Enabled;
        }

        _tried = true;

        if (!OperatingSystem.IsWindows())
        {
            Note = "there is no NGX on this system";
            return false;
        }

        nint core = Core();

        if (core == 0)
        {
            Note = "the NGX core could not be found";
            Log.Info("DLSS: neural rendering is not available — " + Note + ".");

            return false;
        }

        if (!Range(core, out nint start, out nint end))
        {
            Note = "the NGX core is not shaped like a module";
            Log.Info("DLSS: neural rendering is not available — " + Note + ".");

            return false;
        }

        nint* slot = Find(core, start, end);

        if (slot is null)
        {
            // Either a driver new enough to have filled it in — in which case nothing needs
            // doing and the feature will simply work — or one arranged differently, in which
            // case nothing here is safe to write. The two are not told apart, because the
            // answer is the same: leave it alone.
            Note = "this driver's feature table is not the one with the gap";
            Log.Info("DLSS: neural rendering: " + Note + "; it is left alone.");

            return false;
        }

        if (!Write(slot))
        {
            Note = "the feature table could not be made writable";
            Log.Warning("WARNING GK3R3457: neural rendering: " + Note + ".");

            return false;
        }

        Enabled = true;
        Note = "the driver's blank entry for it was filled in";

        Log.Info(
            "DLSS: neural rendering: the driver's feature table had no name for feature 18, " +
            "so it could never load nvngx_dlssnr.dll; " + Missing + " was written into it.");

        return true;
    }

    /// <summary>The NGX core, loading it if nothing has yet.</summary>
    private static nint Core()
    {
        nint loaded = GetModuleHandleW("_nvngx.dll");

        if (loaded != 0)
        {
            return loaded;
        }

        if (Where() is not { Length: > 0 } directory)
        {
            return 0;
        }

        string path = Path.Combine(directory, "_nvngx.dll");

        return NativeLibrary.TryLoad(path, out nint opened) ? opened : 0;
    }

    /// <summary>Where the driver says its NGX core is installed.</summary>
    private static string? Where()
    {
        Span<char> room = stackalloc char[512];
        uint size = (uint)(room.Length * sizeof(char));

        fixed (char* into = room)
        {
            // Restricted to a string value, so a key holding something else cannot be read
            // as a path.
            int result = RegGetValueW(
                HkeyLocalMachine, CoreKey, "FullPath", RrfRtRegSz, null, into, &size);

            if (result != 0)
            {
                return null;
            }
        }

        int length = (int)(size / sizeof(char));

        // The count includes the terminator when the value was stored with one.
        while (length > 0 && room[length - 1] == '\0')
        {
            length--;
        }

        return length > 0 ? new string(room[..length]) : null;
    }

    /// <summary>How far a loaded module reaches.</summary>
    private static bool Range(nint module, out nint start, out nint end)
    {
        start = module;
        end = 0;

        var header = (byte*)module;

        if (*(ushort*)header != 0x5A4D)
        {
            return false;
        }

        byte* nt = header + *(int*)(header + 0x3C);

        if (*(uint*)nt != 0x00004550)
        {
            return false;
        }

        // Size of image, past the file header and the two words that open the optional one.
        end = module + (nint)(*(uint*)(nt + 0x18 + 0x38));

        return end > start;
    }

    /// <summary>Finds the table by matching every name it should hold.</summary>
    /// <returns>The blank entry that follows them, or null.</returns>
    private static nint* Find(nint module, nint start, nint end)
    {
        var header = (byte*)module;
        byte* nt = header + *(int*)(header + 0x3C);

        int sections = *(ushort*)(nt + 6);
        byte* section = nt + 0x18 + *(ushort*)(nt + 0x14);

        for (int i = 0; i < sections; i++, section += 40)
        {
            uint characteristics = *(uint*)(section + 36);

            // Writable, and not code.
            if ((characteristics & 0x80000000) == 0 || (characteristics & 0x20000000) != 0)
            {
                continue;
            }

            nint from = module + (nint)(*(uint*)(section + 12));
            nint to = from + (nint)(*(uint*)(section + 8));

            if (to > end)
            {
                to = end;
            }

            // One past the last place a whole table could begin.
            nint last = to - ((Names.Length + 1) * sizeof(nint));

            for (nint at = Aligned(from); at <= last; at += sizeof(nint))
            {
                var candidate = (nint*)at;

                if (Matches(candidate, start, end))
                {
                    return candidate + Names.Length;
                }
            }
        }

        return null;
    }

    private static nint Aligned(nint address) =>
        (address + (sizeof(nint) - 1)) & ~(nint)(sizeof(nint) - 1);

    /// <summary>Whether every name is where it should be, and the entry after them is blank.</summary>
    private static bool Matches(nint* table, nint start, nint end)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            if (!Reads(table[i], start, end, Names[i]))
            {
                return false;
            }
        }

        return Reads(table[Names.Length], start, end, string.Empty);
    }

    /// <summary>Whether a pointer inside the module reads as exactly this string.</summary>
    private static bool Reads(nint pointer, nint start, nint end, string expected)
    {
        if (pointer < start || pointer >= end)
        {
            return false;
        }

        if ((end - pointer) / sizeof(char) <= expected.Length)
        {
            return false;
        }

        var text = (char*)pointer;

        for (int i = 0; i < expected.Length; i++)
        {
            if (text[i] != expected[i])
            {
                return false;
            }
        }

        return text[expected.Length] == '\0';
    }

    /// <summary>Puts the name into the entry, and puts the page back as it was.</summary>
    private static bool Write(nint* slot)
    {
        if (!VirtualProtect((nint)slot, sizeof(nint), PageReadWrite, out uint was))
        {
            return false;
        }

        // Checked again with the page open, because everything about this rests on the entry
        // being the empty one that was found a moment ago.
        if (*(char*)*slot == '\0')
        {
            *slot = Marshal.StringToHGlobalUni(Missing);
        }

        VirtualProtect((nint)slot, sizeof(nint), was, out _);

        return true;
    }

    private const uint PageReadWrite = 4;
    private const uint RrfRtRegSz = 0x00000002;
    private static readonly nint HkeyLocalMachine = unchecked((nint)0x80000002);

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string name);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(
        nint address, nint size, uint protect, out uint previous);

    [LibraryImport("advapi32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValueW(
        nint key, string subKey, string value, uint flags, uint* type, char* data, uint* size);
}

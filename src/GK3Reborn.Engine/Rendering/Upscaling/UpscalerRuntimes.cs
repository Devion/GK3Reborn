// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Diagnostics;
using GK3Reborn.Foundation;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>What was found of one vendor's runtime, and what is missing.</summary>
/// <param name="Present">Whether everything the backend needs is there.</param>
/// <param name="Directory">Where the files were found, or null.</param>
/// <param name="Version">What the principal file says its version is, or null.</param>
/// <param name="Missing">The files that were looked for and not found.</param>
public readonly record struct RuntimeFiles(
    bool Present,
    string? Directory,
    string? Version,
    IReadOnlyList<string> Missing)
{
    /// <summary>Nothing was found and nothing was looked for.</summary>
    public static RuntimeFiles Absent { get; } = new(false, null, null, []);

    /// <summary>A sentence for the settings page saying what the state of this is.</summary>
    public string Describe() => Present
        ? Version is { Length: > 0 } version ? version : "installed"
        : Missing.Count > 0 ? "missing " + string.Join(", ", Missing) : "not installed";
}

/// <summary>
/// Which of the vendors' upscaler runtimes the player has put beside the game.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here ships with the game.</b> FSR's <c>amd_fidelityfx_vk.dll</c> and
/// NVIDIA's Streamline and NGX libraries are redistributables with their own licences and
/// their own signatures, and the right thing to do with somebody else's signed binary is
/// to let the person who wants it fetch it. So the game looks for them, says plainly what
/// it found, and works without them.
/// </para>
/// <para>
/// This is also the reason none of it is linked. Every entry point is resolved by name at
/// runtime from a file that may not exist, which is what makes "the DLL is not there" an
/// ordinary answer rather than a process that will not start. The one thing the game must
/// never do is fail to launch because an optional upscaler is absent.
/// </para>
/// <para>
/// <b>Where it looks.</b> <c>libs/</c> beside the executable, then <c>libs/streamline/</c>
/// under it, then beside the executable itself, then anywhere <c>--libs-dir</c> named. The
/// nested streamline directory is there because that is the shape NVIDIA's own download
/// unpacks to, and asking somebody to flatten a directory before the game will see it is a
/// support question nobody needs to answer twice.
/// </para>
/// </remarks>
public sealed class UpscalerRuntimes
{
    /// <summary>The directory a player is told to copy runtimes into.</summary>
    public const string LibraryDirectory = "libs";

    /// <summary>AMD's FidelityFX entry point for Vulkan.</summary>
    public const string FidelityFx = "amd_fidelityfx_vk.dll";

    /// <summary>AMD's runtime, for the Direct3D backend.</summary>
    /// <remarks>
    /// A different file rather than a different entry point: the FidelityFX API is one C
    /// interface with one backend built into each library, so a machine that runs the game
    /// on both backends wants both files and a machine that runs one wants one.
    /// </remarks>
    public const string FidelityFxDirect3D12 = "amd_fidelityfx_dx12.dll";

    /// <summary>NVIDIA's Streamline loader.</summary>
    public const string StreamlineInterposer = "sl.interposer.dll";

    /// <summary>Streamline's super-resolution plugin.</summary>
    public const string StreamlineSuperResolution = "sl.dlss.dll";

    /// <summary>Streamline's frame-generation plugin.</summary>
    public const string StreamlineFrameGeneration = "sl.dlss_g.dll";

    /// <summary>Streamline's ray-reconstruction plugin.</summary>
    public const string StreamlineRayReconstruction = "sl.dlss_nr.dll";

    /// <summary>The super-resolution network itself.</summary>
    public const string NgxSuperResolution = "nvngx_dlss.dll";

    /// <summary>The frame-generation network.</summary>
    public const string NgxFrameGeneration = "nvngx_dlssg.dll";

    /// <summary>The ray-reconstruction network.</summary>
    public const string NgxRayReconstruction = "nvngx_dlssnr.dll";

    private UpscalerRuntimes(
        IReadOnlyList<string> searched,
        RuntimeFiles fsr,
        RuntimeFiles dlss,
        RuntimeFiles dlssFrameGeneration,
        RuntimeFiles dlssRayReconstruction)
    {
        Searched = searched;
        Fsr = fsr;
        Dlss = dlss;
        DlssFrameGeneration = dlssFrameGeneration;
        DlssRayReconstruction = dlssRayReconstruction;
    }

    /// <summary>Where this looked, in the order it looked.</summary>
    public IReadOnlyList<string> Searched { get; }

    /// <summary>AMD's runtime.</summary>
    public RuntimeFiles Fsr { get; }

    /// <summary>NVIDIA's, for upscaling.</summary>
    public RuntimeFiles Dlss { get; }

    /// <summary>NVIDIA's, for generating frames.</summary>
    public RuntimeFiles DlssFrameGeneration { get; }

    /// <summary>NVIDIA's, for denoising the traced terms while it upscales them.</summary>
    public RuntimeFiles DlssRayReconstruction { get; }

    /// <summary>What was found for one kind of upscaler.</summary>
    /// <param name="kind">Which one.</param>
    /// <returns>Its files. The two that need nothing installed report themselves present.</returns>
    public RuntimeFiles For(UpscalerKind kind) => kind switch
    {
        UpscalerKind.Fsr => Fsr,
        UpscalerKind.Dlss => Dlss,

        // Off and Spatial are the engine's own and are always there. Saying so through the
        // same type means the settings page has one rule for drawing a row rather than two.
        _ => new RuntimeFiles(true, null, "built in", []),
    };

    /// <summary>What one kind of upscaler needs, whether or not anybody has looked.</summary>
    /// <param name="kind">Which one.</param>
    /// <returns>Its files, or nothing for the two the engine carries itself.</returns>
    /// <remarks>
    /// Static, because the settings page has to be able to name the files on a front end
    /// nobody has handed a search to — which is what a test looks like, and what the first
    /// frame of a run looks like. Saying "copy nothing into libs" is worse than saying
    /// nothing at all.
    /// </remarks>
    public static IReadOnlyList<string> Required(UpscalerKind kind) => kind switch
    {
        UpscalerKind.Fsr => [FidelityFx, FidelityFxDirect3D12],
        UpscalerKind.Dlss =>
            [StreamlineInterposer, StreamlineSuperResolution, NgxSuperResolution],
        _ => [],
    };

    /// <summary>What is known about a kind when nothing has been searched for.</summary>
    /// <param name="kind">Which one.</param>
    /// <returns>Present for the engine's own, and absent-with-a-list for the vendors'.</returns>
    public static RuntimeFiles Unknown(UpscalerKind kind) =>
        kind is UpscalerKind.Off or UpscalerKind.Spatial
            ? new RuntimeFiles(true, null, "built in", [])
            : new RuntimeFiles(false, null, null, Required(kind));

    /// <summary>Looks for every runtime.</summary>
    /// <param name="extraDirectory">A directory named on the command line, or null.</param>
    /// <returns>What is there.</returns>
    /// <remarks>
    /// Called once at startup and kept. The files cannot appear while the game is running
    /// in any way that would help — a Vulkan device is already made by the time the menu
    /// is reachable — and re-probing the disk every time somebody steps the upscaler row
    /// would be a file system hit inside a keyboard repeat.
    /// </remarks>
    public static UpscalerRuntimes Find(string? extraDirectory = null)
    {
        List<string> searched = [];

        if (extraDirectory is { Length: > 0 })
        {
            searched.Add(Path.GetFullPath(extraDirectory));
        }

        string beside = AppContext.BaseDirectory;

        searched.Add(Path.Combine(beside, LibraryDirectory));
        searched.Add(Path.Combine(beside, LibraryDirectory, "streamline"));
        searched.Add(beside);

        // A packaged Mac build keeps its shipped files in the bundle rather than beside the
        // executable. Neither of these runtimes has a macOS build today, so this is for the
        // shape of the search rather than for anything that will be found there.
        if (InstallPaths.BundleResources is { } resources)
        {
            searched.Add(Path.Combine(resources, LibraryDirectory));
        }

        return new UpscalerRuntimes(
            searched,
            FidelityFxFiles(searched),
            Look(searched, StreamlineInterposer,
                [StreamlineInterposer, StreamlineSuperResolution, NgxSuperResolution]),
            Look(searched, StreamlineFrameGeneration,
                [StreamlineFrameGeneration, NgxFrameGeneration]),
            Look(searched, StreamlineRayReconstruction,
                [StreamlineRayReconstruction, NgxRayReconstruction]));
    }

    /// <summary>Finds one file in the directories searched.</summary>
    /// <param name="name">File to find.</param>
    /// <returns>Its full path, or null.</returns>
    /// <remarks>
    /// Public because loading is somebody else's job: the backends resolve their own
    /// entry points, and each needs the path of the file it is about to open.
    /// </remarks>
    public string? Locate(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        foreach (string directory in Searched)
        {
            string candidate = Path.Combine(directory, name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The whole search, as one line for the startup report.
    /// </summary>
    /// <remarks>
    /// Said whether or not anything was found. A player who copied the files into the
    /// wrong directory has no other way to discover it: the settings row would say "not
    /// installed" and they would already believe they had installed it.
    /// </remarks>
    public override string ToString() =>
        $"Upscalers: FSR {Fsr.Describe()}; DLSS {Dlss.Describe()}; " +
        $"DLSS frame generation {DlssFrameGeneration.Describe()}; " +
        $"DLSS ray reconstruction {DlssRayReconstruction.Describe()}";

    /// <summary>AMD's runtime, whichever of the two backends' libraries is there.</summary>
    /// <param name="searched">Where to look.</param>
    /// <returns>Whichever was found, or an absence naming both.</returns>
    /// <remarks>
    /// Which one a run needs depends on the backend it started in, which is not known here
    /// and is not worth threading through: what this answers is whether the settings page
    /// may offer the row at all, and the backend that goes looking for its own file reports
    /// its own absence when it does not find one. What an absence must name is both, because
    /// somebody who reads it does not yet know which backend they will be running.
    /// </remarks>
    private static RuntimeFiles FidelityFxFiles(IReadOnlyList<string> searched)
    {
        RuntimeFiles vulkan = Look(searched, FidelityFx, [FidelityFx]);

        if (vulkan.Present)
        {
            return vulkan;
        }

        RuntimeFiles direct = Look(searched, FidelityFxDirect3D12, [FidelityFxDirect3D12]);

        return direct.Present
            ? direct
            : new RuntimeFiles(false, null, null, [FidelityFx, FidelityFxDirect3D12]);
    }

    private static RuntimeFiles Look(
        IReadOnlyList<string> searched, string principal, IReadOnlyList<string> required)
    {
        List<string> missing = [];
        string? directory = null;

        foreach (string wanted in required)
        {
            string? found = null;

            foreach (string candidate in searched)
            {
                string path = Path.Combine(candidate, wanted);

                if (!File.Exists(path))
                {
                    continue;
                }

                found = path;

                // The directory reported is the one the principal file was found in, which
                // is what a loader is pointed at. The others may legitimately be elsewhere:
                // NVIDIA's own layout has the networks a level up from the plugins.
                if (string.Equals(wanted, principal, StringComparison.OrdinalIgnoreCase))
                {
                    directory = candidate;
                }

                break;
            }

            if (found is null)
            {
                missing.Add(wanted);
            }
        }

        if (missing.Count > 0)
        {
            return new RuntimeFiles(false, directory, null, missing);
        }

        string? version = null;

        foreach (string candidate in searched)
        {
            string path = Path.Combine(candidate, principal);

            if (File.Exists(path))
            {
                version = VersionOf(path);
                break;
            }
        }

        return new RuntimeFiles(true, directory, version, []);
    }

    /// <summary>What a file says its version is, or null.</summary>
    /// <remarks>
    /// Read from the file rather than assumed, because the whole point of these being the
    /// player's own copies is that they can be newer than anything this project has seen.
    /// A version resource is a Windows notion; elsewhere this comes back null and the row
    /// reads "installed", which is all it could honestly say.
    /// </remarks>
    private static string? VersionOf(string path)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);

            return info.FileVersion is { Length: > 0 } version
                ? version.Replace(',', '.').Replace(" ", string.Empty)
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}

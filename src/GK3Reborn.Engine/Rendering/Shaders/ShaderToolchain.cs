using Silk.NET.Direct3D.Compilers;
using Silk.NET.SPIRV.Cross;
using Silk.NET.Shaderc;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The three native compilers, loaded once for as long as the process lives.
/// </summary>
/// <remarks>
/// <para>
/// Silk.NET's <c>GetApi</c> is not a lookup. Every call opens the shared library again and
/// resolves every entry point into a fresh handle, and every <c>Dispose</c> closes it
/// again; when the last handle goes, the library is unmapped. Compilers here are created
/// per <see cref="ShaderCompiler"/> and a <see cref="ShaderCompiler"/> is created per
/// renderer, per pass and — in the tests — per test, so what the engine was actually doing
/// was mapping and unmapping glslang and SPIRV-Cross dozens of times in a run.
/// </para>
/// <para>
/// <b>That is what crashed the Linux build.</b> The suite passed all 1805 tests and then
/// died with SIGSEGV as the process exited, on Ubuntu only. Ubuntu is the only one of the
/// three platforms where the unmapping is real: <c>dlclose</c> on glibc genuinely unmaps
/// the image and runs its static destructors, where Windows keeps the DLL for as long as
/// anything holds it and macOS declines to unload most images at all. Two things go wrong
/// once it does. Handles held by another thread — xunit runs test classes in parallel, and
/// two of them compile shaders — keep pointing into an image that has been unmapped and
/// remapped somewhere else; and glslang and SPIRV-Cross are C++ libraries whose static and
/// thread-local destructors are registered with libstdc++ and libc, which are not
/// unloaded, so a load/unload cycle leaves those registrations pointing at addresses that
/// are no longer mapped. They are called at process exit. Nothing has gone wrong by then,
/// which is why the summary prints first.
/// </para>
/// <para>
/// So the library is loaded at most once and never released. That is what a handle to a
/// compiler is for: it is a table of function pointers, it costs one mapping, and no part
/// of the engine benefits from giving it back. Each is created on first use and not
/// before — a Vulkan session never loads <c>dxcompiler</c>, and a machine with no DXC can
/// still compile every shader it needs — so an absent library is still an exception at the
/// call that wanted it rather than at type load.
/// </para>
/// </remarks>
internal static class ShaderToolchain
{
    private static readonly Lazy<Shaderc> ShadercApi =
        new(Shaderc.GetApi, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Cross> CrossApi =
        new(Cross.GetApi, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<DXC> DxcApi =
        new(DXC.GetApi, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>shaderc, which is glslang and SPIRV-Tools behind a C interface.</summary>
    /// <exception cref="DllNotFoundException">The library is not on this machine.</exception>
    internal static Shaderc Shaderc => ShadercApi.Value;

    /// <summary>SPIRV-Cross.</summary>
    /// <exception cref="DllNotFoundException">The library is not on this machine.</exception>
    internal static Cross Cross => CrossApi.Value;

    /// <summary>DXC, which exists on Windows and nowhere else.</summary>
    /// <exception cref="DllNotFoundException">The library is not on this machine.</exception>
    internal static DXC Dxc => DxcApi.Value;
}

using Silk.NET.Direct3D.Compilers;
using Silk.NET.SPIRV.Cross;
using Silk.NET.Shaderc;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The three native compilers, loaded once for as long as the process lives.
/// </summary>
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

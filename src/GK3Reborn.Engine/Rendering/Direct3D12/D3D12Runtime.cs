using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// The two Direct3D libraries, loaded once for as long as the process lives.
/// </summary>
/// <remarks>
/// <para>
/// Silk.NET's <c>GetApi</c> opens the library and <c>Dispose</c> closes it, and this
/// backend called both from three places: a context, a device survey, and — once per
/// signature — the root signature serializer. Windows refcounts <c>LoadLibrary</c>, so the
/// churn is invisible here in a way it is not on glibc, where the same shape unmapped
/// glslang under the tests and killed the Linux build at exit. The account is in
/// <c>Rendering/Shaders/ShaderToolchain.cs</c>; this is the same mistake on the platform
/// that happens to tolerate it, which is the harder one to notice and the easier one to
/// copy.
/// </para>
/// <para>
/// Both handles are created on first use, so nothing loads <c>d3d12.dll</c> on a machine
/// running the Vulkan backend, and a machine with no Direct3D at all still fails at the
/// call that wanted it. Nothing is lost by never releasing them: they are tables of
/// function pointers, and every device, factory and adapter opened through them is still
/// released by whoever owns it.
/// </para>
/// </remarks>
internal static class D3D12Runtime
{
    private static readonly Lazy<D3D12> Api =
        new(D3D12.GetApi, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<DXGI> DxgiApi =
        new(static () => DXGI.GetApi(null), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Direct3D 12.</summary>
    /// <exception cref="DllNotFoundException">The runtime is not on this machine.</exception>
    internal static D3D12 D3D12 => Api.Value;

    /// <summary>DXGI.</summary>
    /// <exception cref="DllNotFoundException">The runtime is not on this machine.</exception>
    internal static DXGI Dxgi => DxgiApi.Value;
}

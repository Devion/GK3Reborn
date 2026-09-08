using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// The two Direct3D libraries, loaded once for as long as the process lives.
/// </summary>
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

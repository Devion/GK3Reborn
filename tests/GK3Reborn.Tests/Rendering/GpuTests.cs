using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The tests that open a real graphics device, which run one at a time.
/// </summary>
/// <remarks>
/// <para>
/// xUnit runs test classes in parallel, and opening several Direct3D devices at once — each
/// turning the debug layer on, which is a process-wide switch rather than a per-device one —
/// fails somewhere inside the runtime rather than at any call this code makes. The symptom
/// is a null device coming back from a <c>CreateDevice</c> that reported success, and it
/// appears only when the whole suite runs: every one of these classes passes on its own.
/// </para>
/// <para>
/// Naming a collection is how xUnit is told two classes may not overlap. It costs a few
/// seconds of a run that is already dominated by device creation, and buys a suite that
/// does not fail differently depending on what else was running.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuTests
{
    /// <summary>What the collection is called.</summary>
    public const string Name = "gpu";
}

using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// The tests that open a real graphics device, which run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuTests
{
    /// <summary>What the collection is called.</summary>
    public const string Name = "gpu";
}

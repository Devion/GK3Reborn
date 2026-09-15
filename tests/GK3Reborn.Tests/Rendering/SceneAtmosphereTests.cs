using System.Numerics;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

public sealed class SceneAtmosphereTests
{
    [Fact]
    public void Omitted_overrides_preserve_room_defaults()
    {
        SceneAtmosphere atmosphere = SceneAtmosphere.Parse("{}");
        Assert.Null(atmosphere.Ambient);
        Assert.Null(atmosphere.Fog);
        Assert.Equal(1f, SceneAtmosphere.Parse("{\"fog\":{}}").Fog!.Value.DirectLight);
    }

    [Fact]
    public void Dark_cave_can_use_ambient_mist_without_unshadowed_lamp_scatter()
    {
        SceneAtmosphere atmosphere = SceneAtmosphere.Parse("""
            {"ambient":[0.008,0.011,0.018],"fog":{"density":0.0028,"top":3,"falloff":8,"directLight":0}}
            """);
        Assert.Equal(new Vector3(.008f, .011f, .018f), atmosphere.Ambient);
        Assert.True(atmosphere.Fog!.Value.Any);
        Assert.Equal(51f, atmosphere.Fog.Value.Ceiling);
        Assert.Equal(0f, atmosphere.Fog.Value.DirectLight);
    }

    [Theory]
    [InlineData("{\"ambient\":[1,2,3]}")]
    [InlineData("{\"ambient\":[0,0]}")]
    [InlineData("{\"fog\":{\"falloff\":0}}")]
    [InlineData("{\"fog\":{\"density\":-1}}")]
    [InlineData("{\"fog\":{\"steps\":2.5}}")]
    public void Invalid_atmospheres_fail_before_rendering(string json)
    {
        Assert.Throws<FormatException>(() => SceneAtmosphere.Parse(json));
    }
}

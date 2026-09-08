using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for finding a scene's terrain set from its sky.
/// </summary>
public sealed class TerrainBackdropTests
{
    private static SkyboxDefinition Sky(
        string? front = null, string? back = null, string? up = null,
        string? down = null, string? right = null, string? left = null) =>
        new(left, right, front, back, up, down, 0f);

    [Fact]
    public void A_face_names_its_set()
    {
        Assert.Equal("BMB_A", SceneLoader.TerrainSetName(Sky(front: "BMB_A_512FT")));
        Assert.Equal("ARM_N", SceneLoader.TerrainSetName(Sky(front: "ARM_N_512RT")));

        // Case as the data has it: the sets on disk are upper, the scene may not be.
        Assert.Equal("cse_m", SceneLoader.TerrainSetName(Sky(front: "cse_m_512up")));
    }

    [Fact]
    public void Any_face_will_do_when_the_front_is_missing()
    {
        Assert.Equal("LHE_E", SceneLoader.TerrainSetName(Sky(left: "LHE_E_512LF")));
        Assert.Equal("WOD_M", SceneLoader.TerrainSetName(Sky(up: "WOD_M_512UP")));
    }

    [Fact]
    public void A_sky_outside_the_convention_names_no_set()
    {
        // Not the <set>_512<side> shape: nothing to derive, painted horizon kept.
        Assert.Null(SceneLoader.TerrainSetName(Sky(front: "SUNSET")));
        Assert.Null(SceneLoader.TerrainSetName(Sky(front: "BMB_A_512")));
        Assert.Null(SceneLoader.TerrainSetName(Sky(front: "BMB_A_512RTXL")));
        Assert.Null(SceneLoader.TerrainSetName(Sky()));

        // The marker alone is not a set name.
        Assert.Null(SceneLoader.TerrainSetName(Sky(front: "_512RT")));
    }
}

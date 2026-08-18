using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Checks that ray tracing changes the picture in the way it is supposed to.
/// </summary>
/// <remarks>
/// <para>
/// A ray-traced render that merely looks plausible proves nothing: a shader whose rays all
/// miss produces a perfectly reasonable unshadowed image. These build a scene where the
/// right answer is known — a wall standing between a light and a floor — and check that
/// adding the wall darkens the floor with rays and does not darken it without them.
/// </para>
/// <para>
/// The camera looks straight down, so the wall is edge-on and hides almost none of the
/// floor. That is what makes the difference between the two renders evidence of shadowing
/// rather than of the occluder simply being in the way.
/// </para>
/// <para>
/// They skip on hardware without the extensions, so a machine that cannot ray trace still
/// reports a green run.
/// </para>
/// </remarks>
public sealed class RayTracingTests
{
    private static bool HasRayTracing()
    {
        try
        {
            using VulkanContext context = VulkanContext.CreateHeadless();
            return context.SupportsRayTracing;
        }
        catch (VulkanException)
        {
            return false;
        }
    }

    private static float MeanLuminance(DecodedImage image)
    {
        double total = 0;

        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            total += (0.2126 * image.Pixels[i]) +
                     (0.7152 * image.Pixels[i + 1]) +
                     (0.0722 * image.Pixels[i + 2]);
        }

        return (float)(total / (image.Width * image.Height));
    }

    private static ModFile Quad(string texture, Vector3[] positions, Vector3 normal)
    {
        var submesh = new ModSubmesh
        {
            TextureName = texture,
            Color = (255, 255, 255),
            Positions = positions,
            Normals = [normal, normal, normal, normal],
            TexCoords = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
            Indices = [0, 1, 2, 0, 2, 3],
        };

        Vector3 minimum = positions[0];
        Vector3 maximum = positions[0];

        foreach (Vector3 position in positions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        return ModFile.FromMeshes(
            "quad",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = minimum,
                    BoundsMax = maximum,
                    Submeshes = [submesh],
                },
            ]);
    }

    /// <summary>A horizontal quad facing up, centred on the origin.</summary>
    private static ModFile Floor(float half) => Quad(
        "white",
        [
            new(-half, 0, -half), new(half, 0, -half), new(half, 0, half), new(-half, 0, half),
        ],
        Vector3.UnitY);

    /// <summary>A vertical quad standing on the floor along the x axis.</summary>
    private static ModFile Wall(float half, float height) => Quad(
        "white",
        [
            new(-half, 0, 0), new(half, 0, 0), new(half, height, 0), new(-half, height, 0),
        ],
        -Vector3.UnitZ);

    private static DecodedImage White()
    {
        byte[] pixels = new byte[8 * 8 * 4];
        Array.Fill(pixels, (byte)255);
        return new DecodedImage(8, 8, pixels, HasAlpha: false, "test");
    }

    /// <summary>A light low and to one side, so the wall throws a long shadow.</summary>
    private static AuthoredLight SideLight() => new(
        "side",
        AuthoredLightKind.Point,
        new Vector3(0, 90, -260),
        -Vector3.UnitY,
        Vector3.One,
        HotSpot: 0,
        Falloff: 0,
        AttenuationStart: 0,
        AttenuationEnd: 3000,
        UsesAttenuation: true,
        CastsShadows: true,
        Intensity: 2f,
        Radius: 2f);

    /// <summary>Straight down at the floor, with the wall edge-on.</summary>
    private static Camera Overlooking() => new()
    {
        Position = new Vector3(0, 420, 0),
        Target = Vector3.Zero,
        Up = Vector3.UnitZ,
        Background = Vector3.Zero,
        NearPlane = 1f,
        FarPlane = 3000f,
    };

    /// <summary>Renders the floor, with or without the wall that shadows it.</summary>
    private static float Render(SceneRenderer renderer, RayTracingQuality quality, bool wall)
    {
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("white", White());
        geometry.Add(Floor(400f));

        if (wall)
        {
            geometry.Add(Wall(400f, 120f));
        }

        renderer.SetLights([SideLight()]);
        renderer.Quality = quality;

        return MeanLuminance(renderer.Render(geometry, 200, 200, Overlooking()));
    }

    [Fact]
    public void A_device_that_reports_ray_tracing_builds_an_acceleration_structure()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("white", White());
        geometry.Add(Floor(400f));
        geometry.Add(Wall(400f, 120f));
        geometry.Finish();

        Assert.True(renderer.SupportsRayTracing);
        Assert.NotNull(geometry.RayTracing);
        Assert.Equal(4, geometry.TraceableTriangleCount);
    }

    [Fact]
    public void A_wall_between_the_light_and_the_floor_casts_a_shadow()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        float open = Render(renderer, RayTracingQuality.Low, wall: false);
        float blocked = Render(renderer, RayTracingQuality.Low, wall: true);

        Assert.True(open > 20f, $"the floor was not lit to begin with: {open}");

        Assert.True(
            blocked < open * 0.8f,
            $"adding an occluder did not darken the floor: {open} to {blocked}");
    }

    [Fact]
    public void The_same_wall_casts_no_shadow_without_ray_tracing()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        float open = Render(renderer, RayTracingQuality.None, wall: false);
        float blocked = Render(renderer, RayTracingQuality.None, wall: true);

        Assert.True(
            blocked > open * 0.95f,
            $"the picture changed without any rays being traced: {open} to {blocked}");
    }

    [Fact]
    public void Occlusion_darkens_the_scene_further()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        float shadowsOnly = Render(renderer, RayTracingQuality.Low, wall: true);
        float withOcclusion = Render(renderer, RayTracingQuality.Medium, wall: true);

        Assert.True(
            withOcclusion < shadowsOnly,
            $"occlusion did not darken anything: {shadowsOnly} to {withOcclusion}");
    }

    [Fact]
    public void Every_quality_level_renders()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        foreach (RayTracingQuality quality in Enum.GetValues<RayTracingQuality>())
        {
            Assert.True(Render(renderer, quality, wall: true) > 0f, $"{quality} drew nothing");
        }
    }
}

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

    /// <summary>The floor, as the room's own geometry rather than as a model.</summary>
    /// <remarks>
    /// <para>
    /// It has to be the room. A shadow ray leaving a model traces the room and skips every
    /// other model, because GK3's people are a dozen overlapping shells and a ray leaving
    /// the shirt hits the arm inside it — so a floor built out of a <c>.MOD</c> is a floor
    /// that nothing standing on it can ever shadow, and these tests measured exactly that
    /// for as long as they were written that way.
    /// </para>
    /// <para>
    /// The case they are about is the real one: something placed in a room, laying a
    /// shadow on the room. Wound anticlockwise seen from above so the plane normal comes
    /// out along +Y, since a BSP carries no normals and each triangle is given its own
    /// plane's.
    /// </para>
    /// </remarks>
    private static BspFile FloorScene(float half) => BspFile.FromParts(
        "floor",
        ["floor"],
        [
            new BspSurface
            {
                ObjectIndex = 0,
                TextureName = "white",
                LightmapUvOffset = Vector2.Zero,
                LightmapUvScale = Vector2.One,
                Flags = 0,
            },
        ],
        [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 }],
        [
            new(-half, 0, -half), new(-half, 0, half), new(half, 0, half), new(half, 0, -half),
        ],
        [new(0, 0), new(0, 1), new(1, 1), new(1, 0)],
        [0, 1, 2, 3]);

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
    private static DecodedImage Picture(
        SceneRenderer renderer,
        RayTracingQuality quality,
        bool wall,
        RayTracingSettings? settings = null)
    {
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("white", White());
        geometry.AddScene(FloorScene(400f));

        if (wall)
        {
            geometry.Add(Wall(400f, 120f));
        }

        renderer.SetLights([SideLight()]);
        renderer.Quality = quality;
        renderer.Overriding = settings;

        return renderer.Render(geometry, 200, 200, Overlooking());
    }

    /// <summary>How bright that render came out.</summary>
    private static float Render(SceneRenderer renderer, RayTracingQuality quality, bool wall) =>
        MeanLuminance(Picture(renderer, quality, wall));

    /// <summary>
    /// A light too faint to change a pixel, but with a reach long enough that the rig
    /// sorts it ahead of the one that matters.
    /// </summary>
    /// <remarks>
    /// This is what a GK3 scene looks like from indoors: the rig is ordered by brightness
    /// times reach, so the sun and the streetlights come first and the lamp in the room
    /// comes last.
    /// </remarks>
    private static AuthoredLight DistantLight(int index) => new(
        $"distant{index}",
        AuthoredLightKind.Point,
        new Vector3(20_000 + index, 40_000, 30_000),
        -Vector3.UnitY,
        Vector3.One,
        HotSpot: 0,
        Falloff: 0,
        AttenuationStart: 0,
        AttenuationEnd: 100_000_000,
        UsesAttenuation: true,
        CastsShadows: true,
        Intensity: 0.0005f,
        Radius: 2f);

    /// <summary>Renders with the useful light buried behind a crowd of useless ones.</summary>
    private static float RenderBuried(SceneRenderer renderer, RayTracingQuality quality, bool wall)
    {
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("white", White());
        geometry.AddScene(FloorScene(400f));

        if (wall)
        {
            geometry.Add(Wall(400f, 120f));
        }

        renderer.SetLights(
            [.. Enumerable.Range(0, 40).Select(DistantLight), SideLight()]);

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
        geometry.AddScene(FloorScene(400f));
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
    public void A_shadow_survives_a_rig_whose_first_lights_are_the_ones_that_do_not_matter()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        float open = RenderBuried(renderer, RayTracingQuality.Low, wall: false);
        float blocked = RenderBuried(renderer, RayTracingQuality.Low, wall: true);

        Assert.True(open > 20f, $"the floor was not lit to begin with: {open}");

        // Low traces eight lights. Spending them on the first eight of the array spends
        // them all on lights contributing nothing, and nothing in the picture is shadowed
        // at all — which is how characters ended up casting no shadow in a hotel room.
        Assert.True(
            blocked < open * 0.8f,
            $"the occluder cast no shadow once the rig was crowded: {open} to {blocked}");
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

    /// <summary>
    /// The floor is darker where it meets the wall once occlusion is traced, and not
    /// before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Medium against Medium with its occlusion rays given no reach, so the two renders
    /// differ in that and nothing else. The reach rather than the ray count, because it is
    /// the reach the occlusion pass is handed — <c>AmbientOcclusionRays</c> only tells the
    /// mesh shader that occlusion is being traced at all. Comparing two tiers, which is what this used to do, stopped
    /// meaning anything once Medium gave up the baked lightmaps: the tiers now differ in
    /// what lights the room as well as in how many rays they spend, and Medium came out the
    /// brighter of the two for reasons that had nothing to do with occlusion.
    /// </para>
    /// <para>
    /// Measured where the floor meets the wall on the lit side. The shadow the wall throws
    /// lands on the other side and is not in the band at all, so what is left for occlusion
    /// to explain is the near contact — the line under the wall that a shadow ray toward a
    /// single lamp cannot produce.
    /// </para>
    /// </remarks>
    [Fact]
    public void Occlusion_darkens_the_floor_where_it_meets_the_wall()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        RayTracingSettings medium = RayTracingSettings.For(RayTracingQuality.Medium);

        float without = Contact(Picture(
            renderer, RayTracingQuality.Medium, wall: true,
            settings: medium with { AmbientOcclusionRadius = 0f }));

        float with = Contact(Picture(renderer, RayTracingQuality.Medium, wall: true));

        Assert.True(
            without > 0.5f,
            $"the floor was already dark at the wall with no occlusion traced: {without}");

        // Two per cent, and that is the honest size of it on a floor a lamp is shining
        // straight at. Occlusion attenuates the ambient term and nothing else — which is
        // correct, and means its effect is bounded by how much of a surface's light is
        // ambient. On a lit floor that share is small on purpose; where it earns its keep is
        // the corner the lamp does not reach, which this fixture has none of.
        Assert.True(
            with < without * 0.98f,
            $"occlusion did not darken the floor at the wall: {without} to {with}");
    }

    /// <summary>
    /// How bright the floor is where it meets the wall, against the floor in the open.
    /// </summary>
    /// <param name="picture">A render of the floor with the wall standing on it.</param>
    /// <returns>The ratio. Below one because the far band is nearer the lamp.</returns>
    /// <remarks>
    /// The camera looks straight down with its up along positive z and the wall runs along
    /// x through the origin, so the wall is a horizontal line across the middle of the
    /// picture. The light is on the negative z side, which is the bottom half — so that half
    /// is the lit one, and the shadow the wall throws lands in the other.
    /// </remarks>
    private static float Contact(DecodedImage picture)
    {
        float near = MeanRows(picture, picture.Height / 2, (picture.Height / 2) + 12);
        float far = MeanRows(picture, picture.Height - 30, picture.Height);

        return far > 0.01f ? near / far : 0f;
    }

    /// <summary>The mean luminance of a band of rows.</summary>
    private static float MeanRows(DecodedImage picture, int first, int last)
    {
        double total = 0;
        int counted = 0;

        for (int y = Math.Max(first, 0); y < Math.Min(last, picture.Height); y++)
        {
            for (int x = 0; x < picture.Width; x++)
            {
                int at = ((y * picture.Width) + x) * 4;

                total += (0.2126 * picture.Pixels[at]) +
                         (0.7152 * picture.Pixels[at + 1]) +
                         (0.0722 * picture.Pixels[at + 2]);

                counted++;
            }
        }

        return counted > 0 ? (float)(total / counted) : 0f;
    }

    [Fact]
    public void The_same_scene_renders_to_the_same_pixels_twice()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        // Every filtered stage of a ray-traced frame remembers the frame before it, and
        // through the host that is the point: the shadow settles over however many frames
        // the wall clock allowed, so two runs of the same build differ across a few per
        // cent of the picture and a screenshot diff below that floor means nothing.
        //
        // Headless, nothing is carried over — the denoiser and the reflection pass are
        // built for one render and thrown away with it — so the difference is exactly
        // nought, which is what lets a render be compared against a reference at all.
        byte[] first = Picture(renderer, RayTracingQuality.High, wall: true).Pixels;
        byte[] second = Picture(renderer, RayTracingQuality.High, wall: true).Pixels;

        Assert.Equal(first, second);
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

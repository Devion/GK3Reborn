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

    /// <summary>A flat panel, face up, standing a little above the floor.</summary>
    /// <remarks>
    /// A <em>model</em>, which is the point of it: a pixel on a model is the one the trace
    /// stage treats differently, and a floor built out of a <c>.MOD</c> is what these tests
    /// were originally and wrongly written with. Here it is the thing being shadowed rather
    /// than the room, so a model is exactly right.
    /// </remarks>
    private static ModFile Panel(float half, float height) => Quad(
        "white",
        [
            new(-half, height, -half), new(-half, height, half),
            new(half, height, half), new(half, height, -half),
        ],
        Vector3.UnitY);

    /// <summary>The wall, wound so that the ray meets one side of it or the other.</summary>
    /// <param name="half">Half its width.</param>
    /// <param name="height">How tall it stands.</param>
    /// <param name="shell">
    /// True to turn its front face toward the light, which is what a shirt around a torso
    /// looks like to a ray leaving that torso: met from within, and it must go through.
    /// False for the same wall the other way round — something standing between the panel
    /// and the light, which must shadow it.
    /// </param>
    /// <remarks>
    /// The two differ in winding and in nothing else: the same four corners, the same
    /// shading normal, and the mesh pipeline culls no faces, so both are drawn identically —
    /// and the camera looks straight down, so both are the same edge-on line either way.
    /// Any difference between the two renders is the traced ray and can be nothing else.
    /// </remarks>
    private static ModFile Cover(float half, float height, bool shell)
    {
        Vector3[] corners =
        [
            new(-half, 0, 0), new(half, 0, 0), new(half, height, 0), new(-half, height, 0),
        ];

        return Quad("white", shell ? [.. corners.Reverse()] : corners, -Vector3.UnitZ);
    }

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
    /// <param name="renderer">The renderer.</param>
    /// <param name="quality">Which ray budget to render at.</param>
    /// <param name="wall">Whether to stand the occluder in the room.</param>
    /// <param name="settings">A ray budget of one's own, or null for the level's.</param>
    /// <param name="placement">
    /// Where to stand the wall, or null for the model's own origin. Only the transform
    /// changes: the model is the same either way, which is what makes the difference
    /// between two renders evidence about the placement and nothing else.
    /// </param>
    private static DecodedImage Picture(
        SceneRenderer renderer,
        RayTracingQuality quality,
        bool wall,
        RayTracingSettings? settings = null,
        Matrix4x4? placement = null)
    {
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("white", White());
        geometry.AddScene(FloorScene(400f));

        if (wall)
        {
            geometry.Add(Wall(400f, 120f), placement);
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

    /// <summary>
    /// A model shadows the room from where it was placed, not from where it was modelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A model's triangles go into the acceleration structure in the model's own space and
    /// are placed by an instance transform. Every other test here stands its wall at the
    /// model's own origin, so all of them passed while that transform was never applied at
    /// all: the structure was built with every instance at identity and only
    /// <c>MoveModel</c> ever put one right. Nothing moves a prop after a room has loaded,
    /// so every van, bench and signpost in the game traced from (0, 0, 0) — and an actor
    /// did too, until the story first walked them somewhere.
    /// </para>
    /// <para>
    /// The wall is placed five thousand units away, well outside both the floor and the
    /// far plane. It should shadow nothing and be drawn nowhere, so the picture should be
    /// the empty room's. Placed at identity it shadows half the floor, which is what this
    /// used to render.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_model_shadows_the_room_from_where_it_is_placed()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        float open = Render(renderer, RayTracingQuality.Low, wall: false);
        float blocked = Render(renderer, RayTracingQuality.Low, wall: true);

        float away = MeanLuminance(Picture(
            renderer,
            RayTracingQuality.Low,
            wall: true,
            placement: Matrix4x4.CreateTranslation(0, 0, 5000)));

        Assert.True(open > 20f, $"the floor was not lit to begin with: {open}");
        Assert.True(
            blocked < open * 0.8f,
            $"the occluder cast no shadow at its own origin: {open} to {blocked}");

        Assert.True(
            away > blocked * 1.2f,
            $"the occluder shadowed the floor from a place it does not stand: " +
            $"{blocked} at the origin, {away} five thousand units away");

        Assert.True(
            away > open * 0.95f,
            $"a model outside the room darkened it: {open} empty, {away} with it away");
    }

    /// <summary>Renders a model panel, with whatever else is standing over it.</summary>
    /// <param name="renderer">The renderer.</param>
    /// <param name="quality">Which ray budget to render at.</param>
    /// <param name="build">What to place besides the panel, if anything.</param>
    /// <returns>How bright the panel came out.</returns>
    /// <remarks>
    /// <b>No room, and that is deliberate.</b> These measure what a model does to a model,
    /// and a floor would fill most of the frame with a surface that is shadowed by the same
    /// occluder through the other half of the structure — where no face is culled, because
    /// a ray leaving the room may hit either side of anything. Its brightness would swamp
    /// the panel's and move with the occluder in every case, including the ones that are
    /// supposed to be identical.
    /// </remarks>
    private static float Panelled(
        SceneRenderer renderer, RayTracingQuality quality, Action<SceneGeometry>? build = null)
    {
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("white", White());
        geometry.Add(Panel(160f, 10f));

        build?.Invoke(geometry);

        renderer.SetLights([SideLight()]);
        renderer.Quality = quality;
        renderer.Overriding = null;

        return MeanLuminance(renderer.Render(geometry, 200, 200, Overlooking()));
    }

    /// <summary>
    /// One model shadows another, and a model shadows itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both used to be refused outright — a shadow ray leaving a model traced the room and
    /// skipped every model, its own included — because GK3's people are a stack of
    /// overlapping shells and a surface inside one hits it before the ray has gone
    /// anywhere. Which side of a triangle the ray arrives at is what tells the two apart,
    /// so the wall's front face still shadows and a shell's back face no longer does.
    /// </para>
    /// <para>
    /// The wall is placed as its own model in one case and built into the same model as
    /// the panel in the other, which is the whole difference between shadowing a
    /// neighbour and shadowing oneself.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_model_is_shadowed_by_another_model_and_by_itself()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        float open = Panelled(renderer, RayTracingQuality.Low);

        float neighbour = Panelled(
            renderer, RayTracingQuality.Low, g => g.Add(Wall(400f, 120f)));

        // The same wall, in the panel's own model rather than beside it.
        float itself = Panelled(
            renderer,
            RayTracingQuality.Low,
            g => g.Add(ModFile.FromMeshes(
                "panel and wall",
                [.. Panel(160f, 10f).Meshes, .. Wall(400f, 120f).Meshes])));

        Assert.True(open > 20f, $"the panel was not lit to begin with: {open}");

        Assert.True(
            neighbour < open * 0.9f,
            $"one model did not shadow another: {open} to {neighbour}");

        Assert.True(
            itself < open * 0.9f,
            $"a model did not shadow itself: {open} to {itself}");
    }

    /// <summary>
    /// A shell around a surface is not a shadow on it.
    /// </summary>
    /// <remarks>
    /// The other half of the rule, and the one that stops a character being covered in hard
    /// dark patches: a shirt around a torso, a sleeve around an arm, a collar around a
    /// neck. The ray meets those from within, and what it meets is the reason the whole
    /// signal used to be thrown away.
    /// </remarks>
    [Fact]
    public void A_shell_around_a_model_does_not_shadow_it()
    {
        Assert.SkipUnless(HasRayTracing(), "no ray tracing device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);

        float open = Panelled(renderer, RayTracingQuality.Low);

        float shell = Panelled(
            renderer, RayTracingQuality.Low, g => g.Add(Cover(400f, 120f, shell: true)));

        float between = Panelled(
            renderer, RayTracingQuality.Low, g => g.Add(Cover(400f, 120f, shell: false)));

        // The same four corners, the same shading normal, and nothing is back-face culled
        // when it is drawn, so the two pictures differ only in what the rays found.
        Assert.True(
            between < open * 0.9f,
            $"the wall the right way round did not shadow the panel: {open} to {between}");

        Assert.True(
            shell > open * 0.98f,
            $"a shell around the panel shadowed it: {open} without one, {shell} with");
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

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for drawing the room's own surfaces only on the side they face.
/// </summary>
/// <remarks>
/// <para>
/// Reported as a texture blocking the open space inside R25's dumbwaiter. The shaft is a
/// closed box of lath and its room-side face has no hole cut for the door, so opening the
/// door showed the back of that face rather than the shaft: one polygon,
/// <c>r25_duwalls</c> surface 625, a unit behind the doorway with its front pointing into
/// the shaft. The original never showed it, because <c>Renderer::Render</c> culls back
/// faces for all opaque world geometry.
/// </para>
/// <para>
/// The shape of the fault is what these tests build: a wall with a hole in it and a second,
/// unbroken surface behind it whose front points away. Drawing both sides paints the hole
/// shut; drawing one leaves it open.
/// </para>
/// </remarks>
public sealed class BackFaceCullingTests
{
    private static bool HasDevice()
    {
        try
        {
            DeviceReport report = VulkanDeviceSelector.Survey();
            return report.Available && report.Adapters.Count > 0;
        }
        catch (VulkanException)
        {
            return false;
        }
    }

    private static DecodedImage Solid(byte r, byte g, byte b, byte a = 255)
    {
        const int Size = 4;
        byte[] pixels = new byte[Size * Size * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return new DecodedImage(Size, Size, pixels, a != 255, "test");
    }

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    /// <summary>A camera in front of the origin, looking at it along +Z.</summary>
    private static Camera Facing() => new()
    {
        Position = new Vector3(0, 0, -6),
        Target = Vector3.Zero,
        Up = Vector3.UnitY,
        Background = Vector3.Zero,
        LightDirection = new Vector3(0, 0, 1),
    };

    /// <summary>
    /// A quad in the z = <paramref name="depth"/> plane, wound so its front faces the
    /// camera or away from it.
    /// </summary>
    /// <remarks>
    /// Half a unit is the smaller quad's reach and two units the larger one's, which is what
    /// lets the far surface be seen only through the hole in the near one.
    /// </remarks>
    private static Vector3[] Quad(float reach, float depth, bool towards) =>
        towards
            ? [
                new(-reach, -reach, depth), new(-reach, reach, depth),
                new(reach, reach, depth), new(reach, -reach, depth),
            ]
            : [
                new(-reach, -reach, depth), new(reach, -reach, depth),
                new(reach, reach, depth), new(-reach, reach, depth),
            ];

    /// <summary>A room of quads, one surface and one object each.</summary>
    private static BspFile Room(params (Vector3[] Points, string Texture)[] quads)
    {
        List<Vector3> vertices = [];
        List<ushort> indices = [];
        List<BspSurface> surfaces = [];
        List<BspPolygon> polygons = [];
        List<string> names = [];

        foreach ((Vector3[] points, string texture) in quads)
        {
            int at = vertices.Count;
            int offset = indices.Count;

            vertices.AddRange(points);
            indices.AddRange(Enumerable.Range(at, points.Length).Select(i => (ushort)i));
            names.Add($"object{names.Count}");

            surfaces.Add(new BspSurface
            {
                ObjectIndex = names.Count - 1,
                TextureName = texture,
                LightmapUvOffset = Vector2.Zero,
                LightmapUvScale = Vector2.One,
                Flags = 0,
            });

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = offset,
                VertexIndexCount = points.Length,
                SurfaceIndex = surfaces.Count - 1,
            });
        }

        return BspFile.FromParts(
            "test",
            names,
            surfaces,
            polygons,
            [.. vertices],
            [.. vertices.Select(_ => Vector2.Zero)],
            [.. indices]);
    }

    /// <summary>A single quad model, painted with one texture.</summary>
    private static ModFile Model(string texture, bool towards)
    {
        Vector3[] positions = Quad(1f, 0f, towards);

        var submesh = new ModSubmesh
        {
            TextureName = texture,
            Color = (255, 255, 255),
            Positions = positions,
            Normals = [.. positions.Select(_ => -Vector3.UnitZ)],
            TexCoords = [.. positions.Select(_ => Vector2.Zero)],
            Indices = [0, 1, 2, 0, 2, 3],
        };

        return ModFile.FromMeshes(
            "quad",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = new Vector3(-1, -1, 0),
                    BoundsMax = new Vector3(1, 1, 0),
                    Submeshes = [submesh],
                },
            ]);
    }

    [Fact]
    public void The_room_s_own_surfaces_are_culled_and_placed_models_are_not()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 40, 40));
        geometry.AddScene(Room((Quad(2f, 4f, towards: true), "wall")));
        geometry.Add(Model("wall", towards: true));
        geometry.Finish();

        SceneDraw[] draws = [.. geometry.Draws()];

        Assert.Equal(2, draws.Length);
        Assert.False(draws[0].DoubleSided, "the room's own surface should be culled");
        Assert.True(draws[1].DoubleSided, "a placed model should keep both faces");
    }

    [Fact]
    public void A_keyed_surface_of_the_room_keeps_both_faces()
    {
        // A 1999 tree is a crown painted on crossed quads, and the two faces of one of
        // those quads do not carry the same texture coordinates: 434 of the 436 `mapletop1`
        // polygons in CEM's maple differ from their own opposite-wound twin. Drawing both
        // gives the union of two silhouettes, so culling them thins the canopy — 3,251
        // pixels of that one tree turn to sky in a single frame.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("solid", Solid(200, 40, 40));
        geometry.AddTexture("card", Solid(40, 200, 40, a: 0));
        geometry.AddScene(Room(
            (Quad(2f, 4f, towards: true), "solid"),
            (Quad(2f, 3f, towards: true), "card")));

        geometry.Finish();

        SceneDraw[] draws = [.. geometry.Draws()];

        Assert.Equal(2, draws.Length);
        Assert.False(draws[0].DoubleSided, "an opaque surface should be culled");
        Assert.True(draws[1].DoubleSided, "a keyed card should keep both faces");
    }

    [Fact]
    public void Switching_the_culling_off_draws_both_sides_of_everything()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 40, 40));
        geometry.AddScene(Room((Quad(2f, 4f, towards: true), "wall")));
        geometry.Finish();

        Assert.False(geometry.Draws().Single().DoubleSided);

        geometry.CullBackFaces = false;

        Assert.True(
            geometry.Draws().Single().DoubleSided,
            "the switch is read per draw, so it may be changed after the room is built");
    }

    [Fact]
    public void A_sealed_surface_behind_a_doorway_does_not_paint_the_doorway_shut()
    {
        // R25's dumbwaiter in miniature: a red wall with a hole in the middle of it, and a
        // green sheet a unit behind, unbroken and facing away. The hole should show what is
        // beyond the green sheet and not the sheet's own back.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 40, 40));
        geometry.AddTexture("lath", Solid(40, 200, 40));

        // Four quads around a gap rather than one with a hole in it, because a BSP polygon
        // is convex and this is what a doorway in one actually looks like.
        geometry.AddScene(Room(
            (Frame(-3f, -1f, -3f, 3f, 4f), "wall"),
            (Frame(1f, 3f, -3f, 3f, 4f), "wall"),
            (Frame(-1f, 1f, 1f, 3f, 4f), "wall"),
            (Frame(-1f, 1f, -3f, -1f, 4f), "wall"),
            (Quad(3f, 5f, towards: false), "lath")));

        geometry.Finish();

        DecodedImage image = renderer.Render(geometry, 128, 128, Facing());
        (byte r, byte g, byte b) = Pixel(image, 64, 64);

        Assert.True(
            g < 60,
            $"the sealed sheet was drawn through the doorway: centre was {r},{g},{b}");

        // And the wall around the hole is still there, so this is not "drew nothing".
        int red = 0;

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                (byte pr, byte pg, _) = Pixel(image, x, y);

                if (pr > 100 && pg < 100)
                {
                    red++;
                }
            }
        }

        Assert.True(red > 200, $"the wall around the doorway was not drawn: {red} pixels of it");
    }

    [Fact]
    public void Drawing_both_sides_is_what_paints_the_doorway_shut()
    {
        // The other half of the same picture, and the reason this is a defect rather than a
        // missed optimisation: with the culling off, the same room shows the sheet's back
        // where the doorway is.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 40, 40));
        geometry.AddTexture("lath", Solid(40, 200, 40));
        geometry.AddScene(Room(
            (Frame(-3f, -1f, -3f, 3f, 4f), "wall"),
            (Frame(1f, 3f, -3f, 3f, 4f), "wall"),
            (Frame(-1f, 1f, 1f, 3f, 4f), "wall"),
            (Frame(-1f, 1f, -3f, -1f, 4f), "wall"),
            (Quad(3f, 5f, towards: false), "lath")));

        geometry.Finish();
        geometry.CullBackFaces = false;

        DecodedImage image = renderer.Render(geometry, 128, 128, Facing());
        (_, byte g, _) = Pixel(image, 64, 64);

        Assert.True(g > 100, $"the doorway should have been painted shut, and was {g}");
    }

    [Fact]
    public void A_packed_card_is_known_to_be_keyed_and_so_keeps_both_faces()
    {
        // Because that is the configuration players run in. A keyed texture reaches the
        // device as blocks whenever the pack holds it, and TextureCache only ever recorded
        // keying where texels arrived as texels — so `Keyed` was empty in a shipped build
        // and every railing, fence and crown in the game would have been culled.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("solid", PackedBlock(transparent: false));
        geometry.AddTexture("card", PackedBlock(transparent: true));
        geometry.AddScene(Room(
            (Quad(2f, 4f, towards: true), "solid"),
            (Quad(2f, 3f, towards: true), "card")));

        geometry.Finish();

        SceneDraw[] draws = [.. geometry.Draws()];

        Assert.Equal(2, draws.Length);
        Assert.False(draws[0].DoubleSided, "an opaque packed texture should be culled");
        Assert.True(draws[1].DoubleSided, "a keyed packed texture should keep both faces");
    }

    /// <summary>
    /// One 4x4 BC7 block, opaque or with a hole in it.
    /// </summary>
    /// <remarks>
    /// Mode 5, whose alpha has an index set of its own, so a block can be written that is
    /// opaque at one texel and see-through at another. See <c>BlockDecoderTests</c>.
    /// </remarks>
    private static CompressedImage PackedBlock(bool transparent)
    {
        byte[] block = new byte[16];
        int at = 0;

        void Write(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (((value >> i) & 1) != 0)
                {
                    block[(at + i) / 8] |= (byte)(1 << ((at + i) % 8));
                }
            }

            at += count;
        }

        Write(32, 6);                                 // mode 5
        Write(0, 2);                                  // no rotation
        Write(0, 7);
        Write(127, 7);                                // red
        Write(0, 7);
        Write(0, 7);                                  // green
        Write(0, 7);
        Write(0, 7);                                  // blue
        Write(255, 8);
        Write(transparent ? 0u : 255u, 8);            // alpha endpoints

        Write(0, 1);                                  // the colour anchor is a bit short
        for (int i = 1; i < 16; i++)
        {
            Write(3, 2);
        }

        Write(0, 1);                                  // and so is the alpha anchor
        for (int i = 1; i < 16; i++)
        {
            Write(3, 2);
        }

        return new CompressedImage(4, 4, 1, BlockFormat.Bc7Srgb, block, "packed");
    }

    /// <summary>One side of the frame around a doorway, facing the camera.</summary>
    private static Vector3[] Frame(float left, float right, float bottom, float top, float depth) =>
        [
            new(left, bottom, depth), new(left, top, depth),
            new(right, top, depth), new(right, bottom, depth),
        ];
}

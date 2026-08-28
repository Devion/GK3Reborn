using System.Numerics;
using System.Text.Json;
using GK3Reborn.Content;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for growing modelled trees over GK3's flat foliage cards.
/// </summary>
/// <remarks>
/// Two properties carry the whole feature. A card has to be <em>measured</em> rather than
/// guessed at, because the picture on it is the artist's whole description of the tree; and
/// the same card has to produce the same tree on every load, because a wood that reshuffles
/// itself when the player walks out of a room and back in is worse than a wood of identical
/// trees, and it makes two renders of one scene impossible to compare.
/// </remarks>
public sealed class FoliageTests : IDisposable
{
    private static readonly string[] PineSprites = ["PINE2", "PINE2FLAT"];

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "gk3reborn-trees-" + Guid.NewGuid().ToString("N"));

    public FoliageTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A card: one quad, upright, painted with a foliage sprite.</summary>
    private static ModFile Card(
        string texture, Vector3 least, Vector3 most, Matrix4x4? at = null) =>
        ModFile.FromMeshes("CARD",
        [
            new ModMesh
            {
                MeshToLocal = at ?? Matrix4x4.Identity,
                BoundsMin = least,
                BoundsMax = most,
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = texture,
                        Color = (255, 255, 255),
                        Positions =
                        [
                            least,
                            new Vector3(most.X, least.Y, most.Z),
                            most,
                            new Vector3(least.X, most.Y, least.Z),
                        ],
                        Normals = [.. Enumerable.Repeat(Vector3.UnitZ, 4)],
                        TexCoords = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
                        Indices = [0, 1, 2, 0, 2, 3],
                    },
                ],
            },
        ]);

    /// <summary>Writes a library holding one species and however many variants.</summary>
    private TreeLibrary Library(int variants = 2, bool onDisk = true)
    {
        var trees = new List<object>();

        for (int variant = 0; variant < variants; variant++)
        {
            string name = "spruce_" + variant.ToString("00", null);
            trees.Add(new
            {
                name,
                species = "spruce",
                radius = 0.35f,
                triangles = 3000,
            });

            if (onDisk)
            {
                File.WriteAllBytes(
                    Path.Combine(_directory, name + ".glb"),
                    GlbWriter.Encode(Card("RBN_SPRUCE_SPRAY",
                        new Vector3(-0.35f, 0, -0.35f), new Vector3(0.35f, 1, 0.35f))));
            }
        }

        File.WriteAllText(Path.Combine(_directory, "trees.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            species = new
            {
                spruce = new
                {
                    kind = "conifer",
                    canopy = true,
                    sprites = PineSprites,
                },
            },
            trees,
        }));

        return TreeLibrary.Open(_directory);
    }

    [Fact]
    public void A_directory_that_is_not_there_is_simply_an_empty_library()
    {
        // Enhanced content is optional by design, and an empty library has to leave every
        // card exactly where it was rather than take one away.
        TreeLibrary none = TreeLibrary.Open(Path.Combine(_directory, "nothing"));

        Assert.True(none.IsEmpty);
        Assert.Null(none.For("PINE2"));
    }

    [Fact]
    public void A_species_with_no_variant_on_disk_is_not_offered()
    {
        // Otherwise the card is taken away and nothing is put in its place, which is a
        // hole in a hillside rather than a missing tree.
        TreeLibrary described = Library(variants: 2, onDisk: false);

        Assert.True(described.IsEmpty);
        Assert.Null(described.For("PINE2"));
    }

    [Fact]
    public void A_sprite_is_matched_to_the_species_it_draws()
    {
        TreeLibrary library = Library();

        Assert.Equal("spruce", library.For("PINE2")!.Name);
        Assert.Equal("spruce", library.For("pine2flat.BMP")!.Name);

        // A sprite nobody has grown a tree for keeps its card, which is the right default:
        // TREEGROUP01 is a whole hillside of distant trees on one quad and there is no
        // single tree in it to model.
        Assert.Null(library.For("TREEGROUP01"));
    }

    [Fact]
    public void A_card_is_measured_rather_than_guessed_at()
    {
        TreeLibrary library = Library();
        ModFile card = Card("PINE2", new Vector3(100, 80, -50), new Vector3(320, 400, 50));

        TreeSite site = Assert.NotNull(Foliage.SiteFor(card, library));

        Assert.Equal("spruce", site.Species.Name);
        Assert.Equal(320f, site.Height);                        // 400 - 80
        Assert.Equal(new Vector3(210, 80, 0), site.Foot);        // centred, standing on 80
        Assert.Equal(110f, site.Radius);                         // half the wider side
    }

    [Fact]
    public void A_card_is_measured_where_its_mesh_puts_it()
    {
        // GK3's props carry their scene position in their vertices rather than in a
        // transform, but a mesh inside one still has its own, and a tree measured in the
        // wrong space stands somewhere the card never was.
        TreeLibrary library = Library();
        ModFile card = Card(
            "PINE2",
            new Vector3(0, 0, 0),
            new Vector3(200, 300, 0),
            Matrix4x4.CreateTranslation(1000, 50, -200));

        TreeSite site = Assert.NotNull(Foliage.SiteFor(card, library));

        Assert.Equal(new Vector3(1100, 50, -200), site.Foot);
        Assert.Equal(300f, site.Height);
    }

    [Fact]
    public void A_prop_that_is_a_tree_and_something_else_keeps_its_card()
    {
        // The something else would be lost, and there is no way to put it back.
        TreeLibrary library = Library();
        ModFile mixed = ModFile.FromMeshes("MIXED",
        [
            .. Card("PINE2", new Vector3(0, 0, 0), new Vector3(200, 300, 0)).Meshes,
            .. Card("TRUNK01", new Vector3(0, 0, 0), new Vector3(20, 300, 20)).Meshes,
        ]);

        Assert.Null(Foliage.SiteFor(mixed, library));
    }

    [Fact]
    public void Something_too_small_to_be_a_tree_keeps_its_card()
    {
        // Gabriel is 76 units tall. A card half his height is undergrowth, and a grown
        // trunk with a crown on it is the wrong shape for it.
        TreeLibrary library = Library();

        Assert.Null(Foliage.SiteFor(
            Card("PINE2", new Vector3(0, 0, 0), new Vector3(30, 30, 0)), library));
    }

    [Fact]
    public void The_same_place_grows_the_same_tree_every_time()
    {
        TreeLibrary library = Library(variants: 4);
        ModFile card = Card("PINE2", new Vector3(100, 80, -50), new Vector3(320, 400, 50));

        TreeSite first = Assert.NotNull(Foliage.SiteFor(card, library));
        TreeSite again = Assert.NotNull(Foliage.SiteFor(card, library));

        Assert.Equal(first.Seed, again.Seed);
        Assert.Equal(
            TreeLibrary.Variant(first.Species, first.Seed).Name,
            TreeLibrary.Variant(again.Species, again.Seed).Name);
    }

    [Fact]
    public void A_place_hashes_to_the_same_seed_in_every_process()
    {
        // The one property a same-process test cannot check. `HashCode.Combine` is seeded
        // randomly once per process, so a wood hashed with it comes out differently every
        // time the game is started while every test still passes. The seed is therefore
        // pinned to a literal: if the hash changes, every tree in the game changes with it,
        // and that should be a decision rather than a surprise.
        TreeLibrary library = Library();
        ModFile card = Card("PINE2", new Vector3(100, 80, -50), new Vector3(320, 400, 50));

        Assert.Equal(505292005, Assert.NotNull(Foliage.SiteFor(card, library)).Seed);
    }

    [Fact]
    public void Two_places_do_not_have_to_grow_the_same_tree()
    {
        // Not a guarantee — four variants and a hash will collide — but a stand of
        // identical trees is the failure this is meant to catch, and a seed that ignored
        // position would produce exactly that.
        TreeLibrary library = Library(variants: 4);

        var seeds = new HashSet<int>();
        for (int step = 0; step < 8; step++)
        {
            ModFile card = Card(
                "PINE2",
                new Vector3(step * 400, 80, 0),
                new Vector3((step * 400) + 220, 400, 60));

            seeds.Add(Assert.NotNull(Foliage.SiteFor(card, library)).Seed);
        }

        Assert.True(seeds.Count > 1);
    }

    [Fact]
    public void A_grown_tree_is_scaled_to_the_card_it_replaces()
    {
        TreeLibrary library = Library();
        ModFile card = Card("PINE2", new Vector3(100, 80, -50), new Vector3(320, 400, 50));
        TreeSite site = Assert.NotNull(Foliage.SiteFor(card, library));

        Matrix4x4 standing = Foliage.Standing(site, TreeLibrary.Variant(site.Species, 0));

        // A grown tree stands on the origin and is one unit tall, so the height is the
        // whole of the vertical scale and the foot is the whole of the translation.
        Assert.Equal(320f, Vector3.Transform(Vector3.UnitY, standing).Y - site.Foot.Y, 2);
        Assert.Equal(site.Foot, Vector3.Transform(Vector3.Zero, standing));
    }

    [Fact]
    public void A_tree_is_never_squeezed_past_recognition()
    {
        // A crown stretched to a square card's width overhangs walls the card never
        // touched; one squashed to half its width stops reading as that species at all.
        TreeLibrary library = Library();
        ModFile narrow = Card("PINE2", new Vector3(0, 0, 0), new Vector3(20, 400, 20));
        TreeSite site = Assert.NotNull(Foliage.SiteFor(narrow, library));

        Matrix4x4 standing = Foliage.Standing(site, TreeLibrary.Variant(site.Species, 0));

        // The length of the scaled, turned axis rather than its X: a tree is turned about
        // the vertical so that four variants do not read as four copies, and measuring one
        // component of a turned axis measures the turn.
        float across = (Vector3.Transform(Vector3.UnitX, standing)
                        - Vector3.Transform(Vector3.Zero, standing)).Length();

        Assert.InRange(across, 400f * 0.74f, 400f * 1.36f);
    }

    [Fact]
    public void A_tree_that_will_not_read_costs_that_tree_and_nothing_else()
    {
        TreeLibrary library = Library();
        File.WriteAllText(Path.Combine(_directory, "spruce_00.glb"), "not a model");

        TreeLibrary reopened = TreeLibrary.Open(_directory);
        var diagnostics = new DiagnosticBag();
        GrownTree broken = reopened.For("PINE2")!.Variants[0];

        Assert.Null(reopened.Read(broken, diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1120");

        // And the other variant is still perfectly good.
        Assert.NotNull(reopened.Read(reopened.For("PINE2")!.Variants[1], diagnostics));
    }

    [Fact]
    public void A_manifest_that_will_not_read_leaves_every_card_flat()
    {
        Library();
        File.WriteAllText(Path.Combine(_directory, "trees.json"), "{ not json");

        var diagnostics = new DiagnosticBag();
        TreeLibrary library = TreeLibrary.Open(_directory, diagnostics);

        Assert.True(library.IsEmpty);
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1122");
    }

    /// <summary>Writes the loose set into a pack and takes the loose set away.</summary>
    /// <param name="geometry">Whether the pack carries the trees themselves.</param>
    private RebarnContent Pack(bool geometry = true)
    {
        Library();

        var builder = new RebarnBuilder();
        builder.AddBytes(
            RebarnKind.Manifest,
            "trees.json",
            File.ReadAllBytes(Path.Combine(_directory, "trees.json")),
            RebarnPayload.Json);

        if (geometry)
        {
            foreach (string glb in Directory.EnumerateFiles(_directory, "*.glb"))
            {
                builder.AddBytes(
                    RebarnKind.Model,
                    Path.GetFileName(glb),
                    File.ReadAllBytes(glb),
                    RebarnPayload.Glb);
            }
        }

        string packs = Path.Combine(_directory, "packs");
        Directory.CreateDirectory(packs);
        builder.Write(Path.Combine(packs, "Reborn.rebarn"));

        // The loose set goes, so that anything the pack cannot answer answers nothing.
        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            File.Delete(file);
        }

        return RebarnContent.Open(packs);
    }

    [Fact]
    public void Trees_are_read_from_a_pack_when_there_is_no_directory()
    {
        // What a shipped game has: volumes beside the executable and no content workspace
        // anywhere. Gating this on a loose directory would mean nobody who installed the
        // game ever saw a modelled tree.
        using RebarnContent packs = Pack();
        TreeLibrary library = TreeLibrary.Open(string.Empty, packs);

        Assert.False(library.IsEmpty);
        Assert.True(library.Packed);
        Assert.Equal("spruce", library.For("PINE2")!.Name);
        Assert.NotNull(library.Read(library.For("PINE2")!.Variants[0]));
    }

    [Fact]
    public void A_pack_with_a_manifest_and_no_trees_in_it_leaves_every_card_flat()
    {
        // The half-shipped case, and the one that must not leave a hole: a manifest naming
        // trees the pack does not carry has to read as "no trees" rather than as "take the
        // cards away".
        using RebarnContent packs = Pack(geometry: false);
        TreeLibrary library = TreeLibrary.Open(string.Empty, packs);

        Assert.True(library.IsEmpty);
        Assert.Null(library.For("PINE2"));
    }

    [Fact]
    public void No_packs_and_no_directory_is_simply_no_trees()
    {
        // Somebody running the engine against a plain installation. Not an error, and not
        // a reason to draw nothing where the flat trees were.
        TreeLibrary library = TreeLibrary.Open(string.Empty, null);

        Assert.True(library.IsEmpty);
        Assert.False(library.Packed);
        Assert.Null(library.For("PINE2"));
    }

    [Fact]
    public void A_loose_tree_beats_the_packed_one()
    {
        // The same way round as everywhere else here: a tree regrown during a session is
        // what should be drawn, without the pack having to be rebuilt to see it.
        using RebarnContent packs = Pack();
        Library();

        TreeLibrary library = TreeLibrary.Open(_directory, packs);

        Assert.False(library.IsEmpty);
        Assert.NotNull(library.Read(library.For("PINE2")!.Variants[0]));
    }

    /// <summary>
    /// A room holding one upright card, cut into <paramref name="slices"/> polygons the
    /// way a BSP splitter cuts one.
    /// </summary>
    private static BspFile Room(string texture, int slices, float low = 0f, float high = 320f)
    {
        List<Vector3> vertices = [];
        List<ushort> indices = [];
        List<BspPolygon> polygons = [];

        for (int slice = 0; slice < slices; slice++)
        {
            float bottom = low + ((high - low) * slice / slices);
            float top = low + ((high - low) * (slice + 1) / slices);
            var corner = (ushort)vertices.Count;

            vertices.Add(new Vector3(-100, bottom, 0));
            vertices.Add(new Vector3(100, bottom, 0));
            vertices.Add(new Vector3(100, top, 0));
            vertices.Add(new Vector3(-100, top, 0));

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = indices.Count,
                VertexIndexCount = 4,
                SurfaceIndex = 0,
            });

            indices.AddRange([corner, (ushort)(corner + 1), (ushort)(corner + 2), (ushort)(corner + 3)]);
        }

        return BspFile.FromParts(
            "TEST.BSP",
            ["test_treeshadowcasters"],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = texture,
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                    Flags = 0,
                },
            ],
            polygons,
            [.. vertices],
            [],
            [.. indices]);
    }

    [Fact]
    public void One_card_is_one_tree_however_the_splitter_cut_it()
    {
        // The bug this pins put trees in mid-air. A room's geometry has been through a BSP
        // splitter, so the polygons in it are not the faces an artist drew: one 320-unit
        // spruce card in LHM arrives as five, sliced across at whatever heights the tree's
        // planes happened to cut it. Clustered as if they were cards, each slice became a
        // tree — and a slice taken from 300 units up is a tree with its own trunk growing
        // out of the middle of the real one.
        TreeLibrary library = Library();

        Foliage.FoliageObject whole = Assert.Single(Foliage.InGeometry(Room("PINE2", 1), library));
        Foliage.FoliageObject cut = Assert.Single(Foliage.InGeometry(Room("PINE2", 5), library));

        Assert.Single(whole.Sites);
        Assert.Single(cut.Sites);
        Assert.Equal(whole.Sites[0].Foot, cut.Sites[0].Foot);
        Assert.Equal(whole.Sites[0].Height, cut.Sites[0].Height);
    }

    [Fact]
    public void A_slice_of_a_card_never_becomes_a_tree_of_its_own()
    {
        // The same thing said as the symptom rather than as the cause: every tree a room
        // grows has to stand on the ground the card stood on. A five-way cut of one card
        // that produced five trees would put four of them at 64, 128, 192 and 256 units up.
        TreeLibrary library = Library();

        foreach (TreeSite site in Foliage.InGeometry(Room("PINE2", 5), library)[0].Sites)
        {
            Assert.Equal(0f, site.Foot.Y);
        }
    }

    /// <summary>A room whose one object draws each of these boxes as its own surface.</summary>
    private static BspFile Faces(params (string Texture, Vector3 Low, Vector3 High)[] drawn)
    {
        List<Vector3> vertices = [];
        List<ushort> indices = [];
        List<BspPolygon> polygons = [];
        List<BspSurface> surfaces = [];

        foreach ((string texture, Vector3 low, Vector3 high) in drawn)
        {
            var corner = (ushort)vertices.Count;

            vertices.Add(new Vector3(low.X, low.Y, low.Z));
            vertices.Add(new Vector3(high.X, low.Y, high.Z));
            vertices.Add(new Vector3(high.X, high.Y, high.Z));
            vertices.Add(new Vector3(low.X, high.Y, low.Z));

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = indices.Count,
                VertexIndexCount = 4,
                SurfaceIndex = surfaces.Count,
            });

            surfaces.Add(new BspSurface
            {
                ObjectIndex = 0,
                TextureName = texture,
                LightmapUvOffset = Vector2.Zero,
                LightmapUvScale = Vector2.One,
                Flags = 0,
            });

            indices.AddRange(
                [corner, (ushort)(corner + 1), (ushort)(corner + 2), (ushort)(corner + 3)]);
        }

        return BspFile.FromParts(
            "TEST.BSP", ["test_vegitation"], surfaces, polygons,
            [.. vertices], [], [.. indices]);
    }

    [Fact]
    public void An_object_holding_anything_but_foliage_is_left_alone()
    {
        // A room is hidden by object name, and hiding by surface can only take away things
        // this knows how to put back. Leaves it can, and the bole under them; a wall it
        // cannot, so an object holding one keeps its cards.
        TreeLibrary library = Library();

        Assert.Empty(Foliage.InGeometry(
            Faces(
                ("PINE2", new Vector3(-100, 100, 0), new Vector3(100, 320, 0)),
                ("csehousestone2", new Vector3(-100, 0, 40), new Vector3(100, 200, 40))),
            library));
    }

    [Fact]
    public void A_room_that_draws_a_whole_tree_has_its_bole_taken_with_its_leaves()
    {
        // The double trunk. RC1's hotel maple is a bole in Woodbark with its leaves on it,
        // and refusing the object because bark is not foliage left the room drawing a 1999
        // trunk while the scene file's card of the same tree grew a modelled one beside it.
        // Taken together they are one tree, and the tree that replaces it stands on the
        // ground the bole stood on rather than hanging where the leaves were.
        TreeLibrary library = Library();

        Foliage.FoliageObject found = Assert.Single(Foliage.InGeometry(
            Faces(
                ("PINE2", new Vector3(-100, 120, 0), new Vector3(100, 320, 0)),
                ("Woodbark", new Vector3(-12, 0, -12), new Vector3(12, 150, 12))),
            library));

        TreeSite site = Assert.Single(found.Sites);

        Assert.True(site.Trunked);
        Assert.Equal(0f, site.Foot.Y);
        Assert.Equal(320f, site.Height);
        Assert.Equal(2, found.Surfaces.Count);
    }

    [Fact]
    public void Bark_with_no_crown_over_it_keeps_its_wood()
    {
        // A fence sharing an object with a tree, which is the case that stops this rule
        // from being "hide every plank in the room that happens to sit beside foliage".
        // The tree is still replaced; the fence is still drawn.
        TreeLibrary library = Library();

        Foliage.FoliageObject found = Assert.Single(Foliage.InGeometry(
            Faces(
                ("PINE2", new Vector3(-100, 0, 0), new Vector3(100, 320, 0)),
                ("TRUNK01", new Vector3(900, 0, 900), new Vector3(1100, 90, 900))),
            library));

        TreeSite site = Assert.Single(found.Sites);

        Assert.False(site.Trunked);
        Assert.Single(found.Surfaces);
    }

    [Fact]
    public void The_foliage_a_tree_is_painted_with_ships_with_it()
    {
        // A grown tree names textures no archive contains, so the pack has to carry them.
        Library();
        File.Copy(
            Path.Combine(_directory, "spruce_00.glb"),
            Path.Combine(_directory, "RBN_SPRUCE_SPRAY.png"));

        Assert.True(TreeLibrary.Open(_directory).Textures.Has("RBN_SPRUCE_SPRAY"));
    }
}

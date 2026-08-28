using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game;

/// <summary>Somewhere a modelled tree can stand, and how big it has to be to stand there.</summary>
/// <param name="Species">Which species the card was a picture of.</param>
/// <param name="Foot">Where the tree meets the ground, or where its crown begins.</param>
/// <param name="Height">How tall it has to come out, in scene units.</param>
/// <param name="Radius">How far the card reached from its centre, in scene units.</param>
/// <param name="Seed">Something stable about the place, for choosing a variant.</param>
/// <param name="Trunked">
/// Whether the room drew this tree's bole as well as its leaves, so that the site covers a
/// whole tree from the ground up rather than a crown hanging in the air.
/// </param>
/// <remarks>
/// <see cref="Trunked"/> is what settles an argument between a room and a scene file that
/// describe the same tree. RC1 draws the hotel maple twice: <c>rc1_vegitation</c> carries a
/// modelled bole with leaf cards on it, and <c>rc1_hoteltreeleavesff</c> is a flat
/// <c>MAPLESIDE1</c> card of the same tree standing in the same place. Only the room's copy
/// knows where the ground is, so it is the one that gets grown and the prop is put away.
/// </remarks>
public readonly record struct TreeSite(
    TreeSpecies Species,
    Vector3 Foot,
    float Height,
    float Radius,
    int Seed,
    bool Trunked = false);

/// <summary>
/// Finds the trees hiding in a scene's flat foliage cards.
/// </summary>
/// <remarks>
/// <para>
/// GK3 draws a tree as a picture of one on a quad, or on two quads crossed at the trunk.
/// The picture is the tree's whole description: how tall it is, how wide it spread, and
/// which species it was meant to be. So a card is not thrown away and guessed at — it is
/// measured, and what replaces it is grown to the size the artist drew.
/// </para>
/// <para>
/// The species comes from the texture rather than from the model's name, because the names
/// do not agree with each other and the textures do. <c>WOD_BIGDTREEFF</c>,
/// <c>CSE_FFTREE03</c> and <c>PL6_FFTREE01</c> are the same broadleaf under three
/// conventions, and all three draw <c>TREE00</c>.
/// </para>
/// </remarks>
public static class Foliage
{
    /// <summary>
    /// Foliage bitmaps that are a hillside rather than a tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Painted strips of distant woodland, whole ridges of it on one quad. There is no
    /// single tree in one to measure and nothing sensible to put in its place, so they are
    /// left drawn — but they must not stop the <em>real</em> trees beside them being
    /// replaced, which is what they were doing: an object holding two trees and one of
    /// these was refused whole, and nineteen objects across the corpus are shaped that way.
    /// </para>
    /// <para>
    /// Named here rather than in the tree manifest because this is a fact about the 1999
    /// corpus and not about anything that has been grown. A species says which sprites it
    /// stands in for; this says which sprites are nobody's job.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> Backdrops = new(StringComparer.OrdinalIgnoreCase)
    {
        "TREEGROUP01", "TREEGROUP02", "TREEGROUP03",
        "TILEDTREES",
        "FULLTREE01", "FULLTREE01ENDS", "FULLTREE02", "FULLTREE02ENDS",
        "COUMETREES", "HOMMETREES", "HOMMETREES2", "MORTTREES",
        "RC1TREES2", "RC2TREESA", "RC2TREESB", "RC2TREESC", "ARMTREEFLD",
    };

    /// <summary>
    /// The bitmaps a modelled bole or limb is painted with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four textures, and between them they are what used to make a tree untouchable.
    /// <c>rc1_vegitation</c> is a maple: a bole in <c>Woodbark</c>, leaf cards in
    /// <c>maple1trileaf</c>, and nothing else. Refusing it because the bark is not foliage
    /// left the room drawing a 1999 trunk while the scene file's card of the same tree grew
    /// a modelled one beside it — two trunks through each other, which is the shape of the
    /// bug this list removes.
    /// </para>
    /// <para>
    /// Measured rather than guessed: across the corpus, 77 objects mix foliage with
    /// something else and <b>108 of those mixtures are one of these four</b> —
    /// <c>NewBranch</c> 38, <c>Woodbark</c> 33, <c>Trunk01</c> 26, <c>Trunk02</c> 11. What
    /// is left over is bushes and buildings, and those still refuse the object.
    /// </para>
    /// <para>
    /// Bark alone says nothing. A surface is only taken away when a <em>crown of leaves
    /// stands over it</em> — see <see cref="Claims"/> — so a fence or a telegraph pole in
    /// the same object as a tree keeps its wood.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> Barks = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRUNK01", "TRUNK02", "WOODBARK", "NEWBRANCH",
    };

    /// <summary>The smallest card worth replacing, in scene units.</summary>
    /// <remarks>
    /// Gabriel is 76 units tall. Anything under half of him is a shrub or a scrap of
    /// undergrowth rather than a tree, and a grown trunk with a crown on it is the wrong
    /// shape for it.
    /// </remarks>
    private const float SmallestTree = 40f;

    /// <summary>How far a grown tree may be stretched or squeezed to match a card's width.</summary>
    /// <remarks>
    /// A tree is not a rectangle and the two do not have to agree exactly, but a maple card
    /// is square where a grown maple is broader than it is tall, and left alone the crown
    /// would overhang the path the card kept clear. Clamped, because a tree squashed to
    /// half its width stops reading as that species at all.
    /// </remarks>
    private const float LeastSqueeze = 0.75f;

    /// <summary>The other end of <see cref="LeastSqueeze"/>.</summary>
    private const float MostStretch = 1.35f;

    /// <summary>Reads a placed prop as a tree, when that is what it is.</summary>
    /// <param name="model">The parsed prop.</param>
    /// <param name="trees">The grown trees available to stand in for it.</param>
    /// <returns>Where a tree goes, or null when this prop is not a tree.</returns>
    /// <remarks>
    /// <para>
    /// Every submesh has to be foliage, and they all have to be the same species. A model
    /// that is a tree <em>and</em> something else — a lantern hung in one, a sign nailed to
    /// one — would lose the something else, and there is no way to put it back from here.
    /// </para>
    /// <para>
    /// The box is the model's own, in its own space, because that is the space it will be
    /// placed in: GK3's props carry their scene position in their vertices rather than in a
    /// transform, so a card's box is already where the tree goes.
    /// </para>
    /// </remarks>
    public static TreeSite? SiteFor(ModFile model, TreeLibrary trees)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(trees);

        TreeSpecies? species = null;
        var least = new Vector3(float.MaxValue);
        var most = new Vector3(float.MinValue);
        bool any = false;

        foreach (ModMesh mesh in model.Meshes)
        {
            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.Positions.Length == 0)
                {
                    continue;
                }

                if (trees.For(submesh.TextureName) is not { } found)
                {
                    return null;
                }

                if (species is not null && !ReferenceEquals(species, found))
                {
                    return null;
                }

                species = found;
                any = true;

                foreach (Vector3 position in submesh.Positions)
                {
                    Vector3 placed = Vector3.Transform(position, mesh.MeshToLocal);
                    least = Vector3.Min(least, placed);
                    most = Vector3.Max(most, placed);
                }
            }
        }

        return any && species is not null ? Site(species, least, most) : null;
    }

    /// <summary>Turns a measured box into a tree that fills it.</summary>
    /// <param name="species">The species the card was a picture of.</param>
    /// <param name="least">Lower corner of the card's box.</param>
    /// <param name="most">Upper corner of the card's box.</param>
    /// <param name="trunked">Whether the box includes a bole the room drew for itself.</param>
    /// <returns>The site, or null when the box is too small or too flat to be a tree.</returns>
    public static TreeSite? Site(
        TreeSpecies species, Vector3 least, Vector3 most, bool trunked = false)
    {
        ArgumentNullException.ThrowIfNull(species);

        float height = most.Y - least.Y;

        if (height < SmallestTree)
        {
            return null;
        }

        // Half the wider horizontal side. A single card is flat in one direction, so its
        // thin side says nothing about how far the tree spread; a crossed pair is roughly
        // square and either side would do.
        float radius = MathF.Max(most.X - least.X, most.Z - least.Z) * 0.5f;

        var foot = new Vector3((least.X + most.X) * 0.5f, least.Y, (least.Z + most.Z) * 0.5f);

        // Quantised, so that a tree keeps its variant across a rebuild of the geometry it
        // was measured from, and two cards a unit apart do not become two different trees.
        int seed = Mix(
            (int)MathF.Round(foot.X / 8f),
            (int)MathF.Round(foot.Z / 8f),
            (int)MathF.Round(height / 8f),
            species.Name);

        return new TreeSite(species, foot, height, radius, seed & int.MaxValue, trunked);
    }

    /// <summary>A hash that is the same in every process, which is the whole point of it.</summary>
    /// <remarks>
    /// FNV-1a, written out, and deliberately not <see cref="HashCode"/>: that one is seeded
    /// randomly once per process, so a wood hashed with it would be a different wood every
    /// time the game was started. The property being protected is that a room comes out the
    /// same on every load — a stand that reshuffles itself when the player walks out and
    /// back in is worse than a stand of identical trees, and it makes two renders of one
    /// scene impossible to compare. The test for it can only see one process, so the reason
    /// has to be written down here.
    /// </remarks>
    private static int Mix(int x, int z, int height, string species)
    {
        const uint Prime = 16777619;
        uint hash = 2166136261;

        foreach (int part in (stackalloc[] { x, z, height }))
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash = (hash ^ (byte)(part >> shift)) * Prime;
            }
        }

        foreach (char letter in species)
        {
            hash = (hash ^ (byte)char.ToUpperInvariant(letter)) * Prime;
        }

        return (int)hash;
    }

    /// <summary>
    /// A run of the room's own geometry that is nothing but foliage cards, and the trees
    /// hiding in it.
    /// </summary>
    /// <param name="Named">The object name, as the geometry file records it.</param>
    /// <param name="Sites">One site per tree found in it.</param>
    /// <param name="Cards">How many drawn faces those trees were made of.</param>
    /// <param name="Triangles">What growing every one of those sites would cost.</param>
    /// <param name="Surfaces">
    /// Which surfaces of the room these trees replace, and so which must stop being drawn.
    /// </param>
    /// <remarks>
    /// By surface rather than by name, because an object is not always all foliage.
    /// <c>pou_trees01</c> is two trees and a painted strip of distant hillside; the trees
    /// are replaced and the strip is left exactly where it is.
    /// </remarks>
    public readonly record struct FoliageObject(
        string Named,
        IReadOnlyList<TreeSite> Sites,
        int Cards,
        int Triangles,
        IReadOnlyList<int> Surfaces);

    /// <summary>
    /// Finds the trees in a room's own geometry.
    /// </summary>
    /// <param name="scene">The parsed room.</param>
    /// <param name="trees">The grown trees available to stand in for its cards.</param>
    /// <returns>One entry per object that is entirely foliage, largest first.</returns>
    /// <remarks>
    /// <para>
    /// The rooms hold <b>5,760</b> drawn foliage cards, 3,790 of them inside 64 objects —
    /// <c>wod_treeshadowcasters</c>, <c>lhm_treeshadowcasters</c>,
    /// <c>rc1_pleavesshadowcasters</c> — that contain nothing else.
    /// </para>
    /// <para>
    /// Most of those turn out to be the <em>same</em> trees the scene file places as props,
    /// drawn a second time, so what this actually adds is small: across the twenty-five
    /// outdoor scenes measured it is 24 trees, sixteen of them in BAL and two in LHE, where
    /// the room carries trees no prop does. It is worth having for those and it is not
    /// where the bulk of the foliage is.
    /// </para>
    /// <para>
    /// <b>Foliage, bark, and nothing else.</b> An object holding a wall or a gravestone as
    /// well is refused whole, because what it draws in place of the cards cannot be worked
    /// out from here. Bark is the exception and it is the important one: an object that is
    /// leaves on a modelled bole is a <em>whole tree</em>, and the tree that replaces it
    /// stands on the ground the bole stood on rather than hanging where the leaves were.
    /// Only bark with a crown of leaves over it is taken — see <see cref="Claims"/> — so a
    /// fence sharing an object with a tree keeps its wood and the tree is still replaced.
    /// </para>
    /// <para>
    /// <b>A card is a surface, never a polygon.</b> A room's geometry has been through a BSP
    /// splitter, and what that leaves is not the faces an artist drew: one 320-unit spruce
    /// card in LHM arrives as five polygons, sliced across at whatever heights the tree's
    /// planes happened to cut it. Clustering those directly turns a single tree into half a
    /// dozen — and a slice taken from between 300 and 378 units up is a tree that grows in
    /// mid-air, with its own trunk, above the real one. LHM's 1,023 polygons are 190 drawn
    /// faces, and 190 is the number this works from.
    /// </para>
    /// <para>
    /// The clustering is then simple, because the reconstructed data is: one tree is two or
    /// three cards crossed at the same spot, and their centres agree to within about three
    /// units where the trees themselves stand a couple of hundred apart.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FoliageObject> InGeometry(BspFile scene, TreeLibrary trees)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(trees);

        if (trees.IsEmpty)
        {
            return [];
        }

        // The drawn face each polygon came off, put back together. Bounds only: what a tree
        // needs from a card is where it is and how big it was, and the polygons are the
        // same face however the splitter divided it.
        var pieces = new Dictionary<int, (Vector3 Least, Vector3 Most, bool Bark)>();
        var refused = new HashSet<int>();

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= scene.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = scene.Surfaces[polygon.SurfaceIndex];
            int owner = surface.ObjectIndex;

            if (refused.Contains(owner))
            {
                continue;
            }

            // One surface of something else disqualifies the whole object, and the check has
            // to see all of them before any of it is used. Two things are not "something
            // else": a backdrop strip, which stays drawn where it is and says nothing about
            // whether the trees beside it can be replaced, and bark, which is the tree's own
            // bole and is measured along with its leaves.
            bool bark = false;

            if (trees.For(surface.TextureName) is null)
            {
                string plain = Path.GetFileNameWithoutExtension(surface.TextureName);

                if (Backdrops.Contains(plain))
                {
                    continue;
                }

                if (!Barks.Contains(plain))
                {
                    refused.Add(owner);
                    continue;
                }

                bark = true;
            }

            var least = new Vector3(float.MaxValue);
            var most = new Vector3(float.MinValue);

            for (int at = 0; at < polygon.VertexIndexCount; at++)
            {
                int index = polygon.VertexIndexOffset + at;

                if (index < 0 || index >= scene.VertexIndices.Length)
                {
                    continue;
                }

                ushort vertex = scene.VertexIndices[index];

                if (vertex < scene.Vertices.Length)
                {
                    least = Vector3.Min(least, scene.Vertices[vertex]);
                    most = Vector3.Max(most, scene.Vertices[vertex]);
                }
            }

            if (least.X > most.X)
            {
                continue;
            }

            pieces[polygon.SurfaceIndex] = pieces.TryGetValue(
                polygon.SurfaceIndex, out (Vector3 Least, Vector3 Most, bool Bark) already)
                ? (Vector3.Min(already.Least, least), Vector3.Max(already.Most, most), bark)
                : (least, most, bark);
        }

        var cards = new Dictionary<int, List<Card>>();
        var boles = new Dictionary<int, List<Card>>();

        foreach ((int index, (Vector3 least, Vector3 most, bool bark)) in pieces)
        {
            BspSurface surface = scene.Surfaces[index];

            if (refused.Contains(surface.ObjectIndex))
            {
                continue;
            }

            if (bark)
            {
                Owned(boles, surface.ObjectIndex).Add(new Card(null, least, most, index));
                continue;
            }

            if (trees.For(surface.TextureName) is not { } species)
            {
                continue;
            }

            Owned(cards, surface.ObjectIndex).Add(new Card(species, least, most, index));
        }

        List<FoliageObject> found = [];

        foreach ((int owner, List<Card> owned) in cards)
        {
            if (owner < 0 || owner >= scene.ObjectNames.Count || owned.Count == 0)
            {
                continue;
            }

            List<Crown> crowns = Cluster(owned);

            // The boles under those crowns, and only those. A bark surface with no leaves
            // over it is somebody else's — a fence, a telegraph pole, the wooden frame of a
            // sign — and it stays exactly where it is.
            List<int> hidden = [.. owned.Select(c => c.Surface).Distinct()];

            if (boles.TryGetValue(owner, out List<Card>? wood))
            {
                foreach (Card bole in wood)
                {
                    int claimed = Claims(crowns, bole);

                    if (claimed < 0)
                    {
                        continue;
                    }

                    crowns[claimed] = crowns[claimed] with
                    {
                        Least = Vector3.Min(crowns[claimed].Least, bole.Least),
                        Most = Vector3.Max(crowns[claimed].Most, bole.Most),
                        Trunked = true,
                    };

                    hidden.Add(bole.Surface);
                }
            }

            List<TreeSite> sites = [];

            foreach (Crown crown in crowns)
            {
                if (Site(crown.Species, crown.Least, crown.Most, crown.Trunked) is { } site)
                {
                    sites.Add(site);
                }
            }

            if (sites.Count > 0)
            {
                found.Add(new FoliageObject(
                    scene.ObjectNames[owner],
                    sites,
                    owned.Count,
                    sites.Sum(s => TreeLibrary.Variant(s.Species, s.Seed).Triangles),
                    hidden));
            }
        }

        // Largest first, so that a scene spending a triangle budget spends it on the wood
        // the player is standing in rather than on a copse behind a wall.
        return [.. found.OrderByDescending(f => f.Cards)];
    }

    /// <summary>One drawn face of a room's foliage or bark, before its tree is known.</summary>
    /// <remarks>The species is null for bark, which belongs to whichever crown stands over it.</remarks>
    private readonly record struct Card(
        TreeSpecies? Species, Vector3 Least, Vector3 Most, int Surface);

    /// <summary>One tree's worth of cards, gathered but not yet measured.</summary>
    private record struct Crown(
        TreeSpecies Species, Vector3 Least, Vector3 Most, bool Trunked);

    /// <summary>The list an object's cards go in, made on first use.</summary>
    private static List<Card> Owned(Dictionary<int, List<Card>> by, int owner)
    {
        if (!by.TryGetValue(owner, out List<Card>? already))
        {
            already = [];
            by[owner] = already;
        }

        return already;
    }

    /// <summary>
    /// Which crown, if any, a piece of bark is the bole of.
    /// </summary>
    /// <param name="crowns">The trees found in the same object.</param>
    /// <param name="bole">The bark surface.</param>
    /// <returns>Its crown's index, or -1 when nothing stands over it.</returns>
    /// <remarks>
    /// <para>
    /// Under the leaves and reaching up towards them. Both halves are needed: horizontal
    /// position alone would claim a fence running past the foot of a tree, and height alone
    /// would claim a rafter in the same object.
    /// </para>
    /// <para>
    /// The margin is generous because the two measurements are of the same tree drawn by
    /// the same artist — RC1's maple has its bole's centre <b>thirteen units</b> from its
    /// crown's, where the crown is 283 units across — and because the cost of missing is
    /// only that the bole stays drawn under a tree that also has one.
    /// </para>
    /// </remarks>
    private static int Claims(List<Crown> crowns, Card bole)
    {
        var foot = new Vector2(
            (bole.Least.X + bole.Most.X) * 0.5f, (bole.Least.Z + bole.Most.Z) * 0.5f);

        int found = -1;
        float nearest = float.MaxValue;

        for (int index = 0; index < crowns.Count; index++)
        {
            Crown crown = crowns[index];
            var centre = new Vector2(
                (crown.Least.X + crown.Most.X) * 0.5f, (crown.Least.Z + crown.Most.Z) * 0.5f);

            float radius = MathF.Max(
                crown.Most.X - crown.Least.X, crown.Most.Z - crown.Least.Z) * 0.5f;
            float apart = Vector2.Distance(centre, foot);

            // Under the crown's own spread, and rising into the bottom half of it. A bole
            // that stops well below its leaves is a post standing near a tree.
            if (apart > (radius * 1.1f) + 16f ||
                bole.Most.Y < crown.Least.Y - ((crown.Most.Y - crown.Least.Y) * 0.5f) ||
                bole.Least.Y > crown.Most.Y)
            {
                continue;
            }

            if (apart < nearest)
            {
                nearest = apart;
                found = index;
            }
        }

        return found;
    }

    /// <summary>Gathers cards that stand over the same ground into one tree each.</summary>
    private static List<Crown> Cluster(List<Card> cards)
    {
        // Widest first. A tree's widest card is the one that says how far it spread, and
        // starting from it means the narrow crossing quad is drawn into the cluster rather
        // than becoming a second, thinner tree of its own.
        List<Card> order = [.. cards.OrderByDescending(
            c => MathF.Max(c.Most.X - c.Least.X, c.Most.Z - c.Least.Z))];

        bool[] taken = new bool[order.Count];
        List<Crown> crowns = [];

        for (int seed = 0; seed < order.Count; seed++)
        {
            if (taken[seed])
            {
                continue;
            }

            Card first = order[seed];
            taken[seed] = true;

            Vector3 least = first.Least;
            Vector3 most = first.Most;

            // A third of the seed card's own width. The cards of one tree are crossed at its
            // trunk and their centres agree to within about three units, so this is a wide
            // margin rather than a fine one — and it still cannot reach a neighbour, which
            // stands a couple of hundred units away.
            float width = MathF.Max(most.X - least.X, most.Z - least.Z);
            float reach = MathF.Max(width * 0.33f, 8f);

            // Measured from where the seed is and not from where the cluster has got to.
            // Against a running centre a cluster walks: each card it takes moves the middle
            // a little, the next one is then in range, and a stand of six spruces becomes
            // one spruce six trees wide.
            var centre = new Vector2((least.X + most.X) * 0.5f, (least.Z + most.Z) * 0.5f);

            for (int other = seed + 1; other < order.Count; other++)
            {
                if (taken[other])
                {
                    continue;
                }

                Card card = order[other];
                var at = new Vector2(
                    (card.Least.X + card.Most.X) * 0.5f, (card.Least.Z + card.Most.Z) * 0.5f);

                // Over the same ground and overlapping in height. Height matters: a room
                // built on a hillside has cards above cards, and ground position alone
                // would gather a tree and the one on the terrace above it into one.
                if (Vector2.Distance(centre, at) > reach ||
                    card.Least.Y > most.Y || card.Most.Y < least.Y)
                {
                    continue;
                }

                Vector3 grownLeast = Vector3.Min(least, card.Least);
                Vector3 grownMost = Vector3.Max(most, card.Most);

                taken[other] = true;
                least = grownLeast;
                most = grownMost;
            }

            if (first.Species is { } species)
            {
                crowns.Add(new Crown(species, least, most, Trunked: false));
            }
        }

        return crowns;
    }

    /// <summary>Where a grown tree has to be put to fill a site.</summary>
    /// <param name="site">The site.</param>
    /// <param name="tree">The variant chosen for it.</param>
    /// <returns>A transform for the normalised tree.</returns>
    /// <remarks>
    /// <para>
    /// A grown tree stands on the origin and is exactly one unit tall, so the height is the
    /// whole of the vertical scale. The horizontal scale starts there too and is then
    /// nudged towards the card's own width, within limits: a grown maple is wider than it
    /// is tall and the square card it replaces is not, and a crown that overhangs by half
    /// its width reaches over walls the card never touched.
    /// </para>
    /// <para>
    /// The turn about the vertical is what stops four variants from looking like four
    /// copies. It comes from the site's seed rather than from a counter, so a wood is the
    /// same wood every time the room is loaded.
    /// </para>
    /// </remarks>
    public static Matrix4x4 Standing(TreeSite site, GrownTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        float across = site.Height;

        if (tree.Radius > 0.01f && site.Radius > 0.01f)
        {
            float wanted = site.Radius / (tree.Radius * site.Height);
            across *= Math.Clamp(wanted, LeastSqueeze, MostStretch);
        }

        float turn = (site.Seed % 3600) / 3600f * MathF.Tau;

        return Matrix4x4.CreateScale(across, site.Height, across)
            * Matrix4x4.CreateRotationY(turn)
            * Matrix4x4.CreateTranslation(site.Foot);
    }
}

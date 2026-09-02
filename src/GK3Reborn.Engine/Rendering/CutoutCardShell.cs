// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// Gives a keyed card the thickness the thing drawn on it would have had.
/// </summary>
/// <remarks>
/// <para>
/// GK3 draws a railing as a picture of one on a single quad, with the gaps between the
/// balusters cut out of the magenta key. From in front it is convincing and from anywhere
/// else it is a sheet of paper: the balusters have no sides, so the rail vanishes as the
/// camera comes round to it and the shadow it casts has no width. The corpus holds some
/// nine hundred such surfaces once the trees are set aside — stair rails, iron fences,
/// gates, chains, barbed and razor wire, lantern scrollwork, buffet legs, window mullions,
/// a fireplace grate.
/// </para>
/// <para>
/// <b>The silhouette is in the alpha, so that is where the geometry comes from.</b> The
/// card becomes a shell: its own triangles moved half a thickness one way, a mirrored copy
/// moved half a thickness the other, and a rim joining the two around the outline the key
/// cuts. The rim is the part that matters — two parallel cutout planes with nothing between
/// them read as a ghost of a railing from any oblique angle, which is worse than the flat
/// card, not better.
/// </para>
/// <para>
/// <b>Half a thickness each way, never one thickness one way.</b> Which side of a card is
/// its outside is not in this data — the winding is not consistent enough to ask, which is
/// the same fact <see cref="CoplanarCards"/> exists because of — and extruding the wrong way
/// lifts a rail off its posts. Moving symmetrically about the plane the artist placed makes
/// the question go away: the card still occupies the plane it always did.
/// </para>
/// <para>
/// <b>The rim is built from texel runs, not from a traced contour.</b> These outlines are
/// balusters, bars, wires and mullions, which are axis-aligned in texture space almost
/// without exception, so merging the runs of texel edges along each row and column
/// reproduces them exactly — a baluster's whole side is one quad — while a traced-and-
/// simplified contour would need clipping against the card's own footprint, which is the
/// part that goes wrong. What a run misses is the diagonal, and a diagonal comes out as
/// steps a texel high: six millimetres at the scale these cards are drawn at.
/// </para>
/// </remarks>
public static class CutoutCards
{
    /// <summary>
    /// Thinnest a thickened card may come out, in scene units.
    /// </summary>
    /// <remarks>
    /// A character is 72 units tall, so this is about seven millimetres — a wire, and
    /// already ten times the separation <see cref="CoplanarCards"/> gives a flat card to
    /// keep a depth test happy.
    /// </remarks>
    public const float LeastThickness = 0.3f;

    /// <summary>Thickest, in scene units: about ten centimetres.</summary>
    /// <remarks>
    /// Reached only where a card is drawn very large — a texel worth a whole unit — and it
    /// is a cap on the measurement rather than a target. A newel post is not a railing and
    /// should not be given a railing's treatment.
    /// </remarks>
    public const float MostThickness = 4f;

    /// <summary>
    /// Widest a bar may measure in the room and still be a bar, in scene units.
    /// </summary>
    /// <remarks>
    /// <see cref="CutoutMask.WidestFeatureShare"/> asks the same question of the drawing;
    /// this asks it of the placement, because a texture of thin bars stretched over a wall
    /// is no longer thin bars. Eight units is about nineteen centimetres.
    /// </remarks>
    public const float WidestFeatureUnits = 8f;

    /// <summary>
    /// Shortest rim run worth building, in scene units.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rim exists to be seen, and a facet of it five centimetres long cannot be — not at
    /// the distance GK3 puts its camera, and not against a texture whose own texels are
    /// larger than that. Dropping the short runs leaves gaps in the rim that are the same
    /// size as the facets they replace, which is to say invisible, and it is what makes the
    /// difference between this pass costing eighty-four thousand triangles across the corpus
    /// and costing two hundred and twenty-five thousand.
    /// </para>
    /// <para>
    /// It is expressed in scene units and not in texels on purpose: it is a claim about what
    /// the player can resolve. A texture tiled forty times along a fence has texels a
    /// twentieth the size of the same texture used once, and the rim should be coarser there
    /// in exactly that proportion.
    /// </para>
    /// </remarks>
    public const float ShortestRunUnits = 2f;

    /// <summary>Most rim quads one surface may be given.</summary>
    /// <remarks>
    /// <b>A budget reduces the treatment; it does not discard it.</b> Over this, the
    /// shortest run worth building is raised by half and the rim measured again, so a card
    /// that cannot afford every facet keeps its longest ones — the sides of the bars — and
    /// loses the stipple between them. Refusing instead would refuse the chateau's
    /// chain-link fence, which is the single card in the corpus that most needs this.
    /// </remarks>
    public const int MostRimQuads = 800;

    /// <summary>Most occluder quads one surface's shadow may be built from.</summary>
    /// <remarks>
    /// <para>
    /// The rim's budget buys facets nobody can see; this one buys a shadow nobody can
    /// resolve, and the two are reduced in opposite ways. A rim over budget drops its
    /// shortest runs, which leaves gaps as small as the facets they replace. A shadow that
    /// dropped its smallest patches would be a fence casting a shadow with holes in the
    /// bars, so this one <em>coarsens</em> instead: the grid is halved and meshed again.
    /// See <see cref="Coarsen"/> for which way that rounds, which is the part that matters.
    /// </para>
    /// <para>
    /// <b>Two thousand, because a cap this pass reaches is a cap that is doing harm.</b>
    /// Measured against an uncapped build on Montsegur's razor wire — the worst card in the
    /// corpus, 114,000 triangles uncapped — six hundred quads changed 0.19% of the frame
    /// and two thousand changes 0.06%, against a whole effect that is 0.34% of that frame.
    /// So six hundred was throwing away a third of the shadow it had just built and two
    /// thousand throws away a tenth, for 58,000 triangles against 114,000. Everywhere else
    /// in the corpus it never fires at all: RC1, POU, CHU, RC2 and CS3 come out at exactly
    /// their uncapped counts.
    /// </para>
    /// </remarks>
    public const int MostShadowQuads = 2000;

    /// <summary>Largest texel grid one card may be measured over.</summary>
    /// <remarks>
    /// A card tiling its texture forty times is measured across forty tiles' worth of
    /// texels, and this stops a pathological one from asking for an array nothing wants to
    /// allocate at load. Nothing in the corpus comes near it.
    /// </remarks>
    public const long MostTexels = 8L << 20;

    /// <summary>
    /// Cutout textures that are leaves, and are nobody's railing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one thing the measurement cannot answer. A leaf's edge is a smooth curve, which
    /// comes out of the run-merging as long straight runs of a two-texel feature, so a maple
    /// sprite measures <em>straighter</em> than the hotel's wrought-iron balustrade does. It
    /// was worth checking rather than assuming, and it settles the matter: made and grown
    /// things are not separable by this geometry, so the grown ones are named.
    /// </para>
    /// <para>
    /// Two reasons to leave them, and they apply to different halves of the list. The trees
    /// — <c>PINE2</c>, <c>MAPLE</c>, <c>TREE00</c> — are replaced outright by
    /// <c>Foliage</c>'s grown geometry, so thickening one is work done on triangles that are
    /// about to be thrown away. The bushes, vines and hillside strips are not replaced, and
    /// are left because a hard lit rim around a leaf silhouette reads as cardboard, which is
    /// the opposite of what this pass is for.
    /// </para>
    /// <para>
    /// Named here rather than derived, in the same spirit as <c>Foliage.Backdrops</c>: this
    /// is a fact about the 1999 corpus, it is thirty-three names long, and a person reading
    /// it can disagree with any one of them. Every entry was taken from the material
    /// library's own foliage class, less the four it puts there wrongly —
    /// <c>RC1IRONFENCE</c> and <c>CHUFENCE</c> are green because they are painted green and
    /// overgrown, and <c>RC1LANTERNSCROLL</c> and <c>DINFIREPLACE</c> are ironwork.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> Leaves { get; } = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "ARMBUSHES", "BRA", "BRAINSIDE", "BUG_INV", "BUSH00", "BUT_IVY_TREE", "CHUFENCEG",
        "CS2MONEYE02_A", "FULLTREE01", "FULLTREE01ENDS", "MAPLE", "MAPLE1TRILEAF",
        "MAPLESIDE1", "MAPLETOP1", "NEWBRANCH", "PINE2", "PINE2FLAT", "PL6VINES",
        "PL6VINESSIDES", "PLANT", "RC1BUSHTOP", "RC1HOTLBAK", "RC1TREES2", "TILEDTREES",
        "TREE00", "TREE01", "TREE02", "TREE06", "TREEGROUP01", "TREEGROUP02", "TREEGROUP03",
        "VINE_CLIMB", "WOODTREE3",
    };

    /// <summary>How far off its own plane a card's vertices may sit, relative to its size.</summary>
    private const float Flatness = 1e-3f;

    /// <summary>
    /// How far the affine fit may miss a vertex by, in scene units, before the card is left
    /// alone.
    /// </summary>
    /// <remarks>
    /// The whole construction rests on the card's texture coordinates being an affine
    /// function of position, because that is what lets a texel be turned back into a place
    /// in the room. Nearly every card in the corpus is exactly that; 89 of them are not, and
    /// those keep the geometry they shipped with rather than being given a rim somewhere
    /// arbitrary.
    /// </remarks>
    private const float FitTolerance = 0.05f;

    /// <summary>
    /// Builds the shell for one card, if the card is one this should be done to.
    /// </summary>
    /// <param name="positions">The card's vertices, in world space.</param>
    /// <param name="texCoords">Their texture coordinates, one per vertex.</param>
    /// <param name="indices">Its triangles, three indices each.</param>
    /// <param name="mask">What the texture on it measured as.</param>
    /// <returns>The shell, or null when this surface is left exactly as it is.</returns>
    public static ThickCard? Thicken(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector2> texCoords,
        IReadOnlyList<int> indices,
        CutoutMask mask)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(texCoords);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(mask);

        if (indices.Count < 3 || indices.Count % 3 != 0 || positions.Count != texCoords.Count)
        {
            return null;
        }

        if (!Coplanar(positions, indices))
        {
            return null;
        }

        if (!Fit(positions, texCoords, out Vector3 origin, out Vector3 across, out Vector3 down))
        {
            return null;
        }

        float unitsPerTexel =
            0.5f * ((across.Length() / mask.Width) + (down.Length() / mask.Height));

        if (unitsPerTexel <= 0f || !float.IsFinite(unitsPerTexel))
        {
            return null;
        }

        if (mask.FeatureTexels * unitsPerTexel > WidestFeatureUnits)
        {
            return null;
        }

        float thickness = Math.Clamp(
            mask.FeatureTexels * unitsPerTexel, LeastThickness, MostThickness);

        Vector3 plane = Vector3.Cross(across, down);

        if (plane.LengthSquared() <= 1e-12f)
        {
            return null;
        }

        plane = Vector3.Normalize(plane);

        if (Outline(texCoords, indices, mask) is not { } outline)
        {
            return null;
        }

        List<RimRun> rim = Rim(outline, mask, unitsPerTexel);

        // No rim, no thickening. A shell with no sides is two parallel cutouts a thickness
        // apart, and that reads worse than the card it replaced.
        if (rim.Count == 0)
        {
            return null;
        }

        var triangles = new List<CardTriangle>((indices.Count / 3 * 2) + (rim.Count * 2));

        Faces(positions, texCoords, indices, thickness, triangles);

        foreach (RimRun run in rim)
        {
            Wall(run, mask, origin, across, down, plane, thickness, triangles);
        }

        return new ThickCard(
            triangles, thickness, rim.Count, Shadow(outline, mask, origin, across, down));
    }

    /// <summary>
    /// The card's own triangles, moved half a thickness each way.
    /// </summary>
    /// <remarks>
    /// Each triangle is moved along <em>its own</em> normal rather than along the card's,
    /// and its mirror along the opposite. On a card wound consistently the two are the same
    /// thing; on one wound both ways — which GK3's geometry routinely is — this keeps every
    /// triangle shading exactly as it shades today and adds the face it was missing, rather
    /// than silently relighting half the card.
    /// </remarks>
    private static void Faces(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector2> texCoords,
        IReadOnlyList<int> indices,
        float thickness,
        List<CardTriangle> into)
    {
        float half = thickness * 0.5f;

        for (int at = 0; at + 2 < indices.Count; at += 3)
        {
            int ia = indices[at];
            int ib = indices[at + 1];
            int ic = indices[at + 2];

            Vector3 pa = positions[ia];
            Vector3 pb = positions[ib];
            Vector3 pc = positions[ic];

            Vector3 normal = Vector3.Cross(pb - pa, pc - pa);

            if (normal.LengthSquared() <= 1e-12f)
            {
                continue;
            }

            normal = Vector3.Normalize(normal);

            Vector3 step = normal * half;

            into.Add(new CardTriangle(
                new CurvedCorner(pa + step, normal, texCoords[ia]),
                new CurvedCorner(pb + step, normal, texCoords[ib]),
                new CurvedCorner(pc + step, normal, texCoords[ic])));

            into.Add(new CardTriangle(
                new CurvedCorner(pa - step, -normal, texCoords[ia]),
                new CurvedCorner(pc - step, -normal, texCoords[ic]),
                new CurvedCorner(pb - step, -normal, texCoords[ib])));
        }
    }

    /// <summary>One merged run of texel edges, in the card's own global texel grid.</summary>
    /// <param name="Vertical">Whether the run goes down a column rather than across a row.</param>
    /// <param name="Line">The column it runs down, or the row it runs across.</param>
    /// <param name="From">Where along that line it starts.</param>
    /// <param name="Length">How many texels long it is.</param>
    /// <param name="Side">Which way the hole is: -1 towards smaller coordinates, +1 larger.</param>
    private readonly record struct RimRun(
        bool Vertical, int Line, int From, int Length, int Side);

    /// <summary>
    /// Finds the silhouette: which texels of the card the artist actually painted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured in one grid spanning every tile the card covers, not tile by tile.</b> A
    /// railing tiles its texture seven times along a balcony, and the bar that runs off the
    /// right of one tile continues into the left of the next: asked tile by tile, every seam
    /// grows a wall across the middle of a continuous bar. Wrapping the lookup into the
    /// texture and the footprint test into the card makes the seams disappear, because there
    /// are none — the grid is continuous and the tiling is only how the texture is sampled.
    /// </para>
    /// <para>
    /// A texel is part of the card when the card's own triangles cover it. That is what keeps
    /// the rim inside the quad the artist drew: a stair rail uses 55% of its tile, and the
    /// other 45% of the outline belongs to a rail that is not there.
    /// </para>
    /// </remarks>
    private static Silhouette? Outline(
        IReadOnlyList<Vector2> texCoords,
        IReadOnlyList<int> indices,
        CutoutMask mask)
    {
        float minU = float.MaxValue, maxU = float.MinValue;
        float minV = float.MaxValue, maxV = float.MinValue;

        foreach (Vector2 uv in texCoords)
        {
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
            {
                return null;
            }

            minU = Math.Min(minU, uv.X);
            maxU = Math.Max(maxU, uv.X);
            minV = Math.Min(minV, uv.Y);
            maxV = Math.Max(maxV, uv.Y);
        }

        int x0 = (int)MathF.Floor(minU * mask.Width);
        int x1 = (int)MathF.Ceiling(maxU * mask.Width);
        int y0 = (int)MathF.Floor(minV * mask.Height);
        int y1 = (int)MathF.Ceiling(maxV * mask.Height);

        int width = x1 - x0;
        int height = y1 - y0;

        if (width <= 0 || height <= 0 || (long)width * height > MostTexels)
        {
            return null;
        }

        bool[] solid = new bool[width * height];

        Cover(texCoords, indices, mask, x0, y0, width, height, solid);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width) + x;

                if (solid[at])
                {
                    solid[at] = mask.Opaque[
                        (Wrap(y + y0, mask.Height) * mask.Width) + Wrap(x + x0, mask.Width)];
                }
            }
        }

        return new Silhouette(solid, x0, y0, width, height);
    }

    /// <summary>
    /// The texels of one card that are drawn rather than keyed away.
    /// </summary>
    /// <param name="Solid">Row-major, true where the card covers a texel the texture paints.</param>
    /// <param name="X0">Where the grid starts in the texture's own numbering, in texels.</param>
    /// <param name="Y0">The same, down.</param>
    /// <param name="Width">Texels across, spanning every tile the card covers.</param>
    /// <param name="Height">Texels down.</param>
    /// <remarks>
    /// Measured once and read twice — the rim walks its edges, the shadow fills its
    /// interior — because it is the expensive part: a chain tiling a sixteen-texel texture
    /// down a terrace is a grid of two million, and it was worth walking once.
    /// </remarks>
    private readonly record struct Silhouette(
        bool[] Solid, int X0, int Y0, int Width, int Height);

    /// <summary>
    /// Finds the silhouette's edges, as merged runs of texel edges.
    /// </summary>
    private static List<RimRun> Rim(Silhouette outline, CutoutMask mask, float unitsPerTexel)
    {
        (bool[] solid, int x0, int y0, int width, int height) = outline;

        // A budget reduces the treatment: the shortest run worth keeping is raised until the
        // rim fits, so what a card loses is its stipple and never its bars. See MostRimQuads.
        //
        // Extracted once and then filtered, rather than re-extracted at each length. The
        // walk is over every texel of every tile the card covers, and doing it nine times to
        // answer a question about a list cost a tenth of a second on the rooms that tile
        // most.
        float shortest = Math.Max(mask.FeatureTexels, ShortestRunUnits / unitsPerTexel);

        List<RimRun> found = Runs(solid, width, height, shortest);

        for (int attempt = 0; found.Count > MostRimQuads && attempt < 8; attempt++)
        {
            shortest *= 1.5f;
            found = found.FindAll(run => run.Length >= shortest);
        }

        // Runs come back numbered from the corner of the grid that was walked, and the grid
        // starts at (x0, y0) in the texture's own. Everything downstream reads a run as a
        // texture coordinate — where the wall stands on the card, and which texel it takes
        // its colour from — so the offset goes back on here. Cover subtracts it and the mask
        // lookup above wraps it back; this is the third place it has to be accounted for and
        // was the one that was missing.
        //
        // A card whose coordinates start at the texture's origin is unaffected, which is
        // most railings. A card that does not is moved bodily by whole tiles: RC1's
        // "COMPLET / NO VACANCIES" sign runs v from -1 to 0, so its rim was built one tile
        // low — a card's height of loose facets hanging in the air underneath it.
        return found.ConvertAll(run => run with
        {
            Line = run.Line + (run.Vertical ? x0 : y0),
            From = run.From + (run.Vertical ? y0 : x0),
        });
    }

    /// <summary>
    /// The silhouette again, as opaque triangles a shadow ray can be pointed at.
    /// </summary>
    /// <param name="outline">Which texels the card paints.</param>
    /// <param name="mask">The texture they are texels of.</param>
    /// <param name="origin">The affine fit's origin; see <see cref="Fit"/>.</param>
    /// <param name="across">Where u goes.</param>
    /// <param name="down">Where v goes.</param>
    /// <returns>Triangle corners in world space, three to a triangle.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why the drawn geometry cannot be traced instead.</b> A card is keyed, the
    /// structure is built with every triangle opaque and there is no any-hit shader to ask
    /// whether a hit landed on a baluster or on the gap beside it, so putting the shell into
    /// it would have a railing cast the shadow of a wall. That is why keyed geometry was
    /// left out altogether, and why a thickened rail went on casting no shadow at all: the
    /// pass gave it sides to be seen from, and nothing for the sun to be stopped by.
    /// </para>
    /// <para>
    /// The alpha is already decoded and already measured — it is what the rim was built from
    /// — so the test the missing shader would do per hit is done here instead, once, at
    /// load: the drawn texels are merged into as few rectangles as cover them and each
    /// becomes two opaque triangles. What a ray then hits is the bars and not the gaps,
    /// which is the whole of the question, and it costs no shader and no pipeline.
    /// </para>
    /// <para>
    /// <b>On the plane, not at either face.</b> The shell straddles the plane by half a
    /// thickness each way and the occluder lies flat on it, which is where the card has
    /// always been. Two planes would double the cost to widen a shadow by the width of a
    /// baluster — 0.3 to 4 units against a sun tens of thousands away — and one plane
    /// between the two faces cannot shadow either of them: a ray leaves a face along its
    /// own normal, away from the plane behind it.
    /// </para>
    /// <para>
    /// Greedy rectangles rather than a quad per texel. A baluster forty texels tall and
    /// four across is one rectangle, and it is the same merging the rim does along one
    /// dimension — this is the two-dimensional case of it. Measured over the corpus the
    /// merge is worth about thirty to one.
    /// </para>
    /// </remarks>
    private static List<Vector3> Shadow(
        Silhouette outline, CutoutMask mask, Vector3 origin, Vector3 across, Vector3 down)
    {
        bool[] solid = outline.Solid;
        int width = outline.Width;
        int height = outline.Height;
        int step = 1;

        List<(int X, int Y, int W, int H)> patches = Patches(solid, width, height);

        // Over budget the grid is coarsened rather than the patches thinned, so what a
        // fence loses is the crispness of its shadow's edge and never a bar out of the
        // middle of it. See MostShadowQuads.
        for (int attempt = 0; patches.Count > MostShadowQuads && attempt < 4; attempt++)
        {
            int wide = width;
            int tall = height;

            if (wide < 2 || tall < 2)
            {
                break;
            }

            solid = Coarsen(solid, ref wide, ref tall);
            width = wide;
            height = tall;
            step *= 2;

            patches = Patches(solid, width, height);
        }

        var corners = new List<Vector3>(patches.Count * 6);

        foreach ((int x, int y, int w, int h) in patches)
        {
            // Back into the texture's own numbering: the grid started at (X0, Y0) in fine
            // texels and a coarsened cell is `step` of them square. The same offset the rim
            // puts back on its runs, and the same trap — a card whose coordinates do not
            // start at the texture's origin is moved bodily by whole tiles without it.
            float u0 = (outline.X0 + (x * step)) / (float)mask.Width;
            float u1 = (outline.X0 + ((x + w) * step)) / (float)mask.Width;
            float v0 = (outline.Y0 + (y * step)) / (float)mask.Height;
            float v1 = (outline.Y0 + ((y + h) * step)) / (float)mask.Height;

            Vector3 a = origin + (across * u0) + (down * v0);
            Vector3 b = origin + (across * u1) + (down * v0);
            Vector3 c = origin + (across * u1) + (down * v1);
            Vector3 d = origin + (across * u0) + (down * v1);

            corners.Add(a);
            corners.Add(b);
            corners.Add(c);
            corners.Add(a);
            corners.Add(c);
            corners.Add(d);
        }

        return corners;
    }

    /// <summary>
    /// Merges the drawn texels into as few axis-aligned rectangles as cover them.
    /// </summary>
    /// <remarks>
    /// The usual greedy mesh: take the first texel nothing has claimed, run right while the
    /// row allows, then run down while whole rows of that width allow, and claim the block.
    /// It is not the smallest possible set of rectangles — finding that is a much harder
    /// problem and buys nothing here — but on a lattice of bars, which is what every card
    /// this touches is, it finds each bar as one rectangle, which is the answer anybody
    /// would draw by hand.
    /// </remarks>
    private static List<(int X, int Y, int W, int H)> Patches(
        bool[] solid, int width, int height)
    {
        var found = new List<(int, int, int, int)>();
        bool[] taken = new bool[solid.Length];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width) + x;

                if (!solid[at] || taken[at])
                {
                    continue;
                }

                int w = 1;

                while (x + w < width && solid[at + w] && !taken[at + w])
                {
                    w++;
                }

                int h = 1;

                while (y + h < height)
                {
                    int row = ((y + h) * width) + x;
                    bool whole = true;

                    for (int i = 0; i < w; i++)
                    {
                        if (!solid[row + i] || taken[row + i])
                        {
                            whole = false;
                            break;
                        }
                    }

                    if (!whole)
                    {
                        break;
                    }

                    h++;
                }

                for (int j = 0; j < h; j++)
                {
                    int row = ((y + j) * width) + x;

                    for (int i = 0; i < w; i++)
                    {
                        taken[row + i] = true;
                    }
                }

                found.Add((x, y, w, h));
            }
        }

        return found;
    }

    /// <summary>Halves the silhouette, keeping a cell three of its four agreed was drawn.</summary>
    /// <remarks>
    /// <para>
    /// <b>Three and not two, which is where this parts company with
    /// <see cref="CutoutMask"/>.</b> That one is measuring how wide a bar is drawn and takes
    /// a majority; this one is deciding what stops the sun, and the two errors are not
    /// worth the same. A rule that rounds a half-covered cell up turns a lattice into a
    /// sheet — a chain-link fence is exactly half drawn, so a majority makes every one of
    /// its cells solid and the fence casts the shadow of a wall, which is the single
    /// artefact this whole approach exists to avoid. Rounding down loses a thin bar
    /// instead, and a bar that goes missing casts what it cast before this: nothing.
    /// </para>
    /// <para>
    /// It costs nothing measurably, because <see cref="MostShadowQuads"/> is set where this
    /// hardly ever runs: over the corpus the two rules differ by 0.1% of the triangles, and
    /// on the frame that shows the most, by no pixels a threshold of two can find.
    /// </para>
    /// </remarks>
    private static bool[] Coarsen(bool[] solid, ref int width, ref int height)
    {
        int half = Math.Max(1, width / 2);
        int down = Math.Max(1, height / 2);
        bool[] smaller = new bool[half * down];

        for (int y = 0; y < down; y++)
        {
            for (int x = 0; x < half; x++)
            {
                int at = (y * 2 * width) + (x * 2);

                int drawn = (solid[at] ? 1 : 0) +
                            (solid[at + 1] ? 1 : 0) +
                            (solid[at + width] ? 1 : 0) +
                            (solid[at + width + 1] ? 1 : 0);

                smaller[(y * half) + x] = drawn >= 3;
            }
        }

        width = half;
        height = down;

        return smaller;
    }

    /// <summary>Which texels of the grid the card's triangles actually cover.</summary>
    private static void Cover(
        IReadOnlyList<Vector2> texCoords,
        IReadOnlyList<int> indices,
        CutoutMask mask,
        int x0,
        int y0,
        int width,
        int height,
        bool[] into)
    {
        for (int at = 0; at + 2 < indices.Count; at += 3)
        {
            Vector2 a = Texel(texCoords[indices[at]], mask, x0, y0);
            Vector2 b = Texel(texCoords[indices[at + 1]], mask, x0, y0);
            Vector2 c = Texel(texCoords[indices[at + 2]], mask, x0, y0);

            float area = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));

            if (MathF.Abs(area) < 1e-9f)
            {
                continue;
            }

            int left = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
            int right = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            int top = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
            int bottom = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    float w0 = (((b.Y - c.Y) * (px - c.X)) + ((c.X - b.X) * (py - c.Y))) / area;
                    float w1 = (((c.Y - a.Y) * (px - c.X)) + ((a.X - c.X) * (py - c.Y))) / area;
                    float w2 = 1f - w0 - w1;

                    if (w0 >= -1e-5f && w1 >= -1e-5f && w2 >= -1e-5f)
                    {
                        into[(y * width) + x] = true;
                    }
                }
            }
        }
    }

    /// <summary>A texture coordinate as a place in the card's own texel grid.</summary>
    private static Vector2 Texel(Vector2 uv, CutoutMask mask, int x0, int y0) =>
        new((uv.X * mask.Width) - x0, (uv.Y * mask.Height) - y0);

    /// <summary>Merges the texel edges of the silhouette into runs, dropping the short ones.</summary>
    private static List<RimRun> Runs(bool[] solid, int width, int height, float shortest)
    {
        var runs = new List<RimRun>();

        bool At(int x, int y) =>
            x >= 0 && y >= 0 && x < width && y < height && solid[(y * width) + x];

        foreach (int side in (ReadOnlySpan<int>)[-1, 1])
        {
            for (int x = 0; x < width; x++)
            {
                int length = 0;
                int from = 0;

                for (int y = 0; y <= height; y++)
                {
                    bool edge = y < height && At(x, y) && !At(x + side, y);

                    if (edge)
                    {
                        if (length == 0)
                        {
                            from = y;
                        }

                        length++;
                    }
                    else if (length > 0)
                    {
                        if (length >= shortest)
                        {
                            runs.Add(new RimRun(true, x, from, length, side));
                        }

                        length = 0;
                    }
                }
            }

            for (int y = 0; y < height; y++)
            {
                int length = 0;
                int from = 0;

                for (int x = 0; x <= width; x++)
                {
                    bool edge = x < width && At(x, y) && !At(x, y + side);

                    if (edge)
                    {
                        if (length == 0)
                        {
                            from = x;
                        }

                        length++;
                    }
                    else if (length > 0)
                    {
                        if (length >= shortest)
                        {
                            runs.Add(new RimRun(false, y, from, length, side));
                        }

                        length = 0;
                    }
                }
            }
        }

        return runs;
    }

    /// <summary>
    /// Turns one run into the wall down the side of a bar.
    /// </summary>
    /// <remarks>
    /// <b>Both corners take the texture coordinate of the drawn texel beside them, not of
    /// the edge itself.</b> An edge coordinate sits exactly on the boundary the key cuts,
    /// where the shader's own alpha test is as likely to throw the wall away as to keep it;
    /// half a texel inside is unambiguously painted, and it is the colour of the very
    /// baluster the wall is the side of.
    /// </remarks>
    private static void Wall(
        RimRun run,
        CutoutMask mask,
        Vector3 origin,
        Vector3 across,
        Vector3 down,
        Vector3 plane,
        float thickness,
        List<CardTriangle> into)
    {
        // The edge lies on the far side of the texel when the hole is to the right or below.
        float edge = run.Line + (run.Side < 0 ? 0f : 1f);
        float inside = run.Line + 0.5f;

        Vector2 a, b, ua, ub;
        Vector3 normal;

        if (run.Vertical)
        {
            a = new Vector2(edge / mask.Width, run.From / (float)mask.Height);
            b = new Vector2(edge / mask.Width, (run.From + run.Length) / (float)mask.Height);
            ua = new Vector2(inside / mask.Width, (run.From + 0.5f) / mask.Height);
            ub = new Vector2(inside / mask.Width, (run.From + run.Length - 0.5f) / mask.Height);
            normal = across * run.Side;
        }
        else
        {
            a = new Vector2(run.From / (float)mask.Width, edge / mask.Height);
            b = new Vector2((run.From + run.Length) / (float)mask.Width, edge / mask.Height);
            ua = new Vector2((run.From + 0.5f) / mask.Width, inside / mask.Height);
            ub = new Vector2((run.From + run.Length - 0.5f) / mask.Width, inside / mask.Height);
            normal = down * run.Side;
        }

        if (normal.LengthSquared() <= 1e-12f)
        {
            return;
        }

        normal = Vector3.Normalize(normal);

        Vector3 step = plane * (thickness * 0.5f);
        Vector3 pa = origin + (across * a.X) + (down * a.Y);
        Vector3 pb = origin + (across * b.X) + (down * b.Y);

        var front0 = new CurvedCorner(pa + step, normal, ua);
        var front1 = new CurvedCorner(pb + step, normal, ub);
        var back0 = new CurvedCorner(pa - step, normal, ua);
        var back1 = new CurvedCorner(pb - step, normal, ub);

        into.Add(new CardTriangle(front0, front1, back1));
        into.Add(new CardTriangle(front0, back1, back0));
    }

    /// <summary>
    /// Whether every vertex sits on one plane.
    /// </summary>
    /// <remarks>
    /// The plane is the area-weighted sum of the triangles' own normals, with <b>each turned
    /// to agree with the first</b> before it is added. GK3's scene geometry is not wound
    /// consistently — a card is routinely two quads facing opposite ways — so summing the
    /// normals as authored cancels a perfectly flat card to nothing and refuses it. The same
    /// fact is why the shell is built symmetrically about the plane rather than extruded off
    /// one side of it.
    /// </remarks>
    private static bool Coplanar(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        Vector3 origin = positions[indices[0]];
        Vector3 normal = Vector3.Zero;

        for (int at = 0; at + 2 < indices.Count; at += 3)
        {
            Vector3 a = positions[indices[at]];
            Vector3 face = Vector3.Cross(
                positions[indices[at + 1]] - a, positions[indices[at + 2]] - a);

            if (face.LengthSquared() <= 1e-12f)
            {
                continue;
            }

            normal += normal == Vector3.Zero || Vector3.Dot(face, normal) >= 0f ? face : -face;
        }

        if (normal.LengthSquared() <= 1e-12f)
        {
            return false;
        }

        normal = Vector3.Normalize(normal);

        float extent = 1f;

        foreach (Vector3 position in positions)
        {
            extent = Math.Max(extent, (position - origin).Length());
        }

        foreach (Vector3 position in positions)
        {
            if (MathF.Abs(Vector3.Dot(position - origin, normal)) > Flatness * extent)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Fits world position as an affine function of texture coordinate.
    /// </summary>
    /// <remarks>
    /// Least squares over every vertex, solved as the three-by-three normal equations. What
    /// comes out is the map this whole file runs on: <c>position = origin + across*u +
    /// down*v</c>, which turns any texel of the mask into a place on the card, and it is
    /// checked against the vertices it was fitted to rather than assumed — a card whose
    /// texture coordinates are not affine keeps the geometry it shipped with.
    /// </remarks>
    private static bool Fit(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector2> texCoords,
        out Vector3 origin,
        out Vector3 across,
        out Vector3 down)
    {
        origin = across = down = Vector3.Zero;

        Span<float> m = stackalloc float[3 * 6];

        for (int i = 0; i < positions.Count; i++)
        {
            Vector2 uv = texCoords[i];
            Vector3 p = positions[i];

            Span<float> row = [uv.X, uv.Y, 1f];

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    m[(r * 6) + c] += row[r] * row[c];
                }

                m[(r * 6) + 3] += row[r] * p.X;
                m[(r * 6) + 4] += row[r] * p.Y;
                m[(r * 6) + 5] += row[r] * p.Z;
            }
        }

        for (int col = 0; col < 3; col++)
        {
            int pivot = col;

            for (int r = col + 1; r < 3; r++)
            {
                if (MathF.Abs(m[(r * 6) + col]) > MathF.Abs(m[(pivot * 6) + col]))
                {
                    pivot = r;
                }
            }

            if (MathF.Abs(m[(pivot * 6) + col]) < 1e-9f)
            {
                return false;
            }

            if (pivot != col)
            {
                for (int c = 0; c < 6; c++)
                {
                    (m[(col * 6) + c], m[(pivot * 6) + c]) = (m[(pivot * 6) + c], m[(col * 6) + c]);
                }
            }

            float lead = m[(col * 6) + col];

            for (int c = 0; c < 6; c++)
            {
                m[(col * 6) + c] /= lead;
            }

            for (int r = 0; r < 3; r++)
            {
                if (r == col)
                {
                    continue;
                }

                float factor = m[(r * 6) + col];

                if (factor == 0f)
                {
                    continue;
                }

                for (int c = 0; c < 6; c++)
                {
                    m[(r * 6) + c] -= factor * m[(col * 6) + c];
                }
            }
        }

        across = new Vector3(m[3], m[4], m[5]);
        down = new Vector3(m[9], m[10], m[11]);
        origin = new Vector3(m[15], m[16], m[17]);

        if (!float.IsFinite(across.X) || !float.IsFinite(down.X) || !float.IsFinite(origin.X))
        {
            return false;
        }

        for (int i = 0; i < positions.Count; i++)
        {
            Vector2 uv = texCoords[i];
            Vector3 fitted = origin + (across * uv.X) + (down * uv.Y);

            if ((fitted - positions[i]).Length() > FitTolerance)
            {
                return false;
            }
        }

        return across.LengthSquared() > 1e-12f && down.LengthSquared() > 1e-12f;
    }

    /// <summary>A texel index wrapped into the texture, for negative coordinates too.</summary>
    private static int Wrap(int value, int size) => ((value % size) + size) % size;
}

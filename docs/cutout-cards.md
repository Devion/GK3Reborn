# Railings, fences and chains

Gabriel Knight 3 draws a railing as a picture of one on a single quad, with the gaps between
the balusters cut out of the magenta colour key. From in front it is convincing; from
anywhere else it is a sheet of paper. This gives those cards the thickness the thing drawn
on them would have had — at load, from the holes in the texture, with no content to build
and nothing to pack.

```bash
# Look at a railing, both ways.
dotnet run --project tools/GK3Reborn.Tools -- render-scene --source ../GK3/Data \
    --model LBY --camera STAIRS_LEFT --output thick.png
dotnet run --project tools/GK3Reborn.Tools -- render-scene ... --no-thick-cards \
    --output flat.png

# And as it ships, with the packs, where the textures arrive block-compressed.
dotnet run --project tools/GK3Reborn.Tools -- render-scene ... --packs ../ContentWorkspace

# What a room got. The line is absent when a room has no cutout cards in it.
GK3Reborn.exe --scene RC4 --frames 2 --data ../GK3/Data --timings
#   Railings: 8 keyed cards thickened to 1.88-4 units, 4390 triangles
```

Nothing here reads a texture's name to decide what to do. `CutoutCards.Leaves` is the one
exception and it is discussed below.

## What it is, and what it is not

**It is not the improved scene geometry.** That pass (`scene-geometry.md`) replaces whole
objects with meshes somebody bevelled in Blender, and it ships them in the ReBarn. This one
builds a few hundred triangles per room at load out of information the game already has, and
adds nothing to any file. The two are independent settings and either works without the
other.

**Two different things in this game are called a rail, and only one of them is here.**
`CS3STAIRRAIL`, `LBYSTRRAIL01`, `RC1IRONFENCE`, `CSEGATEFRAME`, `MCBFENCE` and the rest are
keyed cutouts: the balusters are drawn and the gaps are holes. `RC1RAIL`, the balustrade
along the RC1, MOP, RC3, RC4 and MA3 balconies, is **not keyed at all** — it is a solid card
with a balustrade painted on it in dark paint, and you cannot see through it today. Nothing
here touches it. Making that one three-dimensional means inferring where the artist meant
the gaps to be from how dark the paint is, which is a guess rather than a measurement.

Across the corpus, 45 of the 81 locations have cutout cards in them: 515 cards and about
75,000 triangles in total, some 14 ms of a room's load at the median and 99 ms at the worst
(MCB, whose chain-link fence is the single card that most needs this and the most expensive
to build).

## The measurement, which decides everything

One number does all the work: **how wide the bars are**, taken from the texture's alpha.

The mask is the texels the colour key does not remove. A distance transform gives every
drawn texel its distance to the nearest hole, and the texels no nearer a hole than any of
their neighbours are the *spine* of whatever they are part of — twice that distance is that
thing's width. Taking the spine rather than every drawn texel is what makes the answer the
width of a feature instead of an average over area: a stair rail is mostly handrail by area
and mostly baluster by spine, and it is the balusters that need a thickness. The low
quartile, not the median, because a card with one thick member and a dozen thin ones should
be measured by the thin ones.

That number answers both questions:

- **Is this a lattice of bars at all?** As a share of the texture's shorter side, the things
  worth thickening measure 0.008 to 0.25 — `MCBFENCE` 0.008, `CS3STAIRRAIL` 0.031,
  `RC1IRONFENCE` 0.063, `MS3MUSWIN` 0.22, `CHAINS` 0.25. The things that must not be touched
  measure 0.55 to 1.0 — `WOODROCK` 0.55, `CHESTDRWERS` (a drawer front with a keyhole) 0.86,
  `LIGHTBULB` 0.86, `RL1_SCRAPE01` (a scrape on the pavement) 0.89, `RC1BOOKSHOP` 1.0.
  Nothing in the game falls in the gap, so the threshold at 0.35 has no case near it either
  side, and a mostly-opaque card with a hole punched in it is rejected without anybody
  having to name it.
- **How deep?** The bar's width in texels, through the card's own texture-to-world scale.
  `CS3STAIRRAIL`'s balusters are four texels across on a card where a texel is a quarter of
  a unit, so they come out about a unit thick — 2.4 cm, at 72 units to a character. The
  median across the corpus is 1.2 units and it is clamped to [0.3, 4].

## The shell

The card becomes its own triangles moved half a thickness one way, a mirrored copy moved
half a thickness the other, and a rim joining the two around the outline the key cuts.

**The rim is the part that matters.** Two parallel cutout planes with nothing between them
read as a ghost of a railing from any oblique angle — worse than the flat card, not better.
So a card whose rim comes out empty is left exactly as it was.

**Half a thickness each way, never one thickness one way.** Which side of a card is its
outside is not in this data — GK3's winding is not consistent enough to ask, which is the
same fact `CoplanarCards` exists because of — and extruding the wrong way lifts a rail off
its posts. Moving symmetrically about the plane the artist placed makes the question go
away. This is the exception to "a flat card cannot be given thickness" in
`scene-geometry.md`, and the alpha channel is what makes it one: for a keyed card the
silhouette is stated rather than guessed.

**The rim is built from merged runs of texel edges, not from a traced contour.** These
outlines are balusters, bars, wires and mullions, which are axis-aligned in texture space
almost without exception, so merging the runs along each row and column reproduces them
exactly — a baluster's whole side is one quad. A traced-and-simplified contour would have to
be clipped against the card's own footprint, and the footprint is a third of a tile on a
stair rail. What a run misses is the diagonal, and a diagonal comes out as steps one texel
high: six millimetres at the scale these are drawn at.

**Both corners of a rim quad take the texture coordinate of the drawn texel beside them**,
not of the edge itself. An edge coordinate sits exactly on the boundary the key cuts, where
the shader's own alpha test is as likely to throw the wall away as to keep it. Half a texel
inside is unambiguously painted, and it is the colour of the very baluster the wall is the
side of.

## Five things about this data that broke the obvious implementation

- **The game ships two of every texture, and the limit was calibrated on the wrong one.**
  The enhanced set is the 1999 drawing at eight to thirty-two times the resolution:
  `CS3STAIRRAIL` is 128 square in the barns and 2,048 square in the packs, so a baluster four
  texels wide is seventy-four texels wide in a shipped build. A limit counted in texels
  therefore rejected every railing in the game the moment the content packs were installed —
  and rejected it *silently*, because every render made without the packs went on showing the
  pass working perfectly. The limit is a share of the texture now, which is the same number
  for both.
- **A packed texture reaches the cache down a different path, and that path never keyed
  anything.** The packer leaves out only the keyed textures whose enhanced replacement did
  not carry the key across as alpha; the ones that did are packed as BC7 with a real alpha
  channel. So a railing in a shipped build arrives as blocks, and the mask has to be taken by
  expanding one level of them.
- **Not the largest level.** Expanding a 2,048-square base colour costs some forty
  milliseconds and sixteen megabytes to answer a question about a silhouette drawn at 128.
  Taking level zero put a second and a quarter on a room's load.
- **A ceiling on the mask is not enough, because the upscale enlarges a small texture most.**
  `CHAINS` is 16 by 32 in the barns and 512 by 1,024 in the packs; stopping at 256 leaves a
  mask eight times finer than anything ever drawn, and a card tiling it down a terrace is
  then a grid of two million texels for the rim to walk. The mask is halved while its bars
  stay at least four texels wide instead, and where that stops is the resolution the outline
  was authored at — it is never told what that is and lands on it anyway: `CHAINS` back to
  exactly 16 by 32, `MCBFENCE` to 256 square, `RC1IRONFENCE` to 64 by 128, `LBYSTRRAIL01` to
  128 by 256. It took the worst rooms from 176 ms to 26.
- **A tiled card measured tile by tile grows a wall across every seam.** A railing tiles its
  texture seven times along a balcony and the bar running off the right of one tile continues
  into the left of the next. The mask is scanned in one grid spanning every tile the card
  covers, with the lookup wrapped into the texture and the footprint tested against the
  card — so there are no seams to get wrong.

## What is named rather than measured

`CutoutCards.Leaves` is thirty-three texture names, and it is the one thing the measurement
cannot answer. A leaf's edge is a smooth curve, which merges into long straight runs of a
two-texel feature, so a maple sprite measures *straighter* than the hotel's wrought-iron
balustrade does. That was worth checking rather than assuming, and it settles the matter:
made and grown things are not separable by this geometry.

Two reasons to leave them, applying to different halves of the list. The trees — `PINE2`,
`MAPLE`, `TREE00` — are replaced outright by `Foliage`'s grown geometry, so thickening one is
work done on triangles about to be thrown away. The bushes, vines and hillside strips are not
replaced, and are left because a hard lit rim around a leaf silhouette reads as cardboard,
which is the opposite of what this is for.

The list was taken from the material library's own foliage class, less the four it puts there
wrongly: `RC1IRONFENCE` and `CHUFENCE` are green because they are painted green and
overgrown, and `RC1LANTERNSCROLL` and `DINFIREPLACE` are ironwork.

## Budgets

**A budget reduces the treatment; it does not discard it.** Over 800 rim quads for one
surface, the shortest run worth building is raised by half and the rim filtered again, so
what a card loses is the stipple between its bars and never the bars. Refusing instead would
refuse the chateau's chain-link fence, which is the card that most needs this.

Below that there is a floor on what is worth building at all: a rim facet shorter than two
scene units — five centimetres — cannot be seen at the distance GK3 puts its camera, and
dropping those leaves gaps in the rim the same size as the facets they replace. It is
expressed in units and not texels on purpose, because it is a claim about what the player can
resolve: a texture tiled forty times along a fence has texels a twentieth the size, and the
rim should be coarser there in exactly that proportion. It is the difference between this
costing 84,000 triangles across the corpus and 225,000.

## Where it plugs in

`SceneGeometry.ThickenCards` runs **first**, ahead of both `Replace` (the improved geometry)
and `RoundOff` (the loader's rounding), and claims its surfaces through the same `emitted`
set they use. It has to be first: a railing is one surface inside an object that is otherwise
a staircase or a wall, and both of those passes work on whole objects, so whichever reached
the object first would claim the rail with it and go on drawing the flat card. Both of them
skip what this has already claimed — without that they draw the flat card *through* the
thickened one, exactly coincident, which is the depth fighting `CoplanarCards` exists to
stop.

Taking the card from the room's own polygons rather than from the overlay loses nothing: a
surface on one plane has no edge to bevel and no curve to recover, so the Blender pass leaves
it exactly as it found it.

Lightmap coordinates come free, because the engine derives them from the texture coordinate
and the surface's own mapping rather than storing them per vertex — so a new rim vertex is
lit like the card it belongs to, and the room's bake is untouched.

## The shadow, added 2026-09-02

**For its first day this pass changed no shadow in the game, and that was by design.** Every
card it touches is keyed; the acceleration structure is built with every triangle opaque and
there is no any-hit shader to ask whether a hit landed on a baluster or on the gap beside it,
so keyed geometry was kept out of it altogether — a railing in the structure would have cast
the shadow of a wall. Giving a rail sides to be seen from therefore left it with nothing for
the sun to be stopped by, and it went on casting exactly what a flat card cast, which is
nothing.

**The alpha test the missing shader would do per hit is done at load instead.** The mask is
already decoded and already measured — it is what the rim is built from — so the drawn texels
are merged into as few rectangles as cover them and each becomes two opaque triangles. What a
ray hits is then the bars and not the gaps, which is the whole of the question, and it costs
no shader and no pipeline. `CutoutCards.Shadow` builds them; `ThickCard.Occluders` carries
them; `SceneGeometry.ThickenCards` puts them in the structure.

The shell and the occluders are two renderings of one outline and neither is the other. The
shell is drawn and never traced; the occluders are traced and never drawn.

- **On the plane, not at either face.** The shell straddles the plane by half a thickness
  each way and the occluder lies flat on it, where the card has always been. Two planes would
  double the cost to widen a shadow by the width of a baluster against a sun tens of thousands
  of units away, and one plane between the two faces cannot shadow either of them: a shadow
  ray leaves a face along its own normal, away from the plane behind it.
- **Greedy rectangles, not a quad per texel.** A baluster forty texels tall and four across
  is one rectangle. Over the corpus the merge is worth about thirty to one, and it is what
  makes this affordable: a card whose shell is two thousand triangles casts its shadow with
  forty.
- **Over budget it coarsens, and coarsens downwards.** `MostShadowQuads` is 2,000, which
  nothing but Montségur's razor wire and the lobby's stair rail ever reaches. Past it the
  grid is halved keeping a cell three of whose four texels were drawn — not a majority, which
  is what `CutoutMask` takes when it is measuring how wide a bar is drawn. A chain-link fence
  is exactly half drawn, so a majority makes every one of its cells solid and the fence casts
  the shadow of a wall; rounding down loses a thin bar instead, and a bar that goes missing
  casts what it cast before this, which is nothing.

### It is not the room's own occlusion, and that is the half that is easy to get wrong

The composite credits the room's occlusion against the 1999 bake and the two cancel exactly:
block a light with room geometry and `residual` rises by what `arrived` lost. That is right
for a wall, because the artists' lightmap already holds its shadow. It is wrong for a railing.
**A 1999 bake cast no alpha-tested rays either**, so a keyed card is in the lightmap as its
whole quad or as nothing, and never as a fence — there is nothing there to double-count. The
occluders therefore carry `TracedWorld.UnbakedMask` in a part of their own and are traced with
the models, in the half of the shadow term the composite spends rather than the half it
subtracts.

Measured, on the lobby stairs, whose lightmap is the whole of the light in the room: the
room's mask changes 0.06% of the frame and the deepest shadow is ten steps of an eight-bit
channel; the unbaked mask changes 0.16% and reaches thirty-four. Outdoors on RC1 the two are
much closer — 156 against 208 — because the rig outruns the bake there and `residual` is
clamped at nothing for most of the frame.

The occluders' instance also disables face culling. They are single-sided patches wound
whichever way the artist wound the card they were fitted to, and the ray that most needs to
hit one is a shadow ray leaving a character, which asks for back faces to be culled so that a
person's own shells do not shadow them. An instance flag overrides a ray flag, so the shells
stay skipped and the fence still stops the light.

### What it costs

| | RC1 | MCB | LBY | POU | CHU | RC2 | CS3 |
|---|---:|---:|---:|---:|---:|---:|---:|
| occluder triangles | 24,840 | 58,310 | 22,382 | 21,240 | 7,644 | 13,180 | 4,232 |

Against 350,000 traced triangles in RC1, and it is the only room where the pass is a
measurable share of the structure. **Load time and frame time are both inside the noise**:
RC1's thickening pass measures 101 ms with the occluders and 99 ms without, MCB 278 against
281, and 400 frames of RC1 at High present at 123 fps against 124. The merge walks the same
grid the rim already walks, so the geometry is very nearly free once the mask exists.

Both backends agree exactly. RC1 at `SIGN_POST`, 112P, High: 5.712% of the frame changed and
a deepest shadow of 208, through Direct3D 12 and through Vulkan alike.

## Settings and switches

| Where | Switch | Effect |
|---|---|---|
| Game | Video → Solid railings and fences | `Settings.ThickCutoutCards`, on by default |
| `GK3Reborn.exe` | `--no-thick-cards` | Every card as the flat quad it shipped as |
| `render-scene` | `--no-thick-cards` | The same, for comparison shots |
| `GK3Reborn.exe` | `--no-card-shadows` | Keep the thickness, let the light through |
| `render-scene` | `--no-card-shadows` | The same: the A/B for the shadow alone |

The two switches are separate on purpose. What is drawn and what is traced are two different
sets of triangles built from one silhouette, so a picture in which a fence looks right and
shades wrongly says which of the two to go and read.

The setting takes effect at the next door, like the trees and the improved geometry, and for
the same reason: it also gates the measurement, which happens as a room's textures are
uploaded.

## What this does not do

- **It cannot help an opaque card.** See `RC1RAIL` above. The silhouette has to be stated in
  the alpha; where it is only implied by the painting, this has nothing to measure.
- **It does not model anything.** A baluster comes out as a bar of the width it was drawn,
  with square corners and no turning. It is the difference between a rail with sides and a
  rail without, not between a 1999 rail and a modelled one.
- **A diagonal is a staircase of texel steps.** True of every outline that is not
  axis-aligned in texture space — `RC1IRONFENCE`'s finials, `LBYSTRRAIL01`'s scrollwork. At a
  texel to a step and six millimetres to a texel this is below what the picture on the same
  card can show, but it is what a traced contour would have improved.
- **It changes no lighting.** New geometry is lit by the room's existing bake through the
  surface's own lightmap mapping. What it does now change is the shadow; see above.
- **A card's shadow is as coarse as its silhouette.** The occluders are whole texels, so a
  diagonal casts the same staircase it draws, and a card coarsened by the budget casts an
  edge stepped by two texels rather than one. At the distance GK3 puts a fence from the wall
  behind it, that is softer than the penumbra already on it.

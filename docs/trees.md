# Trees

Every tree in Gabriel Knight 3 is a picture of a tree on a flat quad. This replaces the
ones worth replacing with modelled trees, grown to the size the artist drew.

```bash
# 1. Draw the foliage the trees are dressed with.
python tools/foliage/make_cards.py --workspace path/to/ContentWorkspace

# 2. Grow the trees.
blender --background --factory-startup --python tools/blender/grow_trees.py -- \
    --workspace path/to/ContentWorkspace [--variants 4] [--far 3]

# 3. Look at one.
dotnet run --project tools/GK3Reborn.Tools -- render-model --source ../GK3/Data \
    --model ../ContentWorkspace/enhanced/trees/spruce_00.glb --output spruce.png

# 4. Look at a room, both ways.
dotnet run --project tools/GK3Reborn.Tools -- render-scene --source ../GK3/Data \
    --model LHM --workspace ../ContentWorkspace --enhanced enhanced/textures \
    --output with.png
dotnet run --project tools/GK3Reborn.Tools -- render-scene ... --no-trees --output without.png

# 5. Look at the wind, by rendering the same shot at two points on its clock.
dotnet run --project tools/GK3Reborn.Tools -- render-scene ... --wind 0   --output still.png
dotnet run --project tools/GK3Reborn.Tools -- render-scene ... --wind 1.6 --output moved.png
```

Everything lands in `ContentWorkspace/enhanced/trees`, outside the repository, beside the
enhanced textures. The engine finds it there; `Settings.ModelledTrees` and the video menu's
**Modelled trees** switch it off, and so does `--no-trees` on a render.

## How much of the game is flat trees

| where | how many cards | what happens to them |
| --- | ---: | --- |
| Foliage props named by scene files | 431 placements | replaced |
| Room objects that are **only** foliage | 3,790 in 64 objects | replaced, budget permitting |
| Room objects that are foliage **on a modelled bole** | 77 objects | replaced whole, bole included |
| Room objects mixing foliage with anything else | the rest | left flat |
| Background strips — `TREEGROUP01`, `TILEDTREES` | the remainder | left flat |

**A card is the face an artist drew, not a polygon.** Counting polygons gives 43,136, and
that number is wrong by seven and a half times: a room has been through a BSP splitter, so
one 320-unit spruce card in LHM arrives as five polygons, sliced across at whatever heights
the tree planes happened to cut it. LHM has 1,023 polygons and 190 drawn faces. Everything
here works from surfaces — see *One card, however it was cut*, which is where getting this
wrong showed up on screen.

Most of what the rooms draw turns out to be the **same trees the scene files place as
props**, drawn a second time. So the room pass adds less than its card count suggests:
across the twenty-five outdoor scenes measured it contributes 24 trees, sixteen of them in
BAL and two in LHE, where the room carries trees no prop does. Worth having for those, and
not where the bulk of the foliage is.

## Leaves on a modelled bole are one tree, and the bole goes too

`rc1_vegitation` is the maple standing beside the bench outside the hotel: a bole painted
in `Woodbark`, leaf cards in `maple1trileaf`, and nothing else. `RC1_HOTELTREELEAVESFF` is
a flat `MAPLESIDE1` card of **the same tree**, placed by the scene file a few units away.

Refusing the room's object because bark is not foliage meant the room went on drawing its
1999 trunk while the prop grew a modelled tree with a trunk of its own beside it — two
trunks through each other, under a crown of flat cards that had not gone anywhere. That is
the shape of the bug, and it is not one room: **77 objects across the corpus mix foliage
with something, and 108 of those mixtures are one of four bark textures** — `NewBranch`
38, `Woodbark` 33, `Trunk01` 26, `Trunk02` 11. What is left over is bushes and buildings,
and those still refuse the object.

So an object that is foliage and bark and nothing else is a **whole tree**. Its cards
cluster into crowns as usual; each bole is then claimed by the crown standing over it —
under the crown's own spread, and rising into the bottom half of it — and what replaces
the pair is fitted to both boxes together, so the tree stands on the ground the bole stood
on instead of hanging where the leaves were. Bark that no crown claims is somebody else's:
a fence sharing an object with a tree keeps its wood, and the tree is still replaced.

Where a scene file also places a card of the same tree, **the prop is still what gets
grown** — it is the thing the scene placed, with whatever noun and script belong to it —
but it is fitted to the *room's* measurement, which is the only one of the two that knows
where the ground is. The room's own copy is then suppressed by the rule that already
suppressed it, because the two answers are now identical.

An object that mixes foliage with a wall still cannot be touched, and the reason is what
it always was: what would have to be drawn in the wall's place cannot be worked out from
here.

## A crown drawn in pieces is still one tree

The clustering was written against a *conifer*: two or three cards crossed at the trunk,
touching, agreeing on where their centres are to within three units. A 1999 **broadleaf**
is drawn nothing like that. RC1's hotel maple is three horizontal discs stacked up the
trunk — 284, 172 and 115 units across — with gaps of six and twelve units between them, and
fourteen small side sprays hung off the branches out to fifty units from the middle.

Clustered by the conifer's rules that comes out as **three trees**: the two upper discs
fail the "overlapping in height" test, start clusters of their own, and grow trees hanging
in the air over the first with boles of their own. It is the same picture the
polygon-clustering bug produced, arriving by a different route, and it is what the RC1 tree
looked like. Two rules fix it, and the second is the interesting one:

1. **Within reach above and below, not touching.** A crown is about as tall as it is wide,
   so the vertical gap is allowed the same reach as the horizontal one. Six units of gap on
   a card 284 across is one tree; the terrace above is a couple of hundred units up *and*
   off to one side.
2. **A bole is what licenses the rest.** The side sprays are too far from the widest disc's
   centre for any reach that does not also gather a stand of spruces into one spruce six
   trees wide. What settles them is the trunk: it says where one tree stands and how far up
   it goes, so a crown standing inside it belongs to it. Crowns with no bole under them —
   the conifer stands — are left exactly as the clustering found them.

Measured across the corpus: **922 crowns become 819**. All 103 folded in are pieces of a
tree that has a bole; the 618 conifer crowns, which have none, are untouched. `WOD` reports
24 trees where it used to report 36, and the twelve that went were never there.

## One card, however it was cut

This is the one mistake in the feature that reached a screenshot, and it is worth writing
down because the data invites it.

Clustering **polygons** rather than surfaces turns a single tree into half a dozen. Four of
the six are slices taken from partway up the trunk, so each grows a tree of its own, with
its own bole, starting in mid-air out of the middle of the real one. WOD reported 87 trees
where 18 stand; LHM reported 162 where 33 do.

The fix is two pieces of principle:

1. **Rebuild the drawn face first.** Polygons are grouped by `SurfaceIndex` and their bounds
   unioned. A surface is what the artist drew — it is what carries the texture and the
   lightmap chart — and the splitter changes neither where it is nor how big it was.
2. **Measure a cluster from its seed card, never from a running centre.** Against a moving
   centre a cluster walks: each card it takes shifts the middle a little, the next one is
   then in range, and a stand of six spruces becomes one spruce six trees wide.

Reconstructed, the clustering barely has to be clever: the cards of one tree are crossed at
its trunk and their centres agree to within about **three units**, where the trees stand a
couple of hundred apart. Two tests hold it — one asserting that a card cut five ways gives
the same single tree as an uncut one, and one asserting that every tree a room grows stands
on the ground its card stood on.

## A card is measured, not guessed at

The picture on a card is the artist's whole description of the tree — how tall, how wide,
which species — so nothing here invents a tree and hopes. `Foliage.SiteFor` and
`Foliage.InGeometry` measure the box the cards occupy and grow something to fill it.

**Species comes from the texture, never from the name.** The names do not agree with each
other and the textures do: `WOD_BIGDTREEFF`, `CSE_FFTREE03` and `PL6_FFTREE01` are the same
broadleaf under three conventions, and all three draw `TREE00`.

| species | stands in for | grown as |
| --- | --- | --- |
| `spruce` | `PINE2`, `PINE2FLAT`, `TALLPINE`, `ARMPINE`, `TREE03`–`TREE05` | crown only |
| `cypress` | `TREE06` | crown only |
| `broadleaf` | `TREE00`, `TREE01`, `TREE02`, `BUSHYTREESIDE1/TOP1` | whole tree |
| `maple` | `MAPLESIDE1`, `MAPLETOP1`, `MAPLE`, `maple1trileaf` | whole tree |
| `darkbroadleaf` | `WOODTREE3`, `MAGENTREE` | whole tree |

`maple1trileaf` is in that table under `maple`, and it is worth naming: it is leaves on
real geometry rather than on a card, and it is what the rooms paint their modelled maples
with — RC1's hotel tree, CEM's three, RC2's and RC4's. Twenty-two objects draw it.

**The conifers are grown as a crown and not as a tree**, and that is what removes the need
to know where the ground is. `PINE2` is a *leaves* card: the rooms that place it draw the
trunk themselves — WOD's ten pines stand on the ten trunks of `wod_pinetrunks`, one per
card and within two units of it — so a spruce that brought its own bole would put a second
one through the first. `TREE00` draws a whole tree, trunk included, and its box is the
whole tree's.

Every tree is grown **normalised**: trunk base at the origin, exactly one unit tall. The
engine scales that to the card's box, nudges the width towards the card's own within
±25/35%, and turns it about the vertical by an amount taken from where it stands. So a
species is grown seven times and stands in sixteen hundred places, and a wood comes out the
same on every load — which is the only thing that makes two renders of one room comparable.

## The rooms and the props overlap, and both were drawn

A room's `*shadowcasters` object is a **second copy** of the trees the scene file also
places as props. WOD draws its ten pines twice, once in `wod_treeshadowcasters` and once as
ten `_pineleavesff` models, and the original engine drew both — the room's copy carries the
`IgnoreLightmapFlag`, so it renders at full brightness beside a copy the bake has lit.

Two flat cards in the same place are a slightly thicker tree. Two *modelled* trees in the
same place are a mess, so the props win and the room's copies of them are left out. The
props are planted first and the room's woods afterwards, skipping any site a prop already
covers.

**The rule for "already covered" is a measured number, not a cautious one.** Across WOD's
eighteen pines — crowns two hundred units across — the room's own copy is within **22 units
and usually within 5**. A third of a crown's radius catches every one of them. The looser
rule this replaced took a whole radius, which suppressed 81 of WOD's 87 stands to remove 18
duplicates and turned the wood into a clearing.

## The budget, and why there are two detail levels

No room in the corpus comes near the budget now that a card is counted properly, so it is a
guard rather than a constraint. It is kept because the arithmetic behind it is still true —
a stand of a hundred and sixty trees at four thousand triangles each is six hundred thousand
triangles of scenery behind a conversation, in a room that shipped at 5,853 — and because a
scene nobody has looked at yet should not be able to spend that.

So each species is grown twice. The **far** tree keeps the silhouette that says which
species it is — the count of whorls, the taper of the crown — and gives up the detail
inside it, which is the part that stops being visible first.

| | near | far |
| --- | ---: | ---: |
| spruce | 10,150 | 920 |
| cypress | 11,190 | 1,110 |
| broadleaf | 19,280 | 3,890 |
| maple | 18,230 | 2,960 |
| darkbroadleaf | 21,320 | 3,890 |

The near figures roughly doubled when the leaves became bowed patches rather than quads,
and the far figures did not move: a far tree is grown flat, because four times the
triangles for a curve across a clump is four times nothing at a hundred metres.

A room grows every stand it can afford at the far detail — all of an object or none of it,
since a room is hidden by name — and spends what is left raising the **tallest** trees to
full. Tallest rather than nearest, because there is no camera at load time and height is
the only thing in the data that says which tree a room is about. `SceneLoader.WoodBudget`
is 400,000 triangles; what it will not stretch to is said in the log rather than left
silent, because a silent cap reads as "there was no more foliage" when it is not.

Measured on the reference installation, with everything else enhanced:

| room | trees grown | of those, from the room | triangles, flat → grown |
| --- | ---: | ---: | --- |
| CSD | 38 | 6 | 1,840,950 → 2,235,142 |
| LHM | 34 | 2 | 1,812,015 → 2,205,673 |
| RC4 | 28 | 0 | 1,834,499 → 2,137,317 |
| PL1 | 27 | 1 | 1,778,078 → 2,097,440 |
| WOD | 24 | 6 | 1,872,674 → 2,175,017 |
| BAL | 22 | 16 | 101,689 → 393,933 |

Those totals are the whole scene, floor displacement included, which is why they are
millions: the wood itself is the difference, between three and four hundred thousand
triangles. A tree costs about twice what it did before the leaves were bowed, and the
budget still has room in every room in the game.

## Packing

`enhanced/trees` is the one source directory that feeds three pack kinds, and it has to be:
a grown tree is geometry, the foliage it is painted with, and a manifest saying which is
which. Splitting them into three directories to suit the packer would put a tree's parts
three places apart for no reason a person would recognise.

| what | kind | how |
| --- | --- | --- |
| `*.glb` | `Model` | verbatim, deflated |
| `*.json` | `Manifest` | verbatim, deflated |
| `*.PNG` | `Texture` | encoded to BC7, like any other colour texture |

```bash
# the whole plan, trees included
dotnet run --project tools/GK3Reborn.Tools -- pack-content --workspace ../ContentWorkspace

# just the trees, after regrowing them
dotnet run --project tools/GK3Reborn.Tools -- pack-content --workspace ../ContentWorkspace     --only enhanced/trees --output build/pack --single-volume
```

`--only` exists because filtering by *kind* cannot reach the trees on their own: any filter
that catches them also drags in every enhanced texture in the game, and re-encoding six
thousand of those to check that a tree packed is an hour. Packed on their own the trees are
**42 entries and 25.7 MB** — 35 GLBs, 5 cards, 2 manifests. It was 11.7 MB before the leaves were bowed and the cards grew their occlusion tiles.

The cards go through the ordinary texture encoder on purpose. Packed as `Texture` under
their own names, the scene loader finds `RBN_SPRUCE_SPRAY` through the compressed-texture
path it already had, without anything in it knowing that the name belongs to a tree.

`render-scene --packs DIR` renders from volumes, which is how the packed path is checked
without installing anything.

## If the trees are not there, the sprites are

Nothing about this is required. The order of supply is loose directory, then packs, then
nothing — and **nothing is a complete answer**: an empty library leaves every foliage card
exactly where the game put it, and the room draws its 1999 trees.

That has to hold for a *partial* set too, and it is arranged rather than hoped for:

- **A manifest naming trees the pack does not carry** offers no species at all. The
  geometry is looked for before a species is offered, because a species offered and then
  not delivered takes cards away and puts nothing in their place.
- **A stand whose trees will not parse stays flat, all of it.** A room is hidden by object
  name, so half a stand is not an option; the geometry is read *before* the object is
  committed to rather than while planting it. Corrupt every spruce in the set and a room
  reports how many stands it left flat and draws its cards instead — the same picture as no
  trees at all, plus a warning naming each bad file.
- **A prop whose tree will not load** keeps its card, one prop at a time.

Measured: with no packs and no workspace, WOD draws **9,561 triangles**. With a pack
holding the manifest but no geometry, WOD draws **9,561 triangles**. With the full pack,
**88,653**.

## The foliage is drawn, and its colour is measured

`tools/foliage/make_cards.py` writes one RGBA card per species — four of it, in fact,
tiled two by two at four brightnesses; see *A crown is a volume*. **It is not a crop of the
original sprite**, and the first attempt at this proved why: a GK3 tree sprite is a whole
tree seen from one side, so a rectangle cut out of the middle of it is almost entirely
opaque. Every card rendered as a solid green box with a hard edge and a spruce built from
two hundred of them was a heap of boxes. Foliage has to be mostly holes — these are 29–36%
opaque.

So the shapes are drawn and the **colours are taken from the sprite**, in the proportions
the sprite uses them: a spruce sprite is mostly two dark greens with a scatter of pale
highlights, and drawn from an unweighted palette the highlights come up one time in
twenty-four and the tree turns out grey. Then the finished card is scaled until its average
colour *equals* the sprite's:

| card | drawn | sprite |
| --- | --- | --- |
| `RBN_SPRUCE_SPRAY` | 48, 60, 48 | 48, 60, 48 |
| `RBN_BROADLEAF_CLUMP` | 68, 77, 43 | 69, 77, 44 |
| `RBN_MAPLE_CLUMP` | 30, 48, 16 | 31, 48, 17 |
| `RBN_DARKBROADLEAF_CLUMP` | 4, 25, 0 | 4, 25, 0 |

That is not fussiness. The lightmaps, the skyboxes and the forty thousand cards still on
the hillsides were all authored against those greens, and a modelled tree mixed fresh reads
as a tree from another game standing in this one. The measure is the test: a grown spruce
should sit in a stand of unreplaced cards without anybody being able to point at which
trees were changed.

The cards ship in `enhanced/trees` and the loader looks there before the archives, because
`RBN_SPRUCE_SPRAY` is a new bitmap rather than a better version of an old one.

## A crown is a volume, and it took four things to stop being a heap of stickers

The first modelled trees read as *sprites on a stick*: flat green plates at hard angles,
with a flicker running through the crown whenever the camera moved. Four changes between
them are what turned that into a mass of leaves, and each is worth its cost for a different
reason.

**A leaf is bowed, not flat.** Every clump is a three-by-three patch pushed out along its
own normal into a shallow dome or saddle. It catches light across itself rather than all at
once, which is what a flat card can never do — and because no two bowed patches can lie in
the same plane, it is also what stopped the flicker.

**The flicker was coplanar cards, and they were coplanar by construction.** The old code
built a clump's frame from the twig it hung on: its long axis along the branch, its other
axis at right angles to that, spun about the branch. So every clump on one twig lay in a
plane *containing* that twig, and two of them a half turn apart at the same point on the
branch were exactly coplanar, drawn over each other, fighting for the same depth. A leaf
now faces where it is asked to face — out of the crown — with the spread and the roll drawn
independently, so two leaves cannot share a plane however they land.

**Leaves face out of the crown rather than along the twig.** Facing along the twig points
half of them back into the tree. Facing outward gives the mass a shell that catches the
light and an inside that does not, which is most of what makes a crown read as a volume.

**A limb is clothed, not left bare.** Bark is pale where a leaf card is dark, so a limb
running out through a crown reads as a stick pushed into a bush — which is what a broadleaf
looked like from ten feet away. Clumps are hung along the whole length of every limb rather
than at its ends, enough of them to cover it, because clothing a limb is cheaper than
shading one: there is nowhere to put a per-vertex occlusion on the bark either, and a real
limb inside a crown is not visible at all.

**Occlusion is baked into the picture, because there is nowhere else to put it.** A crown
is dark at its heart and bright at its shell. The engine's vertex is position, normal and
one texture coordinate, and widening it costs eight bytes on every vertex of every room —
so `make_cards.py` draws each card **four times over at four brightnesses, tiled two by
two**, and the generator gives each leaf the tile its own occlusion earned. What is
measured is sky rather than density: for every leaf, the neighbours standing between it and
the sky, counted by how directly overhead each one is. A leaf on top of the crown has
nothing above it; one in the heart has forty clumps over it; and the underside of the
canopy darkens on its own, which plain density never gives, because a leaf on the bottom of
a crown has as many neighbours as one on the top. Four steps is not crude: the gradient a
crown needs is between its shell and its heart, and inside one twelve-centimetre clump
there is nothing to resolve.

The occlusion factors are centred rather than capped at one — 1.15, 0.92, 0.72, 0.55 —
because the shell of a crown catches more light than the flat card ever did, and a set that
only darkens comes out duller than the sprite it replaces. Weighted by how many leaves land
in each tile, the atlas still averages to the sprite's own colour, which is the measure
that matters.

## Leaf cards are lit as a mass, with the clumps still showing in it

A leaf card's own normal is the wrong answer twice over. Nothing is back-face culled, so
half the cards in any crown are seen from behind and would shade as though lit from the far
side; and a crown of a thousand quads at a thousand angles reads as a heap of litter rather
than as one mass with a lit side and a shaded one.

`grow_trees.py` writes custom split normals pointing **out of the crown centre**, with a
little upwards in the mix so the underside is shaded rather than black. It is the trick
every foliage shader has used since trees stopped being sprites and it costs nothing at
run time.

Taking the crown's normal and *nothing else*, though, makes the mass so smooth that the
clumps inside it disappear and a broadleaf comes out as a green sphere. So each patch keeps
a share — a little under half — of its own bowed normal, flipped where it faces into the
tree. The crown has clumps in it again without losing its shape.

## Foliage moves

The leaves of a grown tree sway, and nothing else in the game does.

The displacement is applied in the model's own space, *before* the transform that places
the tree. A grown tree is normalised — base at the origin, exactly one unit tall — so how
far up its own height a leaf sits is the whole of what the shader needs, and one amplitude
moves a four-hundred-unit maple and an eighty-unit shrub by the right amount each. Two
waves at frequencies that do not divide into each other, so a crown breathes rather than
metronomes; the phase comes from where the tree stands, which the transform already
carries, so a stand of forty trees does not beat in time.

Two things are deliberately left still. **The 1999 cards**, because a flat tree is one
picture on a quad crossed at the trunk and swaying its top corners folds the whole tree
over like a reed. And **bark**, because a grown trunk is opaque and is therefore in the
ray-tracing acceleration structure, where it does not sway: a bole that moved on screen and
stood still for the shadow rays would cast a shadow from where it used to be.

The clock is the renderer's own — a paused game, a menu, a line of dialogue waiting all
leave the trees moving, and nothing that reads it can affect anything the story can see. A
**headless render leaves it at zero**, so two renders of one room are still the same
picture, which is the basis on which everything here is compared. `render-scene --wind
SECONDS` is how the movement itself is looked at: render the same shot at two values and
diff them, and what has moved is the crowns and nothing else.

## What this needed from the engine

**A glTF reader.** `docs/mesh-enhancement.md` used to end by saying that nothing it
produced reached the screen, because the engine drew `.MOD` and had no way back in.
`Formats/Models/GlbReader` is the counterpart of the existing writer and closes that: a
static mesh hierarchy with positions, normals and one UV set, refusing skins, morph targets
and external buffers rather than half-supporting them. It is what makes `enhanced/models`
an input at all, and the trees are its first consumer rather than its only possible one.

Coordinates pass straight through in both directions. The GLB files this toolchain writes
are the game's own axes wearing a glTF label, so a model that goes out to Blender and comes
back lands exactly where it started — which is the property the whole feature rests on,
since a grown tree has to stand where the card it replaces stood.

## What this does not do

**No dynamic level of detail.** Near and far are decided once, at load, by height. A tree
the player walks up to is whatever the room decided it was.

**Trees still do not cast ray-traced shadows** — alpha-tested geometry is left out of the
acceleration structure, as it always was. The one thing that changed is that a grown
broadleaf's **bark is opaque**, so its trunk is traced and does cast one. The wind does not
reach the bark for the same reason: see *Foliage moves*.

**A tree that sways does not sway its shadow.** The baked lightmap under it was authored
against a still tree and stays where it is.

**The background strips and the objects that mix foliage with masonry stay flat.** Bark is
now taken with the leaves it carries, which was the large half of what was left; a wall is
not, and cannot be.

**Five species is not five species of tree.** It is five silhouettes fitted to eighteen
sprites. `TREE01` and `TREE02` are drawn as the same broadleaf; nobody has looked at a
render of every room and said whether that shows.

**Only the near/far split is authored, not chosen at run time.** See above.

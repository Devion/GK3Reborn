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
```

Everything lands in `ContentWorkspace/enhanced/trees`, outside the repository, beside the
enhanced textures. The engine finds it there; `Settings.ModelledTrees` and the video menu's
**Modelled trees** switch it off, and so does `--no-trees` on a render.

## How much of the game is flat trees

| where | how many cards | what happens to them |
| --- | ---: | --- |
| Foliage props named by scene files | 431 placements | replaced |
| Room objects that are **only** foliage | 3,790 in 64 objects | replaced, budget permitting |
| Room objects mixing foliage with something else | 1,887 | left flat |
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

A mixed object cannot be touched, and the reason is structural rather than a lack of
effort: a room is hidden **by object name** and there is no way to hide half of one.
`wod_dectree01` draws its leaves on cards and its bole with `TRUNK01`, so hiding it to
replace the leaves would take the trunk with them.

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
| `maple` | `MAPLESIDE1`, `MAPLETOP1`, `MAPLE` | whole tree |
| `darkbroadleaf` | `WOODTREE3`, `MAGENTREE` | whole tree |

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
| spruce | 3,900 | 900 |
| cypress | 4,200 | 1,150 |
| broadleaf | 9,000 | 2,500 |
| maple | 8,200 | 2,800 |
| darkbroadleaf | 8,500 | 3,200 |

A room grows every stand it can afford at the far detail — all of an object or none of it,
since a room is hidden by name — and spends what is left raising the **tallest** trees to
full. Tallest rather than nearest, because there is no camera at load time and height is
the only thing in the data that says which tree a room is about. `SceneLoader.WoodBudget`
is 400,000 triangles; what it will not stretch to is said in the log rather than left
silent, because a silent cap reads as "there was no more foliage" when it is not.

Measured on the reference installation, with everything else enhanced:

| room | trees grown | of those, from the room | triangles, flat → grown |
| --- | ---: | ---: | --- |
| CSD | 37 | 5 | 7,177 → 200,793 |
| LHM | 33 | 1 | 5,853 → 156,021 |
| RC4 | 28 | 0 | — → 148,235 |
| PL1 | 26 | 0 | — → 128,139 |
| BAL | 22 | 16 | — → 168,835 |
| WOD | 18 | 0 | 9,561 → 88,653 |

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
**42 entries and 11.7 MB** — 35 GLBs, 5 cards, 2 manifests.

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

`tools/foliage/make_cards.py` writes one RGBA card per species. **It is not a crop of the
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

## Leaf cards are lit as a mass, not as quads

A leaf card's own normal is the wrong answer twice over. Nothing is back-face culled, so
half the cards in any crown are seen from behind and would shade as though lit from the far
side; and a crown of two hundred flat quads at two hundred angles reads as a heap of litter
rather than as one mass with a lit side and a shaded one.

`grow_trees.py` writes custom split normals pointing **out of the crown centre**, with a
little upwards in the mix so the underside is shaded rather than black. It is the trick
every foliage shader has used since trees stopped being sprites and it costs nothing at
run time.

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

**Nothing sways.** There is no wind and no vertex animation on foliage.

**No dynamic level of detail.** Near and far are decided once, at load, by height. A tree
the player walks up to is whatever the room decided it was.

**Trees still do not cast ray-traced shadows** — alpha-tested geometry is left out of the
acceleration structure, as it always was. The one thing that changed is that a grown
broadleaf's **bark is opaque**, so its trunk is traced and does cast one.

**The mixed objects and every background strip stay flat** — 1,887 drawn cards, a third of
what the rooms hold. Handling them needs surface-level rather than object-level hiding, and
that is a change to `AddScene`.

**Five species is not five species of tree.** It is five silhouettes fitted to eighteen
sprites. `TREE01` and `TREE02` are drawn as the same broadleaf; nobody has looked at a
render of every room and said whether that shows.

**Only the near/far split is authored, not chosen at run time.** See above.

# Couiza

The town outside the train station, which GK3 is set in and never modelled.

```bash
# build the pieces and generate the placement table
blender --background --factory-startup --python tools/blender/build_couiza.py -- \
    --workspace <ContentWorkspace> [--dry-run]

# see it, and see it without
GK3Reborn.exe --scene TR1 --timeblock 104P --workspace <ContentWorkspace> \
    --eye 1000,140,-600 --aim 60,-3 --frames 90 --settings scratch.json --screenshot after.png
GK3Reborn.exe --scene TR1 --timeblock 104P --rebarn ... --screenshot before.png
```

## What was wrong

`TR1` is Couiza, and the writing treats it as a town. The recorded line behind
`OTR_BUILDINGS` — the noun bound to every building in the room — is *"I'm not sure what
all these buildings are, but I don't think they're connected to the station."*

What was modelled is **eleven boxes totalling about seven hundred triangles**.
`tudorsmall01` is nine. They stand on open grass between two and eight hundred units
apart, none of them facing the road, none of them touching another, and three of them —
`brothel`, `tudorbasic`, `tudorsmall01`, all at X 2473 and beyond — stand past the eastern
edge of the ground mesh, which ends at X 2435.

They are also the wrong building. Every one is skinned in `RL1barwall1/2/3`,
`RL1TILEROOF`, `RL1SHINGLES` and `rl1_Eaves` and carries painted half-timbering, because
they are **Rennes-les-Bains' kit**: Sierra modelled one Alsatian village and used it for
both valley towns. Rennes-le-Château, the hero location, got the accurate Languedoc
treatment instead — rubble stone, stucco, terracotta pantiles, cobbles. So the fault is
not particular to TR1, and TR1 is where it shows worst, because TR1 is a town seen from a
distance and RC2/RC3 are streets seen from inside.

## What this does

Adds the town around what is there. **Nothing that shipped is moved, repainted or taken
away** — a test holds that, by requiring every line of the original `TR1.SIF` to survive
the edit.

Twenty-four buildings, a level crossing with its keeper's cabin, and forty-four trees,
laid along seven frontages, all of it outside the walk bitmap and inside the ground mesh.

## The three rules

**Every piece is the game's own geometry, painted with the game's own bitmaps.** Not one
vertex is modelled from nothing except the level crossing. A house is one of thirteen
usable donor buildings recentred out of RC2, RC3 or the cemetery, or several of them
butted into a terrace. The textures those donors name — `rc1rghstn`, `rc1redroof2`, `rc1Stucco9`,
`rc1yellowroof`, `rc1lrgstone` — are all in `common.brn`, which every scene on every day
already loads. So the set adds **no texture at all**, and every surface already has the
enhanced normal, ORM and height maps the packs carry for Rennes-le-Château.

**Nothing stands where the player can walk.** `TR1.SIF` writes the walk bitmap as
`size={1448.05,1819.05} offset={-153.948,1123.133}`, and `WalkBoundary` maps it as
`world = u * size - offset`, so it covers **X -154 to 1294 by Z -1123 to 696**. The ground
mesh runs X -1405 to 2435 by Z -2584 to 2475. Every placement is checked against both.
`tr1_cambnds` fences the camera inside X -729 to 1756 by Z -1404 to 2328, which is what
lets a house be a house-shaped facade with no interior.

**A donor is turned by what it is missing.** Sierra built only the sides of a building
that face the street it stands on: ten of the seventeen donors have no wall on one side,
and four have none on two or more. The four are refused — `wstuccohouse` is one wall and
nothing else. The ten are turned so the hole faces the back. Only a house closed all round
is turned by its glass instead, measured from the window and door faces weighted by area.

## How a piece is made

`enhanced/scenes/<ROOM>/original/<object>.glb` is the donor — the same tree
`extract-scenes` cuts for the improved room geometry, so this pass needs nothing of its
own extracted.

1. **The windows travel with the house.** In RC2 and RC3 a window is not part of the
   building: it is a separate scene object, one flat quad of `rc1wstuccowin1` laid on the
   wall, because that is how the room was cut for lighting. Recentring
   `rc3_oldstonehouse` alone gives a blank stone box. Each body therefore claims the
   window, door and shutter quads whose boxes lie within eighteen units of its own.
2. **The material name is reduced to the texture name.** A donor's materials are written
   `rc1rghstn#00115` — surface 115 of RC3 — and that index means nothing outside the room
   it was cut from. `GlbReader`, which reads `enhanced/models`, takes the material name as
   the texture name verbatim.
3. **Terraces are houses butted together**, twenty-six units into each other, each keeping
   its own ridge height and stepping a little off the frontage. A French village terrace
   is a row of separate houses sharing party walls, which is both easier and more accurate
   than authoring one long block.

## Standing on the ground

`pos` puts a model's lowest point at the Y it names. Giving a building the height under
its *centre* stands it on the centre, so on any slope the downhill corner hangs in the
air — which is what it did. The height is the **lowest** ground over the footprint,
sampled on a 5×5 grid, less six units so the grass closes over the footing. That buries
the uphill corner slightly, which nobody sees.

The level crossing is the exception: it sits on the trackbed, which is flat at y = -9.5
the whole length of the map, and is sunk so the deck boards come level with the railhead.
Sampling the terrain would have found the grass shoulder beside the rails.

## The layout is packed, not typed

Typing it was tried and does not work. The terraces are thirteen to twenty hundred units
long, the gaps between the four buildings already on the east line are 195 to 315, and a
layout written by eye put **sixty-eight pairs of buildings through each other**. None of
that is visible from any camera the room has: two roofs in the same place read as one roof
from the ground, and as nothing at all from anywhere else.

So a street is a line the fronts stand on, and the packer computes the stretches of it
with nothing in them yet — subtracting everything TR1 already has in that band of X, and
everything it has already placed — then fills each stretch with the next piece from the
rotation that fits and lands on ground the room actually has. It cannot produce an
overlap, and the run re-checks every footprint afterwards and refuses to write the table
if one is found.

Two things the packer gets from the same arithmetic and the eye does not:

- **Every placement names a model no other placement names.** `SceneDefinition.MergeModels`
  keys a room's models by name, TR1 has both a room file and timeblock files so the merge
  runs, and the second of two identical names silently replaces the first. A piece used
  more than once is copied under its own name, and every second copy is mirrored — which
  is worth having anyway, since a street built from four house shapes reads as wallpaper
  without it.
- **Everything standing up is an obstacle, telephone poles included.** The gate asked for
  150 units on both horizontal axes, which excluded the poles — and two houses then went
  straight over one. It asks for 40 now. A pole blocks its own footprint and costs the
  street almost nothing.

## The roads

Couiza is a working town on the D118, not a hamlet at the end of a track, so the network
comes first and the houses are laid along it.

Nine roads and two surfaced areas, all in `ROAD` — plain asphalt with no markings.
`Full_Road`, which is what Poussin's Tomb uses, carries a white edge line down both sides
of the bitmap: right for one carriageway, wrong the moment it tiles, and a car park
surfaced with it comes out striped.

- **The main street**, in four pieces, following the line `rl1_Path2` already takes. It is
  four pieces because the corridor pinches: between `tudorlong02`'s east face at X 1314
  and `tele_pole01` at X 1451 there are 137 units, so that stretch is 120 wide and dead
  straight.
- **The station approach**, west off the main street.
- **Two side streets** east into the town, threaded between the buildings that shipped —
  the north one runs the 197 units between `tudorlong01` and `brothel02`.
- **The crossing lane** west over the railway to the hamlet, and the **hamlet lane**.
- **The station forecourt and car park**, gridded areas rather than ribbons. The forecourt
  is the ground Gabriel parks the moped on; the car park contains `tr1_taxi`, which was
  standing in a field.

A road is a polyline with a width, laid as a ribbon whose every corner is sampled off
`tr1_floor` and lifted four units clear of it. Sampled every 35 units rather than corner
to corner, because the floor rolls and a chord cuts through every rise between two points
— at two units of lift and 70-unit sampling the grass came up through the car park.

Where a road crosses the railway it is lifted to the railhead instead. The floor under the
track is the trackbed at about y -9.5 with the ballast and sleepers drawn on top, so a
road that followed the floor there was buried: it ran up to the rails on one side and
reappeared on the other with nothing between.

## Surfacing is a decal, and that is an engine change

A road laid over the floor is the nearest thing a click meets, and `SceneInteraction.
FloorTarget` only walks the player when the pick **is** the floor object by name. So the
first roads swallowed every click on them: ground the player could see and not cross.

`type=decal` is a new model type — no 1999 scene uses one. It loads a model file like a
prop, draws like a prop, and `ScenePicker` skips it entirely, so a click passes through to
the floor underneath. Two lines in `SceneLoader.IsBakedIn` and one guard in the picker.

It also has to float clear of the floor or the two z-fight, and every unit of that is a
unit the player stands *under* the tarmac: actors walk on the floor and the engine knows
nothing about this surfacing. The lift is one unit — about two and a half centimetres —
which with the envelope's own margin leaves the taxi driver four centimetres down. It was
twelve before the envelope let the lift come down.

## The houses line the roads

A frontage is a road, a side of it, and how far back to stand: the packer walks the road's
polyline and offsets sideways by half the carriageway, the verge, and half the building's
depth. A bend in the road is therefore a bend in the street, and a house is square to the
road it fronts. Each road also carries a second rank set back about five hundred units, so
the town has depth as well as a street.

Two things this needs that the earlier straight-line packer did not:

- **The road's own half-width has to be in the offset.** Without it a house is set out from
  the *centreline* by the verge alone, stands in the carriageway, and is refused — every
  frontage came out empty.
- **Overlap is tested by separating axis, not by axis-aligned boxes.** A house square to a
  bending road and a road cell square to the map have hulls that overlap wherever the
  street turns. Testing hulls refused most of the frontage; giving the packer slack to get
  round it only made the final check refuse what the packer had placed.

## Nothing the quest needs is built over

Every object `TR1.SIF` binds a noun to — the taxi, the barrels, the doors, the sign, the
poles, the buildings that shipped — plus every spot the scene file names for an actor to
stand on, is a box nothing may be built over, with seventy units of margin. Roads are
deliberately exempt: a road under the taxi is the point.

## The trees## The trees

Forty-two foliage cards: a plane-tree avenue down both sides of the road, cypresses on
the slopes, orchards in the empty quarters and a stand closing the view up the line.

`Foliage` reads a card's species from its **texture**, not its name — `TREE00` is a
broadleaf, `TREE06` a cypress — and the modelled-tree pass then grows a tree to the size
the card was drawn. So a card costs two triangles and becomes a tree for free.

Each card is authored at the coordinates it stands at rather than at the origin, and
carries no `pos`. That is not a style choice: a grown tree brings its own transform, and
`SceneLoader` only applies `pos` when the transform is still the identity — so a tree card
with a `pos` grows where the card is and ignores it. TR1's own seventeen `TR1_FFTREE`
models are built the same way.

## The bridge that is not here

A stone overbridge across the railway was asked for and does not fit. Recorded so that
nobody costs it twice.

The trackbed is flat at y = -9.5 for the whole length of the map, and the cutting either
side of it only reaches +40. Three independent checks put the scene at 2.3 to 2.7 cm per
unit — the platform's 23.5 units above the railhead against a French low platform's 55 cm,
the taxi's 194 units against 4.5 m, a door's 75 units against 2 m — so clearance above the
railhead needs a soffit at about **y = +194**. Getting a road up to that from ground at +5
to +90, at any gradient a road can take, needs roughly two thousand units of approach
ramp, which is most of the map.

What goes in instead is a **level crossing** with a keeper's cabin, which is what a rural
French branch line actually has, and which costs no clearance at all. It is the only new
geometry in the set and it is skinned in the station's own timber and RC3's stone.

## The switch is the geometry

There is no setting and no command-line flag. `SceneDressing.Available` asks the model
library whether `RBN_CZ_ROW_A` is installed, and the table is applied only when it is.

That is the honest gate, because the failure it prevents is specific: a scene naming
seventy models nothing has places nothing and reports each one, so a player with no
content packs would get seventy warnings and the same empty field. One directory listing
at startup leaves such an installation byte-for-byte the game as it shipped.

**It is not a restoration and is deliberately not in `CutContent.txt`.** Everything in
that table is the developers' own data switched back on, or an object they wrote and
recorded and never modelled. Nobody at Sierra wrote, recorded or intended any of this.
Keeping it in its own file, under its own section names and its own switch, is what stops
"content the game shipped with and cannot reach" quietly growing to mean "content we
thought it should have had".

## For whoever builds the ReBarn next

The pieces are in `enhanced/models`, which `ContentPackStage.DefaultPlan` already takes as
`RebarnKind.Model` — the same place `make_props.py` writes to, so `pack-content` picks
them up with everything else and nothing has to be done by hand.

`Dressing.txt` is an embedded resource of the engine, not pack content. It therefore ships
with the executable and the geometry ships with the packs, which is exactly the split the
gate needs: the table is always present and does nothing until the models are.

**Re-run the builder after changing the streets, the donors or the pass**, then repack
with `rebuild-content.cmd --packs-only`. The run sweeps every `RBN_CZ_*` file it did not
write, so a layout that no longer exists cannot ship — it was a stale terrace that kept
sixteen materials named `rc1rghstn.001` in the set long after the code that made them was
gone.

## What is checked, and why each check exists

Every one of these was written after the thing it checks had already shipped once.

| Check | The fault it catches |
|---|---|
| Two placements naming one model | `MergeModels` keeps the last; the other building is not in the room |
| Footprint against every shipped and placed building | Terraces packed through each other, invisible from the ground |
| Footprint against the road cells | The east frontage stood on the road, so the road could not be seen |
| Footprint against the walk bitmap | A building where an actor can stand |
| Every corner on the ground mesh | Terraces hanging over the floor's east edge |
| Donor has walls on at least three sides | `wstuccohouse` is one wall; in the open it reads as a flat |
| Glass lies on a wall — donor, assembled piece, and shipped file | Windows hanging in mid-air behind the station |
| Tree trunk against roads, lanes, buildings and the room's own trees | A cypress growing out of the tarmac |
| Lane against the room's own trees | The same, from the other direction: the road routed through the tree |

The glass test is the one worth describing. A quad is claimed by a building only when it
lies on a solid face of it: the face has to point the same way to within 32 degrees, the
quad's centre has to be within sixteen units of that face's *plane*, and it has to fall
inside that face's own extent. The proximity box this replaced asked only whether the
centre fell inside the building's bounding box grown a little, which claims a neighbour's
window standing in the same airspace and leaves it hanging once the building is moved.
The same test then runs on the assembled piece — deleting any face that failed — and once
more on the written GLB, and the run refuses to write the table while any survive.

## What is still open

- **The new buildings are lit by the room's global light, not lightmapped.** The 1999
  ground keeps its baked shadows, so a building lays no shadow on the grass. Keeping the
  new mass at middle distance hides most of it; the two night blocks (`tr1_n`, 2AM and
  9PM) are where a mismatch would show first.
- **Some donors are blank on a face**, and every one now shows a blank back by design.
  `rc3_cathouse` has two windows and `old_house3` has none of its own, so a few walls read
  as plain stucco. That is the donor art; inventing window quads is the line this file does
  not cross.
- **Only thirteen donors survive the four-wall gate**, so the street repeats more than it
  should. More variety wants either new donor rooms or terraces short enough to fit the
  gaps the packer actually leaves.
- **There are no garden walls.** `rc3_stone_wall` is 557 units long and no gap the packer
  leaves is that big, so it was never placed once; the piece and its machinery are gone.
  Closing the ground line between the terraces wants a wall cut to length.
- **The town is thinner than it was.** Refusing four donors and protecting the road took
  it from twenty-five placements to twenty. It is correct now and sparse; more houses want
  more donor rooms.
- **No cobbles.** The lanes are dirt, which is what the room already is. A cobbled street
  would want the floor repainted, and this pass does not touch Sierra's BSP.

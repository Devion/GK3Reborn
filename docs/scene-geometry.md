# Scene geometry

The rooms themselves — the 110 BSP files — cut into their objects, improved outside the
engine, and put back. `mesh-enhancement.md` covers the same work for `.MOD` models, which
are a different set of files and a different problem; nothing here touches those.

```bash
# 1. cut every room into its objects, and classify what each object is
dotnet run --project tools/GK3Reborn.Tools -- extract-scenes \
    --source <GK3 Data> --workspace <ContentWorkspace> [--only ROOM ...] [--crease 40]

# 2. improve them
blender --background --factory-startup --python tools/blender/enhance_scenes.py -- \
    --workspace <ContentWorkspace> [--only ROOM ...] [--objects NAME ...] [--force]

# 3. gather what was improved into the form the game reads
dotnet run --project tools/GK3Reborn.Tools -- compose-scenes \
    --source <GK3 Data> --workspace <ContentWorkspace> [--only ROOM ...]

# 4. see it, and see it without
dotnet run --project tools/GK3Reborn.Tools -- render-scene --source <GK3 Data> \
    --workspace <ContentWorkspace> --model DIN --output after.png
... --no-improved-geometry --output before.png
```

## For whoever builds the ReBarn next

**Take `enhanced/scene-geometry` into the pack.** It is already in
`ContentPackStage.DefaultPlan` as two entries — the shapes as `RebarnKind.SceneGeometry`
and `scene-geometry.json` as a manifest — so `pack-content` picks it up with everything
else and nothing has to be done by hand. What must not happen is somebody trimming the
plan and leaving the manifest behind, or the shapes behind: either half alone is inert.

**The set is optional, at every layer, and the fallbacks are the point.** No pack entry,
no manifest, no manifest entry for a room, no shape for one of its objects, a shape that
will not parse, or a shape built against a different build of the room: each of those
draws that much of the room exactly as it shipped in 1999. A game with none of this
installed is complete. That is not politeness — it is what lets the set be rebuilt,
half-rebuilt or deleted without anybody having to coordinate.

**Rebuild it when the geometry it was cut from changes, which is never, or when the pass
that made it changes, which is often.** The source is the game's own `.BSP` files; they do
not change. So in practice: re-run steps 2 and 3 after touching
`tools/blender/enhance_scenes.py`, and re-run step 1 as well after touching
`SceneObjectGlb`, the classifier, or the crease angle.

**It does not depend on the texture pipeline and the texture pipeline does not depend on
it.** Nothing here reads a normal map and nothing here writes one. `rebuild-content.cmd`
can run the scene pass or not, in any order, without changing a byte of the other.

## What is in the workspace

```
enhanced/scenes/<ROOM>/original/<object>.glb   every drawable object, as the game has it
enhanced/scenes/<ROOM>/<object>.glb            the ones the pass improved
enhanced/scene-geometry/<shape>.glb            what ships: one file per distinct shape
enhanced/scene-geometry/scene-geometry.json    which rooms draw which shapes
manifests/scene-objects.json                   every object, what it is, and why
```

`enhanced/scenes` is the authoring tree and is not shipped. It is safe to open a room's
directory in Blender, change one object by hand, and re-run `compose-scenes`: an object
with a file beside `original/` is used, and an object without one is drawn from the room.
Hand work therefore composes with the batch pass instead of being overwritten by it,
provided the batch pass is not re-run with `--force` over the top of it.

## Why an object's material names carry a number

This is the whole mechanism, and everything else follows from it.

A room's geometry can be improved outside the engine only if every triangle that comes
back can still be matched to the **surface** it came from. A surface is what carries the
texture, the lightmap's offset and scale, and the flags that say whether a thing is
self-lit or casts a shadow. A triangle that has lost its surface has lost its lighting,
and a room lit by nothing is not an improvement.

glTF has several places to hang an identifier and exactly one of them survives a modelling
tool: **the material name**. Face-to-material assignment is preserved through every
operation that matters — bevel, subdivision, decimation, separating a mesh, joining two —
because that assignment is the thing a modeller is manipulating. Custom vertex attributes
are interpolated into nonsense by the first bevel, node extras are dropped by several
exporters, and face attributes have nowhere to live in glTF at all.

So a surface is written as `TEXTURE#00104`, and the picture itself is shared between every
material that names it. Object identity is *derived* rather than carried: every surface
knows which object owns it, so grouping triangles by surface recovers the objects even
when a tool has renamed, split or joined the meshes. Node names are written for a person
to read and nothing reads them back.

**Rules for anyone editing an object by hand.** Keep each face on the material it arrived
on. Do not rename a material, except that a `.001` suffix a tool appends is tolerated.
Geometry you add must be assigned to one of the materials already there, or it will be
dropped with a warning naming the count. Everything else — moving vertices, subdividing,
retopologising, splitting the mesh in two — is fine.

## What each object is, and what that earns it

`extract-scenes` sorts every object into a disposition, ordered by certainty: what a scene
file declared, then what the surface flags say, then what the geometry measurably is, and
only then what the artist called it. Every answer records the evidence in
`manifests/scene-objects.json`, so a wrong call is arguable rather than silent.

Names carry more weight here than they do for models. A `.MOD` file's name is a filename
and lies routinely; an object name inside a room is an artist's own label for a part of
that room — `cem_fountain`, `dinchair03`, `mop_moped` — and there is no other declaration
channel for them at all. The geometry gates still come first and overrule the names: an
object with one plane is a card whatever it is called, and an object three hundred units
across is a piece of the building even if the word "lamp" appears in its name.

Each disposition carries four numbers: how sharp an edge has to be to be bevelled, how many
times a curve is refined, how wide the bevel is, how far shading smooths across a facet, and
**how far a facet may turn before it counts as a corner rather than a curve**. That last one
is the only number here that cannot be measured, because it is not in the geometry: an
eight-sided prism and a crude cylinder are the same mesh, and only what the thing *is*
separates them.

| Disposition | Count | Levels | Corner | Smooth | Bevel |
|---|---:|---:|---:|---:|---|
| `furniture` | 1,088 | 1 | 65° | 35° | 30° |
| `architecture` | 966 | 1 | 50° | 8° | 45°, narrower |
| `flat` | 936 | — | — | — | Nothing: one plane, or too few triangles to enclose anything |
| `ornament` | 758 | 2 | 70° | 60° | 30° |
| `foliage` | 335 | — | — | — | Nothing: replaced by grown trees (`trees.md`) |
| `collision` | 230 | — | — | — | Nothing: hit tests, shadow decals, camera bounds |
| `backdrop` | 81 | — | — | — | Nothing: a painted view of somewhere else |
| `terrain` | 76 | — | — | — | Nothing: the engine cuts the floor's relief at load |
| `rock` | 49 | 2 | 70° | 60° | 35° |
| `review` | 37 | — | — | — | Nothing, unless `--include-review` is passed |
| `vehicle` | 12 | 2 | 70° | 60° | 30°, a quarter wider |

4,568 objects across 110 rooms; 2,850 of them come out improved, at
**677,643 → 4,210,752 triangles**, about six times what was there. The corpus's objects
have a median of 52 triangles, so a room's share is small — the heaviest is about 200,000
— and the whole set is 174 MB.

**Seventy degrees for the things that are round on purpose, fifty for the building.** A
lathe of N sides turns 360/N at each of its own sides: 30° at twelve, 45° at eight, 60° at
six. The moped shop's barrels are six-sided, so a threshold that stops at 55 leaves them
hexagonal; a roof ridge turns 40° to 70° depending on its pitch, so a threshold that runs
to 70 rounds one off. Both facts are in the corpus and neither can be inferred from a mesh
alone, which is why the number belongs to the disposition.

**The smoothing angle has to keep up with the corner angle.** Refining a barrel and then
shading it at forty degrees leaves its eight original staves as eight hard bands: the
geometry is a curve and the shading still says it is a prism. Sixty is what the renderer's
own rounding uses, for the reason recorded there — an eight-sided bell turns 45° at each of
its own sides — while still leaving a shade's rim, at ninety, as the edge it is meant to be.

**Architecture is refined at one level, and it was at zero.** The old grounds were that a
modifier stack does the most damage to walls, and what that actually did was leave every
archway in the game as faceted as it shipped. What protects a wall is that its seams measure
zero degrees and are never selected at all; the arch over the museum steps is a curve by the
same measurement that says the wall beside it is not. Two levels was tried and reverted: it
buys a barely visible improvement on the arches, costs the corpus nine times its triangles
instead of six, and is where the drift check starts refusing things.

**A budget bounds the cost twice.** No object may multiply by more than `--growth` (24) or
exceed `--ceiling` (15,000 triangles), and an object that asks for more is refined one level
less rather than refused. The ceiling is what catches the object that is *already* detailed:
a carved figure of 4,400 triangles has facets a millimetre across, gains nothing from being
made 84,000, and asked for exactly that.

## Where the triangles go, and where they deliberately do not

**A bevel is worth more than a subdivision here, and costs almost nothing.** What makes
1999 geometry read as 1999 is not the triangle count, it is that every edge is infinitely
sharp: an edge with no width catches no highlight, so a table reads as a decal of a table.
An angle-limited bevel touches only the edges that are already sharp and adds nothing at
all across a flat panel — which is the whole of the answer to "do not spend triangles on
surfaces that really are flat".

**Subdivision is spent only where a curve is, and where a curve is is measured.** A face
is refined when it meets a neighbour at an angle that is neither flat nor a corner — the
signature of a curve somebody tessellated — so the sides of a lamp shade are subdivided
and its flat cap is not. 1,178 of the corpus's objects sit on a single plane and are never
touched by anything.

**The refinement is interpolating, not approximating.** Every authored vertex stays exactly
where it was put and each new one is placed on the cubic that the two ends of its edge and
their normals describe: the PN construction, which is what `ObjectRounding` already uses
inside the engine, and for the reason recorded there — an approximating scheme moves every
vertex towards its neighbours' average, which is invisible on a dense mesh and is the whole
shape on a twelve-sided lamp shade, whose panels sag between their ribs while its rim
spikes.

**A budget reduces the treatment; it does not discard it.** An object that would grow past
`--growth` is refined one level less and tried again, down to the bevel alone. The first
version refused instead, and what it refused was the list of things most worth doing — a
toothbrush, a sink, a lamppost, a chafing dish — because those are exactly the objects that
are curves all over and so multiply fastest.

## Seven things about this data that broke the obvious implementation

Each of these produced geometry that was wrong in a way no count could see. Between them
they are the difference between a barrel that is still a hexagon and a barrel that is a
barrel.

**GK3's scene geometry is not consistently wound.** Half the time a perfectly flat seam
between two quads of one wall measures 180° rather than nothing. Every decision in the pass
is made from that angle — what to refine, what to bevel — and taking it at face value made
the bevel modifier cut a groove along the middle of a tablecloth and leave a dark slit
across it.

**Folding the angle back was the second mistake, and a worse one.** `min(a, 180−a)` does
read a flat seam as flat from either side, but it also reads a razor edge at 170° as a
gentle 10° curve and refines it, and a roof ridge at 120° as a 60° lathe step. Measured, it
is wrong on 47 edges of the village fountain and 73 of one room's walls. The angle is taken
exactly from the normals now, and only its *sign* is recovered from the geometry: the two
faces' centres, seen from the middle of the edge, are 180° apart when the surface is flat
and closer together the harder it folds. That estimate is biased by the shape of the
triangles and nowhere near good enough to be the answer — but it does not have to be,
because the two candidates it chooses between are a whole fold apart.

The same fact wrecks vertex normals: two faces of one surface can have opposed normals that
cancel to nearly nothing, and Blender's `subdivide_edges(smooth=1.0)` offsets a new vertex
along that average by an amount proportional to the *edge's length*. A telephone pole came
out four times its own width and a crumbling pillar came out double. Smoothing groups are
gathered by the **unsigned** angle now, each group's normal is accumulated with every face
turned to agree with the first, and the group is pointed away from the object's own middle
so a recovered curve bows outward.

**Every edge between two of a room's surfaces is a texture seam**, because each surface
carries its own mapping. The strip a bevel cuts there sweeps its texture coordinates from
one side's to the other's and draws a smeared band of whatever the picture holds in
between: on the dining room's tables, a dark dashed line across the tablecloth. Those edges
are marked at a sixth of the width rather than left unbevelled — they are also the
silhouette, and a table's top meeting its side is exactly what wants rounding.

**Half the refinement was going to the wrong face, and that is why every barrel in the
game stayed a hexagon.** A new vertex is placed on the cubic that the two ends of its edge
and their normals describe, and the normals come from a smoothing group, which belongs to a
*face*. An edge has two. Taking the first was arbitrary: a barrel's staves meet its lid
along a rim edge, and half the time the first face there is the flat lid, whose normals all
point straight up, agree with each other, and say the edge is straight. It is straight
across the lid. Across the stave it is an eighth of the way round a circle. Both faces are
asked now and the one that bows further wins, because a curve is wherever the normals
disagree and a flat seam gives zero from either side.

**An object is a shadow decal when every surface of it is one, not when any surface is.**
A moped is 38 surfaces of which exactly one — the blob it casts on the ground — carries the
flag, and testing the union of an object's flags called every moped in the game a decal and
left it as it shipped. 54 objects were excluded that way; 8 across the corpus really are
decals throughout.

**Triangle count is not evidence of being a building.** The classifier used to send anything
over a thousand triangles to `architecture`, on the reasoning that a street of lampposts is
one object and must not be subdivided. What it actually did was classify the mopeds and a
detailed carved bike as masonry. Size already answers that question — three hundred units
across is a building — and cost belongs in the pass's own budget, which is where it is now.

**A set of bmesh elements iterates in memory-address order.** The same object imported
twice in one run subdivided its edges in two different orders and came out as two different
meshes, identical to look at and not equal — which defeated the content addressing the
shipped set is deduplicated by. Everything iterates a sorted list of indices now.

**`miter_outer = "MITER_ARC"` and `use_clamp_overlap` together are a Blender bug.** On 25
objects across the corpus — the wall lanterns above all — the two produce a mesh three
hundred trillion units across. Either alone is fine, and the clamp is the one that has to
stay: without it a bevel eats past the middle of the face it is cutting and turns a thin
panel inside out. The default sharp miter is used.

**Two thresholds were set below the precision of the numbers they were thresholding.** A
room's coordinates run to a few thousand, where a 32-bit float resolves about two
ten-thousandths. Merging vertices at 0.0001 therefore could not merge two corners the
original file holds at exactly the same place, because writing them through a float had
already moved them further apart than that — leaving a shading seam down the side of every
object whose two halves are different surfaces. Keying a refined vertex by its position
rounded to four decimals had the same fault from the other side. Both are hundredths now,
which is forty times that resolution and a hundredth of the smallest thing anybody
modelled.

## What ships, and why it is addressed by its own hash

The per-object directory is the shape the work is done in; it is not the shape it ships in.
A pack key carries no directory, and **the corpus repeats itself**: a location has a
geometry file per timeblock — `DIN`, `DIN_302A` and `DIN_303P` are one dining room lit three
ways — holding the same furniture at the same coordinates under a different surface
numbering. Measured over the corpus, 2,710 improved objects are **2,106 distinct shapes**: a
fifth of the set was being shipped more than once.

So a shape is a file named for the hash of its own geometry, holding positions, normals and
texture coordinates with its materials named `slot#000`, `slot#001`; and a placement in
`scene-geometry.json` says which object of which room draws it and what that room calls each
slot. Everything that varies between the rooms sharing a shape is in the placement, and
everything that is genuinely the same is in the file.

Three things fall out of that, and the second is worth as much as the first:

- It ships once. 146 MB became 122 MB.
- **It is read once per session**, cached across rooms, so crossing between a location's
  timeblock variants — which the player does all game — costs a dictionary lookup.
- It stays honest by itself. Two objects that stop being identical stop sharing, because
  the name is computed from the contents; nobody has to notice or record it.

The hash is over a **canonical, quantised** form rather than over the encoded bytes.
Blender given byte-identical input twice writes two meshes that agree on every position and
normal and differ in the last bit of an interpolated texture coordinate — 1.55678988 against
1.55678999 — which is float arithmetic and not a defect. Hashing the exact bytes reported
nine copies of one chafing dish as nine distinct shapes. Each triangle is rotated to start
at its own lowest corner, which keeps its winding; the triangles are sorted; and every
number is rounded to a step below what can be seen — a thousandth of a world unit, a
hundredth of a degree, a fiftieth of a texel on the largest texture in the set.

## What the engine does with it, and what it refuses

`SceneGeometry.Replace` runs before the room's own polygon loop, emits the improved
objects, and marks their surfaces as already handled — the same mechanism that stops a
rounded object being drawn twice. It takes precedence over `ObjectRounding`: an object
somebody has modelled properly is a better answer than one the loader curves at load, and
the two must not both happen to the same surfaces.

**The overlay supplies positions, normals and texture coordinates. It supplies nothing
else, and it is not asked to.** Every triangle names one of the room's own surfaces, and
that surface decides the picture on it, where its lightmap sits, and whether it lights
itself, casts a shadow or is drawn at all — through exactly the same arithmetic an
unmodified surface goes through. Replacing a chair is a change to the chair and not to the
room's lighting. The room's collision, navigation, walk boundary and camera bounds are not
involved at any point: they are read from the original geometry and other files, and the
picture is the only thing this touches.

Two things are refused rather than replaced. A hidden surface stays in its batch as hidden,
because there is no showing something that was never read. And a surface the relief plan is
cutting into keeps its own geometry, because the cut and the replacement are two sets of
triangles for one patch of floor and drawing both puts the floor through itself.

**The hash check is not defensive tidiness.** A surface index is a position in a file. An
overlay built against a different build of the same room puts every lightmap on the wrong
surface, and the result draws perfectly and is lit by somebody else's lighting — a failure
nobody would report as a geometry bug. So the room's bytes are hashed at load and a
mismatch refuses the room with a warning naming what to re-run.

`compose-scenes` refuses on its own account too: an object whose replacement reaches more
than a quarter of its own size outside the box the original occupied is dropped and
reported. Refining a curve does reach outside the authored hull and is supposed to — an
eight-sided lantern's arc stands about 8% of its radius proud of the chord — but an
exporter with the wrong up axis or unit scale is off by a whole multiple, and that is the
mistake which produces no error anywhere else.

## Settings and switches

| Where | Switch | Effect |
|---|---|---|
| Game | Video → Rounded room objects | `Settings.ImprovedSceneGeometry`, on by default |
| `render-scene` | `--no-improved-geometry` | Draw every room as it shipped, for comparison |
| `enhance_scenes.py` | `--levels 0` | Bevel only, so the two can be told apart in a screenshot |
| `enhance_scenes.py` | `--bevel N` | Bevel width, in hundredths of the object's longest edge |
| `enhance_scenes.py` | `--dispositions a,b` | Only these classes |
| `enhance_scenes.py` | `--growth N` / `--ceiling N` | How far an object may multiply, and its cap |
| `enhance_scenes.py` | `--include-review` | Also the 37 nothing classified |

The setting takes effect at the next door, like the trees and the horizon, and for the same
reason: the room standing round the player was built from whichever set was chosen when it
loaded.

## What this does not do

**It cannot invent detail.** Beveling and subdivision round silhouettes and give normals
something to work with; they do not add information that was never modelled. The output is
a better base mesh, not a finished asset. Three consequences are worth naming, because they
look like the pass failing and are not:

- **A balcony gets a bevel and nothing else.** `rc1balcony_1` is six plane orientations over
  58 triangles: a box. There is no curve in it to recover and no baluster to find. Making
  one nicer means modelling one.
- **An opaque flat card cannot be given thickness.** Some roofs, fascias and steps are a
  single plane — `rc1_roofsidez` is one plane over 18 triangles and 949 units across — and a
  solid roof edge would have to be extruded to *some* side. Which side is not in the data:
  extrude the wrong way and the roof lifts off its walls or pushes through them. That
  decision belongs to somebody looking at the room, so those objects are classified `flat`
  and left alone. **A keyed card is the exception**, and `cutout-cards.md` is that pass: the
  colour key states where the silhouette is, so a railing can be given a rim and shelled
  symmetrically about its own plane, which is what makes the question of sides go away.
- **A box with a wooden texture is still a box.** `mop_crate3` looks like a stave tub and
  measures as four right angles: no edge on it is in the curve band at any threshold. The
  name lies, the picture lies, and the geometry is the only one of the three that cannot.

**No geometry nodes.** Everything here is a bevel, a selective subdivision and a
weighted-normal pass, all of which are modifiers or a few lines of bmesh. A node tree would
express the same operations, add a `.blend` file the pipeline has to ship and version, and
put the interesting decision — which faces are curved — inside a graph that cannot be
tested. The measurement that decides it is worth more than the machinery that applies it.

**Terrain and foliage are left alone on purpose**, not for lack of time. The floor already
has its height map cut into it at load, at a million triangles and measured (see
`known-issues.md` and the relief work); and a 1999 foliage card is replaced by a grown tree
rather than refined, because refining a picture of a tree gives a smoother picture of a
tree. Both are somebody else's pass and both are already done.

**Nothing here changes what a surface is made of.** Base colour, normals, ORM and height are
an image pipeline that runs on textures and knows nothing about geometry; the two meet only
in the renderer. A room whose objects have been improved and whose textures have not is a
perfectly ordinary state, and so is the reverse.

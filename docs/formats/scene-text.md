# Scene text: initialisation files and scene assets

Two text formats say what a scene *is*. Neither is binary, and both were easy to
overlook next to the BSP and MUL files — which was a mistake, because between them
they carry the cameras, the props, the visibility rules and the entire original
lighting rig.

## The dialect

Both are INI with three additions, shared with several other GK3 text assets and
parsed once in `IniDocument`.

**A line may hold several key/value pairs**, comma-separated, and commas inside
braces belong to the value:

```
START, pos={235.26, 2.72, 57.99}, heading=-16.04, camera=START
```

That is four pairs — `START`, `pos`, `heading`, `camera` — not six.

**A bare token is a flag** whose value repeats its key, which is how `hidden`,
`ego` and `Default` are written.

**A section header may carry a condition**:

```
[MODELS={IsCurrentTime("202p") && GetEgoCurrentLocationCount() < 1}]
```

The same section then appears many times with contents that depend on the story's
state. Conditions are Sheep expressions and are kept verbatim by the parser; who
decides them is the caller's choice, described under
[Deciding the conditions](#deciding-the-conditions) below.

Comments are `//` to end of line and `/* */` across lines.

One header in `CHU.SIF` closes with `}]]`. The condition is taken as the text
between the first `{` and the last `}` rather than trimmed of braces, so the stray
bracket is not carried into the expression; left attached it reads as trailing
junk and the condition fails for the whole scene.

### Whether commas separate pairs is per file type

Scene initialisation files write vectors braced (`pos={1, 2, 3}`) and need the
comma splitting. Scene assets write them bare (`Position=1,2,3`) and must not be
split. This cannot be inferred from a line — `Position=1,2,3` splits perfectly
happily into three plausible-looking pairs — so it is a parameter of the parse.

Getting it wrong is silent: every vector in the file reduces to its first
component, and a scene reports zero lights rather than an error. That is exactly
what happened here before the two dialects were separated.

## Scene initialisation files (`.SIF`)

**Two per scene, not one.** The general file is named for the location — `R25.SIF` — and
says what the room *is*: its geometry, its furniture, the cameras the player can stand at.
A second file is named for the location and the timeblock together — `R25202P.SIF` — and
says what is *happening* in it: Grace on the couch, Mosely and his bottle, the book, the
dialogue cameras for the conversation they are about to have, and the action file that
drives it. 79 general files and 493 timeblock files, so most location-and-timeblock pairs
have one; a scene read without it is an empty room at every point in a story that mostly
happens in occupied ones.

`SceneDefinition` joins the pair the way the original does. Each file decides its own
conditions independently, and then:

- lists — models, actors, positions, all four camera kinds — **concatenate**, general
  first, so a name declared in both resolves to the timeblock file's version under the
  same last-declaration-wins rule that applies inside one file;
- the `GENERAL` block **accumulates**, with anything the timeblock file sets overriding
  what the general file said;
- the **default camera comes from the general file**. Timeblock files routinely declare an
  empty `[ROOM_CAMERAS]` and fill in `[CINEMATIC_CAMERAS]` instead, so letting them answer
  would open half the game's scenes on nothing.

Across the corpus the merge adds 692 models, 358 actors and 508 cameras that were
previously invisible.

Sections used so far:

| section | contents |
| --- | --- |
| `GENERAL` | `scene=` names the scene asset for this state; `floor=` names the object in the geometry that is the ground; `boundary=` with `size=`/`offset=`; `cameraBounds=`, `globalLight,pos={…}` |
| `ACTORS` | `model=`, `noun=`, `idle=`, `talk=`, `ego` |
| `MODELS` | `model=`, `noun=`, `type=`, `hidden` |
| `ROOM_CAMERAS` | name, `angle={yaw, pitch}` in degrees, `pos={…}`, `Default` |
| `CINEMATIC_CAMERAS`, `INSPECT_CAMERAS`, `DIALOGUE_CAMERAS` | same shape, different use |
| `POSITIONS` | named spots with `pos`, `heading`, and the `camera` to cut to |
| `REGIONS`, `TRIGGERS` | rectangles the game reacts to |
| `ACTIONS`, `AMBIENT` | bare file names, one per line: the `.NVC` action files in scope and the `.STK` soundtracks to play |

### Model types

`type=` decides what a model line means, and the four values behave differently:

| type | meaning |
| --- | --- |
| `prop`, `gasprop` | a `.MOD` file to load and place |
| `scene` | an object already baked into the BSP; the line only names and configures it |
| `hittest` | likewise baked in, but **never drawn** — a clickable volume |
| `noclick` | drawn, but not interactive |

Loading a model file for a `scene` line draws the same furniture twice, slightly
apart, which reads as z-fighting. Drawing a `hittest` puts a large flat slab
through the middle of the room. Both were live bugs before this table was written
down.

Across the corpus: 2,463 `scene`, 1,905 `prop`, 255 `hittest`, 91 `gasprop` and a
single `noclick` — TE3's floor. Model lines may also carry `verb=`, 103 times, and
95 of those are `EXIT` or one of its directional forms; the rest are `GO_UP`,
`GO_DOWN`, `OPEN` and one `CLIMB`. It is the verb a click performs without asking,
so that clicking a doorway walks through it instead of opening the action bar.

`[ACTIONS]` and `[AMBIENT]` carry no `key=`, only names, and for the action files the
name is the condition — `R25_23ALL.NVC` applies on days two and three. See
[`actions.md`](actions.md) for that grammar and for which files are in scope.

### Resolving a click

`ScenePicker` casts one ray at the room and at the models standing in it together
and keeps whatever it reaches first. `Camera.RayThrough` builds the ray from a
pixel, in the same left-handed basis the view matrix uses.

Three rules decide what a hit means, and all three come from the `[MODELS]` table
rather than from the geometry:

- **A noun is the whole test of interactivity**, as it is in the original —
  `CanInteract()` there is `IsActive() && !noun.empty()`. Geometry the file does not
  name is scenery.
- **Scenery still blocks.** Most of a room is wallpaper with no noun, and reporting
  the door behind it would let the player open a door through a wall. The nearest
  hit wins whether or not it can be acted on.
- **A hidden object is not there at all** — not merely undrawn. The ray passes
  through it, which is what the original does by clearing the interactive flag on
  those surfaces rather than by skipping them when drawing.

Faces matter for the room and not for the models. A room is a box seen from the
inside, so a BSP surface facing away is one the player is behind and the original
rejects it; a prop is a closed shell whose winding is the modeller's business.

The awkward part is that **much of what can be clicked is never drawn**. A `hittest`
is ordinary geometry with an ordinary texture that the file marks invisible: a slab
across a doorway, a box over the area a note occupies on a desk. PLO's four exits
are hit tests — at its `FR_MAP` camera three quarters of everything clickable in
view is invisible in the render. So comparing a render against the original says
nothing about whether the doorway can be clicked, and the check has to be a
different picture:

```bash
GK3Reborn.Tools render-scene --model PLO --timeblock 205P --camera FR_MAP \
    --noun-map plo-nouns.png --pick 512,384 ...
```

`--noun-map` casts the game's own ray through every pixel and colours it by what
answered — one stable colour per noun, dark grey for scenery, black for nothing —
then reports which nouns and which kinds of thing covered the view. `--pick X,Y`
answers for one pixel, naming the object, its noun and verb, and how far away it is.

### Deciding the conditions

981 section headers in the corpus carry one, and they call sixteen functions
between them — all state queries, all now implemented:

| function | uses | what it asks |
| --- | ---: | --- |
| `IsCurrentTime` | 1,232 | is it this timeblock |
| `GetNounVerbCount` | 244 | has the player done this to that, and how often |
| `IsActorAtLocation` | 232 | is somebody in this room |
| `GetGameVariableInt` | 175 | an ordinary script variable |
| `GetTopicCount` | 77 | has this come up in conversation |
| `DoesGraceHaveInvItem`, `DoesGabeHaveInvItem` | 78 | is it in a pocket |
| `GetFlag` | 50 | an ordinary script flag |
| `WasLastLocation` | 35 | where the player came in from |
| `GetEgoCurrentLocationCount`, `GetEgoLocationCount` | 48 | how often here already |
| `GetChatCount` | 13 | how often chatted about this |
| `WasEgoEverInLocation` | 2 | ever, in any timeblock |

Read without deciding them, a scene is the union of every state it can be in: R25
holds both the made bed and the unmade one, and its hall door is simultaneously
present and hidden. That is the right answer for a corpus survey and the wrong one
for a game, so it is a parameter — `SceneInitFile.Parse` takes an optional filter,
and `SceneConditions` builds one from the game's state.

All 79 general files and all 493 timeblock files decide cleanly at all 17 timeblocks —
1,343 scene-and-timeblock pairs — with no unresolved function and no expression the parser
cannot read.

Two consequences are worth knowing.

**What a repeated declaration means changes.** Undecided, a pair of complementary
blocks describes two states and the reader has to reconcile them, which is why it
hides a model only when every block that declares it agrees. Decided, at most one
block applies and the later declaration simply refines the earlier — which is what
the original does, and what makes `r25door2hal_scene` a door at 110A and nothing
at all at 202P.

**Location counts are of previous visits.** A SIF asks
`GetEgoCurrentLocationCount() < 1` to mean "the first time here"; scripts that run
once the scene is standing check for one instead. The count is therefore
incremented on arrival *after* the scene is built — `GameState.EnterLocation`,
called by the caller rather than by the loader — and counted per actor, per
location, per timeblock. `WasEgoEverInLocation` is the across-all-timeblocks form.

The timeblock's string form matters here more than it looks: the hour is two
digits, so `Timeblock.ToString()` must render `102P` and not `12P`. Unpadded, every
`IsCurrentTime` comparison in the game is false and each scene quietly loads
whichever state its unconditional block happens to describe.

### Walk boundaries

`[GENERAL]` names one, on a line carrying three pairs:

```
boundary=R25wlkBnds,size={369.064362, 386.198120},offset={39.952667, -32.002213}
```

The boundary is a small palettised bitmap stretched over the world's X/Z plane, and **the
palette index is the datum** — resolving it through the palette to a colour throws the
meaning away. `BitmapDecoder.DecodeIndexed` reads one without doing that.

| index | meaning |
| --- | --- |
| 0–7 | open floor; ascending values are a gradient towards the walls, which the original's pathfinder used to stop actors scraping along them |
| 8, 9 | close enough to a wall to count as wall |
| 128–254 | named regions a script opens and closes — a door that unlocks, a road that becomes available |
| 255 | wall, and everything outside the bitmap |

Across the corpus's 78 boundary bitmaps only 0–8, 229–238 and 245–255 ever appear; 9 is
carried because the original carries it. Sizes run from 24×23 to 556×306 — R25's is 64×64
for a room 369 units across, so a texel is about five units. This is navigation, not
collision.

Three scenes declare no boundary: `DU1` and `DU2`, the dumbwaiter shafts, which are ridden
rather than walked, and `MA2`. Every other location and timeblock — 1,292 pairs — loads
one, and every declared bitmap is present and decodes.

The rows run from the bottom of the covered area upward, so the bitmap's top row is the
far end of the room. `Plan/04` makes overlay validation an exit criterion for this phase,
and this is why: an inverted row order, a swapped offset sign and a boundary half a room
out all produce a mask that looks perfectly reasonable until you put it on the floor.

```bash
GK3Reborn.Tools render-scene --model R25 --timeblock 202P --walk-overlay --camera FR_DU1 ...
```

### Finding a way across one

`WalkPath.Find` answers it, following G-Engine's `WalkerBoundary::FindPath`: a
breadth-first search over the texels, then two passes of conditioning. Not A* — G-Engine's
note that the graph is enormous and its edges effectively unweighted holds here too, and a
heuristic buys nothing the queue was not already giving.

The search runs on a sparse lattice first, taking every fourth texel, and halves the step
only when that fails. RC1's boundary is 392×507 — nearly 200,000 nodes — and most walks
across it are over open ground that the coarsest pass finds at once. Stepping between
lattice nodes still tests the texels in between, so a sparse search cannot cut through a
wall; it can only miss a gap, which is what forces the halving. A doorway one texel wide
at an odd column needs the full grid.

Then the gradient earns its keep. The interior of the route is nudged towards lower
indices until it is clear of the walls, and the route is string-pulled — where the walk
from one node to the node after next is open, the one in between was an artefact of the
grid. Both ends are left exactly where they were asked for: an actor told to stand on a
mark stands on the mark. Either end is first snapped to the nearest open texel, so a walk
that starts inside a wall leaves it and a click on the scenery walks up to the scenery.

A route that cannot arrive is still returned, as the best effort at getting close, with
`ReachedGoal` false. That is the difference between an actor who walks as far as the shut
door and one who ignores the click.

```bash
GK3Reborn.Tools render-scene --model RC1 --timeblock 110A --walk-overlay     --walk-path GABRIEL_INIT:BOOKSTORE ...
```

draws it over the regions, blue when it arrives and red when it could not, and prints
every corner with the region it stands on. Either end may be a position name or a pair of
world coordinates, `x,z`; naming neither lists the scene's positions.

### Loading all of it

`check-scenes` loads every location at every point in the story and reports what came
out. The loading is the engine's own — the same `SceneLoader` the game uses, writing
into a `HeadlessSceneSink` that counts instead of drawing — so a pass means the game
can build these scenes rather than that a second implementation agrees with the first.
That is P6's exit criterion, `Plan/04`.

```bash
GK3Reborn.Tools check-scenes --source <GK3>/Data          # composition only, 4 seconds
GK3Reborn.Tools check-scenes --source <GK3>/Data --deep   # geometry too, 80 seconds
```

Without `--deep` it stops at what a scene *is* — which state the room is in, who is in
it, where they may stand, what may be done to them — all of which is decided by text
files. With it, the geometry, the bakes and every texture load too. `--model` limits it
to one location.

The baseline, which is what a regression shows up against:

- **1,343** location and timeblock pairs compose; **493** have a timeblock file of
  their own.
- 45,945 models, 1,944 actors, 5,807 room cameras, 10,577 positions.
- **1,292** load a walk boundary, over **78** distinct bitmaps, all present and
  decoding. Only `DU1`, `DU2` and `MA2` declare none — the dumbwaiter shafts are
  ridden rather than walked.
- 845 name a soundtrack; 19,651 nouns hang on their objects.
- **1,315 of 1,343 load their geometry**: 14.5M triangles, 52,464 named objects,
  90,314 textures, 63,270 things a click can land on. The other 28 have no geometry at
  that point in the story, which is the story rather than a fault — `ARM` names its
  scene asset only inside conditional blocks, so at ten of the timeblocks it names
  none; `MA2` names one the installation does not contain at any timeblock; `RC1` has
  none at `212P`. The sweep tells that apart from a failure by asking whether an asset
  was found, not by whether loading returned something.

### Where actors stand

`[ACTORS]` lines carry `pos=`, naming a spot in `[POSITIONS]` — `pos=GRACE_INIT`, and
`GRACE_INIT` defined in the same timeblock file. Ego carries none and starts at `START`.

An actor with no `pos=` is placed by a script rather than by the file, which is ordinary:
206 actor-and-timeblock pairs in the corpus are like that, and they are skipped silently
until there is something to run their scripts. Naming a spot the scene does not define is
a different matter and is reported as `SCENE011`; it happens exactly once in the corpus,
for the abbé at `MA1 303P`.

### Camera angles

`angle={yaw, pitch}` in degrees. The original composes the rotation as yaw about
Y then pitch about X and applies it to +Z (`GameCamera::SetAngle`,
`Transform::GetForward`), which gives

```
forward = (cos(pitch)·sin(yaw), −sin(pitch), cos(pitch)·cos(yaw))
```

Reversing the order produces a view that looks plausible from shallow cameras and
badly wrong from steep ones — an easy mistake to ship.

## Scene assets (`.SCN`)

One per scene *and timeblock*: `R25_M.SCN`, `R25_A.SCN`, `R25_E.SCN`,
`R25_N.SCN`. 229 in the corpus.

```
BSP=r25
Version=0x202
ExportDate=Wed Sep 22 14:54:01 1999

[Skybox]
Left=RLC_N_512LF
Front=RLC_N_512FT
Azimuth=-210.000000

[Models]
r25background=1
r25couch=1
…

[Lights]
standing_lamp_omni
moon(key)
…

[Light_standing_lamp_omni]
Type=0
Position=265.175781,80.611328,617.383789
…
```

`[Models]` lists the objects inside the BSP, not files. `[Lights]` lists the rig
by name and each light then gets its own section.

Not every file follows this layout: a few, dated 1998 rather than 1999, have no
`BSP=` key and list models under `[MODELS]` after a bare count. Falling back to
the BSP of the same name handles those.

### Lights

| key | meaning |
| --- | --- |
| `Type` | 0 point, 1 spot |
| `Position`, `Direction` | scene space; direction is already unit length |
| `Color` | each channel 0–1 |
| `HotSpot`, `Falloff` | cone half-angles in radians, the fully lit core and the outer edge |
| `AttenStart`, `AttenEnd`, `UseAtten` | falloff range, and whether it applies |
| `CastShadows` | whether this light cast shadows in the bake |
| `Intensity` | multiplier on the colour |
| `Radius` | emitter size, used by the bake for soft shadows |
| `Overshoot`, `DecayType` | not yet understood; parsed and kept |

Across the corpus: **4,109 lights in 222 of the 229 scene assets** — 2,595 point
and 1,514 spot, 2,618 of them marked as casting shadows. Median 6 per lit scene;
`TE2B` has 148.

The names are the artists' own and carry intent that no analysis of a lightmap
would recover: `standing_lamp_omni`, `window_hot_spot03`, `moon(key)`,
`sky_bounce_`, `fill_light01`, `shadow_maker`, `only_for_camera_shot_bath01`.

This is why [ADR 0007](../adr/0007-authored-light-rigs-from-scene-assets.md)
supersedes the plan's assumption that light positions would have to be inferred
from the bakes.

### Skyboxes

Most scenes name only the faces the fixed cameras can actually see and comment
the rest out, so missing faces are normal rather than an error.

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
| `CINEMATIC_CAMERAS`, `DIALOGUE_CAMERAS` | same shape; a script may cut to any of them by name |
| `INSPECT_CAMERAS` | keyed by `noun=` rather than named, so not somewhere a script cuts to |
| `POSITIONS` | named spots with `pos`, `heading`, and the `camera` to cut to |
| `TRIGGERS` | `noun=` and `rect={x1,z1,x2,z2}` on the ground plan: standing in one does that noun's `WALK` |
| `REGIONS` | rectangles of the same shape, named rather than nouned; two in the corpus, read by nothing |
| `ACTIONS`, `AMBIENT` | bare file names, one per line: the `.NVC` action files in scope and the `.STK` soundtracks to play |

### Triggers

Thirty-four rectangles across twenty-nine files, and they are how the game says "step
closer and you will overhear them": the museum's `GET_CLOSE` behind the display panels,
the front desk of the lobby, the window into Arnaud's office, the lectures on the
Blanchefort tour. A rectangle names a noun and nothing else, and the verb it is looked up
with is always `WALK` — no file says so, because `Scene::Update` hard-codes it.

`Scene::Update` tests every frame rather than on the way in, and leans on the action's own
case to stop it happening twice: the museum's is
`GetNounVerbCount("GET_CLOSE","WALK")==0`, and its script increments that before it waits
on anything. Nothing new is started while an action is playing.

Two things the corpus's rectangles need from a reader. **The corners come in whichever
order the artist dragged them** — the museum's runs from z −400 to z −598 — and a
rectangle whose edges are the wrong way round contains nothing, so they are sorted on the
way in. And **two of them are mistyped**, both in `CSE212P`: one has a doubled comma, one
writes a number as `11.03.58`. The original reads both, discarding empty elements and
parsing with `stof`, which stops at the second point.

A walk the player asked for stops where it would step onto one, rather than crossing it —
`Walker::FindEarliestPathNodeInsideActiveTriggerRegion`, whose own comment gives the case:
in the lobby the way to the front door goes through Jean's rectangle.

`REGIONS` has the same shape with a name rather than a noun, and the corpus has two of
them: R25's `MYTEST` and RC1's `NEAR_EMILIO`. The reference reads them into a list and
never asks the list anything, so neither does this.

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
[`actions.md`](actions.md) for that grammar and for which files are in scope, and
[`soundtracks.md`](soundtracks.md) for what an `.STK` turns out to be.

**A section header need not close its bracket.** 114 of them in the corpus do not —
`[GENERAL={IsCurrentTime("106p") ...}` in RL2, CD1, CDB and a dozen others, some
`[ACTORS={...}` and one `[AMBIENT={...}`. The original accepts them, and requiring the
bracket does not merely lose the section: its lines fold into the section before, so a
conditional block reads as though it were part of an unconditional one.

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

### Pointing the camera

A camera angle is a **name the scene gives** — `OPEN_WARDROBE`, `LONG_FROM_STAIRS` — so
`SceneScripting` registers these beside the walker functions, and they mean nothing in
the next room. Four sections of the file declare cameras and three of them are named:
`ROOM_CAMERAS`, `CINEMATIC_CAMERAS` and `DIALOGUE_CAMERAS` are all fair game for a cut.
`INSPECT_CAMERAS` is a different shape — keyed by `noun=` rather than named — and
belongs to inspecting a thing rather than to pointing the camera somewhere.

| function | who it obeys |
| --- | --- |
| `CutToCameraAngle(name)` | cuts only if the player left cinematics on, or a script has insisted |
| `ForceCutToCameraAngle(name)` | cuts regardless — some things the story has to show |
| `GlideToCameraAngle(name)` | the same, taking a moment it cannot take yet |
| `SetForcedCameraCuts(n)`, `ClearForcedCameraCuts()` | the script's insisting |
| `EnableCinematics()`, `DisableCinematics()` | the player's preference |

The difference between the first two is the player's, and it is the original's own
design: somebody who does not want the story steering the view keeps it, right up to the
point where not seeing something would leave them stuck. `CinematicsEnabled` is in the
state hash for that reason — two runs made with different answers to it diverge, and a
harness should see why rather than wonder.

A camera the scene does not name is reported (`GK3R3202`) rather than ignored, and the
view stays where it was, as it does in the original. `AnyCameraNamed` does not fall back
to the default the way `CameraNamed` does: a script asking for a camera that is not there
is a mistake worth hearing about, not a reason to point the view somewhere else.

```bash
GK3Reborn.Tools render-scene --model CS3 --timeblock 212P --do WARDROBE:OPEN ...
```

Opening the wardrobe cuts to `OPEN_WARDROBE`, and with no `--camera` of its own the
render shows where the story left the view rather than where the scene starts —
`camera: OPEN_WARDROBE at (-93.9, 66.1, 266.5)`, looking straight at the wardrobe. The
doors are still drawn shut, because the animation the same script starts is not played;
what changed is the angle and the walk boundary.

### Putting something in the way

A boundary is painted once, before anybody knows where the van will park or which
wardrobe door will be standing open, so what *occupies* the floor at a given moment is
kept beside the bitmap rather than in it. Four script functions move things onto and
off it, registered by `SceneScripting` against one loaded scene because they mean
nothing outside it:

| function | what it does |
| --- | --- |
| `WalkerBoundaryBlockModel(name)` | shuts off the ground a named object stands on |
| `WalkerBoundaryUnblockModel(name)` | gives it back |
| `WalkerBoundaryBlockRegion(a, b)` | shuts two palette regions |
| `WalkerBoundaryUnblockRegion(a, b)` | opens them again |

The footprint is a box around everything the object is made of, flattened onto the
floor by throwing the height away — the original does the same, and it is coarse in the
right direction: an actor walks around a chair rather than through the gap under its
seat. The name may be a prop standing in the room or an object baked into the geometry,
since the scene files name both the same way.

Blocking changes what `IsWalkable` answers and what `WalkPath` may cross, but **not**
what `RegionAt` reports: what is standing on the floor is not what the floor is. It
does change what the overlay draws, because a rectangle a script has blocked off should
read as a hole — that is what it is to an actor.

Regions take two indices because a scriptable region is painted as an area *and* the
border around it, and moving one without the other leaves a wall a texel thick where
the doorway was. Anything may be shut and reopened; what a script may not do is open
something that was never open, since wall is wall whatever it says.

CS3's wardrobe is the case that names itself:

```text
WARDROBE, OPEN,  CLOSET_CLOSED, script={… wait WalkerBoundaryBlockModel("cs3_wrdb_dr_r");}
WARDROBE, CLOSE, CLOSET_OPEN,   script={… wait WalkerBoundaryUnBlockModel("cs3_wrdb_dr_r");}
```

```bash
GK3Reborn.Tools render-scene --model CS3 --timeblock 212P --do WARDROBE:OPEN --walk-overlay ...
```

opens it, and reports `cs3_wrdb_dr_r over (-31, 205) to (29, 278)` with six fewer
texels open than before. `--do` runs before anything that draws, so what an action
changed is in the picture rather than behind it.

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
- 845 name a soundtrack; 19,651 nouns hang on their objects, with 24,126 verbs
  available across the ones the action files know.
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

An actor with no `pos=` is placed by something other than the file, which is ordinary: 206
actor-and-timeblock pairs in the corpus are like that. **They are still in the room.** The
original only declines to *set* a position (`GKActor::Init`) and leaves everything else
alone; what places them is their `initanim=` or the script that walks them in. Skipping
them instead took Emilio out of the lobby and left the hotel door in the square outside
opening by itself.

The same goes for `hidden`, which is where several characters start: RC1 hides Emilio while
he is still indoors and the animation that walks him out turns him back on. A model that was
never read cannot be shown.

Naming a spot the scene does not define is a different matter and is reported as `SCENE011`;
it happens exactly once in the corpus, for the abbé at `MA1 303P`.

### Opening poses

`initanim=` on a model or actor line names an animation, and **it is a statement about where
the thing rests rather than something that happens**: RC1's copy of the hotel door is placed
by `Rc1PlaceLbyDoor`, Madeline is stood by her van by `MadRc1FigM`, Emilio is sat in the
lobby by `EmlLbyBreathe`. 316 lines carry one.

So the animation's opening frame is sampled and the animation is never played, which is
`Animator::Sample(anim, 0)` in the reference. The difference is not cosmetic: several of
these carry an absolute placement, and playing one takes seven seconds to arrive at a pose
that is meant to be true from the first frame — with the footsteps and the door sounds of
somebody walking in.

**Only the declaring model's own clip is sampled.** An animation is a schedule for as many
models as it likes, and an opening pose is one model's statement about itself: the lobby's
black marker opens with `GabLbyGetMarker`, which is a clip for the marker *and* a clip for
Gabriel picking it up. Sampling both put the player at the front desk before the scene had
begun. The reference passes the model's name to `Animator::Sample` for exactly this.

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

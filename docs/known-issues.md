# Known issues

Open defects and requested work, newest first. Each records how to reproduce it
and whatever was already established about the cause, so picking one up does not
start with rediscovery. Items marked **feature** are requests rather than bugs.

## 1. Several rooms of the hotel loaded and standing at once (feature) — dropped 2026-08-23

**Requested:** 2026-08-22. **Investigated, not attempted, and now dropped** on the
grounds the investigation itself argued: it buys very little the painted backgrounds do
not already buy, and it costs a room-keyed rewrite of navigation, interaction, audio and
the lightmap atlas, plus a hand-authored adjacency table and a suppression list for nine
thousand triangles of stand-in geometry. The 400 ms it would save at a door is not worth
that surface area of new failure.

**One of its prerequisites was built anyway**, because it was worth having on its own:
per-region light culling. `GpuLight.Capacity` is no longer 64 and no longer a cap on
anything — see [rendering.md](rendering.md#the-light-grid). What follows is kept as the
record of what was measured, since anybody reopening this should start from it rather
than from rediscovery.

The question was how well Gabriel's room, the hallway and the lobby would interconnect if
all three were resident together.

**They do not share a coordinate space.** Each location's `.BSP` is authored around its own
origin. The doorway between R25 and the hallway is at x 220.9-254.9, z 292.8-295.5 in R25
and at x 270.4-304.9, z 406.7-411.4 in HAL — the same 34-unit door, in two different frames.

A rigid transform between them does exist and can be recovered from the shared doorway: a
half turn about Y and then a translation of about (525.6, 0, 703.2) carries R25 into HAL's
space. Checking it against a third object agrees to within about two units — HAL's
`hal_r25_gbkg`, its own rendition of the room seen through the door, starts at x 195.0,
z 410.7 where the transform predicts 197.0, 409.6. **But nothing in the game's data states
it.** No `.SIF` key, no scene asset field, nothing. Each adjacency would have to be measured
by hand and written down, and where the geometry is symmetric — HAL's two staircases against
the lobby's two — a half turn and no turn fit the shared features equally well, so it cannot
be recovered automatically either.

**The rooms already contain each other, badly.** This is the deeper problem. The artists
solved "see the next room through the door" in 1999 with painted-in background geometry:

| in | object | triangles | is |
| --- | --- | --- | --- |
| R25 | `r25_hal_bkg` | 1,038 | the hallway, as seen from the room |
| HAL | `hal_r25_gbkg` | 865 | room 25, as seen from the hallway |
| HAL | `hal_r21_gbkg` … `hal_r33_gbkg`, `hal_clo_gbkg` | 8,487 more | the other seven doors |

**9,352 of HAL's 15,381 triangles are fake neighbours** — 61% of the hallway. Load HAL and
R25 together and the real hallway and R25's painted copy of it occupy the same space, which
z-fights; so does the room against HAL's copy of the room. Every `gbkg` and `bkg` object
would have to be suppressed on whichever side is real, and `SceneLoader.HiddenObjects`
already has the mechanism, but the naming is a convention rather than a declaration and
nothing marks which objects are stand-ins.

**Three engine assumptions are one-room-at-a-time**, in rising order of difficulty:

1. `SceneGeometry.AddScene` takes no transform and disposes the lightmap it holds before
   packing a new atlas, indexing it by the new BSP's own surface indices. A second scene
   silently unlights the first. Needs a transform argument and a per-batch atlas index.
2. `WalkBoundary` covers one room — R25's is 369x386 units — and so do `WalkFloor`,
   `ScenePicker`, `ActionResolver`, `SceneAudio` and `GameState.Location`. Each would become
   a set keyed by which room a point is in.
3. ~~**The light rig is the hard cap.** `GpuLight.Capacity` is 64 and `Choose` keeps the
   brightest by intensity across the whole scene. R25, HAL and LBY declare 62, 92 and 41
   authored lights: 195 between them, so two thirds would be dropped and the room the player
   is standing in could lose its lamps to a brighter fixture two rooms away.~~ **Fixed
   2026-08-23.** The rig is a storage buffer holding a thousand, and a fragment loops the
   lights that reach the cell it stands in rather than the whole rig: the lobby's 41 come
   out at 4.8 to a cell and the hallway's 92 at 20.1. Nothing is dropped and nothing is
   ordered by a fixture two rooms away.

**The cost is affordable; the work is not small.** Measured at 110A without `--enhanced`:

    R25   10,461 triangles    925 surfaces   120 textures    9 MB   62 lights   390 ms
    HAL   15,381 triangles  1,924 surfaces   186 textures   14 MB   92 lights   405 ms
    LBY    8,887 triangles    952 surfaces   103 textures   10 MB   41 lights   414 ms

About 35,000 triangles and 33 MB of textures for the three, which is nothing. With
`--enhanced` it is not nothing: R25 and the hallway together already hold 933 MB of textures
on the device, and that is two rooms in sequence rather than three at once.

**What it would buy.** Very little that the painted backgrounds do not already buy, because
the original was designed so that the only thing you ever see of the next room is what the
artists painted through the doorway. The case it would buy is a door that opens onto a room
you can then walk into without a load — and the load is 400 ms.

**If it is picked up, the order is:** per-region light culling first (item 3, and it is
worth having on its own for TE2B's 148 lights); then a transform and a shared lightmap atlas
on `AddScene` (item 1); then a room-keyed navigation and interaction set (item 2); then a
hand-authored adjacency table with the transform per doorway; and last the suppression list
for the stand-in geometry.

## 2. The Eglise/Church sign reads wrong on RC1's signpost

**Reported:** 2026-08-21. **Cause found; the fix is content, not code.**

Not a mirroring, and nothing in the game's data is wrong. RC1's `rc1_signpost` carries three
arms — church, museum, Villa Bethania — each a flat quad with a front texture and a `…BK`
back texture. Checked in the BSP: all six faces are wound opposite their partner, all six
run their U axis from the post towards the tip, and all six therefore read left to right
from the side they face. The originals are correct too: three fronts with the arrow at
u=1 and three backs with it at u=0.

**`enhanced/textures/RC2CHRCHSIGN01.PNG` is a bad upscale.** The original keys the two
corner wedges beyond the arrow's point to the dark teal of the sign's own border, so the
board reads as an arrow. The upscale repainted both wedges as opaque pale filigree, so the
board reads as a full rectangle with an ornate plate on the end and the arrow's silhouette
is gone. Measured as the pale board's height over the last twelfth of the image: 0.11 in
the original, 0.67 in the enhanced. The other five match their originals to within 0.02.

Reproduce:

```bash
GK3Reborn.Host --scene RC1 --timeblock 110A --camera FR_LBY
```

and compare `normalized/textures/RC2CHRCHSIGN01.png` with the enhanced one beside it.

**The fix is to regenerate or refuse that candidate.** `import-textures` has no check that
would have caught it: its checks are dimensions, aspect ratio and alpha, none of which
this violates. A silhouette check — comparing the candidate against the original over the
region the original paints in its background colour — is the shape of the missing test. A
first attempt at a general composition metric (a median-luminance mask over the whole
image) scored a median of 11.4% across all 7,462 enhanced textures and did not put this one
in its top twenty-five, so the check has to be about the background region specifically
rather than about the picture as a whole.

## 3. HDR output (feature)

**Requested:** 2026-08-19.

Output in high dynamic range where the display supports it, with settings for the
display's characteristics — maximum luminance and the rest.

**What already exists.** `VulkanDeviceSelector` detects `VK_EXT_hdr_metadata` and
reports a `HighDynamicRange` tier; an RTX 5090 already comes back as HDR-capable.
Nothing consumes that yet. `Plan/01-architecture.md` section 5 lists HDR among the
display settings, and `Plan/README.md` requires that HDR never prevent raster play,
so it must stay switchable off on hardware that claims support and handles it badly.

**The actual blocker is not the extension.** The pipeline currently shades in linear
space and writes straight to an 8-bit sRGB target with no tone mapping — the
hardware does the sRGB encode on write and that is the whole of it. HDR needs the
chain in between:

1. Render to a floating-point target (`R16G16B16A16_SFLOAT`) instead of 8-bit sRGB,
   so values above white survive to the end of the frame. Ray-traced lighting
   already produces them; they are being clipped today.
2. A tone-mapping pass, with an SDR curve and an HDR one. The SDR path must keep
   looking as it does now, which makes this a good place for a regression image.
3. Pick an HDR swapchain colour space —
   `VK_COLOR_SPACE_HDR10_ST2084_EXT` for PQ, or
   `VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT` for scRGB — from what the surface
   actually offers rather than from what the extension implies.
4. Set the mastering metadata through `VK_EXT_hdr_metadata`.

**Settings it needs.** Maximum display luminance in nits, paper-white level (the
one users notice most: it decides how bright the UI and a lit wall sit), minimum
luminance for the black end, and the colour space or transfer function where the
display offers a choice. None of these can be inferred reliably from the display,
which is why they are settings; a calibration screen showing a clipping pattern is
the usual way to let someone set them by eye.

**Note on the existing exposure choice.** The lightmap multiplier is currently the
original's gamma-space 2, raised to compensate for linear-space shading. That
constant is an exposure decision made against an 8-bit target, and it will need
revisiting once there is a real tone mapper rather than an implicit clip at white.

---

## 4. An exterior has no sun, so nothing standing in one casts a contact shadow

**Reported:** 2026-08-23, out of the fix below. **Not fixed.** Reproduce with

    GK3Reborn.Tools render-scene --model RC1 --timeblock 110A --rt high --output rc1.png

and look at the ground under the woman by the van: she meets it with no shadow at all,
while the same build gives a character in `LBY` a shadow on the wall behind him.

**It is the rig, not the tracing.** `rc1_a_m.SCN` ships **seven** authored lights for the
whole town, against `LBY`'s forty-one, and four of them cast. Outdoors the artists left
nearly everything to the bake, so once Medium and High stopped using it there was no key
light overhead left to throw a shadow down. `RC1`'s mean frame luminance is 55.7 at High
against 75.8 at None, and that gap is the same missing light.

Ambient occlusion is doing what it can — believing 0.85 of it rather than 0.55 is worth 7
points of that mean — but occlusion attenuates the ambient term only, which is correct and
is not a shadow.

**What would fix it:** a sun and sky light synthesised for exterior scenes, from the
timeblock's hour and the scene's own skybox, added to the rig rather than to the shader.
That is a scene-loading change and wants its own decision record, because it is the first
light in the game no artist authored.

## 5. A walk to something far away could be run rather than walked (done 2026-08-23)

Kept here only to record where the threshold is. A walk the **player** asked for — a click on
the floor, or the approach in front of an action — picks up the pace by itself past 250 scene
units, a little over six metres, using the same `HurryFactor` a double-click uses. A player
who has turned that down to one has turned this off with it.

A walk a **script** asked for never does. Their timings were written against the pace the game
walks at, and a cutscene that arrives early is a cutscene with a gap in it.

## Closed

### The camera could get stuck in the geometry — fixed 2026-08-23

The collision was already a swept sphere against the scene's own camera-bounds shells, and
it already slid along a surface rather than stopping dead. What it had no answer for was a
sphere that was **already** overlapping when a step began.

Once overlapping, the rule that makes the shell work turns against it: a step towards a
surface's front is refused and a step along it is allowed, so the camera slid along inside
the wall indefinitely with its near plane through it. Reproduced with a camera one unit from
a wall that wants sixteen — it stayed at that one unit for every step thereafter.

Cameras arrive there routinely. A scene cuts to a viewpoint the artists placed against the
room's own walls rather than against a shell sixteen units thick, `CameraBoundaryBlockModel`
parks a van where the camera is standing, and a step can settle a fraction inside.

`CameraBounds.Free` now pushes it back out, at both ends of a step. Out of the deepest
overlap and then look again, rather than summing every push — summing overshoots in a
corner, sending the camera out through the third wall. Bounded at four passes, because a gap
narrower than the camera cannot satisfy both its sides and best effort beats hanging. A
camera on the far side of a surface is left alone: it is outside, and the way back in is
what the sweep keeps open.

### Inspect was offered for everything and did nothing — fixed 2026-08-23

Reported as "Inspect / Inspect Undo, and inspect didn't even inspect".

Only 111 close-ups are authored across the corpus, against the thousands of nouns a player
can point at. `[INSPECT_CAMERAS]` had nothing to say about most of them, so the view stayed
exactly where it was — and because inspecting still counted as having happened, the menu then
offered a way out of something that had never started.

Two changes. A close-up is now worked out from the object's own bounds where none is
authored, which is what the original does and what the code's own diagnostic said it did not:
the box the thing occupies is measured in the room's space and the camera is put in front of
it, along the line the view was already on, at a distance that fits it in a forty-degree
frame. An authored close-up still wins — the artists chose an angle, and this only chooses a
distance. And neither verb is offered at all for a noun with no geometry to frame.

### Clicking during dialogue sent Gabriel walking — fixed 2026-08-23

A click on the floor while somebody was speaking started a walk across the room behind the
conversation. It now cuts the line short and starts the next one instead, which is what a
click during dialogue means in every game of this kind — the original has no way to skip a
line, which is a limitation of 1999 rather than a design anybody would choose. The rest of
the run is kept, because skipping a line is not abandoning the exchange. Clicks on the
interface and clicks in an open menu still mean what they meant.

### An action's script ran before the actor finished turning — fixed 2026-08-23

Reported as the coffee pot: it began pouring in the air before Gabriel reached the table.

`ActionRunner` holds a script back by exactly the number `Walker.Seconds` returns, and that
number was the ground to cover divided by the pace — the turn at the end of the walk was not
in it. So the script started the moment his feet stopped, with him still coming round to face
what he had walked to. Half a turn at six radians a second is a little over half a second,
which is long enough to watch. `Seconds` now includes the arrival turn, worked out for where
the walk ends rather than where the actor is standing.

### Nobody started with anything in their pockets — fixed 2026-08-23

Prince James's card is where the number Gabriel dials comes from, so a player without it could
not use the pay phone and Day 1 10am could not be finished at all.

Nothing in the shipped data hands these out. No barn holds a list of starting items and no
scene script gives one over: the table was compiled into the original executable, the same way
the score table was, and G-Engine hardcodes it too with a comment saying it ought to be
data-driven and that its author could not find where. The engine now carries it as
`Assets/Story/Pockets.txt` — eight items for Gabriel, four for Grace. Given once when a game
starts; loading a save empties the bag first, so a restored game is unaffected.

### Models cast no shadow on the room — fixed 2026-08-23

Reported: the newspaper and the armchair cast nothing, and a character in the loveseat had
no contact shadow.

Nothing was excluded from the traced world and the instance masks were right. **The cause
was that the ray-traced path used the baked lightmaps at full strength.** A bake is light
computed once for a room with nobody in it; a dynamic shadow can only take away the share of
a surface the rig accounts for, and the bake was holding the rest — so in an interior the
share left to darken was small.

The note this issue used to end on said the lever could not be pulled because applying the
per-tier `bakedWeight` cost 22% of the frame's brightness. It was the wrong lever. Scaling a
bake down throws away the light the rig has not got along with the light it has. What
Medium and High do now is drop the bake outright and light the room from the artists' rig —
`Plan/04` P10 and ADR 0006, which had specified this from the start — with an ambient floor
raised to 0.26/0.28/0.30 to stand in for bounce, and traced occlusion believed at 0.85
rather than 0.55 now that there is no bake to count it twice against.

Measured on `LBY` at `GabEmlWide`: mean 52.7 at High against 54.4 at None, a 3% difference,
with the share of the frame below an eighth of full brightness at 17.9% against 16.6%. The
room is not darker; it is shadowed. Exteriors are a separate matter — see item 4.

The armchair was the separable question it looked like: it is `type=scene`, part of the BSP,
so its shadow lived in the 1999 bake and is now cast like anything else.

### Emilio's newspaper hung in the air while he shook hands — fixed 2026-08-23

A GAS file may declare what to do if it is interrupted:
`USES CLEANUP EmlLbyOpnPaper emllbyclspaper` means "if you stop me while I am reading the
paper, close it first". 328 of the corpus's 341 `USE` lines are these. **The port parsed
them, had a `CleanupFor` and a test for it, and never called it.**

So `StopFidget("Emilio")` before the handshake stopped the script mid-read and the paper
stayed where his hands had been. A behaviour now remembers which animation it last started —
a stopped script cannot be asked afterwards what it was in the middle of — and playing the
cleanup is what stopping it means. Cleanups chain, since one may have a cleanup of its own,
bounded in case a file cleans up in a circle.

### A conversation happened off camera — fixed 2026-08-23

Reported: talking to Emilio left the view pointing across an empty room.

**There was no faithful answer available.** `SetDefaultDialogueCamera` is a no-op in the
reference; the lobby's introduction to Emilio calls it and then starts talking without ever
naming a conversation, so the reference's own hook — cut on `SetConversation` — never fires
for this exchange either. The port had the state and read it nowhere.

Three answers now, in order: the conversation's own `initial` camera where the scene names
one; the camera a script asked for; and otherwise **whichever of the scene's cameras best
holds both speakers**. The third is the port deciding for itself, and it decides between
shots the artists framed — a camera is scored by its worst-placed speaker rather than the
average, because a shot that frames one person beautifully and leaves the other out is not a
shot of a conversation. Where no authored camera can see both, the view is left alone: a bad
cut is worse than no cut. Nothing is invented, which is what `Plan/03` §5 asks.

Chosen once per exchange rather than per line, or the camera would jump every time somebody
drew breath, and never while cinematics are switched off.

`[DIALOGUE_CAMERAS]` lines now keep their `dialogue=`, `initial`, `final` and `fov=`, none of
which was read before — `initial` in particular is a different flag from the `default` that
says where a scene starts, and reading them as one would open every conversation wherever the
room does.

### The story could not get past Day 1, 10am — fixed 2026-08-23

The clock never moved. **No script in the game's own archives calls `SetTime` or
`SetLocationTime` at all**, so reading the corpus alone gives no way to find the mechanism;
it looked as though timeblocks simply were not scripted.

Traced through the C++ reference, the arrangement is this. `LocationManager::ChangeLocationInternal`
runs `Timeblocks.shp:CheckTimeblockComplete$` on **every change of location**, after the new
location is current and before the new scene loads. If that script moved the clock,
`IsChangingTimeblock()` is true and the location change stands aside — the timeblock change
does the loading. So a timeblock ends as the player walks through a door, not the moment they
finish the last thing in it, and 110A's first line is "must be at RC1".

`Timeblocks.shp` is **not in the game's data** either: the original kept these rules in its
executable. What they are is written down in the design document the game shipped with,
`TIMEBLOCKBIBLE.TXT`, one "Completion Rules" list per timeblock. The engine now carries the
script — as source, since it is a set of rules somebody may want to read, and the engine has
a Sheep compiler — adapted from G-Engine under GPL-3; see NOTICE. It compiles to 18
functions and 995 instructions, and **every function it calls is one the game's own scripts
call too**, so the rules are checkable against the corpus rather than being a private
language.

Measured end to end: with 110A's eight requirements met, walking out of the hotel ends the
morning and opens `RC1112P.SIF` — a different cast, different light, "Day 1, 12pm - 2pm" in
the corner. Where a timeblock has a closing film, it plays: four of the sixteen do.

`--did` marks a timeblock's requirements as met, for looking at what happens next without
playing the two hours in front of it.

### Nine more calls the scripts made into nothing — fixed 2026-08-23

Working down the recorded list by how often the game actually calls each:

- **`ActionWaitClearRegion`** (112) — get out of the way. The walk boundary is a
  palette-indexed bitmap and a region is one of its indices, so the test is a lookup: in the
  region, walk to the spot named; not in it, nothing to do.
- **`CameraBoundaryBlockModel`** and its three relatives (102) — the shell the camera may not
  leave. The artists draw one per room and a script adds to it, or turns it off for a shot
  that has to be outside it. Turning it off lasts until the next room, which is the
  original's behaviour and what the scripts that never turn it back on rely on.
- **`SetWalkAnim`** (42) — somebody walking differently for a while. The two turn animations
  it also carries are read past: turning on the spot is the walker's job here, not a clip's.
- **`StartMom`** (37) — a momentary animation, a shrug or a glance up. The asset is localised,
  so the name is `E` and what the script said.
- **`StartVerbCancel`/`StopVerbCancel`** (14) — whether the player may walk away from the
  action bar. `MustChooseAnAction` was state nothing read; a modal menu now stays up.
- **`StartPropFidget`/`StopPropFidget`**, **`GlideToCameraAngleX`**.

**Six that stay recorded, on purpose.** `Glance` and `GlanceX` are eye offsets and nothing
here has eyes — they are commented out in the reference too. `SetCameraAngleType` logs its
arguments and returns. `StartMorphAnimation` and `StopMorphAnimation` are commented out.
`UploadSceneLightmaps` has nothing to do because lightmaps are uploaded with the scene.
Reproducing a no-op faithfully means leaving it a no-op, and the list says which are which
now so a reader can tell them from the gaps.

Recorded calls the game makes are down from **82 functions and about 3,600 calls to 23 and
501** — and 317 of those 501 are `SetTimerSeconds`, which is a script sleeping and correctly
has nothing to do but take the time.

**Still genuinely missing**, in order of how often they are called: model shadows
(`EnableModelShadow`/`DisableModelShadow`, 54) and `SetModelLighting` (23), both of which
want renderer work; construction mode (`AddModel`, `AddActor`, `AddPosition`, `SetScene`, 28
between them), which builds a scene from a script rather than a file; and the two end-of-game
screens, `ShowDeathLayer` and `FinishedScreen` (6).

### The score was always nought — fixed 2026-08-23

`ChangeScore` takes the **name** of a score event — `ChangeScore("e_110a_lby_read_register")`
— and the engine read it as a number, so all **321** calls in the corpus awarded zero.

What each event is worth is not in the game's data at all: there is no such file in any of
the eight barns, because the table was compiled into the original executable. The engine
carries it now, in `Assets/Story/Scores.txt`, adapted from G-Engine's reconstruction under
GPL-3 (see NOTICE). An event scores once; the set earned is part of the state, which is what
makes the score survive a reload and what a timeblock's completion rules will read.

Checked against the corpus: of **281** score names the scripts pass, **278** are in the
table. The three that are not are listed in the file, and score nothing rather than a guess —
the table sums to 948 against the game's documented 965.

The score is drawn in the corner of the screen, in the game's own words: `ScoreText = Score:
%03d of %03d` out of `ESTRINGS.TXT`.

### Nobody ever changed expression — fixed 2026-08-23

`SetMood` and `ClearMood` were recorded and dropped, and between them they are **2,442**
calls, the largest single thing the scripts asked for and did not get.

A mood turns out to be small: it is two animations rather than a state. `gabangryon` puts it
on and `gabangryoff` takes it off, and the names are the character's own three letters plus
the mood. **Those are the face's letters and not the model's** — the lobby places Simone as
`sim_` and her animations are `simsleepon` and `simsleepoff`, so building the name from the
model gives `sim_sleepon`, which is nothing at all.

Setting one clears the last, because they are worn rather than stacked, and which one is worn
is part of the state.

### A script could not show or hide part of a room — fixed 2026-08-23

`ShowSceneModel` and `HideSceneModel` were recorded and dropped: **287** calls. They are not
the same as `ShowModel`/`HideModel`, which are about a model the scene loaded from a file of
its own. These are about the room — a curtain, a van, a door — which is one mesh with names
over runs of its surfaces.

The original renders surface by surface and carries a visible flag on each. This port batches
by texture, so the batches are now cut along the object names as well and a batch carries the
flag. That costs some batching: RC1 goes from 308 draw calls to 566, LBY from 207 to 247.

**A hidden object's geometry is loaded now rather than dropped at build time** — the same
mistake this file has recorded twice before, and there is no showing something that was never
read. Two things follow from having it in the buffers and not in the picture: it must not
block a ray, or a hit-test slab stands a wall of shadow across a doorway; and it must not
grow the room's bounds, which the light grid is divided over. `TriangleCount` is what is
drawn; `LoadedTriangleCount` is everything.

### Everybody walked in silence — fixed 2026-08-23

Three files decide what a step sounds like and none of them was read. `FLOORMAP.TXT` sorts
283 floor textures into carpet, tile, wood, concrete, dirt and grass; `FOOTSTEPS.TXT` and
`FOOTSCUFFS.TXT` give three sounds for each pairing of that with a shoe type, 72 pairings
between them; `CHARACTERS.TXT` says which shoes each character wears.

The animations already said **when**: a walk clip carries three or four `FOOTSTEP` nodes to a
stride in its `[GK3]` section, 3,704 across the corpus, all read past.

**Walking was the case that needed the most work**, because a stride is not played through
`Play` — it is looped by frame in `WalkCycle`, which carries no schedule — so nothing could
notice its footstep nodes. The cycle reports which feet went down between the frame it last
drew and this one, which is a range rather than an equality: a stride is twenty frames, a
frame of the game is a sixtieth of a second, and at any pace above walking an equality misses
steps.

Gabriel crossing the lobby now makes fourteen `MCarpBoot*` noises, which is male boots on
carpet.

### Things that change what they show — fixed 2026-08-23

`[MTEXTURES]` was parsed into nothing, in 168 animations: Larry's alarm clock counting, a
monitor changing what it shows, a sign that lights. The node names a mesh group and a submesh
rather than a texture to replace, so the original is looked up from the model and used as the
handle the sink repaints by.

The replacement is read and uploaded on first use, because the scene loaded only what its
models were painted with — and kept, since a clock swaps through ten digits.

An animation whose whole content is a texture swap or a footstep is doing something, and no
longer reports itself as an animation that moves nothing.

### Emilio came out of the hotel and stood there, with nothing to click — fixed 2026-08-23

Reported after the fix below put him in the square at all. Four separate things, each of
which would on its own have left him standing in the doorway.

**A hotspot was tested against the pose the artist modelled, not the pose being drawn.**
`ScenePicker` baked each mesh group's own transform into its triangles and then moved the
ray by the model's placement. That is right for walking, which moves the placement, and
wrong for everything a clip does: a clip *replaces* each group's transform and the placement
is applied on top. So Emilio sat in the lobby's loveseat with his hotspot still standing in
the middle of the room, and nothing an animation had moved could be clicked where it was.
The triangles are kept per mesh group now, untransformed, and the ray is taken into each
group's own space — the same trick already used for the placement, one level down.

**`WalkToAnimation` was reading its second argument as a place.** It is an animation: walk
to where that animation *begins*. The engine already had `WalkToAnimationStart` for
`approach=ANIM` and this was not wired to it, so all **165** calls in the corpus quietly did
nothing.

**`CHARACTERS.TXT` was looked up by whatever name the script used.** It is keyed by the
three-letter model code, and the fallback of taking a name's first three letters works only
where the two agree. `GABRIEL` gives `GAB` and does; `EMILIO` gives `EMI` and his section is
`[EML]`. Every question about him — his hips, how tall he is — answered "no such character".
The model name behind the noun is resolved first now.

**An absolute animation was giving back the ground it covered.** The original writes it as
one line — `allowMove = allowMove || absolute` — and it follows from what absolute means:
the clip says where in the room it happens, so putting the actor back where they were undoes
the only thing the clip was for. Emilio was returned to the spot he stood on before he
opened the door.

And where an absolute clip *has* carried somebody is a **place**, not a distance. It is read
off the triad under their hips, as `AnimationStart` already does for `approach=ANIM`. The
average of a character's mesh-group origins moves with the same rigid motion, so a
difference of two averages is exact and cheap — but one average on its own is that answer
plus the constant between a torso's middle and the floor, which is why the walk to the bench
set off from a couple of feet behind him.

### Nobody a script gave an idle to ever moved — fixed 2026-08-23

Found while chasing the above. **A behaviour script named without an extension read
nothing.** A scene file writes `idle=jeaIdle.gas` and a script writes
`SetIdleGAS("Emilio", "Eml110aBenchIdle")`, and **all 168** names the scripts pass are the
second kind — so `SetIdleGAS`, `SetTalkGAS`, `SetListenGAS` and `NEWIDLE` between them
handed out nothing at all, and the character stood still.

The same shape as the soundtrack that names `R25Theme1` and means `R25THEME1.WAV`. An
extensionless name is retried with `.GAS`.

### Emilio was not in the lobby, and the hotel door opened by itself — fixed 2026-08-23

Reported: no NPC in the hotel lobby; and on stepping out of the hotel, a door animation
plays a couple of seconds after Gabriel arrives, with a door sound and nobody there.

**One cause, two symptoms, and neither of them was about doors.** `SceneLoader.PlaceActors`
skipped an actor twice over: one with no `pos=` on its line, and one the scene declared
`hidden`. Both are ordinary — 206 actor/timeblock pairs in the corpus have no position, and
`hidden` is where several characters start — and the original skips neither. `GKActor::Init`
only declines to *set* a position; what places the actor is its `initanim=` or the script
that walks it in.

Emilio is one of each. `LBY110A.SIF` gives him no position and an `initanim=EmlLbyBreathe`
that sits him in the lobby's loveseat; `RC1110A.SIF` declares him `hidden` until the moment
he comes out of the hotel. So the lobby had one fewer person in it than it said, and RC1 had
nobody to show.

Three things were missing behind that:

- **`initanim=` was parsed and never applied**, on 316 SIF lines. It is a statement about
  where a thing rests rather than something that happens, so its opening frame is sampled
  and the animation is never played — `Animator::Sample(anim, 0)` in the reference. Without
  it the lobby's copy of the front door, Madeline's map and bag, and every seated character
  stood in their bind pose. `SceneUpdate.Open` does it now, before `SCENE:ENTER` runs, and
  `render-scene` does it too: an init anim takes no time, so unlike everything else that
  tool leaves out, it belongs in a single frame.
- **`[MVISIBILITY]` was not read**, in 208 of the game's animations. It is how somebody who
  is not in the room walks into it: `EmlRc1ExitLobby` opens the hotel door on one line and
  turns Emilio on with another. The door swung and made its noise because those are an
  `[ACTIONS]` clip and a `[SOUNDS]` cue, which were read — so the failure looked like a
  door opening by itself rather than like a missing person.
- **`IsActorNear` and `IsWalkingActorNear` always answered "no".** RC1 waits for Gabriel to
  walk away from the hotel door before sending Emilio out of it, and polls that every two
  seconds; answering "no" sent him out immediately, through the door Gabriel was standing
  in. 96 conditions across the corpus ask one of the two.

`[OPTIONS] FRAMERATE` is read now as well. Thirty animations name a rate between 5 and 580
and all of them were played at fifteen.

### The wrong line, and half the action files out of reach — fixed 2026-08-23

Not reported; found while checking the above. **`ActionResolver` took the first rule in file
order for a noun and verb.** The original scores the *case* instead and takes the highest —
catch-alls lowest, a timeblock's override above them, a condition somebody actually wrote
above that, and "the first time you did this" above everything. The lobby writes `REGISTER,
LOOK, GABE_ALL` above `REGISTER, LOOK, NOT_SEEN_REGISTER`, so looking at the register for
the first time gave the line Gabriel says about one he has already read.

Three more of the same kind, all measured against the reference's `ActionManager`:

- **`ANY_OBJECT` was not a wildcard noun.** `ANY_OBJECT, LOOK, ALL` is the game's answer for
  looking at anything nobody wrote a line for, and it was silence instead.
- **`ANY_INV_ITEM` was not a wildcard verb**, so using an item on something with no rule for
  that pairing did nothing rather than saying so.
- **Eight nouns that stand for two people were unknown**: `LADY_H_ESTELLE`, `GRACE_N_MOSE`,
  `GABE_N_MOSE`, `WILKES_N_BUCHELLI`, `TWO_MEN`, `BUTHANE_MOSE_BUCHELLI`, `DEAD_CLOTHES`
  and `DEAD_THROATS`. Nothing in the data declares the equivalence; the reference hard-codes
  the same list and says so.

`check-scenes` counts **36,723** verbs available across the corpus where it counted 24,126,
all of them still with a script the runner can perform.

### The inventory was a picture of an inventory — fixed 2026-08-23

`GameHud.ItemAt` existed and nothing called it, so the strip along the foot of the screen
could not be clicked. Worse, all **619** actions in `INV_ALL.NVC` were unreachable: every
one is guarded by `ALL_INV`, `GABE_ALL_INV` or `GRACE_ALL_INV`, all three of which are
`IsTopLayerInventory()`, and nothing ever put the inventory on top.

Now: clicking a slot takes the thing in hand, clicking it again opens it close up, and the
close-up lists what can be done to it — which is where those 619 actions live. On a thing in
the room the menu offers **Use...**, and choosing it opens a second column of the things in
the bag that this noun answers to. Only the things actually carried: an item verb is written
exactly like an ordinary one, so without `VERBS.TXT` to tell them apart the menu offered
Buthane a wallet Gabriel had not found yet.

### Exits called Exit3, and a corner that read LBY - 110A — fixed 2026-08-23

`ESTRINGS.TXT` was unread. It names all 79 locations and all 17 timeblocks, and the driving
map was scraping its own third of it with a hand-rolled parser.

RC1's ways out are `EXIT`, `EXIT1` to `EXIT5`, numbered in no order anybody could infer, and
the interface drew the number. An exit is now called after the place it leads to, read out
of its own rule — `EXIT3` runs `SetLocation("rc3")` and `loc_rc3` is "Rennes-le-Château:
Outside Church". One that opens something other than a room, like RC1's `EXIT5` raising the
driving map, is called "Exit" and nothing more.

### Inspecting the register followed you into the next room — fixed 2026-08-23

Reported: leaving the lobby for the phone room and coming back opened on a close-up of the
register rather than on Gabriel. The register had been inspected first, which was the whole
of it.

`InspectObject` sets `GameState.Inspecting`, and **only a script could clear it.** Nothing
did: not walking away, not clicking elsewhere, and not leaving the room — so every room
after it aimed its camera at a thing that was not in it. The original never has this problem
because it puts the way out on the bar itself: `Scene::OnClicked` adds `INSPECT` to every
noun it shows a bar for, and `INSPECT_UNDO` in its place while that noun is the one being
inspected. Both verbs are in `VERBS.TXT`; neither is in any action file, which is why
reading the files alone never found them. The port offers both the same way now, and a
change of room clears the close-up regardless.

### Gabriel talked to Emilio from across the lobby — fixed 2026-08-23

Reported alongside the above. Asking somebody about something took two steps in the
original: `TALK`, which carries the approach — `EMILIO, TALK, DIALOGUE_TOPICS_LEFT,
approach=ANIM, target=GabEmlLbyShake` walks Gabriel over and shakes his hand — and then a
list of topics, which carry no approach because by then he is already standing there.

This port puts the topics straight on the menu, which is the improvement `docs/screens.md`
asks for, and it dropped the walk along with the step it replaced. A topic now borrows the
approach of the Talk it was hoisted out of. Only the approach: the script it runs is its own
and untouched, which is what `Plan/03` §2.3 requires of anything that modernises input.

### A character reset halfway across a room — fixed 2026-08-23

Reported: Gabriel's position sometimes resets while walking. **An idle fidget could fire
mid-stride.** `SceneUpdate.Play` cancelled any walk in progress whatever asked for the clip,
and an ordinary clip gives back the ground it covered when it ends — so the walk stopped and
the walker was put back where the idle had started.

The original exempts a character's own script by name, and says why:
"we don't want to cancel the turn part of a walk due to a breathing anim"
(`GKActor::StartAnimation`). Two rules now, both the reference's: a behaviour clip never
cancels a walk, and nothing a model does on its own runs while it is crossing the room. Both
are a pause rather than a stop, so the idle carries on from where it was when the walk ends,
as `Walker::OnWalkToFinished` does.

**And an opening pose was sampling every clip in its animation.** The lobby's black marker
declares `initanim=GabLbyGetMarker`, which is a clip for the marker and a clip for Gabriel
picking it up: sampling both stood the player at the front desk before the scene had begun,
and the room's own entry script then moved him again. An opening pose is one model's
statement about itself, and only that model's clip is sampled now — the third argument to
`Animator::Sample` in the reference, and the reason it is there.

Measured afterwards, the lobby at 110A opens with Emilio at 9, 41 in the loveseat and Jean
at 431, 255 on her mark, and nothing has touched Gabriel. `--frames 60` prints it.

### Gabriel came and went in the dining room, and the newspaper hung in mid-air — fixed 2026-08-22

Reported: the scene where Gabriel first meets Mosely. Gabriel keeps disappearing and
appearing, and Mosely's newspaper floats beside him rather than being held.

One cause for both. **Two clips were posing one model at the same time.** `SceneUpdate.Play`
appended to its playing list and never took anything off it, so an animation the story
started and an animation a character's idle started both wrote the same mesh groups every
frame. Which one the eye saw was decided by list order, and the order changed every time one
of them ended and another began.

DIN110A is where that is worst. Nothing stops Gabriel's `gabIdle.gas` for the coffee scene —
`StopFidget` is called for Mosely and not for him — so his breathing and his fidgets went on
choosing clips for the whole two minutes of it, each one fighting `GabDinCoffeeShake`,
`GabDinCoffeeGet2` and the rest for where his mesh groups were. Mosely's `mosPaperIdle.gas`
did the same to the double-take: `MosDinPaperFig` holds the paper up in front of his face and
`MosDinPaperLowerA` lowers it, and the two were running over each other.

The original has three rules here, all in `GKActor::StartAnimation`, `GKProp::StartAnimation`
and `VertexAnimator::Start`, and the port now has all three:

- **One clip at a time per model.** Starting one stops whatever that model was playing.
- **A behaviour script never overrides the story.** An idle asking for a clip on a model the
  story is already animating is dropped, not queued.
- **The story holds a model's own script while it animates it, and gives it back after.**
  A pause rather than a stop, so a character goes back to breathing where they left off. A
  script parked waiting out a clip that was taken from it carries on as soon as it has its
  model back, which is what the original's paused player does with the next-node request its
  stopped animation left behind.

Reproduce:

```bash
GK3Reborn.Host --scene DIN --timeblock 110A --frames 1800 --screenshot before.png
```

Frame 1800 is about eleven seconds in. Before: the paper hangs to Mosely's right with his
arms down. Frame 4200, about twenty-five seconds in: Gabriel is not in the picture at all.


### Nouns stayed where an actor had been standing — fixed 2026-08-22

Reported: Gabriel walks across the room and his hotspot does not go with him. The pointer
finds him on the spot he set off from, and finds nothing where he is.

`ScenePicker` gathered every placed model's triangles into world space once, when the room
loaded, using the transform the scene placed it with. Nothing moves an actor by that
transform: `SceneUpdate` walks them by handing `ISceneSink.MoveModel` a new one every frame
and `PlacedModel.Transform` is never written again. So the ray met a room-shaped snapshot of
where everybody had been at load. The same staleness aimed `LookitActor`: `SceneScripting`
measured a target's middle through the placement, so an actor was looked at where they used
to be.

A model's triangles are now kept in the model's own space and the *ray* is put through the
inverse of where it is standing now — `PlacedModel.Standing`, which asks the sink. That is
one 4×4 inversion per model per pick against fifteen thousand triangles of room, and it
means a walking actor costs nothing to keep up with. Distances survive the trip: an affine
transform carries the point at *t* along the ray to the point at *t* along the transformed
ray, so a hit in a model's own space is at the same *t* in the room — which is what lets a
scaled actor and a wall be compared for which the ray reached first.

Still approximate in one way. The triangles are the bind pose, so a character's *shape*
does not follow their animation — a clip that deforms them well away from their own origin
is picked against where the artist modelled them. Their position is now right, which is the
whole of what walking changes.

Reproduce:

```bash
GK3Reborn.Host --scene LBY --timeblock 110A
```

Click the floor to walk Gabriel across the lobby, then point at him.


### Mosely was not in the dining room — fixed 2026-08-22

Reported: entering the hotel dining room on Day 1, Mosely should be at his table and the
scene did not seem to run.

Two faults, one behind the other.

**The actor was being dropped at load.** `DIN110A.SIF` says `pos=MOSTALK` and the scene
defines `TALK_MOSELY` — a typo in the shipped data, and the only one of its kind in the
game. The port took an unresolved position as a reason to leave the actor out of the room
entirely, which took the whole coffee scene with it: the entry script calls
`SetActorLocation("Mosely","DIN")` and then `StopFidget("mosely")`, and the dialogue after
that is addressed to him. The original only skips *setting the position* — see
`GKActor::Init` — and that is what happens now. He stands at the origin until something
moves him.

**And what moves him was being played in the wrong place.** His idle plays
`mos_MosDinPaperShake`, whose action line carries eight zeros. Carrying the numbers is what
makes a clip absolute; the port read all-zero as "no placement" and corrected the clip onto
the model, so his newspaper — a prop, placed by the identity — stayed on the table while he
read it from outside the room. 3,931 action lines are written that way, two fifths of the
corpus.

Fixing that alone moved Gabriel out of the coffee pour, because a posed mesh is placed
relative to its model and an absolute clip has to have the model's placement taken back
off. A prop stands at the identity and there was never anything to take off, which is why
nothing said there was. `ISceneSink.TransformOf` is where that comes from now.

Corpus sweep unchanged apart from Mosely's model appearing in the 33 loads of that scene.


### Scene music cut between rooms rather than crossfading — done 2026-08-22

Leaving a room stopped its bed and entering the next started another, so a door was two
cuts with a gap between them.

`SceneAudio.Leave` now ends what the room was *saying* without ending what it sounded
like: the outgoing bed keeps playing and the next room's comes up underneath it. That
needed per-voice gain in the audio backend, since a crossfade is two voices on one bus at
different levels.

**How long it takes is the game's own number.** A `.STK` gives each sound a `FadeOutMS` —
R25's theme asks for three seconds — and that is the artists' answer to how long this room
should take to stop being the room you are in. A soundtrack that leaves it out gets a
second and a half. A room that names no soundtrack at all lets the last one fade out on its
own, which is the same crossfade with nothing on the other side of it.


### Inspecting the register did nothing — fixed 2026-08-22

`REGISTER, INSPECT, ALL, script={wait InspectObject();}` is the whole of that rule, and
`InspectObject` was modelled as one of `ScreenLayers` — a modal screen, of which nothing
draws yet, so the verb was offered and produced nothing.

**Inspecting is a camera, not a screen.** The scene files carry an `[INSPECT_CAMERAS]`
section giving a close-up position and angle for a thing, and nothing read it: 1,205 of
them across 144 rooms, 735 keyed by `noun=` and 470 by `model=`. It is a different shape
from every other camera list — keyed by what it looks at rather than named — so reading it
the way the named lists are read produces one camera called "noun".

`GameState.Inspecting` sits beside `CameraAngle` rather than replacing it, which is what
makes `UnInspect` free: the angle the story left the view at is still underneath. A close-up
is looked for by three names in turn, which is the original's order — a camera the scene
names, which is what `InspectModelUsingAngle` hands over; then the noun; then the model
standing behind that noun.

`InspectObject()` also takes no arguments in 1,205 of its uses and means "the thing this
action is about", so the API now carries the noun of the action being carried out.

Not done: the original works out a close-up on the fly for anything with no authored
camera, framing it from the object's bounds and looking at a character's face. Without one
the view stays where it was and says so.


### NPCs offered Talk as well as Chat and Ask about — fixed 2026-08-22

Reported as Talk looking like the heading the other two sit under. It is not: `TALK` is a
real verb with 127 rules of its own, and most of them play a line or open a conversation no
topic reaches.

Thirty-two of them are guarded by `DIALOGUE_TOPICS_LEFT`, which means exactly "there is
something to ask about". In the original, choosing Talk there opened the list of `T_`
verbs — and this port puts that list on the menu itself, so those thirty-two were offering
the player a door into the room they were already standing in. Those are hidden when topics
are on the menu beside them. Every other Talk stays, including the nine guarded by
`NOT_DIALOGUE_TOPICS_LEFT`, which are what a character says once there is nothing left to
ask them.

Without `VERBS.TXT` nothing is hidden: whether a verb is a topic is only knowable from that
file, and showing one verb too many beats hiding one the player needs.


### A dotted line ran above and below drawn text — fixed 2026-08-22

Reported with a screenshot: faint dots along the top and bottom of the caption band, plain
on a light surface and nearly invisible on a dark one.

A font sheet stacks its rows and marks the top of each with a marker strip, so a glyph's
rectangle runs from one pixel below its own strip to the top of the next row's with nothing
between them. The sampler filters linearly and a sample at the glyph's edge reaches half a
texel past it, bringing a quarter of a marker strip with it. Rounding text to whole pixels
had closed this at one size; it could not close it at the sizes where a sheet pixel is
drawn as two, which is what the caption ladder does past about 1,600 lines.

`OverlayAtlas.Uv` insets half a texel on every side. Not a switch to nearest sampling — the
caption sheets are antialiased grey, and filtering is what makes a doubled one read as a
larger version of itself rather than a magnified bitmap. The same inset stopped a glyph
reaching into its neighbour, which had been drawing faint ticks between letters.

### The interface said two things it did not need to — fixed 2026-08-22

`right-click for everything it answers to` had done its work, and the inventory bar no
longer announces how much of nothing is in it. Both gone.


### Pour coffee played before Gabriel got to the table — fixed 2026-08-22

Reported in the hotel dining room: the animation started at once instead of after the walk.

`approach=anim` was not implemented, and it is the third most common approach in the game —
398 of the corpus's 3,617, against 688 `WalkTo` and 397 `TurnToModel`. It fell through to
"no approach at all", so every one of those actions ran from wherever the player happened
to be standing.

It is the only approach whose target is not a place: it names an animation, and means walk
to where that animation begins. `AnimationStart` reads that out of the clip's opening frame
through the three axis triads `CHARACTERS.TXT` names — hips and both shoes — which is the
nearest thing a GK3 character has to a skeleton, and which nothing had read before. See
`docs/formats/actions.md`.


### Accented letters came out as boxes, and the interface was tiny — fixed 2026-08-22

Reported as dialogue mangling É and the captions and inventory bar being hard to read at
high resolution.

**The interface was drawn with `F_ARIAL_T12`, which has 94 characters and not one of them
accented** — in a game set in France. GK3 ships 137 fonts and 114 of them carry the full
181-character set. The interface now picks from the game's own caption ladder —
`F_CAPTION_D_26`, `_20`, `_16`, then the 14-point Goudy — all of which have the 52 accented
letters.

**A bitmap font does not scale, so "bigger" means a different sheet.** The rungs cut to 20,
26 and 33 pixel letters and are picked against 2.8% of the framebuffer's height; past
1,600 lines the ladder runs out and each sheet pixel is drawn as two. Every measurement in
`GameHud` is now written in units of the nineteen-pixel line the layout was authored
against and multiplied by `Scale`, so the panels, the inventory slots and the padding grow
with the letters instead of leaving 1999-sized gaps around them. A window that changes size
enough to want a different rung rebuilds the atlas.

**And the multi-row sheets were cut wrong.** A row's last marker is a *terminator* saying
where the last letter stops, not the start of another letter — obvious on a sheet of four
rows and invisible on a sheet of one, where the last letter simply ends at the sheet's
edge. Counting it as a glyph cost each row a character and shifted everything after it, so
the caption fonts wrote `Gabqiel Lnnk` where they meant `Gabriel Look`. Which of the two a
sheet is doing is settled by counting rather than guessing, and 112 of the 136 fonts settle
outright; the rest are judged on whether there is ink after the last mark. A new
`GK3R1142` says so when a sheet cuts into a different number of pieces than the font
declares, which is the check that would have caught it.

Text is also rounded to whole pixels now. A bitmap glyph at a fractional position samples
between texels, and half a texel above a letter is the red marker strip belonging to it —
so a caption laid out at y=17.36 came with a dotted line over it.

Still wrong: the Courier and console families, 24 of the 136, whose marker counts do not
settle under either rule. Nothing draws with them yet — they are for the Sidney computer
interface — and they were wrong before this too.

### Gabriel walked at lobby height wherever he went — fixed 2026-08-21

Reported as needing a height check to stay on geometry.

The walk boundary is a picture of the floor seen from above: it says where somebody may
stand and nothing about how high the floor is there, and nothing else said either, so a
walk held whatever height it set off at.

Every room's general `.SIF` names the object its floor is — `floor=rc1_floor`, 3,050
triangles — and it was being parsed and thrown away. `WalkFloor` buckets those triangles
into a grid on X and Z and answers the height under a point barycentrically, which makes a
slope a slope rather than steps. `Walker` applies it after every move; `SceneUpdate.Place`
applies it when a room stands somebody somewhere, which matters as much, because a spot
authored at the wrong height starts every subsequent walk on the wrong storey.

A room's floor object covers the same ground more than once wherever there are stairs or a
gallery, so the query takes the actor's current height as an argument and picks the nearest
candidate that is not an implausible climb. Neither highest nor lowest is right, and both
are wrong at the top of every staircase. See `walking.md`.

### Gabriel pathfound through walls — fixed 2026-08-21

Reported alongside the height check.

The route finder smooths its result by dropping a corner whenever the line between its
neighbours is clear, and the routine that tested that line **walked a different line**. It
stepped towards the far end one texel at a time, diagonally while both axes differed and
straight afterwards, which for anything but a pure axis or a pure diagonal is another path
entirely: from (0,0) to (10,2) it reached (2,2) diagonally and then ran along the row. A
wall across the middle of the real line was never sampled, the shortcut was taken, and the
actor walked the real line through the wall.

Both callers now go through one routine that walks the actual line, and it also refuses a
diagonal step that would squeeze between two blocked texels meeting at a corner — two
blocks touching at a point are a wall, whatever the texels say. See `walking.md`.

### Gabriel walked exceptionally slowly — fixed 2026-08-21

Requested: double-clicking should make him run.

He walks at his stride's own pace so his feet and the ground agree, which is 35.6 units a
second and is what the game was authored at. A double-click now doubles both the pace and
the rate the stride plays at — one number, applied to both, or the feet slide.

Not a run animation. `CHARACTERS.TXT` names no run for anybody and the archives hold one
general run cycle, `GABERUN`, which belongs to a cutscene; giving Gabriel a run and leaving
the rest of the cast walking would read as a bug. Only the player hurries: the flag travels
from the click to the approach, and a script passes false, because a script's timings are
written against the pace the game walks at.

### The renderer had no material system — fixed 2026-08-21

Requested: diffuse, normal, bump/height/occlusion/roughness/metalness.

`MaterialDefinition` had the numbers since C4 and the shader used none of them. It now
shades Lambert diffuse plus a Cook-Torrance specular lobe over a tangent frame built from
screen-space derivatives, with five textures in descriptor set 1 — colour, lightmap,
normal, packed ORM, height — and the material's own roughness, metalness, specular
reflectance and normal strength travelling as push constants. ORM comes from
`enhanced/orm`, height from `enhanced/height`, both linear, both named for the colour
texture; the maps multiply the material's numbers rather than replacing them, so an edit
made by hand survives a map arriving later.

**None of which changes a pixel until the maps exist.** A surface with no map binds a
neutral one — flat normal, unoccluded, fully rough, not a metal, level height at zero scale
— and every one of those multiplies out to the surface the renderer already drew. Verified
by rendering RC1 at both `--rt none` and `--rt high` before and after: identical.

Height is consumed as single-step parallax; occlusion multiplies the ambient term and
nothing else, which is the part the ray-traced tier does not already compute. See
`rendering.md` and `pbr-materials.md`.

Also changed, and worth knowing while the generated sets are still moving: **the `.png` in
`enhanced/` now beats the `.dds` in `build/`.** That is the opposite of the shipping order
and deliberate — a `.dds` is whatever the last compression run made of whatever the
enhanced set held at the time, so preferring it means regenerating a texture changes
nothing on screen until somebody remembers to recompress.

### Characters wore a permanent shadow, and their hair was glossy — fixed 2026-08-22

Two separate causes, reported together and both visible in the hotel lobby.

**A character was shadowing itself.** GK3's people are not solid bodies — a character is a
dozen separate meshes, a shirt shell with a torso inside it and arms through sleeves — so a
shadow ray leaving the shirt hits the arm underneath before it has gone anywhere. Probing
the composite showed the chest and the small of the back reported as fully shadowed *and*
fully occluded, whatever the lighting was doing. No ray bias helps: the geometry the ray
hits is genuinely inside the surface it left.

The acceleration structure now splits into two instance masks, the room and the models
standing in it, and the mesh pass writes a negative roughness into the normal target to say
which side a pixel is on. A ray leaving a model traces the room only. A ray leaving the
room still traces everything, so a character still lays a shadow on the floor; what is lost
is one character shadowing another.

**The hair was a generated roughness of 0.42–0.44.** The ORM pass gives every character's
hair the same number and every face 0.55–0.57 — which is what a classifier does, and 0.55
for skin is defensible. Hair at 0.43 under an *isotropic* GGX lobe is a plastic sweep
across the crown, because hair is smooth along the strand and rough across it and a lobe
with one width has to take the rough one.

Fixing it exposed that **the material edit layer was never read**. ADR 0006's whole point is
that a classifier guesses and the person looking at the room knows better;
`material-library.materials.edits.json` was being written and never loaded, so every
correction anybody had made did nothing. It is read now, an edited material outranks a
generated map for the same surface, and the scene report counts how many applied. The
fourteen `*_HAIR` materials are corrected to 0.75 with the reasoning in the file.

Also corrected while in there: the rig's lamps carry an emitter radius (4 units for a bulb,
20 for a window) and were being shaded against as points, which puts a pinpoint mirror
highlight on anything smooth. The lobe is widened by the light's apparent size and
renormalised.

**Note on the three failing `RayTracingTests`.** They are not an environment problem, as
previously recorded. They drive `SceneRenderer`, which never runs the composite pass, so
the shadow they assert on cannot appear on any machine. Left alone — fixing them means
either compositing in `SceneRenderer` or rewriting the tests against the host — but the
cause is now known.

### Characters looked like plastic mannequins at RT high — fixed 2026-08-22

Reported after the material system landed, and caused by it. Three faults, compounding.

**A rim light with nothing under it.** The specular lobe was meant to be off for a surface
with no measured finish, and the way it was switched off was to send a reflectance of zero.
Schlick's approximation returns *one* at grazing incidence whatever f0 is, so f0 = 0 leaves
a hard white edge around every silhouette and takes the diffuse away underneath it. The
flag multiplies the Fresnel term itself now, which removes the specular and gives the
diffuse its energy back — the neutral path is the Lambert it was before.

**A missing factor of π.** The BRDF divided the diffuse by π and left the light alone,
which is textbook and wrong here: the rig's intensities were authored in 3ds Max and tuned
against a plain Lambert with no π anywhere, so every rig-lit surface fell to a third of
what it was while the specular stayed at full strength. Both terms are scaled by π instead.

**Two estimates of one number, multiplied.** The ORM map's roughness multiplied the
material library's, following glTF, where the material value is a factor defaulting to one.
Here both are independent estimates of the same quantity — Gabriel's skin is 0.55 in the
library and 0.56 in his map — so multiplying gave 0.31, which is polished plastic. Where
there is a map, the map is now the answer.

Also fixed while chasing it: **the generated ORM and height sets were never being read.**
`SceneLoader` had the properties and `Application` never set them, so all 2,087 maps sat on
disk. R25 now reports 81 of its 139 textures with a finish and relief.

Two things worth knowing before diffing screenshots again. `render-scene` drives
`SceneRenderer` directly and **never runs the composite pass**, so at `--rt high` the rig's
direct light goes into a target that is thrown away — the tool cannot show this class of
bug at all. And the ray-traced tier is not reproducible: two runs of the same build differ
across about 7% of the frame as the denoisers accumulate, so a diff below that floor means
nothing.

### There was no console — fixed 2026-08-21

Requested with completion, and with the `EGG` easter egg in mind.

Backtick opens it. The command language is the game's own scripting language, because that
already is one: 219 functions this build performs, with their prototypes read out of the
224 compiled scripts at load. Typing narrows a list of at most eight, each row showing
`void SetFlag(string)` rather than just a name — which is the whole point, because nobody
can be expected to know that the easter-egg content is behind `SetFlag("EGG")`.

`EGG` is a case every action file in the game tests and the original hard-codes false; its
own source has the same placeholder. `ActionResolver` reads a story flag for it now, so the
console can turn it on. See `screens.md`.


### Nobody's mouth moved and nobody blinked — fixed 2026-08-21

Requested rather than reported: lip sync, and eye blinking with it.

Both were already in the data and neither needed any geometry. GK3's people have
no facial geometry at all — a head is one mesh wearing one bitmap — so talking and
blinking are the same operation as raising an eyebrow: paste a small picture into
a copy of the face and draw that instead. `FACES.TXT` says where each region goes,
and the animations say which picture and when: 98,410 `LIPSYNCH` nodes and 1,268
`FACETEX`/`UNFACETEX` nodes in the `[GK3]` sections that were being skipped.

Lip sync comes from the line being spoken. A `.YAK` carries the recording in its
`[SOUNDS]` and the mouth shapes in its `[GK3]`, against the same frame numbers, so
the mouth follows the words by construction rather than by analysis. 1,362 `.ANM`
files carry their own besides — Gabriel eating a sweet in the lobby is five of them.

Blinking is a timer per character, five to twelve seconds, choosing between two
blink animations by the weights the file gives them. Its animations are nothing but
eyelid textures, so it runs down the same path an expression does.

See `formats/faces.md`. Not done: the eyes, which the file also describes and which
do not track the player, and the talk and listen fidgets, which need the branching
half of the behaviour-script language.


### Gabriel talked to people from across the square — fixed 2026-08-21

Reported as talk and talk-about not walking there first.

An action file says `BUTHANE, TALK, approach=WalkTo, target=TALK_BUTHANE` beside the
script, and the approach is not part of the script — it is what has to be true before
the script runs. The walk was being started and the script run over the top of it,
which also meant a door script's `SetLocation` fired while the player was still three
strides from the door.

The original performs the action from the arrival: `Scene::ExecuteAction` walks the
ego to the target and calls back. So does this now — `Gk3SheepApi.Defers` hands the
script to the room's clock and the room runs it when the walk is over. A host with
nothing to wait with, which is every tool, still runs the action where it was asked
for.

`wait CallSheep(...)` had the same shape of problem and is a fifth of every statement
in the action corpus. How long it takes is not a length of time — it is another
script, which may itself be waiting on a timer or a line of dialogue — so the
scheduler now parks a thread on the *threads* it called rather than on a duration.


### Leaving the hotel played a line about a moped that was not there — fixed 2026-08-21

Reported as audio that did not belong to the scene yet.

The line does belong to it. Leaving the hotel for the first time at 102P is a staged
moment: RC1 shows Wilkes riding past on his moped, cuts to `GABE_WATCH`, has Gabriel
watch it and say "A bike! Man, I need one of those", and hides it again. Three
separate faults left the line playing over an empty square.

**The arrival was counted too early.** `SceneRequest.Continuing` recorded the visit
before the scene file was read. A scene file asks `GetEgoCurrentLocationCount() < 1`
to mean "the first time here" and the scripts that run afterwards ask for one, so the
count has to change between the two. It changed before both, so the file decided not
to place the moped while the script decided the moment was now.

**`ShowModel` and `HideModel` did nothing.** They were on the recorded list, and a
model the scene declares `hidden` was not loaded at all, so there was nothing to show.
Hidden models are placed and not drawn now — out of the picture and out of the
acceleration structure, because a model that is not drawn but is still traced lies its
shadow on the floor. The picker skips them too.

**The clip was corrected onto its model.** See `formats/vertex-animation.md`: a prop
plays its clip exactly as authored, and `wmo`'s clip crosses seventeen hundred units
of RC1 while its model sits at the origin.


### The fountain's water was not on the fountain — fixed 2026-08-21

RC1's water played two hundred and fifty units from the basin. Its animation is one of
the 502 that carry an absolute placement, and the heading in one of those is a
transform rather than a character's heading — so the half turn that reconciles GK3's
headings with its models facing −Z must not be applied to it. It was.


### The ceiling fans turned in visible steps — fixed 2026-08-21

Reported as choppy motion in the hotel lobby.

The clips record fifteen poses a second and the screen shows sixty frames a second, so
each pose was being shown four times over. A fan blade moves six degrees a pose and
ninety degrees a second, which is fast enough for that to read as strobing. Poses are
mixed between the recorded ones now, and a scenery script that is one animation and a
jump back to it is played as a looping clip so that its last pose runs into its first
instead of freezing for a fifteenth of a second every turn.

The mixing had a trap of its own worth remembering: every mesh basis in the game has a
determinant of −1, because the world is left-handed, and decomposing one of those
leaves the runtime free to pick a different axis to call negative on each pose. That
turns a blade inside out between one pose and the next.


### Shadows read as dirt on whatever they fell on — fixed 2026-08-20

Reported as Gabriel's face being "full of smudges of dirt" at High, with the
grain sitting still rather than shimmering.

Two causes, neither of which was the shadowing itself.

The rays were traced inside the mesh shader and averaged on the spot. Eight rays
cannot smooth a shadow edge and nothing averaged across frames, so the seed had
to be pinned to the pixel or the grain crawled — a dither pattern locked to the
screen. Occlusion is now one ray a pixel with a seed that moves, filtered by a
port of AMD's FidelityFX denoiser; see `ray-tracing.md`.

Ambient occlusion was then applied whole to the indirect term. These rooms ship
with lightmaps baked with occlusion already in them, so it was being counted
twice, and enough of the hemisphere above a shoulder is that person's own head
that the shoulder went black. It is applied at 0.55 now.

A third fault was found while looking: the acceleration structure held the pose
each model was authored in, so a ray leaving an animated shoulder started inside
a body still standing at rest. Posed vertices now reach it.


### Every scene rendered as its own mirror image — fixed 2026-08-19

Reported as the numbers on the hotel doors reading backwards. They were: `HAL`'s `27`
plaque came out as its own reflection, and so did the `STAFF` sign, and so did every
other piece of writing in the game.

The plaque was innocent. Its texture reads `27`, its UVs address that texture the right
way round — resampling the render back into texture space reproduces the texture exactly
— and the geometry faces the corridor. What was reversed was the corridor.

GK3's world is left-handed. It was authored for Direct3D, and G-Engine builds its view
the same way: `RenderTransforms.h` sets `VIEW_HAND VIEW_LH`, takes the side axis as
`cross(up, forward)`, and carries a commented-out line noting that negating that axis is
what would make the world appear right-handed. `Camera` used `Matrix4x4.CreateLookAt` and
`CreatePerspectiveFieldOfView`, both right-handed, which is exactly that negation — so
every room, street and corridor was drawn as its own reflection.

It is close to invisible. A mirrored room is still a plausible room; a mirrored painting
is still a painting. Writing is the one thing that gives it away, and a survey of the
corpus is what settled it: of 910 triangles carrying a signage texture, 863 share the
plaques' orientation. Artists notice mirrored text and fix it; they never notice a
mirrored wallpaper.

The view and projection are now left-handed. `FreeCamera`'s strafe axis goes back to
`cross(up, forward)` with them — the earlier strafe fix was correct for a right-handed
view, and inverts with it. Tests derive screen right from the view matrix rather than
assuming a sign, so they carried over; one more asserts the handedness directly.

### Ray-traced lighting is under-exposed and noisy above Low — fixed 2026-08-19

Three separate causes, none of them the exposure constants the entry had been blaming.

**Light fittings sealed in their own lights.** The rig puts each emitter where the bulb
is: inside the lampshade, behind the window pane, under the sconce. The 1999 bake never
traced a fitting against its own light, so the artists had no reason to place them
anywhere else. Tracing them now shut every lamp inside its shade — the shade stayed lit
and the room around it went black. R25's window was the same fault at room scale: the
four `window_hot_spot` lights that stand in for daylight sit between the window backdrop
and the frame, and the backdrop was blocking all four.

The data marks these surfaces. Bit 16 of a BSP surface's flags is light fittings, bit 8
is the surfaces the bake never lit, bit 64 is translucent shadow decals; none of them
now enters the acceleration structure, on the same footing as alpha-keyed geometry. Bit
4 was left alone — it is on a bedsheet in R25 and is too inconsistent to act on.

Bit 8 also fixed a second thing on the way: those surfaces are self-lit, and the original
binds a white lightmap and a multiplier of one for them. They were being multiplied by a
bake instead, which left every bulb and glowing shade as dim as the room it was meant to
be lighting.

**The occlusion radius.** Ninety units at Medium and a hundred and forty at High, in
rooms about three hundred across, so a hemisphere that size reached a wall from nearly
anywhere; occlusion sat low over every surface rather than gathering where two of them
meet, and it multiplies the whole indirect term. Forty-five units now, at both levels,
since the radius describes the effect and the ray count is what quality changes.

**The grain was clumping, not undersampling.** Eight rays drawn independently leave gaps.
They are stratified now — elevation stepped once through the hemisphere, azimuth advanced
by the golden angle, the pair rotated per pixel — and the noise is essentially gone at
the same eight rays. The per-pixel value comes from `gl_FragCoord` rather than the world
position, which also removes a banding artefact: scene coordinates run into the hundreds
and the old hash lost precision at that scale.

Separately, a light that declares no attenuation now has none, rather than being given
its stored end distance doubled. R25's afternoon key light is the sun, fifty thousand
units away with a stored range of two hundred, so the old rule deleted the daylight from
every room with a window in it.

Measured against the bake in R25, mean luminance at High: afternoon 0.126 → 0.292 against
the bake's 0.300, night 0.126 → 0.210 against 0.166. Night sits above the bake, which is
the point — the room is lit by lamps that now actually reach it.

Still open behind all of this: there is no gathered bounce, so the bake stands in as the
indirect term and the exposure constants remain a judgement rather than a measurement.
That is the HDR entry above, and `docs/ray-tracing.md` records what is not traced.

### Nothing casts a shadow indoors — fixed 2026-08-19

Characters, props and scene geometry cast no shadow in any room, at any quality
level. The acceleration structure was never at fault: the geometry was all in it,
and a character even shadowed himself.

`EvaluateRig` decided which lights got a shadow ray by their position in the
array — `if (i < shadowed)` — and `GpuLight.Choose` sorts the array by brightness
times reach. From inside a hotel room that puts the sun and the exterior lights
first, every one of them behind a wall: at Low all eight rays went to lights that
returned "occluded" for the entire image, while the lamp overhead, further down
the array, was never tested. Rendering the raw visibility of the first eight
lights produced a completely black frame, which is what settled it.

The budget is now spent on the lights whose contribution to the pixel is above a
floor of one eight-bit step, in rig order, so it goes to the lights that are
actually lighting the surface. `RayTracingTests` covers it with a rig whose useful
light is buried behind forty faint far-reaching ones.

### A door renders as its knob only — fixed 2026-08-19

`SceneInitFile.Models` collapsed repeated conditional blocks by taking the last
occurrence of a name, which meant any block that hid a model hid it outright. R25
declares `r25door2hal_scene` visible under `{!IsCurrentTime("202p")}` and hidden
under `{IsCurrentTime("202p")}`; the door vanished in every timeblock and its
knob, a `prop` under its own name, kept drawing.

Complementary blocks describe alternative states of a scene, not corrections of
one another, so a model is now hidden only when every block that declares it
agrees. Where they disagree it is drawn and reported as `SCENE009`, since drawing
something that should not be there is a smaller loss than losing a wall or a door.

That reconciliation is now the fallback rather than the answer. Given a timeblock,
the conditions are decided against the game's state and at most one of a pair of
blocks applies, so the later declaration simply wins and nothing is in dispute;
`SCENE009` appears only when a scene is read without a story to read it at. See
`docs/formats/scene-text.md`.

### A and D strafe the wrong way — fixed 2026-08-19

`FreeCamera.Update` built the strafe axis as `cross(up, forward)`.
`Matrix4x4.CreateLookAt` is right-handed, so the basis vector that maps to screen
right is `cross(forward, up)` — the negative of what was there. Tests now derive
which way is right from the view matrix rather than asserting a sign, so they hold
whichever handedness the camera ends up using.

That last part earned its keep the same day: the right-handed view turned out to be
the bug behind the mirrored scenes above, and the strafe axis went back to
`cross(up, forward)` when the view became left-handed. The tests carried over
untouched.

Mouse look needed the same inversion and did not have a test to catch it, so it
shipped reversed for one build. Yaw increases toward screen right under a left-handed
view and toward screen left under a right-handed one, so `_yaw -=` became `_yaw +=`.
There are tests now, deriving the direction from the view matrix the way the strafe
ones do. Pitch is unaffected either way — it turns about the screen's own horizontal
axis, which handedness does not move.

### Z-fighting on the lamp beside the bed — not a defect, 2026-08-19

The mottling on the lampshade in R25 is ray-tracing grain, not z-fighting. It is
absent at `--rt none` and unchanged by either enabling back-face culling or
dropping the coincident faces, which rules out coincident geometry as the cause.

Worth recording, because the investigation turned up two things that look like
causes and are not. Both lamps really do carry coincident faces — fourteen pairs
on `r25lamp2`, thirteen on `r25lamp03` — but every pair is wound in opposite
directions, which is a double-sided lampshade rather than a duplicate. And the
BSP's winding is consistent, contrary to the comment on `CullMode` in
`MeshPipeline`: signed volumes come out positive for every solid prop and negative
for the room shells, exactly as an outward-wound solid inside an inward-wound room
should. Culling is therefore switchable on if a reason to appears; it changes
nothing visible in R25.

The grain itself is tracked as issue 1 above.

### The sky was wrong outdoors and looked like it span when the camera turned — fixed, 2026-08-20

See the commit. Two faults: a cube of side two clipped by a near plane of one, and a
varying that never reached the fragment stage. The sky is a screen-covering triangle now,
with each pixel's ray built from the camera's basis — no vertex buffer, no attribute, no
varying. A faint seam between faces remains at some headings.

**The faces were also on the wrong sides**, which is why the panorama did not join up.
Front is +X and right is +Z, not the other way about. Measured off the images rather than
reasoned from the names, twice: butting each side's right column against every other side's
left column, the four that join are left→back→right→front at 2.9 to 6.1 mean difference
against 23 to 34 for every other pairing; and butting each side's top row against the four
edges of the up face agrees exactly, at 2.9 to 3.2 against 25 to 48.

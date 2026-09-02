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

## 3. HDR output (feature) (done 2026-08-29)

**Requested:** 2026-08-19. **Done 2026-08-29**, with upscaling, in
[docs/upscaling.md](upscaling.md). Verified on an RTX 5090: the surface comes back
`VK_COLOR_SPACE_HDR10_ST2084_EXT` in `A2B10G10R10_UNORM_PACK32`, the room and the interface
are both encoded through ST.2084, and a screenshot is decoded back to sRGB rather than
written as garbage.

What follows is the diagnosis as it stood, kept because the four steps it names are what was
built. Two things it did not anticipate:

- **Everything that writes the swapchain has to encode, not just the room.** There is no
  hardware encode on an HDR surface, so the interface, a movie and the fade each needed the
  transfer function too. Drawn without it they come out through the wrong curve — a correct
  room with a washed-out menu over it.
- **The interesting settings are not the display's.** Paper white and peak luminance are
  necessary and dull. What makes an HDR frame look like HDR rather than like a brighter SDR
  frame is letting *the sun* and *the lamps* exceed diffuse white, and the game already
  knows exactly which pixels those are: `GpuLight.IsDistantKey` for the one, GK3's own
  self-lit surface flag for the other.

What is still open is the exposure note at the end of this section: the lightmap multiplier
is still the original's gamma-space 2, and the SDR tone curve still defaults to the clip it
has always been, because every reference image in the corpus was taken through it.

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

## 4. An exterior has no sun, so nothing standing in one casts a contact shadow (done 2026-08-23)

**Reported:** 2026-08-23, out of the fix below. **Done the same day**, in `Game/Sunlight.cs`
— a sun synthesised for any scene whose asset names a skybox, from the timeblock's hour,
added to the rig at load rather than to the shader. RC1 now reports `Sun: elevation 51°, the
rig's other 6 lights kept` at 110A and the woman by the van has a shadow. What follows is the
diagnosis, kept because it is the reason the light exists at all.

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

### A timeblock's closing film played over the next room, which ran behind it — fixed 2026-09-02

**Reported:** "At the chateau when the cutscene plays between grace/mosely/buthane, the
screen shifts to 'DAY 2', it starts with an intro video BUT the in-game cutscene of grace
entering gabriel's hotel room plays behind the video."

Exactly right, and it was every timeblock that has a closing film — four of the sixteen:
`202AEND`, `205PEND`, `207AEND` and `212PEND`. The reported one is 212P, the Château de
Serres block, handing over to Gabriel's hotel room at two in the afternoon, whose
`SCENE:ENTER BEG_DAY2_2PM` is Grace letting herself in.

**The film was started and never waited for.** `movies.Play(was + "end")` opens it and
returns its length; nothing after that advanced a single frame of it. So the timeblock card
drew over a film that had not begun — which is why the card appeared *first* — the next room
was then built and entered with the film still queued, and from that room's first frame the
main loop found `movies.Playing` and drew it over the top. The room behind it ran normally
the whole time: `SceneUpdate.Advance` is not gated on a movie, so Grace let herself in where
nobody could see her and the scene was over before the film was.

**The fix watches the film out where it is started**, using the loop the opening films
already had — now `Application.Watch`, shared by both — so the order is the film, then the
card, then the room. Escape or Enter ends it at once and holding the mouse button for six
tenths of a second does too, which are the ways out the original offers; the hint says so
over the first six seconds.

A run with `--frames` passes the film over rather than sitting through thirty-nine seconds
of it, the same courtesy the opening films already had — and it *stops* it rather than
leaving it, because leaving it is the fault above.

**The room is deliberately still not frozen while a movie plays**, and that is not an
oversight to tidy up later: a script's `wait PlayFullScreenMovie(...)` is released by the
clock `SceneUpdate.Advance` runs down, so a room that stopped updating during a movie would
never let a waited one finish. Anything that wants a film to have the screen to itself has to
own the frame loop for its duration, which is what `Watch` does.

### A railing built in 3D still cast no shadow — fixed 2026-09-02

**Reported:** "the 3d'ify of 2d sprites doesn't seem to affect light/shadows? so a fence that
is now build in 3d doesn't seem to cast any shadows on the ground?" Correct, and it had been
true by design since the thickening pass was written.

**Two causes, and only the first is the obvious one.**

**Keyed geometry is not in the acceleration structure at all.** `SceneGeometry` refuses it in
both places that record an occluder, and the trace stages ask for `gl_RayFlagsOpaqueEXT` with
no any-hit shader behind it, so a keyed triangle in the structure would cast the shadow of its
whole quad — a railing would shade a wall like a sheet of plywood. Thickening a card changed
neither half of that: it gave the rail sides to be seen from and nothing for the sun to be
stopped by, so it went on casting exactly what a flat card cast.

**And a room occluder would have cancelled itself out.** The composite credits the room's own
occlusion against the 1999 bake — `residual` rises by exactly what `arrived` loses — because
the artists' lightmap already holds every shadow the room casts on itself. So even in the
structure, a fence given `WorldMask` darkens a baked floor by nothing. This is the same fact
that made characters cast no shadow until 2026-08-22, arriving from the other direction.

**The fix does the alpha test at load rather than per hit.** The mask is already decoded and
already measured — the rim is built from it — so the drawn texels are merged into as few
rectangles as cover them and each becomes two opaque triangles lying on the card's own plane.
That copy is traced and never drawn; the keyed shell is drawn and never traced. They go in a
part of their own carrying `TracedWorld.UnbakedMask`, traced with the models, because a 1999
bake cast no alpha-tested rays either and a keyed card is in the lightmap as its whole quad or
as nothing, never as a fence — there is nothing there to double-count.

Measured, RC1 at `SIGN_POST`, 112P, `--rt high`: 5.7% of the frame changes and the deepest
shadow is 208 of 255 — the hotel's wrought-iron sign now lies across the brickwork behind it
and across the board hanging under it. Identical through Direct3D 12 and Vulkan. The lobby
stairs are where the mask choice shows: the room's mask changes 0.06% of that frame against
0.16%, and reaches ten steps of an eight-bit channel against thirty-four.

Costs nothing worth naming. 24,840 occluder triangles in RC1 against 350,000 already traced,
58,310 in MCB at the worst; RC1's thickening pass measures 101 ms against 99 without them, and
400 frames of RC1 at High present at 123 fps against 124. The merge walks the same grid the
rim already walks.

`--no-card-shadows` on both the host and `render-scene` is the A/B, kept separate from
`--no-thick-cards` because what is drawn and what is traced are two different sets of
triangles and a picture only tells you which one to go and read if you can switch them apart.

**Still casting nothing:** windows, and `RC1RAIL` — the balustrade painted on an opaque card,
which has no silhouette in its alpha for any of this to measure. See
[cutout-cards.md](cutout-cards.md#the-shadow-added-2026-09-02).

### A moment spoke none of its own lines — fixed 2026-08-31

**Reported** from the hotel dining room on day one: "Mosely?  Is that YOU?" and the reply to
it, "No, it's my evil twin!  What the hell're you doin' here, Knight?", were both missing —
no audio and no caption. The scene otherwise played: Gabriel drank, spat, and the exchange
carried on afterwards as though the two lines had happened.

**Neither line is a call in `DIN110A`.** The script starts `174AY0W5Z4` — "Thanks, Buddy." —
waits for it, and then `StartMom("coffeepot")`. `ECOFFEEPOT.MOM` carries the rest as animation
nodes of its own: `15,DIALOGUE,E174AY0W5Z5`, `62,DIALOGUE,E174AY0W5Z6` and
`59,CAMERA,VIEW_OF_SPIT`. The `ContinueDialogue(1)` the script makes after the moment is a
continuation *of those* — it is what says `Z7` — so losing the moment's two lines lost a third
one that was called for outright.

**`AnimationFile` read the `[GK3]` section for captions, lip sync, faces and footsteps and
let everything else fall through the `default` arm in silence.** Four node kinds went that
way, and they exist only in the moments: 50 `DIALOGUE`, 18 `CAMERA`, 11 `MOOD` and one
`EXPRESSION`, across 36 of the game's 39 `.MOM` files. So every scripted beat in the game was
played as mime, framed on whatever camera the script had left the view on, and with nobody's
face changing.

This is the second half of *Every scripted moment in the game played nothing* below. That one
found the moments — `.MOM` was not an extension the animation library tried, so the asset was
never opened at all — and restored their clips and their sounds. Its account of what the beat
does was written from the file rather than from what reached the screen, and claimed the two
lines with the rest; they were being parsed away one layer down.

The nodes are now read into `AnimationFile.Dialogue`, `.Shots` and `.Moods`, scheduled by
`SceneUpdate.Play` on the same clock as the sound cues and the footfalls, and handed back to
`SceneScripting` through three hooks — the same shape as `SceneUpdate.Sound`, and for the same
reason: the world knows when, and the audio and the camera know how. A line's plate keeps the
language letter the file writes it with, which is what lets a later `ContinueDialogue` carry on
from the same stem.

The soundtrack nodes that were left over are done too — see below.

### The music never changed under a line of dialogue — fixed 2026-08-31

**Left over from the entry above**, which fixed the moments and named this as the piece it
did not cover.

**A line of dialogue is where GK3 keeps its score changes.** 79 of the corpus's 81
`PLAYSOUNDTRACK` / `STOPSOUNDTRACK` / `STOPALLSOUNDTRACKS` nodes are inside a `.YAK`, on a
frame chosen against the words: `E01KED3S4U6` — "Yes, they dropped Grace at the hotel and
took off. But I'm afraid I have bad news." — cuts the lobby's soundtrack at frame 40 and
brings `FightDrone.STK` up at 50, part-way through the sentence. The remaining two are in a
moment: `EHANDSHAKE.MOM` swaps the hotel's daytime bed for its evening one across frames 665
and 666.

None of them ran. The `[GK3]` reader dropped the three keywords, and a YAK reaches
`SceneAudio`, which had no per-frame schedule at all — it started a line's sound and waited
for the device to say it had stopped. So every fight, every sneak and every arrival in the
game was scored with whatever the room had been playing beforehand.

**A line now carries a schedule of its own**, advanced from `SceneAudio.Update` against the
recording's clock, and both paths end at one `SceneAudio.Cue`. Four things had to be right
beyond the reading:

- **Frame order, not file order.** `E0SB2J3H7B1` writes the stop at frame 9 on the line
  *after* the play at frame 10; performed in file order it silences what it had just
  started. This turned out to be true of `SceneUpdate`'s new schedules as well — they walked
  backwards so a spent entry could be removed as it was passed, which reverses two nodes that
  come due in the same frame, and `EHANDSHAKE`'s 665/666 pair is exactly that. Both now
  dispatch oldest frame first; see `SceneUpdate.Due`.
- **The extension is the typist's, not the file's.** Every script writes `"R25Doors.STK"` and
  half the animation nodes leave it off — `FightDrone`, `LHIHandShakeTell`, `TE5Vamps`. 24 of
  the 46 soundtracks an animation starts were being looked for under a name no archive has.
  The lookup now tries `.STK` and the soundtrack is named by the file that answered, so the
  two spellings are one soundtrack rather than two.
- **A change outlives the sentence it was timed against.** Whatever a line has not reached is
  performed when it ends, is replaced, or is tapped through by the player — otherwise
  skipping "But I'm afraid I have bad news" plays the rest of the scene to the wrong music.
  Not when the room is being left or silenced: that would be music in the wrong room.
- **Two of the corpus's calls miss, and should.** `StopSoundTrack,CS3Monster.STK` names
  nothing that exists and `StopSoundTrack,MontUpstaris.STK` is a misspelling of
  `MontUpStairs.STK`. Both are in the shipped data and the original missed them too.

**Not done, and it needs nothing done:** the `[GK3]` section has a `SHEEP` keyword whose text
is script rather than nodes. The corpus has exactly one, on frame 0 of `E0CFG51K5I3`, and it
is **commented out** — `//0,SHEEP,StopAllSoundTracks();PlaySoundTrack("TestFight.STK")` — so
the INI reader strips it before the parser sees it, and the line beside it, `E0CFG51K5I1`,
writes the same two changes as ordinary nodes. There is nothing live to run. It was
implemented and then removed once the comment marker was noticed; the reference
implementation does not read the keyword either.

### Every scripted moment in the game played nothing — fixed 2026-08-31

**Reported** from the hotel dining room on day one: Mosely reads his newspaper through the
whole conversation instead of folding it onto the table when Gabriel walks over, and the
paper hangs in the air beside him once his talk animations move his arms.

**It is not about the newspaper.** `StartMom("coffeepot")` is the beat in `GabCoffee$` that
runs between the coffee and the walk over, and `ECOFFEEPOT.MOM` holds all of it: Gabriel's
spit take at frame 0, `MosDinPaperShow` at 24, `MosDinPaperDown3B` at 56 — which is the clip
that puts the paper flat on the table, and the only one in the game that does — a cut to
`VIEW_OF_SPIT` at 59, two lines of dialogue and five sounds. (The cut and the two lines
needed a second fix — see *A moment spoke none of its own lines* above.)

**`AnimationLibrary.Read` tried `.ANM` and `.YAK` and nothing else**, so the asset was never
found and `StartMom` returned a length of zero to a script that was waiting on it. The
reference registers `.MOM` for the same asset type as the other two and tags it so only a
moment's own lookup consults it (`GEngine.cpp`). Here it is tried last, which comes to the
same thing on this corpus: `DEFAULT` is the one name that exists as both, and the `.ANM` wins
in the reference too. All 39 moments were silently doing nothing.

The tell was that nothing about the paper was wrong — `mos_paper`'s clips play, at the right
time, in the right hands. Three quarters of the search went into proving that, by mapping
each clip's start and end with `render-scene --play`: `Down3A` runs table-to-reading and
`Down3B` reading-to-table, and `Down3A` is what the script plays at the *end* of the
conversation. A clip that starts with the paper already on the table, played to pick it back
up, is the evidence that something earlier was meant to put it there.

### The Ubuntu release build segfaulted after every test passed — fixed 2026-08-31

**Reported** from the release workflow: `GK3Reborn.Tests  Total: 1805, Errors: 0, Failed: 0,
Skipped: 85` and then `Segmentation fault (core dumped)` out of `dotnet exec`, which
`run-tests.sh` turns into a failed job. Ubuntu only; the same commit passed on Windows and
macOS. Nothing had failed — the crash is in the shutdown after the last test reported.

**The engine was unloading its native libraries, dozens of times a run.** Silk.NET's
`GetApi` opens the shared library and resolves every entry point into a fresh handle, and its
`Dispose` closes it; when the last handle closes, the library is unmapped. That pair was being
called freely. `SpirvCompiler` and `HlslTranspiler` opened shaderc and SPIRV-Cross in their
constructors and closed them in `Dispose`, and a `ShaderCompiler` is made and disposed per
renderer, per pass and — in the suite — per test; `VulkanDeviceSelector.Survey` opened and
closed `libvulkan` on every call, which is every test class that asks whether this machine can
render; `D3D12RootSignature.Serialize` did it once per signature.

Ubuntu is the only one of the three platforms where the unmapping is real. `dlclose` on glibc
genuinely unmaps the image and runs its static destructors, where Windows keeps the DLL for as
long as anything holds it and macOS declines to unload most images at all — which is exactly
the shape of a defect that is invisible on two platforms and fatal on the third. Two things go
wrong once the unmapping happens: a handle held on another thread — xunit runs test classes in
parallel, and two of them compile shaders — goes on pointing into an image that has been
unmapped and remapped elsewhere; and glslang and SPIRV-Cross are C++ libraries whose static and
thread-local destructors are registered with libstdc++ and libc, which are *not* unloaded, so a
load/unload cycle leaves those registrations pointing at addresses that are no longer mapped.
They are called at process exit, which is why the summary prints first.

So nothing unloads a native library any more. `ShaderToolchain` and `D3D12Runtime` hold shaderc,
SPIRV-Cross, DXC, Direct3D 12 and DXGI for the life of the process, created on first use so a
Vulkan session still never loads `dxcompiler`; `OpenAlBackend` holds OpenAL the same way; and
`VulkanContext.LoadApi` is the one place `libvulkan` is opened and nobody closes it. Two
`LayeringTests` keep it that way — one that only those four files may call `GetApi`, one that
nothing may dispose what they hand out — and both were checked against a reintroduced violation.

**Vulkan is the exception that has to keep a handle each.** Sharing one `Vk` across contexts
was tried first and faulted immediately, on Windows as well: a Silk `Vk` is not just a table of
function pointers, it remembers which instance and device it was last used with, so two
contexts sharing one resolve each other's device functions. One handle per owner, none of them
released, and the library is still loaded exactly once.

### Half the cast faced the wrong way, and cutscenes kept resetting them — fixed 2026-08-31

**Reported** as four things, which turned out to be three causes and one of them shared:
Emilio crossing the lobby with his head turned half a circle from his shoulders; Gabriel
spinning a half turn the instant the coat-hanger and the sticky-tape animations ended; Gabriel
facing away from Emilio just before their handshake, turning to face him and inverting again;
and Estelle stepping towards Gabriel in the museum and immediately stepping back, with the pair
resetting between every line of the introduction.

**There were two ways of asking which way a character faces, and they disagreed by a half
turn.** `SceneUpdate.Playing.Correction` draws the body by the triangle the hip and shoe
triads make, which is the reference's own measure — `GKActor::GetModelFacingDirection`, the
branch it takes whenever nothing animates the facing helper. `AnimationStart.Facing` did
something else: it read the heading off the hip *mesh's* rotation and used the triangle only to
choose between that and a half turn from it. Measured on `gab_GabYawn`, the triangle says
−179.9° and the mesh rotation said −3.2°.

Everything that asked the second was therefore aimed at the back of the body the first was
drawing. `approach=anim` stood Gabriel at the wardrobe facing away from it, so his clip played
correctly and his idle spun him round the moment it ended; a head glance is a yaw off the
body's facing, so Emilio's went half a circle the wrong way; and the opening-pose report
accused half the cast of facing the wrong way — the museum pair's now reads 7° and 33° from
the scene file's headings rather than 136°. There is one measurement now.

**The feet were read on frame zero whatever frame the hips were asked about.** `FacingAt`
took the hips at the frame it was given and both shoes from the opening frame, which is a
triangle that never existed at any moment of the clip: right for an opening pose and wrong by
however far the clip has turned the character since. The worst case is a clip whose whole
purpose is a turn, and the museum has one — `Lh2MusEstTurn2Gab` ends with Lady Howard and
Estelle facing Gabriel, and the frame-zero feet under the last frame's hips put them 165° and
99° away from him. They now land 17° and 12° from him.

**An actor's position and heading never followed their model.** The reference syncs them every
frame — `GKActor::OnLateUpdate` ends in `SyncActorToModelPositionAndRotation` — and this
engine did not do it at all. A relative clip is played *through* the placement, so every clip
after the first began again at the spot and heading the scene file opened with, however far
the one before had carried them. That is the cutscene that keeps snapping its cast back, and
it is why `EstOneStep` took Estelle a step towards Gabriel and left her back where she began.

`SceneUpdate.Settle` writes it, at the three points a clip lets go of a model: it finishes, it
is stopped, or another clip takes the model off it. Only for a clip that keeps the ground it
covered — a non-move animation puts the actor back where it found them, which is where the
placement already is. **It is a sync and not a move**: the placement is what a mesh's own
transform is drawn through, so each mesh's transform is rewritten by the same amount the other
way and not a vertex moves. What moves is the frame the next clip will be played in.

**Nothing ever ended a conversation.** `LeaveConversation` was reachable only from a script's
own `EndConversation`, and no shipped script calls one for the museum's, or the front desk's,
or any other conversation a topic list is picked from. The original ends them from its own
code: `ActionManager::OnActionBarCanceled` runs `GLB_ALL`'s `CodeCallEndConv$`, whose whole
body is `EndConversation()`, "every time the action bar disables". Without it the participants
kept the talk and listen scripts `[LISTENERS]` lends them and the pose the enter animation put
them in, and the camera went on framing the pair — reported as Lady Howard and Estelle never
leaving the conversation, with Gabriel stuck in front of them until Get Unstuck was used. The
bar being dismissed ends it now; taking a verb off it is not a cancel and ends nothing.

**A fidget must not relocate anybody, which the sync had to be narrowed to say.** Reported
straight afterwards, as Madeline Buthane holding her whole conversation at the van over her
shoulder: she is placed at `BUTHANE_TALK` facing Gabriel with `initanim=MADRC1TURN2`, and her
idle is `madMapIdle.gas`, whose `MadRc1ReadM` and `MadRc1FigM` are authored *absolutely* at the
back of her van with her turned to the map — 128° off him. Absolute means move, move means keep,
so the sync took the map pose as where she now stood, and `madreltalk.gas` is relative and plays
through exactly that.

The reference syncs every frame whatever is posing the model, and re-snaps the model to the
actor as each new relative clip starts, so a fidget's drift is bounded and cancels. This engine
syncs once, as a clip lets go, which is the same answer for a story clip and a permanent one for
an idle. So a clip a model's own behaviour script asked for no longer writes the placement —
which is what this engine says about fidgets everywhere else anyway: an idle is dropped where
the story is animating, paused for a walk, and cleaned up when interrupted.

**`--trace-actors` is why the last two were found rather than argued about.** A character drawn
in the wrong place and a character whose *placement* is in the wrong place look identical while
one clip plays and diverge the moment the next starts, which is the whole family. It prints the
placement and the heading beside the clip's name as each clip takes an actor and lets one go,
and says whether the clip is absolute and whether it keeps its ground.

### Interior floors were displaced as if they were outdoor ground — fixed 2026-08-31

**Reported** as the museum's tile floor being badly tessellated: every joint between the tiles
wobbled, and each tile curled up at its edges.

**A skybox is not the same thing as being outdoors.** The rule that cuts relief past the
`floor=` object — verges, rock and roadside, which the reconstructed horizon made the sharpest
thing on screen — gathered *every displaced-class texture in the room* and applied it wherever
it appeared, in any scene with a skybox. The museum has one through its doorway and so does
every hotel bedroom with a window, so the rule cut whatever those rooms happen to be furnished
with: R25 displaced its wardrobe, its rug, a lightbulb and the keys of Gabriel's laptop — 40
textures, up to 6.8 units of relief in a hotel bedroom — and MS3 its display cabinets. The set
is the room's own floor textures now, which is what "the ground runs past the floor object"
means; R25 displaces nothing at all, because neither of its floor textures is displaced-class.

**And the outdoor depth boost was landing on the floor itself.** The ×2.5 is for the ground
this feature *added*; the floor's own depth is the number the material library was reviewed at
for a surface somebody walks on. `MSMFLOOR` asked for 1.5 units and was cut at 3.75. It is
applied only beyond the floor object now. Measured: the museum's typical move falls from 0.21
units to 0.14 and RC1's street goes back to the depth it was already right at, while its verges
keep the boost.

**The rest was the height map, and it is a material correction rather than an engine one.** A
tile floor's only real relief is its joint, and at 0.6 units that is finer than the two-unit
cell the geometry can carry — so everything the cut *could* carry on `MSMFLOOR` was the mottled
glaze the field derives from the picture, and 1.5 units of it domed every tile. `displaced` is
off for it in the material edits; the joint still marches and still normal-maps. The lobby's
octagonal tile is left alone: its field resolves the tiles themselves and it never wobbled.

### The black cat at RC3 stood outside the wall, and petting it froze the game — fixed 2026-08-29

**Reported** as the cat in Rennes-le-Château spawning outside the wall, and as the camera
freezing and the player being unable to navigate after interacting with it. Two faults, and
the second one is not about the cat at all — it is about how far away the player was when
they clicked on it.

**The cat is placed by its opening pose and nothing kept the placement.** `RC3102P.SIF`
declares `model=cat, noun=cat, initanim=gabPetsCat, idle=catIdle.gas` — an opening pose and
**no `pos=` anywhere**. `SceneUpdate.Open` sampled the pose, which is absolute and puts the
cat on its ledge at (-201, -105, -1139), and wrote that to the actor's *logical* position
only. The model's own placement stayed at the identity the loader gave it, and a placement
is the whole of what a **relative** clip is played through: on the next frame `catIdle.gas`
started `CatStandFidg1`, which carries no placement, and the cat was drawn at the world
origin — which at RC3 is on top of the courtyard wall, a thousand units from where it
belongs. Measured with `DumpActor("cat")`: (-201.3, -111.6, -1139.4) on frame zero and
(-0.7, 4.4, -2.7) on frame one, for the rest of the room's life.

The reference syncs the actor to the model after sampling an init anim, and that is what was
missing. An actor the scene gave no mark to is now **reseated** onto where the pose leaves
them, at the heading the clip implies, and the pose is sampled again against the placement
they now have — an absolute clip is put where it was authored by a correction worked out
from the placement at the time, so moving one without the other would carry the drawing with
it. The picture is identical; what changes is that every relative clip afterwards plays
where the character is.

**It is not one actor.** 105 actor declarations across the corpus carry an `initanim` and no
`pos=` — the hotel's diners, the Abbé seated in the church, Prince James at the bar — and
every one of them was posed correctly and then animated at the origin by its own idle.
Verified unchanged where the scene *does* place somebody: an actor with a mark keeps it,
because a clip authored elsewhere must not overrule the artist.

**The freeze is the approach walk, and it was real but bounded.** `CAT, PET, 1ST_TIME` is
`approach=ANIM, target=gabPetsCat`, which means walk to where that animation begins. From
RC3's door that is a route of 3,208 units, and `SceneUpdate.Directing` hands the camera to
the story and swallows clicks for the whole of an action — so the player sat with a frozen
view and a dead mouse for **91.4 seconds**, which is indistinguishable from a hang.

Two things were wrong with the number. The cat being drawn at the origin is what made the
player click on it from across the room in the first place; and `ToAnimationStart` was the
only approach in the game that never passed `mayRun`, by omission alone, so the third most
common approach in the corpus was always taken at a stroll however far it went. It runs now,
like `WalkTo` and `WalkToSee` do, and the same walk reports 30.6 seconds. `--do CAT:PET`
from `FR_RC1` now completes: the walk ends at 31.7s, `gabpetscat` plays out by 39.2s and
`CatRunsAway` runs.

**And there is a way out when it happens somewhere else.** `Get Unstuck`, on the pause menu
under Restore, calls `SceneUpdate.Unstick`: it drops the actions held back for a walk, zeroes
the seconds an action said it needed, stops the walks under way, stops a clip the story was
playing on the player, forgets that the room's outstanding scripts were being counted as
something happening, lets go of `ForcedCameraCuts` and any close-up the view was pinned to,
and stands the player on the nearest walkable texel if they are off the floor. It is
deliberately not a reload: flags, counts, score and inventory are untouched, so what the
player had done is still done and only what was *happening* is abandoned. It is a row rather
than a setting because the menu is the only thing a player with no camera and no clicks can
still reach. Reachable from the console as `Unstick()`, which is how a wedged room is
reproduced and undone headlessly.

### Shadows ended at a straight line whenever anything upscaled — fixed 2026-08-29

**Reported:** the ray-traced lighting is cut off partway across the frame, and the room is
too dark behind the cut. Present at every upscaler setting except native, and absent there.

That last part is the whole diagnosis. The denoiser and the reflections were built to
`_extent`, the size the picture is *shown* at, while every target they read — depth,
normals, motion — and everything that reads what they write are built to `_renderExtent`,
the size the room is *drawn* at. `UpscalePlan.Ratio` is one only at native, which is the
single setting where the two agree and the only one that looked right.

Sized to the window, the denoiser traced and filtered over an area larger than the picture
it had been handed. Past the drawn region it fetched texels nothing had written, so the
shadow, the occlusion and the reflections all stopped dead at the boundary between the two
resolutions — a horizontal edge across the ceiling at Performance, where the render height
is half the window's.

Both are built to `_renderExtent` now. `RecreateSwapchain` already discards them, and
`CreateSwapchain` recomputes the render extent from the plan, so a change of upscaler or of
quality rebuilds them at the right size without further help.

**Verified** by running the host headlessly over the same scene and camera at 640x360 into
1280x720 — `GK3Reborn.exe --scene R25 --timeblock 110A --frames 240 --screenshot out.png
--settings <upscaler>.json` — before and after, with both the spatial upscaler and DLSS.
The built-in spatial upscaler reproduces it with no NVIDIA runtime installed, which is the
cheapest way to catch this class of fault again: **anything in the deferred chain that takes
a size wants `_renderExtent`, and only the swapchain, the upscale target, the interface and
the fade want `_extent`.**

### A game brought across from the original knew nobody — fixed 2026-08-29

**Reported:** the twelve people who are strangers until met stay strangers for ever in a
restored save.

Correct for the saves this engine writes and wrong for the ones it imports, which is the
case that matters. The labels ask the game's own conditions — `MET_BUTHANE`,
`INTRODUCED_EMILIO` — and every one of them is a count of a topic raised or a verb done.
A `.gk3` records none of that. It is a name, a room, a timeblock, a score and a picture,
and the rest of the file is the 1999 engine serialising itself through RTTI, which nothing
reads. So an import two days into the story pointed at Madeleine Buthane and read "Woman",
which is the bug the labels exist to avoid rather than the spoiler they exist to prevent.

**What the file does state is the point in the story**, and that is enough. The same
reasoning already recovers the score events: a save cannot be standing at four in the
afternoon without having been through the morning, because the story does not leave a
timeblock until its own rules are met. `Assets/Story/Introductions.txt` gained a second
kind of line for it — a timeblock and the people the story puts in front of the player
during it — and an import is credited with its own block and every block before it:

    110A | BUTHANE, EMILIO, JEAN, GIRARD, LADY_H_ESTELLE, LADY_HOWARD, ESTELLE
    112P | WILKES, BUCHELLI, ABBE
    102P | MARCIE, LARRY

Each line is checked against three things that agree: the `.SIF` that stands the character
in a room that block, the timeblock in the name of the action file the condition above it
was copied from — `RC1110A`, `DIN112P` — and the walkthrough the engine already ships.
Every introduction in the game is on day one, so **a save from day two or three is credited
with the whole list**, and that is answered from the day rather than from the roster
happening to add up to it.

The two kinds of line share a file and a separator and are told apart by the shape of the
left side, because a condition may contain `||` and counting bars would split one in half.

**It is deliberately generous**, in the direction the rest of the list already argues for:
a name shown a little early is a small spoiler, a name withheld from somebody the player
spent two days with is not something they can work around. A player who imports at ten in
the morning is credited with the tour, the lobby and the museum whether or not they had
walked that far.

**Saves already in the store are caught by a migration**, because importing is idempotent
and nobody is going to be offered the import again. What identifies one is that it holds no
topic counts at all and stands past ten in the morning — a position no game played in this
engine can be in, since the first timeblock cannot be left until four separate topics have
been raised. A save still standing in the first block is left alone whichever it is: there
the two cannot be told apart, and the list would be most of the cast.

Unit tests only; the store, the migration and the roster are all reachable without the game
running.

### A character whose model is not drawn around its own origin stood in the wrong place — fixed 2026-08-28

**Reported:** Lady Howard is mispositioned, at Poussin's tomb and at Blanchefort both.

She was 84 units from her mark — at POU207A the scene stands her at `LH1`, `pos={71, 225,
-476}`, and her torso was drawn at `(142, 279, -431)`. The scene places an actor by
translating their model to the spot, which assumes the model is drawn around its own origin.
**It often is not**, and the reference says so in as many words:

> Here's a tricky thing: the 3D model's *visual* position IS NOT always identical to the 3D
> model's *actor* position. The 3D model vertices may be significantly offset in the local
> space of the 3D model actor. — `GKActor::SetModelPositionToActorPosition`

Measured across the 43 characters `CHARACTERS.TXT` gives axis triads: Gabriel, Grace,
Estelle and Madeline are all within a unit of their origin, which is why this survived so
long. Lady Howard is 83.6 units out, Emilio 42.9, the taxi driver 871 and the sitting Wilkes
522. Vertically too — Prince James is modelled 33.8 units below his own origin.

The offset is the model's **floor position**: the hip triad across, at the height of the
lower shoe less its sole. That is `GetModelFloorAndShoePositions`, and it is the same
measure `AnimationStart.Standing` already reads out of a *clip* — the missing half was
reading it out of the model. `Actors.Footing` does that and takes it out of the model as it
is read, so every transform that places a character afterwards — the scene's, a walk's, a
script's — means what it says without carrying a correction of its own.

**An actor the scene gives no position is put back where their model was modelled.**
Standing somebody on their feet is a statement about a placement, and where there is none
there is nothing to say: the original leaves the model actor at the origin and lets the
vertices decide, and an absolute opening clip or the script that walks such an actor in is
written against where the artists left them.

### A sign's lettering striped through with its own back — fixed 2026-08-28

**Reported:** at Blanchefort, "the sign has some Z fighting between the lettering and the
signpost."

The Mt Cardou signpost is **one flat quad with no thickness**: `Cdbmtcrdousgn` on the front
and `rl1postwood` on the back, on exactly the same plane. Both are drawn, both are at the
same depth, and which one a pixel shows comes down to the last bit of an interpolated float
— which differs pixel to pixel because the two faces are triangulated from different
vertices. The two pictures interleave in horizontal bands and the sign is unreadable.

It is not depth precision in the ordinary sense and nothing about the depth buffer moves it:
raising the near plane from 1 to 50 changed 106 pixels of a 1.92-million-pixel frame. The
surfaces are coincident.

**The original avoids it by culling back faces** — `Renderer::Render` sets `CullMode::Back`
for opaque world geometry, so only the side facing the camera is drawn. That was tried here
and cannot be taken: GK3's winding is not consistent enough (`FrontFace.CounterClockwise`
erases the ground, and `Clockwise` renders correctly but takes every foliage card in the
game with it, since those are single quads meant to be seen from both sides).

So `Rendering.CoplanarCards` gives the cards a thickness instead: each face moves 0.05 units
along its own normal, which puts a tenth of a unit between them — half a millimetre at the
game's scale, where a character is 72 units tall, and some hundreds of depth quanta at the
distance a sign is read from. It is applied only to surfaces that face opposite ways, lie on
the same plane to within a hundredth of a unit, and overlap: 3,086 pairs across 98 of the
corpus's 110 rooms, of which all but a handful are foliage whose two sides carry the same
texture and so never showed the fault. Rendering the seven scenes it changes most, the
largest difference outside the signs is 1.4% of WOD's pixels, all of them single-pixel
changes on leaf silhouettes that were previously deciding at random.

**Not every "z-fighting" sign is one.** The Château de Blanchefort board beside it looks the
same at a glance — light patches of bare wood breaking through the paint — and is not a
defect at all: they are painted into `CD1SIGN.BMP`, which is a weathered sign with its paint
flaking off. It is worth checking the texture before the geometry.

### Nobody ever changed their clothes — fixed 2026-08-28

**Reported:** at Poussin's tomb on the second morning, "everyone is wearing white (or almost
everyone)". Grace wears a red top and khaki trousers there in the original and was wearing a
white t-shirt and blue jeans — which are her *first day's* clothes.

A GK3 character owns one model for the whole game and changes clothes by having it
repainted. Each outfit is a one-frame animation holding nothing but `[MTEXTURES]` lines —
`GraClothes207a` is six of them, `GRA_RED` on the torso and `GRA_KHAKI` on the legs — and
`CHARACTERS.TXT` says which one applies from when:

```
ClothesDefault=GraClothes110a
Clothes207a=GraClothes207a
Clothes307a=GraClothes307a
```

**None of it was read.** `CharacterLibrary` parsed the walk animations and the shoe triads
and walked past the `Clothes` keys, so nobody was ever dressed. That is not a defect about
the second day: **the default outfit is one of these animations too**, so the models were
being drawn in the undyed placeholder textures they ship with, which is where the row of
blank white shirts came from. Eleven of the game's forty-five characters have entries and
nine of them are the tour group, which is why one scene showed nearly all of it at once.

`CharacterConfig.Clothes` now holds them in file order and `ClothingFor` decides between
them by `GKActor::Init`'s rule, which is not the tidier one and is kept because it is the
reference's: every dated entry at or before the story's timeblock applies and the **last
listed** wins, while `ClothesDefault` is only used when none has. The order matters for
Wilkes, whose default is camouflage and whose `Clothes207a` is not.

`Wardrobe.Dress` applies it to the model as it is read rather than playing it into the room
as the original does. A change of clothes is a fact about the character rather than
something that happens: baking it in means the room loads the right textures once instead of
loading one set and repainting over it, the enhanced sets reach the clothes a character is
actually wearing, and a still rendered by `render-scene` without running a frame is dressed
the same as the game.

Only lines naming the model being dressed are applied. `[WIL]`'s clothes are
`Wi2ClothesCamo`, which paints `wi2`; the original resolves the name against the whole room
and would dress that other model instead, and the two readings differ only if both stand in
one room, which they never do.

### A prop in somebody's hands was left in the pose the model was authored in — fixed 2026-08-28

**Reported:** "some NPCs were not using objects properly (animation of binoculars but no
binocular in hand)." The Abbé raises his hands to his eyes at Poussin's tomb with nothing
between them.

The prop was found, shown, and pinned to the right man. What was lost was its *pose*.
`SceneGeometry.MoveModel` rebuilt every batch from `model.Meshes[mesh].MeshToLocal` — the
transform the model was authored with — throwing away whatever `PoseMesh` had just written.
On a walking character that is invisible, because the stride poses every mesh again on the
next frame. On a **held prop** it is total: the prop is posed once by its clip and then has
its placement rewritten every frame by `Carry` to follow whoever is holding it, and that
rewrite happens *after* the clip has run.

`ABEBINOCS.MOD` is modelled at y −252.5, so the binoculars were being drawn a little over
252 units underground while the Abbé mimed them. `CAM` and `LENS` are 93 units behind Lady
Howard for the same reason. `BINO1`, Buchelli's, is modelled where its clip's opening frame
puts it, so his looked correct and hid the fault.

`MoveModel` now re-places each batch from `_batches[index].Local`, the pose it is in now —
which is the rule `TurnMesh` a hundred lines above already keeps, and for the same reason. A
prop that should go back to its rest is put there by `SceneUpdate.Rest`, which is where that
decision belongs.

**The existing tests could not have caught it.** `HeldPropTests` asserts against a sink whose
`MoveModel` keeps nothing but the placement, so it modelled a `MoveModel` that was already
correct. The regression test is a Vulkan one — `SceneRenderTests.Moving_a_model_keeps_the_-`
`pose_its_meshes_are_in` — because the defect was in the geometry rather than in the rules.

**`render-scene --play NAME[:SECONDS]`** was added to find it and is the way to photograph
anything that is only true part-way through an animation. It plays the animation, advances
the world a frame at a time, and reports where each model it names ended up — both the
placement and the first mesh, because a held prop fails in two ways that look the same on
screen and only one of them moves the placement.

### The camera stayed the player's during a cutscene — fixed 2026-08-28

**Reported:** the free camera "should be locked at that point, currently it allows free
movement causing some odd behavior when it jumps back."

Nothing stopped the player flying the camera off while a scripted action ran, so the next
angle the script cut to snapped the view back across the room from wherever they had got to.
The jump is the symptom; the missing part is the answer to who is holding the camera.

`GameCamera::SceneUpdateMovement` states the rule in three lines and `SceneUpdate.Directing`
is now the same three: never while a script has asked for forced camera cuts, and never
while an action is playing — unless the player has turned cinematics off, which is what that
switch is for. A player who has turned them off keeps the controls through everything,
because with the cuts gone there is nothing directing the view for them. `--free-camera`
keeps them too, which is the escape hatch the reference makes for `Tools::Active`.

`Occupied` is the same signal the trigger rectangles and the click-through-dialogue rule
already trust, so this does not invent a notion of "cutscene" the rest of the engine does not
have.

### The Bartender easter egg had no disco ball, and no disco — fixed 2026-08-28

**Reported:** "The Bartender easter egg should show a disco ball, but the disco ball isn't
showing. Also it would be nice to add some colored lights and light effects on the wall when
it happens."

Turning the easter eggs on and asking the bartender for one runs `RL2_ALL:ShakeItBaby`,
which is fifty lines of script and a complete nightclub: a ball comes down out of the
ceiling on a pole, the room is relit, the floor turns into a lit dance floor, the bar front
lights up, specks of light rotate across every wall, and the bartender gets up on the bar
and dances. **None of it happened.** The camera cut to the bar, the music changed, the
bartender danced on an ordinary floor in an ordinary room, and the ball went back up again.

Three separate things were missing, and the reason it was all of them at once is that they
are three things a script does to a *room* rather than to a model in one.

**Construction mode was recorded and dropped.** `AddModel("model=discoball_pole,type=prop")`
is a script putting something into a room the scene file never mentioned. Six scripts in the
game use it and all six are easter eggs — the ball and its two light models here, the monkey
in Grace's fridge, the red nose and propeller hat in the lobby, the stream Mosely falls in,
the spinning props at Blanchefort. The call did nothing, so the `ShowModel` after it found
nothing, so the animation after that had nothing to animate.

**They are staged while the room loads rather than built when the call arrives.** Adding a
model to a room that is already standing means new vertex buffers, new descriptor sets and a
new acceleration structure mid-frame, and the reward for all of it is a prop lit and
shadowed differently from everything around it. A room's scripts are a closed set and its
construction calls are string constants in them, so what will be built is simply read before
the room opens: `SceneLoader.StageConstructed` finds the scripts whose name begins with the
scene's, scans their string tables for `model=NAME,type=prop`, and places each one hidden.
The disco ball is then an ordinary prop that happens to start out of sight, and `AddModel`
is left with nothing to do but say so if a script asks for one that was not staged
(`GK3R3348`). Hidden is also the faithful state: every construction call in the game is
followed immediately by `ShowModel` or `HideModel`.

**`SetScene` was recorded and dropped, and it is the coloured light.** The room's lighting
is baked, and a scene asset is a bake: `RL2_DISCO_A.MUL` is the bar lit by a mirror ball and
`RL2_A.MUL` is the same 479 surfaces lit by its lamps. Swapping between them is what the
call means — the reference implementation reloads the named asset's geometry, but every
`SetScene` in the corpus except CEM's at 106P names the same `BSP` as the room already
standing.

**The two bakes disagree about tile sizes**, which is why this is not a texture swap: a
surface the artists lit evenly exports as a single texel and the same surface under a
mirror ball as eight, and 86 of RL2's 479 differ. Where each tile sits in the atlas is
written into the vertices, so repacking would light every surface with some other surface's
bake. `LightmapAtlas.Repack` lays the replacement into the layout that is already there,
sampling a tile into its slot where the two do not agree, and the atlas texture is refreshed
in place — no new descriptor sets, no vertices touched, and the way back is exact.

**The rig goes with the bake.** The bake lights the room and the scene asset's own lights
light everything standing in it, so swapping only the bake leaves the people lit by the
scene the room has just left — Gabriel under warm bar lamps on a floor gone blue. RL2's
disco asset carries 38 lights where its ordinary one carries 23: fifteen coloured omnis and
a key over the ball, none of which reached anybody until now.

**`[STEXTURES]` was parsed as an unknown section.** 198 lines across 78 animations, and they
are the room changing what it shows rather than a model doing it. The bar's floor cycling
`checker_01` through `checker_03` on a two-second loop is nine of them; the bar front and
base lighting up are six more. An animation made of nothing else — `disco_flashdance_a` names
no clips, no sounds and no captions — also has to report a duration, or the `wait` in front
of it walks straight past. `[SVISIBILITY]`, its five-line sibling, is read now too.

**Two more easter eggs and one ordinary puzzle came back with it.** The fridge monkey and
the lobby's hat and nose are construction mode; the light switch in the Château de Serres
garage is `SetScene("gri_b")`, and until now flipping it played the animation, said the
line, and left the garage exactly as dark as it was.

Reproducible headlessly, which needed one small ordering fix of its own — `--did` writes
into the story what has already happened, and an action's case is a question about exactly
that, so it now runs before `--do` rather than after:

    dotnet run --project src/GK3Reborn.Host -- --scene RL2 --timeblock 112P         --did EGG --do BARTENDER:EGG --camera SEE_BAR --frames 1600 --screenshot disco.png

### Gabriel walked the ruins of Chateau de Blanchefort knee-deep in them — fixed 2026-08-28

**Reported:** "at chateau de blachefort, gabriel is standing in the ruins geometry instead
of on top of the floor. he's knee high into the geometry. gets worse when walking into the
'tower' platform, then he's up to his chest into geometry."

**`WalkFloor` picked the nearest floor surface to the actor's feet; it has to pick the
highest one they could have climbed onto.** CD1 names one `floor=cd1_floor` of 5,178
triangles, and it is not a single surface: the hillside the ruins were built on belongs to
the same object and runs on underneath them. Sampling its footprint on a 60x60 grid, 951 of
2,676 points over the floor have two or more surfaces, 752 of them within a step of each
other — 35% of the room. Walking east from `FR_CDB`, the pair under Gabriel's feet is 688
and 699 across the ruins and 681 and 722 on the tower platform, and nearest-to-the-feet
handed back the lower one every time. He was 11 units under the paved floor (knee, on a
76-unit man) and 41 under the tower (chest), which is exactly what was reported, including
its getting worse the higher the floor above him rose.

The reference has no such rule: `BSP::GetFloorInfo` drops a ray from y=10000 and keeps the
first surface it meets, so the answer is always the topmost floor over the feet. The rule
now is that with the storeys above and below rejected — the part a ray from the sky gets
wrong at the foot of a staircase, and the reason the window is kept.

**Inert everywhere the artists placed somebody.** Over the corpus's 107 rooms with a floor,
the two rules pick the same surface at all 830 authored positions. Simulating all 10,232
straight walks between pairs of them, 80 end more than a unit apart — every one of them in
CD1 or at Larry's front step in LHE, where the doorstep is 10 units up — and in all 80 the
new rule ends nearer the height the artists authored for the destination. The disagreement
only ever appears mid-walk, where the seed is the actor's own Y rather than an author's.

`WalkFloor.Surface` had its own separate nearest-height search with no notion of a storey,
so a footstep on the ruins could be answered by the hillside underneath them. Both questions
come out of one `Choose` now.

**A second cause, found while measuring the first.** `AnimationStart.Standing` returned the
hip triad's position outright, height included, and that is what the actor's logical
position follows while any clip plays. So the moment a walk ended and the idle started,
every actor in the game jumped 33.8 units into the air — visible in R25 as much as at CD1,
just harmless-looking there. It is not harmless: `SceneUpdate` seeds the next walk's first
floor query with that position, so the step an actor could climb was effectively 64 units
rather than 30. The reference takes the position from the hips and the *height* from the
lower shoe less `ShoeThickness` (`GKActor::GetModelFloorAndShoePositions`); `ShoeThickness`
was in `CHARACTERS.TXT` and was not being parsed. Gabriel now settles at 0.4 above R25's
floor and 722.9 on CD1's tower against the floor's 722.5.

Verify headlessly:

    GK3Reborn.exe --scene CD1 --timeblock 102P --frames 3400 --data <GK3>/Data       --run 'WalkTo("GABRIEL","USE_BINOCS"); @3350 DumpActor("GABRIEL")'

### Every character's normals lay on their side, so their fronts never lit — fixed 2026-08-28

**Reported** as a shadow that seemed inverted: "when gabriel is looking in the direction of
the sun, he tends to have a shadow on his chest instead of his back", and then, more
precisely, "fully face to shoes is in shadow ... in ANY rotation/direction" while his back
did respond to the light.

**A `.MOD` does not say which space its normals are in, and the corpus is not of one mind.**
Positions are always in the mesh's own space and are placed by `MeshToLocal`. The normals
beside them are in mesh space for a prop — and in the model's *local* space for a character,
already placed. `SceneGeometry.Add` stored both untouched and the vertex shader multiplies
by `mat3(draw.model)`, so a character's got the transform a second time.

It is not a small turn. Every character mesh group carries about ninety degrees of it, which
is 3ds Max's Z-up world written into GK3's Y-up one, baked per limb. Measured in the
renderer against a frame where the floor read exactly (0, 1, 0), **Gabriel's chest read
(-0.01, +0.98, +0.23)** — pointing at the sky. Every character was then shaded almost
entirely by the vertical part of the rig: the sun lit them the same however they were
turned, and their fronts, having no outward component left, never lit at all.

Measured over the shipped corpus, mean agreement between an authored normal and its own
triangle's winding:

| | positions | normals | read as placed | read as needing the transform |
|---|---|---|---|---|
| props, 462 models | mesh space | mesh space | 0.68 | **1.000** |
| characters, 23 models | mesh space | **already local** | **0.87–0.94** | 0.45–0.62 |

In RC1 at 112P, with the afternoon sun full on his face, Gabriel's shirt measured 100.7
against 99.5 at 110A with the sun on his back — no response — while the ground between the
same two frames went 78.1 to 142.4. It is now 183.8 against 134.4, and the room composites
byte-identically.

**Not ray tracing**, though it looks like a shadow: `--rt none` showed the same flat front.
A first pass at this blamed the models' winding against `kSkipShells` and was wrong — the
existing `RayTracingTests` pass on hardware, and working `Cover(shell: false)` through shows
counter-clockwise-from-the-ray is the front face here, so GK3's outward-wound shells are
already correct. Do not add `TriangleFlipFacing`.

**`ModNormals` measures it rather than declaring it.** `CHARACTERS.TXT` names most of them
and the engine already reads it, but it lists the forty-five characters who *walk*, and the
day-3 baby (`BAB`, 1,704 triangles over eight groups) is a character that does not. The
reading is each triangle's authored normal against its own winding, compared as an absolute
dot product because every mesh transform in the corpus has a determinant of -1 — the cross
product flips under one and the normal does not. It selects 27 models of 1,878: the
twenty-two characters, the baby, the chicken, and three flat cards.

**The model decides and a group may overrule it.** Read alone a group is often mute —
Vitorio's legs separate the two readings by 0.011 — and a character whose limbs disagreed
would be lit in pieces. That is not a concession: it is the case that matters, because
`HeadRefinement` rebuilds a subdivided head's normals from its mesh-space positions, so at
`--heads 2` exactly one of Gabriel's thirteen groups needs the transform the other twelve do
not, and a model-wide flag would lay that head on its side while fixing the body.

**The correction is a basis, not a flag** — the normals are stored pre-multiplied by
`inverse(MeshToLocal)`. The shader multiplies by the transform the mesh is posed by *now*,
so cancelling the authored one leaves the clip's own turn on the normal, which is what a
limb's normals should do when the limb moves. A flag would have fixed a standing character
and broken a walking one.

Still open, and separate: `SceneGeometry.Flush` updates only `Position` on a vertex-animated
mesh, so normals stay at the rest pose *within* a group. `.ACT` carries no normals, so there
is nothing better short of recomputing them per frame.

### The horizon fell away behind the first ridge, and stood in fields of stone needles — fixed 2026-08-28

**Reported** from the Tour Magdala lookout: "some mountains in the distance look horrible
as well with very sharp drop offs instead of rolling hills filling the horizon."

Two faults in the offline generator, `PbrLab/make_terrain.py`, and the first one is a
single line.

**The sight-line clamp read the whole ray.** Filled ground must stay below every sight line
from the panorama's own viewpoint or the reconstruction hides terrain the painting shows —
but only a point *beyond* a cell can be hidden by it, and the clamp took the shallowest
elevation ratio over every seen point at that azimuth, near ones included. The nearest land
at any azimuth is the lip of the black band under the camera: a hundred metres out, eighty
metres below, a ratio near -0.9. Extrapolated as a constant, that puts the ceiling at
-1,350 m by the rim of a 1.5 km grid.

So the outer half of every set was a chasm. RLC_A, the vista from the lookout, had a
**median height of -1,063 m** over land whose seen points sit between -85 and +180 — the
ground fell off a cliff behind the first ridge and kept going. It is a suffix minimum over
radius now, taken on a 1024-azimuth by 256-ring grid, so a cell is constrained only by what
lies past it; where nothing does there is no ceiling at all and the fill rolls out to the
horizon.

**The towers were a five-cell opening against forty-metre spikes.** The generator already
opened the heightfield to strip the needles that single-pixel depth outliers leave on a
crest, at five cells — twelve to sixty metres depending on the set. The 1999 art paints
crenellated limestone crags, and a monocular depth model reads each painted spur as a
surface of its own, so what survived was stone needles two hundred metres tall and forty
wide standing in fields. A second opening now runs at a width in *metres* (55) with a soft
cap on how far anything narrower may stand proud of its surroundings (14 m); a hillside
broader than the window is not touched at all, because the opening returns the hillside.

All 59 sets were regenerated, their forests re-scattered onto the new heights, published
and repacked. See `docs/rendering.md`, "The reconstructed horizon".

### A gorge filled half the sky with featureless grey — fixed 2026-08-28

Coume Sourde's panorama defeats the sky mask: 89% of what the generator called land was
steeply above the horizon, which no set with a working mask has any of at all. Monocular
depth gives sky a small depth, so those pixels project to a tiny ground radius and an
enormous height and max-splat into a dome sitting on the camera — two hundred metres thick
over the viewpoint and rising to a kilometre within two hundred metres of it. On screen
that was a wall standing behind the room.

Two guards, because it took both. The generator refuses land above an elevation ratio of
0.9, which is a statement about what a heightfield can hold and costs nothing anywhere the
mask works — measured over the corpus, no healthy set has a single pixel above 0.6. That
leaves CSD mostly fill, which is a smooth valley rather than a reconstruction, and is the
right way for it to fail.

The second is on the engine's side. `TerrainPipeline.LiftMeters` raises the whole backdrop
by a constant twelve metres, and a nearly-all-fill set sits close to zero — so the lift put
the camera a few metres *under* the surface and every direction became a wall of hillside
rising out of the bottom of the frame. `ClearanceMeters` keeps the camera two metres above
the backdrop's own ground, raising rather than clamping, because a lookout genuinely stands
sixty metres over its valley and that has to survive.

### The reconstructed horizon had no air in it — fixed 2026-08-28

**Reported** as "there is no atmospheric fog present which should be added to give the
feeling of being in a valley, esp. on the terrain distance."

There was a distance haze, at 1.6e-4 per metre, which leaves a ridge at the far rim of a
1.5 km reconstruction 94% of itself — which is to say there was not one. A hillside two
kilometres out was drawn as crisply as the wall in front of the camera.

What replaced it is aerial perspective with two properties a constant fog does not have.
**Density falls off with height**, so the haze pools in the valley and thins over the
ridges and a distant crest stands clear of the murk its own foot is buried in — the shape
the eye actually reads as depth in hill country. And **the haze goes warm toward the sun**
and is the sky's own horizon colour away from it, so the terrain dissolves into the sky
instead of ending at a line. The integral along the ray is closed-form, two exponentials.
Shared by the ground, the impostors and the modelled trees.

### The forest on the horizon was cones at every distance — fixed 2026-08-28

**Reported** as "close up trees in the terrain generator needs to be proper LOD0 trees as
now from the Magdala lookout all look extremely low LOD, it should gradually change from
high detail to low poly in the distance."

The backdrop's forest was instanced impostors — sixteen to twenty-four triangles apiece,
four silhouettes. That is the right answer for a hillside a kilometre out and plainly the
wrong one for the slope beyond the wall the player is leaning on.

The near band is drawn with the grown models the rooms already plant. Three tiers: the full
model for the nearest 48 within 70 m, the library's own `_far` variant nearest-first until
a triangle budget runs out, and the cone past that. Both pipelines read the same six-float
instance stream and derive the height jitter from the same seed, so a tree that crosses a
tier changes what it is built from and nothing else; and where the budget stopped is handed
to the impostor shader as the distance the cones start at, so a dense wood and a thin one
hand over exactly where the models ran out and no band is drawn by neither. Re-selected
only when the camera moves eight metres.

### Walking through a door froze the last frame and then cut — fixed 2026-08-28

**Reported** as "add a fade out, fade in between scene changes ... as some loads might take
a second or longer."

A scene change is a stall: the room being left comes off the device, the next one's
geometry, textures and acceleration structures are built, and nothing is drawn while it
happens. The last frame of the old room sat there for up to six seconds and was then
replaced, in one frame, by somewhere else.

`ScreenFade` covers it, and **the load runs inside the fade rather than before it** —
`SceneLoader.Progress` and `ISceneSink.Progress` are offered between pieces of work and the
fade presents a frame from those at thirty a second. A load that beats the fade stops it
where it is; the swap always happens at black, and the way back takes as long as the way
out did. What darkens is a photograph of the last frame, read back off the swapchain, so
the room's textures can be freed under it.

The one thing worth carrying forward: **the ramp has to be gamma-corrected**. The swapchain
is sRGB, so blending happens in linear light — an alpha of a half leaves the screen at 73%
of its brightness and an alpha of 0.995 still has the room faintly visible in it. Driven
straight, the fade looks like nothing happening and then the picture falling off a cliff.


### The maple beside the bench outside the hotel had two trunks — fixed 2026-08-28

**Reported** as the tree next to the bench in RC1 not being replaced, and as a double trunk
on the existing modelled trees.

Both, and they were the same fault. `rc1_vegitation` is that maple as the room draws it: a
bole in `Woodbark` with its leaves on it in `maple1trileaf`. `RC1_HOTELTREELEAVESFF` is a
flat `MAPLESIDE1` card of the same tree, placed by the scene file. Refusing the room's
object because bark is not foliage left the room drawing its 1999 trunk while the prop grew
a modelled tree with a trunk of its own beside it — and the 1999 leaf cards were still there
over both of them.

Bark is now read as part of the tree it carries, not as a reason to refuse it. An object of
foliage and bark and nothing else is a **whole tree**: its cards cluster into crowns, each
bole is claimed by the crown standing over it, and one tree replaces the pair, fitted to
both boxes together so it stands on the ground the bole stood on. Bark that no crown claims
is somebody else's and keeps its wood, which is what stops a fence sharing an object with a
tree from being taken away with it.

Where a prop is a card of the same tree, the prop is still what gets grown — it is what the
scene placed — but it is fitted to the room's measurement, which is the only one of the two
that knows where the ground is. The room's own copy is then suppressed by the rule that
already suppressed duplicates.

**Seventy-seven objects across the corpus** mix foliage with something, and 108 of those
mixtures are one of four bark textures: `NewBranch` 38, `Woodbark` 33, `Trunk01` 26,
`Trunk02` 11. `maple1trileaf` — leaves on real geometry, which twenty-two objects draw and
no species named — is now the maple's, which is what makes the RC1, CEM, RC2 and RC4 trees
foliage at all.

The tree that came out of that was still wrong, and for a second reason. A 1999 broadleaf
is drawn as **horizontal discs stacked up the trunk** — RC1's is three, 284, 172 and 115
units across, six and twelve units apart — with side sprays hung off the branches. The
clustering was written for conifers, which are two cards crossed at the trunk, and it asked
cards to *overlap* in height: the two upper discs failed, started clusters of their own, and
grew as two more trees hanging in the air over the first with boles of their own. A crown is
about as tall as it is wide, so the vertical gap now gets the same reach as the horizontal
one; and the side sprays, which no reach can catch without also gathering a stand of spruces
into one tree, are settled by the bole — anything standing inside a trunked crown belongs to
it. Across the corpus, 922 crowns become 819; all 103 folded in are pieces of a tree that
has a bole, and the 618 conifer crowns are untouched.

The last of it was cosmetic and just as visible: the grown tree's own **limbs ran bare
through its crown**, and bark is pale where a leaf card is dark, so they read as sticks
pushed into a bush. Leaves are now hung along the whole length of a limb rather than at its
ends.

### Riding the moped never went by way of the map — fixed 2026-08-28

Reported as arriving at Larry Chester's house with no moped in the yard and no way out
of the scene.

**The driving map is a location in the original, not a panel over one.** Its location
table lists `map` beside `lhe` and `mop`, and the driving layer holds that entry's index
as its own location — so a ride leaves the room for the map and arrives from it. This
engine draws the map as one of the modal screens and set the destination straight from
the room the player had been standing in, so nothing was ever "from the map". Three
things followed from that at Larry's house alone:

- **No moped.** `LHE.SIF` declares `bikebody` under
  `GetGameVariableInt("BikeLocation")==11 || WasLastLocation("Map")`. Neither held on a
  first arrival — the variable is set by the yard's own enter script a moment *later* —
  so the model was never placed and the `GABES_MOPED` noun did not exist.
- **No way out.** The yard's only route back to the map is
  `EXIT_TO_MAP, EXIT, BIKE_HERE`, and `BIKE_HERE` is that same `BikeLocation==11`. The
  other two cases play a line about the moped not being here. `EXIT_TO_CDB` and
  `EXIT_TO_LMB` are `approach=WalkTo` to spots the far side of the yard.
- **Standing at the origin.** Every one of the sixteen places the moped reaches names an
  `FR_MAP` spot to arrive at, and three of them put it on the player's own actor line.
  Arriving "from MOP" matched no `FR_MOP`, so `StartPosition` fell through to nothing and
  the player stood at the origin until a script moved them.

`GameState.RideTo` makes the ride two moves — into `DrivingMap.Location`, then on to the
destination — which is all it takes: `LastLocation` says `MAP`, and every question the
data asks about a ride has the answer it was written against.

**And it parks the moped where it was ridden.** Larry's house was the only place a ride
could be rescued by `WasLastLocation` alone; the other five that draw a moped ask only for
`BikeLocation`, and Blanchefort, Coume Sourde and L'Homme Mort guard their way back to the
map with the same number. Riding to Blanchefort therefore stranded the player in a field
with no moped in it — reported, and reproduced.

Nothing in the retail engine ever writes that variable: its name sits in a table with no
code reference, and only `LHE.SHP` and `MOP_ALL.SHP` set it, to 11 and 10, from their own
arrival scripts. The other four read a number nothing writes. `DrivingMap.ParkedAt` supplies
it, and the number is the place's own index in the map's list — the six the data gives a
number are 3, 4, 9, 10, 11 and 12, which are exactly their positions in the retail driving
layer's own order. `RideTo` writes it before the room is built, because a scene file's
conditions are decided as it is read while the two scripts that set it themselves run
afterwards. One variable means one moped: parking it somewhere new is what empties the
place it was, which is also why `CDB.SIF` draws it from Larry's number — the driveway
overlooks the yard.

**It was never only Larry's house.** Ten of the compiled scene scripts branch on
`WasLastLocation("map")`, including `PL3` and `CSE_ALL`, and `PL4_ALL` asks
`IsActorAtLocation("estelle","map")`. All of them were taking their fallthrough branch.

`DrivingArrivalTests` covers it: seventeen tests over the ride itself, the moped's
condition in both directions, the number parked at each of the six places that name one,
riding away again, and the spot arrived at.

Reproduce (before the fix):

```bash
GK3Reborn.Host --start MOP --timeblock 110A
```

Ride to Larry Chester's house and look for the moped, or for a way out.


### The driving map was a list of place names — fixed 2026-08-28

Reported as the map showing text where it should show the painting.

`VulkanRenderer.SetOverlayAtlas` rebuilt the whole overlay pipeline to change the sheet
of letters, and a new pipeline has a new descriptor pool, so it cleared every picture
number it had handed out. The interface has more than one sheet — the room's captions and
the menu are cut at different sizes — and `SetOverlay` swaps whenever a display list
arrives from a different one. The front end draws at startup, so the driving map's
seventeen pictures were loaded once, thrown away seconds later, and never reloaded:
`Driving` found no background and fell back to the list it keeps for archives that have
no art. Sidney's survey map went the same way.

`OverlayPipeline.SetAtlas` now replaces the atlas texture and binding zero of its
descriptor set and leaves the shaders, the pipeline, the pool and every loaded picture
alone. Two shaders are no longer recompiled every time the player opens the menu either.

**And the map now names its places.** A marker is a lit copy of the patch of painting
under it, which says that something is there and nothing about what; the original left
the player to hover each of the sixteen in turn. The open places are listed down the
side, each row rides there, the one under the pointer is ringed and named on the map
itself, and pointing at either the row or the marker lights up both. The names are the
game's own, out of `ESTRINGS.TXT`. The column is dropped on a panel too narrow to hold it
without taking the painting down to a thumbnail, where the name on hover still works.

Reproduce (before the fix):

```bash
GK3Reborn.Host --start MOP --timeblock 110A --screen Driving
```


### Room-to-room crossfade overlapped two beds audibly — removed 2026-08-27

**Reported:** 2026-08-27, as significant overlap between sounds. The crossfade added on
2026-08-22 (below) is gone; a room's bed now stops when the room does and the next one
starts at its own level.

The overlap was the design, not a fault in it. `FadeOutMS` decided how long the outgoing
bed stayed audible and the corpus asks for up to three seconds — R25's theme does — which
is most of a walk through a door, spent hearing two rooms at once. `SceneAudio.Leave` now
silences the bed instead of handing it on, and `Fade` is `Begin`, playing the next bed at
full from its first sample. `Crossfade` and `Drop` are gone with the four fields they
needed, and `FadeOutMS` has no consumer left anywhere.

**A second departure used to strand a voice.** The outgoing bed lived in one field,
`_leaving`, which `Leave` overwrote with `_ambience` — already cleared by the previous
`Leave`. Leaving two rooms in quick succession, which is a corridor, therefore dropped the
first room's voice while it was still playing and owned by nothing: no later room could
stop it, and each hurried door added another. That is a plausible second source of what was
reported, and it cannot recur because there is no such field. `AmbienceFadeTests` pins it
along with one bed at a time.

**What it cost.** The thing the crossfade was for is back: a door is two cuts again, and the
bed is a five-minute MP3 decoded off the thread, so the next room stands silent for the
quarter-second that takes. If it wants solving again, the way that does not overlap anything
is to finish the outgoing fade *before* starting the next bed rather than under it.

### A character cast a full shadow on ground a building already shaded — fixed 2026-08-27

**Reported:** 2026-08-27, as the sun appearing to shine through the hotel: Gabriel steps out
of the lobby on the first morning, and he and the hotel's own door pick up a hard cast
shadow in a place the hotel has already taken every ray of sun out of. **Fixed the same
day.**

The two shadows are traced separately and for a good reason — `Shadow` is the room's own
occlusion, the half a bake already contains and the only half subtracted against it, and
`DynamicShadow` is what characters and props take away, which multiplies the result. What
the trace pass never did was let the second answer depend on the first. It asked "does a
model stand between this pixel and the light" without asking whether anything else already
did, and `CompositePipeline` spends that answer on `residual`, the bake-shaped part of the
indirect term:

    residual *= mix(1.0, unblocked, rig.a);

So on ground a wall shades, `shadow` is nought, `arrived` is nought, the pixel is the bake —
and then a person standing on it multiplies that bake by their silhouette against a sun
that does not reach them. A second shadow, hard-edged, laid inside the first.

**RC1 at 110A is the case.** The hotel stands between the square and the morning sun:
tracing the scene's own occluders against the replacement sun, every ground point from
x 2000 to 2700 and z -1350 to -1800 — the whole of the square outside the front door — is
blocked by `rc1_hotel`, at 168 units from the doorstep. Rendered at High, the ground beside
Gabriel came out 15% darker than the bake in the shape of his silhouette, and the woman by
the van and the van itself each laid one of their own.

**The two calls are about one ray.** `ShadowRay` is deterministic in the pixel and the
sample index, so the room call and the models call pick the same light and the same point on
its emitter. So the fix is to answer for that ray: where the room stopped this sample, the
models are not asked and the sample counts as clear. Nobody can take away light that never
arrived.

Where the sun does arrive nothing moves — RC1 at 202P, with Gabriel in the open and casting
a full shadow, renders to within one level of its old self across the whole frame. Where it
does not, all that is left under him is the contact darkening ambient occlusion gives, which
is meant to be there: measured on the test room, a model takes 41 luminance off the floor it
stands on in the light and 0.9 off the same floor in the room's shadow, against 7.8 before.

`A_model_in_the_room_s_own_shadow_takes_no_more_light_away` is the regression, and it needs a
bake to show anything: `rig.a` is written only where a lightmap is, so a synthetic room
without one composites identically either way. `MulFile.FromParts` is new, for the same
reason `BspFile.FromParts` is.

### The binoculars and everything else anybody holds were at the world origin — fixed 2026-08-27

**Reported:** 2026-08-27, from Poussin's Tomb on the second morning: the NPCs raise and
lower binoculars correctly and do it in mid-air at the far corner of the map. **Fixed the
same day.**

**A held prop is not animated in the room's coordinates.** POU207A declares six props with
no position at all —

    model=abebinocs, type=prop, hidden
    model=bino1, type=prop, hidden
    model=vpencil, type=prop, hidden
    model=pad_, type=prop, hidden
    model=cam, type=prop, hidden
    model=lens, type=prop, hidden

— because their position is meant to come from the person holding them. Each is a second
model exported from the same 3ds Max scene the character was, so its clip is authored around
**the character's own origin** rather than the room's. `AbeBinocUp.ANM` is two clips and no
placement:

    [ACTIONS]
    2
    0,abebinocs_AbeBinocUp
    0,abe_AbeBinocUp

`SceneUpdate.Playing.Correction` plays a prop's clip exactly as authored, which is right for
the 92% of prop clips that *are* in room coordinates and wrong for these. Measured through
the real loader at POU 207A, before the fix:

| clip | prop landed at | its owner stood at | apart |
| --- | --- | --- | ---: |
| `AbeBinocBreath` | (0, 63, −6) | abe (337, 259, −469) | **604.9** |
| `VitMagBinoc1` | (3, 66, −11) | vit (211, 259, −441) | **515.7** |
| `VitRc2Write` | (−1, 50, −16) | vit (213, 259, −441) | **520.2** |
| `Lh2FidgetWithCam` | (−3, 44, −93) | lh2 (140, 255, −430) | **422.2** |

Every one of them is within about a hundred units of the world origin, at chest height,
animating perfectly.

**Who a clip belongs to is in the animation's name.** The original reads the first three
letters, finds that model in the scene, and copies its space onto everything else the file
animates, every frame — `VertexAnimNode::Play` picks the holder and
`VertexAnimator::OnLateUpdate` copies the transform. `SceneUpdate.Holder`, `Carry` and
`ModelSpace` are that, and `_carried` is the binding.

The rule is safe because the data says so. Across the whole corpus, **455 clips** sit in an
animation whose three-letter prefix it also animates, and **every one of them is authored
within 94.3 units of that character**, at a median of 27.6 — arm's length. Not one is in
room coordinates. Even `MADSMOKING`'s cigarette, 642 units from the world origin, is 42.2
units from Madeline, because both were exported with her parked out there.

Three details are the original's and each of them showed up as a defect first:

- **The binding outlives the clip.** `VertexAnimator::Stop` clears the animation and leaves
  the parent. `AbeBinocIdle.gas` is a loop of eight separate animations, so dropping it
  between each pair blinks the binoculars back to the origin between every one of them.
  `_space` keeps the last correction for the same reason: a character's space is their
  placement *plus* their clip's correction, and the correction dies with the clip.
- **A carried clip is not corrected.** For a prop the correction was the identity anyway,
  but a clip that carries a *person* — `DemTe6KillGabe` moves `gab` — would otherwise be
  shifted to their own rest and undo the binding.
- **The prefix has to be the animation's subject.** This asks that the animation carry a
  clip for the model it names, which the original does not. It is narrower by exactly four
  action lines in the corpus — `GabJmpOffPen` and `GabJmpPndulm` — and the original excludes
  those by hand with `noParenting` in `Pendulum`. So the two rules agree on every line of
  the game's data, and this one says so without the pendulum ported.

**Still open: a prop carried while walking.** `WalkCycle` plays the character's own stride
clip and nothing else, so the three props that ride along in a walk animation — Prince
James's `cane`, Roxanne's `dust`, Buchelli's `papernew` — do not animate at all while their
owner is walking, rather than animating in the wrong place. Fixing it means a walk playing
its whole `.ANM` rather than one clip, which is what the original's other parenting branch
(`Walker` setting `animParams.parent`) exists to serve. The other 47 stride animations that
name a second model name a `dor_` facing helper, which no scene places.

`HeldPropTests` covers it: five tests, three of which reproduce the reported fault exactly
when the binding is disabled.

### The synthesised sun pointed somewhere the room was never lit from — fixed 2026-08-27

**Reported:** 2026-08-27, as the ray-traced light arriving from nearly straight up rather
than from where the sun is. **Fixed the same day**: `Sunlight.For` takes the scenekey's
own bearing where the asset ships one, and the hour decides only whether there is a sun
and — through the elevation that bearing turns out to stand at — how warm it is. RC1 at
110A now reports `Sun: elevation 40deg` against the artists' 40, and its square measures a
ground mean of 69.3 against the 1999 bake's 71.5, where the old arc made it 99.4.

`Game/Sunlight.cs` throws the artists' `scenekey` away and replaces it with an arc that
knows only the hour: `elevation = sin(day·pi)·62°`, `azimuth = lerp(80°, 280°)`. Nothing in it
reads the scene. The scenekey it replaces carries the direction the room was *baked* from,
and every asset that names a skybox also carries a `Skybox.Azimuth` saying how the painted
sky is turned; neither is consulted.

| scene, block | artists' scenekey | synthesised | apart |
| --- | --- | --- | --- |
| RC1 110A | elev 40°, az 31° | elev 51°, az 141° | **110°** |
| RC2 110A | elev 40°, az 30° | elev 51°, az 141° | **112°** |
| POU 110A | elev 61°, az −90° | elev 51°, az 141° | **128°** |
| CEM 112P | elev 61°, az 153° | elev 62°, az 172° | 19° |
| RC1 102P | elev 61°, az −174° | elev 58°, az −157° | 17° |

The afternoon blocks land close by luck. Every morning block is a third of the way round the
compass from the bake and from the sky above it.

**It reads as overhead as well as sideways.** RC1 at 110A has two lights that reach the
whole town: this sun (elev 51°, luma 1.06) and `sky_bounce` (elev **70°**, luma 0.26).
Their sum sits at elev 56.5°, with 83% of the light vector pointing straight down.
`ground_bounce` is at elev −81° — from below — and attenuates to nothing at the square's
middle.

**The scenekey was replaced for its reach, not its aim.** Its two hundred unit range cannot
touch the geometry, which is what item 4 was about; its azimuth and elevation were never the
problem. Keeping the direction the artists gave it and overriding only reach, attenuation
and emitter size would put the light back where the bake and the painted sky agree it is,
and would cost the timeblock arc nothing — the arc is still the answer for a scene whose
asset ships no scenekey at all.

### A shadow could only take about a fifth of an outdoor pixel — fixed 2026-08-27

**Reported:** 2026-08-27, with the item above, as shadows being very weak outdoors.
**Fixed the same day.** The measurement that follows is what the fix was sized against.

Measured on RC1's sunlit ground at `--rt high`:

- **54% of a lit ground pixel is the ambient floor** — `residual` in `CompositePipeline`,
  which is `ambient × (0.30 + 3·baked)`. The dynamic shadow never touches it, deliberately
  and for the reason that pass documents at length. But at Medium and High the bake is still
  *shaping* that ambient, so a patch of ground the 1999 bake recorded as full sun keeps a
  bright ambient floor with a character standing on it.
- Of the remaining 46%, the sun is about half. The room-shadow fraction inside the obelisk's
  own cast shadow bottoms out at 0.50 and never approaches zero, because `sky_bounce` and
  the lamps are unshadowed there.

So a character's deepest possible shadow is around `0.54 + 0.46 × 0.5 ≈ 0.77` of a lit
pixel: under a quarter darker before gamma, about a tenth after. The obelisk does better
only because its shadow is in the bake too, so it darkens `shaped` as well — which a
character's shadow can never do.


**What was done.** The mesh pass now says, in the rig target's spare alpha, how much of a
pixel's ambient floor is the bake's doing — one minus `kHintFloor` over the shaped floor's
own luminance — and the composite takes that share of the residual away with the dynamic
shadow. The floor under the hint is untouched, because that part is there to keep a dark
corner from reading as a hole and is ambient in the ordinary sense.

The share is written as the part that *may* be taken rather than the part that may not, so
that nought is the right answer for everything the pass does not write. That matters more
than it sounds: the indirect target's alpha clears to **one**, so every is-this-a-surface
test built on it reads the sky as a fully baked wall, while the dynamic channel over the sky
is nought because the denoiser writes nought wherever there is nothing to shadow. Written
the other way round the background went black, which is exactly what the first attempt did.

Gabriel's shadow on RC1's square goes from 0.92 of the lit ground beside it to **0.67**.
Frame means move by a tenth of a per cent or less on RC1, LBY and DIN and by 0.9% on R25,
which is the whole of the point: nothing changes except where somebody is standing.


### A character could not shadow itself or anybody else — fixed 2026-08-27

**Asked for** 2026-08-27, having been refused outright since ray tracing was written: a
shadow ray leaving a model traced the room and skipped every model, its own included.

The reason was real. **GK3's people are not solid bodies** — a shirt shell around a whole
torso, sleeves around whole arms, a collar around a whole neck — so the surface a ray starts
from is very often inside another mesh of the same person and the ray hits it before it has
gone anywhere. No bias fixes that; the geometry really is there, and every character came
out with a hard dark patch across the chest and the small of the back.

**Which side of the triangle the ray arrives at is what tells the two cases apart.** Leaving
a surface that is inside a shell, the ray meets that shell from within and hits its back
face: an artefact of how the model is built. Blocked by something genuinely in the way — an
arm across a chest, a hat brim over a face, another person between this one and the lamp —
it meets that surface from outside and hits its front face. So the self ray culls back
faces, and model instances keep their winding for it. The room does not and is not asked to:
a BSP's polygons carry no consistent winding, which is why each triangle is given its own
plane's normal at load, and every ray that traces the room asks for no culling anyway.

It costs the shadow a shell casts on whatever is directly inside it, which is a shadow
nobody can see — the thing inside is not drawn where the shell covers it.

`A_model_is_shadowed_by_another_model_and_by_itself` and
`A_shell_around_a_model_does_not_shadow_it` are the pair. The second is the one that fails
without the culling, and it is a controlled comparison: the same four corners, the same
shading normal, nothing back-face culled when it is *drawn*, and only the winding reversed —
so the difference between the two renders is the traced ray and can be nothing else. Neither
uses a room, because a floor would be shadowed by the same occluder through the other half
of the structure, where no face is culled, and its brightness would swamp the panel's.

### Faces were stippled with shadow acne — fixed 2026-08-27

**Reported:** 2026-08-27, as "shadow acne smears" on faces, while the self-shadowing above
was being added. **It was not that**, and the measurement said so before anything was
changed: rendering the same frame with self-shadowing off gave 2.25% speckle against 2.22%
with it, and pushing the self ray's start out to thirty units — twenty times what it needs —
changed nothing at all. The stipple was in the room-shadow channel and had always been
there.

**The filter's depth tolerance was in the wrong units.** A blurring pass weighs a neighbour
by `exp(-|Δdepth| / sigma)` with AMD's own sigma of a hundredth, and `LinearDepth` returns
view-space Z **in scene units**. A GK3 unit is about two and a half centimetres, so a room
is hundreds of them deep and a wall one pixel further from the eye than its neighbour is a
hundred tolerances away: the exponential returned nothing and the blur only ever ran across
surfaces standing square-on to the camera. Walls are square-on, which is why the room came
out clean. A head is not square-on anywhere, so the eight rays a pixel spends stayed exactly
as noisy as they arrived.

Dividing by the depth first — which is what the reprojection's own `IsDisoccluded` already
does — takes R25's head from 2.22% speckle to **1.43%**, below the 1.59% the hair texture
itself carries at `--rt none`. Four times the rays only reached 1.47%, and this costs
nothing.

A settled character was never affected: the host at 400 frames measures 5.34% on the same
crop at High against the bake's 6.44%, because the temporal filter gets there in the end.
What this fixes is every pixel that has no history to average — the first frame after a cut,
and anything the camera or the character is moving.

### Nothing placed in a room cast a ray-traced shadow — fixed 2026-08-27

**Reported** as characters casting only a very faint shadow outdoors, alongside two separate
complaints about the sun's direction and about how weak a shadow could be, both above.

**Every model was traced at the world origin.** `RayTracingScene.Build` puts a model's
triangles into the structure in the model's own space and places them with an instance
transform, and it has none to place them by: everything it builds starts at identity.
`SceneGeometry.Finish` replayed the *hidden* models onto the finished structure and never
replayed where any of them stands, and `MoveModel` — the only other caller of `Move` — runs
when the story moves somebody. Nothing moves a prop after a room has loaded. So RC1's van,
benches and signposts sat piled at (0, 0, 0) for the life of the scene, shadowing whatever
is there and nothing where they are drawn; an actor came right only once something first
walked them somewhere, which is why *some* shadow appeared in play and none at all through
`render-scene`.

Measured on RC1 at 110A, dumping the composite's three shadow inputs: the models-only
channel read fully lit on 131,959 of 131,972 ground pixels. Occlusion went with it, because
a floor pixel's occlusion ray traces everything and there was nothing of a character there
to find. `Finish` now replays each placement's transform and settles the structure before
the first frame draws.

**The three tests here could not have caught it**, and that is worth remembering: all of
them stand their occluder at the model's own origin, where identity is the right answer.
`A_model_shadows_the_room_from_where_it_is_placed` is the one that fails without the fix —
it places the same wall five thousand units away and asserts the floor stops being shadowed.

### The tour at Poussin's tomb was cut off at the hips — fixed 2026-08-25

**Reported** as legs missing on the second morning, and correctly re-diagnosed by the
reporter as a sitting pose mixed with a standing one.

The eight people on the tour are given marks to stand on *and* `initanim=VanPouIN`, which is
the van arriving: two hundred frames, an engine, a door slam and a soundtrack under it. An
opening pose is **sampled at its first frame** rather than played — which is right, and is
what stands Madeline by the van at RC1 and sits Emilio in the lobby. The first frame of
`VanPouIN` is all eight of them *inside the van*.

So they were posed seated — thighs horizontal, shins hanging — while standing upright on
their marks, and nothing afterwards put them back. Their idle scripts then animated the upper
body only, so the top of each of them stood and talked while the legs stayed in the van. The
clips are not at fault: every mesh of `gra_VanPouIN`, `wi2_VanPouIN`, `est_VanPouIN`,
`mos_VanPouIN` and `vit_VanPouIN` has a frame-zero transform and a full-count vertex shape.

**A performance is not a pose**, and an actor the scene has already stood somewhere does not
need one. The two are told apart by the soundtrack — the one thing in an animation file that
only something happening has. Across the corpus that picks out **nine actor declarations**:
these eight and Lady Howard's driver getting out of the car at LHE.

The alternatives were measured and rejected. 758 `initanim` references across 385 animations;
156 run longer than sixty frames and 259 carry sound effects, so neither length nor sound
separates a pose from a performance. Of the 47 actors given both a mark and an opening pose,
26 use an animation that drives more than one model — including `MadRc1FigM`, which is a pose
and must stay one.

### Untextured models came out magenta — fixed 2026-08-25

**Reported** as objects floating in front of Gabriel, "mostly untextured (purple)".

A `.MOD` group carries a texture name **and** a colour, and a few of the game's models use
the second instead of the first. `BINO1` and `ABEBINOCS` — the tour's binoculars — name no
texture anywhere in the file; they are a dark teal body and near-black rubber, stored as the
two groups' colours, which nothing read.

With no texture name they took the missing-texture fallback, which is a magenta chequerboard,
and turned up as a loud purple object. A group that names no texture now draws its own colour
from a single texel. The chequerboard stays for a texture that is *named* and not found —
that is a real fault and should be impossible to miss.


### Gabriel stood in every scene that was Grace's — fixed 2026-08-25

**Reported** at Poussin's tomb on the second morning: Gabriel visible in a scene he is not
in, while Grace is the one taking the tour.

A location's general file names the person whose game it usually is, and a timeblock file
names the person whose game it is now. `POU.SIF` declares Gabriel as ego; `POU207A.SIF`
declares Grace. The two casts were **joined**, so both were placed — Grace where the scene
said, Gabriel at the origin with no spot of his own, and the room had two egos, which is one
more than anything downstream expects.

It is not one scene. **157 scene and timeblock pairs across the corpus** name Gabriel
generally and Grace specifically: every Grace timeblock in every location she visits.

The timeblock's ego now replaces the general file's rather than adding to it, and the same
person is never placed twice. `check-scenes` reports the same four diagnostics as before and
153 fewer nouns, which is Gabriel no longer being in 157 rooms he was never in.

### A room with a sky could still have no sun — fixed 2026-08-25

**Reported** as an outdoor scene with no sun, no shadows, and odd self-shadowing.

The rule was "a sky **and** a timeblock". The sky says the room is outdoors and the timeblock
says where the sun stands, and a room entered without the second was lit flat and cast
nothing — which looks like a bug in the renderer rather than a missing argument.

A sky now always means a sun, and *whether that hour has one* is `Sunlight.For`'s business:
it answers null at night, which is a sun's absence for a reason rather than by accident. The
hour is the story's clock, then whatever the caller named, then **the asset's own suffix** —
`_M` morning, `_A` afternoon, `_E` evening, `_N` night. That last one is a real answer and
not a guess: it is the same letter that chose the lightmaps the room is already lit by, so
the sun agrees with the bake by construction. Mid-morning when even that is silent.

Measured: `POU` at 207A gets a low morning sun; `POU` with no timeblock at all gets the
afternoon one its default asset is baked for; `CEM` and `WOD` at night get none.

### Two trees and a painted hillside in one object kept all three flat — fixed 2026-08-25

**Reported** as sprite trees at Poussin's tomb where the modelled ones were expected.

`pou_trees01` is two trees and a strip of distant woodland painted on one quad. The trees
can be replaced and the strip cannot, and a room is hidden **by object name** — so the whole
object was refused and its trees stayed flat.

`AddScene` now takes surfaces to hide as well as objects, and an object is refused only for a
texture that is neither a species nor a known backdrop strip. **Nineteen objects** across the
corpus are shaped that way — `background_trees`, `pl2_trees`, `pou_trees01`, `vgr_bushes` —
and every one of them now grows its trees and keeps its backdrop.

The line was drawn deliberately short of the dangerous case: an object carrying `NEWBRANCH`
or `TRUNK01` was still refused whole, because growing a tree from its leaves alone puts a
second trunk through the modelled one. That was the right call with only the leaves to work
from, and it is superseded — *The maple beside the bench outside the hotel had two trunks*
takes the bole away with them instead.


### A .gk3 in the game's own saves folder was never imported — fixed 2026-08-25

**Reported** as three GK3 saves in a deployed build's `saves` folder that the game does not
pick up.

The importer was pointed at two places, both belonging to the 1999 install: its root, taken
as the parent of the `--data` directory, and the `Save Games` folder beside it. It was never
pointed at the folder the port keeps its own games in — which is the first place somebody
with a `.gk3` file and no original install will put one, and the only place a deployed build
has at all.

The application's `saves` folder is now searched **first and always**, whatever else is on
the command line, followed by the store's own directory when a read-only install has sent
saves to the profile instead, and then the two original locations as before. Import stays
idempotent by slot name, so the first of those to hold a given file wins and a second launch
brings nothing across twice.

The `.gk3` is read and left exactly where it is. Deleting the `.json` it produced is still
how somebody asks for it to be brought across again.

Measured on three retail saves dropped into a saves folder: three imported, `RC1` day 1 2pm,
`TR1` day 1 4pm and `POU` day 2 7am, all reading back with no fault; a second run imported
none; all three `.gk3` files still present.


### Saves imported from the original game could not be reached — fixed 2026-08-25

**Reported** as three GK3 saves sitting in the saves folder that the game does not pick up.
Everything picked them up except the one thing that mattered.

`OriginalSaves.Import` had brought them across correctly, filed under the file names they
had in the 1999 install — `gk3-save0009`, `gk3-save0015`, `gk3-save0024`. `SaveStore.List`
returned all three. `SaveStore.Read` restored any of them perfectly when asked for by name.

The restore **page** drew a fixed fourteen rows: quick, autosave, and slots 01 to 12. A save
filed under any other name had no row, so there was no way to point at it. The import was
working and invisible.

The page now draws a row for anything else the store holds, on the restore side only — the
numbered twelve are what a player saves into, and overwriting an import would throw away the
one copy of the thing it was brought across for. An imported row reads *Original save* and
its title rather than "Slot gk3-save0009", which is what the numbered naming made of it.

Three tests hold it: that a non-numbered save is offered when restoring, that it is offered
once and reads as what it is, and that saving never offers it.


### Restoring a save from the pause menu closed the game — fixed 2026-08-25

**Reported** as a crash on restoring while in-game, and it is not a crash: the game shuts
down cleanly and instantly, which from the other side of the screen looks the same.

The pause menu's Load branch ended in `break`. That leaves the **frame loop**, and the only
thing after the frame loop is `return new RoomExit(0, null)` — which the room loop reads as
"the player quit" and ends the game. The `api.Wanted = story.Location` set two lines above
it was dead: nothing reads it before the `break`.

It shows in a log as a restore with no room after it:

```
Restored 01: Gabriel's Room - Day 1, 10am - 12pm
Presented 236 frames in 3.9s (61 fps)
```

**Quick-load never had the fault.** F9 sets `api.Wanted` and falls through to the handler at
the bottom of the frame loop, which cancels the update and returns a `RoomExit` carrying the
destination. The menu branch now does the same thing, and refuses to return an empty
destination — a save naming no room leaves the player where they are rather than being
mistaken for quitting a second time.

Nothing to do with the modelled trees, which landed the same day; the branch dates from the
commit that added saving.


### A coloured triangle flashed between the intro films — fixed 2026-08-25

**Reported** as a short frame with a colourful pyramid in it between the intros, and guessed
at as stale Vulkan testing. That is exactly what it was.

`TrianglePipeline` is the first bring-up: three vertices from `SV_VertexID`, red at the apex
and green and blue along the bottom, and it proves a device, a swapchain and a present loop
reach the screen on a machine with nothing else to show. It was built at startup always, and
`DrawFrame` falls back to it whenever there is no room to draw — so it was underneath every
frame of the intro, hidden only because a film covers the window.

**What uncovered it is the film ending.** `MoviePlayer.Stop` drops the picture, and the intro
loop hands the renderer whatever picture there is and then draws, so the frame in which a
film ends or is skipped has no picture in it. That frame stays on screen for as long as
opening the next film takes, which is long enough to see.

The triangle is opt-in now — `VulkanRenderer.Create(..., bringUp: true)`, which only the
`--render` smoke test asks for — so a frame with no room and no picture draws the clear
colour, as the black between two films should be. The same fallback was under the menu, the
timeblock card and the moment between two rooms, and the card without its painting was
documented as showing its words over black when it would have shown them over this.

### Gabriel's right shoe came off his ankle every stride — fixed 2026-08-25

**Reported** after the walk was made to interpolate: his right shoe lags behind, comes away
from the leg for a few units, and corrects itself.

**Interpolating between the two *recorded* poses either side of a moment is not the rule.**
The reference mixes an entry only with one recorded on the frame *after* it —
`VertexAnimationPose::GetForTime`, whose own comment says so: if the next pose is not for the
next frame, use the current pose with no interpolation. A gap in the recording means the mesh
does not move, because a mesh that does not move is not written again. The port mixed across
gaps however long, which sets a mesh off the moment its hold begins and lands it as the hold
ends — the whole gap spent somewhere it never was.

**A walk is the clip that holds most, because a planted foot does not move.** `GAB_GABWALK`
records his right shoe on frames 0 and 4 to 15, and his left on 0 to 5 and 14 to 20; the gaps
are the half of each stride that foot spends on the ground. Eleven of the model's thirteen
meshes are recorded on every frame there is, so everything except the two shoes was being
mixed correctly, and the shoes were the only things that could part company with the rest.

**Why the right and not the left.** The left shoe's gap is in the middle of the clip and the
poses either side of it are a quarter of a unit apart, so mixing across it was invisible. The
right shoe's hold runs off the end of the clip, so what it mixed towards was the *opening*
pose, a whole stride's travel away: **fifty units of it, over the last six frames of every
loop**. The body crept with it — the forward travel taken out of the walk is the mean of all
thirteen meshes, so a shoe fifty units adrift moved the mean by three.

The wrap is a step of one frame like any other now, and only happens where the last recorded
pose is on the clip's last frame. The lobby's fans still come round: their blades are
recorded on every frame, which is what the wrap needs and what the shoes do not have.

### An item kept its hotspot after being picked up — fixed 2026-08-24

Taking something takes its model out of the room, so a click already stopped finding it —
`ScenePicker.Pick` skips anything that is not being drawn, and has since the moped waiting
for its scripted ride past RC1 first caught the pointer on empty air. `Interactive`, which
is what holding **Alt** asks for every hotspot at once, made only the other of the two
refusals: it skipped what a script had switched off and not what was no longer drawn. So the
red cap kept a label on the shelf it had been lifted from.

Both lists make both refusals now. The visibility test also goes *before* the one that keeps
each noun once, so an invisible copy of something no longer claims the noun and hides a
visible one behind it.


### The walk was the one animation that did not interpolate — fixed 2026-08-24

**Reported** as the walking legs feeling choppy while the rest of a character did not.

Every clip in the game is played through `ActFile.PoseAt`, which mixes the two *recorded*
poses either side of the moment — a spherical blend for the rotation, since mixing two
rotation matrices shrinks whatever is between them — and `ShapeAt`, which does the same for
the vertex shapes. The stride went through neither. `WalkCycle.Step` truncated to a whole
frame and asked for `PoseOf` and `ShapeOf`, so a walk recorded at fifteen poses a second and
drawn at a hundred and forty showed each pose nine times over. It was the only thing left in
the game still playing at 1999 rates, which is exactly how it was reported: the legs, and
nothing else.

The forward travel is taken out at the same fractional moment that poses the meshes. Taking
it from a whole frame while the legs are mixed between two is the difference between a walk
and a skate.

**What still measures whole frames, deliberately.** How far the stride travels — which sets
the pace the feet have to match — and whether the last frame closes back onto the first are
questions *about* the clip rather than moments *in* it, and a mix bends them towards a wrap
that has not happened. Those keep `PoseOf`. So do the footsteps: a footstep is an event on a
numbered frame, and asking for it twice because a moment landed either side of one is a
doubled sound.

### The clock moved on and nothing said so — fixed 2026-08-24

**Reported:** there is no real indication when the hour switches; it should be made
apparent.

**The original has a screen for this and the port had a console line.** A timeblock ending
dissolved one room, built another, and two hours of story had gone by with nothing said
about it. `TimeblockScreen` in the reference shows a painting for the point in the story with
its name lettered over it, and every one of those paintings is in the archives —
`TBT110A.BMP` and its sixteen siblings — along with `D110A_01` to `_15`, the lettering, as a
sprite series.

So the card is back, in the place the original puts it: after the timeblock's closing film
and before the next room is built, which is the only place it can go — after that the player
is already standing somewhere new.

**The painting is kept and the lettering is not**, which is the division the title screen
already makes: the picture is art and the words are a widget. The original draws the name as
a fifteen-frame sprite animation whose position it *hard-codes per timeblock* because the
artists placed each one differently — its own comment calls that sloppy. The name is already
in `ESTRINGS.TXT` as `Day110a = Day 1, 10am - 12pm`, and setting it in the port's own face
costs nothing, is legible at any window size, and is what the corner of the screen already
says. It is drawn at a fifteenth of the screen height, which is two to four times the size
the sheets were cut at.

It stands for four seconds, or until a click, Enter or Escape. The original's has no timer
and waits for Continue; this ends by itself as well, because a card that must be dismissed is
a card that can be missed by somebody who has walked away. Per-frame input is forgotten on
the way in and on the way out, so the click that opened the door does not dismiss the card
and the click that dismisses it does not act on the next room.

An installation whose archives have no painting gets the words over black, which still says
what time it is.


### An idle dragged a character back between the clips of a scene — fixed 2026-08-24

**Reported** twice over: Gabriel resets to the coffee table partway through fetching coffee
from the kitchen in the dining room, and Lady Howard and Estelle reset after some of their
animations in the museum. One cause.

A scripted sequence is a run of clips one after another — the dining room's is
`GabDinStart2Kitch`, `GabDinCoffeeget`, `GabDinCoffeeget2` to `5`, `GabDinCfeRtrn` — and
between each pair there is a gap of a few frames while the script's wait comes back round.
The port **paused** a character's behaviour script for the length of a clip and gave it back
the moment the clip ended, so the idle fired into every one of those gaps. A breath is not a
move animation, so it gives back all the ground it covered: `GabBreath1` put Gabriel back at
(90, 279) by the table, having just been carried to (168, 425) in the kitchen. Traced frame
by frame, he ping-ponged between the two for the whole sequence.

**The reference does not pause it, it stops it.** `GKActor::StartAnimation` calls
`StopFidget` on the way in to any animation that did not come from the behaviour script
itself, and `OnVertexAnimationStop` does nothing to turn it back on. What turns it back on is
the script, by hand, once it has finished with the character — `PourCoffee$` ends with
`StartIdleFidget("Gabriel")`, and that line is there for exactly this reason.

The pause was right for its own case and was kept: a **walk** pauses an idle and gives it
back on arrival, which is what `Walker::OnWalkToFinished` does. A **story animation** is the
other rule. Props keep the pause too — nothing in the port would ever restart a prop's own
script, so a stop there would freeze every ceiling fan the story ever touched.

Verified both ways round in both rooms. In the dining room Gabriel now walks to the kitchen,
stays there through `Coffeeget2` to `5`, and returns under `GabDinCfeRtrn`. In the museum the
before-and-after is visible in one frame: without the fix Estelle is jammed against Lady
Howard by the display case, and with it she has turned and stepped to her own mark.


### A click went through the door with the player — fixed 2026-08-24

**Reported:** clicking the stairs down in the hallway arrived in the lobby and immediately
played the voice-over of Gabriel looking at himself. The click had been acted on twice —
once on the stairs, and once more in the next room, at the same screen position, which in
the lobby is where Gabriel is standing.

A click is gathered on `MouseUp` and lives in the window's per-frame state until
`EndFrame` throws it away. **A room is left in the middle of a frame**: the click sets a
new location, the room loop notices the story has moved and returns out of itself to let
the next room load — and that return is above the `EndFrame` at the bottom of the loop, so
the frame it belonged to never ended. Eight hundred milliseconds of scene loading later
the next room's first frame reads the same buttons, still pressed, and does whatever the
pointer is now over.

Which is not a leak with one exit: **a door, a load, the pause menu and the end of a film**
each leave the loop the same way. So the input is thrown away on the way *in* rather than
at each way out — a room begins with nothing having been clicked on in it. `IGameWindow`
gained `Forget` for it, which is the clearing `EndFrame` already did under a name that says
why rather than when, and `EndFrame` now calls it.

Worth noting what made this one nasty to see: the second click lands wherever the pointer
happens to be, so what it does depends entirely on which two rooms are involved and where
the player was aiming. Between the hallway and the lobby it is a voice-over. Somewhere
else it could be a door.


### The label knew everybody's name — fixed 2026-08-24

**Reported** as a question: is it right that Gabriel already knows the woman outside by the
bus is called Buthane, without having met her?

No. It is the same leak the second-floor doors had, one room earlier and about a person.
A scene names its people by their surnames — `BUTHANE`, `BUCHELLI`, `WILKES` — and the
hover label read them straight back, so pointing at anybody named them. Worth saying that
the original has nothing to be unfaithful to here: it draws no label at all, only a cursor.
The label is the port's, so the leak is the port's.

**What the label says instead is what can be seen**: "Woman" or "Man", taken from the
character's own `ShoeType` in `CHARACTERS.TXT` — `Female Leather`, `Male Boot` — which is
there to decide what a footstep sounds like and is also the only thing in the shipped data
that says which of the game's forty-five characters is which. No table of descriptions kept
by hand, for the same reason the hotel doors take their number out of the model name.

**When somebody has been introduced is the game's own question, and it already answers it.**
The action files ask it constantly, in `[LOGIC]` sections, under names like `MET_BUTHANE`
and `INTRODUCED_EMILIO`, and they do not agree on a mechanism: most are
`GetTopicCount(noun,"T_INTRODUCE")`, but Buthane has no `T_INTRODUCE` at all and introduces
herself while explaining the tour, Jean says his name when you walk up to the front desk,
Larry is met by turning up at his house, and Wilkes has a flag for the introduction he gets
in room 24 without a topic being raised. So the conditions are copied out verbatim into
`Assets/Story/Introductions.txt`, each with the file it came from beside it, and evaluated
exactly as an action's case is.

That bounds the list at the **twelve** people the data asks about. Anybody it never asks
about keeps their name — `MONTREAUX`, `MACDOUGALL`, `MALLORY`, `MESMI`, `SIMONE`,
`PRINCE_JAMES`, `MONSIEUR_BIGOUT`, listed in the file so the gap is visible — and so does
anybody the character file has no shoes for. Both failures are the same shape and it is the
safe one: a name shown early is a small spoiler, a stranger who stays a stranger after two
days of talking is a bug the player cannot work around. `GRACE` and `MOSELY` are absent for
a different reason: Gabriel arrives knowing them.

**The hotspot overlay was leaking too.** Holding Alt draws every noun in the room, and it
drew them raw — so the corridor the doors fix was written for still named all eight of them
when the key was held. It goes through the same naming now, which was the point of that fix
and half of it was missing.

Verified in the museum: the pair read "Woman" until the introduction, then "Lady Howard"
and "Estelle".


### The floor never noticed anybody standing on it — fixed 2026-08-24

**Reported** as a story blocker: Estelle and Lady Howard cannot be overheard in the museum,
because trying to listen to them from behind the panel walks Gabriel over to them instead.

The walkthrough's instruction for that moment is "go hide behind the panel behind them and
eavesdrop their conversation", and hiding is all it takes. `MS3110A.SIF` marks out a
rectangle of floor behind the display panels and names it:

    [TRIGGERS]
    noun=GET_CLOSE,rect={48.84, -400.57, 370.19, -598.15}

Standing in it does `GET_CLOSE, WALK`, which is the whole eavesdrop — the two women's
conversation about the sacred number of Ra, and the two points for hearing it. **Nothing read
the section.** `[TRIGGERS]` was not one of the parts of a scene file the reader knew about, so
the rectangle did not exist, and the room's only remaining way into the scene was
`LADY_H_ESTELLE, LISTEN` — whose script asks whether Gabriel is within a hundred units of one
of four `Behind_` spots and, finding he is not, walks him to `TryToListen` to say he cannot
hear from there. That walk is the reported symptom; the missing rectangle is the cause.

**Thirty-four rectangles across twenty-nine scene files**, and they carry most of the game's
"step closer and overhear them" beats: the front desk of the lobby, where Jean greets Gabriel
by name on the first morning; the window into Arnaud's office; Mosely's door; the two
lectures on the Blanchefort tour. All of them were silent.

They are read now, and `Scene::Update`'s rule is the one implemented: every frame, if the
player is inside a rectangle and nothing is playing, do that rectangle's noun with the verb
`WALK` — a verb no file writes beside the rectangle, because the original hard-codes it.
Not on the way in: the original tests every frame and relies on the action's own case to stop
it happening twice, which is what `GetNounVerbCount("GET_CLOSE","WALK")==0` is doing in the
museum's rules.

**Two details the rectangles need.**

- **The corners are written in whichever order the artist dragged them.** The museum's runs
  from z −400 to z −598, and a rectangle whose edges are the wrong way round contains
  nothing. They are sorted on the way in, as `Rect::Rect` does.
- **Two of them are mistyped**, both in `CSE212P`: one has a doubled comma and one writes a
  number as `11.03.58`. The original reads both, discarding empty elements and parsing with
  `stof`, which stops at the second point. A trigger dropped for a typo is a room where
  something quietly never happens, so the reader is as forgiving.

**A walk that would cross one now stops at its edge**, which is
`Walker::FindEarliestPathNodeInsideActiveTriggerRegion` — and its own comment names the case:
in the lobby on the first morning, the way to the front door goes through Jean's rectangle.
Without it the player walks over the trigger, the conversation starts behind them, and Jean
introduces himself to somebody already at the door. The stopping point is where the route
crosses the edge rather than the corner of the route that happens to be inside it.

**What stands for "an action is playing".** The original keeps a current action and asks it;
nothing here does, so the answer is assembled from four things that each cover part of one —
an action held back for its approach walk, the waits an action reported when it ran, a clip
the story is playing on the player, and any script still outstanding from the last action the
room started itself. That last one is what covers `wait CallSheep(…)`, whose length is
another script rather than a number of seconds, and it is measured against how many scripts
were waiting *before* the room acted: the dining room and the third-floor hall each keep two
parked for as long as they stand, so "any script is waiting" is not a usable answer.

Verified in the museum both ways round — walk behind the panels and the conversation plays;
stand there and use `LISTEN` and it plays — and in the lobby, where walking to the desk now
gets "Ah! You must be Monsieur Knight in room 25." `check-scenes` counts **112** rectangles
across the location and timeblock pairs, 8 of them with something to run at that point in the
story.


### Round things are rounded, and the village ground gained its relief — done 2026-08-23, rebuilt 2026-08-24

**The bell, the lamps, the vases.** A curated list of round objects — names containing bell,
lamp, lantern, candle, chandel, vase, urn — is rounded at scene load, silhouette and shading
both. The head's subdivision could not do it, for a structural reason worth writing down: it
pins boundary vertices, and a lathed object is strips and caps whose vertices are all on a
boundary — the rim between a bell's side and its top belongs to two surfaces, so refining
each surface alone holds the hexagon exactly where it was. `ObjectRounding` welds the whole
object by position first and carries texture coordinates per corner so seams stay seams.
Capped at five hundred authored triangles per object, so a "lamp" that is really a street of
lampposts stays as authored. The rounded triangles go to the ray tracer too, so the shadow
matches the silhouette. Adding an object is one name in `SceneGeometry.RoundNames`.

**How it rounds them was wrong for a day and is worth recording.** The first version was two
levels of Loop subdivision over the welded object, and it wrecked what it touched — the
lobby's lamp shade came out with its panels sagging inward between their ribs and its rim
spiked into sails, reported as "instead of round it's now oddly inward curved, definitely not
rounded". Loop is an *approximating* scheme: every original vertex moves toward the average
of its neighbours, which is invisible on a dense mesh and is the entire shape on a
twelve-sided shade. It was reverted to smoothed normals alone, which fixed the damage and
left the objects as polygonal as they were found. Rebuilt on 2026-08-24 as PN triangles —
interpolating, so no authored vertex moves at all — with crease-aware normals and a rim
curved along its own polyline. See `docs/rendering.md` (Round things); nine tests pin what it
may and may not do, starting with "a flat face comes out flat".

**The village ground.** Every RC1 walking texture was displacement-mapped except
`RC1MOTGRAS` — the mottled dirt most of the village stands on — because the derivation had
classified it as foliage. It is ground with grass in the picture, not blades standing up;
marked edited, displacement on at depth 2.5, and RC1 grew from 41,675 triangles to 65,765.

**Why outdoor relief still read as subtle, and what it actually was.** Two things were
blamed at the time and only one of them was true. The missing sun was real and was fixed
(issue 4). The other — "the cobbles are cut into the geometry at depth 4 and have been for
some time" — was wrong: they were being cut and then held flat. See the entry below.

### The village's floor was cut into a million triangles and did not move — fixed 2026-08-24

Reported as "the dirt and cobble tiles still have zero depth", twice, with screenshots at a
grazing angle where the ground's horizon was a perfectly straight line. Everything the loader
printed said it was working: 1,107,726 triangles, a sensible cell, height maps loaded and
read. What none of it said is how far anything had moved, which was **0.32 units typically**
against materials asking for 2.5 to 4.

Three causes, and each hid the next.

**The pin rule counted edges instead of looking.** An edge used by one triangle was treated
as the floor's boundary and held still. GK3's ground is laid as separate flat patches that
abut without being welded, and a stitch of stairs or a doorway leaves a long edge with a
vertex partway along it, so 2,201 of RC1's 2,674 once-used edges are not boundaries at all.
With the fade reaching a cell inward from every one of them, nine tenths of the village was
held down. Now an edge used once is tested by looking a little way past it for more floor of
the same texture.

**The fade from a held corner was a barycentric weight, not a distance.** That is only a
distance when the triangle is roughly equilateral, and a village's ground is mostly long thin
strips: 946 of RC1's floor triangles have a shortest side under ten units and they carry 81%
of the cut. Their whole length was damped by a corner seven units away.

**The tiling rate was a mean.** One number per texture decides where the lattice lines fall.
`rc1Coblston` is laid at a clean 120 units to the texture across the village square, and a
handful of triangles with all but collapsed coordinates carried the *mean* to 42,641 — so
every cobble asked for a lattice a thousand times too fine, was refused as impossible by the
per-triangle cap, and came out flat. It is the area-weighted median now.

With all three: RC1 moves **1.23 units typically, up to 3.67**, at 4.4 units a cell. The
budget went from 400,000 to a million at the same time, because its estimate had been out by
2.8× and could now be trusted. The loader reports how far the floor moved on the same line as
how many triangles it cost, which is the number that was missing.

### A relative clip played with its authored turn left in — fixed 2026-08-23

The cause of Estelle and Lady Howard standing back to back, found in the reference at last:
`GKActor::StartAnimation` and `SampleAnimation` both end in `SetModelRotationToActorRotation`,
which measures the posed model's facing and rotates it to the actor's heading. **A relative
clip plays facing whichever way the actor already faces, and whatever turn it was authored
with is cancelled.** This engine applied the clip's rotation raw on top of the placement, so
the museum pair — whose opening clip is authored with a turn in it — came out turned by it.

The correction now measures the clip's opening facing the way the reference measures it when
nothing animates the facing helper — the triangle of hip and shoe mesh origins, whose normal
is the facing outright, no dot product and no rare branch — and turns the clip back to the
model's built-forward before the placement turns both to the scene heading. Mosely, Jean and
Buthane, whose clips are authored in the canonical orientation, do not move by a pixel.

### The head turn overwrote the pose — fixed 2026-08-23

Emilio walked from the lobby door to the bench with his head turned half a circle from his
shoulders. `TurnMesh` rebuilt the head from the model's rest transform while every other part
of him followed the clip — and his walk is an absolute clip, whose correction carries the
authored heading, so the head was anchored to a placement the body was nowhere near. The turn
now composes on top of the pose the mesh is actually in. The glance's yaw is also measured
against the facing the clip has the body at, frame by frame, rather than against the
placement.

### The perception layer existed in the data and not in the engine — fixed 2026-08-23

`WHENNEAR Gabriel, 100, END` is a standing condition: it holds for the whole of a GAS script
and jumps to its label the frame it turns true, wherever execution is. It was parsed and
never run. Emilio could not notice Gabriel standing over his bench; the museum pair could not
notice him walking up, so their whispering played on however close he came — reported as the
sound that would not stop.

Implemented on the edge, as `GasPlayer::CheckDistanceConditions` has it: fire on false to
true, arm again on true to false. With the fourth argument the museum needed and the parser
dropped: `WHENNEAR Gabriel, 140, CHANGEIDLE, LADY_HOWARD` measures Gabriel against Lady
Howard, so both women notice him together. Verified: stand Gabriel beside them and both
switch from the whisper idles to the quiet ones.

### The fingerprint kit works — done 2026-08-23

`ShowFingerPrintInterface` now opens a card — not a page — with the ritual reduced to what
the story records: brush, see what shows, lift with tape. Lifting awards each print's score,
flag and inventory item from a table adapted from the reference's own screen, which is where
the original kept it: no script names these thirteen score events, and none was missing.

One departure, on purpose: the reference carries Buchelli's lobby-glass score commented out,
which makes the objective it belongs to impossible. The game's own score sheet lists it at
two points, so this engine awards it. **check-story now reports zero unreachable events** —
every objective in the journal can be completed.

### The title screen's Restore listed nothing — fixed 2026-08-23

The pause menu filled the slot list in and the title screen never did, so Restore from the
first menu showed empty slots while the store held three saves. Worse, choosing one fell
through "not Play" and closed the game. The title menu now lists the saves, shows their
pictures, and restoring from it starts in the room the save was written in.

### The 1999 game's own saves are imported — done 2026-08-23

A retail `.gk3` starts with a summary — name, location, timeblock, score, and a PNG of the
moment — before the part no reimplementation reads, where the original serialised its whole
class graph. The importer reads the summary and recovers what it implies: every score event
of every timeblock already behind the save is marked earned, the pockets get at least a new
game's starting items, and the original's own thumbnail becomes the slot picture. Imports are
idempotent, filed as `gk3-<filename>`, scanned from the install root and its Save Games
folder at startup.

Measured against three real saves, which corrected the reference twice: the magic is
`GK3!Save` where G-Engine's writer says `SAVE`, and the summary opens with the name outright,
no version number first. All three import: "Mosely Clothes" (RC1, day 1 2PM, 99 points),
"Train Station" (TR1, day 1 4PM, 140), and "On the Tour" (POU, day 2 7AM, 213) — the last
correctly resuming as Grace.

### An actor's position was a running total from wherever they started — fixed 2026-08-23

Reported as clicking Mosely in the dining room walking Gabriel into a corner of the room to
describe a man sitting behind him.

`DIN` names Mosely's spot `MOSTALK` and defines `TALK_MOSELY`. It is a typo in the shipped
data, it happens once in the game, and the loader already reports it — so Mosely's placement
is the origin and his idle draws him in a chair. Two things then went wrong from the same
cause.

**Aiming at him used the shape he was authored in.** A walk towards a model measured the
model's rest-pose vertices against its placement, which for him is a corner. It now measures
the pose a clip has actually put each group in, the same transforms the hit testing uses, so a
character animated into a chair is aimed at where the chair is.

**And his position never recovered.** The per-frame sync added up how far the clip had carried
him from where he began, and beginning at the origin keeps him there however convincingly he
is drawn. A character's position is now read from where the pose puts their feet, which is
what `GKActor::SyncActorToModelPositionAndRotation` does and what makes every `IsActorNear`
about him answer about the chair rather than the corner.

### A click on the floor walked out of a scene — fixed 2026-08-23

Reported from the dining room. A clip a script started is the story happening, and walking out
of the middle of it leaves it playing to an empty patch of floor. A floor click is refused
while one is running on the player.

Only a script's animation. A character's own idle is decoration and may be cut short at any
moment, which is the distinction the whole animation layer already draws.

### The close-up screen is gone, and an item's verbs are offered where the item is — done 2026-08-23

Asked for repeatedly, and the first two attempts did not go far enough.

`ScreenKind.SceneInspect` no longer exists. Looking closely at something in the room is a
camera and always was; the screen was a leftover that nothing opened any more but the painter
could still draw, which is the difference between unlikely and impossible.

Clicking a thing in the inventory used to open a page of its own to hold two words on. It now
offers that item's verbs beside the item, which is the shape a right click gives in the room —
and where an item has exactly one thing to do, that one thing is simply done. Going back from
it leaves the inventory rather than stepping through one entry per thing the player poked at.

The item close-up the scripts still open — 343 calls to `InventoryInspect` — is a card of
about a third of the window rather than a full page. A single object held up to the light
filling the screen reads as a modal error box.

### Escape opened the pause menu over the inventory — fixed 2026-08-23

Escape means "out of whatever is in front of me". Two handlers wanted it and the pause menu's
came first, so closing the inventory opened the menu on top of it instead. A screen on the
stack now takes it.

### Which way a model is built to face is now read from the game, not assumed — done 2026-08-23

`Actors.FacingArrow` loads a character's `DOR_` model — the invisible arrow the original uses
for exactly this — and derives the heading its three vertices point along, with the two
characters whose arrow is read from the other end (`MOS` and `DEM`) written down by name.
`PlacedModel.BuiltFacing` carries it, and placing, walking and reading a heading back all use
it in place of the half turn they assumed.

Nothing moved, which is the point: every arrow in the game reads a half turn, so the
assumption was right and is now checked rather than believed. It also settles that a reversed
character is reversed for some other reason.

### Emilio walked to the bench with his head turned 180 degrees — fixed 2026-08-23

A head's glance is worked out relative to which way the body faces, and `Turning` kept that
as a cached number. Only one thing ever updated it: the walking loop, calling `MovedTo` under
whichever name the walk had been asked for. A walk is asked for by noun as often as by model
name, so `MovedTo("EMILIO")` never matched a head filed under `eml` and the update was
silently dropped.

Emilio is the case that shows it, because `RC1` gives him **no position at all**. His facing
began as the heading of the identity transform — which is a half turn — and nothing ever
replaced it. Exactly 180 degrees, for as long as anything cached it.

The head now reads the model's own transform every frame. A character is moved by walking, by
an animation, by a script placing them and by an opening pose; a cache has to be updated from
all four, and the model's transform is already the answer to all four. Nothing left to keep in
step.

The two glance tests failed on the change and were right to: the fixture built its placement
by hand and never told the sink where the actor stood, so every unmoved actor read as facing a
half turn. The real geometry keeps a placement's transform from the moment the model is added.

### Back from the save slots went to the settings — fixed 2026-08-23

`Back()` read "anything that is not Options is a child of Options", which was true while the
only pages below the top were the three kinds of setting. Saving added two pages that hang off
the main menu, and Back from either landed on the settings screen. Each page now says where it
came from.

### Skipping the intro clicked a menu row — fixed 2026-08-23

The film is skipped by holding the mouse, and the release landed on the menu drawn underneath
the pointer that made it — starting the game, or quitting, depending on where the pointer
happened to be. The gesture is consumed before the menu is shown.

### Two spellings of Mosely — corrected 2026-08-23

The game's own data spells him **Mosely** 425 times and **Mosley** three, and our walkthrough
and quest table had picked up both. Corrected to Mosely throughout the prose.

**The score event names keep the data's spelling**, because they are identifiers rather than
words: `e_112p_r33_talk_mosley_case` is what the shipped scripts pass to `ChangeScore`, and
correcting it would make the objective impossible to complete. The first pass changed those
too and `JournalTests` caught it in the same run — which is exactly the case that test exists
for.

### Saving from the menu, in named slots — done 2026-08-23

F5 and F9 were already bound to quick save and quick load. What was missing was the menu: the
pause page now has Save and Restore, and both open a list of the twelve numbered slots plus
the two the game keeps for itself.

A slot says what it was called and when it was written. A free one says so rather than being
hidden, because a save menu that shows only what has already been saved gives a new player
nothing to aim at. The quick and automatic slots can be restored from and not written to by
hand: they belong to the game, and a player who overwrites their own autosave has been given
a way to lose something they did not know they had. A save with no name of its own is called
after where the player is — "Hotel Lobby, Day 1 10am" beats "Slot 3" and costs them nothing.

The front end owns no store. It turns rows into a choice and hands the host a slot and a
direction; reading and writing a game stays the one place that already does it.

**The pages did not fit.** The menu was built for five rows and a slot list has fifteen, so it
ran off the bottom of the window. The rows now close up until the page fits, down to a hair
over one line of text — the whole page staying visible is what a menu is for, and it beats a
fixed height with a scroll bar in it. The panel is as wide as its widest row, so a save's name
is trimmed at twenty-eight characters: a save is recognised by its first few words and by
when it was written.

**And a picture of the room in each slot.** The frame is captured at the moment of saving,
reduced to a quarter in each direction and written as a PNG *beside* the save rather than
inside it — a saved game is JSON a person can read, and base64 in the middle of it would make
the file unreadable to keep two things together that are happy apart. A save whose picture
could not be written is still a save, and a slot with no picture is a row of words, which is
what every save written before this is.

**The menu reopens on its own first page.** It remembers where it was so that going back from
Picture lands on Settings, which is right within one visit and wrong between two: pressing
escape and finding yourself deep in a slot list from ten minutes ago is nobody's idea of a
pause menu.

### A GAS script could not say a character had left — fixed 2026-08-23

Emilio's bench script outside the hotel ends with `LOCATION LBY`, a get-up animation, a walk
to the door and the door animation: when Gabriel comes within a hundred units, he goes
inside. That first line is the whole of what tells the rest of the game he has gone.

**We parsed it and dropped it.** It was filed under "only run this script at a named
location", which is what the name suggests; the reference's `LocationGasNode::Execute` calls
`SetActorLocation` outright, so it records where the character now is. Dropped, the lobby
goes on believing Emilio is outside on a bench, and every `IsActorAtLocation` about him
answers wrongly for the rest of the morning — `RC1110A` calls both that and `SetActorLocation`
by name.

### A prop left in the air when the idle holding it was cut short — fixed 2026-08-23

Reported for Emilio's newspaper, then again for Mosely's.

Emilio's case was a declared cleanup that was never played, and fixing that fixed him.
Mosely's is not the same bug: `mosPaperIdle.gas` **declares no cleanup at all**, so there is
nothing to play and no amount of looking for one will find it. The paper simply keeps the
last pose the idle gave it, in the air where his hands were.

A prop posed by an idle now goes back to where it lives when that idle is cut short. Only a
prop — a character keeps the pose they were interrupted in, which is right, because a person
stopped mid-gesture stands oddly rather than snapping to attention. And only an idle: an idle
is decoration and may be interrupted at any moment, where a script's animation is the story
and a door it left open is meant to stay open.

### A posed character's heading was the scene file's, not the pose's — fixed 2026-08-23

Reported as Lady Howard pointing at the wall in the museum.

`MS3` stands her and Estelle at 314 and 315 degrees, which is both of them turned towards
Gabriel at the *end* of their conversation; the scene file records where they finish. Where
they begin is stated as an animation, `initanim=lh2musestturn2gab`, and its opening frame
wants 90 and 282 — the two of them whispering to each other. `Open()` sampled that frame and
called `Follow`, which writes the position and nothing else.

Measured rather than guessed: the opening-pose log now prints both headings, and the museum
is the outlier. Everywhere else they already agree, which is why this had gone unnoticed —
the lobby reports Jean at 180 with the clip wanting 180, `RC1` reports Buthane at 112
wanting 112.

**What was actually wrong was the logic, not the picture.** Setting the placement heading to
the clip's changed the numbers and left the render identical, which establishes the thing the
fix had to know: a posed character is oriented by the clip's own mesh transforms, and the
model's placement rotation does not reach them. So before this, the game believed Lady Howard
faced 314 while she was drawn facing 90 — and every walk, glance and `IsActorNear` about her
worked from the wrong number. `AnimationStart.Facing` derives the heading from the clip's
opening frame, shoe triad and half-turn included, and that is now what the actor is placed at.

### Three ways to skip a walk, and a way to see the room — done 2026-08-23

Shift arrives at once. A click walks it, a double-click runs it, shift skips it — three ways
of saying how much of the walk you want to watch. Asked for on the ways out of a room, which
are the walks a player repeats most and learns least from, and it costs nothing to mean the
same thing everywhere. The route is still found, because where the walk would have *ended* is
where the player belongs: the boundary may stop it short, and arriving somewhere the floor
does not reach is worse than the walk it replaced. One walk, self-clearing, so a script's own
walks are untouched.

Holding **Alt** shows every hotspot in the room. A 1999 adventure game hides what can be
clicked and expects the player to sweep the pointer over the furniture until something lights
up. The labels are laid out rather than merely drawn — rooms put a dozen nouns within a few
degrees of each other, and a heap on one spot answers nothing — each pushed down until it
clears the ones already placed, nearest first so the thing at your elbow keeps its place and
the far side of the room gives way. One label per noun, since the church carves its four
angels as four models. A label with nowhere left to go is dropped: a wrong one is worse than a
missing one.

### A stopped animation went on making its noise — fixed 2026-08-23

Reported from the museum: Estelle and Lady Howard stop whispering the moment they notice
Gabriel, and Gabriel says so — but the whispering went on being audible underneath.

An animation's sound cues live in a list of their own, separate from the poses, so that a
clip which moves nothing can still make a noise. `StopAnimating` cleared the poses, the
holds, the visibility changes, the footsteps and the texture swaps, and never the cues. So
stopping an animation stopped everything about it except the part you could hear.

Cues are now dropped by either name — a script stops an animation by the animation's name and
an actor is stopped by their model's, and a cue can be reached from both.

### Inspecting something put a panel over the room — fixed 2026-08-23

Reported as the museum's H panel opening a full-screen dialog that looked like the inventory.

Looking closely at something is a camera: the view moves to a close-up and the room stays
where it is. A scene registers those calls over the base ones, because only a standing scene
has cameras to move to — but the base ones showed a modal screen, and a comment beside them
said that was harmless because nothing drew it. Something draws it now. They set the same
close-up state the scene's own versions do.

### The dining room lost its ambiance at Medium and High — fixed 2026-08-23

Reported after the baked lightmaps stopped lighting rooms at those tiers: the wall sconces
went dark, the tablecloths turned from cream to grey, and a room with **42 authored lights**
in it had almost nothing in it that read as a shadow.

Dropping the bake as *lighting* was right and is what P10 asks for. Dropping it as
*information* was not. It remains the best map anybody has of where the light in a room
goes — the artists decided in 1999 that the wall beside the sconce is warm and the corner
behind the screen is not — so it now shapes the ambient term rather than adding to it:
`0.30 + 3.0 x baked` where a lightmap exists. The term stays ambient, stays subject to
traced occlusion, and is still never subtracted against.

The flat part of the ambient floor came down with it, from 0.26/0.28/0.30 to
0.15/0.16/0.17. A large uniform wash is what drowned the rig's direct light and made the
shadows subtle; most of the ambient a lit surface gets should come from the shape.

Measured on `DIN`: contrast (standard deviation of frame luminance) 32.3 against the bake's
31.1, where before the hint it was flat. **Models are not reached** — a prop has no lightmap,
so the tablecloths are still greyer than the bake's cream. That wants light probes and is
noted in `docs/ray-tracing.md`.

### The church offered a verb from two days later — fixed 2026-08-23

Reported as the four angels offering "Trace" on the first morning.

**The shipped data really does allow it.** The case is `VALID_TO_TRACE`, which reads
`!GetFlag("LockedSquare") && GetNounVerbCount("Four_Angels","Trace") == 0`, and both halves
are true from the moment the game begins. The original offers it early too.

The rule says when it belongs, in its own script: those actions end in
`CallSheep("chu205p", "Done")` — they hand off to the compiled script of one point in the
story, which is loaded then and at no other time. An action calling into a script the game
has not got cannot finish. **107 distinct timeblock scripts are called this way across the
corpus**, so the resolver now withholds any rule that hands off to a timeblock other than the
current one. The corpus sweep went from 36,723 performable verbs to 36,651 — 72 of them were
premature.

### Labels gave away more than the player knew — fixed 2026-08-23

Three separate leaks, all in the hover label.

The second floor names its doors after the guests — `EMILIOS_DOOR`, `BUTHANES_DOOR`,
`WILKES_DOOR` — so the corridor introduced every suspect in the hotel the first time Gabriel
walked down it. They are called by their room number now, which is what is actually on the
door and what the scene's own `R27_PLATE` beside each one says.

The church carves its four angels as four objects, and pointing at one read "Four Angels4".
A trailing number is not always bookkeeping — `BUZZER_RM25` and `DUMB_WAITER_LOCK_R21` end in
room numbers that mean everything — so what tells them apart is whether the scene also
declares the name without it. The church declares `FOUR_ANGELS`; no room declares a
`BUZZER_RM`.

And a name the game's own string table wrote in lower case stayed that way, so an object read
"bed". Only the first letter is decided, because recasing "Rennes-le-Chateau: Outside Church"
puts a lower-case C in the middle of a place name.

### The player arrived at somebody else's spot — fixed 2026-08-23

Reported as Gabriel's position resetting on the way into the phone room and the kitchen: he
appeared somewhere wrong, filling the screen, and a moment later the room's own script moved
him to the door.

The loader placed the player at the first entry of the scene's `[POSITIONS]` whenever nothing
else said where. In the phone room that is `EMILIO_HERE_1` — a spot authored for a different
character, a metre in front of the arrival camera. **22 of the game's scene files reach that
fallback**, and exactly one of the 102 that place a player defines a `START` at all, so the
guess was doing nearly all of the work and doing it wrongly.

The artists' own convention answers it instead. A room names the spot you arrive at for each
door into it, after where you came from: `FR_LBY` is where you stand having come from the
lobby, and there are **308 of them across 80 scenes**. The room's enter script picks one by
hand a frame later; making the same choice at load is what stops the player ever seeing the
wrong one. Failing that, nothing — an unplaced player stands at the origin until a script
moves them, which is what the reference does and is better than standing where somebody else
was meant to.

### Every line of dialogue talked over itself — fixed 2026-08-23

Reported as voices being cut off, and worse the longer the recording.

The four dialogue calls are marked waitable, and `SecondsFor` had no case for any of them, so
they fell through to nought. A waited block containing one finished in the frame it began:
the script ran straight on to the next statement, and starting a line abandons whatever is
being said. Longer recordings lost more, which is exactly how it was reported.

`StartDialogue` and `StartDialogueNoFidgets` now go through the same reckoning
`StartVoiceOver` does — they take the same licence plate and line count. A continuation names
no plate at all, only how many more lines, so it asks whatever is speaking: that is the one
duration the script host cannot work out for itself.

### Inspect won every click — fixed 2026-08-23

The close-up is offered for nearly every noun in the game, and it sorted ahead of everything
else, so it was what a left click did — a click meant to cross the room leaned in at a
doorframe instead. It is on the **middle button** now. The left button takes the first verb
that actually does something, and where a thing answers to nothing else it means what a click
on the floor means and the player walks over.

### The inventory strip is gone — done 2026-08-23

It listed the same items the right-click menu already offers, and it lay across the foot of
the screen — exactly where the floor at the player's feet is drawn, so every click on the
ground in front of you was tested against it first and a good many were swallowed. The
pockets are a key away and a screen of their own, which is where a list of twelve things
belongs.

The layout is kept rather than deleted, in case a strip is ever wanted as something the
player can turn on.

### An inventory item offered to be picked up again — fixed 2026-08-23

An item and the object it was picked up from are the same noun, so the close-up of the marker
in Gabriel's pocket resolved the same rules as the marker on the desk and offered `PICKUP`.
The action files cannot tell the difference and are not wrong to — the rule exists for the
desk. The verbs that only mean something for a thing still in the room are filtered out of an
item's own menu.

### The player arrived at somebody else's spot — fixed 2026-08-23

Reported as Gabriel's position resetting on the way into the phone room and the kitchen: he
appeared somewhere wrong, filling the screen, and a moment later the room's own script moved
him to the door.

The loader placed the player at the first entry of the scene's `[POSITIONS]` whenever nothing
else said where. In the phone room that is `EMILIO_HERE_1` — a spot authored for a different
character, a metre in front of the arrival camera. **22 of the game's scene files reach that
fallback**, and exactly one of the 102 that place a player defines a `START` at all, so the
guess was doing nearly all of the work and doing it wrongly.

The artists' own convention answers it instead. A room names the spot you arrive at for each
door into it, after where you came from: `FR_LBY` is where you stand having come from the
lobby, and there are **308 of them across 80 scenes**. The room's enter script picks one by
hand a frame later; making the same choice at load is what stops the player ever seeing the
wrong one. Failing that, nothing — an unplaced player stands at the origin until a script
moves them, which is what the reference does and is better than standing where somebody else
was meant to.

### Every line of dialogue talked over itself — fixed 2026-08-23

Reported as voices being cut off, and worse the longer the recording.

The four dialogue calls are marked waitable, and `SecondsFor` had no case for any of them, so
they fell through to nought. A waited block containing one finished in the frame it began:
the script ran straight on to the next statement, and starting a line abandons whatever is
being said. Longer recordings lost more, which is exactly how it was reported.

`StartDialogue` and `StartDialogueNoFidgets` now go through the same reckoning
`StartVoiceOver` does — they take the same licence plate and line count. A continuation names
no plate at all, only how many more lines, so it asks whatever is speaking: that is the one
duration the script host cannot work out for itself.

### Inspect won every click — fixed 2026-08-23

The close-up is offered for nearly every noun in the game, and it sorted ahead of everything
else, so it was what a left click did — a click meant to cross the room leaned in at a
doorframe instead. It is on the **middle button** now. The left button takes the first verb
that actually does something, and where a thing answers to nothing else it means what a click
on the floor means and the player walks over.

### The inventory strip is gone — done 2026-08-23

It listed the same items the right-click menu already offers, and it lay across the foot of
the screen — exactly where the floor at the player's feet is drawn, so every click on the
ground in front of you was tested against it first and a good many were swallowed. The
pockets are a key away and a screen of their own, which is where a list of twelve things
belongs.

The layout is kept rather than deleted, in case a strip is ever wanted as something the
player can turn on.

### An inventory item offered to be picked up again — fixed 2026-08-23

An item and the object it was picked up from are the same noun, so the close-up of the marker
in Gabriel's pocket resolved the same rules as the marker on the desk and offered `PICKUP`.
The action files cannot tell the difference and are not wrong to — the rule exists for the
desk. The verbs that only mean something for a thing still in the room are filtered out of an
item's own menu.

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
`TIMEBLOCKBIBLE.TXT`, one "Completion Rules" list per timeblock. The engine carries them as
`Game/Story/TimeblockRules.cs`, adapted from G-Engine's Sheep script under GPL-3; see NOTICE.

They are **code rather than a carried script**, and the reason is the extension. A `.shp` is
compiled Sheep, every compiled Sheep script in existence is original game data, and this
repository refuses that extension in `.gitignore` and again in the CI check — so a file
called `Timeblocks.shp` could not be committed, and a checkout without it failed the Linux
and macOS builds on an `EmbeddedResource` that was not there. As code the rules are type
checked, need no compiler at startup, and cannot go missing. **Every condition still reads
the state the Sheep function of the same name reads** — `GetNounVerbCount` is
`GameState.GetNounVerbCount`, `GetFlag` is `GameState.GetFlag` — so they stay checkable
against the corpus rather than being a private language. Deciding and acting are separate:
`TimeblockRules.Check` returns where the story goes next and `Application` applies it through
the same `ChangeTimeblock` that `SetTime` uses.

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
has nothing to do but take the time. Three of the 23 have gone since that count was taken:
`AddModel`, `SetScene` and `SetSceneNoPreloadTextures`.

**Still genuinely missing**, in order of how often they are called: model shadows
(`EnableModelShadow`/`DisableModelShadow`, 54) and `SetModelLighting` (23), both of which
want renderer work; `AddActor` and `AddPosition`, the half of construction mode that builds
a *character* into a room rather than a prop, which is RC3's parade of farm animals and
nothing else; and the two end-of-game screens, `ShowDeathLayer` and `FinishedScreen` (6).
`AddModel`, `SetScene` and `SetSceneNoPreloadTextures` are done — see the disco ball
entry under Closed.

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


### Scene music cut between rooms rather than crossfading — done 2026-08-22, reverted 2026-08-27

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

**Reverted on 2026-08-27**: three seconds of two beds on one bus is audibly two rooms. See
the entry at the top of this section for what replaced it and what that gave back up.


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

# Known issues

Open defects and requested work, newest first. Each records how to reproduce it
and whatever was already established about the cause, so picking one up does not
start with rediscovery. Items marked **feature** are requests rather than bugs.

## 1. NPCs offer Talk as well as Chat and Ask about

**Reported:** 2026-08-22. Right-clicking a character lists `Chat`, `Talk` and
`Ask about...`. Talk looks like the heading the other two sit under rather than a verb of
its own.

Needs checking against the game's own action files before anything is hidden: if the
originals give a character a TALK rule as well as CHAT and TOPIC ones, the verb is real and
the question is what it does.

## 2. Inspecting the register does nothing

**Reported:** 2026-08-22. The verb is offered and produces no result. Probably an action
whose script is not performed rather than one that is missing; needs `render-scene --do`
against that noun to say which.

## 3. Scene music cuts rather than crossfading (feature)

**Reported:** 2026-08-22. Moving between rooms should fade one room's music into the next
rather than stopping one and starting the other.

## 4. The Eglise/Church sign reads wrong on RC1's signpost

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

## 5. HDR output (feature)

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

## Closed

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

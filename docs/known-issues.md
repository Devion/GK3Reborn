# Known issues

Open defects and requested work, newest first. Each records how to reproduce it
and whatever was already established about the cause, so picking one up does not
start with rediscovery. Items marked **feature** are requests rather than bugs.

## 1. HDR output (feature)

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

### The sky is wrong outdoors, and turning the camera makes it look like it spins — open, 2026-08-20

Reported as "the skybox rotates around its axis in a 90 degree turn of the camera".
Reproduces in any outdoor room; `VGR` at `110A` is the clearest, with all four of its
room cameras at zero pitch so the horizon should be dead level in every one.

**Two faults, one found and one not.** Neither is fixed — the first fix alone makes the
sky look *worse*, because the clipping was hiding the second.

**1. The cube is clipped by the near plane.** `SkyboxPipeline` draws a cube of side two
centred on the camera, so its nearest face is one unit away, and `SceneLoader` gives every
room a near plane of exactly `1f`. The sky is therefore clipped almost everywhere, leaving
wedges of the clear colour whose edges swing about as the view turns — which is what reads
as spinning. Giving the sky its own projection (`0.01f` to `10f`, the room's field of view
and aspect) fills the screen; the depth is forced to the far plane by `clip.xyww` anyway,
so nothing else about those planes matters.

**2. The fragment shader gets a constant direction.** With the coverage fixed, the whole
sky draws in one flat colour. Proved by replacing the texture lookup with
`vec4(normalize(fragDirection) * 0.5 + 0.5, 1.0)`: the sky comes out uniformly yellow,
which is the first cube corner `(1, 1, -1)`. Rasterisation is fine — colouring by
`gl_FragCoord` gives a clean screen gradient — so many fragments run and every one of them
gets the same varying.

What has been ruled out:

- **Not the vertex buffer.** Generating the 36 corners in the shader from `gl_VertexIndex`
  changes nothing.
- **Not a location clash** between the vertex input at location 0 and the varying at
  location 0. Moving the varying to 1 in both stages changes nothing.
- **Not the SPIR-V cache.** Its key hashes the source, and every *fragment* shader edit
  took effect immediately.
- **Not the cube map.** Six faces, `MipLevels = 1`, `ImageViewType.TypeCube`,
  `CreateCubeCompatibleBit`, all six textures 512×512 and correctly oriented on disk.
- **Not the face assignment.** A probe skybox of six flat colours renders one clean face
  per view with no seams, which is right for a 78-degree horizontal field of view.

The striking thing is that **no edit to the vertex shader has ever changed the output**
while every edit to the fragment shader has. That is the thread to pull.

**The tool this needs is the one that is missing**: the startup line says *validation
layers not installed*. Installing the Vulkan SDK's layers would almost certainly name this
in one run, and is worth doing before spending more time reasoning about it.

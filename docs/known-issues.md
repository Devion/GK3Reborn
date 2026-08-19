# Known issues

Open defects and requested work, newest first. Each records how to reproduce it
and whatever was already established about the cause, so picking one up does not
start with rediscovery. Items marked **feature** are requests rather than bugs.

## 1. Ray-traced lighting is under-exposed and noisy above Low

**Reported:** 2026-08-19, as a consequence of fixing the shadow budget below.

Once the ray budget started going to the lights that actually light a pixel, the
lights that were previously contributing unshadowed fill began to be occluded —
correctly, and the room lost most of its light with them. `--rt low` looks right;
`--rt medium` and `--rt high` are markedly darker than `--rt none` and carry heavy
grain, which matters because the host defaults to `--rt high`.

**Reproduce:** `GK3Reborn.exe --scene R25 --timeblock N`, then compare against
`--rt none` and `--rt low`.

**Leads, in order of suspicion:**

1. **The occlusion radius.** `RayTracingSettings.For` uses 90 units at Medium and
   140 at High. R25 is about 300 units across, so a 140-unit hemisphere reaches a
   wall from nearly every point in the room and occlusion sits low everywhere
   rather than gathering in corners. It multiplies the whole indirect term, so
   this is the largest single contributor. Raising `LightmapIndirect` was tried
   and barely moved the image, which points here rather than at the weights.
2. **The grain is undersampling.** Eight occlusion rays and two shadow samples per
   light per pixel, with no accumulation across frames and no filter. It was
   invisible while the direct term was several times over white and swamped it.
   A temporal accumulator, or a spatial filter on the occlusion term, is the real
   answer; more rays only moves the threshold.
3. **The exposure constants were tuned against the broken state.** The lightmap
   multiplier and the `LightmapIndirect` weights were chosen when every light past
   the ray budget lit the scene unshadowed. They are worth revisiting once 1 and 2
   are settled — and again when there is a tone mapper, per the HDR item below.

## 2. HDR output (feature)

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
Deciding it properly needs the Sheep virtual machine.

### A and D strafe the wrong way — fixed 2026-08-19

`FreeCamera.Update` built the strafe axis as `cross(up, forward)`.
`Matrix4x4.CreateLookAt` is right-handed, so the basis vector that maps to screen
right is `cross(forward, up)` — the negative of what was there. Tests now derive
which way is right from the view matrix rather than asserting a sign, so they hold
whichever handedness the camera ends up using.

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

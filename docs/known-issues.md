# Known issues

Open defects and requested work, newest first. Each records how to reproduce it
and whatever was already established about the cause, so picking one up does not
start with rediscovery. Items marked **feature** are requests rather than bugs.

## 1. A and D strafe the wrong way

**Reported:** 2026-08-19, from the windowed scene viewer.

Pressing `A` moves the camera right and `D` moves it left. Forward and back are
correct, so the yaw and the forward vector are fine and only the strafe axis is
wrong.

**Reproduce:** `GK3Reborn.exe --scene R25 --rt high`, then press `A`.

**Lead.** `FreeCamera.Update` builds the strafe axis as
`Vector3.Cross(Vector3.UnitY, forward)` (`src/GK3Reborn.Engine/Rendering/FreeCamera.cs:83`).
In a right-handed system with `+Y` up, the viewer's right is `cross(forward, up)`,
not `cross(up, forward)` — the two differ by sign, which is exactly the symptom.
Swapping the operands is the likely one-line fix.

Worth a test alongside it: with the camera at the origin looking down `+Z` and up
`+Y`, holding *right* must increase `Position.X`. Nothing currently asserts the
handedness of the camera basis, which is why this got through.

## 2. A door renders as its knob only

**Reported:** 2026-08-19.

In a scene with a visible doorway, the door panel is missing and only its knob
draws — so the two are separate objects and only one of them is being drawn.

**Reproduce:** believed to be `R25`; the hall door is the likely subject. Confirm
which scene and camera before digging.

**Leads, in order of suspicion:**

1. **Over-eager hiding.** `SceneLoader.HiddenObjects` hides any BSP-baked model the
   initialisation file marks `hidden`, and `SceneInitFile.Models` collapses
   repeated conditional blocks by taking the *last* occurrence of a name. If a
   door is hidden in a late conditional block and visible in the one that actually
   applies, it will be wrongly hidden — and a knob declared under a different name
   would survive. This is the most likely cause and the newest code in the area.
2. **A missing texture that decodes to fully keyed alpha**, in which case every
   texel is discarded and the panel vanishes while the knob, with its own texture,
   remains. `--verbose` lists textures that failed to load.
3. **The panel is a `prop` whose `.MOD` is missing**, which `--verbose` also
   reports as `SCENE006`.

Running with `--verbose` first will separate 2 and 3 from 1 immediately.

## 3. Z-fighting on the lamp beside the bed

**Reported:** 2026-08-19, in R25.

**Reproduce:** `GK3Reborn.exe --scene R25 --timeblock N`, look at the standing lamp
next to the bed.

**Lead.** Two coincident surfaces, which in this codebase most often means the same
geometry drawn twice. The scene asset lists objects baked into the BSP and the
initialisation file lists models to load; `SceneLoader.PlaceModels` is supposed to
load a `.MOD` only for `prop` and `gasprop` and leave everything else to the BSP.
A lamp declared with a type that does not match its actual storage — or declared
twice under two names — would be drawn from both sources at once.

Check first whether the lamp appears both in the scene asset's `[Models]` list and
as a placed prop. If it does not, the second possibility is genuinely coincident
authored geometry (a lamp shade modelled twice), which needs a depth bias or a
material tweak rather than a loading fix.

## 4. HDR output (feature)

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

# Known issues

Open defects, newest first. Each records how to reproduce it and whatever was
already established about the cause, so picking one up does not start with
rediscovery.

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

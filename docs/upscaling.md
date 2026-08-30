# Upscaling, the display, and high dynamic range

What the end of a frame does now, and why it is one chain rather than two.

## The frame, from the first triangle to the screen

The renderer used to have two orders. A ray-traced frame composited into a target and
copied it out; a plain frame drew straight onto the swapchain. Every feature since has had
to be written twice or has silently only worked on one of them.

There is one order now:

| Stage | Size | Format |
|---|---|---|
| The room, and its G-buffer | render | `R16G16B16A16_SFLOAT` and friends |
| Traced occlusion, reflections, compositing | render | as above |
| The sky or the reconstructed horizon | render | `R16G16B16A16_SFLOAT` |
| **The upscale** | render → display | `R16G16B16A16_SFLOAT` |
| **The encode**: tone curve, sharpen, transfer function | display | the swapchain's |
| The movie, the interface, the fade | display | the swapchain's |

Two extents, with separate names, in `VulkanRenderer`: `_renderExtent` and `_extent`. The
interface is drawn at `_extent` and always was — an interface drawn at render resolution and
stretched with the room is the most visible way to get an upscaler wrong.

The room's colour target is floating point whether or not anything is traced. That is the
change everything else rests on: a ray-traced highlight, a lamp on an HDR display and a
temporal upscaler's history all need values above one to survive to the end of the frame,
and an 8-bit target clips every one of them at white.

## The four upscalers

`Rendering/Upscaling/UpscalerKind.cs`. All four are switchable while the game is running:
the plan is a value, and handing the renderer a different one marks the frame's targets for
rebuilding at the top of the next frame, exactly as a resize does.

**Off.** The room is drawn at the size of the window.

**Built in** (`SpatialUpscaler`). One frame in, edge-directed. For each output pixel it
weights the sixteen source pixels around it with a windowed sinc squeezed across the local
luminance gradient, so a taut diagonal is resampled along its own direction rather than
across it, and clamps the result to the four nearest source pixels so the negative lobes
cannot ring along a hard edge. It recovers no detail that was never drawn — there is only
one frame to look at — and in exchange has no history to be wrong about. Needs nothing
installed and is what the other two fall back to.

**FSR** (`FsrUpscaler`, through `amd_fidelityfx_vk.dll`). AMD's, and it runs on any Vulkan
device — an NVIDIA player without NVIDIA's runtime installed still has a good temporal
upscaler. Driven through the FidelityFX API, which exists so that an application need not
be rebuilt for a new upscaler; nothing here names a version.

**DLSS** (`DlssUpscaler`, through Streamline). NVIDIA's, on NVIDIA's cards, and the best of
the four where it runs at all.

### What a temporal upscaler is given

Three things, and each of them was somewhere the renderer had to be corrected.

**A jittered camera.** `JitterSequence` — Halton, bases 2 and 3, with the sequence length
growing as the square of the ratio, which is FSR's own formula and what DLSS asks for. The
offset goes into the projection's z-to-x and z-to-y terms rather than its translation row,
because it has to be proportional to *w*: a constant there moves a wall by a pixel and a
distant hillside by a hundred.

**Motion vectors without the jitter in them.** The previous frame's view-projection is kept
*unjittered*, and the fragment shader adds this frame's offset back:

```glsl
outMotion = ((there * 0.5 + 0.5) * frame.tuning.zw) - gl_FragCoord.xy + frame.exposure.xy;
```

Left in, every upscaler sees the whole screen shaking by half a pixel whether or not
anything moved, and spends its disocclusion budget on it.

**A reset.** `VulkanRenderer.ResetHistory`, and automatically on a new scene and on any
swapchain rebuild. A temporal upscaler that is not told smears the last frame of the hotel
lobby across the first frame of the street outside.

### Where the runtimes come from, and what happens without them

`UpscalerRuntimes` looks in `libs/`, then `libs/streamline/` — which is the shape NVIDIA's
own download unpacks to — then beside the executable, then anywhere `--libs-dir` names. It
reads each file's version resource and reports the lot on one startup line whether or not
anything was found, because a player who copied the files into the wrong directory has no
other way to discover it.

Nothing is linked. Every vendor entry point is resolved by name from a file that may not
exist, which is what makes "the DLL is not there" an ordinary answer rather than a process
that will not start. A backend that will not build, or that declines a frame, is logged once
and the built-in upscaler takes over for the rest of the session.

**DLSS is not offered on a card that is not NVIDIA's.** `VulkanRenderer.OfferedUpscalers`,
from the PCI vendor identifier rather than from the device's name. A permanently unavailable
row reads as something the game has failed to do rather than as something the hardware
cannot, and a settings file carried over from an NVIDIA machine is answered at startup
rather than by a menu row that can never be made to work.

### Streamline has to start before the device exists

This is the one piece of ordering that is not obvious and is not recoverable. Streamline's
features ask for Vulkan **device extensions** and for **queues of their own**, and both have
to be in the `vkCreateDevice` call. So `Streamline.TryStart` runs before
`VulkanRenderer.Create`, the renderer folds what it asks for into the instance and device it
was going to make anyway, and `slSetVulkanInfo` follows immediately afterwards.

Get it wrong and there is no error: DLSS simply reports that the device does not support it.

Manual hooking, not the interposer. The engine creates its own instance, device and
swapchain and tells Streamline about them afterwards. Replacing the Vulkan loader would mean
the game's own calls went through a third-party DLL whether or not the player ever turned
DLSS on.

`slInit` also needs an engine version. The header calls it optional; it is not. With
`applicationId` at zero and no `engineVersion`, NGX declines to start and every feature comes
back as unsupported on hardware that plainly supports it — which cost an afternoon and is
the reason `Streamline.Start` sets it from the assembly's own version.

## High dynamic range

`Rendering/OutputPlan.cs`, and the encode in `Rendering/Vulkan/OutputPipeline.cs`.

`VK_EXT_swapchain_colorspace` is enabled whenever the loader has it, whether or not HDR is
switched on — without it at *instance* creation the surface never reports an HDR format at
all, so a player turning HDR on from the pause menu would be told their monitor cannot do
it. The swapchain then asks for `HDR10_ST2084` in `A2B10G10R10`, or `EXTENDED_SRGB_LINEAR`
in `R16G16B16A16_SFLOAT`, and takes an ordinary sRGB surface when the display offers
neither. What it got is reported through `HighDynamicRangeActive`, so the settings page can
say "asked for, and this display did not offer it" rather than leaving somebody to wonder.

### Everything that writes the swapchain encodes

On an sRGB surface the hardware encodes on write and there is nothing to think about. On an
HDR surface there is no hardware encode, so a pass that writes linear light onto it produces
a number the display reads through the wrong curve. The room, the interface, a movie and the
fade therefore share one encode — `DisplayEncoding.Glsl`, spliced into all four fragment
shaders — rather than four copies of ST.2084 to be wrong differently.

They blend in encoded space. The alternative is to draw the interface into a target of its
own and composite it, which is more correct and changes how every existing standard-range
frame blends; between a theoretical improvement to HDR blending and leaving the SDR picture
alone, this project's regression images decide it.

> A vector in a push constant block is aligned to sixteen bytes whatever precedes it. The
> interface's `int picture; vec3 display;` therefore puts the vector at offset **16**, not
> 4, and a sixteen-byte push leaves the shader reading past the end of the range. It came
> out almost black. The offsets are stated explicitly now.

### The nits, and which of them is the interesting one

| Setting | Default | What it is |
|---|---|---|
| Paper white | 200 | Where a white wall and the interface sit. Matches what Windows gives the desktop. |
| Peak | 1000 | The brightest the display goes. Above what the panel can do only wastes the top of the range. |
| Sunlight | 800 | Where a sunlit surface may reach. |
| Lamps and windows | 1000 | The emitters themselves, not what they light. |

None of these can be discovered: a monitor's EDID routinely claims a peak luminance it
cannot hold, which is why Windows has an HDR calibration tool at all.

**The last two are the ones that make it look like HDR rather than like a brighter SDR.**
They are applied at the two places the game already knows which pixels they are, and nowhere
else:

- *The sun* through the rig. `GpuLight.From` already answers "which light is the sun" —
  `IsDistantKey`, the switched-off distant key the artists left in every exterior — so its
  intensity is multiplied on the way to the shader. Brightening every lamp in a hotel lobby
  by four is not high dynamic range, it is an exposure error.
- *The lamps* in the mesh shader. GK3 marks self-lit surfaces in its own data — the original
  binds a white lightmap and a multiplier of one to them, which is its way of saying "this
  is its own light source" — so the bulbs and the lit windows are exactly the pixels with
  somewhere to go.

Both gains are exactly one in standard range, so nothing about an SDR frame changes.

Changing paper white or either luminance takes effect on the next frame; the rig is
re-uploaded by `FrameUniformSet.Relight` when the sun's gain actually differs, which is a
couple of hundred kilobytes and a light-grid rebuild rather than a scene reload.

### Screenshots

A screenshot is an 8-bit sRGB file and there is no other kind. `VulkanRenderer.Capture`
decodes the ten-bit PQ or half-float scRGB frame, undoes the transfer, puts it back into a
scale where paper white is one, and encodes for sRGB. What that loses is exactly what an HDR
display was showing that an ordinary one cannot: a screenshot taken in HDR is the nearest
ordinary picture to what was on the screen, not a photograph of it.

## The settings pages

Picture, Display and Upscaling, under Settings. `UI/FrontEnd.cs` builds the rows and
`UI/Menu.cs` draws them, and neither knows about a window or a device — which is why every
row on both new pages is tested without drawing anything.

Rows appear only when they mean something: the four luminances are absent until HDR is on,
the quality ladder is absent until something is upscaling, and the DLSS model and ray
reconstruction rows are absent unless DLSS is chosen. Four dead rows on a page is how a
settings screen teaches somebody that rows can be dead.

### Making the pages fit

Three things changed in `MenuPage`, because a picture page carries a dozen rows. The
explanations that used to sit under half of them are gone — see **No row explains itself**
in `front-end.md` — but a page is still long, and a row that survives still wraps:

1. **The menu's font is capped.** A twenty-sixth of a 4K screen is 83 pixels, which is
   nobody's idea of a settings page. A window is made bigger to see more of the game, not to
   have the menu grow with it.
2. **Explanations wrap.** They are excluded from the width the panel is sized to — a page
   whose width is set by its longest sentence is a page-wide slab — and broken on spaces to
   whatever width the rows settled on. A wrapped row is as tall as the lines it took, which
   is why the hit test walks the rows instead of dividing by one row's height.
3. **A page too tall to fit scrolls.** It used to tighten the spacing until the rows touched
   and then run off the bottom anyway. The window of rows grows outwards from the chosen
   row, downwards first, so stepping down a list reveals what is coming; a bar down the
   right edge says how much more there is.

`tests/GK3Reborn.Tests/UI/MenuBoundsTests.cs` measures the quads the page actually emitted,
at six window sizes, with the background wash suppressed so that "what was drawn" means the
panel rather than the screen.

## What is not done

**Frame generation.** The setting exists, is stored, and is gated on a runtime that can
actually do it **on Vulkan**; on Direct3D it does. Both vendors' implementations replace the
swapchain — FSR through `FFX_API_CREATE_CONTEXT_DESC_TYPE_FGSWAPCHAIN_VK` and its
replacement `vkAcquireNextImageKHR` and `vkQueuePresentKHR`, DLSS-G through Streamline's
present hooks — so it is a change to how the renderer presents rather than another pass at
the end of a frame.

On Direct3D that change is small and is made: `slUpgradeInterface` on the DXGI factory before
the swapchain is created, so the chain that comes back is Streamline's own. On Vulkan it is
the loader change `Streamline`'s class remarks describe and has not been made — the
interposer has to stand in for `vulkan-1.dll`, the surface has to be created through it, and
`slSetVulkanInfo` must then not be called at all, and all three have to land together.

What Direct3D does now, and what a reader coming to the Vulkan side will need:

- **Two, three and four times.** The runtime is told a count of *generated* frames, not a
  multiple: three generated is four times shown. `DLSSGState.numFramesToGenerateMax` is what
  the card will take, and the settings row is trimmed to it — asking for more is not clamped,
  it declines the whole call.
- **Reflex is not optional.** A generated frame is placed in time using the measurements
  Reflex makes, so it is turned on whatever the player set whenever frames are being
  generated. The markers and the sleep hang off one frame token per frame; two tokens in a
  frame is two frames as far as either feature is concerned.
- **The picture without the interface has to be handed over.** `kBufferTypeHUDLessColor` is a
  required tag, and a frame that arrives without it is presented once and nothing is said —
  no error, no warning, no state a caller can read. It is a copy of the back buffer taken
  after the film and before the interface, and it is why the runtime can lay the interface
  over a generated frame rather than interpolating through it.
- **PQ is not ideal for it, and is used anyway.** Interpolating two ST.2084 frames averages
  a quantity that is not linear in light, so scRGB would suit generation better. Automatic
  does not switch to it: the interface blends in whatever space the swapchain carries, and
  changing that under a player wrecks every glyph in the game while leaving the room looking
  right. scRGB is available to anyone who chooses it.

**DLSS ray reconstruction.** Two things are missing and only one of them is ours. It needs
guide buffers this renderer does not produce — an albedo and a specular albedo target, and
the roughness packed into the normal target's spare channel — which is a G-buffer change.
And the plugin shipped with Streamline 2.13 is `sl.dlss_nr.dll`, which declares itself as
feature **1004** with an entry point called `slDLSSNRSetOptions`; no published Streamline
header describes either. The documented `sl.dlss_d.dll` at 1001 is what this build can
drive. Both cases are detected and reported on the settings page rather than guessed at: an
options structure whose layout was inferred rather than read is a pointer handed to somebody
else's signed binary with the fields in the wrong places.

**FSR has not been run, on either backend.** The code is written against the FidelityFX API
headers at `v1.1.4` and the structure layouts are laid out byte for byte against them, but
neither `amd_fidelityfx_vk.dll` nor `amd_fidelityfx_dx12.dll` was available on the machine
this was written on, so the path has never executed. DLSS has, and the equivalent Streamline
structures read back correct driver versions and extension counts, which is the same class of
evidence — but it is not the same as having run it.

There are two libraries because there are two backends: the FidelityFX API is one C interface
with one backend built into each. The parts that say nothing about which graphics API is
underneath live in `Rendering/Upscaling`; what is beside each backend is its own backend
description, what a resource handle points at, and how a format is numbered.

**Exclusive fullscreen is a window state, not a mode set.** It asks the windowing backend
for fullscreen at the current size; it does not change the display's video mode.

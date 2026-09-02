# Direct3D 12 — what is left

Where the second backend stands as of 2026-08-30, and what to pick up next.

## Working

The game runs on Direct3D 12: `--backend d3d12`. Room, sky, **the reconstructed terrain
horizon**, interface, film, fade, ray tracing, DLSS super resolution, **Reflex**, **DLSS
frame generation at two, three and four times**, **FSR**, HDR10 and **scRGB**.

Reference renders: `render-scene --backend d3d12 [--rt high] [--dlss quality]`. Note that
`render-scene` cannot show the horizon on *either* backend — the Vulkan headless
`SceneRenderer` draws the room and nothing behind it — so a backdrop is compared through the
window instead: `GK3Reborn.Host --scene CSD --timeblock 202P --frames 8 --screenshot out.png
--backend d3d12` against the same on Vulkan.

`IRenderer` is the seam and both renderers implement it. `D3D12FramePipeline` is the shared
room-to-picture chain behind the windowed and the headless renderer.

### What it needs of the card

**Feature level 11_0 and shader model 6.0.** That is the floor the survey asks for, the
floor the device is now created at, and everything the raster path uses: root signature
1.0, flip-model swapchain, BC1–BC7, `Texture.Load`, nothing from the 12_x options. The
device used to be created at 12_0 while the survey had just made one at 11_0 — so a
GeForce GTX 960M (first-generation Maxwell, 11_0) passed the survey and then failed with
`DXGI_ERROR_UNSUPPORTED`, 0x887A0004, as an unhandled exception. Asking for 11_0 is a
floor, not a ceiling: the device that comes back has whatever the hardware has, and
`D3D12Context.FeatureLevel` / `ShaderModel` say what that was.

**Shaders are compiled for the device's own shader model**, capped at 6.5 —
`D3D12Context.DxilShaderModel`, threaded into every `ShaderCompiler` the backend makes and
into its cache key. A module compiled for a newer model than the driver reports is refused
at pipeline creation with an error naming neither, which is why it is not simply 6.5
everywhere any more. The ray-traced variants are the only shaders that need 6.5, and they
are only composed on a device the survey gave the ray-tracing tier, which it does not
without 6.5. A driver reporting less than 6.0 cannot load DXIL at all and is refused
with a message that names `--vulkan`.

**Not fallen back from on a named backend, fallen back from otherwise.** `Application
.OpenRenderer` catches the failure to make a Direct3D renderer, logs `GK3R3422` with the
reason, closes the window and opens a Vulkan one. `--d3d12` on the command line turns
that off, so a machine can still be asked the plain question.

Untested on real 11_0 hardware: none is here. The fallback itself was exercised by making
`D3D12Context.Start` throw and watching the run carry on in Vulkan. What is left that
*could* bite on such a card is resource binding tier 1 (Kepler and Haswell — Maxwell is
tier 2), where every descriptor a root table covers has to be valid; the debug layer would
name it. Resource heap tier 1, which Maxwell *is*, is no concern: every resource here is
committed, none placed, so no heap mixes buffers with textures.

### What the two backends measure at

Mean per-channel difference against the Vulkan render of the same shot, over the whole
frame, from `render-scene --model R25 --timeblock 202P --width 1024 --height 768`. Say the
width and the height when quoting one of these: the number moves with the resolution, and
an earlier row measured at another size is not comparable to one measured here.

| shot | delta | max |
|---|---|---|
| R25 202P, enhanced content, raster | 0.862 | 142 |
| R25 202P, enhanced content, `--rt high` | 0.809 | 168 |
| R25 202P, original content, raster | 0.103 | 85 |

**The delta is understood.** It was two things, one of them a real mistake and one of them
the driver.

The mistake was the mip chain, and it is fixed; see below. It is the whole of the
difference on content the packs do not cover, where the chain is built on the device rather
than shipped, and it grew as the picture shrank: at 320 by 240, where every texture is
minified hard, R25 measured 0.843 before and 0.644 after.

The rest — all of it — is **anisotropic filtering**. Both backends ask for sixteen times,
on the same card, from the same blocks with the same shipped levels, and NVIDIA's two
drivers do not sample the same thing. It shows on the enhanced set and hardly at all on the
original one because the enhanced textures are eight times the size, so a room is drawn
from the middle of their chains rather than from the top of them.

How that was pinned down, because each step rules out a whole class of cause:

- Replacing the sampled albedo with a constant in the shader takes 0.862 to 0.106. It is
  the base colour tap and nothing downstream of it.
- Pinning both samplers to a single level — `MinLOD` and `MaxLOD` both 1, 2, 3, 5 — gives
  0.008 to 0.011 at every one. So the levels themselves agree; the two backends are
  identical texture data sampled differently.
- Turning anisotropy off on both gives 0.009. Turning it back up walks the delta straight
  back: one 0.009, two 0.583, four 0.708, eight 0.869, sixteen 0.862.
- **`MipLODBias = 0.25f` on the Direct3D sampler gives 0.009.** A quarter of a level, exactly
  — 0.15 and 0.35 are both worse, in opposite directions. Direct3D is the sharper of the
  two by that quarter level, which an edge-energy measure agrees with: 20.4 against Vulkan's
  17.9.

So the two backends now agree to a quarter of a mip level of sharpness under anisotropy,
and to a rounding error without it. Whether to *write* that quarter level down is a
decision nobody has taken yet — see below.

Ruled out along the way, none of which moved the number at all, and each of which is worth
not re-testing: the ray tracing level (`none`, `low` and `high` land within a twentieth of
each other), the floor relief, the improved geometry, the normal map, the
occlusion-roughness-metalness map,
the height map's addressing, DXC's `-Gis`, and non-determinism — each backend reproduces
its own picture byte for byte.

## To do, roughly in order

### 1. Decide whether to bias Direct3D's sampler by a quarter of a level

`MipLODBias = 0.25f` in `D3D12Samplers.Write` makes the two pictures agree to 0.009 and is
the only thing between them. It has not been written down, because it is a constant
measured against one driver on one card, and because it makes the sharper picture blurrier
to match the softer one rather than the other way round. Neither backend is wrong: both ask
for the same filter and both are within what either specification promises. This is a call
about what "agree" is worth, and it belongs to whoever flips the default.

### 2. Prove frame generation over a session rather than a screenshot

It runs: with the count at two, R25 presented 1,416 frames for 481 drawn, and the runtime's
own state reported three presented per present. But **whether it elects to interpolate
varies between runs with identical settings** — the same build and settings gave a clean
2.94x once and exactly 1.00x the next time, at every count.

That is DLSS-G's own adaptive gate rather than anything on this side: it keeps a
`disable-interpolation` buffer and a readback of it, and decides per frame. What it decides
from was not established. What is established is that everything this side has to get right
is right, because none of it is conditional — if the tags, the options, the proxy or the
markers were wrong it could never have generated at all.

`--frames N --screenshot` is the wrong instrument for the rest of it: nothing moves, the
camera is still, and the loop is not paced like a session. This wants somebody at the
keyboard with the frame counter visible.

### 3. Flip the default

`Application.ChooseBackend` returns `RenderBackend.Vulkan` unconditionally. Make it
Direct3D on Windows, Vulkan elsewhere. The comment in that method says why it has not
happened yet — update it rather than deleting it. The terrain that used to be the reason is
there now, and so is the sampling delta: what is left of it is a quarter of a mip level of
anisotropic sharpness, which is item 1 and is a decision rather than a defect. FSR is there
now too, so an AMD or Intel machine is no longer offered less by this backend than by the
other one.

### 4. The frame ring is one frame deep

`D3D12Renderer.DrawFrame` calls `_ring.Wait()`, which waits for *every* frame the device has
been given rather than for the slot about to be recorded. The ring has two allocators and
`D3D12FrameRing.Begin` already waits for the right one; the extra drain makes the depth
pointless. Removing it is not free — `D3D12TerrainPass.Record` and `SceneGeometry.Flush`
both write host-visible memory the device may still be reading, and they are correct today
only because of that drain. Both would need per-frame buffers first.

## Done since this file was written

- **Reflex, and the frame token everything else hangs off.** `Streamline.BeginFrame` opens
  a frame and holds its token; the sleep, the six markers, the resource tags, the upscale and
  the present all carry it, which is what makes them one frame to a runtime that is timing
  them. Two tokens in a frame is two frames as far as Reflex and frame generation are
  concerned. The sleep is the only call that does anything to latency and it is the first
  thing a frame does; the markers only tell it where the frame's parts are.
- **Frame generation, at two, three and four times.** The count is
  `DLSSGOptions.numFramesToGenerate` and the most a card will take is
  `DLSSGState.numFramesToGenerateMax`, which this machine reports as three. The settings row
  is trimmed to it rather than offering a factor the runtime would refuse — it declines the
  whole call rather than clamping, so an unreachable setting would quietly mean "off".
- **The swapchain is Streamline's.** `slUpgradeInterface` on the DXGI factory, before
  `CreateSwapChainForHwnd`, so the chain that comes back is a proxy and the generated frames
  have somewhere to be presented from. The factory is borrowed rather than replaced: the
  proxy takes its own reference, so the context goes on using the real one. This is also what
  made `presentCommon` observed on this backend, which the Vulkan one still complains about.
- **The picture without the interface.** Frame generation registers `kBufferTypeHUDLessColor`
  as a *required* tag and then says nothing when it is missing — it presents once, which is a
  game that works and a feature that is off. The back buffer is copied after the film and
  before the interface and tagged, and only while something is generating. It was the last
  missing piece: with it the NGX feature is created, without it only the plugin's generic
  resources ever were.
- **scRGB.** `D3D12Swapchain.Choose` offered PQ or eight-bit and nothing else, so
  `HdrTransfer.ExtendedLinear` silently fell back to standard range. `R16G16B16A16_FLOAT`
  with `RgbFullG10NoneP709` is offered now, and is offered **only when it is asked for**.
  Automatic still means PQ first, as it always did — see the fourth thing worth not
  relearning.
- **FSR on Direct3D.** `D3D12FsrUpscaler`, the twin of the Vulkan one. The FidelityFX types
  that say nothing about which API is underneath moved to `Rendering/Upscaling`; what is left
  beside each backend is its own backend description, what a resource handle points at, and
  how a format is numbered. **Neither backend's FSR has been run** — no FidelityFX runtime is
  installed on this machine — and the file says so at the top.
- **Streamline was told it was Vulkan on both backends.** `Preferences.renderApi` was two
  unconditionally, so every feature was asked what Vulkan extensions it wanted and none was
  asked what it wanted of a Direct3D device.
- **A feature that states its requirements has not necessarily loaded.** Ray reconstruction
  answered what driver it wanted, was then dropped with "not supported on this platform", and
  was reported as available — so the setting that turns it on looked like it worked.
  `slIsFeatureLoaded` is asked as well now, and the startup line says which of the two
  happened.

- **The mip chain averaged in the encoding rather than in light.** A colour texture's
  levels were built by reading the stored bytes through a plain view, averaging those, and
  storing them back. Vulkan's `vkCmdBlitImage` decodes an sRGB source before it filters and
  encodes the result after, so the two backends' chains were different textures. Half black
  and half white is 128 one way and 188 the other: every level three quarters of a stop
  dark, and darker again at each level after it, on every texture with detail in it and on
  nothing else. `D3D12MipChain` reads through the texture's own sRGB view now and encodes
  before it stores; `D3D12Texture.DescribeLevel` no longer casts the encode away. The
  chain is within two of an exact reference at every level, where it was sixty out at the
  first. It hid because `D3D12TextureProbe` uploaded everything as data — the one
  path where the encode does not exist — so the tests only ever asked the question that has
  no wrong answer.
- **A plain `dFdx` is coarse on one backend and fine on the other.** The tangent frame is
  built per fragment from screen-space derivatives, and GLSL's `dFdx` is whichever of the
  two the implementation likes: Vulkan takes it fine, Direct3D takes it coarse, which is one
  frame for a whole two-by-two quad. `MeshShaders` asks for `dFdxFine` and `dFdyFine` now,
  which is what a per-pixel frame meant in the first place. Vulkan's picture does not move
  at all; Direct3D's moves onto it.
- **Per-frame state the windowed renderer never set.** `scene.Flush`, `scene.Advance`,
  `Frames.EmissiveGain`, `Frames.Seconds` and the upscaler's delta are all set in
  `D3D12Renderer.DrawFrame` now. Each was a silent wrong answer: last frame's pose on a
  deforming character, a motion buffer measured against nothing, lamps that could not burn
  above white, and a wind that never blew.
- **The terrain horizon.** `TerrainShaders` holds the eight stages, `TerrainPlan` holds
  everything about a backdrop that is not a device — the mesh, the forest gathered by
  species, which trees are near enough to be models, and the two constant blocks a frame is
  drawn with — and `D3D12TerrainPass` is the twin of `TerrainPipeline` over it. The Vulkan
  class lost 1,500 lines to the two shared ones and reads from them instead.
- **`D3D12AccelerationStructure.Reshape`.** A GK3 character has no skeleton — an `.ACT`
  clip rewrites their vertices outright — so there is no transform that could stand for a
  raised arm. `Settle` now rewrites the vertex buffer and rebuilds that piece's bottom
  level, all of a frame's poses in one submission.
- **The traced world was one instance per mesh, not per part.** `Move` and `SetTraced` are
  indexed by the part number every caller holds, and the structure was built with a separate
  instance for each of a model's dozen meshes — so moving a character moved whichever mesh
  happened to land at that index. Parts are concatenated into one piece now, as Vulkan has
  always done, and a part number is looked up rather than used as a subscript.
- **Every instance carried a mask of `0xFF`.** The trace stages ask for the room alone or
  the models alone by mask — `kRoomOnly` and `kModelsOnly` — and were handed both. The rule
  is `TracedWorld` now, stated once and read by both backends, and so is the room's
  cull-disable: a BSP's polygons carry no consistent winding, so `kSkipShells` must not be
  allowed to cull them.
- **A one-shot command list inside an upload batch.** A model group the artists gave no
  texture is drawn as a one-pixel texture of its own colour, uploaded from inside
  `SceneGeometry.Add`'s open batch — which on Direct3D threw and took the scene down.
  R25 and CSD are two of the rooms it killed. The placeholders are resolved before the batch
  opens now.

## Eight things worth not relearning

**Anything drawn at the far plane needs `LESS_EQUAL`.** The depth buffer is cleared to one
and a sky fragment is written at exactly one, so under Direct3D's default strict less-than
every sky fragment loses to the clear. The painted cubemap had never once drawn on this
backend and nothing said so — the room simply stood against black, in every scene with a
sky. `D3D12Pipeline.CreateGraphics` takes `depthEqual` for it now.

**A flip-model swapchain cannot be created with an `_SRGB` format.** The buffers are plain
`R8G8B8A8_UNORM` and the *render target view* carries the sRGB form — that is
`D3D12Swapchain.RenderFormat`, and it is what every pipeline writing a back buffer must be
built for. Getting it wrong is a game that is far too dark, not an error.

**A screenshot is always eight-bit sRGB.** A frame presented in HDR10 read back as four
bytes of RGBA gives the right picture with every value scrambled — perfect geometry,
noise for colour — which looks exactly like a renderer bug and is not one. `HdrCapture`
is the conversion; both backends use it.

**An encoding is not a private matter between the swapchain and the room.** Automatic was
briefly made to prefer scRGB while frames were being generated, on the sound-looking argument
that interpolating two ST.2084 frames averages a quantity that is not linear in light. What
it overlooked is that the interface, the film and the fade are drawn straight onto the
swapchain and **blend in whatever space it carries** — `DisplayEncoding` says so, and says
this project chose that over compositing deliberately. PQ is perceptual and scRGB is linear
light, and a glyph is almost entirely partial coverage, so the blend space is the whole of
how its edges look. The room went on looking correct and every letter in the game came out
wrong.

Two lessons rather than one. The encoding may not be changed underneath a player because
some unrelated setting is on; and **anything that changes the swapchain's format has to be
judged on the interface, not on the room**, because the room is composited through a pass
that handles the transfer function and the interface is not.

**A resize has to read the window, not the thing being resized.** `Recreate` took the size
back out of the swapchain it was about to resize and handed it straight back, so a resize
resized nothing: the chain stayed at whatever size it was first made at and DXGI stretched
every presented frame to fill the window. It reads as a game that gets blurrier the further
the window is dragged from the size it started at, blurriest at fullscreen, with the
interface blurred along with the room — which is what made it look like a font problem. The
Vulkan renderer holds its window for exactly this; this one took a window, made a swapchain
and let go of it.

**A display list carries its own atlas, and the renderer has to follow it.** The interface is
cut at two sizes — the room's captions and the menu — and `SetOverlay` on this backend took
the list and kept whichever sheet was already on the device. The menu was therefore drawn
with the room's atlas: one sheet sampled with another's coordinates, which is a row of
fragments where the words should be. The layout stays correct, so it reads as a broken font
rather than as a renderer that swapped a texture.

`VulkanRenderer.SetOverlay` had always compared the two and re-uploaded, and says so in a
comment naming this exact symptom. Nobody saw it here because Vulkan was the default and
everything except the menu draws from the sheet that is already loaded. **Flipping a default
is not a no-op: it is the first time anybody looks at the other backend's whole surface.**

**A required Streamline tag that is missing is silent.** Frame generation prints the list it
requires once, at startup, into the runtime's own log at a level nothing normally shows —
`Registering required tag 'kBufferTypeHUDLessColor'`. A frame that arrives without one is not
refused, warned about, or reported through any state the application can read: it is
presented exactly once. Everything else looked right for as long as it took to find that
line — the hooks all reported OK, the plugin allocated its resources, the state said no
error. Turn `Preferences.logLevel` up to two and let the informational messages through
before believing that a Streamline feature is configured.

**A backend comparison is only a number beside the resolution it was measured at.** The
delta between the two moves by a factor of ten across the sizes anybody would render at,
and in both directions depending on what is causing it: a mip-chain difference grows as the
picture shrinks, and an anisotropy difference barely moves. Two rows measured at different
sizes cannot be subtracted, and a regression against a row whose size nobody wrote down
cannot be shown. Every row in the table above says 1024 by 768, and a new one must too.

The debug layer is the fastest way to the truth here and nothing drains it by itself.
`D3D12Renderer.Messages` reads it, and `Application` prints whatever it says after the
first presented frame.

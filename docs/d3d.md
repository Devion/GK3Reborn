# Direct3D 12 — what is left

Where the second backend stands as of 2026-08-30, and what to pick up next.

## Working

The game runs on Direct3D 12: `--backend d3d12`. Room, sky, **the reconstructed terrain
horizon**, interface, film, fade, ray tracing, DLSS, HDR10.

Reference renders: `render-scene --backend d3d12 [--rt high] [--dlss quality]`. Note that
`render-scene` cannot show the horizon on *either* backend — the Vulkan headless
`SceneRenderer` draws the room and nothing behind it — so a backdrop is compared through the
window instead: `GK3Reborn.Host --scene CSD --timeblock 202P --frames 8 --screenshot out.png
--backend d3d12` against the same on Vulkan.

`IRenderer` is the seam and both renderers implement it. `D3D12FramePipeline` is the shared
room-to-picture chain behind the windowed and the headless renderer.

### What the two backends measure at

Mean channel delta against the Vulkan render of the same shot:

| shot | delta | max |
|---|---|---|
| R25 202P, raster, `render-scene` | 4.65 | 202 |
| R25 202P, `--rt high`, `render-scene` | 4.35 | 186 |
| CSD 202P, windowed, terrain horizon | 6.72 | 233 |

**These are worse than the 3.85 and 0.424 this file recorded before, and the difference is
not the terrain.** Amplify the R25 difference image and it is a fine edge pattern spread
evenly over every surface in the room — the ceiling relief, the wallpaper, the carpet —
including walls no character or shadow touches. It reads as a sampling difference rather
than a shading one, and it is present with and without tracing. Both backends ask for
sixteen-times anisotropy and both build the same mip chains, so that is not it either. This
is the first thing to chase, and it wants a flat untextured shot and a single quad before it
wants a room.

## To do, roughly in order

### 1. Explain the sampling delta above

See the table. Until it is understood the two backends cannot be said to agree, and the
default cannot move.

### 2. Frame generation and Reflex

Streamline is attached and DLSS super resolution runs. Frame generation wants a
`R16G16B16A16_FLOAT` scRGB swapchain rather than a PQ one — `D3D12Swapchain.Choose`
already names the format and the reason in its own doc comment, but does not offer it.
`HdrCapture.ToOrdinary` already handles reading that format back.

### 3. Flip the default

`Application.ChooseBackend` returns `RenderBackend.Vulkan` unconditionally. Make it
Direct3D on Windows, Vulkan elsewhere. The comment in that method says why it has not
happened yet — update it rather than deleting it. The terrain that used to be the reason is
there now; the sampling delta is the reason today.

### 4. The frame ring is one frame deep

`D3D12Renderer.DrawFrame` calls `_ring.Wait()`, which waits for *every* frame the device has
been given rather than for the slot about to be recorded. The ring has two allocators and
`D3D12FrameRing.Begin` already waits for the right one; the extra drain makes the depth
pointless. Removing it is not free — `D3D12TerrainPass.Record` and `SceneGeometry.Flush`
both write host-visible memory the device may still be reading, and they are correct today
only because of that drain. Both would need per-frame buffers first.

## Done since this file was written

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

## Three things worth not relearning

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

The debug layer is the fastest way to the truth here and nothing drains it by itself.
`D3D12Renderer.Messages` reads it, and `Application` prints whatever it says after the
first presented frame.

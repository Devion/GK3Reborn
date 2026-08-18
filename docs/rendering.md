# Rendering: capability tiers

The first thing the renderer needs is to know what the machine can do, and it is the last
thing that should guess. `Plan/01-architecture.md` section 5.1 requires tiers to be
selected from *queried* capabilities rather than from vendor or version assumptions, since
ray tracing and HDR must never prevent raster play.

## Tiers

| Tier | Requires | Meaning |
|---|---|---|
| Compatibility | `VK_KHR_swapchain` | Can present; the raster path. Without it the device is not a candidate at all. |
| Enhanced | Vulkan 1.2 or later | The compute and descriptor headroom clustered lighting and GPU culling need. |
| RayTracing | `VK_KHR_ray_tracing_pipeline`, `VK_KHR_acceleration_structure`, `VK_KHR_deferred_host_operations` | RT shadows, reflections, GI. |
| HighDynamicRange | `VK_EXT_hdr_metadata` | HDR output. |

Tiers are additive and never gating: a device that reaches only Compatibility renders the
whole game.

## Choosing a device

Ordering is capability count, then discrete hardware, then device-local memory —
deliberately **not** vendor or device name, which is how renderers quietly acquire
hardware-specific behaviour. A device that cannot present is excluded outright regardless
of how capable it is otherwise, which is what stops a compute-only accelerator being
chosen over a modest integrated GPU that can actually draw.

## Reporting

`VulkanDeviceSelector.Survey()` runs before any window exists, so it works as a diagnostic
on a machine that cannot run the game and on a build agent with no GPU. A missing loader is
a reported condition rather than a crash — there is a test asserting the survey never
throws.

On the development machine:

```text
Vulkan: 2 device(s), validation layers not installed
  * NVIDIA GeForce RTX 5090 (Discrete, Vulkan 1.4.341, 31.4 GiB)
      -> Compatibility, Enhanced, RayTracing, HighDynamicRange
    AMD Radeon(TM) Graphics (Integrated, Vulkan 1.3.280, 2.0 GiB)
      -> Compatibility, Enhanced, RayTracing, HighDynamicRange
```

Note that the integrated part also reports every tier. That is exactly why the tier model
exists rather than a hardware list: what a device supports and what it can usefully do at
frame rate are different questions, and only the first can be answered by querying.

Validation layers are not installed here. They ship with the Vulkan SDK and are a
developer prerequisite for the renderer work proper, per the shader toolchain entry in
`Plan/01-architecture.md` section 1.

## Unsafe code

Vulkan is a pointer-based API and Silk.NET surfaces it as one, so `AllowUnsafeBlocks` is
enabled for the engine assembly. Its use is confined to `Rendering/Vulkan`, which the
layering tests already enforce by forbidding any other area from referencing
`Silk.NET.Vulkan` at all.

## The present loop

`VulkanRenderer` opens a device, builds a swapchain and presents. It is P5's foundation
rather than its finished form: it establishes the parts every later pass depends on and
which are painful to retrofit — queue family selection, swapchain creation and
recreation, per-frame synchronisation, and command recording. A render graph sits on top
of exactly this.

```bash
dotnet run --project src/GK3Reborn.Host -- --render                   # until closed
dotnet run --project src/GK3Reborn.Host -- --render --headless-frames # 60 frames, then exit
```

On the development machine:

```text
Renderer: NVIDIA GeForce RTX 5090: 1280x720, 3 images, B8G8R8A8Srgb,
          tiers Compatibility, Enhanced, RayTracing, HighDynamicRange
Presented 60 frames at 1280x720 across 3 swapchain images
```

The frame limit makes this a smoke test rather than something needing a human to close a
window, which is what lets it run on a build agent with a display.

### Decisions worth knowing

**Dynamic rendering, not render passes.** Vulkan 1.3's dynamic rendering removes render
pass and framebuffer objects entirely. Those are a large amount of boilerplate the render
graph would otherwise have to create, cache and invalidate for every pass and every
format combination.

**Two frames in flight, one fence each.** The CPU may run ahead but never overwrites a
command buffer the GPU is still reading. Getting this wrong produces corruption that only
appears under load, which is the worst kind to find late.

**The fence resets only once submission is certain.** Resetting it before a path that can
return early — an out-of-date swapchain, for instance — deadlocks the next wait on it.
This is a classic Vulkan bug and the reason the reset sits after the acquire rather than
before it.

**Swapchain recreation is a normal event.** Resizing, moving between monitors and
minimising all invalidate it, and the driver reports that through `ErrorOutOfDateKhr` and
`SuboptimalKhr` rather than by failing. A minimised window reports a zero-sized
framebuffer, which is filtered in the platform layer so the renderer never tries to build
a zero-extent swapchain.

**FIFO present mode.** The only mode the specification guarantees, so it is the safe
default until the settings screen offers mailbox and immediate.

**An sRGB surface.** The display does the encoding, which keeps shading linear — the
right default for a PBR pipeline and for the HDR work later.

### Layering

The renderer needs a surface from the window, but the platform layer must not depend on
the graphics backend. `IVulkanSurfaceSource` is therefore declared in `Platform` in terms
of native handles, and `Rendering/Vulkan` consumes it. The layering tests enforce the
direction, and the reason for it is that a window should not have to change when the
renderer does.

## Shaders

Shaders are HLSL, as `Plan/01-architecture.md` section 1 chose. The compiler is **not**
DXC, which that section named: DXC ships with the Vulkan SDK, and requiring the SDK to
build the project at all is a real barrier for a contributor who only wants to change
gameplay code. `shaderc` compiles HLSL to SPIR-V just as well and arrives as a NuGet
package, so the toolchain installs itself with the rest of the dependencies.

Compiled SPIR-V is cached on disk under a hash of source, entry point and stage. After
the first build compilation is effectively offline — which is what the plan wanted from
DXC — and the compiler runs only when a shader actually changes. There is a test that a
source edit produces a second cache entry rather than colliding with the first.

## Offscreen rendering

`OffscreenRenderer` draws with no window, no surface and no swapchain, then copies the
image back to host memory. That is what P5's headless image tests require, and it runs on
a machine with no display.

```bash
dotnet run --project src/GK3Reborn.Host -- --offscreen   # writes offscreen.png
```

It matters more than it sounds. A windowed run proves the code does not crash; only
reading the pixels back proves anything was drawn, and from the outside those two failure
modes look identical. The render tests assert that a meaningful number of pixels differ
from the clear colour, and that the vertex colours interpolate the way the shader
describes — red at the apex, green at the lower right, blue at the lower left.

The offscreen target is `R8G8B8A8Srgb`. Textures decode to linear on sample and shading
happens in linear space, so the target has to encode on write; writing linear values into
a UNORM target and calling the result sRGB costs about a gamma, which looks like a
lighting bug rather than a colour-space one. The triangle bring-up test, which writes
constant values and reads them straight back, is the one place that reasoning does not
apply — and it is why the target was UNORM at first.

Tests that need a device skip rather than fail where none exists, so a build agent without
a GPU still reports a green run while a developer machine gets the real check.

### First bring-up choices

The triangle's vertices come from `SV_VertexID` rather than a vertex buffer, and culling
is off. Both are deliberate for a first bring-up: a failure is then a shader, pipeline or
render-target problem and cannot be a buffer, memory or winding problem. Those come next,
once this much is known to work.

Viewport and scissor are dynamic state, so a window resize re-records the command buffer
rather than rebuilding the pipeline.

## Drawing a scene

`SceneGeometry` holds what a scene needs on the GPU — vertex and index buffers, textures,
the lightmap atlas and per-batch descriptor sets — and knows nothing about where it is
drawn. `SceneRenderer` renders it offscreen; `VulkanRenderer` renders it into the
swapchain. Both go through `SceneDraw`, so a regression image and what a player sees
cannot drift apart.

```bash
# a still, headless, from one of the scene's own cameras
GK3Reborn.Tools render-scene --source <GK3>/Data --model R25 --timeblock M --camera SITTINGAREA

# the same scene in a window, with a camera that can be flown around
GK3Reborn --scene R25 --timeblock M --data <GK3>/Data
```

### Resources by rate of change

Descriptor sets are split by how often their contents change:

| set | contents | rebuilt |
| --- | --- | --- |
| 0 | camera: view-projection, key light direction, eye position | once per frame, one buffer per frame in flight |
| 1 | a batch's diffuse texture and lightmap | never, after loading |
| push constants | model transform, shading mode | per draw |

The model transform is a push constant rather than a uniform because per-draw uniform
buffers must be either reallocated every frame or written while a previous frame may still
be reading them. Getting that wrong produces flickering that is very hard to attribute.

One trap worth naming: declaring push constants in HLSL as a plain global rather than
`[[vk::push_constant]] ConstantBuffer<T>` makes glslang treat them as an unbound
descriptor. Every draw then reads undefined transforms and lands off screen, with no
validation error and no crash — the picture is simply empty.

### Baked lighting

A scene has one lightmap per surface: R25 has 925 of them totalling 54,040 texels, an
average of about 7×7 each. Binding each as its own texture would mean a descriptor set,
a draw call and a device allocation per surface, and drivers guarantee only a few thousand
allocations in total — a handful of scenes would exhaust them.

They are packed into one atlas instead, and each vertex carries an atlas coordinate
computed on the CPU as `(uv + surface.offset) * surface.scale`, mapped into the tile and
clamped. Tiles get a one-texel gutter and their coordinates are inset by half a texel;
without that, bilinear filtering at a tile's edge reaches into its neighbour and a wall
picks up the lighting of whatever happened to be packed beside it.

The atlas has no mips and clamps rather than repeats, because both would sample across
tile boundaries. The diffuse textures do have mips: GK3's textures are small — over three
thousand are 128 pixels or fewer — and a small texture on a receding surface without mips
reads as shimmering noise.

Lightmap and texture are multiplied, and the original scales the product by two in gamma
space. Doing the same multiplication in linear space needs that constant raised to the
gamma, or a fully lit surface comes out at about 70% of the brightness the game showed.

### Transparency

GK3 keys transparency on magenta rather than on an alpha channel, and the original
discards it in the fragment shader with a tolerance. That works because it never builds
mips: the key colour is either sampled exactly or not at all.

With mips it does not. Filtering between a magenta texel and its neighbour produces a
colour that is neither, and mip generation spreads it further — every window mullion and
railing ends up ringed in magenta. So the key is converted to alpha *before* upload, and
the colour of keyed texels is replaced by the nearest opaque colour so filtering has
something plausible to reach for. The shader tests alpha, which blurs gracefully; the
colour test stays as a backstop.

### What is hidden

A scene's initialisation file distinguishes three things that look identical in the
geometry. `prop` and `gasprop` name a model file to load; `scene` and `hittest` name
objects the BSP already contains. Hit tests are clickable volumes the player never sees,
made of ordinary textured geometry — nothing about the data itself says to skip them, and
drawing them puts large flat slabs through the middle of a room.

### Current state

All 110 scenes render headlessly without failure. 71 render from one of their own room
cameras; the remaining 39 are variant geometry with no initialisation file of their own
and are framed on their bounds instead.

Shading is still a single directional term with an ambient floor, used for props, plus the
baked lightmaps for scene geometry. The 4,109 lights the artists authored are parsed (see
[ADR 0007](adr/0007-authored-light-rigs-from-scene-assets.md)) but not yet evaluated,
which is why a prop standing in a dark room can be lit as though it were outside.

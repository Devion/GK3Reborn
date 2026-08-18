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

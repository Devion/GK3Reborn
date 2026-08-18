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

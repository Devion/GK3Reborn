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

Shaders were HLSL, as `Plan/01-architecture.md` section 1 chose; the mesh shaders are now
GLSL, because glslang implements inline ray tracing only in its GLSL front end. See
[ADR 0008](adr/0008-glsl-for-ray-traced-shading.md). The compiler is **not** DXC, which
that section named: DXC ships with the Vulkan SDK, and requiring the SDK to
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
GK3Reborn.Tools render-scene --source <GK3>/Data --model R25 --timeblock 202P --camera SITTINGAREA

# the same scene in a window, with a camera that can be flown around
GK3Reborn --scene R25 --timeblock 202P --data <GK3>/Data
```

`--walk-overlay` lays the scene's walk boundary over the floor, each texel at the height
of the ground beneath it and coloured by region: green for open floor, darkening towards
the walls, amber for the regions a script opens. It draws through the same self-lit path
as a light bulb and stays out of the acceleration structure, so an overlay never changes
the picture it exists to check.

`--timeblock` takes either a point in the story — `202P` — or an asset suffix — `M`, `A`,
`E`, `N`. The story form decides the scene file's conditions, so the scene comes out in
one state with the file itself choosing the asset and the bake; the suffix form only
picks a bake, and leaves the scene as the union of every state it can be in. Naming a
camera also finds the cinematic ones, which are often the angles worth comparing.
See `docs/formats/scene-text.md`.

### Resources by rate of change

Descriptor sets are split by how often their contents change:

| set | contents | rebuilt |
| --- | --- | --- |
| 0 | camera: view-projection, key light direction, eye position | once per frame, one buffer per frame in flight |
| 1 | a batch's five textures: colour, lightmap, normal, ORM, height | never, after loading |
| push constants | model transform, shading mode, the surface's finish | per draw |

The model transform is a push constant rather than a uniform because per-draw uniform
buffers must be either reallocated every frame or written while a previous frame may still
be reading them. Getting that wrong produces flickering that is very hard to attribute.

One trap worth naming: declaring push constants in HLSL as a plain global rather than
`[[vk::push_constant]] ConstantBuffer<T>` makes glslang treat them as an unbound
descriptor. Every draw then reads undefined transforms and lands off screen, with no
validation error and no crash — the picture is simply empty.

### Materials

A surface is base colour, a normal map, a packed occlusion/roughness/metalness map and a
height field. Only the first is in the 1999 assets; the other three are generated, live in
`enhanced/normals`, `enhanced/orm` and `enhanced/height`, and are named for the colour
texture they belong to. See [pbr-materials.md](pbr-materials.md) for how they are made and
judged.

**A partial set is a perfectly good set**, and that is a property of the binding rather
than a branch in the shader. Every batch binds all four maps; a surface with none of them
gets a flat normal `(0.5, 0.5, 1)`, a neutral ORM `(1, 1, 0)` — unoccluded, fully rough,
not a metal — and a level height at mid grey with a height scale of zero.

**A surface with no ORM map gets no specular lobe at all**, and that is a decision rather
than an accident. Without a map the roughness is a classifier's guess at median confidence
0.32, and GK3's 1999 diffuse textures already have their highlights painted into them, so a
physical lobe over a painted one counts the same light twice. `SceneGeometry` sends a
reflectance of zero for such a surface and the shader reads that as "no measured finish";
the shading then reduces to exactly the Lambert term the renderer had before any of this
existed.

**Zeroing the reflectance is not how you switch a specular lobe off.** Schlick's
approximation returns *one* at grazing incidence whatever f0 is, so an f0 of zero leaves a
hard white rim around every silhouette and takes the diffuse away underneath it — which is
a very good description of a mannequin. The flag multiplies the Fresnel term itself, so it
removes the specular *and* gives the diffuse its energy back.

**Where there is a map, the map is the answer** — it does not multiply the material's
scalar. Multiplying is the glTF convention, where the material's roughness is a *factor*
defaulting to one; here `manifests/material-library.json` holds a classifier's estimate of
the same quantity the map estimates, and multiplying two independent answers to one
question squares the glossiness. Gabriel's skin is 0.55 in the library and 0.56 in his map,
and 0.31 is polished plastic.

**And a person's correction beats both.** `material-library.materials.edits.json` is read
beside the library — the layer ADR 0006 describes, which was being written and never read,
so every correction anybody made to a material did nothing at all. A material the edit
layer has touched comes through with `Provenance` above `Derived`, `SceneGeometry` sends
its roughness **negative**, and the shader takes that as "this number is the answer, ignore
the map's". The sign is free because roughness is clamped to at least 0.03. Without this
the edit layer cannot fix the one thing it most obviously needs to: a generated map that is
wrong about what the surface is. The scene report says how many corrections applied,
because one that silently failed looks exactly like none.

**A lamp is not a point.** The rig gives every light an emitter radius — four units for a
bulb, twenty for a window — and shading against the centre puts a pinpoint mirror highlight
on anything smooth. The microfacet lobe is widened by the light's apparent size and
renormalised so the energy is unchanged, which is the standard correction: a lamp across
the room is still nearly a point, the same lamp a hand's width away is a soft sheen.

Shading is Lambert diffuse plus a Cook-Torrance specular lobe — GGX distribution, Smith
height-correlated visibility, Schlick Fresnel — with **both terms multiplied by π**. A
textbook BRDF divides the diffuse by π and leaves the light's radiance alone; this rig's
intensities were authored in 3ds Max in 1999 and tuned here against a plain Lambert with no
π anywhere, so introducing the division darkens every rig-lit surface to a third of what it
was. Scaling both terms instead is the same BRDF with the light's radiance in the units the
authored numbers are already in, and it leaves the lightmapped and ambient paths untouched.

**Metalness is a switch between two shading models, not a slider between two numbers**: a
metal has no diffuse term at all and tints its reflection with its own base colour. A
classifier that calls a stone wall metal produces a picture nobody could mistake for
correct, which is why `SurfaceFinishes` reports how many of the corpus it thinks are metal.

Ambient occlusion multiplies the **ambient** term and nothing else. It is a statement about
light arriving from every direction at once, and applying it to a lamp's direct light
darkens a surface the lamp can plainly see.

Height is consumed as single-step parallax: a texture-coordinate offset along the view
direction in tangent space, scaled by how far above or below the modelled surface the field
says the texel is, and divided by how head-on the surface is being looked at — clamped,
because at grazing incidence that divisor goes to zero and the surface tears. It deepens
mortar courses and floorboards convincingly and does nothing whatever to a silhouette.

Three things are worth knowing because they are silent when wrong.

**Everything but base colour is linear data.** Roughness, metalness, occlusion, height and
the normal's channels are measurements stored in a picture. Uploading one through the sRGB
path bends every value towards one end of its range, which reads as a generator that
produced bad numbers rather than as a renderer that misread good ones.

**A layout binding is not added by writing to it.** `BindingCount` has to move with the
array. It did not, once, and the driver did not complain: it quietly corrupted binding 0
and every surface drew the fallback checkerboard. The pool's `DescriptorCount` has to move
with it too, or a room runs the pool dry partway through and the batches after it are never
bound at all.

**A character must not shadow itself.** GK3's people are not solid bodies: a character is a
dozen separate meshes, a shirt shell with a torso inside it and arms passing through
sleeves, so a shadow ray leaving the shirt towards a lamp hits the arm underneath before it
has gone anywhere. Every character in every room wore a hard dark patch across the chest
and the small of the back, reported as fully shadowed *and* fully occluded whatever the
lighting was doing. No ray bias fixes it, because the geometry the ray hits is genuinely
inside the surface it left.

The acceleration structure splits into two instance masks — the room, and the models
standing in it — and the mesh pass writes a **negative roughness** into the normal target
for a model. The tracing pass reads that one bit and traces the room only when the ray
leaves a model. A ray leaving the room still traces everything, so a character still lays a
shadow on the floor; what is lost is one character shadowing another, which is worth it.

**The ray-traced tier is not reproducible frame to frame — in the host.** Two runs of the
same build at `--rt high` differ across about seven per cent of the frame, because the
shadow and occlusion denoisers accumulate over however many frames the wall clock allowed.
Comparing two builds by diffing screenshots therefore needs a difference well above that
floor to mean anything; below it, look at the picture.

**`render-scene` is the tool that does compare.** It runs the whole deferred chain — the
room's parts, the trace, the filter, the composite — and it builds those stages for one
render and throws them away with it, so nothing is carried over and the same scene renders
to the same pixels every time. R25 at `--rt high` twice is byte-identical.

It did not always run that chain. For a while it bound the picture alone and left the other
three colour targets unbound, so at any ray-traced level the rig's direct light went to a
target nothing read and characters came out lit by the ambient floor — a class of shading
bug the tool could not show at all, and three ray-tracing tests that could not pass on any
machine. Binding one attachment against a pipeline that declares four is undefined
behaviour besides, and both renderers bound all four from then on.

What it still does not draw is the sky, and reflections: a single frame has no picture
before it to reflect, so the reflection pass marches against black and adds nothing. Both
are the host's to show.

**Eight bits cannot encode a half.** A flat normal is `(0.5, 0.5, 1)` and the nearest byte
is 128, which decodes to 0.0039 rather than 0. An early-out comparing against exactly
`(0, 0, 1)` therefore never fires, and the derivative maths runs on all 6,300 textures with
no map. The tolerance is one step of an eight-bit channel, not an epsilon.

There is no tangent on the vertex. The frame is built in the fragment shader from the
screen-space derivatives of position and texture coordinate, because `.ACT` clips rewrite
vertex positions every frame — GK3's characters have no skeleton — so a stored tangent
would be stale the moment anybody moved. Parallax and the normal both build it, and both
take the derivative of the **interpolated** coordinate rather than the offset one: a
derivative across an offset that varies per pixel measures the offset as well as the
surface, and the frame comes out skewed wherever the relief is steepest.

Which file wins, where there is more than one: the `.png` in `enhanced/` beats the `.dds`
in `build/`. That is the opposite of the shipping order and deliberate while the generated
sets are still moving — a `.dds` is whatever the last compression run made of whatever the
enhanced set held at the time, and taking it first means regenerating a texture changes
nothing on screen until somebody remembers to recompress.

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

All 110 scenes render headlessly without failure, with and without ray tracing. 71 render
from one of their own room cameras; the remaining 39 are variant geometry with no
initialisation file of their own and are framed on their bounds instead.

Scene geometry is lit by the baked lightmaps, and everything else by the rig the artists
authored (see [ADR 0007](adr/0007-authored-light-rigs-from-scene-assets.md)). Ray-traced
shadows and occlusion are available on hardware that supports them — see
[ray-tracing.md](ray-tracing.md) for the quality ladder and what it does and does not
compute yet.

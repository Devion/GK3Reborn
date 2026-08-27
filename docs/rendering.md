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

## Portability drivers, and devices with no block compression

Vulkan on macOS is MoltenVK translating to Metal, which the specification calls a
*portability* driver rather than a conformant one. Two things follow, and both are
refusals rather than degradations if they are missed:

- an instance has to pass `VK_KHR_portability_enumeration` and the flag that goes with
  it, or **no device is enumerated at all** — the survey reports an empty machine;
- a device advertising `VK_KHR_portability_subset` must have it in the enabled extension
  list, or `vkCreateDevice` fails.

`VulkanPortability` decides both, for every instance the engine creates — the game's, the
headless context's and the device survey's. Neither is behind an operating-system check:
the extensions are simply absent on Windows and Linux drivers, so the same path runs
everywhere and a Linux build exercises it.

The same class also queries the device's features rather than asking for what would be
convenient. **A feature the device lacks fails device creation outright**, so
`SamplerAnisotropy` and `TextureCompressionBC` are requested only where they are offered,
and what the device answered is carried on `VulkanContext.Capabilities` for the texture
path to read.

### The blocks

Apple silicon has **no BC formats whatsoever** — Metal offers ASTC and ETC2 instead — so
the content pipeline's BC7, BC5 and BC4 textures cannot be created there at all. They are
expanded on the host instead: `BlockDecoder` turns each level into eight-bit pixels and
`VulkanTexture` uploads the whole chain as `R8G8B8A8_SRGB` or `R8G8B8A8_UNORM`. The
compressor's own mip chain is kept rather than regenerated, because a BC5 normal map
minified by blitting is a normal map of the wrong length.

It costs **four times the video memory** for every enhanced texture in the scene, which is
the price of the format not existing on that device. Shipping ASTC alongside BC in the
packs would remove that cost and is the obvious follow-up; it needs an encoder the content
pipeline does not have yet, since encoding is `texconv` and `texconv` is Windows-only.

**`--expand-blocks` makes a machine that has the formats take that path**, which is the
only way to exercise it without a Mac. `render-scene` takes it too, and that is what the
path was checked with:

| what | against | result |
|---|---|---|
| 240 of the pipeline's own textures decoded | the pictures they were encoded from | 40.6–61.2 dB, i.e. the compressor's own error |
| a 1024×960 BC5 normal map decoded | `texconv -f R8G8B8A8_UNORM` | **byte-identical**, all four channels |
| R25 202P `TO_BATH` rendered with `--expand-blocks` | the same frame from the blocks | 0.224% of bytes differ, worst 12 of 255 |

The last row is not a decode error, which is what the row above it establishes. It is the
GPU filtering BC5 at more than eight bits of intermediate precision before it interpolates
— hardware is allowed to, and this one does. The difference only appears through the
normal map, where a fraction of a step of tilt reaches the lighting; the same comparison
for BC7 and BC4 is byte-identical.

**The ramp is rounded, not truncated.** BC4 and BC5 write a channel as two endpoints and
six or four values between them, and the specification writes those as fractions. Dividing
by seven in integers instead is one less over about a fifth of the ramp: invisible in a
colour texture, and worth 0.6% of a lit frame through a normal map. That was the first
version of this decoder, and the comparison above is what found it.

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

**It is built only when asked for**, by `VulkanRenderer.Create(..., bringUp: true)`, which
is the `--render` smoke test and nothing else. A frame with no room in it falls back to
whatever this pipeline draws, and the game has several such frames — the intro films, the
menu, the timeblock card, the moment between two rooms. Built always, it showed as one frame
of a red-green-blue triangle between the publisher's logo and the opening film, and it would
have been what the timeblock card's words sat on in an installation with no painting.

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

`--camera` takes a room camera, a cinematic or dialogue camera, or one of the close-up
views the artists keyed by what it looks at rather than named — `--camera HOTEL_DOOR` is
the only shot in `RC1` pointed at the hotel's front door. Failing all of those it takes a
viewpoint spelt out, `at=x,y,z,heading[,pitch]`, the angles in degrees as a scene file
writes them, for the shots nobody framed: looking down on a square to see where a
building's shadow falls is not a camera the game has.

`--walk-overlay` lays the scene's walk boundary over the floor, each texel at the height
of the ground beneath it and coloured by region: green for open floor, darkening towards
the walls, amber for the regions a script opens. It draws through the same self-lit path
as a light bulb and stays out of the acceleration structure, so an overlay never changes
the picture it exists to check.

`--no-trees` leaves the scene's foliage cards flat instead of growing modelled trees over
them, which is how the two are compared. See `docs/trees.md`.

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
| push constants | model transform, shading mode, height depth, the surface's finish | per draw |

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

Height is consumed twice: as a march in the shader, and — on a floor — as geometry.

**Parallax occlusion mapping.** The shader steps along the view ray through the height
field and samples where the ray first meets it, refining once between the last two steps.
The march is in the field's own parameter: mid grey is the modelled surface, the ray
crosses the polygon at that value by construction, and the coordinate at any point on the
ray is the entry coordinate offset by however far the ray is above or below the polygon.
Step count runs from eight looking straight down at a floor to twenty-four at grazing
incidence, where the ray crosses more of the field per unit of depth and a coarse march
stairsteps visibly. Levels of detail are explicit — `textureGrad` off the *entered*
coordinate's derivatives — because an implicit one is undefined in control flow that is not
uniform across the quad.

It replaced a single step, which offset once by the height at the coordinate the ray
entered on. That is exact only where the field is flat and wrong by more the further from
head-on a surface is looked at, which is precisely how a floor is seen.

**Depth is in world units, not texture coordinates.** The tangent frame the march needs
already carries the conversion: the frame is built from the screen-space derivatives of
position against texture coordinate, and the length the Gram-Schmidt step normalises away
is how much world one unit of texture coordinate is worth. Keeping it is what lets one
number mean the same thing on every surface. It was in texture coordinates until the corpus
was measured: the game tiles one road texture over 232 units of street and one lobby floor
over 32, so a single number was seven times as deep on the second and nobody had chosen
that. Depth comes from the material library, by class — four units for rubble stone and a
made road, a unit and a half for tile, half a unit for polished marble, nothing at all for
a painted backdrop.

**A floor's relief also becomes geometry**, which is the half a march cannot do: it moves
the silhouette. See [Displaced floors](#displaced-floors).

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

**Pass `--enhanced` to get the materials.** Without it the tool draws the original textures
at the shader's own defaults — no surface finishes, no normal, ORM or height maps — so it
cannot show a material bug of any kind: a floor the library calls polished comes out matte,
and a correction made in the edit layer changes nothing on screen. It reports what it
loaded (`materials: N measured, N reflective, N metal, N corrected by hand`), and a zero
there means the picture is not the one the host would draw.

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

### The light grid

A scene's lights used to live in a uniform block of sixty-four, and a scene declaring more
had the rest dropped — brightest first, across the whole room. That is the wrong failure:
the lamp beside the player goes because a streetlight three rooms away is brighter. It was
also the hard cap on loading more than one room at once, since the hotel's three rooms
declare 195 lights between them.

Both are gone. The rig is a **storage buffer**, which is unsized on both sides, so the
capacity is now an allocation — a thousand lights, sixty-four kilobytes — rather than a
limit anything reaches. And nothing iterates it: `SceneLightGrid` divides the room into
cells and gives each the list of lights that can actually reach it, so a fragment loops the
handful lighting the place it stands.

**A world grid, not a view frustum.** Clustered renderers usually slice the frustum, because
their lights move and their camera moves and the assignment has to be redone every frame.
GK3's rig is authored per scene and does not move at all, so the world is sliced instead:
the assignment happens once when a room loads, costs nothing per frame, and stays correct
for a ray that leaves the frustum — which the frustum version does not.

Cells are at least 100 units — two and a half metres, finer than the lamps in a lit room are
spaced — and the grid is capped at 16,384 of them. A light with no falloff is in every cell,
because the sun is not somewhere in the room. Each cell's list is ordered brightest first,
which is not decoration: the passes that can only afford a couple of shadow rays spend them
on the front of it.

Measured, at the minimum cell size:

| scene | lights | average a cell | busiest cell |
|---|---:|---:|---:|
| CSE, the village forecourt | 18 | 1.5 | 16 |
| LBY, the hotel lobby | 41 | 4.8 | 19 |
| HAL, the hotel hallway | 92 | 20.1 | 46 |

`render-scene` reports the grid, because the whole point is that nothing looks different —
a fragment gets the same lights, reached more cheaply — so the numbers are the only way to
see it working. A cell that wants more than 96 lights keeps its heaviest and the count of
such cells is reported; no scene in the corpus has one.

The denoiser still walks the whole rig rather than a cell. It runs once a pixel to decide
*which* light is worth a shadow ray, and that is a question about the brightest few in the
room rather than about the handful reaching one point — which is what the rig's ordering is
for.

### Displaced floors

A march cannot make a silhouette. It moves the texel a ray lands on, so a cobbled street
reads as cobbles from above and as a painted plane the moment the camera drops to eye
level and looks along it — which, this being an adventure game about walking down streets,
is most of the time anybody looks at one. So on a floor the relief is cut into the geometry
as well. `ReliefPlan` does it, at load, in `SceneGeometry.AddScene`.

**Which surfaces.** Every scene's general `.SIF` names one `floor=` object and the BSP
knows which surfaces belong to it — the same chain the walk height query follows — and of
those, the ones whose material says `displaced`. That flag is not on by default and must
not be: every texture in the game has a generated height field, and for most of them the
field is not relief at all. A lawn's is blades and a rug's is pile. 615 of the 6,657
materials carry it, 65 of the 126 textures that are on a floor somewhere.

**There is nothing to displace.** The game's floors are enormous flat triangles: PL6's
stretch of road is 96 of them over 1.15 million square units, an average triangle four
metres across. So the floor is subdivided first, and everything awkward follows from having
to do that without opening a crack.

**The cut is a lattice in texture space**, not a subdivision of the triangle. Each triangle
is clipped against a grid of lines at fixed texture coordinates and every piece that falls
out is one cell. Two triangles that share an edge share its texture coordinates, so the
lattice crosses that edge in the same places from both sides — there is no per-triangle
subdivision level for neighbours to disagree about, and nothing to stitch. It is also what
makes the cells the size they need to be: an N by N barycentric grid takes N from a
triangle's longest edge, so a long thin strip of road comes out cut far finer across than
along, and measured over the corpus that wastes between two and four triangles in five.

**How far one unit of texture coordinate goes** is the number the whole lattice hangs on,
and it is one number per texture, so that two triangles either side of an edge agree about
where the lines fall. It is the **area-weighted median** of the triangles' own rates and
not their mean, because a mean is what one stray triangle poisons: `rc1Coblston` is laid at
a clean 120 units to the texture over the whole village square, and a handful of triangles
whose coordinates are all but collapsed carried the average to 42,641. Every cobble then
asked for a lattice a thousand times too fine, was refused as impossible, and came out
flat. A triangle whose own rate is still more than three times from the median after that
is left uncut and its neighbours are held against it — 30 of RC1's 3,050, and none at all
in most rooms.

**The cell is bought with a budget** rather than fixed. A million triangles a room, and the
cell is whatever spends them: the hotel lobby's 450,000 square units of tile come out at
the two-unit floor and RC1's village at 4.4, with nobody tuning a scene. Measured on RC1,
which is the largest paved area in the game: 400,000 buys 7.5 units a cell, a million buys
4.4, four million buys 2, and the frame rate is 150 either way — what it costs is about a
second of that room's load. Four units a cell is about a third of a cobble, which is where
a paved street stops reading as a painted plane.

The budget is only a budget if the estimate is honest, and for a year it was not: RC1 came
out at 1,107,726 triangles against an estimate of 392,407. Two things were wrong and both
are worth knowing. The area term used the texture's average stretch, so triangles tiled
finer than the average were cut into cells smaller than the one the budget bought. And the
ragged-edge term was a perimeter over the cell size, which is right for a triangle lying
square to the lattice and half the answer for one lying across it — a long thin strip of
road at an angle steps through a line of the lattice in both directions at once. Both are
counted per triangle in texture space now, and the estimate lands within about 15% on every
room in the corpus.

**What does not move.** Where the floor *stops* — at a wall, at a kerb, or at the next
texture along, whose lattice is its own — stays exactly where the 1999 geometry put it, and
the displacement fades in over the first cell. So do the *corners* of such an edge, in every
triangle, not only in the ones that own it: pinning edges alone leaves a pinhole where a
boundary corner is also a corner of the triangle behind it, and that triangle has no reason
not to lift it. The fade from a held corner is measured as a **distance**, not as a share of
the triangle, because a barycentric weight is only a distance when the triangle is roughly
equilateral and a village's ground is mostly long thin strips.

**Where the floor stops is a geometric question, not a combinatorial one**, and getting that
wrong is what kept the village flat. GK3's ground is laid as separate flat patches that abut
without being welded — the street against the square, the square against the verge — and a
stitch of stairs or a doorway leaves a long edge with a vertex partway along it. All of
those are used by one triangle and none of them is a boundary. Measured: 2,201 of RC1's
2,674 once-used edges have more floor of the same texture lying against them, and holding
all of them down left nine tenths of the relief unbuilt — the village moved 0.32 units where
it should have moved 1.42, which is the difference between cobbles and a painting of
cobbles. So an edge used once is tested: a point three quarters of a unit past it, in the
surface, is looked up in a grid of the floor's triangles, and if a triangle of the same
texture contains it the surface carries on and the edge is free. Nothing has to be welded
for that to be safe, because the lattice is what makes the two sides agree, not the vertices.

**The field is averaged over a cell** before a vertex moves, and the shader's march keeps a
quarter of the depth on a displaced batch. Geometry can only carry relief coarser than its
own cells; the finer part is what is left for the march and the normal map. Displacing at
full depth and marching at full depth counts the same bump twice. A displaced surface is
its own batch for exactly this reason — `CONCRETE` is on CSE's forecourt and on its walls,
and only the forecourt moved.

**Rays see the cut-up floor**, so a cobble shadows the gutter beside it, which is most of
what moving the vertices was for. `ReliefSettings.Trace` turns that off and leaves the flat
triangles in the acceleration structure.

**The walk floor does not move.** `WalkFloor` builds its height query from the same BSP
object and is deliberately left reading the undisplaced geometry: the relief is a couple of
centimetres and an actor who bobbed over every cobble would be a worse picture than one
whose feet clip them.

**It says how far it moved.** The loader reports the cut as `floor cut into N triangles at
C units a cell, moved up to X units (Y typically), P edges held down and Q carried on
(expected E)`. Every number there but X and Y reads the same whether the floor moved or not,
which is exactly how this shipped flat twice — once from height maps that were never loaded,
once from the fade. A typical move well under half the material's `heightDepth` means
something is holding the floor down.

`--relief N` sets the budget for a run of the game and `--relief 0` displaces nothing, which
is how the two are compared in a screenshot; `render-scene --no-relief` is the same switch
for the tool.

### Round things

A bell, a lamp, a vase and an urn are lathes of eight or twelve sides, and at the distance
an adventure game stands you from them that reads as a polygon. `ObjectRounding` rounds them
off at load, in `SceneGeometry.AddScene`, on a curated list of names —
`SceneGeometry.RoundNames`: bell, lamp, lantern, candle, chandel, vase, urn. A curated list
rather than a measurement, because curvature could be estimated and would then round off
things whose faceting is the point, a cut gem or a timber beam. Adding an object is one name
in that array. Objects over 500 authored triangles are left alone, so a "lamp" that is
really a street of lampposts stays as authored.

**The whole object is welded first, across its surfaces.** A lathed object is strips and
caps, and the rim between a bell's side and its top belongs to two of them; anything that
refines one surface at a time sees that rim as a boundary to hold still, and the hexagon
survives any amount of subdivision. Texture coordinates stay per corner, so a seam where two
textures meet is still a seam.

**PN triangles, not Loop.** Loop was tried and wrecked what it touched: a lamp shade's
panels sagged inward between their ribs and its rim came out spiked. It is an *approximating*
scheme — every original vertex moves toward the average of its neighbours — which is
invisible on a dense mesh and is the whole shape on a twelve-sided shade. PN triangles
interpolate: every authored vertex stays exactly where it is and the surface between them is
a cubic patch whose shape comes from the corner normals. It cannot sag, because there is no
averaging in it. Two levels, so sixteen pieces per authored triangle.

**The normals stop at creases.** Faces are gathered into smoothing groups across the edges
they meet gently at, and a position carries one normal per group rather than one in all — a
bell's rim shaded as though the metal turned over smoothly there was half of what made the
first attempt look wrong. An edge between two groups is a crease: it stays straight, and both
of its sides work that out identically, so a crease cannot open a crack. The same rule is
what keeps a flat cap flat, since a normal perpendicular to an edge asks for no curvature at
all.

**The threshold is 60°, which is not the usual figure** and is chosen against this
population rather than in general. A lathe of twelve sides turns 30° at each, of eight 45°,
of six 60°. At the usual 40° the reception bell, which is eight-sided, was creased at every
one of its own sides and came out exactly as faceted as it went in. What must still crease
is where a lathe meets its own cap or foot, and those are 70° and over.

**A rim is curved along itself.** A crease is straight *across*; it does not follow that it
is straight *along*. The rim where a bell's dome meets its foot is an octagon, and that
octagon is the widest part of the bell and therefore its entire silhouette — curving the
surface either side of it and leaving it an octagon rounds everything the eye does not look
at. So a rim is treated as a polyline and given the tangent a Catmull-Rom spline would.
Only where it has exactly two rim edges at a vertex, which a box corner does not, and only
where it turns more gently than the crease angle, which a rectangular panel's corner does
not.

**The rounded triangles go to the ray tracer too**, so an object's shadow has the silhouette
the object has.

`--round N` sets the number of levels for a run and `--round 0` leaves the authored shape
with only the crease-aware shading, which is how the two are compared in a screenshot. The
loader reports `Rounded: N triangles from <names>`; an empty list where one was expected
means no name matched, which is otherwise silent.

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

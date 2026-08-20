# Ray tracing

Four quality levels, as the settings screen will expose them:

| level | shadowed lights | rays per shadow | occlusion rays | occlusion radius | bake weight |
| --- | --- | --- | --- | --- | --- |
| None | – | – | – | – | 1.0 |
| Low | 8 | 1 | – | – | 0.6 |
| Medium | 16 | 1 | 4 | 45 | 0.5 |
| High | 32 | 2 | 8 | 45 | 0.35 |

```bash
GK3Reborn --scene LBY --rt high --data <GK3>/Data     # F2 cycles the levels live
GK3Reborn.Tools render-scene --model LBY --rt med ... # a still, headless
```

Each level is a superset of the one below, so turning the setting up never removes
a lighting effect. Changing it costs nothing at runtime: both pipelines exist from
startup, and Low, Medium and High differ only in numbers the shader reads from a
uniform.

## What is actually traced

Inline ray query in the fragment shader, not a ray-tracing pipeline. Visibility is
needed exactly where the shading happens, and a ray query computes it there
without a second pass or a shader binding table. It requires
`VK_KHR_acceleration_structure`, `VK_KHR_ray_query` and
`VK_KHR_deferred_host_operations`; a device without them gets the raster pipeline
and the setting reports as unavailable.

**Shadows.** One ray from the shaded point to each of the first N lights, N being
the level's budget. Lights past that still light the scene, unshadowed, so turning
the setting down softens a scene rather than changing which lights are in it. The
rig is sorted by intensity times reach, so the shadowed ones are the ones that
matter rather than the ones the artist happened to place first.

**Soft shadows.** At High, each shadow ray is jittered across the light's own
authored emitter radius — a two-unit bulb and a twenty-unit window then behave
differently, using a number that was already in the data.

**Occlusion.** Cosine-weighted hemisphere rays out to forty-five units — a little
over a metre — modulating the indirect term only. Direct light is already shadowed;
applying occlusion to it as well would double-count. The radius is the same at every
level because it describes the effect rather than the budget: it is the scale at
which a surface counts as being in a corner, and only the ray count changes with
quality. It was ninety at Medium and a hundred and forty at High, which is most of a
room across, so occlusion sat low everywhere instead of gathering in corners and took
the whole indirect term down with it.

Both the occlusion and the soft-shadow rays are stratified — elevation stepped once
through the hemisphere, azimuth advanced by the golden angle, the pair rotated by a
per-pixel random value — rather than drawn independently. At eight rays that is most
of the difference between a smooth term and a visibly grainy one, and it costs
nothing. The random value comes from the pixel rather than the world position: scene
coordinates run into the hundreds, and a sine of a number that large loses enough
precision to band into patterns across a wall.

## What is not traced yet

**Indirect light.** There is no gathered bounce. Getting one needs the material at
the hit point — the texture, its coordinate, the surface colour — and a ray query
returns a hit, a distance and a primitive index, nothing else. Supplying the rest
means vertex and material buffers reachable by address and an index from primitive
to material, which is a larger change than the shading itself.

Until then the baked lightmaps stand in as the indirect term, scaled down and
weighted less as quality rises. This double counts the direct light the bake also
contains, which is exactly why it is scaled rather than used whole. It is also why
the answer to "do the lightmaps become unnecessary" is *not yet*: they stop being
the lighting at Low and above, but they are still supplying the bounce.

**Alpha-tested geometry.** Windows, railings and foliage are keyed on magenta and
are left out of the acceleration structure entirely, so they cast no shadow.
Including them would need an any-hit shader to decide per hit whether a texel is a
hole, and therefore a full ray-tracing pipeline. A missing shadow under a window
reads as bright; a solid rectangle of shadow from a pane of glass reads as a bug.

**Light fittings.** Also left out, and for a related reason. The rig puts its
emitters where the bulb is — inside the shade, behind the pane, under the sconce —
because the 1999 bake never traced a fitting against its own light. Tracing it now
seals each of those lights inside its fixture, and a room lit only by lamps goes
dark. The surfaces are marked in the data: bit 16 of a BSP surface's flags is the
light fittings, bit 8 the surfaces the bake did not light at all, and bit 64 the
translucent shadow decals. None of the three occludes.

**Reflections.** Nothing yet.

## The acceleration structure

One bottom-level structure over every opaque triangle in world space, and one
top-level structure holding a single untransformed instance of it.

Per-object instances with their own transforms would be the general answer, and
GK3 does not need it: the largest scene is under thirty thousand triangles and
nothing moves once a scene is loaded. Instancing would cost several hundred more
device allocations, of which drivers guarantee only a few thousand in total. Moving
props will need instances and a rebuild policy; neither exists yet, and adding
them before anything moves would be guessing at the requirements.

The structure is built whenever the device supports it, including at quality None,
because Vulkan requires every statically used binding to be valid whether its
branch runs or not.

## Tuning, and what it costs

Two departures from the stored values, both visible:

**Falloff is squared, not linear.** The authored range is respected either way,
but a linear ramp spreads a lamp evenly across a whole room and the result is
flatter than the bake it replaces. Squaring concentrates it near the source, which
is both closer to how light behaves and closer to what the artists' own bakes look
like.

That is a guess that produces a better picture, not a measurement. ADR 0006 expects
this: the 1999 values were tuned for a renderer with no exposure control, and every
light stays correctable through the edit layer.

**A light with attenuation switched off has none.** The stored end distance is still
in the file and means nothing once the switch is off, so it is ignored rather than
used as a soft limit. R25's key light for the afternoon is the sun, fifty thousand
units from the room, with a stored range of two hundred; honouring that range deleted
the daylight from every room with a window in it.

Cost, measured at 1920×1080 in the hotel lobby (41 lights, 10,704 opaque triangles)
on an RTX 5090, as the difference from quality None: roughly 10 ms at Low, 30 ms at
Medium and High. That is more than the scene's size suggests, because every pixel
loops over every light in the rig before deciding which ones to trace against.
Light culling — clustered or tiled — is the obvious next optimisation and would cut
most of it.

In-window frame rate is presentation-limited on this machine and therefore says
nothing about GPU cost; the figures above come from the headless path.

## Things that move

The acceleration structure is built from the room **and one part for each placed model**,
each model's triangles held in the model's own space and put in place by an instance
transform. Moving something is that one transform, and the top level is rebuilt — not
refitted, which is only sound for small movements and a character crossing a room is not
one. R25 holds twelve parts, the room and its eleven models; RC1 holds seven.

It used to be one structure with everything baked into world space at load, which meant a
shadow stayed wherever its owner was standing when the room loaded. Walking left it behind;
and outdoors, where `SCENE:ENTER` stands the player at the door they came in through *after*
the geometry is built, it was stranded at the actor's authored spot from the moment the room
appeared.

**Posing is not reflected.** A part is a whole model, so a character walking takes their
shadow with them, but a clip that moves one mesh — a turned head, a swinging arm — casts the
shadow of the rest pose. Per-mesh parts would fix it and would multiply the part count by
about thirteen.

## Noise that sits still

The sampling noise is seeded from the pixel and nothing else. It used to take a frame
counter as well, which is what lets a temporal filter average noise away — and there is no
temporal filter, so the grain simply changed every frame. Measured on a still room at High,
**15.3% of the picture moved by more than a step of an eight-bit channel between one frame
and the next, with nothing in the room moving at all**. Locked to the pixel that is 0.00%.

High showed it worse than Medium because High leans less on the bake — 0.35 against 0.5 —
so more of what you see comes from the sampled terms.

The trade is that the grain is now a dither pattern fixed to the screen rather than
something that averages out over time. That is the right way round until something
accumulates frames; a temporal filter would want the counter back.

## Where a shadow ray starts

A ray's minimum distance is measured **along the ray**, so it clears the surface only in
proportion to the angle it leaves at. A ray leaving at a grazing angle — which is every ray
on a curved surface turned away from the light — is still within a hair of the surface after
it, and hits the surface it started on. A wall has few such angles. A face has little else.

That is what covered Gabriel in black speckle while the room around him stayed clean, and
what read as a face full of smudges of dirt.

So the start is lifted along the surface normal as well. It has to clear not only the
surface but the gap between the smooth normal a low-polygon character is shaded with and the
flat triangle the ray actually starts on, which on a face is most of the error. Measured as
local contrast over Gabriel in the lobby, against the room's own texture detail as a floor:

| normal bias | on Gabriel | on the room |
|---|---|---|
| none | 12.36 | 8.50 |
| 2.5 units | 9.25 | 7.90 |
| **6 units** | **6.78** | 8.07 |
| 12 units | 6.10 | 8.06 |

Six units is where it flattens, and it puts the character below the room's own detail rather
than above it. Twelve buys almost nothing and lifts a shadow further from whatever casts it.
Six units is about fifteen centimetres, which is a lot in the abstract and not much beside a
character seventy-six units tall in a room three hundred and seventy across.

## What the frame writes besides the picture

Anything that filters a ray-traced signal over time needs to know where each pixel's
surface was on the previous frame. The mesh pass therefore writes three colour targets
rather than one, and keeps its depth instead of discarding it:

| Target | Format | Holds |
| --- | --- | --- |
| 0 | the swapchain's | the picture |
| 1 | `R16G16B16A16_SFLOAT` | the shading normal, in world space |
| 2 | `R16G16_SFLOAT` | how far this pixel's surface moved, in pixels |
| depth | `D32_SFLOAT` | now stored and sampleable |

The skybox, overlay and triangle pipelines declare all three attachments — a pipeline has
to describe every one the frame has — and mask the two new ones off, so the sky and the
interface leave a zero motion behind them, which is the truth about them.

A motion vector points **backwards**: it is the offset from this pixel to where the same
surface was, because that is the direction anything reading it wants ("the pixel I want
from the last frame is this far away").

### Getting it right

Two vertex streams are bound for every batch: this frame's pose and the one before it. A
batch nothing animates binds the same buffer twice, so its motion comes out as its
transform's alone; an animated character binds the previous pose, so a figure standing
still while it gestures still reports its arm as having moved. Animated batches keep one
buffer per frame in flight *and one more*, because the frame still in flight is reading
the pose before the one being written.

Where the fragment is now comes from `gl_FragCoord`, not from its own interpolated clip
position. The two agree to within a rounding error, but on distant geometry clip
coordinates are large enough that subtracting two of them leaves nothing but that error.

Both new targets are written before anything in the fragment shader can return. A
fragment that leaves an output alone does not leave it cleared — it leaves it
**undefined**. The self-lit early return (a bulb, a lampshade, the painted street through
the hotel window) skipped both, and every lamp in the frame reported itself as having
crossed the screen since the last frame.

### Checking it

`--motion` reports the field and dumps it to `motion.raw`, eight bits a pixel at the
viewport's size. A motion vector is not visible in the picture and is wrong in ways that
look entirely plausible, so the numbers are the only honest check. Measured at
1280×720:

| | mean | largest | moved over half a pixel |
| --- | --- | --- | --- |
| `R25` still | 0.00 px | 0.0 px | 0.0% |
| `MOP` still | 0.00 px | 0.0 px | 0.0% |
| `R25`, Gabriel walking | 0.18 px | 2.4 px | 8.9% |
| `R25`, camera gliding | 1.21 px | 3.2 px | 92.7% |

A still frame is exactly zero everywhere, a walking character is the only thing moving in
a still room, and a glide moves very nearly all of it. Every earlier version of this
passed the eye and failed these four numbers.

## Denoising

Ray-traced occlusion is now traced once a pixel, in a compute pass of its own, and
filtered rather than averaged on the spot. The filter is a port of AMD's FidelityFX
denoiser (SDK 1.1.4, MIT); `DenoiserShaders` lists what changed in the port and why.

### Rays across time, and rays within a frame

The mesh shader used to trace several rays per light per pixel and average them on the
spot, with nothing averaging across frames — so the seed had to stay pinned to the pixel
or the grain crawled, and the result was a dither pattern locked to the screen. That is
what read as dirt on Gabriel's face.

Averaging across frames is what fixes that, and it is what the filter is for. It does not
follow that one ray a frame is enough. A single ray is an unbiased estimate of the
fraction and a terrible one — its error is half, on every pixel, every frame — and only a
long history hides that. **Anything that moves has no long history**: a walking character
is uncovering new pixels and deforming under the ones it keeps, and a camera that is
moving does the same to the whole frame. With one ray those surfaces showed the bit they
drew rather than an average of anything.

So the trace spends the quality level's ray budget within the frame as well: eight rays a
pixel at High, four at Medium, for each of the two signals. The bitmask the filter chain
is built on still gets one bit — the tile classification reads a whole tile as one word
and has to — but the estimate itself reads a *fraction* written alongside it. Eight rays
cut the per-frame error by two thirds before the filter sees it, and the filter then does
what it is good at.

Two things in the reprojection also had to be corrected before a moving surface could keep
any history at all:

- The damper that reduces the sample count when history disagrees with the local
  neighbourhood divides by the neighbourhood's standard deviation, with a floor of 0.001
  under it. That floor stops a divide by zero and does nothing about a divide by *nearly*
  zero — and most of a character is a uniform neighbourhood, which sent the count to zero
  every frame. The floor is a thousand times larger now, and the count never falls below
  the one sample the pixel did take.
A pixel with no history has only what its own rays found, and with eight of them that is
worth something. It must **not** fall back on the seventeen-by-seventeen neighbourhood the
pass computes for its clamping bounds: that is a flat Gaussian over the bitmask with no
regard for depth or normal, so beside a window it mixes wall with the fully-lit pixels an
undrawn sky is written as. Trying it that way painted a glow around R25's window frame
whenever the camera moved, which faded as soon as it stopped. The three filtering stages
that follow blur with edges in mind, which is what they are for.

### What is denoised

Not a sun's shadow — a GK3 room has a rig of lamps and any of them may be behind a wall,
so there is no single light whose visibility means anything on its own. Each pixel picks
one light with a probability proportional to what that light contributes to it, and traces
one ray at it. The answer is one bit, and its expected value is exactly the fraction of
the direct light that arrives, because sampling by contribution weights each light the way
the shading already does. Occlusion rides the same five stages with its own copy of the
buffers: one cosine-weighted ray, one bit, one fraction.

### What that costs the frame

The mesh pass can no longer finish a pixel, because neither occlusion term exists until
its own depth and normals have been traced against. So it writes the two halves of the
lighting to separate targets — the bake and the ambient in one, the rig's light in the
other, neither occluded — and a pass afterwards multiplies each by its term and adds them.
The sky and the interface moved into that second pass, on top of the picture rather than
underneath it. Nothing changes when ray tracing is off: that path still draws straight
into the swapchain in one scope.

| | before | after |
| --- | --- | --- |
| rays a pixel | up to 8 per light, plus 8 for occlusion | 8 and 8 at High, 4 and 4 at Medium |
| `R25` at High | 156 fps | 155 fps |
| `R25` at High, walking | — | 160 fps |
| `MOP` at High | — | 159 fps |

### Characters, and how much occlusion to believe

Two things had to change before people looked right.

The acceleration structure used to hold the pose each model was authored in. A GK3
character has no skeleton — an `.ACT` clip rewrites its vertices outright, every frame —
and only the transform was being handed to the structure, so a ray leaving an animated
shoulder started inside a body still standing at rest. Posed vertices now go to the
structure as well: everything but the room keeps its vertices where the host can write
them, and a part whose shape changed is rebuilt in the same submission as the top level.
The room, which is most of the triangles and never changes shape, stays device-local and
is built once.

Occlusion is also applied at a little over half strength rather than whole. These rooms
ship with lightmaps that were baked with occlusion already in them, so a hemisphere of
rays measures something the bake has largely accounted for, and counting it twice drives
surfaces to black: enough of the hemisphere above a shoulder is that person's own head
that the shoulder disappears. What is worth keeping is the near contact the bake is too
coarse to hold — the seam where an arm meets a body, the line under a table.

## The bake, and what the rig is allowed to replace

A GK3 room ships with lightmaps, and they contain two different things: the light these
same lamps threw in 1999, which is now being computed afresh and would otherwise be
counted twice, and light from sources the rig has not got — daylight through a window,
sky, the bounce off a wall.

This used to be handled by scaling the whole bake down, by a weight that fell as quality
rose. That throws the second away along with the first. Measured on R25's window at High:
the bake alone gives that area a mean of 71 and the room 85; scaling it to 0.35 gave 50
and 51, and the daylight the artists painted around the window was simply gone.

The compositing pass now subtracts instead, and needs no weight chosen:

    arrived  = direct * shadow
    residual = max(bake - arrived, 0)
    lit      = residual * occlusion + arrived

Where the rig explains the bake the residual falls to nothing and the picture is ray
traced outright, shadows and all. Where it explains none of it — a window with no light
behind it, a corner lit only by bounce — the bake survives whole. R25's window comes back
to 56 and the room to 59.

It is the **arrived** light that is subtracted, not what the rig would give with nothing
in the way. That distinction is the whole of it: R25's rig has sixty-three lights, and
from inside one hotel room a great many of them are in the rooms next door. They
contribute on paper and are stopped by a wall in fact, so subtracting the unshadowed term
takes out light the bake never had — tried that way, the room fell to 32.

`RayTracingSettings.LightmapIndirect` is no longer read while tracing.

### What this does not reach

A surface with no lightmap has no bake to fall back on: models are lit by the rig alone,
and the rig's light is now shadowed where it was not before, so a wardrobe or a plant is
darker than the 1999 picture even though the wall behind it matches. Fixing that properly
means light probes — somewhere for a model to ask what the room around it is lit like —
and nothing here has one.

## Reflections

The church has a tiled floor, the hotel has marble, and until now every surface in the
game was equally matte. The marching is AMD's, from FidelityFX SSSR in the same 1.1.4
release: the hierarchical walk over a min-depth pyramid, the plane intersections that
advance it, the visible-normal sampling that gives a rough surface a wider cone than a
polished one, and the checks that decide a hit is real. `ReflectionShaders` lists what was
left out — their tile classification, indirect dispatch, blue-noise sampler and their own
reflection denoiser, all of which buy throughput on scenes far heavier than these rooms.

### Where roughness comes from

GK3 has no material data: every surface is a diffuse texture and, sometimes, a lightmap.
The workspace's material pass infers roughness and specular reflectance for all 6,657
textures and writes them to `manifests/material-library.json`; 1,456 come out smooth enough
to be worth a ray. `SurfaceFinishes` reads it once and the mesh pass writes each surface's
roughness into the alpha of the frame's normals, which nothing else was using. A texture
the library has never heard of is matte and costs nothing.

### Two things the port had to be told about GK3

Both were invisible in the picture and both made reflections vanish entirely.

**The ray has to be given at the right scale.** AMD form the screen-space ray from the
projection of a point one unit along it. In a game measured in metres that is a good part
of a room; GK3 measures a hotel room at about a thousand units, so one unit projects to a
few millionths of the screen and every subtraction that follows is rounding error. A
world-space line stays a line under projection — normalised depth varies linearly along it
as well — so the ray can be given by any two points on it, and this goes as far along it as
the camera is away, stopping short of the near plane.

**The ray has to start on the level it marches.** The origin's depth is read from the
pyramid at the most detailed mip the march will use, not from the depth buffer. A level of
the pyramid holds the nearest of the pixels under it, so starting there puts the ray just
in front of its own surface. Starting at the pixel's own depth puts it exactly on it, the
first test says it is not above anything, and the march ends where it began.

The depth-thickness tolerance also had to grow from AMD's default to 250 units, for the
same reason of scale: a two-by-two pixel neighbourhood on a receding floor spans more than
twenty units of view space, so almost every hit read as being behind the surface it landed
on.

### What is added to the picture

A reflection arrives already weighted, so the compositing pass adds it whole:

| | |
| --- | --- |
| the marcher's confidence | how much of the ray it could follow, fading at the frame's edges |
| Schlick's term | a base reflectance of 0.16 — generous for a dielectric, and what makes the difference between polish and haze |
| a roughness falloff | the root of the distance to the threshold, so a floor and the wall beside it do not differ by a hard line |

Reflections read the previous frame's finished picture, reprojected through the motion
vectors: reading this frame's is not possible, since it is what the reflection is being
added to. That picture holds the sky, so a floor can reflect it, but not the interface,
which is drawn straight onto the screen after the copy so that it never appears underfoot.

| | before | after |
| --- | --- | --- |
| `CHU` at High | 162 fps | 152 fps |
| `R25` at High | 163 fps | 161 fps |
| `MOP` at High | 159 fps | 159 fps |

Measured on the church floor, reflections change it by a mean of 0.47 of an eight-bit step
— visible as the pews mirrored under them, and nothing like a mirror.

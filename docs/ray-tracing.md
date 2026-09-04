# Ray tracing

Four quality levels, as the settings screen will expose them:

| level | shadowed lights | rays per shadow | occlusion rays | occlusion radius | bake | occlusion believed |
| --- | --- | --- | --- | --- | --- | --- |
| None | – | – | – | – | yes | – |
| Low | 8 | 1 | – | – | yes, 0.6 | – |
| Medium | 16 | 1 | 4 | 45 | **no** | 0.85 |
| High | 32 | 2 | 8 | 45 | **no** | 0.85 |

**Medium and High use no baked lightmaps.** They light a room from the artists'
own rig and nothing else, which is what `Plan/04` P10 asks for — "the RT and
enhanced tiers light scenes from the rig, the compatibility tier keeps baked
lightmaps" — and what ADR 0006 means by re-lighting for modern range rather than
reproducing the 1999 output. None and Low keep the bake: None is the compatibility
tier, and Low is for hardware that can trace a few shadow rays and no more, which
is exactly the case a bake is still the better answer for.

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

At Low the baked lightmaps stand in as the indirect term, scaled down because they
double count the direct light they also contain. At Medium and High there is no
bake, and what stands in for bounce is two things: an ambient floor that traced
occlusion eats into, and the rig's own bounce lights. Those are not a fiction — 125
`sky_bounce` and 169 `ground_bounce` entries across the corpus are the artists'
answer to what the walls and floor throw back, and `ground_bounce` is the most
common light name in the game. Evaluating the rig in full evaluates their bounce
approximation along with their key light.

**And the bake still says where the light goes.** It is not allowed to be the
lighting at these tiers; it is allowed to shape what is. Dropping all of it
flattened the dining room — the wall sconces went dark, the tablecloths turned
from cream to grey, and a room with 42 authored lights in it had almost nothing
that read as a shadow, because there was nothing for a shadow to be darker than.
So the ambient term is multiplied by `0.30 + 3.0 x baked` where a lightmap
exists. Nothing is added: the term stays ambient, stays subject to traced
occlusion, and is still never subtracted against. What it gains is the room's
shape and colour, bright where the artists put brightness and warm where they
painted warmth.

That is also why the flat part of the ambient floor is small — 0.15/0.16/0.17 at
these tiers. A large uniform wash is what drowned the rig's direct light and made
the shadows subtle; most of the ambient a lit surface gets should come from the
shape, not from the floor.

It is still an approximation and a gathered bounce would be better. What it is not
is a regression: measured below, an interior lands within 3% of the bake.

**What this does not reach is models.** A prop or a character has no lightmap, so
it gets the flat floor and no shape — which is why the dining room's tablecloths
read greyer than the bake's cream. Light probes are the fix and nothing here has
one.

**Alpha-tested geometry.** Windows, railings and foliage are keyed on magenta and
are left out of the acceleration structure entirely: including them as they are
would need an any-hit shader to decide per hit whether a texel is a hole, and
therefore a full ray-tracing pipeline. A missing shadow under a window reads as
bright; a solid rectangle of shadow from a pane of glass reads as a bug.

**The railings and fences are the exception, since 2026-09-02**, and they get
round it rather than solving it. The thickening pass has already decoded and
measured their alpha, so the test the missing shader would do per hit is done once
at load instead: the drawn texels are merged into rectangles and each becomes two
*opaque* triangles lying on the card's own plane. The card is still keyed and still
out of the structure; what is in it is a second, opaque, never-drawn copy of the
silhouette. See [cutout-cards.md](cutout-cards.md#the-shadow-added-2026-09-02).
Windows and foliage still cast nothing — a pane of glass has no silhouette to
build, and the trees are replaced by modelled geometry that is already opaque.

These occluders are **not** in the room's half of the structure. The composite
credits room occlusion against the bake and the two cancel; a 1999 bake cast no
alpha-tested rays either, so a keyed card's shadow is not in the lightmap to be
double-counted. They carry `TracedWorld.UnbakedMask`, in a part of their own, and
are traced with the models. Measured on the lobby stairs, whose lightmap is the
whole of the light in the room: the room's mask changes 0.06% of the frame against
0.16%, and reaches ten steps of an eight-bit channel against thirty-four.

**Light fittings.** Also left out, and for a related reason. The rig puts its
emitters where the bulb is — inside the shade, behind the pane, under the sconce —
because the 1999 bake never traced a fitting against its own light. Tracing it now
seals each of those lights inside its fixture, and a room lit only by lamps goes
dark. The surfaces are marked in the data: bit 16 of a BSP surface's flags is the
light fittings, bit 8 the surfaces the bake did not light at all, and bit 64 the
translucent shadow decals. None of the three occludes.

**Reflections.** Screen-space, from AMD's SSSR, plus a planar pass for the mirrors — which
a screen-space march cannot reach. See [Reflections](#reflections) below.

## The acceleration structure

One bottom-level structure per *part*, and one top-level structure holding an
instance of each. Part zero is the room, built once in world space and never moved.
Parts one upwards are the models placed in it, each built in the model's own space
and placed by its instance transform — which is what makes a walking character a
rewritten transform rather than ten thousand rewritten vertices, and what `Move`
and `SetTraced` are called with.

Part -1 is the room's keyed cards, added 2026-09-02. It is the room's own geometry
and never moves, so it needs no number in the placement sequence; it is a part of
its own only because it must carry a different instance mask and a different facing
rule from the rest of the room. See below and
[cutout-cards.md](cutout-cards.md#the-shadow-added-2026-09-02).

`TracedWorld` is the one statement of what each part carries, because both backends
read it and a backend that disagreed would not fail — the trace stages would go on
asking for the room and be handed the characters as well, which reads as a
character standing in their own shadow rather than as a mask nobody set.

| part | mask | faces both ways | posable |
|---|---|---|---|
| 0, the room | `WorldMask` 0x01 | yes — a BSP has no consistent winding | no |
| 1…, a model | `ModelMask` 0x02 | no — the winding is what lets a character shadow itself | yes |
| -1, keyed cards | `UnbakedMask` 0x04 | yes | no |

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

**A stored range is honoured whether or not the switch is on.** 3ds Max's far
attenuation being off means the light had no decay while the scene was being baked,
and reproducing that at runtime is faithful and unusable: a light with no falloff
lights every surface it can see equally, so a rig's fill lights become a flat wash
with no source anywhere in the room.

The hotel lobby is the case that showed it. Of the light arriving at the middle of its
floor, **82% came from lights with the switch off** — one of them 842 units outside
the room — every one at full strength whatever the distance. It reads exactly as it
is: a floor lit from nowhere.

The ranges are in the file and they are the artists' own. All fourteen of the lobby's
switched-off lights carry a full near and far pair — 10 to 77, 33 to 66, 164 to 221 —
set by hand and then disabled, which is a normal way to work in Max and leaves the
intent behind in the file. A light that states **no** range still has none: there is
nothing to honour, and no ramp is invented across the unlimited range either.

This used to ignore the stored range, because honouring it switched off R25's
afternoon sun — fifty thousand units away with a range of two hundred. That no longer
costs anything, and the reason is the compositing pass: the bake carries the daylight
now and the rig only has to explain what it can. Measured over ten scenes at `--rt
high`:

| | unchanged | darker |
| --- | --- | --- |
| scenes | R25, MS3, B25, LHM (±0.2%), RC1 (−1.2%), CHU (+1.1%) | HAL −19%, LBY −32%, CS3 −35%, DIN −46% |

R25's window view is identical in its brightest tenth to the pixel. The four that
darken stop being washed and start looking lamp-lit.

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

Occlusion is also applied at less than full strength, and how much less is the tier's
decision — `RayTracingSettings.OcclusionStrength`, pushed to the compositing pass.

Never all of it, at any tier: whole, it drives surfaces to black, because enough of the
hemisphere above a shoulder is that person's own head that the shoulder disappears. What
is worth keeping is the near contact nothing else holds — the seam where an arm meets a
body, the line under a table, the ground a chair leg stands on.

Where a bake is in play there is a second reason to hold it back, which is that these
lightmaps were baked with occlusion already in them, so a hemisphere of rays measures
something the bake has largely accounted for. That is why Low believes 0.55 of it.
Medium and High have no bake to count twice against and believe 0.85 — which is worth
7 points of `RC1`'s mean on its own, and is what puts a chair leg on the floor rather
than above it.

### Measuring a flicker

A temporal filter's failures do not show in a screenshot. `--flicker` reports the mean
absolute change between consecutive frames and writes the last pair's difference to
`flicker.raw`, and it is the only instrument that finds these. With ray tracing off and
nothing moving it reads **0.000**, which makes everything above that attributable.

Reported as the hallway's lights fighting when the camera moved, HAL at High measured
3.761 of an eight-bit step between frames with the camera *standing still*. Three separate
faults, none visible in any single frame, and each found by measuring rather than by
reading the code:

**The reprojection clamped a fraction against bounds built from a bitmask.** AMD compute
the local neighbourhood as a seventeen-by-seventeen Gaussian over the mask, because a bit
is all their estimate is. Ours is a fraction of eight rays, and clamping it to
`neighbourhood ± ½σ` of a *majority vote* pinned every uniform region to nought or one —
a wall at six-tenths lit read as fully lit once its history settled and as six-tenths
while the camera moved. The neighbourhood is now a seven-by-seven mean of the fractions
themselves, with a real variance rather than the binary `n − n²`.

**A tile shortcut wrote a hard value with no temporal blending.** A tile whose every bit
is set is fully lit, so AMD write one and skip the filtering. Whether a tile qualifies is
decided afresh from each frame's rays, so a tile at nineteen-twentieths lit qualified on
some frames and not others — a whole eight-by-eight block stepping brighter and back,
every frame. The shortcut is gone; the metadata it wrote is still there for the filters.

**The damper was firing constantly.** It discounts the temporal sample count when the
history disagrees with the neighbourhood, dividing the difference by that neighbourhood's
standard deviation. On a signal as smooth as eight rays make, that deviation is small and
AMD's floor of 0.001 is what it divides by nearly everywhere. Measured: the count was
being multiplied by 0.62 every frame, settling at 1.6, so the blend took almost the whole
fresh sample every frame and the entire picture fizzed. The floor is now 0.4 — well above
the fifth of a unit that eight Bernoulli draws are worth — so only a wholesale change in
what a pixel is looking at discards what it has learned.

AMD also raise the final result to a power that depends on the estimate's confidence, to
recover contrast their blur takes out. That makes a settled pixel darker than a moving one
by construction, because the variance is boosted exactly while a pixel has no history.
Their blur is rescuing one bit a pixel; ours is filtering eight rays, and the contrast it
recovers was never lost, so it is gone.

**And the filter chain was feeding on itself.** The three blurs are meant to alternate
between two scratch images: the reprojection lands in the first, the first blur writes the
second, the second blur writes the first, the third reads it and writes the result. The
wiring had the two outputs the wrong way round, so the first blur read and wrote the same
image while the second read the one nothing had written that frame — its own output from
the frame before. It blurred its own result over and over, decaying towards nothing. That
buffer is also what the reprojection reads back as its history, so every pixel's past was
a thing quietly fading out.

This is what a room starting at the right brightness and going dark over half a second
was, and what made it happen again every time the camera moved and reset the counts. It
was also most of the noise, because a history that is decaying is a history worth nothing.
Measured in HAL: the raw eight-ray fraction reads 0.641 on every frame from the third to
the four-hundredth, while the denoised output fell from 0.609 to 0.239. The rays were
never the problem.

Three more, found in the lobby, where the walls are lit from every direction:

**Low was tracing one ray a pixel.** The ray count had been taken from the
ambient-occlusion budget, which is nought at Low because Low has no ambient occlusion —
so the level meant to be cheapest was estimating every shadow from a single sample and
looked far worse than the one above it. Occlusion samples are their own setting now: four,
six and eight.

**The clamp window was as noisy as what it was clamping.** A reprojected history is held
to the local neighbourhood, plus or minus half its deviation. On a smooth wall that window
is a couple of hundredths wide and both its ends are recomputed from this frame's rays, so
the history was dragged along by the window rather than converging inside it. It is
widened by the same sampling error the damper uses, and for the same reason.

**The rays were drawn independently.** The mesh shader used to stratify them — the
elevation stepping once through the hemisphere, the azimuth advancing by the golden angle
— and that was lost when the tracing moved into a pass of its own. Eight independent draws
over a rig's cumulative brightness clump and leave gaps, and the gaps move every frame,
which is a blotch on a wall that will not sit still. Both the light a shadow ray picks and
the direction an occlusion ray takes are stratified again.

| | flicker, still |
| --- | --- |
| no ray tracing | 0.000 |
| `HAL` at High, as reported | 3.76 |
| `HAL` at High, now | 0.07 |
| `LBY` at High, now | 0.43 |
| `LBY` at Low, now | 0.51 |
| `R25` at High, now | 0.09 |

Brightness no longer depends on how long a pixel has been looked at either: HAL reads 66.0
five frames in and 62.9 four hundred frames in, against 60.3 for the bake alone. Before
the chain was rewired it went from 59.7 to 37.4.

Gliding changes the picture whether or not anything is traced; what matters is the gap
above that baseline, and it is now 0.7 of an eight-bit step.

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

### The room's shadow and the moving one

The visibility above is traced twice, against one half of the acceleration structure each
time: `Shadow` is what the room itself blocks, and `DynamicShadow` is what characters and
props block. They are kept apart because they are spent differently — a bake already
contains the room's own shadows, so only that half may be subtracted against it, while a
person who walked in after 1999 is not in the bake at all and has to be taken off the
result, including off the bake-shaped part of the ambient term.

**The second question is asked only where the first let the light through.** Both calls are
deterministic in the pixel and the sample index, so they pick the same light and the same
point on its emitter: they are two answers about one ray. Asked independently, a person
standing on ground a building shades was blocking a sun that never reached that ground, and
the composite multiplied the bake by their silhouette — a second shadow inside the first,
hard-edged, on ground with no light left on it. Outside the hotel on `RC1` at 110A, where
the building stands between the square and the morning sun, that was Gabriel's full shadow
laid across the doorstep and the door.

`RayTracingSettings.LightmapIndirect` is now the gate as well as the weight. At
Medium and High it is zero, the mesh pass emits the ambient floor in place of a
bake, and every pixel arrives at the compositing pass with the ambient alpha — so
`lightmapped` is nought, `residual` is the ambient floor, and nothing is subtracted
from it. The subtraction above is what Low does with the bake it still has.

### Measured, once the bake was taken away

Mean frame luminance, eight bits, from `render-scene`, which is byte-reproducible:

| scene | None | Low | Medium | High |
| --- | --- | --- | --- | --- |
| `LBY` lobby, `GabEmlWide` | 54.4 | 53.4 | 52.7 | 52.7 |
| `RC1` exterior | 75.8 | 67.9 | 55.7 | 55.7 |

The lobby holds: 3% off the bake, and the share of the frame below an eighth of
full brightness moves from 16.6% to 17.9%. What changes is not the level but the
shape of it — a character now throws a shadow on the wall behind him, which he did
not before, and the sconce's painted-on bloom is gone.

`RC1` is 27% down and that is not tuning, it is the rig: the whole town ships with
**seven** authored lights, against `LBY`'s forty-one, and outdoors the artists left
nearly everything to the bake. The picture is legible and correctly overcast, but a
figure standing in the open casts no contact shadow, because there is no sun in the
rig to cast it. An exterior sun-and-sky light is the fix and is not written.

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

### A floor cannot be marched, and had to be rendered

Reported as "the tile floor in the hotel, the tile floor in the church don't reflect much
at all — they need some reflectivity, ie. see the ceiling in the floor type of thing".

**The march can only return what is already in the frame, and what a floor shows is mostly
what is above the camera**: the ceiling, the beams, the lamps hanging off them. None of
that is ever on screen when the camera is looking down at a floor, so the march finds
nothing, the confidence is nought and a tiled hall reflects nothing however smooth its
material says it is. No amount of tuning fixes that; the information is not in the picture.

So a large flat polished floor goes through the **planar pass the mirrors already use** —
the room rendered a second time from the camera reflected through the plane — and the
screen-space march is not run for it at all. What makes that cheap to sample is the same
property that makes it cheap for a mirror: reflection fixes the plane pointwise, so a point
*on* the plane lands on the same pixel in both renders and may read the reflection at its
own screen position, with no matrix and nothing per-surface passed through.

**Which pixels those are is a geometric test and not a flag.** `ReflectUniforms` carries
the plane; a pixel whose world position is within a unit of it takes the planar answer.
Whatever the floor is made of and whatever batch it came from, "is this pixel on the plane
the reflection was rendered for" is answered by the pixel's own position.

**Finding the plane.** `MirrorSurfaces.Ground` asks which height most of the room's floor is
at, not whether the floor is flat. `MirrorSurfaces.Fit` rejects anything that wanders out of
its own plane, which is right for glass and rejects every floor in the game: the church's is
five textures across a nave, a tiled runner up the middle and a step to the altar. The
answer is a horizontal plane at the commonest height, and the step up to the altar simply is
not on it — which is exactly right, because a floor with a step in it reflects on the lower
level and not on the upper.

Which surfaces are the floor is the room's own answer rather than a guess: the scene file
names its floor object, `KeepRelief` is already told the textures on it, and a floor is a
batch drawn with one of those textures whose material is smooth enough to be worth a
reflection at all.

**One plane a frame, and a mirror wins.** A room with both keeps its mirror, because a
mirror that stops reflecting shows a painted fake of a room that is not there and a floor
that stops reflecting shows a floor. No room in the game has a mirror over a floor polished
enough for this in any case.

**A floor is not drawn as a mirror**, and that is the whole difference between the two. The
batch carrying the mirror flag has its own texture thrown away and the reflection put in its
place; done to a floor, the church's tiles vanished and the room appeared upside down where
they had been. A floor keeps its own surface and has the reflection added over it by the
compositing pass, weighed by the angle it is seen at — which is what a polished floor does
and what a mirror does not.

### What it costs, and the two thirds of a frame that was not the drawing

| `LBY` at High, 1920×1080 | fps |
| --- | --- |
| no floor reflection | 92 |
| the first version | 31 |
| after the plane was cached | 84 |

**Fitting the plane was costing three times what drawing the reflection did.** It walks
every vertex of every polished piece of the floor three times, and a room's floor object
names more than the floor: the hotel lobby's is eight textures, four of which are its
panelling and its beams, so "every batch drawn with one of the floor's textures" came to
most of the room. Done every frame that is about three million transforms on one thread.

The measurement that settled it was cutting the *pass* out and leaving the fitting in: still
38 frames a second. A floor does not move, so the plane is worked out once and kept, and
recomputed only when the number of batches changes — which is the one thing that happens to
a room after it has loaded.

### Only real light sources

Asked for as "an option to disable most of the fake lights and try to go realism by only
allowing daylight and/or lamp sources to light the environment, this needs to be togglable
as I expect some scenes might get a lot darker (which is fine)".

`RigBalance` already had the classification and already turned the artists' scaffolding
*down* in proportion to how much of the picture the rays are paying for — a sixth of it
survives at High. The row takes that to nought. What is left is what a photographer would
call a source: the sun, the sky through a window, a lamp, a fire, and the tracer's own
ambient floor shaped by occlusion. Rooms the artists were propping up hardest get a great
deal darker, and that is the point of it rather than a cost of it.

It does nothing at all with no rays, whatever the row says. There the bake *is* the room's
lighting and the rig only reaches the people standing in it, so switching off the fills
would darken the characters and leave the room they stand in exactly as bright — which is
not realism, it is a bug with a switch on it. The row is drawn dead at that tier rather
than quietly doing nothing.

`--real-light` and `--no-real-light` override it for one run, so the same room can be
photographed both ways without editing anybody's settings file.

### Mirrors are not this pass, and were being marched by it

A mirror on a wall facing the player shows what is **behind the camera**, which is the one
thing a screen-space march cannot fetch. It was marching them anyway: the material pass
calls `MIRRORLEFT1` glass at roughness 0.08, well under the 0.6 threshold, so every frame
the marcher walked a surface that already has a reflection painted on it, found almost
nothing, and added the little it did find on top. `SurfaceFinish.Mirror` now takes those
surfaces out of `Reflects`, and the mesh pass reports their glass as matte in the normal
target's alpha so the compute pass skips them. RC1 goes from 1,103 reflective textures to
1,100.

What replaces it is a planar pass: before the room is drawn, the room is drawn again from
the camera reflected through the mirror's plane, into a target of its own, and the glass
reads it.

### The mapping is free

**A point on the mirror's plane lands on the same pixel in both renders.** Reflection fixes
the plane pointwise, so for a point on it the mirrored view matrix and the real one agree
exactly. The glass therefore reads the reflection at `gl_FragCoord.xy / viewport` and needs
no matrix, no second set of texture coordinates and nothing per-mirror in the shader beyond
the flag and the inset. It is also the property that would break silently — a reflection
that slides across the mirror as the camera moves — so there is a test for it.

### What it costs

One image the size of the picture, and one more pass over the room when a mirror faces the
camera. Nothing when none does: `ChooseMirror` finds no glass, no batch carries the flag,
and the pass returns before it clears anything.

It borrows the frame's own normal, motion and depth targets rather than having its own. The
mesh pipeline declares all of them and a pass must bind every target its pipeline writes, so
the reflection cannot be drawn into a colour target alone — and it needs no others, because
the pass that follows clears and overwrites all three.

**One mirror a frame, the biggest on screen.** A second would be a second pass over the room
for a reflection nobody is looking at. TE4 is the room with more than one, and its cameras
are placed at one mirror at a time, which is what makes the rule agree with the one the
player means. The other mirrors keep the picture painted on them, as does the back and the
edges of the chosen mirror's own slab.

**Always the raster pipeline, whatever the tracing setting.** The traced pipeline writes
light rather than a picture and needs the compositing pass to become one; sampled directly
it would be raw irradiance in a mirror. So the glass is lit by the rig without traced
shadows even at High — a real difference from the room around it, and a small one at the
size a mirror is drawn.

### Three things that were each invisible

**A mirrored camera is not a look-at from the reflected points.** Reflecting the eye, the
target and the up vector and building an ordinary look-at puts the camera in exactly the
right place pointing exactly the right way, and is wrong: a look-at always builds a basis of
one handedness, from a cross product, while a reflection has a determinant of minus one and
its view matrix must have the opposite handedness from the camera it came from. The cross
product undoes precisely that and the side axis comes out negated. What the reflection
actually is, is the real view matrix with the reflection applied to the world before it. The
symptom was a mirror full of the wall behind it and then, with the clip disabled, a mirror
full of nothing.

**The clip is what makes it a mirror rather than a hole.** The reflected camera stands behind
the glass, so the wall the mirror hangs on is between it and the room. Fragments behind the
plane are discarded — the cheap half of an oblique near plane, doing the same job, since the
expensive half buys depth precision a reflection sampled once does not need. The mirror
itself is discarded in the same test: from behind a mirror there is no mirror to see, and it
is also what stops the glass reading an image of itself out of a target not yet drawn.

**Zero is not a plane.** The ordinary pass carries a zero normal, so every point is at
distance zero and the clip passes everywhere — no branch, no second shader variant.

### The two passes cannot share a constant buffer

They are recorded into one command list and read their constants when the GPU reaches the
draw, not when it is recorded, so whichever was written last is what both would see. Each
frame's uniform ring is therefore twice as long, one slot for the room and one for its
mirror. The reflection does not advance the motion history: it belongs to the frame, and
letting the mirrored camera write it hands the next frame's motion vectors the view from
behind a mirror. Nothing is lost — the reflection's own motion target is overwritten by the
pass that follows it.

### And the culling was already right

A reflection reverses winding, so a reflected pass normally needs its cull mode turned
around and therefore a second pipeline object in both backends. Neither backend culls
anything: GK3's geometry is not wound consistently — a card is routinely two quads facing
opposite ways — and the mesh pass has always drawn both sides. The one thing that would have
cost a pipeline variant costs nothing.

### Which surfaces, and the two that must never be

GK3 has three mirrors that are pictures of a reflection and can be given a real one:
`MIRRORLEFT1` and `MIRRORRIGHT1`, the temple's two mirrors at rest, and `TE4MIRROR`, the
same frame with an empty interior. All three carry the **ornate silver frame in the
texture**, so a reflection covering the whole card paints the frame out. The inset is
measured rather than guessed: differencing `MIRRORLEFT1` against `MIRRORRIGHT1` leaves only
the texels showing the room — columns 12 to 115, rows 9 to 119 of 128 — so the border is
nine to twelve texels and `mirrorInset: 0.09` is inside it on every edge.

`MIRRORGABEBAD` and `MIRRORGABEGOOD` are **not reflections and must never be given one**.
`GABTE4TOMIRRORL` frame 25 puts `MirrorGabeBad` on the left mirror — a jaundiced,
hollow-eyed Gabriel that is not his reflection at all — and `GABTE4TOMIRRORR` frame 33 puts
the true image on the right. Which mirror shows which is the puzzle, and `TE4.sheep` glides
the camera to `Left_Close` for a close-up of it before setting Gabriel's mood to
`Surprised`. A rendered reflection shows the real Gabriel in both and deletes the puzzle.
They are refused by name in the edits file, with the reason, rather than left to a
judgement about roughness.

**That refusal works because the flag follows the picture, not the surface.** Those images
arrive by `[MTEXTURES]` repaint over the same surface the reflection would occupy, and a
repainted batch keeps its original `TextureName` — so asking the original name whether this
is a mirror answers yes right through the story beat. `Batch.Drawn` is what is asked
instead. It also means the reflection turns itself off and on at exactly the right moments
with nothing scripting it: live while the mirror is at rest, and out of the way the instant
the story puts an image in it.

B31's hand mirror is left out for now. Its glass is the oval inside `MIRRORFRNT` — the
handle and rim are drawn on the same card — so masking it needs a shape and not the
rectangular inset the framed mirrors want. `MIRRORTEX`, the flat grey at roughness 0.22
that looks like the obvious candidate, is the slab's **edge**: 49 of its 89 vertices are on
the underside and the rest on the top face.

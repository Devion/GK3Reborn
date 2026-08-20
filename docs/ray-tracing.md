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

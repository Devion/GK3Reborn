# Ray tracing

Four quality levels, as the settings screen will expose them:

| level | shadowed lights | rays per shadow | occlusion rays | bake weight |
| --- | --- | --- | --- | --- |
| None | – | – | – | 1.0 |
| Low | 8 | 1 | – | 0.6 |
| Medium | 16 | 1 | 4 | 0.5 |
| High | 32 | 2 | 8 | 0.35 |

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

**Occlusion.** Cosine-weighted hemisphere rays out to the level's radius,
modulating the indirect term only. Direct light is already shadowed; applying
occlusion to it as well would double-count.

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

**Lights with attenuation switched off get a bounded range anyway** — their stored
end distance, doubled. Unbounded reach was affordable when the result was baked
once; it is not when it is evaluated every frame.

Both are guesses that produce a better picture, not measurements. ADR 0006 expects
this: the 1999 values were tuned for a renderer with no exposure control, and every
light stays correctable through the edit layer.

Cost, measured at 1920×1080 in the hotel lobby (41 lights, 10,704 opaque triangles)
on an RTX 5090, as the difference from quality None: roughly 10 ms at Low, 30 ms at
Medium and High. That is more than the scene's size suggests, because every pixel
loops over every light in the rig before deciding which ones to trace against.
Light culling — clustered or tiled — is the obvious next optimisation and would cut
most of it.

In-window frame rate is presentation-limited on this machine and therefore says
nothing about GPU cost; the figures above come from the headless path.

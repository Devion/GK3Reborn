# Lightmaps (`.MUL`)

226 files holding **181,089 individual lightmaps** — the whole of the original game's
lighting. The tag reads `TLUM` on disk, being `MULT` stored little-endian.

## Layout

| Size | Field |
|---|---|
| 4 | `TLUM` |
| 4 | lightmap count |
| … | that many bitmaps, packed back to back |

The bitmaps use the ordinary texture format and carry no offset table, so each one has
to be measured to find the next. The count matches the corresponding scene's surface
count, and the order matches too: lightmap *i* lights surface *i*.

## The finding that matters: lighting varies by time of day

Lightmap sets are named after a scene with a one-letter suffix — `RC1_A_A`,
`RC1_A_E`, `RC1_A_M`, `RC1_A_N`. Those are **morning, afternoon, evening and night**,
the same suffixes the scene files use when they pick geometry per timeblock
(`scene=LBY_M`, `scene=LBY_A`, and so on).

**51 scenes carry multiple lighting variants**, and several — `BEC`, `CD1`, `CDB` —
carry all four. Suffix counts across the corpus: 51 afternoon, 45 morning, 40 evening,
38 night.

This has two consequences.

**A per-scene rig is not enough.** ADR 0002 and ADR 0006 describe deriving *a* lighting
rig per scene. A room lit for morning and the same room lit for night are different
lighting solutions over identical geometry, so the unit is a rig per scene *and
timeblock*, or one rig whose parameters vary with time. The runtime already has to
select geometry per timeblock; lighting has to follow the same selection.

**The derivation gets much better evidence.** Clustering luminance maxima from a single
bake is guesswork about how many sources there were. With up to four bakes of the same
surfaces, differencing them separates what changes from what does not: a night bake
shows only artificial light, and subtracting it from a morning bake isolates the sun's
contribution and direction. Sources that persist across all four are practicals —
lamps, windows, fires. That is a far stronger starting point than the single-bake
clustering ADR 0002 assumed, and the extractor should use it.

## Scale

`RC1_A` has 2,438 lightmaps, one per surface. Lightmaps are small — most surfaces get a
few dozen pixels — which is why the derivation has to work from many low-resolution
samples rather than from a few detailed ones.

## Conversion

`organize` writes each set to `scenes/<LOCATION>/lightmaps/<SET>/<SET>_NNNN.png`, in
surface order, so a lightmap can be matched to the surface it belongs to by index.

## Is there enough information to derive light sources?

`GK3Reborn.Tools lighting-analysis` measures this rather than assuming it. Across 221
lightmap sets covering 106 scenes:

| Measure | Value |
|---|---:|
| Surfaces with directional information | 40,801 (23.1%) |
| Surfaces lit evenly | 132,809 |
| Surfaces receiving almost no light | 3,063 |
| Scenes with more than one time of day | 48 |

A surface lit evenly says how much light arrived but not where from. Only a surface with
a gradient across it constrains a direction — and 23% do.

That is enough, because **a scene needs far fewer lights than it has surfaces**. A
typical set carries 150 to 165 directional surfaces, which heavily over-constrains the
handful of sources a room actually has. Only **two of 221 sets** are starved: `GRI_A`
with 4 directional surfaces and `MCB_N` with 5. Both are very dark scenes — mean
luminance 0.05 and 0.13 — where there is little light to leave a gradient. Those need
authoring by hand, which is the small manual fraction ADR 0002 predicted.

The timeblock reading is confirmed by the measurements themselves:

| Timeblock | Sets | Mean luminance |
|---|---:|---:|
| M (morning) | 45 | 0.553 |
| A (afternoon) | 45 | 0.523 |
| E (evening) | 35 | 0.362 |
| N (night) | 37 | 0.274 |

A clean daylight curve, which is what the letters were guessed to mean and now
demonstrably are.

Four sets disagree with their scene on surface count, and five could not be matched to a
scene at all. Both are recorded rather than smoothed over.

## Do the lightmaps stay?

Not at runtime, once ray tracing is doing the work. Ray traced direct lighting, ambient
occlusion and bounce replace what the bakes encode, provided the derived rigs recover
enough real sources — which the measurements above say they do for all but two sets.

Three qualifications:

**The raster tier still needs something baked.** The plan requires every scene to render
correctly without ray-tracing hardware. That does not mean keeping the 1999 bakes: it
means re-baking from the *new* rigs, so the compatibility tier gets modern-range lighting
rather than a fallback to 1999.

**The lightmaps stay as source evidence indefinitely.** They are the only record of what
the original artists intended, and ADR 0006 requires derivation to stay re-runnable as
the extractor improves. They live in the workspace, not in the shipped content.

**Some baked light has no source to recover.** 1999 artists painted light that no
physical object produced — fill on a face, a glow with no lamp. Those have no position to
derive and become authored lights that never physically existed. The edit layer exists
precisely for that.

There is also a subtler version of the same problem in the textures: where shading was
painted into a diffuse map, ray-traced lighting will add a second set of shadows on top
of the first. That needs catching during texture enhancement, not after — a texture with
a shadow painted in should have it removed, not upscaled.

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

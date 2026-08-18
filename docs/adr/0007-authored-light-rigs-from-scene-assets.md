# ADR 0007: Take light rigs from the scene assets, not from the lightmaps

- Status: accepted
- Date: 2026-08-18
- Supersedes: [ADR 0002](0002-lighting-derived-from-lightmaps.md) in part

## Context

ADR 0002 was written on the premise that GK3's lighting exists only as baked
result — MUL lightmaps — and that light *sources* would have to be inferred from
them by clustering luminance maxima and back-projecting along surface normals.
That premise was wrong.

Every scene asset (`.SCN`) carries the rig the original artists authored, one
section per light:

```
[Light_standing_lamp_omni]
Type=0
Position=265.175781,80.611328,617.383789
Direction=0.000000,-1.000000,0.000000
Color=0.796079,0.749020,0.564706
HotSpot=-0.017453
Falloff=-0.017453
AttenStart=42.000000
AttenEnd=133.000000
UseAtten=1
CastShadows=1
Overshoot=0
Intensity=1.000000
Radius=2.000000
DecayType=0
```

Across the corpus:

| measure | value |
| --- | --- |
| scene assets | 229 |
| scene assets declaring lights | 222 |
| lights in total | 4,109 |
| point lights (`Type=0`) | 2,595 |
| spot lights (`Type=1`) | 1,514 |
| marked as casting shadows in the bake | 2,618 |
| median lights per lit scene | 6 |
| most in one scene (`TE2B`) | 148 |

The names are the artists' own and describe intent: `standing_lamp_omni`,
`hal_hanging_special_down04`, `window_hot_spot03`, `moon(key)`, `sky_bounce_`,
`fill_light01`, `shadow_maker`, `only_for_camera_shot_bath01`. Scene assets are
per timeblock, so the rig already varies with time of day exactly as ADR 0002's
amendment said the derived rigs would need to.

This is strictly better evidence than anything recoverable from a bake. A
lightmap conflates light colour with surface albedo, cannot separate overlapping
sources, and at GK3's lightmap resolution — R25's 925 maps total 54,040 texels,
an average of about 7×7 each — localises a source only very roughly. The scene
asset states position, direction, colour, cone angles, attenuation range,
intensity, emitter radius and shadow-casting outright.

## Decision

**The scene asset is the source of truth for lighting.** Rigs are read from it
rather than derived.

1. `SceneAssetFile` parses the rig into `AuthoredLight`, preserving every field
   including the ones nothing consumes yet (`Overshoot`, `DecayType`), because
   discarding them at parse time is what makes them expensive to recover later.
2. The lightmaps stay as the compatibility tier's lighting and as the reference
   for what the original looked like. Nothing about that changes.
3. The lightmap-derived extractor from ADR 0002 is **demoted to a cross-check**.
   Its remaining value is confirming that an authored light actually contributed
   to a bake: a light in the file leaving no trace in the lightmap was disabled,
   occluded, or applied to a different variant, and that discrepancy is worth
   surfacing. It is no longer on the path to shipping lighting.
4. ADR 0006 stands unchanged. Re-lighting for modern range still applies — the
   authored values were tuned for a 1999 renderer with no exposure control, so
   intensity and colour are a starting point rather than physical quantities —
   and the edit layer that makes every light correctable is still required.

## Consequences

**Good.** The uncertain part of ADR 0002 disappears. There is no extraction
success rate to worry about, no clustering to tune, and no scenes falling through
to manual authoring because their bake was too coarse to localise. Light *intent*
survives: a light named `fill_light01` can be treated differently from one named
`moon(key)` when re-lighting, which no amount of texel analysis would have
recovered. The rig is complete on day one for 222 of 229 scene assets.

**Bad.** The authored values are 1999 values. Attenuation is a start/end range
rather than physical falloff, `HotSpot` and `Falloff` are 3ds Max cone angles,
and intensity has no unit. Converting these to something a physically-based
renderer is happy with is guesswork that needs review per scene — which is the
same review queue ADR 0002 anticipated, just starting from much better input.
Seven scene assets declare no lights at all and still need something; the
lightmap-derived rig remains the fallback for those.

**Uncertain.** Whether the authored rig alone reproduces the original mood once
the lightmaps are switched off. The bakes had bounce and area shadowing that
direct evaluation of these lights will not reproduce, which is exactly what ray
tracing is meant to supply — but whether it lands in the same place is an
empirical question, and the re-bake comparison ADR 0006 keeps as a diagnostic is
how it gets answered.

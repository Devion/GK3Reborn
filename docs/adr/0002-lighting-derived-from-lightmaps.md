# ADR 0002: Derive dynamic light rigs from the original baked lightmaps

- Status: accepted
- Date: 2026-08-18
- Supersedes: none

## Context

GK3 scenes are BSP geometry with all lighting baked into MUL lightmaps — 222
unique lightmap assets across the corpus. The 1999 renderer had no dynamic scene
lights to speak of; the mood of every room is painted into texels.

That creates a problem for the project's headline goal. Ray-traced shadows,
reflections and global illumination need light *sources*: positions, colours,
intensities, shapes. Baked lightmaps contain the *result* of lighting, not its
causes. Two obvious options are both bad:

1. **Keep the lightmaps and add ray tracing on top.** The scene is already lit,
   so RT contributes almost nothing beyond doubled-up shadows and incorrect
   bounce. The feature becomes a checkbox with no visible payoff.
2. **Hand-author lighting for every location.** Correct, and completely
   unscheduled — over a hundred locations, each needing an artist, with no
   reference for what "right" means beyond the original screenshots.

## Decision

Treat the lightmaps as **evidence about where lights were**, and generate a
per-scene rig from them that a human then reviews.

Pipeline stage **C4b** (see `Plan/02-content-pipeline.md`):

1. Decode each `.MUL` lightmap per BSP surface and keep it unchanged — it remains
   the compatibility tier's lighting and the ground truth for comparison.
2. Project each lightmap into world space through its surface's UV mapping to get
   a luminance field, and cluster the local maxima.
3. For each cluster, estimate position by back-projecting along the surface
   normal and refining against the falloff, then estimate colour (from lightmap
   chroma, un-tinted by surface albedo where albedo is known), intensity and
   radius. Classify as point, spot, area or ambient from the shape and anisotropy
   of the falloff footprint.
4. Cross-check every candidate against scene data before trusting it: emissive
   materials, skybox presence, lamp and window props in the model set, and
   scripted light changes in Sheep. A hint no geometry supports is suspect.
5. Emit `scenes/<SCENE>.lighting.json` — a hand-editable rig where every light
   carries provenance (derived / edited / authored) and a confidence score.
6. Validate by re-baking the derived rig offline and comparing to the original
   lightmap with a perceptual metric. Scenes above the threshold go to manual
   authoring rather than shipping a bad rig.
7. Require human sign-off per scene. The generator proposes; it does not approve.

The runtime consequence: the compatibility tier renders from baked lightmaps, and
the enhanced and RT tiers render from the rig. Both are selectable, so a reviewer
can flip between them on the same frame.

## Consequences

**Good.** Ray tracing has something real to do. Artists start from a rig that
already resembles the original mood instead of an empty room, which is the
difference between "author 100+ scenes" and "review 100+ scenes". Provenance and
confidence make the review queue orderable — low-confidence scenes first. The
raster path never depends on any of it, so a failure here degrades quality rather
than blocking play.

**Bad.** The extractor is real work with an uncertain success rate; lightmaps
conflate light colour with surface albedo, and low-resolution lightmaps on large
surfaces may not localise a source well enough to matter. Scenes lit by many
weak, overlapping sources will cluster badly. Expect a meaningful fraction to
fall through to manual authoring — the plan must budget for that rather than
assume the generator handles everything.

**Amended.** The corpus turned out to hold *several* bakes per room, not one:
lightmap sets carry morning, afternoon, evening and night suffixes, and 51 scenes have
more than one variant. So the unit of derivation is a rig per scene **and timeblock**,
not per scene, and the extractor should difference the variants rather than cluster a
single bake — a night bake shows only artificial light, so subtracting it from a morning
bake isolates the sun. See `docs/formats/lightmaps.md`.

**Resolved.** The art-direction question left open here — reproduce the original
1999 mood exactly, or re-light for modern dynamic range — is answered by
[ADR 0006](0006-relight-for-modern-range.md): re-light for modern range, with the
step 6 re-bake comparison demoted from an acceptance gate to a diagnostic, and
every derived light correctable through an edit layer that survives regeneration.

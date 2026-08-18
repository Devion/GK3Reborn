# ADR 0006: Re-light for modern range, and make every derived value correctable

- Status: accepted
- Date: 2026-08-18
- Resolves: the open question in [ADR 0002](0002-lighting-derived-from-lightmaps.md)

## Context

ADR 0002 decided to derive dynamic light rigs by back-projecting the original
baked lightmaps, and deliberately left one question open: reproduce the 1999
lighting mood exactly, or re-light for modern range? The answer sets the
acceptance threshold for the re-bake comparison, so it blocks bulk authoring.

The same question turns out to apply to materials. The 1999 assets carry a
diffuse texture and essentially nothing else; roughness, metalness and specular
response all have to be inferred from the texture, the surface's name and its
role in the scene. Those inferences are guesses. Some will be wrong in ways that
only show up in motion under real lighting — a stone floor that reads as wet, a
brass fitting with no highlight at all.

## Decision

**Re-light for modern range.** The rig targets the physically based renderer's
range and the HDR pipeline, not a match to 1999 output. The re-bake comparison in
ADR 0002 step 6 stays, but its role changes: it is a *diagnostic*, not a gate. A
large delta means the derivation may have missed a source and the scene is worth
a look; it does not mean the scene has failed.

**Everything derived is a first draft.** Lightmap-derived lights and inferred
material channels are explicitly best-effort guesses, shipped as a working
starting point and corrected as the game becomes playable and people can see the
scenes in motion.

**Corrections live in their own files and survive regeneration.** Each authorable
document is a pair:

```text
scenes/LBY.lighting.json              generated; the converter owns and rewrites it
scenes/LBY.lighting.edits.json        human-owned; the converter never touches it

materials/LBY.materials.json          generated
materials/LBY.materials.edits.json    human-owned
```

The edits file is an ordered list of `add`, `modify` and `remove` operations.
`modify` carries a sparse patch, so setting `roughness` changes roughness and
nothing else. The effective document is the baseline with the edits replayed over
it. Anything the artist touched is marked `edited`; anything they introduced is
`authored`; the untouched remainder stays `derived` with its confidence score.

This is what makes "improve the extractor and rerun everything" a safe operation
at any point in the project, which is the property that matters most given that
the guesses are being made years before anyone can evaluate them properly.

Implementation: `Content/Authoring/EditLayer.cs`, with `SceneLight` and
`MaterialDefinition` both implementing `IAuthorable<TSelf, TPatch>`. One mechanism,
used by both, extensible to any future derived-then-corrected content.

## Consequences

**Good.** Ray-traced lighting can actually look like 2026 rather than being
constrained to imitate a 1999 lightmap, which was the point of the renderer. Bad
guesses stop being blockers: ship the guess, fix it when it is visible, and never
lose the fix. Provenance makes review orderable — everything still marked
`derived` with low confidence is the queue, and it shrinks visibly as work lands.
An artist can delete a spurious light or dial back a glossy floor by editing one
small file, with no tooling and no code change.

**Bad.** Re-lighting means the game will not look like the original in
side-by-side comparisons, and some of that difference will be a matter of taste
rather than obvious improvement. Purists will notice. The compatibility tier still
renders from the baked lightmaps, so the original look remains available, but it
is a fallback rather than the reference.

The edits layer also introduces a real failure mode: an edit whose target the
generator no longer produces. That is reported as a warning naming the id and the
document, and the remaining edits still apply — a stale edit degrades one light,
never a whole scene. Over time the edits files accumulate corrections whose
reasons nobody remembers, which is why every edit carries a free-text `reason`.

**Follow-on.** In-editor add/remove and live reload of both document types should
land alongside the renderer, so corrections can be made while looking at the
scene rather than by editing JSON and restarting. The file format is designed for
that: it is the same data either way.

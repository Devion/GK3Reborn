# Mesh enhancement

`tools/blender/enhance_models.py` runs the converted models through Blender:

```bash
blender --background --factory-startup --python tools/blender/enhance_models.py -- \
    --workspace path/to/ContentWorkspace [--only NAME ...] [--limit N] [--dry-run]
```

Import GLB → analyse → clean → enhance by category → apply → LOD chain → export →
gltfpack if available. It reads `manifests/model-roles.json` and processes only what
should be processed.

## Which models to touch, and why it is not obvious

Not every `.MOD` is a prop or a character. Some are invisible volumes, and — as
raised during development — some are architecture. Neither the name nor any single
signal separates them:

- `CS2_CAMBNDS` is declared as a scene's camera bounds, yet carries **92 meshes and
  seven textured materials** of actual furniture. It is doing both jobs.
- `LBYCAMERABOUNDS` is 31 meshes with **no texture at all** — a genuine invisible
  volume.

So `classify-models` takes roles from the scene initialisation files, which declare
them outright: `cameraBounds=`, `boundary=`, `floor=`, and `model=…, type=` with
values `scene`, `prop`, `gasprop`, `hittest` and `noclick`, plus models named in an
`[ACTORS]` section. Those declarations are then weighed against what the geometry
actually contains.

| Disposition | Count | Treatment |
|---|---:|---|
| `prop` | 1,372 | Bevel, weighted normals, sharp shading |
| `review` | 307 | **Skipped by default** — declared collision-only but textured |
| `collision` | 142 | Never touched |
| `character` | 41 | Subdivision, broad smooth shading |
| `scene-geometry` | 16 | Smooth shading only |

**Being animated does not make something a character.** 425 models animate without
being one — doors, phones, curtains, an alarm clock. Only the 41 models a scene file
names in `[ACTORS]` are the cast, and that is what the classifier uses. The animated
flag is still recorded, since the pipeline needs it to choose skinned or static
handling later.

Room geometry itself is not in `.MOD` at all: rooms are BSP files, 110 of them.

## What each category gets

**Characters** get two levels of subdivision and smooth shading at 60°. Levels are
kept low deliberately: a 1,200-vertex character subdivided twice is already 19,000
vertices *of the same shape*.

Note that subdividing a whole character is the thing `Plan/05` forbids: it changes vertex
count and order, which invalidates that character's entire clip set. It is fine as an
export and cannot be used at runtime. The refinement that *is* used at runtime touches
only the head, for the reason set out in `head-refinement.md`.

**Props** get an angle-limited bevel with hardened, weighted normals. A bevel is what
stops 1999 geometry reading as paper-thin under real lighting — edges catch a
highlight instead of vanishing.

**Scene geometry** gets smooth shading and nothing else. This is where modifiers do
the most damage: walls and floors have to keep meeting exactly, and rounding an edge
a wall abuts opens a visible seam. Improving these properly means re-modelling with
the room's collision and camera bounds in hand.

**Collision** is never touched. It is never drawn, and the plan requires original
navigation and collision to survive even where visible geometry is replaced.

**Review** is skipped unless `--include-review` is passed. These are the assets doing
two jobs at once, and a human should look before a modifier stack does.

## What this does not do

**It cannot invent detail.** The originals average 123 vertices. Subdivision and
beveling round silhouettes and give normals something to work with; they do not add
information that was never modelled. The output is a better base mesh, not a finished
asset — Tier 0 and Tier 1 assets still need a human in a DCC, per the tiering model in
`Plan/02-content-pipeline.md`.

**There is no rig.** GK3 characters animate by vertex animation in ACT files, not by
skinning. Nothing imported here has a skeleton, so the organic/hard-surface split
comes from the declared role rather than from rig data. Building skeletons is its own
project, scheduled as C6.

**Nothing here reaches the screen.** The engine draws `.MOD` geometry; it has no glTF
reader, so `enhanced/models` is an output and not yet an input. The one piece of mesh
enhancement that is live in the game is the head refinement, which does its subdivision
inside the engine on the original geometry rather than importing anything — see
`head-refinement.md`.

**Texture work is not here.** Upscaling and PBR channel generation are an image
pipeline, not a mesh one; coupling them would join two stages that fail for unrelated
reasons.

## Baking the geometry into a normal map does not work

`--bake` will bake the enhanced geometry down onto the original UVs, writing tangent-space
maps into `enhanced/normals` and merging them with whatever the image pipeline generated
for the same surface. It is **off by default**, because the result was measured and it is
empty.

Over a mixed sample of 24 props and 4 characters, **74 of 76 textures baked perfectly
flat** — median tilt 0.00°, and the two that did not were cage artefacts at UV island edges
rather than surface detail.

The reason is worth writing down, because the idea is an obvious one and someone will have
it again. GK3's meshes already carry welded, smooth vertex normals: measured across `GRA`,
`GAB` and `EST`, normals at a shared position agree to **0.0°**, across material seams
included. A subdivided surface converges to exactly those interpolated normals, so there is
no difference left for a tangent-space map to record. Beveling fares no better, for a
second reason on top of the first: `harden_normals` deliberately makes a bevel shade like
the flats either side of it, and a bevel occupies almost no area in the original UV layout
anyway.

What the modifiers change is **position and silhouette**, and a normal map cannot hold a
silhouette. That is the whole of why the head work in `head-refinement.md` moves geometry
instead.

The code is kept, gated on a measured contribution threshold so it never writes a flat map
over a generated one, and instrumented — `enhanced-models.json` reports `bakedNormals` and
`flatNormals` per model with the tilt percentiles behind each decision. The first run of a
texture copies the generated map aside to `enhanced/normals-source`, so merging is
idempotent and reversible.

## Measured on the reference installation

| Model | Kind | Before | After | LOD1 | LOD2 |
|---|---|---:|---:|---:|---:|
| GAB (Gabriel) | character | 1,746 | 41,904 | 20,947 | 10,468 |
| ABE | character | 1,868 | 44,832 | 22,413 | 11,206 |
| ANGLSWRD | prop | 123 | 908 | 454 | 230 |
| ALARMCLOCK | prop | 22 | 118 | 64 | 36 |

Note that LOD2 for a character is still six times the original triangle count. The
original mesh is, in effect, already the bottom of the LOD chain — worth remembering
before adding further levels.

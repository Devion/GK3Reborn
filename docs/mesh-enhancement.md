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

**Texture work is not here.** Upscaling and PBR channel generation are an image
pipeline, not a mesh one; coupling them would join two stages that fail for unrelated
reasons.

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

# Texture enhancement brief

Work package for whoever — or whatever — produces higher-resolution textures. Generated
by `GK3Reborn.Tools texture-plan` into `manifests/texture-plan.json`.

## Why textures first

Geometry is not what dates this game. All 6,657 textures together hold **213
megapixels**, roughly what twenty-six single 4K textures hold. Gabriel's face is
256×256. Over three thousand textures are 128 pixels or smaller, and 1,131 are 32×32.

Tier 0 — the 721 textures that matter most — currently total **5.8 megapixels between
them**. That is the entire visual budget for the surfaces a player looks at most.

Meanwhile a character subdivided from 1,746 to 41,904 triangles is rounder and no more
modern. Mesh work has a low ceiling until the textures move.

## Tiers

Assigned from evidence — what references a texture, whether those are characters, props
or rooms, how many places use it, and how small it is now. Small textures are promoted a
tier, because a 32-pixel texture on screen is the most visible kind of dated.

| Tier | Count | Target | Meaning |
|---|---:|---:|---|
| 0 | 721 | up to 4096 | On characters, or on many rooms at once |
| 1 | 1,359 | up to 2048 | Room surfaces and widely reused props |
| 2 | 1,679 | up to 1024 | Everything else in use |
| 3 | 2,898 | — | Unreferenced, or a flat colour |

Targets are also capped at 16× the source. Beyond that an upscaler is inventing rather
than restoring.

## What not to touch

**280 textures are a single colour** — including `BAR_SKIN`, `BIG_SKIN` and `BABY_SKIN`,
which are solid skin tones rather than painted maps. Enlarging one produces a larger flat
colour and nothing else. They carry `isFlatColor` and their colour as `flatColor`, and
belong in a material as a base-colour factor, not in an image pipeline.

**Magenta means transparent.** A texture whose top-left pixel is magenta is alpha-tested,
and the converted PNG already carries that as real alpha. 719 textures are affected.
Anything generated for them must preserve the alpha channel and must not bleed colour
into transparent regions, or edges will fringe.

**Unreferenced textures (tier 3)** are not worth effort until something is shown to use
them. C2 records several dangling references, so some of these may be reachable in ways
the static scan cannot see.

## What each entry gives you

```json
{
  "name": "RC1RGHSTNWIN1",
  "width": 64, "height": 64,
  "hasAlpha": false,
  "tier": 0,
  "isFlatColor": false,
  "targetSize": 1024,
  "usedByCharacters": 0, "usedByProps": 2, "usedByRooms": 21,
  "referrers": ["BET", "CEM", "CEM_A_E", "..."]
}
```

`referrers` is the context that matters when authoring: `RC1RGHSTNWIN1` appears in 21
rooms, so it is rough stone around a window in a French village church, and it has to
tile with `RC1RGHSTNEDGE` and `RC1STONE_A_MOULD` beside it. Textures sharing a prefix are
usually one material set and should be treated together, or the seams will not match.

Source images are at `normalized/textures/NAME.PNG`.

## Constraints

**Tiling must survive.** Most tier 0 and tier 1 entries are tiled surfaces. An upscale
that breaks edge continuity is worse than the original, because the seam repeats across a
whole wall.

**UV layouts are fixed.** The geometry references these textures by existing coordinates.
Output must be the same aspect ratio and the same layout — this is an upscale, not a
re-authoring, unless the mesh is being redone at the same time.

**Readable content must stay readable.** Some textures carry text, heraldry or symbols
that matter to the story. A generative pass will happily produce plausible nonsense in
their place; those need review against the original.

**Provenance is required.** Per `Plan/02-content-pipeline.md` section 1, anything derived
from the originals needs its tool, model, prompt and settings recorded before it can be
considered for distribution, and human review before it ships. Generated output is a
draft, not an approval.

## Suggested order

1. A pilot of twenty tier 0 textures spanning stone, wood, fabric and metal, to
   establish settings and measure how much review each one actually needs.
2. The rest of tier 0, grouped by material-set prefix so tiling neighbours are done
   together.
3. Tier 1 in room order, so a room can be evaluated as a whole rather than surface by
   surface.

Tier 2 should wait until the renderer exists and it is possible to judge results in
motion under real lighting rather than in an image viewer.

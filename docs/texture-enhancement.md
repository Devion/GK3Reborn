# Texture enhancement brief

Work package for whoever — or whatever — produces higher-resolution textures. Generated
by `GK3Reborn.Tools texture-plan` into `manifests/texture-plan.json`.

## enhanced/textures is hand-corrected, and nothing overwrites it

**A texture already in `ContentWorkspace/enhanced/textures` is never written over.** Many
of them have been redone by hand. The directory is outside the repository, so there is no
history to recover anything from, and a rerun of a generator that replaced them would
destroy that work silently and without anybody noticing until a room looked wrong again.

Every writer of that directory leaves what it finds alone, by default and without being
asked:

| lane | what it does | how to override |
| --- | --- | --- |
| `import-textures` | records the candidate as `kept` and copies nothing | `--force` |
| `PbrLab/make_basecolour.py` | prints `already there, left alone` and moves on | `--overwrite-existing` |

Both overrides are deliberate, both are documented as destructive, and neither is implied
by anything else — `--force` on `make_basecolour.py` means *regenerate even if the inputs
have not changed* and still will not write over a texture that exists.

A regenerated texture is usually a downgrade on what is already there, quite apart from the
hand corrections: the 324 from the imagegen pilot are better than the local lane produces,
which is why leaving files alone was the right default even before anybody edited one.

**Replacing one is a manual act.** Delete the file, then run the lane; or write the new
picture over it yourself. Both are things a person does on purpose to one texture, which is
the point.

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

## Bringing candidates in

A generator produces a directory of pictures. What the game needs is textures under the
names the geometry already uses, each checked against the original it replaces, with a
record of where it came from. `import-textures` is the step between:

```bash
GK3Reborn.Tools import-textures --workspace <ws> \
    --model enhanced/textures/imagegen-pilot --variant _imagegen_2048w \
    --tool "Codex imagegen (built-in), resized to 2048 width"
```

It writes accepted candidates to `enhanced/textures/NAME.PNG` and the provenance record
to `manifests/enhanced-textures.json`, which keeps **every** candidate — refused ones
included, with the reason. A name that simply vanished from a manifest would tell nobody
that the generator needs going back to.

Three checks disqualify a candidate, because each makes the game look wrong rather than
merely different:

- **aspect ratio**, since UVs are fixed and the geometry stretches whatever it is given;
- **alpha, in both directions** — an alpha-tested texture that comes back opaque draws a
  solid block where a chain or a leaf should be, and an opaque one that comes back with
  holes punches them through a shirt;
- **flat colours**, which belong in a material as a base-colour factor.

Everything else is recorded and passed on with a verdict of `draft`. **Nothing here
approves anything.** Surviving every check a machine can make is not the human review
`Plan/02` section 1 requires.

### What the first pilot produced

351 candidates, **324 accepted and 27 refused**:

| refused | why |
|---:|---|
| 19 | aspect ratio — all of them extreme-aspect sources (`64x16`, `16x64`, `8x32`) that the generator padded towards square. `BETDOORSTP` came back 2048×819 where 4:1 wanted 2048×512 |
| 4 | opaque where the original is alpha-tested: `CHAINS`, `LIGHTBULB`, `MAPLE1TRILEAF`, `OFFLMPSHD` — four of the seven alpha textures in the set |
| 4 | transparent where the original is opaque: `ABE_SHIRTB`, `BAR_SHIRT`, `HE2_PANT`, `SED_WHEEL`, two of them fully so somewhere |

**279 of the 324 accepted are past the 16× cap above** — mostly 32×, some 64×, two at
256× from `8x32` sources. That is a remake rather than a restoration, which is a choice
worth making deliberately rather than by accident; the manifest records the factor per
texture so the decision can be revisited.

### Coverage is measured on screen, not in the list

324 textures sounds like most of a room and is not. Rendering a view twice — once
normally, once with every enhanced name replaced by flat red — measures what they
actually cover:

| scene | of the view |
|---|---:|
| CS3 | 46% |
| R27 | 43% |
| CSE | 36% |
| R25 | 7% |
| RC1 | 3% |
| HAL | 2% |
| LBY | 0.3% |

The pilot took tier 0 by reference count, which favours small shared things — door
latches, hall numbers, bathroom tile — over the wallpaper and carpet that fill a frame.
Those are mostly tier 1. Finishing tier 0 will not change that; the next pass should be
tier 1 in room order, as the order below already says.

## Using them

`render-scene --enhanced <dir>` puts them in front of the archives. A relative path is
taken from `--workspace`. Names are matched without extension or case, so `R25WALLS`,
`R25WALLS.BMP` and `R25WALLS.PNG` are one texture, and anything with no enhanced version
loads from the archive as before — a partial set is a perfectly good set. A file that
will not decode falls back to the original and says so (`GK3R1093`) rather than failing
the scene.

That also makes the comparison possible: the same camera, twice, side by side. It is the
only way to judge this work, and `PngReader` exists so the engine can read what the
pipeline writes.

## The local lane

The pilot above was a hosted generator and it stopped at 324 of 6,657 for reasons of
budget rather than of quality. `PbrLab/make_basecolour.py` is the same work on this
machine, against a running ComfyUI: **4x-UltraSharp** for structure, then
**Qwen-Image-Edit 2509** with the four-step Lightning LoRA for the material, then down to
an exact integer multiple of the source. `PbrLab/README.md` is the operating manual; what
belongs here is what it changes about this brief.

**The 16× cap is deliberately exceeded.** `--target-longest` defaults to 2048, which from
a 64-pixel source is thirty-two times and is a remake by this document's own definition.
That matches what the pilot already did — 279 of its 324 accepted textures are past the
cap — and it is now a setting with a number rather than an accident. The manifest records
the factor per texture.

**Tiling is no longer left to chance.** The pilot broke it on four of the first twelve
textures and `import-textures` never tested for it. The residual difference across the
wrap join is now spread back over a narrow band as a smooth ramp, which is exact
arithmetic rather than a hope about the generator, and it is applied only where the corpus
says the surface is on a wall.

**Candidates are measured against the original.** `import-textures` refuses aspect, alpha
and flat colour. It cannot ask whether the picture is still the same texture, and a
diffusion model makes that the urgent question: `BIK_OFF` was 64 pixels of white and green
blur, came back as an immaculate industrial control panel, and was accepted. The new check
shrinks both to sixteen pixels on the long edge and compares composition and colour there.
Over eighteen pilot textures sorted by eye it separates cleanly — faithful 0.56 to 0.97,
invented -0.13 to 0.38 — and a texture that fails is retried at a lower denoise before it
is refused.

**Nothing regenerates the pilot's 324.** They are better than this lane produces and
`--skip-existing` leaves them alone. Every record now carries its own `tool`, because a
manifest whose header names one generator for a mixed set is a provenance claim that is
not true.

## Skyboxes are a separate lane

The sky faces are never selected by the texture plan — nothing reads `[Skybox]` from the
`.SCN` files, so all 320 are tier 3 with no referrers. `SceneLoader.LoadSkybox` reads the
enhanced set first as of 2026-08-26, and `PbrLab/make_skyboxes.py` produces 2048 faces
into `enhanced/skyboxes/comfy/` (see `PbrLab/README.md`). `enhanced/skyboxes/original/`
is the 512 set and `enhanced/skyboxes/2048/` a plain Lanczos enlargement, with the 25
`*_MASK` faces enlarged nearest-neighbour; masks are keyed colours and must stay so.

## Suggested order

1. A pilot of twenty tier 0 textures spanning stone, wood, fabric and metal, to
   establish settings and measure how much review each one actually needs.
2. The rest of tier 0, grouped by material-set prefix so tiling neighbours are done
   together.
3. Tier 1 in room order, so a room can be evaluated as a whole rather than surface by
   surface.

Tier 2 should wait until the renderer exists and it is possible to judge results in
motion under real lighting rather than in an image viewer.

## After the upscale

A larger base colour is one channel of a material. The normal, roughness, metalness,
height and occlusion maps that make a surface respond to light are a **separate pass over
the finished base colour**, because deriving them from a 64-pixel original derives
64 pixels of detail. See [pbr-materials.md](pbr-materials.md) and `Plan/02` stage C4c.

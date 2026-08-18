# Model format (`.MOD`) and glTF export

1,878 models, 18 MB, in a format nothing outside the game can read. `organize`
converts them to glTF 2.0 binary (`.glb`), which opens in Blender, any web viewer
and most DCC tools without a plugin.

Documented from G-Engine's `Model::ParseFromData`. Tags are stored little-endian, so
they read reversed on disk.

## Layout

**Header, 48 bytes**

| Size | Field |
|---|---|
| 4 | `LDOM` — MODL |
| 2 | minor, major version |
| 2 | unknown, always zero so far |
| 4 | mesh count |
| 4 | data size, excluding this header |
| 8 | unknown |
| 4 | flags — bit 1 marks a billboard model |
| 16 | unknown, likely more flags |
| 4 | unknown, always 8 |

**Per mesh**

| Size | Field |
|---|---|
| 4 | `HSEM` — MESH |
| 36 | i, j, k basis vectors |
| 12 | position |
| 4 | submesh count |
| 24 | bounds min and max |

The basis vectors are the columns of the mesh-to-local transform, and the Z-up to
Y-up rotation is already baked into them. For a character this transform is what
places an arm relative to its torso.

**Per submesh** — the game called these "mesh groups", hence the tag

| Size | Field |
|---|---|
| 4 | `PRGM` — MGRP |
| 32 | texture name, NUL-padded |
| 4 | tint colour, `0xAABBGGRR` |
| 4 | unknown, usually 1 |
| 4 | vertex count |
| 4 | face count |
| 4 | level-of-detail block count |
| 4 | unknown, usually zero |
| … | positions, 3 floats each |
| … | normals, 3 floats each |
| … | texture coordinates, 2 floats each |
| … | per face: three 16-bit indices, then a fourth 16-bit value |

**Per LODK block**: tag `KDOL`, then three counts, then arrays of 8, 4 and 2 bytes
per entry respectively. Contents unidentified; skipped by size.

**Trailer**: `XDOM` — MODX.

## Two things that are not understood

Each triangle carries a **fourth 16-bit value** after its three indices. Observed
values are scattered — 241, 0, 263, 16255, 45398 — and G-Engine's comment on the
line is "WHAT IS IT!?". It is skipped.

**LODK block contents** are unidentified. Only their sizes are known, which is
enough to step over them.

## The trailer is the integrity check

Reading a format defined by someone else's reader invites silent misalignment: a
miscounted field shifts everything after it, and the result often still parses into
plausible-looking garbage. `XDOM` at the end is the cheap defence — any drift
anywhere above lands somewhere else and the tag will not be there.

All 1,878 retail models parse with the trailer verified.

## Colour

The tint is stored `0xAABBGGRR` and its **alpha byte is always zero**. Taking it
literally makes every model invisible, so it is ignored and alpha is forced opaque.

## glTF export

One glTF node per mesh carrying the mesh transform, one primitive per submesh, and
one material per distinct texture. Materials reference the converted PNGs by
relative URI (`../textures/NAME.PNG`), so models open textured as long as the
normalized tree is intact — 3,343 of 3,357 references resolve, the remaining 14
pointing at textures absent from the corpus, consistent with the dangling references
C2 records.

Materials are **double-sided**: GK3's winding is not consistently counter-clockwise
and single-sided materials leave parts of models invisible in a viewer. PBR factors
are neutral placeholders — the originals carry no such channels — for the material
inference pass to replace, per ADR 0006.

### Normals

glTF requires positions and normals to share a coordinate space. In the original
data they do not: positions are mesh space, normals appear to be local space. The
exporter transforms normals into mesh space so the exported document is
self-consistent.

G-Engine applies the same correction, but only to models whose name is exactly three
characters — its own comment calls this "an incredible HACK" and reports that
untransformed normals look wrong on characters while transformed ones look wrong on
props. That distinction is unresolved. Producing a self-consistent document is the
right call for an interchange format; if the difference turns out to be real it
belongs in the material pipeline, not the exporter.

## Scale

231,202 vertices and 203,385 triangles across every model in the game — an average
of 123 vertices per model. The brief's description of the game as "very low poly" is
if anything an understatement, and it is worth keeping in mind when sizing the mesh
enhancement work in P11.

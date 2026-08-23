# Scene geometry (`.BSP`)

110 files, 56 MB: every room in the game. The tag reads `NECS` on disk, being `SCEN`
stored little-endian. Documented from G-Engine's `BSP::ParseFromData`.

## Layout

| Size | Field |
|---|---|
| 4 | `NECS` |
| 4 | version |
| 4 | content size |
| 4 | root node index |
| 36 | nine counts: names, vertices, UVs, vertex indices, secondary indices, surfaces, planes, nodes, polygons |

Then, in order: object names (32 bytes each), surfaces, nodes, polygons, planes,
vertices, texture coordinates, the vertex-index array, a secondary index array, and
per-node bounding spheres.

**Surface**, 60 bytes: object index, 32-byte texture name, lightmap UV offset and
scale, an unknown float, and flags.

Surface flags are a bit field, documented from G-Engine's `BSPSurface` and confirmed
against the corpus. Three of them the renderer acts on:

| bit | meaning | what it changes |
|---|---|---|
| 1 | certain walls, ceilings and floors | nothing |
| 2 | surfaces that are hard to make out | nothing |
| 4 | a mixture of light sources and hit tests | nothing; too inconsistent to act on — it is on a bedsheet in R25 |
| 8 | the bake did not light this surface | drawn at full brightness, and casts no ray-traced shadow |
| 16 | light fittings: shades, globes, sconces, stained glass | casts no ray-traced shadow |
| 32 | not present anywhere in the corpus | – |
| 64 | a translucent shadow decal rather than solid geometry | casts no ray-traced shadow |

Bit 8 is the one the original renderer itself reads: it binds a white lightmap and a
multiplier of one, which comes out as the texture untouched. A lit bulb, a glowing
shade and the painted view through a window are all bit 8, and multiplying any of them
by a bake leaves them as dim as the room they are meant to be lighting.

**Polygon**, 8 bytes: offset into the vertex-index array, an unknown value that is
almost always 1073, index count, surface index.

## What is kept

Only what reconstructs the visible geometry. The BSP tree — nodes, planes, bounding
spheres — is read past rather than retained: a modern renderer does not traverse it,
and the original navigation data lives elsewhere, so nothing load-bearing is lost.

Surfaces keep their lightmap offset and scale, which stage C4b needs when it
back-projects lightmap luminance to propose scene lights (ADR 0002).

## Two things worth knowing

**Polygons are convex and fan-triangulated** from their first vertex, matching how the
reference implementation walks them. A polygon with N indices yields N-2 triangles.

**Texture coordinates can be fewer than vertices.** `DEFAULT.BSP` is a placeholder cube
with eight vertices and four coordinates, so an index valid for the vertex array can be
out of range for the coordinate array — the reference implementation reads out of
bounds there. Indices are validated against the vertex array only, and coordinate
lookups fall back to the origin.

## Integrity

This format has no trailing tag, so the cross-references do that job: every polygon's
index run must fit inside the index array, every index must address a real vertex, and
every surface and object index must exist. A misread count fails at least one.

All 110 retail scenes parse and convert.

## glTF export

Rooms export as `.glb` with **one node per named object** — the grouping the data
already carries, where several surfaces make up a "door" — and a submesh per texture
inside it. A room therefore opens in Blender as a named outliner tree rather than one
undifferentiated soup of triangles.

Vertices are shared across an entire room, so each submesh remaps only the ones it
uses; exporting the full array per submesh would multiply a room's data by the number
of textures in it.

Scene geometry carries no vertex normals in the original. They are reconstructed by
averaging adjoining face normals weighted by area, which is enough to shade sensibly
in a viewer.

## Scale

1,009,659 triangles across all 110 rooms. The largest are `RC1_A` at 27,985 triangles
across 145 named objects, and `RC4_A` at 25,019. For comparison, every character and
prop in the game totals 203,385 triangles — the rooms are five times the geometry of
everything that moves.

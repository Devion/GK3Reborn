# Statues

The five saints in the niches of the church at Rennes-le-Château are each one flat quad
with a photograph of the real statue on it, flagged as a billboard. This sculpts them,
stands them where the quads stood, and paints them with the same photograph.

```bash
# 1. Sculpt the shapes. Needs the Hunyuan3D ComfyUI running; see below.
D:/AI/ComfyUI-H3/python_embeded/python.exe PbrLab/make_statues.py \
    --workspace path/to/ContentWorkspace [--only CHUROCH] [--force]

# 2. Carve them into props: decimate, orient, place, paint.
blender --background --factory-startup --python tools/blender/carve_statues.py -- \
    --workspace path/to/ContentWorkspace [--only CHU_STROCHFF] [--faces 6000]

# 3. Look at one, from the camera the game puts in front of it.
GK3Reborn.exe --scene CHU --frames 4 --workspace path/to/ContentWorkspace \
    --eye -72.47,94.49,148.13 --aim 90.75,0 --screenshot roch.png

# 4. Look at one from the side, which is where the card used to disappear.
GK3Reborn.exe --scene CHU --frames 4 --eye -70,105,110 --aim 55,-3 --screenshot oblique.png
```

Everything lands in `ContentWorkspace/enhanced`, outside the repository. The raw shapes go
to `enhanced/statues/<TEXTURE>/sculpt.glb`, which is an intermediate and is **not** packed;
what ships is `enhanced/models/<MODEL>.glb`, which the packer already carries as
`RebarnKind.Model` and needs nothing added to the plan. `manifests/statues.json` records
what each one came out as.

## What was actually wrong

Two things, and only one of them is about statues.

**GK3's billboard flag was read and never acted on.** Bit 1 of a `.MOD` header says the
model is one quad the engine turns about its own vertical every frame so that it always
presents its face. 527 of the game's models carry it: 372 foliage, 48 flames and lights,
and 107 others — chains, lanterns, flowers, small pines, and the five saints. `ModFile`
parsed the flag into `IsBillboard` and nothing read it, so every one of them stood in
whatever plane it happened to be authored in.

For a chain in a doorway that is invisible. For the saints it is total. All five are
authored facing along the nave — normal ±Z — and all five stand in niches that open across
it, so **the one direction a player looks into the niche from is the direction the card is
edge-on to**. Every screenshot taken at a niche showed a pedestal, a nameplate, and bare
wall. The shadow-casting copies of the same cards in the room's own geometry
(`chu_statueshadowcasters`) are declared `hidden` in `CHU.SIF` and do not cover for it.

**And a photograph on a quad is a photograph on a quad.** Turning it to the camera is what
1999 did and it is still a cut-out: it has no thickness, it casts a flat shadow, and it
swivels. So the flag is honoured for everything that stays a card, and the five are
replaced by geometry.

## The five

| model | noun | texture | where | depth |
| --- | --- | --- | --- | ---: |
| `CHU_STROCHFF` | `ST_ROCH` | `CHUROCH` | −25.6, 100, 146.4 | 14 |
| `CHU_STEGERMAINEFF` | `ST_GERMAINE` | `CHUGERMAINE` | −253.7, 100, 146.4 | 12 |
| `CHU_STERMITEFF` | `ST_ANTHONY` | `CHUANTE` | −253.7, 100, 260.6 | 16 |
| `CHU_ANTIONEFF` | `ST_ANTHONY_DE_PADOUE` | `CHUANTP` | −35.9, 100.7, 366.4 | 11 |
| `CHU_STEMADELEINEFF` | `ST_MAGDALEN_STATUE` | `CHUMARYM` | −25.6, 100, 260.6 | 11 |

All five are 18 units across and 42 tall, because that is what the card was. Each is 6,000
triangles, decimated from about half a million.

## Five decisions, each taken from something the game already ships

Nothing here is a number somebody chose. `carve_statues.py` reads all of it.

- **Where it stands** is the card's own placement matrix, so the statue is centred on
  exactly the rectangle the quad occupied and stands on the same pedestal.
- **How big it is** is the card's rectangle, 18 by 42. The photograph is 1:2 and the card
  is 1:2.33, so the saint was always drawn a sixth taller than life; matching the card
  keeps that, and the lightmaps and the niche were built around the card.
- **Which way it faces** is the inspect camera. A billboard has no true facing — that is
  what makes it a billboard — but `CHU.SIF` puts a camera in front of each of these five
  for the player to look from, and the direction from the statue to that camera is the
  direction the designers meant it to present. It recovers the small turn on St Antoine de
  Padoue, whose niche is 6° off square to the nave, without being told about it.
- **What it is painted with** is the game's own bitmap, projected on from the front in the
  card's own rectangle. The sculpt was made from that picture, so a front-on projection
  puts every pixel back where it came from — and the normal, ORM and height maps the
  content pipeline derives from the same picture go on working unchanged.
- **What its sides are painted with** is the nearest painted pixel. See below.

## The rim, which is the part that does not work by itself

A statue is a sixth as deep as it is wide. A flat projection maps its rim to the very edge
of the cut-out, where the alpha has already fallen to nothing — so the sides would be
drawn and then thrown away by the alpha test, leaving a hole through the saint from every
oblique angle. Every vertex that projects into the transparent margin is pulled to the
nearest opaque texel instead, which is the colour of the edge it belongs to.

Between 11% and 40% of each statue's vertices are pulled. That number is worth watching: at
100% the projection has landed entirely outside the picture and something about the UV
convention is wrong, which is exactly what happened the first two times.

## Traps

- **Blender's glTF exporter flips V, and the importer converts the axes.** The mesh work
  is done in the game's frame with `export_yup=False`, as `make_props.py` does, so the
  axis conversion has to be undone by hand on the way in — but the *V flip* is not part of
  that and still happens. Storing GK3's own downward-growing v gives a statue with its
  picture upside down and, because the alpha goes with it, cut to the silhouette of its own
  reflection. Store Blender's upward v and let the exporter flip it.
- **Do not read the vertices back through the axis conversion twice.** Once the placement
  is written, the object holds the game's coordinates directly. Converting again does not
  show up in the geometry, which is already correct; it lands the whole projection in the
  transparent margin and paints the saint in one edge colour from head to foot.
- **A GLB has no shared vertices.** Every triangle arrives with its own three, so a
  marching-cubes surface imports as half a million loose faces that only look joined.
  Island-finding sees each face as its own island and decimation collapses faces while
  leaving every original vertex behind. Weld first, and drop the orphans after.
- **The turn is measured from where the card already looks, not assumed.** GK3 authors a
  card facing its own −Z and then usually places it with a matrix that flips Z, so "the
  front is world +Z" is true of nearly all of them. A card it is not true of would be
  turned to face away and culled, which reads as a card that vanished rather than one that
  turned the wrong way. `SceneGeometry.Facing` sums the area-weighted face normals; a
  closed shape cancels to nothing and is refused, which is the right answer for a thing
  with no front.
- **A carved statue must not also billboard.** `Statues.IsCard` is asked of the model that
  will actually be drawn, after the tree and statue swaps, so anything that became a shape
  holds still.

## Why the swap is safe

`Content.ModelLibrary` answers only for names the 1999 archives do not have, and that
boundary is the whole reason a workspace full of generated meshes cannot quietly replace
the game's own props. This is the one exception, and it is narrowed by evidence rather than
by a list of five names — nothing in the engine knows a saint from a lamppost:

- what the archives hold has to be a **billboard card**: the flag set, one mesh, one
  submesh, at most four triangles;
- what the library offers has to have **real geometry**: at least 64 triangles, against the
  6,000 a carved saint has and the 2 a card has.

Both fail closed. A name with nothing carved for it keeps its card; a sculpt that will not
read keeps its card; a prop that is not a card is never looked up. The load line says what
happened:

```
billboards: 5 carved into models, 0 left turning to the camera
```

## What is not done

- **The back is invented.** Hunyuan3D reconstructs a whole figure from one photograph, so
  what is behind each saint is a plausible back and not the real one. They stand in niches
  against a wall, which is the only reason that is acceptable.
- **The normal map still describes a flat statue.** The pipeline derives it from the same
  photograph with DeepBump, which reads the whole figure as relief; applied over geometry
  that now has the same relief in it, some of it is counted twice. It is not visible at the
  distances these are seen from and it is the same texture the card used.
- **`chu_mary`, `chu_joe` and the Baptist are still flat.** Those three are surfaces of the
  room's own geometry rather than props, so they go through `scene-geometry.md`'s path and
  not this one.

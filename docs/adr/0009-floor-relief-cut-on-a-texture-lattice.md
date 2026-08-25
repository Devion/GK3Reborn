# ADR 0009: Cut a floor's relief on a lattice in texture space

- Status: accepted
- Date: 2026-08-23
- Extends: `docs/rendering.md` (Materials, Displaced floors)

## Context

A height field can be marched by the shader or cut into the geometry, and the two
answer different questions. A march moves the texel a ray lands on; it deepens a
mortar course convincingly and does nothing at all to a silhouette. So a cobbled
street reads as cobbles from above and as a painted plane the moment the camera
drops to eye level and looks along it — which, in an adventure game about walking
down streets, is most of the time anybody looks at one.

Cutting it into the geometry means subdividing first, because there is nothing to
displace. Measured: PL6's stretch of road is 96 triangles over 1.15 million square
units, an average triangle four metres across. Every floor in the game is like
this.

Subdividing a floor whose triangles are cut up independently has one hard
requirement — the pieces must still meet — and the obvious approach does not have
it for free. An N by N barycentric grid per triangle needs N to be a property the
neighbours agree on, or the shared edge gets vertices in different places from
each side: a T-junction at best and a hairline of skybox across the floor at
worst. The usual fixes are a per-edge cut count with the interior stitched to it,
or a hardware tessellator's outer and inner levels. Both are real work and both
have a second problem: N comes off the triangle's longest edge, so a long thin
strip of road — which is what a road is — is cut far finer across than along.
Measured over five scenes at a four-unit cell, that wastes between two and four
triangles in every five against what the area actually needs.

## Decision

Clip each triangle against a lattice of lines at fixed **texture** coordinates.
Every piece that falls out is one cell of that lattice. The step is per texture
and per scene, from the area-weighted average of how much world one unit of
texture coordinate is worth on that texture's surfaces.

Two triangles that share an edge share its texture coordinates, so the lattice
crosses that edge in the same places from both sides and both put vertices there.
There is no subdivision level for neighbours to disagree about and nothing to
stitch.

What still has to be handled explicitly is where the lattice is *not* shared: the
floor's outer boundary, and any edge whose two triangles carry different textures.
Those stay exactly where the 1999 geometry put them, with the displacement fading
in over the first cell — and so do their corners, in every triangle rather than
only in the ones that own the edge, because a boundary corner is also a corner of
the triangle behind it and that triangle has no reason not to lift it.

The cell size is bought with a triangle budget rather than fixed, since the paved
area of a room spans an order of magnitude across the corpus.

## Consequences

**Good.** Crack-freeness falls out of the construction rather than being enforced
by a stitching pass, which is the kind of correctness that stays correct. The
triangle count lands on what the area needs — 381,000 of a 400,000 budget on CSE,
against 849,000 for the same cell under a barycentric grid. The cells line up with
the height field, because that is what texture space is. And the estimate is
accurate enough that the budget can choose the cell before a single triangle is
cut.

**Bad.** A surface with no texture area — a collapsed coordinate — has no lattice
and is left alone; that is a silent fallback, and the reason `Tessellate` has a
`Whole` path at all. Cells clipped by a triangle's own edges leave slivers along
it, which is a ragged row of small triangles the estimate has to account for
separately. And a texture stretched differently on two neighbouring surfaces gets
one step for both, so its cells are not the same size in world on each.

**Neutral.** Nothing about this is specific to floors; it is a way of subdividing
textured geometry. What restricts it to floors is where the payoff is and where
the budget goes, not the method.

## Amendment, 2026-08-24

The decision stands; three parts of it were wrong in a way that made the village
come out flat, and the "bad" list above understated the last of them badly.

**Where the lattice is not shared is a geometric question.** It was written as
"an edge no second displaced triangle shares", which is a count, and the count is
not the thing. GK3's ground is laid as separate flat patches that abut without
being welded, and a stitch of stairs or a doorway leaves a long edge with a vertex
partway along it; both are used once from either side and neither is a boundary.
2,201 of RC1's 2,674 once-used edges have more floor of the same texture lying
against them. An edge used once is now tested by looking a little way past it for
another triangle of the same texture, in a grid built for the purpose. Nothing has
to be welded for that to be safe: the lattice is what makes the two sides agree,
and it is laid out in texture space, so two triangles carrying the same texture put
vertices at the same coordinates along the line they meet on whether or not they
share a single vertex.

**"A texture stretched differently on two neighbouring surfaces gets one step for
both" was not neutral-bad, it was fatal.** The step came from the *mean* rate, and
a handful of triangles with all but collapsed coordinates took `rc1Coblston`'s from
120 units to 42,641. Every cobble then asked for a lattice a thousand times too
fine and was refused. It is the area-weighted **median** now, and a triangle still
more than three times from it is left uncut with its neighbours held against it.

**The estimate was not accurate enough for the budget to mean anything.** RC1 came
to 1,107,726 triangles against an estimate of 392,407. The area term used the
texture's average stretch rather than each triangle's own, and the ragged-edge term
was a perimeter over the cell size, which is half the answer for a triangle lying
across the lattice rather than square to it. Counted per triangle in texture space
the estimate lands within about 15% across the corpus, and — since a triangle
refused at one cell size is cut into everything it asked for at the next — the cost
is now monotonic in the cell size, which is what makes the solve valid at all.

With those three, the budget was raised from 400,000 to a million: the village
moves 1.23 units typically where it moved 0.32, at 4.4 units a cell instead of 7.5,
for about a second of that room's load and no measurable frame time.

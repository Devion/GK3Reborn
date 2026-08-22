# Head refinement

Characters get a subdivided head. Nothing else about them changes, no `.ACT` file is
touched, and every one of the 5,796 clips still plays.

```bash
GK3Reborn.Tools head-solve --source <GK3>/Data --output head-solve.json
GK3Reborn.Tools render-model --source <GK3>/Data --model GRA --heads 2 --output gra.png
```

In the game it is `SmoothHeads` in the settings, two by default, with `--heads N` and
`--flat-heads` on the command line to override it for one run.

## Why only the head, and why it is allowed at all

`Plan/05` rules out re-meshing the cast, for a reason that has not changed: **`.ACT` is
addressed by vertex index**. Change a character's topology and every clip that character
has becomes noise. 5,796 clips and 399 MB of them.

The head is the exception, because of something that had not been measured. A clip records
where every vertex of a head is on every frame — and every one of those recordings is a
*rigid motion of the authored head*. Nothing in a GK3 head deforms. So the vertex track is
not really vertex data at all: it is one rotation and one translation, written out three
hundred times.

Recover that motion and it will carry any mesh. The clip goes on addressing the 307
vertices it was authored against; the fit turns what it says into a transform; the
transform moves a head with four thousand. This is the smallest useful piece of the rig
solve `Plan/06` specifies — one bone, no clustering, no weight fitting — and it is worth
having on its own, because the head is where the problem is visible.

## What was measured

`head-solve` fits each character's authored head onto every recorded frame of every clip
that moves it, and reports what is left over as a percentage of that head's own width.

**All 56 models with head clips pass**, over 3,069 clips and 122,034 recorded frames. The
median model leaves 1.0% of head width; the worst leaves 4.1%.

| model | head mesh | width | clips | frames | median | p90 | p99 |
|---|---:|---:|---:|---:|---:|---:|---:|
| `gab` | 10 | 20.1 | 943 | 38189 | 0.65% | 1.42% | 4.89% |
| `gra` | 6 | 18.0 | 483 | 18238 | 1.35% | 2.41% | 4.35% |
| `mos` | 12 | 17.5 | 199 | 7848 | 1.52% | 3.06% | 4.53% |
| `mad` | 0 | 23.3 | 161 | 5708 | 2.03% | 5.00% | 6.82% |
| `vit` | 6 | 16.8 | 114 | 3653 | 1.29% | 3.50% | 4.45% |
| `rox` | 11 | 20.1 | 114 | 7023 | 0.79% | 1.27% | 1.90% |
| `eml` | 8 | 18.1 | 112 | 4333 | 2.83% | 5.87% | 7.59% |
| `est` | 7 | 17.7 | 109 | 3901 | 1.64% | 3.01% | 4.02% |
| `lar` | 10 | 15.7 | 94 | 3250 | 0.58% | 1.37% | 1.99% |
| `abe` | 10 | 16.6 | 83 | 3163 | 1.07% | 2.32% | 3.10% |
| `lh2` | 11 | 15.8 | 78 | 2788 | 0.82% | 2.18% | 3.08% |
| `wi2` | 10 | 17.4 | 59 | 1919 | 2.41% | 3.41% | 5.73% |

A percent or two is not deformation. It is the encoding's own quantisation: a one-byte
delta resolves 1/32 of a unit and the deltas accumulate along a clip, so a long clip drifts
by exactly this much whether or not anything moved.

The handful of frames that really do deform a head are all recognisable from their names.

| model | worst frame | clip |
|---|---:|---|
| `gab` | 17.18% | `GAB_GABTE3HDOFF` |
| `glb` | 15.52% | `GLB_GABSLEEPSOFA` |
| `dem` | 13.70% | `DEM_DEMTE6DISINTEGRATE` |
| `em2` | 11.04% | `EM2_EM2HALLEXITROOM` |

`GABTE3HDOFF` is Gabriel's head coming off. A rigid fit is the wrong answer for that frame
and there is no arguing with it, so playback refuses a fit whose leftover exceeds 8% of head
width and carries the head on the clip's transform track instead. The decision is taken once
per clip, from the first frame that shapes the head, so a character sitting near the
threshold cannot flicker between the two answers.

## The trap: the axis triad decides the fit if you let it

Every mesh group in the game carries three extra vertices at (60,0,0), (0,60,0) and
(0,0,60) — `Plan/06` §4.3, where they are stripped for the same reason. They belong to no
triangle, they are four times the size of a head, and they do not travel with it.

Include them in the fit and three points with enormous leverage outvote three hundred with
none. It does not fail loudly. It produces a stable, plausible number: the first run of this
survey reported nine of fifty-six models as deforming their heads, Mosely by 40% of head
width on a tenth of his frames, Roxanne by 6% on half of hers. Those numbers were consistent
across thousands of frames and looked exactly like a finding about the game.

They were a finding about the fit. With the triad dropped, Roxanne goes from 6.35% to 0.79%,
`sed2` from 22.73% to 0.10%, and all fifty-six pass.

`HeadRig.Sample` is what drops them, and `HeadPlaybackTests.TheAxisMarkersDoNotDragTheFit`
is what keeps them dropped — the markers are in every playback fixture, not in a case of
their own, so the fit has to be right in their presence rather than merely capable of it.

## What the subdivision does and does not do

Loop subdivision, two levels by default. Grace goes from 1,622 triangles to 7,592, Gabriel
from 1,750 to 7,720, Madeline from 1,539 to 6,264 — all of it in the head.

**Boundary vertices are pinned.** The textbook rule moves them along the boundary curve,
which is harmless where two submeshes meet — both sides move identically and the seam stays
shut — but not at the rim of the neck, which is the edge of the head shell with nothing on
the other side being refined with it. A rim that shrinks opens a hole in somebody's throat.
Pinning costs a slightly flatter surface within one row of triangles of a boundary and
cannot open a gap anywhere.

**Texture coordinates are interpolated linearly, not subdivided.** The face texture is
composited: `FaceController` blits eyes and a mouth onto it at pixel coordinates
`CHARACTERS.TXT` gives per character. Smoothing the UVs would move a character's mouth.

**Normals are welded across submeshes afterwards.** GK3 splits a head into a face, a
hairline, an ear and a patch of skin, and the authored normals agree across those seams to
0.0°. Each submesh is refined without seeing the others, so without a weld the refinement
would introduce a shading seam along the hairline that the original does not have.

**The head shrinks slightly.** Approximating subdivision pulls vertices towards the limit
surface; a head comes out two or three percent smaller. This is inherent to the scheme and
is the price of the rounder outline.

**It cannot invent detail.** Grace's hair is twenty triangles and Madeline's is thirteen.
Subdivision rounds that silhouette; it does not add a hairstyle.

## Why not a normal map instead

Because it was tried and measured, and it records nothing. See `mesh-enhancement.md`: GK3's
meshes already carry welded, smooth vertex normals, and a subdivided surface converges to
exactly those normals, so a tangent-space bake comes back flat — 74 of 76 textures over a
mixed sample. What subdivision changes is position and silhouette, and neither of those fits
in a normal map.

## Where it lives

| | |
|---|---|
| `Formats/Models/LoopSubdivision.cs` | The subdivision, positions and texture coordinates |
| `Game/Actors/HeadRefinement.cs` | Finding the head, refining it, building the rig |
| `Game/Actors/RigidFit.cs` | Kabsch, with Higham's iteration in place of an SVD |
| `Game/SceneUpdate.cs` | `Playing.Turn` — reading a clip as a motion |
| `Tools/Stages/HeadSolveStage.cs` | The corpus survey |

The head is found by `CharacterHead.Find`, which reads it off the textures a mesh is
painted with rather than from `CHARACTERS.TXT`. The two agree wherever both have an answer,
and the heuristic also covers the fifteen models the file does not list.

# Deriving light rigs from baked lighting

Stage C4b, implementing [ADR 0002](adr/0002-lighting-derived-from-lightmaps.md) as
amended for time of day.

```bash
GK3Reborn.Tools derive-lighting --source <GK3 Data> --workspace <dir>
```

Writes one rig per scene and timeblock to `content/lighting/<SET>.lighting.json`, in the
format the renderer reads, marked `derived` with a confidence per light. Corrections go
in the paired `.edits.json`, which this stage never writes (ADR 0006).

## Method

**Reduce each surface** to a world-space centroid, an area-weighted normal, its area, and
the mean luminance of its lightmap. 176,000 surfaces across the corpus become that many
samples of "a surface facing this way received this much light".

**Difference the timeblocks.** A night bake shows only artificial light, so subtracting it
from a daylight bake isolates the sun and leaves the practicals behind. Trying to explain
a single bake containing both at once is a far worse-posed problem.

**Fit the sun by linear least squares.** This is the part that works well. Lambertian
brightness from a distant source is `I = dot(n, d)`, which is *linear in d* — so given
many surfaces with known normals and measured brightness, direction and intensity fall
out of a three-by-three solve. No iteration, no initial guess, no local minima. Surfaces
are weighted by area so a wall counts for more than a doorknob.

**Cluster the practicals.** Point lights are not linear, because brightness depends on
distance as well as angle. Surfaces still lit at night are clustered by proximity and a
light is placed in front of each cluster, offset along its average normal. This is a
seed, not a solution: it puts a light roughly where one belongs so a human moves it
rather than creates it.

## Results

221 rigs, 1,856 lights. **No scene yielded nothing**, so none needs lighting authored
from scratch. 348 lights (19%) fall below the confidence threshold and want review before
use.

The output is physically coherent, which is the real test:

**137 of 138 fitted suns point downward.** One does not, and is flagged rather than
quietly corrected.

**The sun moves across the sky.** Of the 39 scenes carrying both a morning and an
afternoon bake, **all 39** show the sun's azimuth shifting, most by 160–176° — very close
to a full east-to-west traverse. That is not something the method was told to produce.
It is recovered independently per timeblock, from bakes made in 1999, and it comes out
looking like a sun.

| Scene | Azimuth shift, morning → afternoon |
|---|---:|
| PL4 | +175.8° |
| WOD | +172.9° |
| LER | −172.6° |
| RC1_A | −172.5° |

Elevation is less consistent — some scenes place the evening sun lower than morning, some
higher. Elevation is harder to constrain from surfaces that mostly face sideways, and the
original artists were not necessarily physically consistent about it either.

## What this does not establish

Mean confidence is **0.36**, which is modest and honestly reported. A good azimuth fit
does not mean the intensity, colour or elevation are right, and the practical clustering
is much weaker than the solar fit — it knows roughly where a light is, not what kind it
is or how far away.

None of this has been seen rendered. Physical coherence in the numbers is strong evidence
the method works, but the acceptance test is a scene lit by the derived rig next to the
original bake, and that needs the renderer. Until then these are proposals with a
confidence attached, which is exactly what ADR 0002 asked for and no more.

## Known gaps

Light the artists painted with no physical source — fill on a face, a glow with no lamp —
has no position to recover and will not appear in a derived rig. Those become authored
lights through the edit layer.

Shading painted into diffuse textures is the same problem one layer down, and worse: ray
tracing will add its own shadows on top of the painted ones. That has to be caught during
texture enhancement rather than here.

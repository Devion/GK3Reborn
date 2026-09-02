# Fire

Every fire in Gabriel Knight 3 is one flat quad, always facing the camera, painted with a
bitmap that a behaviour script cycles through two to eight frames of for as long as the room
is loaded. It animates and nothing else happens: the light in the room is perfectly steady,
and nothing rises off it. Both are what make a fire in this game read as a picture of one
pinned to the air.

This gives the nine rooms that have a fire in them a light that wavers and smoke and embers
that leave. Nothing here needs any content built: the fires are found in the models the
scene already loaded, and a sprite is a disc drawn by arithmetic rather than a bitmap
somebody has to author.

```bash
# What a room's fires are, and which of its lights waver with them.
GK3Reborn.exe --scene TE4 --timeblock 205P --frames 2 --lights
#   Fire: 1 open flame(s), 0 of the artists' lights wavering with them and 1 lit that had none
#     flame te4firetransp at -110,39,-213 12.6 tall, swings 25% at 1.3 Hz
#     light flame:te4firetransp r=6.6 i=1.10 reach=177 flickers 25% about 0

# And across the whole game.
dotnet run --project tools/GK3Reborn.Tools -- check-scenes --source ../GK3/Data --deep
#   9 rooms have an open flame in them, 49 flames between them, 105 of the artists'
#   lights wavering with one and 1 fires lit that had no light of their own:
#   CS5 x8, CS6 x12, CS8 x7, DIN x3, MA1 x6, RL2 x1, TE1 x1, TE4 x1, TE6 x10
```

## Finding the fires

Three bitmaps are an open flame, and between them they are every fire in the game:

| Bitmap | Where |
|---|---|
| `CS5FLAME`, `CS5FLAME01`, `CS5FLAME02` | The generic flame: CS5's eight hanging lanterns, CS6's twelve, CS8's six, the dining room's three chafing dishes, MA1's brazier ring, TE6's candles |
| `TE4FIRETRANSP1`–`8` | The temple's bowl of fire |
| `TE2FIRESM*`, `TE2FIREMED*`, `TE2FIREHI*` | A fire in three sizes with a blend between each pair, shared by the bar's fireplace, the chapel's and TE1's brazier |

**No room's own geometry carries one.** Every fire in the corpus is a model the scene
places, which is why finding them is a walk over the placed models and not over the BSP.

**The authored texture is not enough.** `CS8_FIRE` ships painted with `RL2FLOOR`,
`RL2_FIRE` with the same and `TE1FIRE` with `TE1CLMS`; all three become fire only when the
first `[MTEXTURES]` line of their behaviour script lands. So a model is a flame if *any*
texture it ever draws is one, and `Flames.In` reads the `gas=` script to find out.

Two more things the corpus insists on. A flame card is usually modelled **twice, back to
back**, so it draws from either side — counted as two, a room gets twice the light and twice
the smoke. And `TE6_CANDLES` is **five candles in one model**, a hundred units apart, so the
merge cannot simply be "one model, one fire": cards are merged when they are within two
units of each other and not otherwise.

## The flicker

The artists lit these rooms twice: a flame card, and — usually — a light standing inside it.
CS5's lanterns each carry a `cs5_lantern_light01` a third of a unit from the flame; each
chafing dish has a `chafing_dish_special` in the sterno; the chapel's fire has
`firelight_omni` eight units away and the tomb's fourteen candles a `candleside_glow_special`
apiece. This pairs each light with the fire it stands in and marks it to waver.

**The threshold is measured rather than guessed.** Over every scene asset of the nine rooms,
161 lights stand within reach of a fire and the furthest of those is 10.3 units away —
`candle_omni02`, which is a candle's light. The nearest light that is plainly something else
is 16.7, `omni04` in MA1 and in CS6. The threshold is thirteen.

**One fire in the game has no light of its own.** The temple's bowl of fire is lit entirely
by the bake, with the nearest rig light 68 units away and lighting a lantern across the
room. Every other fire is lit once the right asset is loaded — the bar's fireplace looks
unlit under RL2's morning rig and has two lights five units into it under the evening one,
which is the only rig its fire is ever placed with.

A fire that has none gets a light synthesized for it, and **its mean is zero**: it
contributes the waver and nothing else, so the room is exactly as bright on average as it
has always been and a fire that used to be a still picture now moves the wall beside it.

### What the shader does

A packed light carries a fourth vector: how far it swings, what it settles at, how fast, and
a number of its own. `(0, 1, 0, 0)` for a light that stands still, which multiplies it by
exactly one for ever — so a room with no fire in it is shaded by arithmetic that cannot
change what it used to draw.

The wave is four sines whose rates share no common multiple, amplitudes summing to one. Two
flames are told apart by **rate** rather than by phase, and that is not a stylistic choice:
every term is a sine of a phase starting at nought, so the multiplier at `t = 0` is exactly
the light's bias, and **a still frame rendered at the start of the clock is the picture this
renderer has always drawn**. A phase offset would move the first frame, and comparing frames
is how everything in this project is checked.

Larger fires flicker **more slowly** and further: a candle is nervous, because a small flame
is pushed about by every draught in the room, and a bonfire surges, because the mass of
burning gas above it takes time to move. Reading it the other way round is the single thing
that makes an artificial fire look artificial. A candle swings about a tenth at 2.0 Hz; the
bowl of fire a quarter at 1.3 Hz.

### Where a room is lit by its 1999 bake

At ray-tracing tiers the room is lit by the rig and the flicker arrives for free. With no
rays the lightmap *replaces* the rig on scene geometry, so a fire would light nothing: the
bake holds the fire's average light and cannot move.

So `EvaluateRig` returns a second term — the part of the total a fire is responsible for
*moving*, measured from where each flame light settles rather than from zero — and the baked
branch adds it on top of the lightmap. It comes out as `contribution × swing × wave` for
both kinds of flame light, which is why one line does both, and it is exactly zero for every
steady light in the game.

Shadow rays weigh a light by its **bias** rather than by where it stands this instant. A
weight that moved with the flicker would make a pixel trace towards a different light from
one frame to the next, which the temporal filter reads as noise; and a synthesized light
settles at nothing, so it is correctly never worth a ray — it is a modulation of the bake,
and a bake casts no shadows to sample.

## The smoke and the embers

Two kinds leave every fire and they behave nothing alike.

**Embers** are small, bright, short-lived and thrown. They leave fast, slow down hard, cool
from yellow-white through orange to a dull red, and go out within a second or two. They are
wholly additive: an ember is light rather than a thing, and blending one over the wall
behind punches a dull orange hole in it.

**Smoke** is large, dark, slow and long-lived. It drifts up, spreads as it rises, is lit
orange from below near the fire and grey by the time it is above it, and fades in over the
first fifth of its life so that a puff does not appear out of nothing at the top of the
flame. A candle's is a wisp at 11% opacity and the bowl of fire's is a column at 32%.

How much of either is entirely a question of how big the fire is — `Flame.Size`, from the
card's height in world units, which runs from a chafing dish's sterno at 1.4 to the bowl of
fire at 12.6.

A fire the room is not drawing makes nothing. TE6 keeps its candles hidden until a script
lights them, and the emitters follow the placed models' own visibility rather than needing to
be told.

**Nothing about it is random between runs.** Each fire draws from a stream seeded from where
it stands, so the same room in the same state produces the same smoke on every machine and
in both backends.

## The one blended pass

The renderer is deferred and its material pass cannot blend: every surface in the game is
opaque or cut out against a hard alpha test, which is what the 1999 art was drawn for. Smoke
is the one thing in this project that genuinely needs a blend, so it is a forward pass of its
own — `ParticlePipeline` on Vulkan and `D3D12ParticlePass` on Direct3D, from one pair of
shaders — recorded after the picture is composed, against the depth the room left.

**One blend does both kinds.** Colours arrive premultiplied and the blend is
`ONE, ONE_MINUS_SRC_ALPHA`, so what a fragment writes in the alpha channel decides what it
does: an ember writes zero and is added to the wall behind it, smoke writes its coverage and
hides it. Two blends would mean two pipelines and a sort that kept them apart, and embers
would still have to be drawn after the smoke they are flying through.

**There is no texture.** A sprite is a disc with a soft edge and, for smoke, two octaves of
value noise cut out of it — a few lines of arithmetic against a bitmap that would have to be
authored, packed and shipped. It also means a particle is as sharp as the display is at any
size, which a 32-pixel puff from 1999 would not be.

Depth is **tested and never written**. A puff behind a wall is hidden by it; two puffs in
front of one another both draw, which is the whole point of blending them, and a sprite that
wrote depth would delete every sprite behind it. Smoke is handed to the pass sorted furthest
from the eye first, because it is blended over what is behind it.

## What this does not do

**The particles have no motion vectors.** The G-buffer's motion target was written by the
room and read by the denoiser long before this pass runs, and a smoke sprite has no surface
to report the movement of. A temporal upscaler therefore sees them as pixels that changed
without moving and smears them rather than resolving them, so a spark leaves a short trail
with DLSS or FSR on. The pass is where it is because the alternative is after the upscale,
where there is no depth at the right size to test against and every fire in the game would
burn through the wall in front of it. Trails behind a spark are the smaller of the two
faults.

**There are no soft particles.** A puff that intersects a wall is cut off at a hard line
rather than fading into it. Fixing it means sampling the depth target from the fragment
stage, which is a descriptor set this pass does not otherwise need at all.

**Smoke does not know about the lantern around it.** CS5's and CS6's flames are inside glass
housings, and the smoke rises through the glass rather than out of the top of it. It is
faint enough at a lantern's size to read as heat rather than as a fault.

**A fire more than 1,200 units from the camera is not simulated.** It is about "in this part
of the room" rather than about draw distance: it is what keeps CS6's twelve fires from all
being simulated while the camera is looking at one of them.

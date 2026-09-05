# Fog

Gabriel Knight 3 has no fog anywhere. The air in every one of its two hundred rooms is
perfectly clear at every hour of its three days, which is fine for a hotel lobby, wrong for
the two places in the game that are underground and wet — the cellars under Château de
Serres and the chasm the bridge crosses in the temple — and wrong again for the valley at
two in the morning, which is the one hour of the story that happens in the dark.

This gives six of those rooms a layer of fog that is marched per pixel and lit by the room's
own lamps. Nothing here needs any content built.

```bash
# The rooms that have any, and what the layer is.
GK3Reborn.exe --scene CS5 --frames 2 --data ../GK3/Data
#   Fog: lying to y=4, thinning over 12 units, 0.0065 a unit in 32 steps

# Four of them are the small hours' weather, so the hour has to be asked for.
GK3Reborn.exe --scene CEM --timeblock 202A --frames 2 --data ../GK3/Data
#   Fog: lying to y=8, thinning over 16 units, 0.0022 a unit in 32 steps
GK3Reborn.exe --scene CEM --timeblock 102P --frames 2 --data ../GK3/Data
#   (nothing: the same cemetery on a bright afternoon)

# The A/B, which is the only way to see what a layer is worth.
dotnet run --project tools/GK3Reborn.Tools -- render-scene --source ../GK3/Data \
    --model TE5 --camera TILES --output murk.png
dotnet run --project tools/GK3Reborn.Tools -- render-scene ... --no-fog --output clear.png
```

## Which rooms, and why it is a list

**A table, and it has to be.** Everything else this renderer adds to a room is derived from
something the room itself says: a flame is found by the bitmap it is painted with
(`fire.md`), a railing by the holes in its texture (`cutout-cards.md`), a window light by
the name the artists gave it (`lighting-derivation.md`). GK3 says nothing anywhere about
fog. No scene file has a word for it, no texture implies it, and no measurement of the
geometry tells a dry cellar from a damp one. What decides that a place is damp is what the
place *is*, which is a reading of the game rather than a property of its files — so
`Game.SceneFog` is a short list that says so out loud instead of a heuristic pretending to
have found something.

It is deliberately short. Fog is the one effect here that touches every pixel of the frame,
and fog in a room that does not want it is worse than none at all.

| Room | What it is | When | The layer |
|---|---|---|---|
| CS5 | The tunnel under Château de Serres: brick barrel vaults, a stone floor, lanterns in wall brackets | always | Damp on the flagstones — top at `y = 4`, thinning over 12, density `0.0065` |
| TE5 | The temple's bridge room, and the shaft the bridge crosses | always | Murk far down the chasm — top at `y = -280`, thinning over 40, density `0.0140` |
| CEM | Rennes-le-Château cemetery: walled on four sides, flat, and no lamp in it | `202A` | Mist standing between the graves — top at `y = 8`, thinning over 16, density `0.0022` |
| RC1–RC4 | The village, seen from four sides: cobbles, stone walls and street lamps | `202A` | Mist under the lamps — top at `y = 6`, thinning over 12, density `0.0006` |
| WOD | Lady Howard and Estelle's dig site: a bowl of open ground with a lit tent in it | `202A` | Mist across the hollow — top at `y = 8`, thinning over 14, density `0.0010` |
| POU | Poussin's Tomb, on a shoulder with the land falling away under the road | `202A` | Mist below the road — top at `y = 8`, thinning over 14, density `0.0008` |

**Heights are absolute, and the corpus makes that safe.** Every one of these rooms stands its
walking floor within half a unit of `y = 0` — measured with `render-scene --pick`, not
assumed — and none is more than one storey, so a layer placed against a world height lies on
the floor everywhere in it. A room that *climbed* would need the layer to climb with it,
which is a heightfield rather than a plane and is not what any of these wants; the outdoor
four roll rather than climb, and a plane through rolling ground is what a mist lying in it
actually is.

The sign of the top is the whole difference between the two underground ones. CS5's is above
its floor, so the mist lies on it; TE5's is far below, so the layer is deep in the shaft and
the hall the player walks through keeps its own air.

## The hour, and why the room alone is not the question

Two of these rooms are underground and four are outdoors, and that is the whole of the
difference. Underground is underground at every hour. The outdoor four are the small hours'
*weather*: the same cemetery is a walled yard on a bright afternoon on day one, and the
player is in it in daylight far more often than at two in the morning.

So `SceneFog.For` takes a `Timeblock` beside the name, and the gate is one block —
`SceneFog.SmallHours`, day two at two. **Sixteen of the corpus's seventeen blocks run from
seven in the morning to six in the evening**; the seventeenth is `309P`, nine at night, and
it reaches nothing but two hotel bedrooms. There is no other nocturnal outdoor hour in the
game to gate on, and no gradient to interpolate along.

This is not a subtlety. `--scene CEM --timeblock 102P` with the night layer forced on is a
two o'clock sun, hard shadows on the grass and a bank of fog between the stones — the one
failure this table exists to avoid, arrived at from a different direction.

**A caller with no story state gets no weather.** `render-scene --model CEM` with no
`--timeblock` draws clear air, and `--timeblock 202A` draws the mist. An unknown hour is
treated as daylight rather than as night on purpose: a room drawn without fog is the room as
it shipped.

## Density belongs to the room, not to the weather

There is no outdoor preset here, and the four night rooms deliberately do not share one.
What a layer costs the picture is set by **how far a ray travels inside it before it hits
something**, so:

- CEM is walled on four sides with nothing in it more than a few hundred units off, and
  carries `0.0022` — nearly four times the village's.
- RC1–RC4 are enclosed at eye level and open along their length, and want `0.0006`. At the
  cemetery's figure the streets are pea soup with the cobbles gone by the second house.
- WOD's hollow is four times as far across as the cemetery is, and takes `0.0010`.
- POU looks out over most of a kilometre of hillside that has to stay a hillside: `0.0008`.

The same number in two rooms of different sizes is two different pictures, and the ones that
were tried and rejected are as much the evidence as the ones kept. `0.0025` in the open wood
at L'Fauteuil du Diable swallows the trees at thirty metres, which is why **ARM has no
layer**; the same figure in a rock cut (MCB, LMB) buries the mid-ground; `0.0065` — CS5's own
— in the winepress room reads well and then hides the base of the winepress, which is a
thing the player has to work with. GRI, the garage, takes a cellar layer perfectly happily
and was left out because a garage is not a place a mist belongs.

**How far below is not a detail.** TE5's numbers are worth writing down because the picking
was the only way to get them: the walkway is at `y = 0`, the bridge deck at `0.6`, its parapet
tops out at `8`, and `te5_chasm_bottom` is at **`y = -725`** — eighteen metres of shaft. The
first attempt put the top at `-15`, which is *technically* in the pit and still wrong: the
murk laps at the lip, so the drop ends where the floor does and the bridge appears to span a
bank of cloud. At `-280` there are three or four metres of visible wall in the shaft before
the murk closes it, and the bridge with the whole of its underside stands clear above.

Not lower than that either. At `-400` the murk is a faint band at the bottom of the frame from
every camera that can see the pit, and the pit is an ordinary dark hole again — which is the
fault this started as.

## The layer

Density falls off with height and nothing else:

```
sigma(y) = density * exp(-max(y - top, 0) / falloff)
```

Everything below `top` is at full density and everything above it thins. Two numbers, and
between them they say *damp lying on a floor* and *a shaft full of murk* — which is the whole
reason the volume is a height rather than a box. A box has corners the player can walk round.

**Low is a smaller number than it looks.** CS5's first attempt used a falloff of 22, which is
a perfectly reasonable-looking depth and leaves a percent of the layer at head height. That
percent, against a bracket lamp seen nearly end-on down a tunnel, washed the vault and half
the wall behind it. Twelve leaves a tenth of the density at the knee and a hundredth at the
eye. **What is above the mist has to be clear**, or the room reads as dusty rather than as
wet.

## What lights it

The room's own rig, read out of the same three buffers the walls are lit from, through the
same clustered-grid lookup (`SceneLightGrid`). Same linear range squared, same spot cone,
same exemption for a distant key, same flicker — a fire that moves the wall behind it and not
the mist in front of it is two fires.

**Nothing about the fog is a colour it is drawn in.** `FogVolume.Colour` is what a scattering
event returns, not what the fog looks like: what it looks like is whatever the lamps put into
it. A fog with a colour of its own is the flat grey wash `rendering.md` already rejected for
the reconstructed horizon — it paints the lit end of a corridor and the dark end the same,
which is the one thing that stops fog reading as depth.

Two things that are not the rig:

**The phase is Henyey-Greenstein normalised so that isotropic is one**, rather than a quarter
of pi. That is deliberate and it is not physics: these lights are the artists' own, authored
in 1999 against a linear-decay renderer and tuned by what the walls looked like, and a phase
carrying its own `1/4pi` would put the fog two orders of magnitude below the surfaces beside
it. One at `g = 0` makes a lit step of fog comparable to a lit surface. The peak is
`(1-g^2)/(1-g)^3` of that — two and a half at `g = 0.35` and **seven and a half at 0.55**,
which is not a halo round a lantern but a white hole with the doorway lost inside it. Both
rooms sit at 0.35.

**The layer shadows itself**, and it is what makes a deep one dark at the bottom. Each light's
contribution is attenuated by the fog standing between it and the sample, and the ambient
floor by the fog above it. The height profile integrates in closed form — everything under
the top counts for its own length and everything above it for one falloff's worth of what is
left — so it is two exponentials rather than a second march.

Without it the temple's pit came out **white to the bottom, brighter than the hall around
it**: every lamp in the room reaches every sample unimpeded, and a distant key, which is
exempt from range falloff by design, then lights the floor of a chasm exactly as brightly as
its lip. What that draws is not fog, it is a lit cloud sitting in a temple.

What the self-shadowing does not know about is the walls: a lantern on the far side of a pier
still reaches through it. That is a shadow ray's job and this pass does not trace one — see
below.

## The march

One triangle over the finished room, blended. Per pixel: unproject the depth, clip the ray to
the part of it that is in the layer, and step.

**The march is clipped to the layer rather than run over the ray.** A cellar's damp is a
metre deep in a room forty metres long, so a march spread evenly over the ray would put one
sample in the fog and thirty-one in clear air above it. The near end of the interval is where
the ray drops below `top + 6*falloff` — a quarter of a percent of the density, less than the
dither already hides — and the far end is whatever the room drew. Thirty-two steps then
resolve the layer instead of the room.

Each step is integrated as a slab of its own rather than point-sampled, so the answer is very
nearly independent of how many steps there are: halve them and the picture dims by a fraction
of a percent rather than by half.

**Nothing varies with the frame.** The dither that hides the banding is interleaved-gradient
noise of the pixel and not of the clock, and the density noise drifts on the same seconds the
flames flicker on — which a headless render leaves at nought. Two renders of one room are the
same picture, which is the basis on which everything in this project is compared, and there is
a test asserting it.

The density noise is one octave of value noise on an integer hash. Integer rather than the
sine-fract hash the cat's coat uses, because this is asked eight times a step and thirty-two
steps a pixel — half a billion of them at 1080p, and half a billion transcendentals is a pass
nobody can afford. It is also exactly reproducible, which the sine one is not: its accuracy is
the driver's business and two cards disagree about the last bits.

## Where it sits in the frame

After the room and **before the smoke**. A fire's plume stands where the fire does; fogging it
against the wall behind it would dim its near side by however far away that wall happened to
be.

At render resolution, before the upscale. The depth it marches to is the room's own and exists
at no other size, and fog is part of the picture an upscaler is meant to be reconstructing
rather than something laid over its answer.

Both backends draw it from one shader. The Vulkan pass binds the frame set's three light
buffers directly and the depth beside them; the Direct3D one writes the same four descriptors
into a table of its own, which is why it is not a `D3D12ScreenPass` — that class covers every
full-screen pass reading nothing but textures, which is all the others. `FogLayout` is the one
statement of what is bound, and `FogConstants` the one statement of what is said: 192 bytes,
inside what both backends take.

Measured at 1280×720 with ray tracing on High:

| Room | Without | With | Cost |
|---|---|---|---|
| TE5 | 7.7 ms | 8.0 ms | **0.3 ms** — six falloffs over the top is still forty units under the walkway, so only the pixels actually looking into the shaft march at all |
| CS5 | 7.3 ms | 7.0 ms | **nothing measurable** — the two runs differ by less than they differ from each other, because the layer is thin and most rays leave the band within a few steps of entering it |
| CEM | 10.0 ms | 11.1 ms | **1.1 ms**, and the most this costs anywhere: a walled yard puts the whole frame inside the layer, so every pixel marches its full thirty-two steps |
| ARM (rejected) | 10.5 ms | 10.6 ms | **0.1 ms** — the opposite case and worth recording. Open country is mostly sky and distant hill, which is above the ceiling and never enters the march at all |

The outdoor rooms are the expensive ones and it is enclosure rather than size that decides
it. A frame that is all layer is thirty-two steps a pixel; a frame that is mostly sky is a
clip and a branch.

## What is not here

**Shadow rays.** The fog is not occluded by geometry, so there are no shafts with a shape cut
into them. None of these rooms has the opening that would throw one — two are a sealed cellar
and a sealed chamber, and the four outdoors are lit by a sky and by street lamps standing in
the open — and the layer's own extinction is by far the larger term wherever there is enough
fog for the difference to show. A room with a *window*, which is the church or the attic
rather than any of these, would want it; the device already has the acceleration structure
bound one set over. It is the obvious follow-up.

**A mirror does not fog.** The reflection pass draws the room a second time and this runs
once, over the frame. None of these rooms has a mirror in it.

**A layer that follows a floor.** Every room here is flat or rolls gently. A room with a
staircase would want the top to be a heightfield rather than a plane, and the closed-form
self-shadowing above is exactly the thing that would have to be given up for it. It is also
what rules out the rock cuts at MCB and behind Larry's house, where the ground climbs through
the layer within one screen's width.

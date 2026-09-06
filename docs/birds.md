# Birds

Every sky in Gabriel Knight 3 is a painting and nothing in it moves. A village square with a
perfectly still sky over it reads as a photograph of a village square, however good the
painting is, and the thing that fixes it is not detail — the birds here are between six and
twenty pixels of silhouette — but movement that is not the player's own.

Eleven rooms have birds over them in daylight and the other two hundred have nothing. What
gets drawn is a flock of camera-facing sprites through the pass the smoke already uses, so
one behind a roof is hidden by the roof and one against the sky is not. Nothing here needs
any content built: a bird is a shape cut out by arithmetic in the fragment stage.

```bash
# What is up over a room, and where it is flying.
GK3Reborn.exe --scene RC1 --timeblock 110A --frames 2
#   Birds: 14 over RC1, wheeling at (3313, 658, -2018) within 1802 of it,
#          30 across at 4.7 beats a second

# The same square with an empty sky, which is the A/B.
GK3Reborn.exe --scene RC1 --timeblock 110A --frames 2 --no-birds
```

There is also a **Birds in the sky** row on the Picture page, and it takes effect on the
frame it is switched — the flock is stepped whether or not it is drawn, so turning it off and
on again is the same room a moment later rather than a new one.

## Which rooms, and when

**Which rooms is a table, for the reason `SceneFog`'s is one.** Nothing in GK3's data says
where a bird would be. The scene files name a sky, a floor and a light rig and no living
thing that is not a character; no texture implies one; and no measurement of the geometry
tells a courtyard somebody would sit out in from a courtyard nobody would. Fifty-one of the
game's scene assets name a daylight sky, the hotel bedrooms and the museum among them, and a
bird over a room whose sky is a painting seen through one window is a bird nobody will ever
see being simulated all afternoon.

| Kind | Rooms |
|---|---|
| Swifts round the rooftops | RC1, RC2, RC3, RC4, MAG, MA3, CEM |
| Soaring birds over open country | POU, WOD, CD1, MCF |

**When is not a judgement — the game says it, out loud, in sound.** RC1, RC2 and the cemetery
already play birdsong: `RC1BIRDAM.STK` plays `RCBirdAM` every ten to sixty seconds through
the morning blocks, `RC1BIRDAFTNOON.STK` plays three more through the afternoon, and the
evening and the small hours get `RC1OWL.STK` and `RC1CRICKETS.STK` instead. The artists put
birds over these rooms in 1999 and could only afford to do it in sound. So this puts
something in the sky at the hours the room is already singing, and nothing at the hours it
hoots.

The hours are seven in the morning to six in the evening — the same window `Sunlight` places
a sun in, and it has to be the same one: a bird in the sky of a room lit for dusk is lit by a
sun that is not there. The art agrees. Every daylight asset in the corpus is painted against
a sky named `_M` or `_A` and every other against `_E` or `_N`, and those are measurably
different things: `RLC_M` and `RLC_A` average 204 and 214 over their upper face, `RLC_E` 55
and `RLC_N` 21.

**Two kinds, and the difference is the place rather than the species.** Over the village the
birds are small, fast and tightly bunched — what a French hill village looks and sounds like
in summer, and what `RCBird1Aftnoon` is a recording of. Over open country they are large,
slow, far off and few. The same numbers in both places give a village full of buzzards or a
valley full of gnats.

## Where the flock flies

*Which* rooms have birds is a reading of the game. *Where the sky is* over those rooms is a
measurement of them, and every number below comes out of the room rather than out of a table.

**The player never moves the camera in this game.** A room is five or six fixed shots
pointing where the artists pointed them, and that is the single fact everything here turns
on. The first attempt centred the wheel on the middle of the walkable ground, which surrounds
the shot instead of standing in it — and a ring around the eye is a ring of which about a
fifth is in front. Measured at Poussin's tomb: five of six birds were *behind* the camera and
the sixth was off the side of the frame, for every frame of a five-hundred-frame run.

So the wheel is put one of its own radii along the way the room looks, which stands the
camera on the near edge of it and leaves the far half in the shot. Where the cameras disagree
— a square with shots all round it — the average comes back to the middle of the square,
which is what that case wanted anyway. **The shot the room opens on counts for three of the
others**: it is the one every player sees and the only one some of them ever see.

**And a fixed part of the way up the frame, not along the camera's own aim.** Following the
pitch would put the flock in the middle of every shot, ground included. A third of the way
from the middle of the frame to its top edge is the band of sky a shot actually contains,
which is the difference between the tomb's arrival camera — pitched twelve degrees down at a
road — showing three birds and showing none.

### Over the roofs, not among them

> "birds are flying too low, going through building geometry in RC3 museum"

**A roofline for a whole village is a number that is right in the square and wrong in the
lane.** RC1's flock flies over an open square whose roofs are about 250 and RC3's over a
walled street whose sides run past 500, and the room's own average is what put the second
flock among the walls. So the roofline is measured **under the flock** — the tallest thing
inside the wheel, a half-percent off the top so that one aerial does not lift a whole flock
by ten metres. Where the flock is aimed out over open country and there is nothing beneath it
at all, the room's own skyline answers instead.

**Lifting the flock without moving it out puts it straight overhead.** The aim above stands
the birds at the right angle; raising them to clear a roof and leaving them where they were
turns that angle into whatever it becomes — measured over the Tour Magdala's square, seven
times the height of the frame, and nothing in the shot at all. So the wheel is pushed out by
however much the roofs pushed the flock up, and it is the *angle* that is preserved.

**And no further than seven tenths again.** The angle is what this holds; distance is what it
costs. At two and a half times, RC1's flock cleared the Tour Magdala from 2,600 units away
and every bird in it was six pixels of haze. Seventeen hundred is twelve pixels and
twenty-two degrees up, and that is the whole trade.

## What makes it read as birds

**The wheel, and not the flocking.** Pull together, keep apart, match your neighbours — that
gives a cloud that mills about, which is what insects do. Birds over a village go *round*,
all of them the same way, and one number carries it.

**Strung out round the circle rather than orbiting it in a lump.** A single tangent taken at
the flock's middle, with the birds held together hard, gives a tight swarm — and a tight
swarm over a room with five fixed cameras is a sky with fourteen birds in it or with none,
minute after minute. Taken at each bird's own place in the wheel it gathers into two or three
loose knots that drift round and change size, which is both what swifts do and what keeps
something in most of the shots.

**It banks.** How far over a bird is leaning *is* how hard it is turning, and it is what
makes a wheeling flock read as a wheel rather than as a carousel: a bird coming round the
near side shows the eye its back. Measured off the turn it actually made rather than the turn
it was asked to make, in the horizontal only — rolling for the climb puts a bird on its side
every time it tops out — and eased, because the steering is a sum of five terms and a bird
whose wings twitch is a fly.

**It flaps to climb and glides when it does not have to.** A silhouette beating at a steady
rate for ever is a metronome. A gliding bird finishes its stroke first and holds there, wings
level; stopping wherever it happened to be leaves a bird hanging in the sky with one wing up.
Small wings beat faster — a swift's about seven times a second and a buzzard's about three —
and reading that the other way round is the single thing that makes an artificial bird look
artificial.

**And no two of them beat together.** Fourteen birds in step is a shutter rather than a
flock.

### The silhouette

There is no texture. A bird is two tapering blades whose tips rise and fall with the beat and
sweep a little back as they go out, and a short ellipse of a body laid along the flight
direction — a dozen lines of arithmetic against a bitmap that would have to be authored,
packed and shipped, and sharp at any size, which a 32-pixel bird from 1999 would not be.

It is drawn through the pass the smoke is drawn through, which needed one new thing.
`Particle.Shape` used to be *how additive is this*, nought for smoke and one for an ember;
now **nought to one is a disc and two and above is a bird**, with the fraction above two the
point its wings have reached. One channel rather than a fourth vertex attribute, because a
bird is the only thing this pass draws that is not a disc and every sprite in the game would
have paid sixteen bytes a corner for it. The disc values are exactly the numbers they always
were, so a room with no birds in it is drawn by the arithmetic that has always drawn it.

Not black, either: a bird against a bright sky is very dark and slightly blue, because the
only light on its underside is the sky. Pure black reads as a hole in the picture rather than
as a thing in front of it. And it fades with distance, which is aerial perspective rather
than a draw distance — there is half a kilometre of air in front of a bird on the far side of
the wheel.

### Which way up a bird is drawn

A sprite has one angle and a bird has two directions — where it is going and where its
wingtips are — and only one of them can be right. **The wings win**: they are the whole width
of the silhouette and the body is a twelfth of it, so the sprite's own x axis is laid along
the bird's wing axis, which for a level bird is the horizontal at right angles to its flight.

Turning it by the *heading* is what a bird flying at the camera exposes: its heading projects
to almost nothing, so the angle swings about on rounding alone and a bird a hundred metres
off stands on its wingtip for a frame and then lies down again.

**The wings have the opposite degenerate case and it took a screenshot to see.** A bird
flying straight across the view points its wings at the eye; they project to nothing, and the
sprite — which cannot foreshorten — was drawn as a vertical mark. What is actually seen there
is a bird side-on, which is a dash, so the sprite is laid along the flight direction instead,
and the two answers are blended across the band where the wings are within about twenty
degrees of edge-on so that it turns rather than flips. The band is measured on the *unbanked*
span: the lean tips the wings up and down the frame without saying anything about which way
they point, and taking that as the wing direction is how the degenerate case went unnoticed
through two fixes.

**A bird also does not go straight up.** Five terms of steering are summed and two of them
are vertical, so the climb could take the whole of a bird's speed — and one flying vertically
has its wings edge-on to a camera beside it, which is that same vertical mark arrived at from
the simulation rather than from the projection. It is held to about a third of its speed,
which is a steep climb for something that has to keep flying, and the rest is given back to
the horizontal so it does not slow down for it.

## Nothing about it is random between runs

The flock is seeded from where it wheels — the way a fire's smoke is seeded from where the
fire stands — and stepped at a fixed sixtieth of a second whatever the frame rate, with the
remainder carried to the next call. So the same room at the same elapsed time is the same
flock on every machine, in both backends and at any frame rate. That is not tidiness:
comparing two renders of one room is how everything in this project is checked, and a flock
that depended on how long a frame took could not be compared with itself.

A frame that took longer than a quarter of a second is a scene load, a movie or a window
being dragged. The time past that is dropped rather than carried: running the flock through
the whole of it costs more than the frame that is already late, and the birds arrive a little
behind where they would have been, which nobody can see and nothing depends on.

## What this does not do

**A bird has no motion vectors.** The G-buffer's motion target was written by the room and
read by the denoiser long before this pass runs, and a sprite has no surface to report the
movement of. A temporal upscaler therefore sees a bird as pixels that changed without moving
and smears it, so birds trail with DLSS or FSR on — the same trade `docs/fire.md` describes
for a spark, and worse here, because a bird is persistent where a spark is not.

**A bird cannot foreshorten.** The sprite is square and the silhouette drawn in it is always
the full span, so a bird seen along its wings is drawn side-on rather than end-on. See
above — it is handled by laying the sprite differently, not by squashing it, because squashing
needs a second size on the vertex and every sprite in the game would carry it.

**A bird more than about 5,500 units away is not drawn at all.** It has faded into the haze
by then and is a fraction of a pixel across, and a sprite smaller than a pixel does not fade
out, it flickers.

**The reconstructed horizon takes the far tail of the depth buffer** — every terrain fragment
lands in [0.999, 1) — so a bird more than about a thousand units off that is seen *against a
hillside* rather than against the sky is hidden by it. It does not arise in practice: the
flock flies above the skyline in all eleven rooms, and the room's own geometry uses ordinary
depth, so a bird passing behind a real building or a real hill is occluded correctly.

**Nothing lands, and nothing reacts.** The birds do not know where the walls are — they are
held in the wheel and hidden by whatever is in front of them — and no door, gunshot or
conversation puts them up. Each would need the flock to know about the room, which it
deliberately does not.

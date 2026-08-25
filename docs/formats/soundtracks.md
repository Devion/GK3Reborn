# Soundtracks (`.STK`)

269 files, 5,755 steps, named by 554 scene files. A soundtrack is **not a piece of
music**: it is a little script the game walks in order and then repeats, building room
tone out of clips. R25's afternoon reads:

```text
[WAIT]
MinWaitMS=1000
Repeat=1

[SOUND]
Name=R25Theme1
Volume=80.0
Repeat=1
StopMethod=1
FadeOutMS=3000

[WAIT]
MinWaitMS=5000
MaxWaitMS=10000

[SOUND]
Name=R25Mood1
…
```

Wait a second, play the room's theme once, wait five to ten seconds, play a mood. Going
round again with a different wait each time is what keeps a hotel room from sounding
like a loop.

## Sections

| section | what it is |
| --- | --- |
| `SOUNDTRACK` | `SoundType=Music`, `Ambient` or `SFX` — which volume slider it obeys |
| `WAIT` | do nothing for `MinWaitMS`, or a random time up to `MaxWaitMS` |
| `SOUND` | play one sound |
| `PRS` | **a run of these is one step**: pick one of them at random |

`PRS` is the only one that is not what it looks like. Consecutive `[PRS]` sections
accumulate until some other section ends the run, so reading each as its own step would
play all three of the vampire's hisses at once instead of one of them. Repeat and looping
are dropped inside a `PRS`, as they are in the original — they mean nothing to one of
several alternatives.

Every node carries `Repeat` (how many times round the list it still runs, zero for
always) and `Random` (percentage chance it happens at all). The count comes down whether
or not the node did anything, so a node that fails its chance still uses up a turn.

A `SOUND` adds `Volume` (0–100, written `80.0` as often as `80`), `Loop`, `FadeInMs`,
`FadeOutMs`, `StopMethod` (0 play to end, 1 fade out, 2 cut), and for positioned sound
`3D`, `MinDist`, `MaxDist`, `X`/`Y`/`Z` and `Follow`, which names an object in the scene
to move with instead of standing still. **A looping sound stops the rest of the list
running**, which is how a soundtrack meant to be continuous is written: everything before
it is an introduction.

## What the corpus contains

Reading every soundtrack the scene files name: **97 distinct files, 5,755 steps, 125
distinct sounds**, with no section or key unaccounted for.

Four keys in the wider set are typos in the original data, reported (`GK3R1101`) and
ignored exactly as the original ignores them:

- `TITLETHEME.STK` writes `MisWaitMS` where it meant `MinWaitMS`;
- `VAMP1HISS`, `VAMP2HISS` and `VAMP3HISS` write `MinDistWaitMS` and `MaxDistWaitMS`.

Those waits are zero in the original too. Matching that matters more than fixing the
spelling.

## What plays

Every sound the program reaches, where the file places it. See "Running one" below for how
the list is walked; what follows here is what happens to the room's bed at a door.

**`FadeOutMS` is used, and it is the artists' own number.** Leaving a room does not stop
its bed; it starts it on its way out while the next room's comes up underneath, and how
long that takes is what the outgoing sound's own soundtrack asks for — three seconds for
R25's theme. A soundtrack that leaves the key out gets a second and a half, which is about
how long walking through a door takes. See `SceneAudio.Leave`.

## Running one

`SoundtrackProgram` walks the list: a `WAIT` holds it up for its own time or a random one
inside its range, a `SOUND` starts a sound and the list goes on when the sound is over, and
a run of `PRS` sections is **one** step with one of its sounds picked. Each node's `Repeat`
is spent whether or not the node did anything, so a node that fails its `Random` still uses
up a turn — which is what makes "play this twice and then never again" mean what it says.

**A looping sound is the end of the walk.** There is no length that would take the list past
it, so everything after it in the file is unreachable; that is the original's behaviour and
it is how a soundtrack meant to be continuous is written. 83 of the 269 files have one, and
that sound is the room's *bed* — the thing that crossfades into the next room. The other 186
are programs of occasional sounds with silence between them, which is what a hotel room
actually sounds like.

The draws come from the audio layer's own seeded generator rather than the game's, so how
often a room creaks does not depend on how much the player has clicked, and two runs of the
same scene make the same noises at the same moments (ADR 0004).

All of a scene's soundtracks run, not the first: RC1 at ten in the morning names a fountain,
a room tone and birdsong, and they are meant to be heard together. `PlaySoundTrack` starts
another beside them — its argument is a `.STK`, which is why looping it as though it were a
sound found nothing — and `StopSoundTrack` and `StopAllSoundTracks` take them away again.

## What is not here

`FadeOutMs` is applied through the room-to-room crossfade and to sounds the layer still
holds; a one-shot that has already ended has nothing to fade. Nothing reads `SoundType` for
anything but which bus to mix on.

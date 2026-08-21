# Vertex animation (`.ACT`)

5,796 clips, 399 MB, 280,617 keyframes across 709 models. All of the game's movement.

**GK3's characters have no skeleton.** A clip stores, per frame, where each of a model's
mesh groups sits and where every one of its vertices is. A walk cycle is a list of poses,
not a set of bone angles — which is why `Plan/05` concludes that no generative service can
be the character pipeline: `.ACT` is addressed by vertex index, so any topology change
invalidates that character's entire clip set.

The format is specified in full in **`Plan/06-c6-rig-solve.md` §3**, transcribed from
G-Engine and validated against the whole corpus. This is the reader's side of it.

## Layout

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `HTCA` — `ACTH` byte-reversed |
| 4 | 4 | Version. **Always 258** |
| 8 | 4 | Frame count |
| 12 | 4 | Mesh count — must equal the target model's |
| 16 | 4 | Payload size |
| 20 | 32 | Target model, NUL-padded |
| 52 | 4 × frames | Absolute byte offset of each frame |

Then per frame, per mesh: a `uint16` mesh index, a `uint32` byte count, and blocks until the
count runs out. A count of zero is legal and means the mesh has not moved.

| `dataId` | Size | Payload |
| --- | --- | --- |
| 0 | varies | Uncompressed vertex positions — in practice only frame 0 |
| 1 | varies | Compressed vertex positions |
| 2 | 48 | Mesh transform: three basis float3s, then a position |
| 3 | 24 | Mesh bounds: float3 min, float3 max |

## The traps

**The header names the model; the filename does not.** 12.9% of the corpus is filed under
something other than what it animates. Pair by `ActFile.ModelName` or one clip in eight
goes to the wrong place.

**The mesh basis is left-handed** — determinant −1. That is correct: GK3 authored a
left-handed world and the renderer draws it that way. Negating or permuting a basis to tidy
the handedness mirrors every character in the game.

**Deltas are against the previous recorded pose**, not the rest pose. So the shapes must be
decoded even when the caller does not want to keep them, or every later frame of that
submesh is wrong.

**The one-byte delta masks its whole part with `0x7F`, not `0x60`.** The sign bit survives
the mask and is discarded by the shift anyway. Tidying it gives the same answer, which is
exactly why it is easy to get *almost* right.

```
DecompressFloatFromByte(b):   sign(b & 0x80) * ((b & 0x7F) >> 5   + (b & 0x1F) / 32)
DecompressFloatFromUShort(v): sign(v & 0x8000) * ((v & 0x7FFF) >> 8 + (v & 0x00FF) / 256)
```

Compression codes are two bits a vertex, **low bits first** within each byte. Measured over
92.1M samples: code 0 (unchanged) 62.2%, code 1 (byte deltas) 31.1%, code 2 (short deltas)
6.7%, code 3 (raw floats) never. Implement 3 anyway.

**A fifth of the files have a trailer.** 1,201 of 5,796 end with `01 00 00 00 00`, which
G-Engine never reads and never noticed.

**Sampling holds the closest previous pose.** A mesh that does not move is not written
again, so every clip has holes in every mesh's track. Playback is **15 fps**.

## Invariants

`Plan/06` §3.6 names five and says to use them as reader tests. They are checked while
reading, because each one failing means the reader has lost its place and everything after
it is noise read confidently.

1. Magic is `HTCA`, version is 258.
2. The reader is at `offsets[i]` when frame `i` starts.
3. The mesh index read equals the loop index.
4. `dataId 2` is 48 bytes; `dataId 3` is 24.
5. The file ends exactly, or with the five-byte trailer.

## Corpus

```bash
GK3Reborn.Tools act-info --source <GK3>/Data
```

```
5798 vertex animations in 8 archives
5796 read, 2 refused, 280617 keyframes across 709 models
2188 are rigid (37.8%) - transforms only, no skinning needed to play them
1871820 mesh poses
748 clips are named for something other than the model they target (12.9%)
  gab: 943 clips, 41792 keyframes
```

The two refusals — `GAB_GABDOORLOCKED.ACT` and `GAB_GABKITCLSDUMB.ACT` — have no header at
all. They are damaged in the shipped game; the reference implementation refuses them too,
which is why the corpus is 5,796 and not 5,798.

Every figure here matches the Python prototype in `DonorWorkspace/rigsolve/` independently.

## What plays

**All of it.** Mesh transforms through `ISceneSink.PoseMesh` — distinct from `TurnMesh`,
which applies a rotation *on top of* a mesh's own transform where a clip stores the
transform outright — and vertex positions through `ShapeMesh`.

Positions only. Normals stay as the model authored them, which is what the original does:
it swaps the position stream and leaves the rest. Lighting on a deformed character is
therefore as right or wrong as it was in 1999.

**Vertex buffers that change need care.** Writing one from the CPU while the device is
still reading it for an earlier frame gives a character built from two different poses at
once. So an animated batch gets one vertex buffer per frame in flight, and the shapes are
written in `DrawFrame` after the fence says the device has finished with that frame's
buffer. A batch only gets those buffers the first time something reshapes it.

Verified against the game: `GAB`'s 13 meshes and 17 submeshes match
`GAB_GABBREATH2.ACT`'s vertex counts exactly — independent confirmation in C# of the
composition `Plan/06` §4.1 validated in Python.

## Where a clip plays

A clip's mesh transforms replace the model's own, and the model's placement is applied on
top. What that means depends entirely on what is being animated.

**A prop plays its clip exactly as authored.** A prop is placed by the identity: the room's
coordinates *are* the model's coordinates, so a clip written for that room is already in
the right place. That is also what the original does — it swaps the mesh transforms and
stops there.

It has to be measured to be believed, so it was: of the game's prop clips, 1,722 put the
thing somewhere other than where its model rests, because moving it is the point. A book
being picked up is 59 units above the shelf. Wilkes's moped rides seventeen hundred units
across RC1, from (2371, 22, −2489) to (4060, 61, −1294), while its model sits at the
origin. Correcting those back to the model is what left him riding past the world origin
while Gabriel watched an empty square and said "A bike! Man, I need one of those."

**An actor's clip is shifted to them.** An actor is placed where the scene stands them, and
their clip is wherever the animator authored it — for a walk, halfway across some other
room. Played as written the character walks out of frame. So it is shifted once at the
start by however far the clip's first frame sits from where the model rests; root motion
within the clip still happens, measured from where the actor was standing. Taken **once**
and held — recomputing per frame cancels exactly the movement it exists to preserve.

**Absolute**, 502 of 6,040 action lines: the line carries
`x1,y1,z1,angle1,x2,y2,z2,angle2`, and is put there whatever it belongs to. Both quirks
matter. The first offset goes *actor to model* and is wanted the other way round, so it is
**negated**; and **y and z are swapped** in both, because the assets came out of Maya.

```
position = worldToModel + rotateY(worldToModelHeading) · modelToActor
heading  = worldToModelHeading − modelToActorHeading
```

That heading is used **as it stands**. It is a transform, not a character's heading, so the
half turn that turns one into the other — GK3 measures a heading zero-along-+Z and models
its people facing −Z, see `Walker.Rotation` — must not be applied here. It was, and it left
RC1's fountain spraying its water two hundred and fifty units from the fountain.

## Between one recorded pose and the next

A clip records fifteen poses a second and a screen shows sixty frames a second, so playing
the poses as they stand shows each of them four times over. On anything slow that reads as
the original's stiffness; on anything fast it reads as strobing, and the lobby's ceiling
fans — six degrees a recorded pose, ninety a second — are the clearest case in the game.

So a moment between two recorded poses is the two of them mixed, which is what
`ActFile.PoseAt` and `ShapeAt` are for. Three things about that are worth writing down.

**The mix is of the recorded poses either side, not of consecutive frame numbers.** A mesh
that does not move is not written again; reading a held pose as a keyframe would make a
mesh that moves once every ten frames drift the whole way instead of waiting and then
moving.

**Every basis in the corpus is mirrored.** GK3's world is left-handed and its mesh
transforms carry a determinant of −1. `Matrix4x4.Decompose` deals with that by picking an
axis to call negative, and it need not pick the same one twice running — which turns a fan
blade inside out between one pose and the next, and reads as the fan flickering in and out
of existence rather than as a mistake about handedness. The mirror is taken out first, the
rotation mixed as a rotation, and the mirror put back. A basis that is not a rotation at
all — some of the fan housings are squashed flat — falls back to a straight component-wise
mix, which at these step sizes costs about a tenth of a percent of length and cannot go
wrong.

**A clip that loops runs its last pose into its first.** Held instead, it freezes for a
fifteenth of a second at the top of every turn. A scenery script that is one animation and
a jump back to it — `ANIM lbyfan_spin`, `loop`, and nearly every fan, fountain, fire and
flashing clock in the game — is therefore played as a looping *clip* rather than restarted
as a script each time round.

## When a clip ends

**The pose stays where the clip left it.** GK3 reverts an actor's *position and heading*
after a non-move animation, not its pose — `GKActor::OnVertexAnimationStop` calls
`Actor::SetPosition`, and the mesh poses are untouched. That is why an opened wardrobe
stays open. Reverting the poses as well, which reads as the more careful thing to do, shuts
the door again the moment it finishes opening.

A frame long enough to run past the end poses the end before stopping, so a slow frame
leaves a door open rather than half open.

## What does not

**Move animations do not commit their ground.** `StartMoveAnimation` says the actor keeps
the distance the clip covered. The flag is carried and not spent, because committing it
means writing the actor's position and `Walker` already owns that. The two have to be
reconciled before either may write it.

**The axis triads** of `Plan/06` §4.3 — three vertices at (60,0,0), (0,60,0), (0,0,60) in
every mesh group, orientation gizmos rather than geometry. They are in the vertex streams
now that vertices are read. They are not indexed by any triangle, so nothing draws them,
but anything that measures a character's extent from its vertices will be wrong by the 60
units they sit out at.

## Walking

`CHARACTERS.TXT` is an INI file, one section a character, keyed by the three-letter code
the models use. Forty-five characters have one. Gabriel's says:

```
WalkerHeight=76.0
StartAnim=gabstart
ContAnim=Gabwalk
StopAnim=Gabstop
```

Those name `.ANM` files, which name the `.ACT` that holds the geometry. Only `ContAnim` is
played: the reference notes that walk-*end* animations appear never to have been used in
the original at all, and playing the *start* one means the legs accelerate while the walker
moves at a constant pace, which slides worse than starting mid-stride does.

### The clip carries its own ground, and that is the problem

GK3 authors a walk as **root motion**. Gabriel's stride carries his hips 49.9 units along
the model's −Z over 1.40 seconds, and the original lets that motion move the actor —
`animParams.allowMove = true`.

Here `Walker` owns the position instead, because it is what knows the route, the boundary
and where the walk is supposed to end. The original agrees, in the end: it force-sets the
final position when the walk finishes, because root motion cannot be trusted to arrive
anywhere exact.

So the clip is played for its **pose** and the forward travel is taken back out, frame by
frame. **Only the forward travel.** The hips also sway sideways and rise and fall, and
removing those flattens the walk into a glide with moving legs. Measured over Gabriel's
stride:

| | before | after |
|---|---|---|
| Forward travel (Z) | 49.9 units | **0.000000** |
| Sway (X) | 1.93 units | 1.93 units |
| Bob (Y) | 1.29 units | 1.29 units |

What accumulates is Z, and Z is what comes out. The last frame is the first again — they
agree to 0.002 units in sway and exactly in bob — so the loop runs over one frame fewer
than the clip holds, or the stride hitches once a cycle.

### The pace has to come from the same place

The walker's own guess was 65 units a second. The stride is **35.6**. Walking half again as
fast as the legs is precisely what a sliding character looks like, so a walk with a stride
goes at the stride's pace and `Walker.Speed` is only what somebody with no entry in the
file gets.

### Standing off

A named approach spot is where the artists put it and is walked to exactly. A *thing* has
no spot, so the aim is the middle of the object — and the middle of a picture is inside the
wall it hangs on. The boundary stops the actor before they get into it, so nothing looks
broken; they end up with their nose against it. In R25:

| | walking to the middle | standing off |
|---|---|---|
| `r25pic01` | 13 units | 77 |
| `r25pic02` | 9 units | 78 |
| `r25pic03` | 9 units | 78 |
| `r25pic04` | 35 units | 75 |

The distance is the character's own `WalkerHeight`, which agrees with what the artists did
where they placed a spot by hand: the few approaches in the corpus that name both a thing
and a position stand 68 to 184 units off it. Never further than the thing already is, so
somebody standing close does not back away from what they were sent to look at.

### Still open

- **The start and turn animations are read and not played.** `StartTurnLeftAnim` and
  `StartTurnRightAnim` are how the original turns a standing character into a walking one
  without pivoting on the spot.
- **Nothing waits for a walk.** A script's `wait` returns before the actor arrives, so a
  line of dialogue about a painting starts while its speaker is still crossing the room.

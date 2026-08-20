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

**The rigid 37.8%, exactly.** A clip's mesh transforms are applied through
`ISceneSink.PoseMesh` — distinct from `TurnMesh`, which applies a rotation *on top of* a
mesh's own transform where a clip stores the transform outright.

A character plays as mesh groups that move about without deforming. That is where the
geometry goes and it looks wrong. Deformation needs either the rig solve of `Plan/06` or a
vertex buffer that can change between frames; neither exists.

The axis triads of `Plan/06` §4.3 — three vertices at (60,0,0), (0,60,0), (0,0,60) in every
mesh group, which are orientation gizmos and not geometry — do not matter yet, because
nothing reads vertices at runtime. They will matter the moment anything does.

# Barn archive format (`.brn`)

GK3's asset archives. Named for the animals — some asset types are called Sheep
and Yak. All integers are little-endian.

Documented from G-Engine's `Engine/Assets/BarnFile.cpp` and verified against the
eight retail archives (822 MB, 57,948 directory entries). Fields marked *unused*
are read past by the reference implementation without being interpreted; they are
not known to be meaningless, only unneeded.

## Header

| Offset | Size | Field |
|---|---|---|
| 0x00 | 4 | `GK3!` |
| 0x04 | 4 | `Barn` |
| 0x08 | 4 | *unused* — 65536 in every retail archive |
| 0x0C | 4 | *unused* — 65536 in every retail archive |
| 0x10 | 4 | *unused* — appears to be a size |
| 0x14 | 4 | offset of the table of contents |

More header data follows (build numbers, two timestamps a few minutes apart, and
a 64-byte copyright notice) which the reader skips.

## Table of contents

At the offset from 0x14:

| Size | Field |
|---|---|
| 4 | entry count |

Then that many 28-byte entries:

| Size | Field |
|---|---|
| 4 | type: `DDir` or `Data` |
| 16 | *unused* |
| 4 | header offset |
| 4 | data offset |

A `DDir` entry describes a directory of assets: its header offset and data offset
are both used. A `Data` entry marks where asset bytes begin — its *header* offset
is the base that every entry offset is relative to, and its data offset is unused.

## Directory header

At a `DDir` entry's header offset:

| Size | Field |
|---|---|
| 32 | name of the archive that really holds these assets, NUL-padded |
| 4 | *unused* |
| 40 | human-readable description |
| 4 | *unused* |
| 4 | asset count |

When the name is empty the assets live in this archive. When it is not, every
entry in this directory is a **pointer**: the directory names the asset and the
archive holding it, and carries no data. `core.brn` is mostly this — of its 36,957
entries, 20,991 point into the other seven archives, which is how the game
resolves an asset name without knowing which archive it is in.

## Directory entries

At a `DDir` entry's data offset, packed back to back:

| Size | Field |
|---|---|
| 4 | stored size in bytes |
| 4 | offset, relative to the data section |
| 5 | *unused* |
| 1 | compression type |
| 1 | name length |
| *n* | name |
| 1 | NUL terminator |

Compression types:

| Value | Meaning |
|---|---|
| 0 | stored |
| 1 | zlib, header included |
| 2 | LZO1X |
| 3 | stored — treated identically to 0; the difference, if any, is unknown |

## Asset data

Seek to *data section offset* + *entry offset*.

A stored entry is simply the next `size` bytes.

A compressed entry begins with an 8-byte prefix:

| Size | Field |
|---|---|
| 4 | decompressed size |
| 4 | *unused* |

followed by `size` bytes of compressed data.

## Quirks worth knowing

**The last entry can overrun the file.** Some archives record a stored size for
their final entry that exceeds the bytes remaining. The data still decompresses
correctly, so the reader clamps to what is actually there rather than failing.

**LZO streams end at a marker, not at the end of input.** The three-byte sequence
`0x11 0x00 0x00` terminates a stream, and the recorded compressed size is often
larger than the real stream — G-Engine's use of native LZO reports
`LZO_E_INPUT_NOT_CONSUMED` for most GK3 data for exactly this reason. All 36,957
retail entries terminate at the marker, so GK3Reborn treats running out of input
as truncation and says so with an offset.

**Names are case-inconsistent.** The same asset is referenced as `DAY3-3.BIK`,
`day3-3` and `Day3-3` in different places, so lookup is case-insensitive and
identity goes through `AssetId`.

**Later directories win.** When two directories declare the same name, the last
one parsed is the one that resolves, matching the reference implementation.

## Verification

`GK3Reborn.Tools extract-barn --verify` decompresses every entry in every archive
without writing anything, and records each entry's disposition in
`manifests/barn.json`. Against the reference installation: 36,957 entries
extracted, 20,991 pointers, 0 failures, 1.4 GB of output.

Correctness beyond "it did not throw" is established by internal consistency of
the decompressed bytes: all 2,340 extracted WAV files carry RIFF sizes that agree
exactly with their decompressed lengths, and GK3's bitmaps carry dimensions that
match their filenames (`128MUD.BMP` → 128×128). A single wrong byte anywhere in
the decoder would break those relationships.

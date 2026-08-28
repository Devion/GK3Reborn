# ReBarn (`.rebarn`)

The remake's own content, in one or two files that sit beside the executable. Enhanced
colour textures and their material channels, modernised models, imported video — everything
`ContentWorkspace/enhanced` holds — compressed to the form the GPU wants and packed so that
a shipped game is an executable and two files rather than forty thousand.

It is deliberately **not** GK3's Barn format. Barn is a 1999 archive with 32-bit offsets and
per-entry LZO, built for 822 MB of assets that were all decoded on the way to the card. This
holds fifteen gigabytes of block-compressed data that must reach the device *without* being
decoded, which is a different problem and wants a different container.

The original installation is untouched and still read from its own `.brn` archives through
[`GameArchives`](../../src/GK3Reborn.Engine/Content/GameArchives.cs). ReBarn is a layer in
front, exactly as the loose `enhanced/` directory is: a name with no entry falls through to
the original, so a partial pack is a perfectly good pack.

## Building one

```
rebuild-content.cmd
```

In the directory above the repository, beside `ContentWorkspace` and `PbrLab`, and it is
the whole chain: it derives the normal, emissive, material and ORM/height sets for whatever
base colour changed, packs both volumes, verifies them, and copies them to the game's
publish directory. Every pass skips work whose inputs are unchanged, so a handful of
redrawn textures is a couple of minutes. `--packs-only` when the derived channels are
already current, `--dry-run` to see what it would do.

**Packing alone is not the whole job.** A texture in `enhanced/textures` is the source of
its normal map, which is the source of its ORM and its height map. Repacking without
re-deriving ships a set where the material channels belong to a picture that no longer
exists — which verifies perfectly, because a pack cannot see an absence, and shows up as a
surface lighting wrongly.

The packing half by itself:

```
dotnet run --project tools/GK3Reborn.Tools -- pack-content --workspace <ContentWorkspace>
```

That encodes every PNG under `enhanced/` to DDS and writes two volumes into
`<workspace>/build/pack`. Copy them next to `GK3Reborn.exe` and the game finds them.

| Flag | What it does |
| --- | --- |
| `--output <dir>` | Where the volumes go. `<workspace>/build/pack` by default. |
| `--kinds a,b` | Only these kinds. `textures normals orm height emissive models video manifests` |
| `--only <dir>` | Only the kinds packed from a source directory, such as `enhanced/trees`. |
| `--cap normals=512` | Longest edge a kind is encoded at, overriding the default. |
| `--single-volume` | One file rather than two. |
| `--force` | Re-encode even when a cached DDS is still valid. |
| `--dry-run` | Say what would happen and write nothing. |
| `--encode-only` | Encode into `build/` and stop before packing. |
| `--no-gpu` | Do not let texconv use the GPU for BC7. Slower; use if it misbehaves. |
| `--texconv <path>` | Where `texconv.exe` is. Found beside `PbrLab` by default. |

Three more commands read one back:

```
pack-list    --input <file|dir> [--kinds <kind>] [--names]
pack-extract --input <file|dir> --output <dir> [--kinds <kind>] [--name NAME]
pack-verify  --input <file|dir>
```

A volume is written beside its target and moved into place, and every target is checked for
writability *before* the first one is written. The engine memory-maps its packs and holds
them for the whole session, so a running game keeps them open — discovering that after the
first volume has been replaced leaves a mismatched set on disk, which is worse than having
written nothing.

`pack-verify` checks every entry against its CRC **and decodes each DDS with the engine's own
reader**. A checksum only says the bytes are the bytes that were written; a format the loader
refuses is written perfectly and then falls back silently at runtime, which is worse.

### The source tree does not move

`ContentWorkspace/enhanced/textures` stays where it is and keeps its name. It is the source,
the pack is the target, and the two are already distinguished by being a directory outside
the repository and a file beside the executable. Renaming it to `srctextures` would break
`PbrLab`'s five passes, which all read `enhanced/textures` by name, and `--enhanced` in the
engine, and buy nothing that the directory/file distinction does not already buy.

Designers edit `enhanced/`, run `pack-content`, and the pack catches up. Nothing else changes.

### What is re-encoded, and what is not

Encoded DDS are kept in `build/rebarn/<kind>/` and reused, and an existing `build/<kind>/`
DDS from an earlier `PbrLab/compress.py` run is adopted rather than redone. Either is only
used when three things hold: it exists, its extent is what the plan asks for, and **it is no
older than the PNG it was made from**.

That third condition is the whole point. Matching dimensions is not freshness — a
regenerated texture keeps its size — and a rule that checked only the extent packed the
lobby register's picture from the previous night while the new one sat beside it. It was
found by somebody noticing the register looked wrong, which is the only way it could have
been found: the pack was valid, verified, and full of the wrong pictures. 2 colour textures,
9 normals and *all 72* emissive maps were affected.

### Sizing each texture for itself

Nearly every enhanced texture is 2048 on its longest edge, whatever it depicts. `pack-plan`
works out what each one is worth:

```
dotnet run --project tools/GK3Reborn.Tools -- pack-plan   --workspace <ContentWorkspace> --source <GK3>/Data [--density 4] [--floor 512]
```

It writes `manifests/pack-sizes.json` — every texture with its proposed size, the world area
it covers and the rule that decided — and `pack-content` applies it automatically to every
channel. `--no-size-plan` ignores it.

The signal is `worldArea` and `densityTarget` from `surface-analysis.json`: how many texels
of a texture fall across one world unit. `densityTarget` is the size at which a texture
reaches the corpus *median* density, which is a 1999 yardstick, so `--density` multiplies it
to say how much better than the original the remake wants to be. Reference counts are
deliberately not used — they favour door latches over the wallpaper that fills a frame.

At `--density 4` the colour set goes from 10.15 GB to 5.63 GB: 934 textures to 512, 712 to
1024, 875 left at 2048.

**Nothing is demoted without positive evidence.** A texture the surface analysis never saw
keeps its size, because "not measured" is not "not important" — that is 721 of them. Three
classes are protected outright on top of that, each drawn far larger than its world area
suggests:

- **226 face patches.** Eyelids, blinks, winks and mouths are blitted *into* a character's
  face bitmap at offsets in that bitmap's own coordinates ([faces.md](faces.md)), so they
  have to stay in scale with the face.
- **131 inventory sprites**, named by the game's own `INVENTORYSPRITES.TXT`. These are drawn
  as 2D art filling much of the screen in a close-up. Note the *3D model* textures for the
  same objects — `LIPSTKCAP`, `RAZORFRNT` — are a different set and are sized normally, which
  is right: those only ever appear at room scale.
- **157 textures worn by a character**, looked at in conversation close-ups.

### Saying it by hand: `pack-rules.json`

No measurement sees everything, so `manifests/pack-rules.json` is hand-written, applied last
and never regenerated — the same convention as `material-library.materials.edits.json`. A
value is either a size, or an object:

```json
{
  "_why": "a leading underscore is a comment",
  "LBYREGBOOK": 2048,
  "TITLE": {
    "form": "png",
    "materials": false,
    "note": "Title background, drawn full-screen rather than mapped onto geometry."
  }
}
```

| Key | Meaning |
| --- | --- |
| `size` | Longest edge to pack at, overriding the density rule. |
| `form` | `dds` to block-compress, or `png` to store the source file verbatim. |
| `materials` | `false` takes it out of the normal, ORM and height sets **and** out of every PbrLab pass. |
| `note` | Why the rule exists. It is reported when the plan is built. |

Two things this is for. **An in-world close-up camera**, which nothing in the corpus records:
something the player walks up to and reads has a small world area and needs its pixels
anyway — `{"LBYREGBOOK": 2048}`. And **an image that is not a surface at all**: the title
background is drawn full screen at one texel to one pixel, so a normal map and an ORM for it
are meaningless, and BC7 would spend block artefacts on the one thing a title screen is —
smooth gradient. `form: png` stores it exactly as authored.

`materials: false` is read on both sides. The packer skips the name in the material kinds,
and `gk3pbr/workspace.select()` — the single door every PbrLab pass comes through — drops it,
so a later full run does not generate maps for it either.

> **A struct default is not an answer.** The lookup has to distinguish "the plan says this is
> not a surface" from "the plan says nothing about it". `PackedTexture` is a struct, so an
> absent name reads back as `default`, whose `Materials` is `false` — which quietly dropped
> three emissive maps whose colour texture is not in the enhanced set. Check that the lookup
> succeeded before believing what it returned.

## What each channel is encoded as, and why

| Kind | Format | Cap | Why |
| --- | --- | --- | --- |
| textures | `BC7_UNORM_SRGB` | source | The channel a player looks at. Never capped by default. |
| emissive | `BC7_UNORM_SRGB` | source | Same; and there are only 72 of them. |
| normals | `BC5_UNORM` | 1024 | Two channels; the shader reconstructs the third. |
| orm | `BC7_UNORM` | 1024 | Three genuinely different channels, and linear. |
| height | `BC4_UNORM` | 512 | One channel. Half the size of every other block format. |
| models | `.glb`, stored | — | Already compact. |
| video | `.mp4`, stored | — | Already a compressed video stream. |
| manifests | `.json`, deflated | — | Text, and text deflates. |
| raw: `*.splat.png` | `BC7_UNORM` | source | Four blend weights. Data, so **not** colour. |
| raw: `*.tint.png` | `BC7_UNORM_SRGB` | source | The vista's colour. |
| raw: everything else | stored or deflated by payload | — | Heightfields and forests. |

### One directory, three kinds

Every source directory feeds one kind except `enhanced/trees`, which feeds three: a grown
tree is geometry (`*.glb` → models), the foliage it is painted with (`*.PNG` → textures,
encoded like any other colour texture) and a manifest saying which is which (`*.json` →
manifests). They are produced, reviewed and shipped together, and splitting them into three
directories to suit the packer would put a tree's parts three places apart for no reason a
person would recognise. `PackKind.Files` is the search pattern that divides them.

`enhanced/terrain` divides the same way, and it is the reason `PackKind.Files` reaches the
*encoder* and not only the verbatim path: one directory holds two maps that must go through
texconv with different formats and, crucially, opposite answers to `Colour`. A splat map
gamma-converted on the way in is a whole step of brightness in a file that is valid and
loads; the tint *not* converted is the same error the other way. Measured against the
sources, the tint round-trips at 0.58/255 RMSE and the splat at 1.55; with the flag wrong,
56.

**Nothing in a terrain set is parsed at load any more** — see
[rendering.md](../rendering.md#what-ships-and-why-none-of-it-is-parsed-at-load). It used to
cost 190–293 ms of every outdoor scene, spent inside the screen fade with no frame offered
for the length of it, and the raw section fell from 657 MB to 399 in the same change.

That is also why `--only` exists. Filtering by *kind* cannot reach the trees on their own —
any filter that catches them drags in every enhanced texture in the game — and re-encoding
six thousand of those to check that a tree packed is an hour. See `docs/trees.md`.

Colour is the only channel kept at full resolution. The other three modulate a surface the
colour texture has already described, and detail in them below the colour's own resolution is
not resolvable on screen. Capping them takes the pack from about 32 GB to about 15.5 GB.

Two of those choices were settled by looking at the pixels rather than by reasoning about
what the channels are for:

- **Every height map in the set is grey stored as RGB.** Sampled across the corpus, `R == G
  == B` everywhere — one channel of real information in three channels of file. `BC4_UNORM`
  spends its whole eight-byte block on that one channel, where BC7 would spend a seventh of a
  sixteen-byte block on it. Better quality at a quarter of the size.
- **ORM's metalness channel is almost unused.** Eleven of 2,195 maps have any blue at all,
  and all eleven are *fully* metal — one bit, for half a per cent of the set. BC5 over
  occlusion and roughness alone would halve the ORM set and raise its quality, with the
  eleven taking their metalness from the material library instead. It is **not** done that
  way, because the shader reads the blue channel today and changing that changes pixels; it
  is written down here as the obvious next saving, worth about a gigabyte.

`-srgbi` is passed on every colour run. Without it texconv treats an 8-bit PNG as linear and
applies a linear-to-sRGB conversion nothing asked for, which the GPU then decodes as sRGB
again: a full gamma step too bright, in a file that is valid and loads. Measured on
`GAB_FACE`, that is 12.7 dB against the source without the flag and 53.3 dB with it.

## The container

```
  0   header       64 bytes
  64  data         entries, each starting on a 256-byte boundary
  ..  name table   UTF-8, no separators; entries carry an offset and a length
  ..  index        entryCount records of 48 bytes, sorted by key hash
```

The index is last so a volume can be written in one streaming pass without knowing in advance
how large it will be; the header is rewritten at the end with the three offsets that pass
discovered. Nothing but the index is ever held in memory, so fifteen gigabytes go out through
a one-megabyte buffer.

### Header, 64 bytes, little-endian throughout

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `u32` | magic, `RBRN` |
| 4 | `u16` | version, currently 1 |
| 6 | `u16` | volume number |
| 8 | `u32` | flags, reserved, zero |
| 12 | `u32` | entry count |
| 16 | `i64` | index offset |
| 24 | `i64` | name table offset |
| 32 | `i64` | name table length |
| 40 | `i64` | data offset |
| 48 | `u64` | FNV-1a over the name table and index |
| 56 | `i64` | built, as UTC ticks |

### Index record, 48 bytes

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `u64` | key hash, FNV-1a |
| 8 | `i64` | offset of the stored bytes |
| 16 | `i64` | stored length |
| 24 | `i64` | length once decompressed |
| 32 | `u32` | name offset, into the name table |
| 36 | `u16` | name length |
| 38 | `u8` | kind |
| 39 | `u8` | payload |
| 40 | `u8` | compression |
| 44 | `u32` | CRC-32 of the stored bytes |

### Keys

An entry answers to its **kind and its name without an extension or a directory**, uppercased:
`R25WALLS`, not `textures/R25WALLS.dds`. Which is how every other layer in the engine
addresses content — a surface refers to `R25WALLS`, the archive holds `R25WALLS.BMP`, the
pack holds `R25WALLS.DDS`, and all three are the same thing.

The kind is part of the key rather than a property of the entry, because every material
channel is named for the *colour* texture it belongs to. `R25WALLS` is a colour texture, a
normal map, an ORM and a height map. Without the kind they would collide.

### Three properties worth relying on

**Entries are stored, not compressed.** A DDS is already compressed; running DEFLATE over one
buys a few per cent of disk for a decompression pass on the critical path of a room load.
Manifests and anything else that is not already compressed take `Deflate`. Time to display is
what matters, and the memory notes on load performance say so with numbers.

**Every entry starts on a 256-byte boundary**, so a mapped entry can be handed straight to a
staging buffer, and a DDS's block data — 128 bytes into the file — lands on a 128-byte
boundary too. Ten thousand entries waste about a megabyte between them.

**A pack built twice from the same inputs is byte for byte the same file**, apart from the
timestamp at offset 56. Entries are written in the order they were added and the index is
sorted by key hash, so a rebuild that changed nothing looks like a rebuild that changed
nothing.

## Reading one

`RebarnArchive` memory-maps the whole volume once and never reads it into the heap.
`ReadMapped` hands back a `ReadOnlyMemory<byte>` that is a *window onto the mapping*, so a
2048-pixel BC7 texture reaches the device without being copied at all: no decode, no
allocation, a mip chain already built. A room wants dozens of those and they are read on every
core at once, so copying each one out first would double the high-water mark of a scene load
to achieve nothing.

> **The one sharp edge.** A mapped window is only valid while the archive is open. The game
> keeps its packs open for the life of the process, so the mapped path is safe there. A tool
> that opens a pack in a `using` and holds the bytes afterwards reads freed address space —
> use `Read`, which copies, for anything that outlives the archive.

Mapping costs address space rather than memory. An eleven-gigabyte volume reserves eleven
gigabytes of a 64-bit address space and pages in only what is touched, so a session that
visits four rooms pays for four rooms.

`RebarnContent` opens every `*.rebarn` in a directory in file-name order and **the last one
wins**, so a pack dropped in later overrides one shipped earlier: `Reborn.rebarn`, then
`RebornMaterials.rebarn`, then a `RebornPatch.rebarn` somebody adds. That is the whole mod
story, and it needs no support beyond a name that sorts last.

A pack that will not open costs that pack and nothing else (`GK3R1176`). One damaged volume
out of two leaves the game running on what the other holds, the same way one unreadable
texture leaves the rest of a scene alone.

### Where the engine looks

Beside the executable, or wherever `--packs <dir>` says. Packs are opened once for the session
rather than once a room, and `CompressedTextures.Open(directory, packs)` indexes them *first*
so that a loose `build/` DDS overwrites the pack's entry for the same name. That is the same
way round as PNG beating DDS, and for the same reason: while a set is still moving, the looser
and more recent thing should win without a fifteen-gigabyte rebuild.

`--uncompressed` still turns the whole compressed layer off, packs included, which is how the
two are put side by side to see what compression cost.

### `--rebarn`: the packs and nothing else

```
GK3Reborn --scene R25 --timeblock 102P --rt high --rebarn
```

Takes every loose source of enhanced content out of the way — the `enhanced/*.png` sets and
the loose `build/*.dds` — and runs on the packs alone. That is the only honest way to measure
what the shipped form costs: with the loose sets in front of it, a run measures those instead
and reports perfectly good numbers for something nobody asked to measure.

It **refuses to start** if no pack is found, rather than falling back to the original
textures. A silent fallback here produces a run that looks like a successful measurement and
is not one.

Where it looks, in order: `--packs <dir>` if given, then beside the executable, then
`--workspace <dir>`, then the default content workspace. So a development build with the
volumes sitting in `ContentWorkspace` needs no extra flag.

## Why there are no texture atlases

The question is a reasonable one — GK3 ships thousands of loose textures, many of them tiny,
and a file per painting and per mouth shape looks like something to consolidate. Measured
against this corpus, an atlas is the wrong tool, and the reason is worth writing down so it is
not re-proposed.

**The file-count problem is what the pack solves.** After `pack-content` there are two files.
Nothing an atlas does to file count is still available to be done.

**There are no small textures left to atlas.** 553 of the 1,786 textures that appear on
geometry are 64 pixels or smaller in the original — exactly the door latches and hall numbers
that would be natural candidates. 488 of them have enhanced versions, and **298 of those are
now 2048², with only 14 still at 512 or below.** Sixteen 2048² BC7 textures fill an 8192²
atlas page, so the enhanced set would need 183 pages; a room binds around fifty textures
scattered across most of them, so loading a room would mean loading nearly the whole set.
That is worse than what happens now by a wide margin.

**Two hard constraints rule out the largest classes anyway.** 209 textures tile, and an atlas
cannot use wrap addressing without per-sample arithmetic in the shader. 719 carry alpha, whose
mip coverage is preserved per texture with `-sepalpha --keep-coverage` and would bleed across
neighbours on a shared page. Mip chains bleed across atlas neighbours generally, which is why
padding gutters exist and why they get expensive at these sizes.

**The mouth textures are not draw calls.** GK3's people have no facial geometry — a head is
one mesh wearing one bitmap, and talking is done by *patching a region of that bitmap* at the
`Mouth Offset` and `Mouth Size` in `FACES.TXT` (see [faces.md](faces.md)). The ~280 mouth
textures are CPU-side blits into the face texture, never separate bindings. The original game
already atlased them, in the only way that helps.

**And the cost they would address is not where the time goes.** The load-performance work
found PNG decode and redundant uploads, not draw-call submission; a room's few hundred
descriptor sets are not a bottleneck on any hardware this targets. If per-material binding
ever does become one, the answer is descriptor indexing or array textures — both of which keep
wrap addressing, mip chains and per-texture residency, all of which an atlas gives up.

One thing an atlas would have been good for is not there either: the 280 flat-colour textures
were correctly excluded from enhancement, so none of them is sitting in the pack as a
five-megabyte square of one colour.

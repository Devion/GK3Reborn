# Overrides

A directory called `overrides/` beside the executable. Anything in it replaces the file of
the same name that the game would otherwise have read — out of a ReBarn pack, or out of the
1999 archives — with no repack, no reinstall and no patch file.

```
GK3Reborn/
  GK3Reborn.exe
  Reborn.rebarn
  RebornMaterials.rebarn
  overrides/
    textures/NUWALL2B.png        the wallpaper in Gabriel's room
    normals/NUWALL2B.png         its relief
    R25.NVC                      the room's script
```

Nothing has to be enabled. The directory is indexed once at startup, and a run says what it
found:

```
Overrides: 3 file(s) in ...\overrides: 2 textures, 1 normals, 1 game asset(s)
```

That line is worth reading. An override is invisible once it is on screen — that is the
whole point of it — so a run in which a forgotten file is standing in for the shipped one
looks exactly like a run without it, and this is the only thing that ever says otherwise.
`--no-overrides` turns the directory off for one run without moving it.

## What wins

**An override is the top of every stack, not another layer in the middle.** A colour texture
has four possible sources — the archive's bitmap, an enhanced PNG in a content workspace, a
loose `build/` DDS, and a pack — and one that beat only some of them would appear to do
nothing on the machines where a different one happened to win. So it is registered into all
of them:

| Layer | Beaten by |
| --- | --- |
| `Reborn.rebarn`, `RebornMaterials.rebarn`, a patch pack | any override |
| a loose `build/*.dds` | a `.dds` override |
| `ContentWorkspace/enhanced/**.png` | a `.png` or `.bmp` override |
| `*.brn` | an override with the asset's own file name |

It is also not gated on anything else. `--rebarn` takes the loose enhanced sets out of the
way so a measurement measures the shipped form, and turning higher-resolution textures off
in the menu asks for the 1999 picture; neither is a statement about a file the player put
there themselves. Only `--no-overrides` is.

`--overrides <dir>` names a different directory. Without it, `overrides/` beside the
executable — or, on an install that cannot be written to such as a signed `.app` bundle,
`overrides/` under the per-user directory that already holds the settings.

## Where a file goes

Two independent questions, and neither is guessed at.

**The extension says which layer.** The forms the remake's own content takes — `.png`,
`.dds`, `.bmp`, `.glb`, `.mp4`, `.json` — go in front of the packs. Everything else is an
asset of the 1999 game and goes in front of the archives under its own file name:
`R25.SIF`, `R25.NVC`, `R25THEME1.WAV`, `GABRIEL.MOD`.

**A directory says which kind.** The last path segment naming one decides:

```
textures  normals  orm  height  emissive  models  scene-geometry  video  manifests  raw
```

which is exactly how `ContentWorkspace/enhanced` is laid out and exactly what `--extract`
writes, so an extracted tree reads back with nothing moved. Any other directory is your own
filing and is ignored — `overrides/my mod/textures/NUWALL2B.png` is a colour texture, and so
is `overrides/textures/drafts/NUWALL2B.png`. With no kind directory at all, an image is a
colour texture and everything else goes by its extension.

Every file is registered in front of the archives **as well**, under its full name. That is
what makes a dropped `GAB_FACE.BMP` reach the places that ask an archive for a bitmap by
name, rather than only the one that asks the texture stack for `GAB_FACE`.

There is no JPEG. Nothing in the engine decodes one, and a form that was accepted and then
quietly fell back to what it was meant to replace would be worse than one never offered.

## Getting the content out: `--extract`

Replacing a texture means first knowing what is there and what it looks like, and neither a
ReBarn volume nor a 1999 barn is something a paint program can open.

```
GK3Reborn --extract --name NUWALL2B --as png
GK3Reborn --extract --kinds textures,normals --extract-to unpacked
GK3Reborn --extract --from game --kinds SIF,NVC --name R25
```

| Flag | What it does |
| --- | --- |
| `--extract` | Unpack, into `overrides/` unless told otherwise. |
| `--extract-to <dir>` | Somewhere else. Then it is a copy, not an override. |
| `--from packs\|game\|all` | The ReBarn volumes, the game's own archives, or both. `packs` by default. |
| `--kinds a,b` | Only these kinds. For `--from game`, extensions: `SIF,NVC,BMP`. |
| `--name NAME` | Only entries with this name, across every kind. |
| `--as png\|dds` | Textures decoded to PNG, or the block-compressed file verbatim. |
| `--packs <dir>` | Where the volumes are, if not beside the executable. |
| `--data <dir>` | The game's `Data` directory, for `--from game`. |

It needs no window and no graphics device, so it starts and finishes without one.

**`--extract` with no `--kinds` and no `--name` is refused** when it would write into
`overrides/`. Everything extracted there is now an override of itself, so a whole-pack dump
would leave a game reading its own content back through a slower door, and rebuilding the
packs would stop changing anything on screen. Say which content you want, or `--extract-to`
somewhere it is only a copy.

### `--as png` is a conversion, not a rename

A block format keeps only the channels it needs, so what the decoder produces is not the
picture that was compressed:

- A **BC5 normal map** has two channels; the third is reconstructed as
  `z = sqrt(1 - x² - y²)`, which is what a unit normal's blue always was.
- A **BC4 height map** has one; it is written back as grey across three, which is how the
  source PNGs store it — measured across the whole corpus, which is why they are BC4.

Dumped straight, those would be a normal map that is black in blue and a height map that is
red. Both load. Neither is right.

Block compression is lossy, so a PNG extracted from a pack is what the pack holds rather
than what was originally authored. It is the right starting point for editing; it is not a
way to recover `enhanced/`.

## The layout `--extract` writes

```
overrides/
  textures/        colour, sRGB
  normals/         tangent space, BC5 in the pack
  orm/             occlusion, roughness, metalness; linear
  height/          one channel
  emissive/
  models/          .glb
  scene-geometry/  .glb, improved room objects
  video/           .mp4
  manifests/       .json, the material library among them
  raw/             terrain and forests
  game/            everything --from game wrote, flat
```

`game/` is flat and separate on purpose: those assets are matched by their whole file name
rather than by a kind, so no kind directory would tell the override layer anything the
extension does not, and forty thousand files beside a dozen texture directories would bury
the ones you came for.

## Practical notes

- **Delete what you are not changing.** Every file in `overrides/` is read from disk rather
  than from the pack's memory mapping, which is slower and is the whole cost of this
  feature. A directory with four files in it costs four files.
- **Overriding colour leaves the other channels alone.** A repainted wall keeps the packed
  normal, ORM and height maps that were derived from the picture it replaced, which is
  usually what you want and is occasionally exactly wrong — a surface whose relief has moved
  needs its `normals/` and `height/` replaced too.
- **A file that will not read falls through** to what it stands in front of, with a warning,
  rather than failing the load. One bad file costs that asset and nothing else.
- **`.DS_Store` and `Thumbs.db` are ignored**, so the count means what it says.
- **A patch pack still works.** `RebarnContent` opens every `*.rebarn` in file-name order
  and the last one wins, so a `RebornPatch.rebarn` overrides what shipped. That is the right
  tool for a large set; `overrides/` is the right tool for a handful of files and for
  anything in the original archives, which no pack can reach.

## See also

- [formats/rebarn.md](formats/rebarn.md) — the pack format, and `pack-content`/`pack-extract`
  for building and unpacking one from the toolchain rather than the game.
- [texture-enhancement.md](texture-enhancement.md) — how the shipped enhanced set was made.

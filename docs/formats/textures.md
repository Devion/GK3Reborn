# Texture formats

GK3 stores textures three ways. The classifier and `BitmapDecoder` handle all three;
`organize` converts the first two to PNG.

## GK3's own container — 6,330 assets

Despite G-Engine calling it "compressed", it is a raw 16-bit bitmap.

| Offset | Size | Field |
|---|---|---|
| 0x00 | 2 | `0x3136` — reads as `61` on disk |
| 0x02 | 2 | `0x4D6E` — reads as `nM` on disk |
| 0x04 | 2 | **height** |
| 0x06 | 2 | **width** |
| 0x08 | … | width × height pixels, RGB565, top-left first |

Two things to get right:

**Height precedes width.** Transposing them still produces a correct image for every
square texture, which is how the mistake survives to ship. `200AM.BMP` is 128 wide and
64 high and catches it.

**Rows of odd width carry two bytes of padding.** Skipping them shears the image
progressively, one pixel per row.

Channel expansion must scale rather than shift: `r * 255 / 31` and `g * 255 / 63`.
Shifting left leaves full-intensity channels at 248, so every white pixel comes out
slightly grey.

## Windows bitmaps — 328 assets

322 are 8-bit palettised, 6 are 24-bit. Some of the palettised ones are data rather than
pictures — the walk boundaries, where the palette index is the region and the colour is
incidental — and `BitmapDecoder.DecodeIndexed` reads those as indices instead. Standard layout: 54-byte header, then a palette
for the 8-bit ones, then bottom-up rows padded to a four-byte stride, with channels in
blue-green-red order.

## PNG

A handful of assets are already PNG. They pass through untouched.

## Transparency

Magenta is the colour key. G-Engine treats a texture as alpha-tested when its
**top-left pixel** is magenta, and that convention is preserved: those images decode
with every magenta pixel made transparent, so a PNG viewer shows what the game shows.

Images without the marker keep magenta opaque. Applying the key unconditionally would
punch holes in artwork that merely happens to contain the colour.

Of 6,658 converted textures, 719 carry alpha and 5,939 are opaque RGB.

## Conversion

`organize` writes PNG through a small encoder in `Formats/Bitmaps/PngWriter.cs` rather
than an imaging library: the pipeline needs one thing, lossless RGB or RGBA out, and
writing PNG directly avoids taking a dependency with its own licence terms onto a GPL
project and avoids shipping an image library the runtime never uses. Deflate comes from
the BCL.

Result on the reference installation: 6,658 textures converted, 0 failures, every output
structurally valid — chunk CRCs, zlib streams and scanline counts all verified.

## DDS, and what a room load actually costs

The enhanced set is PNG, and PNG is the wrong thing to hand a graphics device. `PbrLab`
compresses it to BC7 for colour and BC5 for normal maps, into
`ContentWorkspace/build/{textures,normals}`, and `DdsFile` reads those. It is preferred
over both the enhanced PNG and the original wherever it has an answer.

Measured on R25 with `--rt high --enhanced`, which wants 43 enhanced textures and 43
normal maps at 2048²:

| | PNG | DDS |
|---|---|---|
| Scene load | 1,644 ms | 623 ms |
| Textures on the device | 1,864 MB | 496 MB |
| Peak working set | 1,942 MB | 1,073 MB |

Three separate reasons, and it is worth keeping them apart:

- **Nothing is decoded.** The blocks go from the file to the staging buffer to the image.
- **The mip chain is already built**, so the device does not blit level to level, and the
  compressor filtered it as a tiling texture, which mip generation here does not.
- **A quarter of the memory**, which is what a block format always gives.

`--uncompressed` turns it off, which is the only way to put the two side by side. Rendering
R25 both ways, the room differs on **0.55% of its pixels by more than 8 of 255, mean
absolute difference 0.21** — that is BC7's error and nothing else. The character in the
frame differs far more, for a reason that has nothing to do with textures: the load is a
second faster, so his idle animation is a second further along.

### Two things it cannot do

**It cannot carry a colour key.** `TextureKeying` works on texels and these are blocks, so
a texture whose original uses GK3's magenta has to take the decoded path. The loader reads
the original — it is 64 pixels square — and asks `TextureKeying.NeedsKey` before choosing.
Three of the 324 in the pilot set need one: `BUTCRYSTAL`, `GRAHANDLE` and `HOTELHLCHAIN2`.
Skipping that check does not fail, warn or look broken in a screenshot of the wrong room —
it paints magenta where the holes should be.

**BC5 keeps two channels.** There is no third to keep, so `PerturbedNormal` reconstructs Z
as `sqrt(1 - x² - y²)`, which is exact for a unit vector in tangent space because Z is never
negative there. An uncompressed map does store a Z, and reconstructing it rather than
reading it agrees to within a rounding step, so both sources take the one path.

### The reader

Deliberately narrow, like `PngReader`: two-dimensional, no arrays, no cube maps, and only
BC4, BC5 and BC7. `DX10` headers are read for the DXGI format, and the older `BC5U`, `ATI2`,
`BC4U` and `ATI1` four-character codes are understood. Anything else is refused by name so
that a pipeline which starts emitting BC1 or a half-float is heard from rather than misread.

The one piece of arithmetic worth testing is the mip chain: nothing in the file says where
a level begins, and a level narrower than four pixels still occupies a whole block. Divide
instead of rounding up and the tail of the chain is zero-length and every offset after it
is wrong — which only shows once a surface is far enough away to be minified.

**A BC4 block is eight bytes, not sixteen**, and every level offset depends on that. It is
the format the height maps take, because every height map the pipeline produces is grey
stored as RGB — one channel of real information in three channels of file. Ask
`CompressedImage.BytesPerBlock` rather than the `BlockBytes` constant anywhere the format is
not known to be one of the sixteen-byte ones.

### Where they come from

Loose `.dds` under `build/`, or a ReBarn pack beside the executable — see
[rebarn.md](rebarn.md). The pack is the shipped form: entries are stored rather than
deflated and aligned to 256 bytes, so a texture is memory-mapped and handed to the device
without being copied at all. Loose files win over the pack for the same name, so a
recompressed texture takes effect without a fifteen-gigabyte rebuild.

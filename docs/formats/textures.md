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

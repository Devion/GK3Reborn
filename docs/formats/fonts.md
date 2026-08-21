# Fonts (`.FON`)

137 of them, and they are how the interface is drawn. Nothing is imported: GK3's own bitmap
fonts are in the archives, they are the size its screens were designed for, and reading one
is a far smaller job than shaping a scalable typeface.

## The definition

Key/value lines with spaces in the keys, so **not** the INI reader — the `Font=` value is a
run of characters that includes `;`, `,`, `=` and everything else the reader would treat as
punctuation, and the whole point is to take it exactly as written.

```
Font=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 !@#$%^&*()-=_+[]\{}|;':",./<>?
Bitmap Name=sid_cap_16.bmp
Line Count=4
Char Extra=0
Line Extra=0
Default Char=<a box>
Type=Color Replacement
Color=107/162/137
```

| Key | Meaning | Present on |
| --- | --- | --- |
| `Font` | Every character in the sheet, in sheet order | 137 |
| `Bitmap Name` | The sheet; otherwise named after the definition | 128 |
| `Line Count` | Rows of glyphs stacked in the sheet | 67 |
| `Char Extra` | Extra pixels between characters | 137 |
| `Line Extra` | Extra pixels between lines | 137 |
| `Default Char` | Drawn in place of one the font lacks | 133 |
| `Type`, `Color`, `Alpha Channel`, `Background Color`, `Foreground Color` | Recolouring; not read | 27–102 |

Read as **Latin-1**, not UTF-8. A third of the characters in a `Font=` line are above 127;
read as UTF-8 they become replacement characters and every accented letter maps to the wrong
picture.

## Where each character is

Nowhere. It has to be measured.

The top row of the sheet carries a marker colour at the left edge of every glyph:

```
row 0   . M . . . M . . . . M . .      M = marker, . = background
row 1+  . A A A . B B B B . C C .      the letters themselves
```

- The **background** is the sheet's own pixel (0, 0).
- The **marker** is the first pixel along the top row that is not the background — usually
  pure red, but not always, and a couple of sheets are a few units off it, so the comparison
  allows slack rather than demanding an exact match.
- A glyph runs from one marker to the next. The last one in a row runs to the edge, because
  nothing marks its end.
- A glyph is **one pixel shorter** than the row that holds it: the marker strip is not part
  of the letter.

## Drawing it

`OverlayAtlas` is the sheet with a block of white added underneath. Every rectangle the
interface draws — a letter, a panel, a divider — is then a piece of the same texture, so the
whole interface is one draw call and the white block is what makes a solid rectangle
possible without a second texture.

Two font conventions have to work and one rule covers both:

| Sheet | Background | Glyphs | Alpha |
| --- | --- | --- | --- |
| `F_ARIAL_T12` | magenta, decoded to transparent | white, hard-edged | already correct |
| `sid_cap_16` | black, opaque | antialiased grey | none at all |

**alpha × brightness.** The magenta sheets keep their crisp edges and lose their black glyph
markers; the black-backed sheets have their antialiasing turned into alpha. Vertex colour
supplies the tint either way.

Colours are authored in sRGB and converted to linear before they reach the vertex buffer,
because the swapchain is sRGB and encodes what the shader writes. Skipping that turns a
0.06 panel into light grey.

## A row's last marker may not be a letter

The marker strip says where letters *start*. On a sheet of one row the last letter ends at
the sheet's right edge and nothing has to say so, and the marker count equals the character
count. On a sheet of several rows the rows are different lengths, so each needs a mark
saying where its last letter stops, with padding after it — and the marker count is the
character count plus one per row.

Reading that terminator as a letter costs the row a character and shifts every character
after it. It is invisible on the single-row fonts, which is why it went unnoticed until the
interface started drawing with the four-row caption sheets and wrote `Gabqiel Lnnk` where
it meant `Gabriel Look`.

Which convention a sheet follows is settled by counting rather than guessing: the `Font=`
line says how many characters there are, so a sheet with exactly that many markers has no
terminators and one with that many plus a marker per row has one each. **112 of the 136
fonts settle outright.** The rest are judged row by row on whether there is any ink between
the last mark and the sheet's edge.

`GK3R1142` reports a sheet that cuts into a different number of pieces than its font
declares. That is the check worth having, because a font whose letters are all somebody
else's looks entirely normal from every other angle.

**24 fonts still do not settle** — the Courier family and the console and tooltip fonts,
whose marker counts come out one to twenty-five over. Nothing draws with them yet; they
belong to the Sidney computer interface.

## Which fonts carry accents

114 of the 137. The ones that do not include every `F_ARIAL` size, which is what the
interface used to draw with — see `../screens.md`.

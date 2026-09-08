# The title screen

GK3 opens on `TITLE.BMP`: a photograph of a weeping angel against a red wall, with the
game's name painted into it, and the port draws its rows over the corner of it. That is
still what a game with nothing but the 1999 discs gets. This describes the other one — the
screen the port draws for itself out of six separate pictures, which moves.

The two are one setting apart and one pack apart. **Display → Title screen** chooses
between them, and the row is dead, with a line under it saying why, on an installation that
has not got `Reborn.rebarn`. Neither choice changes anything else: the theme is
`THEME.WAV` under both, the rows are the same five rows, and the settings pages behind them
are identical.

## The six layers

They live in `ContentWorkspace/enhanced/menu` and are packed into `Reborn.rebarn` under a
kind of their own, `RebarnKind.Menu`. A kind rather than `Texture` because a pack key is the
kind and the name, and these names — `Angel`, `SA`, `Penta` — are short enough to collide
with the game's own texture names. `overrides/menu/<name>.png` outranks the pack, as
everywhere else.

| Layer | What it is | How it is drawn |
| --- | --- | --- |
| `Angel.png` | the statue, cut out to its own edges | over the black, once |
| `RedWOffset.png` | the wall, painted to meet itself left to right | screened over the statue, scrolling |
| `titlename.png` | the game's name, in the middle of a tall transparent sheet | over everything, to the right |
| `SA.png`, `Schat.png`, `Penta.png` | three sigils | multiplied into the wall, one at a time |

They are packed **as the PNGs they were painted as**, not through the block encoder. They
are drawn about one texel to one pixel over a black screen and three of them are large flat
gradients, which is exactly where BC7's blocks show.

`Content.MenuArt` finds them, and it is all six or none: an incomplete set is reported by
name and the game opens on `TITLE.BMP`. A menu missing its statue is not a cheaper menu.
The same rule covers a device that refuses the fifth of six pictures.

## What moves

`UI.TitleScene` owns the whole screen. It reads no clock — the menu's own loop hands it how
long the last frame took, which is what ADR 0004 asks for — and it draws into the menu
page's display list rather than behind it, so the statue and the rows over it are one frame.

Nothing moves quickly. On a 1080-line display the wall travels about eight pixels a second
and the light about six. Both are far below what reads as motion; the point is a screen
that is not still, not a screen with something happening on it.

- **The wall** scrolls left for ever. The picture is painted to meet itself, so this is a
  translation and not a crossfade between two copies. It is drawn in ninety-six upright
  slices a width, and each of them does two things: it thins and thickens as three slow
  waves drift across the window, which is the smoke in front of it, and it hangs a few
  pixels above or below its neighbours as two more do, which is the wall itself breathing.
- **A light out of frame** crosses the statue and comes back. It is the same cut-out drawn
  again in a warm colour, screened over itself through a soft upright band, so what it does
  is lift the stone that is already there rather than paint a colour onto it.
- **Its shadow** leans away from wherever the light is, sheared so that it leans further the
  further it is from the ground, and pinned at the statue's feet. Two hundred and fifty-six
  level slices, because a shear moves every slice by a different amount and the step between
  two of them is the whole lean divided by the count: at forty-eight it was thirteen pixels
  and the shadow was a staircase.
- **A sigil** surfaces in the wall behind the lettering every fifteen to thirty seconds:
  one at a time, never the same one twice running, fading in over four seconds and out over
  four while it rises and turns. Never fully opaque. It is a stain in the wall, not a decal
  on it.
- **The statue's feet** sink into the black the rows are drawn on, so that "Play" is not
  written over a lit marble hem.

The rows themselves are one line of buttons across that black, centred, which is
`MenuPage.Horizontal`. On a window too narrow for them the air between them closes up
first, and only when that is gone does the line run to the edges: a line of five squeezed
until its words nearly touch still reads as five buttons, and one whose words have been cut
does not.

## Four things that were got wrong first

**A slice drawn half a pixel into its neighbour.** Every other layered thing in this
interface is alpha-blended, where an overlap of half a pixel is invisible. Under a screen
blend the overlap is screened *twice*, and the result was a bright upright line every
sixteenth of a wall, sliding across the screen as it scrolled. Slices now abut exactly:
both edges are computed from the tile rather than by adding a width to a left edge, so one
slice's right edge is bit for bit the next one's left.

**One colour a slice.** With the opacity flat across each slice, two neighbours differing
by two per cent showed a step — again invisible under alpha and obvious under screen. A
quad now carries a second colour for its far edge (`OverlayQuad.Gradient`), so the hardware
interpolates the fade and two slices that meet agree exactly where they meet. The shadow
uses the same thing down instead of across, and the black the statue's feet sink into is one
rectangle rather than a dozen bands.

**Fitting the whole wall into the band.** The band is 820 lines of a 1080-line window and
the picture is 1195 tall, so this shrank it by half again — and the interface's pictures
carry no mip chain, because everything else it draws is a map or a thumbnail at about its
own size. A picture minified without one crawls as it moves, which is precisely what a slow
scroll is for noticing. The band now takes a band *out of* the picture at one texel to one
pixel, and magnifies only on a display tall enough to need it.

**A wave with too few slices to carry it.** The wall hangs a few pixels above or below
where the band is, and by a different amount at each slice, which is a discontinuity in the
picture itself: no amount of blending hides it and no gradient helps, because the fault is
in the geometry rather than in the colour. Sixteen slices put a three-pixel step at every
join. The answer is not to give the wave up but to cut the wall finely enough to carry it —
ninety-six slices holds the step under half a pixel at every window size the game opens at,
which is what bilinear filtering is for. The shadow's shear is the same argument with a
harsher constant, and it wanted two hundred and fifty-six.

## Blending

Two of Photoshop's blend modes had to reach the interface, which had only ever drawn one
rectangle over another. Both are fixed-function state — nothing drawn here can read what is
under it — so each is a pipeline of its own in each backend, and the display list is cut
into runs on the blend as well as on the picture. A screen that uses neither, which is every
screen but this one, still costs exactly the one run it did.

- **Screen** is exact, at any opacity. The state is `(one, one minus source colour)`, which
  gives `S + D(1 - S)`; the shader writes the colour already faded by its own coverage,
  which makes that `D + aS(1 - D)` — the screen of the two mixed towards the destination by
  `a`.
- **Colour burn** is not reachable. It is `1 - (1 - D) / S`, and no pair of blend factors
  gives a division. What the sigils use instead is multiply, faded towards leaving the
  destination alone: the state is `(destination colour, zero)` and the shader writes
  `1 - a(1 - S)`. At the opacities a sigil is drawn at the two are within a step of each
  other. At full opacity they are not, which is one more reason nothing here draws at full
  opacity.

And a sigil is **not** multiplied in its own colour. The sigils are painted a saturated red
and the wall they surface in is a saturated red: the wall's green and blue are near nought
already, so scaling them moves a pixel by a step or two, and its red is scaled by the
sigil's red, which is nearly one. The mark was invisible, and real colour burn would have
been almost as invisible — it drives green and blue to nought, which they nearly are. So
what is multiplied in is the sigil's *shape*, in a dark warm ink (`TitleScene.SigilInk`).
It reads as a scorch in the wall, which is what a mark burned into something looks like.

Blending happens in linear light, because the swapchain is an sRGB target and that is where
its hardware blends. Photoshop screens gamma-encoded values, so the port's screen is not
numerically the same as the one the layers were composed against — it is the physically
correct one, and it is what the picture above was tuned to.

## Frame generation

The whole of this screen is the interface's display list, drawn into the swapchain after
everything else. DLSS frame generation hooks the present, so a generated frame is an
interpolation of two frames of *that* — and with no room behind the menu there are no
motion vectors to interpolate it from. What that looks like, on a photograph taken of a
generated frame, is a set of faint level lines across the statue that are in no frame the
engine drew. It is the same interaction the verb bar and the captions have had all along;
the menu is the first screen where something under the interface moves, which is why it is
the first place anybody has looked at it.

Nothing here works around it. The setting is the player's, the artefact is a generated
frame's, and the wall moves at eight pixels a second — two frames of it are within a pixel
of each other.

## Rebuilding

`rebuild-content.cmd` packs `enhanced/menu` with everything else; there is no switch for it
and nothing to derive. Change a layer, run the script, and the new one is in the volume.
`GK3Reborn --extract --kinds menu --from packs` writes them back out.

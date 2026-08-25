# Faces (`FACES.TXT`)

GK3's people have no facial geometry. A head is one mesh wearing one bitmap, and
everything a face does — talking, blinking, raising an eyebrow — is done by patching
regions of that bitmap while the game runs.

`GAB.MOD` proves it: nine textures, and exactly one of them is a face. There is no
mouth to open and no eyelid to lower.

```
GAB_BOOT  GABRIGHTARM  GABLEFTARM  GAB_SHIRT4  GAB_SHIRT3
GABEJEAN2  GAB_FACE  GABE_HAIR  GABPALM
```

## What the file says

An INI file, one section per character, keyed by the three-letter code the models use.
Thirty-two characters have an entry. Keys have spaces in them and values are comma- or
`x`-separated pairs, so it is read one entry a line.

```
[GAB]
Left Eye Offset         = 102,102
Forehead Offset         = 90,77
Eyelids Offset          = 105,106
Eyelids Alpha Channel   = gab_eyelids_alpha
Blink Anims             = gabblink,90,gabblink2,10
Blink Frequency         = 5000,12000
Mouth Offset            = 90,132
Mouth Size              = 78x82
```

| Key | Meaning |
| --- | --- |
| `Mouth Offset`, `Mouth Size` | Where the mouth region sits on the face bitmap, and how big |
| `Eyelids Offset` | Where the eyelids go |
| `Eyelids Alpha Channel` | A bitmap saying how much of the resting eyelids to show |
| `Forehead Offset` | Where the forehead goes |
| `Blink Anims` | Blink animations and the odds of each, in pairs |
| `Blink Frequency` | Shortest and longest gap between blinks, in milliseconds |
| `Face Name` | The face bitmap, when it does not follow the naming convention |
| `Left/Right Eye *` | Sub-pixel eye placement and jitter — read, not used |

Everything else is a convention rather than a list: `xxx_face`, `xxx_eyelids`,
`xxx_forehead`, `xxx_mouth00` to `xxx_mouth07`. Four characters — `VM1`, `VM2`, `VM3`,
`VR3` — break it for the face bitmap alone and say so with `Face Name`.

Two sections, `[CON-XXX]` and `[EM2-xxx]`, still carry the suffix the file's own header
says to remove once the art exists. They are not art that shipped and are skipped.
`[DEFAULT]` supplies `Blink Frequency` to anybody without one; the file says explicitly
that offsets are *not* inherited from it.

## What moves a face

Three kinds of node in an animation's `[GK3]` section, all naming a character by noun.

| Node | Meaning |
| --- | --- |
| `<frame>,LIPSYNCH,<noun>,MOUTH03` | Put a mouth shape on. 98,410 of them — the whole of the game's lip sync |
| `<frame>,FACETEX,<noun>,<bitmap>,<part>` | Paint a bitmap over a region. 860 |
| `<frame>,UNFACETEX,<noun>,<part>` | Take it off again. 408 |

`<part>` is one letter: `M` mouth, `E` eyelids, `H` forehead. Twenty nodes name `L` or
`R` — the two eyes — which nothing paints, because painting them into the wrong region
would be worse than leaving them.

A `LIPSYNCH` node names a *shape* rather than a bitmap: the same eight shapes belong to
all forty-odd characters, and the code in front of it is what says whose mouth it is. A
`FACETEX` node names the bitmap outright. A handful name `MOUTH04_BLOOD`, which is why
"has an underscore" is not a way to tell the two apart.

**Lip sync lives with the recording.** A line of dialogue is one `.YAK`: its `[SOUNDS]`
is the audio and its `[GK3]` is the mouth shapes, against the same frame numbers. So a
mouth follows the words by construction rather than by analysis, and nothing has to be
told who is speaking — every node names its own actor, which is what makes a cutscene
`.YAK` carrying everybody's lines at once work.

**Lip sync is not only dialogue.** 1,362 `.ANM` files carry `LIPSYNCH` nodes of their
own: Gabriel eating a sweet in the lobby is five of them.

**A blink is a `FACETEX` animation and nothing else.** `GABBLINK.ANM` is four frames:

```
[HEADER]
4

[GK3]
4
0,FACETEX,GABRIEL,GAB_BLINK_01,E
1,FACETEX,GABRIEL,GAB_BLINK_02,E
2,FACETEX,GABRIEL,GAB_BLINK_01,E
3,UNFACETEX,GABRIEL,E
```

So blinking needs no system of its own — a timer picks one of the character's two blink
animations by weight and it runs down the same path a raised eyebrow does.

## How it is drawn

`Faces` (Game/Actors) keeps three names per character — mouth, eyelids, forehead — and
whenever any of them changes it pastes the four bitmaps together into a copy of the face,
gives the result to the renderer under a name of its own, and repaints the character's
head with it.

The patches are keyed rather than authored with an alpha channel — a forehead's corners
are magenta and the decoder has already turned that into transparency — so pasting is an
ordinary blend. The resting eyelids also carry an alpha bitmap, because they are a soft
edge against the skin rather than a cut-out.

Order matters and is the order the regions overlap in: forehead, then eyelids, then
mouth. `GAB`'s forehead covers rows 77 to 132 and its eyelids rows 106 to 126, so an
eyelid pasted first is painted over by the brow.

Compositions are cached by what they are made of. A sentence comes back to the same eight
mouth shapes over and over and a blink is the same two pictures every time, so a
conversation in the lobby composes about twenty distinct faces and then stops.

`ISceneSink.Repaint` is what swaps one in: *by texture* rather than by submesh, because a
face is "wherever this model draws `GAB_FACE`" and which submesh that happens to be is the
model's business. Normal maps stay filed under the original texture's name — a repainted
face is the same surface with a different picture on it, and its bumps have not changed.

## What is not done

* The eyes. `FACES.TXT` describes sub-pixel eye placement, a field of view per eye and a
  jitter frequency, none of which is read into anything that moves. Eyes are part of the
  face bitmap and do not track the player.
* ~~The `[LISTENERS]` section.~~ Done. A scene names talk and listen scripts **per
  conversation** rather than per actor, along with an animation to enter the conversation
  and one to leave it — 237 lines across 75 rooms. `SetConversation` hands the named
  actors those scripts and plays the enter animation; `EndConversation` plays the exit and
  gives them their own scripts back. The pair matters: without the exit, Mosely is still
  leaning on the Armorer's counter for the rest of the afternoon.
* Enhanced face bitmaps. Compositing reads the original archives, so a character whose
  face had a higher-resolution replacement would lose it while talking. None of the
  pilot set has one, so nothing does today.

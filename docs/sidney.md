# Sidney

Grace's portable computer, and the one screen the story cannot be finished without.
Parchments are scanned into it, analysed and translated, and `DoesSidneyFileExist` is a
real condition in the game's own action files.

## Almost all of it is data

`ESIDNEY.TXT` holds the menus, the screen names, and — the part that matters — the *actual
output* of every analysis, keyed by what was analysed: `AnalyzeParch1`, `ExtractParch1`,
`Parch1French`, `GeometryParch2`, the Latin word list for the anagram parser, the
translations. `ESIDNEYEMAIL.TXT` holds six complete messages with their headers and
paragraphs.

So the engine supplies the machine, not the words: which files exist, which operation
applies to which, what each one unlocks, and what the story may notice afterwards.

**`KeyedText` reads these files, not `IniDocument`.** A `.SIF` line is a list of
comma-separated settings and the scene reader splits it as one; these hold a single value
that runs to the end of the line, and most of them are English sentences full of commas.
Reading one with the other turns a paragraph of Grace's mail into forty settings, silently.

## Files

Eight names appear in the game's conditions, and they are all of them — found by grepping
strings out of `GK3R.EXE` and confirmed against the `.NVC` files:

`fileParchment1`, `fileParchment2`, `fileMap`, `filePainting1`, `filePainting2`,
`filePainting3`, `fileHermNote`, `fileSUMNote`.

**Scanning is already an ordinary action.** The noun is the inventory item, the verb is
`SCANNER`, the case is `IN_SIDNEY_ADD_DATA`, and `INV_ALL.NVC` carries the script — which
marks the item used and sets a `SidScanner` variable to a number between 1 and 35. Twenty-
nine items can be scanned.

What the original did with that number lives in its executable. What it has to mean here is
the file the scan produces, and `SidneyFiles` is that table, keyed on the item's own name
because that is where the meaning is: `PARCHMENT_1` becomes `fileParchment1`. Fingerprints,
tapes and licences are recognised by what their names say; they get no story-visible file
name because nothing asks `DoesSidneyFileExist` about one.

**`AddSidneyFile` had no caller at all before this.** Every `DoesSidneyFileExist` in the
game answered no, for ever. That is what the add-data screen fixes.

## What has been done

Recorded as flags on the story — `SidneyDid:fileParchment1:ViewGeometry` — rather than kept
in the machine, so it survives a save and the story can read it the way it reads everything
else. Sidney's file list is derived from `GameState` for the same reason: the story is what
a save records, and Sidney holding its own copy would be a second answer to the same
question.

## What works

| screen | state |
|---|---|
| add data | scans anything the table knows, and refuses what it does not |
| e-mail | all six messages, with headers and paragraphs |
| analyze | the file list, the operations that apply, and the text they produce |
| search | 391 pages of encyclopedia, reached by the 908 spellings the game lists |
| suspects | ten files, linking, un-linking and fingerprint matching |
| make I.D. | fifteen identities across five trades, printed to a flag the story can read |

### Search

`SIDSEARCH.TXT` lists 393 subjects, each naming a page and the spellings that should find
it — "arcadia, et in arcadia, sheperds, shepherd" — and the pages are small cross-linked
HTML documents in the archives. `SidneySearch` reads both.

**The subject list is not shown.** 391 pages offered as a menu is a walkthrough; the puzzle
is knowing what to look up. The player types, and what they type is matched against the
game's own spellings and nothing cleverer — that index already carries the variations
somebody thought of, and guessing past it would hand over pages the puzzle means them to
work for.

The markup is *read*, not rendered: these are 1998 pages using about eight tags, and what
the interface needs is headings, paragraphs, rules and links. Anything else is dropped
rather than shown, because a stray `<FONT>` in a sentence about Rennes-le-Château is worse
than no formatting.

### Suspects

Ten people, their nationalities and their vehicle identifications, all from the game's own
text. Files are linked to a suspect and un-linked again, with every refusal the original
wrote — no suspect open, already linked, a fingerprint where one is linked already — because
those are the rules the puzzle is played against.

**Fingerprint matching: a known print carries its owner's name, and that is the whole rule.**
`ABBE_FINGERPRINT` is the Abbé's. `BUCHELLIS_FINGERPRINT_LABELED_WILKES` is **Buchelli's**,
however it is labelled — which is the story point that item exists to make, and an engine
that believed the label would quietly convict the wrong man. There is a test for exactly
that. An *unknown* print matches nobody, which is what the game's own analysis says it is
for: bringing it here to be matched against a known one. Gabriel's and Grace's prints have
their own answers written in the text.

What is linked, and what has matched, are flags on the story, so they survive a save.

### Make I.D.

Fifteen jobs across MEDICAL, REPORTER, REPAIR, SALES and POLICE. Printing one sets
`SidneyId:<title>`, which is a flag the game's conditions can read and a save keeps.

### Analyze

The screen offers **only the operations that apply to the open file**. The original left
every menu item enabled and answered most of them with a note about why not, which is making
the player find the answer by exhaustion — against `screens.md`'s rule that the interface
should never have to be learned.

The chain the story needs:

- **Parchment 1** — start analysis, extract anomalies, then a language. French gives the
  Dagobert line; English and Latin say why they cannot, which is written in the file and is
  not a failure. View geometry saves the shape.
- **Parchment 2** — start analysis, analyze text (which searches, in the machine's own
  words, before asking a language), rotate shape, view geometry.
- **Everything else** — recognised for what it is: a fingerprint known or unknown, a tape
  that may be translated, a licence plate, the map.

### The map

`SIDNEYBIGMAP.BMP` is a 1,368-pixel survey of the Rennes country with the Paris meridian
drawn down it, and the puzzle on it is the one the books are about: mark the churches and
the ruins, and see what they fall on. Opening the map file gives the analyze screen the
picture instead of a paragraph; clicking marks a place, and every mark re-measures the set.

**The geometry is measured, not scripted.** The original could have checked the player's
points against a list of right answers and printed the matching note. Fitting instead means
a player who marks four *other* places that genuinely lie on a circle is told so, and one
who marks the right places sloppily still is. Tolerances are in map pixels and generous,
because the player is clicking a village on a picture.

| what the points make | what the machine says |
|---|---|
| a straight line | `MapLine3Note`, or `MapLineDisallow` for only two |
| four corners | `MapRectNote`, and a flag the story can read |
| four on a circle | `MapCircleConfirmNote`, with the centre's coordinates |
| more than four, scattered | `MapSeveralPossNote` |

**The rectangle is tested before the circle, and it has to be.** Every rectangle's corners
lie on a circle — that is what a circumcircle is — so asking about the circle first answers
"circle" for all of them and the four-to-one rectangle the story is *also* looking for could
never be found. A test covers exactly that case.

**Coordinates are approximate and say so.** The circle note quotes its centre's position,
and Sidney's map carries no georeference anywhere in the game's data: `GPS.TXT` belongs to
the handheld device in three outdoor scenes, not to this. What is used is a linear fit
anchored on the meridian the map draws and on the region's extent, stated as five constants
at the top of `SidneyMap` so anybody who measures them properly can correct them in one
place. Good enough to read out; not good enough to navigate by.

### Shapes

**Shapes are earned, not offered.** Every geometry analysis ends with "The shape has been
saved", and until one has been run the map's `ShapeList` is empty — which is the whole
reason to run them. What each picture grants is what its own text says it found:

| picture | what its analysis names | what it grants |
|---|---|---|
| parchment 2 | "form a perfect square", "the presence of a circle" | square, circle |
| Poussin | "Second triangle forms hexagram shape" | triangle, hexagram |
| Teniers | "indicate a square" | square |
| parchment 1 | names no shape — only "suggest this image" | circle |

Parchment 1 is the one guess in that table, and it is written down here rather than buried:
its note says the devices suggest an image without saying which, and the circle is the
figure that locates the site.

**A shape is fitted to the marks, not dragged into place.** The player has already marked
the places they think matter; the question the screen exists to answer is whether a circle —
or a square, or a hexagram — passes through them, and making them drag a template around
first is asking them to do the analysis by hand. So `USE SHAPE` centres it on the marks,
sizes it to reach the furthest, and turns it so a corner meets the first one marked.
`ROTATE SHAPE` turns it fifteen degrees a step, which is fine enough to find a fit and
coarse enough that finding one takes a few presses rather than a hundred.

**Locking is measured the same way the geometry is.** Every marked place has to lie within
26 map pixels of the outline — the circle's circumference, or the nearest of the sides
between a shape's corners. When they all do, the machine says `MapShapeLockNote` and sets
`SidneyShape:<name>`, a flag the story can read and a save keeps; when they do not it says
`CirclePointsNote`, which is the game's own "Select points to lock down feature". A shape
already laid is re-fitted every time another place is marked, so a confirmation cannot
outlive the marks that earned it. The locked shape is drawn in green and an unconfirmed one
in the screen's usual amber.

**A hexagram is drawn as its two triangles**, not as a twelve-sided outline, because that is
what the analysis of Poussin's painting describes finding — one triangle, then a second
forming the star — and those triangles' sides are what a place has to lie on.

**`ROTATE SHAPE` means two things**, which is what the original's single menu item does: on
a parchment it turns the symbolic device and reads it again, on the map it turns the
template laid over the country. The open file decides which.

Straight sides are drawn as a run of single pixels rather than one rectangle per side. The
overlay draws rectangles, and a rectangle covering a diagonal side's bounding box is a
filled block — right for the axis-aligned grid, wrong for every shape that has been turned.



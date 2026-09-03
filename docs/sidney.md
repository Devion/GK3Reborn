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

## It is a laptop, not a page

The original draws the machine and puts its interface inside the screen. The port's first
pass drew a dark panel over the room instead, which reads as the game's own menu rather than
as Grace opening her computer — and the art for the laptop had been in the archives all
along, unreferenced.

`S_SID_BKGD1024_{TOP,BOTTOM,LEFT,RIGHT}_A.BMP` are four pieces that assemble into a
1024x768 picture with a **640x480 hole** where the screen is, photograph and keyboard
included. `SidneyLaptop` composites them and hands the rest of the interface that hole.

**What is fitted to the window is the screen, not the picture.** Fitting all 1024x768 into a
16:9 window spends a fifth of the height on the desk above the lid and the keyboard below
it. So the band that is fitted is the screen with thirty pixels of lid above and ninety-six
below — enough to read as a laptop, enough for the photograph — and the rest runs off the
edges, where the clip takes it. The room behind goes dark rather than dim: Grace is looking
at a screen in her hands.

## A desktop, not a menu

Eight programs as icons, in two columns down the left, over the machine's own
`S_MAIN_SCN.BMP` wallpaper dimmed to a third. A taskbar under them carries the way home, the
story's clock, the mail light and a **power button** — which is the original's `EXIT` row
said in the one symbol that needs no translating.

**The game has no icons.** Its art for the eight screens is eight 76x13 amber name plates —
`B_SEARCH_U.BMP` and its hover and pressed states — which are captions, not pictures, and a
desktop of eight identical amber bars is a menu with extra steps. So the plates stay as the
captions they were drawn to be, and the picture above each one is drawn in `SidneyGlyphs`
from the same rectangles, lines and circles the rest of the interface is made of. They scale
with the icon, so there is no size at which they are the wrong resolution.

`Files` is a screen here. The game's own main menu has no row for it — its file list lives on
the front screen — but the original's screen bar does, the button art is in the archives
beside the other seven, and on a desktop the file store is a place you go to.

**The mail notification is the original's `NEW E-MAIL` light**, moved to the corner and given
something behind it: `SidneyRead:<id>` is a flag on the story, so opening a message turns the
light off and a save keeps it that way. Before this, nothing ever marked a message read. It
is drawn only on the desktop — over a running program it would sit on the bottom right of the
suspects screen, which is where the button the fingerprint puzzle ends on lives, and take its
clicks.

## Nothing is out of reach

Every list goes through `SidneySurface.BeginScroll`, which clamps the offset, draws a bar
when one is needed and clips the region. Two things made that necessary rather than tidy:

- **The suspects list drew nine of its ten names.** It ran until it reached the bottom of the
  panel and stopped, so Franklin Mosely — and with him the only way to link the print that
  names him — did not exist at ordinary window sizes. There is a test that scrolls a list to
  its end.
- **`MAKE I.D.` drew its way home over its own last row.** The five menus always reach the
  bottom, and the button was placed at a fixed offset from it. The way home is in the taskbar
  now, out of the content entirely.

**The map is what sizes the display list.** A circle drawn as axis-aligned rectangles costs
about its circumference, so four figures over a 4K map come to some five thousand of them —
against a buffer that held four thousand, which took the taskbar off the bottom of the screen
the moment somebody laid a third figure on a large window. Both backends hold sixteen
thousand now, which is about three megabytes a frame in flight and leaves the room the
`GK3R3610` warning is there to notice running out of. A test lays every figure over the map
at sizes up to 3840x2160 and checks the count.

Clipping is done in `Overlay` as quads are added — the destination trimmed to the region and
the source trimmed with it in proportion — so the whole interface is still one draw call and
a half-scrolled letter looks like half a letter rather than a whole one squashed.

## Two buttons that did nothing

`OnScreen` dispatched `sidney:<what>:<which>` and required the `<which>`. Every Sidney
command that is a whole command on its own fell through to `default` and was dropped. Two of
them were drawn, clickable and dead:

- **`SEARCH`** — the button on the search screen. Only Enter worked, and nothing said so.
- **`MATCH ANALYSIS`** — the button the fingerprint puzzle ends on.

The subject is optional now. This is the kind of fault a screenshot cannot show and a test
can: both are covered by asking the painter what is at the point the button was drawn.

## What works

| screen | state |
|---|---|
| add data | scans anything the table knows, and refuses what it does not |
| e-mail | all six messages as a mail program: sender, subject, date, and a reading pane |
| files | everything scanned in, with what the machine takes each to be |
| analyze | the file list, the operations that apply, and the text they produce |
| translate | four languages, the refusals, and the exchange that finishes the inscription |
| search | 391 pages of encyclopedia, reached by the 908 spellings the game lists |
| suspects | ten files, linking, un-linking and fingerprint matching |
| make I.D. | fifteen identities across five trades, printed to a flag the story can read |

### Translate

**It was not a stub; it had never been read.** `ESIDNEY.TXT`'s translate section carries the
four languages the screen offers, every refusal it gives, and both halves of every piece of
text the story needs turned into English: the Abbé's telephone call in French, Buchelli's in
Italian, and the Latin off the tomb. The screen answered "Not implemented yet" for all of it.

**The from-language is a real choice and a real refusal.** The screen asks what a text is
written in before it will translate it, and answering wrongly gets `WrongFrom` — which is
why it has a menu of four rather than one button.

**Et in Arcadia Ego is the one that is not a translation.** Turning it into English gives an
unfinished sentence and the machine offers to add to it; the word that finishes it is `Sum`,
which the player has to have found. Matched against that word and nothing cleverer, because
accepting anything that merely looks Latin would hand the puzzle over. `SidneyText:ArcSUMText`
records it.

### E-mail

Sender, subject and date in the list, headers and paragraphs in the pane beside it, unread
marked down the edge — which is what a mail program has shown since before this one was set.
The first pass listed the six subject lines alone, which is the field the file happens to be
keyed on rather than the thing a reader wants.

The address is turned into a name by dropping what follows the at-sign and turning
underscores into spaces. **Full stops are left alone**: the sixth message is from
`s.pam@easteregg.com` and "s pam" throws the joke away.

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

**A registration is not known until a plate has been linked to somebody.** The screen used
to print all ten the moment it was opened, which hands the player the answer to the very
plates they are out photographing. The data draws the line itself: six suspects carry a
registration and there are only five of them — `VDG945F`, `HJK841J`, `FKS427G`, `FED039A`,
`ASD257K` — because **Lady Howard and Estelle Stiles share a car**, which is the story point.
Five `*_LICENSE` items exist, one per plate, so Estelle's registration can only ever be
filled in by linking Lady Howard's licence to her. The other four carry what anyone could
tell by looking: "Van", "Blue Sedan", "Auto?", and for the Abbé the game's own "Unknown". So a
registration is hidden behind `Unknown` until its owner has a licence linked; a description
is shown from the start, because the player already saw the car. Two of the game's own
strings say this is how it was meant to read — its refusal for a second licence is
"Vehicle information has already been determined for this suspect", which only means
something if there was a point at which it had not been, and its analysis of a plate says to
"use on Suspects Screen to link vehicles to suspects".

Nationality is not gated. It is on the file from the start, and the one nobody knows —
Emilio's — the text already writes as "Unknown" itself.

**A match sets the flag the story is waiting on, which is not the one the port was
writing.** `SidneyMatched:6` was written and read by nothing at all. The game's scripts ask
for `Matched`*Noun* — `MatchedButhane`, `MatchedBuchelli`, `MatchedEstelle`, `MatchedMosely`
are all named in them — and `MatchedEstelle` is live in two places: it opens the `T_LSR`
topic with Estelle in the lobby (`LBY306P.NVC`), and it gives Grace a voice-over over the LSR
envelope in the bag (`INV_ALL.NVC`). Until this was fixed, matching her print unlocked
nothing.

**And a print is matched against the noun the game knows somebody by, not their surname.**
Three of the ten are named after something else — the Abbé by his title, Estelle Stiles and
Larry Chester by their first names — while every item is named after the noun. Comparing
surnames meant `ABBE_FINGERPRINT`, `ESTELLES_FINGERPRINT` and `LARRYS_FINGERPRINT` matched
nobody at all, so three suspects could never be convicted of anything. There is a test that
walks all nine prints the game ships past all ten suspects.

What is linked, and what has matched, are flags on the story, so they survive a save.

### Add data

The screen lists what the player is carrying that the scanner will take, which is the port's
own drawing of a moment the original played as the inventory opening over Sidney.

**Scanning runs the game's own `SCANNER` rule, and adding the file is only half of it.** The
port used to add the file and stop, so none of the work the original hangs off that rule ever
happened. What was being skipped:

- `SetInvItemStatus(item, "USED")` — which **takes the item out of the bag**. Prints and
  tapes are consumed by scanning them; maps and parchments are not, and their rules say so by
  leaving the call out.
- `CallSheep("inv_all", "Estelles_Print")` — which records her print for *both* egos with
  `SetNounVerbCountBoth`, and the licence scans for Buchelli, Emilio and Mosely.
- The noun/verb count, without which a THINK on the Poussin or Teniers postcard goes on
  telling the player to scan something they have already scanned, for ever.

The rules are written against `IN_SIDNEY_ADD_DATA`, which is `IsTopLayerInventory() &&
GetFlag("UsingScanner")` — the scanner is up, and the player is picking something out of the
bag. Because the port draws that moment inside Sidney rather than as the inventory over the
top of it, both halves are made true across the call and put back afterwards. It resolves on
its own from there: **a case somebody defined is worth 7 against `GABE_ALL_INV`'s 2**, so the
rule that scans wins over the two that only say something about scanning. There is a test for
that precedence, because it is the whole thing this depends on.

`SidScanner` is set by those rules and read by nothing in the data — it was how the original's
executable heard which item had been scanned, and the port does that job directly.

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

## The names the story asks about

**The map puzzle's conditions were all dead.** Grepping the action files for what they read
about Sidney turns up seventeen flags, and the machine was setting none of them — it had
invented its own. `R25307A.NVC` will not end its timeblock without
`GetFlag("LockedHexagram")`; seven conditions across the game ask about `LockedSquare`; four
ask `AnalyzedGeomParchment1` and its three siblings; three read `ArcadiaComplete`. The
machine was writing `SidneyShape:Hexagram`, `SidneyDid:fileParchment1:ViewGeometry` and
`SidneyText:ArcSUMText`. Every one of those conditions answered no, for ever — the same
fault as `AddSidneyFile` having had no caller, and this one blocks a timeblock.

Both names are written now: the machine keeps its own for the screen's bookkeeping and sets
the game's beside it.

| what happened | what the game reads |
|---|---|
| geometry viewed on a parchment or painting | `AnalyzedGeomParchment1`, `…2`, `AnalyzedGeomPainting1`, `…3` |
| a figure locked on the map | `LockedCircle`, `LockedSquare`, `LockedHexagram` |
| the Arcadia inscription finished | `SavedArcadiaText`, `ArcadiaComplete` |
| the anagram begun | `StartArcadiaAnagram` |

`filePainting1` is the Poussin and `filePainting3` the Teniers without its temple, which is
how the files are already numbered, so the flag is the file's own id with `file` swapped for
`AnalyzedGeom`.

**Four more are read and nothing sets them yet**: `TempleFloorPlan`,
`OpenedTempleDiagram`, `PlacedTempleDivisions` and `PlacedWalls` belong to the Temple of
Solomon diagram, which is the second of Sidney's illustrated messages and is drawn here as
text. `UseCoordLER`, `UseCoordBEC` and `UseCoordMCF` belong to the handheld GPS rather than
to Sidney.

## The four menus

The analyze screen's operations sit under `OPEN`, `TEXT`, `GRAPHIC` and `MAP` — the menus
`ESIDNEY.TXT` groups them into. Laid out flat they wrapped onto three rows on the map, which
has eight of them, and pushed the picture they act on off the bottom of a screen that is 640
pixels wide to begin with. Only the menus with something applicable are shown and one opens
at a time, so the row is always one row.

**`UNDO POINT` is the port's own.** The original offers `ENTER POINTS` and `CLEAR POINTS`,
so one misplaced click costs every place marked so far. The puzzle is played by clicking
villages on a picture and a misplaced click is the ordinary case.

## The line, and the ruling

**The line was recognised and never drawn.** Two places joined is the first step of the
whole map puzzle — the sunrise line from the church at Rennes-le-Château to the tower at
Blanchefort — and `Measure` has always returned `MapFinding.Line` for it. Nothing put it on
the picture, so the machine told the player their two points made a line and showed them
nothing. It is drawn now in the original's black, right across the country rather than
between the two places: what the puzzle asks is what *else* the join passes through, and a
line stopping at the second village answers nothing.

**The grid has five sizes and two ways of filling.** `ESIDNEY.TXT` offers `Grid2` through
`Grid16`, then `GridFillScreen` against `GridFillShape`; the port ruled eight by eight over
the whole map and offered no choice. The chessboard the Gemini and Cancer passages are about
is eight by eight ruled *inside the tilted square*, which a grid that can only run
north-south cannot draw — so a ruling inside a figure is laid between its opposite sides and
turns with it.

**Arques was measured, not guessed.** The enhanced `SIDNEYBIGMAP` is 2,736 pixels square —
exactly twice the coordinates the marks are kept in — and the village's own block of
buildings, not its label, sits at (2523, 330) on it, which is (1262, 165) in the map's own
pixels. The meridian the same crop shows falls at x=1795, or 0.656 of the width, which is
what `MeridianAcross` already said. So `MapLine1Note` — "intersects with meridian and point
'Arques'" — is answered by two tests the engine can actually make, and the sunrise line the
whole puzzle opens on now earns the note the game wrote for it.

**The verdict used to wait for a third place.** A line is a thing made of exactly two, and
the note about one was only appended once there were more than two marks — so the sunrise
line was marked and never remarked on. Two is the interesting case, not the dull one.

**The map is a target only while it has been armed.** `ENTER POINTS` is a toggle and the
picture takes a click only while it is on, which is what the original's menu item is for:
clicking a map is otherwise ambiguous, and a click meant for a menu behind the pointer put a
village on it. An armed map carries an amber border and the button carries the same colour.

**A marked place can be picked up and put down.** The original offers `ENTER POINTS` and
`CLEAR POINTS` and nothing else, so one stray pixel costs every place marked so far.
Pressing on a place drags it; letting go re-measures the set and re-fits every figure, so a
confirmation cannot outlive the mark that earned it.

**Dragging is a press, not a click.** `WasClicked` is raised on the way back *up*, and only
when the pointer has hardly moved since it went down — which is exactly what a drag is not.
Built on it, a place dragged across the map produced no click at all and a place merely
pressed produced one with the button already released, so nothing ever moved. The place is
picked up on the edge of the press instead, which is the one gesture the window reports that
a drag is made of. The place is kept on the map, because a
place dragged off the edge is one the analysis would measure somewhere the picture does not
show.

**`MapLine4Note` is still not chosen.** "Landmark feature connects points" is the snake —
the railway north of the site — and the engine has no idea where the railway runs. Saying it
on a guess would confirm a passage the player had not solved.

**Marks are blue dots, not amber crosses.** The map is a photograph of pale green country,
not a screen, and the interface's own amber at one pixel wide is the same colour and about
the same size as the contour shading it sits on. The original marks places in solid blue and
draws what it finds in blue and black; so does this, with a pale ring round each dot so it
reads on the white ridges as well as in the dark valleys.

**The figures are offered as themselves, down the side of the map.** A `USE SHAPE` button
that opened a list of *words* over the map was two steps and a covered map to do what one
look at a row of outlines does. Each button is the figure drawn small; clicking it lays that
figure and clicking it again takes it off, so the row is also how they are unstacked. Its
frame says what it is: plain for not laid, blue for laid, green for confirmed. Where no
geometry has been read yet the column holds one empty box and the words "no figures saved
yet", because a blank column teaches nobody that figures are coming.

**A circle is fitted to every marked place, not to the first three of them.** The exact
circumcircle of three points is right when there are three; with five it sailed off the top
of the map through whichever three had been clicked first, ignoring the two at the bottom.
`FitCircle` solves the ordinary algebraic fit — every place satisfies one circle's equation,
which is linear in its three coefficients — around the centroid rather than the map's corner,
so the arithmetic keeps its precision. Places in a line are refused rather than given an
enormous circle, and nothing beyond twice the map's extent is accepted.

**Nothing drawn on the map may leave the map.** A figure is fitted to places the player chose
and no arrangement of them keeps it inside the picture. The map pushes a clip around
everything it draws, so a figure that runs off is cut at the edge instead of being drawn
across the rest of Sidney and out over the title bar.

## Choosing a figure, and what it costs

**A figure is chosen, and then its places are marked.** Pressing a figure's button says what
the places about to be marked are *for*; the figure re-fits as each one lands, so it is never
a step behind what the player can see. Marking first and naming the figure afterwards works
too — the places already down become that figure's — so neither way round loses the other's
work.

**Choosing a figure never throws one away.** Pressing the same button again used to take the
figure off and its places with it, which is a lot to lose to a stray click on the step that
took longest. It picks the figure up to be edited instead. `ERASE SHAPE` is how one goes, and
it is its own deliberate act.

**A figure takes no more places than its answer is made of** — a line two, a triangle three,
a circle or square four, a hexagram six. Letting somebody put eleven on the map and then
wonder why nothing confirms is a puzzle made of arithmetic they cannot see. Each button
carries its own tally — `2/4`, and `OK` once the figure is confirmed — which is the one thing
the original never tells anybody.

**`SCHATGPT` finishes the figure being marked, and only one that has been earned.** A figure
is offered by a picture the player has analysed; one they have not earned is one the machine
has no business knowing about, and handing it over would answer a question they have not
asked. Within that it fills the remaining places from the crosses the survey itself carries,
and stops when it runs out — what is left after that are places only the player can find,
which is the part of the puzzle worth keeping.

**A figure owns the places it was fitted to.** One shared set of marks meant that plotting
the square's four corners re-fitted the circle to eight places and threw it off the four it
had been confirmed by — and the puzzle is a stack of figures each answering to its own
places. Laying a figure takes the working set as its own and leaves a clean map for the next
one; every figure is re-fitted only against what belongs to it. A figure's places are drawn
a little smaller than the working set, because they are settled.

**The line is a figure, not just a finding.** It was recognised, drawn, and then lost the
moment the next place was marked — so the sunrise line could not survive into the step that
needs it. It is on the row of figure buttons now, always offered, because nothing grants it:
it is the tool the puzzle opens with, two places joined before any picture has been read.

**A square with no places of its own goes round the circle.** "Fit exactly on the outer edge
of the previous circle" is what the Aries passage asks for, so laying a square over a circle
with nothing marked circumscribes it — radius times root two, turned forty-five degrees.
Mark four corners instead and it fits those.

**The survey is drawn from the enhanced set.** It is 1,368 pixels shown in about 450, the
places the puzzle is played on are red crosses three pixels across, and the screen can now be
zoomed six times into it — which is the strongest case for an upscale anywhere in the game.
The driving map had preferred the enhanced copy for years, with a comment saying exactly
why; Sidney's map, the bigger case, was still taking the original out of the archives. The
enhanced one is 2,736 square.

**`SCHATGPT` places the next place the survey marks.** An aid to clicking, not an answer:
the crosses are three pixels across, and a player who knows perfectly well they want
Bugarach can still spend a minute failing to hit it. Which places matter, and what to make of
them, is still theirs.

Their positions were **found in the picture, not copied from a walkthrough**: the survey is
green and white and carries exactly five red marks, so scanning the enhanced copy for red and
clustering what it finds gives five, and cropping each one reads its label off the map —
Rennes-le-Château, St-Just-et-le-Bezu, Bugarach, Coustaussa and Montazels. Four of them are
the four the circle wants, and a test presses the button four times and checks the circle
locks.

**The wheel looks closer, and a drag slides.** Zoomed six times into the survey there is more
country off the glass than on it, so dragging the map itself pans it. A click still marks,
because a click is a press and release that hardly moved — which is exactly what a drag is
not, and the same distinction that makes picking a place up work.

**The wheel looks closer.** The map is 1,368 pixels shown in about 450, so marking the church
at Rennes-le-Château means clicking a dot three pixels across — and the original's own
walkthrough says to enter points "on the magnified map". The wheel over the map zooms it
from one to six times about the pointer, keeping the place under the pointer where it is.
Everything drawn goes through the same two numbers — where the view starts and how much of
the country is on the glass — and `ScreenPainter.MapAt` turns a click back through them, so
a mark means the same place at any zoom.

**More than one at a time.** The books this game is built on lay a circle over a square over
a pentagram and read the country off where the lines cross; a screen that holds one figure
at a time makes the player remember the last one. `SidneyMap.Laid` is the list, the most
recently laid is the one `ROTATE SHAPE` turns, and every one of them is re-fitted whenever
another place is marked.

**What is on the map is part of the story, so a save keeps it.** The puzzle runs over several
sittings — mark a village, go and read a painting's geometry, come back and lay the figure it
saved — and the marks, the figures and the ruling all live in `GameState.SidneyMap` and come
back with a save. Whether a figure is *confirmed* is worked out again from the marks it is
restored beside rather than taken on trust, because it is a fact about the two together.

**Only the analyses are ever finished.** An operation that has been run is drawn greyed, which
is right for extracting a parchment's anomalies and wrong for everything the map does: points
are entered and cleared and entered again, the grid comes and goes, a figure is turned a step
at a time. Greying those the moment they were used once said they were spent when they were
not.

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



# Screens, and getting out of them

GK3 puts a lot of things in front of the room: the inventory, an item held up close, the
binoculars, the fingerprint kit, the driving map, Sidney. In the original each arrived
with its own way in and its own way out, and the inventory itself was a small target to
click at the edge of the screen.

`Plan/03-gameplay-ui-audio.md` section 3 asks for the opposite — that the modal systems
"share navigation, back behavior and scaling conventions" — and section 4 says outright
not to reproduce the old UI. `ScreenLayers` is the mechanism that makes that possible:
one stack, one way out, one rule about where the inventory is reachable.

**Reproducing the original's awkwardness is a defect, not fidelity.** What must not
change is what an action *does* (§2.3) and that no puzzle action fires because the engine
guessed (§2.1). How the player reaches things is ours.

## The three rules

**One Back.** `Back()` closes whatever is on top and puts the player where they were. It
closes exactly one thing, so backing out of an item held up close returns to the
inventory it came from rather than all the way to the room. Backing out of the room is
not an error; it is simply nothing.

**Asking again brings a screen forward** rather than opening a second copy. A player who
asks for the inventory while it is buried under an inspect panel gets what they meant,
not two panels to close.

**The inventory is reachable wherever the player's pockets are.** `InventoryReachable`
is true in the room, true over any panel that leaves the player where they were — the
binoculars, an inspect view — and true when the inventory is itself on top, so the same
binding shuts it again. It is false in exactly one place: the driving map, where the
player is somewhere else entirely.

That last is the only screen whose `TakesOverInput` is true, and the distinction is worth
keeping sharp: a *panel over the room* and *being somewhere else* are different things,
and only the second is a reason to take a control away.

## What scripts do with it

Screens are game state, not presentation. Scripts ask what is showing —
`IsTopLayerInventory` is a real question in the data — and behave differently by the
answer, so it is part of the state hash: two runs that disagree about what is in front of
the room have diverged.

| function | effect |
| --- | --- |
| `ShowInventory`, `HideInventory` | open and close the inventory |
| `InventoryInspect(item)`, `InventoryUninspect` | hold an item up close |
| `InspectObject(noun)`, `InspectModelUsingAngle(model, …)`, `UnInspect` | look at something in the room |
| `ShowBinocs` | the binoculars |
| `ShowDrivingInterface`, `FollowOnDrivingMap` | the driving map |
| `ShowFingerprintInterface(noun)` | the fingerprint kit |
| `IsTopLayerInventory` | whether the inventory or an item held up close is on top |

`HideInventory` closes the inspect panel with it. The original leaves it behind, which is
a bug it shipped with: scanning an item from the inspect screen left the panel over a
room it had no business being over.

`SetVerbModal(1)` records that the story wants one of the offered actions rather than a
shrug. The flag is kept faithfully, and the interface still owes the player a way out
that chooses *nothing* — a modal question is a reason to keep asking, never a reason to
trap somebody in a menu.

`SetInvItemStatus(item, status)` takes the six statuses the original accepts —
`NotPlaced`, `Placed`, `Used`, `GabeHas`, `GraceHas`, `BothHave` — and boils them down to
who is holding the thing, which is all any of them ever meant. The first three say
nobody. An unrecognised status is ignored and reported, as it is in the original.

```bash
GK3Reborn.Tools render-scene --model BEC --timeblock 312P --do EXIT_TO_MAP:EXIT ...
```

rides the moped out of Le Serpent Rouge and reports `in front of the room now: Driving,
inventory out of reach`.

## What is drawn

The launcher now draws, and what it draws is the three rules taken literally.

**The pointer says what a click will do, in words, before the click.** Hovering anything
the room names puts its label under the pointer — `Bathroom Door` in white, `Look` in
amber — and the amber is present only when there is actually something to do. Hunting for
a hotspot is not a puzzle, and neither is guessing which icon means "look".

**Everything it answers to is one right-click away**, as a plain list of verbs with the
noun as its heading, not a ring of icons whose meanings have to be learned. The row under
the pointer is lit; clicking it performs that verb, clicking anywhere else dismisses it.

**The original's icon is beside the word, not instead of it.** `VERBS.TXT` names a resting
and a lit picture for all but three of the 287 verbs, and those pictures are the whole of
what the original's ring ever showed — a magnifier for Inspect, an eye for Look, a speech
bubble for Talk. They are 32-pixel squares in the archives, so a menu row is built tall
enough for one at the size it was painted rather than resampling it down to fit a line of
text; the picked-out row draws the lit one, which is the ring's own second use for them. A
verb the file gives no picture keeps the indent so the words stay in a column, and a list
too long for the window shrinks the art rather than the list, because a row below the
bottom edge cannot be clicked at all. There are no upscales of these yet — when there are,
`Application.VerbIcon` is the one place that has to prefer them.

**Inspect and its undo are on the list, and neither is in the data.** Both verbs are in
`VERBS.TXT` and no action file names `INSPECT_UNDO` at all: the original adds one or the
other to the bar itself, whichever the thing under the pointer is not already being looked
at closely (`Scene::OnClicked`). Without the undo there is no way back out of a close-up,
and a close-up outlives the room it was of.

**A numbered exit is called after the place it leads to.** RC1's ways out are `EXIT`,
`EXIT1` to `EXIT5`, in no order anybody could infer. The rule behind the door says where it
goes — `SetLocation("rc3")` — and `ESTRINGS.TXT` says what that place is called, so the
label reads "Rennes-le-Château: Outside Church". Same file, same reason, for the corner of
the screen: "Hotel Lobby - Day 1, 10am - 12pm" rather than `LBY - 110A`.

**A label never names somebody the player has not met.** A scene names its people by their
surnames — `BUTHANE`, `BUCHELLI`, `WILKES` — so a label that reads them back introduces
every suspect in the game the first time the player points at one. It is the leak the
second-floor doors had, where `EMILIOS_DOOR` and `BUTHANES_DOOR` named half the cast to
anybody walking down the corridor, in a place where there is no room number to fall back
on. Until the introduction has happened the label says what can actually be seen, "Woman"
or "Man", out of the character's own `ShoeType` in `CHARACTERS.TXT` — which is there to
pick a footstep sound and is also the only thing in the shipped data that says which is
which.

When the introduction has happened is the game's own question and the game's own answer:
the conditions in `Assets/Story/Introductions.txt` are copied out of the `[LOGIC]` sections
of the action files, `MET_BUTHANE` and `MET_WILKES` and the rest, and each line says which
file it came from. Which bounds the list at the twelve the data asks about — anybody it
never asks about keeps their name, as does anybody the character file has no shoes for.
Both failures are the same shape and it is the safe one: a name a little early is a small
spoiler, and a stranger who is still a stranger after two days of conversation is a bug the
player cannot get round.

**The inventory is a strip along the bottom that never goes away.** The original put it
behind a mode change, so checking what you were carrying cost you the sight of the room you
were carrying it in. Clicking a slot takes the thing in hand; clicking it again opens it
close up, which is where its own verbs are — look at it, think about it, read it, scan it
into Sidney. All 619 of those live in `INV_ALL.NVC` behind cases that ask whether the
inventory is what the player is looking at, so the close-up is the only place they can be
reached.

**Using an item on something is one row of the menu.** An action file writes it as a rule
whose verb is the item's name — `BUTHANE, WALLET, MET_BUTHANE` — which is indistinguishable
from an ordinary verb without `VERBS.TXT` to say which is which. Listed flat they read as
the same kind of thing, and late in the game there are thirty items against three real
verbs, so they go behind one **Use...** row that opens a column of its own. Only the things
actually carried appear there: offering every item on every noun is offering the player each
puzzle's solution as a menu entry from the first room.

**Captions are shown for every spoken line**, with the speaker above them, read out of the
`[GK3]` section of the animation that carries the audio.

It is laid out fresh every frame — a function from what the game is doing to a list of
rectangles — so there is no widget tree to keep in step with the world and no way for the
interface to show something that stopped being true. The verb menu's rows are laid out and
hit-tested from the same pass, which is what keeps the thing you click and the thing you
saw from drifting apart.

`GameHud` is the layout, `Overlay` the display list, `OverlayPipeline` the one draw call
that puts it on the screen, and `docs/formats/fonts.md` covers where the letters come from.

Two switches exist for photographing it, since a label that follows the pointer cannot be
captured by a headless run: `--pointer X,Y` puts the pointer somewhere fixed and `--menu`
opens the verb list without a right-click.

## Getting places

Clicking something does two things, in order. The action's **approach** takes the player to
it — `approach=WalkTo, target=TO_B25` is written beside the script and means *this has to be
true before the script runs* — and then the script runs. There are 3,617 approaches across
the corpus:

| Approach | Count | What it means |
| --- | --- | --- |
| `WalkToSee` | 2,120 | Walk until the target is in view |
| `WalkTo` | 687 | Walk to a named spot on the floor |
| `Anim` | 398 | Play an animation instead of walking |
| `TurnToModel` | 394 | Turn to face it without moving |
| `NearModel` | 17 | Be close enough |
| `Region` | 1 | Be inside a region |

The route is found across the walk boundary, so an actor crossing R25 goes round the bed
rather than through it. Seeing is not worked out yet, so `WalkToSee` walks to the target
instead — the same answer for anything in the open and too close for anything behind
something else. `Anim` does nothing until animations play.

A **door** is a script that says `SetLocation` and nothing more. The launcher watches the
story's location rather than the click, so it works however the story asked — clicked, on a
timer, or from a script three calls deep — and the room is rebuilt around a story that
carries on: one host, one state, one audio device, one interface.

## The letters

GK3 ships 137 bitmap fonts. The interface draws with the game's own **caption ladder** —
`F_CAPTION_D_26`, `F_CAPTION_D_20`, `F_CAPTION_D_16`, and the 14-point Goudy behind them —
because those carry the full 181-character set including **52 accented letters**. The
`F_ARIAL` fonts the interface used to take carry 94 characters and not one of them is
accented, which in a game set in France meant `H?tel de Rennes-le-Ch?teau` on screen.

**A bitmap font has one size and there is no scaling it**, so making the text bigger means
picking a different sheet. The rungs cut to 20, 26 and 33 pixel letters; the one nearest
2.8% of the framebuffer's height wins, which puts a 720-line display on the smallest and
anything from 1080 up on the largest. Past about 1,600 lines the ladder runs out and each
sheet pixel is drawn as two — a whole number, because a fraction lands glyph edges between
pixels and the sampler then averages neighbouring letters into each other.

Everything else follows from the letters. `GameHud.Scale` is the chosen line height over
the nineteen-pixel one the layout was written against, and every padding, panel and
inventory slot is written in those units and multiplied by it — so the interface grows
together instead of leaving 1999-sized gaps around 2026-sized text. A window that changes
size enough to want a different rung rebuilds the atlas mid-run.

Text is drawn on whole pixels. A bitmap glyph at a fractional position samples between
texels, and on a stacked sheet what sits half a texel above a letter is the marker strip
belonging to it.

## The console

Backtick opens it. It is across the top rather than the bottom, because the inventory strip
and the captions both live along the bottom edge and a console over either of them would
hide the thing a command was about to change.

**The command language is the game's own scripting language**, because that already is one.
Everything the story can do is a Sheep call; the calls are named in the 224 compiled scripts
the game shipped with, and those scripts carry the prototypes — 219 functions this build
performs, and the signatures for them come out of the archives at load. Inventing a second
vocabulary on top would mean maintaining a translation between two sets of verbs that mean
the same things.

**Which is why the completion is the feature and not a nicety.** Nobody can be expected to
know that the way to see the easter-egg content is `SetFlag("EGG")`, or to remember which
of `SetLocation` and `SetEgoLocation` takes what. Typing narrows a list of at most eight,
each row showing the prototype — `void SetFlag(string)` — so the arguments are visible
before they are typed rather than after they are wrong. Names that *start* with what was
typed come first, then names that merely contain it: the first serves somebody typing a
name they know, the second somebody hunting for one they half remember, and one list in
that order serves both without a mode.

Tab takes the chosen completion and writes the opening bracket with it, because a function
is being called rather than named. Up and down move the choice while there is a list and
recall earlier lines when there is not, which is what every shell does and is why neither
needs a key of its own.

A line is parsed rather than compiled. It is one call with literal arguments — no variables
to resolve, no control flow to run — so a parser of forty lines does the whole job, and the
alternative would be standing up the compiler, a script file and a thread to run one
`SetFlag`. Strings may be quoted or not; a comma inside quotes is part of the string. A
function of no arguments may be typed without brackets.

**Calls go through `Gk3SheepApi` and no further.** A console that reached past it into the
game's own objects could put the story into states no script can reach, and the first thing
anybody would do with it is produce a save that nothing can load. A call that throws is
printed rather than allowed to end the game: a console that closes the game when a command
is wrong is a console nobody uses twice.

One console for the whole run, not one per room. Its history and its scrollback are the
player's working notes, and losing them at every door would make it useless for the one
thing it is best at — watching something across a transition.

While it is open it has the keyboard and the room ignores clicks. Otherwise typing
`SetFlag` walks the camera across the room: W, A, S and D are all in the word and every one
of them is a movement key.

`--console <text>` opens it and types into it, which is the only way to photograph it: a
headless run has no keyboard, and an interface nobody can render is an interface whose
layout nobody can check.

### EGG

`EGG` is a case every action file in the game tests and nothing in the shipped game ever
sets — the original's own resolver answers false for it and says so in a comment. It is in
`NvcFile.BuiltInCases` and `ActionResolver` answers the flag now, so `SetFlag("EGG")` from
the console is what turns that content on. It is the shortest demonstration of why the
console is worth having.

## The screens in front of the room

`ScreenLayers` says which screens are open; `ScreenPainter` draws them. Five kinds — the
inventory, an item close up, the binoculars, the driving map and Sidney — and they share
their chrome, their way out and their scaling, because `Plan/03` section 3 asks that the
player learn the way out once rather than once per screen.

**Drawn, not blitted.** Rectangles and text, like the rest of the interface since it
stopped using GK3's bitmap sheets. `OverlayPipeline` is one texture and one draw, so a
screen made of the original's art would need a second one; and a screen made of text is
legible at any resolution and scales with the font. Sidney gains most from this — it is a
computer terminal, which is exactly what this style draws well.

**Except the things themselves.** An item's own picture is drawn beside its name, in the
inventory, in the close-up of one item, and in the column the **Use...** row opens — the
one place the game's art says something no arrangement of rectangles can, which is what a
thing in your pocket looks like. `INVENTORYSPRITES.TXT` maps each item to the stem of its
art, `CANDY = candy`, and the file wanted is that stem with `9` after it — the size the
original's own inventory screen lists, 94 pixels square with its transparency in a second
file, `CANDY9_OP.BMP`, whose brightness is the alpha. A stem takes the number straight or
with an underscore first and there is no rule for which, so both are tried, and twenty of
the items the table names have no picture at all and are shown by their name alone.
Loaded when an item is first shown and kept, because which dozen of the hundred and thirty
a game reaches is not knowable at startup.

**Nothing is retained.** Same arrangement as `GameHud`: a function from what the game is
doing to a list of rectangles, laid out fresh every frame, with hit testing reading back
the same pass that drew it. There is no widget tree to keep in step with the world.

**The verbs beside an item belong to the item last clicked.** Clicking a thing with one
action performs it, and with several offers them where the thing sits. Both move the list:
a click that performed an action or found nothing to offer used to leave the previous
item's list open over the page, because only the branch that opened one ever said which
item the page was about. The list is also laid down after the page rather than beside its
own item, so it is drawn over the row below it and hit-tested before it — a word means the
word, not the item the word is covering.

**A click is a string.** The painter records `item:PARCHMENT_1`, `sidney:do:Analyse`,
`close`; `Application.OnScreen` decides what each one means. The painter knows where things
are and the caller knows what they do, so no rule about the game lives in the drawing.

**A screen takes the frame.** While one is open nothing behind it is hovered, walked to or
acted on. Without that, a click on Sidney's menu is also a click on the floor behind it.

The inventory opens on **I** as well as when a script asks, because the original made it a
small target at the edge of the screen and there is nothing to be gained by reproducing
that. Sidney opens by clicking Sidney, which is a thing Grace carries.

## What is not here

An actor crosses a room rather than walking across it: position and facing move, and the
walk cycle needs the `.ACT` vertex animation format that nothing reads yet.

The retained UI tree of `Plan/03` section 4. Text is laid out rather than shaped — no
kerning, no bidirectional text — which GK3's own bitmap fonts do not need and a
retranslation would.

Of Sidney's seven screens, six work — see [sidney.md](sidney.md). What is left is the
analyze screen's map geometry: entering points, drawing grids, locking shapes.

## The headset

Gabriel wears a radio headset for one hour of the game — day three, nine in the evening,
the temple — and can ask Grace about what he is looking at. `RADIO` is one of the game's own
287 verbs (`VERBS.TXT` line 138) and the temple's files write twenty-four rules for it, so
this is content that shipped rather than content invented for the port.

**The original made it two things and hid both.** The verb was reachable only by holding the
button, waiting for the ring and recognising an icon — which for `RADIO` is a picture of
Grace's face. And the option bar carried a second, separate headset button
(`rc_radio_std` in `RC_LAYOUT.TXT`) that called one function in the room's script,
`RadioButton$`, with no indication of when it had anything to say.

So the port puts both behind one button, and what it opens is the list of things the room
will actually answer to now:

- **The picture is the original's.** `RC_RADIO_STD` and its hover, down and disabled states
  are the four the game's own option bar used. It is a headset with a boom microphone, drawn
  at a size that is a function of the font, which is a function of the window, so it grows
  with everything else.
- **It hangs under the top bar at the left, half again the bar's height.** It was inside the
  bar first, at the height of a row beside the room's name, and that was wrong twice over: it
  read as another label rather than as something to press, and at a row's height it was the
  smallest thing on the screen. A control the player has to discover cannot be the hardest
  one to see. The label under the pointer is suppressed while the list is up, for the reason
  it is under the verb menu — it names whatever is behind the list rather than the row being
  pointed at.
- **Every row is one of the game's own rules**, resolved through the same `ActionResolver`
  a right-click goes through, with the same conditions. Picking one performs
  `NOUN, RADIO` exactly as picking `RADIO` off the verb menu would. Nothing here changes
  what an answer is; see `Plan/03` §2.3.
- **The room's own general call is the first row**, where the room declares
  `RadioButton$`. It is not a duplicate of the noun list and cannot be dropped: TE4's rules
  for radioing Grace about the Solomon statue are commented out in `TE4309P.NVC` precisely
  because the button covered them, so without this row that conversation and the point it
  scores are unreachable.

**Several nouns are often one conversation, and the data says which name to keep.** The
porch's tiles are four nouns — `TILES`, `CROSS_TILES`, `SKULL_TILES`, `SWORD_TILES` — with
one radio conversation between them; TE3's scales are seven with one. Listing them all
would offer four rows that say the same thing. Two rules with the same script are folded
into one topic, and the survivor is the noun that rule's own script names in its
`IncNounVerbCount` — `TILES` for the porch, `SCALE_ON_TABLE` for the scales. File order is
right for neither: the tiles put the plain noun last and the scales put it first.

**The button is shown for the whole hour and dimmed when there is nothing to say**, rather
than appearing only in rooms with something in them — which would be the interface telling
the player where the puzzles are. It is the reference engine's own gate
(`Timeblock(3, 21)`), and `rc_radio_dis` is the art for it.

It also dims while Gabriel is performing something or a line is playing, because asking
Grace something in the middle of her answering the last thing would stack two voice-overs.
**Two other signals were tried for that and both were wrong all the time.** `SceneUpdate.Occupied`
is four conditions, one of them "the story has scripts outstanding", which in the temple is
true from the moment the room opens and never clears — TE3 offered nothing at all. And
`SceneAudio.Speaker` is set by an animation's caption and is not cleared when that animation
ends, so TE1 read as Mosely still screaming for the rest of the room. `SceneAudio.Talking` is
the one that is about a line playing now.

A topic's label goes through `SceneInteraction.NameOf`, the same naming the hover label and
the hotspot overlay use. That is the third thing in this interface that puts a noun in front
of the player, and twice before a new one has been written that did not go through it — both
times it introduced somebody the player had not met.

## The binoculars

Two places have them — the Armchair of the Devil and the tower at Blanchefort — and
`BINOCS.TXT` describes twenty-one vantage points between them, forty-seven things worth
looking at, and four spots that have a line of dialogue rather than a destination.

**The panorama is the room, not a picture.** The binoculars do not show a painted backdrop;
they narrow the view and let the player pan the camera they already have. Each thing worth
seeing is a rectangle in *degrees* — heading across, pitch up and down — and the file's own
numbers say so: they run 1 to 189 across and −7 to 11 up, which is an arc of hillside and a
few degrees either side of the horizon rather than any kind of image coordinate.

So this is the one screen that is not a panel over the room. It draws a mask — two circles
cut out of the dark in four-pixel bands, because the overlay draws rectangles and a
row-per-pixel mask on a 4K display is nine thousand of them against a budget of four — plus
crosshairs and a readout, and the camera keeps taking the player's input underneath it.

**Leaning in is a camera and usually a room.** Each sight names where the camera stands and
looks once the player zooms, and that is generally inside a different room, which is why it
also names that room's floor. Where the room changes, the camera travels with the request
in `Gk3SheepApi.WantedCamera` — the room has not been built yet when the choice is made.

**The file's case is inconsistent** between a heading and the sections under it: `CD1102P`
names `CD1102pPL3`. A case-sensitive lookup loses four of the forty-seven sights, and one
of them is the only way to look at Blanchefort. There is a test for it.

## The driving map

The game's own painting of the Rennes-le-Château valley — `DM_BASE.BMP`, 640 by 480 — with
sixteen places on it. Each place's marker is a **lit copy of that patch of the map** rather
than a pin over it, which is why the markers look like part of the picture: `DM_RLC.BMP` is
the village, painted brighter.

**Where the positions came from.** The retail engine builds the list in the constructor of
its driving layer, sixteen calls with the coordinates as immediates, each naming a marker
and the room arriving there loads. They are recovered from there and written down in
`DrivingMap` — nothing this engine ships may depend on the original executable, and sixteen
pairs of integers about where a village sits on a painting are a fact about the map rather
than something that can be derived. The pictures themselves come from the player's own
`.BRN` archives, and from `enhanced/textures` where an upscale exists: 14 of the 17 do.

**The size used for layout is always the archive's**, whatever is drawn. The map is laid
out in the 640-by-480 pixels the original was built in and every marker's position is a
coordinate in that space; an upscaled marker is the same marker at more samples, not a
bigger one. Recording the enhanced size would put the markers in the wrong places by a
factor of thirty-two.

**What is open.** Five places from the first ride — Rennes-le-Château, Larry Chester's
house, Blanchefort, Rennes-les-Bains and the Couiza train station — plus anywhere the
player has been, plus anywhere a script has named with `EngineOpenOnMap`. All three are read
out of the game's own state, so the map after a load is the map before the save.

**The places are named.** A marker is a lit copy of the patch of painting under it, which
tells the player that something is there and nothing whatever about what — the original
left them to hover each of the sixteen in turn. The open places are listed down the side of
the map, every row rides there, the one under the pointer is ringed and named on the
painting itself, and pointing at either the row or the marker lights up both. The names are
the game's own, from the `dm_*` entries in `ESTRINGS.TXT`. The column is dropped on a panel
too narrow to hold it without taking the map down to a thumbnail, and the name on hover
still works there.

**The map is a location, not a panel.** The retail engine's location table lists `map`
beside `lhe` and `mop`, and its driving layer holds that entry's index as its own location:
riding the moped is leaving a room for the map and arriving somewhere else from it. That is
what the game's data expects — `LHE.SIF` puts Gabriel's moped in the yard on
`WasLastLocation("Map")`, every place the moped reaches names an `FR_MAP` spot to arrive at,
and ten compiled scene scripts branch on the same question. `GameState.RideTo` makes the
ride two moves rather than one so that all of it holds, while the map stays a screen here
rather than becoming a room with nothing in it.

**A ride parks the moped, which the original never did.** Six scene files draw the moped
from `BikeLocation` and three action files let the player leave on it only when that number
is where they are standing, but nothing in the retail engine ever writes it — only
`LHE.SHP` and `MOP_ALL.SHP` do, from their own arrival scripts, so riding to Blanchefort,
Coume Sourde or L'Homme Mort left no moped and no way off. `DrivingMap.ParkedAt` gives the
number and it is simply the place's index in the list above: the six the data names are 3,
4, 9, 10, 11 and 12, which are their positions in it. One variable means one moped, so
riding on empties the place it was — and `CDB.SIF`, the driveway overlooking Larry's yard,
draws it from the yard's own number.

**The pictures outlive a change of font.** They hang off the overlay pipeline's descriptor
pool, and changing the sheet of letters used to rebuild that pipeline — so opening the menu
once, which happens before the player reaches a room, threw the map's art away and left it
drawing the fallback list for the rest of the session. `OverlayPipeline.SetAtlas` swaps the
sheet in place now and keeps everything else.

`PATHDATA.TXT` is in the archives and describes twenty road junctions with their map
positions and the roads between them. It is parsed and held; riding the moped along it
rather than cutting straight there is the next step.

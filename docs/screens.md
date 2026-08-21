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

**The inventory is a strip along the bottom that never goes away.** The original put it
behind a mode change, so checking what you were carrying cost you the sight of the room you
were carrying it in.

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

## What is not here

An actor crosses a room rather than walking across it: position and facing move, and the
walk cycle needs the `.ACT` vertex animation format that nothing reads yet.

The retained UI tree of `Plan/03` section 4, and the screens themselves. `ScreenLayers`
still only says which screens are open; the binoculars, Sidney, the driving map and the
inventory *screen* draw nothing of their own. Text is laid out rather than shaped — no
kerning, no bidirectional text — which GK3's own bitmap fonts do not need and a
retranslation would.

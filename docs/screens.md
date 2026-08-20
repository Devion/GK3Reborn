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

## What is not here

An actor crosses a room rather than walking across it: position and facing move, and the
walk cycle needs the `.ACT` vertex animation format that nothing reads yet.

The retained UI tree of `Plan/03` section 4, and the screens themselves. `ScreenLayers`
still only says which screens are open; the binoculars, Sidney, the driving map and the
inventory *screen* draw nothing of their own. Text is laid out rather than shaped — no
kerning, no bidirectional text — which GK3's own bitmap fonts do not need and a
retranslation would.

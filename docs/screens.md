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

## What is not here

Nothing draws. A screen being open is a fact about the game, not about a window: the
retained UI tree, text shaping, layout and input bindings of `Plan/03` section 4 sit on
top of this and do not exist yet. What exists is the contract they have to honour, which
is the part that decides whether the interface is learnable.

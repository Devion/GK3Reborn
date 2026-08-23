# Saved games

How a game is written down, where it goes, and what a load is allowed to do.

## What a save holds

Not a design decision so much as a reading of `GameState.ComputeHash`. That method already
enumerates everything observable about a run, in a fixed order, because a state hash that
missed something would be useless for the comparison it exists for. A save that stored less
than the hash covers is a save that can be loaded into a different game than the one that
was saved, so the two lists are kept deliberately identical:

flags, variables, noun/verb counts, topic counts, which individual lines of a topic have
been said, chat counts, location counts, where every actor is, the Sidney files that have
been scanned, blocked hit tests, every actor's inventory and what is in their hand, pending
timers, the timeblock, both rooms, the camera, who the player is, and the score.

The test for this compares the state hash across a round trip rather than comparing fields.
A test that listed the fields would be the same list the save already is, and would pass
while missing whatever the save missed.

**The random generator's own state goes too** — its four words, not the draw count. Without
it a reload is a way to re-roll anything the story left to chance, which is not what a save
is for. The count alone cannot restore it, because the generator is a state machine rather
than a position in a stream.

## What a save does not hold

Presentation. Which screen was open, where the camera was gliding, what was half-said: none
of it is state the story reads, and restoring it would mean restoring a moment rather than a
position. A loaded game puts the player in the room, at the scene's own camera, with nothing
in front of it.

## Loading clears first

`GameState.Restore` empties every collection before it fills any of them. This is the classic
save bug and it is worth naming: a flag nobody set in *this* run survives the load, the story
reads it and takes a branch the player never earned, and it surfaces hours later somewhere
else entirely. Setting is not enough — a save records only what is set, so unsetting has to
happen too, and the only way to unset everything is to clear.

## Where they live

`%AppData%\GK3Reborn\saves` on Windows, `~/.config/GK3Reborn/saves` on Linux — the profile
directory the settings already use, and for the same reasons. A game directory may be
read-only, shared between accounts, or replaced wholesale by an update, and none of those
should cost somebody their progress.

One file per slot, and the slot name is the file name: `autosave`, `quicksave`, `slot-01`.
A name is checked before it becomes a path, because slot names arrive from a console command
and a save called `..\..\settings` must not be a way to write one.

**Every write is atomic.** Written to a temporary file, flushed, moved into place. A process
that dies halfway through leaves the previous save untouched rather than a half-written one.
This is the single most important property here — the plan's words are that "failures cannot
corrupt the last good save" — and it is why `AtomicFile` exists.

## Versions

A save carries the schema it was written with. One from a later build is refused **by name**
rather than half-read: reading it would silently drop whatever fields this build does not
know, and dropping a field of a save is losing a game. One from an earlier build goes
through `SaveStore.Migrate`, which does nothing yet and is there anyway — the alternative is
discovering at the first schema change that every save in the wild is unreadable, and that
fix is much harder to add then than now.

## How the player reaches it

| gesture | slot |
|---|---|
| F5 | `quicksave` |
| F9 | loads `quicksave` |
| arriving in a room | `autosave`, never on the first room of a run |
| `EngineSaveGame("slot-01", "title")` | any slot, from the console |
| `EngineLoadGame("slot-01")` | any slot, from the console |

The autosave is written **on arriving** rather than on leaving. Arrival is the moment the
story is at rest — the room is built, its opening script has run, nothing is half-done — so
it is a place the player can be put back rather than a doorway they were passing through.

A load names the room the save was taken in, and it may be the room the player is already
standing in. The loop watches for that case explicitly: the ordinary "the story moved us"
test would not fire, and the room would keep the props and people of the game just thrown
away.

The save functions carry an `Engine` prefix, like `EngineHasSaidTopicLine` does. They are not
the game's own API — the original saves through its shell and no script asks it to — and the
prefix is what stops anybody mistaking them for functions a script may call.

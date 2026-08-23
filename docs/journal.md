# The journal

GK3 shipped without one. This is the port's own, and it exists because a 1999
adventure game will happily let a player wander for an hour with no idea what it
wants of them. `Plan/03` section 3 asks for an interface easier than the
original's, and knowing where you are in a story is most of what that means.

Press **J**. It opens wherever the inventory does.

## Two levels, and the player chooses

**The objectives say what.** "Telephone Prince James", "Look in at the bookshop".
142 of them across the game's 17 points in the story, in
`Assets/Story/Quests.txt`. Nothing there says *how* — several of this game's
puzzles are the best things in it, and printing the answer where nobody asked
takes them away.

**The hints say how**, one line at a time and only when asked. They come from
`Assets/Story/Walkthrough.txt`, which is a spoiler from end to end. A player a
little stuck usually needs the first line, which says where to go; the one that
gives a puzzle away is further down, and reaching it takes asking again.

## It holds almost no state

What is done is read from the **score events** the story already records, so the
journal cannot drift out of step with the game. An objective names the events
that measure it:

    Telephone Prince James | score:e_110a_pho_phone_prince_james | 5
    Search your room       | score:e_110a_r25_tape,e_110a_r25_hanger | 1

`score:` means every one of them, `any:` means one will do, and `story` means the
game awards nothing for it and it counts as done once the story has moved past.
The last field is which walkthrough lines, counted within that point in the
story, answer "how".

The one thing the journal owns is **which hints have been asked for**, which is a
player's own business and is saved with the game.

### This is what forced a save-format change

A save has always carried the player's total and never which events made it up,
so loading one and doing the same thing again scored it twice. Nothing showed it
until the journal read those events to know what had been done. Schema 2 records
them.

**Old saves are read.** Everything belonging to a point in the story the player is
past is marked earned — the story cannot advance out of a timeblock until its own
rules are satisfied, so a save sitting in Day 2 has been through Day 1, and
marking those is also what stops the player being paid for them twice. The block
they are standing in is not recoverable and nothing is invented about it: those
objectives show unfinished until done again.

Saves now live in a `saves` folder beside the game rather than in the profile
beside the settings. A save is something a player copies, backs up and sends to
somebody else; a preferences file is not.

## The other half: checking the story can be finished

    GK3Reborn.Tools check-story --source <GK3>/Data

`check-scenes` asks whether every room loads and every function exists. Both can
be true of a game that cannot be completed. This asks the other question, and the
walkthrough is what makes it possible to ask: it is a record of a game somebody
finished, so every score event the objectives are measured by has to be one the
shipped data can actually award.

It reads the score names out of the compiled scripts' string tables **and** out of
the action files' Sheep source. Reading only the scripts reported every
fingerprint in the game as unreachable, which was an alarming and entirely wrong
answer — 20 of the journal's events are awarded only from `.NVC`.

What it found, and what is real:

| finding | verdict |
| --- | --- |
| walkthrough parses, 341 steps, totals add up | the file's own running totals check the parse |
| 142 objectives across 17 points in the story | every point covered |
| every score name exists | enforced by `JournalTests` |
| 13 fingerprint scores awarded by nothing | **real gap** — see below |

### The fingerprint kit

Every `*_fingerprint_kit_*` score is awarded by the original's own fingerprint
screen rather than by data — hardcoded in its code the way the score table and the
starting inventory are. No script names them and nothing about the shipped data is
wrong. What is missing is on this side: until that screen exists, thirteen
objectives across five points in the story cannot complete. Reported as
`GK3R3404`, and tracked in `known-issues.md`.

## What the tests guarantee

`JournalTests` reads the shipped tables rather than a fixture, because an
objective naming a score event that does not exist can never be completed — the
journal would tell a player to do something and then never admit they had done it,
which is worse than having no journal. So a typo fails a build:

- every score name an objective uses exists in `Scores.txt`
- every objective is filed under the day its events belong to (two documented
  exceptions)
- every hint points at a walkthrough line that exists
- no objective is impossible — no `score:` condition with nothing in it
- no objective title contains a word that gives a puzzle away

That last one has caught two.

### And it found a defect in the score table reader

Four lines of `Scores.txt` carry two events separated by a comma. The reader took
each line as a single pair, so the value came out as
`4, e_212p_cse_open_cellar_doors = 2`, which is not a number, and the whole line
was skipped. Four score events did not exist, and nothing said so — a missing
event scores nothing and is indistinguishable from one the player has not earned.
The journal found it by naming one of them.

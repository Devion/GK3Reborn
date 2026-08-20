# Action files (`.NVC`) and the interaction model

390 files, 6,043 rules, 964 nouns, 221 verbs — everything the player can do.

## Format

Each line is:

```text
NOUN, VERB, CASE, [approach=…,] [target=…,] script={…}
```

followed by a `[LOGIC]` section naming the cases as Sheep expressions:

```text
STAIRS_LEFT, GO_UP, GABE_ALL, approach=WalkTo, target=TO_HAL_L, script={wait CallSheep("lby102p","STAIRS_LEFT"); SetLocation("HAL");}
MOSELY,      LOOK,  GABE_ALL, script={wait StartVoiceOver("1E91244Q81",1);}

[LOGIC]
RETURNED_COAT={ DoesEgoHaveInvItem("MOPED_KEYS") || GetGameVariableInt("MoselyOnCandyPath102p") }
```

The script field contains commas and braces, so it has to be lifted out before the rest
of the line is split. `approach` says how the actor gets into position — `WalkTo`,
`WalkToSee`, `ANIM` — and `target` says where.

Cases the engine answers itself, rather than reading them from a `[LOGIC]` section:

| case | holds when |
| --- | --- |
| `ALL`, `DEFAULT` | always |
| `GABE_ALL`, `GRACE_ALL`, `NOT_GABE_ALL`, `NOT_GRACE_ALL` | the player is (or is not) that character — GK3 switches between Gabriel and Grace |
| `TIME_BLOCK`, `TIME_BLOCK_OVERRIDE` | always; they mark an action a timeblock's file writes over one the location's general file gives, the second outranking the first |
| `1ST_TIME`, `2CD_TIME`/`2ND_TIME`, `3RD_TIME`, `OTR_TIME` | the player has done this to this 0, 1, 2, or more than 0 times |
| `DIALOGUE_TOPICS_LEFT`, `NOT_DIALOGUE_TOPICS_LEFT` | there is (or is not) a `T_` verb for the noun still to be raised |
| `EGG` | never — easter eggs are off, as in the original |

Missing one of these is expensive and silent, because an unrecognised case is treated
as unavailable and the action simply leaves the game. `TIME_BLOCK_OVERRIDE` is used by
90 of the corpus's action files and written into the logic section of exactly one, so
before it was recognised, `check-scenes` counted **918** actions naming a case nothing
defined; afterwards, 78, and 448 more verbs available across the corpus.

Those 78 are the game's own typos. `CHU_ALL.NVC` asks for `G_DONE_PISCES_NOT_ARIES`
and defines `GOT_LSR_DONE_PISCES_NOT_ARIES`; the action never fires in the original
either.

The case is the text between the braces, and the braces are the field rather than the
expression — three cases in the corpus end in a semicolon inside them, such as
`LBY110A02P.NVC`'s `{!DoesEgoHaveInvItem("Candy");}`. The original tolerates it because
it compiles a case as a snippet of Sheep rather than reading it as an expression, so
the terminator comes off before the expression reader sees it.

## Verbs are not all verbs

Of the 221 distinct verbs, a large share are **inventory items**: `ABBE_TAPE`,
`BINOCULARS`, `BLACK_MARKER`, `CHURCH_PAMPHLET`, `DAGGER`, `COORDINATE_FIXING_DEVICE`.
Using an item on something is expressed as a verb whose name is the item.

That shapes the modern interaction model. The real verbs — `LOOK`, `TALK`, `PICKUP`,
`OPEN`, `PUSH`, `GO_UP` — belong in the action chooser; item verbs should surface only
when the player holds the item, or the chooser for a busy noun would list dozens of
things the player cannot do.

## Conditions use `n$` and `v$`

Some conditions reference bare `n$` and `v$`, which bind to the noun and verb currently
being evaluated:

```text
{(GetFlag("AnsweringWho")) && (GetTopicCountInt(n$, v$) == 0)}
```

That is what lets one condition serve many rules. Without binding them, 59 of the 1,286
conditions fail to evaluate; with them, **1,284 of 1,286 evaluate** and the remaining two
are individually reported.

## The expression reader

Conditions are source text, not bytecode, so evaluating them needs a reader for the
language rather than the VM. `SheepExpression` is a hand-written recursive-descent parser
over the expression production of the grammar in `SHEEP ENGINE.DOC` — the same approach
`Plan/01-architecture.md` section 6 chose for the full compiler, built here first so the
harder job starts from something already proven against real content.

Precedence follows C, which the language reference says it was modelled on. Two details
bite: `<` must not swallow the `<` of `<=`, and `<>` is the language's second spelling of
"not equal".

Both sides of `&&` and `||` are evaluated rather than short-circuited. These conditions
call into game state, so which calls happen would otherwise depend on data — and a
differential comparison would see that as a divergence.

## Which files are in scope

A scene's verbs do not all come from the scene. Four sets are in play at once, and
`ActionSets.For` puts them in the order they should be consulted:

| where from | how chosen |
| --- | --- |
| the timeblock file's `[ACTIONS]` | taken as listed, never name-checked |
| the general file's `[ACTIONS]` | name-checked against the current timeblock |
| twelve global sets, `GLB*.NVC` | fixed list, name-checked |
| sixteen inventory sets, `INV*.NVC` | fixed list, name-checked |

Without the last two most objects have no `LOOK`, because looking at things is a rule
about the game rather than about the room. R25 on the second afternoon reads eleven
files: `r25202p.nvc`, then `r25_all`, `r25_23all`, `r25_2all`, `r25_12all`, then
`GLB_ALL`, `GLB_23ALL`, `GLB202P`, `INV_ALL`, `INV_23ALL`, `INV202P`.

Two things a sweep of the corpus finds in the listings themselves. 21 pairs list an
action file no archive contains — `arm_202p.nvc` among them (`SCENE014`). And
`MA2207A.SIF` lists `ma2207a.sif` in its own `[ACTIONS]` section, meaning the `.nvc`
beside it; anything not named `.nvc` is skipped rather than read as one, because a
scene file read as an action file would put invented nouns and verbs into scope
(`SCENE018`).

**The name is the condition.** `TimeblockRange` reads it, following G-Engine's
`Timeblock::ParseTimeblockRange`: three letters of location, an optional underscore,
then either `ALL` preceded by the digits of the days it covers, or a timeblock code
optionally followed by a second one giving the end of a range.

| name | applies |
| --- | --- |
| `R25_ALL.NVC` | every timeblock in the game |
| `R25_1ALL.NVC` | all of day one |
| `R25_23ALL.NVC` | days two and three |
| `R25202P.NVC` | that afternoon alone |
| `HAL110A04P.NVC` | day one, ten in the morning until four — the end borrows the start's day |

A name that cannot be read this way covers nothing and is never loaded, which is the
original's behaviour and the safe direction: loading an action file at the wrong point
in the story puts verbs on objects that should not have them yet. Of the 362 distinct
names the corpus's scene files list, exactly one cannot be read — CHU's
`ch312p06p.nvc` — and it is listed by a timeblock file, where the question is never
asked. The general-file path warns (`SCENE015`) if it ever meets one.

Order is priority, because the resolver keeps the first rule it finds for a verb, so
the most specific file goes first. That is the opposite of the order the original
inserts them in; it can afford general-first because it keeps every rule and separates
them by case at the point of use, where this keeps one entry per verb so a menu can be
built from the answer.

`[AMBIENT]` sits beside `[ACTIONS]` in the same files and names `.STK` soundtracks —
scripts saying which sounds to play and how often, not music to loop. R25 plays
`R25SNDTRKL.STK`, except on the third morning when Grace is the player and it plays
`R25Grace.STK`.

## Nouns have two halves

The scene file hangs a noun on a piece of geometry; the action files say what that noun
can have done to it. Neither is wrong on its own, so a noun in one and not the other is
only visible by putting them side by side, which `render-scene` does on every load:

```text
nouns: 33 on the scene's objects, 33 of them known to the action files
```

R25 at 202P covers all of its own. The gaps elsewhere are real rather than defects in
the loading: at LBY 102P, `ESTELLE` and `LADY_HOWARD` have no verbs because at that
point the pair is one noun, `LADY_H_ESTELLE`; PLO's mopeds are interactive only at
104P; RC1 declares `SUITCASES`, whose verbs live in R25's files.

## Resolving

`ActionResolver` answers "what can the player do to this, right now" by taking every rule
for a noun and evaluating its case. That is the same query the original engine ran to
decide which verbs to put on its verb wheel, answered for a different interface.

Two properties it must keep:

**It never mutates state.** A resolver that evaluated a condition by trying the action
would corrupt a save just by hovering the cursor. There is a test asserting the state hash
is unchanged after resolving.

**It only selects.** The script it returns is the original, unchanged, and execution still
goes through Sheep — `Plan/03` section 2.3 requires that modernising input must not change
what an action does.

Inspection sorts first so left click always has something predictable to do. Nothing else
is marked as the primary action: choosing one is a design decision the resolver should not
make alone, since `Plan/03` section 2.1 requires that no puzzle action fires because the
engine guessed.

Asked for `MOSELY` in the lobby, it currently answers: LOOK (inspect), TALK, Z_CHAT,
ABBE_TAPE, PICKUP, CANDY — nine actions, drawn from the layered files in scope, with the
first matching rule for a verb winning so a timeblock file overrides a shared one.

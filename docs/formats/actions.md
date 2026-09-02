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
of the line is split. `approach` says how the actor gets into position and `target` says
where. There are seven across the corpus:

| approach | uses | target | what it means |
| --- | ---: | --- | --- |
| `WalkToSee` | 2,120 | a model | walk until it can be seen. Sight is not worked out, so it walks to the model |
| `WalkTo` | 688 | a named spot | walk there exactly, and face the heading the spot carries |
| `ANIM` | 398 | **an animation** | walk to where that animation begins, then play it |
| `TurnToModel` | 397 | a model | turn on the spot to face it |
| `none` | 105 | — | do it from where you are |
| `NearModel` | 17 | a model | the nearest walkable spot to it |
| `Region` | 1 | a region | never implemented in the original either |

`ANIM` is the odd one and the only one whose target is not a place. A GK3 character has no
skeleton, so the animation cannot be asked where it puts anybody; what it has instead is
three axis triads carried along with the body — one at the hips and one under each shoe —
and `CHARACTERS.TXT` says which mesh, group and vertex each one is. The hip triad on the
opening frame is where the actor stands. Which way they face comes from the triangle the
three triads make: its normal, flattened onto the floor, is the direction the body is
pointing, and comparing that against the hip mesh's own Y axis says whether this clip was
authored with the model facing −Z (nearly all of them, so the heading is the mesh's
rotation plus a half turn) or +Z (a few, where it is not).

Without it the action runs from wherever the player happens to be standing: Gabriel pours
the coffee in the hotel dining room with the pot across the room, which is what the report
that prompted this said.

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

`Find(noun, verb)` is the same selection for one verb, returning the rule rather than
something to put in a menu, because what a click needs is the script.

## Performing

`ActionRunner` carries out what the resolver chose. The two are deliberately separate:
choosing happens whenever the cursor moves and must not touch the story, while
performing is what a click is for.

An action's script is a much smaller language than Sheep. Across the corpus's 5,872
scripts there are **6,842 statements, every one of them a function call**, 5,824 of
those prefixed with `wait`, drawn from 55 distinct functions of which `StartVoiceOver`
alone is 4,314. There are no branches, no loops and no locals — the files put that in
the case conditions and in the `.SHP` scripts the actions call into. So the runner reads
statements rather than compiling a language, and refuses anything else out loud instead
of guessing at it. A script with a statement it cannot read is refused **whole**, the
way a compiler refuses a file: half an action is worse than none, because the half that
ran has already changed the story.

`wait` means what it says. A waited call reports how long it takes and the statement
takes that long, so an action takes the time it was written to take instead of happening
all at once:

```
do GABRIEL:LOOK [ALL] from GLB_ALL.NVC:30, 224 scripts loaded
  ran 1 statement(s): wait StartVoiceOver
  takes 3.3s of the player's time
```

Almost all of that is dialogue, and how long a line of dialogue lasts is a frame count in
a `.YAK` — see `docs/formats/animations.md`, particularly the part about finding which
`.YAK`, which is where this is easy to get silently wrong. Across the corpus **21,064 of
22,556 waited statements have a length (93.4%), 1,201 minutes of the player's time in
all**. A call whose length lives somewhere still unread — a soundtrack, a walk, a
conversation — reports zero and is over as soon as it starts, which is what every waited
call did before any of this could be measured.

Working out a duration needs the call's arguments, and working those out means evaluating
the expression. Reading a script is supposed to change nothing, so a read evaluates
against a host that does nothing and catches the call on its way in.

**One waited call has no duration at all, and it is the second commonest statement in the
corpus.** `wait CallSheep("cs6_all", "Old_Grace$")` is over when that function is over, and
how long that is depends on the animations, dialogue, walks and timers inside it — a
number no host can answer before the fact. So the runner does not try to: the statement is
made inside a scope that notices which script threads it started, and if any of them is
still parked when it returns, the rest of the action is held until none of them is. That is
`Gk3SheepApi.DefersUntil`, wired to `SceneUpdate.Until`, and it is the companion to
`Defers`/`SceneUpdate.After`, which is the same idea for the approach walk in front of an
action.

There are **14,853 `CallSheep` statements** across the reachable verbs; 303 action scripts
have a statement after a waited one and **58 of those change location**. Treating the call
as instantaneous therefore tore the room down in the frame the cutscene began. CS6's old
lady is the reported case:

```
OLD_LADY, TALK, ALL, approach=WalkTo, target=TO_STAIR, script={
    wait CallSheep("cs6_all", "Old_Grace$");
    incnounverbcount("old_lady","talk");
    setlocation("cse");}
```

`Old_Grace$` is forty seconds of forced camera cuts, four called functions and nine lines
of dialogue. All of it ran, and the courtyard replaced the room it was running in on the
same frame, so none of it was ever seen.

An **unwaited** `CallSheep` holds nothing up, which is what it is for: the script left that
one running behind itself deliberately. And a call into a script that never blocks — the
ordinary case — leaves nothing outstanding, so the statement after it is still the same
frame's. A host with no scheduler at all, which is every tool, runs the callee inline to
completion and carries straight on exactly as it always did.

Two things happen after a script finishes, whatever it said. A **topic** verb — one
named `T_…` — increments its own topic count, and `Z_CHAT` increments the noun's chat
count. Ordinary verbs do **not**: an ordinary action increments its count only if its
script says `IncNounVerbCount`, which 260 of the corpus's scripts do. Counting them all
would make every `1ST_TIME` rule fire once and never again.

Across the corpus, **all 24,126 reachable verbs have a script the runner can perform** —
26,552 statements calling 41 distinct functions. 24 of those functions are performed;
the other 17 are the presentation surface — the driving interface, the binoculars,
inventory screens, conversation, camera modes — recorded rather than performed, because
none of those subsystems exists yet.

```bash
GK3Reborn.Tools render-scene --model R25 --timeblock 202P --do WINDOW:OPEN ...
```

resolves the rule, loads the compiled scripts, runs it, and prints the statements, the
calls that were recorded and whether the story moved. Opening R25's window enters
`R25_ALL:WINDOW_OPEN`, which shows the roof models, hides the backdrop, cuts to the
`Look_out_window` camera and starts three animations.

**A compiled script names its functions with a `$` on the end.** R25_ALL's disassembly
reads `Window_Open$`, and the callers routinely leave it off — `CallSheep("R25_ALL",
"WINDOW_OPEN")` is how the action files spell it, and the original appends the suffix
when it is missing with the comment "some GK3 data files do this, some don't". Matching
exactly instead means the call finds nothing, the thread faults and the action appears
to run and do nothing at all, which is what opening that window did until the VM began
comparing names with the suffix trimmed off both.

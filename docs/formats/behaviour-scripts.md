# Behaviour scripts (`.GAS`)

What a thing does when nobody is asking it to. A scene's model line names one —

    model=fanblades, type=gasprop, gas=lbyfan.gas

— and an actor's line names **three**:

    model=mad, noun=BUTHANE, idle=madrc1mapidle.gas, talk=madreltalk.gas, listen=madreltalk.gas

The game runs them for as long as the scene is loaded. There are 502 in the archives; 77
are named by a `gasprop` line and drive scenery, and the rest belong to characters —
breathing, shifting weight, gesturing while they speak, looking up when somebody walks
past.

`LBYFAN.GAS`, in full, is why the lobby's ceiling fans turn:

    ANIM lbyfan_spin
    loop

## The format

A line an instruction: a keyword and its arguments, `//` to the end of the line being a
comment. Counted over the corpus, the keywords are:

| | uses | |
| --- | --- | --- |
| `ONEOF` | 1,559 | pick one of the following at random |
| `ANIM` | 987 | play an animation and wait for it |
| `LABEL` | 320 | somewhere to jump to |
| `GOTO` | 284 | jump there |
| `LOOP` | 272 | start again from the top |
| `USE` / `USES` | 303 | which model the following applies to |
| `WAIT` | 206 | do nothing, for a while |
| `SET` / `IF` / `INC` | 427 | the script's own variables, and branching on them |
| `LOOPANIM` | 42 | play an animation, repeating |
| `WALKTO` / `CHOOSEWALK` | 59 | send an actor somewhere |
| `USETALK` / `NEWIDLE` | 56 | swap the script an actor is running |
| `WHENNEAR` / `WHENNOLONGERNEAR` | 25 | fire when the ego arrives or leaves |
| `LOOKAT` / `DLG` / `SETMOOD` / `LOCATION` / `RESETIPOS` | 17 | the rest |

**Arguments are separated by commas or by spaces**, and the content uses both — sometimes
in one line, as in `ANIM AbeHe1FightFidget, FALSE 50`. Splitting on either makes every
spelling one shape, which is also what turns `IF A = 1 LABEL` and `IF A, = , 1, LABEL`
into the same instruction. Brackets are decoration: only `CHOOSEWALK` writes any.

Three arguments are easy to miss. `ANIM` takes an optional `TRUE`/`FALSE` saying whether
the animation plays where the model already is, and an optional **percentage chance of
playing at all** — which is what keeps a fidget repeated nine times in a row from reading
as a loop. `ONEOF` takes a weight. And `USE` is not an instruction but a declaration whose
meaning depends on the word after it, `CLEANUP` in 328 of its 341 uses:

    USE CLEANUP abebinocbreath, abebinocdown

If the Abbé is interrupted while breathing through his binoculars, he lowers them, rather
than snapping to standing with them still raised.

## What is run

**All of it, bar the perception layer.** All 502 scripts parse completely; `check-scenes`
says so and is the regression baseline.

The instruction that matters most is the smallest. **`ONEOF` is 1,559 of the corpus's
4,655 instructions, and a *run* of them is one choice, not several.** Reading them as
separate instructions plays every fidget a character has, in order, for ever — which is the
difference between an idle that reads as a person and one that reads as a loop.

    LABEL START
    ONEOF GabRTalk2Subtle, 100
    ONEOF GabRTalk2Subtle2, 100
    ONEOF GabRTalk2Talk1, 50
    ONEOF GabRTalk2Talk3, 50
    GOTO START

The weights need not add to anything; that one means a third each for the first two and a
sixth each for the others.

An animation's own length is what the script then waits for, which is what makes a fan turn
continuously rather than restarting every frame. `SceneUpdate.StartScenery` begins them
once the scene is standing and its animation libraries are attached; `Advance` steps them,
at most sixteen instructions a frame — the corpus has several scripts that loop without
ever waiting, and without a bound one of them takes the frame with it.

**The choices are drawn from the update's own generator, not the story's.**
`GameState.NextRandom` counts its draws into the state hash on purpose, and an idle picks a
fidget every couple of seconds for as long as anybody stands in the room; drawing those
from the story would make the hash depend on how long the player loitered.

## Which of the three a character runs

Decided every frame by **who is speaking**. The line being spoken names its own actor — see
`faces.md` — so the speaker runs their `talk` script, everybody else runs `listen`, and
with nobody speaking everybody idles. A character with no script for a mode falls back to
their idle, which most of the cast do, because standing perfectly still while speaking is
worse than gesturing the way they do when they wait.

Sheep overrides it. `SetIdleGAS` and its two relatives replace a script and start it from
the top; `StartTalkFidget` and its relatives pin a mode whatever is happening; `StopFidget`
stands somebody still, which is what a script does before handing them something specific
to do. All seven used to be recorded, which was a cast standing motionless through every
conversation in the game.

## What is not

**The perception layer**: `WHENNEAR`, `WHENNOLONGERNEAR` and `WHENINVIEW`. They are
standing conditions rather than instructions — from here on, jump to that label whenever
this becomes true — so they need the player to test them between steps rather than to
execute them. They are parsed and skipped, which loses a cue and nothing else: 25 uses
across the corpus, the chicken in RC1 being the liveliest.

**`DLG`, `SETMOOD`, `LOCATION` and `RESETIPOS`**, for the same reason and with less at
stake: 17 uses between them.

**Cleanups are declared and not yet spent.** `GasFile.CleanupFor` answers what to play when
an animation is cut short; nothing interrupts one yet, so nothing asks.

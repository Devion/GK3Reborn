# Behaviour scripts (`.GAS`)

What a thing does when nobody is asking it to. A scene's model line names one —

    model=fanblades, type=gasprop, gas=lbyfan.gas

— and the game runs it for as long as the scene is loaded. There are 4,664 of them in the
archives; 77 are named by a `gasprop` line and drive scenery, and the rest belong to
characters.

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

## What is run

`ANIM`, `LOOPANIM`, `WAIT`, `LABEL`, `GOTO` and `LOOP` — the half of the language that
describes a thing doing the same thing for ever. That is **70 of the 77** scripts the
scenes actually name.

An animation's own length is what the script then waits for, which is what makes a fan
turn continuously rather than restarting every frame. `SceneUpdate.StartScenery` begins
them once the scene is standing and its animation libraries are attached; `Advance` steps
them.

## What is not

The other seven scenery scripts, and every one of the several hundred belonging to a
character, use the branching half: `ONEOF` to pick an idle at random, `WALKTO` to send
someone across a room, `IF` and `SET` over variables of their own.

A script using any of it is **not run at all**, and says so. Half of a behaviour is worse
than none: the branching decides *which* idle to play, so running only the parts that are
understood would pick the wrong one and repeat it for as long as the room was open.
Finishing this wants the Sheep virtual machine behind it, and a place for a script's own
variables to live.

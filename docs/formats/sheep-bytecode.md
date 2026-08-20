# Compiled Sheep (`.SHP`)

224 scripts holding the game's logic. The language and its runtime API are specified by
the original team in `SHEEP ENGINE.DOC` (see [sheep-language.md](sheep-language.md)); the
compiled layout is not covered there and is documented from G-Engine's
`SheepScript::ParseFromData`.

## Layout

| Size | Field |
|---|---|
| 8 | `GK3Sheep` |
| 4 | possibly a format version |
| 4 | header size |
| 8 | header size again, then content size |
| 4 | section count |
| … | one 4-byte offset per section, relative to the end of the header |

Each section begins with a 12-byte name — `SysImports`, `StringConsts`, `Variables`,
`Functions` or `Code` — then repeats its own sizes and an offset table before its
contents.

**SysImports** names every API function the script calls, with a return-type code and one
code per argument: 0 void, 1 int, 2 float, 3 string.

**StringConsts** is a block of NUL-terminated text with an offset table. Instructions
refer to strings by offset into the block, not by index.

**Variables** carries a name, a type code and an initial value.

**Functions** maps each name to its offset in the bytecode.

**Code** is a single block of raw bytecode.

### The off-by-one that hides

A length-prefixed string occupies two bytes of length, then that many bytes, **then one
further byte**. Consuming only the counted bytes leaves the reader one short — and
because every field after it then reads from one byte early, the failure surfaces much
later as an absurd length in an unrelated section rather than at the string itself. This
is worth knowing before debugging a misalignment from where it appears.

## Instruction set

52 opcodes, `0x00` to `0x34`, with `0x0C` unused — the original documentation describes
it as a deprecated export instruction, and no retail script contains it.

Operands are 32-bit and only some instructions carry one:

| Instructions | Operand |
|---|---|
| `CallSysFunctionV/I/F/S` | index into the import table |
| `Branch`, `BranchGoto`, `BranchIfZero` | absolute bytecode address |
| `StoreI/F/S`, `LoadI/F/S` | variable index |
| `PushI` | int literal |
| `PushF` | float literal |
| `PushS` | offset into the string block |
| `IToF`, `FToI` | how far down the stack to convert |

`IToF` and `FToI` are the easy ones to get wrong: the names suggest they act on the top
of the stack, but each carries an index saying how far down to reach.

## Results

All **224 scripts decode completely** — 1,481 functions, 147,223 instructions, no script
stopping early and none failing to parse.

That is the check worth having. An unknown opcode or a miscounted operand desynchronises
the stream immediately, and everything after decodes as nonsense or runs off the end. Every
byte of every script decoding as a valid instruction is strong evidence the instruction
set and operand sizes are right.

**139 distinct API functions are actually called** across the corpus. The specification
documents 359 entries of which 174 are development-only, leaving about 130 gameplay
functions — so what the shipped scripts use lines up closely with what the documentation
says gameplay needs, and confirms the P4 scoping.

## Listings

`GK3Reborn.Tools sheep` writes one `.sheep` listing per script to
`normalized/scripts-disassembled/`, with operands resolved against the script's own
tables so a call shows the function it invokes and a push shows the string it pushes:

```text
  JeanTalk$
        0  BeginWait
        1  PushS              1          // "Jean"
        6  GetString
        7  PushI              1
       12  CallSysFunctionV   0          // StopFidget
       17  Pop
       18  PushS              6          // "lby"
       23  GetString
       24  PushS              10         // "Talk"
       29  GetString
       30  PushI              2
       35  CallSysFunctionV   1          // CallSheep
       40  Pop
       41  EndWait
       42  ReturnV
```

User-defined functions carry the `$` suffix the language requires, and the wait block is
the `wait { … }` construct from the grammar.

## The virtual machine

`SheepVirtualMachine` executes the bytecode. It is a stack machine matching the
original's conventions rather than improving on them.

**Calling convention.** Arguments are pushed in order, then their count. A call pops the
count, takes that many arguments and pushes a result — *including void calls*, because
the original compiler emits a `Pop` after every one. A VM that pushed nothing for void
would drift the stack by one per call and fail somewhere unrelated.

**Waiting is a resumable state, not a blocked thread.** `EndWait` suspends the thread
only if something waitable was called inside the block; the caller resumes it when those
calls report completion. That is what Plan/01 section 6 requires — `wait` must be a
suspension the game thread can schedule around, not an operation that stops the engine.

**Execution is bounded.** These scripts come from data the project does not control, so a
runaway loop faults rather than hanging the caller. Division by zero yields zero for the
same reason.

## Running the whole corpus

```bash
GK3Reborn.Tools sheep --execute --source <GK3 Data> --workspace <dir> [--api-returns <n>]
```

Every function of every script runs against a stub API that records calls and returns a
constant. Results with the stub returning zero:

| | |
|---|---:|
| Functions executed | 1,481 |
| Completed normally | 1,464 |
| Faulted | 17 |
| API calls made | 454,280 |

**All 17 faults are the instruction limit** — not unknown opcodes, not stack imbalance,
not bad branch targets, not bad import indices. None of the structural fault codes appear
at all.

That points at the stub rather than the machine, and `--api-returns` settles it. With the
stub returning 1 instead of 0, faults drop to **12** and calls to 338,415: the failures
move when the answers move, which is what a condition-driven loop does and what a VM
defect does not. A script polling `IsWalkingActorNear` never terminates when the answer is
always the same.

So the sweep says the instruction set, operand sizes, calling convention and control flow
survive 147,223 instructions of code written for a different engine — while saying nothing
yet about whether the *game* behaves correctly, which needs the real API and the
differential harness.

## The API surface

`Gk3SheepApi` binds the virtual machine to game state. The ~130 gameplay functions divide
by what they do, not by how often they are called:

**State functions are implemented.** Flags, game variables, noun/verb counts, topic
counts, score, timeblock, location and actor placement. These decide whether the story can
progress, and they are what a differential comparison between engines must agree on. GK3
gates a great deal of dialogue on noun/verb counts — the second time you ask about
something you get a different answer — so those counts are state, not statistics.

**Presentation functions are recorded.** `CutToCameraAngle` is called 2,235 times across
the corpus and `StartAnimation` 2,067, but neither changes what the game permits, and
neither can be performed before the renderer exists. Recording keeps the trace honest: the
call happened, in that order, with those arguments, and nothing was faked.

**Anything unregistered is reported once, then recorded.** Silence would let a missing
function look like a working one. 80 remain unimplemented.

### Running the corpus against real state

```bash
GK3Reborn.Tools sheep --execute --source <GK3 Data> --workspace <dir>
```

| | |
|---|---:|
| Functions executed | 1,481 |
| Completed | 1,469 |
| Still suspended | 8 |
| Faulted | 4 |
| API calls | 143,777 |
| Presentation calls recorded | 67,362 |

Wait handling is doing real work here. Before the API knew which functions were waitable,
nothing suspended; with the specification's classification wired in, **1,154 functions
suspend on a wait block** — which is what GK3 scripts do, since most are
`wait { WalkTo(…); StartAnimation(…); }`. The sweep then resumes them on the assumption
that every waited call finishes at once, which is not how the game behaves but is what
lets a whole function be traced.

The remaining 4 faults are still the instruction limit, and shrank from 17 as state
functions began returning real answers — polling loops terminate once the thing they poll
can change.

### Determinism

The sweep ends by hashing the game state, and the hash is byte-identical across runs
(`d0a2267c9950a1e2`, 143,777 calls, twice). That is the property the differential harness
depends on: a state hash that varied between runs of the same build could not detect a
divergence between two builds. Ordering is made explicit before hashing rather than
relying on dictionary enumeration order.

## Scripts calling scripts

`CallSheep` appears 640 times across the corpus and `Call` another 190, so a machine that
cannot follow a call from one script into another cannot follow the game's control flow.
`ScriptHost` closes that: a repository of scripts by name plus the API functions that jump
between them — `CallSheep`, `CallGlobalSheep` and `CallSceneFunction`, the last resolving
against the current location.

Calls run inline to completion rather than being scheduled. The original waits on them —
`wait CallSheep(…)` is the usual form — so running the callee immediately produces the
same observable order for anything that does not depend on real elapsed time. Depth is
bounded, because the data does call in circles.

With every script loaded, re-running the lobby's entry points enters **186 functions
across scripts**, and 37 calls name scripts absent from the corpus — recorded rather than
silently ignored.

## Inventory

Inventory is per character, not global. GK3 switches between Gabriel and Grace, they carry
different things, and `DoesEgoHaveInvItem` appears in 161 action conditions with
`DoesGraceHaveInvItem` existing separately for exactly that reason. It forms part of the
state hash, since which character holds what decides whether puzzles can be solved.

`CombineInvItems` consumes both sources and produces the third. Leaving the sources in
place would let a player combine the same pair repeatedly, which is a puzzle-semantics
question rather than a bookkeeping one.

Carrying an item and having it **in hand** are different. GK3's inventory screen has one
item selected at a time, and using an item on something is written in the action files as
a verb named for the item, so `IsActiveInvItem` asks which of the things in the bag is the
one about to be used. `SetEgoActiveInvItem` puts it there — not refused when the character
is not carrying it, because the original logs a warning and does it anyway and scripts
rely on that. Removing an item empties the hand that held it.

## The six the corpus asked for and nothing answered

`check-scenes` names every function a scene calls and no host implements. Six came from
the action files' own case conditions, where an unimplemented function returns zero and
warns once — so the condition reads as false, the action leaves the game, and nothing says
so at the point it matters.

| function | what it asks |
| --- | --- |
| `IsActiveInvItem` | is this the item in ego's hand |
| `DoesSidneyFileExist` | has the player gathered this evidence in Sidney, the in-game computer |
| `GetNounVerbCountInt`, `GetTopicCountInt` | the same counts as the named forms |
| `GetRandomInt` | a number in a range, both ends inclusive |
| `IsTopLayerInventory` | is the inventory screen on top |

The `Int` forms exist because the original numbers nouns and verbs: its script host can
only pass integers between a case and a function, so `n$` and `v$` are indices into the
action manager's tables. Here they carry the names themselves, so the two spellings ask
the same question and the suffix is only history.

`GetRandomInt` is drawn from the state's own generator, seeded fixed — ADR 0004 forbids
ambient nondeterminism in engine code, and the differential harness compares two runs of
the same story. How many numbers have been drawn is part of the state hash: two runs that
have drawn a different number of times will disagree about everything random from then
on, and that should show up at once rather than at the first visible consequence.

Sidney's files and the item in hand are in the hash for the same reason. Nothing writes
Sidney's files yet — that is the analysis screen — so it reads as an investigation nobody
has started, and `IsTopLayerInventory` is answered no because there is no inventory screen
to be on top of.

Together these took the unimplemented surface from 80 functions to 71, and faults across
the whole corpus from 4 to 1 — `CallSheep` doing real work means the loops that poll for
its effects now terminate. With the six above, `check-scenes` reports that every function
the scene files and their action files call is implemented.

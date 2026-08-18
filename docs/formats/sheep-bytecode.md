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

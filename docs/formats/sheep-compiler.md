# Compiling Sheep

The game ships bytecode. This is the other direction: source in, a `.SHP` out, running on
the same machine the game's own 224 scripts run on.

`Plan/01-architecture.md` section 6 decided to hand-write a scanner and a recursive-descent
parser rather than port G-Engine's flex/bison output, and `SHEEP ENGINE.DOC` — the original
team's own language reference, which the archives contain — is what makes that cheap: the
BNF did not have to be recovered from generated code. See `sheep-language.md` for what the
document establishes.

```
compile-sheep --input demo.sheep-src --output DEMO.SHP --source <GK3>/Data
```

## The pieces

| Piece | What it does |
| --- | --- |
| `SheepLexer` | Source to tokens |
| `SheepParser` | Tokens to a syntax tree |
| `SheepCompiler` | A syntax tree to a `SheepScriptFile` |
| `SheepScriptWriter` | A `SheepScriptFile` to the `.SHP` container |
| `SheepSignatures` | What every function takes and returns |

## Four lexical rules that are not the obvious ones

* **Underscore counts as a letter**, so a name may begin with one.
* **Identifiers are case-insensitive**, which is why nothing downstream compares them
  ordinally.
* **A user identifier ends in a dollar** — a function a script defines, or a label — and
  the dollar is part of the name. It is the only thing that tells a call to `TwoShot$`
  from a call to a system function at a call site.
* **Tokenising is maximal munch.** That is what makes `<=` one token and `<>` — the
  language's second spelling of "not equal" — a token at all.

Comments come in both C forms and **do not nest**: the first `*/` ends a `/*` however many
opens are inside it. There are no string escapes; the reference gives none and the content
agrees, because the game's strings are asset names and licence plates.

## What the compiler has to know

The instruction set is **typed rather than polymorphic**: there is an `AddI` and an `AddF`
and nothing that adds whichever it is given. So the type of every expression has to be
known. Three types — int, float, string — and one conversion.

**`IToF`'s operand is how far down the stack to reach**, not a value. In `1 + 0.5` the int
is one below the top when the conversion happens, so the reach is one; in `0.5 + 1` it is
zero. Emitting it as though it converted the top is a mistake that only appears in
expressions mixing the two.

**Calls follow the original's convention**, because the machine reads it: arguments left to
right, then the count as an int, then the call. A **void call still leaves a value behind**
and the compiler emits the matching `Pop` — which is why the corpus has exactly as many of
those as it has void calls, 18,447 of each. A string is pushed as its offset in the constant
pool and then fetched with `GetString`.

**A whole number written where a float belongs is converted, not replaced.**
`SetTimerSeconds(2)` compiles to a `PushI` and an `IToF`, never a `PushF`. That is the
original's choice and it needs the signature to know.

**Every function ends with `ReturnV` and four `SitnSpin` bytes.** Not a guess: the corpus
holds 1,481 of the one and 5,924 of the other, four to one exactly, and the next function
always starts after them.

**The string pool opens with an empty string**, so the first real one sits at offset one.
All 224 of the game's scripts are laid out that way.

## Where the signatures come from

The specification has them, but the specification is a Word document and its extracted
index lives in the content workspace because it is derived from copyrighted material.

**The game answers the question itself.** Every compiled script carries an import table
giving the return type and argument types of every function it calls, so the 224 shipped
scripts between them describe all 139 functions the game uses. `sheep` gathers them into
`normalized/scripts-disassembled/signatures.txt`, and `compile-sheep --source` reads them
straight out of the archives.

Two scripts giving one function two signatures would mean either the reader is wrong or the
assumption that a name has one signature is. Neither happens; both are reported if they
ever do.

Without a catalogue the compiler assumes a call returns an int and takes what it was given.
The arity is right and the types are a guess, which is exactly the case where a float
argument goes in as an int — so `compile-sheep` says which functions it did not recognise.

## How it is checked

Three ways, none of them "the tree looks right".

**Everything compiled is run.** A compiler can be wrong in ways a syntax tree comparison
cannot see — the wrong conversion, a jump one byte out, an argument count that does not
match what was pushed — and the machine notices all of them.

**Every shipped script round-trips through the writer.** `sheep` reads all 224, writes each
one back out, reads it again, and compares the imports and their signatures, the string pool
at the offsets the bytecode names, the variables, where each function starts, and the code.
All 224 survive. A container half-understood reads the game's own files perfectly well and
produces something nothing else can open, and there is no way to notice that from the reader
alone.

**The output is compared against the original compiler's.** Reconstructing `RC1102P.SHP`'s
`LookMop$` from its disassembly and compiling it gives **52 instructions identical to
Sierra's** — same opcodes, same addresses, same operands, same string offsets. That is the
strongest conformance evidence available without the original toolchain.

## What is not implemented

* **`symbols` initialisers must be constants.** The block runs before any code does, so
  there is nowhere to evaluate a call.
* **No string arithmetic or comparison.** The instruction set has none; compiling `"a" ==
  "b"` as an integer compare would answer confidently and wrongly, so it is refused.
* **Functions take no arguments**, which is the language's own rule rather than a gap.
* **The offset tables inside sections are written as zeroes** for the four sections whose
  reader skips them. Matching the original's would be reproducing something nothing reads.

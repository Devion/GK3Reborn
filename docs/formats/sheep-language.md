# Sheep: what the original specification tells us

The archives contain `SHEEP ENGINE.DOC` (2.3 MB), the original team's own
specification of the Sheep language and its runtime API. It is the authoritative
source for P4 and supersedes reverse engineering from G-Engine's reader wherever
the two could disagree.

This page records what the document establishes and what that means for the
implementation. It deliberately does not reproduce the document: it is Sierra's
copyrighted material. Extract it locally and read it.

## What the document contains

Two parts.

**The Sheep Language** — a language reference in the style of K&R (the document
says so itself, crediting Kernighan and Ritchie for about 80% of the prose).
Sections cover a tutorial introduction, types, operators and expressions, control
flow, functions and program structure, execution and synchronization, multiple
threads, the console, and then a formal reference: a BNF grammar, lexical
conventions, comments, identifiers, keywords, constants and syntax notation.

**Function Reference** — every runtime function, specified in a fixed format:
`FunctionName`, `Prototype`, `Behavior`, `Parameters`, `Return Value`,
`Description`, `Example`, and a `History` table giving the release and timestamp
each function was created or changed. Functions are grouped into Actors, Animation
and Dialogue, Application, Camera, Construction Mode, Debugging, Engine, Game
Logic, General, Insets, Inventory, Models, Reports, Scene, Sound, Tracing and
Prototypes.

## Why this matters for the plan

`01-architecture.md` decided to hand-write a scanner and recursive-descent parser
rather than port G-Engine's flex/bison output. That decision stands, and it just
became much cheaper: **the grammar does not have to be recovered from the
generated parser, because the original BNF is written down.**

The plan's risk register rates "Sheep semantic mismatch" as able to stop the game
from being completable. The mitigation is no longer only differential testing
against a re-implementation — there is a specification to conform to.

## Facts the parser has to honour

From the lexical conventions:

- Identifiers begin with a letter; underscore counts as a letter; digits allowed
  after the first character.
- **Identifiers are case-insensitive.** Upper and lower case are the same.
- Any number of characters is significant.
- "User" identifiers — custom function names and labels — **must end in `$`**,
  with no whitespace before it. System function names do not.
- Two comment forms: `/* … */` and `// … newline`. Comments do **not** nest in
  Sheep v1.
- Tokenizing is maximal munch: the next token is the longest string of characters
  that could constitute one.

From the grammar, the shape of a script: an optional `symbols { }` block of typed
variable declarations (`int`, `float`, `string`, with optional initialisers),
followed by an optional `code { }` block of functions. Statements cover
assignment, expression statements, `if`/`else`, blocks, `return`, `goto` and
labels, and three that are specific to this language: `breakpoint`, `sitnspin`,
and `wait` — the last in three forms, bare, applied to a single call, or applied
to a braced group of calls.

Operators, in the grammar's expression production: unary `-` and `!`; arithmetic
`+ - * / %`; comparison `< > <= >= != <>` (both inequality spellings); logical
`||` and `&&`; parenthesised subexpressions; function calls.

## Scope: the API surface is smaller than it looks

The document specifies **359 function entries**. Machine-extracting the
`Prototype` and `Behavior` fields yields 305 distinct functions that parse
cleanly, classified as:

| Behavior | Count | Meaning |
|---|---:|---|
| `DEVELOPMENT` | 174 | Console, debugging and content-tooling functions |
| `IMMEDIATE` | 81 | Gameplay functions that complete within the call |
| `WAIT` | 49 | Gameplay functions the caller can block on |

That reframes P4's scope. The conformance surface for *completing the game* is on
the order of **130 functions**, not 359 — the `DEVELOPMENT` group matters for the
developer console and content tools, and can follow the gameplay set rather than
gate it.

G-Engine implements roughly 246 functions across both groups, so it is neither a
subset nor a superset of the specification. Where they differ, the specification
describes intent and G-Engine describes observed behaviour; the differential
harness exists to find where the game depends on the latter.

The `Behavior` field is also exactly the metadata the runtime needs. The plan
requires modelling wait/yield as explicit resumable operations on the game thread,
and the specification already states, per function, whether a call can be waited
on. That classification should be generated into the API registry rather than
rediscovered.

Arity is modest: 99 functions take no arguments, 129 take one, 45 take two, and a
long tail reaches nine.

## Using it

Extract with any Word reader; `antiword` handles it cleanly:

```bash
antiword -w 100 "ContentWorkspace/raw/core/SHEEP ENGINE.DOC" > sheep-engine.txt
```

A machine-readable index of the function surface — name, prototype arguments and
behaviour class — is generated to
`ContentWorkspace/manifests/sheep-api-from-docs.json`. It lives in the workspace,
not the repository: it is derived from copyrighted documentation, and it is a
working aid rather than a deliverable.

## Other documents worth reading first

`SIF.DOC` (1.6 MB) for scene initialisation, `NVC.DOC` for the noun/verb/case
action system, `PERSISTENCE.DOC` for save state, `GAS.DOC` for autoscripts,
`TIMEBLOCKBIBLE.DOC` for the day and timeblock structure, `SOUND TRACK FILES.DOC`
for the soundtrack system, and `GK3 FONTS.DOC` for the font format. Each covers a
subsystem the plan schedules for reimplementation.

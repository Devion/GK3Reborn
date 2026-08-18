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

Built-in cases the engine answers itself: `ALL`, `DEFAULT`, and the ego-specific
`GABE_ALL`, `GRACE_ALL` and their negations, which matter because GK3 switches between
Gabriel and Grace.

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

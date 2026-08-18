# ADR 0004: Own the RNG; do not chase G-Engine parity

- Status: accepted
- Date: 2026-08-18

## Context

An early version of the program plan required reproducing G-Engine's random number
streams bit-for-bit wherever gameplay observes them, so that differential testing
could compare the two implementations step for step.

Inspection of `Engine/Math/Random.h` shows this is not achievable:

- the generator is `std::default_random_engine` seeded from
  `std::chrono::system_clock::now()`, so it does not reproduce across runs at all;
- it is declared `static` at namespace scope **in a header**, so every translation
  unit that includes it gets its own independently seeded generator;
- it draws through `std::uniform_int_distribution` and
  `std::uniform_real_distribution`, whose output is implementation-defined and
  already differs between MSVC and libstdc++.

It is also G-Engine's own choice of algorithm, not the 1999 executable's. Matching it
would not be fidelity to the original game even if it were possible.

## Decision

GK3Reborn defines its own randomness: **xoshiro256++ seeded by SplitMix64 from one
explicit 64-bit seed**, exposed as `DeterministicRandom`. The seed and the full
generator state are captured and restored with the game state, so a save resumes the
same stream and a replay reproduces it exactly.

Differential tests against G-Engine compare RNG-dependent outcomes as **equivalence
classes** — the set of outcomes the game logic permits — not as exact streams. Where
a step-for-step comparison is genuinely needed, it runs against the instrumented
G-Engine fork built in P0, which is patched to use a fixed seed.

No other source of randomness is permitted in engine code. `Random.Shared`, ambient
`Guid.NewGuid` ordering and hash-order iteration are all defects in anything that
affects game state.

## Consequences

**Good.** Replays, save/load and headless story traversal are genuinely
deterministic, which the plan depends on for automated coverage. The algorithm is
documented and stable across platforms and runtime versions, unlike the C++ standard
library's distributions.

**Bad.** Differential testing loses the sharpest possible signal: a divergence in an
RNG-dependent outcome now needs a human to decide whether it is a bug or a legal
alternative. Equivalence classes have to be written per case, which is real work
that exact comparison would have avoided — if exact comparison had been possible.
